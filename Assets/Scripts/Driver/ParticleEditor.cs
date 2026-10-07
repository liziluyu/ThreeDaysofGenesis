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
    /// **右键**在可投放类型间循环切换（材料类固/液 + Gas 白名单——默认只有火；蒸汽、云、大气等 Gas 中间态由相变产生，不可手刷）；鼠标旁常驻色块 + 类型名 + 半径，显示当前正在投放的是哪一种、多大；
    /// **空格**暂停/恢复整个模拟（调驱动的 <see cref="ParticleSimulationDriver.Pause"/> / <see cref="ParticleSimulationDriver.Resume"/>）；
    /// **按住左方向键 / A**、**按住右方向键 / D** 把相机在沙箱内左右平移（视口始终夹在网格左右边界内，移不出沙箱；
    /// 平移只动相机、不碰世界，因此暂停中与高速模式中都能用）；
    /// **`=` / `-`** 手动调倍速（×2 / ÷2，夹在 [1,16]，切回手动档）；**Tab** 开关自适应倍速（开启时从当前倍率起步）；
    /// **H** 进入**高速模式**（强制自适应倍速、锁编辑，跑满设置资产的高速时长自动暂停并在屏幕中央显示产生/死亡结算，
    /// **Enter 或 H** 确认退出——退出后仍保持暂停，由玩家自己按空格继续）；
    /// **按住 E 时按左键**改为擦除：把鼠标处半径 <see cref="BrushRadius"/> 内的粒子清成空气（不切换当前投放类型）；
    /// 屏幕左上角常驻**光标格读数**：格子坐标、该格的粒子名、温度、热量与湿度，末尾追加当前倍速（<c>×N</c>，自适应时 <c>×N auto</c>）；
    /// 高速模式运行中屏幕左下角另有**进度读数**：<c>高速 1234/6000 ×8 auto 地表 12.3° 湿度 0.456</c>（未采样时温度显示 <c>--</c>）。
    /// </summary>
    /// <remarks>
    /// <para>屏幕 → 格子：从相机发射射线打到网格所在的 z=0 平面，再换算到 <see cref="ParticleRenderer"/> 的局部坐标
    /// （网格 quad 挂在它的 transform 下，1 cell = 1 unit，无旋转与缩放）。</para>
    /// <para>写入一律走 <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.Clear"/>：世界会把新格标记成「本 tick 未处理」，
    /// 因此画出来的粒子必定会在下一个 tick 被模拟处理，不需要编辑器额外做什么。</para>
    /// <para>高速模式的状态机与统计全在内核 <see cref="HighSpeedSession"/>（纯观察者），本类只管输入与显示：
    /// <see cref="ParticleSimulationDriver.HighSpeedActive"/> 为真时在 <see cref="Update"/> 的**同一处**统一拦截
    /// 右键切换 / 滚轮调半径 / 左键投放与 <c>=</c>/<c>-</c>/<c>Tab</c>，只留下空格暂停、相机平移与只读读数——锁的是编辑，看视角不锁。</para>
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

        [Tooltip("高速模式热键：不在模式中时按它进入；结算完成后按它（或 Enter）确认退出。设为 None 关闭热键。")]
        [SerializeField] private KeyCode _highSpeedKey = KeyCode.H;

        [Header("相机平移")]
        [Tooltip("左移热键（按住持续左移）：默认 左方向键 + A。列表留空 / 全设 None 即关闭左移。")]
        [SerializeField] private KeyCode[] _panLeftKeys = { KeyCode.LeftArrow, KeyCode.A };

        [Tooltip("右移热键（按住持续右移）：默认 右方向键 + D。列表留空 / 全设 None 即关闭右移。")]
        [SerializeField] private KeyCode[] _panRightKeys = { KeyCode.RightArrow, KeyCode.D };

        [Tooltip("平移速度（格/秒）。<= 0 关闭平移（相机仍会被夹回沙箱内）。")]
        [SerializeField] private float _panSpeed = 30f;

        [Header("投放白名单")]
        [Tooltip("不可手动投放的类型名（按 DebugName 或类名匹配；留空 = 全部可投）。" +
                 "草、湿土、腐殖质、腐殖土、水生植物只能由模拟产生，默认已列入。")]
        [SerializeField] private string[] _unplaceableTypes =
        {
            "GrassParticle", "WetSoilParticle", "HumusParticle", "HumusSoilParticle", "AquaticPlantParticle",
        };

        [Tooltip("「单颗投放」白名单（按 DebugName 或类名匹配）：右键切到这些类型时，无论笔刷半径多大，" +
                 "都只在光标中心落一颗，且两次投放之间共享 500ms 冷却。默认草种子与水生态种子，将来可追加。")]
        [SerializeField] private string[] _singlePlacementTypes = { "GrassSeedParticle", "AquaticSeedParticle" };

        [Tooltip("可手动投放的 Gas 类型白名单（按 DebugName 或类名匹配）：Gas 默认一律不可投放" +
                 "（蒸汽 / 云 / 大气是水循环的中间态，只能由相变产生），火是当前唯一可手刷的例外。")]
        [SerializeField] private string[] _placeableGasTypes = { "FireParticle" };

        private readonly HashSet<ushort> _unplaceable = new HashSet<ushort>();
        private readonly HashSet<ushort> _singlePlacement = new HashSet<ushort>();
        private readonly HashSet<ushort> _placeableGas = new HashSet<ushort>();
        private ushort[] _placeable = new ushort[0];
        private int _cursor;
        private ushort _typeId;
        private string _hudText;
        private Color _hudColor;
        private string _probeText;
        private string _highSpeedHudText;
        private string[] _highSpeedReportLines;
        private float _lastSinglePlacementTime = float.NegativeInfinity;

        /// <summary>笔刷半径下限（格）。</summary>
        public const int MinBrushRadius = 1;

        /// <summary>笔刷半径上限（格）。</summary>
        public const int MaxBrushRadius = 10;

        /// <summary>「单颗投放」白名单类型的投放冷却（秒）。</summary>
        public const float SinglePlacementCooldownSeconds = 0.5f;

        /// <summary>倍速读数里「实测倍率」括号的显示阈值：实测与目标的相对差小于它就不显示（稳态下免噪音）。</summary>
        public const float EffectiveSpeedDisplayThreshold = 0.05f;

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
            // 三个白名单按名字解析成 typeId：不可投放集合决定右键循环里剔除哪些类型，
            // 单颗投放集合决定哪些类型走「单颗 + 冷却」而不是圆形笔刷，
            // Gas 白名单决定哪些 Gas 类型（默认只有火）例外可手刷。
            ResolveTypeNames(_unplaceableTypes, _unplaceable);
            ResolveTypeNames(_singlePlacementTypes, _singlePlacement);
            ResolveTypeNames(_placeableGasTypes, _placeableGas);
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

            // 高速模式热键：不在模式中时按 H 进入；结算完成后按 Enter（或 H）确认退出。
            HandleHighSpeedKeys();
            RefreshHighSpeed();

            // 暂停热键：这里只负责「按下了就切一下」，
            // 具体冻结/恢复由驱动暴露的 Pause()/Resume() 执行（驱动不认识输入）。高速模式中也保留。
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

            // 相机平移：只动相机、不碰世界，所以放在高速模式的锁编辑拦截**之前**——
            // 高速模式锁的是编辑，观看视角（平移 + 光标读数）跟着解锁一起禁用没有意义。
            UpdateCameraPan(world);

            // 高速模式锁编辑：在这里**统一拦截**——右键盘切换、滚轮调半径、左键投放/擦除与倍速档（=`/`-`/Tab`）
            // 一概跳过，只保留上面的空格暂停与下面只读的光标读数。不是每个输入分支各加一个判断。
            if (_driver.HighSpeedActive)
            {
                ProbeCell(world, Input.mousePosition);
                return;
            }

            // 倍速热键：与暂停同理，这里只负责「按下了就切一下」，
            // 档位与自适应状态都在驱动暴露的 Speed 控制器里（驱动不认识输入）。
            SpeedController speed = _driver.Speed;
            if (speed != null)
            {
                if (Input.GetKeyDown(KeyCode.Equals))
                {
                    // ×2 手动调档（SetSpeed 切回手动档并钳到 [1, 16]）。
                    speed.SetSpeed(speed.Speed * 2f);
                }
                else if (Input.GetKeyDown(KeyCode.Minus))
                {
                    speed.SetSpeed(speed.Speed * 0.5f);
                }

                if (Input.GetKeyDown(KeyCode.Tab))
                {
                    speed.SetAdaptive(!speed.Adaptive);
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

        /// <summary>
        /// 相机左右平移：按住 <see cref="_panLeftKeys"/> / <see cref="_panRightKeys"/> 里的任一键（默认左方向键 + A、
        /// 右方向键 + D），按 <see cref="_panSpeed"/> 格/秒把相机沿沙箱水平方向推，同时按住两个方向即抵消（不动）。
        /// 相机位置的写入与「视口不许越出沙箱左右边界」的夹取都在渲染器（<see cref="ParticleRenderer.PanCamera"/>），
        /// 本方法只管按键 → 格数：只动相机、不碰 <c>Cell[]</c>，因此不参与确定性、也不受暂停影响。
        /// </summary>
        private void UpdateCameraPan(ParticleWorld world)
        {
            float direction = 0f;
            if (IsAnyKeyHeld(_panLeftKeys))
            {
                direction -= 1f;
            }

            if (IsAnyKeyHeld(_panRightKeys))
            {
                direction += 1f;
            }

            // 无输入时也调一次（delta = 0）：Game 视图宽高比变化会让原本界内的相机落到沙箱外，靠这次夹取拉回来。
            // Time.deltaTime 是真实帧时间——暂停（驱动不推进模拟）不冻结它，因此暂停中也能平移观看。
            _renderer.PanCamera(world, direction * Mathf.Max(0f, _panSpeed) * Time.deltaTime);
        }

        /// <summary>数组里是否有键被按住；<paramref name="keys"/> 为 null / 空（或元素是 <see cref="KeyCode.None"/>）即无键。</summary>
        private static bool IsAnyKeyHeld(KeyCode[] keys)
        {
            if (keys == null)
            {
                return false;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] != KeyCode.None && Input.GetKey(keys[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 高速模式热键：不在模式中时按 <see cref="_highSpeedKey"/> 进入；结算完成后按 Enter / 小键盘 Enter
        /// （或再次按 <see cref="_highSpeedKey"/>）确认退出。模式进行中这些键都不生效——进入后不能主动退出，只能等跑完。
        /// </summary>
        private void HandleHighSpeedKeys()
        {
            HighSpeedSession session = _driver.HighSpeed;
            if (session == null)
            {
                return;
            }

            if (!session.Active)
            {
                if (_highSpeedKey != KeyCode.None && Input.GetKeyDown(_highSpeedKey))
                {
                    _driver.EnterHighSpeed();
                }

                return;
            }

            if (!session.Completed)
            {
                return;
            }

            bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            if (_highSpeedKey != KeyCode.None && Input.GetKeyDown(_highSpeedKey))
            {
                confirm = true;
            }

            if (confirm)
            {
                ConfirmHighSpeedExit(session);
            }
        }

        /// <summary>确认结算：退出内核会话（恢复进入前的倍速档位），清掉结算缓存并解除编辑锁定。
        /// **保持暂停**——恢复推进由玩家自己按空格决定。</summary>
        private void ConfirmHighSpeedExit(HighSpeedSession session)
        {
            session.Exit(_driver.Speed);
            _highSpeedHudText = null;
            _highSpeedReportLines = null;
        }

        /// <summary>
        /// 刷新高速模式 HUD：运行时每帧重建左下角那一行读数；结算完成后**只建一次**结算行
        /// （<see cref="HighSpeedSession.Completed"/> 是终态，行内容不会再变）。不在模式中时清空两个缓存。
        /// </summary>
        private void RefreshHighSpeed()
        {
            HighSpeedSession session = _driver.HighSpeed;
            if (session == null || !session.Active)
            {
                _highSpeedHudText = null;
                _highSpeedReportLines = null;
                return;
            }

            _highSpeedHudText = DescribeHighSpeed(
                session.ElapsedLogicTicks,
                session.DurationLogicTicks,
                DescribeSpeed(),
                session.LatestSurfaceTemperature,
                session.LatestAverageHumidity);

            if (session.Completed && _highSpeedReportLines == null)
            {
                _highSpeedReportLines = BuildHighSpeedReport(session);
            }
        }

        /// <summary>
        /// 左下角进度行的文案：<c>高速 1234/6000 ×8 auto 地表 12.3° 湿度 0.456</c>。
        /// 倍速部分复用 <see cref="DescribeSpeed"/>；温度 / 湿度还没采到样本（<see cref="float.NaN"/>）时显示 <c>--</c>，不猜数字。
        /// </summary>
        private static string DescribeHighSpeed(int elapsed, int duration, string speedText, float temperature, float humidity)
        {
            string surface = float.IsNaN(temperature)
                ? "--"
                : temperature.ToString("0.0", CultureInfo.InvariantCulture) + "°";
            string wetness = float.IsNaN(humidity)
                ? "--"
                : humidity.ToString("0.000", CultureInfo.InvariantCulture);

            return string.Format(
                CultureInfo.InvariantCulture,
                "高速 {0}/{1} {2} 地表 {3} 湿度 {4}",
                elapsed, duration, speedText, surface, wetness);
        }

        /// <summary>
        /// 结算行：遍历注册表，列出**产生或死亡非零**的类型，每行 <c>名称 产生 N 死亡 M</c>；
        /// 全零类型过滤掉。空气（typeId 0）不显示——它只是被抹除留下的真空，不是「产生」。
        /// </summary>
        private static string[] BuildHighSpeedReport(HighSpeedSession session)
        {
            ParticleType[] types = ParticleRegistry.Types;
            var lines = new List<string>();

            for (int id = 1; id < types.Length; id++)
            {
                ParticleType type = types[id];
                if (type == null)
                {
                    continue;
                }

                long births = session.GetBirths(id);
                long deaths = session.GetDeaths(id);
                if (births == 0 && deaths == 0)
                {
                    continue;
                }

                lines.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}  产生 {1}  死亡 {2}",
                    DisplayName(type), births, deaths));
            }

            return lines.ToArray();
        }

        /// <summary>结算面板：屏幕中央的 <see cref="GUI.Box"/> + 逐行统计 + 确认提示。</summary>
        private void DrawHighSpeedReport()
        {
            const float width = 360f;
            const float lineHeight = 20f;
            float height = (_highSpeedReportLines.Length + 2) * lineHeight + 16f;
            var box = new Rect(
                (Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f,
                width,
                height);

            GUI.Box(box, "高速模式结算");

            float y = box.y + 8f + lineHeight;
            for (int i = 0; i < _highSpeedReportLines.Length; i++)
            {
                GUI.Label(new Rect(box.x + 12f, y, width - 24f, lineHeight), _highSpeedReportLines[i]);
                y += lineHeight;
            }

            GUI.Label(new Rect(box.x + 12f, y, width - 24f, lineHeight), "按 Enter 或 H 确认退出");
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
        /// **火**另有投放资格判定（<see cref="FireParticle.CanPlaceAt"/>）：不覆盖实体、八邻内必须有**锚点**——
        /// 固 / 液的一格，**或另一格火**（投放层允许火链互相锚定，圆刷贴地时能连锁铺满整团火；
        /// 物理燃烧的附着物**不含火**）。不合格的格被跳过（圆内部分投放，其余类型不受影响）。
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

                    // 火不许覆盖实体、不许浮空（见 FireParticle.CanPlaceAt）；其余类型照常投放。
                    if (_typeId == ParticleIds.Fire && !FireParticle.CanPlaceAt(world, cx + dx, cy + dy))
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

            // 高速模式的读数与结算面板都从缓存绘制，但用驱动的**实时**状态兜底：
            // 任何结束会话的路径（确认退出、世界重建丢掉会话）都会立刻把两者隐藏掉，不留一帧残影。
            if (_driver != null && _driver.HighSpeedActive)
            {
                // 进度读数固定在屏幕左下角（IMGUI 原点在左上，所以 y 从底部往上量）。
                if (_highSpeedHudText != null)
                {
                    GUI.Label(new Rect(6f, Screen.height - 24f, 520f, 20f), _highSpeedHudText);
                }

                if (_highSpeedReportLines != null)
                {
                    DrawHighSpeedReport();
                }
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

        /// <summary>刷新左上角读数：光标在网格内则显示该格的信息，否则显示「视口外」；按住 E（擦除修饰键）时前置提示。
        /// 末尾追加当前倍速（见 <see cref="DescribeSpeed"/>）。</summary>
        private void ProbeCell(ParticleWorld world, Vector3 screenPosition)
        {
            string reading = TryGetCell(screenPosition, world, out int x, out int y)
                ? DescribeCell(world, x, y)
                : "视口外";

            // 按住 E 时前缀一个固定串，免得以为左键还在投放。
            string line = Input.GetKey(KeyCode.E) ? "【擦除】" + reading : reading;
            _probeText = line + "  " + DescribeSpeed();
        }

        /// <summary>
        /// 左上角读数的倍速后缀：手动档 <c>×N</c>、自适应档 <c>×N auto</c>；目标倍率与实测吞吐（<see cref="SpeedController.EffectiveSpeed"/>）
        /// 相差 ≥5% 时再追加 <c>(实 ×M)</c>——稳态下不显示这个括号，免得读数抖。
        /// </summary>
        private string DescribeSpeed()
        {
            SpeedController speed = _driver != null ? _driver.Speed : null;
            if (speed == null)
            {
                return "×1";
            }

            float target = speed.Speed;
            string text = "×" + target.ToString("0.##", CultureInfo.InvariantCulture);
            if (speed.Adaptive)
            {
                text += " auto";
            }

            float effective = speed.EffectiveSpeed;
            // effectiv <= 0 = 还没有过有效样本（例如一直暂停），不猜一个假数字出来。
            if (effective > 0f && Mathf.Abs(effective - target) >= target * EffectiveSpeedDisplayThreshold)
            {
                text += " (实 ×" + effective.ToString("0.##", CultureInfo.InvariantCulture) + ")";
            }

            return text;
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

        /// <summary>按注册顺序收集可投放的类型 id：跳过空气（0）、注册表尾部的空槽、所有不在
        /// <see cref="_placeableGasTypes"/> 白名单里的 Gas 类型（蒸汽/云/大气是水循环的中间态，
        /// 只能由相变产生；火是白名单例外），以及 <see cref="_unplaceableTypes"/> 里的类型
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
            if (type == null || _unplaceable.Contains((ushort)typeId))
            {
                return false;
            }

            // Gas 默认不可手刷；白名单（_placeableGasTypes，默认火）里的 Gas 例外放行。
            bool isGas = (type.Info.Tags & ParticleTags.Gas) != 0;
            return !isGas || _placeableGas.Contains((ushort)typeId);
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
