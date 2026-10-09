using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="HighSpeedSession"/> 的行为契约：进入 / 退出恢复倍速状态、产生 / 死亡计数（含空气、Swap 不计）、
    /// 采样节拍与最新值（未采样为 NaN）、地表高度的增量维护、湿度总量的 O(1) 增量镜像与逐格真源一致，
    /// 跑满时长的一次性终态存量扫描（含空气；未结算 / Exit 为 0），
    /// 以及「监视器 / 扫描是纯观察者」的确定性回归（挂与不挂会话，世界逐格与 RNG 流位置完全一致）。
    /// </summary>
    /// <remarks>
    /// 全部用隔离类型表（<see cref="TestTables.Build"/>）构造世界，不触碰全局注册表；只调公开 API，
    /// 不依赖 Unity 时钟，也不跑驱动。
    /// </remarks>
    public sealed class HighSpeedSessionTests
    {
        // ============================== 统计：产生 / 死亡 ==============================

        /// <summary>
        /// <c>Set</c> / <c>SetPhase</c> / <c>Clear</c> 的计数语义：old ≠ new 才计一次死亡 + 一次产生，
        /// 空气也是一条计数项；同类型重复 Set 是空操作、不计数。
        /// </summary>
        [Test]
        public void SetClearAndSetPhase_CountBirthsAndDeathsIncludingAir()
        {
            const ushort grain = 1;
            const ushort atmosphere = 2;
            ParticleWorld world = new ParticleWorld(
                4, 4, 7u, TestTables.Build(new TestGrainParticle(), new AtmosphereParticle()));

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 100);
            session.Enter(speed);

            world.Set(1, 1, grain);                                   // 空气 → 固体
            Assert.AreEqual(1L, session.GetBirths(grain));
            Assert.AreEqual(1L, session.GetDeaths(ParticleRegistry.AirTypeId), "被覆盖的空气记一次死亡");
            Assert.AreEqual(0L, session.GetBirths(ParticleRegistry.AirTypeId));
            Assert.AreEqual(0L, session.GetDeaths(grain));

            world.Set(1, 1, grain);                                   // 同类型重复 Set：空操作
            Assert.AreEqual(1L, session.GetBirths(grain), "同类型重复 Set 不计数");
            Assert.AreEqual(1L, session.GetDeaths(ParticleRegistry.AirTypeId));

            world.Set(1, 1, atmosphere);                              // 固体 → 气体
            Assert.AreEqual(1L, session.GetDeaths(grain), "被覆盖的固体记一次死亡");
            Assert.AreEqual(1L, session.GetBirths(atmosphere));

            world.SetPhase(2, 2, grain);                              // 空气 → 固体（相变路径）
            Assert.AreEqual(2L, session.GetBirths(grain));
            Assert.AreEqual(2L, session.GetDeaths(ParticleRegistry.AirTypeId));

            world.Clear(1, 1);                                        // 气体 → 空气
            Assert.AreEqual(1L, session.GetDeaths(atmosphere));
            Assert.AreEqual(1L, session.GetBirths(ParticleRegistry.AirTypeId), "抹除后空气诞生");

            Assert.AreEqual(0L, session.GetBirths(999), "越界 typeId 返回 0");
            Assert.AreEqual(0L, session.GetDeaths(999));

            session.Exit(speed);
            Assert.AreEqual(0L, session.GetBirths(grain), "Exit 清计数");
            Assert.AreEqual(0L, session.GetDeaths(grain));
        }

        /// <summary><c>Swap</c> 是搬运不是生灭：不改变任何计数，只更新两端列的地表高度。</summary>
        [Test]
        public void Swap_DoesNotChangeCounters()
        {
            const ushort grain = 1;
            const ushort atmosphere = 2;
            ParticleWorld world = new ParticleWorld(
                4, 4, 11u, TestTables.Build(new TestGrainParticle(), new AtmosphereParticle()));

            world.Set(0, 0, grain);
            world.Set(0, 1, atmosphere);

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 100);
            session.Enter(speed);

            long grainBirths = session.GetBirths(grain);
            long grainDeaths = session.GetDeaths(grain);
            long airBirths = session.GetBirths(ParticleRegistry.AirTypeId);
            long airDeaths = session.GetDeaths(ParticleRegistry.AirTypeId);

            Assert.IsTrue(world.Swap(0, 0, 0, 1));

            Assert.AreEqual(grainBirths, session.GetBirths(grain));
            Assert.AreEqual(grainDeaths, session.GetDeaths(grain));
            Assert.AreEqual(airBirths, session.GetBirths(ParticleRegistry.AirTypeId));
            Assert.AreEqual(airDeaths, session.GetDeaths(ParticleRegistry.AirTypeId));

            session.Exit(speed);
        }

        // ============================== 采样节拍与最新值 ==============================

        [Test]
        public void Sampling_HappensEveryTenTicksAndLatestValuesStartNaN()
        {
            ParticleWorld world = new ParticleWorld(2, 2, 3u, TestTables.Build(new TestStaticParticle()));
            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 30);
            session.Enter(speed);

            Assert.IsNaN(session.LatestSurfaceTemperature, "未采样时地表温度是 NaN");
            Assert.IsNaN(session.LatestAverageHumidity, "未采样时平均湿度是 NaN");
            Assert.AreEqual(0, session.SampleCount);

            for (int i = 0; i < 9; i++)
            {
                session.OnLogicTick();
            }

            Assert.AreEqual(0, session.SampleCount, "第 9 逻辑帧还没有样本");
            Assert.IsNaN(session.LatestSurfaceTemperature);
            Assert.IsFalse(session.Completed);

            session.OnLogicTick();
            Assert.AreEqual(1, session.SampleCount, "第 10 逻辑帧出第一个样本");
            Assert.IsFalse(float.IsNaN(session.LatestSurfaceTemperature));
            Assert.IsFalse(float.IsNaN(session.LatestAverageHumidity));

            for (int i = 0; i < 10; i++)
            {
                session.OnLogicTick();
            }

            Assert.AreEqual(2, session.SampleCount, "第 20 逻辑帧出第二个样本");
            Assert.AreEqual(2, session.SurfaceTemperatureSamples.Count);
            Assert.AreEqual(2, session.AverageHumiditySamples.Count);

            session.Exit(speed);
        }

        // ============================== 地表高度 ==============================

        /// <summary>
        /// 地表平均温度只读每列**最上方的非气体格**：进入时全列扫一次，之后靠 O(1) 增量维护——
        /// 上方新增固体把地表抬上去，顶端固体被抹除掉下来重新找到下一个非气体，气体不算地表。
        /// </summary>
        [Test]
        public void SurfaceTemperature_TracksTheTopmostNonGasCell()
        {
            const ushort rock = 1;
            const ushort atmosphere = 2;
            const float groundTemperature = 10f;
            const float ridgeTemperature = 30f;

            ParticleWorld world = new ParticleWorld(
                1, 5, 5u, TestTables.Build(new TestStaticParticle(), new AtmosphereParticle()));

            world.Set(0, 0, rock);
            world.Set(0, 1, atmosphere);                 // 气体不算地表
            world.Heat.SetHeat(0, 0, groundTemperature); // 热容 1 ⇒ T = Q

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 1000);
            session.Enter(speed);

            SampleOnce(session);
            Assert.AreEqual(groundTemperature, session.LatestSurfaceTemperature, 1e-4f, "地表在 y = 0（气体不算）");

            world.Set(0, 2, rock);                       // 上方新增固体 ⇒ 地表上移
            world.Heat.SetHeat(0, 2, ridgeTemperature);
            SampleOnce(session);
            Assert.AreEqual(ridgeTemperature, session.LatestSurfaceTemperature, 1e-4f, "地表上移到 y = 2");

            world.Clear(0, 2);                           // 顶端固体被抹除 ⇒ 地表下移到下一个非气体
            SampleOnce(session);
            Assert.AreEqual(groundTemperature, session.LatestSurfaceTemperature, 1e-4f, "地表下移回 y = 0");

            world.Clear(0, 0);                           // 整列没有非气体 ⇒ 该列贡献 0
            SampleOnce(session);
            Assert.AreEqual(0f, session.LatestSurfaceTemperature, 1e-4f, "整列皆气体时贡献 0");

            session.Exit(speed);
        }

        // ============================== 湿度总量 ==============================

        /// <summary>
        /// 湿度场 O(1) 增量维护的总量必须与逐格求和一致：写路径（Set / Add / AddAt）逐笔精确记账，
        /// 覆盖持湿格时 <c>EvacuateHumidity</c> 推给四邻（总量不变），输送内核的成对运送也不改总量。
        /// </summary>
        [Test]
        public void TotalHumidity_MatchesDirectFieldScan()
        {
            const ushort rock = 1;
            ParticleWorld world = new ParticleWorld(4, 4, 13u, TestTables.Build(new TestStaticParticle()));
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Humidity.Diffusion = 0.25f;      // 打开输送，让成对运送真的发生
            world.Humidity.HorizontalBias = 0.2f;
            world.Humidity.RiseBias = 0.2f;

            world.Humidity.Set(1, 1, 0.5f);
            world.Humidity.Add(2, 2, 0.25f);
            world.Humidity.AddAt(world.Humidity.Index(3, 3), 0.75f);
            world.Heat.SetHeat(1, 1, 50f);          // 给温度定向项一点梯度

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 1000);
            session.Enter(speed);

            SampleOnce(session);
            Assert.AreEqual(
                world.Humidity.Sum(), session.LatestAverageHumidity * world.CellCount, 1e-6f,
                "纯写路径之后：增量总量必须等于逐格求和");

            world.Humidity.Set(0, 0, 0.4f);
            world.Set(0, 0, rock);                  // 覆盖持湿的空气格：水汽被推给四邻
            SampleOnce(session);
            Assert.AreEqual(
                world.Humidity.Sum(), session.LatestAverageHumidity * world.CellCount, 1e-6f,
                "疏散之后仍一致（疏散只搬运，不产不灭）");

            for (int i = 0; i < 10; i++)
            {
                world.Humidity.Step(world);         // 输送内核：成对反对称，总量不变
            }

            SampleOnce(session);
            Assert.AreEqual(
                world.Humidity.Sum(), session.LatestAverageHumidity * world.CellCount, 1e-3f,
                "输送之后仍一致（只差浮点 ulp 级漂移）");

            session.Exit(speed);
        }

        // ============================== 完成与倍速状态 ==============================

        [Test]
        public void Completed_FlipsExactlyAtDuration()
        {
            ParticleWorld world = new ParticleWorld(2, 2, 17u, TestTables.Build(new TestStaticParticle()));
            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 30);
            session.Enter(speed);

            Assert.IsFalse(session.Completed);
            Assert.AreEqual(0, session.ElapsedLogicTicks);
            Assert.AreEqual(30, session.DurationLogicTicks);

            for (int i = 0; i < 29; i++)
            {
                session.OnLogicTick();
            }

            Assert.AreEqual(29, session.ElapsedLogicTicks);
            Assert.IsFalse(session.Completed, "第 duration − 1 帧还没完成");

            session.OnLogicTick();
            Assert.AreEqual(30, session.ElapsedLogicTicks);
            Assert.IsTrue(session.Completed);
            Assert.IsTrue(session.Active, "完成后 Active 仍为 true，直到 Exit");

            for (int i = 0; i < 5; i++)
            {
                session.OnLogicTick();
            }

            Assert.AreEqual(30, session.ElapsedLogicTicks, "完成后不再计数");
            Assert.AreEqual(3, session.SampleCount, "满程样本数 = 时长 / 10");

            session.Exit(speed);
            Assert.IsFalse(session.Active);
            Assert.IsFalse(session.Completed);
            Assert.AreEqual(0, session.ElapsedLogicTicks, "Exit 清计时");
            Assert.AreEqual(0, session.SampleCount, "Exit 清样本");
            Assert.IsNaN(session.LatestSurfaceTemperature);
        }

        /// <summary>进入即钉满档 16× 手动；退出钉回 1×（普通编辑模式不允许调速度，恒 1×）；Enter/Exit 幂等。</summary>
        [Test]
        public void EnterPinsMaxSpeedAndExitReturnsToOneTimes()
        {
            ParticleWorld world = new ParticleWorld(2, 2, 19u, TestTables.Build(new TestStaticParticle()));
            var session = new HighSpeedSession(world, 10);

            var speed = new SpeedController();
            session.Enter(speed);
            Assert.IsTrue(session.Active);
            Assert.AreEqual(SpeedController.MaxSpeed, speed.Speed, "进入即钉满档 16×");
            Assert.IsFalse(speed.Adaptive, "满档是手动钉死，不走自适应");

            session.Exit(speed);
            Assert.IsFalse(session.Active);
            Assert.AreEqual(SpeedController.MinSpeed, speed.Speed, "退出钉回 1×");
            Assert.IsFalse(speed.Adaptive);

            // 不记旧档：无论从什么状态进入，退出都回 1×。
            speed.SetSpeed(4f);
            speed.SetAdaptive(true);
            session.Enter(speed);
            Assert.AreEqual(SpeedController.MaxSpeed, speed.Speed);
            session.Exit(speed);
            Assert.AreEqual(SpeedController.MinSpeed, speed.Speed, "退出不恢复进入前的 4× 自适应，钉回 1×");
            Assert.IsFalse(speed.Adaptive);

            // Enter 幂等：重复进入是空操作，计时与统计不重置。
            session.Enter(speed);
            session.OnLogicTick();
            session.Enter(speed);
            Assert.AreEqual(1, session.ElapsedLogicTicks, "重复 Enter 不重置计时");
            session.Exit(speed);
            session.Exit(speed);
            Assert.AreEqual(SpeedController.MinSpeed, speed.Speed);
        }

        // ============================== 确定性回归 ==============================

        /// <summary>
        /// 监视器是纯观察者：同种子、同命令序列，挂监视器的世界与不挂的世界跑若干逻辑帧后逐格相等
        /// （typeId / flags / shade / 热量 / 湿度），且随机流位置一致（后续 NextUInt 逐值相同）。
        /// </summary>
        [Test]
        public void Monitor_IsPureObserver_WorldAndRngStreamStayIdentical()
        {
            const int frames = 40;

            ParticleWorld reference = BuildDeterminismWorld();
            ParticleWorld monitored = BuildDeterminismWorld();

            var speed = new SpeedController();
            var session = new HighSpeedSession(monitored, 600);
            session.Enter(speed);

            for (int i = 0; i < frames; i++)
            {
                reference.StepLogic();
                monitored.StepLogic();
                session.OnLogicTick();
            }

            for (int i = 0; i < reference.CellCount; i++)
            {
                Assert.AreEqual(
                    (int)reference.Cells[i].typeId, (int)monitored.Cells[i].typeId,
                    $"格子 {i} 的 typeId 不一致");
                Assert.AreEqual(reference.Cells[i].flags, monitored.Cells[i].flags, $"格子 {i} 的 flags 不一致");
                Assert.AreEqual(reference.Cells[i].shade, monitored.Cells[i].shade, $"格子 {i} 的 shade 不一致");
                Assert.AreEqual(reference.Heat.Values[i], monitored.Heat.Values[i], $"格子 {i} 的热量不一致");
                Assert.AreEqual(
                    reference.Humidity.GetAt(i), monitored.Humidity.GetAt(i), $"格子 {i} 的湿度不一致");
            }

            Assert.AreEqual(reference.NextUInt(), monitored.NextUInt(), "监视器不得消耗随机流");
            Assert.AreEqual(reference.NextUInt(), monitored.NextUInt());

            session.Exit(speed);
        }

        // ============================== 终态存量扫描 ==============================

        /// <summary>
        /// 跑满时长那一刻对全网格扫一次存量：每种 typeId 的格数（空气也计）与逐格真源一致；
        /// 计数格数用 <c>GetCell(x,y).typeId</c> 自行统计做对照。
        /// </summary>
        [Test]
        public void FinalCount_CountsEveryTypeAcrossTheGridIncludingAir()
        {
            const ushort stone = 1;
            const ushort water = 2;
            const ushort soil = 3;

            ParticleWorld world = new ParticleWorld(
                4, 3, 23u,
                TestTables.Build(new TestStaticParticle(), new TestStaticParticle(), new TestStaticParticle()));

            for (int x = 0; x < 4; x++)
            {
                world.Set(x, 0, stone);            // 4 格石头
            }

            world.Set(0, 1, water);                // 2 格水
            world.Set(1, 1, water);
            world.Set(2, 1, soil);                 // 1 格土；其余 5 格是空气

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 3);
            session.Enter(speed);

            Assert.AreEqual(0L, session.GetFinalCount(stone), "未跑满时长前不扫描");

            for (int i = 0; i < 3; i++)
            {
                session.OnLogicTick();
            }

            Assert.IsTrue(session.Completed);
            Assert.AreEqual(4L, session.GetFinalCount(stone));
            Assert.AreEqual(2L, session.GetFinalCount(water));
            Assert.AreEqual(1L, session.GetFinalCount(soil));
            Assert.AreEqual(12 - 4 - 2 - 1, session.GetFinalCount(ParticleRegistry.AirTypeId), "空气也计数");

            for (ushort typeId = 0; typeId < world.Types.Length; typeId++)
            {
                Assert.AreEqual(
                    CountCells(world, typeId), session.GetFinalCount(typeId),
                    $"typeId {typeId} 的存量与逐格真源一致");
            }

            Assert.AreEqual(0L, session.GetFinalCount(999), "越界 typeId 返回 0");

            session.Exit(speed);
        }

        /// <summary>未跑满时长（未结算）时 <c>GetFinalCount</c> 恒为 0。</summary>
        [Test]
        public void FinalCount_ReturnsZeroBeforeCompletion()
        {
            ParticleWorld world = new ParticleWorld(2, 2, 29u, TestTables.Build(new TestStaticParticle()));
            world.Set(0, 0, 1);

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 5);
            session.Enter(speed);

            Assert.AreEqual(0L, session.GetFinalCount(0));
            Assert.AreEqual(0L, session.GetFinalCount(1));

            for (int i = 0; i < 4; i++)
            {
                session.OnLogicTick();
            }

            Assert.IsFalse(session.Completed);
            Assert.AreEqual(0L, session.GetFinalCount(1), "第 duration − 1 帧仍未结算 ⇒ 0");

            session.Exit(speed);
        }

        /// <summary>Exit 之后存量归零（会话结束后不再可读）。</summary>
        [Test]
        public void FinalCount_ReturnsZeroAfterExit()
        {
            ParticleWorld world = new ParticleWorld(2, 2, 31u, TestTables.Build(new TestStaticParticle()));
            world.Set(0, 0, 1);

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 2);
            session.Enter(speed);
            session.OnLogicTick();
            session.OnLogicTick();

            Assert.IsTrue(session.Completed);
            Assert.AreEqual(1L, session.GetFinalCount(1));

            session.Exit(speed);
            Assert.AreEqual(0L, session.GetFinalCount(1), "Exit 清终态存量");
            Assert.AreEqual(0L, session.GetFinalCount(ParticleRegistry.AirTypeId));
        }

        /// <summary>
        /// 终态扫描只在跑满时长那一刻发生一次：完成后再 <c>OnLogicTick</c> 不重扫，
        /// 完成之后的格子改动不会反映到已定格的存量里。
        /// </summary>
        [Test]
        public void FinalCount_IsScannedExactlyOnce()
        {
            ParticleWorld world = new ParticleWorld(3, 3, 37u, TestTables.Build(new TestStaticParticle()));
            world.Set(0, 0, 1);

            var speed = new SpeedController();
            var session = new HighSpeedSession(world, 2);
            session.Enter(speed);
            session.OnLogicTick();
            session.OnLogicTick();

            Assert.IsTrue(session.Completed);
            Assert.AreEqual(1L, session.GetFinalCount(1));
            Assert.AreEqual(8L, session.GetFinalCount(ParticleRegistry.AirTypeId));

            world.Set(1, 1, 1);                    // 完成之后的世界改动
            for (int i = 0; i < 3; i++)
            {
                session.OnLogicTick();
            }

            Assert.AreEqual(1L, session.GetFinalCount(1), "完成后的改动不被重扫进来");
            Assert.AreEqual(8L, session.GetFinalCount(ParticleRegistry.AirTypeId));
            Assert.AreEqual(2, session.ElapsedLogicTicks, "完成后计时不再增长");

            session.Exit(speed);
        }

        /// <summary>
        /// 终态扫描是纯观察者：同种子同命令序列，跑到「完成 + 扫描」那一刻，挂会话的世界与裸世界逐格相等、
        /// 随机流位置一致（后续 <c>NextUInt</c> 逐值相同），且存量与逐格真源一致。
        /// </summary>
        [Test]
        public void FinalScan_IsPureObserver_WorldAndRngStreamStayIdentical()
        {
            const int frames = 30;

            ParticleWorld reference = BuildDeterminismWorld();
            ParticleWorld monitored = BuildDeterminismWorld();

            var speed = new SpeedController();
            var session = new HighSpeedSession(monitored, frames);
            session.Enter(speed);

            for (int i = 0; i < frames; i++)
            {
                reference.StepLogic();
                monitored.StepLogic();
                session.OnLogicTick();
            }

            Assert.IsTrue(session.Completed, "帧数恰好覆盖完成 + 扫描那一刻");

            for (int i = 0; i < reference.CellCount; i++)
            {
                Assert.AreEqual(
                    (int)reference.Cells[i].typeId, (int)monitored.Cells[i].typeId,
                    $"格子 {i} 的 typeId 不一致");
                Assert.AreEqual(reference.Heat.Values[i], monitored.Heat.Values[i], $"格子 {i} 的热量不一致");
                Assert.AreEqual(
                    reference.Humidity.GetAt(i), monitored.Humidity.GetAt(i), $"格子 {i} 的湿度不一致");
            }

            Assert.AreEqual(reference.NextUInt(), monitored.NextUInt(), "终态扫描不得消耗随机流");
            Assert.AreEqual(reference.NextUInt(), monitored.NextUInt());

            for (ushort typeId = 0; typeId < monitored.Types.Length; typeId++)
            {
                Assert.AreEqual(
                    CountCells(monitored, typeId), session.GetFinalCount(typeId),
                    $"typeId {typeId} 的存量与逐格真源一致");
            }

            session.Exit(speed);
        }

        // ============================== 辅助 ==============================

        /// <summary>逐格统计 typeId 的格数（<c>GetFinalCount</c> 的真源对照）。</summary>
        private static long CountCells(ParticleWorld world, ushort typeId)
        {
            long count = 0;
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

        /// <summary>再推进 <see cref="HighSpeedSession.SampleInterval"/> 个逻辑帧，逼出一个新样本。</summary>
        private static void SampleOnce(HighSpeedSession session)
        {
            for (int i = 0; i < HighSpeedSession.SampleInterval; i++)
            {
                session.OnLogicTick();
            }
        }

        /// <summary>
        /// 两块**完全同构**的世界（唯一差别是其中一块挂了监视器）：固体堆 + 气体 + 湿度，
        /// 打开热传导 / 散热 / 光照与随机刻，让逻辑帧真的消耗随机数并改动世界。
        /// </summary>
        private static ParticleWorld BuildDeterminismWorld()
        {
            const int width = 12;
            const int height = 10;
            const ushort grain = 1;
            const ushort ticker = 2;
            const ushort atmosphere = 3;

            ParticleWorld world = new ParticleWorld(
                width, height, 20261007u,
                TestTables.Build(new TestGrainParticle(), new TestRandomTickParticle(), new AtmosphereParticle()));

            world.Heat.Conductivity = 0.24f;
            world.Heat.VoidHeatLoss = 0.12f;
            world.Heat.RadiativeLoss = 0.05f;
            world.Light.Enabled = true;
            world.RandomTicksPerStep = 8;

            for (int x = 0; x < width; x++)
            {
                world.Set(x, 0, grain);
            }

            for (int y = 1; y < 5; y++)
            {
                for (int x = 2; x < 8; x++)
                {
                    world.Set(x, y, grain);
                }
            }

            world.Set(1, 6, atmosphere);
            world.Set(2, 6, atmosphere);
            world.Set(3, 6, ticker);
            world.Humidity.Set(5, 5, 0.3f);
            world.Humidity.Add(6, 5, 0.2f);
            return world;
        }
    }
}
