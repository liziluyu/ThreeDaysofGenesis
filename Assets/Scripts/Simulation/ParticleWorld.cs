using System;
using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 粒子世界的唯一持有者：一个连续的 <see cref="Cell"/>[] + (x,y) 直接算下标寻址 + 三个原子写操作。
    /// 纯 C# 类：不继承 MonoBehaviour、不持有任何 Unity 对象、**不认识渲染**。
    /// </summary>
    /// <remarks>
    /// <para>坐标：x ∈ [0, Width)，y ∈ [0, Height)，原点在左下角，y 向上增大，重力方向为 -y。</para>
    /// <para>下标：<c>index = y * Width + x</c>。</para>
    /// <para>扫描：行从 y = Height-1 到 0；同一行内 x 方向逐 tick 交替。</para>
    /// <para>越界读返回空气格；越界写是定义好的空操作（返回 false，不抛异常）。</para>
    /// <para>世界不做合法性判断：是否允许替换某个邻居由类型自己的逻辑决定。</para>
    /// </remarks>
    public sealed class ParticleWorld
    {
        /// <summary>单边最大格数。</summary>
        public const int MaxSide = 4096;

        /// <summary>
        /// 有机物衰变概率的分母：带 <see cref="ParticleTags.Organic"/> 标签的格被覆盖 / 抹除时，
        /// 有 <c>1 / 本值 = 1/5 = 20%</c> 的概率就地把该格变成腐殖质（<see cref="ParticleIds.Humus"/>）。
        /// 判据只看标签不看 typeId；每次有机物死亡都会摇一次骰（消耗一个随机数，保持确定性）。
        /// </summary>
        public const uint OrganicDecayDenominator = 5u;

        private const uint DefaultSeed = 0x9E3779B9u;

        /// <summary>候选位图的 chunk 边长（格）。256 bit / chunk = <c>ulong[4]</c>，全 0 = 整块休眠。</summary>
        private const int ChunkShift = 4;

        /// <summary>chunk 边长（= <c>1 &lt;&lt; ChunkShift</c>，16）。</summary>
        private const int ChunkSize = 1 << ChunkShift;

        /// <summary>chunk 内的格掩码（低 <see cref="ChunkShift"/> 位）。</summary>
        private const int ChunkMask = ChunkSize - 1;

        /// <summary>每个 chunk 的位图字数（256 bit / 64）。</summary>
        private const int WordsPerChunk = 4;

        /// <summary>
        /// 温度唤醒刷子半径（格，Chebyshev）：一处 Q 或湿度变化后，需要重新求值的运动决策最远读到几格温度。
        /// 取 2 = <see cref="ParticleRules.TryThermalDrift"/> 的「连片」判据读 <c>(x ± 2, y)</c> 的温度；
        /// 水对流 / 大气浮力只读相邻格（1）。温度唤醒与 <see cref="ParticleTypeInfo.MotionReadReach"/> 无关：
        /// 前者只看温度读距，后者只算结构（<c>_cells</c>）读距。
        /// </summary>
        private const int HeatWakeRadius = 2;

        /// <summary>
        /// 稀疏 / 密集脏集的分水岭：一次 <see cref="FlushTemperatureDirty"/> 的脏格数超过
        /// <c>格数 / 本值</c> 时，逐格刷球的并集已逼近整网格，逐格成本纯亏 → 回退
        /// <see cref="FlushDirtyChunkWide"/>（见 <see cref="FlushDirty"/>）。
        /// </summary>
        private const int DenseFlushCellDivisor = 32;

        /// <summary>
        /// 极密脏集的分水岭：脏格数超过 <c>格数 / 本值</c> 时，球并集已吞掉整张网格，直接整图置位
        /// （见 <see cref="FlushDirty"/> 的 <c>_saturatedFlushThreshold</c> 分支）。
        /// </summary>
        private const int SaturatedFlushCellDivisor = 2;

        /// <summary>四邻偏移（<see cref="EvacuateHumidity"/> 用；提成静态数组避免每次降雨都分配）。</summary>
        private static readonly int[] NeighbourX = { -1, 1, 0, 0 };
        private static readonly int[] NeighbourY = { 0, 0, -1, 1 };

        private readonly int _width;
        private readonly int _height;
        private readonly Cell[] _cells;
        private readonly HeatMap _heat;
        private readonly HumidityField _humidity;
        private readonly LightController _light;
        private readonly MotionMonitor _liquidMotion;
        private readonly MotionMonitor _solidMotion;
        private readonly ParticleCountMonitor _counts;
        private readonly ParticleType[] _types;

        // ── 运动休眠（阶段 B）──
        // 候选位图：每 chunk 256 bit（ulong[4]），扁平一维；bit0 处理标记机制不变。
        // _candidates 收集「写入 / 温度脏」置位的待求值格；StepMotion 开始时与 _scanCandidates 互换，
        // 扫描期间的新写入继续落回 _candidates（留给下一帧），扫描末尾清空刚消费的 _scanCandidates。
        private readonly int _chunkCols;
        private readonly int _chunkCount;

        /// <summary><see cref="DenseFlushCellDivisor"/> 对应的实际脏格阈值（<c>格数 / 本值</c>，构造时算一次）。</summary>
        private readonly int _denseFlushThreshold;

        /// <summary><see cref="SaturatedFlushCellDivisor"/> 对应的实际脏格阈值（构造时算一次）。</summary>
        private readonly int _saturatedFlushThreshold;
        private readonly ulong[] _processedBits;

        /// <summary>
        /// 持久位图（与 <see cref="_candidates"/> 同布局）：每格 1 bit = 该格当前类型带
        /// <see cref="ParticleTypeInfo.HasLogic"/> 且**非空气、非静态**。逻辑帧混合扫描
        /// （<see cref="ScanLogicSleeping"/>）用它跳过「没有 OnLogic 可跑」的格。
        /// 维护点：<see cref="Set"/> / <see cref="SetPhase"/> / <see cref="Swap"/>（两端格）；世界初建是空网格，
        /// 故初值全 0。与候选位图不同，它**不随扫描换出 / 清空**，是持久状态。
        /// </summary>
        private readonly ulong[] _logicCells;

        // 写入唤醒集：类型表所有 MotionReadReach 盒**反转**后的并集（见 MotionReadBox）。并集按行表示——
        // 每行 dy 上被唤醒的 x 恰好是一个关于写入格对称的连续区间（多个对称区间取并 = 最宽的那个），
        // 所以只需存每行的半宽 _wakeHalf[dy]（-1 = 该行无格）。写入时逐行按 64 位字 OR，而不是逐格置位，
        // 行内区间连续、通常只落在 1..2 个字里——比「全体最大值球」（41×41 = 1681 格）便宜两个数量级。
        private readonly sbyte[] _wakeHalf;
        private readonly int _wakeMinDy;
        private readonly int _wakeMaxDy;
        private ulong[] _candidates;
        private ulong[] _scanCandidates;

        private int _tick;
        private uint _rng;
        private int _randomTicksPerStep;
        private byte _stamp;
        private Cell _outside;
        private bool _unknownTypeReported;
        private bool _invalidShadeReported;
        private bool _motionSleepEnabled = true;
        private bool _groundwaterSealingEnabled = true;

        /// <summary>
        /// 逐格「运动中」标记（live，0 = 静止 / 1 = 运动中）：地下水闸门用它判断固体能否排开液体。
        /// 内容 = 上一扫描位移过的格 + 刚放置的格（<see cref="Set"/> / <see cref="SetPhase"/> 与
        /// <see cref="Swap"/> 经 <see cref="MarkInMotion"/> 写入，本扫描内即刻可读）；
        /// 由 <see cref="RotateMomentumAtScanStart"/> 在每次 <see cref="Scan"/> 开头与 pending 缓冲交换，
        /// **显式过期**——判据不看 <see cref="Tick"/>、无模运算，因此不会出现「序号按模 256 回绕后凭空变真」
        /// 的幽灵动量（幽灵会让静止的石头在全扫描路径里自己沉进水里，且与休眠路径逐位发散）。
        /// </summary>
        private byte[] _inMotion;

        /// <summary>
        /// 本扫描的位移 / 放置集合（pending）：<see cref="MarkInMotion"/> 同时写它，
        /// 下一次扫描开始时转正为 <see cref="_inMotion"/>（即「上一扫描位移过」从此可见），
        /// 再下一次扫描开始时过期。与 <see cref="_candidates"/> / <see cref="_scanCandidates"/> 同一套双缓冲惯例。
        /// </summary>
        private byte[] _inMotionPending;

        private bool _motionScanActive;
        private int _motionCellsEvaluatedLastStep;

        /// <summary>
        /// 格子**类型布局**版本：<see cref="Set"/> / <see cref="SetPhase"/> 每次改类型时自增；<see cref="Swap"/>
        /// 只在两端 <c>typeId</c> 不同时自增（同型交换不改变任何一格的热输入，不必打断不动点跳过）。
        /// 热图不动点跳过（<see cref="HeatMap.Step"/>）靠它判断「逐格热容 / σ / ε / 天空暴露自上次热 pass
        /// 以来有没有变」，由 <see cref="StepLogic"/> 在调 <see cref="HeatMap.Step"/> 前写入
        /// <see cref="HeatMap.CellsVersion"/>。Q 与湿度的变化**不**走这个版本号（各有独立版本）。
        /// </summary>
        private int _writeVersion;

        /// <summary>
        /// 高速模式监视器（<see cref="HighSpeedSession"/> 在 <c>Enter</c> 挂上、<c>Exit</c> 摘掉）：
        /// 由 <see cref="Set"/> / <see cref="SetPhase"/> / <see cref="Swap"/> 在写完格子之后驱动，
        /// 增量统计每种粒子的产生 / 死亡与每列地表高度。<c>null</c> = 未开启——写路径只多一次判空，
        /// 关闭时的开销可忽略。监视器是**纯观察者**：零 RNG 消耗、零格子写入，挂 / 摘不改变推进结果。
        /// </summary>
        internal HighSpeedMonitor _monitor;

        /// <summary>
        /// 诊断消息的出口：**诊断消息由世界产生、由持有者转发**。
        /// <c>null</c> = 无人接管（测试 / 主线程直接使用世界）⇒ 当场 <see cref="Debug.LogError(string)"/>，与既有行为一致；
        /// 非 null（模拟 worker 接管）⇒ 只调这个委托，世界**绝不直接碰 Unity 日志**——Unity 日志只能在主线程调，
        /// 而世界在阶段 3 起是跑在 worker 线程上的。
        /// </summary>
        internal Action<string> MessageSink;

        /// <summary>使用全局注册表 <see cref="ParticleRegistry.Types"/> 创建世界。</summary>
        public ParticleWorld(int width, int height, uint seed)
            : this(width, height, seed, ParticleRegistry.Types)
        {
        }

        /// <summary>
        /// 使用指定的类型表创建世界。传入自定义表可以让单元测试完全不碰全局注册表，
        /// 从而在隔离环境里单独验证一个粒子类型。
        /// </summary>
        /// <param name="types">下标 0 必须是 <c>null</c>（空气），其余为 <see cref="ParticleType"/>。</param>
        public ParticleWorld(int width, int height, uint seed, ParticleType[] types)
        {
            if (width < 1 || width > MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, $"width 必须在 1..{MaxSide} 之间。");
            }

            if (height < 1 || height > MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, $"height 必须在 1..{MaxSide} 之间。");
            }

            if (types == null || types.Length == 0 || types[0] != null)
            {
                throw new ArgumentException("类型表长度至少为 1，且下标 0（空气）必须为 null。", nameof(types));
            }

            // 自定义表同样要过注册表那套校验：否则空调色板会让 Set 抛 DivideByZero、或让渲染层越界。
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType entry = types[i];
                if (entry == null)
                {
                    continue;
                }

                try
                {
                    ParticleRegistry.ValidateTypeInfo(entry.Info, entry.Info.DebugName);
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArgumentException($"types[{i}] 非法：{exception.Message}", nameof(types), exception);
                }
            }

            _width = width;
            _height = height;
            _types = types;
            _cells = new Cell[width * height];
            _humidity = new HumidityField(width, height);
            _heat = new HeatMap(width, height, _cells, types, _humidity);
            _light = new LightController();
            _liquidMotion = new MotionMonitor(this, ParticleTags.Liquid);
            _solidMotion = new MotionMonitor(this, ParticleTags.Solid, ParticleTags.Organic); // 无机固体：落沙声的口径
            _counts = new ParticleCountMonitor(this);

            // 扫描进行中的热 / 湿度变化要同帧并入候选位图（见 OnThermalCellDirtied）：全网格旧路径里，
            // 先扫到的格 OnLogic 改了温度后，扫描序在其后的格同帧就能在新温度下做 OnMotion；混合扫描
            // 必须复现这一点，否则晚一帧、逐位发散。委托实例缓存一次，运行期零分配。
            _heat.DirtiedHook = OnThermalCellDirtied;
            _humidity.DirtiedHook = OnThermalCellDirtied;

            // 湿度场的**增量**通道（与上面那条脏格通道是两个独立消费者位）：光照的每列光路湿度缓存按
            // 逐格 Δ 平移。**不覆盖** DirtiedHook——两者各占一个字段、各绑一个委托，任何一条都能再加消费者。
            _humidity.ChangedHook = _light.OnHumidityChanged;
            _tick = 0;
            _rng = seed == 0u ? DefaultSeed : seed;
            _stamp = OutsideStamp;

            // 运动休眠：唤醒集 = 类型表所有 MotionReadReach 盒**反转**后的并集（见 MotionReadBox）。
            // 反转：读者读 (x ± X, y−Down..y+Up) ⇒ 写在这些格的任一格都要唤醒它 ⇒ 写入唤醒盒 (X, Down, Up)，
            // 即「行 dy ∈ [−Up, Down]、半宽 X」。并集按行取最宽半宽即可（对称区间求并仍是区间）。
            // 初态整张位图置位——世界刚建好，所有格都还没被求值过，第一帧必须全扫描。
            int reach = MotionReadBox.MaxReach;
            var halfByDy = new sbyte[2 * reach + 1];
            for (int i = 0; i < halfByDy.Length; i++)
            {
                halfByDy[i] = -1;
            }

            int minDy = reach + 1;
            int maxDy = -reach - 1;
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType entry = types[i];
                if (entry == null)
                {
                    continue;
                }

                MotionReadBox writeBox = entry.Info.MotionReadReach.Reversed;
                for (int dy = -writeBox.Down; dy <= writeBox.Up; dy++)
                {
                    int slot = dy + reach;
                    if (halfByDy[slot] < (sbyte)writeBox.X)
                    {
                        halfByDy[slot] = (sbyte)writeBox.X;
                    }

                    if (dy < minDy)
                    {
                        minDy = dy;
                    }

                    if (dy > maxDy)
                    {
                        maxDy = dy;
                    }
                }
            }

            // 运动状态的传播格固定需要 +1 行：固体向下移动会把**原格正上方**的同伴标成运动中
            //（见 Swap 的接力标记），那颗同伴必须在下一扫描被求值，否则接力断掉。是否求值取决于
            // 本并集，所以这里把 dy = +1 行固定并入——不赌各类型 MotionReadReach 的声明凑巧覆盖
            //（宁多求值一行，不影响逐位语义）。置位越界格由 MarkInMotionAbove 自己裁掉。
            if (halfByDy[reach + 1] < 1)
            {
                halfByDy[reach + 1] = 1;
            }

            if (maxDy < minDy)
            {
                // 类型表里没有任何带行为的类型：没有任何行需要唤醒。
                minDy = 0;
                maxDy = -1;
            }
            else if (maxDy < 1)
            {
                maxDy = 1;
            }

            var wakeHalf = new sbyte[maxDy - minDy + 1];
            for (int dy = minDy; dy <= maxDy; dy++)
            {
                wakeHalf[dy - minDy] = halfByDy[dy + reach];
            }

            _wakeHalf = wakeHalf;
            _wakeMinDy = minDy;
            _wakeMaxDy = maxDy;

            _chunkCols = (width + ChunkMask) >> ChunkShift;
            _chunkCount = _chunkCols * ((height + ChunkMask) >> ChunkShift);
            _denseFlushThreshold = _cells.Length / DenseFlushCellDivisor;
            _saturatedFlushThreshold = _cells.Length / SaturatedFlushCellDivisor;
            _candidates = new ulong[_chunkCount * WordsPerChunk];
            _scanCandidates = new ulong[_chunkCount * WordsPerChunk];
            _logicCells = new ulong[_chunkCount * WordsPerChunk]; // 初建为空网格 ⇒ 全 0（没有格带逻辑）
            _processedBits = new ulong[(_cells.Length + 63) >> 6];
            _inMotion = new byte[_cells.Length];
            _inMotionPending = new byte[_cells.Length];
            for (int i = 0; i < _candidates.Length; i++)
            {
                _candidates[i] = ulong.MaxValue;
            }
        }

        /// <summary>写入唤醒集的格数（= 类型表所有运动阅读盒反转后的并集大小）。只读，供测试 / 诊断。</summary>
        public int MotionWakeOffsetCount
        {
            get
            {
                int total = 0;
                for (int dy = _wakeMinDy; dy <= _wakeMaxDy; dy++)
                {
                    sbyte half = _wakeHalf[dy - _wakeMinDy];
                    if (half >= 0)
                    {
                        total += 2 * half + 1;
                    }
                }

                return total;
            }
        }

        /// <summary>类型表。<c>Types[0]</c> 为 <c>null</c>。</summary>
        public ParticleType[] Types
        {
            get { return _types; }
        }

        /// <summary>
        /// 底层格子数组。<b>只读契约</b>：外部只允许读，任何写入都必须走
        /// <see cref="Set"/> / <see cref="Clear"/> / <see cref="Swap"/>，否则会绕过钩子与处理标记。
        /// </summary>
        public Cell[] Cells
        {
            get { return _cells; }
        }

        /// <summary>
        /// 与网格同形的热图（每格一个热量 Q），世界持有并随世界重建。
        /// 热量**由物质携带**：<see cref="Swap"/> 会把 Q 一起搬走；<see cref="Set"/> / <see cref="Clear"/>
        /// 换上的新物质继承该格原有热量（旧物质离场，热就地留给新物质）。温度是导出量（<c>T = Q / C</c>）。
        /// 每逻辑帧由 <see cref="StepLogic"/> 做一次全格四邻热传导；粒子类型通过它查询 / 修改任意位置的温度与热量。
        /// </summary>
        public HeatMap Heat
        {
            get { return _heat; }
        }

        /// <summary>
        /// 与网格同形的**湿度**场（每格一个水汽质量），世界持有并随世界重建。
        /// 只有非固非液的格持有湿度；水蒸发提升湿度，湿度像热量一样传播并带温度定向
        /// （水平冷→热、竖直越热越向上），高值低温处凝结成雨。
        /// 湿度随 <see cref="Swap"/> 被气流搬走（云随风平移）；<see cref="Set"/> / <see cref="Clear"/> 换上全新物质时，
        /// 本格残余水汽一律先由 <see cref="EvacuateHumidity"/> 推给四邻气体格再清零——无论目标是气体还是固/液
        /// （固/液不持有湿度），旧水汽都不会被就地湮灭（推不掉则留下，守恒优先于不变量）。
        /// 蒸发 / 凝结 / 降水都是 1:1 搬运，因此水总量守恒。见 <see cref="HumidityField"/>。
        /// </summary>
        public HumidityField Humidity
        {
            get { return _humidity; }
        }

        /// <summary>
        /// 光照控制器：按时序通断，照亮时向每列最上面的非气体粒子（固体或液体——光打到水面就停）注入热量。
        /// 每逻辑帧由 <see cref="StepLogic"/> 驱动；游戏侧通过 <c>world.Light.Enabled</c> 等属性配置。
        /// </summary>
        public LightController Light
        {
            get { return _light; }
        }

        /// <summary>
        /// 液体**运动量**的观察者（按换位的液体端数记账，见 <see cref="MotionMonitor"/>）。
        /// 世界常驻持有并随世界重建，读数方每帧读完 <see cref="MotionMonitor.Reset"/> 一次
        /// （水的流动声就是它的消费方：响度 ∝ <see cref="MotionMonitor.MovingRatio"/>）。
        /// 掩码 = <see cref="ParticleTags.Liquid"/>（带上该标签的类型）。
        /// 它是纯观察者：零 RNG 消耗、零格子写入、零场写入——读不读、用它做什么都不改变模拟结果。
        /// </summary>
        public MotionMonitor LiquidMotion
        {
            get { return _liquidMotion; }
        }

        /// <summary>
        /// 固体**运动量**的观察者（按换位的固体端数记账，见 <see cref="MotionMonitor"/>）。
        /// 世界常驻持有并随世界重建，读数方每帧读完 <see cref="MotionMonitor.Reset"/> 一次
        /// （下落声就是它的消费方：它读**绝对** <see cref="MotionMonitor.MovingCount"/> 而不是比例——
        /// 固体存量里大多是常年定植不动的成员，<see cref="MotionMonitor.MovingRatio"/> 会被稀释，
        /// 详见 <see cref="MotionMonitor"/> 的类型注释）。
        /// 掩码 = 带 <see cref="ParticleTags.Solid"/> **且不带** <see cref="ParticleTags.Organic"/>：
        /// 石 / 沙 / 土 / 湿土 / 腐殖质 / 腐殖土 / 湿润腐殖土 / 草种子——会下落的粉末；
        /// 树叶 / 树木种子 / 草 / 花 / 树干带 <see cref="ParticleTags.Organic"/>，被排除。
        /// 它是纯观察者：零 RNG 消耗、零格子写入、零场写入——读不读、用它做什么都不改变模拟结果。
        /// </summary>
        public MotionMonitor SolidMotion
        {
            get { return _solidMotion; }
        }

        /// <summary>
        /// 粒子**存量**的观察者（按 <see cref="Set"/> / <see cref="SetPhase"/> 的旧/新类型差分记账，
        /// 见 <see cref="ParticleCountMonitor"/>）。世界常驻持有并随世界重建；消费方读
        /// <see cref="ParticleCountMonitor.Count"/> 取某类型的当前格数、读
        /// <see cref="ParticleCountMonitor.ConsumeCreated"/> 取自上次读取以来新写入的格数
        /// （火焰持续声就是它的消费方：世界里有没有火、这一帧又冒出几格火）。
        /// 它是纯观察者：零 RNG 消耗、零格子写入、零场写入——读不读、用它做什么都不改变模拟结果。
        /// </summary>
        public ParticleCountMonitor Counts
        {
            get { return _counts; }
        }

        /// <summary>网格宽（格数）。</summary>
        public int Width
        {
            get { return _width; }
        }

        /// <summary>网格高（格数）。</summary>
        public int Height
        {
            get { return _height; }
        }

        /// <summary>格子总数。</summary>
        public int CellCount
        {
            get { return _cells.Length; }
        }

        /// <summary>已完成的 tick 数量。步进过程中它还是「本次 Step 之前」的值。</summary>
        public int Tick
        {
            get { return _tick; }
        }

        /// <summary>
        /// 运动休眠开关（默认 <c>true</c>）。为 true 时 <see cref="StepMotion"/> 只求值候选格
        /// （被写格按类型表运动阅读盒并集唤醒的格 + 温度变化格半径 2 内的格），
        /// 静止粒子整块 chunk 跳过；为 false 时退回**全网格旧路径**，用于 A/B 对照与回归验证。
        /// </summary>
        /// <remarks>
        /// 语义等价性：候选格集合是「自上次求值以来输入可能变化」的格的超集，因此跳过静止粒子
        /// 不改变任何模拟结果。<see cref="StepLogic"/> 的扫描在本开关开启时同样走混合扫描
        /// （候选格跑完整逻辑 + 运动、其余 HasLogic 格只跑 <c>OnLogic</c>；<c>OnLogic</c> 从不休眠，
        /// 见 <see cref="ScanLogicSleeping"/>），关闭时退回全网格旧路径。
        /// 运行中随时可改；休眠关闭期间不累积候选位，从 false 切回 true 时整张位图置位一次，
        /// 等价于「刚构造完」的初态，不会漏唤醒。
        /// </remarks>
        public bool MotionSleepEnabled
        {
            get { return _motionSleepEnabled; }
            set
            {
                if (value && !_motionSleepEnabled)
                {
                    // 关闭期间没有累积候选位（写路径整段跳过），重新打开时整张置位一次，
                    // 等价于「刚构造完」的初态，避免漏唤醒。
                    for (int i = 0; i < _candidates.Length; i++)
                    {
                        _candidates[i] = ulong.MaxValue;
                    }
                }

                _motionSleepEnabled = value;
            }
        }

        /// <summary>
        /// 地下水规则开关（默认 <c>true</c>）：**固体置换液体前必须处于运动中**
        /// （<see cref="HasMomentum"/>：上一扫描位移过，或运行中刚放置）——静止的固体推不动液体，
        /// 石头 / 沙 / 土里封闭的水不再被上方静止的固体挤上去。
        /// 为 false 时退回旧的 rank 置换行为（A/B 对照与回归验证）。
        /// 运行期切换会整张置位运动候选位图（静止粒子需要重新求值），可在播放中安全地做 A/B 对照。
        /// </summary>
        /// <remarks>
        /// 纯读取规则：不开状态、不消耗随机流；运动状态（<see cref="_inMotion"/>）的维护与开关无关，
        /// 关掉开关也照常记账。运动休眠透明性不受影响
        /// （回归见 <c>GroundwaterTests.MotionSleepTransparency_WithGroundwaterRule</c>）。
        /// </remarks>
        public bool GroundwaterSealingEnabled
        {
            get { return _groundwaterSealingEnabled; }
            set
            {
                if (value == _groundwaterSealingEnabled)
                {
                    return;
                }

                _groundwaterSealingEnabled = value;

                // 闸门翻转会改变「已静止粒子」的置换决策（开关关掉后静止的沙开始下沉、或反过来被挡），
                // 它们没有写入、不会被自然唤醒——整张候选位图置位一次，等价于「刚构造完」的初态。
                // 休眠关闭时不消费位图，留给 MotionSleepEnabled 的 setter 在重新打开时统一置位。
                if (_motionSleepEnabled)
                {
                    for (int i = 0; i < _candidates.Length; i++)
                    {
                        _candidates[i] = ulong.MaxValue;
                    }
                }
            }
        }

        /// <summary>
        /// (x,y) 上的内容是否处于运动中：上一扫描位移过，或刚放置（见 <see cref="_inMotion"/>）。
        /// 越界返回 false。地下水闸门（<see cref="ParticleRules.CanDisplace"/>）读它。
        /// </summary>
        public bool HasMomentum(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            return _inMotion[y * _width + x] != 0;
        }

        /// <summary>
        /// 把 (x,y) 的内容标记为「运动中」：本扫描内即刻可读（刚放置的格当场就能置换液体），
        /// 并写入 pending —— 下一次扫描开始时转正，再下一次扫描开始时过期。
        /// </summary>
        private void MarkInMotion(int x, int y)
        {
            int index = y * _width + x;
            _inMotion[index] = 1;
            _inMotionPending[index] = 1;
        }

        /// <summary>把 (x,y) 正上方那格标记为「运动中」——它是下一步会跟随落下的同伴。</summary>
        private void MarkInMotionAbove(int x, int y)
        {
            int above = y + 1;
            if ((uint)above < (uint)_height)
            {
                MarkInMotion(x, above);
            }
        }

        /// <summary>
        /// 扫描开始时的动量轮转：pending 的集合转正为 live（= 上一扫描位移过的格 + 扫描之间放置的格），
        /// 旧 live 清空后用作下一轮的 pending。由 <see cref="Scan"/> 在每次扫描（运动帧 / 逻辑帧、
        /// 休眠开 / 关）开头调用一次——显式过期，全程 O(格数/字节) 且稳态零分配。
        /// </summary>
        private void RotateMomentumAtScanStart()
        {
            byte[] live = _inMotion;
            _inMotion = _inMotionPending;
            _inMotionPending = live;
            Array.Clear(_inMotionPending, 0, _inMotionPending.Length);
        }

        /// <summary>
        /// 最近一次扫描实际求值的格数（= 调用过 <c>OnMotion</c> 的格数，不含被跳过的格）。
        /// 静止场景应远小于 <see cref="CellCount"/>；<see cref="MotionSleepEnabled"/> 为 false 时约为粒子总数。
        /// 逻辑帧（<see cref="StepLogic"/>）也消费运动候选，同样更新它（休眠关闭时逻辑帧仍走全扫、不计数）。
        /// </summary>
        public int MotionCellsEvaluatedLastStep
        {
            get { return _motionCellsEvaluatedLastStep; }
        }

        /// <summary>下一次（或正在进行的）<see cref="StepLogic"/> 的扫描方向奇偶：false = x 升序，true = x 降序。</summary>
        public bool Parity
        {
            get { return (_tick & 1) != 0; }
        }

        /// <summary>
        /// 每逻辑帧随机抽取并触发随机刻的格数（0 = 关闭，默认）。类似 Minecraft 的 random tick：
        /// 不是全网格扫描，而是每逻辑帧只随机挑这么多格，命中非空气、非静态的格就调用其类型的
        /// <c>OnRandomTick</c>。适合「缓慢、按概率发生」的过程（生长、蔓延、渗流、腐化）。
        /// </summary>
        /// <remarks>
        /// 抽取**有放回**（同一格可能在同一 tick 被抽中多次），且用的是世界同一条确定性随机流，
        /// 因此计数与普通粒子规则的随机序列共享。赋值夹在 <c>[0, CellCount]</c>。
        /// </remarks>
        public int RandomTicksPerStep
        {
            get { return _randomTicksPerStep; }
            set
            {
                int max = _cells.Length;
                if (value < 0)
                {
                    _randomTicksPerStep = 0;
                }
                else
                {
                    _randomTicksPerStep = value > max ? max : value;
                }
            }
        }

        /// <summary>
        /// 步进之外的写入标记：等于**下一次 Step** 奇偶取反，即「本格尚未被本次 Step 处理」。
        /// </summary>
        private byte OutsideStamp
        {
            get { return (byte)((_tick & 1) ^ 1); }
        }

        /// <summary>把 (x,y) 换算成 <see cref="Cells"/> 的下标。不检查越界。</summary>
        public int Index(int x, int y)
        {
            return y * _width + x;
        }

        /// <summary>(x,y) 是否在网格内。</summary>
        public bool IsInBounds(int x, int y)
        {
            return (uint)x < (uint)_width && (uint)y < (uint)_height;
        }

        /// <summary>(x,y) 是否为空气/越界。</summary>
        public bool IsAir(int x, int y)
        {
            return GetCell(x, y).typeId == ParticleRegistry.AirTypeId;
        }

        /// <summary>读取格子（值拷贝，4 字节）。越界返回空气格。</summary>
        public Cell GetCell(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return default;
            }

            return _cells[y * _width + x];
        }


        /// <summary>
        /// 就地读写本格（flags / shade / 将来的 vx,vy）。
        /// 越界时返回一个共享的只读暂存格，写入它不会影响网格。
        /// </summary>
        /// <remarks>
        /// 移动之后坐标即失效：任何改变自身位置的写操作之后，禁止继续使用旧的 (x, y) 或之前的 ref。
        /// 类型代码只能 OR 位：<c>At(x,y).flags |= CellFlags.Burning;</c>，整体赋值会破坏 bit0。
        /// 就地改了会**影响自己 OnMotion 决策**的状态（如失活标记）之后，必须再调一次
        /// <see cref="WakeMotion"/>，否则运动休眠会跳过这格（详见其 remarks）。
        /// </remarks>
        public ref Cell At(int x, int y)
        {
            if (IsInBounds(x, y))
            {
                return ref _cells[y * _width + x];
            }

            _outside = default;
            return ref _outside;
        }

        /// <summary>
        /// 交换两个格子。位置变化**只能**通过它完成：不产生任何对象创建或销毁。
        /// 两个格子的 bit0 都会被写成当前 tick 的标记，因此被移动的粒子不会在本 tick 被二次处理。
        /// **不触发** <c>OnSpawn</c> / <c>OnKill</c>；<c>shade</c> 随格子一起移动，不重新抽样。
        /// Q 与湿度随物质一起搬运；若被搬动的是**气体**，竖直位移还会按
        /// <see cref="HeatMap.AdiabaticLapse"/> 做一次绝热直减（上升冷却、下沉增温，见 <see cref="ApplyAdiabatic"/>）。
        /// </summary>
        /// <returns>两个坐标都在界内则 true；任一越界则什么都不做并返回 false。</returns>
        public bool Swap(int x0, int y0, int x1, int y1)
        {
            if (!IsInBounds(x0, y0) || !IsInBounds(x1, y1))
            {
                return false;
            }

            int a = y0 * _width + x0;
            int b = y1 * _width + x1;
            if (a == b)
            {
                return true;
            }

            Cell swap = _cells[a];
            ushort type0 = swap.typeId;
            ushort type1 = _cells[b].typeId;
            _cells[a] = _cells[b];
            _cells[b] = swap;
            _heat.SwapHeat(a, b);

            // 热 pass 的逐格输入（热容 / σ / ε / 天空暴露）只由 typeId 决定；两端同型时交换不改变任何一格
            // 的热输入，故不必打断热图的不动点跳过。Q 与湿度的变化各有自己的版本号（SwapHeat / HumidityField.Swap
            // 只在两格值不等时才动版本，绝热直减走 AddHeat），因此这里只在 typeId 真的换位时才递增写入版本。
            if (_cells[a].typeId != _cells[b].typeId)
            {
                _writeVersion++;
            }

            // 湿度跟着物质走：云因此随风平移。固/液永远持有 0 湿度（不变量），
            // 所以水与气体换位时搬过去的是气体那侧的湿度，水不会把水汽带走。
            _humidity.Swap(a, b);

            // 光照的每列缓存：两格（类型 / 湿度）都已换完之后才通知。挡光格参与换位会改变光路本身，
            // 由控制器重扫那两列；同行换位只涉及一列。湿度搬迁那条路已由湿度场的增量通知平移过。
            _light.OnColumnsSwapped(type0, type1, x0, y0, x1, y1);

            // 绝热直减：气体竖直换位时按位移调整自身热量——上升（Δy > 0）冷却、下沉（Δy < 0）增温。
            // 两格各自按「新 y − 旧 y」独立结算，因此上升侧与下沉侧的 ΔQ 符号相反；水平换位 Δy = 0 直接跳过。
            ApplyAdiabatic(a, x0, y0, y0 - y1);
            ApplyAdiabatic(b, x1, y1, y1 - y0);

            byte stamp = _stamp;
            _cells[a].flags = CellFlags.WithProcessed(_cells[a].flags, stamp);
            _cells[b].flags = CellFlags.WithProcessed(_cells[b].flags, stamp);

            // 运动休眠：被写格（两个都算）周围 R 内置候选位——移动后的粒子与两个格的邻居
            // 都可能在下一运动帧改主意；本帧扫描中还要把写入格之外的邻居也立即唤醒（同帧级联）。
            // 两个被写格本身标成「本帧已处理」：本帧稍后扫到它们时跳过（等价于旧 bit0 语义）。
            MarkProcessed(a);
            MarkProcessed(b);

            // 地下水闸门：两端格各记「运动中」——本扫描内即刻可读（下一步就能置换液体），
            // 下一扫描起仍有效（显式过期见 RotateMomentumAtScanStart）。
            MarkInMotion(x0, y0);
            MarkInMotion(x1, y1);

            // 接力：只有**向下移动的固体**才把它**原格正上方**的同伴记为运动中——沙柱 / 整块沙逐格
            // 下落时，上一颗腾出的空位（随即被水填上）由下一颗接着置换，靠的就是这一格标记。
            // 液体↔液体（水↔水对流）、气体换位与水平换位（y0 == y1）都不置位：否则池面上一块静止的
            // 固体被水下对流「从底下」凭空点亮动量，随即自己沉进水里（对流拖石板）。
            // 两格已在上面交换完毕：向下移动的那份此刻正躺在较低的那格里。
            if (y0 != y1)
            {
                int lowerX = y0 < y1 ? x0 : x1;
                int lowerY = y0 < y1 ? y0 : y1;
                int upperX = y0 < y1 ? x1 : x0;
                int upperY = y0 < y1 ? y1 : y0;
                ushort moverId = _cells[lowerY * _width + lowerX].typeId;
                if (moverId != ParticleRegistry.AirTypeId && moverId < _types.Length)
                {
                    ParticleType mover = _types[moverId];
                    if (mover != null && (mover.Info.Tags & ParticleTags.Solid) != 0)
                    {
                        MarkInMotionAbove(upperX, upperY);
                    }
                }
            }

            MarkCandidates(x0, y0);
            MarkCandidates(x1, y1);
            MarkCandidatesThisScan(x0, y0, x1, y1);

            // 逻辑帧混合扫描：两格的类型整体换位，_logicCells 位随之交换（两端各拿到对方的位）。
            SwapLogicCells(x0, y0, x1, y1);

            // 高速模式监视器：搬运不是生灭——不计数，只把两端列的地表高度按交换后的真实内容拉回真值。
            if (_monitor != null)
            {
                _monitor.OnSwap(x0, y0, x1, y1);
            }

            // 液体运动量的记账（水的流动声的数据源）：按换位的液体端数累加，无方向 / 无格位过滤——
            // 平静水面的逐格抖动与瀑布的整片下落都算「液体在动」，量级自然区分开。
            _liquidMotion.OnSwap(x0, y0, x1, y1);

            // 固体运动量的记账（下落声的数据源）：同一交换按换位的「无机固体」端数累加。液体与固体各自
            // 只看自己掩码内的端点，一次只涉及一类的交换对另一类毫无影响。
            _solidMotion.OnSwap(x0, y0, x1, y1);

            return true;
        }

        /// <summary>
        /// (x,y) 上的物质是不是气体 / 空（空气 typeId 0，或带 <see cref="ParticleTags.Gas"/> 标签的类型）。
        /// 湿度的可驻留判据：非气体格必须持有 0 湿度（固/液是屏障，见 <see cref="EvacuateHumidity"/>）。
        /// </summary>
        private bool IsGasType(ushort typeId)
        {
            if (typeId == ParticleRegistry.AirTypeId)
            {
                return true;
            }

            if (typeId >= _types.Length)
            {
                return false;
            }

            ParticleType type = _types[typeId];
            return type != null && (type.Info.Tags & ParticleTags.Gas) != 0;
        }

        /// <summary>
        /// <see cref="Swap"/> 之后的绝热直减结算：<paramref name="index"/> 上的物质刚从 (x, y - Δy) 搬到 (x, y)，
        /// 若它是气体则按 <c>ΔQ = -AdiabaticLapse × Δy × C</c> 调整热量（上升冷却、下沉增温）。
        /// C 取**该格**热容（类型热容 + 湿度附加，见 <see cref="HeatMap.GetCapacityAt(int)"/>）。水平位移 Δy = 0 时直接返回。
        /// </summary>
        /// <remarks>
        /// 热量按**位温**守恒的近似兑现：气块在界内竖直移动时 Q 增减，而不是把 Q 原样搬走。
        /// 两格独立结算，故上升格与下沉格的 ΔQ 大小相同、符号相反——界内总量在成对换位里精确抵消。
        /// 热路径：一次标签/typeId 判断 + 一次类型表查表，无分配。
        /// </remarks>
        private void ApplyAdiabatic(int index, int x, int y, int deltaY)
        {
            if (deltaY == 0)
            {
                return;
            }

            ushort typeId = _cells[index].typeId;
            if (!IsGasType(typeId))
            {
                return;
            }

            float capacity = _heat.GetCapacityAt(index);
            _heat.AddHeat(x, y, -_heat.AdiabaticLapse * deltaY * capacity);
        }

        /// <summary>
        /// 把 (x,y) 无条件写成 <paramref name="typeId"/>。
        /// </summary>
        /// <remarks>
        /// 执行顺序严格为：读受害者 → 受害者 <c>OnKill(world, x, y, typeId)</c>（此刻网格上仍是受害者本体）
        /// → 写入新格（typeId / flags = 当前标记 / shade 重新抽样）→ 新类型 <c>OnSpawn</c>。
        /// 因此 <c>OnKill</c> 内对本格的写会被随后写入的新格覆盖，残留物必须写到邻居格。
        /// 换上的是**全新物质**：旧物质离场，但它的热就地留给新物质（继承该格原有热量）；旧格残余水汽
        /// （本格残湿 > 0 时）先由 <see cref="EvacuateHumidity"/> 推给四邻气体格再清零，无论目标是气体
        /// 还是固/液（推不掉则留下，守恒优先于不变量）。
        /// 同类型重复 Set 是空操作，不触发任何钩子、不消耗随机数。
        /// </remarks>
        /// <returns>坐标越界或 typeId 未注册则 false（非法 typeId 只报错一次）。</returns>
        public bool Set(int x, int y, ushort typeId)
        {
            return SetCore(x, y, typeId, 0, false);
        }

        /// <summary>
        /// 与 <see cref="Set"/> **同一条写入路径**，但 <c>shade</c> 由调用方显式给定、**不随机抽样**。
        /// </summary>
        /// <remarks>
        /// 唯一用途是动物死亡轮廓的物质化：轮廓的每一格要带着 profile 里的颜色索引落格
        /// （见 <c>CreatureDeathMaterializer</c>），重抽颜色会把尸体打散成随机噪点。
        /// 其余语义逐条等同 <see cref="Set"/>：同样触发旧类型 <c>OnKill</c> / 新类型 <c>OnSpawn</c>、
        /// 继承该格原有热量、疏散残余水汽（<see cref="EvacuateHumidity"/>）、维护运动休眠 / 逻辑帧位图、
        /// 通知 <c>_monitor.OnCellReplaced</c>、走通用有机衰变骰（<see cref="MaybeLeaveHumus"/>）。
        /// 同类型重复写入同样是空操作（不触发任何钩子、不改 <c>shade</c>）。
        /// 这两条公共入口都转调私有的 <see cref="SetCore"/>，不存在第二份会分叉的实现。
        /// </remarks>
        /// <param name="shade">写入 <see cref="Cell.shade"/> 的颜色索引。越界（&gt;= 该类型调色板长度）则
        /// **整次写入被拒绝**（该格一字节不动），每个世界实例至多报一条错；空气格没有调色板，<c>shade</c> 恒写成 0。</param>
        /// <returns>坐标越界、typeId 未注册或 shade 越界则 false。</returns>
        public bool SetWithShade(int x, int y, ushort typeId, byte shade)
        {
            return SetCore(x, y, typeId, shade, true);
        }

        /// <summary>
        /// <see cref="Set"/> 与 <see cref="SetWithShade"/> 的共同实现。
        /// <paramref name="explicitShade"/> 为 true 时 <c>shade</c> 用调用方给的值，否则按类型调色板抽样。
        /// </summary>
        private bool SetCore(int x, int y, ushort typeId, byte shade, bool explicitShade)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            if (typeId != ParticleRegistry.AirTypeId && (typeId >= _types.Length || _types[typeId] == null))
            {
                ReportUnknownType(typeId);
                return false;
            }

            // 显式 shade 越界：写入前就整次拒绝（该格保持不变）。空气没有调色板条目、shade 恒被写成 0，不校验。
            if (explicitShade && typeId != ParticleRegistry.AirTypeId
                && shade >= _types[typeId].Info.Palette.Length)
            {
                ReportInvalidShade(typeId, shade);
                return false;
            }

            int index = y * _width + x;
            ushort previousId = _cells[index].typeId;
            if (previousId == typeId)
            {
                return true;
            }

            if (previousId != ParticleRegistry.AirTypeId && previousId < _types.Length)
            {
                ParticleType victim = _types[previousId];
                if (victim != null)
                {
                    victim.OnKill(this, x, y, typeId);
                }
            }

            ref Cell cell = ref _cells[index];
            cell.typeId = typeId;
            cell.flags = _stamp;
            _writeVersion++;

            // 地下水闸门：**运行中**放置的物质视作「运动中」——画笔直接放进水里的沙石照旧下沉。
            // 构造期（tick 0）例外：初始条件一律视作静止，不给宽限期，否则用 Set 搭出来的初始布局
            // 在第一遍运动扫描里会被整片当作「刚运动过」，水囊首帧即被抽动。
            if (_tick > 0)
            {
                MarkInMotion(x, y);
            }

            // 运动休眠：新物质落入本格——本格与周围 R 内的邻居都要重新求值（下一帧），
            // 本帧扫描中邻居立即唤醒（同帧级联）；本格标成「本帧已处理」，本帧稍后扫到时跳过。
            MarkProcessed(index);
            MarkCandidates(x, y);
            MarkCandidatesThisScan(x, y, x, y);

            // 逻辑帧混合扫描：本格的新类型是否带 OnLogic 行为，决定它的 _logicCells 位。
            SetLogicCell(x, y, typeId);

            // 高速模式监视器：新格刚写完（OnSpawn 之前）——old ≠ new 才计生灭，空气也计一条；顺带更新该列地表。
            if (_monitor != null)
            {
                _monitor.OnCellReplaced(previousId, typeId, x, y);
            }

            // 液体存量：类型换了就按旧/新是否液体差分（水的流动声的响度的分母）。
            _liquidMotion.OnCellReplaced(previousId, typeId);

            // 固体存量：同一次类型替换按旧/新是否「无机固体」差分（下落声那条的存量，纯观察、无消费方读比例）。
            _solidMotion.OnCellReplaced(previousId, typeId);

            // 粒子存量：同一次类型替换按旧/新类型差分，并记「新格」一次（火焰持续声的存量与新增来源）。
            // Clear(x, y) 就等价于 Set(x, y, AirTypeId)（见 Clear），因此递减走的是这一条路径，不需要额外钩子。
            _counts.OnCellReplaced(previousId, typeId);

            // 新物质继承该格原有热量：旧物质离场，但它的热视作就地留给了新物质（新生成的有机物
            // 不会被拍成 0°——例如夜里发芽的草继承暖空气的 Q，不会当帧冻死）。相变要带走自己的热时用 SetPhase。

            // 水汽也是旧物质的一部分：无论目标是气体还是固/液，本格残余水汽都先推给四邻气体格再清零
            // （推不掉则留在原地，守恒优先，见 EvacuateHumidity）。旧写法对气体目标直接 ResetAt 会
            // 凭空湮灭水汽（FB2：火蔓延 / 自灭）；固/液目标本不持有湿度，两条路径因此统一为同一疏散
            // 语义，residual <= 0 的快路径由 EvacuateHumidity 内部保留。
            EvacuateHumidity(x, y, index);

            // 光照的每列缓存：格子（类型 / 湿度）都已提交之后才通知，缓存绝不会读到半写状态。
            _light.OnTypesReplaced(previousId, typeId, x, y);

            if (typeId == ParticleRegistry.AirTypeId)
            {
                cell.shade = 0;
                MaybeLeaveHumus(previousId, typeId, x, y);
                return true;
            }

            ParticleType type = _types[typeId];
            if (explicitShade)
            {
                // 调用方给了颜色索引（死亡轮廓），原样落格、不抽样——这次写入因此不消耗随机流。
                cell.shade = shade;
            }
            else
            {
                RenderColor[] palette = type.RenderPalette;
                cell.shade = (byte)(NextUInt() % (uint)palette.Length);
            }

            type.OnSpawn(this, x, y);
            MaybeLeaveHumus(previousId, typeId, x, y);
            return true;
        }

        /// <summary>
        /// 有机物死亡的衰变：<paramref name="previousId"/> 带 <see cref="ParticleTags.Organic"/> 标签、
        /// 且新类型不带该标签时，有 <c>1/<see cref="OrganicDecayDenominator"/></c>（20%）的概率把这一格
        /// 就地变成腐殖质（<see cref="ParticleIds.Humus"/>）。
        /// </summary>
        /// <remarks>
        /// 为什么放在世界里而不是类型的 <c>OnKill</c>：<c>OnKill</c> 在写入新格**之前**调用，
        /// 它对自身格的写入会被随后写入的新格覆盖（见 ParticleTypeInterface.md §6）。要让「死掉的有机体
        /// 变成腐殖质」真正落到这一格，只能在写完新格之后改判——与 <see cref="StepRain"/> 直接引用
        /// <see cref="ParticleIds.Water"/> 同理，是世界的**通用过程**（判据只看标签，产物是固定的腐殖质）。
        /// 摇骰只对「有机物被非有机物替换」发生，因此普通死亡路径的随机流不受影响、依旧可复现。
        /// **例外**：<paramref name="newId"/> 本身就是腐殖质时不摇骰——显式霉烂（草种子 /
        /// 树木种子的 <c>SetPhase → Humus</c>）已经是完成态的衰变，通用骰不可能再改变结果
        /// （命中后 <c>SetPhase(Humus)</c> 被 <c>previousId == typeId</c> early-return 吞掉），只会白耗一个随机数。
        /// 该豁免让霉烂路径之后的世界随机流比旧行为前移一格（确定性不受影响：同种子同命令序列仍逐位可复现）。
        /// </remarks>
        private void MaybeLeaveHumus(ushort previousId, ushort newId, int x, int y)
        {
            // 新格已是腐殖质（显式霉烂的产物）＝ 衰变已经发生，不再叠加通用衰变骰。
            if (newId == ParticleIds.Humus)
            {
                return;
            }

            if (!IsOrganic(previousId) || IsOrganic(newId))
            {
                return;
            }

            if (NextUInt() % OrganicDecayDenominator != 0u)
            {
                return;
            }

            // OnSpawn 可能已把本格改成别的类型（相变链）；只在仍是刚写入的类型时才留下腐殖质。
            if (_cells[y * _width + x].typeId != newId)
            {
                return;
            }

            // 走 SetPhase：腐殖质继承本格热量，且再触发不了本规则（新类型非有机）。
            SetPhase(x, y, ParticleIds.Humus);
        }

        /// <summary>(x,y) 上的粒子是否带 <see cref="ParticleTags.Organic"/> 标签。空气 / 越界 / 空槽一律 false。</summary>
        private bool IsOrganic(ushort typeId)
        {
            if (typeId == ParticleRegistry.AirTypeId || typeId >= _types.Length)
            {
                return false;
            }

            ParticleType type = _types[typeId];
            return type != null && (type.Info.Tags & ParticleTags.Organic) != 0;
        }

        /// <summary>抹除 (x,y)：等价于 <c>Set(x, y, AirTypeId)</c>，会触发原类型的 <c>OnKill(world, x, y, 0)</c>。</summary>
        public bool Clear(int x, int y)
        {
            return Set(x, y, ParticleRegistry.AirTypeId);
        }

        /// <summary>
        /// **就地改写本格状态之后的运动唤醒**：类型在 <c>OnRandomTick</c> / <c>OnLogic</c> 里直接经
        /// <see cref="At"/> 改了本格 <c>flags</c>（不改 <c>typeId</c>、不走 <see cref="Set"/> /
        /// <see cref="SetPhase"/> / <see cref="Swap"/>）之后，只要这次改动会影响自己**后面** <c>OnMotion</c>
        /// 的决策，就必须调用它——否则运动休眠会把这格当成未变化而跳过，模拟结果与全网格旧路径发散。
        /// </summary>
        /// <remarks>
        /// 语义 = 写入原语自带的候选唤醒那一半（<see cref="MarkCandidates"/> 唤醒下一帧 +
        /// <see cref="MarkCandidatesThisScan"/> 补齐本帧正在扫描的位图），但**不改变格子内容**、不触发
        /// <c>OnSpawn</c> / <c>OnKill</c>、不重抽 <c>shade</c>、不动动量与处理标记。休眠关闭时是空操作
        /// （全网格本来每帧都会求值）。
        /// </remarks>
        public void WakeMotion(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return;
            }

            MarkCandidates(x, y);
            MarkCandidatesThisScan(x, y, x, y);
        }

        /// <summary>
        /// **相变专用**：把 (x,y) 换成 <paramref name="typeId"/>，并把旧格的**热量 Q 与湿度**原样带过去，
        /// 随后触发新类型的 <c>OnSpawn</c>（新类型因此可以在 OnSpawn 里结算潜热、消费湿度等）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="Set"/> 的区别正在于「携带」：<c>Set</c> 视为换上**全新物质**（继承该格热量、湿度驱逐），
        /// 而相变是**同一份物质换了个形态**——水 / 大气之间的质量与热量必须留在原格、逐格守恒。
        /// 典型用法是蒸发：<c>SetPhase(x, y, ParticleIds.Atmosphere)</c> 保留 Q，新大气因为热容骤降而变得更烫（浮力来源）。
        /// **湿度只对气体形态原样携带**：换成非气体（固/液）时固/液不持有湿度，该格水汽改由
        /// <see cref="EvacuateHumidity"/> 推给四邻气体格（推不掉则留下，守恒优先于不变量）。
        /// 抛给新类型 <c>OnSpawn</c> 的理由是它可能还要改这一格；它**不会**替相变双方做守恒记账，
        /// 那由各自的类型代码负责（见 <c>WaterParticle</c> 与 <see cref="StepRain"/>）。
        /// </remarks>
        /// <returns>坐标越界或 typeId 未注册则 false（非法 typeId 只报错一次）。</returns>
        public bool SetPhase(int x, int y, ushort typeId)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            if (typeId != ParticleRegistry.AirTypeId && (typeId >= _types.Length || _types[typeId] == null))
            {
                ReportUnknownType(typeId);
                return false;
            }

            int index = y * _width + x;
            ushort previousId = _cells[index].typeId;
            if (previousId == typeId)
            {
                return true;
            }

            if (previousId != ParticleRegistry.AirTypeId && previousId < _types.Length)
            {
                ParticleType victim = _types[previousId];
                if (victim != null)
                {
                    victim.OnKill(this, x, y, typeId);
                }
            }

            float heat = _heat.GetHeat(x, y);

            // 相变到非气体（固/液）时固/液不持有湿度：先把它推给四邻气体格再清零。
            // 气体形态则原样携带——本格湿度不动，即随之进入新形态。
            if (!IsGasType(typeId))
            {
                EvacuateHumidity(x, y, index);
            }

            ref Cell cell = ref _cells[index];
            cell.typeId = typeId;
            cell.flags = _stamp;
            _writeVersion++;

            // 地下水闸门：**运行中**相变出来的物质视作「运动中」（与 Set 的新格同一语义）——
            // 扫描中（OnLogic 内）相变同样在下一扫描继续有效。
            // 与 Set 同理，构造期（tick 0）不给宽限期——初始条件一律视作静止。
            if (_tick > 0)
            {
                MarkInMotion(x, y);
            }

            // 运动休眠：相变后的新形态与周围邻居都要重新求值（下一帧），本帧扫描中邻居立即唤醒；
            // 本格标成「本帧已处理」。
            MarkProcessed(index);
            MarkCandidates(x, y);
            MarkCandidatesThisScan(x, y, x, y);

            // 逻辑帧混合扫描：相变后的新形态是否带 OnLogic 行为，决定它的 _logicCells 位。
            SetLogicCell(x, y, typeId);

            // 高速模式监视器：新形态刚写完（OnSpawn 之前）——相变同样是一次「旧的灭、新的生」。
            if (_monitor != null)
            {
                _monitor.OnCellReplaced(previousId, typeId, x, y);
            }

            // 液体存量：相变改了类型就差分（水↔湿土、水↔蒸汽等都算）。
            _liquidMotion.OnCellReplaced(previousId, typeId);

            // 固体存量：相变同样按旧/新是否「无机固体」差分（水↔湿土这类相变在固体那条也会出入账）。
            _solidMotion.OnCellReplaced(previousId, typeId);

            // 粒子存量：相变同样是一次类型替换，按旧/新类型差分并记「新格」一次（水⇄蒸汽等相变也计入存量）。
            _counts.OnCellReplaced(previousId, typeId);

            // Q 跟着物质走，不重置（这正是与 Set 的区别）。
            _heat.SetHeat(x, y, heat);

            // 光照的每列缓存：类型、湿度与热量都已提交之后才通知（见 SetCore 的同名调用）。
            _light.OnTypesReplaced(previousId, typeId, x, y);

            if (typeId == ParticleRegistry.AirTypeId)
            {
                cell.shade = 0;
            }
            else
            {
                RenderColor[] palette = _types[typeId].RenderPalette;
                cell.shade = (byte)(NextUInt() % (uint)palette.Length);
            }

            if (typeId != ParticleRegistry.AirTypeId)
            {
                _types[typeId].OnSpawn(this, x, y);
            }

            MaybeLeaveHumus(previousId, typeId, x, y);
            return true;
        }

        /// <summary>
        /// 降水：湿度达到当前温度的**饱和湿度**（<see cref="HumidityField.SaturationHumidity"/>）的气体格
        /// 按 <c>湿度 × RainRate</c> 的概率凝结成雨。由 <see cref="StepLogic"/> 每逻辑帧调用一次。
        /// </summary>
        /// <remarks>
        /// 饱和湿度随温度下降而下降（饱和水汽压曲线的指数近似）：越冷越容易凝结，这是"云底在低温处"的判据。
        /// 湿度越高掉得越快，这是让云量收敛的负反馈（否则水汽会一路攒成满天薄雾）。
        /// 凝结把一整个水格的质量从湿度场搬成一个 Water 粒子（1:1，水守恒），并按
        /// <see cref="HumidityField.LatentHeat"/> 按凝结温度给本格补上潜热（凝结放热，加热云层）；
        /// 格子变成液体后由 <see cref="SetPhase"/> 把剩余水汽推给周围的气体邻居——
        /// 固/液不持有湿度这条不变量因此得以保持，同时不丢质量。
        /// </remarks>
        private void StepRain()
        {
            int width = _width;
            int height = _height;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    if (!ParticleRules.IsGasOrEmpty(this, x, y))
                    {
                        continue; // 只有气体 / 空持有湿度
                    }

                    float humidity = _humidity.GetAt(i);
                    if (humidity <= 0f)
                    {
                        continue; // 绝大多数格没有水汽：跳过温度读取与饱和曲线计算
                    }

                    float temperature = _heat.GetTemperatureAt(i);
                    // 饱和判据：湿度低于该温度下的饱和湿度就不凝结。SaturationHumidity 用
                    // RainThreshold · 2^((T − DewPoint)/10) 表达「越冷越容易饱和」，替代旧的线性 coldFactor；
                    // 高湿高温的空气因此不再降雨（热地表不会积累成永久白斑），低温柔量空气反而能凝结。
                    if (humidity < HumidityField.SaturationHumidity(temperature))
                    {
                        continue;
                    }

                    // 碰并：雨滴由**本格 + 气体邻居**的水汽凑成（现实中雨滴靠碰并长大，不是凭空出现）。
                    // 先看凑不凑得够一个整水滴——凑不够就这一 tick 不降，绝不能"取不足 1 却生成 1"（凭空造水）。
                    if (GatherableHumidity(x, y) < HumidityField.Unit)
                    {
                        continue;
                    }

                    float roll = (NextUInt() >> 8) * (1f / 16777216f);
                    if (roll >= humidity * HumidityField.RainRate)
                    {
                        continue;
                    }

                    // 本格剩下的水汽（湿度高于一个水滴时）马上要变成液体，而固/液不持有湿度——
                    // 若四周全是液/固（封闭气泡），残余无处安放，降这一滴就会把水汽永久困在水里。
                    // 所以先确认收得下，收不下就这一 tick 不降（而不是降完再漏）。
                    if (_humidity.GetAt(i) - HumidityField.Unit > 0f && CountMoistReceivers(x, y) == 0)
                    {
                        continue;
                    }

                    // 本格优先，再从气体邻居补足。
                    float need = HumidityField.Unit;
                    float self = _humidity.GetAt(i);
                    float takeSelf = self < need ? self : need;
                    _humidity.AddAt(i, -takeSelf);
                    need -= takeSelf;

                    for (int dy = -1; dy <= 1 && need > 0f; dy++)
                    {
                        for (int dx = -1; dx <= 1 && need > 0f; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            int nx = x + dx;
                            int ny = y + dy;
                            if (!IsInBounds(nx, ny) || !ParticleRules.IsGasOrEmpty(this, nx, ny))
                            {
                                continue;
                            }

                            float available = _humidity.Get(nx, ny);
                            float take = available < need ? available : need;
                            _humidity.Add(nx, ny, -take);
                            need -= take;
                        }
                    }

                    // 本格剩下的水汽（湿度高于一个水滴时）由 SetPhase 在换成液体时推给四邻气体格
                    // （固/液不持有湿度）——上面的封闭气泡检查保证收得下、不会把水汽困在水里。
                    SetPhase(x, y, ParticleIds.Water);

                    // 凝结释放潜热，加热云层：每凝结一个水格，按凝结时的温度还回相变潜热。
                    _heat.AddHeat(x, y, HumidityField.LatentHeat(temperature));
                }
            }
        }

        /// <summary>
        /// (x,y) 及其**八邻**的气体格上总共有多少湿度（降雨"碰并"的可用料）。
        /// 用 3×3 而不是四邻：云层里每格湿度只有 ~0.15，四邻凑不满一个整水滴，
        /// 于是只有湿度最高的顶行才下得了雨——雨就显得"从虚空里落下来"。八邻让整层云都能下雨。
        /// </summary>
        private float GatherableHumidity(int x, int y)
        {
            float total = 0f;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(this, nx, ny))
                    {
                        total += _humidity.Get(nx, ny);
                    }
                }
            }

            return total;
        }

        /// <summary>(x,y) 四邻中能接收湿度的气体格数（0 = 封闭：残余湿度无处安放）。</summary>
        private int CountMoistReceivers(int x, int y)
        {
            int receivers = 0;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NeighbourX[k];
                int ny = y + NeighbourY[k];
                if (IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(this, nx, ny))
                {
                    receivers++;
                }
            }

            return receivers;
        }

        /// <summary>
        /// 把 (x,y) 一格的水汽**全部**推给四邻气体格，随即把本格湿度清零；无气体邻居可推时留在原地。
        /// </summary>
        /// <remarks>
        /// 调用时机：任何覆盖旧气体的写入——<see cref="Set"/> 无条件调用（目标气体或固/液皆然），
        /// <see cref="SetPhase"/> 在目标非气体时调用（相变到气体则原样携带湿度、不经此处），
        /// 降雨凝结路径则经 <see cref="SetPhase"/> 间接走到这里。固/液不持有湿度：若留下残余，等它日后
        /// 相变回气体时会与新增的水汽叠加、凭空造水（实测：每 4000 tick 多出 0.3 个水格）；气体被直接
        /// 覆盖若就地清零，同样会凭空湮灭水汽（FB2：火蔓延 / 自灭）。四面都不是气体时**不清零**——
        /// 守恒优先于「非固液格才持湿」这条不变量。
        /// </remarks>
        private void EvacuateHumidity(int x, int y, int index)
        {
            float residual = _humidity.GetAt(index);
            if (residual <= 0f)
            {
                _humidity.ResetAt(index); // 抹掉 -0f 之类的极小负值，保证不变量成立
                return;
            }

            int receivers = CountMoistReceivers(x, y);
            if (receivers == 0)
            {
                return; // 四面都不是气体：无处可去，只能留在原地（守恒优先）
            }

            float share = residual / receivers;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NeighbourX[k];
                int ny = y + NeighbourY[k];
                if (IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(this, nx, ny))
                {
                    _humidity.Add(nx, ny, share);
                }
            }

            _humidity.ResetAt(index);
        }

        /// <summary>确定性随机数（xorshift32）。类型代码一律用它，禁止 UnityEngine.Random。</summary>
        public uint NextUInt()
        {
            uint x = _rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _rng = x;
            return x;
        }

        /// <summary>[0, maxExclusive) 内的确定性随机整数；maxExclusive &lt;= 0 时返回 0。</summary>
        public int NextInt(int maxExclusive)
        {
            return maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);
        }

        /// <summary>[0, maxExclusive) 内的确定性随机字节；maxExclusive == 0 时返回 0。</summary>
        public byte NextByte(byte maxExclusive)
        {
            return maxExclusive == 0 ? (byte)0 : (byte)(NextUInt() % maxExclusive);
        }

        /// <summary>
        /// 推进一个**运动帧**（60 Hz 的基本节拍）：只做一次扫描，对每个粒子调用其
        /// <see cref="ParticleType.OnMotion"/>。**不跑相变、热传导、湿度、降水、光照或随机刻**——
        /// 那些都属于逻辑帧，见 <see cref="StepLogic"/>。
        /// </summary>
        /// <remarks>
        /// <para>与渲染解耦：本方法不接触任何渲染资源，由上层决定跑完几次之后才上屏一次。
        /// 运动帧是世界的唯一时基：<see cref="Tick"/> 无论走哪一个入口都每帧自增一次，
        /// 扫描方向与 bit0 处理标记也随之交替。</para>
        /// <para><b>运动休眠</b>（<see cref="MotionSleepEnabled"/>，默认开）：只求值候选格——
        /// 自上次求值以来输入可能变化的格（被写格按运动阅读盒并集唤醒、以及温度变化格周围 2 格）。静止粒子整个 chunk 跳过，因此静止场景的
        /// <see cref="MotionCellsEvaluatedLastStep"/> 远小于 <see cref="CellCount"/>。
        /// 候选集合是「输入可能变化」的超集，跳过静止粒子不改变任何模拟结果。</para>
        /// </remarks>
        public void StepMotion()
        {
            try
            {
                Scan(logic: false);
                _tick++;
            }
            finally
            {
                // 某个 OnMotion 抛异常时 _tick 未自增，此处按「旧 tick」恢复写入标记，
                // 使异常后新建的粒子仍会被下一帧处理；正常路径则按自增后的 tick 恢复。
                _stamp = OutsideStamp;

                // 异常时休眠扫描提前退出，位图收尾与正常路径一致（本帧消费的位清空、标志复位）；
                // 扫描期间已写入的候选位留在 _candidates，下一帧照常求值。
                if (_motionScanActive)
                {
                    _motionScanActive = false;
                    Array.Clear(_scanCandidates, 0, _scanCandidates.Length);
                }
            }
        }

        /// <summary>
        /// 推进一个**逻辑帧**（20 Hz）：在照常做一次运动扫描的基础上，先对每个粒子调用其
        /// <see cref="ParticleType.OnLogic"/>（相变 / 反应），扫描后再推进随机刻与整场的
        /// 热传导、湿度输送、降水与光照。
        /// </summary>
        /// <remarks>
        /// 分段：<b>①粒子扫描</b>（每格先 <c>OnLogic</c>、未被相变换走的再 <c>OnMotion</c>；休眠开启时走
        /// <see cref="ScanLogicSleeping"/> 混合扫描——候选格跑完整两钩子、其余 HasLogic 格只跑 <c>OnLogic</c>，
        /// <c>OnLogic</c> 从不休眠）→
        /// <b>①′随机刻</b>（随机抽 <see cref="RandomTicksPerStep"/> 个格，命中非空气非静态的格就调 <c>OnRandomTick</c>）→
        /// <b>②热传导 + 顶行向虚空散热</b>（<see cref="HeatMap.Step"/>）→
        /// <b>③湿度输送</b>（<see cref="HumidityField.Step"/>：扩散 + 水平冷→热 + 竖直越热越向上）→
        /// <b>③′降水</b>（湿度越过该温度饱和湿度的气体格凝结成雨，并按 <see cref="HumidityField.LatentHeat"/> 释放潜热）→
        /// <b>④光照</b>（<see cref="LightController.Step"/>，向每列最上面的非气体粒子施热）→
        /// <b>⑤热唤醒并入</b>（把本阶段 Q 变化过的格按半径 2 刷子并入运动休眠的候选位图）。
        /// 因此逻辑帧注入的热 / 湿度会在下一个逻辑帧被粒子读到、并参与下一轮扩散。
        /// 上层应让逻辑帧落在运动帧的节拍上——每 3 个运动帧跑一次本方法，即 60 Hz 运动 / 20 Hz 逻辑。
        /// </remarks>
        public void StepLogic()
        {
            try
            {
                Scan(logic: true);

                // ①′ 随机刻：与扫描同属「块行为」，因此放在 _tick 自增之前、
                // 让 OnRandomTick 读到的 world.Tick 与 OnLogic / OnMotion 一致；热/湿度/光照仍在其后。
                RunRandomTicks();

                _tick++;

                // 网格扫描之后：②全格四邻热传导（含辐射 / 虚空散热）→ ③湿度输送（扩散 + 冷→热 + 越热越向上）
                // → ③′降水（饱和且够冷的气体格凝结成雨、释放潜热）→ ④光照（向每列最上面的非气体粒子施热）。
                // 全程在 try 内：任一阶段抛异常时，其后阶段一并不生效；此时 _tick 已自增（见上），
                // 但 finally 会按新 tick 恢复写入标记，因此「异常后粒子仍会在下一帧被处理」的约定依然成立。
                _heat.CellsVersion = _writeVersion;
                _heat.Step();
                _humidity.Step(this);
                StepRain();
                _light.Step(this);

                // ⑤ 运动休眠的温度唤醒：本逻辑阶段内 Q 或湿度变化过的格，按半径 2 刷子并入候选位图
                // （半径 2 = 运动决策的温度读距上界，见 HeatWakeRadius）。这些标记随其它写入一起
                // 留到下一个 StepMotion 消费——两次逻辑帧之间温度场不变，因此唤醒一次求值后若粒子
                // 没有位移，后续运动帧跳过它与全扫描精确等价。
                FlushTemperatureDirty();
            }
            finally
            {
                // 无论正常还是异常退出，都按**当前** tick 恢复写入标记：扫描 / 随机刻阶段抛异常时 _tick 尚未自增，
                // 按旧 tick 恢复，使异常后新建的粒子仍会被下一帧处理；_tick 自增之后（热 / 湿度 / 降水 / 光照）
                // 抛异常则按新 tick 恢复，标记同样与下一帧的扫描一致。
                _stamp = OutsideStamp;

                // 混合扫描（休眠开启）的位图收尾与 StepMotion 一致：正常路径已在 ScanLogicSleeping 内完成，
                // 这里只兜住逻辑钩子抛异常的提前退出，避免 _motionScanActive 悬留为 true。
                if (_motionScanActive)
                {
                    _motionScanActive = false;
                    Array.Clear(_scanCandidates, 0, _scanCandidates.Length);
                }
            }
        }

        /// <summary>
        /// 扫描入口。休眠开启（<see cref="MotionSleepEnabled"/>，默认）时走休眠扫描：运动帧
        /// （<paramref name="logic"/> = false）只求值候选格（<see cref="ScanSleeping"/>）；逻辑帧走
        /// <see cref="ScanLogicSleeping"/>——候选格跑完整 <c>OnLogic → OnMotion</c>，其余带
        /// <see cref="ParticleTypeInfo.HasLogic"/> 的格只跑 <c>OnLogic</c>（不跑运动）。休眠关闭时走
        /// **全网格旧路径**，行为逐位不变：行从高到低、x 方向按 <see cref="Tick"/> 奇偶交替，逐格
        /// <see cref="ProcessCell"/>。
        /// </summary>
        private void Scan(bool logic)
        {
            byte stamp = (byte)(_tick & 1);
            _stamp = stamp;

            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int height = _height;
            bool reversed = (_tick & 1) != 0;

            // 「本帧已处理」记录：每次扫描开始时整张清空（O(N/64)），扫描中逐格置位，
            // 写入（Set / SetPhase / Swap）也立即置位，使本帧稍后扫到的被写格被跳过。
            // 为什么不用 Cell.flags 的 bit0：bit0 只有 1 位奇偶，无法区分「本帧已处理」与
            // 「偶数个 tick 之前处理过」——休眠跳过若干帧的格会残留同奇偶的 bit0，被误判成已处理
            // 而漏求值（全扫描路径靠「每 tick 处理所有格」掩盖了这一点，休眠路径不能）。
            // bit0 仍照旧由写入维护（类型作者契约与既有测试都依赖它），只是不再作为跳过判据。
            Array.Clear(_processedBits, 0, _processedBits.Length);

            // 地下水闸门：动量在扫描边界轮转——pending（本扫描期间位移 / 放置的格）转正为 live，
            // 旧 live 清空后接替为 pending。必须在任何求值之前完成；休眠开 / 关、运动帧 / 逻辑帧走同一处。
            RotateMomentumAtScanStart();

            if (_motionSleepEnabled)
            {
                if (logic)
                {
                    ScanLogicSleeping(stamp, reversed, cells, types, width, height);
                }
                else
                {
                    ScanSleeping(stamp, reversed, cells, types, width, height);
                }

                return;
            }

            bool counting = !logic;
            int evaluated = 0;
            for (int y = height - 1; y >= 0; y--)
            {
                int rowBase = y * width;
                if (reversed)
                {
                    for (int x = width - 1; x >= 0; x--)
                    {
                        if (ProcessCell(x, y, rowBase + x, stamp, logic, cells, types))
                        {
                            evaluated++;
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (ProcessCell(x, y, rowBase + x, stamp, logic, cells, types))
                        {
                            evaluated++;
                        }
                    }
                }
            }

            if (counting)
            {
                _motionCellsEvaluatedLastStep = evaluated;
            }
        }

        /// <summary>
        /// 运动休眠扫描：只求值候选格，扫描顺序与全网格扫描**逐格一致**（y 降序、x 随 Tick 奇偶交替），
        /// 只是跳过位图未置位的格——因此确定性契约（同种子同命令逐格可复现）不受影响。
        /// </summary>
        /// <remarks>
        /// 位图生命周期：本方法开始时把「待消费」的 <see cref="_candidates"/> 与 <see cref="_scanCandidates"/>
        /// 互换（写入从此刻起落到 <see cref="_candidates"/>，留给下一帧），扫描只读 <see cref="_scanCandidates"/>，
        /// 末尾把它整张清空。因此扫描期间的写操作不会丢、也不会影响本帧的读位图。
        /// 以 16 格为粒度做快路径：某 chunk 在当前行的那 16 位全 0 就整段跳过（chunk 全 0 等价于每行都整段跳过）。
        /// <b>段内必须每次重新读那一个 64 位字</b>：<see cref="MarkCandidatesThisScan"/> 会在扫描途中往同一段里补位
        /// （本帧级联），把字缓存在局部变量里会让这些新位永远看不到 —— 表现为「同一 16 格段内的级联晚一帧」，
        /// 与全扫描结果发散。段入口的那次判空仍然是新鲜的：能让本段置位的写入，要么在本段之前就已发生，
        /// 要么必然来自本段自身的求值（那只有在段入口非空时才可能）。
        /// </remarks>
        private void ScanSleeping(
            byte stamp, bool reversed, Cell[] cells, ParticleType[] types, int width, int height)
        {
            ulong[] candidates = _candidates;
            _candidates = _scanCandidates;
            _scanCandidates = candidates;

            ulong[] bits = _scanCandidates;
            int chunkCols = _chunkCols;
            int evaluated = 0;

            _motionScanActive = true;

            for (int y = height - 1; y >= 0; y--)
            {
                int rowBase = y * width;
                int chunkRowBase = (y >> ChunkShift) * chunkCols;
                int wordInChunk = (y & ChunkMask) >> 2;
                int bitBase = (y & 3) << 4;

                if (reversed)
                {
                    for (int cx = chunkCols - 1; cx >= 0; cx--)
                    {
                        ulong word = bits[((chunkRowBase + cx) << 2) + wordInChunk];
                        if (word == 0UL)
                        {
                            continue;
                        }

                        int wordIndex = ((chunkRowBase + cx) << 2) + wordInChunk;
                        int xStart = cx << ChunkShift;
                        int xEnd = xStart + ChunkSize;
                        if (xEnd > width)
                        {
                            xEnd = width;
                        }

                        for (int x = xEnd - 1; x >= xStart; x--)
                        {
                            if ((bits[wordIndex] & (1UL << (bitBase + (x & ChunkMask)))) == 0UL)
                            {
                                continue;
                            }

                            if (ProcessCell(x, y, rowBase + x, stamp, false, cells, types))
                            {
                                evaluated++;
                            }
                        }
                    }
                }
                else
                {
                    for (int cx = 0; cx < chunkCols; cx++)
                    {
                        ulong word = bits[((chunkRowBase + cx) << 2) + wordInChunk];
                        if (word == 0UL)
                        {
                            continue;
                        }

                        int wordIndex = ((chunkRowBase + cx) << 2) + wordInChunk;
                        int xStart = cx << ChunkShift;
                        int xEnd = xStart + ChunkSize;
                        if (xEnd > width)
                        {
                            xEnd = width;
                        }

                        for (int x = xStart; x < xEnd; x++)
                        {
                            if ((bits[wordIndex] & (1UL << (bitBase + (x & ChunkMask)))) == 0UL)
                            {
                                continue;
                            }

                            if (ProcessCell(x, y, rowBase + x, stamp, false, cells, types))
                            {
                                evaluated++;
                            }
                        }
                    }
                }
            }

            _motionCellsEvaluatedLastStep = evaluated;

            _motionScanActive = false;
            Array.Clear(_scanCandidates, 0, _scanCandidates.Length);
        }

        /// <summary>
        /// 逻辑帧混合扫描（休眠开启时）：扫描顺序与全网格扫描**逐格一致**（y 降序、x 随 Tick 奇偶交替），
        /// 但对格分三种处理：
        /// <list type="bullet">
        /// <item>运动候选位置位 → 完整 <see cref="ProcessCell"/>（先 <c>OnLogic</c>、未被相变换走的再 <c>OnMotion</c>），
        /// 计入运动求值；</item>
        /// <item>仅 <see cref="_logicCells"/> 置位（该格类型带 <c>OnLogic</c>）→ <see cref="TakeProcessed"/> + flags 戳 +
        /// 只调 <c>OnLogic</c>（不调 <c>OnMotion</c>、不计运动求值）；</item>
        /// <item>都不置位 → 跳过。</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// <para><b>为什么 OnLogic 永不休眠</b>：<c>OnLogic</c> 含随机重掷型行为（水蒸发摇骰、火熄灭档位骰），
        /// 「输入没变就跳过」对它们不等价（满扫每帧重掷、休眠不重掷会改变速率）。因此逻辑帧对**每个**
        /// HasLogic 格仍每帧同序调用一次 <c>OnLogic</c>（RNG 消耗序列与全扫完全一致）；休眠只作用于 <c>OnMotion</c>
        /// 那一半，其等价性由运动休眠既有证明（输入未变 ⇒ 运动决策不变）保证，见 <see cref="ScanSleeping"/>。</para>
        /// <para>位图生命周期与 <see cref="ScanSleeping"/> 相同：换出 <see cref="_candidates"/> ↔ <see cref="_scanCandidates"/>、
        /// 扫描期间 <see cref="_motionScanActive"/> = true（写入继续往本帧位图补位，保持同帧级联）、末尾清空
        /// <see cref="_scanCandidates"/>。<see cref="_logicCells"/> 是持久位图，不换出、不清空。段内每格重读候选字
        /// （级联可能在段内补位）。</para>
        /// </remarks>
        private void ScanLogicSleeping(
            byte stamp, bool reversed, Cell[] cells, ParticleType[] types, int width, int height)
        {
            ulong[] candidates = _candidates;
            _candidates = _scanCandidates;
            _scanCandidates = candidates;

            ulong[] bits = _scanCandidates;
            ulong[] logicBits = _logicCells;
            int chunkCols = _chunkCols;
            int evaluated = 0;

            _motionScanActive = true;

            for (int y = height - 1; y >= 0; y--)
            {
                int rowBase = y * width;
                int chunkRowBase = (y >> ChunkShift) * chunkCols;
                int wordInChunk = (y & ChunkMask) >> 2;
                int bitBase = (y & 3) << 4;

                if (reversed)
                {
                    for (int cx = chunkCols - 1; cx >= 0; cx--)
                    {
                        int wordIndex = ((chunkRowBase + cx) << 2) + wordInChunk;
                        if ((bits[wordIndex] | logicBits[wordIndex]) == 0UL)
                        {
                            continue;
                        }

                        int xStart = cx << ChunkShift;
                        int xEnd = xStart + ChunkSize;
                        if (xEnd > width)
                        {
                            xEnd = width;
                        }

                        for (int x = xEnd - 1; x >= xStart; x--)
                        {
                            if (ProcessHybridCell(x, y, rowBase + x, stamp, bits, logicBits, wordIndex, bitBase, cells, types))
                            {
                                evaluated++;
                            }
                        }
                    }
                }
                else
                {
                    for (int cx = 0; cx < chunkCols; cx++)
                    {
                        int wordIndex = ((chunkRowBase + cx) << 2) + wordInChunk;
                        if ((bits[wordIndex] | logicBits[wordIndex]) == 0UL)
                        {
                            continue;
                        }

                        int xStart = cx << ChunkShift;
                        int xEnd = xStart + ChunkSize;
                        if (xEnd > width)
                        {
                            xEnd = width;
                        }

                        for (int x = xStart; x < xEnd; x++)
                        {
                            if (ProcessHybridCell(x, y, rowBase + x, stamp, bits, logicBits, wordIndex, bitBase, cells, types))
                            {
                                evaluated++;
                            }
                        }
                    }
                }
            }

            _motionCellsEvaluatedLastStep = evaluated;

            _motionScanActive = false;
            Array.Clear(_scanCandidates, 0, _scanCandidates.Length);
        }

        /// <summary>
        /// 处理逻辑帧混合扫描的一格。返回是否真的调用了 <c>OnMotion</c>（候选格才可能，用于求值计数）。
        /// 候选位可能被**本帧更早的求值**写入（同帧级联），因此每格都重读 <paramref name="bits"/> 的那一个字。
        /// </summary>
        /// <remarks>仅 logic 位置位时走「只跑 OnLogic」分支：它与 <see cref="ProcessCell"/> 的 logic 半等价
        /// （同样 <see cref="TakeProcessed"/> + flags 戳 + <c>OnLogic</c>），只是不接 <c>OnMotion</c>。</remarks>
        private bool ProcessHybridCell(
            int x, int y, int index, byte stamp, ulong[] bits, ulong[] logicBits, int wordIndex, int bitBase,
            Cell[] cells, ParticleType[] types)
        {
            ulong bit = 1UL << (bitBase + (x & ChunkMask));
            if ((bits[wordIndex] & bit) != 0UL)
            {
                return ProcessCell(x, y, index, stamp, true, cells, types);
            }

            if ((logicBits[wordIndex] & bit) == 0UL || !TakeProcessed(index))
            {
                return false;
            }

            cells[index].flags = CellFlags.WithProcessed(cells[index].flags, stamp);
            ushort id = cells[index].typeId;
            if (id < types.Length && types[id] != null)
            {
                types[id].OnLogic(this, x, y);
            }

            return false;
        }

        /// <summary>
        /// 场脏格回调（<see cref="HeatMap.DirtiedHook"/> / <see cref="HumidityField.DirtiedHook"/>）：
        /// 把温度发生变化的格**同帧**并入正在扫描的候选位图，复现全网格旧路径里「先扫格的 OnLogic 改温度、
        /// 后扫格同帧在新温度下决策」的可见性。
        /// </summary>
        /// <remarks>
        /// <para>为什么必须同帧：火 <c>OnLogic</c> 向本格注热 250Q，越过大气浮力的温度死区；全网格里扫描序
        /// 在其下方（y 更小）的大气格当帧就能读到新温度并上浮，混合扫描若把这次热变化留到帧末
        /// <see cref="FlushTemperatureDirty"/>（下一帧才唤醒），就会晚一帧决策、逐位发散（实测首个点火 tick 即发散）。
        /// 蒸发摊湿度改变热容 <c>C</c>、进而改变 <c>T = Q / C</c>，走的是同一条通道。</para>
        /// <para>唤醒盒即类型表反转阅读盒并集（生产表 151 格——雨水穿冠把水的阅读盒下沿扩到 9），⊇ 运动决策的温度读距（半径 2），是安全超集：
        /// 多唤醒几格不改变结果。非扫描期（场 pass、光照、降水等）由 <see cref="MarkCandidatesThisScan"/>
        /// 自带的 <see cref="_motionScanActive"/> 门控变成空操作，那条后置路径仍由帧末半径 2 唤醒球处理。</para>
        /// </remarks>
        private void OnThermalCellDirtied(int index)
        {
            if (!_motionScanActive)
            {
                return;
            }

            int y = index / _width;
            MarkCandidatesThisScan(index - y * _width, y, index - y * _width, y);
        }

        /// <summary>本帧是否已处理过 <paramref name="index"/>；未处理则置位并返回 true（至多处理一次）。</summary>
        private bool TakeProcessed(int index)
        {
            int word = index >> 6;
            ulong mask = 1UL << (index & 63);
            if ((_processedBits[word] & mask) != 0UL)
            {
                return false;
            }

            _processedBits[word] |= mask;
            return true;
        }

        /// <summary>把 <paramref name="index"/> 标成「本帧已处理」。写入（Set / SetPhase / Swap）调用，
        /// 使本帧稍后扫到的被写格不会被再次求值（等价于旧的 bit0 语义）。</summary>
        private void MarkProcessed(int index)
        {
            _processedBits[index >> 6] |= 1UL << (index & 63);
        }

        /// <summary>
        /// 维护 <see cref="_logicCells"/>：(x,y) 换成 <paramref name="typeId"/> 后，该格是否需要在逻辑帧调用
        /// <c>OnLogic</c>（= 非空气、非静态、<see cref="ParticleTypeInfo.HasLogic"/>）。由写入原语调用。
        /// </summary>
        private void SetLogicCell(int x, int y, ushort typeId)
        {
            int word = ((((y >> ChunkShift) * _chunkCols) + (x >> ChunkShift)) << 2) + ((y & ChunkMask) >> 2);
            ulong mask = 1UL << (((y & 3) << 4) + (x & ChunkMask));
            if (HasLogicAt(typeId))
            {
                _logicCells[word] |= mask;
            }
            else
            {
                _logicCells[word] &= ~mask;
            }
        }

        /// <summary>交换两格在 <see cref="_logicCells"/> 里的位（<see cref="Swap"/> 的格子内容整体搬走）。</summary>
        private void SwapLogicCells(int x0, int y0, int x1, int y1)
        {
            int word0 = ((((y0 >> ChunkShift) * _chunkCols) + (x0 >> ChunkShift)) << 2) + ((y0 & ChunkMask) >> 2);
            int word1 = ((((y1 >> ChunkShift) * _chunkCols) + (x1 >> ChunkShift)) << 2) + ((y1 & ChunkMask) >> 2);
            ulong mask0 = 1UL << (((y0 & 3) << 4) + (x0 & ChunkMask));
            ulong mask1 = 1UL << (((y1 & 3) << 4) + (x1 & ChunkMask));

            // 先读后写：同字不同格时两次写必须基于各自的原始位，不能踩掉第一次写。
            bool bit0 = (_logicCells[word0] & mask0) != 0UL;
            bool bit1 = (_logicCells[word1] & mask1) != 0UL;
            _logicCells[word0] = bit1 ? (_logicCells[word0] | mask0) : (_logicCells[word0] & ~mask0);
            _logicCells[word1] = bit0 ? (_logicCells[word1] | mask1) : (_logicCells[word1] & ~mask1);
        }

        /// <summary>typeId 是否需要在逻辑帧调用 <c>OnLogic</c>（非空气、非静态、HasLogic）。</summary>
        private bool HasLogicAt(ushort typeId)
        {
            if (typeId == ParticleRegistry.AirTypeId || typeId >= _types.Length)
            {
                return false;
            }

            ParticleType type = _types[typeId];
            return type != null && type.Info.HasLogic && !type.Info.IsStatic;
        }

        /// <summary>
        /// 把写入格 (x,y) 的唤醒偏移集（类型表所有运动阅读盒反转后的并集，见 <see cref="MotionReadBox"/>）
        /// 并入**下一帧**的候选位图。写操作（<see cref="Set"/> / <see cref="SetPhase"/> / <see cref="Swap"/>，
        /// <see cref="Clear"/> 经 <see cref="Set"/>）走这里。
        /// </summary>
        /// <remarks>休眠关闭时位图不被消费，整个遍历都跳过（A/B 对照走的就是这条快路径）；
        /// 重新打开时由 <see cref="MotionSleepEnabled"/> 的 setter 整张置位，保证不丢唤醒。
        /// 热路径：按固定偏移数组置位，无分配、无边界裁剪循环。</remarks>
        private void MarkCandidates(int x, int y)
        {
            if (!_motionSleepEnabled)
            {
                return;
            }

            MarkOffsets(_candidates, _wakeHalf, _wakeMinDy, _wakeMaxDy, x, y, -1, -1, -1, -1);
        }

        /// <summary>
        /// 写操作期间同步更新**本帧正在扫描**的候选位图：被写格唤醒集内的格（不含被写格自身）本帧立即重新求值。
        /// </summary>
        /// <remarks>
        /// 这是「同帧级联」的复现：全扫描里，某格改动后，扫描顺序在其**之后**的邻居会在同一帧用新状态决策；
        /// 只写下一帧的位图会让这些邻居晚一帧才看到改动，逐格结果就会与全扫描不同（实测：静止水面 + 落雨场景
        /// 在 ~37 个逻辑周期后出现水位差）。被写格自身**不置位**——它由 <see cref="_processedBits"/> 的
        /// 「本帧已处理」跳过，等价于全扫描的行为；下一帧再靠 <see cref="MarkCandidates"/> 重新求值。
        /// 已扫描过的格置位无害（本帧不会回头），下一帧的位图里本来就有它。
        /// </remarks>
        private void MarkCandidatesThisScan(int x0, int y0, int x1, int y1)
        {
            if (!_motionScanActive)
            {
                return;
            }

            MarkOffsets(_scanCandidates, _wakeHalf, _wakeMinDy, _wakeMaxDy, x0, y0, x0, y0, x1, y1);
            if (x0 != x1 || y0 != y1)
            {
                MarkOffsets(_scanCandidates, _wakeHalf, _wakeMinDy, _wakeMaxDy, x1, y1, x0, y0, x1, y1);
            }
        }

        /// <summary>把 (x,y) 的唤醒集置入 <paramref name="bits"/>：按 <paramref name="halfs"/> 逐行刷对称 x 区间；
        /// 两个 <paramref name="skip"/> 坐标（写操作自己的写入格）不置位。传 <c>-1</c> 作 skip 表示不排除任何格
        /// （行内实际格坐标恒非负，<c>skipY == -1</c> 永不命中）。</summary>
        /// <remarks>按行（dy）扫、行内按 64 位字 OR——唤醒集是逐行的对称 x 区间，见 <see cref="_wakeHalf"/>
        /// （写入唤醒集的反转盒并集）。温度脏格走专用的 <see cref="MarkHeatBall"/>，不经过这里。</remarks>
        private void MarkOffsets(
            ulong[] bits, sbyte[] halfs, int minDy, int maxDy,
            int x, int y, int skipX0, int skipY0, int skipX1, int skipY1)
        {
            if (maxDy < minDy)
            {
                return;
            }

            int width = _width;
            int height = _height;
            int chunkCols = _chunkCols;
            for (int dy = minDy; dy <= maxDy; dy++)
            {
                int half = halfs[dy - minDy];
                if (half < 0)
                {
                    continue;
                }

                int ny = y + dy;
                if ((uint)ny >= (uint)height)
                {
                    continue;
                }

                int x0 = x - half;
                int x1 = x + half;
                if (x0 < 0)
                {
                    x0 = 0;
                }

                if (x1 >= width)
                {
                    x1 = width - 1;
                }

                if (x0 > x1)
                {
                    continue;
                }

                int wordBase = (((ny >> ChunkShift) * chunkCols) << 2) + ((ny & ChunkMask) >> 2);
                int bitBase = (ny & 3) << 4;
                int cx0 = x0 >> ChunkShift;
                int cx1 = x1 >> ChunkShift;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int lo = cx == cx0 ? (x0 & ChunkMask) : 0;
                    int hi = cx == cx1 ? (x1 & ChunkMask) : ChunkMask;
                    ulong mask = ((1UL << (hi - lo + 1)) - 1UL) << lo;

                    // 写入格自身不唤醒（它们本帧由「已处理」跳过，下一帧再由普通通道置位）。
                    if (skipY0 == ny && (skipX0 >> ChunkShift) == cx)
                    {
                        mask &= ~(1UL << (skipX0 & ChunkMask));
                    }

                    if (skipY1 == ny && (skipX1 >> ChunkShift) == cx)
                    {
                        mask &= ~(1UL << (skipX1 & ChunkMask));
                    }

                    bits[wordBase + (cx << 2)] |= mask << bitBase;
                }
            }
        }

        /// <summary>
        /// 消费热图与湿度场的温度脏记录：Q 变化过的格（Δ ≠ 0）与湿度变化过的格（Δ ≠ 0，因
        /// <c>T = Q / C</c> 且 C 含湿度项）都并入候选位图（半径 2 唤醒球 / 密集回退 chunk 整块，
        /// 见 <see cref="FlushDirty"/>），随后清空记录。由 <see cref="StepLogic"/> 每逻辑帧末尾调用一次。
        /// </summary>
        /// <remarks>
        /// 这是「温度变化 ⇒ 唤醒读温度的运动决策」的完整闭环：热图管传导 / 散热 / 光照 / 潜热，
        /// 湿度场管扩散与温度定向平流——后者不经任何写入就能改变空气格的热容，因此必须一并记账，
        /// 否则大气浮力 / 热压流可能在温度已变的情况下被休眠跳过。
        /// 消费的是场维护的**紧凑脏索引列表**，只遍历真实脏格（不再 O(W·H) 扫整张脏 byte 表）。
        /// </remarks>
        private void FlushTemperatureDirty()
        {
            if (_heat.AnyDirty)
            {
                FlushDirty(_heat.DirtyIndices, _heat.DirtyCount);
                _heat.ClearDirty();
            }

            if (_humidity.AnyDirty)
            {
                FlushDirty(_humidity.DirtyIndices, _humidity.DirtyCount);
                _humidity.ClearDirty();
            }
        }

        /// <summary>
        /// 把紧凑脏索引列表里的每个脏格并入候选位图。按脏集**密度**三选一：
        /// 稀疏 → 逐格 Chebyshev 半径 <see cref="HeatWakeRadius"/> 唤醒球（<see cref="MarkHeatBall"/>）；
        /// 密集（脏格数 &gt; <see cref="_denseFlushThreshold"/>）→ chunk 整块置位（<see cref="FlushDirtyChunkWide"/>）；
        /// 极密（脏格数 &gt; <see cref="_saturatedFlushThreshold"/>）→ 整图置位。
        /// </summary>
        /// <remarks>
        /// <para>超集安全：三条通道都覆盖「运动决策读温度的最远距离」（<see cref="HeatWakeRadius"/> = 2，
        /// 见 <see cref="ParticleRules.TryThermalDrift"/> 的 <c>x ± 2</c> 判据），因此候选集 ⊇ 真实依赖集；
        /// 多唤醒几格不改变任何模拟结果（与 <see cref="MotionSleepEnabled"/> 的「超集即安全」同一依据）。</para>
        /// <para><b>为什么按密度分岔</b>：唤醒球把每个脏格换成 25 个候选位，chunk 整块置位换成 16×16——
        /// 两者的 chunk 足迹完全相同（都先把脏格按半径 2 扩张、再量化到 chunk），区别只在**块内**置了多少位。
        /// 稀疏场里块的绝大多数格是干净的，球保住「段内 64 位字全 0 就整段跳过」的快路径，因此值得逐格算；
        /// 但脏集一大，球的并集本身就逼近整网格（快路径怎么都保不住），逐格 5×5 的固定成本于是纯亏，
        /// 而 chunk 整块置位在密集场里被「整块已满 → 跳过」摊成每脏格几次字读，实测快 ~4×。
        /// 脏格过半时连 chunk 记账也成了浪费——并集已吞掉全图，一次 O(词数) 填充最便宜，
        /// 该逻辑帧的休眠退化为全扫描（与 off 同基准），换来的是不再为「反正都要算」的格付费。</para>
        /// </remarks>
        private void FlushDirty(int[] indices, int count)
        {
            if (!_motionSleepEnabled || count == 0)
            {
                return;
            }

            ulong[] bits = _candidates;

            if (count > _saturatedFlushThreshold)
            {
                for (int i = 0; i < bits.Length; i++)
                {
                    bits[i] = ulong.MaxValue;
                }

                return;
            }

            if (count > _denseFlushThreshold)
            {
                FlushDirtyChunkWide(bits, indices, count);
                return;
            }

            int width = _width;
            for (int k = 0; k < count; k++)
            {
                int index = indices[k];
                int y = index / width;
                MarkHeatBall(bits, index - y * width, y);
            }
        }

        /// <summary>
        /// 稀疏通道：把 (x,y) 的 Chebyshev 半径 <see cref="HeatWakeRadius"/> 唤醒球并入候选位图，
        /// 逐行按 64 位字 OR（<c>HeatWakeRadius</c> 固定，故按行半宽恒为 2、无需通用半宽表）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="MarkOffsets"/> 的通用版本相比：x 区间（及其两列掩码）对 5 行是同一个，
        /// 提到行循环外算一次；无 <c>skip</c> 排除、无半宽表查表、无逐行 x 夹取。
        /// <para>球半宽 2 &lt; chunk 边长 16，故 x 区间至多跨越**一条** chunk 边界（<c>cx1 - cx0 ≤ 1</c>），
        /// 右列可直接写成「左列词 + <see cref="WordsPerChunk"/>」。</para>
        /// </remarks>
        private void MarkHeatBall(ulong[] bits, int x, int y)
        {
            int width = _width;
            int height = _height;
            int chunkCols = _chunkCols;

            int x0 = x - HeatWakeRadius;
            int x1 = x + HeatWakeRadius;
            if (x0 < 0)
            {
                x0 = 0;
            }

            if (x1 >= width)
            {
                x1 = width - 1;
            }

            int cx0 = x0 >> ChunkShift;
            int cx1 = x1 >> ChunkShift;
            int lo = x0 & ChunkMask;
            int hi = x1 & ChunkMask;
            ulong maskLo = cx0 == cx1
                ? ((1UL << (hi - lo + 1)) - 1UL) << lo
                : ((1UL << (ChunkMask + 1 - lo)) - 1UL) << lo;
            ulong maskHi = cx0 == cx1 ? 0UL : (1UL << (hi + 1)) - 1UL;

            for (int dy = -HeatWakeRadius; dy <= HeatWakeRadius; dy++)
            {
                int ny = y + dy;
                if ((uint)ny >= (uint)height)
                {
                    continue;
                }

                int word = (((ny >> ChunkShift) * chunkCols + cx0) << 2) + ((ny & ChunkMask) >> 2);
                int bitBase = (ny & 3) << 4;
                bits[word] |= maskLo << bitBase;
                if (maskHi != 0UL)
                {
                    bits[word + WordsPerChunk] |= maskHi << bitBase;
                }
            }
        }

        /// <summary>
        /// 密集通道：把每个脏格周围半径 <see cref="HeatWakeRadius"/> 覆盖到的 16×16 chunk **整块**置位；
        /// 整块已满（之前有别的脏格命中过同一 chunk）即跳过，密集场里因此摊成每脏格几次字读。
        /// </summary>
        /// <remarks>整块 ⊇ 球，候选集仍是真实依赖集的超集（见 <see cref="FlushDirty"/> 的 remarks）。</remarks>
        private void FlushDirtyChunkWide(ulong[] bits, int[] indices, int count)
        {
            int width = _width;
            int chunkCols = _chunkCols;
            int chunkRows = _chunkCount / chunkCols;
            for (int k = 0; k < count; k++)
            {
                int index = indices[k];
                int y = index / width;
                int x = index - y * width;

                // 唤醒球可能跨 chunk 边界（脏格离边界不足 HeatWakeRadius），故取其覆盖到的 chunk 区间。
                int cx0 = (x - HeatWakeRadius) >> ChunkShift;
                int cx1 = (x + HeatWakeRadius) >> ChunkShift;
                int cy0 = (y - HeatWakeRadius) >> ChunkShift;
                int cy1 = (y + HeatWakeRadius) >> ChunkShift;
                if (cx0 < 0) cx0 = 0;
                if (cy0 < 0) cy0 = 0;
                if (cx1 >= chunkCols) cx1 = chunkCols - 1;
                if (cy1 >= chunkRows) cy1 = chunkRows - 1;

                for (int cy = cy0; cy <= cy1; cy++)
                {
                    int rowBase = (cy * chunkCols) << 2;
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int word = rowBase + (cx << 2);
                        if (bits[word] == ulong.MaxValue && bits[word + 1] == ulong.MaxValue
                            && bits[word + 2] == ulong.MaxValue && bits[word + 3] == ulong.MaxValue)
                        {
                            continue;
                        }

                        bits[word] = ulong.MaxValue;
                        bits[word + 1] = ulong.MaxValue;
                        bits[word + 2] = ulong.MaxValue;
                        bits[word + 3] = ulong.MaxValue;
                    }
                }
            }
        }

        /// <summary>
        /// ①′ 随机刻：均匀有放回地抽 <see cref="RandomTicksPerStep"/> 个格，命中非空气、非静态的格就调其类型委托。
        /// 不看、也不写 bit0 处理标记——随机刻独立于扫描，同一格可以在同一次 <see cref="StepLogic"/> 里被抽中多次。
        /// </summary>
        private void RunRandomTicks()
        {
            int picks = _randomTicksPerStep;
            if (picks <= 0)
            {
                return;
            }

            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int count = cells.Length;

            for (int i = 0; i < picks; i++)
            {
                int index = (int)(NextUInt() % (uint)count);
                ushort id = cells[index].typeId;
                if (id == ParticleRegistry.AirTypeId || id >= types.Length)
                {
                    continue;
                }

                ParticleType type = types[id];
                if (type == null || type.Info.IsStatic)
                {
                    continue;
                }

                int y = index / width;
                type.OnRandomTick(this, index - y * width, y);
            }
        }

        /// <summary>处理一格。返回是否真的调用了该格的 <c>OnMotion</c>（用于休眠路径的求值计数）。</summary>
        private bool ProcessCell(
            int x, int y, int index, byte stamp, bool logic, Cell[] cells, ParticleType[] types)
        {
            ushort id = cells[index].typeId;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return false;
            }

            ParticleType type = types[id];
            if (type == null || type.Info.IsStatic)
            {
                return false;
            }

            if (!TakeProcessed(index))
            {
                return false;
            }

            cells[index].flags = CellFlags.WithProcessed(cells[index].flags, stamp);

            if (logic && type.Info.HasLogic)
            {
                // 逻辑钩子先跑（相变 / 反应）。它若把本格换成了别的类型，说明这一格本帧已经「用掉」了：
                // 新格已由 Set / SetPhase 标成已处理，旧类型的运动规则也不再适用于新物质——本帧跳过运动。
                //
                // 契约（onLogic）：`OnLogic` 里**同类型**的 `Swap` 同样禁止绕过这次运动跳过。
                // 跳过判定只比较 typeId，因此「相变后原地同 id 换位」不会被识别；任何 OnLogic
                // 若在本格上做同 id 的 Swap（例如与自己左边的同类换位），必须自己在换位后
                // 保证本帧不再执行运动——否则会在同一帧被 ProcessCell 二次运动。当前所有
                // OnLogic 实现（水、湿土、大气等）都不做同 id 换位。
                //
                // HasLogic 为 false 时整段跳过：OnLogic 是严格空体（不读 / 不写 / 不耗随机流），
                // 跳过一次空调用逐位等价，只是省下调用与相变后 typeId 比较。
                type.OnLogic(this, x, y);
                if (cells[index].typeId != id)
                {
                    return false;
                }
            }

            type.OnMotion(this, x, y);
            return true;
        }

        private void ReportUnknownType(ushort typeId)
        {
            if (_unknownTypeReported)
            {
                return;
            }

            _unknownTypeReported = true;
            Report(
                $"[ParticleWorld] 未注册的 typeId {typeId} 被写入网格，已忽略。请确认 ParticleRegistration.RegisterAll() 已执行。");
        }

        /// <summary>
        /// 显式 <c>shade</c> 越界的报告（<see cref="SetWithShade"/>）：与 <see cref="ReportUnknownType"/>
        /// 同一约定——**每个世界实例至多一条** LogError（逐格日志会淹没控制台），后续越界写入静默拒绝。
        /// </summary>
        private void ReportInvalidShade(ushort typeId, byte shade)
        {
            if (_invalidShadeReported)
            {
                return;
            }

            _invalidShadeReported = true;
            Report(
                $"[ParticleWorld] SetWithShade 的 shade {shade} 超出类型 {typeId} 的调色板范围，已忽略本次写入。");
        }

        /// <summary>把一条诊断消息交给持有者（worker）或直接记日志（无人接管时），见 <see cref="MessageSink"/>。</summary>
        private void Report(string message)
        {
            Action<string> sink = MessageSink;
            if (sink != null)
            {
                sink(message);
                return;
            }

            Debug.LogError(message);
        }
    }
}
