using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 水（<see cref="WaterParticle"/>）的流体行为测试：下落、无安息角摊平、被固体沉穿、确定性，
    /// 以及生产注册路径的回填与标签。
    /// </summary>
    /// <remarks>
    /// 行为测试一律走 <see cref="TestTables.Build"/> 的隔离类型表（水 = id 1，搭配的固体 = id 2），
    /// 不触碰全局注册表；只有 <see cref="ProductionRegistration_BackfillsLiquidTaggedId"/>
    /// 走 <see cref="ParticleRegistration.RegisterAll"/> 验证生产路径。
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

            world.Step();
            Assert.AreEqual(WaterId, world.GetCell(4, 9).typeId, "第一 tick 应直落一格");
            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(4, 10).typeId);

            for (int i = 0; i < 20; i++)
            {
                world.Step();
            }

            Assert.AreEqual(WaterId, world.GetCell(4, 1).typeId, "最终应停在石头地板上");
            Assert.AreEqual(1, CountType(world, WaterId), "水量守恒，一滴不丢");
        }

        /// <summary>
        /// 液体无安息角：一坨 10x4 的水倒在平底上，收敛后必须摊成覆盖全宽的两层水平面，
        /// 而不是像粉末那样堆出斜坡。40 格水在 20 宽的容器里恰好是 2 层。
        /// </summary>
        [Test]
        public void Water_LevelsOut_OnFlatFloor()
        {
            const int width = 20;
            ParticleWorld world = CreateWorld(width, 16);
            FillRow(world, 0, StoneId);

            for (int y = 5; y < 9; y++)
            {
                for (int x = 5; x < 15; x++)
                {
                    world.Set(x, y, WaterId);
                }
            }

            for (int i = 0; i < 300; i++)
            {
                world.Step();
            }

            Assert.AreEqual(40, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(width, RowCount(world, WaterId, 1), "第一层应铺满全宽");
            Assert.AreEqual(width, RowCount(world, WaterId, 2), "第二层应铺满全宽");
            Assert.AreEqual(0, RowCount(world, WaterId, 3), "第三层应全空（表面严格水平）");
        }

        /// <summary>
        /// 回归：世界被大气粒子填满（没有 typeId 0 的格子）时水仍必须摊平。
        /// 曾有的 bug：横流落点判据用 IsAir，大气不是「空气 id」，水永远找不到落点而不再流动。
        /// </summary>
        [Test]
        public void Water_LevelsOut_InAtmosphereFilledWorld()
        {
            const int width = 20;
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

            for (int y = 5; y < 9; y++)
            {
                for (int x = 5; x < 15; x++)
                {
                    world.Set(x, y, WaterId);
                }
            }

            for (int i = 0; i < 400; i++)
            {
                world.Step();
            }

            Assert.AreEqual(40, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(width, RowCount(world, WaterId, 1), "第一层应铺满全宽");
            Assert.AreEqual(width, RowCount(world, WaterId, 2), "第二层应铺满全宽");
            Assert.AreEqual(0, RowCount(world, WaterId, 3), "第三层应全空（表面严格水平）");
        }

        /// <summary>
        /// rank 规则的集成验证：沙（Solid，rank 3）从水柱上方落下，沉穿水到底；
        /// 等量的水被置换上浮。沙与水各自守恒。
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
                world.Step();
            }

            Assert.AreEqual(SandId, world.GetCell(4, 1).typeId, "沙应沉到水柱底部");
            Assert.AreEqual(WaterId, world.GetCell(4, 2).typeId, "水被置换上浮");
            Assert.AreEqual(WaterId, world.GetCell(4, 3).typeId);
            Assert.AreEqual(WaterId, world.GetCell(4, 4).typeId);
            Assert.AreEqual(3, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(1, CountType(world, SandId), "沙量守恒");
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
                a.Step();
                b.Step();
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
    }
}
