using System.Collections.Generic;
using TMPro;
using TapTap2026.Game;
using TapTap2026.Rendering;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace TapTap2026.EditorTools
{
    /// <summary>
    /// 场景 UI 脚手架：把原本「运行期动态创建」的那批对象补齐到 <c>MainScene</c> 里并接好线——
    /// 粒子网格 quad 与笔刷叠层、主相机取景、EventSystem、HUD Canvas（<see cref="SandboxHud"/> 的层级 +
    /// 暂停 / 光照 / 高速三颗按钮、光照面板里强度滑块 + 白天 / 夜晚两行秒读数 + 两行按秒步进按钮
    /// + 正中央显示相机画面的 <c>SandboxView</c>）、
    /// 投放面板（<c>ParticlePanel</c>）的按钮接线、
    /// Dialogue Canvas（<see cref="DialogueDebugView"/> 的层级）、中文字体资产、屏幕背板。
    /// </summary>
    /// <remarks>
    /// <para><b>幂等</b>：对象一律按名字 / 路径找，缺才建。**已存在对象的 RectTransform、文本内容与其它外观参数
    /// 一概不动**——用户手调过的布局、文案、字号不会被跑一次脚手架抹掉。每次重跑都会做的只有：
    /// 引用接线、三个按钮与光照面板八颗按秒步进按钮的 <c>onClick</c> 持久监听、
    /// 强度滑块的三个 rect 接线与 0..30 整数档范围（面板契约，不算外观）、
    /// 显示图的「居中 + 底层 + 不吃点击」三条契约、
    /// 预览 mesh 的尺寸刷新、笔刷叠层的 <c>sortingOrder</c>、相机的编辑态取景、
    /// 两个 Canvas 的渲染模式 / 排序 / 缩放参数（层级契约，见 <see cref="BuildCanvas"/>）与字体套用、
    /// 以及**清掉多余的 <c>EventSystem</c>**（同场景多个会让 UI 全哑，见 <see cref="EnsureEventSystem"/>）。</para>
    /// <para>用法：菜单 <c>Tools/TapTap2026/Scene UI Setup（补缺失+接线)</c>，
    /// 或批处理 <c>-executeMethod TapTap2026.EditorTools.SceneUiScaffolder.Run</c>。
    /// 批处理必须在 Unity 编辑器关着的时候跑。</para>
    /// </remarks>
    public static class SceneUiScaffolder
    {
        private const string ScenePath = "Assets/Scenes/MainScene.unity";

        /// <summary>场景里那个**唯一**的输入根对象的约定名（<see cref="EnsureEventSystem"/> 保证仅此一个）。</summary>
        private const string EventSystemRootName = "EventSystem";

        private const string DataFolder = "Assets/Data";
        private const string PreviewMeshPath = "Assets/Data/SandboxPreviewMesh.asset";
        private const string PreviewMaterialPath = "Assets/Data/SandboxPreview.mat";
        private const string FontAssetPath = "Assets/Data/CjkDynamicFont.asset";
        private const string FontFolderPath = "Assets/Data/Fonts";

        /// <summary>显示相机画面的那张图的占位图（编辑模式看到的就是它；运行期换成相机画面）。</summary>
        private const string SandboxViewPlaceholderPath = "Assets/Images/UI/SandboxView.png";

        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;

        private const int FontSamplingPointSize = 44;
        private const int FontAtlasPadding = 4;
        private const int FontAtlasSize = 1024;

        /// <summary>中文字体候选链（C:/Windows/Fonts 下的 .ttf 文件名，不含扩展名）：按序取第一个存在的。
        /// 只收 .ttf——.gitattributes 已把 *.ttf 纳入 LFS，.ttc（雅黑/宋体是 .ttc）未纳入、不用。</summary>
        private static readonly string[] OsFontCandidates =
        {
            "simhei", "NotoSansSC-VF",
        };

        /// <summary>显示图占位的默认尺寸（参考分辨率 1920×1080 下：2:1，四周留出 UI 边距）。
        /// 宽高比 2:1 与默认沙箱网格 200×100 一致——相机视野因此恰好是整张网格（见 CameraScreenFeed）。</summary>
        private static readonly Vector2 SandboxViewSize = new Vector2(1600f, 800f);

        private static readonly Color DialoguePanelColor = new Color(0.05f, 0.06f, 0.09f, 0.85f);
        private static readonly Color ReportPanelColor = new Color(0.05f, 0.06f, 0.09f, 0.92f);
        private static readonly Color ButtonColor = new Color(0.16f, 0.2f, 0.28f, 0.95f);
        private static readonly Color BannerColor = new Color(0.25f, 0.9f, 0.35f, 0.9f);
        private static readonly Color HintColor = new Color(0.75f, 0.8f, 0.9f, 1f);
        private static readonly Color BannerTextColor = new Color(0.05f, 0.06f, 0.09f, 1f);

        /// <summary>光照面板行标签相对面板左边的偏移（x）——与场景里手调过的行标签一致；只作用于**新建**对象。</summary>
        private const float LightRowLabelX = 17f;

        /// <summary>光照面板行标签的颜色（用户手调的深灰，压在浅色面板底上）——只作用于**新建**对象；
        /// 已存在的行标签一律不动。</summary>
        private static readonly Color LightLabelColor = new Color(0.275f, 0.275f, 0.275f, 1f);

        /// <summary>强度面板的范围与默认值：**0..30（整数档）**——负数制冷只能在 Inspector 上设，面板不给负档；
        /// 默认 10。只作用于**新建**的滑块（值只在首次创建时套用，范围与整数档是面板契约、每次都套）。</summary>
        private const float LightIntensityMin = 0f;
        private const float LightIntensityMax = 30f;
        private const float LightIntensityDefault = 10f;

        /// <summary>强度滑块三条轨道的平色（与面板里其它控件的平色风格一致：背景深、填充亮、滑块近白）。</summary>
        private static readonly Color SliderBackgroundColor = new Color(0.10f, 0.12f, 0.18f, 0.9f);
        private static readonly Color SliderFillColor = new Color(0.62f, 0.72f, 0.92f, 1f);
        private static readonly Color SliderHandleColor = new Color(0.95f, 0.96f, 1f, 1f);

        [MenuItem("Tools/TapTap2026/Scene UI Setup（补缺失+接线)")]
        public static void Run()
        {
            var report = new Report();

            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!AssetDatabase.IsValidFolder(DataFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Data");
            }

            GameObject sandbox = FindRoot(scene, "Particle Sandbox");
            if (sandbox == null)
            {
                Debug.LogError("[SceneUiScaffolder] 场景里找不到「Particle Sandbox」，脚手架中止。");
                return;
            }

            ResolveSandboxSize(sandbox, out int width, out int height);

            ParticleRenderer renderer = EnsureComponent<ParticleRenderer>(sandbox);
            BuildSandboxVisuals(sandbox.transform, renderer, width, height, report);

            FrameMainCamera(scene, width, height, report);
            EnsureEventSystem(scene, report);

            GameObject hudRoot = BuildCanvas(scene, "HUD Canvas", 500, report);
            SandboxHud hud = EnsureComponent<SandboxHud>(hudRoot);
            BuildHud(hudRoot.transform, report);

            GameObject dialogueRoot = BuildCanvas(scene, "Dialogue Canvas", 1000, report);
            DialogueDebugView view = FindComponent<DialogueDebugView>(scene, "Dialogue Controller");
            BuildDialogue(dialogueRoot.transform, report);

            // 字体必须在接线之前建好：DialogueDebugView._font 与两个 Canvas 下的 TMP_Text 一次套全。
            TMP_FontAsset font = EnsureCjkFont(report);
            if (font != null)
            {
                ApplyFont(hudRoot, font);
                ApplyFont(dialogueRoot, font);
            }

            WireHud(hud, hudRoot, sandbox, report);
            WireDialogue(view, dialogueRoot, font, report);
            WireGameController(scene, FindComponent<ParticleEditor>(scene, "Particle Sandbox"), hud, report);
            WireButtons(FindComponent<ParticleEditor>(scene, "Particle Sandbox"), hudRoot, report);
            WireLightStepButtons(hud, hudRoot.transform, report);
            WireParticlePanel(FindComponent<ParticleEditor>(scene, "Particle Sandbox"), hudRoot, report);
            EnsureScreenFeed(scene, hudRoot, report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log("[SceneUiScaffolder] 完成：沙箱 " + width + "×" + height
                + "，" + report.Describe() + "。场景与资产已保存。");
        }

        // ============================== 沙盒世界对象 ==============================

        /// <summary>
        /// 驱动器的有效沙箱尺寸：组件上的覆盖值 &gt; 0 就用它，否则回退到 Settings 资产的宽高
        /// （与 <c>ParticleSimulationDriver</c> 的取尺寸规则一致）。
        /// </summary>
        private static void ResolveSandboxSize(GameObject sandbox, out int width, out int height)
        {
            width = 0;
            height = 0;

            ParticleSimulationDriver driver = sandbox.GetComponent<ParticleSimulationDriver>();
            if (driver != null)
            {
                var driverSo = new SerializedObject(driver);
                SerializedProperty widthOverride = driverSo.FindProperty("_widthOverride");
                SerializedProperty heightOverride = driverSo.FindProperty("_heightOverride");
                if (widthOverride != null && widthOverride.intValue > 0)
                {
                    width = widthOverride.intValue;
                }

                if (heightOverride != null && heightOverride.intValue > 0)
                {
                    height = heightOverride.intValue;
                }

                SerializedProperty settingsProperty = driverSo.FindProperty("_settings");
                var settings = settingsProperty != null
                    ? settingsProperty.objectReferenceValue as ParticleSimulationSettings
                    : null;
                if (settings != null)
                {
                    if (width <= 0)
                    {
                        width = settings.Width;
                    }

                    if (height <= 0)
                    {
                        height = settings.Height;
                    }
                }
            }

            // 兜底到 Settings 资产的默认值——驱动器都没接线时也得有个能看的取景。
            if (width <= 0)
            {
                width = 320;
            }

            if (height <= 0)
            {
                height = 180;
            }
        }

        /// <summary>
        /// 粒子沙盒下的两个世界子物体：网格 quad（MeshFilter + MeshRenderer + 预览 mesh / 材质）
        /// 与笔刷轮廓叠层（SpriteRenderer）。两者都是场景对象，编辑模式可见可选；运行期只在没接线时才自建。
        /// </summary>
        private static void BuildSandboxVisuals(Transform sandbox, ParticleRenderer renderer, int width, int height, Report report)
        {
            Mesh mesh = EnsurePreviewMesh(width, height);
            Material material = EnsurePreviewMaterial();

            GameObject quad = EnsureChild(sandbox, "ParticleGridQuad", report, out bool _);
            MeshFilter meshFilter = EnsureComponent<MeshFilter>(quad);
            MeshRenderer quadRenderer = EnsureComponent<MeshRenderer>(quad);

            // mesh 与材质属于「预览接线」：尺寸跟着驱动有效宽高走，每次重跑都刷新。
            meshFilter.sharedMesh = mesh;
            if (material != null)
            {
                quadRenderer.sharedMaterial = material;
            }

            ApplyQuadRendererSettings(quadRenderer);

            GameObject brush = EnsureChild(sandbox, "ParticleBrushOverlay", report, out bool createdBrush);
            SpriteRenderer brushRenderer = EnsureComponent<SpriteRenderer>(brush);
            if (createdBrush)
            {
                brushRenderer.sprite = null;
                brushRenderer.enabled = false;
            }

            // 与运行期 EnsureBrushOverlay 的约定一致：叠层永远压在像素与动物之上。
            brushRenderer.sortingOrder = 1;

            var rendererSo = new SerializedObject(renderer);
            SetRef(rendererSo, "_meshFilter", meshFilter, report);
            SetRef(rendererSo, "_meshRenderer", quadRenderer, report);
            SetRef(rendererSo, "_brushRenderer", brushRenderer, report);
            rendererSo.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// quad 的渲染设置：不投影 / 不接收阴影、不采光探针与反射探针、不生成运动矢量——
        /// 与 <c>ParticleRenderer</c> 里运行期自建 quad 走的那份设置逐条一致。
        /// </summary>
        private static void ApplyQuadRendererSettings(MeshRenderer quadRenderer)
        {
            quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
            quadRenderer.receiveShadows = false;
            quadRenderer.lightProbeUsage = LightProbeUsage.Off;
            quadRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            quadRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>
        /// 预览 mesh 资产：内容永远是当前有效宽高的 <c>ParticleRenderer.BuildQuadMesh</c>
        /// （与运行期上屏用的是同一个生成器）。已存在的资产原地换掉内容，不新建资产、不换 GUID。
        /// </summary>
        private static Mesh EnsurePreviewMesh(int width, int height)
        {
            Mesh fresh = ParticleRenderer.BuildQuadMesh(width, height);

            // 运行期生成器给的是 DontSave；落盘前必须清掉，否则 CreateAsset 不认它。
            fresh.hideFlags = HideFlags.None;

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(PreviewMeshPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(fresh, PreviewMeshPath);
                return fresh;
            }

            // 逐项换数据而不是 CopySerialized：后者会把资产名字一起改成源 mesh 的名字。
            existing.Clear();
            existing.vertices = fresh.vertices;
            existing.uv = fresh.uv;
            existing.triangles = fresh.triangles;
            existing.RecalculateBounds();

            Object.DestroyImmediate(fresh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        /// <summary>预览材质资产（白天天空色）。已存在就直接复用，不覆盖用户调过的参数。</summary>
        private static Material EnsurePreviewMaterial()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(PreviewMaterialPath);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            if (shader == null)
            {
                Debug.LogWarning("[SceneUiScaffolder] 找不到 2D sprite 着色器，跳过预览材质——"
                    + "ParticleGridQuad 会是一张没材质的白板。");
                return null;
            }

            var material = new Material(shader) { name = "SandboxPreview" };
            if (material.HasProperty("_Color"))
            {
                material.color = ParticleRenderer.DefaultDayBackground;
            }

            AssetDatabase.CreateAsset(material, PreviewMaterialPath);
            return material;
        }

        /// <summary>主相机编辑态取景：正交、竖直刚好框住整张网格、居中（与 <c>ParticleRenderer.FitCamera</c> 同值）。</summary>
        private static void FrameMainCamera(Scene scene, int width, int height, Report report)
        {
            GameObject cameraObject = FindRoot(scene, "Main Camera");
            if (cameraObject == null)
            {
                report.Notice("场景里没有「Main Camera」，跳过取景。");
                return;
            }

            Camera camera = cameraObject.GetComponent<Camera>();
            if (camera == null)
            {
                report.Notice("「Main Camera」上没有 Camera 组件，跳过取景。");
                return;
            }

            camera.orthographic = true;
            camera.orthographicSize = height * 0.5f;
            cameraObject.transform.position = new Vector3(width * 0.5f, height * 0.5f, -10f);
        }

        /// <summary>
        /// 场景必须**恰好一个** EventSystem：对话按钮、光照滑块都靠它收事件（组件不再自建）。
        /// <para><b>为什么按组件找而不是按名字找</b>：多出来的 EventSystem（从 GameObject 菜单 / MCP 拖进来的
        /// 第二个，名字往往不叫 <c>EventSystem</c>）会静默废掉整个 UI——<see cref="EventSystem.current"/> 只认
        /// 注册表里的第一个，其余既不收指针事件也不再更新选中，表现就是「所有按钮点了没反应」。
        /// 按名字找只保证「有」，这里按组件找并清掉多余的，才叫幂等。</para>
        /// <para>保留谁：带输入模块的那个（能收事件的就是它）；都没有模块时优先保留叫 <c>EventSystem</c> 的根对象。</para>
        /// </summary>
        private static void EnsureEventSystem(Scene scene, Report report)
        {
            var systems = new List<EventSystem>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                systems.AddRange(roots[i].GetComponentsInChildren<EventSystem>(true));
            }

            EventSystem keeper = null;
            for (int i = 0; i < systems.Count && keeper == null; i++)
            {
                if (systems[i].GetComponent<BaseInputModule>() != null)
                {
                    keeper = systems[i];
                }
            }

            for (int i = 0; i < systems.Count && keeper == null; i++)
            {
                if (systems[i].gameObject.name == EventSystemRootName)
                {
                    keeper = systems[i];
                }
            }

            if (keeper == null && systems.Count > 0)
            {
                keeper = systems[0];
            }

            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] == keeper)
                {
                    continue;
                }

                report.Notice("删除多余的 EventSystem（" + PathOf(systems[i].transform) + "）——同场景多个会让 UI 全哑");
                Undo.DestroyObjectImmediate(systems[i].gameObject);
            }

            GameObject eventSystemObject = keeper != null ? keeper.gameObject : EnsureChild(null, EventSystemRootName, report, out bool _);
            if (eventSystemObject.name != EventSystemRootName)
            {
                // 名字按契约收回：下一次找它就只认这一个，且 Inspector 里一眼能看出这是场景的输入根。
                report.Notice("EventSystem 改名：" + eventSystemObject.name + " → " + EventSystemRootName);
                eventSystemObject.name = EventSystemRootName;
            }

            EnsureComponent<EventSystem>(eventSystemObject);
            EnsureComponent<StandaloneInputModule>(eventSystemObject);
        }

        // ============================== HUD Canvas ==============================

        /// <summary>确保一个 overlay 根画布（Canvas + CanvasScaler + GraphicRaycaster）。</summary>
        private static GameObject BuildCanvas(Scene scene, string name, int sortingOrder, Report report)
        {
            GameObject root = FindRoot(scene, name);
            if (root == null)
            {
                root = new GameObject(name, typeof(RectTransform));
                report.Created(name + "（根对象）");
            }

            Canvas canvas = EnsureComponent<Canvas>(root);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // 层级契约：HUD 在对话 UI 下面（对话框 1000、HUD 500），排序值每次重跑都保证。
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = EnsureComponent<CanvasScaler>(root);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);

            EnsureComponent<GraphicRaycaster>(root);
            return root;
        }

        /// <summary>
        /// HUD 层级：光标读数 / 鼠标跟随组 / 高速读数 / 光照面板 / 高速结算面板 / 通关横幅 /
        /// 暂停·光照·高速三颗按钮。
        /// 全部是场景里的可编辑对象，<see cref="SandboxHud"/> 只持引用。
        /// </summary>
        private static void BuildHud(Transform root, Report report)
        {
            // 左上角光标格读数
            TMP_Text probe = EnsureText(root, "ProbeText", report, out bool createdProbe);
            if (createdProbe)
            {
                Place(probe.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(6f, -4f), new Vector2(520f, 20f));
                StyleText(probe, 16f, TextAlignmentOptions.Left, Color.white);
            }

            // 鼠标跟随组（色块 + 文字）；运行期由 SandboxHud 贴指针摆位置，初始隐藏。
            RectTransform cursorHint = EnsureUiChild(root, "CursorHint", report, out bool createdCursor);
            if (createdCursor)
            {
                Place(cursorHint, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0.5f),
                    Vector2.zero, new Vector2(240f, 20f));
                cursorHint.gameObject.SetActive(false);
            }

            Image swatch = EnsureComponent<Image>(EnsureUiChild(cursorHint, "Swatch", report, out bool createdSwatch).gameObject);
            if (createdSwatch)
            {
                Place((RectTransform)swatch.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    Vector2.zero, new Vector2(12f, 12f));
                swatch.color = Color.white;
            }

            TMP_Text cursorLabel = EnsureText(cursorHint, "Label", report, out bool createdCursorLabel);
            if (createdCursorLabel)
            {
                Place(cursorLabel.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(18f, 0f), new Vector2(222f, 20f));
                StyleText(cursorLabel, 16f, TextAlignmentOptions.Left, Color.white);
            }

            // 左下角高速模式读数
            TMP_Text highSpeed = EnsureText(root, "HighSpeedStatus", report, out bool createdHighSpeed);
            if (createdHighSpeed)
            {
                Place(highSpeed.rectTransform, Vector2.zero, Vector2.zero, Vector2.zero,
                    new Vector2(6f, 4f), new Vector2(560f, 20f));
                StyleText(highSpeed, 16f, TextAlignmentOptions.Left, Color.white);
            }

            BuildLightPanel(root, report);
            BuildReportPanel(root, report);
            BuildCompletionBanner(root, report);
            BuildButtonPanel(root, report);
        }

        /// <summary>
        /// 暂停 / 光照 / 高速三颗按钮的容器（左上角读数下方横排一排）。
        /// 按钮是**场景对象**，<c>onClick</c> 由 <see cref="WireButtons"/> 接成持久监听指向 <see cref="ParticleEditor"/>。
        /// </summary>
        private static void BuildButtonPanel(Transform root, Report report)
        {
            RectTransform panel = EnsureUiChild(root, "ButtonPanel", report, out bool createdPanel);
            if (createdPanel)
            {
                Place(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(6f, -30f), new Vector2(330f, 30f));
            }

            BuildButton(panel, "PauseButton", "暂停", new Vector2(0f, 0f), new Vector2(100f, 30f), 16f, report);
            BuildButton(panel, "LightButton", "光照", new Vector2(110f, 0f), new Vector2(100f, 30f), 16f, report);
            BuildButton(panel, "HighSpeedButton", "高速", new Vector2(220f, 0f), new Vector2(100f, 30f), 16f, report);
        }

        /// <summary>
        /// 一颗按钮：背景图（收点击）+ 居中文本；布局相对面板**左上角**
        /// （<paramref name="anchoredPosition"/> = 相对该角的偏移、<paramref name="size"/> = 尺寸）。
        /// 布局值只在**首次创建**时套用，已存在的对象一律不动。
        /// </summary>
        private static void BuildButton(Transform panel, string name, string label, Vector2 anchoredPosition,
            Vector2 size, float fontSize, Report report)
        {
            RectTransform rect = EnsureUiChild(panel, name, report, out bool created);
            Image background = EnsureComponent<Image>(rect.gameObject);
            Button button = EnsureComponent<Button>(rect.gameObject);
            if (created)
            {
                Place(rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), anchoredPosition, size);
                background.color = ButtonColor;
                button.targetGraphic = background;
            }

            TMP_Text text = EnsureText(rect, "Label", report, out bool createdText);
            if (createdText)
            {
                Stretch(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(6f, 2f), new Vector2(-6f, -2f));
                StyleText(text, fontSize, TextAlignmentOptions.Center, Color.white);
                text.text = label;
            }
        }

        /// <summary>
        /// 右上角光照面板：标题 + 一行「标签 + **强度滑块** + 数值读数」+ 两行「标签 + 相位**秒读数**」
        /// + 两行按秒步进按钮（白天 / 夜晚，各四颗「−5秒 / −1秒 / +1秒 / +5秒」），
        /// 整体挂 CanvasGroup 供 <see cref="SandboxHud"/> 变灰 / 屏蔽点击。
        /// 步进按钮的 <c>onClick</c> 由 <see cref="WireLightStepButtons"/> 接成指向 <see cref="SandboxHud"/> 的持久监听。
        /// </summary>
        private static void BuildLightPanel(Transform root, Report report)
        {
            RectTransform panel = EnsureUiChild(root, "LightPanel", report, out bool createdPanel);
            Image background = EnsureComponent<Image>(panel.gameObject);
            EnsureComponent<CanvasGroup>(panel.gameObject);
            if (createdPanel)
            {
                Place(panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                    new Vector2(-6f, -4f), new Vector2(240f, 150f));
                background.color = DialoguePanelColor;
            }

            TMP_Text title = EnsureText(panel, "Title", report, out bool createdTitle);
            if (createdTitle)
            {
                Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(10f, -4f), new Vector2(220f, 18f));
                StyleText(title, 16f, TextAlignmentOptions.Left, Color.white);
                title.text = "光照";
            }

            BuildLightSliderRow(panel, "IntensityLabel", "强度", 0, report);
            BuildLightValueRow(panel, "LitTicksLabel", "LitTicks", "白天", 1, report);
            BuildLightValueRow(panel, "DarkTicksLabel", "DarkTicks", "夜晚", 2, report);

            BuildLightStepRow(panel, "LitTicksStepLabel", "白天", -100f, report);
            BuildLightStepRow(panel, "DarkTicksStepLabel", "夜晚", -122f, report);
        }

        /// <summary>
        /// 光照面板的一行行标签（同时也是这一行的容器）：行顶 y = −26 − 22 × <paramref name="row"/>；
        /// x 取 <see cref="LightRowLabelX"/>、颜色取 <see cref="LightLabelColor"/>（与场景里手调过的行标签一致）；
        /// 布局与文案只在该标签**首次创建**时套用，已存在的一律不动（与各行的子件同款）。
        /// </summary>
        private static TMP_Text BuildLightRowLabel(Transform panel, string labelName, string label, int row, Report report)
        {
            TMP_Text rowLabel = EnsureText(panel, labelName, report, out bool createdLabel);
            if (createdLabel)
            {
                Place(rowLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(LightRowLabelX, -26f - 22f * row), new Vector2(40f, 20f));
                StyleText(rowLabel, 15f, TextAlignmentOptions.Left, LightLabelColor);
                rowLabel.text = label;
            }

            return rowLabel;
        }

        /// <summary>
        /// 光照面板的**强度行**：行标签 + 强度滑块（<c>Intensity</c>，挂在标签下）+ 右侧数值读数
        /// （<c>IntensityValue</c>，也挂在标签下）。滑块与读数的布局只在**首次创建**时套用。
        /// </summary>
        private static void BuildLightSliderRow(Transform panel, string labelName, string label, int row, Report report)
        {
            TMP_Text rowLabel = BuildLightRowLabel(panel, labelName, label, row, report);

            Slider slider = EnsureSlider(rowLabel.transform, "Intensity", report, out bool createdSlider);
            if (createdSlider)
            {
                Place((RectTransform)slider.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f), new Vector2(41f, 0f), new Vector2(120f, 14f));
            }

            TMP_Text value = EnsureText(rowLabel.transform, "IntensityValue", report, out bool createdValue);
            if (createdValue)
            {
                Place(value.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(166f, 0f), new Vector2(44f, 16f));
                StyleText(value, 15f, TextAlignmentOptions.Left, Color.white);
            }
        }

        /// <summary>
        /// 光照面板的一行**相位读数**：行标签 + 秒读数文本（<paramref name="valueName"/>，挂在标签下）。
        /// 文本的布局与初始内容只在**首次创建**时套用——内容运行期由 <see cref="SandboxHud"/> 每帧写。
        /// </summary>
        private static void BuildLightValueRow(Transform panel, string labelName, string valueName, string label,
            int row, Report report)
        {
            TMP_Text rowLabel = BuildLightRowLabel(panel, labelName, label, row, report);

            TMP_Text value = EnsureText(rowLabel.transform, valueName, report, out bool createdValue);
            if (createdValue)
            {
                Place(value.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(41f, 0f), new Vector2(120f, 20f));
                StyleText(value, 15f, TextAlignmentOptions.Left, Color.white);
            }
        }

        /// <summary>步进按钮的后缀名与标签文本（顺序即摆放顺序，x = 41 + 45 × i）。</summary>
        private static readonly string[] LightStepSuffixes = { "Minus5", "Minus1", "Plus1", "Plus5" };
        private static readonly string[] LightStepLabels = { "-5秒", "-1秒", "+1秒", "+5秒" };

        /// <summary>步进行标签名的固定后缀：按钮名前缀 = 标签名去掉它（"LitTicksStepLabel" → "Lit"）。</summary>
        private const string LightStepLabelSuffix = "TicksStepLabel";

        /// <summary>
        /// 光照面板的一行步进按钮：左侧标签（同时也是这一行的容器）+ 四颗「−5秒 / −1秒 / +1秒 / +5秒」按钮，
        /// 按钮相对**标签左上角**横排（x = 41 + 45 × i）。按钮结构与 <see cref="BuildButton"/> 完全同构。
        /// 布局值只在**首次创建**时套用，已存在的对象一律不动（与 <see cref="BuildLightRowLabel"/> 同款）。
        /// </summary>
        private static void BuildLightStepRow(Transform panel, string labelName, string label, float y, Report report)
        {
            // 按钮名 = 行前缀（Lit / Dark）+ 步长后缀，行前缀由标签名给出。
            string prefix = labelName.Substring(0, labelName.Length - LightStepLabelSuffix.Length);

            TMP_Text rowLabel = EnsureText(panel, labelName, report, out bool createdLabel);
            if (createdLabel)
            {
                Place(rowLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(LightRowLabelX, y), new Vector2(40f, 20f));
                StyleText(rowLabel, 15f, TextAlignmentOptions.Left, LightLabelColor);
                rowLabel.text = label;
            }

            for (int i = 0; i < LightStepSuffixes.Length; i++)
            {
                BuildButton(rowLabel.transform, prefix + LightStepSuffixes[i], LightStepLabels[i],
                    new Vector2(41f + 45f * i, 0f), new Vector2(44f, 20f), 13f, report);
            }
        }

        /// <summary>
        /// 高速模式结算面板：竖排布局 + 高度自适应（内容多长面板就多高），默认隐藏，
        /// 由 <see cref="SandboxHud.ShowHighSpeedReport"/> 在会话进终态时打开。
        /// </summary>
        private static void BuildReportPanel(Transform root, Report report)
        {
            RectTransform panel = EnsureUiChild(root, "ReportPanel", report, out bool createdPanel);
            Image background = EnsureComponent<Image>(panel.gameObject);
            VerticalLayoutGroup layout = EnsureComponent<VerticalLayoutGroup>(panel.gameObject);
            ContentSizeFitter fitter = EnsureComponent<ContentSizeFitter>(panel.gameObject);
            if (createdPanel)
            {
                Place(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    Vector2.zero, new Vector2(360f, 0f));
                background.color = ReportPanelColor;

                layout.padding = new RectOffset(12, 12, 12, 12);
                layout.spacing = 4f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                layout.childAlignment = TextAnchor.UpperCenter;

                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                panel.gameObject.SetActive(false);
            }

            TMP_Text title = EnsureText(panel, "Title", report, out bool createdTitle);
            if (createdTitle)
            {
                Stretch(title.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                StyleText(title, 16f, TextAlignmentOptions.Center, Color.white);
                title.text = "高速模式结算";
            }

            TMP_Text body = EnsureText(panel, "Body", report, out bool createdBody);
            if (createdBody)
            {
                Stretch(body.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                StyleText(body, 15f, TextAlignmentOptions.TopLeft, Color.white);
            }

            TMP_Text verdict = EnsureText(panel, "Verdict", report, out bool createdVerdict);
            if (createdVerdict)
            {
                Stretch(verdict.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                StyleText(verdict, 15f, TextAlignmentOptions.TopLeft, Color.white);
                verdict.gameObject.SetActive(false);
            }

            TMP_Text hint = EnsureText(panel, "Hint", report, out bool createdHint);
            if (createdHint)
            {
                Stretch(hint.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                StyleText(hint, 13f, TextAlignmentOptions.TopLeft, HintColor);
                hint.text = "按 Enter 或 H 确认退出";
            }
        }

        /// <summary>全部阶段完成的横幅（绿底深字，默认隐藏，由 <c>GameController</c> 打开）。</summary>
        private static void BuildCompletionBanner(Transform root, Report report)
        {
            RectTransform banner = EnsureUiChild(root, "CompletionBanner", report, out bool createdBanner);
            Image background = EnsureComponent<Image>(banner.gameObject);
            if (createdBanner)
            {
                Place(banner, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, -8f), new Vector2(400f, 24f));
                background.color = BannerColor;
                banner.gameObject.SetActive(false);
            }

            TMP_Text label = EnsureText(banner, "Label", report, out bool createdLabel);
            if (createdLabel)
            {
                Stretch(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                StyleText(label, 15f, TextAlignmentOptions.Center, BannerTextColor);
                label.text = "全部阶段完成";
            }
        }

        // ============================== Dialogue Canvas ==============================

        /// <summary>
        /// 对话层级：面板（立绘 / 头像 / 名字 / 台词 / 选项容器 + 按钮模板）。布局数值逐条沿用被删掉的
        /// <c>BuildDialoguePanel</c>，面板初始隐藏。
        /// </summary>
        private static void BuildDialogue(Transform root, Report report)
        {
            RectTransform panel = EnsureUiChild(root, "DialoguePanel", report, out bool createdPanel);
            Image panelBackground = EnsureComponent<Image>(panel.gameObject);
            if (createdPanel)
            {
                Place(panel, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(0f, 30f), new Vector2(-80f, 260f));
                panelBackground.color = DialoguePanelColor;
                panel.gameObject.SetActive(false);
            }

            // 立绘（左）
            Image portrait = EnsureComponent<Image>(EnsureUiChild(panel, "Portrait", report, out bool createdPortrait).gameObject);
            if (createdPortrait)
            {
                Place((RectTransform)portrait.transform, Vector2.zero, Vector2.zero, Vector2.zero,
                    new Vector2(16f, 16f), new Vector2(150f, 228f));
                portrait.color = Color.white;
                portrait.preserveAspect = true;
            }

            // 小头像
            Image avatar = EnsureComponent<Image>(EnsureUiChild(panel, "Avatar", report, out bool createdAvatar).gameObject);
            if (createdAvatar)
            {
                Place((RectTransform)avatar.transform, Vector2.zero, Vector2.zero, Vector2.zero,
                    new Vector2(182f, 172f), new Vector2(48f, 48f));
                avatar.color = Color.white;
                avatar.preserveAspect = true;
            }

            // 名字
            TMP_Text name = EnsureText(panel, "Name", report, out bool createdName);
            if (createdName)
            {
                Place(name.rectTransform, Vector2.zero, Vector2.zero, Vector2.zero,
                    new Vector2(242f, 196f), new Vector2(600f, 44f));
                StyleText(name, 34f, TextAlignmentOptions.BottomLeft, Color.white);
            }

            // 台词（拉伸：左让开立绘、右让开选项）
            TMP_Text line = EnsureText(panel, "Line", report, out bool createdLine);
            if (createdLine)
            {
                Stretch(line.rectTransform, Vector2.zero, Vector2.one, new Vector2(242f, 16f), new Vector2(-380f, -60f));
                StyleText(line, 30f, TextAlignmentOptions.TopLeft, Color.white);
            }

            // 选项容器（右侧竖排）
            RectTransform choices = EnsureUiChild(panel, "Choices", report, out bool createdChoices);
            VerticalLayoutGroup choicesLayout = EnsureComponent<VerticalLayoutGroup>(choices.gameObject);
            if (createdChoices)
            {
                Place(choices, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                    new Vector2(-16f, 16f), new Vector2(360f, 228f));
                choicesLayout.childControlHeight = true;
                choicesLayout.childControlWidth = true;
                choicesLayout.childForceExpandHeight = false;
                choicesLayout.childForceExpandWidth = true;
                choicesLayout.spacing = 8f;
                choicesLayout.childAlignment = TextAnchor.LowerRight;
            }

            BuildChoiceButtonTemplate(choices, report);
        }

        /// <summary>选项按钮模板：<see cref="DialogueDebugView.ShowChoices"/> 每次按它 Instantiate 一份，模板自身隐藏。</summary>
        private static void BuildChoiceButtonTemplate(Transform choices, Report report)
        {
            RectTransform template = EnsureUiChild(choices, "ChoiceButtonTemplate", report, out bool createdTemplate);
            Image background = EnsureComponent<Image>(template.gameObject);
            Button button = EnsureComponent<Button>(template.gameObject);
            LayoutElement element = EnsureComponent<LayoutElement>(template.gameObject);
            if (createdTemplate)
            {
                background.color = ButtonColor;
                button.targetGraphic = background;
                element.minHeight = 48f;
                element.preferredHeight = 48f;
                template.gameObject.SetActive(false);
            }

            TMP_Text label = EnsureText(template, "Label", report, out bool createdLabel);
            if (createdLabel)
            {
                Stretch(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 4f), new Vector2(-12f, -4f));
                StyleText(label, 28f, TextAlignmentOptions.Center, Color.white);
            }
        }

        // ============================== 中文字体 ==============================

        /// <summary>
        /// 中文 TMP 字体资产：把系统字体文件**拷进工程**（Assets/Data/Fonts/，ttf 走 LFS），再从工程内字体资产
        /// 建动态 TMP 字体。不走 <c>Font.CreateDynamicFontFromOSFont</c>——它在批处理模式下拿不到字体数据
        /// （"Unable to load font face"），且 TMP 3.0.7 无法把 OS 字体引用持久化（下次域重载 sourceFontFile 变 null）。
        /// 候选全失败只警告不报错（与「没有中文字体资产」的现状等价，中文退化成豆腐块）。
        /// </summary>
        private static TMP_FontAsset EnsureCjkFont(Report report)
        {
            TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (existing != null)
            {
                return existing;
            }

            // 只收 .ttf（.gitattributes 已把它纳入 LFS；.ttc 未纳入，不用）。
            string sourceName = null;
            string projectFontPath = null;
            for (int i = 0; i < OsFontCandidates.Length; i++)
            {
                string systemPath = "C:/Windows/Fonts/" + OsFontCandidates[i] + ".ttf";
                if (!System.IO.File.Exists(systemPath))
                {
                    continue;
                }

                sourceName = OsFontCandidates[i];
                projectFontPath = FontFolderPath + "/" + sourceName + ".ttf";
                if (AssetDatabase.LoadAssetAtPath<Font>(projectFontPath) == null)
                {
                    if (!AssetDatabase.IsValidFolder(FontFolderPath))
                    {
                        AssetDatabase.CreateFolder("Assets/Data", "Fonts");
                    }

                    System.IO.File.Copy(systemPath, projectFontPath);
                    AssetDatabase.ImportAsset(projectFontPath);
                }

                break;
            }

            Font sourceFont = projectFontPath != null
                ? AssetDatabase.LoadAssetAtPath<Font>(projectFontPath)
                : null;
            if (sourceFont == null)
            {
                Debug.LogWarning("[SceneUiScaffolder] 系统里找不到候选中文字体文件（"
                    + string.Join("、", OsFontCandidates) + ".ttf，C:/Windows/Fonts），跳过 TMP 字体资产与字体接线——"
                    + "中文会显示成豆腐块。");
                return null;
            }

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(sourceFont, FontSamplingPointSize,
                FontAtlasPadding, GlyphRenderMode.SDFAA, FontAtlasSize, FontAtlasSize, AtlasPopulationMode.Dynamic);
            if (fontAsset == null)
            {
                Debug.LogWarning("[SceneUiScaffolder] 从字体资产「" + projectFontPath + "」创建 TMP 字体资产失败，跳过字体接线。");
                return null;
            }

            fontAsset.name = "CjkDynamicFont";
            fontAsset.material.name = "CjkDynamicFont Atlas Material";
            fontAsset.atlasTextures[0].name = "CjkDynamicFont Atlas";

            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);

            // 图集与材质得跟着落盘（源字体本身已是工程资产，引用可序列化，不要再 AddObjectToAsset）。
            AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();

            report.Notice("中文字体取自系统字体文件「" + sourceName + ".ttf」（已拷入 " + projectFontPath + "）");
            return fontAsset;
        }

        /// <summary>把字体套到画布下所有 TMP 文本上（含隐藏面板里的读数）。</summary>
        private static void ApplyFont(GameObject canvasRoot, TMP_FontAsset font)
        {
            TMP_Text[] texts = canvasRoot.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i].font = font;
            }
        }

        // ============================== 接线 ==============================

        /// <summary>HUD 的全部引用：读数、鼠标跟随组、光照面板（强度滑块 + 数值读数 + 两行秒读数）、结算面板。</summary>
        private static void WireHud(SandboxHud hud, GameObject hudRoot, GameObject sandbox, Report report)
        {
            if (hud == null)
            {
                return;
            }

            Transform root = hudRoot.transform;
            var hudSo = new SerializedObject(hud);
            SetRef(hudSo, "_lightDriver", sandbox.GetComponent<ParticleLightDriver>(), report);
            SetRef(hudSo, "_probeText", ComponentAt<TMP_Text>(root, "ProbeText"), report);
            SetRef(hudSo, "_cursorHint", ComponentAt<RectTransform>(root, "CursorHint"), report);
            SetRef(hudSo, "_cursorSwatch", ComponentAt<Image>(root, "CursorHint/Swatch"), report);
            SetRef(hudSo, "_cursorLabel", ComponentAt<TMP_Text>(root, "CursorHint/Label"), report);
            SetRef(hudSo, "_highSpeedStatus", ComponentAt<TMP_Text>(root, "HighSpeedStatus"), report);
            SetRef(hudSo, "_reportPanel", ObjectAt(root, "ReportPanel"), report);
            SetRef(hudSo, "_reportBody", ComponentAt<TMP_Text>(root, "ReportPanel/Body"), report);
            SetRef(hudSo, "_reportVerdict", ComponentAt<TMP_Text>(root, "ReportPanel/Verdict"), report);
            SetRef(hudSo, "_lightPanel", ComponentAt<RectTransform>(root, "LightPanel"), report);
            SetRef(hudSo, "_buttonPanel", ComponentAt<RectTransform>(root, "ButtonPanel"), report);
            SetRef(hudSo, "_lightIntensity", ComponentAt<Slider>(root, "LightPanel/IntensityLabel/Intensity"), report);
            SetRef(hudSo, "_lightIntensityValue", ComponentAt<TMP_Text>(root, "LightPanel/IntensityLabel/IntensityValue"), report);
            SetRef(hudSo, "_lightLitTicks", ComponentAt<TMP_Text>(root, "LightPanel/LitTicksLabel/LitTicks"), report);
            SetRef(hudSo, "_lightDarkTicks", ComponentAt<TMP_Text>(root, "LightPanel/DarkTicksLabel/DarkTicks"), report);
            hudSo.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>对话视图的 11 个场景引用 + 字体（<c>_controller</c> 保持场景原样）。</summary>
        private static void WireDialogue(DialogueDebugView view, GameObject dialogueRoot, TMP_FontAsset font, Report report)
        {
            if (view == null)
            {
                return;
            }

            Transform root = dialogueRoot.transform;
            var viewSo = new SerializedObject(view);
            SetRef(viewSo, "_canvas", dialogueRoot.GetComponent<Canvas>(), report);
            SetRef(viewSo, "_dialoguePanel", ObjectAt(root, "DialoguePanel"), report);
            SetRef(viewSo, "_portraitImage", ComponentAt<Image>(root, "DialoguePanel/Portrait"), report);
            SetRef(viewSo, "_avatarImage", ComponentAt<Image>(root, "DialoguePanel/Avatar"), report);
            SetRef(viewSo, "_nameText", ComponentAt<TMP_Text>(root, "DialoguePanel/Name"), report);
            SetRef(viewSo, "_lineText", ComponentAt<TMP_Text>(root, "DialoguePanel/Line"), report);
            SetRef(viewSo, "_choiceRoot", ComponentAt<RectTransform>(root, "DialoguePanel/Choices"), report);
            SetRef(viewSo, "_choiceButtonTemplate", ComponentAt<Button>(root, "DialoguePanel/Choices/ChoiceButtonTemplate"), report);
            SetRef(viewSo, "_font", font, report);
            viewSo.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// 屏幕取景的三块：**显示相机画面的那张图**（<c>HUD Canvas/SandboxView</c>）、
        /// **把画面导进它的组件**（<see cref="CameraScreenFeed"/>）与**屏幕背板相机**（<c>ScreenBackdrop</c>）。
        /// </summary>
        /// <remarks>
        /// <para>背板相机存在的唯一理由：主相机被 <see cref="CameraScreenFeed"/> 改成画纹理之后，
        /// 屏幕上就没有相机在画了——Game 视图会显示「No cameras rendering」警告、背景也不被清理。
        /// 它只清屏（不画任何东西），因此不花任何渲染成本。</para>
        /// <para><b>幂等</b>：对象与组件缺才建；契约参数（显示图居中 + 2:1 + 画布最底层 + 不吃点击、背板的
        /// culling mask / 清屏方式 / 深度 / 不能画进纹理不放 AudioListener）每次重跑都保证，
        /// 尺寸与清屏色只在新建时套用（不覆盖手调）。</para>
        /// </remarks>
        private static void EnsureScreenFeed(Scene scene, GameObject hudRoot, Report report)
        {
            GameObject cameraObject = FindRoot(scene, "Main Camera");
            Camera mainCamera = cameraObject != null ? cameraObject.GetComponent<Camera>() : null;
            if (mainCamera == null)
            {
                report.Notice("场景里没有「Main Camera」，屏幕取景没接线。");
                return;
            }

            RawImage target = BuildSandboxView(hudRoot.transform, report);

            GameObject backdropObject = EnsureChild(null, "ScreenBackdrop", report, out bool createdBackdrop);
            Camera backdrop = EnsureComponent<Camera>(backdropObject);
            if (createdBackdrop)
            {
                backdropObject.transform.position = mainCamera.transform.position;
                backdrop.orthographic = true;
                backdrop.orthographicSize = mainCamera.orthographicSize;
                Color clear = mainCamera.backgroundColor;
                clear.a = 1f;
                backdrop.backgroundColor = clear;
            }

            // 契约：只清屏、最先画、不画进纹理、不带 AudioListener（否则与主相机的监听重复）。
            backdrop.clearFlags = CameraClearFlags.SolidColor;
            backdrop.cullingMask = 0;
            backdrop.orthographic = true;
            backdrop.depth = -100f;
            backdrop.targetTexture = null;
            AudioListener listener = backdropObject.GetComponent<AudioListener>();
            if (listener != null)
            {
                Object.DestroyImmediate(listener, true);
            }

            // 清屏色必须不透明：它会成为纹理的 alpha，透明纹理在 UI 混合下会被当成全透明（整张图看不见）。
            Color mainClear = mainCamera.backgroundColor;
            if (!Mathf.Approximately(mainClear.a, 1f))
            {
                mainClear.a = 1f;
                mainCamera.backgroundColor = mainClear;
                EditorUtility.SetDirty(mainCamera);
            }

            CameraScreenFeed feed = EnsureComponent<CameraScreenFeed>(cameraObject);
            var feedSo = new SerializedObject(feed);
            SetRef(feedSo, "_camera", mainCamera, report);
            if (target != null)
            {
                SetRef(feedSo, "_target", target, report);
            }

            feedSo.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// 显示相机画面的那张图（<c>HUD Canvas/SandboxView</c>）：**屏幕正中央**、宽高比 2:1
        /// （= 默认沙箱网格 200×100 的长宽比，见 <see cref="SandboxViewSize"/>），
        /// 是画布的**最底层**（其余 UI 都压在它上面），且不吃点击（纯显示层）。
        /// </summary>
        /// <remarks>
        /// 场景里它带着 <c>SandboxView.png</c> 占位图（编辑模式看到的就是它）；运行期
        /// <see cref="CameraScreenFeed"/> 会把纹理换成相机画面、并在退出时还原。找不到占位图资产时只提示、
        /// 仍然把图建出来（一张白板总好过没有显示层）。
        /// <para>已存在时不动 <c>anchoredPosition</c> / <c>sizeDelta</c>——尺寸是手调过的布局，
        /// 只有「居中 + 底层 + 不吃点击」这三条契约每次重跑都保证。</para>
        /// </remarks>
        private static RawImage BuildSandboxView(Transform hudRoot, Report report)
        {
            RectTransform rect = EnsureUiChild(hudRoot, "SandboxView", report, out bool created);
            RawImage raw = EnsureComponent<RawImage>(rect.gameObject);
            if (created)
            {
                Place(rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    Vector2.zero, SandboxViewSize);

                Texture2D placeholder = AssetDatabase.LoadAssetAtPath<Texture2D>(SandboxViewPlaceholderPath);
                if (placeholder == null)
                {
                    report.Notice("找不到占位图「" + SandboxViewPlaceholderPath + "」，显示图没有占位纹理（编辑模式是白板）。");
                }

                raw.texture = placeholder;
                raw.color = Color.white;
            }

            raw.raycastTarget = false;
            rect.SetSiblingIndex(0);
            return raw;
        }

        /// <summary>三颗按钮的 <c>onClick</c> 接到 <see cref="ParticleEditor"/> 的三个公开方法上（持久监听，落进场景）。</summary>
        private static void WireButtons(ParticleEditor editor, GameObject hudRoot, Report report)
        {
            if (editor == null)
            {
                report.Notice("「Particle Sandbox」上没有 ParticleEditor，三个按钮的 onClick 没接。");
                return;
            }

            Transform root = hudRoot.transform;
            BindButton(ComponentAt<Button>(root, "ButtonPanel/PauseButton"), editor,
                nameof(ParticleEditor.TogglePauseMenu), report);
            BindButton(ComponentAt<Button>(root, "ButtonPanel/LightButton"), editor,
                nameof(ParticleEditor.ToggleLightPanel), report);
            BindButton(ComponentAt<Button>(root, "ButtonPanel/HighSpeedButton"), editor,
                nameof(ParticleEditor.EnterHighSpeedMode), report);
        }

        /// <summary>
        /// 把按钮的 <c>onClick</c> 钉到 <paramref name="target"/> 的 <paramref name="methodName"/> 上。
        /// **幂等**：已有「同目标 + 同方法」的持久监听就直接跳过（不动用户在 Inspector 里改过的触发档位）；
        /// 没有才新增一条。用持久监听而不是运行期 <c>AddListener</c>——按钮是场景对象，接线也该落进场景。
        /// </summary>
        private static void BindButton(Button button, Object target, string methodName, Report report)
        {
            if (button == null)
            {
                return;
            }

            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                if (button.onClick.GetPersistentTarget(i) == target
                    && button.onClick.GetPersistentMethodName(i) == methodName)
                {
                    return;
                }
            }

            UnityEventTools.AddVoidPersistentListener(button.onClick,
                (UnityAction)System.Delegate.CreateDelegate(typeof(UnityAction), target, methodName));
            EditorUtility.SetDirty(button);
            report.Wired();
        }

        /// <summary>光照面板八颗按秒步进按钮的接线表：按钮路径（相对 <c>HUD Canvas</c>）、
        /// <see cref="SandboxHud"/> 上的方法名、秒参数（顺序与摆放一致）。</summary>
        private static readonly (string ButtonPath, string MethodName, int Seconds)[] LightStepBindings =
        {
            ("LightPanel/LitTicksStepLabel/LitMinus5", nameof(SandboxHud.AddLitSeconds), -5),
            ("LightPanel/LitTicksStepLabel/LitMinus1", nameof(SandboxHud.AddLitSeconds), -1),
            ("LightPanel/LitTicksStepLabel/LitPlus1", nameof(SandboxHud.AddLitSeconds), 1),
            ("LightPanel/LitTicksStepLabel/LitPlus5", nameof(SandboxHud.AddLitSeconds), 5),
            ("LightPanel/DarkTicksStepLabel/DarkMinus5", nameof(SandboxHud.AddDarkSeconds), -5),
            ("LightPanel/DarkTicksStepLabel/DarkMinus1", nameof(SandboxHud.AddDarkSeconds), -1),
            ("LightPanel/DarkTicksStepLabel/DarkPlus1", nameof(SandboxHud.AddDarkSeconds), 1),
            ("LightPanel/DarkTicksStepLabel/DarkPlus5", nameof(SandboxHud.AddDarkSeconds), 5),
        };

        /// <summary>
        /// 八颗按秒步进按钮的 <c>onClick</c> 接到 <see cref="SandboxHud.AddLitSeconds"/> /
        /// <see cref="SandboxHud.AddDarkSeconds"/> 上（整型参数的持久监听，落进场景）。
        /// **幂等**：先清掉按钮上的全部持久监听再加一条——「清后加」是既有先例（投放面板就是这么做的）。
        /// </summary>
        private static void WireLightStepButtons(SandboxHud hud, Transform root, Report report)
        {
            if (hud == null)
            {
                return;
            }

            for (int i = 0; i < LightStepBindings.Length; i++)
            {
                (string path, string methodName, int seconds) = LightStepBindings[i];
                Button button = ComponentAt<Button>(root, path);
                if (button == null)
                {
                    continue;
                }

                ClearPersistentListeners(button, report);
                UnityEventTools.AddIntPersistentListener(button.onClick,
                    (UnityAction<int>)System.Delegate.CreateDelegate(typeof(UnityAction<int>), hud, methodName), seconds);
                EditorUtility.SetDirty(button);
                report.Wired();
            }
        }

        /// <summary>面板子物体名 → 它选中的投放项显示名（<see cref="ParticleSelectionEntry.DisplayName"/>）。
        /// 固定顺序即 <c>_bindings</c> 的顺序；面板按钮的名字与这个表对不上时报错，不静默跳过。</summary>
        private static readonly (string Button, string SelectionName)[] ParticlePanelBindings =
        {
            ("Stone", "StoneParticle"),
            ("Sand", "SandParticle"),
            ("Dirt", "SoilParticle"),
            ("Water", "WaterParticle"),
            ("Fire", "FireParticle"),
            ("Tree_seed", "TreeSeedParticle"),
            ("Grass_seed", "GrassSeedParticle"),
            ("Rabbit", "兔子"),
            ("Fish", "鱼"),
            ("Bird", "鸟"),
        };

        /// <summary>
        /// 投放面板的接线：把 <c>ParticlePanel</c> 下的 10 颗投放按钮与「清除」按钮交给 <see cref="ParticleSelectionPanel"/>，
        /// 并**清掉这些按钮上场景里的持久监听**——点击改由组件在运行期绑定，留着旧的会重复触发
        /// （<c>Erease</c> 上就有一条从「高速」按钮复制过来的错的 <c>EnterHighSpeedMode</c>）。
        /// **幂等**：引用每次重写、持久监听清了就没了、组件缺才建。
        /// </summary>
        private static void WireParticlePanel(ParticleEditor editor, GameObject hudRoot, Report report)
        {
            Transform root = hudRoot.transform;
            RectTransform panel = EnsureUiChild(root, "ParticlePanel", report, out _);
            ParticleSelectionPanel selectionPanel = EnsureComponent<ParticleSelectionPanel>(panel.gameObject);

            var panelSo = new SerializedObject(selectionPanel);
            SetRef(panelSo, "_editor", editor, report);
            SetRef(panelSo, "_eraseButton", ComponentAt<Button>(root, "ParticlePanel/Erease"), report);

            SerializedProperty bindings = panelSo.FindProperty("_bindings");
            bindings.arraySize = ParticlePanelBindings.Length;
            for (int i = 0; i < ParticlePanelBindings.Length; i++)
            {
                (string buttonName, string selectionName) = ParticlePanelBindings[i];
                Button button = ComponentAt<Button>(root, "ParticlePanel/" + buttonName);

                SerializedProperty element = bindings.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("Button").objectReferenceValue = button;
                element.FindPropertyRelative("SelectionName").stringValue = selectionName;

                ClearPersistentListeners(button, report);
            }

            panelSo.ApplyModifiedPropertiesWithoutUndo();
            ClearPersistentListeners(ComponentAt<Button>(root, "ParticlePanel/Erease"), report);
        }

        /// <summary>清掉按钮上的全部持久监听（投放面板的点击由 <see cref="ParticleSelectionPanel"/> 运行期绑定、
        /// 光照步进按钮由 <see cref="WireLightStepButtons"/> 重新接一条——两者都是「先清后绑」）。</summary>
        private static void ClearPersistentListeners(Button button, Report report)
        {
            if (button == null)
            {
                return;
            }

            int count = button.onClick.GetPersistentEventCount();
            for (int i = count - 1; i >= 0; i--)
            {
                UnityEventTools.RemovePersistentListener(button.onClick, i);
            }

            if (count > 0)
            {
                EditorUtility.SetDirty(button);
                report.Notice("清掉了「" + button.name + "」上 " + count + " 条旧的 onClick 持久监听。");
            }
        }

        /// <summary>编辑器与流程控制器的两条 HUD 引用。</summary>
        private static void WireGameController(Scene scene, ParticleEditor editor, SandboxHud hud, Report report)
        {
            if (editor != null)
            {
                var editorSo = new SerializedObject(editor);
                SetRef(editorSo, "_hud", hud, report);
                editorSo.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                report.Notice("「Particle Sandbox」上没有 ParticleEditor，_hud 没接。");
            }

            GameController controller = FindComponent<GameController>(scene, "Game Controller");
            if (controller == null)
            {
                report.Notice("场景里没有「Game Controller」，_completionBanner 没接。");
                return;
            }

            var controllerSo = new SerializedObject(controller);
            SetRef(controllerSo, "_completionBanner", ObjectAt(scene, "HUD Canvas", "CompletionBanner"), report);
            controllerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        // ============================== 层级小工具 ==============================

        private static GameObject FindRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name)
                {
                    return roots[i];
                }
            }

            return null;
        }

        private static T FindComponent<T>(Scene scene, string rootName) where T : Component
        {
            GameObject root = FindRoot(scene, rootName);
            return root != null ? root.GetComponent<T>() : null;
        }

        /// <summary>按路径找场景对象（<paramref name="rootName"/> 之下），找不到就报错并返回 null。</summary>
        private static GameObject ObjectAt(Scene scene, string rootName, string path)
        {
            GameObject root = FindRoot(scene, rootName);
            if (root == null)
            {
                Debug.LogError("[SceneUiScaffolder] 场景里找不到根对象「" + rootName + "」。");
                return null;
            }

            return ObjectAt(root.transform, path);
        }

        private static GameObject ObjectAt(Transform root, string path)
        {
            Transform target = root.Find(path);
            if (target == null)
            {
                Debug.LogError("[SceneUiScaffolder] 场景层级里找不到「" + root.name + "/" + path + "」。");
                return null;
            }

            return target.gameObject;
        }

        private static T ComponentAt<T>(Transform root, string path) where T : Component
        {
            GameObject target = ObjectAt(root, path);
            if (target == null)
            {
                return null;
            }

            T component = target.GetComponent<T>();
            if (component == null)
            {
                Debug.LogError("[SceneUiScaffolder] 「" + root.name + "/" + path + "」上没有 " + typeof(T).Name + "。");
            }

            return component;
        }

        /// <summary>按名字取子物体（<paramref name="parent"/> 为 null = 场景根）；不存在才建。</summary>
        private static GameObject EnsureChild(Transform parent, string name, Report report, out bool created)
        {
            Transform existing = parent != null ? parent.Find(name) : FindRootGameObject(name);
            if (existing != null)
            {
                created = false;
                return existing.gameObject;
            }

            var child = new GameObject(name);
            if (parent != null)
            {
                child.transform.SetParent(parent, false);
            }

            created = true;
            report.Created(PathOf(child.transform));
            return child;
        }

        /// <summary>同 <see cref="EnsureChild"/>，但结果必须是 UI 对象：新建时带 RectTransform 组件，
        /// 已存在但不是 UI 对象（普通 Transform）时拒绝并提示——硬转会丢既有 Transform 数据。</summary>
        private static RectTransform EnsureUiChild(Transform parent, string name, Report report, out bool created)
        {
            Transform existing = parent != null ? parent.Find(name) : FindRootGameObject(name);
            if (existing != null)
            {
                if (existing is RectTransform existingRect)
                {
                    created = false;
                    return existingRect;
                }

                throw new System.InvalidOperationException(
                    "[SceneUiScaffolder]「" + PathOf(existing) + "」已存在但不是 UI 对象（没有 RectTransform）；"
                    + "请改名或删除它再跑脚手架。");
            }

            var child = new GameObject(name, typeof(RectTransform));
            if (parent != null)
            {
                child.transform.SetParent(parent, false);
            }

            created = true;
            report.Created(PathOf(child.transform));
            return (RectTransform)child.transform;
        }

        private static TMP_Text EnsureText(Transform parent, string name, Report report, out bool created)
        {
            RectTransform rect = EnsureUiChild(parent, name, report, out created);
            return EnsureComponent<TextMeshProUGUI>(rect.gameObject);
        }

        /// <summary>
        /// 光照强度滑块：背景 + 填充区（含 Fill）+ 滑块区（含 Handle）三段，全是**纯色 Image、无 sprite**
        /// （与面板里其它控件的平色风格一致）。三个 rect 接线（<c>fillRect</c> / <c>handleRect</c> / <c>targetGraphic</c>）
        /// 与范围 / 整数档 / 方向**每次都重挂**——它们是接线与面板契约，不是外观；滑块值只在首次创建时给默认值。
        /// </summary>
        private static Slider EnsureSlider(Transform parent, string name, Report report, out bool created)
        {
            RectTransform rect = EnsureUiChild(parent, name, report, out created);
            Slider slider = EnsureComponent<Slider>(rect.gameObject);

            // 背景：铺满整条轨道，收点击（拖动的指针事件由它进到滑块根上）。
            RectTransform background = EnsureUiChild(rect, "Background", report, out bool createdBackground);
            Image backgroundImage = EnsureComponent<Image>(background.gameObject);
            if (createdBackground)
            {
                Stretch(background, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                backgroundImage.color = SliderBackgroundColor;
            }

            // 填充区：竖直居中留出上下各 1/4，左右按背景内缩（右端让开滑块宽度）。
            RectTransform fillArea = EnsureUiChild(rect, "Fill Area", report, out bool createdFillArea);
            if (createdFillArea)
            {
                Stretch(fillArea, new Vector2(0f, 0.25f), new Vector2(1f, 0.75f),
                    new Vector2(3f, 0f), new Vector2(-12f, 0f));
            }

            RectTransform fill = EnsureUiChild(fillArea, "Fill", report, out bool createdFill);
            Image fillImage = EnsureComponent<Image>(fill.gameObject);
            if (createdFill)
            {
                Stretch(fill, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                fillImage.color = SliderFillColor;
            }

            // 滑块区：左右各让开半个滑块宽度，Handle 才能贴到两条端点上。
            RectTransform handleArea = EnsureUiChild(rect, "Handle Slide Area", report, out bool createdHandleArea);
            if (createdHandleArea)
            {
                Stretch(handleArea, Vector2.zero, Vector2.one, new Vector2(6f, 0f), new Vector2(-6f, 0f));
            }

            RectTransform handle = EnsureUiChild(handleArea, "Handle", report, out bool createdHandle);
            Image handleImage = EnsureComponent<Image>(handle.gameObject);
            if (createdHandle)
            {
                Place(handle, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                    Vector2.zero, new Vector2(10f, 14f));
                handleImage.color = SliderHandleColor;
            }

            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = LightIntensityMin;
            slider.maxValue = LightIntensityMax;
            slider.wholeNumbers = true;
            if (created)
            {
                slider.value = LightIntensityDefault;
            }

            return slider;
        }

        private static T EnsureComponent<T>(GameObject target) where T : Component
        {
            T component = target.GetComponent<T>();
            return component != null ? component : target.AddComponent<T>();
        }

        private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }

        /// <summary>拉伸锚点（anchorMin ≠ anchorMax 的轴）必须用 offsetMin/offsetMax 定位。</summary>
        private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void StyleText(TMP_Text text, float fontSize, TextAlignmentOptions alignment, Color color)
        {
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.enableWordWrapping = true;

            // 文字不参与点击：按钮靠背景图收事件，读数与说明更不该挡住画笔。
            text.raycastTarget = false;
        }

        /// <summary>把引用写进序列化字段；值真的变了才算接上一条线。</summary>
        private static void SetRef(SerializedObject target, string field, Object value, Report report)
        {
            SerializedProperty property = target.FindProperty(field);
            if (property == null)
            {
                Debug.LogError("[SceneUiScaffolder] " + target.targetObject.GetType().Name
                    + " 上没有序列化字段「" + field + "」。");
                return;
            }

            if (property.objectReferenceValue == value)
            {
                return;
            }

            property.objectReferenceValue = value;
            report.Wired();
        }

        private static Transform FindRootGameObject(string name)
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject root = FindRoot(scene, name);
            return root != null ? root.transform : null;
        }

        private static string PathOf(Transform transform)
        {
            string path = transform.name;
            for (Transform parent = transform.parent; parent != null; parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }

            return path;
        }

        private sealed class Report
        {
            private readonly List<string> _created = new List<string>();
            private readonly List<string> _notices = new List<string>();

            private int WiredCount { get; set; }

            public void Created(string path)
            {
                _created.Add(path);
            }

            public void Wired()
            {
                WiredCount++;
            }

            public void Notice(string message)
            {
                _notices.Add(message);
            }

            public string Describe()
            {
                string summary = "新建 " + _created.Count + " 个对象，接线 " + WiredCount + " 条引用";
                if (_created.Count > 0)
                {
                    summary += "；新建：" + string.Join("、", _created);
                }

                if (_notices.Count > 0)
                {
                    summary += "；" + string.Join("；", _notices);
                }

                return summary;
            }
        }
    }
}
