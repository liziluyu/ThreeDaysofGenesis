using NUnit.Framework;
using TapTap2026.Game;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 声音控制器（<see cref="SoundController"/>）的纯逻辑契约：按文件名查音频、时间步长钳制、响度平滑一步、
    /// 比例→响度映射与 EMA 数学，以及「每条音频一条独立循环声床 + 一个一次性音效源」这套声床账。
    /// </summary>
    /// <remarks>
    /// 音频表用 <see cref="AudioClip.Create"/> 现建现销的零数据片段（只关心资产名与时长），经
    /// <see cref="SerializedObject"/> 塞进 <c>_clips</c>；测试不依赖当前打开的场景，也不进播放模式。
    /// 真正的回放（循环声床是否在音频线程上出声）无法在 EditMode 里断言，这里只覆盖与回放无关的确定性逻辑；
    /// 需要 <see cref="MonoBehaviour.Update"/> 的那一条（音量逐帧朝目标推进）由
    /// <see cref="SoundController.NextVolume"/> 的契约覆盖。
    /// </remarks>
    public sealed class SoundControllerTests
    {
        private const int SampleRate = 44100;

        private GameObject _controllerObject;
        private SoundController _controller;
        private AudioClip _water;
        private AudioClip _rain;

        [SetUp]
        public void SetUp()
        {
            _controllerObject = new GameObject("SoundControllerTests.Controller");
            _controller = _controllerObject.AddComponent<SoundController>();

            // 2 秒 / 1 秒的零数据片段：只取资产名。
            _water = AudioClip.Create("water", SampleRate * 2, 1, SampleRate, false);
            _rain = AudioClip.Create("rain", SampleRate, 1, SampleRate, false);

            var serialized = new SerializedObject(_controller);
            SerializedProperty clips = serialized.FindProperty("_clips");
            clips.arraySize = 2;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _water;
            clips.GetArrayElementAtIndex(1).objectReferenceValue = _rain;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_controllerObject);
            Object.DestroyImmediate(_water);
            Object.DestroyImmediate(_rain);
        }

        /// <summary>查音频：资产名、带扩展名的文件名、带目录的路径都指向同一个片段，且不区分大小写。</summary>
        [Test]
        public void FindClip_AcceptsNameExtensionPathAndAnyCase()
        {
            Assert.AreSame(_water, _controller.FindClip("water"), "资产名");
            Assert.AreSame(_water, _controller.FindClip("water.mp3"), "带扩展名");
            Assert.AreSame(_water, _controller.FindClip("Assets/Sounds/water.mp3"), "带目录的路径");
            Assert.AreSame(_water, _controller.FindClip(@"C:\proj\Assets\Sounds\water.mp3"), "Windows 反斜杠路径");
            Assert.AreSame(_water, _controller.FindClip("WATER"), "不区分大小写");
            Assert.AreSame(_rain, _controller.FindClip("rain.mp3"), "查另一条");
        }

        /// <summary>只在最后一段文件名上做完整匹配：前缀相同的别的名字、查不到的名字、空名字都返回 null。</summary>
        [Test]
        public void FindClip_RejectsNonMatches()
        {
            Assert.IsNull(_controller.FindClip("water2"), "前缀相同的别的名字不算命中");
            Assert.IsNull(_controller.FindClip("Assets/Sounds/wat"), "目录段不参与匹配");
            Assert.IsNull(_controller.FindClip("fire"), "表里没有");
            Assert.IsNull(_controller.FindClip(""), "空字符串");
            Assert.IsNull(_controller.FindClip(null), "null");
        }

        /// <summary>时间步长钳制：正常值原样、巨大值 / 负数 / NaN 一律折算成有限的一步。</summary>
        [Test]
        public void ClampDeltaTime_BoundsHitchesAndJunk()
        {
            Assert.AreEqual(0.02f, SoundController.ClampDeltaTime(0.02f), 1e-6f);
            Assert.AreEqual(SoundController.MaxDeltaTime, SoundController.ClampDeltaTime(10f), 1e-6f,
                "编辑器卡顿 / 断点后的秒级 dt 被钳到上界");
            Assert.AreEqual(0f, SoundController.ClampDeltaTime(-1f), 1e-6f, "负数不推进");
            Assert.AreEqual(0f, SoundController.ClampDeltaTime(float.NaN), 1e-6f, "NaN 不推进");
            Assert.AreEqual(0f, SoundController.ClampDeltaTime(0f), 1e-6f);
        }

        /// <summary>响度平滑一步：按「每秒推进量 × dt」朝目标走，不过冲；速率 0 = 关掉平滑（立即对齐）。</summary>
        [Test]
        public void NextVolume_ApproachesTheTargetAtTheConfiguredRate()
        {
            Assert.AreEqual(0.03f, SoundController.NextVolume(0f, 1f, 3f, 0.01f), 1e-6f);
            Assert.AreEqual(0.5f, SoundController.NextVolume(0.2f, 1f, 3f, 0.1f), 1e-6f);
            Assert.AreEqual(1f, SoundController.NextVolume(0.9f, 1f, 3f, 1f), 1e-6f, "到位即停，不过冲");
            Assert.AreEqual(1f, SoundController.NextVolume(0f, 1f, 0f, 0.01f), 1e-6f, "速率 0 = 立即对齐");
        }

        /// <summary>异常 dt 不得让音量在一步里跳满量程：推进量恒 ≤ 速率 × 钳制上界；NaN 冻结。</summary>
        [Test]
        public void NextVolume_IsBoundedByTheDeltaTimeCap()
        {
            float step = SoundController.NextVolume(0f, 1f, 3f, 600f);
            Assert.AreEqual(3f * SoundController.MaxDeltaTime, step, 1e-6f, "600 秒的 dt 也只能推进一个上界");

            Assert.AreEqual(0.5f, SoundController.NextVolume(0.5f, 0f, 3f, float.NaN), 1e-6f, "NaN dt 冻结在当前音量");
        }

        /// <summary>映射：死区边界 0、满量程比例 1、区间内线性、区间外封顶；区间退化（full ≤ min）恒满响且不除零。</summary>
        [Test]
        public void MapLoudness_MapsTheBandAndDegeneratesToFull()
        {
            Assert.AreEqual(0f, SoundController.MapLoudness(0.1f, 0.15f, 0.6f), 1e-6f, "死区以下");
            Assert.AreEqual(0f, SoundController.MapLoudness(0.15f, 0.15f, 0.6f), 1e-6f, "死区边界响度 0");
            Assert.AreEqual(0.5f, SoundController.MapLoudness(0.375f, 0.15f, 0.6f), 1e-6f, "区间中点正好半响");
            Assert.AreEqual(1f, SoundController.MapLoudness(0.6f, 0.15f, 0.6f), 1e-6f, "满量程比例");
            Assert.AreEqual(1f, SoundController.MapLoudness(0.9f, 0.15f, 0.6f), 1e-6f, "封顶");

            Assert.AreEqual(1f, SoundController.MapLoudness(0.5f, 0.5f, 0.5f), 1e-6f, "区间退化 = 恒满响");
            Assert.AreEqual(1f, SoundController.MapLoudness(0.9f, 0.5f, 0.2f), 1e-6f, "满量程比例低于死区也不除零");
        }

        /// <summary>EMA：朝目标推进且不越过、反复推进收敛；平滑时长 0 = 不平滑；异常 dt 只按钳制上界推进。</summary>
        [Test]
        public void Ema_SmoothsTowardTheTargetAndIsBoundedByTheDeltaTimeCap()
        {
            const float frame = 0.016f;

            float first = SoundController.Ema(0f, 1f, 0.25f, frame);
            Assert.Greater(first, 0f, "朝目标推进");
            Assert.Less(first, 1f, "单帧只推进很小的一步（平滑的目的就是别一步到位）");

            float bounded = SoundController.Ema(0f, 1f, 0.25f, 600f);
            Assert.LessOrEqual(bounded, 0.5f, "秒级 dt 被钳到上界：一步内最多走一半");

            Assert.AreEqual(1f, SoundController.Ema(0f, 1f, 0f, frame), 1e-6f, "平滑时长 0 = 不平滑");
            Assert.AreEqual(0.4f, SoundController.Ema(0.4f, 1f, 0.25f, float.NaN), 1e-6f, "NaN dt 冻结");
            Assert.AreEqual(0.4f, SoundController.Ema(0.4f, 1f, 0.25f, 0f), 1e-6f, "零 dt 冻结");

            float current = 0f;
            for (int i = 0; i < 200; i++)
            {
                current = SoundController.Ema(current, 1f, 0.25f, frame);
            }

            Assert.AreEqual(1f, current, 1e-3f, "持续朝目标收敛");
        }

        /// <summary>每条音频一条独立声床：推一条的目标不动另一条，目标钳在 [0, 1]，且目标不等于当前音量。</summary>
        [Test]
        public void SetAmbience_WritesClampedTargetPerBed()
        {
            _controller.SetAmbience("water", 0.4f);
            _controller.SetAmbience("rain", 1.7f);

            Assert.AreEqual(0.4f, _controller.TargetLoudness("water"), 1e-6f);
            Assert.AreEqual(1f, _controller.TargetLoudness("rain"), 1e-6f, "超出 [0, 1] 被钳制");
            Assert.AreEqual(0f, _controller.CurrentLoudness("water"), 1e-6f, "目标不等于当前音量：实际音量留给平滑推进");

            _controller.SetAmbience("water", -0.5f);
            Assert.AreEqual(0f, _controller.TargetLoudness("water"), 1e-6f, "负值被钳到 0");
            Assert.AreEqual(1f, _controller.TargetLoudness("rain"), 1e-6f, "推 water 不影响 rain 的目标");
        }

        /// <summary><c>StopAmbience(file)</c> 只把点名那条声床的目标归零，别的声床纹丝不动。</summary>
        [Test]
        public void StopAmbience_OnlySilencesTheNamedBed()
        {
            _controller.SetAmbience("water", 0.6f);
            _controller.SetAmbience("rain", 0.3f);

            _controller.StopAmbience("water");

            Assert.AreEqual(0f, _controller.TargetLoudness("water"), 1e-6f);
            Assert.AreEqual(0.3f, _controller.TargetLoudness("rain"), 1e-6f, "别的声床不受影响");

            _controller.StopAmbience("fire");
            Assert.AreEqual(0.3f, _controller.TargetLoudness("rain"), 1e-6f, "未知音频的 Stop 是无操作");
        }

        /// <summary>一次性音效：不建立 / 不改动任何声床目标与播放状态，每次成功起播计数恰好 +1。</summary>
        [Test]
        public void PlayOneShot_DoesNotTouchBedsAndCountsEachCall()
        {
            _controller.SetAmbience("water", 0.6f);

            _controller.PlayOneShot("rain", 0.7f);
            _controller.PlayOneShot("rain", 2f);
            _controller.PlayOneShot("rain", -1f);

            Assert.AreEqual(3, _controller.OneShotPlayCount, "每次成功起播 +1（响度不改变次数）");
            Assert.AreEqual(0f, _controller.TargetLoudness("rain"), 1e-6f, "一次性音效不建立声床目标");
            Assert.AreEqual(0f, _controller.CurrentLoudness("rain"), 1e-6f, "一次性音效不占声床音量");
            Assert.IsFalse(_controller.IsPlaying("rain"), "一次性音效走独立声源，不占用循环声床");
            Assert.AreEqual(0.6f, _controller.TargetLoudness("water"), 1e-6f, "声床目标不受一次性音效影响");
        }

        /// <summary>查不到音频：两个入口都保持现状、不抛——已建立的声床目标与一次性计数都不被破坏。</summary>
        [Test]
        public void UnknownAudio_IsIgnoredWithoutThrowing()
        {
            _controller.SetAmbience("water", 0.5f);
            _controller.PlayOneShot("water", 0.9f);

            _controller.SetAmbience("fire", 0.9f);
            _controller.PlayOneShot("fire", 0.9f);

            Assert.AreEqual(0.5f, _controller.TargetLoudness("water"), 1e-6f, "查不到的名字不改动已建立的声床");
            Assert.AreEqual(1, _controller.OneShotPlayCount, "查不到的名字不计入一次性音效次数");
        }

        /// <summary>查询接口对「未知音频 / 从未推过的声床」一律给中立值 0 / false，不抛、不建源。</summary>
        [Test]
        public void Queries_ReportZeroForUnknownAndUnsetAudio()
        {
            Assert.AreEqual(0f, _controller.TargetLoudness("fire"), 1e-6f);
            Assert.AreEqual(0f, _controller.CurrentLoudness("fire"), 1e-6f);
            Assert.IsFalse(_controller.IsPlaying("fire"));

            Assert.AreEqual(0f, _controller.TargetLoudness("rain"), 1e-6f, "已知但从未推过的音频同样没有声床");
            Assert.IsFalse(_controller.IsPlaying("rain"));
            Assert.AreEqual(0f, _controller.TargetLoudness(null), 1e-6f, "空名字");
        }
    }
}
