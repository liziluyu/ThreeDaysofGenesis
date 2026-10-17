using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 落下的固体声（<see cref="FallingSandSoundDriver"/>）的纯逻辑契约：死区映射，
    /// 以及「读一帧固体运动格数 → 把目标响度推给声音控制器」这条连续声床管线。
    /// </summary>
    /// <remarks>
    /// <para>走生产注册路径（<see cref="ParticleRegistration.RegisterAll"/>），世界用
    /// <see cref="ParticleIds.Sand"/> 摆出**悬空的一整行固体**——正下方全是空气，因此一个
    /// <see cref="ParticleWorld.StepMotion"/> 就让整行各下落一格，运动格数恰好等于摆了几格
    /// （与扫描方向无关，因为每格的正下方都不受同帧其它格影响）。驱动不读比例，所以「摆几格 = 几格在动」
    /// 就是可直接断言的期望值。声音控制器用现建现销的零数据片段，经 <see cref="SerializedObject"/> 接线；
    /// <c>Pump(frame)</c> 不带 dt——本驱动没有 EMA；帧由 <see cref="FramePump"/> 按生产路径采出。</para>
    /// </remarks>
    public sealed class FallingSandSoundDriverTests
    {
        private const int W = 12;
        private const int H = 12;

        private GameObject _object;
        private SoundController _sound;
        private FallingSandSoundDriver _driver;
        private AudioClip _sand;

        /// <summary>帧采集装置：把世界采成已发布的帧（本驱动只读帧，不读世界）。</summary>
        private readonly FramePump _frames = new FramePump();

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();

            _object = new GameObject("FallingSandSoundDriverTests");
            _sound = _object.AddComponent<SoundController>();
            _driver = _object.AddComponent<FallingSandSoundDriver>();

            _sand = AudioClip.Create("falling_sand", 44100 * 2, 1, 44100, false);

            var soundSerialized = new SerializedObject(_sound);
            SerializedProperty clips = soundSerialized.FindProperty("_clips");
            clips.arraySize = 1;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _sand;
            soundSerialized.ApplyModifiedPropertiesWithoutUndo();

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_sound").objectReferenceValue = _sound;
            serialized.FindProperty("_soundFile").stringValue = "falling_sand";
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_object);
            Object.DestroyImmediate(_sand);
        }

        /// <summary>没有东西在动就彻底静音：填满底行的沙已经落定，运动格数 0、目标响度也是 0。</summary>
        [Test]
        public void Pump_SilencesWhenNothingMoves()
        {
            ParticleWorld world = CreateWorld();
            FillFloor(world);

            for (int i = 0; i < 3; i++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(0L, world.SolidMotion.MovingCount, "底行的沙不会移动");
            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world)), 1e-6f, "没有运动格 → 目标响度 0");
            Assert.AreEqual(0f, _sound.TargetLoudness("falling_sand"), 1e-6f);
        }

        /// <summary>管线：悬空的一整行沙下落一格 → 返回正响度，且等于声床目标响度。</summary>
        [Test]
        public void Pump_PushesLoudnessOfFallingSolid()
        {
            ParticleWorld world = CreateWorld();
            FloatSolidRow(world, 5, W, ParticleIds.Sand);
            world.StepMotion();

            float loudness = _driver.Pump(_frames.Publish(world));

            Assert.Greater(loudness, 0f, "固体在落就该有声");
            Assert.AreEqual(loudness, _sound.TargetLoudness("falling_sand"), 1e-6f, "目标响度推给了声音控制器");
        }

        /// <summary>响度量**取绝对运动格数**：同样下落，落得多的一侧严格更响（一整行比半行响）。</summary>
        [Test]
        public void Pump_IsLouderForMoreFallingCells()
        {
            ParticleWorld small = CreateWorld();
            FloatSolidRow(small, 5, W / 2, ParticleIds.Sand);
            small.StepMotion();

            ParticleWorld large = CreateWorld();
            FloatSolidRow(large, 5, W, ParticleIds.Sand);
            large.StepMotion();

            Assert.AreEqual((long)(W / 2), small.SolidMotion.MovingCount, "半行沙下落 = 半行运动格");
            Assert.AreEqual((long)W, large.SolidMotion.MovingCount, "整行沙下落 = 整行运动格");

            float smallLoudness = _driver.Pump(_frames.Publish(small));
            float largeLoudness = _driver.Pump(_frames.Publish(large));

            Assert.Greater(smallLoudness, 0f, "半行沙也该有声");
            Assert.Greater(largeLoudness, smallLoudness, "落得越多越响（响度取绝对运动格数而非比例）");
        }

        /// <summary>死区：运动格数 ≤ <c>_minMovingCount</c> 时目标恒为 0，越过它才有声。</summary>
        [Test]
        public void Pump_RespectsTheMinMovingCountDeadZone()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_minMovingCount").floatValue = 8f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld atDeadZone = CreateWorld();
            FloatSolidRow(atDeadZone, 5, 8, ParticleIds.Sand); // 恰好等于死区
            atDeadZone.StepMotion();

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(atDeadZone)), 1e-6f, "死区边界上的下落完全静音");
            Assert.AreEqual(0f, _sound.TargetLoudness("falling_sand"), 1e-6f);

            ParticleWorld aboveDeadZone = CreateWorld();
            FloatSolidRow(aboveDeadZone, 5, 9, ParticleIds.Sand);
            aboveDeadZone.StepMotion();

            Assert.Greater(_driver.Pump(_frames.Publish(aboveDeadZone)), 0f, "越过死区即出声");
        }

        /// <summary><c>_volume</c> 是总音量缩放：目标响度 = 映射值 × 它，满量程那一帧恰好被缩到它。</summary>
        [Test]
        public void Pump_ScalesByVolume()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_volume").floatValue = 0.5f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld world = CreateWorld();
            // 四行悬空的沙（行距 2，彼此不相干）：同帧各下落一格 = 48 格在动 > _fullVolumeCount(40) ⇒ 映射值满格。
            for (int y = 2; y <= 8; y += 2)
            {
                FloatSolidRow(world, y, W, ParticleIds.Sand);
            }

            world.StepMotion();

            Assert.GreaterOrEqual(world.SolidMotion.MovingCount, 40L, "铺够 48 格下落，映射值应当满格");

            float loudness = _driver.Pump(_frames.Publish(world));

            Assert.AreEqual(0.5f, loudness, 1e-2f, "满量程映射 × 0.5 = 0.5");
        }

        /// <summary>关掉或没有帧即收声：把目标归零（交给控制器淡出），并返回 0。</summary>
        [Test]
        public void Pump_IsSilentWhenDisabledOrFrameIsNull()
        {
            ParticleWorld world = CreateWorld();
            FloatSolidRow(world, 5, W, ParticleIds.Sand);
            world.StepMotion();

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_enabled").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _sound.SetAmbience("falling_sand", 0.8f);

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world)), 1e-6f, "关掉即返回 0");
            Assert.AreEqual(0f, _sound.TargetLoudness("falling_sand"), 1e-6f, "关掉即收声");

            _sound.SetAmbience("falling_sand", 0.8f);

            Assert.AreEqual(0f, _driver.Pump(null), 1e-6f, "帧为 null 即返回 0");
            Assert.AreEqual(0f, _sound.TargetLoudness("falling_sand"), 1e-6f, "帧为 null 即收声");
        }

        /// <summary>关闭期间也必须每帧清账：否则重新打开时会把积压的运动当作「此刻在落」补发出去。</summary>
        [Test]
        public void Pump_DrainsTheMonitorWhileDisabled()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_enabled").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld world = CreateWorld();
            FloatSolidRow(world, 5, W, ParticleIds.Sand); // 接下来若干运动帧整行都在下落

            for (int i = 0; i < 3; i++)
            {
                world.StepMotion();
                _driver.Pump(_frames.Publish(world));

                Assert.AreEqual(0L, world.SolidMotion.MovingCount, "关闭期间每帧读掉了本轮记账");
            }

            // 关着继续跑到那行沙彻底落定：若关闭期间从不清账，累计的运动格数会一直留着。
            for (int i = 0; i < 20; i++)
            {
                world.StepMotion();
                _driver.Pump(_frames.Publish(world));
            }

            serialized.FindProperty("_enabled").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _sound.SetAmbience("falling_sand", 0.8f);

            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world)), 1e-6f, "重新打开时不补发关闭期间积压的运动");
            Assert.AreEqual(0f, _sound.TargetLoudness("falling_sand"), 1e-6f);
        }

        /// <summary>口径锚点：带 <see cref="ParticleTags.Organic"/> 的固体不算落沙声——树木种子下落也静音。</summary>
        [Test]
        public void Pump_IgnoresOrganicSolidsSuchAsTreeSeeds()
        {
            ParticleWorld world = CreateWorld();
            FloatSolidRow(world, 5, W, ParticleIds.TreeSeed);
            world.StepMotion();

            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(0, 5).typeId, "树木种子确实下落了一格");
            Assert.AreEqual(ParticleIds.TreeSeed, world.GetCell(0, 4).typeId);
            Assert.AreEqual(0L, world.SolidMotion.MovingCount, "树木种子带 Organic，不计入落沙声的运动格数");
            Assert.AreEqual(0f, _driver.Pump(_frames.Publish(world)), 1e-6f, "有机固体下落不触发落沙声");
            Assert.AreEqual(0f, _sound.TargetLoudness("falling_sand"), 1e-6f);
        }

        /// <summary>12×12 的生产表世界。</summary>
        private static ParticleWorld CreateWorld()
        {
            return new ParticleWorld(W, H, 2026u);
        }

        /// <summary>填满底行：底下的越界即「墙」，这行沙不会掉、也不会斜滑。</summary>
        private static void FillFloor(ParticleWorld world)
        {
            for (int x = 0; x < W; x++)
            {
                world.Set(x, 0, ParticleIds.Sand);
            }
        }

        /// <summary>在第 <paramref name="y"/> 行从左侧铺 <paramref name="count"/> 格悬空的固体：
        /// 正下方全是空气，下一个运动帧整行各下落一格。</summary>
        private static void FloatSolidRow(ParticleWorld world, int y, int count, ushort typeId)
        {
            for (int x = 0; x < count; x++)
            {
                world.Set(x, y, typeId);
            }
        }
    }
}
