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
    /// 场景接线的回归测试：<c>SampleScene</c> 里那批「编辑化的对象」必须齐备、层级正确、引用接好。
    /// </summary>
    /// <remarks>
    /// <para>对象与字段都是**场景里的可编辑内容**（由 <c>SceneUiScaffolder</c> 补齐），所以这里直接开真场景读，
    /// 不做任何现建现销——这正是这批断言存在的意义：运行期兜底建出来的对象不算通过。</para>
    /// <para>私有序列化字段一律用 <see cref="SerializedObject"/> 读（与 Inspector 看到的完全一致），
    /// 不去反射私有托管字段。</para>
    /// </remarks>
    public sealed class SceneWiringTests
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

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
                "_reportPanel", "_reportBody", "_reportVerdict", "_lightPanel",
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
