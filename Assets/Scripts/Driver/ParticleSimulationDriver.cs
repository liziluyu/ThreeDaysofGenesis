using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 主线程侧的模拟驱动（阶段 3）：**只采样、只计划、只消费帧**——世界与 tick 循环都在
    /// <see cref="SimulationWorker"/> 的那条常驻线程上。
    /// </summary>
    /// <remarks>
    /// <para><b>主线程只做四件事</b>：①采 Unity 侧配置（Inspector 字段 / 设置资产 / <c>Time.deltaTime</c> /
    /// <c>Application.targetFrameRate</c>）并变化时入队命令；②按上一批的实测耗时与预算计划「本帧跑几个运动 tick」
    /// （<see cref="SpeedController"/> 全在主线程，worker 侧没有第二套累加器）；③非阻塞地排一批请求
    /// （槽位满就放弃本次，**绝不等待模拟**）；④消费最新已发布帧并在有新帧时调
    /// <see cref="ParticleRenderer.Present(RenderFrame)"/> 上传纹理。</para>
    /// <para><b>对外只有两个口</b>：入口 <see cref="Submit"/>（有序值类型命令）与出口 <see cref="CurrentFrame"/>
    /// （不可变 <see cref="SimulationFrame"/>）。本类**不持有** <see cref="ParticleWorld"/> / <see cref="CreatureWorld"/> /
    /// <see cref="HighSpeedSession"/> 的任何引用，也没有任何可变兼容别名。</para>
    /// <para><b>节拍</b>：运动帧 60 Hz（只跑粒子运动）、逻辑帧 20 Hz（相变 / 反应 / 热传导 / 湿度 / 降水 / 光照 / 随机刻），
    /// 逻辑帧是运动帧节拍上的子节拍（每 <see cref="ParticleSimulationSettings.LogicTicksPerMotionTick"/> 个运动帧一次）；
    /// 倍速只改「本帧计划几个运动 tick」，tick 内部逐位不变，超预算的部分直接丢弃、绝不结转。</para>
    /// <para><b>命令边界</b>：命令随请求一起交给 worker，在**下一个运动 tick 开始前**按
    /// <see cref="SimulationCommand.Sequence"/> 顺序全部应用；应用过命令的批次一定发布一帧，
    /// 因此每一帧都对应一个完整的世界状态，不存在「半个 tick 的世界」。</para>
    /// <para><b>生命周期</b>：<see cref="Awake"/> 建 worker（世界与第一帧同步建好，消费方随后立刻可读）并启线程；
    /// <see cref="OnEnable"/> 兜底重启；<see cref="OnDisable"/> / <see cref="OnDestroy"/> 停止并 <c>Join</c>
    /// （绝不让线程活过 Unity 对象销毁）。worker 故障由 <see cref="Update"/> 在主线程读出来并
    /// <c>Debug.LogError</c>——worker 自己从不碰任何 Unity API。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSimulationDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationSettings _settings;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("是否推进模拟。关掉即暂停：运动帧与逻辑帧一起冻结，仍可提交 PresentNow 命令手动上屏。" +
                 "运行时由对话 / UI 提交的 SetPaused 命令改的是 worker 里的权威状态，不会与本字段互相覆盖。")]
        [SerializeField] private bool _simulate = true;

        [Header("沙箱尺寸（格，0 = 用 Settings 资产的值）")]
        [Tooltip("网格宽度。运行时在 Inspector 改这里（或改 Settings 资产）会在下一个命令边界重建一个空世界。")]
        [SerializeField, Min(0)] private int _widthOverride;

        [Tooltip("网格高度。运行时在 Inspector 改这里（或改 Settings 资产）会在下一个命令边界重建一个空世界。")]
        [SerializeField, Min(0)] private int _heightOverride;

        [Header("调试")]
        [Tooltip("热力叠加：把每格温度按「越热越红」叠在上屏像素上（空气格也显示）。只影响显示，不影响模拟。")]
        [SerializeField] private bool _heatDebugOverlay;

        [Tooltip("热力叠加的满量程温度：温度达到它即为纯红。<= 0 时不叠色。")]
        [SerializeField] private float _heatDebugFullScale = HeatMap.DefaultDebugFullScaleTemperature;

        private ParticleSimulationSettings _fallbackSettings;

        /// <summary>常驻模拟 worker（世界 / tick / 帧环的唯一持有者）。</summary>
        private SimulationWorker _worker;

        /// <summary>主线程独占的倍速控制器：计划「本帧跑几个运动 tick」，并把上一批的实测耗时回喂成每 tick 成本。</summary>
        private SpeedController _speed;

        /// <summary>待发命令（主线程入队；随请求整批交给 worker，抽不完的留在这里等下一批）。</summary>
        private readonly SimulationCommandQueue _commands = new SimulationCommandQueue();

        /// <summary>主线程是否已启动 worker（用于区分「worker 未跑」与「worker 正在跑」）。</summary>
        private bool _workerRunning;

        /// <summary>当前光照配置（世界重建后由 worker 用最近的命令值重新应用）。</summary>
        private LightSettings _lightSettings = LightSettings.Default;

        /// <summary>当前 tick 配置（同上）。</summary>
        private SimulationTickSettings _tickSettings;

        /// <summary>上一次采样到的 <see cref="_simulate"/>：只有真的变了才入队 SetPaused（不与其它暂停方打架）。</summary>
        private bool _sampledSimulate;

        /// <summary>上一批对应的请求序号；回执序号与之相同才把实测耗时当作有效样本回喂（否则样本与计划数不配对）。</summary>
        private long _sampledRequestSequence;

        /// <summary>上一批的实测耗时（毫秒）；无有效样本时为 0。</summary>
        private double _lastBatchSimMs;

        /// <summary>是否已有一个在途的 Resize（避免异步窗口里每帧重发、反复重建世界）。</summary>
        private bool _resizeInFlight;

        /// <summary>在途 Resize 的目标尺寸。</summary>
        private int _resizeInFlightWidth;

        /// <summary>在途 Resize 的目标尺寸。</summary>
        private int _resizeInFlightHeight;

        /// <summary>最近一次已上屏的帧序号（0 = 还没上屏过）。</summary>
        private long _presentedSequence;

        /// <summary>最近一次已上屏的世代号。换世代必须重新上屏（尺寸 / 内容都换了）。</summary>
        private int _presentedGeneration = -1;

        /// <summary>最近一次已读到的帧引用（与 <see cref="_presentedSequence"/> 一起判「这一帧是不是新的」）。</summary>
        private SimulationFrame _presentedFrame;

        /// <summary>已报过的故障次数（每个新故障只报一次）。</summary>
        private int _reportedFaults;

        /// <summary>高速模式的倍速钉档状态：帧里的 <see cref="HighSpeedFrame.Active"/> 变化时钉满档 / 钉回 1×。</summary>
        private bool _highSpeedPinned;

        /// <summary>最新已发布的完整帧；<see cref="Awake"/> 之后恒非 null。**UI / 渲染 / 音频只读它。**</summary>
        public SimulationFrame CurrentFrame
        {
            get
            {
                SimulationWorker worker = _worker;
                return worker != null ? worker.CurrentFrame : null;
            }
        }

        /// <summary>逻辑帧速率（Hz，来自设置资产）。光照面板按它把「秒」换算成刻数。</summary>
        public float LogicRate
        {
            get { return Settings.LogicRate; }
        }

        /// <summary>worker 是否在跑（诊断 / 测试用）。</summary>
        public bool WorkerRunning
        {
            get { return _workerRunning; }
        }

        /// <summary>worker 最近一批的实测耗时（毫秒，回喂倍速控制器的那个样本）；还没有有效样本时为 0。</summary>
        public double LastBatchSimMs
        {
            get { return _lastBatchSimMs; }
        }

        /// <summary>
        /// 提交一条命令：立即定序，随下一批请求交给 worker，在**下一个运动 tick 开始前**按序应用（见类注释）。
        /// 命令是值类型，因此调用方不需要、也拿不到任何活世界引用。
        /// </summary>
        public void Submit(SimulationCommand command)
        {
            _commands.Enqueue(command);
        }

        /// <summary>设置后续高速模式的时长（逻辑帧）；≤ 0 = 回到设置资产默认值。只影响下一次进入高速模式时创建的会话。</summary>
        public void SetHighSpeedLogicFrames(int logicFrames)
        {
            Submit(SimulationCommand.SetHighSpeedLogicFrames(logicFrames));
        }

        /// <summary>本帧生效的网格宽度：覆盖值 &gt; 0 时用它，否则用 <see cref="ParticleSimulationSettings.Width"/>。</summary>
        private int RequestedWidth
        {
            get
            {
                return _widthOverride > 0
                    ? Mathf.Clamp(_widthOverride, 1, ParticleWorld.MaxSide)
                    : Settings.Width;
            }
        }

        /// <summary>本帧生效的网格高度：覆盖值 &gt; 0 时用它，否则用 <see cref="ParticleSimulationSettings.Height"/>。</summary>
        private int RequestedHeight
        {
            get
            {
                return _heightOverride > 0
                    ? Mathf.Clamp(_heightOverride, 1, ParticleWorld.MaxSide)
                    : Settings.Height;
            }
        }

        private ParticleSimulationSettings Settings
        {
            get
            {
                if (_settings != null)
                {
                    return _settings;
                }

                if (_fallbackSettings == null)
                {
                    _fallbackSettings = ScriptableObject.CreateInstance<ParticleSimulationSettings>();
                    _fallbackSettings.name = "ParticleSimulationSettings (code defaults)";
                    Debug.LogWarning(
                        "[ParticleSimulationDriver] 未指定 ParticleSimulationSettings，使用代码内置默认值。");
                }

                return _fallbackSettings;
            }
        }

        private void Awake()
        {
            ParticleRegistration.RegisterAll();

            ParticleSimulationSettings settings = Settings;
            _speed = new SpeedController(SpeedController.MinSpeed, settings.SpeedMax);

            // 先采一次 Unity 侧配置：建世界时就按生产参数应用（与「建出来立刻带资产参数」的既有行为一致）。
            _tickSettings = ReadTickSettings();
            _sampledSimulate = _simulate;

            // 建 worker：世界与第一帧在**当前线程**同步建好（消费方不必等第一次 Update 才有帧可读），
            // 随后世界只归 worker 线程；这里立刻启线程，Update 因此永远有一个活的 worker。
            _worker = new SimulationWorker(new SimulationWorkerSetup(
                RequestedWidth,
                RequestedHeight,
                settings.Seed,
                settings.LogicTicksPerMotionTick,
                settings.LogicRate,
                settings.StepsPerPresent,
                settings.HighSpeedLogicFrames,
                _tickSettings,
                _lightSettings));
            StartWorker();

            // 免场景接线的过渡手段：动物渲染器必须与驱动在同一个 GameObject 上，当前场景还没手工挂它，
            // 因此这里补挂一个。今后若在场景里手工配置（或换成别的渲染实现），这段自动补挂可直接删掉。
            if (GetComponent<CreatureRenderer>() == null)
            {
                gameObject.AddComponent<CreatureRenderer>();
            }
        }

        private void OnEnable()
        {
            // Awake 已启线程；这条兜底是为「运行时按 Inspector 勾掉再勾上」准备的（OnDisable 会停线程）。
            if (_worker != null)
            {
                StartWorker();
            }
        }

        private void OnDisable()
        {
            // 停止并 Join：unity 对象进入销毁流程 / 组件被禁用后，绝不允许 worker 再写世界。
            StopWorker();
        }

        private void OnDestroy()
        {
            StopWorker();

            // 代码兜底创建的 ScriptableObject 不受场景管理，必须自己销毁，否则每次进播放都泄漏一个实例。
            if (_fallbackSettings != null)
            {
                Destroy(_fallbackSettings);
                _fallbackSettings = null;
            }
        }

        private void StartWorker()
        {
            SimulationWorker worker = _worker;
            if (worker == null || _workerRunning)
            {
                return;
            }

            worker.Start();
            _workerRunning = true;
        }

        private void StopWorker()
        {
            SimulationWorker worker = _worker;
            if (worker == null || !_workerRunning)
            {
                return;
            }

            _workerRunning = false;
            worker.Stop();
        }

        /// <summary>
        /// 一个驱动帧：①采样 Unity 侧配置并变化时入队；②非阻塞排一批请求（把命令与计划 tick 数交给 worker，
        /// **本帧不同步推进世界**）；③非阻塞收回执（回喂耗时才、钉高速倍速）；④消费新帧并上屏。
        /// </summary>
        /// <remarks>
        /// <see cref="Update"/> 用 <see cref="Time.deltaTime"/> 调它；公开（含显式 <paramref name="deltaTime"/>）是为了让测试
        /// 能脱离帧循环确定性地驱动命令 / 帧边界——与四个声音驱动器的 <c>Pump</c> 同一口径。
        /// 注意本方法**从不等待 worker**：worker 忙时这一帧就只是不发请求，帧仍旧可以取用上一批的成果。
        /// </remarks>
        public void Advance(float deltaTime)
        {
            SimulationWorker worker = _worker;
            if (worker == null || !_workerRunning)
            {
                return;
            }

            SampleUnityConfiguration();
            SurfaceWorkerFault(worker);
            DrainWorldMessages(worker);
            DrainResponses(worker);
            RequestWorkerBatch(worker, deltaTime);
            ConsumeFrame(worker);
        }

        private void Update()
        {
            Advance(Time.deltaTime);
        }

        /// <summary>
        /// 采样主线程侧配置：Inspector 的尺寸覆盖变化 ⇒ 入队 <see cref="SimulationCommandKind.Resize"/>;
        /// Inspector 的推进开关变化 ⇒ 入队 <see cref="SimulationCommandKind.SetPaused"/>；
        /// 热力学 / 随机刻 / 调试叠加变化 ⇒ 入队 <see cref="SimulationCommandKind.SetSimulationSettings"/>。
        /// 三者都只在**真的变了**时入队，因此队列不会被每帧的无意义写入淹没。
        /// </summary>
        private void SampleUnityConfiguration()
        {
            SimulationFrame frame = CurrentFrame;
            int width = RequestedWidth;
            int height = RequestedHeight;

            // 观测到的尺寸已经是目标 ⇒ 在途的 resize 落地了；否则最多补一次（worker 异步推进，
            // 不补的话新尺寸要等下一帧采样才生效，补多次的话会反复重建世界、把刚播种的内容冲掉）。
            if (frame != null && frame.World.Width == width && frame.World.Height == height)
            {
                _resizeInFlight = false;
            }
            else if (!_resizeInFlight || _resizeInFlightWidth != width || _resizeInFlightHeight != height)
            {
                _commands.Enqueue(SimulationCommand.Resize(width, height));
                _resizeInFlight = true;
                _resizeInFlightWidth = width;
                _resizeInFlightHeight = height;
            }

            if (_simulate != _sampledSimulate)
            {
                _sampledSimulate = _simulate;
                _commands.Enqueue(SimulationCommand.SetPaused(!_simulate));
            }

            SimulationTickSettings tick = ReadTickSettings();
            if (!tick.Equals(_tickSettings))
            {
                _tickSettings = tick;
                _commands.Enqueue(SimulationCommand.SetSimulationSettings(tick));
            }
        }

        /// <summary>从设置资产与 Inspector 字段采一份 tick 配置快照（唯一的 Unity 侧配置面）。</summary>
        private SimulationTickSettings ReadTickSettings()
        {
            ParticleSimulationSettings settings = Settings;
            return new SimulationTickSettings(
                settings.RandomTicksPerStep,
                settings.Conductivity,
                settings.VoidHeatLoss,
                settings.RadiativeLoss,
                settings.AdiabaticLapse,
                settings.AirThermalConductivity,
                settings.AirThermalEmissivity,
                _heatDebugOverlay,
                _heatDebugFullScale);
        }

        /// <summary>
        /// 非阻塞地排一批请求：先按预算计划本帧的运动 tick 数，等到有空闲请求槽（worker 没在跑满两批）才把命令
        /// 与计划数交出去；暂停中只要有命令待发也照样排（否则画笔 / resize / PresentNow 在暂停时会卡住）。
        /// **任何情况下都不等待 worker**：抢不到槽就放弃本次请求（超预算的欠账按既有「丢时间不追账」语义丢弃）。
        /// </summary>
        private void RequestWorkerBatch(SimulationWorker worker, float deltaTime)
        {
            SimulationFrame frame = CurrentFrame;
            bool paused = frame != null && frame.World.Paused;
            int pending = _commands.PendingCount;

            if (pending == 0 && paused)
            {
                // 暂停且无事可做：不发请求，worker 保持空闲（也就不产生无意义的帧）。
                return;
            }

            int ticks = paused ? 0 : PlanMotionTicks(deltaTime);
            if (ticks == 0 && pending == 0)
            {
                return;
            }

            if (!worker.TryAcquireRequestSlot(out int slot))
            {
                return;
            }

            // 本批的请求序号 = worker 侧自增序号的下一个：回执带回来的实测耗时靠它与本批计划配对。
            _sampledRequestSequence = worker.RequestSequence + 1;
            worker.RequestBatch(slot, ticks, _speed.EffectiveSpeed, _commands);
        }

        /// <summary>
        /// 主线程侧的计划：按真实帧时间、上一批实测耗时与名义帧预算，算出本帧该跑几个运动 tick。
        /// 预算取 <c>1000 / Application.targetFrameRate</c>（不限帧按 60 兜底）× 预算比例，是**绝对上限**语义。
        /// </summary>
        private int PlanMotionTicks(float deltaTime)
        {
            ParticleSimulationSettings settings = Settings;
            int motionRate = Mathf.Max(1, Mathf.RoundToInt(settings.MotionRate));
            int targetFrameRate = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60;
            double budgetMs = 1000.0 / targetFrameRate * settings.SpeedBudgetFraction;

            int ticks = _speed.PlanMotionTicks(
                deltaTime, _lastBatchSimMs, budgetMs, motionRate, settings.LogicTicksPerMotionTick);

            return ticks > SimulationWorker.MaxBatchTicks ? SimulationWorker.MaxBatchTicks : ticks;
        }

        /// <summary>
        /// 非阻塞地取回执：把与本次计划配对的实测耗时回喂倍速控制器（不配对的样本按无效样本跳过），
        /// 并按帧里的高速状态把倍速钉满档 / 钉回 1×。
        /// </summary>
        private void DrainResponses(SimulationWorker worker)
        {
            while (worker.TryReadResponse(out SimulationBatchResult result))
            {
                if (result.RequestSequence == _sampledRequestSequence)
                {
                    _lastBatchSimMs = result.SimMs;
                }
            }

            SimulationFrame frame = CurrentFrame;
            if (frame == null)
            {
                return;
            }

            bool active = frame.HighSpeed.Active;
            if (active != _highSpeedPinned)
            {
                _highSpeedPinned = active;
                _speed.SetSpeed(active ? _speed.MaxSpeedLimit : _speed.MinSpeedLimit);
            }
        }

        /// <summary>
        /// 消费最新已发布帧：**只有新帧**（换了发布器 / 序号前进）才走渲染上传；上传本身是主线程的 Unity 纹理操作
        /// （世界 → 像素的映射早已在 worker 上完成）。resize 换了尺寸时 <see cref="ParticleRenderer.Present(RenderFrame)"/>
        /// 会自己重建纹理与网格并重新框镜头。
        /// </summary>
        private void ConsumeFrame(SimulationWorker worker)
        {
            SimulationFrame frame = worker.CurrentFrame;
            if (frame == null)
            {
                return;
            }

            if (ReferenceEquals(frame, _presentedFrame)
                && frame.Sequence == _presentedSequence
                && frame.Generation == _presentedGeneration)
            {
                return;
            }

            _presentedFrame = frame;
            _presentedSequence = frame.Sequence;
            _presentedGeneration = frame.Generation;

            if (_renderer != null)
            {
                _renderer.Present(frame.Render);
            }
        }

        /// <summary>把 worker 的故障在主线程报出来（每个新故障一次）；worker 自己绝不调 Unity 日志。</summary>
        private void SurfaceWorkerFault(SimulationWorker worker)
        {
            int count = worker.FaultCount;
            if (count <= _reportedFaults)
            {
                return;
            }

            _reportedFaults = count;
            Debug.LogError(
                $"[ParticleSimulationDriver] 模拟 worker 故障，已停止推进模拟：{worker.Fault}");
        }

        /// <summary>
        /// 把世界产生的诊断消息（未注册类型 / shade 越界等）在主线程记出来——消息本身由 worker 线程产生，
        /// 但 Unity 日志只能在主线程调（见 <c>ParticleWorld.MessageSink</c> 与 <c>SimulationWorker.TryReadWorldMessage</c>）。
        /// </summary>
        private void DrainWorldMessages(SimulationWorker worker)
        {
            while (worker.TryReadWorldMessage(out string message))
            {
                Debug.LogError(message);
            }
        }
    }
}
