using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 投放面板（<see cref="ParticleSelectionPanel"/>）的公开契约：点击按显示名选中投放项、每帧回显
    /// （选中项压灰 / 未选中回原色）、名字对不上当前可投列表的槽与锁编辑时的整排变灰，以及「清除」槽
    /// 翻转 <see cref="ParticleEditor.EraseMode"/>。
    /// </summary>
    /// <remarks>
    /// 现建现销，**不依赖场景**：按钮与面板都在测试里搭出来，序列化字段直接写（与 Inspector 同名同义）。
    /// EditMode 不跑 <c>Awake</c>，因此显式反射调一次，走真实初始化路径（同 <c>ParticleSelectionTests</c> 的做法）。
    /// </remarks>
    public sealed class ParticleSelectionPanelTests
    {
        /// <summary>本次测试现建的全部对象（面板 / 按钮 / 编辑器），TearDown 里统一销毁。</summary>
        private readonly List<GameObject> _spawned = new List<GameObject>();

        private ParticleEditor _editor;
        private Button _sand;
        private Button _water;
        private Button _grass;
        private Button _fire;
        private Button _erase;
        private ParticleSelectionPanel _panel;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();

            _editor = Spawn("PanelTests.Editor").AddComponent<ParticleEditor>();
            Invoke(_editor, "Awake");

            _sand = MakeButton("Sand");
            _water = MakeButton("Water");
            _grass = MakeButton("Grass");
            _fire = MakeButton("Fire");
            _erase = MakeButton("Erase");

            // 草（GrassParticle）在默认白名单里就不可手投，因此这个槽的名字永远对不上可投列表；
            // 火（FireParticle）默认可投，拿它看阶段名单把类型剔掉 / 放回来的效果。
            _panel = MakePanel(_erase,
                (_sand, "SandParticle"),
                (_water, "WaterParticle"),
                (_grass, "GrassParticle"),
                (_fire, "FireParticle"),
                (null, "SandParticle"));
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }

        /// <summary>点按钮就按显示名选中那一项（与 <see cref="ParticleEditor.SelectByName"/> 同一规则）。</summary>
        [Test]
        public void Bindings_SelectOnClick()
        {
            _sand.onClick.Invoke();
            Assert.AreEqual(ParticleIds.Sand, _editor.CurrentSelection.TypeId, "沙按钮选中沙");

            _water.onClick.Invoke();
            Assert.AreEqual(ParticleIds.Water, _editor.CurrentSelection.TypeId, "水按钮选中水");
        }

        /// <summary>选中项的按钮压成灰、其余保持原色；换选后前一颗复原。</summary>
        [Test]
        public void SelectedButtonTurnsGray()
        {
            Color baseColor = _sand.image.color;

            _sand.onClick.Invoke();

            AssertColor(ParticleSelectionPanel.SelectedTint, _sand.image.color, "选中的按钮压成灰");
            AssertColor(baseColor, _water.image.color, "未选中的按钮保持原色");

            _water.onClick.Invoke();

            AssertColor(baseColor, _sand.image.color, "换选后前一颗恢复原色");
            AssertColor(ParticleSelectionPanel.SelectedTint, _water.image.color, "新选中的按钮压成灰");
        }

        /// <summary>名字对不上当前可投列表的槽恒灰不可点；阶段名单剔掉某类型时那颗按钮跟着变灰，清掉限制又恢复。</summary>
        [Test]
        public void UnavailableSelectionDisablesItsButton()
        {
            Assert.IsFalse(_grass.interactable, "不可手投的类型（名字对不上可投列表）变灰");
            Assert.IsTrue(_fire.interactable, "默认白名单里的火可点");

            ParticleSelectionEntry before = _editor.CurrentSelection;
            _grass.onClick.Invoke();
            Assert.AreSame(before, _editor.CurrentSelection, "灰按钮点了也不改选择");

            // 阶段名单把火剔掉：列表重建（换了一批实例），面板重新解析绑定后那颗按钮跟着变灰。
            _editor.SetPlacementFilter(new[] { "SandParticle" });
            _panel.Refresh();

            Assert.IsFalse(_fire.interactable, "阶段名单剔掉的类型变灰");
            Assert.IsTrue(_sand.interactable, "名单里的类型仍可点");

            _fire.onClick.Invoke();
            Assert.AreEqual(ParticleIds.Sand, _editor.CurrentSelection.TypeId, "灰按钮点了也不改选择");

            _editor.SetPlacementFilter(null);
            _panel.Refresh();

            Assert.IsTrue(_fire.interactable, "清掉阶段限制后恢复可点");
            Assert.IsFalse(_grass.interactable, "不可手投的类型仍旧灰着");
        }

        /// <summary>锁编辑（对话激活 / 高速模式）时整排按钮变灰不可点，且不再染色。</summary>
        [Test]
        public void LockedEditorDisablesEveryButton()
        {
            Color baseColor = _sand.image.color;
            _sand.onClick.Invoke();

            _editor.SuppressInput = true;
            _panel.Refresh();

            Assert.IsFalse(_sand.interactable, "锁编辑时投放按钮不可点");
            Assert.IsFalse(_water.interactable);
            Assert.IsFalse(_fire.interactable);
            Assert.IsFalse(_erase.interactable, "锁编辑时清除按钮不可点");
            AssertColor(baseColor, _sand.image.color, "锁编辑时不再染色（选中标记也撤下）");

            _editor.SuppressInput = false;
            _panel.Refresh();

            Assert.IsTrue(_sand.interactable, "解锁后恢复可点");
            AssertColor(ParticleSelectionPanel.SelectedTint, _sand.image.color, "解锁后选中项重新压灰");
        }

        /// <summary>「清除」按钮翻转 <see cref="ParticleEditor.EraseMode"/> 并压灰；再点一次关掉、恢复原色。</summary>
        [Test]
        public void EraseButtonTogglesEraseModeAndTurnsGray()
        {
            Color baseColor = _erase.image.color;

            _erase.onClick.Invoke();

            Assert.IsTrue(_editor.EraseMode, "点一次进清除模式");
            AssertColor(ParticleSelectionPanel.SelectedTint, _erase.image.color, "清除槽压灰");

            _erase.onClick.Invoke();

            Assert.IsFalse(_editor.EraseMode, "再点一次退出清除模式");
            AssertColor(baseColor, _erase.image.color, "关掉后恢复原色");
        }

        /// <summary>没接按钮的槽整段跳过：刷新不抛异常，也不影响其它槽。</summary>
        [Test]
        public void NullSlotIsSkipped()
        {
            Assert.DoesNotThrow(() => _panel.Refresh());

            Assert.IsTrue(_sand.interactable, "沙槽照常可点");
            Assert.IsTrue(_water.interactable, "水槽照常可点");
            Assert.IsTrue(_fire.interactable, "火槽照常可点");
        }

        /// <summary>名字写错（对不上任何投放项）的槽变灰不可点，点击不改选择。</summary>
        [Test]
        public void UnknownSelectionNameDisablesThatButton()
        {
            Button unknown = MakeButton("Unknown");
            MakePanel(null, (unknown, "Nope"));

            Assert.IsFalse(unknown.interactable, "对不上名字的槽变灰");

            ParticleSelectionEntry before = _editor.CurrentSelection;
            unknown.onClick.Invoke();
            Assert.AreSame(before, _editor.CurrentSelection, "点了也不改选择");
        }

        // ============================== 小工具 ==============================

        private GameObject Spawn(string name)
        {
            var gameObject = new GameObject(name);
            _spawned.Add(gameObject);
            return gameObject;
        }

        /// <summary>一颗面板按钮：Image + Button，并把 targetGraphic 接上自己——
        /// 运行期 <c>AddComponent&lt;Button&gt;</c> 不会自动接 targetGraphic，不接 <c>button.image</c> 就是 null。</summary>
        private Button MakeButton(string name)
        {
            GameObject gameObject = Spawn(name);
            Image image = gameObject.AddComponent<Image>();
            Button button = gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            return button;
        }

        /// <summary>现建一个面板接线（序列化字段直接写，再显式跑一次 Awake：EditMode 不跑 Awake）。</summary>
        private ParticleSelectionPanel MakePanel(Button eraseButton, params (Button Button, string Name)[] bindings)
        {
            ParticleSelectionPanel panel = Spawn("PanelTests.Panel").AddComponent<ParticleSelectionPanel>();
            var serialized = new SerializedObject(panel);
            serialized.FindProperty("_editor").objectReferenceValue = _editor;
            serialized.FindProperty("_eraseButton").objectReferenceValue = eraseButton;

            SerializedProperty array = serialized.FindProperty("_bindings");
            array.arraySize = bindings.Length;
            for (int i = 0; i < bindings.Length; i++)
            {
                SerializedProperty element = array.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("Button").objectReferenceValue = bindings[i].Button;
                element.FindPropertyRelative("SelectionName").stringValue = bindings[i].Name;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            Invoke(panel, "Awake");
            panel.Refresh();
            return panel;
        }

        private static void Invoke(object target, string methodName)
        {
            target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, null);
        }

        /// <summary>颜色按分量比（相等容差与 <see cref="Color"/> 的近似相等一致）。</summary>
        private static void AssertColor(Color expected, Color actual, string message)
        {
            Assert.AreEqual(expected.r, actual.r, 0.001f, message + "（红）");
            Assert.AreEqual(expected.g, actual.g, 0.001f, message + "（绿）");
            Assert.AreEqual(expected.b, actual.b, 0.001f, message + "（蓝）");
            Assert.AreEqual(expected.a, actual.a, 0.001f, message + "（透明度）");
        }
    }
}
