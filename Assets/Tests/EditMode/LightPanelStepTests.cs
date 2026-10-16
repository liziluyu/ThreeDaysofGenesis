using System.Reflection;
using NUnit.Framework;
using TapTap2026.Game;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 光照面板的行为契约：<see cref="SandboxHud.AddLitSeconds"/> / <see cref="SandboxHud.AddDarkSeconds"/>
    /// 把**秒**换算成逻辑帧刻数（1 秒 = 当前逻辑帧速率刻，结果钳制到非负、不会因加法溢出翻负）；
    /// 强度滑块把值写进驱动器；白天 / 夜晚的秒读数按当前逻辑帧速率实时换算。
    /// </summary>
    /// <remarks>
    /// 现建现销，不依赖场景：HUD 对象、光照驱动器、模拟驱动器、设置资产、滑块与读数文本都在测试里搭出来，
    /// 私有序列化字段直接写（与 Inspector 同名同义）。EditMode 不跑 <c>Awake</c> / <c>Update</c>：
    /// 按秒步进只在调用时才懒解析模拟驱动器，因此无需反射触发初始化；需要挂运行时监听的用例自己反射调一次
    /// <c>Awake</c>，需要回填面板的用例直接反射调私有 <c>SyncLightValues</c>。
    /// </remarks>
    public sealed class LightPanelStepTests
    {
        private GameObject _hudObject;
        private SandboxHud _hud;
        private ParticleLightDriver _light;
        private ParticleSimulationSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _hudObject = new GameObject("HUD (test)");
            _hud = _hudObject.AddComponent<SandboxHud>();
            _light = _hudObject.AddComponent<ParticleLightDriver>();

            var hudSo = new SerializedObject(_hud);
            hudSo.FindProperty("_lightDriver").objectReferenceValue = _light;
            hudSo.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_hudObject);
            if (_settings != null)
            {
                Object.DestroyImmediate(_settings);
            }
        }

        /// <summary>亮相位按秒步进，减到负数钳到 0；没有模拟驱动器时 1 秒按兜底的 20 刻算。</summary>
        [Test]
        public void AddLitSeconds_StepsAndClamps()
        {
            _light.LitTicks = 60;

            _hud.AddLitSeconds(1);
            Assert.AreEqual(80, _light.LitTicks, "没有模拟驱动器时 1 秒 = 兜底速率 20 刻");

            _hud.AddLitSeconds(-1);
            Assert.AreEqual(60, _light.LitTicks, "−1 秒退回 20 刻");

            _hud.AddLitSeconds(5);
            Assert.AreEqual(160, _light.LitTicks, "+5 秒 = 加 100 刻");

            _light.LitTicks = 5;
            _hud.AddLitSeconds(-5);
            Assert.AreEqual(0, _light.LitTicks, "减到负数钳到 0");
        }

        /// <summary>暗相位按秒步进与亮相位同一语义（0 再减仍是 0，+5 秒 = 100 刻）。</summary>
        [Test]
        public void AddDarkSeconds_UsesSameSemantics()
        {
            _light.DarkTicks = 0;

            _hud.AddDarkSeconds(-5);
            Assert.AreEqual(0, _light.DarkTicks, "已是 0 再减仍是 0");

            _hud.AddDarkSeconds(5);
            Assert.AreEqual(100, _light.DarkTicks, "没有模拟驱动器时 +5 秒 = 加 100 刻");
        }

        /// <summary>步长跟随设置资产的逻辑帧速率（30 刻/秒），并对加法溢出设防。</summary>
        [Test]
        public void StepLengthFollowsLogicRate()
        {
            _settings = ScriptableObject.CreateInstance<ParticleSimulationSettings>();
            var settingsSo = new SerializedObject(_settings);
            settingsSo.FindProperty("_logicRate").floatValue = 30f;
            settingsSo.ApplyModifiedPropertiesWithoutUndo();

            ParticleSimulationDriver sim = _hudObject.AddComponent<ParticleSimulationDriver>();
            var simSo = new SerializedObject(sim);
            simSo.FindProperty("_settings").objectReferenceValue = _settings;
            simSo.ApplyModifiedPropertiesWithoutUndo();
            _light.LitTicks = 60;
            _hud.AddLitSeconds(1);
            Assert.AreEqual(90, _light.LitTicks, "1 秒 = 设置资产里的 30 刻（不能写死 20）");

            _light.LitTicks = int.MaxValue;
            _hud.AddLitSeconds(5);
            Assert.AreEqual(int.MaxValue, _light.LitTicks, "加到 int 上限也不许溢出翻负");
        }

        /// <summary>强度滑块写驱动器：拖一次滑块，值原样落进驱动器的强度字段（面板不解析文本、只写驱动器）。</summary>
        [Test]
        public void SliderWritesDriverIntensity()
        {
            Slider slider = Spawn<Slider>("Intensity (test)");
            slider.minValue = 0f;
            slider.maxValue = 30f;
            slider.wholeNumbers = true;

            var hudSo = new SerializedObject(_hud);
            hudSo.FindProperty("_lightIntensity").objectReferenceValue = slider;
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            // EditMode 不跑 Awake：运行时监听（onValueChanged → 写驱动器）要自己反射接上。
            Invoke(_hud, "Awake");

            slider.value = 21f;
            Assert.AreEqual(21f, _light.Intensity, 0.0001f, "滑块的值变化应当原样写进驱动器的强度");

            // 面板契约：范围与整数档由场景里的滑块给出，Awake 不该动它们。
            Assert.AreEqual(0f, slider.minValue, 0.0001f, "强度滑块的下限是 0");
            Assert.AreEqual(30f, slider.maxValue, 0.0001f, "强度滑块的上限是 30");
            Assert.IsTrue(slider.wholeNumbers, "强度滑块只走整数档");
        }

        /// <summary>白天 / 夜晚的秒读数按**当前逻辑帧速率**换算：同一刻数在兜底 20 刻/秒下是 30 秒，
        /// 速率改成 30 刻/秒后同一刻数变 20 秒（硬编码 20 会在这里红）。</summary>
        [Test]
        public void SecondsDisplayFollowsTicksAndLogicRate()
        {
            TMP_Text lit = Spawn<TextMeshProUGUI>("LitTicks (test)");
            TMP_Text dark = Spawn<TextMeshProUGUI>("DarkTicks (test)");

            var hudSo = new SerializedObject(_hud);
            hudSo.FindProperty("_lightLitTicks").objectReferenceValue = lit;
            hudSo.FindProperty("_lightDarkTicks").objectReferenceValue = dark;
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            _light.LitTicks = 600;
            _light.DarkTicks = 400;
            Invoke(_hud, "SyncLightValues");
            Assert.AreEqual("30秒", lit.text, "600 刻 ÷ 兜底速率 20 = 30 秒");
            Assert.AreEqual("20秒", dark.text, "400 刻 ÷ 兜底速率 20 = 20 秒");

            _settings = ScriptableObject.CreateInstance<ParticleSimulationSettings>();
            var settingsSo = new SerializedObject(_settings);
            settingsSo.FindProperty("_logicRate").floatValue = 30f;
            settingsSo.ApplyModifiedPropertiesWithoutUndo();

            ParticleSimulationDriver sim = _hudObject.AddComponent<ParticleSimulationDriver>();
            var simSo = new SerializedObject(sim);
            simSo.FindProperty("_settings").objectReferenceValue = _settings;
            simSo.ApplyModifiedPropertiesWithoutUndo();

            Invoke(_hud, "SyncLightValues");
            Assert.AreEqual("20秒", lit.text, "600 刻 ÷ 30 = 20 秒：读数得按当前速率实时换算");
            Assert.AreEqual("13秒", dark.text, "400 刻 ÷ 30 = 13.33 → 就近取整 13 秒");
        }

        /// <summary>建一个挂在 HUD 对象下的子对象，随 <see cref="TearDown"/> 一起销毁。</summary>
        private T Spawn<T>(string name) where T : Component
        {
            var target = new GameObject(name);
            target.transform.SetParent(_hudObject.transform, false);
            return target.AddComponent<T>();
        }

        /// <summary>反射调私有方法（EditMode 不跑 <c>Awake</c> / <c>Update</c>，监听与回填都得显式调一次）。</summary>
        private static void Invoke(object target, string methodName)
        {
            target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, null);
        }
    }
}
