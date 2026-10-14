using TapTap2026.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// 整个网格只用一个渲染对象：一张 quad + 一张 Point Filter 的 <see cref="Texture2D"/>。
    /// 不创建任何 per-particle 的 GameObject / SpriteRenderer / Transform。
    /// </summary>
    /// <remarks>
    /// 本组件只做「把 CPU 缓冲推上 GPU」，不关心模拟跑了几步。
    /// 上屏时机完全由调用方（<c>ParticleSimulationDriver</c>）决定——这是渲染与模拟解耦的接缝。
    /// <para><b>quad 与笔刷叠层都是场景对象</b>（<see cref="_meshFilter"/> / <see cref="_meshRenderer"/> /
    /// <see cref="_brushRenderer"/> 三个字段在 Inspector 里接线）：它们在编辑模式可见、可选中、可调参数，
    /// 方便直接看取景与叠层位置。只有编辑器里没接线时才在运行期自建（<see cref="CreateQuad"/> /
    /// <see cref="EnsureBrushOverlay"/>）——那条路是给「无接线构造」的测试与临时脚本兜底的，
    /// 建出来的对象一律 <see cref="HideFlags.DontSave"/>，绝不落进 .unity 文件。</para>
    /// <para>除网格 quad 外，本组件还持有**笔刷轮廓叠层**（<see cref="SetBrushOutline"/>，编辑器驱动）：
    /// 一层与格子逐格对齐的白像素圆 sprite，压在所有像素之上。</para>
    /// <para>上屏前按 <see cref="LightController.Daylight"/> 做昼夜表现：天空色在
    /// <see cref="NightBackground"/> / <see cref="DayBackground"/> 之间取色，粒子与云按
    /// <c>Lerp(NightBrightness, 1, Daylight)</c> 整体变暗。过渡长度由光照周期自动给出（下一时段长度的 1/10）。</para>
    /// <para>这三个昼夜参数**不在本组件上配置**：它们的唯一配置面是 <c>ParticleLightDriver</c>，
    /// 由它每帧推送过来（同 <c>ParticleSimulationDriver</c> 把热力学参数写回世界）。这里只保留代码默认值，
    /// 好在没有光源驱动器时也能正常上屏。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleRenderer : MonoBehaviour
    {
        private static readonly string[] ShaderCandidates =
        {
            "Universal Render Pipeline/2D/Sprite-Unlit-Default",
            "Sprites/Default",
        };

        /// <summary>白天天空色的代码默认值（配置面在 <c>ParticleLightDriver</c>）。</summary>
        public static readonly Color32 DefaultDayBackground = new Color32(0xCC, 0xE5, 0xFF, 255);

        /// <summary>夜晚天空色的代码默认值（配置面在 <c>ParticleLightDriver</c>）。</summary>
        public static readonly Color32 DefaultNightBackground = new Color32(0x2A, 0x22, 0x3A, 255);

        /// <summary>夜晚亮度倍率的代码默认值（配置面在 <c>ParticleLightDriver</c>）。</summary>
        public const float DefaultNightBrightness = 0.5f;

        private Color32 _dayBackground = DefaultDayBackground;
        private Color32 _nightBackground = DefaultNightBackground;
        private float _nightBrightness = DefaultNightBrightness;

        [Tooltip("留空则运行时用 URP 2D 的无光照 sprite 着色器创建。运行期一律实例化副本，不会改写本资产。")]
        [SerializeField] private Material _material;

        [Tooltip("留空则回退到 Camera.main。")]
        [SerializeField] private Camera _camera;

        [Tooltip("首次建立纹理时把相机摆正到刚好框住整张网格。")]
        [SerializeField] private bool _fitCameraOnInitialize = true;

        /// <summary>
        /// 笔刷轮廓叠层的深度：比世界 quad（0）与动物视图（−1）都更靠近相机，稳稳压在所有像素之上。
        /// 相机在 z = −10 朝 +z 看，因此这里取 −2（越小越靠前）。
        /// </summary>
        private const float BrushOverlayZ = -2f;

        private Color32[] _pixels;
        private Texture2D _texture;
        private Mesh _mesh;
        private Material _runtimeMaterial;
        private bool _targetFailed;

        [Tooltip("场景接线（世界 quad 上的 MeshFilter）；留空则运行时自建（HideFlags.DontSave，测试兜底）。")]
        [SerializeField] private MeshFilter _meshFilter;

        [Tooltip("场景接线（世界 quad 上的 MeshRenderer）；留空则运行时自建（HideFlags.DontSave，测试兜底）。")]
        [SerializeField] private MeshRenderer _meshRenderer;

        /// <summary>笔刷轮廓叠层的 sprite 视图（场景接线；未接线时运行期懒建）。</summary>
        [Tooltip("场景接线（笔刷轮廓叠层上的 SpriteRenderer）；留空则运行时自建（HideFlags.DontSave，测试兜底）。")]
        [SerializeField] private SpriteRenderer _brushRenderer;

        /// <summary>叠层纹理（边长 <c>2 * _brushCapacity + 1</c>、Point 过滤）与它对应的 sprite。</summary>
        private Texture2D _brushTexture;
        private Sprite _brushSprite;

        /// <summary>叠层的像素缓冲（行主序，与 <see cref="Texture2D.SetPixels32"/> 一致）。</summary>
        private Color32[] _brushPixels;

        /// <summary>叠层缓冲当前撑开的半径（= sprite 覆盖的半宽格数）。0 = 还没建。</summary>
        private int _brushCapacity;

        /// <summary>当前画进纹理的轮廓半径；−1 = 纹理内容无效（新建或换过尺寸），必须重画。</summary>
        private int _brushRadius = -1;

        /// <summary>当前上屏用的纹理，尚未就绪时为 null。</summary>
        public Texture2D Texture
        {
            get { return _texture; }
        }

        /// <summary>白天天空色（背景）。配置面在 <c>ParticleLightDriver</c>，由它推送。</summary>
        public Color32 DayBackground
        {
            get { return _dayBackground; }
            set { _dayBackground = value; }
        }

        /// <summary>夜晚天空色（背景）。配置面在 <c>ParticleLightDriver</c>，由它推送。</summary>
        public Color32 NightBackground
        {
            get { return _nightBackground; }
            set { _nightBackground = value; }
        }

        /// <summary>夜晚亮度倍率，夹在 [0, 1]；1 = 不随昼夜变暗。配置面在 <c>ParticleLightDriver</c>，由它推送。</summary>
        public float NightBrightness
        {
            get { return _nightBrightness; }
            set { _nightBrightness = Mathf.Clamp01(value); }
        }

        /// <summary>场景接线时套一遍 quad 的渲染设置（未接线的那条路在 <see cref="CreateQuad"/> 里套）。</summary>
        private void Awake()
        {
            if (_meshRenderer != null)
            {
                ApplyQuadRendererSettings(_meshRenderer);
            }
        }

        /// <summary>把 <paramref name="world"/> 的当前状态填进像素缓冲并上传一次。同一帧调用多次即上传多次。</summary>
        public void Present(ParticleWorld world)
        {
            if (world == null)
            {
                return;
            }

            if (!EnsureTarget(world))
            {
                return;
            }

            // 昼夜：天空色在夜 / 昼两色之间按日照度取色，粒子与云按同一进度整体变暗。
            // 过渡长度由 LightController.Daylight 自动给出（= 下一时段长度的 1/10）。
            float daylight = world.Light.Daylight;
            float brightness = Mathf.Lerp(_nightBrightness, 1f, daylight);
            Color32 background = Color32.Lerp(_nightBackground, _dayBackground, daylight);
            ParticlePixelBuffer.Fill(world, _pixels, background, brightness);

            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
        }

        /// <summary>
        /// 光标处的**笔刷轮廓叠层**：在以 (centerX, centerY) 为圆心的像素圆轮廓格（<see cref="BrushOutline"/>）
        /// 上叠一层白像素，标出编辑器左键会写到哪几格。<paramref name="radius"/> &lt; 1 视同撤下。
        /// </summary>
        /// <remarks>
        /// 叠层是**场景里的一个 sprite**，不是屏幕空间的白块：与粒子网格共用同一套格子坐标、1 像素 = 1 格
        /// （<c>pixelsPerUnit = 1</c> + pivot 居中，同 <see cref="CreatureRenderer"/> 的占位块），
        /// 所以与像素场景**逐格对齐**，相机缩放 / 平移都不需要换算；深度 <see cref="BrushOverlayZ"/> 外加
        /// <c>sortingOrder = 1</c> 保证它压在所有像素（世界 quad）与动物之上。
        /// <para>缓冲按半径**增长式**复用：同半径只挪位置，半径变化才重画纹理（≤ 21×21 像素）；
        /// 叠层不参与昼夜调光——白圈是操作提示，夜里也必须是白的。</para>
        /// </remarks>
        public void SetBrushOutline(int centerX, int centerY, int radius)
        {
            if (radius < 1)
            {
                HideBrushOutline();
                return;
            }

            EnsureBrushOverlay(radius);

            if (_brushRadius != radius)
            {
                // 换半径要整块重画：只补新环会留下旧环的残影（见 BrushOutline.Fill）。
                BrushOutline.Fill(_brushPixels, _brushCapacity, radius);
                _brushTexture.SetPixels32(_brushPixels);
                _brushTexture.Apply(false, false);
                _brushRadius = radius;
            }

            // pivot 居中 ⇒ 位置取格中心：sprite 因此恰好覆盖 [centerX − capacity, centerX + capacity] 这一方块，
            // 缓冲中心的 texel 正落在光标格上。
            _brushRenderer.transform.localPosition = new Vector3(centerX + 0.5f, centerY + 0.5f, BrushOverlayZ);
            _brushRenderer.enabled = true;
        }

        /// <summary>撤下笔刷轮廓叠层（还没建过就什么都不做）。</summary>
        public void HideBrushOutline()
        {
            if (_brushRenderer != null)
            {
                _brushRenderer.enabled = false;
            }
        }

        /// <summary>
        /// 按需建 / 撑开叠层的纹理与 sprite：<see cref="_brushRenderer"/> 未接线时先自建子物体，
        /// 容量够用（<see cref="_brushCapacity"/> ≥ radius）时原样复用，否则重建到新半径（纹理边长 = <c>2 * radius + 1</c>）。
        /// </summary>
        private void EnsureBrushOverlay(int radius)
        {
            if (_brushRenderer == null)
            {
                // 运行期生成的子物体一律 DontSave：绝不能被保存场景烘进 .unity 文件（同世界 quad）。
                var child = new GameObject("ParticleBrushOverlay") { hideFlags = HideFlags.DontSave };
                child.transform.SetParent(transform, false);

                _brushRenderer = child.AddComponent<SpriteRenderer>();
            }

            // 与同层其他 sprite（动物视图，order 0）拉开先后：即使相机的透明排序不看深度，叠层也在最上。
            // 场景接线的叠层也每帧补一遍——Inspector 里手改过 sortingOrder 就不会悄悄压不住世界 quad。
            _brushRenderer.sortingOrder = 1;

            if (_brushCapacity >= radius && _brushSprite != null)
            {
                return;
            }

            ReleaseBrushTexture();

            _brushCapacity = radius;
            int size = radius * 2 + 1;

            _brushPixels = new Color32[size * size];
            _brushTexture = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                name = "ParticleBrushOverlay",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };

            // pixelsPerUnit = 1 ⇒ 1 texel 恰好 1 格；pivot 居中 ⇒ 叠层以光标格中心为中心。
            _brushSprite = Sprite.Create(_brushTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 1f);
            _brushSprite.name = "ParticleBrushOverlay";
            _brushSprite.hideFlags = HideFlags.DontSave;
            _brushRenderer.sprite = _brushSprite;

            // 新纹理是空的：标成无效，逼调用方这一帧就重画（否则会显示一个空圆）。
            _brushRadius = -1;
        }

        /// <summary>释放叠层的纹理与 sprite（子物体与 <see cref="SpriteRenderer"/> 留着复用）。</summary>
        private void ReleaseBrushTexture()
        {
            if (_brushRenderer != null)
            {
                _brushRenderer.sprite = null;
            }

            if (_brushSprite != null)
            {
                Destroy(_brushSprite);
                _brushSprite = null;
            }

            if (_brushTexture != null)
            {
                Destroy(_brushTexture);
                _brushTexture = null;
            }

            _brushPixels = null;
            _brushRadius = -1;
        }

        private void OnDestroy()
        {
            // 这里不碰 _meshRenderer / _brushRenderer：销毁顺序不确定，子物体可能已被销毁。
            _pixels = null;

            // 纹理与 sprite 由本类在运行期创建，必须自己释放（子物体随宿主一起销毁）。
            if (_brushSprite != null)
            {
                Destroy(_brushSprite);
                _brushSprite = null;
            }

            if (_brushTexture != null)
            {
                Destroy(_brushTexture);
                _brushTexture = null;
            }

            _brushPixels = null;

            if (_runtimeMaterial != null)
            {
                _runtimeMaterial.mainTexture = null;
                Destroy(_runtimeMaterial);
                _runtimeMaterial = null;
            }

            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }

            if (_mesh != null)
            {
                Destroy(_mesh);
                _mesh = null;
            }
        }

        /// <summary>纹理尺寸匹配、且网格与材质都已挂上，才算可以上屏。</summary>
        private bool IsTargetReady(ParticleWorld world)
        {
            return _texture != null
                && _mesh != null
                && _meshRenderer != null
                && _meshRenderer.sharedMaterial != null
                && _texture.width == world.Width
                && _texture.height == world.Height;
        }

        private bool EnsureTarget(ParticleWorld world)
        {
            if (IsTargetReady(world))
            {
                return true;
            }

            // 上次因找不到 shader 而失败，且调用方仍未提供材质：不重试、也不重复刷日志。
            // 一旦在 Inspector 或代码里补上 _material，下一次 Present 会自动重建。
            if (_targetFailed && _material == null)
            {
                return false;
            }

            ReleaseTarget();

            _pixels = new Color32[world.CellCount];
            _texture = new Texture2D(world.Width, world.Height, TextureFormat.RGBA32, false, false)
            {
                name = "ParticleGrid",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };

            // 运行期材质永远是我们自己的副本：_material 是可能被多个对象共用的资产，绝不能被改写。
            Material material = _material != null ? new Material(_material) : CreateRuntimeMaterial();
            if (material == null)
            {
                _targetFailed = true;
                ReleaseTarget();
                return false;
            }

            _runtimeMaterial = material;

            if (_meshRenderer == null)
            {
                CreateQuad();
            }

            _mesh = BuildQuadMesh(world.Width, world.Height);
            _meshFilter.sharedMesh = _mesh;
            _meshRenderer.sharedMaterial = material;
            material.mainTexture = _texture;

            if (_fitCameraOnInitialize)
            {
                FitCamera(world);
            }

            return true;
        }

        /// <summary>释放所有运行期渲染资源，保留 <see cref="_meshRenderer"/><see cref="_meshFilter"/> 以便复用。</summary>
        private void ReleaseTarget()
        {
            _pixels = null;

            if (_texture != null)
            {
                // 先断开引用，再销毁纹理：否则材质会持有 Missing 引用。
                if (_runtimeMaterial != null)
                {
                    _runtimeMaterial.mainTexture = null;
                }

                if (_meshRenderer != null)
                {
                    _meshRenderer.sharedMaterial = null;
                }

                Destroy(_texture);
                _texture = null;
            }

            if (_runtimeMaterial != null)
            {
                Destroy(_runtimeMaterial);
                _runtimeMaterial = null;
            }

            if (_mesh != null)
            {
                Destroy(_mesh);
                _mesh = null;
            }
        }

        private Material CreateRuntimeMaterial()
        {
            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                Shader shader = Shader.Find(ShaderCandidates[i]);
                if (shader == null)
                {
                    continue;
                }

                return new Material(shader)
                {
                    name = "ParticleGridMaterial",
                    hideFlags = HideFlags.DontSave,
                };
            }

            Debug.LogError(
                "[ParticleRenderer] 找不到 URP 2D 无光照 sprite 着色器，已停止上屏。请在 Inspector 指定 Material 或在 Graphics 设置里保留该 shader。");
            return null;
        }

        private void CreateQuad()
        {
            // 整条渲染链（mesh / 材质 / 纹理）都是运行期生成的，四边形的 GameObject 同理：绝不能随场景落盘。
            // 否则在编辑器里调用过 Present（工具、测试、暂停中涂改）之后保存场景，就会把一个空壳
            // ParticleGridQuad 烘进 .unity 文件（它不被任何序列化字段引用，纯粹是垃圾）。
            // 旗标与同链的 mesh / 材质 / 纹理一致：HideFlags.DontSave（= DontSaveInEditor | DontSaveInBuild）。
            var quad = new GameObject("ParticleGridQuad") { hideFlags = HideFlags.DontSave };
            quad.transform.SetParent(transform, false);

            _meshFilter = quad.AddComponent<MeshFilter>();
            _meshRenderer = quad.AddComponent<MeshRenderer>();
            ApplyQuadRendererSettings(_meshRenderer);
        }

        /// <summary>
        /// quad 的渲染设置：不投影 / 不接收阴影、不采光探针与反射探针、不生成运动矢量。
        /// 粒子网格是一张整屏自发光像素画布——参与光照与运动矢量只会平白拖慢，还会把观感带偏。
        /// 场景接线的 quad（<see cref="Awake"/>）与运行期自建的 quad（<see cref="CreateQuad"/>）走同一份设置，
        /// 免得接线与兜底两条路的观感不一致。
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
        /// 世界 quad 的网格：<paramref name="width"/> × <paramref name="height"/> 个 1×1 单位格的平面。
        /// <c>public static</c> 供编辑器脚手架（<c>SceneUiScaffolder</c>）在场景里生成同一份预览网格，
        /// 免得两处再抄一遍顶点 / UV / 三角形。<b>hideFlags 与释放由调用方负责</b>：本方法只建网格。
        /// </summary>
        public static Mesh BuildQuadMesh(int width, int height)
        {
            var mesh = new Mesh
            {
                name = "ParticleGridQuad",
                hideFlags = HideFlags.DontSave,
            };

            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(width, 0f, 0f),
                new Vector3(0f, height, 0f),
                new Vector3(width, height, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void FitCamera(ParticleWorld world)
        {
            Camera cam = _camera != null ? _camera : Camera.main;
            if (cam == null)
            {
                return;
            }

            cam.orthographic = true;
            cam.orthographicSize = world.Height * 0.5f;
            cam.transform.position = new Vector3(world.Width * 0.5f, world.Height * 0.5f, -10f);
        }

        /// <summary>
        /// 把相机沿网格水平方向平移 <paramref name="deltaCells"/> 格（正 = 网格 +x 方向），
        /// 并把视口夹回沙箱内——相机中心的 x 恒在 <c>[半视宽, Width − 半视宽]</c>；
        /// 视口比网格还宽（半视宽 &gt; Width/2）时恒居中，平移无效。
        /// 与本类的取景约定一致（<see cref="FitCamera"/>）：网格局部坐标即世界坐标。
        /// </summary>
        /// <remarks>
        /// 只改 x——相机的竖直框取（由 <see cref="FitCamera"/> 定的 orthographicSize 与位置 y / z）不动，
        /// 也不会触碰 <c>Cell[]</c> 或重建纹理（相机移动与模拟、上传完全解耦）。
        /// <para>无输入时也照常调（<paramref name="deltaCells"/> = 0）：Game 视图宽高比变化会让原本在界内的相机
        /// 落在界外，靠这一次夹取把它拉回沙箱内。相机缺失或不是正交相机时什么都不做。</para>
        /// </remarks>
        public void PanCamera(ParticleWorld world, float deltaCells)
        {
            if (world == null)
            {
                return;
            }

            Camera cam = _camera != null ? _camera : Camera.main;

            // 正交相机才有「半视宽」这个与深度无关的常数；透视相机的水平可见范围随 z 变化，
            // 而本项目的取景约定就是正交（FitCamera），因此这里不猜、直接跳过。
            if (cam == null || !cam.orthographic)
            {
                return;
            }

            float halfView = cam.orthographicSize * cam.aspect;
            float minX = halfView;
            float maxX = world.Width - halfView;

            Vector3 position = cam.transform.position;
            position.x = minX > maxX
                ? world.Width * 0.5f
                : Mathf.Clamp(position.x + deltaCells, minX, maxX);
            cam.transform.position = position;
        }
    }
}
