using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Rendering;
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
    /// 场景接线的回归测试：<c>MainScene</c> 里那批「编辑化的对象」必须齐备、层级正确、引用接好。
    /// </summary>
    /// <remarks>
    /// <para>对象与字段都是**场景里的可编辑内容**（由 <c>SceneUiScaffolder</c> 补齐），所以这里直接开真场景读，
    /// 不做任何现建现销——这正是这批断言存在的意义：运行期兜底建出来的对象不算通过。</para>
    /// <para>私有序列化字段一律用 <see cref="SerializedObject"/> 读（与 Inspector 看到的完全一致），
    /// 不去反射私有托管字段。</para>
    /// </remarks>
    public sealed class SceneWiringTests
    {
        private const string ScenePath = "Assets/Scenes/MainScene.unity";

        /// <summary>Particle Sandbox 上 ParticleSimulationDriver 的沙箱尺寸覆盖值——驱动有效尺寸。</summary>
        private const int WorldWidth = 200;
        private const int WorldHeight = 100;

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
        public void SceneHasExactlyOneEventSystem()
        {
            EventSystem[] eventSystems = Object.FindObjectsOfType<EventSystem>();

            Assert.AreEqual(1, eventSystems.Length, "场景应当恰好有一个 EventSystem（对话按钮 / 输入框 / 滚动都要它）");
            Assert.IsNotNull(eventSystems[0].GetComponent<StandaloneInputModule>(),
                "EventSystem 上缺少 StandaloneInputModule，指针事件进不来");
        }

        [Test]
        public void BothCanvasesAreWiredForUiInput()
        {
            AssertCanvas("HUD Canvas", 500);
            AssertCanvas("Dialogue Canvas", 1000);
        }

        [Test]
        public void ParticleRendererIsWiredToSceneObjects()
        {
            ParticleRenderer renderer = Renderer();

            Assert.IsNotNull(Ref(renderer, "_meshFilter"), "ParticleRenderer._meshFilter 没接场景里的 MeshFilter");
            Assert.IsNotNull(Ref(renderer, "_meshRenderer"), "ParticleRenderer._meshRenderer 没接场景里的 MeshRenderer");
            Assert.IsNotNull(Ref(renderer, "_brushRenderer"), "ParticleRenderer._brushRenderer 没接场景里的 SpriteRenderer");
        }

        [Test]
        public void GridQuadUsesPreviewMeshSizedByDriver()
        {
            MeshFilter meshFilter = (MeshFilter)Ref(Renderer(), "_meshFilter");
            MeshRenderer meshRenderer = (MeshRenderer)Ref(Renderer(), "_meshRenderer");

            Assert.AreEqual("ParticleGridQuad", meshFilter.gameObject.name, "世界 quad 应当是 Particle Sandbox 下的 ParticleGridQuad");
            Assert.IsNotNull(meshFilter.sharedMesh, "ParticleGridQuad 上没有网格资产");
            Assert.AreEqual(WorldWidth, meshFilter.sharedMesh.bounds.size.x, 0.001f,
                "预览网格的宽度应当跟着驱动的有效宽（200）走");
            Assert.AreEqual(WorldHeight, meshFilter.sharedMesh.bounds.size.y, 0.001f,
                "预览网格的高度应当跟着驱动的有效高（100）走");

            Assert.IsNotNull(meshRenderer.sharedMaterial, "ParticleGridQuad 上没有预览材质");
            Assert.IsNotNull(meshRenderer.sharedMaterial.shader, "预览材质的着色器丢了");
        }

        [Test]
        public void BrushOverlayIsConfigured()
        {
            SpriteRenderer brush = (SpriteRenderer)Ref(Renderer(), "_brushRenderer");

            Assert.AreEqual("ParticleBrushOverlay", brush.gameObject.name);
            Assert.AreEqual(1, brush.sortingOrder, "笔刷叠层必须压在像素（order 0）与动物之上");
            Assert.IsFalse(brush.enabled, "笔刷叠层的初始状态是隐藏");
            Assert.IsNull(brush.sprite, "笔刷叠层的圆点是运行期画的，场景里不该带 sprite");
        }

        [Test]
        public void DialogueViewIsWiredAndStartsHidden()
        {
            DialogueDebugView view = Root("Dialogue Controller").GetComponent<DialogueDebugView>();
            Assert.IsNotNull(view, "Dialogue Controller 上没有 DialogueDebugView");

            string[] fields =
            {
                "_canvas", "_dialoguePanel", "_reviewPanel", "_portraitImage", "_avatarImage", "_nameText",
                "_lineText", "_choiceRoot", "_choiceButtonTemplate", "_reviewText", "_reviewScroll", "_font",
            };

            for (int i = 0; i < fields.Length; i++)
            {
                Assert.IsNotNull(Ref(view, fields[i]), "DialogueDebugView." + fields[i] + " 没接上");
            }

            Assert.IsFalse(((GameObject)Ref(view, "_dialoguePanel")).activeSelf, "对话面板的初始状态是隐藏");
            Assert.IsFalse(((GameObject)Ref(view, "_reviewPanel")).activeSelf, "回顾面板的初始状态是隐藏");
            Assert.IsFalse(((Button)Ref(view, "_choiceButtonTemplate")).gameObject.activeSelf, "选项按钮模板的初始状态是隐藏");

            TMP_Text line = (TMP_Text)Ref(view, "_lineText");
            Assert.AreEqual(30f, line.fontSize, 0.001f, "台词字号应当沿用旧面板的 30");
        }

        [Test]
        public void SandboxHudIsWiredAndStartsHidden()
        {
            GameObject hudRoot = Root("HUD Canvas");
            SandboxHud hud = hudRoot.GetComponent<SandboxHud>();
            Assert.IsNotNull(hud, "HUD Canvas 上没有 SandboxHud");

            string[] fields =
            {
                "_lightDriver", "_probeText", "_cursorHint", "_cursorSwatch", "_cursorLabel", "_highSpeedStatus",
                "_reportPanel", "_reportBody", "_reportVerdict", "_lightPanel", "_buttonPanel",
                "_lightIntensity", "_lightLitTicks", "_lightDarkTicks",
            };

            for (int i = 0; i < fields.Length; i++)
            {
                Assert.IsNotNull(Ref(hud, fields[i]), "SandboxHud." + fields[i] + " 没接上");
            }

            Assert.IsFalse(((GameObject)Ref(hud, "_reportPanel")).activeSelf, "高速结算面板的初始状态是隐藏");
            Assert.IsFalse(hudRoot.transform.Find("CompletionBanner").gameObject.activeSelf,
                "通关横幅的初始状态是隐藏");

            TMP_InputField[] inputs =
            {
                (TMP_InputField)Ref(hud, "_lightIntensity"),
                (TMP_InputField)Ref(hud, "_lightLitTicks"),
                (TMP_InputField)Ref(hud, "_lightDarkTicks"),
            };

            for (int i = 0; i < inputs.Length; i++)
            {
                Assert.IsNotNull(inputs[i].GetComponent<Image>(), "光照输入框自身要有 Image 才能收点击");
                Assert.IsNotNull(inputs[i].textComponent, "光照输入框的 textComponent 没接上");
            }
        }

        [Test]
        public void EditorAndGameControllerAreWired()
        {
            ParticleEditor editor = Root("Particle Sandbox").GetComponent<ParticleEditor>();
            Assert.IsNotNull(editor, "Particle Sandbox 上没有 ParticleEditor");
            Assert.IsNotNull(Ref(editor, "_hud"), "ParticleEditor._hud 没接 HUD 画布上的 SandboxHud");

            GameController controller = Root("Game Controller").GetComponent<GameController>();
            Assert.IsNotNull(controller, "Game Controller 上没有 GameController");

            GameObject banner = Ref(controller, "_completionBanner") as GameObject;
            Assert.IsNotNull(banner, "GameController._completionBanner 没接上");
            Assert.AreEqual("CompletionBanner", banner.name);
            Assert.IsFalse(banner.activeSelf, "通关横幅的初始状态是隐藏");
        }

        [Test]
        public void ScreenFeedAndBackdropAreWired()
        {
            // 显示图 = 屏幕正中央、2:1、画布最底层的 RawImage（纹理是运行期相机画面，场景里带占位图）。
            GameObject viewObject = Root("HUD Canvas").transform.Find("SandboxView").gameObject;
            RawImage feedImage = viewObject.GetComponent<RawImage>();
            Assert.IsNotNull(feedImage, "HUD Canvas/SandboxView 应当是显示相机画面的 RawImage");

            RectTransform view = (RectTransform)viewObject.transform;
            Assert.AreEqual(new Vector2(0.5f, 0.5f), view.anchorMin, "显示图应当锚在屏幕正中");
            Assert.AreEqual(new Vector2(0.5f, 0.5f), view.anchorMax);
            Assert.AreEqual(Vector2.zero, view.anchoredPosition, "显示图应当在屏幕正中央");
            Assert.AreEqual(2f, view.sizeDelta.x / view.sizeDelta.y, 0.001f,
                "显示图的宽高比应当是 2:1（= 默认沙箱网格 200×100）");
            Assert.AreEqual(0, view.GetSiblingIndex(), "显示图应当是 HUD 画布的最底层（其余 UI 压在它上面）");
            Assert.IsFalse(feedImage.raycastTarget, "显示图是纯显示层，不该吃点击");

            // 占位图：编辑模式看到的就是它；运行期被 CameraScreenFeed 换成相机画面并在退出时还原。
            Assert.AreEqual("SandboxView", feedImage.texture != null ? feedImage.texture.name : null,
                "显示图在场景里应当带着占位图 SandboxView");
            Assert.IsTrue(feedImage.enabled, "显示图应当启用（编辑模式显示占位图）");

            Camera mainCamera = Camera.main;
            Assert.IsNotNull(mainCamera, "场景里应当有一台 MainCamera");
            CameraScreenFeed feed = mainCamera.GetComponent<CameraScreenFeed>();
            Assert.IsNotNull(feed, "Main Camera 上没有 CameraScreenFeed");
            Assert.AreSame(mainCamera, Ref(feed, "_camera"), "CameraScreenFeed._camera 没接主相机");
            Assert.AreSame(feedImage, Ref(feed, "_target"), "CameraScreenFeed._target 没接 SandboxView");

            // 主相机改画纹理后，屏幕上必须还有相机在画，否则 Game 视图会警告 No cameras rendering。
            Camera backdrop = Root("ScreenBackdrop").GetComponent<Camera>();
            Assert.IsNotNull(backdrop, "ScreenBackdrop 上没有相机");
            Assert.AreNotSame(mainCamera, backdrop, "背板相机不能就是主相机");
            Assert.AreEqual(0, backdrop.cullingMask, "屏幕背板相机只清屏、不画任何东西");
            Assert.AreEqual(CameraClearFlags.SolidColor, backdrop.clearFlags);
            Assert.Less(backdrop.depth, mainCamera.depth, "背板相机要在主相机之前画");
            Assert.IsNull(backdrop.targetTexture, "背板相机必须画到屏幕，不能也画进纹理");
            Assert.IsNull(backdrop.GetComponent<AudioListener>(), "背板相机不该带 AudioListener（会与主相机重复）");

            Assert.IsNull(mainCamera.targetTexture, "编辑模式下主相机直接画屏，纹理由 CameraScreenFeed 运行期接管");
            Assert.AreEqual(1f, mainCamera.backgroundColor.a, 0.001f, "清屏色必须不透明——它会成为纹理的 alpha");
        }

        [Test]
        public void ParticlePanelIsWired()
        {
            // 投放面板：10 颗按钮各绑一个投放项显示名，点击由 ParticleSelectionPanel 在运行期绑定
            // （场景里留持久监听会重复触发）。
            GameObject panelObject = Root("HUD Canvas").transform.Find("ParticlePanel").gameObject;
            ParticleSelectionPanel panel = panelObject.GetComponent<ParticleSelectionPanel>();
            Assert.IsNotNull(panel, "HUD Canvas/ParticlePanel 上没有 ParticleSelectionPanel");

            Assert.AreSame(Root("Particle Sandbox").GetComponent<ParticleEditor>(), Ref(panel, "_editor"),
                "ParticleSelectionPanel._editor 没接 Particle Sandbox 上的 ParticleEditor");
            Assert.AreSame(panelObject.transform.Find("Erease").GetComponent<Button>(), Ref(panel, "_eraseButton"),
                "ParticleSelectionPanel._eraseButton 没接 ParticlePanel/Erease");

            string[,] expected =
            {
                { "Stone", "StoneParticle" },
                { "Sand", "SandParticle" },
                { "Dirt", "SoilParticle" },
                { "Water", "WaterParticle" },
                { "Fire", "FireParticle" },
                { "Tree_seed", "TreeSeedParticle" },
                { "Grass_seed", "GrassSeedParticle" },
                { "Rabbit", "兔子" },
                { "Fish", "鱼" },
                { "Bird", "鸟" },
            };

            SerializedObject serialized = new SerializedObject(panel);
            SerializedProperty bindings = serialized.FindProperty("_bindings");
            Assert.AreEqual(expected.GetLength(0), bindings.arraySize, "投放面板应当接 10 颗按钮");

            for (int i = 0; i < bindings.arraySize; i++)
            {
                SerializedProperty element = bindings.GetArrayElementAtIndex(i);
                string name = expected[i, 0];

                Assert.AreSame(panelObject.transform.Find(name).GetComponent<Button>(),
                    element.FindPropertyRelative("Button").objectReferenceValue,
                    "第 " + i + " 槽没接 ParticlePanel/" + name);
                Assert.AreEqual(expected[i, 1], element.FindPropertyRelative("SelectionName").stringValue,
                    "ParticlePanel/" + name + " 绑的投放项显示名不对");
            }

            // 面板点击一律走运行期绑定：11 颗按钮（10 投放 + 清除）上都不该留场景里的持久监听。
            for (int i = 0; i < panelObject.transform.childCount; i++)
            {
                Transform child = panelObject.transform.GetChild(i);
                Button button = child.GetComponent<Button>();
                Assert.IsNotNull(button, "ParticlePanel/" + child.name + " 上没有 Button");
                Assert.AreEqual(0, button.onClick.GetPersistentEventCount(),
                    "ParticlePanel/" + child.name + " 上不该有 onClick 持久监听（点击由 ParticleSelectionPanel 运行期绑定）");
            }
        }

        // ============================== 小工具 ==============================

        private static ParticleRenderer Renderer()
        {
            ParticleRenderer renderer = Root("Particle Sandbox").GetComponent<ParticleRenderer>();
            Assert.IsNotNull(renderer, "Particle Sandbox 上没有 ParticleRenderer");
            return renderer;
        }

        /// <summary>Read a private [SerializeField] reference through the serialized object, i.e. exactly what the Inspector shows.</summary>
        private static UnityEngine.Object Ref(Component target, string fieldName)
        {
            SerializedProperty property = new SerializedObject(target).FindProperty(fieldName);
            Assert.IsNotNull(property, target.GetType().Name + " 上没有序列化字段 " + fieldName);
            return property.objectReferenceValue;
        }

        private static void AssertCanvas(string name, int sortingOrder)
        {
            GameObject root = Root(name);
            Canvas canvas = root.GetComponent<Canvas>();
            Assert.IsNotNull(canvas, name + " 上没有 Canvas");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode, name + " 应当是 overlay 画布");
            Assert.AreEqual(sortingOrder, canvas.sortingOrder, name + " 的排序值与层级契约不符");

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            Assert.IsNotNull(scaler, name + " 上没有 CanvasScaler");
            Assert.AreEqual(CanvasScaler.ScaleMode.ScaleWithScreenSize, scaler.uiScaleMode);
            Assert.AreEqual(new Vector2(1920f, 1080f), scaler.referenceResolution, name + " 的参考分辨率应当是 1920×1080");

            Assert.IsNotNull(root.GetComponent<GraphicRaycaster>(), name + " 上没有 GraphicRaycaster，点击进不来");
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
