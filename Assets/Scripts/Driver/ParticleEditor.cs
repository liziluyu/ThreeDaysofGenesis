using System.Collections.Generic;
using System.Globalization;
using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 沙箱里的粒子编辑器（调试工具，不属于模拟内核）：
    /// **按住左键**用当前粒子填满鼠标处半径 <see cref="BrushRadius"/> 的圆；**滚轮**在 1..10 之间调整半径；
    /// **右键**在可投放类型间循环切换（只含材料类：固/液；蒸汽、云、大气等 Gas 中间态由相变产生，不可手刷）；鼠标旁常驻色块 + 类型名 + 半径，显示当前正在投放的是哪一种、多大；
    /// **空格**暂停/恢复整个模拟（调驱动的 <see cref="ParticleSimulationDriver.Pause"/> / <see cref="ParticleSimulationDriver.Resume"/>）；
    /// **按住 E 时按左键**改为擦除：把鼠标处半径 <see cref="BrushRadius"/> 内的粒子清成空气（不切换当前投放类型）；
    /// 屏幕左上角常驻**光标格读数**：格子坐标、该格的粒子名、温度、热量与湿度。
    /// </summary>
    /// <remarks>
    /// <para>屏幕 → 格子：从相机发射射线打到网格所在的 z=0 平面，再换算到 <see cref="ParticleRenderer"/> 的局部坐标
    /// （网格 quad 挂在它的 transform 下，1 cell = 1 unit，无旋转与缩放）。</para>
    /// <para>写入一律走 <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.Clear"/>：世界会把新格标记成「本 tick 未处理」，
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

        [Tooltip("显示鼠标旁的当前粒子/半径，以及左上角光标格的读数。")]
        [SerializeField] private bool _drawHud = true;

        [Tooltip("暂停热键：按一下停掉所有帧（运动帧与逻辑帧），再按一下继续。设为 None 关闭热键。")]
        [SerializeField] private KeyCode _pauseKey = KeyCode.Space;

        [Header("投放白名单")]
        [Tooltip("不可手动投放的类型名（按 DebugName 或类名匹配；留空 = 全部可投）。" +
                 "草、湿土、腐殖质、腐殖土只能由模拟产生，默认已列入。")]
        [SerializeField] private string[] _unplaceableTypes =
        {
            "GrassParticle", "WetSoilParticle", "HumusParticle", "HumusSoilParticle",
        };

        [Tooltip("「单颗投放」白名单（按 DebugName 或类名匹配）：右键切到这些类型时，无论笔刷半径多大，" +
                 "都只在光标中心落一颗，且两次投放之间共享 500ms 冷却。默认只有草种子，将来可追加。")]
        [SerializeField] private string[] _singlePlacementTypes = { "GrassSeedParticle" };

        private readonly HashSet<ushort> _unplaceable = new HashSet<ushort>();
        private readonly HashSet<ushort> _singlePlacement = new HashSet<ushort>();
        private ushort[] _placeable = new ushort[0];
        private int _cursor;
        private ushort _typeId;
        private string _hudText;
        private Color _hudColor;
        private string _probeText;
        private float _lastSinglePlacementTime = float.NegativeInfinity;

        /// <summary>笔刷半径下限（格）。</summary>
        public const int MinBrushRadius = 1;

        /// <summary>笔刷半径上限（格）。</summary>
        public const int MaxBrushRadius = 10;

        /// <summary>「单颗投放」白名单类型的投放冷却（秒）。</summary>
        public const float SinglePlacementCooldownSeconds = 0.5f;

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
            // 两个白名单按名字解析成 typeId：不可投放集合决定右键循环里有哪些类型，
            // 单颗投放集合决定哪些类型走「单颗 + 冷却」而不是圆形笔刷。
            ResolveTypeNames(_unplaceableTypes, _unplaceable);
            ResolveTypeNames(_singlePlacementTypes, _singlePlacement);
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

            // 暂停热键：这里只负责「按下了就切一下」，
            // 具体冻结/恢复由驱动暴露的 Pause()/Resume() 执行（驱动不认识输入）。
            if (_pauseKey != KeyCode.None && Input.GetKeyDown(_pauseKey))
            {
                if (_driver.Paused)
                {
                    _driver.Resume();
                }
                else
                {
                    _driver.Pause();
                }
            }

            ProbeCell(world, Input.mousePosition);

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
                // E 是擦除修饰键：与左键同按时改走擦除（按住的整段时间都擦），不改当前投放类型。
                if (Input.GetKey(KeyCode.E))
                {
                    Erase(world, x, y);
                }
                else
                {
                    Paint(world, x, y);
                }

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
        /// **擦除**：把以 (cx, cy) 为圆心、<see cref="BrushRadius"/> 为半径的实心圆清成空气。
        /// 与 <see cref="Paint"/> 无关——擦除不看当前投放类型，也不受单颗投放白名单影响（按住 E 就是纯擦）。
        /// </summary>
        /// <returns>真正被清掉的格数（本来就是空气的格不计）；越界格被跳过。</returns>
        public int Erase(ParticleWorld world, int cx, int cy)
        {
            int radiusSquared = BrushRadius * BrushRadius;
            int cleared = 0;

            for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
            {
                for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
                {
                    if (dx * dx + dy * dy > radiusSquared)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    int y = cy + dy;

                    // 先确认真有东西可清：Clear 抹空气也返回 true，直接计数会把「白擦的格」算进去。
                    if (world.IsInBounds(x, y) && !world.IsAir(x, y) && world.Clear(x, y))
                    {
                        cleared++;
                    }
                }
            }

            return cleared;
        }

        /// <summary>
        /// 投放当前粒子。普通类型填满以 (cx, cy) 为圆心、<see cref="BrushRadius"/> 为半径的实心圆；
        /// **单颗投放白名单**里的类型（见 <see cref="_singlePlacementTypes"/>）无论半径多大都只在中心落一颗，
        /// 且两次投放之间共享 <see cref="SinglePlacementCooldownSeconds"/> 的冷却。
        /// </summary>
        /// <returns>写入成功的格数；越界格被跳过（<see cref="ParticleWorld.Set"/> 返回 false）；冷却中返回 0。</returns>
        public int Paint(ParticleWorld world, int cx, int cy)
        {
            if (_singlePlacement.Contains(_typeId))
            {
                return PaintSingle(world, cx, cy);
            }

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

        /// <summary>
        /// 单颗投放：只在 (cx, cy) 落一颗。冷却未到时什么都不做（返回 0），因此按住左键也只会每
        /// <see cref="SinglePlacementCooldownSeconds"/> 落一颗，而不是每帧一颗。
        /// </summary>
        private int PaintSingle(ParticleWorld world, int cx, int cy)
        {
            // unscaledTime：冷却按真实时间流逝，暂停（Simulate = false）时也照常计时。
            if (Time.unscaledTime - _lastSinglePlacementTime < SinglePlacementCooldownSeconds)
            {
                return 0;
            }

            if (!world.Set(cx, cy, _typeId))
            {
                return 0;
            }

            _lastSinglePlacementTime = Time.unscaledTime;
            return 1;
        }

        private void OnGUI()
        {
            if (!_drawHud)
            {
                return;
            }

            if (_probeText != null)
            {
                GUI.Label(new Rect(6f, 4f, 420f, 20f), _probeText);
            }

            if (_hudText == null)
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

        /// <summary>刷新左上角读数：光标在网格内则显示该格的信息，否则显示「视口外」；按住 E（擦除修饰键）时前置提示。</summary>
        private void ProbeCell(ParticleWorld world, Vector3 screenPosition)
        {
            string reading = TryGetCell(screenPosition, world, out int x, out int y)
                ? DescribeCell(world, x, y)
                : "视口外";

            // 按住 E 时前缀一个固定串，免得以为左键还在投放。
            _probeText = Input.GetKey(KeyCode.E) ? "【擦除】" + reading : reading;
        }

        /// <summary>
        /// (x,y) 的读数行：坐标 + 粒子名 + 温度 + 热量 + 湿度。
        /// 温度是导出量 <c>T = Q / 热容</c>，热量是格子里存的那份 Q，湿度对这一格而言（固/液恒为 0）。
        /// 数值固定用不变文化格式化，免得跟着系统区域设置漂出逗号小数点。
        /// </summary>
        public static string DescribeCell(ParticleWorld world, int x, int y)
        {
            ushort typeId = world.GetCell(x, y).typeId;
            ParticleType[] types = world.Types;

            string name = typeId != ParticleRegistry.AirTypeId && typeId < types.Length && types[typeId] != null
                ? DisplayName(types[typeId])
                : "空气";

            float temperature = world.Heat.GetTemperature(x, y);
            float heat = world.Heat.GetHeat(x, y);
            float humidity = world.Humidity.Get(x, y);

            return string.Format(
                CultureInfo.InvariantCulture,
                "({0},{1})  {2}  T={3:0.0}  Q={4:0.00}  湿度={5:0.000}",
                x, y, name, temperature, heat, humidity);
        }

        /// <summary>按注册顺序收集可投放的类型 id：跳过空气（0）、注册表尾部的空槽、所有 Gas 类型
        /// （蒸汽/云/大气是水循环的中间态，只能由相变产生），以及 <see cref="_unplaceableTypes"/> 里的类型
        /// （草与湿土只能由模拟产生）。</summary>
        private ushort[] CollectPlaceableTypes()
        {
            ParticleType[] types = ParticleRegistry.Types;

            int count = 0;
            for (int i = 1; i < types.Length; i++)
            {
                if (IsPlaceable(types[i], i))
                {
                    count++;
                }
            }

            var placeable = new ushort[count];
            int index = 0;
            for (int i = 1; i < types.Length; i++)
            {
                if (IsPlaceable(types[i], i))
                {
                    placeable[index++] = (ushort)i;
                }
            }

            return placeable;
        }

        private bool IsPlaceable(ParticleType type, int typeId)
        {
            return type != null
                && (type.Info.Tags & ParticleTags.Gas) == 0
                && !_unplaceable.Contains((ushort)typeId);
        }

        /// <summary>
        /// 把 Inspector 上的类型名列表解析成 typeId 集合：按 <see cref="ParticleTypeInfo.DebugName"/> 或类名匹配
        /// 注册表里的每一个类型。找不到的名字记一条警告并跳过（拼错类型名不会静默失效）。
        /// 注册表由 <c>BeforeSceneLoad</c> 钩子填充，本方法只在 <c>Awake</c> 里调用，因此读得到全部类型。
        /// </summary>
        private static void ResolveTypeNames(string[] names, HashSet<ushort> destination)
        {
            if (names == null)
            {
                return;
            }

            ParticleType[] types = ParticleRegistry.Types;
            for (int n = 0; n < names.Length; n++)
            {
                string name = names[n];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                bool found = false;
                for (int i = 1; i < types.Length; i++)
                {
                    ParticleType type = types[i];
                    if (type == null)
                    {
                        continue;
                    }

                    if (type.Info.DebugName == name || type.GetType().Name == name)
                    {
                        destination.Add((ushort)i);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    Debug.LogWarning(
                        $"[ParticleEditor] 白名单里的类型名 \"{name}\" 未在注册表中找到，已忽略。");
                }
            }
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

            _hudColor = info.Palette[0];
            string brush = _singlePlacement.Contains(_typeId) ? "单颗" : $"r={BrushRadius}";
            _hudText = $"{DisplayName(type)}  {_cursor + 1}/{_placeable.Length}  {brush}";
        }

        /// <summary>类型的显示名：优先 <see cref="ParticleTypeInfo.DebugName"/>，没填则回退类名。</summary>
        private static string DisplayName(ParticleType type)
        {
            ParticleTypeInfo info = type.Info;
            return string.IsNullOrEmpty(info.DebugName) ? type.GetType().Name : info.DebugName;
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
