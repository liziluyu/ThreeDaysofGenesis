using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 运动休眠（阶段 B）的测试：休眠透明性、静止场景的求值收益、石头净空链远距唤醒、
    /// 以及 <see cref="ParticleWorld.MotionSleepEnabled"/> 的回退与切换。
    /// </summary>
    /// <remarks>
    /// <para>透明性与唤醒测试走生产注册表（<see cref="ParticleRegistration.RegisterAll"/>）：需要水 / 土 / 石 /
    /// 水生植物等真实类型的相互作用，且 <see cref="ParticleIds"/> 必须已回填。</para>
    /// <para>对比时 <see cref="CellFlags.ProcessedParity"/>（bit0）必须屏蔽：它是「本帧已处理」的瞬时标记，
    /// 被休眠跳过的格保留旧奇偶，因此 on/off 两个世界在 bit0 上本来就可能不同——这不是语义差异。</para>
    /// </remarks>
    public sealed class MotionSleepTests
    {
        private const byte FlagsMask = CellFlags.ProcessedParityMask;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>
        /// 休眠透明性：同一确定性场景（受热浅水池促对流 + 石壁 + 沙 / 土层 + 水生植物）分别以
        /// 休眠开 / 关跑 N 个「3 运动帧 + 1 逻辑帧」周期，最终逐格（typeId / shade / 非 bit0 的 flags）
        /// 与热场、湿度场必须完全一致。
        /// </summary>
        [Test]
        public void MotionSleep_IsTransparentToAFullScan()
        {
            ParticleWorld sleeping = BuildHeatedScene();
            ParticleWorld full = BuildHeatedScene();
            full.MotionSleepEnabled = false;

            const int Cycles = 200;
            for (int t = 0; t < Cycles; t++)
            {
                sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepLogic();
                full.StepMotion(); full.StepMotion(); full.StepMotion(); full.StepLogic();
            }

            Assert.AreEqual(full.Tick, sleeping.Tick, "时基必须一致");
            Cell[] a = sleeping.Cells;
            Cell[] b = full.Cells;
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(b[i].typeId, a[i].typeId, $"格子 {i} 的 typeId 不一致");
                Assert.AreEqual(b[i].shade, a[i].shade, $"格子 {i} 的 shade 不一致");
                Assert.AreEqual((byte)(b[i].flags & FlagsMask), (byte)(a[i].flags & FlagsMask), $"格子 {i} 的 flags 不一致");
            }

            float[] ha = sleeping.Heat.Values;
            float[] hb = full.Heat.Values;
            for (int i = 0; i < ha.Length; i++)
            {
                Assert.AreEqual(hb[i], ha[i], 0f, $"格子 {i} 的热量不一致");
            }

            for (int y = 0; y < sleeping.Height; y++)
            {
                for (int x = 0; x < sleeping.Width; x++)
                {
                    Assert.AreEqual(full.Humidity.Get(x, y), sleeping.Humidity.Get(x, y), 0f, $"({x},{y}) 的湿度不一致");
                }
            }
        }

        /// <summary>
        /// 静止场景的求值收益：一整池落在世界底行上的水（无处可流、无处可落）里，稳定之后
        /// <see cref="ParticleWorld.MotionCellsEvaluatedLastStep"/> 应为 0（远小于全网格 5%）；
        /// 丢一颗沙下去后计数必须立刻回升，证明计数不是恒 0 的死值。
        /// </summary>
        [Test]
        public void MotionSleep_StillPoolEvaluatesAlmostNothing()
        {
            const int W = 24, H = 16;
            ParticleWorld world = new ParticleWorld(W, H, 4242u);
            // 隔离热输运（传导 / 散热 / 光照全关）：静止只由运动休眠决定，热唤醒在透明性测试里覆盖。
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Light.Enabled = false;

            for (int x = 0; x < W; x++)
            {
                world.Set(x, 0, ParticleIds.Water);
                world.Set(x, 1, ParticleIds.Water);
            }

            world.StepMotion(); // 初态整张位图置位：全池求值一次
            world.StepMotion(); // 池子没动过 = 没有写入：候选位图应为空

            Assert.AreEqual(0, world.MotionCellsEvaluatedLastStep,
                $"静止水池不该再求值（全网格 {world.CellCount} 格）");

            world.Set(5, 12, ParticleIds.Sand);
            world.StepMotion();
            Assert.Greater(world.MotionCellsEvaluatedLastStep, 0, "写入必须唤醒求值");
        }

        /// <summary>
        /// 石头净空链的远距唤醒：粘住的石头（净空 19）初始被最远那一格（Chebyshev 距离 19）挡住而不滑；
        /// 在该格写入（清空）后，石头必须在下一运动帧正确滑动——覆盖 <see cref="MotionReadBox"/> 声明的
        /// 最深读距。
        /// </summary>
        [Test]
        public void MotionSleep_WakesStoneAcrossItsWholeClearanceRange()
        {
            const int W = 12, H = 30;
            ParticleWorld world = new ParticleWorld(W, H, 7u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Light.Enabled = false;

            ushort stone = ParticleIds.Stone;
            // 不可动的大石块（每一侧净空链的第一格都是石头或越界）。
            for (int x = 0; x <= 10; x++)
            {
                for (int y = 0; y <= 24; y++)
                {
                    world.Set(x, y, stone);
                }
            }

            // 走廊底部的柱子：右侧越界、左侧石块、下方石块 ⇒ 不可动。
            for (int y = 0; y <= 6; y++)
            {
                world.Set(11, y, stone);
            }

            int sx = 10, sy = 25;
            world.Set(sx, sy, stone);       // 被测石头（坐在大石块顶上）
            world.Set(sx - 1, sy, stone);   // 粘住它、且自身不可动的邻居

            world.StepMotion(); // 初态整张位图置位：全网格求值一次
            Assert.AreEqual((int)stone, (int)world.GetCell(sx, sy).typeId, "净空链被最远那格挡住时不得滑动");

            world.StepMotion(); // 没有任何写入 ⇒ 候选位图应为空
            Assert.AreEqual(0, world.MotionCellsEvaluatedLastStep, "静止石头不该被反复求值");
            Assert.AreEqual((int)stone, (int)world.GetCell(sx, sy).typeId, "石头仍然不动");

            // 清空的那一格 (sx+1, BlockerY) 在石头 (sx, sy) 斜下 19 格处，正好覆盖声明的阅读盒深度。
            const int BlockerY = 6;
            world.Clear(sx + 1, BlockerY);
            world.StepMotion();

            Assert.AreEqual((int)stone, (int)world.GetCell(sx + 1, sy - 1).typeId, "远端清空后石头必须滑到斜下方");
            Assert.IsTrue(world.IsAir(sx, sy), "原格应已空");
        }

        /// <summary>
        /// <see cref="ParticleWorld.MotionSleepEnabled"/> 的语义：false 时必须走全网格旧路径
        /// （求值数约为全部粒子、远大于静止池比例），且与 true 的逐格结果一致；
        /// 运行中来回切换也不能破坏状态。
        /// </summary>
        [Test]
        public void MotionSleep_DisabledFallsBackToFullScan()
        {
            ParticleWorld world = BuildHeatedScene();
            ParticleWorld reference = BuildHeatedScene();
            reference.MotionSleepEnabled = false;

            for (int t = 0; t < 60; t++)
            {
                world.StepMotion(); world.StepMotion(); world.StepMotion(); world.StepLogic();
                reference.StepMotion(); reference.StepMotion(); reference.StepMotion(); reference.StepLogic();
            }

            // 切到全扫描：求值数应当接近全部粒子（而不是静止池那样的 0）。
            world.MotionSleepEnabled = false;
            world.StepMotion();
            reference.StepMotion();
            int fullScanEvaluated = world.MotionCellsEvaluatedLastStep;
            Assert.Greater(fullScanEvaluated, world.CellCount / 20, "全扫描路径必须求值远多于 5% 的格");
            Assert.AreEqual(reference.Tick, world.Tick);
            AssertGridsEqual(reference, world);

            // 切回休眠：位图整张置位（一次全量），状态仍与全扫描参考一致。
            world.MotionSleepEnabled = true;
            world.StepMotion();
            reference.StepMotion();
            Assert.AreEqual(reference.Tick, world.Tick);
            AssertGridsEqual(reference, world);

            for (int t = 0; t < 20; t++)
            {
                world.StepMotion(); world.StepMotion(); world.StepMotion(); world.StepLogic();
                reference.StepMotion(); reference.StepMotion(); reference.StepMotion(); reference.StepLogic();
            }

            AssertGridsEqual(reference, world);
        }

        /// <summary>
        /// 湿度变化也要唤醒：<c>T = Q / C</c> 而 C 含湿度附加项，湿度输送（扩散 / 温度定向平流）
        /// **不经任何写入**就能改变空气格的温度，因此必须与热图一样把变化格并入候选位图。
        /// 场景：一整个世界都是大气（非静态、能收到 OnMotion），热输运与光照全关、湿度低于饱和
        /// （不降水 ⇒ 整帧没有任何 Set / Swap），只靠一步湿度输送制造 Δ湿度。
        /// </summary>
        /// <remarks>
        /// 世界取 32×32。温度脏格按**逐格 Chebyshev 半径 2 唤醒球**并入候选位图（球覆盖运动决策的
        /// 温度读距，候选集 ⊇ 真实依赖集，超集语义安全），因此只有变化格周围的球被唤醒，而不是整张网格。
        /// </remarks>
        [Test]
        public void MotionSleep_WakesCellsWhoseHumidityChanged()
        {
            const int W = 32, H = 32;
            ParticleWorld world = new ParticleWorld(W, H, 99u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Heat.AdiabaticLapse = 0f;
            world.Light.Enabled = false;

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                }
            }

            // 消化初态（构造时整张位图置位）后的下一个运动帧应为 0：全场静止、无写入、无温差。
            world.StepMotion();
            world.StepMotion();
            Assert.AreEqual(0, world.MotionCellsEvaluatedLastStep, "静止大气场不该被求值");

            // 低于饱和湿度（T = 0 时饱和 ≈ 0.05）的一个湿度点：一步输送只改湿度，不写任何格。
            world.Humidity.Set(6, 6, 0.04f);
            world.Humidity.ClearDirty();
            world.StepLogic();
            world.StepMotion();

            Assert.Greater(world.MotionCellsEvaluatedLastStep, 0, "湿度变化必须唤醒读温度的运动决策");
            Assert.Less(world.MotionCellsEvaluatedLastStep, world.CellCount, "只该唤醒变化格周围的半径 2 球，而不是整张网格");
        }

        /// <summary>受热浅水池 + 石壁 + 沙 / 土 / 水生植物：既有运动写入、也有热变化，用于透明性对照。</summary>
        private static ParticleWorld BuildHeatedScene()
        {
            const int W = 40, H = 28;
            ParticleWorld world = new ParticleWorld(W, H, 987654321u);
            world.Heat.Conductivity = 0.24f;
            world.Heat.VoidHeatLoss = 0.12f;
            world.Heat.RadiativeLoss = 0.05f;
            world.Light.Enabled = true;

            ushort stone = ParticleIds.Stone, sand = ParticleIds.Sand, soil = ParticleIds.Soil;
            ushort water = ParticleIds.Water, aplant = ParticleIds.AquaticPlant;

            for (int x = 0; x < W; x++)
            {
                world.Set(x, 0, stone);
                world.Set(x, 1, stone);
            }

            for (int y = 0; y < H; y++)
            {
                world.Set(0, y, stone);
                world.Set(W - 1, y, stone);
            }

            for (int x = 1; x < 15; x++)
            {
                for (int y = 2; y < 8; y++)
                {
                    world.Set(x, y, sand);
                }

                for (int y = 8; y < 11; y++)
                {
                    world.Set(x, y, soil);
                }

                world.Set(x, 11, stone);
            }

            for (int x = 16; x < W - 1; x++)
            {
                for (int y = 2; y < 6; y++)
                {
                    world.Set(x, y, sand);
                }

                for (int y = 6; y < 18; y++)
                {
                    world.Set(x, y, water);
                }
            }

            for (int y = 6; y < 12; y++)
            {
                world.Set(22, y, aplant);
            }

            return world;
        }

        private static void AssertGridsEqual(ParticleWorld expected, ParticleWorld actual)
        {
            Cell[] a = expected.Cells;
            Cell[] b = actual.Cells;
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].typeId, b[i].typeId, $"格子 {i} 的 typeId 不一致");
                Assert.AreEqual(a[i].shade, b[i].shade, $"格子 {i} 的 shade 不一致");
                Assert.AreEqual((byte)(a[i].flags & FlagsMask), (byte)(b[i].flags & FlagsMask), $"格子 {i} 的 flags 不一致");
            }
        }
    }
}
