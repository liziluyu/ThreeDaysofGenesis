using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 火焰持续声（<see cref="FireSoundDriver"/>）的纯逻辑契约：死区映射 + EMA 平滑，
    /// 以及「读一帧火格数 → 把目标响度推给声音控制器」这条连续声床管线。
    /// </summary>
    /// <remarks>
    /// <para>火格用生产注册表（<see cref="ParticleRegistration.RegisterAll"/>）里的
    /// <see cref="ParticleIds.Fire"/> 直接写进网格，再由 <see cref="FramePump"/> 按生产路径采成一帧——
    /// 本驱动只读帧快照里的火格存量，不推进世界，因此「写几格就是几格」。
    /// 声音控制器用现建现销的零数据片段，经 <see cref="SerializedObject"/> 接线。驱动器的
    /// <c>Pump(frame, deltaTime)</c> 带显式 dt，管线测试不需要帧循环、也不依赖
    /// <see cref="Time.unscaledDeltaTime"/>。</para>
    /// <para>映射与 EMA 的数学契约已随 <see cref="SoundController.MapLoudness"/> /
    /// <see cref="SoundController.Ema"/> 迁到 <see cref="SoundControllerTests"/>，这里只覆盖管线的接线与缩放。</para>
    /// </remarks>
    public sealed class FireSoundDriverTests
    {
        /// <summary>测试帧长（秒）；EMA 的平滑时长默认 0.25 s，故单帧只推进约 6%。</summary>
        private const float Frame = 0.016f;

        private const int W = 12;
        private const int H = 12;

        private GameObject _object;
        private SoundController _sound;
        private FireSoundDriver _driver;
        private AudioClip _fire;

        /// <summary>帧采集装置：把世界采成已发布的帧（本驱动只读帧，不读世界）。</summary>
        private readonly FramePump _frames = new FramePump();

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();

            _object = new GameObject("FireSoundDriverTests");
            _sound = _object.AddComponent<SoundController>();
            _driver = _object.AddComponent<FireSoundDriver>();

            _fire = AudioClip.Create("fire", 44100 * 2, 1, 44100, false);

            var soundSerialized = new SerializedObject(_sound);
            SerializedProperty clips = soundSerialized.FindProperty("_clips");
            clips.arraySize = 1;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _fire;
            soundSerialized.ApplyModifiedPropertiesWithoutUndo();

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_sound").objectReferenceValue = _sound;
            serialized.FindProperty("_soundFile").stringValue = "fire";
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_object);
            Object.DestroyImmediate(_fire);
        }

        /// <summary>没有火就彻底静音：全空气世界推 0，声床目标也是 0。</summary>
        [Test]
        public void Pump_SilencesWhenThereIsNoFire()
        {
            ParticleWorld world = CreateWorld();

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world), Frame), 1e-6f, "没有火格 → 目标响度 0");
            Assert.AreEqual(0f, _sound.TargetLoudness("fire"), 1e-6f);
        }

        /// <summary>满格火 → 响度单调逼近 <c>_volume</c>（默认 1），且每一帧都把目标推给了声音控制器。</summary>
        [Test]
        public void Pump_ApproachesFullVolumeAsFireGrows()
        {
            ParticleWorld world = CreateWorld();
            FillFire(world, 40); // == _fullVolumeCount

            float previous = 0f;
            float loudness;
            for (int i = 0; i < 120; i++)
            {
                loudness = _driver.Pump(_frames.Publish(world), Frame);
                Assert.GreaterOrEqual(loudness, previous, "EMA 单调逼近目标");
                Assert.LessOrEqual(loudness, 1f, "响度不超过 _volume");
                previous = loudness;
            }

            Assert.Greater(previous, 0.9f, "满格火应当逼近满响度");
            Assert.AreEqual(previous, _sound.TargetLoudness("fire"), 1e-6f, "目标响度推给了声音控制器");
        }

        /// <summary>响度量**取「火格数」**：火烧得越大越响（同一驱动器上，小火稳定后的响度明显低于大火）。</summary>
        [Test]
        public void Pump_IsLouderForMoreFireCells()
        {
            ParticleWorld small = CreateWorld();
            FillFire(small, 10);

            ParticleWorld large = CreateWorld();
            FillFire(large, 40);

            float smallLoudness = Settle(small);
            float largeLoudness = Settle(large);

            Assert.Greater(smallLoudness, 0f, "十格火也该有声");
            Assert.Greater(largeLoudness, smallLoudness, "火烧得越大越响（响度取绝对格数而非比例）");
        }

        /// <summary>死区：火格数 ≤ <c>_minFireCount</c> 时目标恒为 0，越过它才有声。</summary>
        [Test]
        public void Pump_RespectsTheMinFireCountDeadZone()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_minFireCount").floatValue = 8f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld atDeadZone = CreateWorld();
            FillFire(atDeadZone, 8); // 恰好等于死区

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(atDeadZone), Frame), 1e-6f, "死区边界上的火完全静音");
            Assert.AreEqual(0f, _sound.TargetLoudness("fire"), 1e-6f);

            ParticleWorld aboveDeadZone = CreateWorld();
            FillFire(aboveDeadZone, 9);

            Assert.Greater(_driver.Pump(_frames.Publish(aboveDeadZone), Frame), 0f, "越过死区即出声");
        }

        /// <summary><c>_volume</c> 是总音量缩放：目标响度 = 映射值 × 它，稳定值随之等比缩小。</summary>
        [Test]
        public void Pump_ScalesByVolume()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_volume").floatValue = 0.5f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld world = CreateWorld();
            FillFire(world, 40);

            float loudness = 0f;
            for (int i = 0; i < 120; i++)
            {
                loudness = _driver.Pump(_frames.Publish(world), Frame);
                Assert.LessOrEqual(loudness, 0.5f, "缩放后任何一帧都不得超过 _volume");
            }

            Assert.AreEqual(0.5f, loudness, 1e-2f, "满格火逼近 _volume 缩放后的响度");
        }

        /// <summary>单帧推进受 <see cref="SoundController.ClampDeltaTime"/> 上界约束：秒级 dt 也只走一步，
        /// 绝不在一帧里跳满量程；dt ≤ 0 则冻结。</summary>
        [Test]
        public void Pump_AdvancesAtMostOneClampedStepPerFrame()
        {
            ParticleWorld world = CreateWorld();
            FillFire(world, 40); // 目标 = 满响度

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world), 0f), 1e-6f, "dt = 0 时冻结在当前响度");

            float stepped = _driver.Pump(_frames.Publish(world), 1000f);
            float expected = SoundController.Ema(0f, 1f, 0.25f, SoundController.MaxDeltaTime);

            Assert.AreEqual(expected, stepped, 1e-6f, "秒级 dt 只按 ClampDeltaTime 的上界推进一步");
            Assert.Less(stepped, 1f, "绝不在一帧里跳满量程");
        }

        /// <summary>关掉或没有帧即收声：清掉平滑记忆、把目标归零（交给控制器淡出），并返回 0。</summary>
        [Test]
        public void Pump_IsSilentWhenDisabledOrFrameIsNull()
        {
            ParticleWorld world = CreateWorld();
            FillFire(world, 40);

            _sound.SetAmbience("fire", 0.8f);

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_enabled").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world), Frame), 1e-6f, "关掉即返回 0");
            Assert.AreEqual(0f, _sound.TargetLoudness("fire"), 1e-6f, "关掉即收声");

            serialized.FindProperty("_enabled").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _sound.SetAmbience("fire", 0.8f);

            Assert.AreEqual(0f, _driver.Pump(null, Frame), 1e-6f, "帧为 null 即返回 0");
            Assert.AreEqual(0f, _sound.TargetLoudness("fire"), 1e-6f, "帧为 null 即收声");
        }

        /// <summary>12×12 的生产表世界；本驱动只读帧快照，不推进世界。</summary>
        private static ParticleWorld CreateWorld()
        {
            return new ParticleWorld(W, H, 2026u);
        }

        /// <summary>把前 <paramref name="count"/> 格（行优先）写成火。</summary>
        private static void FillFire(ParticleWorld world, int count)
        {
            for (int i = 0; i < count; i++)
            {
                world.Set(i % W, i / W, ParticleIds.Fire);
            }
        }

        /// <summary>连续推若干帧（每帧按生产路径重采一次），返回稳定后的目标响度。</summary>
        private float Settle(ParticleWorld world)
        {
            float loudness = 0f;
            for (int i = 0; i < 200; i++)
            {
                loudness = _driver.Pump(_frames.Publish(world), Frame);
            }

            return loudness;
        }
    }
}
