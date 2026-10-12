using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 水的流动声（<see cref="LiquidFlowSoundDriver"/>）的纯逻辑契约：死区 + 滞回门，
    /// 以及「读一帧世界 → 把目标响度推给声音控制器」这条连续声床管线。
    /// </summary>
    /// <remarks>
    /// 世界用隔离类型表（水 = id 1、石头 = id 2），不触碰全局注册表；声音控制器用现建现销的零数据片段，
    /// 经 <see cref="SerializedObject"/> 接线。驱动器的 <c>Pump(world, deltaTime)</c> 带显式 dt，
    /// 因此管线测试不需要帧循环、也不依赖 <see cref="Time.unscaledDeltaTime"/>。
    /// 比例→响度映射与 EMA 的数学契约已随 <see cref="SoundController.MapLoudness"/> /
    /// <see cref="SoundController.Ema"/> 迁到 <see cref="SoundControllerTests"/>，这里只覆盖管线接线。
    /// </remarks>
    public sealed class LiquidFlowSoundDriverTests
    {
        /// <summary>测试帧长（秒）；EMA 的平滑时长默认 0.25 s，故单帧只推进约 6%。</summary>
        private const float Frame = 0.016f;

        private const ushort WaterId = 1;
        private const ushort StoneId = 2;

        private GameObject _object;
        private SoundController _sound;
        private LiquidFlowSoundDriver _driver;
        private AudioClip _water;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("LiquidFlowSoundDriverTests");
            _sound = _object.AddComponent<SoundController>();
            _driver = _object.AddComponent<LiquidFlowSoundDriver>();

            _water = AudioClip.Create("water", 44100 * 2, 1, 44100, false);

            var soundSerialized = new SerializedObject(_sound);
            SerializedProperty clips = soundSerialized.FindProperty("_clips");
            clips.arraySize = 1;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _water;
            soundSerialized.ApplyModifiedPropertiesWithoutUndo();

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_sound").objectReferenceValue = _sound;
            serialized.FindProperty("_soundFile").stringValue = "water";
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_object);
            Object.DestroyImmediate(_water);
        }

        /// <summary>死区 + 滞回：达到「死区 + 滞回」才进入流动，掉到死区以下才退出，滞回带内维持原状。</summary>
        [Test]
        public void IsFlowing_KeepsStateInsideTheHysteresisBand()
        {
            Assert.IsTrue(LiquidFlowSoundDriver.IsFlowing(false, 0.18f, 0.18f, 0.15f), "达到进入阈值");
            Assert.IsTrue(LiquidFlowSoundDriver.IsFlowing(false, 0.30f, 0.18f, 0.15f), "远高于阈值");

            Assert.IsFalse(LiquidFlowSoundDriver.IsFlowing(false, 0.16f, 0.18f, 0.15f), "滞回带内、本来没流动 → 不进入");
            Assert.IsTrue(LiquidFlowSoundDriver.IsFlowing(true, 0.16f, 0.18f, 0.15f), "滞回带内、本来在流动 → 不退出");

            Assert.IsFalse(LiquidFlowSoundDriver.IsFlowing(true, 0.14f, 0.18f, 0.15f), "低于死区 → 退出");
            Assert.IsTrue(LiquidFlowSoundDriver.IsFlowing(false, 0.15f, 0.15f, 0.15f), "无滞回：恰好等于死区算进入");
            Assert.IsFalse(LiquidFlowSoundDriver.IsFlowing(true, 0.1499f, 0.15f, 0.15f), "无滞回：低于死区即退出");
        }

        /// <summary>管线：静止的水推 0（死区静音），流动的水把响度推给声音控制器；流动停止后 EMA 衰减回静音。</summary>
        [Test]
        public void Pump_PushesLoudnessOfMovingWaterAndSilencesCalmWater()
        {
            ParticleWorld calm = CreateWorld();
            FillFloor(calm);
            calm.Set(1, 1, WaterId); // 已经躺在石板上：没有运动

            Assert.AreEqual(0f, _driver.Pump(calm, Frame), 1e-6f, "静水在死区内");
            Assert.AreEqual(0f, _sound.TargetLoudness("water"), 1e-6f);

            ParticleWorld flowing = CreateWorld();
            FillFloor(flowing);
            flowing.Set(1, 4, WaterId); // 悬空：下一运动帧就下落
            flowing.Set(1, 5, WaterId);
            flowing.StepMotion();

            float loudness = _driver.Pump(flowing, Frame);
            Assert.Greater(loudness, 0f, "水在动就该有声");
            Assert.AreEqual(loudness, _sound.TargetLoudness("water"), 1e-6f, "目标响度推给了声音控制器");

            Settle(flowing);

            float previous = loudness;
            for (int i = 0; i < 120; i++)
            {
                float next = _driver.Pump(flowing, Frame);
                Assert.LessOrEqual(next, previous, "流动停止后单调衰减");
                previous = next;
            }

            Assert.Less(previous, 0.01f, "流动停止 120 帧后目标响度落到听不见");
            Assert.AreEqual(previous, _sound.TargetLoudness("water"), 1e-6f);
        }

        /// <summary>关掉即收声：即使世界在流动，目标也推 0，且清掉平滑 / 滞回的记忆。</summary>
        [Test]
        public void Pump_IsSilentWhenDisabled()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_enabled").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _sound.SetAmbience("water", 0.8f);

            ParticleWorld flowing = CreateWorld();
            FillFloor(flowing);
            flowing.Set(1, 4, WaterId);
            flowing.Set(1, 5, WaterId);
            flowing.StepMotion();

            Assert.AreEqual(0f, _driver.Pump(flowing, Frame), 1e-6f);
            Assert.AreEqual(0f, _sound.TargetLoudness("water"), 1e-6f, "关掉即把目标归零，交给控制器淡出");
        }

        /// <summary>关闭期间也必须每帧把监视器清账：否则重新打开时会把积压的流动当作「此刻在流」补发出去。</summary>
        [Test]
        public void Pump_DrainsTheMonitorWhileDisabled()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_enabled").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld world = CreateWorld();
            FillFloor(world);
            world.Set(1, 3, WaterId); // 三格水柱：接下来 3 个运动帧都在下落
            world.Set(1, 4, WaterId);
            world.Set(1, 5, WaterId);

            for (int i = 0; i < 3; i++)
            {
                world.StepMotion();
                _driver.Pump(world, Frame);

                Assert.AreEqual(0L, world.LiquidMotion.MovingCount, "关闭期间每帧读掉了本轮记账");
            }
        }

        /// <summary>3×6 的隔离世界（水 = 1、石头 = 2），不触碰全局注册表。</summary>
        private static ParticleWorld CreateWorld()
        {
            return new ParticleWorld(3, 6, 2026u, TestTables.Build(new WaterParticle(), new StoneParticle()));
        }

        private static void FillFloor(ParticleWorld world)
        {
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, StoneId);
            }
        }

        /// <summary>跑到「一整个运动帧没有任何液体换位」为止（水落定）。</summary>
        private static void Settle(ParticleWorld world)
        {
            for (int i = 0; i < 500; i++)
            {
                world.LiquidMotion.Reset();
                world.StepMotion();

                if (world.LiquidMotion.MovingRatio <= 0f)
                {
                    return;
                }
            }

            Assert.Fail("水面在 500 个运动帧内没有安定");
        }
    }
}
