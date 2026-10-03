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
    /// <para>上屏前按 <see cref="LightController.Daylight"/> 把整场亮度缩到
    /// <c>Lerp(_nightBrightness, 1, Daylight)</c>：白天上限、夜晚下限，过渡由光照周期给出。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleRenderer : MonoBehaviour
    {
        private static readonly string[] ShaderCandidates =
        {
            "Universal Render Pipeline/2D/Sprite-Unlit-Default",
            "Sprites/Default",
        };

        [SerializeField] private Color32 _background = new Color32(0, 0, 0, 255);

        [Header("昼夜亮度（光照系统的可见表现）")]
        [Tooltip("夜晚亮度倍率 0..1。白天恒为 1（现在的正常亮度），夜晚取本值；1 = 不随昼夜变暗。")]
        [SerializeField, Range(0f, 1f)] private float _nightBrightness = 0.5f;

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

            // 整场明暗：白天上限（1）、夜晚下限（_nightBrightness），过渡由 LightController.Daylight 给出。
            float brightness = Mathf.Lerp(_nightBrightness, 1f, world.Light.Daylight);
            ParticlePixelBuffer.Fill(world, _pixels, _background, brightness);

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
            var quad = new GameObject("ParticleGridQuad");
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
