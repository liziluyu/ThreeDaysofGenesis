using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 建 worker 时一次性给它的世界/节拍参数（**值类型、无 Unity 引用**）：主线程从设置资产采样后一次交给 worker，
    /// 此后 worker 不再认识任何 Unity 资产 / 对象。
    /// </summary>
    /// <remarks>
    /// 这里刻意**不含**倍速控制器：计划「本帧跑几个运动 tick」留在主线程（<c>SpeedController</c> 在主线程上按
    /// 真实帧时间与**上一批的实测耗时**做计划），worker 只收到「跑几个 tick」这个有界请求并如实执行。
    /// 因此 worker 侧不存在第二套累加器，也就不存在两套节拍漂移。
    /// </remarks>
    public readonly struct SimulationWorkerSetup
    {
        /// <summary>初始网格宽（格），钳到 <c>[1, ParticleWorld.MaxSide]</c>。</summary>
        public readonly int Width;

        /// <summary>初始网格高（格），钳到 <c>[1, ParticleWorld.MaxSide]</c>。</summary>
        public readonly int Height;

        /// <summary>世界随机流种子（动物层由它派生出一个独立种子）。</summary>
        public readonly uint Seed;

        /// <summary>每多少个运动 tick 跑一次逻辑 tick（<c>round(MotionRate / LogicRate)</c>，至少 1）。</summary>
        public readonly int LogicTicksPerMotionTick;

        /// <summary>每秒逻辑帧数（Hz）：写进实体帧供渲染层推进插值。</summary>
        public readonly float LogicRate;

        /// <summary>每多少个运动 tick 上屏一次（1 = 每个运动 tick 后都发布帧）。</summary>
        public readonly int StepsPerPresent;

        /// <summary>高速模式时长（逻辑帧）。</summary>
        public readonly int HighSpeedLogicFrames;

        /// <summary>初始 tick 配置（热力学 / 随机刻 / 热力调试叠加）。</summary>
        public readonly SimulationTickSettings TickSettings;

        /// <summary>初始光照配置。</summary>
        public readonly LightSettings Light;

        public SimulationWorkerSetup(
            int width,
            int height,
            uint seed,
            int logicTicksPerMotionTick,
            float logicRate,
            int stepsPerPresent,
            int highSpeedLogicFrames,
            in SimulationTickSettings tickSettings,
            in LightSettings light)
        {
            Width = width;
            Height = height;
            Seed = seed;
            LogicTicksPerMotionTick = logicTicksPerMotionTick;
            LogicRate = logicRate;
            StepsPerPresent = stepsPerPresent;
            HighSpeedLogicFrames = highSpeedLogicFrames;
            TickSettings = tickSettings;
            Light = light;
        }
    }

    /// <summary>
    /// 一批请求里主线程交给 worker 的东西（值类型）：**主线程已经计划好的**运动 tick 数 + 供帧显示用的实测倍率。
    /// </summary>
    internal struct SimulationTickRequest
    {
        /// <summary>本批要跑的运动 tick 数（主线程按倍速 / 预算计划好；worker 不再自行计划）。</summary>
        internal int Ticks;

        /// <summary>主线程实测吞吐倍率（只写进帧里的显示读数，不参与 worker 的任何决策）。</summary>
        internal float EffectiveSpeed;
    }

    /// <summary>
    /// worker 处理完一批之后的回执（值类型）：实测耗时回喂主线程的倍速控制器，发布结果供主线程观察。
    /// </summary>
    /// <remarks>回执存放在 worker 预分配的槽位里、由 worker 逐字段覆盖写入；消费方（<c>SimulationWorker.TryReadResponse</c>）
    /// 读到的总是一份完整拷贝，因此回执也是不可变快照。</remarks>
    public struct SimulationBatchResult
    {
        /// <summary>本条回执对应的请求序号（与 <see cref="SimulationWorker.RequestSequence"/> 对齐）。</summary>
        public long RequestSequence;

        /// <summary>本批被请求的运动 tick 数。</summary>
        public int RequestedTicks;

        /// <summary>本批实际跑掉的运动 tick 数（暂停 / 高速跑满 / 故障时会小于请求数）。</summary>
        public int ExecutedTicks;

        /// <summary>本批在 worker 上跑 tick 循环的实测耗时（毫秒）；没跑 tick 时为 0。</summary>
        public double SimMs;

        /// <summary>本批是否发布了一帧。</summary>
        public bool Published;

        /// <summary>本批处理后最新已发布帧的序号（0 = 还没发布过）。</summary>
        public long FrameSequence;

        /// <summary>本批处理后最新已发布帧的世界世代号。</summary>
        public int Generation;

        /// <summary>本批应用的命令条数。</summary>
        public int CommandsApplied;

        /// <summary>本批结束时世界是否处于暂停。</summary>
        public bool Paused;
    }

    /// <summary>
    /// **常驻模拟 worker**：一条私有 <see cref="Thread"/>，串行拥有 <see cref="ParticleWorld"/> / <see cref="CreatureWorld"/> /
    /// <see cref="HighSpeedSession"/> / 帧环，是这些东西**唯一**的写者与读者。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么是单线程</b>：<see cref="ParticleWorld.StepMotion"/> / <see cref="ParticleWorld.StepLogic"/> 的扫描顺序、
    /// 世界 RNG 与 <c>OnLogic</c> / <c>OnMotion</c> 的调用次序都是确定性契约。本类因此**绝不**把同一个 world 的一个 tick
    /// 拆给多个线程：一族 world 恒由一个线程串行推进，结果与在主线程同步推进逐位一致（回归见 <c>SimulationWorkerTests</c>）。</para>
    /// <para><b>两个单向通道（都无锁）</b>：</para>
    /// <list type="bullet">
    /// <item><description><b>请求环</b>（主线程 → worker）：<see cref="RequestSlots"/> 个预分配槽位，主线程用
    /// <see cref="TryAcquireRequestSlot"/> **非阻塞地**取得一个空闲槽，再用 <see cref="RequestBatch"/> 填好并移交；
    /// 满载就放弃本次请求（绝不等 worker）。命令随请求一起交出：<see cref="RequestBatch"/> 在移交前把调用方队列里的
    /// 待发命令整批搬进该槽，因此 worker **在第一个运动 tick 之前**就把它们全部应用。</description></item>
    /// <item><description><b>回执环</b>（worker → 主线程）：同样预分配，worker 每处理完一批写一条回执；
    /// 主线程用 <see cref="TryReadResponse"/> 非阻塞地取。回执里带回实测耗时（回喂倍速控制器）与发布结果。</description></item>
    /// </list>
    /// <para><b>帧发布</b>：worker 在批次末<b>至多发布一帧</b>（命令应用过、或累计够 <see cref="SimulationWorkerSetup.StepsPerPresent"/>
    /// 个运动 tick 时），发布走 <see cref="SimulationFramePublisher.Publish"/> 的 release 语义——主线程因此只可能看到
    /// 完整帧，且允许丢弃旧帧、绝不阻塞等待模拟。世界重建（resize）会换一个新的发布器（新世代号），
    /// 主线程用 <see cref="Frames"/> 取到的引用即最新发布器（volatile 读）。</para>
    /// <para><b>暂停与 resize 的有序性</b>：<c>SetPaused</c> / <c>Resize</c> / <c>PresentNow</c> 都是普通命令，与画笔、光照等
    /// 命令在同一条序列里按 <see cref="SimulationCommand.Sequence"/> 应用；resize 在自己的命令被应用的那一刻重建世界并**立即
    /// 发布**一帧（新世代号 + 新尺寸），因此依赖它的 UI 只会看到新世代的帧。暂停期间照样抽干命令、照样发布
    /// （<c>PresentNow</c> 因此在暂停中也有效），只是不跑运动 tick。</para>
    /// <para><b>故障通道</b>：任一批抛出的异常都被捕获、写进 <see cref="Fault"/>（首个异常胜出）并让 worker 就地停摆
    /// （暂停推进、丢弃其后命令、仍照常回执），主线程在 <c>Update</c> 里读到后走 <c>Debug.LogError</c>。
    /// worker 自己**不碰** Debug.Log 等任何 Unity API。</para>
    /// <para><b>生命周期</b>：构造时（调用线程上）建好世界并发布第一帧——消费方在 <c>Awake</c> 之后立刻能读到完整帧；
    /// 随后 <see cref="Start"/> 起线程，<see cref="Stop"/> 请求停止并 <c>Join</c>。**必须在 Unity 对象销毁前 Join**：
    /// 线程活着时绝不允许销毁驱动 / 帧环（否则 worker 会写进已销毁的对象）。</para>
    /// </remarks>
    public sealed class SimulationWorker : IDisposable
    {
        /// <summary>请求槽位数（主线程最多提前这么多批）。2 = 允许一批在跑、下一批排队，再多就丢请求而不是排队等。</summary>
        public const int RequestSlots = 2;

        /// <summary>回执槽位数（与请求槽位一一对应）。</summary>
        public const int ResponseSlots = 2;

        /// <summary>单批允许的最大运动 tick 数：请求再大也钳到这里（有界请求 ⇒ 有界响应延迟）。</summary>
        public const int MaxBatchTicks = 255;

        /// <summary>单批允许携带的最大命令条数；装不下的命令留在调用方队列，下一批继续送（绝不丢弃）。</summary>
        public const int MaxCommandsPerBatch = 256;

        private readonly SimulationWorkerSetup _setup;

        // ---- 世界诊断消息（worker 生产、主线程记录；世界本身绝不调 Unity 日志）----
        private readonly ConcurrentQueue<string> _worldMessages = new ConcurrentQueue<string>();

        // ---- 请求环（主线程写、worker 读；靠信号量做“槽位在谁手里”的身份约定）----
        // 三个下标**各归一个线程**：发号（主线程）、消费（worker）、发布回执（worker）、收回执（主线程）——
        // 因此环上没有一处被两条线程同时改写，同步全靠两个信号量的移交语义。
        private readonly SemaphoreSlim _pendingRequests = new SemaphoreSlim(0, RequestSlots);
        private readonly SemaphoreSlim _freeRequestSlots = new SemaphoreSlim(RequestSlots, RequestSlots);
        private readonly SimulationTickRequest[] _requestData = new SimulationTickRequest[RequestSlots];
        private readonly SimulationCommand[] _requestCommands = new SimulationCommand[RequestSlots * MaxCommandsPerBatch];
        private readonly int[] _requestCommandCount = new int[RequestSlots];
        private readonly long[] _requestSequencePerSlot = new long[RequestSlots];

        /// <summary>下一个要交出的请求槽（**主线程私有**）。</summary>
        private int _acquireIndex;

        /// <summary>下一个要处理的请求槽（**worker 私有**）。</summary>
        private int _consumeIndex;

        /// <summary>已发出的请求数（**主线程私有**；即请求序号）。</summary>
        private long _requestSequence;

        // ---- 回执环（worker 写、主线程读）----
        private readonly SemaphoreSlim _responses = new SemaphoreSlim(0, ResponseSlots);
        private readonly SemaphoreSlim _freeResponseSlots = new SemaphoreSlim(ResponseSlots, ResponseSlots);
        private readonly SimulationBatchResult[] _responseData = new SimulationBatchResult[ResponseSlots];

        /// <summary>下一个要写入的回执槽（**worker 私有**）。</summary>
        private int _publishIndex;

        /// <summary>下一个要读取的回执槽（**主线程私有**）。</summary>
        private int _readIndex;

        // ---- 跨线程可见的 worker 状态 ----
        private volatile bool _stopRequested;
        private volatile Exception _fault;
        private volatile int _faultCount;
        private volatile SimulationFramePublisher _frames;
        private volatile int _batches;
        private long _lastCompletedRequestSequence;

        private Thread _thread;
        private bool _started;

        // ---- worker 线程私有状态（构造时在调用线程建好，随后只由 worker 线程碰）----
        private ParticleWorld _world;
        private CreatureWorld _creatures;
        private HighSpeedSession _highSpeed;
        private LightSettings _lightSettings;
        private SimulationTickSettings _tickSettings;
        private CellProbe _probe;
        private int _publisherGeneration;
        private int _motionTicks;
        private int _stepsSincePresent;
        private bool _paused;
        private float _latestEffectiveSpeed;
        private long _publishedFrameSequence;
        private readonly Stopwatch _batchStopwatch = new Stopwatch();

        /// <summary>
        /// 建 worker：在**调用线程**上建好世界并发布第一帧（消费方不必等第一次 Update 才有帧可读），
        /// 但不启线程——<see cref="Start"/> 才启。此后世界只由 worker 线程触碰。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="setup"/> 里的配置默认值非法（结构体不会为 null）。</exception>
        public SimulationWorker(in SimulationWorkerSetup setup)
        {
            _setup = setup;
            _tickSettings = setup.TickSettings;
            _lightSettings = setup.Light;
            CreateWorld(setup.Width, setup.Height);
        }

        /// <summary>当前帧环（最新发布器；resize 会整体换新）。消费方只经它读帧，拿不到活世界。</summary>
        public SimulationFramePublisher Frames
        {
            get { return _frames; }
        }

        /// <summary>最新已发布的完整帧；尚未发布（或已停止）时为 null。与 <c>Frames.Current</c> 等价。</summary>
        public SimulationFrame CurrentFrame
        {
            get
            {
                SimulationFramePublisher frames = _frames;
                return frames != null ? frames.Current : null;
            }
        }

        /// <summary>worker 线程是否在跑（<see cref="Start"/> 之后、<see cref="Stop"/> 之前）。</summary>
        public bool Running
        {
            get { return _started; }
        }

        /// <summary>worker 线程的托管线程 id；尚未启动时为 0。供测试断言「世界确实由另一条线程推进」。</summary>
        public int WorkerThreadId
        {
            get
            {
                Thread thread = _thread;
                return thread != null ? thread.ManagedThreadId : 0;
            }
        }

        /// <summary>已处理的批次数（含只抽命令 / 只发布的批次）。</summary>
        public int Batches
        {
            get { return _batches; }
        }

        /// <summary>最近一批处理完的请求序号（诊断 / 测试用）。</summary>
        public long LastCompletedRequestSequence
        {
            get { return Interlocked.Read(ref _lastCompletedRequestSequence); }
        }

        /// <summary>下一条将要发出的请求序号（主线程侧计数）。</summary>
        public long RequestSequence
        {
            get { return _requestSequence; }
        }

        /// <summary>是否已发生过故障（此后 worker 停摆：不跑 tick、不发布、只丢弃命令并回执）。</summary>
        public bool HasFault
        {
            get { return _fault != null; }
        }

        /// <summary>首个故障异常；无故障时为 null。</summary>
        public Exception Fault
        {
            get { return _fault; }
        }

        /// <summary>故障次数（每次批次异常 +1）；主线程据此「只报一次新故障」。</summary>
        public int FaultCount
        {
            get { return _faultCount; }
        }

        /// <summary>当前空闲的请求槽数（诊断 / 测试用；非阻塞读）。</summary>
        public int FreeRequestSlots
        {
            get { return _freeRequestSlots.CurrentCount; }
        }

        /// <summary>尚未被取走的回执数（诊断 / 测试用；非阻塞读）。</summary>
        public int PendingResponses
        {
            get { return _responses.CurrentCount; }
        }

        /// <summary>启线程（幂等）。未启动时交出的请求无人处理，因此驱动在 <c>Awake</c> 里就会调它。</summary>
        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TapTap2026.SimulationWorker",
            };
            _thread.Start();
        }

        /// <summary>
        /// 请求停止并 <c>Join</c>（幂等）：先置停止标志、再唤醒可能正阻塞在请求信号量上的 worker，然后等它退出。
        /// **Join 之前线程一定不会卡住**：worker 唯一的阻塞点是请求信号量，而这里无论它是否在等都会补一次 release。
        /// Join 之后两环一并复位，<see cref="Start"/> 可以再次启动一个新线程（沿用同一个世界）。
        /// </summary>
        public void Stop()
        {
            if (_started)
            {
                _stopRequested = true;

                // 只在信号量为空（worker 很可能正阻塞在等请求上）时补一次 release 把它唤醒。
                // 先判空再 release 保证不会撞上 SemaphoreSlim 的上限（满员时 worker 一定没在阻塞，
                // 它会在循环顶端读到 _stopRequested 自己退出）。
                if (_pendingRequests.CurrentCount == 0)
                {
                    _pendingRequests.Release(1);
                }

                if (_freeResponseSlots.CurrentCount == 0)
                {
                    _freeResponseSlots.Release(1);
                }

                Thread thread = _thread;
                _thread = null;
                _started = false;
                thread.Join();
            }

            ResetChannels();
        }

        /// <summary>停止并 Join（等价于 <see cref="Stop"/>）。</summary>
        public void Dispose()
        {
            Stop();
        }

        // ============================== 主线程侧：请求 ==============================

        /// <summary>
        /// 非阻塞地取一个空闲请求槽；全忙（worker 还在跑前面两批）时返回 false——**调用方应放弃本次请求**，
        /// 而不是等待：主线程绝不为了模拟排队。
        /// </summary>
        /// <param name="slot">取到的槽下标（交给 <see cref="RequestBatch"/>）。</param>
        public bool TryAcquireRequestSlot(out int slot)
        {
            if (!_freeRequestSlots.Wait(0))
            {
                slot = -1;
                return false;
            }

            slot = _acquireIndex;
            _acquireIndex = _acquireIndex + 1 == RequestSlots ? 0 : _acquireIndex + 1;
            return true;
        }

        /// <summary>
        /// 移交一批请求：把 <paramref name="commands"/> 里待发的命令（至多 <see cref="MaxCommandsPerBatch"/> 条）统统搬进该槽，
        /// 再把它连同「本批跑几个运动 tick」一起交给 worker。
        /// </summary>
        /// <param name="slot"><see cref="TryAcquireRequestSlot"/> 取到的槽。</param>
        /// <param name="ticks">主线程计划好的运动 tick 数（钳到 <c>[0, MaxBatchTicks]</c>）。</param>
        /// <param name="effectiveSpeed">主线程实测吞吐倍率（只进帧里的显示读数）。</param>
        /// <param name="commands">待发命令队列（可为 null）；未被搬走的命令留在队列里，下一批继续送。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> 不在 <c>[0, RequestSlots)</c> 内。</exception>
        public void RequestBatch(int slot, int ticks, float effectiveSpeed, SimulationCommandQueue commands)
        {
            if ((uint)slot >= (uint)RequestSlots)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            int count = 0;
            if (commands != null)
            {
                int offset = slot * MaxCommandsPerBatch;
                while (count < MaxCommandsPerBatch && commands.TryDequeue(out SimulationCommand command))
                {
                    _requestCommands[offset + count] = command;
                    count++;
                }
            }

            _requestCommandCount[slot] = count;
            _requestData[slot] = new SimulationTickRequest
            {
                Ticks = ticks < 0 ? 0 : (ticks > MaxBatchTicks ? MaxBatchTicks : ticks),
                EffectiveSpeed = effectiveSpeed,
            };
            _requestSequencePerSlot[slot] = ++_requestSequence;

            _pendingRequests.Release(1);
        }

        // ============================== 主线程侧：回执 ==============================

        /// <summary>非阻塞地取一条回执（没有就返回 false）。</summary>
        public bool TryReadResponse(out SimulationBatchResult result)
        {
            if (!_responses.Wait(0))
            {
                result = default(SimulationBatchResult);
                return false;
            }

            return ReadResponse(out result);
        }

        /// <summary>
        /// 阻塞等待一条回执（至多 <paramref name="timeoutMs"/> 毫秒）。
        /// **只给测试 / 确定性重放用**——生产驱动一律走非阻塞的 <see cref="TryReadResponse"/>。
        /// </summary>
        public bool TryWaitResponse(int timeoutMs, out SimulationBatchResult result)
        {
            if (!_responses.Wait(timeoutMs))
            {
                result = default(SimulationBatchResult);
                return false;
            }

            return ReadResponse(out result);
        }

        /// <summary>
        /// 在当前线程上把「一批请求」跑完并返回回执：**只给测试 / 确定性重放用**，绕开线程，
        /// 语义与 worker 线程完全相同（同一段 <c>Service</c>）。生产路径永远不会调它。
        /// </summary>
        /// <remarks>暂停、故障、发布规则都照常生效；调用方仍需先 <see cref="TryAcquireRequestSlot"/> / <see cref="RequestBatch"/>，
        /// 本方法只是替代 worker 线程来消费那个槽。</remarks>
        public SimulationBatchResult RunBatchInline()
        {
            if (!_pendingRequests.Wait(0))
            {
                throw new InvalidOperationException("没有待处理的请求：先 TryAcquireRequestSlot + RequestBatch。");
            }

            int index = _consumeIndex;
            _consumeIndex = _consumeIndex + 1 == RequestSlots ? 0 : _consumeIndex + 1;

            SimulationTickRequest request = _requestData[index];
            int commandCount = _requestCommandCount[index];

            SimulationBatchResult result = Service(index, request, commandCount);
            _freeRequestSlots.Release(1);
            PublishResponse(result);
            return result;
        }

        /// <summary>写入一条回执（**worker 私有下标**）并交给等待的主线程；写入顺序 = 请求顺序。</summary>
        private void PublishResponse(in SimulationBatchResult result)
        {
            _freeResponseSlots.Wait();
            _responseData[_publishIndex] = result;
            _publishIndex = _publishIndex + 1 == ResponseSlots ? 0 : _publishIndex + 1;
            _responses.Release(1);
        }

        /// <summary>取走回执环里的下一条（调用方已确认信号量在手）；读取顺序与写入顺序一一对应。</summary>
        private bool ReadResponse(out SimulationBatchResult result)
        {
            result = _responseData[_readIndex];
            _readIndex = _readIndex + 1 == ResponseSlots ? 0 : _readIndex + 1;
            _freeResponseSlots.Release(1);
            return true;
        }

        // ============================== worker 线程 ==============================

        private void Run()
        {
            while (!_stopRequested)
            {
                _pendingRequests.Wait();
                if (_stopRequested)
                {
                    break;
                }

                int index = _consumeIndex;
                _consumeIndex = _consumeIndex + 1 == RequestSlots ? 0 : _consumeIndex + 1;

                // 先把请求内容抓成局部量：槽位一旦回执就会被主线程重新取走，此后不再读它。
                SimulationTickRequest request = _requestData[index];
                int commandCount = _requestCommandCount[index];

                SimulationBatchResult result = Service(index, request, commandCount);

                // 先还槽位，再回执：主线程可以立刻再排一批，而回执送到的顺序与请求顺序一一对应。
                _freeRequestSlots.Release(1);
                PublishResponse(result);
            }
        }

        /// <summary>处理一批：抽命令 → 按请求跑 tick → 至多发布一帧 → 写回执。异常在此转成故障，绝不逃出线程。</summary>
        private SimulationBatchResult Service(int slot, in SimulationTickRequest request, int commandCount)
        {
            SimulationBatchResult result = new SimulationBatchResult
            {
                RequestSequence = _requestSequencePerSlot[slot],
                RequestedTicks = request.Ticks,
                Paused = _paused,
            };

            try
            {
                int commandsApplied = DrainCommands(slot, commandCount);
                result.CommandsApplied = commandsApplied;

                double simMs = 0.0;
                bool cadenceReached = false;
                _latestEffectiveSpeed = request.EffectiveSpeed;

                if (_fault == null && !_paused && request.Ticks > 0)
                {
                    RunTicks(request.Ticks, out int executed, out cadenceReached, out simMs);
                    result.ExecutedTicks = executed;
                }

                if (_fault == null && (commandsApplied > 0 || cadenceReached))
                {
                    PublishFrame();
                    result.Published = true;
                }

                result.SimMs = simMs;
                result.Paused = _paused;
            }
            catch (Exception error)
            {
                PublishFault(error);
                result.Published = false;
                result.SimMs = 0.0;
            }

            SimulationFramePublisher frames = _frames;
            result.FrameSequence = _publishedFrameSequence;
            result.Generation = frames != null ? frames.Generation : 0;
            _lastCompletedRequestSequence = result.RequestSequence;
            _batches++;
            return result;
        }

        /// <summary>按序应用本批全部命令（**在第一个运动 tick 之前**），返回实际应用条数。</summary>
        private int DrainCommands(int slot, int commandCount)
        {
            // 故障后停摆：命令一律丢弃（世界不再被写），但批次照常回执。
            if (commandCount <= 0 || _fault != null)
            {
                return 0;
            }

            int offset = slot * MaxCommandsPerBatch;
            int applied = 0;
            for (int i = 0; i < commandCount; i++)
            {
                Apply(_requestCommands[offset + i]);
                applied++;

                if (_fault != null)
                {
                    break;
                }
            }

            return applied;
        }

        /// <summary>跑本批的运动 tick（倍速只改次数，tick 内部逐位不变），并回喂实测耗时。</summary>
        private void RunTicks(int ticks, out int executed, out bool cadenceReached, out double simMs)
        {
            int logicEvery = _setup.LogicTicksPerMotionTick < 1 ? 1 : _setup.LogicTicksPerMotionTick;

            _batchStopwatch.Restart();
            executed = 0;
            for (int i = 0; i < ticks; i++)
            {
                bool highSpeedCompleted = false;

                // 逻辑帧是运动帧节拍上的一个子节拍：每 logicEvery 个运动帧跑一次「逻辑 + 运动」，其余只跑运动。
                _motionTicks++;
                if (_motionTicks >= logicEvery)
                {
                    _motionTicks = 0;
                    _world.StepLogic();

                    // 动物层与世界同节拍推进（物理对世界只读），位置与既有顺序一致：逻辑之后、高速转发之前。
                    _creatures.Step(_world);

                    HighSpeedSession session = _highSpeed;
                    if (session != null && session.Active)
                    {
                        if (!session.Completed)
                        {
                            session.OnLogicTick();
                        }

                        if (session.Completed)
                        {
                            _paused = true;
                            highSpeedCompleted = true;
                        }
                    }
                }
                else
                {
                    _world.StepMotion();
                }

                executed++;
                _stepsSincePresent++;

                if (highSpeedCompleted)
                {
                    break;
                }
            }

            _batchStopwatch.Stop();
            simMs = _batchStopwatch.Elapsed.TotalMilliseconds;
            cadenceReached = _stepsSincePresent >= (_setup.StepsPerPresent < 1 ? 1 : _setup.StepsPerPresent);
        }

        /// <summary>把当前活世界映射进帧槽并发布（像素 / 世界面向 / 实体 / 音频 / 高速五段一次写完才可见）。</summary>
        private void PublishFrame()
        {
            _stepsSincePresent = 0;

            SimulationFramePublisher frames = _frames;
            if (frames == null || _world == null)
            {
                return;
            }

            SimulationFrame frame = frames.BeginWrite();
            SimulationFrameCollector.CollectRender(_world, _lightSettings, frame.Render);
            SimulationFrameCollector.CollectWorld(_world, _paused, _probe, frame.World);
            SimulationFrameCollector.CollectCreatures(_creatures, _setup.LogicRate, frame.Creatures);
            SimulationFrameCollector.CollectAudio(_world, frame.Audio);
            SimulationFrameCollector.CollectHighSpeed(_highSpeed, _latestEffectiveSpeed, frame.HighSpeed);
            frames.Publish();
            _publishedFrameSequence = frame.Sequence;
        }

        /// <summary>记下首个故障并让 worker 就地停摆（暂停推进、丢弃其后命令）；主线程负责报错。</summary>
        private void PublishFault(Exception error)
        {
            if (_fault == null)
            {
                _fault = error;
            }

            _faultCount++;
            _paused = true;
        }

        /// <summary>
        /// 取一条世界的诊断消息（主线程在 <c>Update</c> 里抽干并记日志）；没有就返回 false。
        /// **世界从不自己调 Unity 日志**：它把消息交给 worker，worker 只入队，记录由主线程做。
        /// </summary>
        public bool TryReadWorldMessage(out string message)
        {
            return _worldMessages.TryDequeue(out message);
        }

        /// <summary>世界的诊断出口（跑在 worker 线程上）：只入队，绝不触碰任何 Unity API。</summary>
        private void OnWorldMessage(string message)
        {
            _worldMessages.Enqueue(message);
        }

        private void ResetChannels()
        {
            while (_pendingRequests.Wait(0))
            {
            }

            while (_freeRequestSlots.CurrentCount < RequestSlots)
            {
                _freeRequestSlots.Release(1);
            }

            while (_responses.Wait(0))
            {
            }

            while (_freeResponseSlots.CurrentCount < ResponseSlots)
            {
                _freeResponseSlots.Release(1);
            }

            _acquireIndex = 0;
            _consumeIndex = 0;
            _publishIndex = 0;
            _readIndex = 0;
            _stopRequested = false;
        }

        // ============================== 命令应用（worker 私有；阶段 1 里在主线程）==============================

        private void Apply(in SimulationCommand command)
        {
            switch (command.Kind)
            {
                case SimulationCommandKind.PaintDisk:
                    ApplyPaint(command);
                    return;

                case SimulationCommandKind.EraseDisk:
                    ApplyErase(command);
                    return;

                case SimulationCommandKind.SpawnCreature:
                    ApplySpawnCreature(command);
                    return;

                case SimulationCommandKind.SetPaused:
                    _paused = command.Flag;
                    return;

                case SimulationCommandKind.Resize:
                    CreateWorld(command.Width, command.Height);
                    return;

                case SimulationCommandKind.ClearAll:
                    ClearWorld();
                    return;

                case SimulationCommandKind.FillRect:
                    FillRect(command);
                    return;

                case SimulationCommandKind.SetLightSettings:
                    _lightSettings = command.Light;
                    ApplyLightSettings();
                    return;

                case SimulationCommandKind.SetSimulationSettings:
                    _tickSettings = command.Settings;
                    ApplyTickSettings();
                    return;

                case SimulationCommandKind.ProbeCell:
                    // 在命令边界当场采一份：此后的 tick 会改动网格，发布时再读就不是这条命令看到的世界了。
                    _probe = _world != null
                        ? SimulationFrameCollector.ReadProbe(_world, command.X, command.Y)
                        : default(CellProbe);
                    return;

                case SimulationCommandKind.EnterHighSpeed:
                    ApplyEnterHighSpeed();
                    return;

                case SimulationCommandKind.ExitHighSpeed:
                    if (_highSpeed != null)
                    {
                        _highSpeed.Exit();
                    }

                    return;

                case SimulationCommandKind.PresentNow:
                    // 上屏由批次末的发布决策统一处理（本批应用过命令 ⇒ 一定发布），这里无事可做。
                    return;
            }
        }

        /// <summary>
        /// 投放当前选中项：<paramref name="command"/>.Flag 为真走「单颗投放」（只写中心格 + 盖树木物种），
        /// 否则填满以中心为圆心的实心圆。火另有投放资格判定（<see cref="FireParticle.CanPlaceAt"/>）。
        /// </summary>
        private void ApplyPaint(in SimulationCommand command)
        {
            if (_world == null)
            {
                return;
            }

            int cx = command.X;
            int cy = command.Y;
            ushort typeId = command.TypeId;

            if (command.Flag)
            {
                if (!_world.Set(cx, cy, typeId))
                {
                    return;
                }

                if (typeId == ParticleIds.TreeSeed)
                {
                    TreeSpecies.Stamp(_world, cx, cy, command.Index, TreeSpecies.SeedColorsPerSpecies);
                }

                return;
            }

            int radius = command.Radius < 0 ? 0 : command.Radius;
            int radiusSquared = radius * radius;

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radiusSquared)
                    {
                        continue;
                    }

                    if (typeId == ParticleIds.Fire && !FireParticle.CanPlaceAt(_world, cx + dx, cy + dy))
                    {
                        continue;
                    }

                    _world.Set(cx + dx, cy + dy, typeId);
                }
            }
        }

        /// <summary>擦除：把以中心为圆心的实心圆清成空气（本来就没东西的格不重复清）。</summary>
        private void ApplyErase(in SimulationCommand command)
        {
            if (_world == null)
            {
                return;
            }

            int cx = command.X;
            int cy = command.Y;
            int radius = command.Radius < 0 ? 0 : command.Radius;
            int radiusSquared = radius * radius;

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radiusSquared)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    int y = cy + dy;

                    if (_world.IsInBounds(x, y) && !_world.IsAir(x, y))
                    {
                        _world.Clear(x, y);
                    }
                }
            }
        }

        /// <summary>生成实体：脚底中心落在格中心 <c>(cx + 0.5, cy)</c>；越界由 <see cref="CreatureWorld.Spawn"/> 夹回世界内。</summary>
        private void ApplySpawnCreature(in SimulationCommand command)
        {
            if (_world == null || _creatures == null)
            {
                return;
            }

            Creature creature = CreatureSpawns.Create(command.Index);
            if (creature == null)
            {
                return;
            }

            _creatures.Spawn(creature, _world, command.X + 0.5f, command.Y);
        }

        /// <summary>把整个网格清成空气（不改变尺寸）。</summary>
        private void ClearWorld()
        {
            if (_world == null)
            {
                return;
            }

            for (int y = 0; y < _world.Height; y++)
            {
                for (int x = 0; x < _world.Width; x++)
                {
                    _world.Clear(x, y);
                }
            }
        }

        /// <summary>把矩形区域填成一种粒子（越界部分由 <see cref="ParticleWorld.Set"/> 跳过）。</summary>
        private void FillRect(in SimulationCommand command)
        {
            if (_world == null)
            {
                return;
            }

            int minX = command.X < 0 ? 0 : command.X;
            int minY = command.Y < 0 ? 0 : command.Y;
            int maxX = Min(_world.Width, command.X + command.Width);
            int maxY = Min(_world.Height, command.Y + command.Height);

            for (int y = minY; y < maxY; y++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    _world.Set(x, y, command.TypeId);
                }
            }
        }

        /// <summary>
        /// 进入高速模式：懒建会话并解除暂停（否则 tick 循环被暂停挡住，会话永远停在 0 / 时长，而模式又锁住编辑与退出热键，
        /// 形成死锁）。倍速钉满档由**主线程**按帧里的 <see cref="HighSpeedFrame.Active"/> 完成——worker 不持有倍速控制器。
        /// </summary>
        private void ApplyEnterHighSpeed()
        {
            if (_world == null)
            {
                return;
            }

            if (_highSpeed == null)
            {
                _highSpeed = new HighSpeedSession(_world, _setup.HighSpeedLogicFrames);
            }

            _highSpeed.Enter();
            if (_highSpeed.Active)
            {
                _paused = false;
            }
        }

        /// <summary>
        /// 按给定尺寸重建一个**空**世界并立即发布一帧（新世代号 + 新尺寸 ⇒ 依赖它的 UI 只会看到新世代的帧）。
        /// **已有内容不保留**；高速会话、探针请求与帧环一并换代。空格填成大气粒子——风与对流需要空气本身是会动、
        /// 携带热量的物质，typeId 0 此后只作为「被抹除留下的真空」短暂出现。
        /// </summary>
        private void CreateWorld(int width, int height)
        {
            width = Clamp(width);
            height = Clamp(height);

            // 世界换了，旧会话持有的是旧世界——退出并丢弃，下次进入高速时按新世界重建。
            if (_highSpeed != null)
            {
                if (_highSpeed.Active)
                {
                    _highSpeed.Exit();
                }

                _highSpeed = null;
            }

            _world = new ParticleWorld(width, height, _setup.Seed);

            // 世界的诊断消息改由本 worker 收下、留待主线程记录：world 在 worker 线程上运行，
            // 而 Unity 日志只能在主线程调（见 ParticleWorld.MessageSink）。
            _world.MessageSink = OnWorldMessage;

            _stepsSincePresent = 0;
            _motionTicks = 0;
            _probe = default(CellProbe);

            if (ParticleIds.Atmosphere != ParticleRegistry.AirTypeId)
            {
                for (int y = 0; y < _world.Height; y++)
                {
                    for (int x = 0; x < _world.Width; x++)
                    {
                        _world.Set(x, y, ParticleIds.Atmosphere);
                    }
                }
            }

            // 动物世界与世界同步重建。种子与世界随机流**分离**（异或一个黄金比例常数派生）：
            // 动物层不消耗世界的随机流，两者的确定性互不污染。
            _creatures = new CreatureWorld(_setup.Seed ^ 0x9E3779B9u);

            ApplyTickSettings();
            ApplyLightSettings();

            // 帧环随世界重建：像素数组绝不跨尺寸复用；世代号 +1 让消费方丢掉上一世代的视图。
            _publisherGeneration++;
            _frames = new SimulationFramePublisher(width, height, _publisherGeneration);

            // 重建本身就是「世界变了」：立刻发布一帧，resize 的依赖方（相机 / UI / 世代对账）不必等下一个 tick。
            PublishFrame();
        }

        /// <summary>把光照配置写回世界的光照控制器。</summary>
        private void ApplyLightSettings()
        {
            if (_world == null)
            {
                return;
            }

            LightController light = _world.Light;
            light.Enabled = _lightSettings.Enabled;
            light.LitTicks = _lightSettings.LitTicks;
            light.DarkTicks = _lightSettings.DarkTicks;
            light.Intensity = _lightSettings.Intensity;
            light.CloudExtinction = _lightSettings.CloudExtinction;
        }

        /// <summary>把 tick 配置写回世界（热力学参数 / 随机刻 / 热力调试叠加）。</summary>
        private void ApplyTickSettings()
        {
            if (_world == null)
            {
                return;
            }

            HeatMap heat = _world.Heat;
            heat.Conductivity = _tickSettings.Conductivity;
            heat.VoidHeatLoss = _tickSettings.VoidHeatLoss;
            heat.RadiativeLoss = _tickSettings.RadiativeLoss;
            heat.AdiabaticLapse = _tickSettings.AdiabaticLapse;
            heat.AirThermalConductivity = _tickSettings.AirThermalConductivity;
            heat.AirThermalEmissivity = _tickSettings.AirThermalEmissivity;
            heat.DebugHeatOverlay = _tickSettings.DebugHeatOverlay;
            heat.DebugFullScaleTemperature = _tickSettings.DebugFullScaleTemperature;
            _world.RandomTicksPerStep = _tickSettings.RandomTicksPerStep;
        }

        private static int Clamp(int side)
        {
            if (side < 1)
            {
                return 1;
            }

            return side > ParticleWorld.MaxSide ? ParticleWorld.MaxSide : side;
        }

        private static int Min(int a, int b)
        {
            return a < b ? a : b;
        }
    }
}
