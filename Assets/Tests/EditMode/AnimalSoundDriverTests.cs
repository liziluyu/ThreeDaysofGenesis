using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 动物声音驱动（<see cref="AnimalSoundDriver"/>）的纯逻辑契约：按物种**存在性**间歇放一声闲聊声、
    /// 出生 / 死亡各一声且每物种每路最多一声，以及高速静音、随机流隔离与同 seed 的确定性。
    /// </summary>
    /// <remarks>
    /// <para>手法照抄 <c>LightningSoundDriverTests</c>：<see cref="GameObject"/> + <c>AddComponent</c>，
    /// 用 <see cref="SerializedObject"/> 注入私有字段（<c>_clips</c> / <c>_sound</c> / <c>_seed</c> /
    /// 三份 <c>CreatureVoice</c>），用 <see cref="AudioClip.Create"/> 造真 clip。
    /// 可观测量只有 <see cref="SoundController.OneShotPlayCount"/> 与 <see cref="AnimalSoundDriver.Pump"/>
    /// 的返回值——听感不是可断言的东西。</para>
    /// <para>随机流「有没有前进」用反射读私有 <c>_state</c>（它不是 <c>[SerializeField]</c>，
    /// <see cref="SerializedObject"/> 看不见）；<c>_rabbitTimer</c> 同理。</para>
    /// <para>高速静音走**生产路径**：接一个真的 <see cref="ParticleSimulationDriver"/> 并
    /// <see cref="ParticleSimulationDriver.EnterHighSpeed"/>（同 <c>SoundControllerTests</c> 的手法），
    /// 而不是把驱动里的判定改成可覆盖的虚方法——静音判据来自 <see cref="SoundController.HighSpeedSilenced"/>，
    /// 生产里怎么真就怎么测。</para>
    /// </remarks>
    public sealed class AnimalSoundDriverTests
    {
        private const int W = 24;
        private const int H = 24;

        /// <summary>固定帧步长（秒）。取 0.5 的整数分之一是为了让 <c>timer -= dt</c> 的浮点减法精确可推算。</summary>
        private const float Dt = 0.1f;

        /// <summary>驱动的随机流种子（与默认值一致，同 seed 出声时刻可复现）。</summary>
        private const uint Seed = 0x5EED2026u;

        private GameObject _object;
        private SoundController _sound;
        private AnimalSoundDriver _driver;
        private AudioClip _rabbit;
        private AudioClip _bird;
        private AudioClip _fish;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();

            _object = new GameObject("AnimalSoundDriverTests");
            _sound = _object.AddComponent<SoundController>();
            _driver = _object.AddComponent<AnimalSoundDriver>();

            _rabbit = AudioClip.Create("rabbit", 4410, 1, 44100, false);
            _bird = AudioClip.Create("bird", 4410, 1, 44100, false);
            _fish = AudioClip.Create("fish", 4410, 1, 44100, false);

            var soundSerialized = new SerializedObject(_sound);
            SerializedProperty clips = soundSerialized.FindProperty("_clips");
            clips.arraySize = 3;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _rabbit;
            clips.GetArrayElementAtIndex(1).objectReferenceValue = _bird;
            clips.GetArrayElementAtIndex(2).objectReferenceValue = _fish;
            soundSerialized.ApplyModifiedPropertiesWithoutUndo();

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_sound").objectReferenceValue = _sound;
            serialized.FindProperty("_seed").uintValue = Seed;
            SetVoice(serialized, "_rabbit", "rabbit", 6f, 14f, 0.5f);
            SetVoice(serialized, "_bird", "bird", 5f, 12f, 0.5f);
            SetVoice(serialized, "_fish", "fish", 8f, 18f, 0.45f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_object);
            Object.DestroyImmediate(_rabbit);
            Object.DestroyImmediate(_bird);
            Object.DestroyImmediate(_fish);
        }

        /// <summary>没有动物世界可观察时不响。</summary>
        [Test]
        public void Pump_ReturnsZeroWithoutAWorld()
        {
            Assert.AreEqual(0, _driver.Pump(null, Dt));
            Assert.AreEqual(0, _sound.OneShotPlayCount);
        }

        /// <summary>场上无动物：连推 300 帧零出声，且随机流一位都不前进（连骰子都不摇）。</summary>
        [Test]
        public void Pump_WithoutCreatures_NeverPlays()
        {
            CreatureWorld creatures = new CreatureWorld(1u);

            for (int i = 0; i < 300; i++)
            {
                Assert.AreEqual(0, _driver.Pump(creatures, Dt), "没有动物就不该出声");
            }

            Assert.AreEqual(0, _sound.OneShotPlayCount);
            Assert.AreEqual(Seed, StateOf(_driver), "没有动物就一次都不摇骰（状态仍等于种子）");
        }

        /// <summary>
        /// 有兔 ⇒ 闲聊恰落在摇出的间隔窗口内。种子固定 ⇒ 具体帧号可推算（此处 113 / 235 / 358，<c>dt = 0.1</c>）；
        /// 三声的间隔不完全相同 ⇒ 是「间歇」而不是「节拍器」。
        /// </summary>
        [Test]
        public void Pump_WithRabbit_PlaysOncePerRolledInterval()
        {
            CreatureWorld creatures = new CreatureWorld(11u);
            ParticleWorld world = BuildWorld(11u);
            creatures.Spawn(new RabbitCreature(), world, 8f, 8f);

            var frames = new List<int>();
            for (int i = 0; i < 400; i++)
            {
                if (_driver.Pump(creatures, Dt) > 0)
                {
                    frames.Add(i);
                }
            }

            CollectionAssert.AreEqual(new[] { 113, 235, 358 }, frames, "固定种子下出声帧号序列必须一致");
            Assert.AreEqual(3, _sound.OneShotPlayCount);

            for (int k = 1; k < frames.Count; k++)
            {
                float gap = (frames[k] - frames[k - 1]) * Dt;
                Assert.GreaterOrEqual(gap, 6f, "间隔不得短于 idleIntervalMin");
                Assert.LessOrEqual(gap, 14f, "间隔不得长于 idleIntervalMax");
            }

            Assert.AreNotEqual(
                frames[1] - frames[0],
                frames[2] - frames[1],
                "连续三声的间隔不完全相同 ⇒ 间歇而非节拍器");
        }

        /// <summary>
        /// 动物消失 ⇒ 计时解除武装：重新出现时不会立刻出声，要等一个**完整**间隔。
        /// 重现那一帧只会响出生事件声（那是事件，不是闲聊），随后 75 帧（滚出 7.43 秒）才轮到闲聊声。
        /// </summary>
        [Test]
        public void Pump_DisappearThenReappear_WaitsAFullInterval()
        {
            CreatureWorld creatures = new CreatureWorld(3u);
            ParticleWorld world = BuildWorld(3u);
            creatures.Spawn(new RabbitCreature(), world, 8f, 8f);

            int first = FirstSoundFrame(creatures, 400);
            Assert.AreEqual(113, first, "首次闲聊声在第一轮间隔到点时响");

            // 消失：Clear() 不发死亡事件（也顺带不打扰事件标志）。
            creatures.Clear();
            for (int i = 0; i < 30; i++)
            {
                Assert.AreEqual(0, _driver.Pump(creatures, Dt), "动物不存在就不该出声");
            }

            int baseline = _sound.OneShotPlayCount;

            // 重现那一帧：先响一声出生事件声。
            creatures.Spawn(new RabbitCreature(), world, 8f, 8f);
            Assert.AreEqual(1, _driver.Pump(creatures, Dt), "重现时先响出生事件声");
            int afterSpawn = _sound.OneShotPlayCount;
            Assert.AreEqual(baseline + 1, afterSpawn);

            // 接下来 59 帧（5.9 秒 < 下限 6 秒）不得响闲聊。
            for (int i = 0; i < 59; i++)
            {
                Assert.AreEqual(0, _driver.Pump(creatures, Dt), "重现后不得立刻响闲聊（要等一个完整间隔）");
            }

            // 继续直到闲聊声出现：从重现算起恰在第 75 帧（滚出的间隔 7.43 秒）。
            int extra = 0;
            while (_sound.OneShotPlayCount == afterSpawn && extra < 1000)
            {
                _driver.Pump(creatures, Dt);
                extra++;
            }

            Assert.AreEqual(16, extra, "第 60..75 帧之间才响闲聊（完整间隔）");
            Assert.AreEqual(afterSpawn + 1, _sound.OneShotPlayCount);
        }

        /// <summary>出生一只 ⇒ 下一 <see cref="AnimalSoundDriver.Pump"/> 一声；之后不再补发。</summary>
        [Test]
        public void Pump_SpawnEvent_PlaysOnceOnNextPump()
        {
            CreatureWorld creatures = new CreatureWorld(5u);
            ParticleWorld world = BuildWorld(5u);

            Assert.AreEqual(0, _driver.Pump(creatures, Dt), "先推一帧完成订阅");

            creatures.Spawn(new RabbitCreature(), world, 8f, 8f);
            Assert.AreEqual(1, _driver.Pump(creatures, Dt), "出生一只 ⇒ 下一帧一声");
            Assert.AreEqual(1, _sound.OneShotPlayCount);

            Assert.AreEqual(0, _driver.Pump(creatures, Dt), "账已清，不再补发");
            Assert.AreEqual(1, _sound.OneShotPlayCount);
        }

        /// <summary>同一帧死 5 只 ⇒ 折叠成一声（不是五声）。</summary>
        [Test]
        public void Pump_FiveDeathsInOneFrame_PlaysOnce()
        {
            CreatureWorld creatures = new CreatureWorld(5u);
            ParticleWorld world = BuildWorld(5u);

            Assert.AreEqual(0, _driver.Pump(creatures, Dt), "先推一帧完成订阅");

            for (int i = 0; i < 5; i++)
            {
                creatures.Spawn(new RabbitCreature(), world, 6f + i, 8f);
            }

            Assert.AreEqual(1, _driver.Pump(creatures, Dt), "五只同帧出生也只响一声（折叠）");
            int afterSpawn = _sound.OneShotPlayCount;

            IReadOnlyList<Creature> list = creatures.Creatures;
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Kill(CreatureDeathCause.Burned);
            }

            creatures.Step(world);

            Assert.AreEqual(1, _driver.Pump(creatures, Dt), "同帧死 5 只也只响一声");
            Assert.AreEqual(afterSpawn + 1, _sound.OneShotPlayCount);
        }

        /// <summary>兔与鸟同帧各死一只 ⇒ 两声（跨物种不折叠）。</summary>
        [Test]
        public void Pump_RabbitAndBirdDieInSameFrame_PlaysTwice()
        {
            CreatureWorld creatures = new CreatureWorld(5u);
            ParticleWorld world = BuildWorld(5u);

            Assert.AreEqual(0, _driver.Pump(creatures, Dt), "先推一帧完成订阅");

            creatures.Spawn(new RabbitCreature(), world, 6f, 8f);
            creatures.Spawn(new BirdCreature(), world, 10f, 10f);

            Assert.AreEqual(2, _driver.Pump(creatures, Dt), "两个物种同帧出生 ⇒ 两声");
            int afterSpawn = _sound.OneShotPlayCount;

            IReadOnlyList<Creature> list = creatures.Creatures;
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Kill(CreatureDeathCause.OldAge);
            }

            creatures.Step(world);

            Assert.AreEqual(2, _driver.Pump(creatures, Dt), "兔与鸟同帧各死一只 ⇒ 两声");
            Assert.AreEqual(afterSpawn + 2, _sound.OneShotPlayCount);
        }

        /// <summary>
        /// 高速静音（走生产路径：真的 <see cref="ParticleSimulationDriver"/> + <c>EnterHighSpeed</c>）：
        /// 静音期间零出声、随机流一位都不前进、倒计时也不推进；事件标志照旧保留，退出后补一声。
        /// </summary>
        [Test]
        public void Pump_HighSpeedSilenced_PlaysNothingAndFreezesTheStream()
        {
            var settings = ScriptableObject.CreateInstance<ParticleSimulationSettings>();
            var driverObject = new GameObject("AnimalSoundDriverTests.Driver");
            ParticleSimulationDriver simulation = driverObject.AddComponent<ParticleSimulationDriver>();

            try
            {
                // 小网格（4×4）省掉一次大世界的建世界开销；其余参数用类型默认值（同 SoundControllerTests）。
                var driverSerialized = new SerializedObject(simulation);
                driverSerialized.FindProperty("_settings").objectReferenceValue = settings;
                driverSerialized.ApplyModifiedPropertiesWithoutUndo();

                var settingsSerialized = new SerializedObject(settings);
                settingsSerialized.FindProperty("_width").intValue = 4;
                settingsSerialized.FindProperty("_height").intValue = 4;
                settingsSerialized.ApplyModifiedPropertiesWithoutUndo();

                // EditMode 里 AddComponent 不触发 Awake，直接反射调一次（世界 / 倍速控制器都在那里建）。
                typeof(ParticleSimulationDriver)
                    .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(simulation, null);

                var soundSerialized = new SerializedObject(_sound);
                soundSerialized.FindProperty("_driver").objectReferenceValue = simulation;
                soundSerialized.ApplyModifiedPropertiesWithoutUndo();

                CreatureWorld creatures = new CreatureWorld(4u);
                ParticleWorld world = BuildWorld(4u);
                creatures.Spawn(new RabbitCreature(), world, 8f, 8f);

                Assert.IsFalse(_sound.HighSpeedSilenced);
                Assert.AreEqual(0, _driver.Pump(creatures, Dt), "订阅 + 武装，本帧不出声");

                simulation.EnterHighSpeed();
                Assert.IsTrue(_sound.HighSpeedSilenced, "进入高速模式即静音");

                uint stateBefore = StateOf(_driver);
                float timerBefore = FloatField(_driver, "_rabbitTimer");
                int countBefore = _sound.OneShotPlayCount;

                // 静音期间又出生一只：事件标志照旧置位（驱动已订阅）。
                creatures.Spawn(new RabbitCreature(), world, 8f, 8f);

                for (int i = 0; i < 100; i++)
                {
                    Assert.AreEqual(0, _driver.Pump(creatures, Dt), "高速静音期间不出声");
                }

                Assert.AreEqual(countBefore, _sound.OneShotPlayCount, "静音期间不计数");
                Assert.AreEqual(stateBefore, StateOf(_driver), "静音期间随机流一位都不前进");
                Assert.AreEqual(timerBefore, FloatField(_driver, "_rabbitTimer"), "静音期间不倒计时");

                simulation.HighSpeed.Exit(simulation.Speed);
                Assert.IsFalse(_sound.HighSpeedSilenced, "确认退出后不再静音");

                // 退出后按保留的标志补一声出生声（计时从原进度继续，与本帧无关）。
                Assert.AreEqual(1, _driver.Pump(creatures, Dt), "退出后补上静音期间积压的出生声");
                Assert.AreEqual(countBefore + 1, _sound.OneShotPlayCount);
            }
            finally
            {
                Object.DestroyImmediate(driverObject);
                Object.DestroyImmediate(settings);
            }
        }

        /// <summary>不消耗模拟随机流：<see cref="AnimalSoundDriver.Pump"/> 前后，粒子世界 / 动物世界的流
        /// 与从未被驱动过的同 seed 世界逐位一致。</summary>
        [Test]
        public void Pump_DoesNotConsumeWorldRandomStreams()
        {
            CreatureWorld creatures = new CreatureWorld(42u);
            CreatureWorld reference = new CreatureWorld(42u);
            ParticleWorld world = BuildWorld(7u);
            ParticleWorld referenceWorld = BuildWorld(7u);

            creatures.Spawn(new RabbitCreature(), world, 8f, 8f);
            creatures.Spawn(new BirdCreature(), world, 10f, 10f);

            for (int i = 0; i < 200; i++)
            {
                _driver.Pump(creatures, Dt);
            }

            for (int i = 0; i < 8; i++)
            {
                Assert.AreEqual(reference.NextUInt(), creatures.NextUInt(), "动物世界的随机流不得被驱动消耗");
                Assert.AreEqual(referenceWorld.NextUInt(), world.NextUInt(), "粒子世界的随机流不得被驱动消耗");
            }
        }

        /// <summary>同 seed 两次运行的出声帧号序列完全一致。</summary>
        [Test]
        public void Pump_SameSeed_ReplaysIdenticalSoundFrames()
        {
            CollectionAssert.AreEqual(RunSoundFrames(), RunSoundFrames(), "同 seed 的出声时刻必须逐帧一致");
        }

        /// <summary>跑一遍 500 帧，返回「<see cref="AnimalSoundDriver.Pump"/> 出声」的帧号序列（供确定性比对）。</summary>
        private static List<int> RunSoundFrames()
        {
            GameObject go = new GameObject("AnimalSoundDriverTests.Determinism");
            AudioClip rabbit = AudioClip.Create("rabbit", 4410, 1, 44100, false);

            try
            {
                SoundController sound = go.AddComponent<SoundController>();
                AnimalSoundDriver driver = go.AddComponent<AnimalSoundDriver>();

                var soundSerialized = new SerializedObject(sound);
                SerializedProperty clips = soundSerialized.FindProperty("_clips");
                clips.arraySize = 1;
                clips.GetArrayElementAtIndex(0).objectReferenceValue = rabbit;
                soundSerialized.ApplyModifiedPropertiesWithoutUndo();

                var serialized = new SerializedObject(driver);
                serialized.FindProperty("_sound").objectReferenceValue = sound;
                serialized.FindProperty("_seed").uintValue = Seed;
                SetVoice(serialized, "_rabbit", "rabbit", 6f, 14f, 0.5f);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                ParticleWorld world = BuildWorld(21u);
                CreatureWorld creatures = new CreatureWorld(21u);
                creatures.Spawn(new RabbitCreature(), world, 8f, 8f);

                var frames = new List<int>();
                for (int i = 0; i < 500; i++)
                {
                    if (i == 120)
                    {
                        creatures.Spawn(new RabbitCreature(), world, 9f, 9f); // 混入一次出生事件
                    }

                    if (driver.Pump(creatures, Dt) > 0)
                    {
                        frames.Add(i);
                    }
                }

                return frames;
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rabbit);
            }
        }

        /// <summary>推进到第一次出声，返回其帧号（找不到返回 -1）。</summary>
        private int FirstSoundFrame(CreatureWorld creatures, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                if (_driver.Pump(creatures, Dt) > 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static ParticleWorld BuildWorld(uint seed)
        {
            return new ParticleWorld(W, H, seed);
        }

        /// <summary>用 <see cref="SerializedObject"/> 写一份 <see cref="AnimalSoundDriver.CreatureVoice"/> 字段。</summary>
        private static void SetVoice(SerializedObject serialized, string root, string idle, float min, float max, float volume)
        {
            serialized.FindProperty(root + ".idleClip").stringValue = idle;
            serialized.FindProperty(root + ".idleIntervalMin").floatValue = min;
            serialized.FindProperty(root + ".idleIntervalMax").floatValue = max;
            serialized.FindProperty(root + ".idleVolume").floatValue = volume;
            serialized.FindProperty(root + ".deathClip").stringValue = string.Empty;
            serialized.FindProperty(root + ".spawnClip").stringValue = string.Empty;
        }

        /// <summary>反射读私有 <c>_state</c>（随机流状态；不是 <c>[SerializeField]</c>，<see cref="SerializedObject"/> 看不见）。</summary>
        private static uint StateOf(AnimalSoundDriver driver)
        {
            FieldInfo field = typeof(AnimalSoundDriver).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
            return (uint)field.GetValue(driver);
        }

        /// <summary>反射读私有 float 字段（如 <c>_rabbitTimer</c>）。</summary>
        private static float FloatField(AnimalSoundDriver driver, string name)
        {
            FieldInfo field = typeof(AnimalSoundDriver).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return (float)field.GetValue(driver);
        }
    }
}
