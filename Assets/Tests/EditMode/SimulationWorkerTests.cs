using System;
using System.Threading;
using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 阶段 3 常驻 worker 的契约测试（见 docs/design-threaded-simulation-rendering.md）：
    /// 世界与 tick 循环跑在**另一条线程**上；命令**有序**、只在 tick 边界整批应用；
    /// 主线程**绝不等待**模拟；已发布的帧是完整快照；世界诊断由主线程侧记录。
    /// </summary>
    /// <remarks>
    /// <para>只走公开边界（<c>Submit</c> 同级的 <c>RequestBatch</c> / <c>CurrentFrame</c> / 回执），
    /// 测试里没有任何 <c>ParticleWorld</c> / <c>CreatureWorld</c> 句柄——世界状态一律经帧观察。</para>
    /// <para>确定性用 <c>RunBatchInline</c>（同一段 <c>Service</c>，绕开线程）与真线程逐批对比：
    /// 每个批次都等到回执才发下一批，因此两条路径的请求序列完全相同。</para>
    /// </remarks>
    public sealed class SimulationWorkerTests
    {
        private const uint Seed = 12345u;

        [OneTimeSetUp]
        public void RegisterTypes()
        {
            ParticleRegistration.RegisterAll();
        }

        private static SimulationWorkerSetup Setup(int width = 16, int height = 12)
        {
            return new SimulationWorkerSetup(
                width,
                height,
                Seed,
                logicTicksPerMotionTick: 3,
                logicRate: 20f,
                stepsPerPresent: 1,
                highSpeedLogicFrames: 60,
                new SimulationTickSettings(0, 0f, 0f, 0f, 0f, 1f, 1f, false, 0f),
                LightSettings.Default);
        }

        /// <summary>跑一批：<paramref name="threaded"/> 为真走 worker 线程并等回执，否则在调用线程上跑同一段 <c>Service</c>。</summary>
        private static SimulationBatchResult RunBatch(
            SimulationWorker worker, SimulationCommandQueue commands, int ticks, bool threaded)
        {
            Assert.IsTrue(worker.TryAcquireRequestSlot(out int slot), "应能取到空闲请求槽");
            worker.RequestBatch(slot, ticks, 1f, commands);

            if (threaded)
            {
                Assert.IsTrue(worker.TryWaitResponse(10000, out SimulationBatchResult result), "worker 未在超时内回执");
                return result;
            }

            SimulationBatchResult inline = worker.RunBatchInline();
            while (worker.TryReadResponse(out _))
            {
            }

            return inline;
        }

        /// <summary>确定性对比用的命令日志：同一份内容可以被两条路径各取一份（队列是破坏性抽干的）。</summary>
        private static SimulationCommandQueue[] BuildCommandLog()
        {
            var first = new SimulationCommandQueue();
            first.Enqueue(SimulationCommand.FillRect(0, 0, 16, 3, ParticleIds.Stone));
            first.Enqueue(SimulationCommand.PaintDisk(8, 9, 2, ParticleIds.Water, false, 0));
            first.Enqueue(SimulationCommand.PaintDisk(4, 9, 1, ParticleIds.Sand, false, 0));

            var second = new SimulationCommandQueue();
            second.Enqueue(SimulationCommand.ProbeCell(8, 9));
            second.Enqueue(SimulationCommand.ProbeCell(4, 9));

            var third = new SimulationCommandQueue();
            third.Enqueue(SimulationCommand.SetPaused(true));

            return new[] { first, second, third };
        }

        private static RenderColor[] Snapshot(SimulationFrame frame)
        {
            var copy = new RenderColor[frame.Render.Pixels.Length];
            Array.Copy(frame.Render.Pixels, copy, copy.Length);
            return copy;
        }

        private static void AssertPixelsEqual(RenderColor[] expected, RenderColor[] actual, string label)
        {
            Assert.AreEqual(expected.Length, actual.Length, label + "：像素数量");
            for (int i = 0; i < expected.Length; i++)
            {
                if (!expected[i].Equals(actual[i]))
                {
                    Assert.Fail($"{label}：像素 {i} 不一致（期望 {expected[i]}，实际 {actual[i]}）");
                }
            }
        }

        [Test]
        public void Construction_PublishesACompleteFirstFrameBeforeTheThreadStarts()
        {
            var worker = new SimulationWorker(Setup(8, 6));
            try
            {
                Assert.IsFalse(worker.Running, "构造不启线程");

                SimulationFrame frame = worker.CurrentFrame;
                Assert.IsNotNull(frame, "构造时就要有帧可读，消费方不必等第一次 Update");
                Assert.AreEqual(8, frame.World.Width);
                Assert.AreEqual(6, frame.World.Height);
                Assert.AreEqual(8 * 6, frame.Render.Pixels.Length);
                Assert.AreEqual(0, frame.World.Tick);
                Assert.AreEqual(1, frame.Generation);
                Assert.AreEqual(frame.Generation, worker.Frames.Generation);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void Commands_AreAppliedInSubmissionOrderWithinOneBatch()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                var commands = new SimulationCommandQueue();
                commands.Enqueue(SimulationCommand.PaintDisk(4, 6, 0, ParticleIds.Stone, false, 0));
                commands.Enqueue(SimulationCommand.PaintDisk(4, 6, 0, ParticleIds.Sand, false, 0));
                commands.Enqueue(SimulationCommand.ProbeCell(4, 6));

                SimulationBatchResult result = RunBatch(worker, commands, 0, threaded: false);

                Assert.AreEqual(3, result.CommandsApplied, "三条命令都在本批应用");
                CellProbe probe = worker.CurrentFrame.World.Probe;
                Assert.IsTrue(probe.Valid);
                Assert.AreEqual(ParticleIds.Sand, probe.TypeId, "同一格连投两次，后入队的沙必须覆盖先入队的石头");
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void Batch_AppliesCommandsBeforeTheFirstMotionTick_AndProbesAtThatBoundary()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                var commands = new SimulationCommandQueue();
                commands.Enqueue(SimulationCommand.PaintDisk(5, 9, 0, ParticleIds.Sand, false, 0));
                commands.Enqueue(SimulationCommand.ProbeCell(5, 9));

                // 本批跑 3 个运动 tick：沙会逐 tick 下落，因此「发布时再读」与「命令边界读」结果必然不同。
                SimulationBatchResult result = RunBatch(worker, commands, 3, threaded: false);
                Assert.AreEqual(3, result.ExecutedTicks);

                CellProbe probe = worker.CurrentFrame.World.Probe;
                Assert.AreEqual(ParticleIds.Sand, probe.TypeId,
                    "探针必须在命令边界当场采样：此后 tick 已经把沙挪走了");
                Assert.AreEqual(5, probe.X);
                Assert.AreEqual(9, probe.Y);

                // 再探一次同一格：此时沙确已下落，因此那一格不再是沙——反证上面的读数不是「发布时读的」。
                var after = new SimulationCommandQueue();
                after.Enqueue(SimulationCommand.ProbeCell(5, 9));
                RunBatch(worker, after, 0, threaded: false);

                Assert.AreNotEqual(ParticleIds.Sand, worker.CurrentFrame.World.Probe.TypeId,
                    "tick 之后原格已空，说明沙确实在命令之后才移动");
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void AtMostOneFrameIsPublishedPerBatch()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                var commands = new SimulationCommandQueue();
                commands.Enqueue(SimulationCommand.FillRect(0, 0, 16, 4, ParticleIds.Stone));

                SimulationBatchResult first = RunBatch(worker, commands, 10, threaded: false);
                Assert.IsTrue(first.Published, "应用过命令的批次必须发布一帧");
                long afterFirst = first.FrameSequence;

                SimulationBatchResult second = RunBatch(worker, null, 10, threaded: false);
                Assert.IsTrue(second.Published, "累计够 StepsPerPresent 的批次必须发布一帧");
                Assert.AreEqual(afterFirst + 1, second.FrameSequence, "一批至多发布一帧（高倍速下多推无意义）");
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void PausedWorker_DoesNotAdvanceTicks_ButStillAppliesCommands()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                var pause = new SimulationCommandQueue();
                pause.Enqueue(SimulationCommand.SetPaused(true));
                RunBatch(worker, pause, 5, threaded: false);

                int tick = worker.CurrentFrame.World.Tick;
                Assert.IsTrue(worker.CurrentFrame.World.Paused);

                SimulationBatchResult idle = RunBatch(worker, null, 5, threaded: false);
                Assert.AreEqual(0, idle.ExecutedTicks, "暂停时运动 / 逻辑帧一起冻结");
                Assert.IsFalse(idle.Published, "无事可做的暂停批次不发布帧");
                Assert.AreEqual(tick, worker.CurrentFrame.World.Tick);

                // 暂停中的命令照常抽干并上屏（画笔 / resize / PresentNow 在暂停时必须有效）。
                var present = new SimulationCommandQueue();
                present.Enqueue(SimulationCommand.PresentNow());
                SimulationBatchResult now = RunBatch(worker, present, 5, threaded: false);
                Assert.IsTrue(now.Published, "暂停中 PresentNow 必须能上屏");
                Assert.AreEqual(0, now.ExecutedTicks);
                Assert.AreEqual(tick, worker.CurrentFrame.World.Tick);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void Resize_RebuildsTheWorld_AdvancesGeneration_AndPublishesImmediately()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                var commands = new SimulationCommandQueue();
                commands.Enqueue(SimulationCommand.Resize(9, 7));

                SimulationBatchResult result = RunBatch(worker, commands, 0, threaded: false);
                Assert.IsTrue(result.Published, "resize 必须立即发布新世代的帧");

                SimulationFrame frame = worker.CurrentFrame;
                Assert.AreEqual(9, frame.World.Width);
                Assert.AreEqual(7, frame.World.Height);
                Assert.AreEqual(9 * 7, frame.Render.Pixels.Length, "像素数组绝不跨尺寸复用");
                Assert.AreEqual(2, frame.Generation, "重建世界 +1 世代，消费方据此丢掉旧视图");
                Assert.AreEqual(frame.Generation, result.Generation);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void MainThread_NeverWaits_BecauseRequestSlotsAreBounded()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                Assert.IsTrue(worker.TryAcquireRequestSlot(out int first));
                Assert.IsTrue(worker.TryAcquireRequestSlot(out int second));
                Assert.IsFalse(worker.TryAcquireRequestSlot(out _), "槽位满载时必须非阻塞失败，主线程绝不为模拟排队");

                worker.RequestBatch(first, 1, 1f, null);
                worker.RequestBatch(second, 1, 1f, null);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void WorldDiagnostics_AreQueuedForTheMainThreadInsteadOfLogged()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                var commands = new SimulationCommandQueue();
                commands.Enqueue(SimulationCommand.PaintDisk(2, 2, 0, ushort.MaxValue, false, 0));
                RunBatch(worker, commands, 0, threaded: false);

                bool sawMessage = false;
                while (worker.TryReadWorldMessage(out string message))
                {
                    sawMessage = true;
                    Assert.IsFalse(string.IsNullOrEmpty(message));
                }

                Assert.IsTrue(sawMessage, "世界的未注册类型诊断必须经 worker 队列交给主线程记录（worker 不调 Unity 日志）");
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void ThreadedWorker_RunsTheWorldOnAnotherThread()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                worker.Start();
                Assert.IsTrue(worker.Running);
                Assert.AreNotEqual(0, worker.WorkerThreadId);
                Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, worker.WorkerThreadId,
                    "世界必须由 worker 那条线程推进，而不是调用线程");

                SimulationBatchResult result = RunBatch(worker, null, 6, threaded: true);
                Assert.AreEqual(6, result.ExecutedTicks);
                Assert.AreEqual(6, worker.CurrentFrame.World.Tick);
                Assert.Greater(worker.Batches, 0);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void Stop_JoinsTheThread_AndStartCanResumeOnANewOne()
        {
            var worker = new SimulationWorker(Setup());
            try
            {
                worker.Start();
                RunBatch(worker, null, 3, threaded: true);
                int tickAfterFirstRun = worker.CurrentFrame.World.Tick;

                worker.Stop();
                Assert.IsFalse(worker.Running, "Stop 之后线程必须已 Join");
                Assert.AreEqual(0, worker.WorkerThreadId);

                worker.Start();
                SimulationBatchResult resumed = RunBatch(worker, null, 3, threaded: true);
                Assert.AreEqual(3, resumed.ExecutedTicks, "重启后沿用同一个世界继续推进");
                Assert.AreEqual(tickAfterFirstRun + 3, worker.CurrentFrame.World.Tick);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Test]
        public void ThreadedAndInlineBatches_ProduceIdenticalFrames()
        {
            var threaded = new SimulationWorker(Setup());
            var inline = new SimulationWorker(Setup());
            try
            {
                threaded.Start();

                // 两条路径各自拿到一份**内容相同**的命令日志（队列是破坏性抽干的，不能共用同一批实例）。
                // 每批都等到回执才发下一批 ⇒ 请求序列完全相同，结果必须逐位一致。
                SimulationCommandQueue[] threadBatches = BuildCommandLog();
                SimulationCommandQueue[] inlineBatches = BuildCommandLog();
                int[] ticks = { 6, 3, 0 };

                var threadResults = new SimulationBatchResult[ticks.Length];
                var inlineResults = new SimulationBatchResult[ticks.Length];

                for (int i = 0; i < ticks.Length; i++)
                {
                    threadResults[i] = RunBatch(threaded, threadBatches[i], ticks[i], threaded: true);
                    inlineResults[i] = RunBatch(inline, inlineBatches[i], ticks[i], threaded: false);
                }

                for (int i = 0; i < ticks.Length; i++)
                {
                    Assert.AreEqual(inlineResults[i].ExecutedTicks, threadResults[i].ExecutedTicks, $"批 {i} 执行的 tick 数");
                    Assert.AreEqual(inlineResults[i].Published, threadResults[i].Published, $"批 {i} 是否发布");
                    Assert.AreEqual(inlineResults[i].CommandsApplied, threadResults[i].CommandsApplied, $"批 {i} 应用的命令数");
                    Assert.AreEqual(inlineResults[i].FrameSequence, threadResults[i].FrameSequence, $"批 {i} 的帧序号");
                    Assert.AreEqual(inlineResults[i].Generation, threadResults[i].Generation, $"批 {i} 的世代号");
                }

                SimulationFrame expected = inline.CurrentFrame;
                SimulationFrame actual = threaded.CurrentFrame;

                Assert.AreEqual(expected.World.Tick, actual.World.Tick, "tick 数必须一致");
                Assert.AreEqual(expected.Generation, actual.Generation);
                Assert.AreEqual(expected.World.Paused, actual.World.Paused);
                Assert.AreEqual(expected.World.Probe.TypeId, actual.World.Probe.TypeId, "探针读数一致");
                Assert.AreEqual(expected.World.Probe.Temperature, actual.World.Probe.Temperature, 1e-6f);
                Assert.AreEqual(expected.Render.Daylight, actual.Render.Daylight, 1e-6f);

                // 像素帧是「世界 → 颜色」映射的真源：逐位一致即证明分线程没有改变推进结果。
                AssertPixelsEqual(Snapshot(expected), Snapshot(actual), "分线程与同步推进的像素帧");
            }
            finally
            {
                threaded.Dispose();
                inline.Dispose();
            }
        }
    }
}
