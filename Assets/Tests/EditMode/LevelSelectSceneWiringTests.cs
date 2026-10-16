using NUnit.Framework;
using TapTap2026.Game;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 场景接线的回归测试：<c>LevelSelect</c> 场景里的五颗关卡按钮必须齐备、接线正确、不留持久监听，
    /// 悬停描述用的 <c>ProbeText</c> 与五个阶段资产也接在控制器上，且控制器指向的游戏场景名确实存在于 Build Settings。
    /// </summary>
    /// <remarks>
    /// <para>对象与字段都是**场景里的可编辑内容**，所以这里直接开真场景读，不做任何现建现销——
    /// 这正是这批断言存在的意义：运行期兜底建出来的对象不算通过。</para>
    /// <para>私有序列化字段一律用 <see cref="SerializedObject"/> 读（与 Inspector 看到的完全一致），
    /// 不去反射私有托管字段。按钮查找一律走 <c>Transform.Find</c>（能命中隐藏对象），不用
    /// <c>GameObject.Find</c>（会跳过未激活对象——本场景里只有第一关是激活的）。</para>
    /// </remarks>
    public sealed class LevelSelectSceneWiringTests
    {
        private const string ScenePath = "Assets/Scenes/LevelSelect.unity";
        private const string MainScenePath = "Assets/Scenes/MainScene.unity";
        private const string ControllerRootName = "Level Select Controller";
        private const string CanvasRootName = "Canvas";

        /// <summary><c>_probeText</c> 指向的文本对象名：<c>Canvas</c> 下用来显示关卡描述的 TMP 文本。</summary>
        private const string ProbeTextName = "ProbeText";

        /// <summary><c>_gameSceneName</c> 的期望值：选中关卡后要加载的游戏场景。</summary>
        private const string GameSceneName = "MainScene";

        /// <summary>界面登记的 5 条：按钮名（<c>Canvas</c> 下的直接子对象）↔ 阶段 id，顺序即界面顺序。</summary>
        private static readonly string[,] ExpectedLevels =
        {
            { "Day0", "phase_01" },
            { "Day1", "phase_02" },
            { "Day2", "phase_03" },
            { "Day3", "phase_04" },
            { "Sandbox", "phase_05" },
        };

        [SetUp]
        public void SetUp()
        {
            // 批处理跑测试时 MCP 插件连不上外部服务器，会异步刷 SocketException（连接被拒绝）——
            // 时机不可控，落在哪个测试的 SetUp/TearDown 就算谁失败。与本测试的断言无关，吞掉
            // （MCP 包自己的测试也用同一招，见其 ToolThreadSafetyTests）。
            LogAssert.ignoreFailingMessages = true;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            // 别把测试打开的场景留在编辑器里：跑完回到一个干净的空场景。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void LevelSelectControllerIsWiredToFiveButtons()
        {
            SerializedProperty levels = Property(Controller(), "_levels");
            Assert.AreEqual(ExpectedLevels.GetLength(0), levels.arraySize,
                "界面应当登记 5 条「按钮 ↔ 阶段 id」");

            Transform canvas = Root(CanvasRootName).transform;

            for (int i = 0; i < levels.arraySize; i++)
            {
                SerializedProperty entry = levels.GetArrayElementAtIndex(i);
                string buttonName = ExpectedLevels[i, 0];

                Assert.AreSame(canvas.Find(buttonName).GetComponent<Button>(),
                    Relative(entry, "button").objectReferenceValue,
                    "第 " + i + " 条没接 " + CanvasRootName + "/" + buttonName + " 上的 Button");
                Assert.AreEqual(ExpectedLevels[i, 1], Relative(entry, "phaseId").stringValue,
                    CanvasRootName + "/" + buttonName + " 登记的阶段 id 不对");
            }
        }

        [Test]
        public void LevelSelectButtonsHaveNoPersistentListeners()
        {
            // 点击由 LevelSelectController.Awake 在运行期绑定（场景里留持久监听会重复触发）。
            Transform canvas = Root(CanvasRootName).transform;

            for (int i = 0; i < ExpectedLevels.GetLength(0); i++)
            {
                string buttonName = ExpectedLevels[i, 0];
                Button button = canvas.Find(buttonName).GetComponent<Button>();

                Assert.IsNotNull(button, CanvasRootName + "/" + buttonName + " 上没有 Button");
                Assert.AreEqual(0, button.onClick.GetPersistentEventCount(),
                    CanvasRootName + "/" + buttonName
                    + " 上不该有 onClick 持久监听（点击由 LevelSelectController.Awake 运行期绑定）");
            }
        }

        [Test]
        public void LevelSelectSceneHasUiInput()
        {
            GameObject canvasRoot = Root(CanvasRootName);
            Canvas canvas = canvasRoot.GetComponent<Canvas>();
            Assert.IsNotNull(canvas, CanvasRootName + " 上没有 Canvas");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode,
                CanvasRootName + " 应当是 overlay 画布");
            Assert.IsNotNull(canvasRoot.GetComponent<GraphicRaycaster>(),
                CanvasRootName + " 上没有 GraphicRaycaster，点击进不来");

            EventSystem[] eventSystems = Object.FindObjectsOfType<EventSystem>();
            Assert.AreEqual(1, eventSystems.Length, "场景应当恰好有一个 EventSystem（按钮点击全靠它）");
            Assert.IsNotNull(eventSystems[0].GetComponent<StandaloneInputModule>(),
                "EventSystem 上缺少 StandaloneInputModule，指针事件进不来");
        }

        [Test]
        public void GameSceneNamePointsAtBuildScene()
        {
            Assert.AreEqual(GameSceneName, Property(Controller(), "_gameSceneName").stringValue,
                "关卡选择成功后要加载的游戏场景名应当是 " + GameSceneName);

            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            int mainSceneIndex = -1;
            bool levelSelectPresent = false;

            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path == MainScenePath)
                {
                    mainSceneIndex = i;
                    Assert.IsTrue(scenes[i].enabled,
                        "Build Settings 里的 " + MainScenePath + " 必须启用，否则 LoadScene 找不到它");
                }
                else if (scenes[i].path == ScenePath)
                {
                    levelSelectPresent = true;
                    Assert.IsTrue(scenes[i].enabled, "Build Settings 里的 " + ScenePath + " 必须启用");
                }
            }

            Assert.AreEqual(0, mainSceneIndex,
                MainScenePath + " 必须在 Build Settings 下标 0（构建后的启动场景就是它）");
            Assert.IsTrue(levelSelectPresent, "Build Settings 里缺少 " + ScenePath);
        }

        [Test]
        public void ProbeTextIsWiredAndStartsHidden()
        {
            TMP_Text probe = (TMP_Text)Property(Controller(), "_probeText").objectReferenceValue;

            Assert.IsNotNull(probe, ControllerRootName + " 的 _probeText 应当指向场景里的 " + ProbeTextName);
            Assert.AreEqual(ProbeTextName, probe.gameObject.name,
                "_probeText 指向的对象应当叫 " + ProbeTextName + "（关卡描述往它上面写）");
            Assert.IsFalse(probe.gameObject.activeSelf,
                ProbeTextName + " 的初始状态是隐藏，等指针悬停到关卡按钮上才显示");
            Assert.AreEqual(string.Empty, probe.text,
                ProbeTextName + " 里不该预置占位文案（描述由悬停时写入）");
        }

        [Test]
        public void PhasesAreWiredForDescriptions()
        {
            SerializedProperty phases = Property(Controller(), "_phases");
            Assert.AreEqual(ExpectedLevels.GetLength(0), phases.arraySize,
                "_phases 应当登记 5 个阶段资产，与界面上的 5 颗按钮一一对应");

            for (int i = 0; i < phases.arraySize; i++)
            {
                TextAsset phase = (TextAsset)phases.GetArrayElementAtIndex(i).objectReferenceValue;

                Assert.IsNotNull(phase, "_phases 第 " + i + " 项没接 TextAsset");
                Assert.AreEqual(ExpectedLevels[i, 1], phase.name,
                    "_phases 第 " + i + " 项应当接 " + ExpectedLevels[i, 1] + " 的资产");
            }
        }

        // ============================== 小工具 ==============================

        private static LevelSelectController Controller()
        {
            LevelSelectController controller = Root(ControllerRootName).GetComponent<LevelSelectController>();
            Assert.IsNotNull(controller, ControllerRootName + " 上没有 LevelSelectController");
            return controller;
        }

        /// <summary>Read a private [SerializeField] through the serialized object, i.e. exactly what the Inspector shows.</summary>
        private static SerializedProperty Property(Component target, string fieldName)
        {
            SerializedProperty property = new SerializedObject(target).FindProperty(fieldName);
            Assert.IsNotNull(property, target.GetType().Name + " 上没有序列化字段 " + fieldName);
            return property;
        }

        private static SerializedProperty Relative(SerializedProperty parent, string fieldName)
        {
            SerializedProperty property = parent.FindPropertyRelative(fieldName);
            Assert.IsNotNull(property, parent.propertyPath + " 下没有子字段 " + fieldName);
            return property;
        }

        private static GameObject Root(string name)
        {
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name)
                {
                    return roots[i];
                }
            }

            Assert.Fail("场景里找不到根对象「" + name + "」");
            return null;
        }
    }
}
