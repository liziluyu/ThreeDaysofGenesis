using Stopwatch = System.Diagnostics.Stopwatch;
using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 固定时间步长驱动 + 渲染调度。是**唯一**同时认识模拟与渲染的地方。
    /// </summary>
    /// <remarks>
    /// 解耦点：<see cref="ParticleWorld.StepMotion"/> / <see cref="ParticleWorld.StepLogic"/> 只管推进模拟，从不渲染；
    /// 渲染由本类在 <see cref="Update"/> 里按 <see cref="ParticleSimulationSettings.StepsPerPresent"/> 决定何时上屏。
    /// 默认值 1 = 每个运动帧后渲染一次；调大即可变成「多个运动帧渲染一次」，无需改动模拟或渲染代码。
    /// <para>两级节拍：运动帧 60 Hz（只跑粒子运动）、逻辑帧 20 Hz（相变 / 反应 / 热传导 / 湿度 / 降水 / 光照 / 随机刻）。
    /// 逻辑帧是运动帧节拍上的子节拍——每 <see cref="ParticleSimulationSettings.LogicTicksPerMotionTick"/>
    /// 个运动帧跑一次 <see cref="ParticleWorld.StepLogic"/>，其余只跑 <see cref="ParticleWorld.StepMotion"/>。</para>
    /// <para>沙箱尺寸：默认取 <see cref="ParticleSimulationSettings"/> 的宽高，本组件的「沙箱尺寸」字段非 0 时覆盖之。
    /// 运行中改尺寸（Inspector 或 <see cref="Resize"/>）会重建一个空世界，已有内容不保留。</para>
    /// <para>倍速：每真实帧跑几个运动帧由 <see cref="SpeedController"/> 决定（普通编辑模式恒 1×，
    /// 只有高速模式会改档——进入钉满档、退出钉回 1×）。
    /// 本类只负责测量本帧 tick 循环耗时并回喂、以及转发倍率；输入与展示在 <see cref="ParticleEditor"/>。
    /// 倍速只改每帧的 tick 数，tick 内部逐位不变；超预算的部分直接丢弃、绝不结转。</para>
    /// <para>高速模式：状态机与统计全在内核 <see cref="HighSpeedSession"/>（纯 C#、纯观察者——零 RNG 消耗、零格子写入）。
    /// 本类只做三件事：懒建会话、把每个逻辑帧转发给 <see cref="HighSpeedSession.OnLogicTick"/>、
    /// 见到 <see cref="HighSpeedSession.Completed"/> 就地 <see cref="Pause"/>。进入 / 退出与输入封锁、HUD 在
    /// <see cref="ParticleEditor"/>。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSimulationDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationSettings _settings;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("是否推进模拟。关掉即暂停：运动帧与逻辑帧一起冻结，仍可用 PresentNow 手动上屏。")]
        [SerializeField] private bool _simulate = true;

        [Header("沙箱尺寸（格，0 = 用 Settings 资产的值）")]
        [Tooltip("网格宽度。运行时在 Inspector 改这里（或改 Settings 资产）会重建一个空世界。")]
        [SerializeField, Min(0)] private int _widthOverride;

        [Tooltip("网格高度。运行时在 Inspector 改这里（或改 Settings 资产）会重建一个空世界。")]
        [SerializeField, Min(0)] private int _heightOverride;

        [Header("调试")]
        [Tooltip("热力叠加：把每格温度按「越热越红」叠在上屏像素上（空气格也显示）。只影响显示，不影响模拟。")]
        [SerializeField] private bool _heatDebugOverlay;

        [Tooltip("热力叠加的满量程温度：温度达到它即为纯红。<= 0 时不叠色。")]
        [SerializeField] private float _heatDebugFullScale = HeatMap.DefaultDebugFullScaleTemperature;

        private ParticleSimulationSettings _fallbackSettings;
        private ParticleWorld _world;
        private SpeedController _speed;
        private HighSpeedSession _highSpeed;
        private readonly Stopwatch _frameStopwatch = new Stopwatch();
        private double _lastFrameSimMs;
        private int _stepsSincePresent;
        private int _motionTicks;

        /// <summary>当前世界。在 <c>Awake</c> 之后可用。</summary>
        public ParticleWorld World
        {
            get { return _world; }
        }

        /// <summary>倍速控制器（本类只测量耗时并转发倍率）。输入与展示在 <see cref="ParticleEditor"/>，
        /// 档位只由 <see cref="HighSpeedSession"/> 改变——普通编辑模式恒 1×。</summary>
        public SpeedController Speed
        {
            get { return _speed; }
        }

        /// <summary>高速模式会话（状态机与统计全在内核，纯 C#）。世界建好后**懒建**，时长取设置资产的
        /// <see cref="ParticleSimulationSettings.HighSpeedLogicFrames"/>；世界重建会连带丢弃旧会话。</summary>
        public HighSpeedSession HighSpeed
        {
            get
            {
                if (_highSpeed == null && _world != null)
                {
                    _highSpeed = new HighSpeedSession(_world, Settings.HighSpeedLogicFrames);
                }

                return _highSpeed;
            }
        }

        /// <summary>是否处于高速模式：进入后、确认退出前恒为 true（<see cref="HighSpeedSession.Completed"/> 后仍为 true）。</summary>
        public bool HighSpeedActive
        {
            get
            {
                HighSpeedSession session = HighSpeed;
                return session != null && session.Active;
            }
        }

        /// <summary>进入高速模式：转发给内核会话（把倍速钉到控制器满档，退出时钉回 1×）。幂等，已在模式中则无事发生。
        /// 暂停中进入会先 <see cref="Resume"/>——否则 tick 循环被暂停挡住，会话永远停在 0/<see cref="HighSpeedSession.DurationLogicTicks"/>
        /// 且模式锁定编辑与退出热键，形成死锁。跑满时长后驱动会重新 <see cref="Pause"/>（见 Update）。
        /// 本方法只做转发与暂停解除——输入与显示在 <see cref="ParticleEditor"/>。</summary>
        public void EnterHighSpeed()
        {
            HighSpeedSession session = HighSpeed;
            if (session != null)
            {
                session.Enter(_speed);
                if (session.Active)
                {
                    Resume();
                }
            }
        }

        /// <summary>是否推进模拟（运动帧与逻辑帧）。运行时可切换（继续/暂停）。</summary>
        public bool Simulate
        {
            get { return _simulate; }
            set { _simulate = value; }
        }

        /// <summary>是否处于暂停：暂停时不推进任何帧（运动帧与逻辑帧一起冻结），网格内容不变化。</summary>
        public bool Paused
        {
            get { return !_simulate; }
            set { _simulate = !value; }
        }

        /// <summary>进入暂停：冻结所有帧（运动帧与逻辑帧）。幂等。由调用方决定何时暂停（例如输入处理）。</summary>
        public void Pause()
        {
            _simulate = false;
        }

        /// <summary>退出暂停：恢复推进所有帧。幂等。</summary>
        public void Resume()
        {
            _simulate = true;
        }

        /// <summary>把网格**当前**状态立即上屏一次（暂停中改了网格后可用）。本方法不推进逻辑帧。</summary>
        public void PresentNow()
        {
            Present();
        }

        /// <summary>
        /// 按新尺寸重建一个**空**世界并立即上屏一次（相机由渲染器重新框住整张网格）。**已有内容不保留。**
        /// </summary>
        /// <remarks>
        /// 等价于在 Inspector 里填「沙箱尺寸」字段：两者都只是把尺寸覆盖写回本组件，
        /// 下一帧 <see cref="Update"/> 检测到世界尺寸不符时同样重建。范围夹在 <c>1..ParticleWorld.MaxSide</c>。
        /// </remarks>
        public void Resize(int width, int height)
        {
            _widthOverride = Mathf.Clamp(width, 1, ParticleWorld.MaxSide);
            _heightOverride = Mathf.Clamp(height, 1, ParticleWorld.MaxSide);

            if (_world == null)
            {
                return;
            }

            CreateWorld();
            Present();
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
            CreateWorld();
        }

        /// <summary>按 <see cref="RequestedWidth"/>/<see cref="RequestedHeight"/> 建世界并清空累加器，
        /// 然后把所有空格填成大气粒子——风与对流需要空气本身是会动、携带热量的物质。
        /// typeId 0 此后只作为「被抹除留下的真空」短暂出现，行为规则同样允许气体换入。</summary>
        private void CreateWorld()
        {
            // 世界换了，旧会话持有的是旧世界——退出并丢弃，下次访问 HighSpeed 时按新世界重建。
            if (_highSpeed != null)
            {
                if (_highSpeed.Active)
                {
                    _highSpeed.Exit(_speed);
                }

                _highSpeed = null;
            }

            ParticleSimulationSettings settings = Settings;
            _world = new ParticleWorld(RequestedWidth, RequestedHeight, settings.Seed);
            _speed.ResetAccumulator();
            _lastFrameSimMs = 0.0;
            _stepsSincePresent = 0;
            _motionTicks = 0;

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
        }

        private void Start()
        {
            Present();
        }

        private void OnDestroy()
        {
            // 代码兜底创建的 ScriptableObject 不受场景管理，必须自己销毁，否则每次进播放都泄漏一个实例。
            if (_fallbackSettings != null)
            {
                Destroy(_fallbackSettings);
                _fallbackSettings = null;
            }
        }

        private void Update()
        {
            if (_world == null)
            {
                return;
            }

            // Inspector 里改了「沙箱尺寸」（或 Settings 资产的宽高）后在此生效；尺寸一致时零成本跳过。
            if (_world.Width != RequestedWidth || _world.Height != RequestedHeight)
            {
                CreateWorld();
                Present();
            }

            // 热力学参数与调试叠加：每帧写回，运行时改 Inspector / 资产即时生效，重建世界后也自动重新应用。
            HeatMap heat = _world.Heat;
            ParticleSimulationSettings current = Settings;
            heat.Conductivity = current.Conductivity;
            heat.VoidHeatLoss = current.VoidHeatLoss;
            heat.RadiativeLoss = current.RadiativeLoss;
            heat.AdiabaticLapse = current.AdiabaticLapse;
            heat.AirThermalConductivity = current.AirThermalConductivity;
            heat.AirThermalEmissivity = current.AirThermalEmissivity;
            heat.DebugHeatOverlay = _heatDebugOverlay;
            heat.DebugFullScaleTemperature = _heatDebugFullScale;
            _world.RandomTicksPerStep = current.RandomTicksPerStep;

            if (_simulate)
            {
                ParticleSimulationSettings settings = Settings;
                int motionRate = Mathf.Max(1, Mathf.RoundToInt(settings.MotionRate));
                int stepsPerPresent = settings.StepsPerPresent;
                int logicEvery = settings.LogicTicksPerMotionTick;

                // 预算：本帧允许花在模拟 tick 上的时间**绝对上限** = 目标帧率对应的一帧时间 × 预算比例。
                // 目标帧率取 Application.targetFrameRate（<= 0 即不限帧时按 60 算），因此基准是固定的名义帧时间，
                // 不是本帧的真实 deltaTime：高刷屏（如 144 Hz）下 8.33ms 相当于真实帧时间的 120%，
                // 掉帧时则只占真实帧时间的一小部分——这是有意的绝对上限语义。
                int targetFrameRate = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60;
                double budgetMs = 1000.0 / targetFrameRate * settings.SpeedBudgetFraction;

                // 倍速在这里落地：倍率只改变「本帧跑几个运动 tick」，每个 tick 内部逐位不变。
                // 暂停（_simulate = false）时不调用：欠账不涨，恢复后接着当前倍率跑。
                // 把 logicEvery（= round(运动速率 / 逻辑速率)）一并注入：自适应档按它把一个逻辑周期的运动成本折算进预算比较。
                int ticks = _speed.PlanMotionTicks(
                    Time.deltaTime, _lastFrameSimMs, budgetMs, motionRate, logicEvery);

                _frameStopwatch.Restart();
                for (int i = 0; i < ticks; i++)
                {
                    bool highSpeedCompleted = false;

                    // 逻辑帧是运动帧节拍上的一个子节拍：每 logicEvery 个运动帧跑一次「逻辑 + 运动」，
                    // 其余运动帧只跑运动。因此运动帧 60 Hz、逻辑帧 = 60 / logicEvery。
                    _motionTicks++;
                    if (_motionTicks >= logicEvery)
                    {
                        _motionTicks = 0;
                        _world.StepLogic();

                        // 高速模式：每个逻辑帧给内核会话记一笔（产生/死亡计数 + 每 10 帧采样），
                        // 跑满时长就地暂停并跳出——世界恰好停在 DurationLogicTicks 这一刻，不越跑。
                        HighSpeedSession session = _highSpeed;
                        if (session != null && session.Active)
                        {
                            if (!session.Completed)
                            {
                                session.OnLogicTick();
                            }

                            if (session.Completed)
                            {
                                Pause();
                                highSpeedCompleted = true;
                            }
                        }
                    }
                    else
                    {
                        _world.StepMotion();
                    }

                    // 渲染与模拟解耦的接缝：上屏只在本帧所有运动帧跑完之后决定一次（高倍速下多推无意义）。
                    _stepsSincePresent++;

                    if (highSpeedCompleted)
                    {
                        break;
                    }
                }
                _frameStopwatch.Stop();

                // 回喂实测耗时，供控制器更新「每 tick 多少钱」的 EMA 与自适应档的 AIMD 判断。
                _lastFrameSimMs = _frameStopwatch.Elapsed.TotalMilliseconds;

                if (_stepsSincePresent >= stepsPerPresent)
                {
                    Present();
                }
            }
        }

        private void Present()
        {
            _stepsSincePresent = 0;
            if (_renderer != null)
            {
                _renderer.Present(_world);
            }
        }
    }
}
