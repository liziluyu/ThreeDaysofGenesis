using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="SpeedController"/> 的行为契约：手动档缩放与忽略反馈、自适应档的 AIMD（加性升 / 乘性降）、
    /// K 帧门控 + 平滑 EMA 使单帧成本噪声不驱动调档、超预算丢账不雪球、滞回不振荡、预算保底 ≥1 tick，
    /// 以及「默认手动 1× 与旧固定累加器逐帧一致」和「实测倍率稳态归一」两条回归。
    /// </summary>
    /// <remarks>
    /// 全部用注入常量的确定性构造 + 人造 <c>dt</c> / 耗时序列，不依赖 Unity 时钟，也不跑世界扫描。
    /// 预算按生产默认取「1000 / 60 × 0.5」毫秒。
    /// </remarks>
    public sealed class SpeedControllerTests
    {
        private const int MotionRate = 60;
        private const double BudgetMs = 1000.0 / 60.0 * 0.5;
        private const float FrameSeconds = 1f / 60f;

        /// <summary>手动档：tick 数随倍率精确缩放；耗时反馈（不管多贵）都不改倍率。</summary>
        [Test]
        public void ManualSpeedScalesTicksAndIgnoresFeedback()
        {
            SpeedController speed = new SpeedController();

            // 默认手动 1×：每帧 1/60 秒 → 每帧恰 1 个运动 tick。
            double lastSimMs = 0.0;
            for (int i = 0; i < 60; i++)
            {
                Assert.That(speed.PlanMotionTicks(FrameSeconds, lastSimMs, BudgetMs, MotionRate), Is.EqualTo(1));
                lastSimMs = 0.1; // 实测每个 tick 0.1ms：预算极充裕
            }

            speed.SetSpeed(4f);
            Assert.That(speed.Speed, Is.EqualTo(4f));
            Assert.That(speed.Adaptive, Is.False);

            // 4×：每帧 4 个 tick（预算是 1× 时的 4 倍余量，不触发丢账）。
            lastSimMs = 0.4;
            for (int i = 0; i < 30; i++)
            {
                Assert.That(speed.PlanMotionTicks(FrameSeconds, lastSimMs, BudgetMs, MotionRate), Is.EqualTo(4));
            }

            // 手动档不看反馈：连喂 500ms（远超预算）的昂贵样本，倍率仍是 4。
            for (int i = 0; i < 5; i++)
            {
                speed.PlanMotionTicks(FrameSeconds, 500.0, BudgetMs, MotionRate);
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
                speed.PlanMotionTicks(FrameSeconds, lastSimMs, BudgetMs, MotionRate);
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
                speed.PlanMotionTicks(FrameSeconds, 60.0, BudgetMs, MotionRate);
                frames++;
            }

            Assert.That(speed.Speed, Is.EqualTo(SpeedController.MinSpeed));
            Assert.That(
                frames,
                Is.LessThanOrEqualTo(SpeedController.DecisionIntervalFrames * 5),
                "乘性降档应快（16→8→4→2→1，共 4 次评估），而不是逐帧只降一档");
        }

        /// <summary>超预算丢账不追账：巨大卡顿帧被 cap、欠账清零、下一帧不雪球；预算再紧也保底 1 tick。</summary>
        [Test]
        public void OverBudgetTicksAreDroppedWithoutSnowballing()
        {
            SpeedController speed = new SpeedController();

            // 首帧就是 1 秒的大卡顿：需求 60 tick，但每 tick 估 1ms、预算 8.33ms 只买得起 8 个。
            int ticks = speed.PlanMotionTicks(1f, 0.0, BudgetMs, MotionRate);
            Assert.That(ticks, Is.EqualTo(8));
            Assert.That(speed.LastPlannedTicks, Is.EqualTo(8));
            Assert.That(speed.DroppedTicksLastFrame, Is.EqualTo(52));

            // 下一帧恢复正常 dt：丢掉的欠账没有被记回来（构造性防死亡螺旋）。
            int next = speed.PlanMotionTicks(FrameSeconds, 8.0, BudgetMs, MotionRate);
            Assert.That(next, Is.EqualTo(1));
            Assert.That(speed.DroppedTicksLastFrame, Is.Zero);

            // 保底：预算为 0（极端超预算）时上限仍为 1，照跑 1 个 tick。
            SpeedController tight = new SpeedController();
            int floorTicks = tight.PlanMotionTicks(FrameSeconds, 0.0, 0.0, MotionRate);
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
        /// 默认手动 1× 与迁移前的固定累加器逐帧一致：60 fps 附近抖动的确定性帧序列下，
        /// 「每帧跑几个运动 tick」的序列完全相同（倍速的默认档不得改变现状行为）。
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

                int planned = speed.PlanMotionTicks(dt, legacyTicks * 0.3, BudgetMs, MotionRate);
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
                int ticks = speed.PlanMotionTicks(FrameSeconds, 0.0, tightBudgetMs, MotionRate);
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
                int ticks = speed.PlanMotionTicks(FrameSeconds, 0.0, BudgetMs, MotionRate);
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
                steady.PlanMotionTicks(FrameSeconds, 0.0, BudgetMs, MotionRate);
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

                Assert.That(jittered.PlanMotionTicks(dt, legacyTicks * 0.3, BudgetMs, MotionRate), Is.EqualTo(legacyTicks));
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
                speed.PlanMotionTicks(FrameSeconds, simMs, BudgetMs, MotionRate);

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
    }
}
