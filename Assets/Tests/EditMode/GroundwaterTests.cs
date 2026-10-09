using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 地下水规则（<see cref="ParticleRules.CanDisplace"/> 的「静止固体不让路」闸门）：
    /// 固体置换液体前必须处于运动中（上一扫描位移过，或运行中刚放置）——静止的固体推不动液体。
    /// 单测走隔离类型表；水囊 / 联通 / 释放 / 休眠 A/B 走生产注册路径（依赖 ParticleIds 回填）。
    /// </summary>
    public sealed class GroundwaterTests
    {
        private const uint SeedValue = 20261009u;

        // 隔离类型表：水 = 1、石头 = 2、大气 = 3（TestTables.Build 约定第 n 个粒子 = typeId n + 1）。
        private const ushort WaterId = 1;
        private const ushort StoneId = 2;
        private const ushort AtmosphereId = 3;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ParticleWorld CreateIsolatedWorld(int width, int height)
        {
            return new ParticleWorld(
                width, height, SeedValue,
                TestTables.Build(new WaterParticle(), new StoneParticle(), new AtmosphereParticle()));
        }

        private static ParticleWorld CreateProductionWorld(int width, int height)
        {
            return new ParticleWorld(width, height, SeedValue);
        }

        private static void FillRect(ParticleWorld world, int x0, int y0, int x1, int y1, ushort typeId)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Assert.IsTrue(world.Set(x, y, typeId), $"Set({x},{y}) 应成功");
                }
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

        /// <summary>某一列 x 上某类型的格数。</summary>
        private static int CountTypeInColumn(ParticleWorld world, int x, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                if (world.GetCell(x, y).typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>布局哈希（typeId / shade / 非 bit0 的 flags；FNV-1a，无分配）。</summary>
        private static ulong LayoutHash(ParticleWorld world)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                Cell[] cells = world.Cells;
                for (int i = 0; i < cells.Length; i++)
                {
                    h = (h ^ cells[i].typeId) * 1099511628211UL;
                    h = (h ^ cells[i].shade) * 1099511628211UL;
                    h = (h ^ (ulong)(cells[i].flags & CellFlags.ProcessedParityMask)) * 1099511628211UL;
                }

                return h;
            }
        }

        /// <summary>全状态哈希（布局 + 每格热量 / 湿度位模式）。</summary>
        private static ulong StateHash(ParticleWorld world)
        {
            unchecked
            {
                ulong h = LayoutHash(world);
                for (int y = 0; y < world.Height; y++)
                {
                    for (int x = 0; x < world.Width; x++)
                    {
                        h = (h ^ (ulong)System.BitConverter.SingleToInt32Bits(world.Heat.GetHeat(x, y))) * 1099511628211UL;
                        h = (h ^ (ulong)System.BitConverter.SingleToInt32Bits(world.Humidity.Get(x, y))) * 1099511628211UL;
                    }
                }

                return h;
            }
        }

        /// <summary>跑一个逻辑周期（3 运动帧 + 1 逻辑帧，同生产节拍）。</summary>
        private static void RunCycles(ParticleWorld world, int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                world.StepMotion();
                world.StepMotion();
                world.StepMotion();
                world.StepLogic();
            }
        }

        [Test]
        public void CanDisplace_StaticSolid_CannotEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(1, 1, StoneId);
            world.Set(1, 0, StoneId);   // 先垫底，让上面的石头静止下来
            world.StepMotion();          // tick 0 的初始布局本就没有宽限：石头下方是石头，不动、也无运动状态
            world.Set(1, 0, WaterId);    // 垫底换成水
            world.StepMotion();          // 石头已静止 → 推不动水

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "静止石头不能置换液体");
        }

        [Test]
        public void CanDisplace_SolidThatJustFell_CanEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 3);
            world.Set(0, 2, StoneId);
            world.Set(0, 0, WaterId);
            world.StepMotion();          // 石头落进 (0,1)（空气目标不受闸门约束）→ 获得运动状态

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "刚下落的石头可以置换液体");
        }

        [Test]
        public void CanDisplace_FreshlyPlacedSolid_CanEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 2);
            world.Set(0, 0, WaterId);
            world.StepMotion();          // 先让世界跑一帧（tick ≥ 1）：tick 0 是初始条件、一律视作静止
            world.Set(0, 1, StoneId);    // 运行中放置 → 享有宽限期（视作刚运动过）

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "运行中放置的石头视作运动中（画笔直接放进水里照旧下沉）");
        }

        [Test]
        public void CanDisplace_GraceExpiresAfterOneScan()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 3);
            world.Set(0, 0, StoneId);
            world.StepMotion();          // tick ≥ 1
            world.Set(0, 1, StoneId);    // 运行中放置：有宽限，但下方是石头不会移动
            world.StepMotion();          // 宽限所在的那次扫描
            world.StepMotion();          // 宽限过期
            world.Set(0, 0, WaterId);    // 地板换成水

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "宽限只持续一个扫描，之后不再算运动中");
        }

        [Test]
        public void GroundwaterSealingDisabled_StillTracksMomentum()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 2);
            world.Set(0, 0, WaterId);
            world.StepMotion();
            world.Set(0, 1, StoneId);
            world.GroundwaterSealingEnabled = false;

            Assert.IsTrue(world.HasMomentum(0, 1), "开关只影响闸门，不影响运动状态维护");
            Assert.IsTrue(ParticleRules.CanDisplace(world, 0, 1, 0, 0), "开关关 → 退回旧行为");
        }

        [Test]
        public void CanDisplace_AirTargetIsUnaffected()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, StoneId);
            // (1,0) 保持空气：目标是气体/空 → 本闸门不适用（气体照旧可被置换）。
            Assert.IsTrue(ParticleRules.CanDisplace(world, 1, 1, 1, 0));
        }

        [Test]
        public void GroundwaterSealingDisabled_RestoresLegacyDisplacement()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, StoneId);
            world.Set(1, 0, WaterId);
            world.GroundwaterSealingEnabled = false;

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "开关关 → 退回旧的 rank 置换行为");
        }

        [Test]
        public void WaterPocketInStone_StaysBitIdentical()
        {
            // 前提：默认世界热学死寂——无光照、环境热 0、辐射 0 ⇒ T 恒 0，无相变 / 对流；
            // 本用例只验证地下水闸门下的完全静止（若将来默认参数变化，失败信息需另行判断）。
            ParticleWorld world = CreateProductionWorld(12, 12);
            FillRect(world, 0, 0, 11, 11, ParticleIds.Stone);
            FillRect(world, 4, 4, 6, 6, ParticleIds.Water);

            ulong before = LayoutHash(world);
            int waterBefore = CountType(world, ParticleIds.Water);

            RunCycles(world, 120);

            Assert.AreEqual(before, LayoutHash(world), "石头里的水囊与整块石头都不许动");
            Assert.AreEqual(waterBefore, CountType(world, ParticleIds.Water), "水量守恒");
        }

        [Test]
        public void BrushShapedWaterPocketInStone_StaysBitIdentical()
        {
            ParticleWorld world = CreateProductionWorld(20, 16);
            FillRect(world, 0, 0, 19, 15, ParticleIds.Stone);
            // 画刷形状：4×3 主体 + 斜边（模拟手绘的水囊边界）。
            FillRect(world, 6, 6, 9, 8, ParticleIds.Water);
            world.Set(10, 8, ParticleIds.Water);
            world.Set(10, 9, ParticleIds.Water);
            world.Set(11, 9, ParticleIds.Water);

            ulong before = LayoutHash(world);

            RunCycles(world, 120);

            Assert.AreEqual(before, LayoutHash(world), "画刷画出的水囊不许动");
        }

        [Test]
        public void GroundwaterConnectedToPond_StaysStill_AndSandDrillsTheShaft()
        {
            ParticleWorld world = CreateProductionWorld(12, 12);

            // 石层 y = 0..4（整层石头）。
            FillRect(world, 0, 0, 11, 4, ParticleIds.Stone);

            // 水囊（2×2）+ 一格宽的竖井（y = 2..4），竖井把水囊与上方池塘联通。
            FillRect(world, 3, 2, 4, 3, ParticleIds.Water);
            world.Set(5, 2, ParticleIds.Water);
            world.Set(5, 3, ParticleIds.Water);
            world.Set(5, 4, ParticleIds.Water);

            // 两侧挡墙 + 池塘（水面 y = 5..6）。
            for (int y = 5; y <= 8; y++)
            {
                world.Set(0, y, ParticleIds.Stone);
                world.Set(11, y, ParticleIds.Stone);
            }
            FillRect(world, 1, 5, 10, 6, ParticleIds.Water);

            // 池塘上方丢一颗沙，正对竖井。
            world.Set(5, 8, ParticleIds.Sand);

            RunCycles(world, 200);

            // 下落的沙在运动中：沿竖井沉到井底，把井里的水逐格挤上去（1:1）；石头静止不塌，水囊不受扰动。
            Assert.AreEqual(ParticleIds.Sand, world.GetCell(5, 2).typeId, "沙沿竖井沉到井底");
            Assert.AreEqual(1, CountType(world, ParticleIds.Sand), "沙量守恒");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(5, 3).typeId, "井里的水被挤上去一格");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(5, 4).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(3, 2).typeId, "水囊纹丝不动");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 2).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(3, 3).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 3).typeId);
            Assert.AreEqual(4 + 3 + 20, CountType(world, ParticleIds.Water), "水总量守恒");
        }

        [Test]
        public void DiggingBesidePocket_ReleasesGroundwater()
        {
            ParticleWorld world = CreateProductionWorld(8, 8);
            FillRect(world, 0, 0, 7, 7, ParticleIds.Stone);
            world.Set(3, 3, ParticleIds.Water);
            world.Set(4, 3, ParticleIds.Water);

            // 在水囊左侧挖一口贯通到顶的竖井：水由自身流动排出。
            // 1 格宽缺口只会放出一格水——它流走后上方石头在同一逻辑周期内塌进空格把缺口封住，第二格留在水囊里（自封，涌现行为）。
            for (int y = 1; y <= 7; y++)
            {
                world.Clear(2, y);
            }

            RunCycles(world, 200);

            Assert.AreEqual(2, CountType(world, ParticleIds.Water), "水量守恒");
            Assert.AreNotEqual(ParticleIds.Water, world.GetCell(3, 3).typeId, "缺口那格已不是水");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 3).typeId, "内侧那格被塌下来的石头封住");
            Assert.AreEqual(1, CountTypeInColumn(world, 2, ParticleIds.Water), "流出的那格落在竖井里");
        }

        [Test]
        public void MotionSleepTransparency_WithGroundwaterRule()
        {
            ParticleWorld sleeping = BuildSleepScene();
            ParticleWorld full = BuildSleepScene();
            full.MotionSleepEnabled = false;

            const int BeforeDig = 20;
            const int AfterDig = 120;
            for (int cycle = 0; cycle < BeforeDig + AfterDig; cycle++)
            {
                if (cycle == BeforeDig)
                {
                    // 挖掉井壁：让水与沙重新流动，外部写入改变场景后 A/B 仍逐位一致（写入唤醒本身由 MotionSleepTests 覆盖）。
                    sleeping.Clear(5, 4);
                    full.Clear(5, 4);
                }

                sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepLogic();
                full.StepMotion(); full.StepMotion(); full.StepMotion(); full.StepLogic();

                Assert.AreEqual(
                    StateHash(full), StateHash(sleeping), $"第 {cycle} 周期休眠与全网格应逐位一致");
            }
        }

        [Test]
        public void GasPocketInStone_StillRises()
        {
            ParticleWorld world = CreateProductionWorld(3, 8);
            FillRect(world, 0, 0, 2, 7, ParticleIds.Stone);
            world.Set(1, 2, ParticleIds.Atmosphere);

            RunCycles(world, 60);

            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(1, 7).typeId, "气穴照旧冒泡升到最顶");
            Assert.AreEqual(1, CountType(world, ParticleIds.Atmosphere), "气量守恒");
        }

        [Test]
        public void SandColumn_DrillsThroughWater_InOneWideShaft()
        {
            // 接力（ParticleWorld.Swap 的 MarkInMotionAbove）：沙柱的**头一颗**靠「运行中放置」的宽限获得
            // 初始动量，后续每一颗都靠「上一颗向下换位时把原格正上方的同伴预置为运动中」才拿到置换资格。
            // 场景刻意不给空气间隔、沙柱直接压在水面上：自顶向下的扫描里上面两颗被上一颗挡着，
            // 宽限当帧作废——只有接力能把动量逐颗传下去。变异实测：删掉 Swap 里的接力块后本用例变红
            // （只埋空气间隔时每颗沙都能自己穿过空气拿动量，接力删了也不变红——所以不能留空气）。
            ParticleWorld world = CreateProductionWorld(9, 14);
            FillRect(world, 0, 0, 8, 0, ParticleIds.Stone);     // 地板 y = 0
            for (int y = 1; y <= 12; y++)
            {
                world.Set(3, y, ParticleIds.Stone);             // 井壁 x = 3 / x = 5，y = 1..12
                world.Set(5, y, ParticleIds.Stone);
            }

            for (int y = 1; y <= 8; y++)
            {
                world.Set(4, y, ParticleIds.Water);             // 井底水柱 8 格
            }

            world.StepMotion();                                 // tick ≥ 1：之后的放置才有「运动中」宽限

            // 沙柱 3 颗直接压在水面上（y = 9..11，不留空气）。三颗都带着放置宽限，
            // 但上面两颗当帧被挡后宽限立即过期——它们只能等接力。
            world.Set(4, 9, ParticleIds.Sand);
            world.Set(4, 10, ParticleIds.Sand);
            world.Set(4, 11, ParticleIds.Sand);

            RunCycles(world, 240);

            Assert.AreEqual(ParticleIds.Sand, world.GetCell(4, 1).typeId, "第一颗沙沉到井底");
            Assert.AreEqual(ParticleIds.Sand, world.GetCell(4, 2).typeId, "第二颗沙接踵而至");
            Assert.AreEqual(ParticleIds.Sand, world.GetCell(4, 3).typeId, "第三颗沙接踵而至");
            for (int y = 4; y < world.Height; y++)
            {
                Assert.AreNotEqual(ParticleIds.Sand, world.GetCell(4, y).typeId, $"y = {y} 不该悬着沙");
            }

            Assert.AreEqual(3, CountType(world, ParticleIds.Sand), "沙量守恒");
            Assert.AreEqual(8, CountType(world, ParticleIds.Water), "水量守恒");
        }

        /// <summary>一格宽石井（井壁 x=3、x=5）+ 井底三格水 + 井内最高那格一颗沙。</summary>
        private static ParticleWorld BuildSleepScene()
        {
            ParticleWorld world = CreateProductionWorld(9, 8);
            FillRect(world, 0, 0, 8, 0, ParticleIds.Stone);
            for (int y = 1; y <= 5; y++)
            {
                world.Set(3, y, ParticleIds.Stone);
                world.Set(5, y, ParticleIds.Stone);
            }

            world.Set(4, 1, ParticleIds.Water);
            world.Set(4, 2, ParticleIds.Water);
            world.Set(4, 3, ParticleIds.Water);
            world.Set(4, 5, ParticleIds.Sand);
            return world;
        }
    }
}
