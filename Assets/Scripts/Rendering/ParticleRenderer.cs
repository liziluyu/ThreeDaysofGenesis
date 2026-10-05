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

        private Color32[] _pixels;
        private Texture2D _texture;
        private Mesh _mesh;
        private Material _runtimeMaterial;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private bool _targetFailed;

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

        private void OnDestroy()
        {
            // 这里不碰 _meshRenderer：销毁顺序不确定，子物体可能已被销毁。
            _pixels = null;

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
            var quad = new GameObject("ParticleGridQuad") { hideFlags = HideFlags.DontSaveInEditor };
            quad.transform.SetParent(transform, false);

            _meshFilter = quad.AddComponent<MeshFilter>();
            _meshRenderer = quad.AddComponent<MeshRenderer>();
            _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            _meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        private static Mesh BuildQuadMesh(int width, int height)
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
    }
}
