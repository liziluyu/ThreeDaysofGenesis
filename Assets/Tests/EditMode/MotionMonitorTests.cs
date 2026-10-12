using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="MotionMonitor"/>（世界新成员 <see cref="ParticleWorld.LiquidMotion"/> /
    /// <see cref="ParticleWorld.SolidMotion"/>）的测试：按标签掩码分类、两条信号互不干扰、
    /// 存量差分（Set / Clear / Swap）、运动计数按换位匹配端数累加、比例语义与稀释、
    /// 以及「读观察者不改变世界与随机流」的纯净性。
    /// </summary>
    /// <remarks>
    /// <para>全部用隔离类型表（<see cref="TestTables.Build"/>）——**不碰全局注册表**，
    /// 因此本文件与生产注册顺序、与其它测试文件互不污染。</para>
    /// <para>类型表：1 = 无机固体（会下落）、2 = 带 <see cref="ParticleTags.Organic"/> 的固体（会下落，
    /// 应被固体那条排除）、3 = 液体。</para>
    /// </remarks>
    public sealed class MotionMonitorTests
    {
        private const ushort SandId = 1;
        private const ushort OrganicSolidId = 2;
        private const ushort WaterId = 3;
        private const uint SeedValue = 20261012u;

        /// <summary>隔离世界：1 = 无机固体、2 = 带 Organic 的固体、3 = 液体。</summary>
        private static ParticleWorld CreateWorld(int width, int height)
        {
            return new ParticleWorld(
                width,
                height,
                SeedValue,
                TestTables.Build(new TestGrainParticle(), new TestOrganicSolidParticle(), new TestLiquidParticle()));
        }

        /// <summary>逐格真源：某类型在网格上的实际格数（与观察者读数对照）。</summary>
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

        /// <summary>某类型所在的行（不存在则 −1）。用来断言「它确实下落了」而不必猜落点。</summary>
        private static int FindY(ParticleWorld world, ushort typeId)
        {
            Cell[] cells = world.Cells;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].typeId == typeId)
                {
                    return i / world.Width;
                }
            }

            return -1;
        }

        /// <summary>铺满底行：底行之下即网格外，这些固体永不移动（静止存量的来源）。</summary>
        private static void FillBottomRow(ParticleWorld world, ushort typeId)
        {
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, typeId);
            }
        }

        // ============================== 掩码口径 ==============================

        /// <summary>
        /// 固体那条只认「带 Solid 且不带 Organic」：无机固体下落计入，液体下落不计（互不干扰）。
        /// </summary>
        [Test]
        public void SolidMotion_TracksInorganicSolidsAndIgnoresLiquid()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(1, 6, SandId);
            world.Set(5, 6, WaterId);

            world.SolidMotion.Reset();
            world.LiquidMotion.Reset();
            world.StepMotion();

            Assert.Greater(world.SolidMotion.MovingCount, 0L, "无机固体下落要计入固体那条");
            Assert.Greater(world.LiquidMotion.MovingCount, 0L, "水下落要计入液体那条");
            Assert.Less(FindY(world, SandId), 6, "砂确实在下落");
            Assert.Less(FindY(world, WaterId), 6, "水确实在下落");
        }

        /// <summary>
        /// 带 <see cref="ParticleTags.Organic"/> 的固体（草种子那类）：它**确实在下落**，
        /// 但被 excludedTags 排除，固体那条恒为 0。
        /// </summary>
        [Test]
        public void SolidMotion_IgnoresOrganicSolidsEvenWhileTheyFall()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(4, 6, OrganicSolidId);

            world.SolidMotion.Reset();
            world.LiquidMotion.Reset();
            world.StepMotion();
            world.StepMotion();

            Assert.Less(FindY(world, OrganicSolidId), 6, "带 Organic 的固体确实在下落（否则本用例没有说服力）");
            Assert.AreEqual(0L, world.SolidMotion.MovingCount, "带 Organic 的固体被 excludedTags 排除");
            Assert.AreEqual(0, world.SolidMotion.TrackedCount, "排除的也不计入存量");
            Assert.AreEqual(0L, world.LiquidMotion.MovingCount, "固体不落在液体那条上");
        }

        /// <summary>同一次类型替换同时喂给两条：水↔湿土这类相变在液体那条 +1、在固体那条（无机固体=湿土）也 +1。</summary>
        [Test]
        public void TrackedCount_DiffersByMaskOnTheSameRewrite()
        {
            ParticleWorld world = CreateWorld(8, 8);

            world.Set(2, 2, WaterId);
            Assert.AreEqual(1, world.LiquidMotion.TrackedCount, "水入液体那条");
            Assert.AreEqual(0, world.SolidMotion.TrackedCount, "水不入固体那条");

            world.Set(2, 2, SandId);
            Assert.AreEqual(0, world.LiquidMotion.TrackedCount, "被覆盖掉的水离场");
            Assert.AreEqual(1, world.SolidMotion.TrackedCount, "覆盖进去的砂入场");

            world.Set(2, 2, OrganicSolidId);
            Assert.AreEqual(0, world.SolidMotion.TrackedCount, "换成带 Organic 的固体：固体那条 −1");
            Assert.AreEqual(0, world.LiquidMotion.TrackedCount);
        }

        // ============================== 存量语义 ==============================

        /// <summary>TrackedCount（原 LiquidCount）：Set 匹配格 +1、Clear −1、Swap 不改存量。</summary>
        [Test]
        public void TrackedCount_TracksStockAndSwapDoesNotMoveIt()
        {
            ParticleWorld world = CreateWorld(8, 8);
            Assert.AreEqual(0, world.SolidMotion.TrackedCount, "新建世界没有固体");

            world.Set(2, 2, SandId);
            Assert.AreEqual(1, world.SolidMotion.TrackedCount, "Set 进一个匹配格 +1");

            world.Set(3, 2, SandId);
            world.Set(3, 3, SandId);
            Assert.AreEqual(3, world.SolidMotion.TrackedCount);

            Assert.IsTrue(world.Clear(3, 3));
            Assert.AreEqual(2, world.SolidMotion.TrackedCount, "Clear 等价于 Set(..., AirTypeId)，必须递减");

            Assert.IsTrue(world.Swap(2, 2, 6, 6));
            Assert.AreEqual(2, world.SolidMotion.TrackedCount, "交换两格不改变存量");
            Assert.AreEqual(SandId, world.GetCell(6, 6).typeId, "匹配格确实被搬走了");
        }

        /// <summary>Reset 只清 MovingCount，不动 TrackedCount。</summary>
        [Test]
        public void Reset_ClearsOnlyTheMovingCount()
        {
            ParticleWorld world = CreateWorld(8, 8);
            world.Set(2, 6, SandId);

            world.SolidMotion.Reset();
            world.StepMotion();
            Assert.Greater(world.SolidMotion.MovingCount, 0L);
            int tracked = world.SolidMotion.TrackedCount;

            world.SolidMotion.Reset();

            Assert.AreEqual(0L, world.SolidMotion.MovingCount, "Reset 清空本轮移动计数");
            Assert.AreEqual(tracked, world.SolidMotion.TrackedCount, "Reset 不动存量");
        }

        // ============================== 比例语义 ==============================

        /// <summary>无存量 → 0；有存量 → MovingCount ÷ TrackedCount；超过存量时夹在 1。</summary>
        [Test]
        public void MovingRatio_IsCountOverStockAndClamped()
        {
            ParticleWorld world = CreateWorld(8, 8);
            Assert.AreEqual(0f, world.SolidMotion.MovingRatio, "没有匹配格 → 0");
            Assert.AreEqual(0f, world.LiquidMotion.MovingRatio, "没有液体 → 0");

            world.Set(2, 6, SandId);
            world.SolidMotion.Reset();
            world.StepMotion();

            Assert.Greater(world.SolidMotion.MovingCount, 0L);
            float expected = world.SolidMotion.MovingCount / (float)world.SolidMotion.TrackedCount;
            Assert.AreEqual(expected, world.SolidMotion.MovingRatio, 1e-6f, "比例 = 移动数 ÷ 存量");

            // 单格持续下落（不 Reset）：累计移动数很快超过存量 1 —— 比例必须夹在 1。
            world.SolidMotion.Reset();
            world.StepMotion();
            world.StepMotion();
            world.StepMotion();

            Assert.Greater(world.SolidMotion.MovingCount, (long)world.SolidMotion.TrackedCount, "累计移动数确实超过了存量");
            Assert.AreEqual(1f, world.SolidMotion.MovingRatio, 1e-6f, "比例夹在 [0, 1]");
        }

        /// <summary>
        /// **比例对固体会被稀释**：存量里绝大多数是常年不动的成员（铺满底行的固体），
        /// 少量真在下落的固体让 MovingCount 不为 0，但比例远小于 1 ——
        /// 这正是下落声用绝对 <see cref="MotionMonitor.MovingCount"/> 而不是比例的证据。
        /// </summary>
        [Test]
        public void MovingRatio_IsDilutedByStationarySolids_SoConsumersUseAbsoluteCount()
        {
            ParticleWorld world = CreateWorld(12, 12);
            FillBottomRow(world, SandId);

            int resting = world.SolidMotion.TrackedCount;
            Assert.AreEqual(world.Width, resting, "静止存量 = 底行整条");

            world.Set(6, 10, SandId); // 唯一真正在落的那一格
            world.SolidMotion.Reset();
            world.StepMotion();

            Assert.GreaterOrEqual(world.SolidMotion.MovingCount, 1L, "绝对数反映真实运动格数");
            Assert.AreEqual(resting + 1, world.SolidMotion.TrackedCount);
            Assert.AreEqual(
                world.SolidMotion.MovingCount / (float)world.SolidMotion.TrackedCount,
                world.SolidMotion.MovingRatio,
                1e-6f);
            Assert.Less(world.SolidMotion.MovingRatio, 0.25f, "静止成员把比例稀释到近 0——死区打不开");

            // 静水塘那侧反例：水的存量小、整片都在动 ⇒ 比例就是响度。这里只断言口径不同不算错。
            Assert.AreEqual(0f, world.LiquidMotion.MovingRatio, "同一世界里没有液体 ⇒ 液体那条为 0");
        }

        // ============================== 纯净性 ==============================

        /// <summary>
        /// 纯观察者：同种子、同布局、同推进节拍的两个世界，其中一个每步把两条观察者的读数全读一遍，
        /// N 步后逐格（typeId / shade / flags）与热场、湿度场必须逐位一致，且随机流位置一致。
        /// 读数还必须与逐格真源相等（防「只在空世界相等」的假阳性）。
        /// </summary>
        [Test]
        public void MotionMonitors_ArePureObservers_WorldAndRngStreamStayIdentical()
        {
            const int Steps = 60;

            ParticleWorld reference = BuildMotionScene();
            ParticleWorld probed = BuildMotionScene();

            for (int t = 0; t < Steps; t++)
            {
                reference.StepMotion();
                probed.StepMotion();

                // 每步把两条观察者的读数全读一遍（存量、移动数、比例），读完即清账——
                // 若读会写世界 / 消耗随机流，两个世界立刻分叉。
                Assert.AreEqual(CountInGrid(probed, SandId), probed.SolidMotion.TrackedCount, "固体存量应与逐格真源一致");
                Assert.AreEqual(CountInGrid(probed, WaterId), probed.LiquidMotion.TrackedCount, "液体存量应与逐格真源一致");

                float solidRatio = probed.SolidMotion.MovingRatio;
                float liquidRatio = probed.LiquidMotion.MovingRatio;
                Assert.GreaterOrEqual(solidRatio, 0f, "比例下界");
                Assert.LessOrEqual(solidRatio, 1f, "比例上界");
                Assert.GreaterOrEqual(liquidRatio, 0f, "比例下界");
                Assert.LessOrEqual(liquidRatio, 1f, "比例上界");

                probed.SolidMotion.Reset();
                probed.LiquidMotion.Reset();
                Assert.AreEqual(0L, probed.SolidMotion.MovingCount, "读后清账");
                Assert.AreEqual(0L, probed.LiquidMotion.MovingCount, "读后清账");

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

            Assert.AreEqual(CountInGrid(probed, SandId), probed.SolidMotion.TrackedCount, "固体存量应与逐格真源一致");
            Assert.AreEqual(CountInGrid(probed, WaterId), probed.LiquidMotion.TrackedCount, "液体存量应与逐格真源一致");
        }

        /// <summary>有真实运动发生的场景：底行铺满，上面高低错落的固体与液体（会下落 / 横流）。</summary>
        private static ParticleWorld BuildMotionScene()
        {
            ParticleWorld world = CreateWorld(10, 10);
            FillBottomRow(world, SandId);
            world.Set(2, 8, SandId);
            world.Set(3, 8, WaterId);
            world.Set(4, 8, OrganicSolidId);
            world.Set(5, 8, WaterId);
            world.Set(6, 9, SandId);
            return world;
        }

        /// <summary>测试专用的「带 Organic 的固体」：像草种子那样会下落，但应被固体运动观察者排除。</summary>
        private sealed class TestOrganicSolidParticle : IParticleType
        {
            private static readonly Color32[] Palette = { new Color32(120, 200, 60, 255) };

            public ParticleTypeInfo Info
            {
                get
                {
                    return new ParticleTypeInfo(
                        ParticleTags.Solid | ParticleTags.Organic, 1f, 1f, 1f, 1f, true, false, 0, 0.5f, 0.5f,
                        Palette, "TestOrganicSolidParticle");
                }
            }

            public void OnMotion(ParticleWorld world, int x, int y)
            {
                ParticleRules.TryFall(world, x, y);
            }

            public void OnLogic(ParticleWorld world, int x, int y)
            {
            }

            public void OnRandomTick(ParticleWorld world, int x, int y)
            {
            }

            public void OnSpawn(ParticleWorld world, int x, int y)
            {
            }

            public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
            {
            }
        }
    }
}
