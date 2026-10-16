using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物实体基类：连续浮点位置 + 贴地 AABB 碰撞体，随逻辑帧（20 Hz）推进。
    /// 相当于粒子体系里 <see cref="IParticleType"/> 的地位——物种差异全部由派生类的虚属性表达，
    /// <see cref="CreatureWorld"/> 只认识本基类，不认识具体物种。
    /// </summary>
    /// <remarks>
    /// <para><b>位置约定：</b><see cref="X"/> / <see cref="Y"/> 是碰撞盒**底边中点**（脚底中心），
    /// 而不是几何中心——脚下的格坐标可以直接用 <c>floor(Y)</c> 拿到，支撑 / 落地判定不必再减半高；
    /// 渲染层补上 <c>Height/2</c> 即可得到精灵中心（见 CreatureRenderer）。
    /// 碰撞盒跨度 <c>[X − Width/2, X + Width/2] × [Y, Y + Height]</c>，单位 = 格；
    /// 世界坐标与格坐标**同一套数值**（与渲染取景约定一致，无单位换算）。</para>
    /// <para><b>对世界只读：</b>物理路径（<see cref="StepPhysics"/>）只调用 <see cref="CreatureTerrain"/>
    /// 的查询，绝不写世界。未来的写行为（打洞 / 吃草）**必须**走
    /// <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.Clear"/> / <see cref="ParticleWorld.SetPhase"/>，
    /// 让粒子侧看得见这次改动（否则世界账目会悄悄失衡）。</para>
    /// <para><b>不消耗世界随机流：</b>动物用自己的 <see cref="CreatureWorld.NextUInt"/>；
    /// 纯 C# 物理路径禁止 UnityEngine.Random 与 Time.*，也不做任何分配。</para>
    /// <para><b>时间约定：</b><see cref="VelocityX"/> / <see cref="VelocityY"/> 的单位是「格 / 逻辑帧」。
    /// <see cref="PrevX"/> / <see cref="PrevY"/> 由 <see cref="CreatureWorld.Step"/> 在每帧开头统一快照，
    /// 渲染层据此在两组位置之间插值。</para>
    /// </remarks>
    public abstract class Creature
    {
        /// <summary>覆盖列 / 行判定用的微小 epsilon：把「压在整格边界上」的情况稳定地归到内侧。</summary>
        private const float Epsilon = 1e-4f;

        /// <summary>朝向迟滞阈值：<c>|IntentVx|</c> 超过它才更新 <see cref="Facing"/>，使无脑读 Facing 的渲染不抖。</summary>
        private const float FacingEpsilon = 0.001f;

        /// <summary>思考间隔（逻辑帧）：每 <c>ThinkEvery</c> 帧才调一次 Brain.Think（20 Hz 下 = 2 Hz）。
        /// 调大即整体降频；个体间的错峰相位由 <see cref="SensePhase"/> 决定。</summary>
        public const int ThinkEvery = 10;

        /// <summary>威胁扫描半径（格）：<see cref="CreatureSensors.FindNearestFire"/> 圆盘**进入逃命**的半径。
        /// **设计值 = 16**（2026-10-12 定稿，不再计划调回 6）：半径 16 圆盘 ≈ 733 格读，仅在思考帧（2 Hz）执行——
        /// 30 只同相位的理论最坏值 ≈ 2,440 格读/逻辑帧，占一个世界 pass 的 ~4%（见 docs/design-creature-ai.md §9）。
        /// 已在逃命时用 <c>ThreatRadius + RabbitBrain.FleeExitMargin</c>（滞回）判断何时脱离。</summary>
        public const int ThreatRadius = 16;

        /// <summary>连续淹没帧数上限（160 帧 ≈ 8 秒），超过即溺死（受 <see cref="DrownsInWater"/> 门控）。
        /// **虚属性而非常量**：物种可覆盖以自带阈值（如将来的两栖动物），默认仍是 160 帧。</summary>
        public virtual int DrownTicksLimit
        {
            get { return 160; }
        }

        /// <summary>连续离水帧数上限（200 帧 ≈ 10 秒），超过即搁浅而死（受 <see cref="DiesWhenBeached"/> 门控）。</summary>
        public const int BeachedTicksLimit = 200;

        /// <summary>连续被静止固体完全覆盖的帧数上限（10 帧 = 0.5 秒 @20 逻辑帧/秒），超过即压死（受 <see cref="DiesWhenBuried"/> 门控）。</summary>
        public const int BuriedTicksLimit = 10;

        /// <summary>当前位置 x（碰撞盒底边中点，格），为本逻辑帧步进后的值。</summary>
        public float X;

        /// <summary>当前位置 y（碰撞盒底边中点，格），为本逻辑帧步进后的值。</summary>
        public float Y;

        /// <summary>上一逻辑帧位置 x（渲染插值用；由 <see cref="CreatureWorld.Step"/> 开头统一快照）。</summary>
        public float PrevX;

        /// <summary>上一逻辑帧位置 y（渲染插值用；由 <see cref="CreatureWorld.Step"/> 开头统一快照）。</summary>
        public float PrevY;

        /// <summary>水平速度（格 / 逻辑帧）。</summary>
        public float VelocityX;

        /// <summary>竖直速度（格 / 逻辑帧），向上为正；受重力为负。</summary>
        public float VelocityY;

        /// <summary>朝向：+1 朝右、−1 朝左（渲染镜像用）。</summary>
        public int Facing;

        /// <summary>是否存活。由 <see cref="Kill"/> 置 false，在世界清扫阶段真正移除。</summary>
        public bool Alive;

        /// <summary>已存活的逻辑帧数（每推进一帧 +1，用于寿命 / 渲染节拍）。</summary>
        public int AgeTicks;

        /// <summary>本帧脚下是否有支撑（<see cref="StepPhysics"/> 收尾时刷新）。</summary>
        public bool IsGrounded;

        /// <summary>本帧身体中心（<c>X, Y + Height/2</c>）是否在水中（<see cref="StepPhysics"/> 收尾时刷新）。</summary>
        public bool IsSubmerged;

        /// <summary>本帧横向积分被挡住的方向：+1 = 向右被挡、−1 = 向左被挡、0 = 横向畅通。
        /// <see cref="StepPhysics"/> 每帧开头清零、在横向扫描 / 世界边界夹取两处置位，
        /// 只供 <see cref="ApplyLocomotion"/> 第 ④ 段判定「本帧是不是真被挡住」——它不是黑板槽位：
        /// 不参与渲染插值、不进 <see cref="AgeTicks"/>、Brain 也不该读写。</summary>
        private sbyte _blockedXDir;

        /// <summary><see cref="_blockedXDir"/> 所记的那次受阻是否来自**世界边界夹取**（false = 撞到固体）。
        /// 两者必须分开：撞边界时前方一格越出世界，墙探针把越界一律当非固体、必然回答
        /// <see cref="CreatureSensors.WallAhead.None"/>——若与「前方是开阔地」混为一谈，动物就会永远顶着边界。
        /// 仅在 <see cref="_blockedXDir"/> ≠ 0 时有意义。</summary>
        private bool _blockedXAtWorldEdge;

        /// <summary>碰撞盒宽（格）。构造时给定，之后不变。</summary>
        public readonly float Width;

        /// <summary>碰撞盒高（格）。构造时给定，之后不变。</summary>
        public readonly float Height;

        // ===================== AI 黑板（状态全在内联槽位，Brain 因此可以无状态、零分配） =====================

        /// <summary>物种自定义 AI 状态编号（兔子：0=Idle 1=Wander 2=Flee；鱼：0=Swim 1=Stranded）。
        /// 语义按物种复用——基类只负责存，解释权在各 <c>Brain</c>。</summary>
        public byte AiState;

        /// <summary>当前状态剩余逻辑帧数：Brain 置值、运动层每帧递减，归零后下次思考触发状态转移。</summary>
        public int StateTicksLeft;

        /// <summary>思考相位 ∈ [0, <see cref="ThinkEvery"/>)：由 <see cref="CreatureWorld.Spawn"/> 按生成序号钉死，
        /// 使同批个体的思考帧彼此错开；死亡移除不改变他者相位（生成序号只增不减）。</summary>
        public byte SensePhase;

        /// <summary>目标水平速度（格 / 逻辑帧），运动层以 <see cref="MoveAccel"/> 逼近的意图量。</summary>
        public float IntentVx;

        /// <summary>目标竖直速度（格 / 逻辑帧）；仅对 <see cref="SwimAccel"/> 非零的水生物种生效，离水后自动失效。</summary>
        public float IntentVy;

        /// <summary>竖直带记忆槽位：**Brain 写、Brain 读**，基类只存不解释（语义同 <see cref="AiState"/>）。
        /// 鱼用它把「已进入下潜带」锁存成 Schmitt 滞回（<see cref="FishBrain.ThinkSwim"/>），使浅水里
        /// 「触底 / 触面」不会逐思考帧来回切换；基类只在 <see cref="ApplyLocomotion"/> 第 ⓪b 段把它当作
        /// 「本个体已锁潜」这一事实的门控位消费（锁潜 + 贴地 ⇒ 停住水平意图，浅水伏底）。
        /// 不写它的物种恒为 false ⇒ 零行为差异、零开销。</summary>
        public bool VerticalDiveLatched;

        /// <summary>巡航高度目标（格）：相对「正下方第一个可栖面 / 水」的目标高度，**Brain 写、Brain 读**，基类只存
        /// 不解释（语义同 <see cref="AiState"/>）。飞行物种（<see cref="BirdCreature"/>）的高度带上下巡航用
        /// （<see cref="BirdBrain.MinAltitudeCells"/>~<see cref="BirdBrain.MaxAltitudeCells"/> 格，见
        /// <c>docs/plan-bird-ai.md</c> §12）；其余物种恒不读、恒不写 ⇒ 零行为差异。</summary>
        public int TargetAltitudeCells;

        /// <summary>鱼的目标深度（格）：**体顶之上的连续水格数**（水面到体顶的距离），Brain 写 / 读，基类只存不解释
        /// （语义同 <see cref="AiState"/>）。默认 0 视为无效，鱼在第一个思考帧重摇（见 <c>docs/plan-bird-ai.md</c> §15）；
        /// 不读它的物种恒不写 ⇒ 零行为差异。</summary>
        public int TargetDepthCells;

        /// <summary>捕猎目标引用槽位：**Brain 写、Brain 读**，基类只存不解释（语义同 <see cref="AiState"/>）。
        /// 鸟（<see cref="BirdBrain"/>）用它把当前追击的猎物（兔 / 鱼）记在这里；非空且两碰撞盒重叠时，
        /// <see cref="ApplyLocomotion"/> 第 ⑥b 段按锁存的 <see cref="HuntKillLatched"/> 结算（命中判死
        /// <see cref="CreatureDeathCause.Hunted"/>、未命中则该次攻击落空），两种情况都把本槽位清空。
        /// 失去目标 / 放弃追击 / 击杀之后**必须**由 Brain 置回 null（防悬挂引用）。
        /// 不写它的物种恒 null ⇒ 零行为差异、零开销。</summary>
        public Creature HuntTarget;

        /// <summary>本次追击的**命中结果锁存**：EnterHunt 摇一次死亡骰后写入，运动层 ⑥b 只读它决定接触时是否判死
        /// （真 = 咬杀，假 = 扑空）。**Brain 写 / 运动层读**，基类只存不解释（语义同 <see cref="VerticalDiveLatched"/>）。
        /// 死亡骰放在思考帧而不是接触帧，是为了守住「动物随机流只在思考帧消耗」的纪律，并且让运动层保持零随机数依赖。
        /// 离开 Hunt 态时应由 Brain 复位为 false（防残留影响下一次追击）。</summary>
        public bool HuntKillLatched;

        /// <summary>被捕食接触时的死亡骰分母：锁定目标时摇 <c>NextInt(本值) == 0</c> 才判死，否则该次攻击落空。
        /// **&lt;= 1 ⇒ 必杀且不摇骰**（默认 1，保持「必死」物种零行为差异、零随机流消耗）。物种在自己的构造函数里
        /// 按各自分母写入（兔子 2 = 50%、鱼 3 ≈ 33%）；测试可改写以分别钉住命中 / 扑空两条路径。</summary>
        public int HuntDeathDenominator = 1;

        /// <summary>起跳请求：运动层在 <see cref="IsGrounded"/> 时消费成 <see cref="VelocityY"/> 脉冲并清零。</summary>
        public bool WantHop;

        /// <summary>连续淹没帧数（运动层每帧维护）；超过 <see cref="DrownTicksLimit"/> 且 <see cref="DrownsInWater"/> 时溺死。</summary>
        public int SubmergedTicks;

        /// <summary>连续离水帧数（运动层每帧维护）；超过 <see cref="BeachedTicksLimit"/> 且 <see cref="DiesWhenBeached"/> 时搁浅而死。</summary>
        public int BeachedTicks;

        /// <summary>连续被埋帧数（运动层每帧维护）；超过 <see cref="BuriedTicksLimit"/> 且 <see cref="DiesWhenBuried"/> 时压死。</summary>
        public int BuriedTicks;

        // ===================== 生态扩展黑板（食草 / 饥饿：仍为内联值类型，不建 per-creature 黑板对象） =====================

        /// <summary>无食物目标的哨兵值。目标格字段用它与合法格坐标（&ge; 0）区分。</summary>
        public const int NoFoodTarget = int.MinValue;

        /// <summary>饥饿饱和上限（逻辑帧）：<see cref="HungerTicks"/> 的饱和值，达到且 <see cref="StarvesWithoutFood"/>
        /// 为真即饿死（60 秒 @20 Hz）。放在基类是因为**推进与封顶都在运动层**（每帧 +1），物种 Brain 只读它做阈值判断。</summary>
        public const int MaxHungerTicks = 1200;

        /// <summary>饥饿计数（逻辑帧）：距上次进食的帧数，<see cref="ApplyLocomotion"/> 每帧 +1、饱和在
        /// <see cref="MaxHungerTicks"/>（只有 <see cref="StarvesWithoutFood"/> 的物种会因它饿死）。</summary>
        public int HungerTicks;

        /// <summary>进食冷却（逻辑帧）：进食后禁止立即再次进食的剩余帧数，运动层每帧递减到 0 为止。</summary>
        public int FeedingCooldownTicks;

        /// <summary>当前食物目标格 x；<see cref="NoFoodTarget"/> 表示无目标。仅物种 Brain 读写。</summary>
        public int FoodTargetX;

        /// <summary>当前食物目标格 y；<see cref="NoFoodTarget"/> 表示无目标。仅物种 Brain 读写。</summary>
        public int FoodTargetY;

        /// <summary>连续无法接近 / 找不到食物目标的思考帧数，超过 <see cref="RabbitBrain.FoodTargetFailLimit"/> 即放弃目标。</summary>
        public int FoodTargetFailTicks;

        /// <summary>本实体被 <see cref="Kill"/> 标注的死因，仅供 <see cref="CreatureWorld"/> 清扫时读取。</summary>
        internal CreatureDeathCause PendingDeathCause;

        /// <summary>构造碰撞盒尺寸（格）给定的实体。</summary>
        /// <param name="width">碰撞盒宽（格），须为正。</param>
        /// <param name="height">碰撞盒高（格），须为正。</param>
        protected Creature(float width, float height)
        {
            Width = width;
            Height = height;
            Facing = 1;
            Alive = true;
            FoodTargetX = NoFoodTarget;
            FoodTargetY = NoFoodTarget;
        }

        /// <summary>物种重力加速度（格 / 逻辑帧²）。</summary>
        protected abstract float Gravity { get; }

        /// <summary>物种最大下落速率（格 / 逻辑帧），竖直速度的绝对值上限。</summary>
        protected abstract float MaxFallSpeed { get; }

        /// <summary>水中每帧净浮力（格 / 逻辑帧²；负 = 下沉）。默认 0，即水里只有阻力没有浮力。</summary>
        protected virtual float Buoyancy
        {
            get { return 0f; }
        }

        /// <summary>水中速度保持系数折损 ∈ [0, 1)；每逻辑帧把竖直速度乘以 <c>1 − WaterDrag</c>。默认 0.5。</summary>
        protected virtual float WaterDrag
        {
            get { return 0.5f; }
        }

        // ---- AI 物种常量：数值本体放在各物种 Brain（§5 表格），基类只开一道虚门，运动层不认物种 ----

        /// <summary>陆上水平加速度（格 / 逻辑帧²），<see cref="IntentVx"/> 的逼近速率。</summary>
        protected virtual float MoveAccel
        {
            get { return 0f; }
        }

        /// <summary>水中竖直加速度（格 / 逻辑帧²）；0 表示不主动游（兔子竖直交给浮力 / 重力）。</summary>
        protected virtual float SwimAccel
        {
            get { return 0f; }
        }

        /// <summary>空中（非淹没）竖直加速度（格 / 逻辑帧²），<see cref="IntentVy"/> 的逼近速率；
        /// 0 表示不主动飞（默认）。只有 <see cref="FliesInAir"/> 的物种在空中才会走它——
        /// 与 <see cref="SwimAccel"/> 分管「水里 / 空中」两套竖直意图推进。</summary>
        protected virtual float FlyAccel
        {
            get { return 0f; }
        }

        /// <summary>起跳脉冲的竖直速度（格 / 逻辑帧）。</summary>
        protected virtual float HopVelocity
        {
            get { return 0f; }
        }

        /// <summary>
        /// 能否**跨上一格高的坎**：横向积分被固体挡住时，若挡路者恰好是「脚底行的一格坎、且抬升一格后身体有余量」，
        /// 就贴地抬升一格并继续横向移动（在 <see cref="StepPhysics"/> 里完成，见该方法③段）。
        /// 默认 false（多数物种不具备跨越能力）；兔子打开，鱼（搁浅时）不打开。
        /// </summary>
        protected virtual bool CanStepUp
        {
            get { return false; }
        }

        /// <summary>
        /// 是否是**飞行物种**（鸟 true、默认 false）。打开后两处泛化：
        /// ① <see cref="ApplyLocomotion"/> 的竖直意图逼近在空中（非淹没）也生效，走 <see cref="FlyAccel"/>；
        /// ② <see cref="StepPhysics"/> 在空中**挂起重力**（净竖直驱动只有意图——同鱼在水里的中性浮力，
        /// 因此鸟可悬停、不会被动坠落，落地 / 入水即恢复常规则）。
        /// </summary>
        protected virtual bool FliesInAir
        {
            get { return false; }
        }

        /// <summary>
        /// 向下支撑扫描是否把**树叶 / 树干**也算作可栖面（鸟 true、默认 false）。
        /// 打开时 <see cref="StepPhysics"/> 的**向下**积分与 <see cref="IsGrounded"/> 收尾改用
        /// <see cref="CreatureTerrain.IsPerchable"/>；向上顶头、水平阻挡、掩埋、尸体覆盖一律仍走
        /// <see cref="CreatureTerrain.IsSolid"/>（叶子只服务「站上去」，不挡上升 / 水平 / 不压死）。
        /// </summary>
        protected virtual bool PerchesOnFoliage
        {
            get { return false; }
        }

        /// <summary>连续淹没是否致死（兔子 true、鱼 false）。</summary>
        protected virtual bool DrownsInWater
        {
            get { return false; }
        }

        /// <summary>连续离水是否致死（鱼 true、兔子 false）。</summary>
        protected virtual bool DiesWhenBeached
        {
            get { return false; }
        }

        /// <summary>是否会被**活埋**压死。**默认 true**——与 <see cref="DrownsInWater"/> / <see cref="DiesWhenBeached"/>
        /// 等 opt-in 门控相反：被静止固体完全覆盖是对任何实体都成立的物理挤压，不存在「物种天生免疫挤压」；
        /// 将来需要免疫的物种（穴居动物、地底潜行体）再覆盖本属性返回 false。</summary>
        protected virtual bool DiesWhenBuried
        {
            get { return true; }
        }

        /// <summary>是否跳过悬崖守卫（逃命优先：兔子 Flee 态返回 true）。</summary>
        protected virtual bool IgnoresCliffGuard
        {
            get { return false; }
        }

        /// <summary>是否会因饥饿而死（兔子 true、鱼 / 探针 false）：门控 <see cref="ApplyLocomotion"/> 的饿死判定，
        /// 使饥饿致死与「溺水 / 搁浅」同属物种差异而非基类特判。</summary>
        protected virtual bool StarvesWithoutFood
        {
            get { return false; }
        }

        /// <summary>物种调试名（中文，日志 / 编辑器用）。</summary>
        public abstract string DebugName { get; }

        /// <summary>渲染占位色（本阶段没有美术资源，渲染层用纯色方块表示）。</summary>
        public abstract Color32 PlaceholderColor { get; }

        /// <summary>生成钩子：加入世界后、首个逻辑帧之前调用一次。本阶段默认空体。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public virtual void OnSpawn(CreatureWorld creatures, ParticleWorld world)
        {
        }

        /// <summary>逻辑钩子：每逻辑帧、**物理之前**调用（决策 → 运动：写速度 / 朝向 / 行为）。本阶段默认空体。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public virtual void OnLogic(CreatureWorld creatures, ParticleWorld world)
        {
        }

        /// <summary>
        /// 思考门控：仅当本帧的 <see cref="CreatureWorld.LogicTicks"/> 命中本个体的 <see cref="SensePhase"/>
        /// 时为真。派生类的 <see cref="OnLogic"/> 据此把 <c>Brain.Think</c> 降到 2 Hz（<see cref="ThinkEvery"/> = 10）。
        /// </summary>
        /// <remarks>RNG 只允许出现在被本门控放行的 <c>Brain.Think</c> 内——思考帧才消耗，保证确定性。</remarks>
        /// <param name="creatures">所属动物世界（取其逻辑帧计数）。</param>
        /// <returns>本帧是否该思考。</returns>
        protected bool ShouldThink(CreatureWorld creatures)
        {
            return creatures.LogicTicks % ThinkEvery == SensePhase;
        }

        /// <summary>
        /// 运动层：每逻辑帧执行一次（位于 <see cref="OnLogic"/> 与 <see cref="StepPhysics"/> 之间），
        /// 把 Brain 写下的意图转成速度（加速度限幅）、维护朝向迟滞、推进饥饿 / 冷却与环境致死计时器。
        /// </summary>
        /// <remarks>
        /// <para>与 Brain.Think 的分工：Think 是低频「决策」（2 Hz），这里是无条件的高频「反射」（20 Hz）。
        /// 除状态计时器递减与朝向迟滞外，全部条目都对世界只读、零分配、无随机数、无时间。</para>
        /// <para>顺序即语义：**先消费「锁潜 + 贴地」的伏底门控**（⓪b）→ 再逼近意图 → 再按 <see cref="IntentVx"/> 定朝向 → 悬崖守卫可能在探到前方无支撑时
        /// 反转意图 → 若**上一步物理的横向积分确实被挡住**则按墙探针三态分流（一格高的坎 → 贴地跨上去；
        /// 不可翻越 → 反转意图；探针答「无墙」→ 不动），若被**世界边界**夹住则掉头（边界处探针必答「无墙」，
        /// 不能与开阔地混同，否则永远顶着边界） → 消费起跳脉冲 → 最后统一做环境致死判定（⑥ 火 / ⑦ 掩埋属
        /// 「即时环境致死」——判据只依赖当前帧环境；⑧ 溺死 / ⑨ 搁浅 / ⑩ 饿死属「计时类死因」，排在其后）。</para>
        /// </remarks>
        /// <param name="world">被查询的粒子世界（只读）。</param>
        internal void ApplyLocomotion(ParticleWorld world)
        {
            // ⓪ 状态计时器递减（每逻辑帧一格，Brain 下次思考时若已归零即触发转移）。
            if (StateTicksLeft > 0)
            {
                StateTicksLeft--;
            }

            // ⓪b 浅水伏底：Brain 把「本个体已锁潜」记在 VerticalDiveLatched 上，本帧又确实贴地 ⇒ 停住水平意图。
            //     为什么放在 ① 之前：水平意图归零后 ① 让速度按 MoveAccel 收到 0、② 因此不再翻朝向、
            //     ③ 悬崖守卫与 ④ 撞墙掉头都因 |IntentVx| = 0 而不触发——「伏底」是一个整体行为，不是逐段打补丁。
            //     语义（鱼）：浅水里贴底的鱼伏底不动，不再沿坑洼池底爬行，自然也不会撞上每一个一格坎掉头
            //     （鱼 CanStepUp = false，旧行为在浅池里就是「爬 → 撞坎 → 掉头」来回抽，见 FishShallowWaterTests）。
            //     基类不解释该位的含义，只把它当作 Brain 写下的一个门控位（同 AiState）；不写它的物种恒 false。
            if (VerticalDiveLatched && IsGrounded)
            {
                IntentVx = 0f;
            }

            // ① 意图逼近：水平恒做；竖直在水里走 SwimAccel（鱼），在空中走 FlyAccel（飞行物种鸟）。
            //    两者都只对相应物种生效：兔子 SwimAccel / FlyAccel 皆 0 ⇒ 竖直完全交给浮力 / 重力。
            VelocityX = MoveTowards(VelocityX, IntentVx, MoveAccel);
            if (IsSubmerged)
            {
                VelocityY = MoveTowards(VelocityY, IntentVy, SwimAccel);
            }
            else if (FliesInAir)
            {
                VelocityY = MoveTowards(VelocityY, IntentVy, FlyAccel);
            }

            // ② 朝向迟滞：只有意图幅度过阈值才翻面，渲染无脑读 Facing 因而永不抖动。
            if (IntentVx > FacingEpsilon)
            {
                Facing = 1;
            }
            else if (IntentVx < -FacingEpsilon)
            {
                Facing = -1;
            }

            float absIntent = Abs(IntentVx);

            // ③ 悬崖守卫：贴地前进、且前方一格脚下 2 格内没有支撑 → 反转意图（Flee 态豁免，逃命优先）。
            if (!IgnoresCliffGuard && IsGrounded && absIntent > FacingEpsilon)
            {
                int ahead = IntentVx > 0f ? 1 : -1;
                if (!CreatureSensors.ProbeGroundAhead(world, X, Y, ahead))
                {
                    IntentVx = -IntentVx;
                }
            }

            // ④ 撞墙：贴地、**上一步物理的横向积分确实被挡住**、且被挡方向就是当前意图方向 → 试探前方（见上）。
            //    为什么不能用「水平速度接近零」当判据（旧实现）：贴墙时 ① 的意图逼近每帧仍会给出
            //    VelocityX = MoveAccel(0.02)，与任何「接近零」的阈值同量级 —— 速度**永远进不了那个窗口**，
            //    ④ 段整段不可达，只剩「顶上 → 被物理归零 → 下帧加速 0.02 → 再顶上」；
            //    反过来，主动转向穿过零点（MoveTowards 步长 0.02：0.06→0.04→0.02→0.00）却恰好落进窗口，
            //    「开阔地上转向」于是被误判成撞墙。触发条件必须换成物理层的事实：本帧横向被固体拦住 / 被边界夹取。
            //    方向也要对上：被挡方向与当前意图方向不一致（意图在这一帧里被 Brain 或悬崖守卫翻过面）时不动作，
            //    免得拿「被挡住的那个方向」去探反方向。
            if (IsGrounded && absIntent > 0f)
            {
                int ahead = IntentVx > 0f ? 1 : -1;
                if (_blockedXDir == ahead)
                {
                    if (_blockedXAtWorldEdge)
                    {
                        // 世界边界：前方格越出世界，墙探针把越界一律当非固体（必然回答 None，因此不必去探）。
                        // 但「顶着世界外壁」与「前方是开阔地」是两回事——后者必须空操作（否则与主动转向打架，
                        // 见下面 None 分支），前者必须掉头，否则动物会永远贴着边界原地蹭。边界在此等价于不可翻越的实心体。
                        IntentVx = -IntentVx;
                    }
                    else
                    {
                        CreatureSensors.WallAhead wall = CreatureSensors.ProbeWallAhead(world, X, Y, Height, ahead);
                        if (wall == CreatureSensors.WallAhead.Blocked
                            || (wall == CreatureSensors.WallAhead.Climbable && !CanStepUp))
                        {
                            // 不可翻越的墙，或**本物种跨不上去**（鱼）的一格坎 → 掉头。
                            IntentVx = -IntentVx;
                        }

                        // 一格坎且本物种能跨（CanStepUp）：横向扫描里已经贴地抬格、继续走（见 StepPhysics ③ 段），
                        // 走到这里说明那是过渡帧的偶发分歧，不动最安全。
                        // wall == None：横向扫描覆盖**整个身体**，探针只查身体底行 floor(Y)——身体跨行、且挡住它的
                        // 那格固体不在底行的瞬间，两者会给出相反的结论。这种分歧只发生在落地前的过渡帧
                        // （贴地后 Y 落在格边界上，两处口径重合），保守起见什么都不做：不反向意图，
                        // 让下一次横向扫描在自洽的位置上重新给出答案。
                    }
                }
            }

            // ⑤ 起跳消费：贴地才把请求兑现成竖直脉冲，随后清零（空中悬挂的请求留到落地那一帧）。
            if (WantHop && IsGrounded)
            {
                VelocityY = HopVelocity;
                WantHop = false;
            }

            // ⑥ 接触火即死（通用机制，鱼也怕被烧）：身体中心格是火 → 烧死。
            int centerX = FloorToInt(X);
            int centerY = FloorToInt(Y + Height * 0.5f);
            if (world.IsInBounds(centerX, centerY)
                && world.GetCell(centerX, centerY).typeId == ParticleIds.Fire)
            {
                Kill(CreatureDeathCause.Burned);
            }

            // ⑥b 捕猎接触结算（通用机制，与 ⑥「接触火即死」同层）：HuntTarget 非 null 且两碰撞盒 AABB 重叠
            //     ⇒ 猎物被咬中一次，按**锁存**的命中结果决定死活（v5，见 docs/plan-bird-ai.md §14）。
            //     命中与否已在 Brain 的 EnterHunt 思考帧摇好并锁存在 HuntKillLatched：这里只结算、不摇骰、
            //     不依赖动物随机流（运动层保持零随机数依赖）。这是**反射**而不是「决策」——Brain 只负责把
            //     追击目标与命中结果写进黑板，真正判定「咬到没咬到」放在运动层，和接触火并列（每帧零分配）。
            //     一次接触 = 一次结算：命中即死、未命中该次攻击落空；两种情况都立刻清空 HuntTarget，
            //     Brain 下次思考看到「身在 Hunt 态但目标已空」即走既有 ExitHunt(retreat: true) 拉升飞走。
            //     只有鸟会写 HuntTarget ⇒ 其它物种恒 null，本条目对它们零行为差异、零开销。
            Creature huntTarget = HuntTarget;
            if (huntTarget != null && Overlaps(huntTarget))
            {
                // 命中与否已在 EnterHunt 摇好并锁存在 HuntKillLatched：这里只结算，不摇骰、不依赖动物随机流。
                // 未命中同样清空目标 ⇒ Brain 的撤离出口让鸟飞走。
                if (HuntKillLatched)
                {
                    huntTarget.Kill(CreatureDeathCause.Hunted);
                }

                HuntTarget = null; // 命中 / 扑空都算这一次攻击结束
            }

            // ⑦ 掩埋致死（压死）：身体覆盖的**每一格**都是**静止**的固体（沙 / 石 / 土家族）时开始计时，
            //    连续超过 BuriedTicksLimit 帧即 Kill(Crushed)。
            //    为什么放在这里：与 ⑥ 同为「即时环境致死」——判据只依赖当前帧的环境本身，不依赖任何计时阈值；
            //    排在 ⑧⑨⑩ 的计时类死因（溺死 / 搁浅 / 饿死）之前，使被活埋的个体优先报更准确的死因
            //    （活埋现场往往同时离水，先判压死者胜）。
            //    为什么要求覆盖物**静止**（HasMomentum，与地下水闸门同一套运动语义）：正在下落的沙柱穿过身体时，
            //    每一格也恰好都是固体，但那是塌方 / 挖掘的过渡态，不是被活埋——必须排除。
            //    为什么给 0.5 秒宽限：落沙擦过、单帧穿模不该误杀，需**连续**静止覆盖才判压死。
            if (IsFullyBuried(world, X, Y, Width, Height))
            {
                BuriedTicks++;
            }
            else
            {
                BuriedTicks = 0;
            }

            if (DiesWhenBuried && BuriedTicks > BuriedTicksLimit)
            {
                Kill(CreatureDeathCause.Crushed);
            }

            // ⑧ 连续淹没计时 + 溺死（仅兔子等 DrownsInWater 的物种会死）。
            if (IsSubmerged)
            {
                SubmergedTicks++;
            }
            else
            {
                SubmergedTicks = 0;
            }

            if (DrownsInWater && SubmergedTicks > DrownTicksLimit)
            {
                Kill(CreatureDeathCause.Drowned);
            }

            // ⑨ 连续离水计时 + 搁浅而死（仅鱼等 DiesWhenBeached 的物种会死）。兔子照常计时但门控挡住。
            if (IsSubmerged)
            {
                BeachedTicks = 0;
            }
            else
            {
                BeachedTicks++;
            }

            if (DiesWhenBeached && BeachedTicks > BeachedTicksLimit)
            {
                Kill(CreatureDeathCause.Beached);
            }

            // ⑩ 饥饿推进 + 进食冷却递减（每逻辑帧，属「反射」而非「思考」，因此放在运动层而非 Brain）。
            //    只有 StarvesWithoutFood 的物种会因饥饿饱和而死；其余物种照常累计（无害）但不杀。
            if (HungerTicks < MaxHungerTicks)
            {
                HungerTicks++;
            }

            if (FeedingCooldownTicks > 0)
            {
                FeedingCooldownTicks--;
            }

            if (StarvesWithoutFood && HungerTicks >= MaxHungerTicks)
            {
                Kill(CreatureDeathCause.Starved);
            }
        }

        /// <summary>死亡钩子：世界清扫本实体时、<see cref="CreatureWorld.CreatureDied"/> 之前调用。本阶段默认空体。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        /// <param name="cause">死因。</param>
        public virtual void OnDeath(CreatureWorld creatures, ParticleWorld world, CreatureDeathCause cause)
        {
        }

        /// <summary>两个碰撞盒的 AABB 是否重叠（半开区间：贴边不算重叠）。碰撞盒跨度
        /// <c>[X − Width/2, X + Width/2] × [Y, Y + Height]</c>。纯四则、零分配，供
        /// <see cref="ApplyLocomotion"/> 第 ⑥b 段的捕猎接触判定使用。</summary>
        /// <param name="other">另一实体。</param>
        /// <returns>两盒重叠时 true。</returns>
        private bool Overlaps(Creature other)
        {
            float halfWidth = Width * 0.5f;
            float otherHalfWidth = other.Width * 0.5f;
            return X - halfWidth < other.X + otherHalfWidth
                && X + halfWidth > other.X - otherHalfWidth
                && Y < other.Y + other.Height
                && Y + Height > other.Y;
        }

        /// <summary>标注死亡（幂等：重复调用只有第一次生效，死因以第一次为准）。</summary>
        /// <remarks>
        /// 本方法**只置标记**，不从世界移除、不发事件——真正的移除与
        /// <see cref="CreatureWorld.CreatureDied"/> 由 <see cref="CreatureWorld.Step"/> 的清扫阶段统一做，
        /// 避免在遍历中间改动列表。
        /// </remarks>
        /// <param name="cause">死因。</param>
        public void Kill(CreatureDeathCause cause)
        {
            if (!Alive)
            {
                return;
            }

            Alive = false;
            PendingDeathCause = cause;
        }

        /// <summary>
        /// 一步物理：流体作用 → 竖直积分（逐行扫）→ 水平积分 → 世界边界夹取 → 刷新支撑 / 淹没状态；
        /// 同时算出**本帧横向受阻信号**（<see cref="_blockedXDir"/> / <see cref="_blockedXAtWorldEdge"/>，
        /// 供下一帧运动层第 ④ 段判定撞墙）。轴分离 AABB，对世界**只读**。
        /// </summary>
        /// <remarks>
        /// <para><b>为什么轴分离：</b>先解竖直再解水平，每一步都退化成「沿一条轴扫一格宽的条带」，
        /// 判定简单、结果可预测，且天然允许「贴着墙下滑」「沿地面平移」这类滑行行为，
        /// 不会像对角射线那样需要处理法线分解。</para>
        /// <para><b>为什么逐行 / 逐列扫而不是只看落点：</b>单帧位移可能跨多格（未来可能有高速动物 /
        /// 小世界），只查落点会穿透薄地形。这里从旧边扫到新边，遇到的第一格实体就停住——
        /// 位移 &lt; 1 格时退化成一次检查，成本与朴素做法相同。</para>
        /// <para><b>横向受阻信号：</b>每帧开头清零（本帧的结论只描述本帧的积分，绝不残留上一帧），
        /// 在横向扫描撞到固体、以及世界边界夹取这两处置位；它是「被挡住」这一**物理事实**的载体，
        /// 运动层因此不必再拿速度当代理量（见 <see cref="ApplyLocomotion"/> 第 ④ 段）。</para>
        /// <para>全程 float 四则 + floor，无随机数、无时间、无分配。</para>
        /// </remarks>
        /// <param name="world">被查询的粒子世界（只读）。</param>
        internal void StepPhysics(ParticleWorld world)
        {
            // 横向受阻信号每帧重算：先清零，再由下面的横向扫描 / 边界夹取按需置位。
            _blockedXDir = 0;
            _blockedXAtWorldEdge = false;

            // —— ① 流体作用：用**旧位置**的中心格判断是否在水中，再积分竖直速度 ——
            int oldCenterRow = FloorToInt(Y + Height * 0.5f);
            bool inWaterHere = CreatureTerrain.IsWater(world, FloorToInt(X), oldCenterRow);
            if (inWaterHere)
            {
                VelocityY = VelocityY * (1f - WaterDrag) + Buoyancy;
            }

            // 飞行挂起重力：FliesInAir 的物种在空中（非淹没）不累加重力，净竖直驱动只有意图
            // （同鱼在水里的中性浮力，鸟因此可悬停、不会被动坠落）；入水 / 落地即恢复常规则。
            if (!(FliesInAir && !inWaterHere))
            {
                VelocityY -= Gravity;
            }

            if (VelocityY < -MaxFallSpeed)
            {
                VelocityY = -MaxFallSpeed;
            }
            else if (VelocityY > MaxFallSpeed)
            {
                VelocityY = MaxFallSpeed;
            }

            // —— ② 竖直积分：从旧边逐行扫到新边（跨几行扫几行），撞上就停在格子外沿 ——
            float oldY = Y;
            float targetY = Y + VelocityY;
            int colMin = FloorToInt(X - Width * 0.5f + Epsilon);
            int colMax = FloorToInt(X + Width * 0.5f - Epsilon);

            if (VelocityY < 0f)
            {
                // 下行：底边进入的行 = floor(旧底边) − 1 递减到 floor(新底边)
                int rowFirst = FloorToInt(oldY) - 1;
                int rowLast = FloorToInt(targetY);
                for (int row = rowFirst; row >= rowLast; row--)
                {
                    if (AnySupportInRow(world, row, colMin, colMax))
                    {
                        targetY = row + 1;
                        VelocityY = 0f;
                        break;
                    }
                }
            }
            else if (VelocityY > 0f)
            {
                // 上行：头顶进入的行 = floor(旧顶边 − ε) + 1 递增到 floor(新顶边 − ε)
                int rowFirst = FloorToInt(oldY + Height - Epsilon) + 1;
                int rowLast = FloorToInt(targetY + Height - Epsilon);
                for (int row = rowFirst; row <= rowLast; row++)
                {
                    if (AnySolidInRow(world, row, colMin, colMax))
                    {
                        targetY = row - Height;
                        VelocityY = 0f;
                        break;
                    }
                }
            }

            Y = targetY;

            // —— ③ 水平积分：按移动方向扫侧边覆盖的**行**，撞上就停在格子内沿 ——
            // 竖直位移已经落定，因此侧边条带用的是更新后的 Y。
            float targetX = X + VelocityX;
            int rowMin = FloorToInt(Y + Epsilon);
            int rowMax = FloorToInt(Y + Height - Epsilon);
            bool steppedUp = false; // 本帧是否已跨过一格坎（每帧至多一格，防连续抬升）

            if (VelocityX > 0f)
            {
                int colFirst = FloorToInt(X + Width * 0.5f - Epsilon) + 1;
                int colLast = FloorToInt(targetX + Width * 0.5f - Epsilon);
                for (int col = colFirst; col <= colLast; col++)
                {
                    if (AnySolidInColumn(world, col, rowMin, rowMax))
                    {
                        // 跨坎（CanStepUp 物种，每帧至多一格）：挡路者是一格高的坎且抬升后有余量 → 抬一格后重测本列。
                        // 抬升放在物理层，位移因此连续（不需要速度 / 弹道参与），由本帧的竖直收尾重新判定支撑。
                        if (!steppedUp
                            && CanStepUp
                            && CreatureSensors.ProbeWallAhead(world, X, Y, Height, 1) == CreatureSensors.WallAhead.Climbable)
                        {
                            steppedUp = true;
                            Y += 1f;
                            rowMin++;
                            rowMax++;
                            col--; // 重测同一列（抬升后它通常已不再是障碍）
                            continue;
                        }

                        targetX = col - Width * 0.5f;
                        VelocityX = 0f;
                        _blockedXDir = 1;
                        break;
                    }
                }
            }
            else if (VelocityX < 0f)
            {
                int colFirst = FloorToInt(X - Width * 0.5f + Epsilon) - 1;
                int colLast = FloorToInt(targetX - Width * 0.5f + Epsilon);
                for (int col = colFirst; col >= colLast; col--)
                {
                    if (AnySolidInColumn(world, col, rowMin, rowMax))
                    {
                        if (!steppedUp
                            && CanStepUp
                            && CreatureSensors.ProbeWallAhead(world, X, Y, Height, -1) == CreatureSensors.WallAhead.Climbable)
                        {
                            steppedUp = true;
                            Y += 1f;
                            rowMin++;
                            rowMax++;
                            col++; // 重测同一列
                            continue;
                        }

                        targetX = col + 1 + Width * 0.5f;
                        VelocityX = 0f;
                        _blockedXDir = -1;
                        break;
                    }
                }
            }

            X = targetX;

            // —— ④ 世界边界夹取：撞到世界外壁等同于撞到静止实体，速度清零，并记一次「被边界挡住」 ——
            //    信号方向按**被违反的那一侧**定（左侧夹取 ⇒ 向左被挡），与夹取前的速度符号无关：
            //    只有意图仍指向那一侧时运动层才会掉头（见 ApplyLocomotion 第 ④ 段），
            //    因此「贴着边界往内走」不会被这条信号打断。
            float halfWidth = Width * 0.5f;
            float maxX = world.Width - halfWidth;
            if (maxX < halfWidth)
            {
                // 世界比动物还窄（极端小世界）：退化为中心对齐，避免 min > max。
                // 这里**不**置受阻信号：横向位移已经被位置钉死取代，没有可掉头的方向。
                X = world.Width * 0.5f;
                VelocityX = 0f;
            }
            else if (X < halfWidth)
            {
                X = halfWidth;
                VelocityX = 0f;
                _blockedXDir = -1;
                _blockedXAtWorldEdge = true;
            }
            else if (X > maxX)
            {
                X = maxX;
                VelocityX = 0f;
                _blockedXDir = 1;
                _blockedXAtWorldEdge = true;
            }

            // 世界高过动物时上界是 world.Height − Height，矮于动物时退化为 0（贴地），保证 Y ≥ 0 恒成立。
            float maxY = world.Height - Height;
            if (maxY < 0f)
            {
                maxY = 0f;
            }

            if (Y < 0f)
            {
                Y = 0f;
                VelocityY = 0f;
            }
            else if (Y > maxY)
            {
                Y = maxY;
                VelocityY = 0f;
            }

            // —— ⑤ 收尾：刷新支撑与淹没状态（渲染 / 下一步决策读的就是这份结果）——
            // 底边正下方（Y − ε）覆盖列里任一固体即为有支撑；Y 被夹到 0 也算落地。
            IsGrounded = false;
            if (Y <= 0f)
            {
                IsGrounded = true;
            }
            else
            {
                int groundRow = FloorToInt(Y - Epsilon);
                int groundColMin = FloorToInt(X - Width * 0.5f + Epsilon);
                int groundColMax = FloorToInt(X + Width * 0.5f - Epsilon);
                for (int col = groundColMin; col <= groundColMax; col++)
                {
                    if (IsSupportCell(world, col, groundRow))
                    {
                        IsGrounded = true;
                        break;
                    }
                }
            }

            IsSubmerged = CreatureTerrain.IsWater(world, FloorToInt(X), FloorToInt(Y + Height * 0.5f));

            // 支撑面把向下的速度吃掉：恰好落在格边界上时，竖直扫描可能一行都没扫到（没有「进入」任何行），
            // 但底边已经贴住固体——此时若留着负速度，会与「贴地」的物理语义矛盾。补一条与夹取落地同口径的收尾。
            if (IsGrounded && VelocityY < 0f)
            {
                VelocityY = 0f;
            }
        }

        /// <summary>身体覆盖矩形是否**每一格**都是静止的固体（压死判据，见 <see cref="ApplyLocomotion"/> 第 ⑦ 段）。
        /// 覆盖口径与物理同一套：<c>colMin = floor(x − width/2 + ε)</c>、<c>colMax = floor(x + width/2 − ε)</c>、
        /// <c>rowMin = floor(y + ε)</c>、<c>rowMax = floor(y + height − ε)</c>。
        /// 任一格不是固体、或虽是固体但仍在运动中（<see cref="ParticleWorld.HasMomentum"/>，如正下落的沙柱），即返回 false。</summary>
        private static bool IsFullyBuried(ParticleWorld world, float x, float y, float width, float height)
        {
            int colMin = FloorToInt(x - width * 0.5f + Epsilon);
            int colMax = FloorToInt(x + width * 0.5f - Epsilon);
            int rowMin = FloorToInt(y + Epsilon);
            int rowMax = FloorToInt(y + height - Epsilon);

            for (int row = rowMin; row <= rowMax; row++)
            {
                for (int col = colMin; col <= colMax; col++)
                {
                    if (!CreatureTerrain.IsSolid(world, col, row) || world.HasMomentum(col, row))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>本格是否算作本物种的**向下支撑面**：默认同 <see cref="CreatureTerrain.IsSolid"/>；
        /// <see cref="PerchesOnFoliage"/> 的物种（鸟）额外把树叶 / 树干算进来（只服务向下支撑口径）。</summary>
        private bool IsSupportCell(ParticleWorld world, int col, int row)
        {
            return PerchesOnFoliage
                ? CreatureTerrain.IsPerchable(world, col, row)
                : CreatureTerrain.IsSolid(world, col, row);
        }

        /// <summary>行内任一覆盖列是本物种的向下支撑面（<see cref="IsSupportCell"/>）。</summary>
        private bool AnySupportInRow(ParticleWorld world, int row, int colMin, int colMax)
        {
            for (int col = colMin; col <= colMax; col++)
            {
                if (IsSupportCell(world, col, row))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>行内任一覆盖列是固体。</summary>
        private static bool AnySolidInRow(ParticleWorld world, int row, int colMin, int colMax)
        {
            for (int col = colMin; col <= colMax; col++)
            {
                if (CreatureTerrain.IsSolid(world, col, row))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>列内任一覆盖行是固体。</summary>
        private static bool AnySolidInColumn(ParticleWorld world, int col, int rowMin, int rowMax)
        {
            for (int row = rowMin; row <= rowMax; row++)
            {
                if (CreatureTerrain.IsSolid(world, col, row))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>向下取整（不走 Mathf，避免任何 UnityEngine 依赖进入物理路径）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        /// <summary>把 <paramref name="current"/> 以不超过 <paramref name="maxDelta"/> 的步长移向 <paramref name="target"/>，
        /// 到位后**恰好**落在目标上（不超调）——意图逼近的限幅核心（不走 Mathf，保持物理路径纯净）。</summary>
        private static float MoveTowards(float current, float target, float maxDelta)
        {
            float delta = target - current;
            if (delta > maxDelta)
            {
                return current + maxDelta;
            }

            if (delta < -maxDelta)
            {
                return current - maxDelta;
            }

            return target;
        }

        /// <summary>绝对值（不走 Math，保持物理路径零 UnityEngine 依赖）。</summary>
        private static float Abs(float value)
        {
            return value < 0f ? -value : value;
        }
    }
}
