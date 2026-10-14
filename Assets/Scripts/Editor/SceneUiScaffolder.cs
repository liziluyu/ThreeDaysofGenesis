using System.Collections.Generic;
using TMPro;
using TapTap2026.Game;
using TapTap2026.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace TapTap2026.EditorTools
{
    /// <summary>
    /// 场景 UI 脚手架：把原本「运行期动态创建」的那批对象补齐到 <c>SampleScene</c> 里并接好线——
    /// 粒子网格 quad 与笔刷叠层、主相机取景、EventSystem、HUD Canvas（<see cref="SandboxHud"/> 的层级）、
    /// Dialogue Canvas（<see cref="DialogueDebugView"/> 的层级）、中文字体资产。
    /// </summary>
    /// <remarks>
    /// <para><b>幂等</b>：对象一律按名字 / 路径找，缺才建。**已存在对象的 RectTransform、文本内容与其它外观参数
    /// 一概不动**——用户手调过的布局、文案、字号不会被跑一次脚手架抹掉。每次重跑都会做的只有：
    /// 引用接线、预览 mesh 的尺寸刷新、笔刷叠层的 <c>sortingOrder</c>、相机的编辑态取景、
    /// 两个 Canvas 的渲染模式 / 排序 / 缩放参数（层级契约，见 <see cref="BuildCanvas"/>）与字体套用。</para>
    /// <para>用法：菜单 <c>Tools/TapTap2026/Scene UI Setup（补缺失+接线)</c>，
    /// 或批处理 <c>-executeMethod TapTap2026.EditorTools.SceneUiScaffolder.Run</c>。
    /// 批处理必须在 Unity 编辑器关着的时候跑。</para>
    /// </remarks>
    public static class SceneUiScaffolder
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string DataFolder = "Assets/Data";
        private const string PreviewMeshPath = "Assets/Data/SandboxPreviewMesh.asset";
        private const string PreviewMaterialPath = "Assets/Data/SandboxPreview.mat";
        private const string FontAssetPath = "Assets/Data/CjkDynamicFont.asset";
        private const string FontFolderPath = "Assets/Data/Fonts";

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

        private static readonly Color DialoguePanelColor = new Color(0.05f, 0.06f, 0.09f, 0.85f);
        private static readonly Color ReviewPanelColor = new Color(0.03f, 0.04f, 0.07f, 0.96f);
        private static readonly Color ReportPanelColor = new Color(0.05f, 0.06f, 0.09f, 0.92f);
        private static readonly Color ButtonColor = new Color(0.16f, 0.2f, 0.28f, 0.95f);
        private static readonly Color BannerColor = new Color(0.25f, 0.9f, 0.35f, 0.9f);
        private static readonly Color HintColor = new Color(0.75f, 0.8f, 0.9f, 1f);
        private static readonly Color BannerTextColor = new Color(0.05f, 0.06f, 0.09f, 1f);

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
            BuildDialogue(dialogueRoot.transform, DefaultReviewHint, report);

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

        /// <summary>场景必须有 EventSystem：对话按钮、光照输入框、滚动回顾都靠它收事件（组件不再自建）。</summary>
        private static void EnsureEventSystem(Scene scene, Report report)
        {
            GameObject eventSystemObject = FindRoot(scene, "EventSystem");
            if (eventSystemObject == null)
            {
                eventSystemObject = EnsureChild(null, "EventSystem", report, out bool _);
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
        /// HUD 层级：光标读数 / 鼠标跟随组 / 高速读数 / 光照面板 / 高速结算面板 / 通关横幅。
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
        }

        /// <summary>右上角光照面板：标题 + 三行「标签 + 输入框」，整体挂 CanvasGroup 供 SandboxHud 变灰 / 屏蔽点击。</summary>
        private static void BuildLightPanel(Transform root, Report report)
        {
            RectTransform panel = EnsureUiChild(root, "LightPanel", report, out bool createdPanel);
            Image background = EnsureComponent<Image>(panel.gameObject);
            EnsureComponent<CanvasGroup>(panel.gameObject);
            if (createdPanel)
            {
                Place(panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                    new Vector2(-6f, -4f), new Vector2(240f, 96f));
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

            BuildLightRow(panel, "IntensityLabel", "Intensity", "强度", 0, report);
            BuildLightRow(panel, "LitTicksLabel", "LitTicks", "亮刻", 1, report);
            BuildLightRow(panel, "DarkTicksLabel", "DarkTicks", "暗刻", 2, report);
        }

        /// <summary>光照面板的一行：左侧标签 + 右侧输入框。行顶 y = −26 − 22 × <paramref name="row"/>。</summary>
        private static void BuildLightRow(Transform panel, string labelName, string fieldName, string label, int row, Report report)
        {
            float y = -26f - 22f * row;

            TMP_Text rowLabel = EnsureText(panel, labelName, report, out bool createdLabel);
            if (createdLabel)
            {
                Place(rowLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(10f, y), new Vector2(40f, 20f));
                StyleText(rowLabel, 15f, TextAlignmentOptions.Left, Color.white);
                rowLabel.text = label;
            }

            TMP_InputField field = EnsureInputField(panel, fieldName, report, out bool createdField);
            if (createdField)
            {
                Place((RectTransform)field.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(52f, y), new Vector2(178f, 21f));
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

        /// <summary>回顾面板提示文案的默认值（场景里可直接编辑；这里只是首次创建时的兜底）。</summary>
        private const string DefaultReviewHint = "Review  (Tab/Esc to close, scroll wheel to browse)";

        /// <summary>
        /// 对话层级：面板（立绘 / 头像 / 名字 / 台词 / 选项容器 + 按钮模板）与回顾面板（提示 + 滚动正文）。
        /// 布局数值逐条沿用被删掉的 <c>BuildDialoguePanel</c> / <c>BuildReviewPanel</c>，两个面板初始隐藏。
        /// </summary>
        private static void BuildDialogue(Transform root, string reviewHint, Report report)
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
            BuildReviewPanel(root, reviewHint, report);
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

        /// <summary>回顾面板：顶部提示 + 滚动正文（ScrollRect + 遮罩视口 + 高度自适应的内容）。</summary>
        private static void BuildReviewPanel(Transform root, string reviewHint, Report report)
        {
            RectTransform panel = EnsureUiChild(root, "ReviewPanel", report, out bool createdPanel);
            Image background = EnsureComponent<Image>(panel.gameObject);
            if (createdPanel)
            {
                Stretch(panel, Vector2.zero, Vector2.one, new Vector2(120f, 90f), new Vector2(-120f, -90f));
                background.color = ReviewPanelColor;
                panel.gameObject.SetActive(false);
            }

            TMP_Text hint = EnsureText(panel, "Hint", report, out bool createdHint);
            if (createdHint)
            {
                Stretch(hint.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(24f, -56f), new Vector2(-24f, -16f));
                StyleText(hint, 24f, TextAlignmentOptions.TopLeft, HintColor);
                hint.text = reviewHint;
            }

            RectTransform scroll = EnsureUiChild(panel, "Scroll", report, out bool createdScroll);
            ScrollRect scrollRect = EnsureComponent<ScrollRect>(scroll.gameObject);
            if (createdScroll)
            {
                Stretch(scroll, Vector2.zero, Vector2.one, new Vector2(24f, 24f), new Vector2(-24f, -70f));
                scrollRect.horizontal = false;
                scrollRect.vertical = true;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
            }

            RectTransform viewport = EnsureUiChild(scroll, "Viewport", report, out bool createdViewport);
            EnsureComponent<RectMask2D>(viewport.gameObject);
            if (createdViewport)
            {
                Stretch(viewport, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            }

            TMP_Text content = EnsureText(viewport, "Content", report, out bool createdContent);
            ContentSizeFitter fitter = EnsureComponent<ContentSizeFitter>(content.gameObject);
            if (createdContent)
            {
                Place(content.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                    Vector2.zero, Vector2.zero);
                StyleText(content, 28f, TextAlignmentOptions.TopLeft, Color.white);

                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            // 滚动视图的两个端点引用（每次都挂：丢了整块回顾就不滚了）。
            scrollRect.viewport = viewport;
            scrollRect.content = content.rectTransform;
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

        /// <summary>把字体套到画布下所有 TMP 文本上（含隐藏面板与输入框的 <c>textComponent</c>）。</summary>
        private static void ApplyFont(GameObject canvasRoot, TMP_FontAsset font)
        {
            TMP_Text[] texts = canvasRoot.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i].font = font;
            }
        }

        // ============================== 接线 ==============================

        /// <summary>HUD 的全部引用：读数、鼠标跟随组、光照面板与三个输入框、结算面板。</summary>
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
            SetRef(hudSo, "_lightIntensity", ComponentAt<TMP_InputField>(root, "LightPanel/Intensity"), report);
            SetRef(hudSo, "_lightLitTicks", ComponentAt<TMP_InputField>(root, "LightPanel/LitTicks"), report);
            SetRef(hudSo, "_lightDarkTicks", ComponentAt<TMP_InputField>(root, "LightPanel/DarkTicks"), report);
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
            SetRef(viewSo, "_reviewPanel", ObjectAt(root, "ReviewPanel"), report);
            SetRef(viewSo, "_portraitImage", ComponentAt<Image>(root, "DialoguePanel/Portrait"), report);
            SetRef(viewSo, "_avatarImage", ComponentAt<Image>(root, "DialoguePanel/Avatar"), report);
            SetRef(viewSo, "_nameText", ComponentAt<TMP_Text>(root, "DialoguePanel/Name"), report);
            SetRef(viewSo, "_lineText", ComponentAt<TMP_Text>(root, "DialoguePanel/Line"), report);
            SetRef(viewSo, "_choiceRoot", ComponentAt<RectTransform>(root, "DialoguePanel/Choices"), report);
            SetRef(viewSo, "_choiceButtonTemplate", ComponentAt<Button>(root, "DialoguePanel/Choices/ChoiceButtonTemplate"), report);
            SetRef(viewSo, "_reviewText", ComponentAt<TMP_Text>(root, "ReviewPanel/Scroll/Viewport/Content"), report);
            SetRef(viewSo, "_reviewScroll", ComponentAt<ScrollRect>(root, "ReviewPanel/Scroll"), report);
            SetRef(viewSo, "_font", font, report);
            viewSo.ApplyModifiedPropertiesWithoutUndo();
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
        /// 光照值输入框：自身 Image（背景）+ TMP_InputField + 子文本（四向拉伸、左右各留 4px）。
        /// <c>textComponent</c> 每次都重挂——它是接线，不是布局。
        /// </summary>
        private static TMP_InputField EnsureInputField(Transform parent, string name, Report report, out bool created)
        {
            RectTransform rect = EnsureUiChild(parent, name, report, out created);
            Image background = EnsureComponent<Image>(rect.gameObject);
            TMP_InputField field = EnsureComponent<TMP_InputField>(rect.gameObject);
            if (created)
            {
                background.color = ButtonColor;
            }

            RectTransform textRect = EnsureUiChild(rect, "Text", report, out bool createdText);
            TMP_Text text = EnsureComponent<TextMeshProUGUI>(textRect.gameObject);
            if (createdText)
            {
                Stretch(textRect, Vector2.zero, Vector2.one, new Vector2(4f, 0f), new Vector2(-4f, 0f));
                StyleText(text, 15f, TextAlignmentOptions.Left, Color.white);
            }

            field.textComponent = text;
            return field;
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
