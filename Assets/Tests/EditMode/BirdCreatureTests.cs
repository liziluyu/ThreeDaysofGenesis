using System.Collections.Generic;
using NUnit.Framework;
using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 第三物种**鸟**（<see cref="BirdCreature"/> / <see cref="BirdBrain"/> / <see cref="CreatureTerrain.IsPerchable"/> /
    /// <see cref="CreatureSensors.ProbeSurfaceBelow"/> 与 <see cref="Creature"/> 的两道飞行虚门）的测试：
    /// 按设计契约 <c>docs/plan-bird-ai.md</c> §8 的 1-13 条逐条落点——空中悬停、天花板压制、落水自救、
    /// 栖在叶顶、水平飞穿树冠、贴地起飞；同 seed 逐位重放、世界随机流隔离、2 Hz 门控与相位错峰、
    /// 栖息计时起飞、火进 Flee（方向 / 滞回 / 最短时长）、思考帧外零 RNG 消耗；死亡物质化格数与竖直锚。
    /// </summary>
    /// <remarks>
    /// <para>兔 / 鱼回归由既有 <c>CreatureAiTests</c> / <c>CreatureWorldTests</c> / <c>CreatureBiomassTests</c>
    /// 全绿证明（本类只跑鸟，不动既有行为）。</para>
    /// <para>地形走生产注册路径（<see cref="ParticleRegistration.RegisterAll"/>）以便按 <see cref="ParticleIds"/> 常量摆放；
    /// 为避免粒子世界演化干扰（火熄灭 / 水流走），除隔离用例外的多数用例只调 <c>creatures.Step(world)</c> 而不调
    /// <c>world.StepLogic()</c>——动物层本就只读世界。</para>
    /// </remarks>
    public sealed class BirdCreatureTests
    {
        private const int W = 48;
        private const int H = 64;
        private const uint SeedValue = 20261011u;

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort Fire
        {
            get { return ParticleIds.Fire; }
        }

        private static ushort TreeLeaf
        {
            get { return ParticleIds.TreeLeaf; }
        }

        private static ushort Biomass
        {
            get { return ParticleIds.Biomass; }
        }

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>帧选择用例造的假纹理：用例间清理，避免泄漏。</summary>
        private readonly List<Texture2D> _frameTextures = new List<Texture2D>();

        [TearDown]
        public void TearDown()
        {
            foreach (Texture2D texture in _frameTextures)
            {
                Object.DestroyImmediate(texture);
            }

            _frameTextures.Clear();
        }

        // ============================== 共用脚手架 ==============================

        private static ParticleWorld NewWorld()
        {
            return new ParticleWorld(W, H, SeedValue);
        }

        private static void FillRows(ParticleWorld world, int y0, int y1, ushort typeId)
        {
            FillRect(world, 0, world.Width - 1, y0, y1, typeId);
        }

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

        /// <summary>连跑 <paramref name="count"/> 个逻辑帧（不推进粒子世界，地形 / 火 / 水保持静止）。</summary>
        private static void RunSteps(ParticleWorld world, CreatureWorld creatures, int count)
        {
            for (int i = 0; i < count; i++)
            {
                creatures.Step(world);
            }
        }

        /// <summary>感知扫描中心所在的行（<c>floor(Y + Height/2)</c>，与 <see cref="BirdBrain.Think"/> 同一口径）。</summary>
        private static int BodyCenterRow(Creature creature)
        {
            return Mathf.FloorToInt(creature.Y + creature.Height * 0.5f);
        }

        /// <summary>统计全世界里某 typeId 的格数。</summary>
        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>轮廓非空格总数。</summary>
        private static int NonEmptyCount(sbyte[] cells)
        {
            int count = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static void AssertCreatureEqual(Creature a, Creature b, string context)
        {
            Assert.AreEqual(a.X, b.X, $"{context} X 应逐位一致");
            Assert.AreEqual(a.Y, b.Y, $"{context} Y 应逐位一致");
            Assert.AreEqual(a.VelocityX, b.VelocityX, $"{context} VelocityX 应逐位一致");
            Assert.AreEqual(a.VelocityY, b.VelocityY, $"{context} VelocityY 应逐位一致");
            Assert.AreEqual(a.AiState, b.AiState, $"{context} AiState 应逐位一致");
            Assert.AreEqual(a.StateTicksLeft, b.StateTicksLeft, $"{context} StateTicksLeft 应逐位一致");
            Assert.AreEqual(a.Facing, b.Facing, $"{context} Facing 应逐位一致");
            Assert.AreEqual(a.SubmergedTicks, b.SubmergedTicks, $"{context} SubmergedTicks 应逐位一致");
            Assert.AreEqual(a.IsGrounded, b.IsGrounded, $"{context} IsGrounded 应逐位一致");
        }

        /// <summary>无大脑的探针实体：暴露 protected 的 <see cref="Creature.ShouldThink"/>（门控用例用）。</summary>
        private sealed class ProbeCreature : Creature
        {
            public ProbeCreature()
                : base(1f, 1f)
            {
            }

            protected override float Gravity
            {
                get { return 0f; }
            }

            protected override float MaxFallSpeed
            {
                get { return 1f; }
            }

            public override string DebugName
            {
                get { return "探针"; }
            }

            public override Color32 PlaceholderColor
            {
                get { return new Color32(1, 2, 3, 255); }
            }

            public bool ExposeShouldThink(CreatureWorld creatures)
            {
                return ShouldThink(creatures);
            }
        }

        // ============================== 1. 空中悬停 ==============================

        /// <summary>空中栖息（不摇起飞计时）的鸟 300 帧内竖直位移 ≤ 0.5 格——飞行挂起重力，不会被动坠落。</summary>
        [Test]
        public void Hover_InAir_StaysAtAltitudeWithinHalfCell()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 30f);
            bird.StateTicksLeft = 100000; // 抑制起飞，保持 Perched 悬停

            float y0 = bird.Y;
            RunSteps(world, creatures, 300);

            Assert.LessOrEqual(Mathf.Abs(bird.Y - y0), 0.5f, "空中悬停不该被动坠落");
            Assert.IsTrue(bird.Alive);
        }

        // ============================== 2. 天花板压制 ==============================

        /// <summary>出生在天花板余量上方的鸟会被压回高度带内（<c>Y ≤ 世界高 − CeilingMarginCells</c>）。</summary>
        [Test]
        public void SpawnedAboveCeiling_IsPushedBackIntoAltitudeBand()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 60f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000; // 不进入降落分支

            RunSteps(world, creatures, 300);

            Assert.Less(bird.Y, 60f, "应被下压");
            Assert.LessOrEqual(
                bird.Y, (H - BirdBrain.CeilingMarginCells) + 0.5f, "应回到高度带内（世界高 − 天花板余量）");
        }

        // ============================== 3. 落水自救 ==============================

        /// <summary>落水的鸟靠正浮力自动上浮出水、不出水不死（<c>SubmergedTicks</c> 清零）。</summary>
        [Test]
        public void Submerged_RisesOutOfWater_AndSurvives()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 1, Stone);
            FillRect(world, 8, 40, 2, 14, Water);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 5f);

            RunSteps(world, creatures, 400);

            Assert.IsTrue(bird.Alive, "落水自救不该溺死");
            Assert.IsFalse(bird.IsSubmerged, "应已上浮出水");
            Assert.AreEqual(0, bird.SubmergedTicks, "出水后淹没计时应清零");
        }

        // ============================== 4. 停在叶顶 ==============================

        /// <summary>巡航计时到点且正下方有树冠 ⇒ 停在叶顶（IsGrounded、不再下掉）。</summary>
        [Test]
        public void LandingTimerExpired_WithCanopyBelow_PerchesOnLeafTop()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 4, 44, 12, 12, TreeLeaf); // 一行树冠（叶在行 12，顶面 ⇒ 脚底停在 13）

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 20f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 0; // 立即尝试降落

            RunSteps(world, creatures, 120);

            Assert.IsTrue(bird.IsGrounded, "应停在叶顶");
            Assert.AreEqual(BirdBrain.StatePerched, bird.AiState, "落地后应转入栖息");
            Assert.AreEqual(13f, bird.Y, 1e-3f, "叶在第 12 行 ⇒ 脚底应停在 13");

            RunSteps(world, creatures, 50); // 栖息计时下限 100 帧内不会起飞
            Assert.AreEqual(13f, bird.Y, 1e-3f, "栖息后应停住、不再下掉");
        }

        // ============================== 5. 水平飞穿树冠 ==============================

        /// <summary>树叶不挡水平：鸟水平飞穿一整片树冠不受阻（方向保持 + 位置真的越过树冠）。</summary>
        [Test]
        public void FlyingIntoCanopy_PassesThroughHorizontally()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 20, 30, 14, 15, TreeLeaf); // 面前的一堵树叶墙

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 5f, 14f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;
            bird.IntentVx = BirdBrain.FlySpeed;

            RunSteps(world, creatures, 500);

            Assert.Greater(bird.X, 30f, "应水平飞穿树冠");
            Assert.AreEqual(1, bird.Facing, "方向应保持向右");
            Assert.IsTrue(bird.Alive);
        }

        // ============================== 6. 贴地起飞 ==============================

        /// <summary>栖息计时的鸟（StateTicksLeft = 0）思考到点即竖直离地起飞。</summary>
        [Test]
        public void PerchedOnGround_TakesOffVertically()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 1f);

            RunSteps(world, creatures, 100);

            Assert.AreEqual(BirdBrain.StateFly, bird.AiState, "应已进入巡航");
            Assert.Greater(bird.Y, 3f, "应竖直离地");
        }

        // ============================== 7. 确定性重放 ==============================

        /// <summary>同 seed 的两个动物世界 + 同种子两个粒子世界，各放一只鸟跑 300 帧，逐位一致。</summary>
        [Test]
        public void Determinism_SameSeed_ReplayBitExact()
        {
            ParticleWorld wa = NewWorld();
            ParticleWorld wb = NewWorld();
            FillRows(wa, 0, 1, Stone);
            FillRows(wb, 0, 1, Stone);
            FillRect(wa, 8, 24, 2, 14, Water);
            FillRect(wb, 8, 24, 2, 14, Water);
            FillRect(wa, 30, 40, 20, 20, TreeLeaf);
            FillRect(wb, 30, 40, 20, 20, TreeLeaf);

            CreatureWorld ca = new CreatureWorld(SeedValue);
            CreatureWorld cb = new CreatureWorld(SeedValue);
            BirdCreature a = ca.Spawn(new BirdCreature(), wa, 16f, 24f);
            BirdCreature b = cb.Spawn(new BirdCreature(), wb, 16f, 24f);

            for (int i = 0; i < 300; i++)
            {
                wa.StepLogic();
                ca.Step(wa);
                wb.StepLogic();
                cb.Step(wb);
            }

            Assert.AreEqual(ca.Count, cb.Count, "同 seed 同操作下存活数应一致");
            AssertCreatureEqual(a, b, "鸟");
        }

        // ============================== 8. 世界随机流隔离 ==============================

        /// <summary>有 / 无鸟两种跑法各自推进 300 帧后，抽样同样次数的世界 <c>NextUInt</c> 必须逐位一致——
        /// 鸟若不偷摇一次世界骰，粒子流不会分叉。</summary>
        [Test]
        public void RngIsolation_BirdDoesNotConsumeWorldStream()
        {
            ParticleWorld solo = NewWorld();
            ParticleWorld withBird = NewWorld();
            FillRows(solo, 0, 0, Stone);
            FillRows(withBird, 0, 0, Stone);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), withBird, 24f, 30f);
            bird.StateTicksLeft = 100000; // 高空悬停，窗口内不死不移除

            for (int i = 0; i < 300; i++)
            {
                solo.StepLogic();
                withBird.StepLogic();
                creatures.Step(withBird);
            }

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(solo.NextUInt(), withBird.NextUInt(), "鸟不该消耗世界随机流");
            }
        }

        // ============================== 9. 思考门控与相位错峰 ==============================

        /// <summary>连生 3 只鸟相位为 0/1/2；门控只在 <c>LogicTicks % ThinkEvery == SensePhase</c> 的帧放行。</summary>
        [Test]
        public void ThinkPhase_StaggersBySpawnOrder_AndGateHitsOnlyOnPhaseFrame()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature b0 = creatures.Spawn(new BirdCreature(), world, 4f, 5f);
            BirdCreature b1 = creatures.Spawn(new BirdCreature(), world, 8f, 5f);
            BirdCreature b2 = creatures.Spawn(new BirdCreature(), world, 12f, 5f);

            Assert.AreEqual((byte)0, b0.SensePhase, "第 1 只鸟相位应为 0");
            Assert.AreEqual((byte)1, b1.SensePhase, "第 2 只鸟相位应为 1");
            Assert.AreEqual((byte)2, b2.SensePhase, "第 3 只鸟相位应为 2");

            ParticleWorld gateWorld = NewWorld();
            FillRows(gateWorld, 0, 0, Stone);
            CreatureWorld gate = new CreatureWorld(SeedValue);
            ProbeCreature probe = gate.Spawn(new ProbeCreature(), gateWorld, 5f, 1f);
            probe.SensePhase = 3;

            for (int tick = 0; tick < 30; tick++)
            {
                bool expected = tick % Creature.ThinkEvery == 3;
                Assert.AreEqual(expected, probe.ExposeShouldThink(gate), $"第 {tick} 逻辑帧的门控结果应命中相位 3");
                gate.Step(gateWorld);
            }
        }

        // ============================== 10. 栖息计时归零起飞 ==============================

        /// <summary>栖息计时未走完不起飞，走完后的下一个思考帧起飞。</summary>
        [Test]
        public void PerchTimer_ExpiryTriggersTakeOff()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 1f);
            bird.StateTicksLeft = 25;

            RunSteps(world, creatures, 20);
            Assert.AreEqual(BirdBrain.StatePerched, bird.AiState, "计时未走完不该起飞");

            RunSteps(world, creatures, 20);
            Assert.AreEqual(BirdBrain.StateFly, bird.AiState, "计时归零后应起飞");
        }

        // ============================== 11. 逃命 ==============================

        /// <summary>火进 Flee：方向远离火、竖直向上冲。</summary>
        [Test]
        public void FireThreat_EntersFlee_AwayFromFire_AndClimbs()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 20f);
            world.Set(34, 20, Fire); // 同排右侧，距离 10 < ThreatRadius(16)

            creatures.Step(world); // 相位 0 ⇒ 首帧即思考

            Assert.AreEqual(BirdBrain.StateFlee, bird.AiState, "火在圈内应进入逃命");
            Assert.Less(bird.IntentVx, 0f, "应朝火的**反方向**（向左）逃");
            Assert.Greater(bird.IntentVy, 0f, "应向上冲");
            Assert.Greater(bird.StateTicksLeft, 0, "进入时应写入最短逃命时长");
        }

        /// <summary>最短逃命时长内即使火出圈也保持 Flee；走完后回 Fly。</summary>
        [Test]
        public void Flee_LastsAtLeastFleeMinTicks_ThenReturnsToFly()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 20f);
            world.Set(34, 20, Fire);
            creatures.Step(world); // 进入 Flee

            world.Clear(34, 20); // 火出圈

            RunSteps(world, creatures, 30);
            Assert.AreEqual(BirdBrain.StateFlee, bird.AiState, "最短逃命时长内不该退出");

            RunSteps(world, creatures, 40);
            Assert.AreEqual(BirdBrain.StateFly, bird.AiState, "最短时长走完且火出圈后应回巡航");
        }

        /// <summary>火与鸟同列时只摇一次骰选边；期间保持同一逃跑方向不翻面。</summary>
        [Test]
        public void FireSameColumn_PicksSideOnce_AndKeepsDirection()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 20f);

            int fireX = -1;
            int fireY = -1;
            int sign = 0;

            for (int frame = 0; frame < 60; frame++)
            {
                if (creatures.LogicTicks % Creature.ThinkEvery == bird.SensePhase)
                {
                    // 把火钉在鸟**当前列**的正上方 5 格：保证「同列」条件在每个思考帧都成立。
                    if (fireX >= 0)
                    {
                        world.Clear(fireX, fireY);
                    }

                    fireX = Mathf.FloorToInt(bird.X);
                    fireY = BodyCenterRow(bird) + 5;
                    world.Set(fireX, fireY, Fire);
                }

                creatures.Step(world);

                if (bird.AiState == BirdBrain.StateFlee && bird.IntentVx != 0f)
                {
                    int current = bird.IntentVx > 0f ? 1 : -1;
                    if (sign == 0)
                    {
                        sign = current;
                    }
                    else
                    {
                        Assert.AreEqual(sign, current, "火始终同列时应保持既有逃跑方向，不该逐思考帧翻面");
                    }
                }
            }

            Assert.AreNotEqual(0, sign, "同列的鸟也应选出一侧逃跑（IntentVx ≠ 0）");
        }

        /// <summary>逃命脱离的滞回：火在 <c>ThreatRadius</c> 之外、<c>+FleeExitMargin</c> 之内仍保持 Flee，
        /// 超出放大半径后才回 Fly。</summary>
        [Test]
        public void Flee_Hysteresis_ExitsOnlyBeyondMargin()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 20f);

            // 先用近距离火进入 Flee。
            world.Set(30, 20, Fire);
            creatures.Step(world);
            Assert.AreEqual(BirdBrain.StateFlee, bird.AiState);

            int fireX = 30;
            int fireY = 20;

            // 火钉在距离 17（> 进入半径 16、≤ 脱离半径 19）⇒ 应保持 Flee。
            for (int frame = 0; frame < 100; frame++)
            {
                if (creatures.LogicTicks % Creature.ThinkEvery == bird.SensePhase)
                {
                    world.Clear(fireX, fireY);
                    fireX = Mathf.FloorToInt(bird.X) + 17;
                    fireY = BodyCenterRow(bird);
                    world.Set(fireX, fireY, Fire);
                }

                creatures.Step(world);
                Assert.AreEqual(BirdBrain.StateFlee, bird.AiState, "滞回窗内不该退出逃命");
            }

            // 火钉在距离 20（> 脱离半径 19）⇒ 应退出到 Fly。
            for (int frame = 0; frame < 30; frame++)
            {
                if (creatures.LogicTicks % Creature.ThinkEvery == bird.SensePhase)
                {
                    world.Clear(fireX, fireY);
                    fireX = Mathf.FloorToInt(bird.X) + 20;
                    fireY = BodyCenterRow(bird);
                    world.Set(fireX, fireY, Fire);
                }

                creatures.Step(world);
            }

            Assert.AreEqual(BirdBrain.StateFly, bird.AiState, "火超出放大半径后应回巡航");
        }

        // ============================== 12. 思考帧外零 RNG 消耗 ==============================

        /// <summary>
        /// 相位错开的鸟在**非思考帧**里不消耗自身随机流：跑 5 帧（无思考帧）后的下一次抽取，
        /// 与一个从未消耗随机流的空世界逐位一致；而多跑一帧命中思考帧后的下一次抽取则不同。
        /// </summary>
        [Test]
        public void Rng_IsConsumedOnlyOnThinkFrames()
        {
            // W1：鸟相位 5，跑 5 帧（帧 0..4 都不是思考帧）⇒ 随机流不该被动过。
            ParticleWorld worldNoThink = NewWorld();
            FillRows(worldNoThink, 0, 0, Stone);
            CreatureWorld noThink = new CreatureWorld(SeedValue);
            BirdCreature noThinkBird = noThink.Spawn(new BirdCreature(), worldNoThink, 24f, 20f);
            noThinkBird.SensePhase = 5;
            RunSteps(worldNoThink, noThink, 5);

            // W3：空世界（从未消耗随机流）的第一抽，作为基线。
            CreatureWorld untouched = new CreatureWorld(SeedValue);

            // W2：鸟相位 5，跑 6 帧（帧 5 命中思考帧 ⇒ 起飞要摇计时与方向）。
            ParticleWorld worldThink = NewWorld();
            FillRows(worldThink, 0, 0, Stone);
            CreatureWorld think = new CreatureWorld(SeedValue);
            BirdCreature thinkBird = think.Spawn(new BirdCreature(), worldThink, 24f, 20f);
            thinkBird.SensePhase = 5;
            RunSteps(worldThink, think, 6);

            uint baseline = untouched.NextUInt();
            uint afterNoThink = noThink.NextUInt();
            uint afterThink = think.NextUInt();

            Assert.AreEqual(baseline, afterNoThink, "思考帧外不得消耗随机流");
            Assert.AreNotEqual(afterNoThink, afterThink, "思考帧应消耗随机流（否则本用例没测到门控）");
        }

        // ============================== 13. 死亡物质化 ==============================

        /// <summary>鸟死亡物质化：写入数 == <see cref="CreatureArtData.Bird"/> 非空格数（值随美术走、不钉字面量），
        /// 且竖直锚在 <c>floor(Y)</c>（pivot = 0）。</summary>
        [Test]
        public void Death_MaterializesBirdProfile_AnchoredAtFloorY()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 30f);
            bird.Kill(CreatureDeathCause.Unknown);
            creatures.Step(world); // 清扫阶段物质化（鸟死前被跳过推进，位置即 30）

            sbyte[] cells = CreatureArtData.Bird.BiomassCells;
            Assert.AreEqual(0, BiomassProfile.Bird.PivotRowCells, "鸟切片 pivot = 0 ⇒ 竖直锚在 floor(Y)");

            int baseX = Mathf.FloorToInt(24f) - (BiomassProfile.Size / 2);
            int baseY = Mathf.FloorToInt(30f) - BiomassProfile.Bird.PivotRowCells;

            int written = 0;
            for (int dy = 0; dy < BiomassProfile.Size; dy++)
            {
                for (int dx = 0; dx < BiomassProfile.Size; dx++)
                {
                    if (cells[(dy * BiomassProfile.Size) + dx] < 0)
                    {
                        continue;
                    }

                    int x = baseX + dx;
                    int y = baseY + dy;
                    Assert.IsTrue(world.IsInBounds(x, y), $"轮廓格 ({dx},{dy}) 应落在界内");
                    Assert.AreEqual(Biomass, world.GetCell(x, y).typeId, $"轮廓格 ({dx},{dy}) 应写成生物质");
                    written++;
                }
            }

            // 轮廓格数不钉字面量（美术重绘就会变）——「美术有没有变」由 CreatureArtDataTests 的漂移对拍专门负责。
            Assert.AreEqual(NonEmptyCount(cells), written, "每个非空轮廓格都应写入");
            Assert.AreEqual(written, CountType(world, Biomass), "场上生物质格数应恰等于轮廓格数（无多余写入）");
        }

        // ============================== 14. 帧组选择（纯函数） ==============================

        /// <summary>造一个假帧 sprite（1×1 白纹理，仅用于引用比对；命名便于失败时的断言信息）。</summary>
        private Sprite NewFrame(string name)
        {
            var texture = new Texture2D(1, 1);
            _frameTextures.Add(texture);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0f, 0f), 1f);
            sprite.name = name;
            return sprite;
        }

        /// <summary>① 栖息态：帧选择只在收翅组内交替（永远不返回飞行 / 展翅帧）。</summary>
        [Test]
        public void BirdFrame_Perched_PlaysPerchGroup()
        {
            Sprite[] fly = { NewFrame("fly0"), NewFrame("fly1"), NewFrame("fly2"), NewFrame("fly3") };
            Sprite[] perch = { NewFrame("perch4"), NewFrame("perch6") };
            Sprite takeoff = NewFrame("takeoff5");

            var seen = new HashSet<Sprite>();
            for (int i = 0; i < 16; i++)
            {
                Sprite frame = CreatureRenderer.ResolveBirdFrame(
                    BirdBrain.StatePerched, 0f, i, fly, perch, takeoff);
                Assert.Contains(frame, perch, "栖息态应播收翅组帧");
                seen.Add(frame);
            }

            Assert.AreEqual(perch.Length, seen.Count, "收翅两片都应在栖息态交替出现（不是死盯第一片）");
        }

        /// <summary>② 飞行态：帧选择只在上排飞行组内循环。</summary>
        [Test]
        public void BirdFrame_Fly_PlaysFlyGroup()
        {
            Sprite[] fly = { NewFrame("fly0"), NewFrame("fly1"), NewFrame("fly2"), NewFrame("fly3") };
            Sprite[] perch = { NewFrame("perch4"), NewFrame("perch6") };
            Sprite takeoff = NewFrame("takeoff5");

            var seen = new HashSet<Sprite>();
            for (int i = 0; i < 8; i++)
            {
                Sprite frame = CreatureRenderer.ResolveBirdFrame(
                    BirdBrain.StateFly, 0f, i, fly, perch, takeoff);
                Assert.Contains(frame, fly, "飞行态应播上排飞行组帧");
                seen.Add(frame);
            }

            Assert.AreEqual(fly.Length, seen.Count, "上排 4 片都应循环出现");
        }

        /// <summary>③ Perched → Fly 沿：先定格展翅帧，过渡耗尽后进飞行组循环。</summary>
        [Test]
        public void BirdFrame_PerchedToFly_PlaysTakeoffThenFly()
        {
            const float duration = 0.3f;
            Sprite[] fly = { NewFrame("fly0"), NewFrame("fly1"), NewFrame("fly2"), NewFrame("fly3") };
            Sprite[] perch = { NewFrame("perch4"), NewFrame("perch6") };
            Sprite takeoff = NewFrame("takeoff5");

            // 沿那一帧起满过渡。
            float remaining = CreatureRenderer.AdvanceBirdTakeoff(
                BirdBrain.StatePerched, BirdBrain.StateFly, 0f, 0.02f, duration);
            Assert.AreEqual(duration, remaining, "Perched → Fly 沿应把过渡计时重置为满时长");

            Sprite during = CreatureRenderer.ResolveBirdFrame(
                BirdBrain.StateFly, remaining, 7f, fly, perch, takeoff);
            Assert.AreSame(takeoff, during, "过渡期间应定格展翅帧（不随相位换帧）");

            // 过渡剩余按真实秒数递减到 0。
            remaining = CreatureRenderer.AdvanceBirdTakeoff(
                BirdBrain.StateFly, BirdBrain.StateFly, remaining, 0.1f, duration);
            Assert.AreEqual(0.2f, remaining, 1e-5f, "过渡应随真实时间递减");

            remaining = CreatureRenderer.AdvanceBirdTakeoff(
                BirdBrain.StateFly, BirdBrain.StateFly, remaining, 0.5f, duration);
            Assert.AreEqual(0f, remaining, "过渡应夹到 0，不越界为负");

            Sprite after = CreatureRenderer.ResolveBirdFrame(
                BirdBrain.StateFly, remaining, 7f, fly, perch, takeoff);
            Assert.Contains(after, fly, "过渡耗尽后应进飞行组循环");
        }

        /// <summary>④ 重复进出栖息：每次 Perched → Fly/Flee 沿都能重新触发过渡；Fly → Perched 无过渡。</summary>
        [Test]
        public void BirdTakeoff_Retriggers_OnEachPerchDeparture()
        {
            const float duration = 0.3f;
            Sprite[] fly = { NewFrame("fly0"), NewFrame("fly1"), NewFrame("fly2"), NewFrame("fly3") };
            Sprite[] perch = { NewFrame("perch4"), NewFrame("perch6") };
            Sprite takeoff = NewFrame("takeoff5");

            float remaining = CreatureRenderer.AdvanceBirdTakeoff(
                BirdBrain.StatePerched, BirdBrain.StateFly, 0f, 0f, duration);
            Assert.AreEqual(duration, remaining, "第一次起飞应触发过渡");

            // Fly → Perched：无过渡，立即切栖息组（帧选择层保证），且不重启计时。
            remaining = CreatureRenderer.AdvanceBirdTakeoff(
                BirdBrain.StateFly, BirdBrain.StatePerched, remaining, 0.05f, duration);
            Assert.AreEqual(duration - 0.05f, remaining, 1e-5f, "Fly → Perched 不重启过渡计时");
            Assert.Contains(
                CreatureRenderer.ResolveBirdFrame(BirdBrain.StatePerched, remaining, 3f, fly, perch, takeoff),
                perch,
                "落到栖息面应立即播收翅组（无过渡）");

            // 再次 Perched → Fly：重新起满过渡。
            remaining = CreatureRenderer.AdvanceBirdTakeoff(
                BirdBrain.StatePerched, BirdBrain.StateFly, remaining, 0.02f, duration);
            Assert.AreEqual(duration, remaining, "重复进出栖息应能重新触发过渡");
            Assert.AreSame(
                takeoff,
                CreatureRenderer.ResolveBirdFrame(BirdBrain.StateFly, remaining, 3f, fly, perch, takeoff),
                "第二次起飞应重新定格展翅帧");

            // Perched → Flee 沿同样触发（起飞逃命也是展翅）。
            Assert.AreEqual(
                duration,
                CreatureRenderer.AdvanceBirdTakeoff(BirdBrain.StatePerched, BirdBrain.StateFlee, 0f, 0.02f, duration),
                "Perched → Flee 沿同样触发过渡");

            // 未观测过（LastAiState = −1）不算沿，开局即为 Fly 不误播展翅。
            Assert.AreEqual(
                0f,
                CreatureRenderer.AdvanceBirdTakeoff(-1, BirdBrain.StateFly, 0f, 0.02f, duration),
                "尚未观测过的首帧不算沿");
        }

        /// <summary>⑤ 鸟 Flee 的动画倍率 == 3（与兔子同惯例）；在动 ×2、静止 ×1。</summary>
        [Test]
        public void BirdSpeedFactor_FleeIsTriple()
        {
            var bird = new BirdCreature { AiState = BirdBrain.StateFly, VelocityX = 0.05f };
            Assert.AreEqual(2f, CreatureRenderer.ResolveAnimSpeedFactor(bird), "飞行移动应 ×2");

            bird.AiState = BirdBrain.StateFlee;
            Assert.AreEqual(3f, CreatureRenderer.ResolveAnimSpeedFactor(bird), "鸟 Flee 应与兔子同惯例 ×3");

            bird.AiState = BirdBrain.StatePerched;
            bird.VelocityX = 0f;
            Assert.AreEqual(1f, CreatureRenderer.ResolveAnimSpeedFactor(bird), "栖息静止应 ×1");
        }
    }
}
