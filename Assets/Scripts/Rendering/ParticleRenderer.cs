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

        [Tooltip("留空则运行时用 URP 2D 的无光照 sprite 着色器创建。")]
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

        /// <summary>当前上屏用的纹理，未初始化时为 null。</summary>
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

            ParticlePixelBuffer.Fill(world, _pixels, _background);
            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
        }

        private void OnDestroy()
        {
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

            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }
        }

        private bool EnsureTarget(ParticleWorld world)
        {
            if (_texture != null && _texture.width == world.Width && _texture.height == world.Height)
            {
                return true;
            }

            if (_runtimeMaterial != null)
            {
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

            _pixels = new Color32[world.CellCount];
            _texture = new Texture2D(world.Width, world.Height, TextureFormat.RGBA32, false, false)
            {
                name = "ParticleGrid",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };

            Material material = _material != null ? _material : CreateRuntimeMaterial();
            if (material == null)
            {
                return false;
            }

            material.mainTexture = _texture;

            if (_meshRenderer == null)
            {
                CreateQuad();
            }

            _mesh = BuildQuadMesh(world.Width, world.Height);
            _meshFilter.sharedMesh = _mesh;
            _meshRenderer.sharedMaterial = material;

            if (_fitCameraOnInitialize)
            {
                FitCamera(world);
            }

            return true;
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

                _runtimeMaterial = new Material(shader)
                {
                    name = "ParticleGridMaterial",
                    hideFlags = HideFlags.DontSave,
                };
                return _runtimeMaterial;
            }

            Debug.LogError(
                "[ParticleRenderer] 找不到 URP 2D 无光照 sprite 着色器，无法渲染粒子网格。请在 Inspector 手动指定 Material。");
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
