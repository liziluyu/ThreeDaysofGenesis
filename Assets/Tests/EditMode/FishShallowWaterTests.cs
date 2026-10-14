using System.Text;
using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 鱼在**浅水**里的竖直 / 水平行为回归：贴底水深阈、锁潜（Schmitt 滞回）的零交替、开阔池巡游方向
    /// 不被竖直逻辑扰动、狭池带坎的「左右抽搐」抑制，以及确定性重放 / 世界随机流隔离。
    /// </summary>
    /// <remarks>
    /// <para><b>身体口径：</b>鱼是 1 × 0.5 格的碰撞盒、<see cref="Creature.X"/> / <see cref="Creature.Y"/> 是碰撞盒
    /// **底边中点**；可见身体铺在世界行 <c>floor(Y) + 0 .. floor(Y) + 3</c>（= <see cref="CreatureArtData.Fish"/>
    /// 的 <c>MaxOpaqueRow − PivotRowPixels</c> = 8 − 5 = 3；尸体物质化与渲染同口径）。「整条没入水下」=
    /// 身体顶行 <c>floor(Y) + 3</c> ≤ 水面之下的最后一行水。</para>
    /// <para><b>本文件是「先复现后修」的产物。</b>修复前（旧 FishBrain：每思考帧拿「盒中心上方 8 格内有无非水」
    /// 实时比较 + 无条件 1/3 摇骰换向）四个用例全红，600 逻辑帧的实测轨迹摘要：</para>
    /// <list type="bullet">
    /// <item><b>开阔池 d = 9</b>：竖直意图换号 <b>50</b> 次，第 101 帧起逐思考帧在 <c>Y = 3.424 / 3.760</c> 之间来回
    /// （<c>IntentVy</c> 交替 <c>+0.1 / −0.1</c>），且 <c>groundedFrames = 0</c>（从未真正贴地）——
    /// 「下潜带」与「近底上浮带」在只剩一格的间隔上重叠导致的双带振荡；</item>
    /// <item><b>开阔池 d = 10</b>：同型，换号 <b>52</b> 次，<c>Y</c> 在 <c>4.724 / 5.060</c> 之间来回；</item>
    /// <item><b>开阔池 d = 5 / 6 / 7</b>：竖直换号 0 次（旧 8 格带在这三档恒真 ⇒ 一路压到池底，<c>endY = 3.0</c>），
    /// 但朝向每 600 帧翻转 <b>12</b> 次——与池形无关的纯骰子（每思考帧 1/3 概率摇、摇中再 1/2 概率换向）；</item>
    /// <item><b>狭池带坎 d = 5</b>：朝向翻转 <b>21</b> 次 = 12 次随机换向 + 9 次「贴底爬行撞上一格坎掉头」
    /// （鱼 <c>CanStepUp = false</c>，旧规则把浅水鱼恒压池底，池底每个一格坎都是一次掉头）。</item>
    /// </list>
    /// <para><b>主导机制判定：</b>竖直侧是<b>双带振荡</b>（d ∈ {9,10}，硬指标 50 / 52 次换号）——「触底 / 触面不断切换」
    /// 即此；水平侧是<b>1/3 纯随机换向</b>（所有池形都恰好 12 次 / 600 帧的基线），狭池带坎在此基础上叠加
    /// <b>撞坎掉头</b>（+9 次）。两条水平机制都有证据，因此两条都修：墙感知换向（去掉随机抽）＋
    /// 「锁潜 + 贴地 ⇒ 停住水平意图」的浅水伏底（去掉贴底爬行踩坎）。</para>
    /// <para><b>修复后同一批轨迹（本文件全绿）：</b>竖直换号 d = 5/6/9/10 均为 <b>0</b>（后 300 帧恒 0 或恒 −VerticalSpeed），
    /// d = 7 全程只有 1 次换号（下潜到池底 → 解锁 → 抬离，随后恒 0，<c>endY = 4.784</c> = 池底 + 1.78）；
    /// <see cref="Creature.Facing"/> 翻转在**每一档**都是 <b>0</b> 次（含狭池带坎 21 → 0）；d = 5/6 与狭池的
    /// <c>endIntentVx = 0</c>（浅水伏底，不再爬行）。</para>
    /// <para><b>为什么用 320×180 的隔离世界：</b>开阔池铺满整幅世界宽度，鱼从池中央出发 600 帧最多横移 30 格，
    /// 永远碰不到世界边界——竖直 / 水平两根轴因此互不污染，churn 只能来自鱼自己的决策。</para>
    /// </remarks>
    public sealed class FishShallowWaterTests
    {
        /// <summary>世界宽（格）：开阔池铺满整幅宽度，600 帧内鱼碰不到边界。</summary>
        private const int W = 320;

        /// <summary>世界高（格）：浅池 + 足够空气层，鱼从高处落水也够用。</summary>
        private const int H = 180;

        /// <summary>池底石头占行 <c>0 .. BottomRow − 1</c>，水从 <see cref="BottomRow"/> 行开始往上灌。</summary>
        private const int BottomRow = 3;

        /// <summary>回归世界的固定 seed（与 <c>CreatureAiTests</c> 同一条 xorshift32 流，仅取值不同）。</summary>
        private const uint SeedValue = 20261014u;

        /// <summary>每次跑的逻辑帧数。</summary>
        private const int Frames = 600;

        /// <summary>「后 300 帧」的窗口长度：此时鱼必然已经沉降到稳态。</summary>
        private const int TailFrames = 300;

        /// <summary>狭池用例的池宽（格）：≤ 12，让鱼几百帧内必然撞上坎或壁。</summary>
        private const int TankWidth = 12;

        /// <summary>狭池左壁 = <see cref="TankLeft"/> − 1，内腔列 <c>TankLeft .. TankLeft + TankWidth − 1</c>。</summary>
        private const int TankLeft = 100;

        /// <summary>沙坎在内腔中的列偏移（一格高差，正好顶在鱼的贴底身体行上）。</summary>
        private const int RidgeOffset = 6;

        /// <summary>
        /// **修复前实测**：狭池带坎用例 600 帧内的 <see cref="Creature.Facing"/> 翻转次数（2026-10-14 复现轨迹
        /// <c>facingFlips=21</c>：12 次纯随机换向 + 9 次贴底爬行撞坎掉头）。修复后应 ≤ 它的一半。
        /// </summary>
        private const int NarrowTankFlipsBeforeFix = 21;

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Sand
        {
            get { return ParticleIds.Sand; }
        }

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        // ============================== 脚手架 ==============================

        private static void FillRect(ParticleWorld world, int x0, int x1, int y0, int y1, ushort typeId)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    world.Set(x, y, typeId);
                }
            }
        }

        /// <summary>开阔池：全世界铺石底（行 0..BottomRow−1），其上 <paramref name="depth"/> 格水，再上是空气。</summary>
        private static ParticleWorld OpenPool(int depth)
        {
            ParticleWorld world = new ParticleWorld(W, H, SeedValue);
            FillRect(world, 0, W - 1, 0, BottomRow - 1, Stone);
            FillRect(world, 0, W - 1, BottomRow, BottomRow + depth - 1, Water);
            return world;
        }

        /// <summary>水柱的「池中央」Y（底行 <see cref="BottomRow"/> 与最上一行水的中间）。</summary>
        private static float PoolCenterY(int depth)
        {
            return BottomRow + (depth - 1) * 0.5f;
        }

        /// <summary>狭池 + 池底一格高差的沙坎：两堵石壁把水封死在内腔，坎在池宽 6 格处。</summary>
        private static ParticleWorld NarrowTankWithRidge(int depth)
        {
            ParticleWorld world = new ParticleWorld(W, H, SeedValue);
            FillRect(world, 0, W - 1, 0, BottomRow - 1, Stone);
            FillRect(world, TankLeft - 1, TankLeft - 1, BottomRow, BottomRow + depth + 1, Stone);
            FillRect(world, TankLeft + TankWidth, TankLeft + TankWidth, BottomRow, BottomRow + depth + 1, Stone);
            FillRect(world, TankLeft, TankLeft + TankWidth - 1, BottomRow, BottomRow + depth - 1, Water);
            world.Set(TankLeft + RidgeOffset, BottomRow, Sand);
            return world;
        }

        /// <summary>身体顶点世界行（= floor(Y) + 图案顶相对碰撞盒底边的偏移），渲染 / 尸体同口径。</summary>
        private static int BodyTopRow(FishCreature fish)
        {
            return FloorToInt(fish.Y) + CreatureArtData.Fish.MaxOpaqueRow - CreatureArtData.Fish.PivotRowPixels;
        }

        /// <summary>向下取整（不引 Mathf，与模拟层口径一致）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        /// <summary>
        /// 跑 <paramref name="frames"/> 个逻辑帧，每帧记录一根轨迹；每个思考帧额外打一行 <c>TRACE|</c>
        /// 到测试日志（存活下来的证据：修复前用它定位机制，修复后它是回归说明）。
        /// </summary>
        private static FrameLog RunAndLog(
            ParticleWorld world, CreatureWorld creatures, FishCreature fish, int frames, string label)
        {
            FrameLog log = new FrameLog(frames);
            StringBuilder trace = new StringBuilder();
            trace.Append("TRACE|").Append(label).Append("|tick,Y,Vy,Vx,Facing,Grounded,Submerged,Latched\n");
            for (int i = 0; i < frames; i++)
            {
                bool thinking = creatures.LogicTicks % Creature.ThinkEvery == fish.SensePhase;
                creatures.Step(world);
                log.Capture(i, fish);
                if (thinking)
                {
                    trace.Append("TRACE|").Append(label)
                        .Append("|").Append(creatures.LogicTicks)
                        .Append(",").Append(fish.Y.ToString("0.0000"))
                        .Append(",").Append(fish.IntentVy.ToString("0.0000"))
                        .Append(",").Append(fish.IntentVx.ToString("0.0000"))
                        .Append(",").Append(fish.Facing)
                        .Append(",").Append(fish.IsGrounded ? 1 : 0)
                        .Append(",").Append(fish.IsSubmerged ? 1 : 0)
                        .Append(",").Append(fish.VerticalDiveLatched ? 1 : 0)
                        .Append('\n');
                }
            }

            TestContext.WriteLine(trace.ToString());
            TestContext.WriteLine(log.Summarize(label));
            return log;
        }

        /// <summary>逐逻辑帧的鱼轨迹快照 + 统计（交替次数 / 翻转次数都在这里算，避免断言里塞循环）。</summary>
        private sealed class FrameLog
        {
            private readonly int _frames;
            private readonly float[] _y;
            private readonly float[] _vy;
            private readonly float[] _vx;
            private readonly int[] _facing;
            private readonly bool[] _grounded;
            private readonly bool[] _submerged;
            private readonly bool[] _latched;

            public FrameLog(int frames)
            {
                _frames = frames;
                _y = new float[frames];
                _vy = new float[frames];
                _vx = new float[frames];
                _facing = new int[frames];
                _grounded = new bool[frames];
                _submerged = new bool[frames];
                _latched = new bool[frames];
            }

            public void Capture(int index, FishCreature fish)
            {
                _y[index] = fish.Y;
                _vy[index] = fish.IntentVy;
                _vx[index] = fish.IntentVx;
                _facing[index] = fish.Facing;
                _grounded[index] = fish.IsGrounded;
                _submerged[index] = fish.IsSubmerged;
                _latched[index] = fish.VerticalDiveLatched;
            }

            /// <summary>竖直意图换号次数：把 0 当「无方向」，比较相邻两个**非零**符号是否相反（−→0→+ 记 1 次）。</summary>
            public int VerticalReversals(int from, int to)
            {
                int reversals = 0;
                int lastSign = 0;
                for (int i = from; i < to; i++)
                {
                    int sign = _vy[i] > 0f ? 1 : (_vy[i] < 0f ? -1 : 0);
                    if (sign == 0)
                    {
                        continue;
                    }

                    if (lastSign != 0 && sign != lastSign)
                    {
                        reversals++;
                    }

                    lastSign = sign;
                }

                return reversals;
            }

            /// <summary><see cref="Creature.Facing"/> 在线上的翻转次数。</summary>
            public int FacingFlips(int from, int to)
            {
                int flips = 0;
                for (int i = from + 1; i < to; i++)
                {
                    if (_facing[i] != _facing[i - 1])
                    {
                        flips++;
                    }
                }

                return flips;
            }

            /// <summary>窗口内竖直意图是否恒等于 <paramref name="value"/>（逐位相等，不做容差）。</summary>
            public bool AllIntentVyEqual(int from, int to, float value)
            {
                for (int i = from; i < to; i++)
                {
                    if (_vy[i] != value)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>窗口内每一帧身体是否整条没入水下（身体顶行 ≤ <paramref name="lastWaterRow"/>）。</summary>
            public bool AllBodyUnderWater(int from, int to, int lastWaterRow)
            {
                // 与 BodyTopRow 同一口径：体顶 = floor(Y) + (MaxOpaqueRow − PivotRowPixels)。
                int bodyTopOffset = CreatureArtData.Fish.MaxOpaqueRow - CreatureArtData.Fish.PivotRowPixels;
                for (int i = from; i < to; i++)
                {
                    if (FloorToInt(_y[i]) + bodyTopOffset > lastWaterRow)
                    {
                        return false;
                    }
                }

                return true;
            }

            public int GroundedFrames(int from, int to)
            {
                int count = 0;
                for (int i = from; i < to; i++)
                {
                    if (_grounded[i])
                    {
                        count++;
                    }
                }

                return count;
            }

            public int SubmergedFrames(int from, int to)
            {
                int count = 0;
                for (int i = from; i < to; i++)
                {
                    if (_submerged[i])
                    {
                        count++;
                    }
                }

                return count;
            }

            /// <summary>窗口内锁存位是否恒为 <paramref name="value"/>。</summary>
            public bool AllLatchedEqual(int from, int to, bool value)
            {
                for (int i = from; i < to; i++)
                {
                    if (_latched[i] != value)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>一行摘要：交替 / 翻转 / 贴底 / 淹没 / 终态——修复前后的对照表就取自这里。</summary>
            public string Summarize(string label)
            {
                return string.Concat(
                    "SUMMARY|", label,
                    "|vyReversals=", VerticalReversals(0, _frames),
                    "|vyReversalsTail=", VerticalReversals(_frames - TailFrames, _frames),
                    "|facingFlips=", FacingFlips(0, _frames),
                    "|facingFlipsTail=", FacingFlips(_frames - TailFrames, _frames),
                    "|groundedFrames=", GroundedFrames(0, _frames),
                    "|submergedFrames=", SubmergedFrames(0, _frames),
                    "|endY=", _y[_frames - 1].ToString("0.0000"),
                    "|endIntentVy=", _vy[_frames - 1].ToString("0.0000"),
                    "|endIntentVx=", _vx[_frames - 1].ToString("0.0000"),
                    "|endFacing=", _facing[_frames - 1],
                    "|endLatched=", _latched[_frames - 1] ? 1 : 0);
            }
        }

        // ============================== 场景 A：开阔池 ==============================

        /// <summary>
        /// **贴底档（d ≤ 6）**：鱼在开阔浅池里一旦触面就永久锁潜，一路贴底 —— 后 300 帧竖直意图恒为
        /// −<see cref="FishBrain.VerticalSpeed"/>（零交替），且贴底后不再爬行（水平意图被归零 ⇒ 朝向不再翻转）。
        /// </summary>
        /// <remarks>修复前：d = 5/6 竖直意图虽然也恒为 −0.1（旧 8 格带恒真、直接贴底），但朝向每 600 帧翻转约 10 次
        /// （1/3 纯随机换向）；修复后朝向翻转数为 0。</remarks>
        [TestCase(5)]
        [TestCase(6)]
        public void OpenShallowPool_LocksDiveAndPinsToBottom(int depth)
        {
            ParticleWorld world = OpenPool(depth);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, W * 0.5f + 0.5f, PoolCenterY(depth));

            FrameLog log = RunAndLog(world, creatures, fish, Frames, "open depth=" + depth);

            Assert.IsTrue(fish.Alive, "浅池里的鱼不该死");
            Assert.IsTrue(fish.VerticalDiveLatched, "d ≤ 6 的池摆不下「身体 + 3 格解锁余量」⇒ 锁存位应保持为真");
            Assert.IsTrue(log.AllLatchedEqual(Frames - TailFrames, Frames, true), "后 300 帧锁存位应恒为真（永久锁潜）");
            Assert.AreEqual(
                0,
                log.VerticalReversals(0, Frames),
                "竖直意图不该换号：旧 8 格带在浅水里的「触底 / 触面」双带振荡会逐思考帧在 ±VerticalSpeed 之间交替");
            Assert.IsTrue(
                log.AllIntentVyEqual(Frames - TailFrames, Frames, -FishBrain.VerticalSpeed),
                "锁潜后竖直意图应恒为 −VerticalSpeed（后 300 帧零交替）");
            Assert.AreEqual(
                0,
                log.FacingFlips(Frames - TailFrames, Frames),
                "贴底伏底的鱼不该再爬行换向（左右抽搐）");
            Assert.LessOrEqual(fish.Y, BottomRow + 0.05f, "d ≤ 6 的鱼应一路贴底（池底石头之上）");
        }

        /// <summary>
        /// **稳态休息档（d ≥ 7）**：鱼后 300 帧竖直意图恒为 0（锁潜解锁后中性浮力保持深度），
        /// 身体整条没入水面之下，且不再贴底。
        /// </summary>
        /// <remarks>修复前：d = 7/9/10 都被旧 8 格带推着一路贴底，随后在池底附近逐思考帧交替
        /// （d = 9/10 的双带振荡）；d = 9 从池中央出发时旧规则先深潜到池底再振荡，修复后原地
        /// 保持深度（体顶 + 1 格就是水 ⇒ 不触发下潜）。</remarks>
        [TestCase(7)]
        [TestCase(9)]
        public void OpenDeeperPool_RestsWithoutVerticalIntent(int depth)
        {
            ParticleWorld world = OpenPool(depth);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, W * 0.5f + 0.5f, PoolCenterY(depth));

            FrameLog log = RunAndLog(world, creatures, fish, Frames, "open depth=" + depth);

            int lastWaterRow = BottomRow + depth - 1;
            Assert.IsTrue(fish.Alive, "开阔池里的鱼不该死");
            Assert.IsFalse(fish.VerticalDiveLatched, "稳态休息区里锁存位应为假（已解锁）");
            Assert.IsTrue(
                log.AllIntentVyEqual(Frames - TailFrames, Frames, 0f),
                "进入稳态休息区后竖直意图应恒为 0（不再上浮 / 下潜交替）");
            Assert.IsTrue(log.AllBodyUnderWater(0, Frames, lastWaterRow), "鱼身体应始终整条没入水面之下");
            Assert.AreEqual(0, log.FacingFlips(Frames - TailFrames, Frames), "巡游方向不该被竖直逻辑扰动");
        }

        /// <summary>
        /// **开阔池 d = 10**：稳定后竖直意图恒 0，且后 300 帧 <see cref="Creature.Facing"/> 一次都不翻——
        /// 前方开阔时换向逻辑不摇骰（墙感知），游向因此不被随机换向扰动。
        /// </summary>
        /// <remarks>修复前：旧规则在这里是双带振荡（cy 在池底附近 4/5 两格间来回）＋ 1/3 纯随机换向，
        /// 后 300 帧朝向翻转约 5 次。</remarks>
        [Test]
        public void OpenPool_Depth10_HoldsDepthAndHeading()
        {
            const int depth = 10;
            ParticleWorld world = OpenPool(depth);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, W * 0.5f + 0.5f, PoolCenterY(depth));

            FrameLog log = RunAndLog(world, creatures, fish, Frames, "open depth=" + depth);

            Assert.IsTrue(fish.Alive, "开阔池里的鱼不该死");
            Assert.IsFalse(fish.VerticalDiveLatched, "d = 10 的稳态位置体顶之上水量充足 ⇒ 不该锁潜");
            Assert.IsTrue(
                log.AllIntentVyEqual(Frames - TailFrames, Frames, 0f),
                "d = 10 的稳态竖直意图应为 0（旧规则在此逐思考帧上下交替）");
            Assert.AreEqual(
                0,
                log.FacingFlips(Frames - TailFrames, Frames),
                "开阔水域前方畅通 ⇒ 保持原方向不摇骰，朝向不该翻转");
            Assert.IsTrue(log.AllBodyUnderWater(0, Frames, BottomRow + depth - 1), "鱼身体应始终整条没入水面之下");
        }

        // ============================== 场景 B：狭池 + 池底沙坎 ==============================

        /// <summary>
        /// **狭池带坎（d = 5，池宽 12）**：鱼贴底伏底后不再爬行 ⇒ 600 帧内的朝向翻转次数掉到修复前的一半以下，
        /// 且始终存活（撞坎掉头 / 贴底爬行不再把它来回抽）。
        /// </summary>
        /// <remarks>修复前实测值见 <see cref="NarrowTankFlipsBeforeFix"/>：1/3 随机换向（约 10 次）叠加
        /// 「贴底爬行撞上一格坎 → 掉头」（鱼 <c>CanStepUp = false</c>，池底每个一格坎都是一次掉头）。</remarks>
        [Test]
        public void NarrowShallowTank_WithRidge_StopsLeftRightChurn()
        {
            const int depth = 5;
            ParticleWorld world = NarrowTankWithRidge(depth);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, TankLeft + 2.5f, BottomRow + 1.5f);

            FrameLog log = RunAndLog(world, creatures, fish, Frames, "narrow tank depth=" + depth);

            int flips = log.FacingFlips(0, Frames);
            Assert.IsTrue(fish.Alive, "狭池浅水里的鱼不该被抽死 / 搁浅死");
            Assert.IsTrue(fish.VerticalDiveLatched, "d = 5 的狭池同样摆不下身体 + 3 格余量 ⇒ 应永久锁潜");
            Assert.LessOrEqual(
                flips,
                NarrowTankFlipsBeforeFix / 2,
                "修复后朝向翻转次数应 ≤ 修复前实测值的一半（修复前 = " + NarrowTankFlipsBeforeFix + "）");
            Assert.AreEqual(0, log.FacingFlips(Frames - TailFrames, Frames), "伏底后朝向应彻底冻结");
        }

        // ============================== 确定性与随机流隔离 ==============================

        /// <summary>同 seed、同操作序列的两个浅池世界里，鱼 300 帧内逐帧逐字段一致（可重放）。</summary>
        [Test]
        public void Determinism_SameSeed_ShallowPoolReplayBitExact()
        {
            const int depth = 5;
            ParticleWorld worldA = OpenPool(depth);
            ParticleWorld worldB = OpenPool(depth);
            CreatureWorld creaturesA = new CreatureWorld(SeedValue);
            CreatureWorld creaturesB = new CreatureWorld(SeedValue);
            FishCreature fishA = creaturesA.Spawn(new FishCreature(), worldA, W * 0.5f + 0.5f, PoolCenterY(depth));
            FishCreature fishB = creaturesB.Spawn(new FishCreature(), worldB, W * 0.5f + 0.5f, PoolCenterY(depth));

            for (int i = 0; i < 300; i++)
            {
                creaturesA.Step(worldA);
                creaturesB.Step(worldB);
                Assert.AreEqual(fishA.X, fishB.X, "X 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.Y, fishB.Y, "Y 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.VelocityX, fishB.VelocityX, "VelocityX 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.VelocityY, fishB.VelocityY, "VelocityY 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.IntentVx, fishB.IntentVx, "IntentVx 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.IntentVy, fishB.IntentVy, "IntentVy 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.Facing, fishB.Facing, "Facing 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.IsGrounded, fishB.IsGrounded, "IsGrounded 应逐位一致（帧 " + i + "）");
                Assert.AreEqual(fishA.IsSubmerged, fishB.IsSubmerged, "IsSubmerged 应逐位一致（帧 " + i + "）");
            }
        }

        /// <summary>
        /// 一条鱼关在**封闭浅水舱**里跑 300 帧：世界随机流的 64 次抽样与同 seed 的「无动物」世界逐位一致
        /// （动物层绝不消耗世界随机流）——鱼的墙感知换向 / 锁潜只读世界，不摇世界骰。
        /// </summary>
        [Test]
        public void RngIsolation_FishInShallowTank_DoesNotConsumeWorldStream()
        {
            ParticleWorld solo = NewSealedTank();
            ParticleWorld withFish = NewSealedTank();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), withFish, TankSealLeft + 2.5f, BottomRow + 1.5f);

            for (int i = 0; i < 300; i++)
            {
                solo.StepLogic();
                withFish.StepLogic();
                creatures.Step(withFish);
            }

            Assert.IsTrue(fish.Alive, "封闭水舱里的鱼不该死（不搁浅、不溺死）");
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(solo.NextUInt(), withFish.NextUInt(), "动物层不该消耗世界随机流");
            }
        }

        /// <summary>封闭浅水舱的左壁列（内腔右界见 <see cref="TankSealRight"/>）。</summary>
        private const int TankSealLeft = 20;

        /// <summary>封闭浅水舱的右壁列。</summary>
        private const int TankSealRight = 40;

        /// <summary>
        /// 封闭浅水舱：石底 + 石盖 + 两堵石壁把 5 格水（行 BottomRow..BottomRow+4）封在 x ∈ [21, 39] 内。
        /// 水不会流走、鱼永远淹没（搁浅门打不开），窗口内也无生物质产生 ⇒ 世界随机流两边同源。
        /// </summary>
        private static ParticleWorld NewSealedTank()
        {
            const int depth = 5;
            ParticleWorld world = new ParticleWorld(W, H, SeedValue);
            FillRect(world, 0, W - 1, 0, BottomRow - 1, Stone);
            FillRect(world, TankSealLeft, TankSealRight, BottomRow + depth, BottomRow + depth, Stone);
            FillRect(world, TankSealLeft, TankSealLeft, BottomRow, BottomRow + depth, Stone);
            FillRect(world, TankSealRight, TankSealRight, BottomRow, BottomRow + depth, Stone);
            FillRect(world, TankSealLeft + 1, TankSealRight - 1, BottomRow, BottomRow + depth - 1, Water);
            return world;
        }
    }
}
