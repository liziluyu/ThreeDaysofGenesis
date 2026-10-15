using UnityEngine;
using UnityEngine.UI;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// **把相机画面导进 UI**：建一张与场景里那张显示图**等像素**的 <see cref="RenderTexture"/>，设为相机的
    /// <see cref="Camera.targetTexture"/>，再绑到 <see cref="RawImage"/> 上。相机因此不再直接画到屏幕，
    /// 屏幕只剩 overlay 画布——世界就在画布里的这张图里，随 UI 一起被摆放、遮挡与开关。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么非要绕这一圈</b>：overlay 画布永远画在相机输出之上，UI 里想显示「相机现在看到的画面」，
    /// 只能把相机渲染进纹理、再由 UI 显示这张纹理——画布挖不出能让相机透出来的洞。</para>
    /// <para><b>纹理尺寸 = 显示图的屏幕尺寸</b>（每帧核对，变了就重建）。纹理宽高比因此恒等于那张图的宽高比，
    /// 相机的宽高比（<c>Camera.aspect</c> 在有 targetTexture 时取纹理的）也就恒等于它——所以：
    /// 世界在纹理里的成像与 UI 里那张图的矩形**逐像素对应**，格子是方的、没有多余的边。
    /// 显示图若是 2:1（沙箱网格 200×100 的长宽比），相机视野恰好就是整张网格（<c>orthographicSize = 50</c>
    /// ⇒ 竖 100 格、横 100 × 2 = 200 格），平移因此自然锁死居中。</para>
    /// <para><b>输入必须换算</b>：相机画进纹理之后，<c>Camera.ScreenPointToRay</c> 吃的是**纹理像素**（原点左下）
    /// 而不是屏幕像素（实测：纹理中心 → 视野中心，<c>Camera.pixelRect</c> = 纹理矩形）。显示图不再铺满屏幕时，
    /// 两者相差一个平移（本组件把纹理做成与图等像素，所以只差平移、不差缩放）——调用方用
    /// <see cref="ToCameraPixel"/> 把指针位置换算过去即可。</para>
    /// <para><b>纹理只在运行期存在</b>（<see cref="HideFlags.DontSave"/>）——本组件**不在编辑器里跑**：
    /// 编辑模式下写 <c>targetTexture</c> 会把场景标脏（每次域重载都提示未保存，批处理测试会因「场景有未保存修改」
    /// 直接中止）。编辑模式因此是「相机直接画屏 + 显示图保持场景里的占位图」，运行期才换成实时画面。</para>
    /// <para><b>退出即还原</b>：<see cref="OnDisable"/> 清空相机的 <c>targetTexture</c>、把显示图恢复成场景里的
    /// 原始纹理与开关状态并释放纹理——组件一关，相机照旧直接画屏、图照旧显示占位图。</para>
    /// <para><b>配套</b>：相机全部改画纹理之后，屏幕上必须还有相机在画，否则 Game 视图会显示
    /// 「No cameras rendering」警告、背景也不被清理——场景里的 <c>ScreenBackdrop</c>（culling mask 0、
    /// 只清屏、depth 更小）就是干这个的。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CameraScreenFeed : MonoBehaviour
    {
        /// <summary>纹理边长上限：显示图被拉得再大也不至于分配一张荒唐的纹理（正常远小于此）。</summary>
        private const int MaxTextureSize = 4096;

        [Tooltip("画面来源相机。留空则取同物体上的 Camera → Camera.main；仍找不到就什么都不做（相机照旧直接画屏）。")]
        [SerializeField] private Camera _camera;

        [Tooltip("显示画面的 UI 图（场景里的占位图）。留空则取同物体上的 RawImage → 场景里第一张 RawImage；" +
                 "仍找不到就什么都不做——没有显示层就不该把相机从屏幕上拿走。" +
                 "接管时会启用它并把纹理换成实时画面，未接管时显示它自带的占位纹理（编辑模式下看到的就是它）。")]
        [SerializeField] private RawImage _target;

        [Tooltip("纹理深度缓冲位数。世界是纯 2D（前后靠 sortingOrder），深度不是必需，但 URP 的透明 pass 会用到，16 位即可。")]
        [SerializeField, Range(0, 24)] private int _depthBits = 16;

        /// <summary>当前那张运行期纹理；null = 还没接管（编辑模式、或没接上相机 / 显示层）。</summary>
        private RenderTexture _texture;

        /// <summary>显示图在场景里的原始开关状态与占位纹理（<see cref="Awake"/> 记一次，<see cref="OnDisable"/> 还原）。</summary>
        private bool _targetWasEnabled;
        private Texture _targetWasTexture;

        private readonly Vector3[] _corners = new Vector3[4];

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
                _targetWasTexture = _target.texture;
            }
        }

        private void OnEnable()
        {
            if (_camera == null || _target == null)
            {
                return;
            }

            // 先建一次：接管要发生在第一帧渲染之前，否则第一帧仍由相机直接画屏（画面会闪一下）。
            RebuildForTarget();
        }

        /// <summary>显示图的屏幕尺寸变了（Game 视图变化 / 布局改动）就重建纹理。</summary>
        private void Update()
        {
            if (_camera == null || _target == null)
            {
                return;
            }

            if (!TryGetTargetSize(out int width, out int height))
            {
                return;
            }

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
                _target.texture = _targetWasTexture;
                _target.enabled = _targetWasEnabled;
            }

            ReleaseTexture();
        }

        /// <summary>
        /// 屏幕像素坐标（原点左下）→ 相机渲染目标的像素坐标（原点左下）。
        /// 相机画进纹理后 <c>ScreenPointToRay</c> 吃纹理像素，因此指针位置要先过这一道（见类注释）。
        /// 未接管（编辑模式，相机直接画屏）时原样返回——那时屏幕像素就是相机像素。
        /// </summary>
        public Vector2 ToCameraPixel(Vector2 screenPoint)
        {
            if (_texture == null || !TryGetDisplayRect(out Vector2 origin, out Vector2 size)
                || size.x <= 0f || size.y <= 0f)
            {
                return screenPoint;
            }

            return new Vector2(
                (screenPoint.x - origin.x) * _texture.width / size.x,
                (screenPoint.y - origin.y) * _texture.height / size.y);
        }

        /// <summary>重建纹理并接管：相机画进它、显示图显示它。</summary>
        private void RebuildForTarget()
        {
            if (TryGetTargetSize(out int width, out int height))
            {
                Rebuild(width, height);
            }
        }

        /// <summary>显示图的屏幕像素尺寸（宽高比即纹理宽高比；纹理高按宽等比取整，免得取整把比例带偏）。</summary>
        private bool TryGetTargetSize(out int width, out int height)
        {
            width = 1;
            height = 1;

            if (!TryGetDisplayRect(out _, out Vector2 size) || size.x <= 0f || size.y <= 0f)
            {
                return false;
            }

            width = Mathf.Clamp(Mathf.RoundToInt(size.x), 1, MaxTextureSize);
            height = Mathf.Clamp(Mathf.RoundToInt(width * (size.y / size.x)), 1, MaxTextureSize);
            return true;
        }

        /// <summary>显示图在屏幕上的矩形：<paramref name="origin"/> = 左下角（像素），<paramref name="size"/> = 像素宽高。</summary>
        private bool TryGetDisplayRect(out Vector2 origin, out Vector2 size)
        {
            origin = Vector2.zero;
            size = Vector2.zero;

            if (_target == null)
            {
                return false;
            }

            RectTransform rect = _target.rectTransform;
            rect.GetWorldCorners(_corners);

            // overlay 画布：世界坐标 → 屏幕像素走 null 相机这一支（画布已按屏幕缩放）。
            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(null, _corners[0]);
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(null, _corners[2]);

            origin = bottomLeft;
            size = topRight - bottomLeft;
            return size.x > 0f && size.y > 0f;
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
