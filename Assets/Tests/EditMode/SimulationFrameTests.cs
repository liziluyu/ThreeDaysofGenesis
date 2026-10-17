using System.Reflection;
using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 阶段 1 帧边界的契约测试（见 docs/design-threaded-simulation-rendering.md）：
    /// 命令**有序**、只在 tick 边界应用；已发布的帧是**完整且不可变**的世界快照；
    /// 帧里的像素与「世界 → 像素」映射逐位一致；音频观察**读后不清账**，多个消费者各读各的。
    /// </summary>
    /// <remarks>
    /// <para>驱动用生产里的同一个 <see cref="ParticleSimulationDriver"/>（EditMode 不跑 <c>Awake</c> / <c>Update</c>，
    /// 因此反射调一次 <c>Awake</c>，再用公开的 <c>Advance(deltaTime)</c> 显式推进帧——与四个声音驱动器的
    /// <c>Pump</c> 同一口径）。小网格（16×12）省掉建大世界的开销。</para>
    /// <para>只走公开边界：测试里**没有任何** <c>ParticleWorld</c> / <c>CreatureWorld</c> 句柄来自驱动——
    /// 世界状态一律经帧（探针 / 像素 / 实体 / 音频）观察，这正是阶段 1 要立住的契约。</para>
    /// </remarks>
    public sealed class SimulationFrameTests
    {
        private const int W = 16;
        private const int H = 12;

        /// <summary>名义帧长（秒）：驱动按它计划 tick，值本身不影响这些用例的断言。</summary>
        private const float Frame = 1f / 60f;

        /// <summary>探针使用的中心格（格坐标）。</summary>
        private const int ProbeX = 5;

        /// <summary>探针使用的中心格（格坐标）。</summary>
        private const int ProbeY = 6;

        private GameObject _driverObject;
        private ParticleSimulationDriver _driver;
        private ParticleSimulationSettings _settings;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();

            _settings = ScriptableObject.CreateInstance<ParticleSimulationSettings>();
            var settingsSerialized = new SerializedObject(_settings);
            settingsSerialized.FindProperty("_width").intValue = W;
            settingsSerialized.FindProperty("_height").intValue = H;
            settingsSerialized.FindProperty("_randomTicksPerStep").intValue = 0;
            settingsSerialized.FindProperty("_stepsPerPresent").intValue = 1;
            settingsSerialized.ApplyModifiedPropertiesWithoutUndo();

            _driverObject = new GameObject("SimulationFrameTests.Driver");
            _driver = _driverObject.AddComponent<ParticleSimulationDriver>();

            var driverSerialized = new SerializedObject(_driver);
            driverSerialized.FindProperty("_settings").objectReferenceValue = _settings;
            driverSerialized.ApplyModifiedPropertiesWithoutUndo();

            // EditMode 里 AddComponent 不触发 Awake：显式调一次，世界 / 帧环 / 倍速控制器都在那里建。
            typeof(ParticleSimulationDriver)
                .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_driver, null);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_driverObject);
            Object.DestroyImmediate(_settings);
        }

        /// <summary>
        /// 推进驱动直到出现比 <paramref name="before"/> 更新的已发布帧（阶段 3 的 worker 是异步的：
        /// 单次 <c>Advance</c> 只负责「采样 + 非阻塞排一批请求 + 消费上一批成果」，因此测试必须泵到新帧为止，
        /// 而不是假设一次调用就恰好消费到本批结果）。超过预算仍没有新帧即失败。
        /// </summary>
        private SimulationFrame AdvanceUntilNewFrame(long before)
        {
            return AdvanceFrameUntilNewFrame(_driver, before);
        }

        /// <summary>同 <see cref="AdvanceUntilNewFrame"/>，但作用于任意驱动实例（确定性对比要泵两个驱动）。</summary>
        private static SimulationFrame AdvanceFrameUntilNewFrame(ParticleSimulationDriver driver, long before)
        {
            for (int i = 0; i < 4000; i++)
            {
                driver.Advance(Frame);
                SimulationFrame frame = driver.CurrentFrame;
                if (frame != null && frame.Sequence > before)
                {
                    return frame;
                }

                System.Threading.Thread.Sleep(1);
            }

            Assert.Fail("worker 未在预期时间内发布新帧");
            return null;
        }

        /// <summary>建好世界即发布一帧完整快照：五个 DTO 段都在，尺寸与设置一致。</summary>
        [Test]
        public void FirstFrame_IsCompleteAndSizedByTheSettings()
        {
            SimulationFrame frame = _driver.CurrentFrame;

            Assert.IsNotNull(frame, "Awake 之后必须已有一帧可读（消费者不必等第一次 Update）");
            Assert.AreEqual(1, frame.Sequence);
            Assert.AreEqual(W, frame.World.Width);
            Assert.AreEqual(H, frame.World.Height);
            Assert.AreEqual(W * H, frame.Render.Pixels.Length, "像素缓冲长度 = 宽 × 高");
            Assert.AreEqual(W * H, frame.Render.Width * frame.Render.Height);
            Assert.IsNotNull(frame.Creatures, "完整帧必须带上实体段");
            Assert.IsNotNull(frame.Audio, "完整帧必须带上音频观察段");
            Assert.IsNotNull(frame.HighSpeed, "完整帧必须带上高速状态段");
            Assert.IsFalse(frame.HighSpeed.Active, "尚未进入高速模式");
        }

        /// <summary>命令入队**本身不改变已发布的帧**：状态只在下一个 tick 边界（<c>Advance</c>）整体应用并发布。</summary>
        [Test]
        public void SubmittingCommands_PublishesNothingUntilTheNextBoundary()
        {
            long before = _driver.CurrentFrame.Sequence;

            _driver.Submit(SimulationCommand.PaintDisk(ProbeX, ProbeY, 0, ParticleIds.Sand, false, 0));
            _driver.Submit(SimulationCommand.ProbeCell(ProbeX, ProbeY));

            Assert.AreEqual(before, _driver.CurrentFrame.Sequence, "只是入队，还没有应用，也没有新帧");
            Assert.IsFalse(_driver.CurrentFrame.World.Probe.Valid, "入队前的帧里没有这次请求的探针读数");

            SimulationFrame frame = AdvanceUntilNewFrame(before);

            Assert.Greater(frame.Sequence, before, "tick 边界应用命令后必须发布新帧");
            Assert.IsTrue(frame.World.Probe.Valid, "新帧里带着请求的探针读数");
        }

        /// <summary>
        /// 命令**按入队顺序**应用：同格上「先涂后擦 ⇒ 空气」「先擦后涂 ⇒ 沙」，
        /// 且每条 <see cref="SimulationCommandKind.ProbeCell"/> 看到的都是它**之前**那些命令的结果。
        /// </summary>
        [Test]
        public void Commands_ApplyInSubmissionOrder()
        {
            // 先涂后擦 ⇒ 该格是空气。
            _driver.Submit(SimulationCommand.PaintDisk(ProbeX, ProbeY, 0, ParticleIds.Sand, false, 0));
            _driver.Submit(SimulationCommand.EraseDisk(ProbeX, ProbeY, 0));
            _driver.Submit(SimulationCommand.ProbeCell(ProbeX, ProbeY));
            SimulationFrame afterEraseFrame = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);

            CellProbe afterErase = afterEraseFrame.World.Probe;
            Assert.IsTrue(afterErase.Valid);
            Assert.AreEqual(ParticleRegistry.AirTypeId, afterErase.TypeId, "后到的擦除覆盖先到的投放");

            // 先擦后涂 ⇒ 该格是沙。
            _driver.Submit(SimulationCommand.EraseDisk(ProbeX, ProbeY, 0));
            _driver.Submit(SimulationCommand.PaintDisk(ProbeX, ProbeY, 0, ParticleIds.Sand, false, 0));
            _driver.Submit(SimulationCommand.ProbeCell(ProbeX, ProbeY));
            SimulationFrame afterPaintFrame = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);

            CellProbe afterPaint = afterPaintFrame.World.Probe;
            Assert.IsTrue(afterPaint.Valid);
            Assert.AreEqual(ParticleIds.Sand, afterPaint.TypeId, "后到的投放覆盖先到的擦除");
        }

        /// <summary>投放**笔刷半径**是命令的一部分：半径 1 的圆盘 = 中心 + 四邻（5 格），斜角不在内
        /// （驱动建的空世界填的是大气粒子而不是真空，因此圆盘外的格读到的就是大气）。</summary>
        [Test]
        public void PaintDiskCommand_CoversTheBrushDisk()
        {
            _driver.Submit(SimulationCommand.PaintDisk(ProbeX, ProbeY, 1, ParticleIds.Sand, false, 0));
            _driver.Submit(SimulationCommand.ProbeCell(ProbeX + 1, ProbeY));
            SimulationFrame right = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);
            Assert.AreEqual(ParticleIds.Sand, right.World.Probe.TypeId, "圆盘内的右邻格");

            _driver.Submit(SimulationCommand.ProbeCell(ProbeX + 1, ProbeY + 1));
            SimulationFrame diagonal = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);
            Assert.AreEqual(
                ParticleIds.Atmosphere,
                diagonal.World.Probe.TypeId,
                "圆盘外的斜角格没被笔刷碰到，仍是建世界时填的大气");
        }

        /// <summary>Resize 命令重建世界并**换世代**：新帧的尺寸、像素长度与世代号都跟着换。</summary>
        [Test]
        public void ResizeCommand_RebuildsTheWorldAndAdvancesTheGeneration()
        {
            int generation = _driver.CurrentFrame.Generation;

            _driver.Submit(SimulationCommand.Resize(8, 6));
            SimulationFrame frame = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);

            Assert.AreEqual(8, frame.World.Width);
            Assert.AreEqual(6, frame.World.Height);
            Assert.AreEqual(8 * 6, frame.Render.Pixels.Length, "像素缓冲随尺寸重建，绝不跨尺寸复用");
            Assert.AreEqual(generation + 1, frame.Generation, "换世界必须换世代号，消费方据此丢弃旧视图");
        }

        /// <summary>生成实体的命令在边界应用，实体经帧里的 DTO 观察：数量、物种、位置都在帧上说清楚。</summary>
        [Test]
        public void SpawnCreatureCommand_ShowsUpInTheCreatureFrame()
        {
            _driver.Submit(SimulationCommand.SpawnCreature(0, ProbeX, ProbeY));
            SimulationFrame frame = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);

            CreatureFrame creatures = frame.Creatures;
            Assert.AreEqual(1, creatures.Count, "生成命令应用后，帧里就该有一只实体");

            CreatureRenderData creature = creatures.Creature(0);
            Assert.AreEqual(CreatureSpecies.Rabbit, creature.Species, "物种下标 0 = 兔子");
            Assert.Greater(creature.Id, 0);
            Assert.Greater(creature.Width, 0f);
            Assert.AreEqual(ProbeX + 0.5f, creature.X, 1e-4f, "脚底中心落在该格中心");
        }

        /// <summary>
        /// 帧环的**发布松弛度**：3 槽轮换（<see cref="SimulationFramePublisher.SlotCount"/>）下，一个已发布的帧
        /// 在它被合法复用之前，至少还能挺过 **两次后续发布**——这正是「消费方读一帧、用一帧」的时间窗。
        /// </summary>
        /// <remarks>
        /// 这条契约住在发布器里（纯值、无线程），所以在这里**确定性地**验证它；异步 worker 那一侧只验证
        /// 「发布推进、且 Current 永远不是写着的那一槽」（见下一个用例）。
        /// </remarks>
        [Test]
        public void Publisher_KeepsAPublishedFrameStableForTwoFurtherPublishes()
        {
            var publisher = new SimulationFramePublisher(2, 1, generation: 7);

            SimulationFrame first = publisher.BeginWrite();
            first.Render.Pixels[0] = new RenderColor(1, 2, 3, 255);
            publisher.Publish();

            var captured = new RenderColor[2];
            System.Array.Copy(first.Render.Pixels, captured, captured.Length);

            Assert.AreSame(first, publisher.Current, "发布之后 Current 指向刚写完的槽");

            // 后续第 1、2 次发布：写的是另外两个槽，已发布帧的内容逐位不变。
            for (int i = 0; i < 2; i++)
            {
                SimulationFrame next = publisher.BeginWrite();
                Assert.AreNotSame(first, next, $"第 {i + 1} 次发布后写入的槽不得与已发布帧同槽");
                next.Render.Pixels[0] = new RenderColor((byte)(10 + i), 0, 0, 255);
                publisher.Publish();

                Assert.AreNotSame(first, publisher.Current, "Current 已前进到新帧");
                CollectionAssert.AreEqual(captured, first.Render.Pixels, $"两次后续发布之内，已发布帧内容不变（第 {i + 1} 次）");
            }

            Assert.AreEqual(3, publisher.Current.Sequence, "三次发布 ⇒ 序号 3");
            Assert.AreEqual(7, publisher.Current.Generation, "世代号随每一帧发布");

            // 第 3 次后续发布才轮到复用第一帧的槽（有意的「丢旧帧、不等消费方」策略）。
            SimulationFrame recycled = publisher.BeginWrite();
            Assert.AreSame(first, recycled, "3 槽轮换：第 3 次后续发布才复用最早那一槽");
        }

        /// <summary>
        /// worker 是异步生产者：驱动每次 <c>Advance</c> 都非阻塞地排一批请求，因此帧会持续推进；
        /// 已发布帧（<c>Current</c>）**永远不是工人正在写的那一槽**，读取它不会读到半写状态。
        /// </summary>
        /// <remarks>
        /// 这条异步侧契约只能断言「发布推进 + 帧自洽」，不能再断言「某一帧跨多次发布不变」——
        /// 那取决于主线程读取的时机（保证窗口只有两次发布，见上一个用例），不是异步生产者能承诺的。
        /// </remarks>
        [Test]
        public void AsyncDriver_PublishesAdvancingFramesWithoutPartialWrites()
        {
            // 造一柱会持续下落的水：后续帧的世界确实在变。
            _driver.Submit(SimulationCommand.PaintDisk(ProbeX, ProbeY, 1, ParticleIds.Water, false, 0));
            SimulationFrame first = AdvanceUntilNewFrame(_driver.CurrentFrame.Sequence);
            long sequence = first.Sequence;
            int tick = first.Render.Tick;

            SimulationFrame latest = null;
            long observed = sequence;
            for (int i = 0; i < 4000 && observed < sequence + 3; i++)
            {
                _driver.Advance(Frame);

                SimulationFrame current = _driver.CurrentFrame;
                if (current == null || current.Sequence <= observed)
                {
                    System.Threading.Thread.Sleep(1);
                    continue;
                }

                observed = current.Sequence;
                latest = current;

                // 同一帧连读两次必须逐位相同：Current 绝不是写着的那一槽，读它不会看到半写状态。
                var firstRead = new RenderColor[current.Render.Pixels.Length];
                System.Array.Copy(current.Render.Pixels, firstRead, firstRead.Length);
                CollectionAssert.AreEqual(firstRead, current.Render.Pixels, "同一已发布帧连读两次内容一致（无半写）");
                Assert.GreaterOrEqual(_driver.CurrentFrame.Sequence, current.Sequence, "Current 只会前进，不出现回退");
            }

            Assert.IsNotNull(latest, "worker 必须持续发布新帧");
            Assert.Greater(latest.Render.Tick, tick, "新帧的 tick 更靠后（世界确实在推进）");
        }

        /// <summary>帧里的像素与「世界 → 像素」映射逐位一致：白天（光照关闭 ⇒ 日照度恒 1）下，
        /// 空气格是白天天空色、孤立的沙格是该类型调色板的 shade 色**按描边系数调暗**（四邻皆空气 ⇒ 命中描边），
        /// 整场亮度为 1（不额外调暗）。</summary>
        [Test]
        public void RenderFrame_PixelsMatchTheKnownWorldMapping()
        {
            var world = new ParticleWorld(6, 4, 2026u);
            world.Light.Enabled = false;    // 没有昼夜循环 ⇒ Daylight 恒 1（白天）
            world.Set(1, 1, ParticleIds.Sand);

            var frames = new FramePump { Light = LightSettings.Default };
            SimulationFrame frame = frames.Publish(world);

            Assert.AreEqual(1f, frame.Render.Daylight, 1e-6f, "光照关闭时日照度恒为白天");
            Assert.AreEqual(1f, frame.Render.Brightness, 1e-6f, "白天不额外调暗");
            Assert.AreEqual(world.Index(1, 1), 6 + 1, "像素索引约定：行主序 y * width + x");

            RenderColor air = frame.Render.Pixels[world.Index(0, 0)];
            Assert.AreEqual(LightSettings.DefaultDayBackground, air, "空气格 = 白天天空色");

            ParticleType sand = ParticleRegistry.Types[ParticleIds.Sand];
            Color32 shade = sand.Info.Palette[world.GetCell(1, 1).shade];

            // 这一格沙是孤立的（四邻全是空气）⇒ 命中描边：基底 shade 色按 OutlineFactor 调暗，
            // 不是裸 shade 色（见 ParticlePixelBuffer.Fill 的描边判据）。期望值按 Unity 算式亲手算。
            var expectedSand = RenderColorConversion.FromColor32(new Color32(
                (byte)(shade.r * ParticlePixelBuffer.OutlineFactor),
                (byte)(shade.g * ParticlePixelBuffer.OutlineFactor),
                (byte)(shade.b * ParticlePixelBuffer.OutlineFactor),
                shade.a));
            Assert.AreEqual(expectedSand, frame.Render.Pixels[world.Index(1, 1)], "孤立沙格 = shade 色调暗 OutlineFactor 倍");
        }

        /// <summary>
        /// 音频观察是**无副作用**的：记账由帧发布侧抽干一次（<see cref="SimulationFrameCollector.CollectAudio"/>），
        /// 两个消费者读**同一帧**各拿各的同一份值——先读的那个不会把后读的那个的账清掉。
        /// </summary>
        [Test]
        public void AudioObservation_IsNonDestructiveForMultipleConsumers()
        {
            var world = new ParticleWorld(8, 8, 2026u);
            world.RandomTicksPerStep = 0;

            // 悬空一整行沙：下一个运动帧整行各落一格 ⇒ 8 格运动。
            for (int x = 0; x < 8; x++)
            {
                world.Set(x, 5, ParticleIds.Sand);
            }

            world.StepMotion();
            Assert.AreEqual(8L, world.SolidMotion.MovingCount, "前提：整行沙确实在动");

            var frames = new FramePump();
            SimulationFrame frame = frames.Publish(world);

            Assert.AreEqual(8L, frame.Audio.SolidMovingCount, "帧里带着本轮的固体运动格数");
            Assert.AreEqual(0L, world.SolidMotion.MovingCount, "抽干发生在发布侧：发布之后 world 的记账已清零");

            // 两个消费者读同一帧：各拿各的同一份值，互不影响。
            long first = frame.Audio.SolidMovingCount;
            long second = frame.Audio.SolidMovingCount;
            Assert.AreEqual(first, second, "同一帧读两次必须一模一样（帧不可变、读不清账）");
            Assert.AreEqual(8L, second, "后读的那个也拿得到完整读数");

            // 发布侧再发一帧（这一帧没有新的运动）：两个消费者都读到 0，没有残留的「补发」。
            SimulationFrame idle = frames.Publish(world);
            Assert.AreEqual(0L, idle.Audio.SolidMovingCount, "没有新运动的帧读到 0");
        }

        /// <summary>两个真实的落沙声驱动读**同一帧**：先读的那个不影响后读的那个（各自独立、值相同）。</summary>
        [Test]
        public void TwoSoundDrivers_ReadTheSameFrameIndependently()
        {
            ParticleRegistration.RegisterAll();

            var world = new ParticleWorld(8, 8, 2026u);
            for (int x = 0; x < 8; x++)
            {
                world.Set(x, 5, ParticleIds.Sand);
            }

            world.StepMotion();

            var frames = new FramePump();
            SimulationFrame frame = frames.Publish(world);

            var host = new GameObject("SimulationFrameTests.Sounds");
            AudioClip clip = AudioClip.Create("falling_sand", 4410, 1, 44100, false);
            try
            {
                SoundController sound = host.AddComponent<SoundController>();
                var soundSerialized = new SerializedObject(sound);
                SerializedProperty clips = soundSerialized.FindProperty("_clips");
                clips.arraySize = 1;
                clips.GetArrayElementAtIndex(0).objectReferenceValue = clip;
                soundSerialized.ApplyModifiedPropertiesWithoutUndo();

                // 每个驱动一个宿主对象（组件是 DisallowMultipleComponent），共享同一个声音控制器。
                FallingSandSoundDriver first = AddSandDriver("First", sound);
                FallingSandSoundDriver second = AddSandDriver("Second", sound);

                float firstLoudness = first.Pump(frame);
                float secondLoudness = second.Pump(frame);

                Assert.Greater(firstLoudness, 0f, "固体在落就该有声");
                Assert.AreEqual(firstLoudness, secondLoudness, 1e-6f, "两个消费者读同一帧得到同一个响度");
                Assert.AreEqual(8L, frame.Audio.SolidMovingCount, "读数本身不因被读过而改变");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(clip);
            }
        }

        /// <summary>
        /// 相同 seed + **相同命令日志** ⇒ 在同一个 tick 边界应用之后，两边的世界逐位一致：
        /// 帧里的像素缓冲、tick 与探针读数完全相同。这是「命令是权威输入序列」的确定性回归
        /// （命令应用不依赖驱动实例，也不依赖入队的时间点）。
        /// </summary>
        [Test]
        public void SameCommandLog_ProducesIdenticalFrames()
        {
            var otherObject = new GameObject("SimulationFrameTests.Driver2");
            var other = otherObject.AddComponent<ParticleSimulationDriver>();
            try
            {
                var otherSerialized = new SerializedObject(other);
                otherSerialized.FindProperty("_settings").objectReferenceValue = _settings;
                otherSerialized.ApplyModifiedPropertiesWithoutUndo();

                typeof(ParticleSimulationDriver)
                    .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(other, null);

                // 同一份命令日志（含一笔会触发相变 / 运动的投放），两边的应用顺序完全一致。
                var log = new[]
                {
                    SimulationCommand.PaintDisk(ProbeX, ProbeY, 2, ParticleIds.Sand, false, 0),
                    SimulationCommand.PaintDisk(ProbeX + 1, ProbeY + 1, 1, ParticleIds.Water, false, 0),
                    SimulationCommand.FillRect(0, 0, W, 1, ParticleIds.Stone),
                    SimulationCommand.ProbeCell(ProbeX, ProbeY - 1),
                };

                for (int i = 0; i < log.Length; i++)
                {
                    _driver.Submit(log[i]);
                    other.Submit(log[i]);
                }

                long mineBefore = _driver.CurrentFrame.Sequence;
                long theirsBefore = other.CurrentFrame.Sequence;

                SimulationFrame mine = AdvanceUntilNewFrame(mineBefore);
                SimulationFrame theirs = AdvanceFrameUntilNewFrame(other, theirsBefore);

                Assert.AreEqual(mine.Render.Tick, theirs.Render.Tick, "同一命令日志推进出同一个 tick");
                Assert.AreEqual(mine.World.Probe.TypeId, theirs.World.Probe.TypeId, "同一次探针读数一致");
                CollectionAssert.AreEqual(mine.Render.Pixels, theirs.Render.Pixels, "像素逐位一致");
            }
            finally
            {
                Object.DestroyImmediate(otherObject);
            }
        }

        /// <summary>在新建的宿主对象上挂一个接线好的落沙声驱动（同一个声音控制器可接多个驱动）。</summary>
        private static FallingSandSoundDriver AddSandDriver(string name, SoundController sound)
        {
            var host = new GameObject("SimulationFrameTests." + name);
            host.transform.SetParent(sound.transform, false);

            FallingSandSoundDriver driver = host.AddComponent<FallingSandSoundDriver>();
            var serialized = new SerializedObject(driver);
            serialized.FindProperty("_sound").objectReferenceValue = sound;
            serialized.FindProperty("_soundFile").stringValue = "falling_sand";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return driver;
        }
    }
}
