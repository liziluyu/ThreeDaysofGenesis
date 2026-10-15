using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 冰（<see cref="IceParticle"/>）与水 ⇄ 冰相变：水面低温成核 / 沿冰缘蔓延 / 按几何梯度向下加厚、
    /// 镜像融化（越深越难融）、潜热与热容差的守恒结算、静止固体不移动、浅色描边、隔离类型表不结冰。
    /// </summary>
    /// <remarks>
    /// <para><b>两条相变都走随机刻</b>（<see cref="ParticleType.OnRandomTick"/>）：随机刻命中是低频事件
    /// （世界每逻辑帧只抽 <see cref="ParticleWorld.RandomTicksPerStep"/> 格，生产 48），
    /// 因此钩子里**不再叠概率骰**——条件满足即冻 / 即化，测试也因此在「条件成立」时**调一次就够**，
    /// 不需要窗口循环；<c>RandomTicksPerStep</c> 默认 0（关闭），只有整帧集成用例才显式打开它。</para>
    /// <para>跨类型相变（水 → 冰、冰 → 水）依赖 <see cref="ParticleIds"/> 回填，隔离类型表里这些字段是 0，
    /// 因此全部走 <see cref="ParticleRegistration.RegisterAll"/> 的生产表；唯一的例外是
    /// <c>IsolatedTypeTableWorld_NeverFreezes</c> 那条守卫回归，它刻意用隔离表建世界。</para>
    /// <para>数值口径：结冰放热 / 融化吸热都按「**温度步长** <see cref="IceParticle.FusionTemperatureStep"/> ×
    /// 本相热容」结算（结冰 +C_冰·s、融化 −C_水·s），再加 / 减热容差项
    /// <see cref="IceParticle.HeatCapacityDelta"/>·T——两者合起来保证冻融往返逐位可逆。</para>
    /// <para>温度标定：水面结冰门 4.0°、成核门 3.5°——实测世界里夜间水温稳定在 3° 上下
    /// （刚放置那一刻才是 0°），所以门槛必须落在 3° 之上（见 <see cref="WaterParticle.FreezingPoint"/>）。</para>
    /// </remarks>
    public sealed class IceParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort Ice
        {
            get { return ParticleIds.Ice; }
        }

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>水量账：水格 + 冰格 + 湿度场（相变在两者之间来回，总量必须不变）。</summary>
        private static float WaterAccount(ParticleWorld world)
        {
            return CountType(world, Water) + CountType(world, Ice) + world.Humidity.Sum();
        }

        /// <summary>把 (x,y) 设成指定温度（T = Q / 热容；空气用 <see cref="HeatMap.AirHeatCapacity"/>）。</summary>
        private static void SetTemperature(ParticleWorld world, int x, int y, float temperature)
        {
            ushort id = world.GetCell(x, y).typeId;
            float capacity = id == ParticleRegistry.AirTypeId
                ? HeatMap.AirHeatCapacity
                : world.Types[id].Info.HeatCapacity;
            world.Heat.SetHeat(x, y, temperature * capacity);
        }

        /// <summary>直接调某类型的**随机刻**钩子（生产表里的委托）——两条相变都从这里进。</summary>
        private static void RandomTick(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnRandomTick(world, x, y);
        }

        /// <summary>直接调某类型的逻辑帧钩子（水只有蒸发了；冰的 OnLogic 是空体）。</summary>
        private static void Logic(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnLogic(world, x, y);
        }

        /// <summary>
        /// 把光照强度调到**适宜区间**内（8~12 ⇒ 光照冷度缩放恰为 1）：门槛类用例最省心的口径，
        /// 让断言值就等于标定常量。光照本身的影响由专门的光照缩调用例覆盖。
        /// </summary>
        private static void UseComfortLight(ParticleWorld world)
        {
            world.Light.Intensity = 10f;
        }

        /// <summary>铺一行石头地板（让水「落定」：正下方不是气体 / 空）。</summary>
        private static void Ground(ParticleWorld world, int y)
        {
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, y, Stone);
            }
        }

        // ============================== 生产注册 ==============================

        /// <summary>
        /// 生产注册路径：冰必须紧随生物质追加在清单末尾，且是「静止固体」的全部体征——
        /// Solid、不带 Organic（死亡不留腐殖质）、非静态（收得到随机刻）、不受重力、
        /// <c>OnLogic</c> 严格空体（融化在随机刻里 ⇒ hasLogic: false）、空 OnMotion 用默认阅读盒、
        /// 调色板不透明、固体描边且**用浅色**、不做液面高光。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsSolidIceId()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, Ice, "冰 id 应被回填");
            Assert.AreEqual((ushort)(ParticleIds.Biomass + 1), Ice, "冰必须紧随生物质追加在清单末尾");

            ParticleTypeInfo info = ParticleRegistry.Types[Ice].Info;
            Assert.AreEqual("冰", info.DebugName);
            Assert.IsTrue((info.Tags & ParticleTags.Solid) != 0, "冰是固体");
            Assert.AreEqual(0, (int)(info.Tags & ParticleTags.Organic), "冰不带 Organic：被覆盖时不留腐殖质");
            Assert.IsFalse(info.IsStatic, "冰必须非静态，否则收不到随机刻（融化）");
            Assert.IsFalse(info.AffectedByGravity, "冰原地不落");
            Assert.IsFalse(info.HasLogic, "OnLogic 是严格空体（融化写在 OnRandomTick），hasLogic 必须为 false");
            Assert.IsTrue(info.HasOutline, "固体描边");
            Assert.IsTrue(info.OutlineLighten, "冰用浅色描边（向 OutlineLightColor 提亮）");
            Assert.IsFalse(info.HasSurfaceHighlight, "液面高光是给液体的");

            MotionReadBox reach = info.MotionReadReach;
            Assert.AreEqual(1, (int)reach.X, "运动阅读盒的水平半径（OnMotion 空体，用默认八邻盒）");
            Assert.AreEqual(1, (int)reach.Up, "运动阅读盒的向上半径");
            Assert.AreEqual(1, (int)reach.Down, "运动阅读盒的向下半径");

            Assert.AreEqual(IceParticle.HeatCapacity, info.HeatCapacity, 1e-6f, "冰的热容 = 水的一半");
            Assert.AreEqual(IceParticle.HeatCapacity, WaterParticle.HeatCapacity - IceParticle.HeatCapacityDelta,
                1e-6f, "热容差必须自洽：水热容 − ΔC = 冰热容");
            Assert.AreEqual(0.85f, info.ThermalConductivity, 1e-6f, "导热好：冷锋能往冰下走");
            Assert.AreEqual(1f, info.ThermalEmissivity, 1e-6f, "暴露冰面夜间继续辐射抽冷");
            Assert.AreEqual(0.4f, info.LightAbsorption, 1e-6f, "冰面反光：日照下升温慢");

            Assert.IsTrue(info.Palette.Length >= 2, "调色板至少两档");
            for (int i = 0; i < info.Palette.Length; i++)
            {
                Assert.AreEqual(255, info.Palette[i].a, $"调色板第 {i} 档必须不透明（α = 0 会被渲染成背景）");
            }
        }

        // ============================== 结冰（随机刻）：位置与几何门 ==============================

        /// <summary>
        /// 水面（正上方界内气体 / 空 + 正下方落定）且挨着冰：<see cref="WaterParticle.FreezingPoint"/> 之内
        /// 随机刻命中即冻。水温取「蔓延门 − 0.1」——**高于成核门**（`FreezingPoint − NucleationSupercooling`），
        /// 因此这条只可能由**蔓延**达成。
        /// </summary>
        [Test]
        public void SurfaceWater_NextToIce_FreezesAtSpreadThreshold()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(3, 2, Ice);     // 冰种：左侧邻格
            world.Set(4, 2, Water);
            UseComfortLight(world);
            SetTemperature(world, 4, 2, WaterParticle.FreezeThresholdAt(world, 0) - 0.1f);

            RandomTick(world, Water, 4, 2);

            Assert.AreEqual(Ice, world.GetCell(4, 2).typeId, "挨着冰的水面在冰点内、随机刻命中就冻上");
            Assert.AreEqual(Ice, world.GetCell(3, 2).typeId, "原来的冰种保持不动");
        }

        /// <summary>
        /// 没有冰邻格的水面要**过冷**才成核：低于蔓延门但高于成核门（= 蔓延门 − 0.1）不冻；
        /// 降到成核门（`FreezingPoint − NucleationSupercooling`）才冻。
        /// </summary>
        [Test]
        public void SurfaceWater_WithoutIceNeighbor_NeedsSupercoolingToNucleate()
        {
            var warm = new ParticleWorld(9, 9, 20261013u);
            Ground(warm, 1);
            warm.Set(4, 2, Water);
            UseComfortLight(warm);
            float nucleationGate = WaterParticle.FreezeNucleationThresholdAt(warm);
            float spreadGate = WaterParticle.FreezeThresholdAt(warm, 0);
            SetTemperature(warm, 4, 2, spreadGate - 0.1f);
            RandomTick(warm, Water, 4, 2);
            RandomTick(warm, Water, 4, 2);   // 再摇一次：判定与随机无关，重复调用仍不该冻

            Assert.AreEqual(Water, warm.GetCell(4, 2).typeId,
                $"没有冰邻格时 {spreadGate - 0.1f}° 不够冷：成核门是 {nucleationGate}°");

            var cold = new ParticleWorld(9, 9, 20261013u);
            Ground(cold, 1);
            cold.Set(4, 2, Water);
            UseComfortLight(cold);
            SetTemperature(cold, 4, 2, WaterParticle.FreezeNucleationThresholdAt(cold));
            RandomTick(cold, Water, 4, 2);

            Assert.AreEqual(Ice, cold.GetCell(4, 2).typeId, $"过冷到成核门（{nucleationGate}°）以内就冻上");
        }

        /// <summary>几何门：正上方不是**界内**气体 / 空的水不结冰——水下、被固体压住、顶行（越界不当天空）。</summary>
        [Test]
        public void Water_WithoutOpenSkyAbove_NeverFreezes()
        {
            var world = new ParticleWorld(9, 12, 20261013u);
            Ground(world, 1);
            world.Set(3, 2, Water);   // 水下：正上方还是水（不是水面）
            world.Set(3, 3, Water);
            world.Set(4, 2, Water);   // 被石头压住
            world.Set(4, 3, Stone);
            world.Set(5, 11, Water);  // 顶行：正上方越界（不算天空，与蒸发的审计 B3 同口径）

            SetTemperature(world, 3, 2, 0f);
            SetTemperature(world, 4, 2, 0f);
            SetTemperature(world, 5, 11, 0f);

            for (int i = 0; i < 3; i++)
            {
                RandomTick(world, Water, 3, 2);
                RandomTick(world, Water, 4, 2);
                RandomTick(world, Water, 5, 11);
            }

            Assert.AreEqual(0, CountType(world, Ice), "这几格都不该冻出冰");
            Assert.AreEqual(Water, world.GetCell(3, 2).typeId, "水下（正上方是水）不结冰：只在水面结");
            Assert.AreEqual(Water, world.GetCell(4, 2).typeId, "被固体压住的水不结冰");
            Assert.AreEqual(Water, world.GetCell(5, 11).typeId, "顶行水不结冰（越界不算天空）");
        }

        /// <summary>悬空的水滴不结冰（正下方是气体 / 空）：否则雨滴会在空中冻成悬浮冰晶。落在固体上就冻。</summary>
        [Test]
        public void Water_SuspendedInAir_DoesNotFreeze()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(4, 6, Water);   // 空中：正下方是空气

            SetTemperature(world, 4, 6, 0f);
            RandomTick(world, Water, 4, 6);

            Assert.AreEqual(Water, world.GetCell(4, 6).typeId, "悬空水滴不结冰（落定要求：正下方不是气体 / 空）");

            world.Set(4, 2, Water);   // 落在石头上 → 落定
            SetTemperature(world, 4, 2, 0f);
            RandomTick(world, Water, 4, 2);

            Assert.AreEqual(Ice, world.GetCell(4, 2).typeId, "落在固体上的同样冷的水会结冰");
        }

        /// <summary>两条随机刻相变都**不消耗世界随机流**（抽取由世界层完成）：调用前后与对照世界的下一个随机数一致。</summary>
        [Test]
        public void RandomTicks_ConsumeNoRandomStream()
        {
            var world = new ParticleWorld(9, 9, 777u);
            Ground(world, 1);
            world.Set(3, 2, Ice);
            world.Set(4, 2, Water);   // 够冷：会被冻上
            SetTemperature(world, 4, 2, 3.5f);
            world.Set(5, 2, Ice);     // 够暖：会被化掉
            SetTemperature(world, 5, 2, IceParticle.MeltingPoint + 1f);

            var control = new ParticleWorld(9, 9, 777u);
            Ground(control, 1);
            control.Set(3, 2, Ice);
            control.Set(4, 2, Water);
            SetTemperature(control, 4, 2, 3.5f);
            control.Set(5, 2, Ice);
            SetTemperature(control, 5, 2, IceParticle.MeltingPoint + 1f);

            ushort frozenBefore = world.GetCell(4, 2).typeId;
            RandomTick(world, Water, 4, 2);
            RandomTick(world, Ice, 5, 2);

            Assert.AreEqual(Water, frozenBefore, "前置：那格本来是水");
            Assert.AreEqual(control.NextUInt(), world.NextUInt(),
                "随机刻相变（冻 / 化）都不消耗世界随机流");
        }

        // ============================== 结冰：深度梯度与加厚 ==============================

        /// <summary>
        /// 加厚：正上方压着 d 格冰的水，阈值 = FreezingPoint · 0.5^d——比它暖不冻、比它冷就冻
        /// （d = 2 的阈值 1.0，与表层 4.0 拉开四倍）。
        /// </summary>
        [Test]
        public void Water_UnderIce_ThickensOnlyBelowDepthThreshold()
        {
            var tooWarm = new ParticleWorld(9, 14, 20261013u);
            Ground(tooWarm, 1);
            tooWarm.Set(4, 2, Water);
            tooWarm.Set(4, 3, Ice);
            tooWarm.Set(4, 4, Ice);   // 水上方两格冰 ⇒ depth = 2
            UseComfortLight(tooWarm);
            float gate = WaterParticle.FreezeThresholdAt(tooWarm, 2);
            SetTemperature(tooWarm, 4, 2, gate + 0.1f);
            RandomTick(tooWarm, Water, 4, 2);

            Assert.AreEqual(Water, tooWarm.GetCell(4, 2).typeId,
                $"depth = 2 的阈值是 {gate}，比它暖就不冻");

            var cold = new ParticleWorld(9, 14, 20261013u);
            Ground(cold, 1);
            cold.Set(4, 2, Water);
            cold.Set(4, 3, Ice);
            cold.Set(4, 4, Ice);
            UseComfortLight(cold);
            SetTemperature(cold, 4, 2, gate - 0.1f);
            RandomTick(cold, Water, 4, 2);

            Assert.AreEqual(Ice, cold.GetCell(4, 2).typeId, "低于 depth = 2 的阈值就会加厚");
        }

        /// <summary>
        /// 深度扫描有上限 <see cref="IceParticle.ColumnScanDepth"/>：压 8 格与压 12 格冰的水用**同一个**
        /// 阈值（超出按最后一档处理）。最后一档仍为正，而冰下的水会一路被抽冷，因此「厚度无上限」——
        /// 只是越深越难，永不被阈值或扫描上限卡死。
        /// </summary>
        [Test]
        public void FreezeThreshold_SaturatesAtColumnScanDepth()
        {
            Assert.Greater(WaterParticle.FreezeThresholdAtDepth(IceParticle.ColumnScanDepth), 0f,
                "最后一档阈值仍为正：够冷的水永远达标");

            var deep = new ParticleWorld(9, 20, 20261013u);
            Ground(deep, 1);
            deep.Set(4, 2, Water);
            UseComfortLight(deep);
            for (int y = 3; y < 15; y++)
            {
                deep.Set(4, y, Ice);   // 12 格冰：扫描在 8 格处封顶
            }

            Assert.AreEqual(IceParticle.ColumnScanDepth, IceParticle.DepthBelowIceTop(deep, 4, 2),
                "深度扫描在 ColumnScanDepth 处封顶");

            SetTemperature(deep, 4, 2, 0f);
            RandomTick(deep, Water, 4, 2);

            Assert.AreEqual(Ice, deep.GetCell(4, 2).typeId, "最深的冰层下仍会继续加厚（0° 足够冷）");
        }

        /// <summary>深度计数：自下往上数「正上方连续冰格」，被非冰格打断即止。</summary>
        [Test]
        public void DepthBelowIceTop_CountsContiguousIceOnly()
        {
            var world = new ParticleWorld(9, 12, 20261013u);
            world.Set(4, 4, Ice);     // 最下面那格冰（上方压着 1 格冰）
            world.Set(4, 5, Ice);     // 冰顶面（上方是水）
            world.Set(4, 6, Water);   // 打断
            world.Set(4, 7, Ice);     // 上方是空气

            Assert.AreEqual(1, IceParticle.DepthBelowIceTop(world, 4, 4), "上方压着一格冰");
            Assert.AreEqual(0, IceParticle.DepthBelowIceTop(world, 4, 5), "冰顶面 = 0（上方是水）");
            Assert.AreEqual(1, IceParticle.DepthBelowIceTop(world, 4, 6), "水格上方的那格冰照数（被水打断后重新起算）");
            Assert.AreEqual(0, IceParticle.DepthBelowIceTop(world, 4, 7), "上方是空气：0");
        }

        // ============================== 融化（随机刻，镜像） ==============================

        /// <summary>表层冰（depth = 0）在 <see cref="IceParticle.MeltingPoint"/> 之上、随机刻命中即化；之下不动。</summary>
        [Test]
        public void SurfaceIce_MeltsAboveMeltingPoint()
        {
            var warm = new ParticleWorld(9, 9, 20261013u);
            Ground(warm, 1);
            warm.Set(4, 2, Ice);
            UseComfortLight(warm);
            SetTemperature(warm, 4, 2, IceParticle.MeltThresholdAt(warm, 0) + 0.5f);
            RandomTick(warm, Ice, 4, 2);

            Assert.AreEqual(Water, warm.GetCell(4, 2).typeId, "高于融化门、随机刻命中就化");

            var cold = new ParticleWorld(9, 9, 20261013u);
            Ground(cold, 1);
            cold.Set(4, 2, Ice);
            UseComfortLight(cold);
            SetTemperature(cold, 4, 2, IceParticle.MeltThresholdAt(cold, 0) - 0.5f);
            RandomTick(cold, Ice, 4, 2);
            RandomTick(cold, Ice, 4, 2);

            Assert.AreEqual(Ice, cold.GetCell(4, 2).typeId, "不到融化门不化");
        }

        /// <summary>融化镜像：越深越难融——压着 d 格冰的那一格要 MeltingPoint + 2·d 才化。</summary>
        [Test]
        public void Ice_MeltThresholdRisesWithDepth()
        {
            var shallow = new ParticleWorld(9, 14, 20261013u);
            Ground(shallow, 1);
            shallow.Set(4, 2, Ice);   // depth = 1（上方还压着一格冰）
            shallow.Set(4, 3, Ice);
            UseComfortLight(shallow);
            float gate = IceParticle.MeltThresholdAt(shallow, 1);
            SetTemperature(shallow, 4, 2, gate - 0.2f);
            RandomTick(shallow, Ice, 4, 2);

            Assert.AreEqual(Ice, shallow.GetCell(4, 2).typeId,
                $"depth = 1 的融化门是 {gate}，差 0.2° 不化");

            SetTemperature(shallow, 4, 2, gate + 0.2f);
            RandomTick(shallow, Ice, 4, 2);

            Assert.AreEqual(Water, shallow.GetCell(4, 2).typeId, "超过 depth = 1 的门就化");
        }

        // ============================== 光照强度 → 冷度缩放 ==============================

        /// <summary>
        /// 纯映射：强度落在适宜区间 [8, 12] 内冷度缩放恒为 1；弱光侧线性升到
        /// <see cref="WaterParticle.LightColdScaleMax"/>（0 处），强光侧线性降到
        /// <see cref="WaterParticle.LightColdScaleMin"/>（2×上沿 = 24 处饱和）——两侧都随强度连续变化。
        /// </summary>
        [Test]
        public void LightColdScale_IsFlatInComfortBand_AndChangesGraduallyOutside()
        {
            Assert.AreEqual(1f, WaterParticle.LightColdScale(WaterParticle.LightComfortIntensityMin), 1e-6f, "下沿（8）");
            Assert.AreEqual(1f, WaterParticle.LightColdScale(10f), 1e-6f, "区间内");
            Assert.AreEqual(1f, WaterParticle.LightColdScale(WaterParticle.LightComfortIntensityMax), 1e-6f, "上沿（12）");

            Assert.AreEqual(WaterParticle.LightColdScaleMax, WaterParticle.LightColdScale(0f), 1e-6f,
                "光照为 0：最易结冰");
            Assert.AreEqual(WaterParticle.LightColdScaleMax, WaterParticle.LightColdScale(-3f), 1e-6f,
                "非法负值按 0 处理");
            Assert.AreEqual(1f + (WaterParticle.LightColdScaleMax - 1f) * 0.5f, WaterParticle.LightColdScale(4f), 1e-6f,
                "弱光侧线性插值（4 是 0..8 的中点）");

            Assert.AreEqual(WaterParticle.LightColdScaleMin, WaterParticle.LightColdScale(24f), 1e-6f,
                "2× 上沿：最难结冰");
            Assert.AreEqual(WaterParticle.LightColdScaleMin, WaterParticle.LightColdScale(40f), 1e-6f,
                "更热处饱和");
            Assert.AreEqual(1f + (WaterParticle.LightColdScaleMin - 1f) * 0.5f, WaterParticle.LightColdScale(18f), 1e-6f,
                "强光侧线性插值（18 是 12..24 的中点）");

            Assert.Greater(WaterParticle.LightColdScale(0f), WaterParticle.LightColdScale(4f), "越弱越易结冰（单调）");
            Assert.Greater(WaterParticle.LightColdScale(6f), WaterParticle.LightColdScale(16f), "越强越难结冰（单调）");
        }

        /// <summary>
        /// 行为：同一份「凉但没凉透」的水（4.5°，高于适宜光下的蔓延门 4.0），
        /// 在**弱光**（强度 0 ⇒ 门 4.8）下冻得上，在适宜光（强度 10 ⇒ 门 4.0）下不冻。
        /// </summary>
        [Test]
        public void WeakLight_FreezesWaterThatComfortLightWouldNot()
        {
            const float t = 4.5f;

            var comfort = new ParticleWorld(9, 9, 20261013u);
            Ground(comfort, 1);
            comfort.Set(3, 2, Ice);
            comfort.Set(4, 2, Water);
            UseComfortLight(comfort);
            SetTemperature(comfort, 4, 2, t);
            RandomTick(comfort, Water, 4, 2);

            Assert.AreEqual(Water, comfort.GetCell(4, 2).typeId,
                "光照适宜（10）：门槛就是标定的 4.0°，4.5° 的水还不够冷");

            var weak = new ParticleWorld(9, 9, 20261013u);
            Ground(weak, 1);
            weak.Set(3, 2, Ice);
            weak.Set(4, 2, Water);
            weak.Light.Intensity = 0f;
            SetTemperature(weak, 4, 2, t);
            RandomTick(weak, Water, 4, 2);

            Assert.AreEqual(Ice, weak.GetCell(4, 2).typeId,
                "光照最弱（0）：门槛抬到 4.8°，同一份水就冻上了——「越弱越易结冰」");
        }

        /// <summary>
        /// 行为：**强光**（强度 24 ⇒ 门槛乘 0.6）让同一份冷水冻不上、同一块冰化得更早；
        /// **弱光**（强度 0 ⇒ 门槛乘 1.2）反过来把冰压住不化。两侧共用同一份缩放。
        /// </summary>
        [Test]
        public void StrongLight_HardensFreezingAndSoftensMelting()
        {
            var comfort = new ParticleWorld(9, 9, 20261013u);
            Ground(comfort, 1);
            comfort.Set(3, 2, Ice);
            comfort.Set(4, 2, Water);
            UseComfortLight(comfort);
            SetTemperature(comfort, 4, 2, 3f);
            RandomTick(comfort, Water, 4, 2);
            Assert.AreEqual(Ice, comfort.GetCell(4, 2).typeId, "光照适宜：3° 的水会冻");

            var strong = new ParticleWorld(9, 9, 20261013u);
            Ground(strong, 1);
            strong.Set(3, 2, Ice);
            strong.Set(4, 2, Water);
            strong.Light.Intensity = 24f;
            SetTemperature(strong, 4, 2, 3f);
            RandomTick(strong, Water, 4, 2);
            Assert.AreEqual(Water, strong.GetCell(4, 2).typeId,
                "强光（24）：门槛压到 2.4°，同一份 3° 的水就冻不上了——「越强越难结冰」");

            var melting = new ParticleWorld(9, 9, 20261013u);
            Ground(melting, 1);
            melting.Set(4, 2, Ice);
            UseComfortLight(melting);
            SetTemperature(melting, 4, 2, 6.5f);
            RandomTick(melting, Ice, 4, 2);
            Assert.AreEqual(Water, melting.GetCell(4, 2).typeId, "光照适宜：6.5° 的冰化掉");

            var weak = new ParticleWorld(9, 9, 20261013u);
            Ground(weak, 1);
            weak.Set(4, 2, Ice);
            weak.Light.Intensity = 0f;
            SetTemperature(weak, 4, 2, 6.5f);
            RandomTick(weak, Ice, 4, 2);
            Assert.AreEqual(Ice, weak.GetCell(4, 2).typeId,
                "弱光（0）：融点抬到 7.2°，同一块 6.5° 的冰留得住——弱光下冰更「顽固」");
        }

        // ============================== 潜热与热容差（守恒） ==============================

        /// <summary>
        /// 结冰放热：相变后本格温度恰好上升 <see cref="IceParticle.FusionTemperatureStep"/>，
        /// 热量增量恰好是「+C_冰·s − ΔC·T」——热容差项保证相变瞬间温度不跳变。
        /// </summary>
        [Test]
        public void Freezing_ReleasesLatentHeatAndPreservesTemperature()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(3, 2, Ice);
            world.Set(4, 2, Water);
            UseComfortLight(world);
            const float t0 = 1.0f;
            SetTemperature(world, 4, 2, t0);
            float heatBefore = world.Heat.GetHeat(4, 2);

            RandomTick(world, Water, 4, 2);

            Assert.AreEqual(Ice, world.GetCell(4, 2).typeId, "应已结冰");
            float expectedDelta = IceParticle.HeatCapacity * IceParticle.FusionTemperatureStep
                - IceParticle.HeatCapacityDelta * t0;
            Assert.AreEqual(heatBefore + expectedDelta, world.Heat.GetHeat(4, 2), 1e-3f,
                "结冰的热量增量 = 潜热（+C_冰·s）− 热容差（ΔC·T）");
            Assert.AreEqual(t0 + IceParticle.FusionTemperatureStep, world.Heat.GetTemperature(4, 2), 1e-3f,
                "结冰让本格升温 s 度（放热）");
        }

        /// <summary>融化吸热：相变后本格温度恰好下降 s 度，热量增量 = ΔC·T − C_水·s。</summary>
        [Test]
        public void Melting_AbsorbsLatentHeatAndPreservesTemperature()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(4, 2, Ice);
            UseComfortLight(world);
            const float t0 = 7.5f;
            SetTemperature(world, 4, 2, t0);
            float heatBefore = world.Heat.GetHeat(4, 2);

            RandomTick(world, Ice, 4, 2);

            Assert.AreEqual(Water, world.GetCell(4, 2).typeId, "应已融化");
            float expectedDelta = IceParticle.HeatCapacityDelta * t0
                - WaterParticle.HeatCapacity * IceParticle.FusionTemperatureStep;
            Assert.AreEqual(heatBefore + expectedDelta, world.Heat.GetHeat(4, 2), 1e-3f,
                "融化的热量增量 = 热容差（ΔC·T）− 潜热（C_水·s）");
            Assert.AreEqual(t0 - IceParticle.FusionTemperatureStep, world.Heat.GetTemperature(4, 2), 1e-3f,
                "融化让本格降温 s 度（吸热）");
        }

        /// <summary>
        /// 冻融往返可逆（反热泵回归）：结冰再融化之后，整条链的热账必须等于「原水热 + 期间注入的热 ×
        /// C_水/C_冰」。若潜热写成两个方向同一个 Q 常数，往返会净产热（s·ΔC·C_冰/C_水），这条会红。
        /// </summary>
        [Test]
        public void FreezeThenMelt_RoundTripIsHeatConservative()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(3, 2, Ice);
            world.Set(4, 2, Water);
            UseComfortLight(world);
            const float t0 = 1.0f;
            SetTemperature(world, 4, 2, t0);
            float waterHeatBefore = world.Heat.GetHeat(4, 2);

            RandomTick(world, Water, 4, 2);
            Assert.AreEqual(Ice, world.GetCell(4, 2).typeId, "先结冰");

            // 把冰加热到刚好越过融化门：这一步注入的热量记在账上，其余必须由潜热/热容差自己解释。
            float iceHeatBeforeMelt = world.Heat.GetHeat(4, 2);
            float warmTarget = IceParticle.MeltThresholdAt(world, 0) + 0.5f;
            world.Heat.SetHeat(4, 2, warmTarget * IceParticle.HeatCapacity);
            float injected = world.Heat.GetHeat(4, 2) - iceHeatBeforeMelt;

            RandomTick(world, Ice, 4, 2);
            Assert.AreEqual(Water, world.GetCell(4, 2).typeId, "再融化");

            float expected = waterHeatBefore
                + injected * (WaterParticle.HeatCapacity / IceParticle.HeatCapacity);
            Assert.AreEqual(expected, world.Heat.GetHeat(4, 2), 1e-2f,
                "冻融往返的热账 = 原水热 + 注入热 × C_水/C_冰（潜热与热容差必须互为逆运算，否则净产热）");
        }

        // ============================== 静止固体、守恒与渲染 ==============================

        /// <summary>冰在原地不动：泡在水里、悬在空中都不落不浮，水也推不动它（rank 3 固体）。</summary>
        [Test]
        public void Ice_StaysInPlace_AcrossMotionFrames()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(2, 5, Ice);   // 空中
            world.Set(5, 2, Ice);   // 水里：下面两格水
            world.Set(5, 3, Water);
            world.Set(5, 4, Water);

            for (int i = 0; i < 200; i++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(Ice, world.GetCell(2, 5).typeId, "空中的冰不下落");
            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(2, 4).typeId, "空中的冰下方仍是空气");
            Assert.AreEqual(Ice, world.GetCell(5, 2).typeId, "水里的冰不上浮也不下沉");
            Assert.AreEqual(Water, world.GetCell(5, 4).typeId, "水没能把冰挤走");
        }

        /// <summary>水账守恒：水 → 冰 → 水 的全过程里「水格 + 冰格 + 湿度」始终不变。</summary>
        [Test]
        public void FreezeAndMelt_ConserveWaterAccount()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(3, 2, Ice);
            world.Set(4, 2, Water);
            world.Set(5, 2, Water);
            UseComfortLight(world);

            float before = WaterAccount(world);

            SetTemperature(world, 4, 2, 3.5f);
            SetTemperature(world, 5, 2, 3.5f);
            RandomTick(world, Water, 4, 2);
            RandomTick(world, Water, 5, 2);

            Assert.AreEqual(3, CountType(world, Ice), "相邻的两格水面都该冻上");
            Assert.AreEqual(before, WaterAccount(world), 1e-4f, "冻结不改变水量账");

            for (int y = 2; y <= 3; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == Ice)
                    {
                        SetTemperature(world, x, y, IceParticle.MeltThresholdAt(world, 0) + 1f);
                        RandomTick(world, Ice, x, y);
                    }
                }
            }

            Assert.AreEqual(0, CountType(world, Ice), "全部化回水");
            Assert.AreEqual(before, WaterAccount(world), 1e-4f, "融化同样不改变水量账");
        }

        /// <summary>冰挡光、封住水面：冰下的暖水不再蒸发（正上方不是气体 ⇒ 慢蒸发门直接 false，与审计 B3 同款）。</summary>
        [Test]
        public void WaterUnderIce_DoesNotEvaporate()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            Ground(world, 1);
            world.Set(4, 2, Water);
            world.Set(4, 3, Ice);
            SetTemperature(world, 4, 2, 30f);   // 暖到足以慢蒸发

            for (int i = 0; i < 5000; i++)
            {
                Logic(world, Water, 4, 2);
            }

            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "冰封水面下水不蒸发");
            Assert.AreEqual(Water, world.GetCell(4, 2).typeId, "也不会被冻上（30° 远高于冰点）");
        }

        /// <summary>冰算地形：<see cref="CreatureTerrain.IsSolid"/> 认它（冻湖可以走），但 <c>IsWater</c> 不认。</summary>
        [Test]
        public void Ice_CountsAsTerrainForCreatures_ButNotAsWater()
        {
            var world = new ParticleWorld(9, 9, 20261013u);
            world.Set(4, 2, Ice);
            world.Set(5, 2, Water);

            Assert.IsTrue(CreatureTerrain.IsSolid(world, 4, 2), "冰是挡动物的固体（冻湖可走）");
            Assert.IsFalse(CreatureTerrain.IsWater(world, 4, 2), "冰不是水（不参与淹没 / 浮力）");
            Assert.IsFalse(CreatureTerrain.IsSolid(world, 5, 2), "水照旧不是固体");
        }

        /// <summary>
        /// 浅色描边：冰与空气（或水）交界处**向 <see cref="ParticlePixelBuffer.OutlineLightColor"/> 提亮**，
        /// 与其它固体的「调暗描边」相反——冰面反光的观感。
        /// </summary>
        [Test]
        public void Ice_OutlineIsLighterThanBaseColor()
        {
            var world = new ParticleWorld(3, 3, 7u);
            world.Set(1, 1, Ice);

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, new Color32(9, 8, 7, 255));

            Cell cell = world.GetCell(1, 1);
            Color32 baseColor = ParticleRegistry.Types[Ice].Info.Palette[cell.shade];
            Color32 expected = Color32.Lerp(
                baseColor, ParticlePixelBuffer.OutlineLightColor, ParticlePixelBuffer.OutlineLightenFactor);

            Assert.AreEqual(expected, pixels[world.Index(1, 1)], "冰的描边应向浅色插值");
            Assert.Greater((int)pixels[world.Index(1, 1)].r, (int)baseColor.r, "描边后的红分量高于基底色（提亮）");
        }

        /// <summary>
        /// 固液交界同样触发浅色描边：把冰的其余三邻都填成**不触发描边**的实体（石头），
        /// 只留右侧一格水——提亮就只可能来自固液交界那条判据。
        /// </summary>
        [Test]
        public void Ice_OutlineLightensAtLiquidBoundary()
        {
            var world = new ParticleWorld(3, 3, 7u);
            world.Set(1, 0, Stone);   // 正下方：石头（挡实体，不触发描边）
            world.Set(0, 1, Stone);   // 左侧：石头
            world.Set(1, 2, Stone);   // 正上方：石头（顺手把天空关掉）
            world.Set(1, 1, Ice);
            world.Set(2, 1, Water);   // 右侧：水（固液交界）

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, new Color32(9, 8, 7, 255));

            Cell cell = world.GetCell(1, 1);
            Color32 baseColor = ParticleRegistry.Types[Ice].Info.Palette[cell.shade];
            Assert.Greater((int)pixels[world.Index(1, 1)].r, (int)baseColor.r,
                "挨着水的冰格也提亮（固液交界与背景交界同一判据）");
        }

        // ============================== 世界级集成与守卫 ==============================

        /// <summary>
        /// 整帧集成：冷池开随机刻跑 <c>StepLogic</c> 会在水面冻出冰（随机刻路径整条通）；
        /// 同种子的两个世界逐位一致（类型 / shade / flags / 热场）。
        /// </summary>
        [Test]
        public void StepLogic_FreezesColdPond_Deterministically()
        {
            ParticleWorld a = BuildColdPond(20261013u);
            ParticleWorld b = BuildColdPond(20261013u);

            for (int i = 0; i < 300; i++)
            {
                a.StepLogic();
                b.StepLogic();
            }

            Assert.Greater(CountType(a, Ice), 0, "冷池的水面应在 300 逻辑帧内结出冰");

            for (int i = 0; i < a.CellCount; i++)
            {
                Assert.AreEqual(a.Cells[i].typeId, b.Cells[i].typeId, $"第 {i} 格的类型应逐位一致");
                Assert.AreEqual(a.Cells[i].shade, b.Cells[i].shade, $"第 {i} 格的 shade 应逐位一致");
                Assert.AreEqual((byte)(a.Cells[i].flags & CellFlags.ProcessedParityMask),
                    (byte)(b.Cells[i].flags & CellFlags.ProcessedParityMask), $"第 {i} 格的 flags 应逐位一致");
                Assert.AreEqual(a.Heat.Values[i], b.Heat.Values[i], $"第 {i} 格的热量应逐位一致");
            }
        }

        /// <summary>先冻后化：冷池的水面在暗夜里结冰，开灯强日照足够久之后全部化回水，水量账守恒。</summary>
        [Test]
        public void ColdPond_FreezesInTheDark_AndMeltsUnderDaylight()
        {
            ParticleWorld world = BuildColdPond(20261013u);
            float accountBefore = WaterAccount(world);

            for (int i = 0; i < 600; i++)
            {
                world.StepLogic();   // 冷夜：无光照、水本来就在环境温度
            }

            Assert.Greater(CountType(world, Ice), 0, "冷夜过后水面应结冰");
            Assert.AreEqual(accountBefore, WaterAccount(world), 1e-4f, "结冰不改变水量账");

            world.Light.Enabled = true;
            world.Light.Intensity = 10f;
            world.Light.LitTicks = int.MaxValue;
            world.Light.DarkTicks = 0;
            for (int i = 0; i < 600; i++)
            {
                world.StepLogic();   // 白天：强日照（浅水会升温蒸发，水账连湿度一起算）
            }

            Assert.AreEqual(0, CountType(world, Ice), "白天日照足够久应把冰全部化回水");
            Assert.Greater(CountType(world, Water), 0, "化回来的还是水");
            Assert.AreEqual(accountBefore, WaterAccount(world), 1e-3f, "冻融 + 蒸发全程水量账守恒");
        }

        /// <summary>
        /// 隔离类型表守卫：世界没注册冰（<see cref="ParticleIds.Ice"/> 越界 / 空槽）时，冷水水面的随机刻
        /// 既不结冰、也不消耗随机流、更不会让 <c>SetPhase</c> 打错误日志（Unity 把意外的 error 日志算失败）。
        /// </summary>
        [Test]
        public void IsolatedTypeTableWorld_NeverFreezes()
        {
            // 隔离表：第 1 个类型是水、第 2 个是石头（地板用）——表里没有冰。
            ParticleWorld world = new ParticleWorld(
                9, 9, 20261013u, TestTables.Build(new WaterParticle(), new StoneParticle()));
            world.Set(4, 0, 2);
            world.Set(4, 1, 1);
            SetTemperature(world, 4, 1, 0f);

            ParticleWorld control = new ParticleWorld(
                9, 9, 20261013u, TestTables.Build(new WaterParticle(), new StoneParticle()));
            control.Set(4, 0, 2);
            control.Set(4, 1, 1);

            for (int i = 0; i < 10; i++)
            {
                world.Types[1].OnRandomTick(world, 4, 1);
            }

            Assert.AreEqual(1, world.GetCell(4, 1).typeId, "隔离表里没有冰这种物质：水不结冰");
            Assert.AreEqual(control.NextUInt(), world.NextUInt(), "也不该消耗任何随机流");
        }

        /// <summary>冷池：石头地板 + 6×3 水柱，两侧石壁，上方大气（水面暴露）——冷夜配置（无光照、随机刻调高）。</summary>
        private static ParticleWorld BuildColdPond(uint seed)
        {
            var world = new ParticleWorld(12, 12, seed);
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                }
            }

            Ground(world, 0);
            for (int y = 1; y <= 3; y++)
            {
                world.Set(2, y, Stone);
                world.Set(9, y, Stone);
                for (int x = 3; x <= 8; x++)
                {
                    world.Set(x, y, Water);
                }
            }

            world.Heat.RadiativeLoss = 0.05f;         // 生产辐射率
            world.Heat.AirThermalEmissivity = 0.1f;   // 生产空气辐射率
            world.Light.Enabled = false;              // 冷夜
            world.RandomTicksPerStep = 48;            // 小世界调高频随机刻：144 格 / 48 抽 ⇒ 每格每帧约 1/3 命中
            return world;
        }
    }
}
