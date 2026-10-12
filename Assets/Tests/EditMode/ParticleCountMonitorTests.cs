using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="ParticleCountMonitor"/>（世界新成员 <see cref="ParticleWorld.Counts"/>）的测试：
    /// 存量差分、覆盖写、Clear、Swap 不参与、读后清零、按类型独立、空气 / 越界口径，
    /// 以及「读观察者不改变世界与随机流」的纯净性。
    /// </summary>
    /// <remarks>
    /// <para>全部用隔离类型表（<see cref="TestTables.Build"/>）——**不碰全局注册表**，
    /// 因此本文件与生产注册顺序、与其它测试文件互不污染。</para>
    /// </remarks>
    public sealed class ParticleCountMonitorTests
    {
        private const ushort GrainId = 1;
        private const ushort LiquidId = 2;
        private const uint SeedValue = 20261012u;

        /// <summary>隔离世界：类型 1 = 重力颗粒（会下落），类型 2 = 液体。</summary>
        private static ParticleWorld CreateWorld(int width, int height)
        {
            return new ParticleWorld(
                width, height, SeedValue, TestTables.Build(new TestGrainParticle(), new TestLiquidParticle()));
        }

        /// <summary>逐格真源：网格上某类型的实际格数（与观察者读数对照）。</summary>
        private static int CountInGrid(ParticleWorld world, ushort typeId)
        {
            Cell[] cells = world.Cells;
            int count = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>有真实运动发生的场景：不同列上高低错落的颗粒与液体（会下落 / 横流，产生 Set / Swap）。</summary>
        private static ParticleWorld BuildMotionScene()
        {
            ParticleWorld world = CreateWorld(10, 10);
            world.Set(2, 8, GrainId);
            world.Set(3, 8, GrainId);
            world.Set(4, 8, LiquidId);
            world.Set(5, 8, GrainId);
            world.Set(6, 9, LiquidId);
            return world;
        }

        // ============================== 存量差分 ==============================

        /// <summary>Set 写入新格后存量 +1；写入空气格（Clear）后存量 −1；不与别的类型混淆。</summary>
        [Test]
        public void Count_TracksStockAcrossBothWritePrimitives()
        {
            ParticleWorld world = CreateWorld(8, 8);
            Assert.AreEqual(0, world.Counts.Count(GrainId), "新建世界没有颗粒");

            world.Set(2, 2, GrainId);
            world.Set(3, 2, GrainId);
            Assert.AreEqual(2, world.Counts.Count(GrainId));
            Assert.AreEqual(0, world.Counts.Count(LiquidId), "另一种类型不受影响");

            Assert.IsTrue(world.Clear(3, 2));
            Assert.AreEqual(1, world.Counts.Count(GrainId), "Clear 等价于 Set(..., AirTypeId)，必须递减");
        }

        /// <summary>
        /// 同类型覆盖写不改变存量（<see cref="ParticleWorld.Set"/> 的公开契约：同类型重复 Set 是空操作，
        /// 在钩子之前就 <c>return true</c>），也不产生「新增」——冗余写根本没触发
        /// <see cref="ParticleCountMonitor.OnCellReplaced"/>。
        /// </summary>
        [Test]
        public void Count_SameTypeOverwriteKeepsStock()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(2, 2, GrainId);
            world.Set(2, 2, GrainId);
            world.Set(2, 2, GrainId);

            Assert.AreEqual(1, world.Counts.Count(GrainId));
            Assert.IsTrue(world.GetCell(2, 2).typeId == GrainId);

            // 三次写入里只有第一次是真实的类型替换（空气 → 颗粒），后两次是空操作 ⇒ 新增恰好 1。
            Assert.AreEqual(1, world.Counts.ConsumeCreated(GrainId));
        }

        /// <summary>A → B 覆盖写：A 存量 −1、B 存量 +1，两端都正确。</summary>
        [Test]
        public void Count_TypeOverwriteMovesBothEnds()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(2, 2, GrainId);
            world.Set(4, 2, LiquidId);

            world.Set(2, 2, LiquidId);

            Assert.AreEqual(0, world.Counts.Count(GrainId), "被覆盖掉的颗粒离场");
            Assert.AreEqual(2, world.Counts.Count(LiquidId), "覆盖进去的液体入场");
        }

        /// <summary>
        /// Swap 不改变任何类型的存量，也不算「新增」：交换两格只是类型换位，没有格生灭
        /// （与 <see cref="ParticleWorld.Swap"/> 不调 <see cref="ParticleCountMonitor"/> 的钩子一致）。
        /// </summary>
        [Test]
        public void Count_SwapKeepsStockAndAddsNoCreated()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(2, 2, GrainId);
            world.Counts.ConsumeCreated(GrainId); // 先清空，让下面的「新增」断言只反映 Swap

            Assert.IsTrue(world.Swap(2, 2, 5, 5));

            Assert.AreEqual(1, world.Counts.Count(GrainId), "交换两格不改变存量");
            Assert.AreEqual(0, world.Counts.ConsumeCreated(GrainId), "交换不产生新格");
            Assert.IsTrue(world.GetCell(5, 5).typeId == GrainId, "颗粒确实被搬到了 (5,5)");
        }

        // ============================== 新增计数 ==============================

        /// <summary>ConsumeCreated 首次返回新写入的格数，再次调用返回 0，且不触碰存量。</summary>
        [Test]
        public void ConsumeCreated_ReturnsOnceThenZeroes()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(1, 1, GrainId);
            world.Set(2, 1, GrainId);
            world.Set(3, 1, GrainId);

            Assert.AreEqual(3, world.Counts.ConsumeCreated(GrainId));
            Assert.AreEqual(0, world.Counts.ConsumeCreated(GrainId), "读后清零");
            Assert.AreEqual(3, world.Counts.Count(GrainId), "读新增不得动存量");

            world.Set(4, 1, GrainId);
            Assert.AreEqual(1, world.Counts.ConsumeCreated(GrainId), "清零后只累计此后新增");
        }

        /// <summary>ConsumeCreated 只清自己那一种类型：读颗粒不得动液体的新增计数。</summary>
        [Test]
        public void ConsumeCreated_OnlyClearsTheRequestedType()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(1, 1, GrainId);
            world.Set(2, 1, LiquidId);
            world.Set(3, 1, GrainId);

            Assert.AreEqual(2, world.Counts.ConsumeCreated(GrainId));
            Assert.AreEqual(0, world.Counts.ConsumeCreated(GrainId));
            Assert.AreEqual(1, world.Counts.ConsumeCreated(LiquidId), "读颗粒不得清掉液体的槽");

            world.Set(4, 1, LiquidId);
            Assert.AreEqual(1, world.Counts.ConsumeCreated(LiquidId));
        }

        // ============================== 边界 ==============================

        /// <summary>空气与越界 typeId：两个入口都返回 0、都不抛。</summary>
        [Test]
        public void Count_AirAndOutOfRangeAreZero()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(1, 1, GrainId);

            Assert.AreEqual(0, world.Counts.Count(ParticleRegistry.AirTypeId), "空气永不计数");
            Assert.AreEqual(0, world.Counts.Count(ushort.MaxValue), "越界读 0");
            Assert.AreEqual(0, world.Counts.Count((ushort)world.Types.Length), "恰好越界读 0");

            Assert.AreEqual(0, world.Counts.ConsumeCreated(ParticleRegistry.AirTypeId));
            Assert.AreEqual(0, world.Counts.ConsumeCreated(ushort.MaxValue), "越界新增读 0");
            Assert.AreEqual(0, world.Counts.ConsumeCreated((ushort)world.Types.Length));

            Assert.AreEqual(1, world.Counts.Count(GrainId), "越界查询不得扰动真实存量");
        }

        // ============================== 纯净性 ==============================

        /// <summary>
        /// 纯观察者：同种子、同布局、同推进节拍的两个世界，其中一个每步对**每种类型**反复读
        /// Count / ConsumeCreated，N 步后逐格（typeId / shade / flags）与热场、湿度场必须逐位一致，
        /// 且随机流位置一致（后续 NextUInt 逐值相同）。读数还必须与逐格真源相等。
        /// </summary>
        [Test]
        public void Counts_IsPureObserver_WorldAndRngStreamStayIdentical()
        {
            const int Steps = 60;

            ParticleWorld reference = BuildMotionScene();
            ParticleWorld probed = BuildMotionScene();

            for (int t = 0; t < Steps; t++)
            {
                reference.StepMotion();
                probed.StepMotion();

                for (ushort typeId = 0; typeId < probed.Types.Length; typeId++)
                {
                    probed.Counts.Count(typeId);
                    probed.Counts.ConsumeCreated(typeId);
                }

                if ((t & 1) == 1)
                {
                    reference.StepLogic();
                    probed.StepLogic();
                }
            }

            for (int i = 0; i < reference.CellCount; i++)
            {
                Assert.AreEqual(
                    (int)reference.Cells[i].typeId, (int)probed.Cells[i].typeId, $"格子 {i} 的 typeId 不一致");
                Assert.AreEqual(reference.Cells[i].shade, probed.Cells[i].shade, $"格子 {i} 的 shade 不一致");
                Assert.AreEqual(reference.Cells[i].flags, probed.Cells[i].flags, $"格子 {i} 的 flags 不一致");
                Assert.AreEqual(reference.Heat.Values[i], probed.Heat.Values[i], 0f, $"格子 {i} 的热量不一致");
            }

            for (int y = 0; y < reference.Height; y++)
            {
                for (int x = 0; x < reference.Width; x++)
                {
                    Assert.AreEqual(
                        reference.Humidity.Get(x, y), probed.Humidity.Get(x, y), 0f, $"({x},{y}) 的湿度不一致");
                }
            }

            Assert.AreEqual(reference.NextUInt(), probed.NextUInt(), "读取观察者不得消耗随机流");
            Assert.AreEqual(reference.NextUInt(), probed.NextUInt());

            Assert.AreEqual(CountInGrid(probed, GrainId), probed.Counts.Count(GrainId), "颗粒存量应与逐格真源一致");
            Assert.AreEqual(CountInGrid(probed, LiquidId), probed.Counts.Count(LiquidId), "液体存量应与逐格真源一致");
        }
    }
}
