using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 地下水规则（<see cref="ParticleRules.CanDisplace"/> 的「被夹住不放行」闸门）：
    /// 固体置换液体前若左右同一行都被固体堵住（越界按固体算），置换不成立。
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
        public void CanDisplace_SolidWedgedBetweenSolids_CannotEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, StoneId);
            world.Set(1, 0, WaterId);

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "左右都被固体堵住 → 置换液体不成立");
        }

        [Test]
        public void CanDisplace_AnyOpenSide_CanEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(1, 0, WaterId);
            // 右侧 (2,1) 是空气：旁路存在 → 允许置换。
            Assert.IsTrue(ParticleRules.CanDisplace(world, 1, 1, 1, 0));
        }

        [Test]
        public void CanDisplace_LiquidSideIsAnEscape()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, WaterId);   // 右侧是液体：旁路（开阔水域里沙的两侧就是水）
            world.Set(1, 0, WaterId);

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "一侧是液体 → 允许置换（池塘照旧下沉靠这条）");
        }

        [Test]
        public void CanDisplace_GasSideIsAnEscape()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, AtmosphereId);   // 右侧是气体：旁路
            world.Set(1, 0, WaterId);

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "一侧是气体 → 允许置换");
        }

        [Test]
        public void CanDisplace_WorldEdgeCountsAsBlockedSide()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 2);
            world.Set(0, 1, StoneId);
            world.Set(0, 0, WaterId);

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "两侧越界按固体算 → 置换不成立");
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
    }
}
