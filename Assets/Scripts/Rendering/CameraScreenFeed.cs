using UnityEngine;
using UnityEngine.UI;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// **把相机画面导进 UI**：建一张与 Game 视图等像素的 <see cref="RenderTexture"/>，设为相机的
    /// <see cref="Camera.targetTexture"/>，再绑到场景里的 <see cref="RawImage"/> 上。相机因此不再直接画到屏幕，
    /// 屏幕只剩 overlay 画布——世界就在画布里的这张图里，随 UI 一起被摆放、遮挡与开关。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么非要绕这一圈</b>：overlay 画布永远画在相机输出之上，UI 里想显示「相机现在看到的画面」，
    /// 只能把相机渲染进纹理、再由 UI 显示这张纹理——画布挖不出能让相机透出来的洞。</para>
    /// <para><b>纹理尺寸 = Game 视图尺寸</b>：每帧核对 <see cref="Screen"/> 的像素尺寸，变了就重建。
    /// 尺寸跟着屏幕走，相机的宽高比就恒等于 Game 视图宽高比，于是取景（<c>ParticleRenderer.FitCamera</c>）、
    /// 平移夹取（<c>PanCamera</c> 按 <c>cam.aspect</c> 算半视宽）与鼠标 → 格子（<c>ParticleEditor</c> 用
    /// <c>ScreenPointToRay</c>）三条换算都与「相机直接画屏」逐位一致。换成固定尺寸的纹理资产就不行：
    /// Game 视图一旦不是那个宽高比，世界就会被拉伸。</para>
    /// <para><b>纹理只在运行期存在</b>（<see cref="HideFlags.DontSave"/>）——本组件**不在编辑器里跑**：
    /// 编辑模式下写 <c>targetTexture</c> 会把场景标脏（每次域重载都提示未保存，批处理测试会因「场景有未保存修改」
    /// 直接中止）。编辑模式因此是「相机直接画屏 + 这张图不显示」，看到的东西与运行期相同
    /// （这张图铺满屏幕、又是画布最底层的元素）。</para>
    /// <para><b>退出即还原</b>：<see cref="OnDisable"/> 清空相机的 <c>targetTexture</c>、把图恢复成场景里的
    /// 原始开关状态并释放纹理——组件一关，相机照旧直接画屏。</para>
    /// <para><b>配套</b>：相机全部改画纹理之后，屏幕上必须还有相机在画，否则 Game 视图会显示
    /// 「No cameras rendering」警告、背景也不被清理——场景里的 <c>ScreenBackdrop</c>（culling mask 0、
    /// 只清屏、depth 更小）就是干这个的。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CameraScreenFeed : MonoBehaviour
    {
        [Tooltip("画面来源相机。留空则取同物体上的 Camera → Camera.main；仍找不到就什么都不做（相机照旧直接画屏）。")]
        [SerializeField] private Camera _camera;

        [Tooltip("显示画面的 UI 图（场景里的占位图）。留空则取同物体上的 RawImage → 场景里第一张 RawImage；" +
                 "仍找不到就什么都不做——没有显示层就不该把相机从屏幕上拿走。" +
                 "绑定时会启用它（场景里默认禁用 ⇒ 编辑模式下不画）。")]
        [SerializeField] private RawImage _target;

        [Tooltip("纹理深度缓冲位数。世界是纯 2D（前后靠 sortingOrder），深度不是必需，但 URP 的透明 pass 会用到，16 位即可。")]
        [SerializeField, Range(0, 24)] private int _depthBits = 16;

        /// <summary>当前那张运行期纹理；null = 还没接管（编辑模式、或没接上相机 / 显示层）。</summary>
        private RenderTexture _texture;

        /// <summary>场景里那张图的原始开关状态（<see cref="Awake"/> 记一次，<see cref="OnDisable"/> 还原）。</summary>
        private bool _targetWasEnabled;

        private void Awake()
        {
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
            }

            if (_camera == null)
            {
                _camera = Camera.main;
            }

            if (_target == null)
            {
                _target = GetComponent<RawImage>();
            }

            if (_target == null)
            {
                _target = FindObjectOfType<RawImage>();
            }

            if (_target != null)
            {
                _targetWasEnabled = _target.enabled;
            }
        }

        private void OnEnable()
        {
            if (_camera == null || _target == null)
            {
                return;
            }

            // 先建一次：接管要发生在第一帧渲染之前，否则第一帧仍由相机直接画屏（画面会闪一下）。
            Rebuild(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
        }

        /// <summary>Game 视图尺寸变了就重建纹理（宽高比跟着屏幕走，见类注释）。</summary>
        private void Update()
        {
            if (_camera == null || _target == null)
            {
                return;
            }

            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            if (_texture == null || _texture.width != width || _texture.height != height)
            {
                Rebuild(width, height);
            }
        }

        private void OnDisable()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_target != null)
            {
                _target.texture = null;
                _target.enabled = _targetWasEnabled;
            }

            ReleaseTexture();
        }

        /// <summary>重建纹理并接管：相机画进它、图显示它。</summary>
        private void Rebuild(int width, int height)
        {
            ReleaseTexture();

            var texture = new RenderTexture(width, height, _depthBits,
                _camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default)
            {
                // 运行期对象，绝不能随场景落盘（与 ParticleRenderer 的运行时纹理 / 材质同一条约定）。
                name = "SandboxScreenFeed",
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            texture.Create();

            _texture = texture;
            _camera.targetTexture = texture;
            _target.texture = texture;
            _target.enabled = true;
        }

        private void ReleaseTexture()
        {
            if (_texture == null)
            {
                return;
            }

            _texture.Release();
            Destroy(_texture);
            _texture = null;
        }
    }
}
