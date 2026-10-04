using NUnit.Framework;
using UnityEngine;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 湿土（<see cref="WetSoilParticle"/>）与土的吸水反应：水被土喝掉、
    /// 湿土在有水压时把这份水往下渗 / 侧向渗（无压则永久持水）、受热往上送 / 从表面蒸发进空气，以及生产注册路径的回填与标签。
    /// </summary>
    /// <remarks>
    /// <para>跨类型相变（土 ↔ 湿土）依赖 <see cref="ParticleIds.WetSoil"/> / <see cref="ParticleIds.Soil"/>
    /// 的回填，隔离类型表里这些字段是 0，因此本组测试走 <see cref="ParticleRegistration.RegisterAll"/>
    /// 的全局表——与 <c>WeatherParticleTests</c> 的取舍一致。</para>
    /// <para>反应都在**逻辑帧**（<see cref="ParticleWorld.StepLogic"/>）里跑，运动在**运动帧**（<see cref="ParticleWorld.StepMotion"/>）；
    /// 前者也含一次运动扫描，所以只关心反应时用 1 格宽的井（水无处横流、土无处斜落），结果完全确定。</para>
    /// <para>水总量 = 水格 + 湿土格（每格湿土含一份水）+ 湿度场总量，守恒断言一律用它；
    /// 热量守恒用全场 Q 之和。水迁移（<c>GiveWater</c>）带着 <c>T(源) × 1.2</c> 走，蒸发吸走一份潜热。</para>
    /// <para>涉及随机流的断言（上行门、先判后摇）**直接调类型的 <c>OnLogic</c>**，避开 <c>StepLogic</c> 里
    /// 运动扫描等其它随机消费者，从而能按「构造时 <c>Set</c> 的调用次数」精确推算掷骰结果。</para>
    /// </remarks>
    public sealed class WetSoilParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Sand
        {
            get { return ParticleIds.Sand; }
        }

        private static ushort Soil
        {
            get { return ParticleIds.Soil; }
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort WetSoil
        {
            get { return ParticleIds.WetSoil; }
        }

        private static float WetSoilHeatCapacity
        {
            get { return ParticleRegistry.Types[WetSoil].Info.HeatCapacity; }
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

        /// <summary>水总量：水格 + 湿土格（每格含一份水）+ 湿度场。</summary>
        private static float TotalWater(ParticleWorld world)
        {
            return CountType(world, Water) + CountType(world, WetSoil) + world.Humidity.Sum();
        }

        /// <summary>全场热量 Q 之和（守恒断言用）。</summary>
        private static float TotalHeat(ParticleWorld world)
        {
            float total = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                total += world.Heat.Values[i];
            }

            return total;
        }

        private static float Luminance(Color32 color)
        {
            return 0.299f * color.r + 0.587f * color.g + 0.114f * color.b;
        }

        /// <summary>把 (x,y) 加热到干燥阈值以上。</summary>
        private static void HeatAboveDryingPoint(ParticleWorld world, int x, int y)
        {
            world.Heat.SetHeat(x, y, WetSoilParticle.DryingTemperature * WetSoilHeatCapacity + 1f);
        }

        /// <summary>
        /// 生产注册路径：RegisterAll 必须回填出非 0 且不与其它类型重复的湿土 id，带 Solid 标签、受重力、非静态。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsSolidWetSoilId()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, WetSoil, "湿土 id 应被回填");
            Assert.AreNotEqual(Stone, WetSoil);
            Assert.AreNotEqual(Sand, WetSoil);
            Assert.AreNotEqual(Soil, WetSoil);
            Assert.AreNotEqual(Water, WetSoil);
            Assert.AreNotEqual(ParticleIds.Atmosphere, WetSoil);

            ParticleTypeInfo info = ParticleRegistry.Types[WetSoil].Info;
            Assert.IsTrue((info.Tags & ParticleTags.Solid) != 0, "湿土必须带 Solid 标签（仍是固体粉末）");
            Assert.IsTrue(info.AffectedByGravity, "湿土受重力");
            Assert.IsFalse(info.IsStatic, "湿土不是静态类型");
        }

        /// <summary>水把正下方的土变成湿土，**自己随即被吸干**，水格的热量并进湿土（总热量不变）。</summary>
        [Test]
        public void Water_WetsSoilBeneathIt_AndIsConsumed()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, Water);
            world.Heat.SetHeat(0, 1, 3f);
            world.Heat.SetHeat(0, 2, 5f);
            float heatBefore = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                heatBefore += world.Heat.Values[i];
            }

            float waterBefore = TotalWater(world);
            world.StepLogic();

            Assert.AreEqual(WetSoil, world.GetCell(0, 1).typeId, "正上方是水，土应吸饱水变成湿土");
            Assert.AreEqual(0, CountType(world, Water), "水润湿一格后应被吸干");
            Assert.IsTrue(world.IsAir(0, 2), "水原来那一格应空出来");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-4f, "水总量守恒（水格 → 湿土格）");

            float heatAfter = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                heatAfter += world.Heat.Values[i];
            }

            Assert.AreEqual(heatBefore, heatAfter, 1e-4f, "水被吸走时它的热量应并进湿土（总热量守恒）");
        }

        /// <summary>
        /// 有压才渗（核心回归，锁死「湿斑像液体一样沉底」的现象）：**孤立湿土永久持水**——
        /// 正上方是空气、下方有干土也不渗（田间持水量：毛管力拽住水），湿斑不会自己沉底。
        /// </summary>
        [Test]
        public void WetSoil_WithoutPressureFromAbove_HoldsWaterForever()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            for (int y = 1; y <= 6; y++)
            {
                world.Set(0, y, Soil);
            }

            world.Set(0, 6, WetSoil); // 土柱顶一格湿土，上方是空气 = 无压

            for (int i = 0; i < 60; i++)
            {
                world.StepLogic();
                Assert.AreEqual(1f, TotalWater(world), 1e-3f, $"第 {i} 帧水总量守恒");
            }

            Assert.AreEqual(WetSoil, world.GetCell(0, 6).typeId, "无压湿土应原地持水，一步也不往下渗");
            for (int y = 1; y <= 5; y++)
            {
                Assert.AreEqual(Soil, world.GetCell(0, y).typeId, $"第 {y} 层应始终是干土");
            }
        }

        /// <summary>
        /// 有压才渗的另一半：湿土正上方是**水**时被压着，把水交给正下方的干土（自己变干）；
        /// 随后这格干土把上方的水喝掉重新变湿——湿润锋面整体下移一格、身后保持湿润。
        /// </summary>
        [Test]
        public void WetSoil_PressedByWaterAbove_PercolatesDownAndStaysWetBehind()
        {
            ParticleWorld world = new ParticleWorld(1, 6, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, Soil);
            world.Set(0, 3, WetSoil);
            world.Set(0, 4, Water);

            for (int i = 0; i < 60 && CountType(world, Water) > 0; i++)
            {
                world.StepLogic();
                Assert.AreEqual(2f, TotalWater(world), 1e-3f, $"第 {i} 帧水总量守恒（1 水格 + 1 湿土格）");
            }

            Assert.AreEqual(0, CountType(world, Water), "水格最终被吸进土里");
            Assert.AreEqual(2, CountType(world, WetSoil), "两份水都在土里（锋面下移 + 身后保持湿润）");
            Assert.AreEqual(WetSoil, world.GetCell(0, 3).typeId, "原湿土格喝掉上方的水重新变湿（顶部持水）");
        }

        /// <summary>
        /// 受热上行：湿土温度达到阈值时，水改往上走——润湿上方那格干土，自身变干。
        /// 上行有 <see cref="WetSoilParticle.PercolationDenominator"/>（= 50%）的概率门，因此直接调逻辑钩子并
        /// **最多试若干次**（避开 <c>StepLogic</c> 里运动扫描的随机消耗与热传导降温）；上方是干土而非空气，
        /// 这条路唯一的可能动作就是上行（不存在蒸发 / 下渗的歧义）。
        /// </summary>
        [Test]
        public void HotWetSoil_WetsTheDrySoilAbove_AndTurnsDry()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, WetSoil);
            world.Set(0, 3, Soil);
            HeatAboveDryingPoint(world, 0, 2);

            for (int i = 0; i < 8 && world.GetCell(0, 2).typeId == WetSoil; i++)
            {
                ParticleRegistry.Types[WetSoil].OnLogic(world, 0, 2);
            }

            Assert.AreEqual(WetSoil, world.GetCell(0, 3).typeId, "水应上行一格，润湿上方的干土");
            Assert.AreEqual(Soil, world.GetCell(0, 2).typeId, "原湿土应变干");
            Assert.AreEqual(Soil, world.GetCell(0, 1).typeId, "水不应同时往下走");
            Assert.AreEqual(1f, TotalWater(world), 1e-3f, "水总量守恒");
        }

        /// <summary>
        /// 上行概率门：受热上行与下渗同分母（50%）——同一布局换种子，掷中就走、掷不中就不走，
        /// 用来验证「上行不再每帧必走」（B7 的对称化）。
        /// </summary>
        [Test]
        public void HotWetSoil_UpwardMove_IsProbabilityGated()
        {
            // 种子 1 → 第 5 次 NextUInt 为奇（门不通过）；种子 2 → 为偶（门通过）。
            ParticleWorld miss = BuildHotWell(1u);
            ParticleRegistry.Types[WetSoil].OnLogic(miss, 0, 2);
            Assert.AreEqual(WetSoil, miss.GetCell(0, 2).typeId, "掷不中时水应留在原地");
            Assert.AreEqual(Soil, miss.GetCell(0, 3).typeId, "掷不中时上方干土保持干");

            ParticleWorld hit = BuildHotWell(2u);
            ParticleRegistry.Types[WetSoil].OnLogic(hit, 0, 2);
            Assert.AreEqual(WetSoil, hit.GetCell(0, 3).typeId, "掷中时水应上行一格");
            Assert.AreEqual(Soil, hit.GetCell(0, 2).typeId, "掷中时原湿土变干");
        }

        /// <summary>1 格宽热井：石底 / 干土 / 湿土（加热）/ 干土顶盖——上行是唯一出路。</summary>
        private static ParticleWorld BuildHotWell(uint seed)
        {
            ParticleWorld world = new ParticleWorld(1, 5, seed);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, WetSoil);
            world.Set(0, 3, Soil);
            HeatAboveDryingPoint(world, 0, 2);
            return world;
        }

        /// <summary>
        /// B7 温度门：热土里的水**不下渗**（上方被石头封死、无处蒸发），因此热源消失后不会立刻往回渗，
        /// 相邻两格不再来回走。常温且**有压**的同布局才会下渗（见 <see cref="WetSoil_PressedByWaterAbove_PercolatesDownAndStaysWetBehind"/>）。
        /// </summary>
        [Test]
        public void HotWetSoil_DoesNotPercolateWhileHot()
        {
            ParticleWorld world = new ParticleWorld(1, 4, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, WetSoil);
            world.Set(0, 3, Stone);
            HeatAboveDryingPoint(world, 0, 2);

            for (int i = 0; i < 50; i++)
            {
                ParticleRegistry.Types[WetSoil].OnLogic(world, 0, 2);
            }

            Assert.AreEqual(WetSoil, world.GetCell(0, 2).typeId, "热土里的水不该下渗（B7 温度门）");
            Assert.AreEqual(Soil, world.GetCell(0, 1).typeId, "正下方干土不应接到水");
            Assert.AreEqual(1f, TotalWater(world), 1e-3f, "水总量守恒");
        }

        /// <summary>
        /// M10 侧向渗流：有压（上方有水）且正下方被石头挡住时，水按随机顺序改试**同行左右**的干土邻居，
        /// 而不是原地卡死。
        /// </summary>
        [Test]
        public void WetSoil_BlockedBelow_FlowsSidewaysIntoDrySoil()
        {
            ParticleWorld world = new ParticleWorld(3, 5, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(1, 0, Stone);
            world.Set(2, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(1, 1, WetSoil);
            world.Set(2, 1, Soil);
            world.Set(1, 2, Water); // 水压来源：有压才渗
            // 湿土正下方是石头；上方是水（常温，不蒸发），左右各一格干土。

            for (int i = 0; i < 50 && world.GetCell(1, 1).typeId == WetSoil; i++)
            {
                ParticleRegistry.Types[WetSoil].OnLogic(world, 1, 1);
            }

            Assert.AreEqual(Soil, world.GetCell(1, 1).typeId, "水应离开被挡住的这一格");
            bool movedLeft = world.GetCell(0, 1).typeId == WetSoil;
            bool movedRight = world.GetCell(2, 1).typeId == WetSoil;
            Assert.IsTrue(movedLeft ^ movedRight, "水应侧向流入左右干土之一（且只有一份）");
            Assert.AreEqual(1, CountType(world, WetSoil), "全程只有一份水在土里");
            Assert.AreEqual(2f, TotalWater(world), 1e-3f, "水总量守恒（1 湿土格 + 上方 1 水格）");
        }

        /// <summary>
        /// C8 热随水走：搬运一份水时把 <c>T(源) × 1.2</c> 的热量一并搬过去
        /// （1.2 = 湿土热容 2.0 − 干土热容 0.8），源格扣同样多，总热量严格守恒、两侧温度不跳。
        /// </summary>
        [Test]
        public void WetSoil_GiveWaterCarriesHeatWithTheWaterUnit()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, WetSoil);
            world.Set(0, 3, Water); // 水压来源：有压才渗
            // T = Q / C = 40 / 2 = 20（常温，走下渗）。
            world.Heat.SetHeat(0, 2, 40f);
            float heatBefore = TotalHeat(world);

            for (int i = 0; i < 50 && world.GetCell(0, 2).typeId == WetSoil; i++)
            {
                ParticleRegistry.Types[WetSoil].OnLogic(world, 0, 2);
            }

            Assert.AreEqual(WetSoil, world.GetCell(0, 1).typeId, "水应下渗一格");
            Assert.AreEqual(Soil, world.GetCell(0, 2).typeId, "原湿土变干");
            // 搬走的热 = T(源) × 1.2 = 20 × 1.2 = 24。
            Assert.AreEqual(24f, world.Heat.GetHeat(0, 1), 1e-3f, "目标格应收到 +T(源)×1.2");
            Assert.AreEqual(16f, world.Heat.GetHeat(0, 2), 1e-3f, "源格应扣掉 −T(源)×1.2");
            Assert.AreEqual(heatBefore, TotalHeat(world), 1e-3f, "总热量严格守恒");
        }

        /// <summary>
        /// B11：没有可接水的干土目标时，湿土**不消耗随机流**（先判目标、再摇骰）。
        /// 否则随机流会随场上湿土格数漂移，破坏确定性。
        /// </summary>
        [Test]
        public void WetSoil_NoReachableTarget_DoesNotConsumeRandomStream()
        {
            // 参照世界：同样构造后直接取下一个随机数。
            ParticleWorld reference = new ParticleWorld(2, 3, 777u);
            reference.Set(0, 0, Stone);
            reference.Set(0, 1, WetSoil);
            reference.Set(0, 2, Water); // 水压来源：有压才渗，否则在压力门就提前返回、测不到目标短路
            uint expected = reference.NextUInt();

            ParticleWorld world = new ParticleWorld(2, 3, 777u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, WetSoil);
            world.Set(0, 2, Water);
            // (0,1)：有压（上方是水），但下方是石头、左右无干土 —— 无目标，应当短路、不摇骰。
            ParticleRegistry.Types[WetSoil].OnLogic(world, 0, 1);

            Assert.AreEqual(expected, world.NextUInt(), "无可接水目标时不应消耗随机流（B11 先判后摇）");
            Assert.AreEqual(WetSoil, world.GetCell(0, 1).typeId, "没有目标，水留在原地");
        }

        /// <summary>
        /// 受热蒸发：湿土上方是空气且空气没吸饱时，像水粒子慢蒸发一样把一份水汽摊进空气，自身变干，
        /// 并从土壤吸走相变潜热（C1）。
        /// **直接调该类型的逻辑钩子**（不走整帧），因此测的就是"播撒本身"：摊出来的水汽每格都低于可见阈值。
        /// </summary>
        [Test]
        public void HotWetSoil_UnderAir_EvaporatesInvisibly()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(4, 1, WetSoil);
            HeatAboveDryingPoint(world, 4, 1);
            float heatBefore = world.Heat.GetHeat(4, 1);

            ParticleRegistry.Types[WetSoil].OnLogic(world, 4, 1);

            Assert.AreEqual(Soil, world.GetCell(4, 1).typeId, "蒸发后本格变干土");
            Assert.AreEqual(0, CountType(world, WetSoil), "湿土已消失");
            Assert.AreEqual(HumidityField.Unit, world.Humidity.Sum(), 1e-3f, "一份水汽（水总量守恒）");
            Assert.Greater(world.Humidity.Get(4, 2), 0f, "正上方的空气接到水汽");
            Assert.AreEqual(heatBefore - HumidityField.LatentHeatPerUnit, world.Heat.GetHeat(4, 1), 1e-3f,
                "干燥从土壤吸走相变潜热（C1：蒸发 Q 减 LatentHeatPerUnit，可为冷）");

            for (int i = 0; i < world.CellCount; i++)
            {
                Assert.Less(world.Humidity.GetAt(i), HumidityField.VisibleThreshold,
                    "土壤干燥蒸发必须不可见（下标 " + i + "）");
            }
        }

        /// <summary>
        /// 同一条蒸发路径在完整逻辑帧里的表现：湿土变干、水汽进了空气、水总量守恒。
        /// （整帧里湿度场随后会按自己的规则输送、在热处辐合——那是"云"的成因，不属于蒸发播撒的可见性契约。）
        /// </summary>
        [Test]
        public void HotWetSoil_UnderAir_EvaporatesThroughAFrame()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(4, 1, WetSoil);
            HeatAboveDryingPoint(world, 4, 1);
            float waterBefore = TotalWater(world);

            world.StepLogic();

            Assert.AreEqual(Soil, world.GetCell(4, 1).typeId, "蒸发后本格变干土");
            Assert.AreEqual(0, CountType(world, WetSoil), "湿土已消失");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-3f, "水总量守恒（湿土格 → 水汽）");
            Assert.Greater(world.Humidity.Sum(), 0f, "水汽进了空气");
        }

        /// <summary>
        /// 上方空气已经吸饱（≥ <see cref="HumidityField.InvisibleSpreadCap"/>）时蒸不动：
        /// 湿土保持湿土，湿度不增加（饱和水汽压的节流）。
        /// </summary>
        [Test]
        public void HotWetSoil_UnderSaturatedAir_DoesNotEvaporate()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(4, 1, WetSoil);
            world.Humidity.Set(4, 2, HumidityField.InvisibleSpreadCap);
            HeatAboveDryingPoint(world, 4, 1);
            float waterBefore = TotalWater(world);

            world.StepLogic();

            Assert.AreEqual(WetSoil, world.GetCell(4, 1).typeId, "空气吸饱了就不该蒸发，湿土保持不变");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-3f, "水总量守恒");
        }

        /// <summary>
        /// C3 份额门控：石墙口袋里只剩极少的接收格时，干燥这份水汽会摊得太浓、立刻可见——
        /// 此时**不蒸发**（<c>SpreadUnitIntoAir</c> 返回 0），湿土保持湿土、水与热量都不动。
        /// </summary>
        [Test]
        public void HotWetSoil_InStonePocket_KeepsWaterWhenSpreadWouldBeVisible()
        {
            ParticleWorld world = new ParticleWorld(3, 3, 20261004u);
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (x == 1 && y == 2)
                    {
                        continue; // 只留这一格空气当"口袋口"，其余全砌石头。
                    }

                    world.Set(x, y, Stone);
                }
            }

            world.Set(1, 1, WetSoil);
            HeatAboveDryingPoint(world, 1, 1);
            float heatBefore = world.Heat.GetHeat(1, 1);
            float waterBefore = TotalWater(world);

            ParticleRegistry.Types[WetSoil].OnLogic(world, 1, 1);

            Assert.AreEqual(WetSoil, world.GetCell(1, 1).typeId, "接收格太少时不应蒸发，湿土保留");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "没播出去，湿度不增加");
            Assert.AreEqual(heatBefore, world.Heat.GetHeat(1, 1), 1e-3f, "没蒸发就不该吸走潜热");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-4f, "水总量守恒");
        }

        /// <summary>
        /// 没有干土可接、也不是热天：水留在土里。石头/沙/水都接不住水（它们是别的物质，没有"湿"的形态）；
        /// 常温下上方是空气也**不蒸发**——蒸发是受热才发生的事。
        /// </summary>
        [Test]
        public void WetSoil_WithNoDrySoilNeighbour_NeverDries()
        {
            ParticleWorld world = new ParticleWorld(3, 8, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(0, 1, Sand);
            world.Set(1, 1, WetSoil);
            world.Set(2, 1, Stone);
            // 上方是空气：常温下不该蒸发。

            for (int i = 0; i < 40; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(WetSoil, world.GetCell(1, 1).typeId, "四周没有干土、温度也低，湿土不该变干");
            Assert.AreEqual(1, CountType(world, WetSoil), "那一份水仍在湿土里");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "常温湿土不蒸发");
        }

        /// <summary>湿土仍是固体：悬空时照样逐帧下落（运动帧负责，与逻辑帧的水迁移互不干扰）。</summary>
        [Test]
        public void WetSoil_StillFallsLikeASolid()
        {
            ParticleWorld world = new ParticleWorld(3, 10, 20261004u);
            world.Set(1, 0, Stone);
            world.Set(1, 7, WetSoil);

            world.StepMotion();
            Assert.AreEqual(WetSoil, world.GetCell(1, 6).typeId, "运动帧应直落一格");
            Assert.AreEqual(1, CountType(world, WetSoil), "水没有丢");
        }

        /// <summary>湿土的调色板逐档都比干土暗——「颜色更深」是用户可见的契约。</summary>
        [Test]
        public void WetSoilPalette_IsDarkerThanDrySoil()
        {
            Color32[] soil = ParticleRegistry.Types[Soil].Info.Palette;
            Color32[] wet = ParticleRegistry.Types[WetSoil].Info.Palette;

            Assert.AreEqual(soil.Length, wet.Length, "两种土的调色板档数应一致（逐档对应）");
            for (int i = 0; i < soil.Length; i++)
            {
                Assert.Less(Luminance(wet[i]), Luminance(soil[i]), $"第 {i} 档湿土应比干土深");
            }
        }
    }
}
