using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 倍速控制器：把「每真实帧跑几个运动 tick」从固定累加器改成可缩放（手动档 + 自适应档）的预算式决策。
    /// **纯 C#（不引用 UnityEngine）**，因此既能在 Unity 里被驱动调用，也能在 net9 测试工程下单独编译与测试。
    /// </summary>
    /// <remarks>
    /// <para>四条硬语义（详见 <c>docs/ParticleTypeInterface.md</c> §10「倍速」）：</para>
    /// <list type="number">
    /// <item><description><b>固定步长不变</b>：倍速只改变每真实帧跑几个运动 tick，tick 内部逐位不变——
    /// 任何倍率下同一个 tick 序列推进出的世界逐位相同。本类不认识 <c>StepLogic</c> / <c>StepMotion</c>。</description></item>
    /// <item><description><b>丢时间不追账</b>：单帧应跑 tick 数超过预算上限时，超出部分连同欠账一起丢弃
    /// （<see cref="_acc"/> 清零），绝不结转，因此死亡螺旋构造性不可能。</description></item>
    /// <item><description><b>进度保底</b>：预算上限至少为 1，因此再紧的预算也照跑 1 个运动 tick（帧率代价调用方自负）。
    /// 注意保底的是<b>上限</b>：<c>dt</c> 本身不足一个 tick 时该帧照旧 0 个 tick，与不含本控制器的固定累加器逐位一致。</description></item>
    /// <item><description><b>默认关闭</b>：初值手动 1×、自适应关。</description></item>
    /// </list>
    /// <para>自适应档的调档**只在已平滑的「每 tick 耗时」EMA 上做**，且每
    /// <see cref="DecisionIntervalFrames"/> 帧才评估一次（单帧成本噪声不进决策），再配以
    /// <see cref="UpWatermark"/>～<see cref="DownWatermark"/> 的死区与换向滞回，因此倍率不会跟着帧耗时抖动。</para>
    /// <para>暂停由调用方控制：暂停时不调用 <see cref="PlanMotionTicks"/>，欠账不涨、EMA 不更新；恢复后接着当前倍率跑。</para>
    /// <para>热路径零分配：无闭包、无装箱、无字符串。</para>
    /// </remarks>
    public sealed class SpeedController
    {
        /// <summary>默认倍率下限（手动档与自适应档都夹在它之上）。</summary>
        public const float MinSpeed = 1f;

        /// <summary>默认倍率上限（手动档 ×2 调档到顶，自适应档加性升到这里为止）。由设置资产可覆盖注入。</summary>
        public const float MaxSpeed = 16f;

        /// <summary>默认自适应升档步长（加性慢升：每评估一次 +UpStep，直到 <see cref="MaxSpeed"/>）。</summary>
        public const float UpStep = 0.25f;

        /// <summary>默认自适应降档系数（乘性快降：每评估一次 ×DownFactor，直到 <see cref="MinSpeed"/>）。</summary>
        public const float DownFactor = 0.5f;

        /// <summary>默认「每运动 tick 耗时」EMA 的平滑系数（新样本权重）。</summary>
        public const float EmaAlpha = 0.2f;

        /// <summary>默认「实测倍率」显示 EMA 的平滑系数（新样本权重）。比 <see cref="EmaAlpha"/> 更钝，
        /// 因为它只喂 HUD，抖动越小越好。</summary>
        public const float DisplayEmaAlpha = 0.1f;

        /// <summary>一个逻辑周期包含的运动 tick 数（20 Hz 逻辑 / 60 Hz 运动）：自适应档把「每 tick 成本」
        /// 折成「一周期成本」再与预算比较。</summary>
        public const int MotionTicksPerCycle = 3;

        /// <summary>默认升档水位线：一周期实测成本低于「预算 × 它」才升档。</summary>
        public const double UpWatermark = 0.75;

        /// <summary>默认降档水位线：一周期实测成本高于「预算 × 它」才降档。
        /// 它与 <see cref="UpWatermark"/> 之间是死区，单帧成本噪声落在这里不再引起调档。</summary>
        public const double DownWatermark = 0.95;

        /// <summary>默认滞回帧数：一次调档后 N 帧内不许反向，避免便宜/昂贵帧交替时来回振荡。</summary>
        public const int HysteresisFrames = 10;

        /// <summary>默认调档节拍：自适应档每这么多次 <see cref="PlanMotionTicks"/> 才评估一次升/降。</summary>
        public const int DecisionIntervalFrames = 10;

        /// <summary>默认「每运动 tick 耗时」初值（毫秒，保守值）：首帧还没样本时用它算预算上限。</summary>
        public const double InitialPerTickMs = 1.0;

        private readonly float _minSpeed;
        private readonly float _maxSpeed;
        private readonly float _upStep;
        private readonly float _downFactor;
        private readonly float _emaAlpha;
        private readonly double _upWatermark;
        private readonly double _downWatermark;
        private readonly int _hysteresisFrames;
        private readonly int _decisionIntervalFrames;

        private float _speed;
        private bool _adaptive;
        private float _acc;
        private double _perTickMs;

        // 实测倍率 = _emaTicks / (_emaDt × _lastMotionRate)：分子分母分别 EMA 再相除，
        // 因此 dt 抖动 / tick 量化（0/1/2 tick 帧）都不会给比值引入偏差。
        private double _emaTicks;
        private double _emaDt;
        private int _lastMotionRate;

        private int _lastPlannedTicks;
        private int _droppedTicksLastFrame;
        private int _hysteresisRemaining;
        private int _lastDirection;
        private int _framesSinceDecision;

        /// <summary>用全部默认常量构造（等价于 <c>new SpeedController(MinSpeed, MaxSpeed)</c>）。</summary>
        public SpeedController()
            : this(MinSpeed, MaxSpeed)
        {
        }

        /// <summary>用默认调档/反馈常量构造，只覆盖速率区间（生产路径：上限来自设置资产）。</summary>
        public SpeedController(float minSpeed, float maxSpeed)
            : this(
                minSpeed, maxSpeed, UpStep, DownFactor, EmaAlpha,
                UpWatermark, DownWatermark, HysteresisFrames, DecisionIntervalFrames)
        {
        }

        /// <summary>全参数构造：所有常量可注入，便于测试用确定性的档位与反馈曲线。</summary>
        public SpeedController(
            float minSpeed,
            float maxSpeed,
            float upStep,
            float downFactor,
            float emaAlpha,
            double upWatermark,
            double downWatermark,
            int hysteresisFrames,
            int decisionIntervalFrames)
        {
            _minSpeed = minSpeed > 0f ? minSpeed : MinSpeed;
            _maxSpeed = maxSpeed >= _minSpeed ? maxSpeed : _minSpeed;
            _upStep = upStep > 0f ? upStep : UpStep;
            _downFactor = downFactor > 0f && downFactor < 1f ? downFactor : DownFactor;
            _emaAlpha = emaAlpha > 0f && emaAlpha <= 1f ? emaAlpha : EmaAlpha;
            _upWatermark = upWatermark > 0.0 && upWatermark < 1.0 ? upWatermark : UpWatermark;
            _downWatermark = downWatermark > 0.0 && downWatermark < 1.0 ? downWatermark : DownWatermark;
            _hysteresisFrames = hysteresisFrames > 0 ? hysteresisFrames : 1;
            _decisionIntervalFrames = decisionIntervalFrames > 0 ? decisionIntervalFrames : 1;

            _speed = _minSpeed;
            _perTickMs = InitialPerTickMs;
        }

        /// <summary>当前倍率（手动档 = 用户档位；自适应档 = AIMD 当前值），恒在 <c>[MinSpeed, MaxSpeed]</c> 内。</summary>
        public float Speed
        {
            get { return _speed; }
        }

        /// <summary>是否处于自适应档。默认 false（手动档）。</summary>
        public bool Adaptive
        {
            get { return _adaptive; }
        }

        /// <summary>
        /// **实测**倍率：<see cref="Speed"/> 是目标倍率，而预算封顶 / 丢账时实际吞吐达不到它。
        /// 本属性 = <c>EMA(本帧 tick 数) / (EMA(dt) × 运动速率)</c>（系数 <see cref="DisplayEmaAlpha"/>），
        /// 0 表示还没有过有效样本。
        /// </summary>
        /// <remarks>
        /// **显示用观测量，不参与任何调度决策**：它只把已经发生的事实（本帧跑了几个 tick）汇报出来。
        /// 分子分母分别 EMA 再相除，而不是对逐帧比值 <c>ticks / (dt × 速率)</c> 做 EMA——后者在 dt 抖动、
        /// tick 量化（0/1/2 tick 帧）下对标称倍率有偏（Jensen 不等式），稳态 1× 也未必收敛到 1。
        /// <c>dt ≤ 0</c> 的帧没有定义速率，跳过更新（保持上一个值）。
        /// </remarks>
        public float EffectiveSpeed
        {
            get
            {
                if (_emaDt <= 0.0 || _lastMotionRate <= 0)
                {
                    return 0f;
                }

                double speed = _emaTicks / (_emaDt * _lastMotionRate);
                return speed > float.MaxValue ? float.MaxValue : (float)speed;
            }
        }

        /// <summary>最近一次 <see cref="PlanMotionTicks"/> 计划跑的运动 tick 数。</summary>
        public int LastPlannedTicks
        {
            get { return _lastPlannedTicks; }
        }

        /// <summary>最近一次计划因超预算被丢弃的 tick 数（0 = 没丢）。</summary>
        public int DroppedTicksLastFrame
        {
            get { return _droppedTicksLastFrame; }
        }

        /// <summary>「每个运动 tick 耗时（毫秒）」的 EMA 估计；首帧为保守初值 <see cref="InitialPerTickMs"/>。</summary>
        public double PerTickMsEma
        {
            get { return _perTickMs; }
        }

        /// <summary>
        /// 手动调档：设置倍率并**切到手动档**（自适应关），钳到 <c>[MinSpeed, MaxSpeed]</c>。幂等。
        /// 欠账不清——调档发生在两次 <see cref="PlanMotionTicks"/> 之间时按新倍率继续消化。
        /// </summary>
        public void SetSpeed(float speed)
        {
            _adaptive = false;
            ResetAdaptiveDirection();
            _speed = ClampSpeed(speed);
        }

        /// <summary>
        /// 自适应档开关：开启时从**当前倍率**起步（不重置到 1×），并清掉滞回状态，让首帧就能按反馈调档。
        /// </summary>
        public void SetAdaptive(bool on)
        {
            _adaptive = on;
            ResetAdaptiveDirection();
        }

        /// <summary>
        /// 世界重建后清掉未消化的欠账（等价于旧固定累加器在 <c>CreateWorld</c> 里的归零）：
        /// 不改变倍率、自适应开关与耗时 EMA。
        /// </summary>
        public void ResetAccumulator()
        {
            _acc = 0f;
            _lastPlannedTicks = 0;
            _droppedTicksLastFrame = 0;
        }

        /// <summary>
        /// 每真实帧调用一次：先按上一帧实测耗时更新「每 tick 多少钱」的 EMA，再（自适应档、每
        /// <see cref="DecisionIntervalFrames"/> 帧一次）按平滑 EMA 与预算水位线调倍率，
        /// 然后按 <c>dt × 倍率</c> 累加时间并算出本帧要跑几个运动 tick，最后按预算上限截断（超出的丢弃、欠账清零）。
        /// </summary>
        /// <remarks>
        /// tick 数是按 <c>1f / motionRate</c> 这个 <b>float</b> 步长从欠账里重复扣减出来的（而不是 double 的
        /// <c>floor(欠账 × 速率)</c>）：这一步与迁移前驱动的固定累加器逐位同构，因此在默认手动 1× 且预算未截断时，
        /// 每帧 tick 数序列与改动前完全相同。倍速只改「扣几次」，不改每次扣多少。
        /// </remarks>
        /// <param name="dtSeconds">本真实帧的真实时长（秒，非负；通常来自 <c>Time.deltaTime</c>）。</param>
        /// <param name="lastFrameSimMs">上一帧实际跑运动 tick 循环的耗时（毫秒）；本帧调它时传上一帧的值，
        /// 首帧传 0（无效样本，不更新 EMA、不触发降档）。</param>
        /// <param name="budgetMs">本帧允许花在模拟 tick 上的时间预算（毫秒）：
        /// <c>1000 / 目标帧率 × 预算比例</c>。自适应档按「一周期 = 1 逻辑 + 3 运动」折算，即与
        /// <see cref="MotionTicksPerCycle"/> 相乘后比较。</param>
        /// <param name="motionRate">运动 tick 速率（Hz，至少 1）。</param>
        /// <returns>本帧应跑的运动 tick 数（≥ 0；需求为正时至少 1——预算上限有 1 tick 保底）。</returns>
        public int PlanMotionTicks(float dtSeconds, double lastFrameSimMs, double budgetMs, int motionRate)
        {
            // 1. 用上一帧的实测样本更新「每个运动 tick 耗时」的 EMA（无效样本跳过）。
            if (_lastPlannedTicks > 0 && lastFrameSimMs > 0.0)
            {
                double sample = lastFrameSimMs / _lastPlannedTicks;
                _perTickMs = _emaAlpha * sample + (1.0 - _emaAlpha) * _perTickMs;
            }

            int rate = motionRate < 1 ? 1 : motionRate;
            _lastMotionRate = rate;

            // 2. 自适应档调档：K 帧门控（单帧成本噪声不进决策）+ 平滑 EMA + 死区 + 滞回。
            if (_adaptive)
            {
                if (_hysteresisRemaining > 0)
                {
                    _hysteresisRemaining--;
                }

                _framesSinceDecision++;
                if (_framesSinceDecision >= _decisionIntervalFrames)
                {
                    _framesSinceDecision = 0;
                    UpdateAdaptive(budgetMs);
                }
            }

            // 3. 欠账 += dt × 倍率；再按与迁移前驱动**完全相同**的 float 步长重复扣减，
            //    得到本帧应跑的运动 tick 数（默认 1× 且 dt × 1f == dt，因此逐帧与旧累加器逐位一致）。
            float stepSeconds = 1f / rate;

            if (dtSeconds > 0f)
            {
                _acc += dtSeconds * _speed;
            }

            if (_acc < 0f)
            {
                _acc = 0f;
            }

            // 4. 预算上限：这帧还能塞下几个运动 tick。上限至少 1（进度保底），因此再紧的预算也照跑 1 个 tick。
            double perTickMs = _perTickMs > 0.0 ? _perTickMs : InitialPerTickMs;
            int cap;
            if (budgetMs > 0.0)
            {
                double raw = budgetMs / perTickMs;
                cap = raw >= int.MaxValue ? int.MaxValue : (int)raw;
                if (cap < 1)
                {
                    cap = 1;
                }
            }
            else
            {
                cap = 1;
            }

            int ticks = 0;
            while (ticks < cap && _acc >= stepSeconds)
            {
                _acc -= stepSeconds;
                ticks++;
            }

            _droppedTicksLastFrame = 0;
            if (_acc >= stepSeconds)
            {
                // 丢时间不追账：预算用光却还有需求，超出的部分连同欠账一起丢，绝不结转。
                int remaining = (int)(_acc / stepSeconds);
                _droppedTicksLastFrame = remaining > 0 ? remaining : 1;
                _acc = 0f;
            }

            _lastPlannedTicks = ticks;

            // 5. 实测倍率（显示用观测量，不参与上面的任何调度决策）：分子（tick 数）与分母（dt）分别 EMA，
            //    比值 = EMA(ticks) / (EMA(dt) × 速率)。dt ≤ 0 的帧没有定义速率，跳过更新。
            if (dtSeconds > 0f)
            {
                double alpha = DisplayEmaAlpha;
                _emaDt = alpha * dtSeconds + (1.0 - alpha) * _emaDt;
                _emaTicks = alpha * ticks + (1.0 - alpha) * _emaTicks;
            }

            return ticks;
        }

        /// <summary>
        /// AIMD：一周期（<see cref="MotionTicksPerCycle"/> 个运动 tick）的**平滑**实测成本低于
        /// 「预算 × <see cref="UpWatermark"/>」→ 加性升一档；高于「预算 × <see cref="DownWatermark"/>」→ 乘性降一档；
        /// 落在死区里不动。换方向受 <see cref="_hysteresisFrames"/> 帧滞回约束，抑制振荡。
        /// </summary>
        /// <param name="budgetPerCycle">一个逻辑周期的预算（毫秒）——与传给 <see cref="PlanMotionTicks"/> 的预算同值。</param>
        private void UpdateAdaptive(double budgetPerCycle)
        {
            int direction = 0;
            if (budgetPerCycle > 0.0)
            {
                double perTick = _perTickMs > 0.0 ? _perTickMs : InitialPerTickMs;
                double cycleCost = perTick * MotionTicksPerCycle;
                if (cycleCost < _upWatermark * budgetPerCycle)
                {
                    direction = 1;
                }
                else if (cycleCost > _downWatermark * budgetPerCycle)
                {
                    direction = -1;
                }
            }

            if (direction == 0)
            {
                return;
            }

            if (_lastDirection != 0 && direction != _lastDirection && _hysteresisRemaining > 0)
            {
                return;
            }

            if (direction > 0)
            {
                float up = _speed + _upStep;
                _speed = up > _maxSpeed ? _maxSpeed : up;
            }
            else
            {
                float down = _speed * _downFactor;
                _speed = down < _minSpeed ? _minSpeed : down;
            }

            _lastDirection = direction;
            _hysteresisRemaining = _hysteresisFrames;
        }

        private void ResetAdaptiveDirection()
        {
            _hysteresisRemaining = 0;
            _lastDirection = 0;
            _framesSinceDecision = 0;
        }

        private float ClampSpeed(float speed)
        {
            if (float.IsNaN(speed) || speed < _minSpeed)
            {
                return _minSpeed;
            }

            return speed > _maxSpeed ? _maxSpeed : speed;
        }
    }
}
