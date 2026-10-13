using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 水（<see cref="WaterParticle"/>）的流体行为测试：下落、无安息角摊平、被固体沉穿（下落的固体在运动中，照旧可排开液体；静止固体则不能——见地下水规则）、确定性、
    /// 孤立水滴静止不抖动（平地 / 世界底行）、
    /// 液体热对流（暖水换位 / 死区 / 不抢下落 / 夜间海面反超陆面的涌现回归），
    /// 穿冠（水穿叶柱落到冠下 / 叶柱下是固体不穿 / 超过深度上限不穿 / 零随机流消耗），
    /// 以及蒸发（慢蒸发不可见 / 沸腾可见 / 潜热吸热）与生产注册路径的回填与标签。
    /// </summary>
    /// <remarks>
    /// <para>纯流体行为测试走 <see cref="TestTables.Build"/> 的隔离类型表（水 = id 1，搭配的固体 = id 2），
    /// 不触碰全局注册表。</para>
    /// <para>蒸发测试必须走 <see cref="ParticleRegistration.RegisterAll"/> 的生产表：播撒的接收格判据
    /// <see cref="ParticleRules.IsGasOrEmpty"/> 与相变目标 <see cref="ParticleIds.Atmosphere"/> 都依赖
    /// <see cref="ParticleIds"/> 的回填字段，隔离表里它们是 0（契约允许的唯一例外路径，与
    /// <c>WeatherParticleTests</c> 的取舍一致）。</para>
    /// </remarks>
    public sealed class WaterParticleTests
    {
        // 隔离类型表里的 id：TestTables.Build 约定第 n 个粒子的 typeId 是 n + 1。
        private const ushort WaterId = 1;
        private const ushort StoneId = 2;
        private const ushort SandId = 3;

        private static ParticleWorld CreateWorld(int width, int height, uint seed = 12345u)
        {
            return new ParticleWorld(
                width, height, seed,
                TestTables.Build(new WaterParticle(), new StoneParticle()));
        }

        private static ParticleWorld CreateSandWorld(int width, int height, uint seed = 12345u)
        {
            // 水 = 1，石头 = 2（容器地板），沙 = 3。
            return new ParticleWorld(
                width, height, seed,
                TestTables.Build(new WaterParticle(), new StoneParticle(), new SandParticle()));
        }

        private static void FillRow(ParticleWorld world, int y, ushort typeId)
        {
            for (int x = 0; x < world.Width; x++)
            {
                Assert.IsTrue(world.Set(x, y, typeId), $"Set({x},{y}) 应成功");
            }
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

        /// <summary>某类型的最高一格的 y；不存在时返回 -1。</summary>
        private static int HighestTypeY(ParticleWorld world, ushort typeId)
        {
            for (int y = world.Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        return y;
                    }
                }
            }

            return -1;
        }

        private static int RowCount(ParticleWorld world, ushort typeId, int y)
        {
            int count = 0;
            for (int x = 0; x < world.Width; x++)
            {
                if (world.GetCell(x, y).typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 悬空的水每 tick 直落一格（下方是空气）；石头地板把它停在地面上方。
        /// </summary>
        [Test]
        public void Water_FallsOneCellPerTick_UntilFloor()
        {
            ParticleWorld world = CreateWorld(8, 16);
            FillRow(world, 0, StoneId);
            world.Set(4, 10, WaterId);

            world.StepLogic();
            Assert.AreEqual(WaterId, world.GetCell(4, 9).typeId, "第一 tick 应直落一格");
            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(4, 10).typeId);

            for (int i = 0; i < 20; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(WaterId, world.GetCell(4, 1).typeId, "最终应停在石头地板上");
            Assert.AreEqual(1, CountType(world, WaterId), "水量守恒，一滴不丢");
        }

        /// <summary>
        /// 液体无安息角：一坨 2*width 格的水柱倒在平底上，收敛后必须摊成覆盖全宽的两层水平面，
        /// 而不是像粉末那样堆出斜坡。2*width 格水在 width 宽的容器里恰好是 2 层。
        /// </summary>
        /// <remarks>
        /// 容器宽度与初始堆形是**刻意选定**的，不是随手取的常数。横流的唯一长程机制是
        /// 「落差视野」闸门（<see cref="WaterParticle"/> 的 <c>TryFlowSide</c> 扫描
        /// <c>Info.MaxFlowSpread</c> = 5 格），而「第二层少一格 + 上方悬一格孤水」这类
        /// **差一格的终态是动力学极限环**：洞会被左右两侧的水接力推走（闸门一「身后有推」），
        /// 每 tick 可跑多格、比孤水 1 格/tick 的追击更快，一旦追击错过就不再收敛。
        /// 实测：贴左壁的 2×width 水柱在宽度 14 时 100% 卡死；旧设置「宽 20 + 居中 10×4 方块」
        /// 在 seed 12345 下也永久卡在 19/20（HEAD 的旧随机流同样如此——20/40 个种子会卡死，
        /// 与本次 RNG→坐标哈希改动无关，是同一枚硬币的另一面：见
        /// <c>Water_RestsAlone_OnFlatFloor</c> 的几何限制）。
        /// 因此这里改用**贴左壁的窄水柱**：该场景在 60000 个种子 × 两种世界（空气 / 大气填满）下
        /// 全部收敛（最坏 24 tick），断言因此不依赖任何一条幸运的随机轨迹。
        /// </remarks>
        [Test]
        public void Water_LevelsOut_OnFlatFloor()
        {
            const int width = 10;
            ParticleWorld world = CreateWorld(width, 16);
            FillRow(world, 0, StoneId);

            // 2 宽 × width 高的紧凑水柱（2*width 格 = 恰好两层），贴左壁落下：堆体必须真正横流摊开。
            for (int y = 5; y < 5 + width; y++)
            {
                world.Set(0, y, WaterId);
                world.Set(1, y, WaterId);
            }

            for (int i = 0; i < 300; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(2 * width, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(width, RowCount(world, WaterId, 1), "第一层应铺满全宽");
            Assert.AreEqual(width, RowCount(world, WaterId, 2), "第二层应铺满全宽");
            Assert.AreEqual(0, RowCount(world, WaterId, 3), "第三层应全空（表面严格水平）");
        }

        /// <summary>
        /// 回归：世界被大气粒子填满（没有 typeId 0 的格子）时水仍必须摊平。
        /// 曾有的 bug：横流落点判据用 IsAir，大气不是「空气 id」，水永远找不到落点而不再流动。
        /// </summary>
        /// <remarks>
        /// 场景（宽度、水柱形状）与 <see cref="Water_LevelsOut_OnFlatFloor"/> 完全同构，理由见那里的
        /// remarks：窄水柱贴壁落下是**与随机轨迹无关**的稳定收敛场景，而宽容器 + 居中方块会 pin 住
        /// 某一条幸运序列。本变体只多一项：先把整个世界填满大气再倒水（与生产中 driver 的初始化
        /// 顺序一致），验证「摊平不依赖空气格 typeId == 0」。
        /// </remarks>
        [Test]
        public void Water_LevelsOut_InAtmosphereFilledWorld()
        {
            const int width = 10;
            // 水 = 1，石头 = 2，大气 = 3。
            ParticleWorld world = new ParticleWorld(
                width, 16, 12345u,
                TestTables.Build(new WaterParticle(), new StoneParticle(), new AtmosphereParticle()));
            const ushort atmosphereId = 3;

            // 先填满大气，再铺地板、倒水（与生产中 driver 的初始化顺序一致）。
            for (int y = 0; y < world.Height; y++)
            {
                FillRow(world, y, atmosphereId);
            }

            FillRow(world, 0, StoneId);

            // 与 OnFlatFloor 同构：2 宽 × width 高的水柱（2*width 格 = 恰好两层），贴左壁落下。
            for (int y = 5; y < 5 + width; y++)
            {
                world.Set(0, y, WaterId);
                world.Set(1, y, WaterId);
            }

            for (int i = 0; i < 400; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(2 * width, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(width, RowCount(world, WaterId, 1), "第一层应铺满全宽");
            Assert.AreEqual(width, RowCount(world, WaterId, 2), "第二层应铺满全宽");
            Assert.AreEqual(0, RowCount(world, WaterId, 3), "第三层应全空（表面严格水平）");
        }

        /// <summary>
        /// 观感回归：孤立水滴躺在平地（下面是石头、两侧是空气）上必须**静止**——
        /// 往哪边流都不会更低。旧的横流「见空格就跳」，水滴会以 60 Hz 在原地左右瞬移抖动。
        /// 逐 tick 断言位置，抓瞬时离位而不是只看终点。
        /// </summary>
        [Test]
        public void Water_RestsAlone_OnFlatFloor()
        {
            ParticleWorld world = CreateWorld(16, 8);
            FillRow(world, 0, StoneId);
            world.Set(8, 1, WaterId);

            for (int i = 0; i < 120; i++)
            {
                world.StepLogic();
                Assert.AreEqual(WaterId, world.GetCell(8, 1).typeId,
                    $"第 {i} tick：孤立水滴不该离开原地（原地抖动回归）");
                Assert.AreEqual(1, CountType(world, WaterId), "水量守恒");
            }
        }

        /// <summary>
        /// 同上，但水滴直接躺在**世界底行**（正下方越界）上：
        /// 落差视野不得把越界当成「还能继续落」，否则底行水滴会永远往两侧流。
        /// </summary>
        [Test]
        public void Water_RestsAlone_OnWorldBottomRow()
        {
            ParticleWorld world = CreateWorld(16, 8);
            world.Set(8, 0, WaterId);

            for (int i = 0; i < 120; i++)
            {
                world.StepLogic();
                Assert.AreEqual(WaterId, world.GetCell(8, 0).typeId,
                    $"第 {i} tick：底行上的孤立水滴不该离开原地（越界当落差回归）");
                Assert.AreEqual(1, CountType(world, WaterId), "水量守恒");
            }
        }


        /// <summary>
        /// rank 规则的集成验证：沙（Solid，rank 3）从水柱上方落下，沉穿水到底；等量的水被置换上浮。
        /// 沙与水各自守恒。下落的沙在运动中，照旧可排开液体（运动状态闸门——静止的固体则不能，
        /// 见 <see cref="GroundwaterTests"/>）。
        /// </summary>
        [Test]
        public void Sand_SinksThrough_Water()
        {
            ParticleWorld world = CreateSandWorld(8, 12);
            FillRow(world, 0, StoneId);

            // 两侧石墙把水关在一格宽的竖井里——没有侧壁水会横流摊开（那是正确行为，
            // 由 Water_LevelsOut_OnFlatFloor 验证），本测试专看竖直方向上的置换。
            for (int y = 1; y <= 6; y++)
            {
                world.Set(3, y, StoneId);
                world.Set(5, y, StoneId);
            }
            // 三格水柱立在井底，井口上方一颗沙。
            for (int y = 1; y <= 3; y++)
            {
                world.Set(4, y, WaterId);
            }

            world.Set(4, 6, SandId);

            for (int i = 0; i < 60; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(SandId, world.GetCell(4, 1).typeId, "沙应沉到水柱底部");
            Assert.AreEqual(WaterId, world.GetCell(4, 2).typeId, "水被置换上浮");
            Assert.AreEqual(WaterId, world.GetCell(4, 3).typeId);
            Assert.AreEqual(WaterId, world.GetCell(4, 4).typeId);
            Assert.AreEqual(3, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(1, CountType(world, SandId), "沙量守恒");
        }

        /// <summary>
        /// 开阔水域照旧：沙依旧沉穿水柱到底、等量的水被置换上浮
        /// （下落的沙在运动中，照旧可排开液体）。这是地下水规则不得破坏的既有玩法。
        /// </summary>
        [Test]
        public void Sand_SinksThrough_Water_InOpenBasin()
        {
            ParticleWorld world = CreateSandWorld(9, 12);
            FillRow(world, 0, StoneId);

            // 两侧石墙离水池足够远：池内每一列的水左右都是水，被向下压时有横向让位空间。
            for (int y = 1; y < 12; y++)
            {
                world.Set(1, y, StoneId);
                world.Set(7, y, StoneId);
            }

            // 5×4 水池，池面正上方一颗沙。
            for (int y = 1; y <= 4; y++)
            {
                for (int x = 2; x <= 6; x++)
                {
                    world.Set(x, y, WaterId);
                }
            }

            world.Set(4, 6, SandId);

            for (int i = 0; i < 120; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(SandId, world.GetCell(4, 1).typeId, "开阔水域：沙照旧沉到底");
            Assert.AreEqual(20, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(1, CountType(world, SandId), "沙量守恒");
            Assert.AreEqual(5, HighestTypeY(world, WaterId), "被挤出的水浮到原水面之上（池底被沙占掉一格）");
        }

        /// <summary>
        /// 大块沙（3×3）也照旧沉穿开阔水池：块先从池面上方一格落下取得真实运动
        /// （地下水闸门只放行「运动中」的固体置换液体），整块下沉、摊在池底，水被挤高。
        /// </summary>
        [Test]
        public void SandBlock_SinksThrough_Water_InOpenBasin()
        {
            ParticleWorld world = CreateSandWorld(13, 14);
            FillRow(world, 0, StoneId);

            for (int y = 1; y < 14; y++)
            {
                world.Set(1, y, StoneId);
                world.Set(11, y, StoneId);
            }

            // 9 格宽 × 6 深的水池，池面上方一整块 3×3 沙——与水面之间留一格空气：
            // tick 0 由 Set 搭出的初始布局一律视作静止（无宽限期），沙块必须先真实下落一格取得
            // 运动状态，才能在下一帧置换池面的水（地下水闸门只放行“运动中”的固体）。
            for (int y = 1; y <= 6; y++)
            {
                for (int x = 2; x <= 10; x++)
                {
                    world.Set(x, y, WaterId);
                }
            }

            for (int y = 8; y <= 10; y++)
            {
                for (int x = 5; x <= 7; x++)
                {
                    world.Set(x, y, SandId);
                }
            }

            for (int i = 0; i < 300; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(9, CountType(world, SandId), "沙量守恒");
            Assert.AreEqual(54, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(SandId, world.GetCell(6, 1).typeId, "整块沙沉降到底（同行相邻格先落者腾出的水让它一路沉底）");
            Assert.LessOrEqual(HighestTypeY(world, SandId), 6, "沙不高于原水面（全部没入池内）");
            Assert.GreaterOrEqual(HighestTypeY(world, WaterId), 7, "被挤出的水升到原水面之上");
        }

        /// <summary>
        /// 同种子、同写入序列的两个世界，跑 100 tick 后 Cell[] 必须逐格一致。
        /// </summary>
        [Test]
        public void Water_IsDeterministic_SameSeedSameResult()
        {
            ParticleWorld a = CreateWorld(16, 16);
            ParticleWorld b = CreateWorld(16, 16);
            FillRow(a, 0, StoneId);
            FillRow(b, 0, StoneId);

            for (int y = 6; y < 10; y++)
            {
                for (int x = 4; x < 12; x++)
                {
                    a.Set(x, y, WaterId);
                    b.Set(x, y, WaterId);
                }
            }

            for (int i = 0; i < 100; i++)
            {
                a.StepLogic();
                b.StepLogic();
            }

            for (int i = 0; i < a.CellCount; i++)
            {
                Assert.AreEqual(a.Cells[i].typeId, b.Cells[i].typeId, $"格子 {i} 的 typeId 不一致");
                Assert.AreEqual(a.Cells[i].flags, b.Cells[i].flags, $"格子 {i} 的 flags 不一致");
                Assert.AreEqual(a.Cells[i].shade, b.Cells[i].shade, $"格子 {i} 的 shade 不一致");
            }
        }

        /// <summary>
        /// 生产注册路径：RegisterAll 必须回填出非 0 的水 id，且带 Liquid 标签、受重力、非静态、有横流上限。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsLiquidTaggedId()
        {
            ParticleRegistration.RegisterAll();

            Assert.AreNotEqual(ParticleRegistry.AirTypeId, ParticleIds.Water, "水 id 应被回填");
            Assert.AreNotEqual(ParticleIds.Stone, ParticleIds.Water);
            Assert.AreNotEqual(ParticleIds.Sand, ParticleIds.Water);
            Assert.AreNotEqual(ParticleIds.Soil, ParticleIds.Water);

            ParticleType water = ParticleRegistry.Types[ParticleIds.Water];
            Assert.IsTrue((water.Info.Tags & ParticleTags.Liquid) != 0, "水必须带 Liquid 标签");
            Assert.IsTrue(water.Info.AffectedByGravity, "水必须受重力");
            Assert.IsFalse(water.Info.IsStatic, "水不是静态类型");
            Assert.Greater(water.Info.MaxFlowSpread, 0, "液体必须有非零横流上限");
        }

        // ==================== 蒸发：B2 / B3 回归 + 潜热（C1 / C3） ====================

        /// <summary>把 (x,y) 设成指定温度（T = Q / 热容；热容从**本世界**的类型表读，故隔离表与生产表通用；
        /// 空气没有类型条目，用 <see cref="HeatMap.AirHeatCapacity"/>）。</summary>
        private static void SetTemperature(ParticleWorld world, int x, int y, float temperature)
        {
            ushort id = world.GetCell(x, y).typeId;
            float capacity = id == ParticleRegistry.AirTypeId
                ? HeatMap.AirHeatCapacity
                : world.Types[id].Info.HeatCapacity;
            world.Heat.SetHeat(x, y, temperature * capacity);
        }

        /// <summary>
        /// B3 回归：世界顶行的水，正上方越界——**不得**把越界当成空气而蒸发。
        /// 直接重复调用水的逻辑钩子（不跑整帧）：概率门每约 300 次就会放行一次，因此若缺
        /// <c>IsInBounds</c> 检查，这里必然抓到一次"整份水汽落进自己那格"的假蒸发。
        /// </summary>
        [Test]
        public void TopRowWater_NeverEvaporatesSlowly()
        {
            ParticleRegistration.RegisterAll();
            const int height = 6;
            const int x = 6;
            ParticleWorld world = new ParticleWorld(12, height, 20261004u);
            world.Set(x, height - 1, ParticleIds.Water);
            SetTemperature(world, x, height - 1, WaterParticle.SlowEvaporationTemperature);

            ParticleType water = ParticleRegistry.Types[ParticleIds.Water];
            for (int i = 0; i < 10000; i++)
            {
                water.OnLogic(world, x, height - 1);
            }

            Assert.AreEqual(ParticleIds.Water, world.GetCell(x, height - 1).typeId,
                "顶行的水正上方越界（不是空气），不该被当成空气蒸发（审计 B3）");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "顶行的水不该产生任何湿度");
        }

        /// <summary>
        /// B2 回归：半封闭地形里接收格太少时，慢蒸发必须**放弃并保留水**——否则
        /// <c>Unit/接收格数</c> 会远超可见阈值，凭空冒出一坨看得见的水汽。
        /// 1 格宽竖井只剩正上方一格空气：份额 0.5 ≫ <see cref="HumidityField.InvisibleSpreadMaxShare"/>。
        /// </summary>
        [Test]
        public void SlowEvaporation_WithTooFewReceivers_KeepsTheWater()
        {
            ParticleRegistration.RegisterAll();
            ParticleWorld world = new ParticleWorld(1, 3, 20261004u);
            world.Set(0, 0, ParticleIds.Stone);
            world.Set(0, 1, ParticleIds.Water);
            SetTemperature(world, 0, 1, WaterParticle.SlowEvaporationTemperature);

            ParticleType water = ParticleRegistry.Types[ParticleIds.Water];
            for (int i = 0; i < 10000; i++)
            {
                water.OnLogic(world, 0, 1);
            }

            Assert.AreEqual(ParticleIds.Water, world.GetCell(0, 1).typeId,
                "接收格只有 1 个（份额 0.5）：慢蒸发必须放弃、水保留（审计 B2）");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "半封闭地形不得凭空冒出可见水汽");
        }

        /// <summary>
        /// 开阔地形里慢蒸发最终发生，且**带走一份潜热**（C1）：
        /// 本格 Q = 原 Q − <see cref="HumidityField.LatentHeat"/>（随温度线性），水格变大气、湿度总量守恒。
        /// </summary>
        [Test]
        public void SlowEvaporation_RemovesLatentHeatFromTheWater()
        {
            ParticleRegistration.RegisterAll();
            const int x = 8;
            const int y = 4;
            ParticleWorld world = new ParticleWorld(16, 16, 20261004u);
            world.Set(x, y, ParticleIds.Water);
            float capacity = ParticleRegistry.Types[ParticleIds.Water].Info.HeatCapacity;
            float startHeat = WaterParticle.SlowEvaporationTemperature * capacity;
            world.Heat.SetHeat(x, y, startHeat);

            ParticleType water = ParticleRegistry.Types[ParticleIds.Water];
            int frames = 0;
            while (world.GetCell(x, y).typeId == ParticleIds.Water && frames < 20000)
            {
                water.OnLogic(world, x, y);
                frames++;
            }

            Assert.AreNotEqual(ParticleIds.Water, world.GetCell(x, y).typeId,
                "够暖、正上方是开阔空气：慢蒸发最终应发生");
            Assert.AreEqual(0, CountType(world, ParticleIds.Water), "水格已蒸发");
            Assert.AreEqual(HumidityField.Unit, world.Humidity.Sum(), 1e-3f, "一个水格蒸发出一份水汽");
            Assert.AreEqual(startHeat - HumidityField.LatentHeat(WaterParticle.SlowEvaporationTemperature),
                world.Heat.GetHeat(x, y), 1e-3f,
                "蒸发带走潜热（随温度线性；20° 锚点 = 110）：本格变冷（C1，海面被蒸发冷却）");
        }

        /// <summary>
        /// 沸腾是**可见**路径，按设计不受份额门控：同样只剩 1 个接收格的竖井里，
        /// 到达沸点的水照样蒸发（与 B2 的慢蒸发形成对照）。
        /// </summary>
        [Test]
        public void BoilingEvaporation_IsNotLimitedByMaxShare()
        {
            ParticleRegistration.RegisterAll();
            ParticleWorld world = new ParticleWorld(1, 3, 20261004u);
            world.Set(0, 0, ParticleIds.Stone);
            world.Set(0, 1, ParticleIds.Water);
            SetTemperature(world, 0, 1, WaterParticle.BoilingPoint);

            ParticleRegistry.Types[ParticleIds.Water].OnLogic(world, 0, 1);

            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(0, 1).typeId,
                "沸腾必须蒸发：可见路径不限份额（maxShare = 0）");
            Assert.AreEqual(HumidityField.Unit, world.Humidity.Sum(), 1e-3f, "一个水格蒸发出一份水汽");
            Assert.GreaterOrEqual(world.Humidity.Get(0, 1), HumidityField.VisibleThreshold,
                "沸腾汽团以原地为中心可见（接收格含本格）");
        }

        // ==================== 液体热对流（海洋 / 湖泊翻转） ====================

        /// <summary>每列最上面**非气体**格（水面 / 地面 / 墙顶）的温度；整列都是气体时返回 <see cref="float.NaN"/>。</summary>
        private static float SurfaceTemperature(ParticleWorld world, int x)
        {
            for (int y = world.Height - 1; y >= 0; y--)
            {
                if (!ParticleRules.IsGasOrEmpty(world, x, y))
                {
                    return world.Heat.GetTemperature(x, y);
                }
            }

            return float.NaN;
        }

        /// <summary>
        /// 单格对流：上冷下暖两格静止水夹在石壁之间（横竖都动不了），是不稳定分层——
        /// 跑若干运动帧后暖水上浮一格、两格换位，热量随粒子一起搬（Q 由 <c>Swap</c> 带走）。
        /// </summary>
        [Test]
        public void LiquidConvection_WarmWaterRisesThroughTheColdAbove()
        {
            ParticleWorld world = CreateWorld(3, 6);
            FillRow(world, 0, StoneId);
            for (int y = 1; y <= 4; y++)
            {
                world.Set(0, y, StoneId);
                world.Set(2, y, StoneId);
            }

            world.Set(1, 1, WaterId); // 下暖
            world.Set(1, 2, WaterId); // 上冷
            SetTemperature(world, 1, 1, 40f);
            SetTemperature(world, 1, 2, 10f);

            for (int i = 0; i < 10; i++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(WaterId, world.GetCell(1, 1).typeId, "水量守恒");
            Assert.AreEqual(WaterId, world.GetCell(1, 2).typeId, "水量守恒");
            Assert.AreEqual(40f, world.Heat.GetTemperature(1, 2), 1e-3f, "暖水上浮：热量随粒子搬到上方");
            Assert.AreEqual(10f, world.Heat.GetTemperature(1, 1), 1e-3f, "冷水被换到下方");
        }

        /// <summary>
        /// 下暖上冷但温差不超过 <see cref="WaterParticle.LiquidConvectionEpsilon"/> 时纹丝不动：
        /// 死区把等温面上的浮点残差挡在门外，避免无意义的换位。
        /// </summary>
        [Test]
        public void LiquidConvection_IgnoresDifferenceWithinEpsilon()
        {
            float warm = 10f + WaterParticle.LiquidConvectionEpsilon * 0.8f;

            ParticleWorld world = CreateWorld(3, 6);
            FillRow(world, 0, StoneId);
            for (int y = 1; y <= 4; y++)
            {
                world.Set(0, y, StoneId);
                world.Set(2, y, StoneId);
            }

            world.Set(1, 1, WaterId);
            world.Set(1, 2, WaterId);
            SetTemperature(world, 1, 1, warm); // 下方略暖，但在死区内
            SetTemperature(world, 1, 2, 10f);

            for (int i = 0; i < 30; i++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(warm, world.Heat.GetTemperature(1, 1), 1e-3f, "温差在死区内：下方保持不动");
            Assert.AreEqual(10f, world.Heat.GetTemperature(1, 2), 1e-3f, "温差在死区内：上方保持不动");
        }

        /// <summary>
        /// 对流不抢在下落前面：正下方是气体的落水照常下落一格；
        /// 原语对「正上方不是液体」恒为 false（上方是空气也绝不换上去）。
        /// </summary>
        [Test]
        public void LiquidConvection_DoesNotTriggerOverGas()
        {
            ParticleWorld world = CreateWorld(4, 8);
            world.Set(2, 5, WaterId);
            SetTemperature(world, 2, 5, 60f); // 就算很暖，上下都是空气也不对流

            world.StepMotion();

            Assert.AreEqual(WaterId, world.GetCell(2, 4).typeId, "正下方是空气：照常下落一格");
            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(2, 5).typeId, "原格已空");

            ParticleWorld ruleWorld = CreateWorld(4, 8);
            ruleWorld.Set(2, 5, WaterId);
            SetTemperature(ruleWorld, 2, 5, 60f);

            Assert.IsFalse(
                ParticleRules.TryLiquidConvect(ruleWorld, 2, 5, WaterParticle.LiquidConvectionEpsilon),
                "正上方不是液体：原语返回 false");
            Assert.AreEqual(WaterId, ruleWorld.GetCell(2, 5).typeId, "原语不该换位");
        }

        /// <summary>
        /// 核心涌现回归，锁死用户报告的现象「陆地无论昼夜总比水域热、陆风无法涌现」：
        /// 一窄世界里左侧是深 8 格的水体立柱、右侧是高 8 格的干土立柱，上方皆大气。
        /// 光照强度 10 跑 600 逻辑帧（白天），再关灯跑 600 逻辑帧（夜间）；
        /// 断言**夜晚后段**水面温度高于土面温度——对流把白天搅进深水的热在夜里翻回表面，
        /// 正是海风 / 陆风环流需要的"夜间海面更暖"。
        /// 无对流时水只有表层储热、整夜被辐射抽冷，水面低于只靠浅层传导的土面（pre-fix 挂）。
        /// 白天不断言方向：陆面更热是预期，本用例只锁"夜间反转"。
        /// </summary>
        /// <remarks>
        /// 走 <see cref="ParticleRegistration.RegisterAll"/> 的**生产表**：这段模拟跑满 1200 逻辑帧，
        /// 必定出现降雨，而降雨由 <c>ParticleWorld.StepRain</c> 用 <see cref="ParticleIds.Water"/> 凝结成水——
        /// 隔离表里该字段是 0（或上一次生产注册留下的越界 id），只有生产表才自洽（与 <c>WeatherParticleTests</c> 同一取舍）。
        /// 探针取两柱的**中线列**（各距最近的石壁 7~8 格），把 1 格宽石壁顶被晒热后的侧向导热
        /// 影响压到最小；水面 / 土面高度用 <see cref="SurfaceTemperature"/> 动态取最上面非气体格，
        /// 因此白天的蒸发掉格、降雨补水都不影响断言。
        /// </remarks>
        [Test]
        public void LiquidConvection_KeepsSeaWarmerThanLand_ThroughTheNight()
        {
            ParticleRegistration.RegisterAll();
            ParticleWorld world = new ParticleWorld(40, 16, 20261004u);

            // 与生产 driver 一致：先把所有空格填成大气，再铺地板、砌墙、立柱。
            for (int y = 0; y < world.Height; y++)
            {
                FillRow(world, y, ParticleIds.Atmosphere);
            }

            FillRow(world, 0, ParticleIds.Stone);

            // 水体立柱：x = 1..14、深 8；石壁 x = 0 / 15 把它关住。
            for (int y = 1; y <= 8; y++)
            {
                world.Set(0, y, ParticleIds.Stone);
                world.Set(15, y, ParticleIds.Stone);
                for (int x = 1; x <= 14; x++)
                {
                    world.Set(x, y, ParticleIds.Water);
                }
            }

            // 干土立柱：x = 25..38、高 8；石壁 x = 24 / 39。
            for (int y = 1; y <= 8; y++)
            {
                world.Set(24, y, ParticleIds.Stone);
                world.Set(39, y, ParticleIds.Stone);
                for (int x = 25; x <= 38; x++)
                {
                    world.Set(x, y, ParticleIds.Soil);
                }
            }

            world.Heat.RadiativeLoss = 0.05f;       // 生产辐射率
            world.Heat.AirThermalEmissivity = 0.1f; // 生产空气辐射率
            world.Light.Enabled = true;
            world.Light.Intensity = 10f;            // 生产光照强度
            world.Light.LitTicks = int.MaxValue;
            world.Light.DarkTicks = 0;

            for (int i = 0; i < 600; i++)
            {
                world.StepLogic(); // 白天：光照 600 逻辑帧
            }

            world.Light.Enabled = false;

            for (int i = 0; i < 600; i++)
            {
                world.StepLogic(); // 夜间：无光照 600 逻辑帧
            }

            float water = SurfaceTemperature(world, 8);
            float soil = SurfaceTemperature(world, 32);

            Assert.Greater(water, soil,
                $"夜间后段水面温度（{water:F2}）应高于土面温度（{soil:F2}）：对流本应把白天储进深水的热带回表面");
        }

        // ============================== 液面高光（渲染映射，HasSurfaceHighlight → ParticlePixelBuffer 提亮） ==============================

        /// <summary>该格按调色板与 shade 应显示的基底色（未提亮、未变暗）。</summary>
        private static Color32 WaterBaseColorAt(ParticleWorld world, int x, int y)
        {
            Cell cell = world.GetCell(x, y);
            return world.Types[cell.typeId].Info.Palette[cell.shade];
        }

        /// <summary>
        /// 液面不变量：正上方是空气的水格向浅蓝白插值（液面），被另一格水压住的格保持基底色（水下）。
        /// </summary>
        [Test]
        public void SurfaceHighlight_TopWaterCellIsLightened_UnderwaterCellIsNot()
        {
            ParticleWorld world = CreateWorld(4, 4);
            world.Set(2, 0, WaterId);
            world.Set(2, 1, WaterId);

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, new Color32(9, 8, 7, 255));

            Color32 expected = Color32.Lerp(
                WaterBaseColorAt(world, 2, 1),
                ParticlePixelBuffer.SurfaceHighlightColor,
                ParticlePixelBuffer.SurfaceHighlightFactor);
            Assert.AreEqual(expected, pixels[world.Index(2, 1)], "液面水格应向浅蓝白插值");
            Assert.AreEqual(WaterBaseColorAt(world, 2, 0), pixels[world.Index(2, 0)], "被水压住的格不是液面，保持基色");
        }

        /// <summary>正上方是固体的水不是液面（被岩石盖住的水不高光）。</summary>
        [Test]
        public void SurfaceHighlight_WaterUnderSolidIsNotHighlighted()
        {
            ParticleWorld world = CreateWorld(4, 4);
            world.Set(2, 0, WaterId);
            world.Set(2, 1, StoneId);

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, new Color32(9, 8, 7, 255));

            Assert.AreEqual(WaterBaseColorAt(world, 2, 0), pixels[world.Index(2, 0)], "正上方是固体的水不是液面");
        }

        // ============================== 穿冠（雨水穿冠） ==============================

        /// <summary>
        /// 穿冠测试走 <see cref="ParticleRegistration.RegisterAll"/> 的**生产表**：穿冠门比较
        /// <see cref="ParticleIds.TreeLeaf"/>，隔离表里它是 0（= 空气的 typeId，与
        /// <c>WetSoilParticle.IsDrySoil</c> 同一取舍），只有回填后才有意义。
        /// </summary>
        private static ParticleWorld CreateCanopyWorld(int width, int height, uint seed = 20261012u)
        {
            ParticleRegistration.RegisterAll();
            return new ParticleWorld(width, height, seed);
        }

        /// <summary>
        /// 雨水穿冠：水正下方是 1~3 格连续树叶、叶柱下方是空气时，**一个运动帧**后水出现在
        /// 叶柱下方的第一格，被顶开的气体换上水的原格（冠顶正上方）——逐格断言这次交换。
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Water_PassesThroughCanopy_ToCellBelowLeaves(int leafDepth)
        {
            const int x = 2;
            const int waterY = 8;
            ParticleWorld world = CreateCanopyWorld(5, 12);

            for (int i = 0; i < leafDepth; i++)
            {
                Assert.IsTrue(world.Set(x, waterY - 1 - i, ParticleIds.TreeLeaf), "铺树叶");
            }

            world.Set(x, waterY, ParticleIds.Water);

            world.StepMotion();

            int targetY = waterY - 1 - leafDepth;
            Assert.AreEqual(ParticleIds.Water, world.GetCell(x, targetY).typeId,
                $"{leafDepth} 格叶柱：水应穿到叶柱下方第一格");
            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(x, targetY - 1).typeId,
                "叶柱下方再下一格不该被动到（水只穿到叶柱下第一格）");
            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(x, waterY).typeId,
                "被顶开的气体换上水的原格（冠顶正上方）——质量守恒");
            for (int i = 0; i < leafDepth; i++)
            {
                Assert.AreEqual(ParticleIds.TreeLeaf, world.GetCell(x, waterY - 1 - i).typeId,
                    "叶柱逐格不动");
            }

            Assert.AreEqual(1, CountType(world, ParticleIds.Water), "水量守恒");
            Assert.AreEqual(leafDepth, CountType(world, ParticleIds.TreeLeaf), "叶量守恒");
        }

        /// <summary>
        /// 叶柱下方是**固体**（石头 / 干土）时水不穿冠、也不离开冠顶：
        /// 一格宽竖井把水关住（斜滑 / 横流都无路可走），于是水原地不动——穿冠失败不产生任何位移。
        /// </summary>
        [Test]
        public void Water_DoesNotPassCanopy_WhenSolidBelow()
        {
            foreach (ushort blockId in new[] { ParticleIds.Stone, ParticleIds.Soil })
            {
                const int x = 1;
                const int waterY = 9;
                ParticleWorld world = CreateCanopyWorld(3, 14);

                // 两壁 + 一整列基质（底部托住、不会下沉）把水彻底关在一格宽的竖井里。
                for (int y = 0; y < world.Height; y++)
                {
                    world.Set(0, y, ParticleIds.Stone);
                    world.Set(2, y, ParticleIds.Stone);
                }

                for (int y = 0; y <= 6; y++)
                {
                    world.Set(x, y, blockId);
                }

                world.Set(x, 7, ParticleIds.TreeLeaf);
                world.Set(x, 8, ParticleIds.TreeLeaf);
                world.Set(x, waterY, ParticleIds.Water);

                for (int i = 0; i < 5; i++)
                {
                    world.StepMotion();
                }

                Assert.AreEqual(ParticleIds.Water, world.GetCell(x, waterY).typeId,
                    $"{blockId}：叶柱下方是固体，水不穿冠、留在冠顶");
                Assert.AreEqual(ParticleIds.TreeLeaf, world.GetCell(x, 8).typeId, "叶柱没被动到");
                Assert.AreEqual(ParticleIds.TreeLeaf, world.GetCell(x, 7).typeId, "叶柱没被动到");
                Assert.AreEqual(blockId, world.GetCell(x, 6).typeId, "叶柱下的固体没被换走");
                Assert.AreEqual(1, CountType(world, ParticleIds.Water), "水量守恒");
            }
        }

        /// <summary>
        /// 深度上限的边界：恰好 <see cref="WaterParticle.CanopyPassMaxDepth"/> 格连续树叶仍能穿过，
        /// 再多一格就不传送（水留在冠顶，且既有四段也走不通——竖井封死）。
        /// 两个场景除叶柱深度外完全同构。
        /// </summary>
        [Test]
        public void Water_PassesCanopy_AtMaxDepth_ButNotDeeper()
        {
            const int x = 1;
            const int leafTopY = 12;
            int maxDepth = WaterParticle.CanopyPassMaxDepth;

            ParticleWorld shallow = CreateCanopyShaft(maxDepth, out int bottomY);
            shallow.StepMotion();
            Assert.AreEqual(ParticleIds.Water, shallow.GetCell(x, bottomY).typeId,
                $"{maxDepth} 格叶柱（= 上限）：水应穿过去，落在叶柱下方第一格");
            Assert.AreEqual(ParticleRegistry.AirTypeId, shallow.GetCell(x, leafTopY + 1).typeId,
                "水原格被换上气体");

            ParticleWorld deep = CreateCanopyShaft(maxDepth + 1, out _);
            for (int i = 0; i < 5; i++)
            {
                deep.StepMotion();
            }

            Assert.AreEqual(ParticleIds.Water, deep.GetCell(x, leafTopY + 1).typeId,
                $"{maxDepth + 1} 格叶柱（超限）：不传送，水留在冠顶");
            Assert.AreEqual(1, CountType(deep, ParticleIds.Water), "水量守恒");
        }

        /// <summary>
        /// 一格宽竖井里的穿冠场景：水直接坐在 <paramref name="leafDepth"/> 格连续树叶上、
        /// 叶柱下方是空气。两壁填满整列（斜滑 / 横流无路可走），故穿冠失败时水只能待在冠顶。
        /// </summary>
        /// <param name="bottomY">叶柱下方第一格的 y（穿冠成功时水应到达的位置）。</param>
        private static ParticleWorld CreateCanopyShaft(int leafDepth, out int bottomY)
        {
            const int x = 1;
            const int leafTopY = 12;
            ParticleWorld world = CreateCanopyWorld(3, 24);

            for (int y = 0; y < world.Height; y++)
            {
                world.Set(0, y, ParticleIds.Stone);
                world.Set(2, y, ParticleIds.Stone);
            }

            for (int i = 0; i < leafDepth; i++)
            {
                world.Set(x, leafTopY - i, ParticleIds.TreeLeaf);
            }

            world.Set(x, leafTopY + 1, ParticleIds.Water);
            bottomY = leafTopY - leafDepth;
            return world;
        }

        /// <summary>
        /// 穿冠是纯确定性扫描：**零随机流消耗**（运动路径铁律）。两个同种子、同布局的世界，
        /// 只对一个跑一帧运动，之后两世界的下一个随机数必须相同。
        /// </summary>
        [Test]
        public void CanopyPass_DoesNotConsumeRandomStream()
        {
            ParticleWorld triggered = CreateCanopyWorld(5, 12);
            ParticleWorld control = CreateCanopyWorld(5, 12);
            foreach (ParticleWorld world in new[] { triggered, control })
            {
                world.Set(2, 7, ParticleIds.TreeLeaf);
                world.Set(2, 6, ParticleIds.TreeLeaf);
                world.Set(2, 8, ParticleIds.Water);
            }

            triggered.StepMotion();

            Assert.AreEqual(ParticleIds.Water, triggered.GetCell(2, 5).typeId, "前置：穿冠确实发生了");
            Assert.AreEqual(control.NextUInt(), triggered.NextUInt(),
                "穿冠不得消耗世界随机流（运动路径铁律）");
        }
    }
}
