using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="SpeedController"/> 的行为契约：手动档缩放与忽略反馈、自适应档的 AIMD（加性升 / 乘性降，
    /// 并把超预算的目标需求折进反馈）、K 帧门控 + 平滑 EMA 使单帧成本噪声不驱动调档、超预算丢账不雪球、
    /// 滞回不振荡、预算保底 ≥1 tick，以及「默认手动 1× 与旧固定累加器在预算未截断时逐帧一致」和
    /// 「实测倍率稳态归一」两条回归。
    /// </summary>
    /// <remarks>
    /// 全部用注入常量的确定性构造 + 人造 <c>dt</c> / 耗时序列，不依赖 Unity 时钟，也不跑世界扫描。
    /// 预算按生产默认取「1000 / 60 × 0.5」毫秒，一周期运动 tick 数按生产默认注入 3（运动 60 Hz / 逻辑 20 Hz）。
    /// </remarks>
    public sealed class SpeedControllerTests
    {
        private const int MotionRate = 60;
        private const double BudgetMs = 1000.0 / 60.0 * 0.5;
        private const float FrameSeconds = 1f / 60f;

        /// <summary>一个逻辑周期包含的运动 tick 数：<c>round(60 / 20)</c>，与驱动注入值同源。</summary>
        private const int LogicTicksPerMotionTick = 3;

        /// <summary>手动档：tick 数随倍率精确缩放；耗时反馈（不管多贵）都不改倍率。</summary>
        [Test]
        public void ManualSpeedScalesTicksAndIgnoresFeedback()
        {
            SpeedController speed = new SpeedController();

            // 默认手动 1×：每帧 1/60 秒 → 每帧恰 1 个运动 tick。
            double lastSimMs = 0.0;
            for (int i = 0; i < 60; i++)
            {
                Assert.That(Plan(speed, FrameSeconds, lastSimMs, BudgetMs), Is.EqualTo(1));
                lastSimMs = 0.1; // 实测每个 tick 0.1ms：预算极充裕
            }

            speed.SetSpeed(4f);
            Assert.That(speed.Speed, Is.EqualTo(4f));
            Assert.That(speed.Adaptive, Is.False);

            // 4×：每帧 4 个 tick（预算是 1× 时的 4 倍余量，不触发丢账）。
            lastSimMs = 0.4;
            for (int i = 0; i < 30; i++)
            {
                Assert.That(Plan(speed, FrameSeconds, lastSimMs, BudgetMs), Is.EqualTo(4));
            }

            // 手动档不看反馈：连喂 500ms（远超预算）的昂贵样本，倍率仍是 4。
            for (int i = 0; i < 5; i++)
            {
                Plan(speed, FrameSeconds, 500.0, BudgetMs);
                Assert.That(speed.Speed, Is.EqualTo(4f));
                Assert.That(speed.Adaptive, Is.False);
            }

            // 手动调档钳到 [1, 16]。
            speed.SetSpeed(1000f);
            Assert.That(speed.Speed, Is.EqualTo(SpeedController.MaxSpeed));
            speed.SetSpeed(0.01f);
            Assert.That(speed.Speed, Is.EqualTo(SpeedController.MinSpeed));
        }

        /// <summary>自适应档：便宜帧序列加性升到上限 <see cref="SpeedController.MaxSpeed"/>（每 K 帧评估一次）。</summary>
        [Test]
        public void AdaptiveRisesToMaxOnCheapFrames()
        {
            SpeedController speed = new SpeedController();
            speed.SetAdaptive(true);
            Assert.That(speed.Speed, Is.EqualTo(SpeedController.MinSpeed));

            // 每 tick 0.01ms（占预算万分之一）→ 每次评估加性升一档。1→16 需 60 次评估，
            // 而调档节拍是 DecisionIntervalFrames 帧一次，故留足 700 帧。
            double lastSimMs = 0.0;
            for (int i = 0; i < 700; i++)
            {
                Plan(speed, FrameSeconds, lastSimMs, BudgetMs);
                lastSimMs = 0.01;
            }

            Assert.That(speed.Speed, Is.EqualTo(SpeedController.MaxSpeed));
            Assert.That(speed.Adaptive, Is.True);
        }

        /// <summary>自适应档：昂贵帧乘性快降（16× 到 1× 只需 4 次评估，即 4 × K 帧）。</summary>
        [Test]
        public void AdaptiveDropsMultiplicativelyOnExpensiveFrames()
        {
            SpeedController speed = new SpeedController();
            speed.SetSpeed(SpeedController.MaxSpeed);
            speed.SetAdaptive(true);

            int frames = 0;
            while (speed.Speed > SpeedController.MinSpeed && frames < 200)
            {
                Plan(speed, FrameSeconds, 60.0, BudgetMs);
                frames++;
            }

            Assert.That(speed.Speed, Is.EqualTo(SpeedController.MinSpeed));
            Assert.That(
                frames,
                Is.LessThanOrEqualTo(SpeedController.DecisionIntervalFrames * 5),
                "乘性降档应快（16→8→4→2→1，共 4 次评估），而不是逐帧只降一档");
        }

        /// <summary>
        /// 超预算需求折进反馈：目标倍率的需求超过预算时，反馈必须降档——这是旧反馈「只统计已交付吞吐、
        /// 被 cap 丢弃的需求不算数」导致自适应档饱和失效的回归（P3b）。
        /// </summary>
        [Test]
        public void AdaptiveDropsWhenDemandExceedsBudget()
        {
            SpeedController speed = new SpeedController();
            speed.SetSpeed(SpeedController.MaxSpeed);
            speed.SetAdaptive(true);

            // 每 tick 恒 1ms、预算 8.33ms ⇒ 每帧只买得起 ≈8.3 个 tick，16× 的另 ≈7.7 个需求被 cap 丢弃：
            // 旧反馈算一周期成本 1ms × 3 = 3ms < 0.75 × 预算，永远达不到降档水位线，倍率锁死 16；
            // 新反馈把需求折成 min(16, 8.3)，估计值饱和到「预算 × 周期」这一档，必须降档。
            const double perTickMs = 1.0;
            double lastSimMs = 0.0;
            for (int i = 0; i < 400; i++)
            {
                int ticks = Plan(speed, FrameSeconds, lastSimMs, BudgetMs);
                lastSimMs = ticks * perTickMs;
            }

            Assert.That(
                speed.Speed,
                Is.LessThan(8f),
                "需求被 cap 丢弃时自适应档必须降到预算买得起的吞吐以下，而不是锁在 16");
            Assert.That(speed.Speed, Is.InRange(SpeedController.MinSpeed, SpeedController.MaxSpeed));
        }

        /// <summary>
        /// 一周期运动 tick 数由驱动注入，而不是写死 3（P3c 回归）：同一份成本信号（每 tick 恒 3ms）下，
        /// 注入 1（运动 60 Hz / 逻辑 60 Hz）时一周期成本 3ms 低于升档水位线 ⇒ 升档；注入 3（60 / 20）时
        /// 一周期成本 9ms 高于降档水位线 ⇒ 不升档。旧实现把 3 写死，改 <c>MotionRate / LogicRate</c> 后
        /// 阈值失准一倍。
        /// </summary>
        [Test]
        public void AdaptiveThresholdUsesInjectedCycleLength()
        {
            SpeedController one = new SpeedController();
            SpeedController three = new SpeedController();

            // 先用恒定样本把「每 tick 3ms」的 EMA 收敛，排除 EMA 初值的瞬态干扰。
            double oneSample = 0.0;
            double threeSample = 0.0;
            for (int i = 0; i < 200; i++)
            {
                oneSample = Plan(one, FrameSeconds, oneSample, BudgetMs, 1) * 3.0;
                threeSample = Plan(three, FrameSeconds, threeSample, BudgetMs, 3) * 3.0;
            }

            one.SetAdaptive(true);
            three.SetAdaptive(true);
            for (int i = 0; i < 400; i++)
            {
                oneSample = Plan(one, FrameSeconds, oneSample, BudgetMs, 1) * 3.0;
                threeSample = Plan(three, FrameSeconds, threeSample, BudgetMs, 3) * 3.0;
            }

            Assert.That(three.Speed, Is.EqualTo(SpeedController.MinSpeed), "周期 3 的一周期成本超预算，不该升档");
            Assert.That(one.Speed, Is.GreaterThan(three.Speed), "周期 1 的同一成本信号应升档");
        }

        /// <summary>超预算丢账不追账：巨大卡顿帧被 cap、欠账清零、下一帧不雪球；预算再紧也保底 1 tick。</summary>
        [Test]
        public void OverBudgetTicksAreDroppedWithoutSnowballing()
        {
            SpeedController speed = new SpeedController();

            // 首帧就是 1 秒的大卡顿：需求 60 tick，但每 tick 估 1ms、预算 8.33ms 只买得起 8 个。
            int ticks = Plan(speed, 1f, 0.0, BudgetMs);
            Assert.That(ticks, Is.EqualTo(8));
            Assert.That(speed.LastPlannedTicks, Is.EqualTo(8));
            Assert.That(speed.DroppedTicksLastFrame, Is.EqualTo(52));

            // 下一帧恢复正常 dt：丢掉的欠账没有被记回来（构造性防死亡螺旋）。
            int next = Plan(speed, FrameSeconds, 8.0, BudgetMs);
            Assert.That(next, Is.EqualTo(1));
            Assert.That(speed.DroppedTicksLastFrame, Is.Zero);

            // 保底：预算为 0（极端超预算）时上限仍为 1，照跑 1 个 tick。
            SpeedController tight = new SpeedController();
            int floorTicks = Plan(tight, FrameSeconds, 0.0, 0.0);
            Assert.That(floorTicks, Is.EqualTo(1));
            Assert.That(tight.DroppedTicksLastFrame, Is.Zero);
        }

        /// <summary>
        /// 滞回：便宜 / 昂贵**成块**交替（每 20 帧翻转）时，反向加锁把方向反转次数压到远低于无滞回对照。
        /// 对照组滞回 1 帧（= 几乎不挡反向），受试组注入 25 帧（跨过 2 个调档节拍）。
        /// </summary>
        [Test]
        public void HysteresisBoundsDirectionReversals()
        {
            SpeedController damped = new SpeedController(
                SpeedController.MinSpeed, SpeedController.MaxSpeed, SpeedController.UpStep, SpeedController.DownFactor,
                SpeedController.EmaAlpha, SpeedController.UpWatermark, SpeedController.DownWatermark, 25,
                SpeedController.DecisionIntervalFrames);
            SpeedController control = new SpeedController(
                SpeedController.MinSpeed, SpeedController.MaxSpeed, SpeedController.UpStep, SpeedController.DownFactor,
                SpeedController.EmaAlpha, SpeedController.UpWatermark, SpeedController.DownWatermark, 1,
                SpeedController.DecisionIntervalFrames);

            int dampedReversals = CountReversals(damped, 200, 20);
            int controlReversals = CountReversals(control, 200, 20);

            Assert.That(controlReversals, Is.GreaterThanOrEqualTo(5), "无滞回对照应跟着成块输入反复换向");
            Assert.That(dampedReversals, Is.LessThan(controlReversals));
        }

        /// <summary>
        /// 单帧成本噪声不进决策：便宜 / 昂贵逐帧交替（旧实现下几乎逐帧反向）时，K 帧门控 + 平滑 EMA + 死区
        /// 让倍率基本不动——这是「自适应档倍率严重抖动」的回归。
        /// </summary>
        [Test]
        public void AdaptiveIgnoresPerFrameCostNoise()
        {
            SpeedController speed = new SpeedController();

            int reversals = CountReversals(speed, 200, 1);

            Assert.That(reversals, Is.LessThanOrEqualTo(3), "逐帧成本噪声不该驱动倍率来回换向");
            Assert.That(speed.Speed, Is.InRange(SpeedController.MinSpeed, SpeedController.MaxSpeed));
        }

        /// <summary>
        /// 默认手动 1× 与迁移前的固定累加器逐帧一致（**预算未截断时**）：60 fps 附近抖动的确定性帧序列下，
        /// 「每帧跑几个运动 tick」的序列完全相同（倍速的默认档不得改变现状行为；超预算丢账语义另测）。
        /// </summary>
        [Test]
        public void DefaultManualOneTimesMatchesLegacyAccumulator()
        {
            // 旧驱动的固定累加器（迁移前的实现，原样保留在测试里作基线）。
            const float stepSeconds = 1f / MotionRate;
            const int legacyMaxSteps = 4;
            float accumulator = 0f;

            SpeedController speed = new SpeedController();
            uint seed = 12345u;
            for (int i = 0; i < 600; i++)
            {
                // 16.67ms ± 1.5ms 的确定性抖动（不含 >66ms 的卡顿帧——那种帧按新语义走预算丢账）。
                seed = seed * 1664525u + 1013904223u;
                int jitter = (int)((seed >> 8) % 7u) - 3;
                float dt = FrameSeconds + jitter * 0.0005f;

                accumulator += dt;
                float maxLag = stepSeconds * legacyMaxSteps;
                if (accumulator > maxLag)
                {
                    accumulator = maxLag;
                }

                int legacyTicks = 0;
                while (accumulator >= stepSeconds && legacyTicks < legacyMaxSteps)
                {
                    accumulator -= stepSeconds;
                    legacyTicks++;
                }

                int planned = Plan(speed, dt, legacyTicks * 0.3, BudgetMs);
                Assert.That(planned, Is.EqualTo(legacyTicks), "第 {0} 帧的 tick 数应与旧累加器一致", i);
            }
        }

        /// <summary>
        /// 实测倍率：手动 16× 撞上紧预算时，<see cref="SpeedController.EffectiveSpeed"/> 收敛到被 cap 卡住的真实吞吐，
        /// 而不是目标 16——HUD 的「实 ×M」就是靠它说真话。
        /// </summary>
        [Test]
        public void EffectiveSpeedReportsCappedThroughputNotTarget()
        {
            SpeedController speed = new SpeedController();
            speed.SetSpeed(SpeedController.MaxSpeed);

            // 4ms 预算、每 tick 估 1ms（耗时样本恒为 0 = 无效，EMA 保持保守初值）⇒ 每帧恰好吃 4 个 tick，
            // 而 16× 在 60 Hz 下每帧需求 16 个 ⇒ 每帧都被 cap 并丢账。
            const double tightBudgetMs = 4.0;
            for (int i = 0; i < 200; i++)
            {
                int ticks = Plan(speed, FrameSeconds, 0.0, tightBudgetMs);
                Assert.That(ticks, Is.EqualTo(4));
                Assert.That(speed.DroppedTicksLastFrame, Is.GreaterThan(0), "被 cap 的那部分应记为丢弃");
            }

            Assert.That(speed.Speed, Is.EqualTo(16f), "手动档不受反馈影响，目标仍是 16");
            Assert.That(speed.LastPlannedTicks, Is.EqualTo(4));
            Assert.That(speed.EffectiveSpeed, Is.EqualTo(4f).Within(0.05f));
        }

        /// <summary>实测倍率：预算充裕时收敛到目标倍率附近（手动 4×，实测应 ≈4×）。</summary>
        [Test]
        public void EffectiveSpeedConvergesToTargetWhenUnthrottled()
        {
            SpeedController speed = new SpeedController();
            speed.SetSpeed(4f);

            for (int i = 0; i < 200; i++)
            {
                int ticks = Plan(speed, FrameSeconds, 0.0, BudgetMs);
                Assert.That(ticks, Is.EqualTo(4));
                Assert.That(speed.DroppedTicksLastFrame, Is.Zero, "预算充裕不该丢账");
            }

            Assert.That(speed.EffectiveSpeed, Is.EqualTo(4f).Within(0.05f));
        }

        /// <summary>
        /// 实测倍率稳态归一：手动 1× 下，无论 <c>dt</c> 恒定还是抖动（含 0/1/2 tick 的量化帧），
        /// 分子分母分别 EMA 再相除都得 <c>≈1</c>——这是「1× 时不归一」的回归。
        /// </summary>
        [Test]
        public void EffectiveSpeedNormalizesAtSteadyStateOneTimes()
        {
            // 恒定 dt：60 帧后 EMA 早已收敛，实测应为 1.00。
            SpeedController steady = new SpeedController();
            for (int i = 0; i < 200; i++)
            {
                Plan(steady, FrameSeconds, 0.0, BudgetMs);
            }

            Assert.That(steady.EffectiveSpeed, Is.EqualTo(1f).Within(0.02f));

            // 抖动 dt：逐帧仍与旧固定累加器一致，实测倍率依旧归一（逐帧比率 EMA 会被量化噪声带偏）。
            const float stepSeconds = 1f / MotionRate;
            const int legacyMaxSteps = 4;
            float accumulator = 0f;
            SpeedController jittered = new SpeedController();
            uint seed = 12345u;
            for (int i = 0; i < 600; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                int jitter = (int)((seed >> 8) % 7u) - 3;
                float dt = FrameSeconds + jitter * 0.0005f;

                accumulator += dt;
                float maxLag = stepSeconds * legacyMaxSteps;
                if (accumulator > maxLag)
                {
                    accumulator = maxLag;
                }

                int legacyTicks = 0;
                while (accumulator >= stepSeconds && legacyTicks < legacyMaxSteps)
                {
                    accumulator -= stepSeconds;
                    legacyTicks++;
                }

                Assert.That(Plan(jittered, dt, legacyTicks * 0.3, BudgetMs), Is.EqualTo(legacyTicks));
            }

            Assert.That(jittered.EffectiveSpeed, Is.EqualTo(1f).Within(0.02f));
        }

        /// <summary>
        /// 逐帧数「倍率变化方向反转」的次数。<paramref name="blockFrames"/> 帧一组交替喂便宜 / 昂贵样本
        /// （昂贵 = 20ms，远超 8.33ms 预算）：1 = 逐帧交替，20 = 每 20 帧成块翻转。
        /// </summary>
        private static int CountReversals(SpeedController speed, int frames, int blockFrames)
        {
            speed.SetSpeed(4f);
            speed.SetAdaptive(true);

            int reversals = 0;
            int previous = 0;
            float last = speed.Speed;

            for (int i = 0; i < frames; i++)
            {
                double simMs = ((i / blockFrames) % 2) == 0 ? 20.0 : 0.01;
                Plan(speed, FrameSeconds, simMs, BudgetMs);

                int direction = speed.Speed > last ? 1 : (speed.Speed < last ? -1 : 0);
                if (direction != 0)
                {
                    if (previous != 0 && direction != previous)
                    {
                        reversals++;
                    }

                    previous = direction;
                }

                last = speed.Speed;
            }

            return reversals;
        }

        /// <summary>按生产默认速率与默认周期注入（<see cref="MotionRate"/> Hz、<see cref="LogicTicksPerMotionTick"/>）。</summary>
        private static int Plan(SpeedController speed, float dtSeconds, double lastFrameSimMs, double budgetMs)
        {
            return Plan(speed, dtSeconds, lastFrameSimMs, budgetMs, LogicTicksPerMotionTick);
        }

        /// <summary>显式注入一周期运动 tick 数（用于验证驱动注入值真的参与预算折算）。</summary>
        private static int Plan(
            SpeedController speed, float dtSeconds, double lastFrameSimMs, double budgetMs, int logicTicksPerMotionTick)
        {
            return speed.PlanMotionTicks(dtSeconds, lastFrameSimMs, budgetMs, MotionRate, logicTicksPerMotionTick);
        }
    }
}
