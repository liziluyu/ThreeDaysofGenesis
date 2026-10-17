using System;
using System.Collections.Generic;
using System.Globalization;
using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 沙箱里的粒子编辑器（调试工具，不属于模拟内核）：**输入与读数生成**归本类、**上屏**归场景里的
    /// <see cref="SandboxHud"/>——本类不画任何 IMGUI，每帧只把算好的字符串推给 HUD（见 <see cref="PushHud"/>）；
    /// 因此所有屏幕元素（鼠标旁色块、左上角读数、左下角高速读数与结算面板、右上角光照面板）
    /// 都是可在编辑器里摆放的场景对象。
    /// **按住左键**用当前选中项填满鼠标处半径 <see cref="BrushRadius"/> 的圆（选中动物物种时改为**在鼠标格放置一只实体**）；**滚轮**在 1..10 之间调整半径；**Shift+滚轮**循环切换树木物种（上下滚动，选中树木种子时 HUD 显示物种名，见 <see cref="TreeSpecies"/>）；
    /// **右键**在可投放项间循环切换（材料类固/液 + Gas 白名单——默认只有火；蒸汽、云、大气等 Gas 中间态由相变产生，不可手刷；
    /// **末尾接动物物种**兔子 / 鱼 / 鸟，见 <see cref="CreatureSpawns"/>）；鼠标旁常驻色块 + 类型名 + 半径，显示当前正在投放的是哪一种、多大；
    /// 光标处另有一圈**白色像素圆**标出笔刷足迹——半径 = 当前笔刷半径，圈上的格恰好是左键会写到的格；
    /// 它画在**像素场景的顶层**（渲染器的一层 sprite，1 像素 = 1 格，与格子逐格对齐、压在所有像素之上，见
    /// <see cref="ParticleRenderer.SetBrushOutline"/>），随 <see cref="_drawHud"/> 一起开关；
    /// **空格**暂停/恢复整个模拟（提交 <see cref="SimulationCommand.SetPaused"/> 命令，见 <see cref="SubmitPaused"/>）；
    /// **按住左方向键 / A**、**按住右方向键 / D** 把相机在沙箱内左右平移（视口始终夹在网格左右边界内，移不出沙箱；
    /// 平移只动相机、不碰世界，因此暂停中与高速模式中都能用）；
    /// **H** 进入**高速模式**（倍速钉满档 16×、锁编辑，跑满设置资产的高速时长自动暂停并在屏幕中央**只显示一行**结论——
    /// 过关「过关！」绿、未过关为阶段 JSON 该检查项配的失败原因文案（红），取自 <see cref="GameController"/>；
    /// 不再列产生 / 死亡数）；
    /// **Enter 或 H** 确认退出——退出后倍速回 1×、仍保持暂停，由玩家自己按空格继续）；
    /// 普通编辑模式**不能调模拟速度**（恒 1×，速度只由高速模式改变）；
    /// **右上角光照面板**：一个 0..30 的**强度滑块** + 两行**秒读数**（白天 / 夜晚）+ 两行按秒步进按钮，
    /// 都直接写 <see cref="ParticleLightDriver"/> 的**强度 / 亮相位刻数 / 暗相位刻数**
    /// （驱动器是这三个值的唯一配置面，变化时才提交 <see cref="SimulationCommandKind.SetLightSettings"/> 命令），改完即时生效；
    /// 面板里没有文本输入，键盘热键不必再为它让位，但**使用 HUD 面板期间（指针落在
    /// 光照面板 / 按钮面板矩形内）屏蔽左键投放/擦除**——点按钮、拖滑块都不会顺手在底下画一笔；
    /// 对话激活时面板变灰不可编辑，**高速模式则整块锁死**——进入会话即强制隐藏（会话结果依赖光照输入），
    /// 模式期间按钮与热键都打不开它，退出后按原显隐状态放回来；
    /// **清除模式**（按住 <c>E</c> 即进、松开即退；投放面板的「清除」按钮翻转它，见 <see cref="EraseMode"/> / <see cref="ToggleEraseMode"/>）下按左键改为擦除：把鼠标处半径 <see cref="BrushRadius"/> 内的粒子清成空气（不切换当前投放项）；
    /// **L** 等价于点场景里的**光照按钮**——翻转右上角光照面板的显隐（<see cref="ToggleLightPanel"/>，热键 <see cref="_lightPanelKey"/>；
    /// 高速模式期间与按钮一样被拒绝）；
    /// 屏幕左上角常驻**光标格读数**：格子坐标、该格的粒子中文名、温度、热量与湿度——ProbeText 显示的其实是
    /// **高速状态行 &gt; 悬停的投放项名 &gt; 光标格读数**三段按优先级取一（悬停名字由投放面板经
    /// <see cref="SetHoveredSelection"/> 上报，判据见 <see cref="ComposeProbeLine"/>）；
    /// 高速模式运行中屏幕左下角另有**进度读数**：<c>高速 1234/6000 ×7.6 地表 12.3° 湿度 0.456</c>——倍速只显示**实测**吞吐
    /// （<c>SpeedController.EffectiveSpeed</c>，EMA 平滑），不显示目标档；未采样时温度显示 <c>--</c>）。
    /// </summary>
    /// <remarks>
    /// <para>屏幕 → 格子：从相机发射射线打到网格所在的 z=0 平面，再换算到 <see cref="ParticleRenderer"/> 的局部坐标
    /// （网格 quad 挂在它的 transform 下，1 cell = 1 unit，无旋转与缩放）。</para>
    /// <para><b>本类不写世界</b>：投放 / 擦除 / 生成实体 / 暂停 / 相机外的读数一律走
    /// <see cref="ParticleSimulationDriver.Submit"/>（值类型命令，在下一个 tick 边界按序应用）。
    /// 世界会把新格标记成「本 tick 未处理」，因此画出来的粒子必定会在下一个 tick 被模拟处理，不需要编辑器额外做什么。</para>
    /// <para><b>只读入口</b>：光标格读数取自 <see cref="ParticleSimulationDriver.CurrentFrame"/>（世界面向帧里的探针快照），
    /// 高速读数 / 结算取自同一帧的 <see cref="SimulationFrame.HighSpeed"/>——本类因此拿不到、也不读活世界。</para>
    /// <para>高速模式的状态机与统计全在内核 <see cref="HighSpeedSession"/>（纯观察者，world 私有），本类只管输入与显示：
    /// 帧里的 <see cref="HighSpeedFrame.Active"/> 为真时在 <see cref="Update"/> 的**同一处**统一拦截
    /// 右键切换 / 滚轮调半径 / 左键投放，只留下空格暂停、光照面板热键、相机平移与只读读数——锁的是编辑，看视角不锁。
    /// 进入 / 退出分别提交 <see cref="SimulationCommandKind.EnterHighSpeed"/> / <see cref="SimulationCommandKind.ExitHighSpeed"/> 命令。</para>
    /// <para>阶段限制：<see cref="SetPlacementFilter"/> 由 <see cref="GameController"/> 在阶段启动时按 JSON 调用，
    /// 粒子名单与 Inspector 白名单取交集（只缩小、不放开），动物名单则限制可投放的物种（null = 不限制、空数组 = 全部隐藏）；
    /// 粒子名单清空（null / 空数组）即恢复默认白名单。</para>
    /// <para>光照面板（场景里的 <see cref="SandboxHud"/>）写的是 <see cref="ParticleLightDriver"/> 的字段：该组件是这三个值的唯一配置面，
    /// 变化时才把整份配置作为命令提交给驱动。同一份字段也是 Inspector 的显示值——面板与 Inspector 是同一个配置面。</para>
    /// <para>按钮入口（给场景里的 uGUI 按钮 <c>onClick</c> 接线）：<see cref="TogglePauseMenu"/>（开关暂停界面 +
    /// 暂停 / 恢复模拟）、<see cref="ToggleLightPanel"/>（开关光照面板）、<see cref="EnterHighSpeedMode"/>（进入高速模式）。
    /// 投放面板（<c>HUD Canvas/ParticlePanel</c>）的按钮走 <see cref="SelectByName"/> /
    /// <see cref="Select(ParticleSelectionEntry)"/>，由 <c>ParticleSelectionPanel</c> 接线。</para>
    /// <para><b>投放选择入口</b>：当前选中项与全部可投项
    /// 都是 <see cref="ParticleSelectionEntry"/> 描述符——枚举 <see cref="Selections"/> 铺按钮、读
    /// <see cref="CurrentSelection"/> 决定高亮、按下时调 <see cref="Select(ParticleSelectionEntry)"/> /
    /// <see cref="SelectByName"/> / <see cref="SelectByTypeId"/>，选中项真的变化时收 <see cref="SelectionChanged"/>。
    /// 按钮只读这些入口，不改白名单与投放逻辑。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleEditor : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationDriver _driver;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("可选：高速模式结算面板据此显示一行胜负结论。留空则 Awake 里 GetComponent → FindObjectOfType 兜底；" +
                 "仍找不到就只不显示胜负行（不报错）。")]
        [SerializeField] private GameController _gameController;

        [Tooltip("场景里的 HUD（左上角读数 / 鼠标跟随色块 / 高速读数与结算面板 / 光照面板的显示层）。留空则 Awake 里 " +
                 "GetComponent → FindObjectOfType 兜底；仍找不到就所有 HUD 推送整段跳过（不报错——输入照常工作）。")]
        [SerializeField] private SandboxHud _hud;

        [Tooltip("留空则回退到 Camera.main。")]
        [SerializeField] private Camera _camera;

        [Tooltip("笔刷半径（格）。运行期可用鼠标滚轮调整。")]
        [SerializeField, Range(MinBrushRadius, MaxBrushRadius)] private int _brushRadius = 3;

        [Tooltip("显示编辑器的屏幕叠层：鼠标旁的当前粒子/半径色块、左上角光标格读数、光标处的白色笔刷轮廓圈。")]
        [SerializeField] private bool _drawHud = true;

        [Tooltip("暂停热键：按一下停掉所有帧（运动帧与逻辑帧），再按一下继续。设为 None 关闭热键。")]
        [SerializeField] private KeyCode _pauseKey = KeyCode.Space;

        [Tooltip("高速模式热键：不在模式中时按它进入；结算完成后按它（或 Enter）确认退出。设为 None 关闭热键。")]
        [SerializeField] private KeyCode _highSpeedKey = KeyCode.H;

        [Tooltip("光照面板热键：按一下等价于点场景里的「光照」按钮（翻转右上角光照面板的显隐，见 ToggleLightPanel）。" +
                 "设为 None 关闭热键。")]
        [SerializeField] private KeyCode _lightPanelKey = KeyCode.L;

        [Header("相机平移")]
        [Tooltip("左移热键（按住持续左移）：默认 左方向键 + A。列表留空 / 全设 None 即关闭左移。")]
        [SerializeField] private KeyCode[] _panLeftKeys = { KeyCode.LeftArrow, KeyCode.A };

        [Tooltip("右移热键（按住持续右移）：默认 右方向键 + D。列表留空 / 全设 None 即关闭右移。")]
        [SerializeField] private KeyCode[] _panRightKeys = { KeyCode.RightArrow, KeyCode.D };

        [Tooltip("平移速度（格/秒）。<= 0 关闭平移（相机仍会被夹回沙箱内）。")]
        [SerializeField] private float _panSpeed = 30f;

        /// <summary>
        /// 为 true 时本组件**完全不响应输入**（暂停 / 高速模式热键 / 切类型 / 画笔全部跳过）——
        /// 对话系统激活时置 true，避免"下一句"键（Space / 左键）同时触发暂停或画出粒子。
        /// </summary>
        public bool SuppressInput { get; set; }

        [Header("投放白名单")]
        [Tooltip("不可手动投放的类型名（按 DebugName 或类名匹配；留空 = 全部可投）。" +
                 "草、花、湿土、腐殖质、腐殖土、湿润腐殖土、水生植物、树干、树叶、生物质、冰只能由模拟产生，默认已列入。" +
                 "注意：**运行时以场景序列化的名单为准**，本默认值只对新建场景生效，改白名单要连 MainScene 一起改。")]
        [SerializeField] private string[] _unplaceableTypes =
        {
            "GrassParticle", "FlowerParticle", "WetSoilParticle", "HumusParticle", "HumusSoilParticle",
            "WetHumusSoilParticle", "AquaticPlantParticle", "TreeTrunkParticle", "TreeLeafParticle",
            "BiomassParticle", "IceParticle",
        };

        [Tooltip("「单颗投放」白名单（按 DebugName 或类名匹配）：右键切到这些类型时，无论笔刷半径多大，" +
                 "都只在光标中心落一颗，且两次投放之间共享 500ms 冷却。默认草种子与树木种子，将来可追加。")]
        [SerializeField] private string[] _singlePlacementTypes = { "GrassSeedParticle", "TreeSeedParticle" };

        [Tooltip("可手动投放的 Gas 类型白名单（按 DebugName 或类名匹配）：Gas 默认一律不可投放" +
                 "（蒸汽 / 云 / 大气是水循环的中间态，只能由相变产生），火是当前唯一可手刷的例外。")]
        [SerializeField] private string[] _placeableGasTypes = { "FireParticle" };

        /// <summary>
        /// 可投放的动物物种：投放选择里**排在所有粒子类型之后**（见 <see cref="Selections"/>），
        /// 选中后左键放置改为提交 <see cref="SimulationCommandKind.SpawnCreature"/> 命令（见 <see cref="SubmitPlacement"/>）。
        /// </summary>
        /// <remarks>
        /// 物种表在 <see cref="CreatureSpawns"/>（与命令应用共用一份真源）；显示名 / 占位色 / 类名取自原型实例
        /// （<see cref="Creature.DebugName"/> / <see cref="Creature.PlaceholderColor"/>），物种元数据的唯一真源
        /// 留在 <see cref="Creature"/> 派生类里，这里不再抄一份。
        /// 动物投放受阶段名单（<see cref="SetPlacementFilter"/> 的第二参数）限制：null = 不限制，空数组 = 全部隐藏；
        /// 不在名单里的物种不会出现在 <see cref="Selections"/> 里。
        /// </remarks>
        private Creature[] _creaturePrototypes = new Creature[0];

        private readonly HashSet<ushort> _unplaceable = new HashSet<ushort>();
        private readonly HashSet<ushort> _singlePlacement = new HashSet<ushort>();
        private readonly HashSet<ushort> _placeableGas = new HashSet<ushort>();

        /// <summary>阶段投放限制（<see cref="SetPlacementFilter"/>）：非 null 时只允许集合里的 typeId 投放。null = 无限制。
        /// 在 <see cref="IsPlaceable"/> 里与既有白名单取交集。</summary>
        private HashSet<ushort> _placementFilter;

        /// <summary>阶段动物限制（SetPlacementFilter 的第二参数）：非 null 时只允许集合里的物种下标投放；null = 不限制（空集合 = 全部隐藏）。</summary>
        private HashSet<int> _creatureFilter;

        /// <summary>当前可投放的粒子 typeId（注册顺序；<see cref="Selections"/> 的粒子段就是按它建的）。
        /// 阶段投放名单（<see cref="SetPlacementFilter"/>）会整体重算它。</summary>
        private ushort[] _placeable = new ushort[0];

        /// <summary>当前选中项在 <see cref="_selections"/> 里的下标（右键循环的游标）。</summary>
        private int _cursor;

        /// <summary>
        /// 当前全部可投放项：**先粒子类型、后动物物种**，顺序即右键循环顺序。白名单或阶段投放名单变化时
        /// 由 <see cref="BuildSelections"/> 整体重建（因此下标只在一次构建内有效，见 <see cref="ParticleSelectionEntry"/>）。
        /// 无可投项时为空数组（<see cref="CurrentSelection"/> 即 null）。
        /// </summary>
        private ParticleSelectionEntry[] _selections = Array.Empty<ParticleSelectionEntry>();

        private string _hudText;
        private Color _hudColor;
        private string _probeText;

        /// <summary>投放面板（<see cref="ParticleSelectionPanel"/>）的悬停上报（见 <see cref="SetHoveredSelection"/>）：
        /// 非 null 时 ProbeText 改显示它的 <see cref="ParticleSelectionEntry.DisplayName"/>；null = 没人在悬停。</summary>
        private ParticleSelectionEntry _hoveredSelection;

        /// <summary>光标是否落在网格内，以及光标格——<see cref="ProbeCell"/> 用同一次射线投影顺手记下
        /// （不重复算），<see cref="ApplyBrushOutline"/> 据此摆笔刷轮廓叠层。</summary>
        private bool _hoverCellValid;
        private int _hoverCellX;
        private int _hoverCellY;

        /// <summary>上一次向驱动请求的探针格与请求时的世界 tick（见 <see cref="ProbeCell"/> 的重发判据）。</summary>
        private bool _probedCellValid;
        private int _probedCellX;
        private int _probedCellY;
        private int _probedTick;

        private string _highSpeedHudText;
        private string[] _highSpeedReportLines;

        /// <summary>ProbeText 上的高速状态行（<see cref="DescribeHighSpeedRunning"/> 的产物）；
        /// 不在高速会话中为 null——它是 <see cref="ComposeProbeLine"/> 三段里的最高优先级那段。</summary>
        private string _highSpeedProbeText;

        /// <summary>结算面板是否有胜负结论行（<see cref="BuildHighSpeedReport"/> 时确定；沙盒阶段为 false）。</summary>
        private bool _highSpeedReportHasVerdict;

        /// <summary>胜负结论是否为过关（决定结算面板那一行的颜色）。</summary>
        private bool _highSpeedVerdictPass;

        /// <summary>暂停界面（暂停菜单）是否可见——由暂停按钮翻转（见 <see cref="TogglePauseMenu"/>，可见即暂停模拟），
        /// 实际显隐由 <see cref="PushHud"/> 每帧推给 HUD。</summary>
        private bool _pauseMenuVisible;

        /// <summary>光照面板（光照选择界面）是否可见——由光照按钮翻转（见 <see cref="ToggleLightPanel"/>），
        /// 默认 true（保持既有常显行为，按钮按下即隐藏）。**这只是玩家的意愿**：高速模式期间显示被
        /// <see cref="PushHud"/> 强制压成隐藏、本值冻结不动，退出会话后原样恢复。</summary>
        private bool _lightPanelVisible = true;

        private float _lastSinglePlacementTime = float.NegativeInfinity;

        /// <summary>相机上的画面接管组件（<see cref="CameraScreenFeed"/>，可选）。非 null 时相机画进纹理、
        /// <see cref="TryGetCell"/> 需要先把指针换算进那张图（见 Awake 的兜底查找）。</summary>
        private CameraScreenFeed _screenFeed;

        /// <summary>当前树木物种（Shift+滚轮切换；仅选中树木种子时参与投放与 HUD，见 <see cref="CycleTreeSpecies"/>）。</summary>
        private int _treeSpecies;

        /// <summary>是否处于**清除模式**（见 <see cref="EraseMode"/>）：为真时左键改为擦除。键盘口径是 E 按下置真、
        /// 抬起置假（<see cref="UpdateEraseMode"/>），投放面板的「清除」按钮翻转它（<see cref="ToggleEraseMode"/>）。</summary>
        private bool _eraseMode;

        /// <summary>笔刷半径下限（格）。</summary>
        public const int MinBrushRadius = 1;

        /// <summary>笔刷半径上限（格）。</summary>
        public const int MaxBrushRadius = 10;

        /// <summary>「单颗投放」白名单类型的投放冷却（秒）。</summary>
        public const float SinglePlacementCooldownSeconds = 0.5f;

        /// <summary>编辑是否被锁住：对话激活（<see cref="SuppressInput"/>）或高速模式进行中
        /// （帧里的 <see cref="HighSpeedFrame.Active"/>）。投放面板据此把**可见的**按钮变灰不可点
        /// （与 <see cref="SandboxHud.SetLightPanelInteractable"/> 同一条判据）。</summary>
        public bool EditingLocked { get { return SuppressInput || HighSpeedActive; } }

        /// <summary>高速模式是否进行中（读最新已发布帧；驱动尚未发布过帧时为 false）。</summary>
        private bool HighSpeedActive
        {
            get
            {
                SimulationFrame frame = _driver != null ? _driver.CurrentFrame : null;
                return frame != null && frame.HighSpeed.Active;
            }
        }

        /// <summary>是否处于**清除模式**：为真时左键走 <see cref="Erase"/>（在同一半径内清成空气）而不是投放。
        /// 键盘上按住 <c>E</c> 即进、松开即退；投放面板的「清除」按钮翻转它（<see cref="ToggleEraseMode"/>）。</summary>
        public bool EraseMode { get { return _eraseMode; } }

        /// <summary>**「清除」按钮**：翻转清除模式。供场景里的按钮 <c>onClick</c> 与投放面板接线。</summary>
        public void ToggleEraseMode() { SetEraseMode(!_eraseMode); }

        /// <summary>显式设置清除模式（等值不动）。</summary>
        public void SetEraseMode(bool erase)
        {
            if (_eraseMode == erase)
            {
                return;
            }

            _eraseMode = erase;
            RefreshHud();
        }

        /// <summary>当前笔刷半径（格）。</summary>
        public int BrushRadius
        {
            // 手改 YAML 越界时由 getter 兜底夹紧。
            get { return Mathf.Clamp(_brushRadius, MinBrushRadius, MaxBrushRadius); }
        }

        /// <summary>
        /// 当前全部可投放项（**先粒子类型、后动物物种**，顺序即右键循环顺序）——数组后备，遍历零分配。
        /// </summary>
        /// <remarks>
        /// 只读快照：条目在构建后不再变，白名单或阶段投放名单（<see cref="SetPlacementFilter"/>）变化时**整体重建**
        /// （因此 <see cref="ParticleSelectionEntry.Index"/> 与这里的下标只在一次构建内有效，缓存的条目会变陈旧）。
        /// 未来的投放选择按钮（粒子调色板 / 物种按钮）遍历本列表铺按钮，按下时把对应条目交给
        /// <see cref="Select(ParticleSelectionEntry)"/>。
        /// </remarks>
        public IReadOnlyList<ParticleSelectionEntry> Selections
        {
            get { return _selections; }
        }

        /// <summary>
        /// 当前选中的投放项（左键投放的就是它）；**没有任何可投项时为 null**。
        /// </summary>
        /// <remarks>
        /// 选中项的描述（名字 / 色块 / 是否动物 / 是否单颗投放）全在这一个对象上，取代了此前散落的
        /// 「当前 typeId / 是否选中动物」两个平行读数；投放路径每帧读它，不做查询。
        /// </remarks>
        public ParticleSelectionEntry CurrentSelection
        {
            get { return _cursor >= 0 && _cursor < _selections.Length ? _selections[_cursor] : null; }
        }

        /// <summary>
        /// 选中项实例**真的换了**时触发（重新选中同一项不触发）——未来按钮据此刷新高亮。
        /// 载荷是新的 <see cref="CurrentSelection"/>（绝不传 null：触发时必有选中项）。
        /// </summary>
        public event Action<ParticleSelectionEntry> SelectionChanged;

        /// <summary>选中第 <paramref name="index"/> 项（<see cref="Selections"/> 的下标）。</summary>
        /// <remarks>越界（含负数）返回 false 且**不改变当前选择**；成功时刷新 HUD，选中项实例变化才发 <see cref="SelectionChanged"/>。</remarks>
        public bool Select(int index)
        {
            if (index < 0 || index >= _selections.Length)
            {
                return false;
            }

            ParticleSelectionEntry previous = CurrentSelection;
            _cursor = index;
            ParticleSelectionEntry current = _selections[index];
            RefreshHud();

            if (!ReferenceEquals(previous, current))
            {
                SelectionChanged?.Invoke(current);
            }

            return true;
        }

        /// <summary>选中 <paramref name="entry"/> 这一项（按钮接线的主入口）。</summary>
        /// <remarks>
        /// 只认**当前 <see cref="Selections"/> 里的同一个实例**：null，或陈旧的 / 外来的实例
        /// （<see cref="SetPlacementFilter"/> 重建后残留的旧条目、别处新造的条目）一律返回 false 且不改变选择——
        /// 因此拿着过期条目点按钮不会选错东西。
        /// </remarks>
        public bool Select(ParticleSelectionEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            int index = entry.Index;
            if (index < 0 || index >= _selections.Length || !ReferenceEquals(_selections[index], entry))
            {
                return false;
            }

            return Select(index);
        }

        /// <summary>按粒子 typeId 选中可投放的粒子项（动物项不参与匹配）。找不到返回 false 且不改变选择。</summary>
        public bool SelectByTypeId(int typeId)
        {
            for (int i = 0; i < _selections.Length; i++)
            {
                ParticleSelectionEntry entry = _selections[i];
                if (!entry.IsCreature && entry.TypeId == typeId)
                {
                    return Select(i);
                }
            }

            return false;
        }

        /// <summary>
        /// 按名字选中一项：比显示名（<see cref="ParticleSelectionEntry.DisplayName"/>，中文）或类名
        /// （<see cref="ParticleSelectionEntry.ClassName"/>）——匹配规则与 <see cref="ResolveTypeNames"/> 的
        /// 「DebugName 或类名」一致，因此阶段 JSON / 场景接线用类名、界面用中文名都能选中。
        /// </summary>
        /// <remarks>null / 空名字、或两项都比不中的名字返回 false 且不改变选择。</remarks>
        public bool SelectByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (int i = 0; i < _selections.Length; i++)
            {
                ParticleSelectionEntry entry = _selections[i];
                if (entry.DisplayName == name || entry.ClassName == name)
                {
                    return Select(i);
                }
            }

            return false;
        }

        /// <summary>
        /// 投放面板报进来的**悬停投放项**（见 <see cref="SetHoveredSelection"/>）：非 null 时 ProbeText 改显示它的
        /// <see cref="ParticleSelectionEntry.DisplayName"/>；没人在悬停时为 null。
        /// </summary>
        /// <remarks>只读：悬停只影响显示来源，不改投放选择（<see cref="CurrentSelection"/> 仍由
        /// <see cref="Select(ParticleSelectionEntry)"/> 等入口决定）。</remarks>
        public ParticleSelectionEntry HoveredSelection { get { return _hoveredSelection; } }

        /// <summary>
        /// 投放面板（<c>ParticleSelectionPanel</c>）的悬停入口：指针停在某颗投放按钮上时把那一项报进来，
        /// 指针移开或面板收起时报 null（撤下）。
        /// </summary>
        /// <remarks>
        /// 只改**显示来源**，不动投放选择；同一实例重复上报不动作（面板每帧都会再报一次，见
        /// <see cref="ParticleSelectionPanel.Refresh"/>）。这里**不**主动推 HUD——值变了下一帧
        /// <see cref="PushHud"/> 自然带上，免得面板的 PointerEnter / PointerExit（跑在 EventSystem 里）
        /// 在帧中途插一次上屏。
        /// </remarks>
        public void SetHoveredSelection(ParticleSelectionEntry entry)
        {
            if (ReferenceEquals(_hoveredSelection, entry))
            {
                return;
            }

            _hoveredSelection = entry;
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

            // 结算面板的胜负行来源；找不到就只是不显示那一行（可选依赖，不报错）。
            if (_gameController == null)
            {
                _gameController = GetComponent<GameController>();
            }

            if (_gameController == null)
            {
                _gameController = FindObjectOfType<GameController>();
            }

            // HUD 的写入点；找不到就不推任何 HUD（可选依赖，不报错——输入与读数生成照常）。
            if (_hud == null)
            {
                _hud = GetComponent<SandboxHud>();
            }

            if (_hud == null)
            {
                _hud = FindObjectOfType<SandboxHud>();
            }

            // 相机画面接管（可选依赖）：它把相机改成画进纹理，指针 → 格子要先换算进那张图（见 TryGetCell）。
            // 组件就挂在相机上，因此先看自己引用的相机 / Camera.main，再退回全场景找（不报错——没有它时
            // 相机直接画屏，屏幕像素即相机像素，直传即可）。
            Camera feedCamera = _camera != null ? _camera : Camera.main;
            _screenFeed = feedCamera != null ? feedCamera.GetComponent<CameraScreenFeed>() : null;
            if (_screenFeed == null)
            {
                _screenFeed = FindObjectOfType<CameraScreenFeed>();
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

            // 动物物种的原型实例：显示名 / 占位色 / 类名都取自它，物种差异的唯一真源在 Creature 派生类里；
            // 物种全集与生成共用同一张表（CreatureSpawns），因此这里与命令应用不会漂移。
            _creaturePrototypes = new Creature[CreatureSpawns.Count];
            for (int i = 0; i < CreatureSpawns.Count; i++)
            {
                _creaturePrototypes[i] = CreatureSpawns.Create(i);
            }

            _selections = BuildSelections();
            Select(0);
        }

        private void Update()
        {
            // 笔刷轮廓由 UpdateEditing 按光标位置刷新（见 ApplyBrushOutline），所以每次进来先按「不可见」起手：
            // 任何提前返回（对话激活、世界没就绪、高速模式锁编辑）都不会留下上一帧的白圈。
            _hoverCellValid = false;

            UpdateEraseMode();

            UpdateEditing();

            ApplyBrushOutline();
            PushHud();
        }

        /// <summary>
        /// 清除模式的键盘口径：<c>E</c> 按下置真、抬起置假。用**边沿**事件而不是按住查询
        /// （<c>Input.GetKey</c>）——按住查询每帧都会把面板按钮翻转出来的值立刻抹回「E 没按住 = 假」。
        /// 放在 <see cref="UpdateEditing"/> 之前、不受 <see cref="SuppressInput"/> 与高速锁编辑影响，
        /// 因此松开 E 一定会把标记清掉，不会留下「对话结束后第一笔莫名擦除」的残留。
        /// </summary>
        private void UpdateEraseMode()
        {
            if (Input.GetKeyDown(KeyCode.E))
            {
                SetEraseMode(true);
            }
            else if (Input.GetKeyUp(KeyCode.E))
            {
                SetEraseMode(false);
            }
        }

        /// <summary>
        /// 把这一帧算好的显示内容推给场景里的 <see cref="SandboxHud"/>（每帧无条件推一次，让它自己按状态去重）：
        /// 可见性、暂停界面与光照面板的显隐、光标格读数、鼠标跟随组、高速读数与结算面板、光照面板的可用性。
        /// 未接线 HUD 时整段跳过——本类照常处理输入、只是没人显示。
        /// </summary>
        /// <remarks>
        /// 高速读数与结算面板都以驱动的**实时**状态兜底（同旧 IMGUI）：任何结束会话的路径
        /// （确认退出、世界重建丢掉会话）都会立刻把它们隐藏掉，不留一帧残影。
        /// </remarks>
        private void PushHud()
        {
            if (_hud == null)
            {
                return;
            }

            bool highSpeedActive = HighSpeedActive;

            _hud.SetHudVisible(_drawHud);
            _hud.SetPauseMenuVisible(_drawHud && _pauseMenuVisible);
            // 高速模式**强制隐藏**光照面板（会话结果依赖光照输入，见 SetLightPanelVisible 的锁）：
            // 不管 _lightPanelVisible 记着什么，会话进行中都不显示——退出后按原状态恢复。
            _hud.SetLightPanelVisible(_drawHud && _lightPanelVisible && !highSpeedActive);
            _hud.SetProbeText(_drawHud ? BuildProbeLine() : null);
            _hud.SetCursorHint(_hudText, _hudColor);
            _hud.SetHighSpeedStatus(_drawHud && highSpeedActive ? _highSpeedHudText : null);

            if (_drawHud && highSpeedActive && _highSpeedReportLines != null && _highSpeedReportLines.Length > 0)
            {
                _hud.ShowHighSpeedReport(_highSpeedReportLines, _highSpeedReportHasVerdict, _highSpeedVerdictPass);
            }
            else
            {
                _hud.HideHighSpeedReport();
            }

            // 对话激活（本组件完全不响应输入）与高速模式进行中都把光照面板变灰——两者的会话结果都依赖光照输入。
            // 判据只有 EditingLocked 一份：投放面板（ParticleSelectionPanel）读的是同一个属性。
            _hud.SetLightPanelInteractable(!EditingLocked);
        }

        /// <summary>ProbeText 当前该显示的那一段（三段按优先级取一，判据见 <see cref="ComposeProbeLine"/>）。</summary>
        private string BuildProbeLine()
        {
            return ComposeProbeLine(
                _highSpeedProbeText,
                _hoveredSelection != null ? _hoveredSelection.DisplayName : null,
                _probeText);
        }

        /// <summary>
        /// ProbeText 的内容按优先级取一：**高速状态行 &gt; 悬停的投放项名 &gt; 光标格读数**（三段都空则空文本）。
        /// </summary>
        /// <param name="highSpeedLine">高速会话运行中的状态行（<see cref="DescribeHighSpeedRunning"/>）；不在会话中为 null。</param>
        /// <param name="hoveredName">投放面板报上来的悬停项显示名（见 <see cref="SetHoveredSelection"/>）；没人在悬停为 null。</param>
        /// <param name="cellReadout">光标格读数（见 <see cref="ProbeCell"/>）；光标在视口外时也有值。</param>
        /// <remarks>
        /// 两条优先级都来自「哪一段此刻才说得通」：① 高速会话跑着时编辑被锁
        /// （<see cref="EditingLocked"/>）、投放面板点不动，状态行是此刻唯一有意义的读数；
        /// ② 指针停在投放按钮上时光标格读数（面板底下的格子）没有意义，该按钮绑定的投放项名才是玩家在问的。
        /// 空串与 null 一视同仁（HUD 的空文本就是「不显示」，见 <see cref="SandboxHud.SetProbeText"/> 的去重口径），
        /// 因此返回的要么是这三段之一，要么是 <see cref="string.Empty"/>，不会是空白串。
        /// </remarks>
        public static string ComposeProbeLine(string highSpeedLine, string hoveredName, string cellReadout)
        {
            if (!string.IsNullOrEmpty(highSpeedLine))
            {
                return highSpeedLine;
            }

            if (!string.IsNullOrEmpty(hoveredName))
            {
                return hoveredName;
            }

            return string.IsNullOrEmpty(cellReadout) ? string.Empty : cellReadout;
        }

        /// <summary>编辑输入：热键、相机平移、光标读数、切类型 / 调半径 / 投放 / 擦除。</summary>
        private void UpdateEditing()
        {
            if (SuppressInput)
            {
                return;
            }

            if (_placeable.Length == 0 || _driver == null || _renderer == null)
            {
                return;
            }

            SimulationFrame frame = _driver.CurrentFrame;
            if (frame == null)
            {
                return;
            }

            // 高速模式热键：不在模式中时按 H 进入；结算完成后按 Enter（或 H）确认退出。
            HandleHighSpeedKeys(frame);

            RefreshHighSpeed(frame);

            // 暂停热键：这里只负责「按下了就切一下」，具体冻结/恢复由驱动在下一 tick 边界应用 SetPaused 命令。
            if (_pauseKey != KeyCode.None && Input.GetKeyDown(_pauseKey))
            {
                SubmitPaused(!frame.World.Paused);
            }

            // 光照面板热键：与场景里的「光照」按钮同一个入口（ToggleLightPanel）——因此按钮改语义时热键跟着改，
            // 不在这里自己翻转 _lightPanelVisible。高速模式期间这个入口自己会拒绝（见 SetLightPanelVisible 的锁），
            // 所以放在锁编辑的拦截之前不影响语义，只与「按钮在模式中照旧可点」保持同一个位置。
            if (_lightPanelKey != KeyCode.None && Input.GetKeyDown(_lightPanelKey))
            {
                ToggleLightPanel();
            }

            // 相机平移：只动相机、不碰世界，所以放在高速模式的锁编辑拦截**之前**——
            // 高速模式锁的是编辑，观看视角（平移 + 光标读数）跟着解锁一起禁用没有意义。
            UpdateCameraPan(frame.World.Width);

            // 高速模式锁编辑：在这里**统一拦截**——右键切换、滚轮调半径、左键投放/擦除一概跳过，
            // 只保留上面的空格暂停、光照面板热键（模式期间被锁死，按了也不会打开面板）与下面只读的光标读数。
            // 不是每个输入分支各加一个判断。
            if (frame.HighSpeed.Active)
            {
                ProbeCell(frame, Input.mousePosition);

                // 锁编辑时不画笔刷轮廓：白圈会被读成「这里能落笔」（只读光标读数照旧）。
                _hoverCellValid = false;
                return;
            }

            // 普通编辑模式不允许调模拟速度（恒 1×）——速度只由高速模式钉 16× / 退出回 1×，
            // 因此这里没有 = / - / Tab 的热键处理；SpeedController 的档位 API 只被内核会话调用。

            ProbeCell(frame, Input.mousePosition);

            if (Input.GetMouseButtonDown(1))
            {
                CycleType();
            }

            // 滚轮调半径；按住 Shift（左右皆可）时改为循环切换树木物种（见 TreeSpecies）。
            float scroll = Input.mouseScrollDelta.y;
            if (scroll > 0f)
            {
                if (IsShiftHeld())
                {
                    CycleTreeSpecies(1);
                }
                else
                {
                    AdjustRadius(1);
                }
            }
            else if (scroll < 0f)
            {
                if (IsShiftHeld())
                {
                    CycleTreeSpecies(-1);
                }
                else
                {
                    AdjustRadius(-1);
                }
            }

            // HUD 面板（光照面板 / 三个按钮）正在被使用时**屏蔽放置**：左键点进按钮或拖滑块时指针同时也在网格上，
            // 不挡就会既改 UI 又在底下画一笔。判据只有一条：指针当前就在会吃点击的面板矩形内。
            bool pointerOverPanel = _hud != null && _hud.IsPointerOverPanel(Input.mousePosition);

            if (!pointerOverPanel
                && Input.GetMouseButton(0)
                && TryGetCell(Input.mousePosition, frame.World.Width, frame.World.Height, out int x, out int y))
            {
                SubmitPlacement(x, y);
            }
        }

        /// <summary>
        /// 在 (cx, cy) 落一笔：清除模式走 <see cref="SimulationCommandKind.EraseDisk"/>，
        /// 选中动物物种走 <see cref="SimulationCommandKind.SpawnCreature"/>，否则走
        /// <see cref="SimulationCommandKind.PaintDisk"/>（单颗投放白名单的类型由命令里的 Flag 表达）。
        /// </summary>
        /// <remarks>
        /// 单颗投放 / 生成实体的 <see cref="SinglePlacementCooldownSeconds"/> 冷却在这里判——它是**输入节流**（按住左键
        /// 不该每帧落一颗），因此归输入侧；真正写世界的是驱动在下一 tick 边界应用的命令。
        /// 暂停中画完还要补一条 <see cref="SimulationCommandKind.PresentNow"/>：命令同批入队，
        /// 驱动按序应用后再发布帧，因此刚画下去的东西当帧就能看到。
        /// </remarks>
        /// <returns>是否真的入队了一条投放 / 擦除命令（冷却中或无可投项时 false）。</returns>
        public bool SubmitPlacement(int cx, int cy)
        {
            if (_driver == null)
            {
                return false;
            }

            // 清除模式（E 按住或面板「清除」槽）：纯擦除，不看当前投放项、不受单颗白名单影响。
            if (_eraseMode)
            {
                _driver.Submit(SimulationCommand.EraseDisk(cx, cy, BrushRadius));
                SubmitPresentNowIfPaused();
                return true;
            }

            ParticleSelectionEntry selection = CurrentSelection;
            if (selection == null)
            {
                // 没有任何可投项：没有东西可画。
                return false;
            }

            if (selection.IsCreature)
            {
                // 选中动物物种：生成一只实体（不写格），与单颗投放共用冷却。
                if (!TryConsumeSinglePlacement())
                {
                    return false;
                }

                _driver.Submit(SimulationCommand.SpawnCreature(selection.CreatureIndex, cx, cy));
                SubmitPresentNowIfPaused();
                return true;
            }

            // 单颗投放类（草种子 / 树木种子）与生成实体共用冷却：按住左键也只会每 500ms 落一颗。
            if (selection.SinglePlacement && !TryConsumeSinglePlacement())
            {
                return false;
            }

            _driver.Submit(SimulationCommand.PaintDisk(
                cx, cy, BrushRadius, selection.TypeId, selection.SinglePlacement, _treeSpecies));
            SubmitPresentNowIfPaused();
            return true;
        }

        /// <summary>暂停中改了世界后提交一次立即上屏（推进中不必：下一帧自然会发布）。</summary>
        private void SubmitPresentNowIfPaused()
        {
            SimulationFrame frame = _driver.CurrentFrame;
            if (frame != null && frame.World.Paused)
            {
                _driver.Submit(SimulationCommand.PresentNow());
            }
        }

        /// <summary>
        /// 单颗投放 / 生成实体的冷却闸门：冷却未到返回 false，否则记下本次时间并返回 true。
        /// 用 <see cref="Time.unscaledTime"/>——冷却按真实时间流逝，暂停时也照常计时。
        /// </summary>
        private bool TryConsumeSinglePlacement()
        {
            if (Time.unscaledTime - _lastSinglePlacementTime < SinglePlacementCooldownSeconds)
            {
                return false;
            }

            _lastSinglePlacementTime = Time.unscaledTime;
            return true;
        }

        /// <summary>提交暂停 / 恢复命令（值类型：驱动在下一 tick 边界应用）。</summary>
        public void SubmitPaused(bool paused)
        {
            if (_driver != null)
            {
                _driver.Submit(SimulationCommand.SetPaused(paused));
            }
        }

        /// <summary>
        /// 把 <see cref="UpdateEditing"/> 记下的光标格推给渲染器的**笔刷轮廓叠层**
        /// （<see cref="ParticleRenderer.SetBrushOutline"/>）：以光标格为圆心、当前 <see cref="BrushRadius"/>
        /// 为半径的白色像素圆，圈上的格恰好是左键会写到的格。
        /// </summary>
        /// <remarks>
        /// 叠层画在**场景里**（渲染器自建的 sprite，1 像素 = 1 格、<c>sortingOrder</c> 压在世界 quad 之上），
        /// 不是屏幕空间的白块：因此与像素场景逐格对齐，相机缩放 / 平移都不用换算，也不会糊在粒子上面。
        /// 光标不在网格内、HUD 关了（<see cref="_drawHud"/>）或渲染器缺失时撤下叠层。
        /// </remarks>
        private void ApplyBrushOutline()
        {
            if (_renderer == null)
            {
                return;
            }

            if (!_drawHud || !_hoverCellValid)
            {
                _renderer.HideBrushOutline();
                return;
            }

            _renderer.SetBrushOutline(_hoverCellX, _hoverCellY, BrushRadius);
        }

        /// <summary>
        /// 相机左右平移：按住 <see cref="_panLeftKeys"/> / <see cref="_panRightKeys"/> 里的任一键（默认左方向键 + A、
        /// 右方向键 + D），按 <see cref="_panSpeed"/> 格/秒把相机沿沙箱水平方向推，同时按住两个方向即抵消（不动）。
        /// 相机位置的写入与「视口不许越出沙箱左右边界」的夹取都在渲染器（<see cref="ParticleRenderer.PanCamera"/>），
        /// 本方法只管按键 → 格数：只动相机、不碰世界（宽度取自帧），因此不参与确定性、也不受暂停影响。
        /// </summary>
        private void UpdateCameraPan(int worldWidth)
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
            _renderer.PanCamera(worldWidth, direction * Mathf.Max(0f, _panSpeed) * Time.deltaTime);
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
        private void HandleHighSpeedKeys(SimulationFrame frame)
        {
            HighSpeedFrame highSpeed = frame.HighSpeed;

            if (!highSpeed.Active)
            {
                if (_highSpeedKey != KeyCode.None && Input.GetKeyDown(_highSpeedKey))
                {
                    SubmitEnterHighSpeed();
                }

                return;
            }

            if (!highSpeed.Completed)
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
                SubmitHighSpeedExit();
            }
        }

        /// <summary>提交进入高速模式命令（幂等；暂停中进入由驱动先解除暂停）。</summary>
        public void SubmitEnterHighSpeed()
        {
            if (_driver != null)
            {
                _driver.Submit(SimulationCommand.EnterHighSpeed());
            }
        }

        /// <summary>确认结算：提交退出命令（驱动把倍速钉回 1×），清掉结算缓存并解除编辑锁定。
        /// **保持暂停**——恢复推进由玩家自己按空格决定。</summary>
        public void SubmitHighSpeedExit()
        {
            if (_driver != null)
            {
                _driver.Submit(SimulationCommand.ExitHighSpeed());
            }

            _highSpeedHudText = null;
            _highSpeedProbeText = null;
            _highSpeedReportLines = null;
            _highSpeedReportHasVerdict = false;
        }

        /// <summary>
        /// 刷新高速模式 HUD：运行时每帧重建左下角那一行进度读数与 ProbeText 上的**高速状态行**；结算完成后
        /// **只建一次**结算行（帧里的 <see cref="HighSpeedFrame.Completed"/> 是终态，行内容不会再变）。
        /// 不在模式中时清空三个缓存。
        /// </summary>
        private void RefreshHighSpeed(SimulationFrame frame)
        {
            HighSpeedFrame highSpeed = frame.HighSpeed;
            if (!highSpeed.Active)
            {
                _highSpeedHudText = null;
                _highSpeedProbeText = null;
                _highSpeedReportLines = null;
                _highSpeedReportHasVerdict = false;
                return;
            }

            _highSpeedHudText = DescribeHighSpeed(
                highSpeed.ElapsedLogicTicks,
                highSpeed.DurationLogicTicks,
                highSpeed.EffectiveSpeed,
                highSpeed.LatestSurfaceTemperature,
                highSpeed.LatestAverageHumidity);

            // ProbeText 上的状态行：同一份帧数据、另一种口径（见 DescribeHighSpeedRunning）——
            // 编辑被锁的会话期间它是 ProbeText 唯一显示的内容（见 ComposeProbeLine）。
            _highSpeedProbeText = DescribeHighSpeedRunning(
                highSpeed.EffectiveSpeed,
                highSpeed.LatestSurfaceTemperature,
                highSpeed.LatestAverageHumidity);

            if (highSpeed.Completed && _highSpeedReportLines == null)
            {
                _highSpeedReportLines = BuildHighSpeedReport();
            }
        }

        /// <summary>
        /// 左下角进度行的文案：<c>高速 1234/6000 ×7.6 地表 12.3° 湿度 0.456</c>。
        /// 倍速只显示**实测**吞吐（<see cref="HighSpeedFrame.EffectiveSpeed"/>，EMA 平滑的一位小数）——
        /// 高速模式恒钉 16× 目标，目标没有显示价值；温度 / 湿度还没采到样本（<see cref="float.NaN"/>）时显示 <c>--</c>，不猜数字。
        /// </summary>
        private static string DescribeHighSpeed(int elapsed, int duration, float effectiveSpeed, float temperature, float humidity)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "高速 {0}/{1} ×{2} {3}",
                elapsed, duration, effectiveSpeed.ToString("0.0", CultureInfo.InvariantCulture),
                DescribeEnvironment(temperature, humidity));
        }

        /// <summary>
        /// ProbeText 上的**高速状态行**：<c>正在运转 ×7.6 地表 12.3° 湿度 0.456</c>。
        /// </summary>
        /// <remarks>
        /// **格式固定**：状态词 + 实测倍速 + 地表温度 + 湿度。倍速是 <see cref="HighSpeedFrame.EffectiveSpeed"/>
        /// （EMA 平滑的一位小数），**不显示目标档**（会话恒钉 16×，目标没有显示价值）；温度 / 湿度未采样
        /// （<see cref="float.NaN"/>）时显示 <c>--</c>。**进度不进这行**（已跑 tick / 总时长留给了左下角的
        /// <see cref="DescribeHighSpeed"/>）：会话期间编辑被锁，玩家只需要知道「还在跑、跑多快、环境怎么样」。
        /// </remarks>
        public static string DescribeHighSpeedRunning(float effectiveSpeed, float temperature, float humidity)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "正在运转 ×{0} {1}",
                effectiveSpeed.ToString("0.0", CultureInfo.InvariantCulture),
                DescribeEnvironment(temperature, humidity));
        }

        /// <summary>
        /// 环境尾段（两条高速读数行共用）：<c>地表 12.3° 湿度 0.456</c>——温度一位小数带 <c>°</c>、
        /// 湿度三位小数；未采样（<see cref="float.NaN"/>）时显示 <c>--</c>，不猜数字。
        /// 数值固定用不变文化格式化，免得跟着系统区域设置漂出逗号小数点。
        /// </summary>
        private static string DescribeEnvironment(float temperature, float humidity)
        {
            string surface = float.IsNaN(temperature)
                ? "--"
                : temperature.ToString("0.0", CultureInfo.InvariantCulture) + "°";
            string wetness = float.IsNaN(humidity)
                ? "--"
                : humidity.ToString("0.000", CultureInfo.InvariantCulture);

            return string.Format(CultureInfo.InvariantCulture, "地表 {0} 湿度 {1}", surface, wetness);
        }

        /// <summary>
        /// 高速模式结算行：**只有一行**结论——过关「过关！」，未过关为阶段 JSON 该检查项配的
        /// 「失败原因」文案（经 <see cref="GameController.LastReason"/> 透传；未配则回退到详细理由）。
        /// 不逐类型列产生 / 死亡数；沙盒阶段（无胜负结论）返回空数组，结算面板因此不显示。
        /// 过关与否记进 <see cref="_highSpeedVerdictPass"/> 供 <see cref="SandboxHud.ShowHighSpeedReport"/> 上色。
        /// </summary>
        private string[] BuildHighSpeedReport()
        {
            _highSpeedReportHasVerdict = _gameController != null && _gameController.HasVerdict;
            if (!_highSpeedReportHasVerdict)
            {
                _highSpeedVerdictPass = false;
                return Array.Empty<string>();
            }

            _highSpeedVerdictPass = _gameController.LastResult;
            return _highSpeedVerdictPass
                ? new[] { "过关！" }
                : new[] { _gameController.LastReason ?? "未过关" };
        }

        /// <summary>切到下一个可投放项（粒子类型之后接动物物种，到末尾回到开头）。</summary>
        public void CycleType()
        {
            if (_selections.Length == 0)
            {
                return;
            }

            Select((_cursor + 1) % _selections.Length);
        }

        /// <summary>滚轮每格调 <paramref name="steps"/> 格半径，结果夹在 [<see cref="MinBrushRadius"/>, <see cref="MaxBrushRadius"/>]。</summary>
        public void AdjustRadius(int steps)
        {
            _brushRadius = Mathf.Clamp(BrushRadius + steps, MinBrushRadius, MaxBrushRadius);
            RefreshHud();
        }

        /// <summary>
        /// **暂停按钮**：翻转暂停界面（暂停菜单）的显隐。界面可见即暂停模拟、收起即恢复
        /// （菜单本身就是「暂停」的语义，见 <see cref="SetPauseMenuVisible"/>），与空格热键
        /// （<see cref="_pauseKey"/>）走同一对 <see cref="ParticleSimulationDriver.Pause"/> /
        /// <see cref="ParticleSimulationDriver.Resume"/>。供场景里的按钮 <c>onClick</c> 接线。
        /// </summary>
        public void TogglePauseMenu()
        {
            SetPauseMenuVisible(!_pauseMenuVisible);
        }

        /// <summary>
        /// 显式设置暂停界面的显隐（按钮想拆成「开 / 关」两个时用），并同步模拟的暂停状态：
        /// 可见 → 提交 <see cref="SimulationCommandKind.SetPaused"/>（暂停）、收起 → 提交恢复。
        /// <para><b>两处豁免</b>：① 高速模式期间只切显示、不动模拟——会话自持推进状态（进入时解除暂停、
        /// 跑满自动暂停），中途被外部暂停会卡在跑不满时长的死状态；② 对话激活（<see cref="SuppressInput"/>）
        /// 时同样只切显示——暂停记账归对话系统，这里抢会破坏它的「只恢复自己暂停的」语义。立即推给 HUD。</para>
        /// </summary>
        public void SetPauseMenuVisible(bool visible)
        {
            _pauseMenuVisible = visible;

            if (!SuppressInput && !HighSpeedActive)
            {
                SubmitPaused(visible);
            }

            PushHud();
        }

        /// <summary>
        /// **光照选择按钮**：翻转右上角光照面板（光照选择界面）的显隐。隐藏不丢值——面板改的是
        /// <see cref="ParticleLightDriver"/> 上的字段，重新显示即原样回来。供场景里的按钮 <c>onClick</c> 接线。
        /// <para>高速模式期间整个请求被锁吞掉（见 <see cref="SetLightPanelVisible"/>），因此按钮与 L 热键
        /// 都无法把面板切回来。</para>
        /// </summary>
        public void ToggleLightPanel()
        {
            SetLightPanelVisible(!_lightPanelVisible);
        }

        /// <summary>
        /// 显式设置光照面板的显隐（无光源驱动器时 HUD 仍会保持隐藏）。立即推给 HUD。
        /// <para><b>高速模式锁</b>：会话进行中整个请求被吞掉、状态冻结，显示另由 <see cref="PushHud"/>
        /// 强制为隐藏——会话结果依赖光照输入，跑一场高速前先把光照面板收起来、跑完才按原状态放回来。
        /// 锁在**这一个入口**上，因此按钮 <c>onClick</c> 与 <see cref="_lightPanelKey"/> 热键行为天然一致。</para>
        /// </summary>
        public void SetLightPanelVisible(bool visible)
        {
            if (HighSpeedActive)
            {
                return;
            }

            _lightPanelVisible = visible;
            PushHud();
        }

        /// <summary>
        /// **高速模式按钮**：进入高速模式（等价于按 <see cref="_highSpeedKey"/>）。对话激活（<see cref="SuppressInput"/>）
        /// 或无驱动时不做任何事。会话本身幂等——已在模式中再调用无事发生。供场景里的按钮 <c>onClick</c> 接线。
        /// </summary>
        public void EnterHighSpeedMode()
        {
            if (_driver == null || SuppressInput)
            {
                return;
            }

            SubmitEnterHighSpeed();

            // 按钮的 onClick 跑在 Update 之外（EventSystem），不补这一下的话光照面板要等下一帧才被
            // PushHud 的锁收起来——点「高速」后有一帧仍看得见面板。热键路径本来就在 Update 内、同帧推到。
            PushHud();
        }

        /// <summary>左右 Shift 是否按住（legacy Input Manager，没有 Input System）。</summary>
        private static bool IsShiftHeld()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }

        /// <summary>
        /// 循环切换树木物种（0..<see cref="TreeSpecies.SpeciesCount"/>-1）并刷新 HUD。
        /// 与当前选中类型无关、始终生效（无害）；物种只在选中树木种子时参与投放与 HUD 显示。
        /// </summary>
        public void CycleTreeSpecies(int steps)
        {
            int count = TreeSpecies.SpeciesCount;
            _treeSpecies = ((_treeSpecies + steps) % count + count) % count;
            RefreshHud();
        }

        /// <summary>
        /// 按名字列表限制可投放的粒子类型；动物物种不受限制（等价于
        /// <see cref="SetPlacementFilter(string[], string[])"/> 的第二参数为 null）。
        /// </summary>
        /// <param name="allowedNames">允许投放的类型名；null 或空数组 = 清除限制、恢复默认白名单行为。</param>
        public void SetPlacementFilter(string[] allowedNames)
        {
            SetPlacementFilter(allowedNames, null);
        }

        /// <summary>按名字列表限制可投放的粒子类型与动物物种。</summary>
        /// <param name="allowedNames">粒子白名单；null 或空数组 = 清除粒子限制（与旧行为一致）。</param>
        /// <param name="allowedCreatureNames">动物物种白名单（按 DebugName 或 Creature 子类类名匹配）；
        /// null = 不限制动物；空数组 = 本阶段没有任何动物可投。</param>
        /// <remarks>
        /// 粒子名单生效的是「名单 ∩ 既有白名单」——名单只会**缩小**可投范围，不会把草 / 湿土等原本不可投放的类型放进来；
        /// 查不到的类型名记警告并跳过（同 <see cref="ResolveTypeNames"/>）。动物名单同样按名字匹配，未知物种名记警告并跳过。
        /// 重算后自动选中第一个可投项。
        /// <para>粒子名单与既有白名单的交集为空时记错误并**忽略这次调用**（<see cref="_placementFilter"/>、可投放列表
        /// 与 <see cref="_creatureFilter"/> 全部保持原样）——既不能让编辑器落进「一个类型都选不了」的死状态，
        /// 也不能因为一次配置错误就悄悄把限制丢掉。</para>
        /// <para>成功时 <see cref="Selections"/> **整体重建**（旧条目作废、<see cref="SelectionChanged"/> 按新选中项触发），
        /// 动物物种段只保留白名单里的物种、照旧接在粒子段之后；空动物白名单即整段隐藏。</para>
        /// <para><b>调用时机</b>：必须在 <c>Awake</c> 之后（注册表已由 <c>BeforeSceneLoad</c> 钩子填好、
        /// 本组件也已完成初始白名单解析）。<see cref="GameController"/> 在阶段启动时调用，晚于所有 <c>Awake</c>，安全。</para>
        /// </remarks>
        public void SetPlacementFilter(string[] allowedNames, string[] allowedCreatureNames)
        {
            HashSet<ushort> previousFilter = _placementFilter;
            bool hasNameFilter = allowedNames != null && allowedNames.Length > 0;

            HashSet<ushort> nextFilter = null;
            if (hasNameFilter)
            {
                nextFilter = new HashSet<ushort>();
                ResolveTypeNames(allowedNames, nextFilter);
            }

            // CollectPlaceableTypes 读的是 _placementFilter：先临时落字段算出新列表，成功后再正式提交。
            _placementFilter = nextFilter;
            ushort[] nextPlaceable = CollectPlaceableTypes();

            if (hasNameFilter && nextPlaceable.Length == 0)
            {
                Debug.LogError(
                    "[ParticleEditor] SetPlacementFilter：名单与既有白名单的交集为空，本次调用被忽略，保持当前可投放类型不变。");
                _placementFilter = previousFilter;
                return;
            }

            if (nextPlaceable.Length == 0)
            {
                Debug.LogError(
                    "[ParticleEditor] SetPlacementFilter：没有任何可投放的粒子类型。请确认 ParticleRegistration.RegisterAll() 已执行。");
                _placementFilter = previousFilter;
                return;
            }

            HashSet<int> nextCreatureFilter;
            if (allowedCreatureNames == null)
            {
                nextCreatureFilter = null;
            }
            else
            {
                nextCreatureFilter = new HashSet<int>();
                for (int n = 0; n < allowedCreatureNames.Length; n++)
                {
                    string name = allowedCreatureNames[n];
                    bool found = false;
                    for (int i = 0; i < _creaturePrototypes.Length; i++)
                    {
                        Creature prototype = _creaturePrototypes[i];
                        if (prototype.DebugName == name || prototype.GetType().Name == name)
                        {
                            nextCreatureFilter.Add(i);
                            found = true;
                        }
                    }

                    if (!found)
                    {
                        Debug.LogWarning(
                            $"[ParticleEditor] 动物白名单里的物种名 \"{name}\" 未在物种表中找到，已忽略。");
                    }
                }
            }

            // 粒子状态与动物限制一起提交，再整体重建可投项（旧条目全部作废）并选中新的第一项。
            _placementFilter = nextFilter;
            _placeable = nextPlaceable;
            _creatureFilter = nextCreatureFilter;
            _selections = BuildSelections();
            Select(0);
        }

        /// <summary>
        /// 刷新左上角读数：光标在网格内则显示该格的信息，否则显示「视口外」。
        /// 只有这一行格子读数——清除模式与当前倍速都不在这里显示（清除模式看光标旁的色块与面板高亮，
        /// 实测吞吐看高速模式左下角读数行，见 <see cref="DescribeHighSpeed"/>）。
        /// </summary>
        /// <remarks>
        /// 读数**不读活世界**：光标格变化时提交一条 <see cref="SimulationCommandKind.ProbeCell"/> 请求，
        /// 驱动在该命令**被应用的那一刻**采一份快照并写进下一帧的 <see cref="WorldFrame.Probe"/>；这里只在请求格与
        /// 帧里带回的格一致时显示（否则等下一帧，光标刚移过去的那一帧显示上一次的读数——实际不可见）。
        /// 快照按命令批次采一次、之后不再重采，因此这里在**光标换格 / 世界推进了一 tick / 帧里没有有效读数**时
        /// 重发一次请求（没有 tick 的渲染帧不发命令），读数才能持续跟着世界走。
        /// </remarks>
        private void ProbeCell(SimulationFrame frame, Vector3 screenPosition)
        {
            bool inGrid = TryGetCell(screenPosition, frame.World.Width, frame.World.Height, out int x, out int y);

            // 光标格顺手留给 ApplyBrushOutline 的笔刷轮廓（与读数同一次射线，不重复投影）。
            _hoverCellValid = inGrid;
            _hoverCellX = x;
            _hoverCellY = y;

            if (!inGrid)
            {
                _probeText = "视口外";
                _probedCellValid = false;
                return;
            }

            bool cellChanged = !_probedCellValid || _probedCellX != x || _probedCellY != y;

            // 换格、世界又推进了一 tick（读数会变）、或帧里根本没有有效读数（刚重建世界）⇒ 重新请求。
            // 驱动在**命令被应用的那一刻**采快照、之后不再重采（见 ParticleSimulationDriver 的 ProbeCell 分支），
            // 所以读数要与世界同步就得每个 tick 重发一次；而按 tick 判据重发不会让「没有 tick 的渲染帧」
            // 白白制造命令（暂停中不移动光标同样一条命令都不发）。
            bool needsProbe = cellChanged
                || frame.World.Tick != _probedTick
                || !frame.World.Probe.Valid;
            if (needsProbe)
            {
                _probedCellValid = true;
                _probedCellX = x;
                _probedCellY = y;
                _probedTick = frame.World.Tick;
                if (_driver != null)
                {
                    _driver.Submit(SimulationCommand.ProbeCell(x, y));
                }
            }

            CellProbe probe = frame.World.Probe;
            _probeText = probe.Valid && probe.X == x && probe.Y == y ? DescribeCell(probe) : "读取中…";
        }

        /// <summary>
        /// 读数行的文案：坐标 + 粒子中文名 + 温度 + 热量 + 湿度。
        /// 温度是导出量 <c>T = Q / 热容</c>，热量是格子里存的那份 Q，湿度对这一格而言（固/液恒为 0）。
        /// 粒子名取 <see cref="ParticleTypeInfo.DebugName"/>（每类型一个中文名，见各粒子类型的 <c>Info</c>），
        /// 空气与未注册 / 空槽一律显示「空气」。
        /// 数值固定用不变文化格式化，免得跟着系统区域设置漂出逗号小数点。
        /// </summary>
        /// <param name="probe">帧里带回的探针快照（<see cref="WorldFrame.Probe"/>）；无效快照按空气显示。</param>
        public static string DescribeCell(in CellProbe probe)
        {
            ParticleType[] types = ParticleRegistry.Types;
            ushort typeId = probe.TypeId;

            string name = typeId != ParticleRegistry.AirTypeId && typeId < types.Length && types[typeId] != null
                ? DisplayName(types[typeId])
                : "空气";

            return string.Format(
                CultureInfo.InvariantCulture,
                "({0},{1})  {2}  温度={3:0.0}  热量={4:0.00}  湿度={5:0.000}",
                probe.X, probe.Y, name, probe.Temperature, probe.Heat, probe.Humidity);
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

            // 阶段限制（GameController.SetPlacementFilter）：名单外的类型一律不可投放。
            // _placementFilter == null（默认）时这一步整段跳过，行为与加过滤前逐位一致。
            if (_placementFilter != null && !_placementFilter.Contains((ushort)typeId))
            {
                return false;
            }

            // Gas 默认不可手刷；白名单（_placeableGasTypes，默认火）里的 Gas 例外放行。
            bool isGas = (type.Info.Tags & ParticleTags.Gas) != 0;
            return !isGas || _placeableGas.Contains((ushort)typeId);
        }

        /// <summary>
        /// 把 Inspector 上的类型名列表解析成 typeId 集合：按 <see cref="ParticleTypeInfo.DebugName"/>（中文名）
        /// 或 <see cref="ParticleType.ClassName"/>（类名）匹配注册表里的每一个类型。找不到的名字记一条警告并跳过
        /// （拼错类型名不会静默失效）。
        /// 注册表由 <c>BeforeSceneLoad</c> 钩子填充，本方法只在 <c>Awake</c> 与 <c>SetPlacementFilter</c> 里调用，因此读得到全部类型。
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

                    if (type.Info.DebugName == name || type.ClassName == name)
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

        /// <summary>该物种下标是否在阶段动物白名单内（<see cref="_creatureFilter"/> 为 null = 不限制）。</summary>
        private bool IsCreatureAllowed(int creatureIndex)
        {
            return _creatureFilter == null || _creatureFilter.Contains(creatureIndex);
        }

        /// <summary>
        /// 按当前 <see cref="_placeable"/> 与 <see cref="_creaturePrototypes"/> 重建 <see cref="_selections"/>：
        /// **先粒子类型**（顺序即 <see cref="_placeable"/> 的注册顺序，显示名 / 色块 / 是否单颗投放都取自类型注册表），
        /// **再白名单内的动物物种**（顺序即 <see cref="CreatureSpawns.Species"/>，显示名 / 色块取自原型实例）。
        /// </summary>
        /// <remarks>
        /// 只在 <see cref="Awake"/> 与 <see cref="SetPlacementFilter"/> 的成功路径上调用；每次调用都造一批新实例
        /// （因此调用前的旧条目全部作废，见 <see cref="Select(ParticleSelectionEntry)"/> 的陈旧实例判据）。
        /// </remarks>
        private ParticleSelectionEntry[] BuildSelections()
        {
            int creatureCount = 0;
            for (int i = 0; i < _creaturePrototypes.Length; i++)
            {
                if (IsCreatureAllowed(i))
                {
                    creatureCount++;
                }
            }

            var selections = new ParticleSelectionEntry[_placeable.Length + creatureCount];

            for (int i = 0; i < _placeable.Length; i++)
            {
                ushort typeId = _placeable[i];
                ParticleType type = ParticleRegistry.Types[typeId];
                selections[i] = ParticleSelectionEntry.ForParticle(
                    i,
                    typeId,
                    DisplayName(type),
                    type.ClassName,
                    type.Info.Palette[0],
                    _singlePlacement.Contains(typeId));
            }

            int index = _placeable.Length;
            for (int i = 0; i < _creaturePrototypes.Length; i++)
            {
                if (!IsCreatureAllowed(i))
                {
                    continue;
                }

                Creature prototype = _creaturePrototypes[i];
                selections[index] = ParticleSelectionEntry.ForCreature(
                    index,
                    i,
                    prototype.DebugName,
                    prototype.GetType().Name,
                    prototype.PlaceholderColor);
                index++;
            }

            return selections;
        }

        /// <summary>按当前选中项与半径重建 HUD 文本与色块颜色（动物项显示物种名与「单只」）；无可投项时清空。</summary>
        private void RefreshHud()
        {
            ParticleSelectionEntry entry = CurrentSelection;
            if (entry == null)
            {
                _hudText = null;
                _hudColor = default(Color);
                return;
            }

            _hudColor = entry.Swatch;
            if (entry.IsCreature)
            {
                _hudText = $"{entry.DisplayName}  {_cursor + 1}/{_selections.Length}  单只";
                return;
            }

            string brush = entry.SinglePlacement ? "单颗" : $"r={BrushRadius}";
            string species = entry.TypeId == ParticleIds.TreeSeed ? "  " + TreeSpecies.NameOf(_treeSpecies) : string.Empty;
            _hudText = $"{entry.DisplayName}  {_cursor + 1}/{_selections.Length}{species}  {brush}";
        }

        /// <summary>类型的显示名：优先 <see cref="ParticleTypeInfo.DebugName"/>，没填则回退类名。</summary>
        private static string DisplayName(ParticleType type)
        {
            ParticleTypeInfo info = type.Info;
            return string.IsNullOrEmpty(info.DebugName) ? type.GetType().Name : info.DebugName;
        }

        /// <summary>
        /// 屏幕坐标（像素，原点左下）→ 格子坐标。射线打到 z=0 平面，再换算到渲染器局部坐标取整。
        /// </summary>
        /// <remarks>
        /// 相机被 <c>CameraScreenFeed</c> 改成画进纹理之后，<c>ScreenPointToRay</c> 吃的是**纹理像素**而不是屏幕像素
        /// （见 <see cref="CameraScreenFeed"/> 类注释）——显示图不再是满屏时两者相差一个平移，因此先用
        /// <see cref="CameraScreenFeed.ToCameraPixel"/> 把指针换算进那张图的矩形。未接管（编辑模式）时原样直传。
        /// </remarks>
        /// <returns>映射结果在网格内则 true；相机缺失、射线与平面平行或落在网格外则 false。</returns>
        public bool TryGetCell(Vector3 screenPosition, int worldWidth, int worldHeight, out int x, out int y)
        {
            x = 0;
            y = 0;

            Camera camera = _camera != null ? _camera : Camera.main;
            if (camera == null)
            {
                return false;
            }

            Vector3 rayPoint = _screenFeed != null
                ? (Vector3)_screenFeed.ToCameraPixel(screenPosition)
                : screenPosition;

            Ray ray = camera.ScreenPointToRay(rayPoint);
            if (Mathf.Approximately(ray.direction.z, 0f))
            {
                return false;
            }

            Vector3 worldPoint = ray.origin + ray.direction * (-ray.origin.z / ray.direction.z);
            Vector3 local = _renderer.transform.InverseTransformPoint(worldPoint);

            x = Mathf.FloorToInt(local.x);
            y = Mathf.FloorToInt(local.y);
            return (uint)x < (uint)worldWidth && (uint)y < (uint)worldHeight;
        }
    }
}
