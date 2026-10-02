using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 沙箱里的粒子编辑器（调试工具，不属于模拟内核）：
    /// **按住左键**用当前粒子填满鼠标处半径 <see cref="BrushRadius"/> 的圆；**滚轮**在 1..10 之间调整半径；
    /// **右键**在已注册的粒子类型间循环切换；鼠标旁常驻色块 + 类型名 + 半径，显示当前正在投放的是哪一种、多大。
    /// </summary>
    /// <remarks>
    /// <para>屏幕 → 格子：从相机发射射线打到网格所在的 z=0 平面，再换算到 <see cref="ParticleRenderer"/> 的局部坐标
    /// （网格 quad 挂在它的 transform 下，1 cell = 1 unit，无旋转与缩放）。</para>
    /// <para>写入一律走 <see cref="ParticleWorld.Set"/>：世界会把新格标记成「本 tick 未处理」，
    /// 因此画出来的粒子必定会在下一个 tick 被模拟处理，不需要编辑器额外做什么。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleEditor : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationDriver _driver;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("留空则回退到 Camera.main。")]
        [SerializeField] private Camera _camera;

        [Tooltip("笔刷半径（格）。运行期可用鼠标滚轮调整。")]
        [SerializeField, Range(MinBrushRadius, MaxBrushRadius)] private int _brushRadius = 3;

        [Tooltip("鼠标旁显示当前粒子与半径：色块 + 类型名 + 序号 + r。")]
        [SerializeField] private bool _drawHud = true;

        private ushort[] _placeable = new ushort[0];
        private int _cursor;
        private ushort _typeId;
        private string _hudText;
        private Color _hudColor;

        /// <summary>笔刷半径下限（格）。</summary>
        public const int MinBrushRadius = 1;

        /// <summary>笔刷半径上限（格）。</summary>
        public const int MaxBrushRadius = 10;

        /// <summary>当前笔刷半径（格）。</summary>
        public int BrushRadius
        {
            // 手改 YAML 越界时由 getter 兜底夹紧。
            get { return Mathf.Clamp(_brushRadius, MinBrushRadius, MaxBrushRadius); }
        }

        /// <summary>当前正在投放的 typeId。</summary>
        public ushort CurrentTypeId
        {
            get { return _typeId; }
        }

        private void Awake()
        {
            if (_driver == null)
            {
                _driver = GetComponent<ParticleSimulationDriver>();
            }

            if (_driver == null)
            {
                _driver = FindObjectOfType<ParticleSimulationDriver>();
            }

            if (_renderer == null)
            {
                _renderer = GetComponent<ParticleRenderer>();
            }

            if (_renderer == null)
            {
                _renderer = FindObjectOfType<ParticleRenderer>();
            }

            // 注册表由 ParticleRegistration 的 BeforeSceneLoad 钩子填充，因此这里读得到全部类型。
            _placeable = CollectPlaceableTypes();
            if (_placeable.Length == 0)
            {
                Debug.LogError(
                    "[ParticleEditor] 没有任何已注册的粒子类型，编辑器不生效。请确认 ParticleRegistration.RegisterAll() 已执行。");
                return;
            }

            Select(0);
        }

        private void Update()
        {
            if (_placeable.Length == 0 || _driver == null || _renderer == null)
            {
                return;
            }

            ParticleWorld world = _driver.World;
            if (world == null)
            {
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                CycleType();
            }

            float scroll = Input.mouseScrollDelta.y;
            if (scroll > 0f)
            {
                AdjustRadius(1);
            }
            else if (scroll < 0f)
            {
                AdjustRadius(-1);
            }

            if (Input.GetMouseButton(0) && TryGetCell(Input.mousePosition, world, out int x, out int y))
            {
                Paint(world, x, y);

                // 暂停时驱动不会自己上屏，不补这一下就看不到刚画下去的东西。
                if (!_driver.Simulate)
                {
                    _driver.PresentNow();
                }
            }
        }

        /// <summary>切到下一个可投放类型（到末尾回到开头）。</summary>
        public void CycleType()
        {
            if (_placeable.Length == 0)
            {
                return;
            }

            Select((_cursor + 1) % _placeable.Length);
        }

        /// <summary>滚轮每格调 <paramref name="steps"/> 格半径，结果夹在 [<see cref="MinBrushRadius"/>, <see cref="MaxBrushRadius"/>]。</summary>
        public void AdjustRadius(int steps)
        {
            _brushRadius = Mathf.Clamp(BrushRadius + steps, MinBrushRadius, MaxBrushRadius);
            RefreshHud();
        }

        /// <summary>
        /// 用当前粒子填满以 (cx, cy) 为圆心、<see cref="BrushRadius"/> 为半径的实心圆。
        /// </summary>
        /// <returns>写入成功的格数；越界格被跳过（<see cref="ParticleWorld.Set"/> 返回 false）。</returns>
        public int Paint(ParticleWorld world, int cx, int cy)
        {
            int radiusSquared = BrushRadius * BrushRadius;
            int written = 0;

            for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
            {
                for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
                {
                    if (dx * dx + dy * dy > radiusSquared)
                    {
                        continue;
                    }

                    if (world.Set(cx + dx, cy + dy, _typeId))
                    {
                        written++;
                    }
                }
            }

            return written;
        }

        private void OnGUI()
        {
            if (!_drawHud || _hudText == null)
            {
                return;
            }

            // Input.mousePosition 原点在左下，IMGUI 原点在左上。
            Vector3 mouse = Input.mousePosition;
            float x = mouse.x + 18f;
            float y = Screen.height - mouse.y - 6f;

            GUI.color = _hudColor;
            GUI.DrawTexture(new Rect(x, y, 12f, 12f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 16f, y - 3f, 200f, 20f), _hudText);
        }

        /// <summary>按注册顺序收集可投放的类型 id：跳过空气（0）与注册表尾部的空槽。</summary>
        private static ushort[] CollectPlaceableTypes()
        {
            ParticleType[] types = ParticleRegistry.Types;

            int count = 0;
            for (int i = 1; i < types.Length; i++)
            {
                if (types[i] != null)
                {
                    count++;
                }
            }

            var placeable = new ushort[count];
            int index = 0;
            for (int i = 1; i < types.Length; i++)
            {
                if (types[i] != null)
                {
                    placeable[index++] = (ushort)i;
                }
            }

            return placeable;
        }

        /// <summary>选中第 <paramref name="index"/> 个可投放类型，并重建 HUD 文本。</summary>
        private void Select(int index)
        {
            _cursor = index;
            _typeId = _placeable[index];
            RefreshHud();
        }

        /// <summary>按当前类型与半径重建 HUD 文本与色块颜色。</summary>
        private void RefreshHud()
        {
            ParticleType type = ParticleRegistry.Types[_typeId];
            ParticleTypeInfo info = type.Info;
            string debugName = string.IsNullOrEmpty(info.DebugName) ? type.GetType().Name : info.DebugName;

            _hudColor = info.Palette[0];
            _hudText = $"{debugName}  {_cursor + 1}/{_placeable.Length}  r={BrushRadius}";
        }

        /// <summary>屏幕坐标（像素，原点左下）→ 格子坐标。射线打到 z=0 平面，再换算到渲染器局部坐标取整。</summary>
        /// <returns>映射结果在网格内则 true；相机缺失、射线与平面平行或落在网格外则 false。</returns>
        public bool TryGetCell(Vector3 screenPosition, ParticleWorld world, out int x, out int y)
        {
            x = 0;
            y = 0;

            Camera camera = _camera != null ? _camera : Camera.main;
            if (camera == null)
            {
                return false;
            }

            Ray ray = camera.ScreenPointToRay(screenPosition);
            if (Mathf.Approximately(ray.direction.z, 0f))
            {
                return false;
            }

            Vector3 worldPoint = ray.origin + ray.direction * (-ray.origin.z / ray.direction.z);
            Vector3 local = _renderer.transform.InverseTransformPoint(worldPoint);

            x = Mathf.FloorToInt(local.x);
            y = Mathf.FloorToInt(local.y);
            return world.IsInBounds(x, y);
        }
    }
}
