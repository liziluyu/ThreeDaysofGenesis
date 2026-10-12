using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 闪电一次性音效（<see cref="LightningSoundDriver"/>）的纯逻辑契约：一次起爆恰响一声、
    /// 一帧最多一声、没响过就不再响，以及关闭期间也清账。
    /// </summary>
    /// <remarks>
    /// <para>起爆夹具与 <see cref="LightningParticleTests"/> 一致：全大气 + 石头地板、热输运全关，
    /// 给半径 3 圆盘灌足湿度，再反复调大气的随机刻直到真的起爆。全部走生产注册路径
    /// （<see cref="ParticleRegistration.RegisterAll"/>），因为要用 <see cref="ParticleIds.Lightning"/>。</para>
    /// <para>可观测量只有 <see cref="SoundController.OneShotPlayCount"/> 与 <c>Pump</c> 的返回值——
    /// 「一帧一声」是可以用计数断言的硬语义，而听感不是。</para>
    /// </remarks>
    public sealed class LightningSoundDriverTests
    {
        private const int W = 24;
        private const int H = 24;

        /// <summary>尝试次数：2% 门在 2000 次内一次不中的概率 ≈ 2.8e-18（同 <see cref="LightningParticleTests"/>）。</summary>
        private const int MaxAttempts = 2000;

        private GameObject _object;
        private SoundController _sound;
        private LightningSoundDriver _driver;
        private AudioClip _lightning;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();

            _object = new GameObject("LightningSoundDriverTests");
            _sound = _object.AddComponent<SoundController>();
            _driver = _object.AddComponent<LightningSoundDriver>();

            _lightning = AudioClip.Create("lightning", 44100, 1, 44100, false);

            var soundSerialized = new SerializedObject(_sound);
            SerializedProperty clips = soundSerialized.FindProperty("_clips");
            clips.arraySize = 1;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _lightning;
            soundSerialized.ApplyModifiedPropertiesWithoutUndo();

            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_sound").objectReferenceValue = _sound;
            serialized.FindProperty("_soundFile").stringValue = "lightning";
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_object);
            Object.DestroyImmediate(_lightning);
        }

        /// <summary>没有世界可读时直接不响。</summary>
        [Test]
        public void Pump_ReturnsFalseWithoutAWorld()
        {
            Assert.IsFalse(_driver.Pump(null));
            Assert.AreEqual(0, _sound.OneShotPlayCount);
        }

        /// <summary>无起爆就没有一次性音效：干燥世界连推多帧，起播计数纹丝不动。</summary>
        [Test]
        public void Pump_DoesNotPlayWithoutAStrike()
        {
            ParticleWorld dry = BuildWorld(11u); // 无湿度：起爆门永远过不了

            for (int i = 0; i < 200; i++)
            {
                Assert.IsFalse(_driver.Pump(dry), "没有起爆就不该响");
            }

            Assert.AreEqual(0, _sound.OneShotPlayCount);
        }

        /// <summary>一次起爆恰响一声：整条雷电主干是**几十格**（格数 ≠ 次数），一帧内只许 +1，
        /// 之后没有新起爆就不再响（不重复、不取模、不循环）。</summary>
        [Test]
        public void Pump_PlaysExactlyOncePerStrike()
        {
            ParticleWorld world = BuildWorld(7u);
            Saturate(world, 12, 20, 0.12f);

            Assert.IsTrue(StrikeOnce(world, 12, 20), "饱和世界里必须起爆");
            Assert.Greater(CountType(world, ParticleIds.Lightning), 1, "一条完整闪电是几十格，绝不是 1 格");

            Assert.IsTrue(_driver.Pump(world), "起爆那一帧要响");
            Assert.AreEqual(1, _sound.OneShotPlayCount, "整条多格雷电只响一次");

            for (int i = 0; i < 10; i++)
            {
                Assert.IsFalse(_driver.Pump(world), "没有新起爆就不再响");
            }

            Assert.AreEqual(1, _sound.OneShotPlayCount, "一次起爆只累计一次起播");
        }

        /// <summary>同一帧内两次起爆仍只响一声：判据是 <c>created &gt; 0</c>，不是把格数当次数累加。</summary>
        [Test]
        public void Pump_PlaysAtMostOncePerFrameForTwoStrikes()
        {
            ParticleWorld world = BuildWorld(7u);
            Saturate(world, 12, 20, 0.12f);
            Saturate(world, 6, 20, 0.12f);

            Assert.IsTrue(StrikeOnce(world, 12, 20), "第一处必须起爆");
            Assert.IsTrue(StrikeOnce(world, 6, 20), "第二处必须起爆");

            Assert.IsTrue(_driver.Pump(world), "这一帧有新雷电 → 响");
            Assert.AreEqual(1, _sound.OneShotPlayCount, "两处起爆同帧也只响一声");

            Assert.IsFalse(_driver.Pump(world), "账已清，下一帧不再响（不补发）");
            Assert.AreEqual(1, _sound.OneShotPlayCount);
        }

        /// <summary>关闭期间也照样清账：重新打开不会补发关闭期间劈下的那几道闪电。</summary>
        [Test]
        public void Pump_DrainsTheMonitorWhileDisabled()
        {
            var serialized = new SerializedObject(_driver);
            serialized.FindProperty("_enabled").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ParticleWorld world = BuildWorld(7u);
            Saturate(world, 12, 20, 0.12f);
            Assert.IsTrue(StrikeOnce(world, 12, 20), "必须起爆");

            Assert.IsFalse(_driver.Pump(world), "关闭时不响");
            Assert.AreEqual(0, _sound.OneShotPlayCount);

            serialized.FindProperty("_enabled").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.IsFalse(_driver.Pump(world), "关闭期间的起爆已被读掉，重新打开不补发");
            Assert.AreEqual(0, _sound.OneShotPlayCount);
        }

        /// <summary>24×24、全格等温（T = 100）、全格大气，外加 y = 0 一整行石头地板。
        /// 温度取 100 是为了关掉降水（云不自凝成水格），热输运全关使世界除雷电外完全静止。</summary>
        private static ParticleWorld BuildWorld(uint seed)
        {
            ParticleWorld world = new ParticleWorld(W, H, seed);
            world.RandomTicksPerStep = 0;
            world.Light.Enabled = false;
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                    world.Heat.SetHeat(x, y, 100f);
                }
            }

            for (int x = 0; x < W; x++)
            {
                world.Set(x, 0, ParticleIds.Stone);
                world.Heat.SetHeat(x, 0, 100f);
            }

            return world;
        }

        /// <summary>把以 (cx,cy) 为中心、半径 3 的圆盘（29 格）每格灌入 <paramref name="perCell"/> 湿度。</summary>
        private static void Saturate(ParticleWorld world, int cx, int cy, float perCell)
        {
            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (dx * dx + dy * dy <= 9)
                    {
                        world.Humidity.Set(cx + dx, cy + dy, perCell);
                    }
                }
            }
        }

        /// <summary>直接调大气（= 空气粒子）的随机刻委托——雷电起爆的**唯一**入口。</summary>
        private static void RandomTickAtmosphere(ParticleWorld world, int x, int y)
        {
            ParticleRegistry.Types[ParticleIds.Atmosphere].OnRandomTick(world, x, y);
        }

        /// <summary>反复触发随机刻直到真的起爆（返回是否起爆），把「概率门」与「音效」解耦。</summary>
        private static bool StrikeOnce(ParticleWorld world, int x, int y)
        {
            for (int i = 0; i < MaxAttempts; i++)
            {
                int before = CountType(world, ParticleIds.Lightning);
                RandomTickAtmosphere(world, x, y);
                if (CountType(world, ParticleIds.Lightning) > before)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int i = 0; i < world.CellCount; i++)
            {
                if (world.Cells[i].typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
