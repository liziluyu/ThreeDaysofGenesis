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

        /// <summary>v5 死亡骰「扑空」用例的固定 seed：进入 Hunt 那一帧的两抽依次为
        /// <c>NextInt(HuntDenominator=40)</c>（= 641605160 % 40 == 0 ⇒ 命中检测）与
        /// <c>NextInt(prey.HuntDeathDenominator=2)</c>（= 1519842933 % 2 == 1 ⇒ **扑空**）。
        /// 对照 <see cref="SeedValue"/>：第一抽 549735040 % 40 == 0（同样进入 Hunt），第二抽
        /// 2602030858 % 2 == 0（**命中**）。两个 seed 都按既有钉法现场试出并写死（见 docs/plan-bird-ai.md §14.4）。</summary>
        private const uint DeathRollMissSeed = 20261152u;

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

        private static ushort TreeTrunk
        {
            get { return ParticleIds.TreeTrunk; }
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

        /// <summary>相对正下方第一个可栖面 / 水的当前高度（格），与 <see cref="BirdBrain.ThinkFly"/> 同一口径：
        /// <c>altitude = (floor(Y) − 1) − surfaceRow</c>，其中 <c>surfaceRow</c> 由
        /// <see cref="CreatureSensors.ProbeSurfaceBelow"/> 自脚底下一行向下扫得。</summary>
        private static int AltitudeAboveSurface(ParticleWorld world, Creature creature)
        {
            int footRow = Mathf.FloorToInt(creature.Y);
            int cx = Mathf.FloorToInt(creature.X);
            if (!CreatureSensors.ProbeSurfaceBelow(
                    world, cx, footRow - 1, world.Height, out int surfaceRow, out _, out _))
            {
                return int.MaxValue; // 无基准：不参与带内断言
            }

            return (footRow - 1) - surfaceRow;
        }

        /// <summary>§12.5-③ 带内长跑帧数：单段上下行程 ≈ <c>(MaxAltitudeCells − MinAltitudeCells) / FlyVSpeed</c>
        /// 逻辑帧，取 8 段（留足余量）以保证「上 / 下两个方向都出现过」在任何随机目标序列下都成立。
        /// 随高度带常量自动伸缩，不钉字面量。</summary>
        private static int CruiseLongRunTicks
        {
            get
            {
                int legTicks = Mathf.CeilToInt(
                    (BirdBrain.MaxAltitudeCells - BirdBrain.MinAltitudeCells) / BirdBrain.FlyVSpeed);
                return legTicks * 8;
            }
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
            Assert.AreEqual(a.TargetAltitudeCells, b.TargetAltitudeCells, $"{context} TargetAltitudeCells 应逐位一致");
            Assert.AreEqual(a.Facing, b.Facing, $"{context} Facing 应逐位一致");
            Assert.AreEqual(a.SubmergedTicks, b.SubmergedTicks, $"{context} SubmergedTicks 应逐位一致");
            Assert.AreEqual(a.IsGrounded, b.IsGrounded, $"{context} IsGrounded 应逐位一致");
            Assert.AreEqual(a.HuntTarget == null, b.HuntTarget == null, $"{context} HuntTarget 是否为空应一致");
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
            const int canopyRow = 14; // 鸟钉在此行；树叶墙铺在它**上面一行起**
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            // 树叶墙错开一行（铺 15..16），鸟的碰撞盒仍与树叶行相接（故意让身体蹭着树冠穿墙）；关键是脚底行
            // （row 13）恒为空气：IsGrounded 用 floor(Y − ε) 采样支撑行，树冠若铺在脚底那一行（14）就会被判
            // 「站在树叶上」⇒ 转栖息、水平意图清零。故墙整体上移一行，把竖直自由度（±FlyVSpeed）也从落地判定里隔离。
            FillRect(world, 20, 30, canopyRow + 1, canopyRow + 2, TreeLeaf); // 面前的一堵树叶墙（行 15..16）

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 5f, canopyRow);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;
            bird.IntentVx = BirdBrain.FlySpeed;

            // 高度带放大（MaxAltitudeCells = 50）后带内鸟会主动升降（Y 在 14 附近摆动），可能顺势落在树冠顶并
            // 转栖息（那是既有且有专测覆盖的 §8-④ 行为）；本用例只钉「树叶不挡水平」，故每逻辑帧把 Y 钉回行 14
            // 以隔离竖直自由度；脚底行 13 恒空 ⇒ 无论竖直意图朝上（→14.05）还是朝下（→13.95）都不会判落地。
            for (int i = 0; i < 500; i++)
            {
                bird.Y = canopyRow;
                creatures.Step(world);
            }

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

        /// <summary>① 栖息态：帧选择只在上排栖息组内交替（永远不返回飞行 / 展翅帧）。</summary>
        [Test]
        public void BirdFrame_Perched_PlaysPerchGroup()
        {
            Sprite[] fly = { NewFrame("fly4"), NewFrame("fly5"), NewFrame("fly6") };
            Sprite[] perch = { NewFrame("perch0"), NewFrame("perch1"), NewFrame("perch2"), NewFrame("perch3") };
            Sprite takeoff = NewFrame("takeoff5");

            var seen = new HashSet<Sprite>();
            for (int i = 0; i < 16; i++)
            {
                Sprite frame = CreatureRenderer.ResolveBirdFrame(
                    BirdBrain.StatePerched, 0f, i, fly, perch, takeoff);
                Assert.Contains(frame, perch, "栖息态应播上排栖息组帧");
                seen.Add(frame);
            }

            Assert.AreEqual(perch.Length, seen.Count, "上排 4 片都应在栖息态交替出现（不是死盯第一片）");
        }

        /// <summary>② 飞行态：帧选择只在下排飞行组内循环。</summary>
        [Test]
        public void BirdFrame_Fly_PlaysFlyGroup()
        {
            Sprite[] fly = { NewFrame("fly4"), NewFrame("fly5"), NewFrame("fly6") };
            Sprite[] perch = { NewFrame("perch0"), NewFrame("perch1"), NewFrame("perch2"), NewFrame("perch3") };
            Sprite takeoff = NewFrame("takeoff5");

            var seen = new HashSet<Sprite>();
            for (int i = 0; i < 8; i++)
            {
                Sprite frame = CreatureRenderer.ResolveBirdFrame(
                    BirdBrain.StateFly, 0f, i, fly, perch, takeoff);
                Assert.Contains(frame, fly, "飞行态应播下排飞行组帧");
                seen.Add(frame);
            }

            Assert.AreEqual(fly.Length, seen.Count, "下排 3 片都应循环出现");
        }

        /// <summary>③ Perched → Fly 沿：先定格展翅帧，过渡耗尽后进飞行组循环。</summary>
        [Test]
        public void BirdFrame_PerchedToFly_PlaysTakeoffThenFly()
        {
            const float duration = 0.3f;
            Sprite[] fly = { NewFrame("fly4"), NewFrame("fly5"), NewFrame("fly6") };
            Sprite[] perch = { NewFrame("perch0"), NewFrame("perch1"), NewFrame("perch2"), NewFrame("perch3") };
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
            Sprite[] fly = { NewFrame("fly4"), NewFrame("fly5"), NewFrame("fly6") };
            Sprite[] perch = { NewFrame("perch0"), NewFrame("perch1"), NewFrame("perch2"), NewFrame("perch3") };
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
                "落到栖息面应立即播栖息组（无过渡）");

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
            Assert.AreEqual(2f, SimulationFrameCollector.ResolveAnimationSpeedFactor(bird), "飞行移动应 ×2");

            bird.AiState = BirdBrain.StateFlee;
            Assert.AreEqual(3f, SimulationFrameCollector.ResolveAnimationSpeedFactor(bird), "鸟 Flee 应与兔子同惯例 ×3");

            bird.AiState = BirdBrain.StatePerched;
            bird.VelocityX = 0f;
            Assert.AreEqual(1f, SimulationFrameCollector.ResolveAnimationSpeedFactor(bird), "栖息静止应 ×1");
        }

        // ============================== §11 v2 捕食（Hunt）测试清单 1-9 ==============================

        /// <summary>§11-① 视野无猎物 ⇒ 永不进 Hunt，且（视野外的猎物存在也不）扰动随机流。
        /// 两个同 seed 世界：A 只有鸟、B 鸟 + 一只**视野外**的猎物（钉住计时使其 Brain 不摇骰、原地不动），
        /// 跑 300 帧后两世界的下一次随机抽取必须逐位一致——「无猎物在视野内不摇骰」的直接证据。</summary>
        [Test]
        public void Hunt_NoPreyInView_NeverHunts_AndRngUnchanged()
        {
            ParticleWorld worldA = NewWorld();
            FillRows(worldA, 0, 0, Stone);
            CreatureWorld ca = new CreatureWorld(SeedValue);
            BirdCreature birdA = ca.Spawn(new BirdCreature(), worldA, 4f, 7f);
            birdA.AiState = BirdBrain.StateFly;
            birdA.StateTicksLeft = 100000;

            ParticleWorld worldB = NewWorld();
            FillRows(worldB, 0, 0, Stone);
            CreatureWorld cb = new CreatureWorld(SeedValue);
            BirdCreature birdB = cb.Spawn(new BirdCreature(), worldB, 4f, 7f);
            birdB.AiState = BirdBrain.StateFly;
            birdB.StateTicksLeft = 100000;
            RabbitCreature farPrey = cb.Spawn(
                new RabbitCreature(), worldB, 4f + BirdBrain.HuntDetectCells + 1f, 1f); // 视野外（> HuntDetectCells）
            farPrey.AiState = RabbitBrain.StateIdle; // Idle + 计时不走完 ⇒ 思考帧不消耗随机流
            farPrey.StateTicksLeft = 100000;

            bool enteredHunt = false;
            for (int i = 0; i < 300; i++)
            {
                birdA.X = 4f; // 钉住水平位置，保证猎物始终在视野外（盒中心距离 > HuntDetectCells）
                birdB.X = 4f;
                ca.Step(worldA);
                cb.Step(worldB);
                enteredHunt |= birdA.AiState == BirdBrain.StateHunt || birdB.AiState == BirdBrain.StateHunt;
            }

            Assert.IsFalse(enteredHunt, "视野内无猎物时不该进入 Hunt");
            Assert.AreEqual(ca.NextUInt(), cb.NextUInt(), "视野外的猎物不该扰动捕食触发（无猎物在视野内时不摇骰）");
        }

        /// <summary>§11-② 视野内有猎物 ⇒ 跑够帧数必进 Hunt（触发骰子命中）。
        /// 猎物钉在同一列正下方 6 格（Idle 不动），鸟钉住水平位置并抑制降落计时——</summary>
        [Test]
        public void Hunt_PreyInView_EntersHuntWithinBudget()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 20f, 7f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 20f, 1f);
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            bool entered = false;
            for (int i = 0; i < BirdBrain.HuntTimeoutTicks * 10 && !entered; i++)
            {
                bird.X = 20f;
                prey.X = 20f;
                prey.HungerTicks = 0; // 钉住饥饿，避免 600/1200 帧后转入觅食 / 饿死（那段会消耗随机流且移动）
                if (bird.AiState == BirdBrain.StateFly)
                {
                    bird.StateTicksLeft = 100000; // 不降落，保持巡航扫视野
                }

                creatures.Step(world);
                entered = bird.AiState == BirdBrain.StateHunt;
            }

            Assert.IsTrue(entered, "视野内有猎物时应在若干思考帧内摇中骰子进入 Hunt");
        }

        /// <summary>§11-③ 接触击杀：猎物死一次、死因 <see cref="CreatureDeathCause.Hunted"/>、事件恰好一次、
        /// 目标槽位清空，且尸体物质化格数 == 兔子轮廓非空格数（走既有管线）。</summary>
        [Test]
        public void Hunt_Contact_KillsPreyOnceAsHunted_AndMaterializes()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 1f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 24f, 1f);
            prey.HuntDeathDenominator = 1; // 本用例钉必死路径（分母 ≤ 1 不摇骰，见 §14）

            int deaths = 0;
            Creature seen = null;
            CreatureDeathCause seenCause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (c, cause) =>
            {
                deaths++;
                seen = c;
                seenCause = cause;
            };

            bird.AiState = BirdBrain.StateHunt;
            bird.HuntTarget = prey;
            bird.HuntKillLatched = true; // 显式锁存「咬杀」：本用例手工预置 Hunt 态、绕过 EnterHunt 的死亡骰
            bird.StateTicksLeft = BirdBrain.HuntTimeoutTicks;

            creatures.Step(world); // 相位 0：思考 → 运动层 ⑥b 重叠结算（锁存命中即死） → 清扫物质化

            Assert.IsFalse(prey.Alive, "接触即应被猎杀");
            Assert.AreEqual(1, deaths, "死亡事件应恰好一次");
            Assert.AreSame(prey, seen, "事件应送出猎物本身");
            Assert.AreEqual(CreatureDeathCause.Hunted, seenCause, "死因应为 Hunted");
            Assert.IsTrue(bird.HuntTarget == null, "击杀后目标槽位应被清空");

            sbyte[] cells = BiomassProfile.Rabbit.Cells;
            int baseX = Mathf.FloorToInt(24f) - (BiomassProfile.Size / 2);
            int baseY = Mathf.FloorToInt(1f) - BiomassProfile.Rabbit.PivotRowCells;

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

            Assert.AreEqual(NonEmptyCount(cells), written, "每个非空兔子轮廓格都应写入");
            Assert.AreEqual(written, CountType(world, Biomass), "场上生物质格数应恰等于兔子轮廓格数");
        }

        /// <summary>§11-④ 击杀后撤离：鸟回 Fly 且竖直意图向上（不停留）。</summary>
        [Test]
        public void Hunt_AfterKill_RetreatsToFlyGoingUp()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 1f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 24f, 1f);

            bird.AiState = BirdBrain.StateHunt;
            bird.HuntTarget = prey;
            bird.HuntKillLatched = true; // 手工预置 Hunt 态、绕过 EnterHunt ⇒ 显式锁存「咬杀」
            bird.StateTicksLeft = BirdBrain.HuntTimeoutTicks;

            bool sawRetreat = false;
            float intentVyAtRetreat = 0f;
            for (int i = 0; i < 40 && !sawRetreat; i++)
            {
                byte before = bird.AiState;
                creatures.Step(world);
                if (before == BirdBrain.StateHunt && bird.AiState == BirdBrain.StateFly)
                {
                    sawRetreat = true;
                    intentVyAtRetreat = bird.IntentVy;
                }
            }

            Assert.IsTrue(sawRetreat, "击杀后应回到 Fly");
            Assert.Greater(intentVyAtRetreat, 0f, "撤离应带向上冲的竖直意图（不停留）");
            Assert.IsTrue(bird.HuntTarget == null, "撤离后目标槽位应为空");
        }

        /// <summary>§11-⑤ 超时与追丢都放弃回 Fly。</summary>
        [Test]
        public void Hunt_Timeout_And_LostTarget_BothAbandonToFly()
        {
            // A. 超时：目标在放弃半径内（不远于 HuntAbortCells，故不会被距离放弃）；StateTicksLeft 归零即放弃。
            ParticleWorld worldA = NewWorld();
            FillRows(worldA, 0, 0, Stone);
            CreatureWorld ca = new CreatureWorld(SeedValue);
            BirdCreature birdA = ca.Spawn(new BirdCreature(), worldA, 10f, 7f);
            RabbitCreature preyA = ca.Spawn(
                new RabbitCreature(), worldA, 10f + (BirdBrain.HuntAbortCells / 2f), 1f);
            birdA.AiState = BirdBrain.StateHunt;
            birdA.HuntTarget = preyA;
            birdA.StateTicksLeft = 3;

            bool abandonedByTimeout = false;
            for (int i = 0; i < 40 && !abandonedByTimeout; i++)
            {
                ca.Step(worldA);
                abandonedByTimeout = birdA.AiState == BirdBrain.StateFly;
            }

            Assert.IsTrue(abandonedByTimeout, "超时应放弃回 Fly");
            Assert.IsTrue(birdA.HuntTarget == null, "放弃后目标槽位应清空");
            Assert.IsTrue(preyA.Alive, "超时放弃时猎物仍活");

            // B. 追丢：目标逃出 HuntAbortCells ⇒ 立即放弃。
            ParticleWorld worldB = NewWorld();
            FillRows(worldB, 0, 0, Stone);
            CreatureWorld cb = new CreatureWorld(SeedValue);
            BirdCreature birdB = cb.Spawn(new BirdCreature(), worldB, 2f, 20f);
            RabbitCreature preyB = cb.Spawn(
                new RabbitCreature(), worldB, 2f + BirdBrain.HuntAbortCells + 8f, 1f);
            birdB.AiState = BirdBrain.StateHunt;
            birdB.HuntTarget = preyB; // 盒中心距离 > HuntAbortCells
            birdB.StateTicksLeft = BirdBrain.HuntTimeoutTicks;

            cb.Step(worldB); // 相位 0 ⇒ 首帧即思考
            Assert.AreEqual(BirdBrain.StateFly, birdB.AiState, "目标逃出放弃半径应回 Fly");
            Assert.IsTrue(birdB.HuntTarget == null, "放弃后目标槽位应清空");
        }

        /// <summary>§11-⑥ 俯冲入水杀浅层鱼且鸟不死：Hunt 态且目标是鱼 ⇒ 豁免落水自救反射，能扎进水里叼鱼。</summary>
        [Test]
        public void Hunt_DivesIntoWater_KillsShallowFish_AndSurvives()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 1, Stone);
            FillRows(world, 2, 2, Water); // 一层浅水：鱼贴水面且伏底不动

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 8f);
            FishCreature prey = creatures.Spawn(new FishCreature(), world, 24f, 2f);
            prey.HuntDeathDenominator = 1; // 本用例钉必死路径（分母 ≤ 1 ⇒ EnterHunt 锁存必杀且不摇骰，见 §14）

            bird.AiState = BirdBrain.StateHunt;
            bird.HuntTarget = prey;
            bird.HuntKillLatched = true; // 手工预置 Hunt 态、绕过 EnterHunt ⇒ 显式锁存「咬杀」
            bird.StateTicksLeft = BirdBrain.HuntTimeoutTicks;

            bool killed = false;
            for (int i = 0; i < BirdBrain.HuntTimeoutTicks && !killed; i++)
            {
                creatures.Step(world);
                killed = !prey.Alive;
            }

            Assert.IsTrue(killed, "俯冲应叼到浅层鱼");
            Assert.IsTrue(bird.Alive, "短暂入水不该溺死鸟");
            Assert.IsTrue(bird.SubmergedTicks < bird.DrownTicksLimit, "入水时长应远小于溺死阈值");
        }

        /// <summary>§11-⑦ 同 seed 重放逐位一致（含猎物与捕食逻辑）。</summary>
        [Test]
        public void Hunt_Determinism_SameSeed_ReplayBitExact()
        {
            ParticleWorld wa = NewWorld();
            ParticleWorld wb = NewWorld();
            FillRows(wa, 0, 0, Stone);
            FillRows(wb, 0, 0, Stone);

            CreatureWorld ca = new CreatureWorld(SeedValue);
            CreatureWorld cb = new CreatureWorld(SeedValue);
            BirdCreature a = ca.Spawn(new BirdCreature(), wa, 20f, 7f);
            BirdCreature b = cb.Spawn(new BirdCreature(), wb, 20f, 7f);
            ca.Spawn(new RabbitCreature(), wa, 24f, 1f);
            cb.Spawn(new RabbitCreature(), wb, 24f, 1f);

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

        /// <summary>§11-⑧ 世界随机流隔离：鸟 + 视野外猎物都不消耗粒子世界的随机流。</summary>
        [Test]
        public void Hunt_RngIsolation_BirdAndPreyDoNotConsumeWorldStream()
        {
            ParticleWorld solo = NewWorld();
            ParticleWorld withLife = NewWorld();
            FillRows(solo, 0, 0, Stone);
            FillRows(withLife, 0, 0, Stone);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), withLife, 24f, 30f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;
            RabbitCreature farPrey = creatures.Spawn(new RabbitCreature(), withLife, 2f, 1f); // 竖直差 ≈ 29 ⇒ 视野外
            farPrey.StateTicksLeft = 100000;

            for (int i = 0; i < 300; i++)
            {
                solo.StepLogic();
                withLife.StepLogic();
                creatures.Step(withLife);
            }

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(solo.NextUInt(), withLife.NextUInt(), "鸟与视野外猎物都不该消耗世界随机流");
            }
        }

        /// <summary>§11-⑨ 兔子对鸟无感知（v1 契约）：即使捕食的鸟就在头顶，兔子也永不进入 Flee。</summary>
        [Test]
        public void Hunt_RabbitHasNoPredatorAwareness()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 7f);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 24f, 1f);

            bool everFled = false;
            bool predatorAttacked = false;
            for (int i = 0; i < BirdBrain.HuntTimeoutTicks * 10 && !predatorAttacked; i++)
            {
                bird.X = 24f;   // 钉住鸟，保证始终与兔子同列（视野内）
                rabbit.X = 24f;
                rabbit.HungerTicks = 0; // 钉住饥饿，避免饿死 / 转觅食干扰
                if (bird.AiState == BirdBrain.StateFly)
                {
                    bird.StateTicksLeft = 100000;
                }

                creatures.Step(world);
                everFled |= rabbit.AiState == RabbitBrain.StateFlee;
                predatorAttacked = bird.AiState == BirdBrain.StateHunt;
            }

            Assert.IsTrue(predatorAttacked, "鸟应确实发起过捕食（证明兔子面对的是真实威胁）");
            Assert.IsFalse(everFled, "兔子没有天敌感知（v1）：面对捕食的鸟也不该进入 Flee");
        }

        // ============================== v4 观感调参：视野顶也能捕食（常量耦合契约） ==============================

        /// <summary>v4 契约（§18 起口径修正）：从**可发现猎物的最高高度**（相对高度 == <see cref="BirdBrain.HuntDetectCells"/>）
        /// 发起追击，能在 <see cref="BirdBrain.HuntTimeoutTicks"/> 内够到地面猎物——钉住「视野高度 / 俯冲速度
        /// <see cref="BirdBrain.HuntDiveSpeed"/> / 追击超时」三者的耦合：若俯冲仍用 <see cref="BirdBrain.FlyVSpeed"/>，
        /// 从视野顶下压必然超过 <see cref="BirdBrain.HuntTimeoutTicks"/> 而超时。
        /// §18.2 起带顶 50 刻意大于捕食视野 24：带顶（乃至任何 altitude &gt; 24）本就看不见地面猎物、也超出
        /// <see cref="BirdBrain.HuntAbortCells"/>，因此本用例改用视野顶而非 <see cref="BirdBrain.MaxAltitudeCells"/>。</summary>
        [Test]
        public void Hunt_FromDetectAltitude_ReachesGroundPrey_WithinTimeout()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            // 相对高度 = HuntDetectCells ⇒ 恰好是能发现地面猎物的最高巡航高度。
            BirdCreature bird = creatures.Spawn(
                new BirdCreature(), world, 24f, BirdBrain.HuntDetectCells + 1f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 24f, 1f);
            prey.HuntDeathDenominator = 1; // 本用例钉必死路径（分母 ≤ 1 ⇒ EnterHunt 锁存必杀且不摇骰，见 §14）
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            bird.AiState = BirdBrain.StateHunt;
            bird.HuntTarget = prey;
            bird.HuntKillLatched = true; // 手工预置 Hunt 态、绕过 EnterHunt ⇒ 显式锁存「咬杀」
            bird.StateTicksLeft = BirdBrain.HuntTimeoutTicks;

            bool killed = false;
            for (int i = 0; i < BirdBrain.HuntTimeoutTicks && !killed; i++)
            {
                creatures.Step(world);
                killed = !prey.Alive;
            }

            Assert.IsTrue(killed, "从视野顶发起的俯冲应能在 HuntTimeoutTicks 内够到地面猎物");
            Assert.IsTrue(bird.Alive, "俯冲杀猎物不该让鸟死");
        }

        // ================== §14 v5 捕食死亡骰（锁定目标时摇、接触时只读锁存） ==================

        /// <summary>§14.4-1 分母 = 1 ⇒ 锁定目标即锁存「必杀」（不摇骰），接触当帧即判死。</summary>
        [Test]
        public void Hunt_DeathRoll_DenominatorOne_AlwaysKills()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue); // 第一抽 %40==0 ⇒ 首帧在视野内锁定猎物
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 8f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 24f, 8f); // 与鸟同格 ⇒ 进入即接触
            prey.HuntDeathDenominator = 1; // 分母 ≤ 1 ⇒ 必杀且不摇骰

            CreatureDeathCause seenCause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (c, cause) => seenCause = cause;

            bird.AiState = BirdBrain.StateFly; // 空中巡航：思考帧在视野内发现猎物 → 锁定（分母 1 ⇒ 直接锁存必杀）
            bird.StateTicksLeft = 100000;

            creatures.Step(world); // 相位 0：思考锁定 → 运动层 ⑥b 接触 → 判死 → 清扫

            Assert.IsTrue(bird.HuntKillLatched, "分母 = 1 时锁定即锁存必杀");
            Assert.IsFalse(prey.Alive, "分母 = 1 时接触当帧即死");
            Assert.AreEqual(CreatureDeathCause.Hunted, seenCause, "死因应为 Hunted");
            Assert.IsTrue(bird.HuntTarget == null, "接触后目标槽位应被清空");
        }

        /// <summary>§14.4-1 分母 = 2 命中路径：seed <see cref="SeedValue"/> 在进入 Hunt 那一帧的两抽为
        /// <c>NextInt(40)==0</c>（检测）与 <c>NextInt(2)==0</c>（死亡骰命中）⇒ 锁存「咬杀」，
        /// 接触当帧即死、死因 <see cref="CreatureDeathCause.Hunted"/>、尸体走既有物质化管线。</summary>
        [Test]
        public void Hunt_DeathRoll_Hit_DiesAndMaterializes()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue); // 两抽：40→命中检测、2→命中
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 8f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 24f, 8f); // 默认分母 = HuntDeathRoll

            Assert.AreEqual(RabbitCreature.HuntDeathRoll, prey.HuntDeathDenominator, "兔子默认分母应为 HuntDeathRoll");

            CreatureDeathCause seenCause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (c, cause) => seenCause = cause;

            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            creatures.Step(world); // 锁定（死亡骰命中）+ 同帧接触

            Assert.IsTrue(bird.HuntKillLatched, "该 seed 的死亡骰应命中并锁存必杀");
            Assert.IsFalse(prey.Alive, "锁存命中 ⇒ 接触当帧即死");
            Assert.AreEqual(CreatureDeathCause.Hunted, seenCause, "死因应为 Hunted");
            Assert.Greater(CountType(world, Biomass), 0, "命中后尸体应物质化出生物质");
            Assert.IsTrue(bird.HuntTarget == null, "接触后目标槽位应被清空");
        }

        /// <summary>§14.4-1 / §14.4-2 分母 = 2 扑空路径：seed <see cref="DeathRollMissSeed"/> 在进入 Hunt
        /// 那一帧的两抽为 <c>NextInt(40)==0</c> 与 <c>NextInt(2)==1</c> ⇒ 锁存「扑空」⇒ 猎物存活、
        /// 零生物质增量、目标清空，鸟走既有撤离出口（<c>ExitHunt(retreat: true)</c>）回 Fly 并向上冲；
        /// 撤离时命中锁存被复位。</summary>
        [Test]
        public void Hunt_DeathRoll_Miss_PreySurvivesAndBirdRetreats()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(DeathRollMissSeed); // 两抽：40→命中检测、2→扑空
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 8f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 24f, 8f);
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            int biomassBefore = CountType(world, Biomass);

            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            bool sawRetreat = false;
            float intentVyAtRetreat = 0f;
            for (int i = 0; i < 40 && !sawRetreat; i++)
            {
                byte before = bird.AiState;
                creatures.Step(world);
                if (before == BirdBrain.StateHunt && bird.AiState == BirdBrain.StateFly)
                {
                    sawRetreat = true;
                    intentVyAtRetreat = bird.IntentVy;
                }
            }

            Assert.IsTrue(prey.Alive, "扑空 ⇒ 猎物存活");
            Assert.AreEqual(biomassBefore, CountType(world, Biomass), "扑空不写生物质");
            Assert.IsTrue(sawRetreat, "扑空后应走撤离出口回 Fly");
            Assert.AreEqual(BirdBrain.StateFly, bird.AiState, "撤离后应回 Fly");
            Assert.IsTrue(bird.HuntTarget == null, "撤离后目标槽位应为空");
            Assert.IsFalse(bird.HuntKillLatched, "撤离时命中锁存应被复位");
            Assert.Greater(intentVyAtRetreat, 0f, "撤离应带向上冲的竖直意图（不停留）");
        }

        /// <summary>§14.4-3 死亡骰在行为层（锁定那一刻）摇、运动层只读锁存。
        /// <para><b>A. 锁定即消耗：</b>鸟在 Fly 思考帧进入 Hunt（猎物在视野内但**不相交**，故此刻无接触）。
        /// 该帧两抽 = <c>NextInt(40)</c>（检测）+ <c>NextInt(2)</c>（死亡骰）；用「从未消耗随机流的空世界」
        /// 的抽取序列对齐，场景世界的下一次抽取应恰为空世界的**第三抽**（证明恰好消耗两抽、且发生在接触之前）。</para>
        /// <para><b>B. 运动层零消耗：</b>用无 Brain 的 <see cref="ProbeCreature"/>，预置 <c>HuntKillLatched</c>
        /// 与 <c>HuntTarget</c> 后强制两盒持续重叠 30 帧，与从未消耗流的空世界比对应逐位一致（⑥b 不摇骰）。</para></summary>
        [Test]
        public void Hunt_DeathRoll_RolledAtLockTime_NotOnContact()
        {
            // A. 锁定即消耗两抽（检测骰 + 死亡骰），且此刻尚无接触（鸟在 8 格、猎物在 1 格，两盒不相交）。
            ParticleWorld worldA = NewWorld();
            FillRows(worldA, 0, 0, Stone);
            CreatureWorld scenario = new CreatureWorld(SeedValue); // 第一抽 %40==0 ⇒ 首帧进入 Hunt
            BirdCreature bird = scenario.Spawn(new BirdCreature(), worldA, 24f, 8f);
            RabbitCreature prey = scenario.Spawn(new RabbitCreature(), worldA, 24f, 1f); // 视野内（距离 7）、不相交
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            scenario.Step(worldA); // 相位 0：Fly 思考帧 → 检测 + 锁定（摇死亡骰）

            Assert.AreEqual(BirdBrain.StateHunt, bird.AiState, "首帧应进入 Hunt");
            Assert.IsTrue(bird.HuntTarget != null, "锁定后目标槽位应非空（尚未接触）");
            Assert.IsTrue(prey.Alive, "尚未接触 ⇒ 猎物仍活");

            CreatureWorld untouched = new CreatureWorld(SeedValue); // 从未消耗随机流的空世界（序列基准）
            uint detectDraw = untouched.NextUInt(); // 场景世界的第一抽 = 检测骰
            untouched.NextUInt();                   // 场景世界的第二抽 = 锁定时的死亡骰
            uint thirdDraw = untouched.NextUInt();  // 恰好消耗两抽之后的下一抽

            uint scenarioNext = scenario.NextUInt();
            Assert.AreNotEqual(detectDraw, scenarioNext, "进入 Hunt 应已消耗随机流（否则本用例没测到摇骰）");
            Assert.AreEqual(thirdDraw, scenarioNext, "锁定那一刻恰好消耗「检测骰 + 死亡骰」两抽（不在接触帧摇）");

            // B. 运动层只读锁存：预置锁存 + 目标，强制两盒持续重叠 30 帧，随机流不得被消耗。
            ParticleWorld worldB = NewWorld();
            FillRows(worldB, 0, 0, Stone);
            CreatureWorld motion = new CreatureWorld(SeedValue);
            ProbeCreature attacker = motion.Spawn(new ProbeCreature(), worldB, 24f, 30f);
            ProbeCreature victim = motion.Spawn(new ProbeCreature(), worldB, 24f, 30f);
            victim.HuntDeathDenominator = 2;
            attacker.HuntKillLatched = false; // 模拟 EnterHunt 已摇好「扑空」
            attacker.HuntTarget = victim;

            for (int i = 0; i < 30; i++)
            {
                attacker.X = 24f;
                attacker.Y = 30f;
                victim.X = 24f;
                victim.Y = 30f;
                motion.Step(worldB);
            }

            CreatureWorld untouchedB = new CreatureWorld(SeedValue); // 从未消耗随机流的空世界
            Assert.AreEqual(untouchedB.NextUInt(), motion.NextUInt(), "运动层 ⑥b 全程不得消耗动物随机流");
        }

        /// <summary>§14.4-4 世界随机流隔离：发生捕食接触（本用例为扑空）时，粒子世界的随机流仍零消耗——
        /// 扩展自 <see cref="Hunt_RngIsolation_BirdAndPreyDoNotConsumeWorldStream"/>（扑空不写世界；
        /// 死亡物质化走 <c>SetWithShade</c> 不抽样颜色）。</summary>
        [Test]
        public void Hunt_DeathRoll_Contact_DoesNotConsumeWorldStream()
        {
            ParticleWorld solo = NewWorld();
            ParticleWorld withLife = NewWorld();
            FillRows(solo, 0, 0, Stone);
            FillRows(withLife, 0, 0, Stone);

            CreatureWorld creatures = new CreatureWorld(DeathRollMissSeed);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), withLife, 24f, 8f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), withLife, 24f, 8f); // 与鸟同格 ⇒ 进入 Hunt 即接触（扑空）
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            for (int i = 0; i < 300; i++)
            {
                solo.StepLogic();
                withLife.StepLogic();
                creatures.Step(withLife);
            }

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(solo.NextUInt(), withLife.NextUInt(), "捕食接触不该消耗粒子世界的随机流");
            }
        }

        /// <summary>§14.4-5 物种配置守卫：只钉「功能没被静默禁用」（分母 &gt; 1），不钉具体数值（调参不改测试）。</summary>
        [Test]
        public void Hunt_DeathRoll_SpeciesConfigured()
        {
            Assert.Greater(new RabbitCreature().HuntDeathDenominator, 1, "兔子应配成可存活的概率（否则功能静默失效）");
            Assert.Greater(new FishCreature().HuntDeathDenominator, 1, "鱼应配成可存活的概率（否则功能静默失效）");
        }

        /// <summary>§14.4-7 确定性：同 seed 两个世界重放逐位一致，含「锁定目标时摇死亡骰 + 接触时按锁存结算」
        /// 这条 v5 新增路径（两世界都走自然进入 Hunt 的完整链路）。既有的
        /// <see cref="Hunt_Determinism_SameSeed_ReplayBitExact"/> 覆盖捕食逻辑但不含死亡骰的落点，故本用例补钉。</summary>
        [Test]
        public void Hunt_DeathRoll_Determinism_SameSeedReplayBitExact()
        {
            ParticleWorld wa = NewWorld();
            ParticleWorld wb = NewWorld();
            FillRows(wa, 0, 0, Stone);
            FillRows(wb, 0, 0, Stone);

            CreatureWorld ca = new CreatureWorld(DeathRollMissSeed);
            CreatureWorld cb = new CreatureWorld(DeathRollMissSeed);
            BirdCreature a = ca.Spawn(new BirdCreature(), wa, 24f, 8f);
            BirdCreature b = cb.Spawn(new BirdCreature(), wb, 24f, 8f);
            RabbitCreature pa = ca.Spawn(new RabbitCreature(), wa, 24f, 8f);
            RabbitCreature pb = cb.Spawn(new RabbitCreature(), wb, 24f, 8f);
            pa.AiState = RabbitBrain.StateIdle;
            pb.AiState = RabbitBrain.StateIdle;
            pa.StateTicksLeft = 100000;
            pb.StateTicksLeft = 100000;

            a.AiState = BirdBrain.StateFly;
            b.AiState = BirdBrain.StateFly;
            a.StateTicksLeft = 100000;
            b.StateTicksLeft = 100000;

            for (int i = 0; i < 120; i++)
            {
                wa.StepLogic();
                ca.Step(wa);
                wb.StepLogic();
                cb.Step(wb);
            }

            Assert.AreEqual(ca.Count, cb.Count, "同 seed 同操作下存活数应一致");
            AssertCreatureEqual(a, b, "鸟");
            AssertCreatureEqual(pa, pb, "猎物");
            Assert.AreEqual(ca.NextUInt(), cb.NextUInt(), "同 seed 下动物随机流应逐位一致（含锁定时的死亡骰）");
        }

        // ============================== §12 v3 高度带随机上下巡航（验收 1-8） ==============================

        /// <summary>§12.5-① 低放（相对高度 &lt; <see cref="BirdBrain.MinAltitudeCells"/>）⇒ 上抬；爬升后相对高度落在
        /// <c>[MinAltitudeCells, MaxAltitudeCells]</c>（±1 格物理容差）。</summary>
        [Test]
        public void LowPlacement_ClimbsIntoAltitudeBand()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 2f); // 相对高度 = 1 < MinAltitudeCells
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000; // 不进入降落分支

            creatures.Step(world); // 相位 0 ⇒ 首帧即思考
            Assert.Greater(bird.IntentVy, 0f, "低于下限应先上抬");

            int maxAltitude = 0;
            for (int i = 0; i < 200; i++)
            {
                bird.X = 24f; // 钉住列，基准面恒为世界底
                creatures.Step(world);
                maxAltitude = Mathf.Max(maxAltitude, AltitudeAboveSurface(world, bird));
            }

            Assert.GreaterOrEqual(maxAltitude, BirdBrain.MinAltitudeCells, "应爬进 [Min, Max] 带内");
            Assert.LessOrEqual(maxAltitude, BirdBrain.MaxAltitudeCells + 1, "不该冲出带上限（±1 物理容差）");
            int altitude = AltitudeAboveSurface(world, bird);
            Assert.GreaterOrEqual(altitude, BirdBrain.MinAltitudeCells - 1, "终态相对高度应在带内（±1）");
            Assert.LessOrEqual(altitude, BirdBrain.MaxAltitudeCells + 1, "终态相对高度应在带内（±1）");
        }

        /// <summary>§12.5-② 高放（相对高度 &gt; <see cref="BirdBrain.MaxAltitudeCells"/>）⇒ 主动下压回带内——旧「单向棘轮」的修复回归。</summary>
        [Test]
        public void HighPlacement_DescendsBackIntoAltitudeBand()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            // 相对高度 = MaxAltitudeCells + 9 > MaxAltitudeCells，且 Y 远低于天花板门槛。
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, BirdBrain.MaxAltitudeCells + 10f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            float y0 = bird.Y;
            for (int i = 0; i < 400; i++)
            {
                bird.X = 24f;
                creatures.Step(world);
            }

            Assert.Less(bird.Y, y0, "高于上限应主动下压（不再只升不降）");
            int altitude = AltitudeAboveSurface(world, bird);
            Assert.GreaterOrEqual(altitude, BirdBrain.MinAltitudeCells - 1, "应下压进带内（±1）");
            Assert.LessOrEqual(altitude, BirdBrain.MaxAltitudeCells + 1, "应下压进带内（±1）");
        }

        /// <summary>§12.5-③ 带内长跑：相对高度始终留在 <c>[MinAltitudeCells, MaxAltitudeCells]</c>（±1 格），且上下两个方向
        /// 都出现过（不粘死、不长期悬停）。跑 <see cref="CruiseLongRunTicks"/> 帧——新振幅（Max − Min 格）下单段行程
        /// 约 (Max − Min) / FlyVSpeed 逻辑帧，取足 4 段以上以确保「上 / 下都出现过」在任何随机目标序列下都成立。</summary>
        [Test]
        public void LongCruise_StaysInBand_AndAlternatesDirections()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 9f); // 相对高度 = 8 ∈ 带内
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            int minAltitude = int.MaxValue;
            int maxAltitude = int.MinValue;
            int climbs = 0;
            int descents = 0;
            int prevSign = 0;

            for (int i = 0; i < CruiseLongRunTicks; i++)
            {
                bird.X = 24f;
                creatures.Step(world);

                int altitude = AltitudeAboveSurface(world, bird);
                minAltitude = Mathf.Min(minAltitude, altitude);
                maxAltitude = Mathf.Max(maxAltitude, altitude);

                int sign = bird.IntentVy > 0f ? 1 : (bird.IntentVy < 0f ? -1 : 0);
                if (sign != 0)
                {
                    if (prevSign != 0 && sign != prevSign)
                    {
                        if (sign > 0)
                        {
                            climbs++;
                        }
                        else
                        {
                            descents++;
                        }
                    }

                    prevSign = sign;
                }
            }

            Assert.GreaterOrEqual(minAltitude, BirdBrain.MinAltitudeCells - 1, "不应跌出带下限（±1）");
            Assert.LessOrEqual(maxAltitude, BirdBrain.MaxAltitudeCells + 1, "不应冲出带上限（±1）");
            Assert.Greater(climbs, 0, "应出现过向上巡航（目标抬高）");
            Assert.Greater(descents, 0, "应出现过向下巡航（目标降低）");
        }

        /// <summary>§12.5-④ 抽走脚下支柱（先造高柱让鸟进带，再清空该列）⇒ 高度主动回落——棘轮回归。</summary>
        [Test]
        public void SurfaceRemoved_BirdActivelyDescends()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone); // 世界底地板行恒在（保证抽柱后仍有基准）
            FillRect(world, 24, 24, 1, 20, Stone); // 高柱（顶面在行 20）

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 30f); // 相对高度 = 9 ∈ 带内
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            for (int i = 0; i < 20; i++) // 先在柱顶上方带内稳定
            {
                bird.X = 24f;
                creatures.Step(world);
            }

            float yBefore = bird.Y;

            for (int y = 1; y <= 20; y++) // 抽掉支柱（保留地板行 0，基准面变低）
            {
                world.Clear(24, y);
            }

            // 带顶已放宽到 50（§18），抽柱后相对高度 42 在带内仍合法 ⇒ 仅靠「基准面变低」不再必然触发下降。
            // 本用例改钉「棘轮修复机制」本身：显式把目标写成下限，则「相对高度 > 目标」⇒ 必定主动下降。
            // TargetAltitudeCells 是公开黑板槽位，测试显式写入合法（生产侧只由 BirdBrain 重摇 / 天花板钳制写入）。
            bird.TargetAltitudeCells = BirdBrain.MinAltitudeCells;

            for (int i = 0; i < 500; i++)
            {
                bird.X = 24f;
                creatures.Step(world);
            }

            Assert.Less(bird.Y, yBefore, "基准面变低后应主动回落（不再只升不降）");
            int altitude = AltitudeAboveSurface(world, bird);
            Assert.LessOrEqual(altitude, BirdBrain.MaxAltitudeCells + 1, "应回落到带内（±1）");
        }

        /// <summary>§12.5-⑤ <c>Y &gt; 世界高 − CeilingMarginCells</c> ⇒ 下压且 <c>TargetAltitudeCells</c> 收敛到下限
        /// （硬边界，不摇骰）。</summary>
        [Test]
        public void AboveCeiling_PressesDown_AndResetsTargetToMin()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 60f); // Y > 世界高 − CeilingMarginCells
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            creatures.Step(world); // 相位 0 ⇒ 首帧即思考

            Assert.Less(bird.IntentVy, 0f, "天花板是硬边界：应无条件下压");
            Assert.AreEqual(
                BirdBrain.MinAltitudeCells, bird.TargetAltitudeCells, "触发天花板时应把目标收敛到下限（不摇骰）");
        }

        /// <summary>§12.5-⑥ 正下方无基准（整列扫到世界底仍空）⇒ 悬停 <c>IntentVy == 0</c>（与 v2 一致、不摇骰）。</summary>
        [Test]
        public void NoSurfaceBelow_Hovers()
        {
            ParticleWorld world = NewWorld(); // 空世界：正下方无任何可栖面 / 水
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 30f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            creatures.Step(world); // 相位 0 ⇒ 首帧即思考

            Assert.AreEqual(0f, bird.IntentVy, "探不到基准应悬停");
            Assert.AreEqual(0, bird.TargetAltitudeCells, "无基准时不该摇骰写目标（保持默认 0）");
        }

        /// <summary>§12.5-⑦ 目标无效（0 / <c>MaxAltitudeCells + 1</c>）⇒ 下一个思考帧重摇到
        /// <c>[MinAltitudeCells, MaxAltitudeCells]</c>（兼顾新实体默认 0 与越界值）。</summary>
        [Test]
        public void InvalidTarget_IsRerolledIntoBand()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 10f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            bird.TargetAltitudeCells = 0;
            RunSteps(world, creatures, Creature.ThinkEvery); // 至少命中一个思考帧
            Assert.GreaterOrEqual(bird.TargetAltitudeCells, BirdBrain.MinAltitudeCells, "默认 0 应被重摇到带内");
            Assert.LessOrEqual(bird.TargetAltitudeCells, BirdBrain.MaxAltitudeCells, "默认 0 应被重摇到带内");

            bird.TargetAltitudeCells = BirdBrain.MaxAltitudeCells + 1;
            RunSteps(world, creatures, Creature.ThinkEvery);
            Assert.GreaterOrEqual(bird.TargetAltitudeCells, BirdBrain.MinAltitudeCells, "越界值应被重摇到带内");
            Assert.LessOrEqual(bird.TargetAltitudeCells, BirdBrain.MaxAltitudeCells, "越界值应被重摇到带内");
        }

        /// <summary>§12.5-⑧ 到达目标即重摇：把目标强制等于当前相对高度，随后 100 帧内相对高度应离开该值（不卡死）。</summary>
        [Test]
        public void ReachingTarget_RerollsInsteadOfSticking()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 9f); // 相对高度 = 8
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            int stuckAt = AltitudeAboveSurface(world, bird);
            bird.TargetAltitudeCells = stuckAt;

            bool left = false;
            for (int i = 0; i < 100 && !left; i++)
            {
                bird.X = 24f;
                creatures.Step(world);
                left = AltitudeAboveSurface(world, bird) != stuckAt;
            }

            Assert.IsTrue(left, "达成目标后应重摇新目标、相对高度离开原值（不粘死）");
        }

        // ============================== §17 只落树（地面不落） ==============================

        /// <summary>§17.3-① 无树的图（只有行 0 石底）：巡航计时归零后 300 帧内状态恒为 Fly、永不触地——
        /// 地面虽「可栖」但不算树，降落立即改成 2 秒重试（<see cref="BirdBrain.LandRetryTicks"/>）。</summary>
        [Test]
        public void Landing_OverBareGround_DoesNotLand_KeepsCruising()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone); // 只有石底，全图没有任何树

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 30f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 0; // 立即尝试降落

            for (int i = 0; i < 300; i++)
            {
                creatures.Step(world);
                Assert.AreEqual(BirdBrain.StateFly, bird.AiState, $"第 {i} 帧不应转 Perched（地面不落）");
                Assert.IsFalse(bird.IsGrounded, $"第 {i} 帧不应触地");
            }
        }

        /// <summary>§17.3-③ 树干露头（顶面上一格非固体、没有叶子盖着）也算「树」：可降落并转栖息。</summary>
        [Test]
        public void Landing_OverTrunkTop_Perches()
        {
            ParticleWorld world = NewWorld();
            FillRect(world, 24, 24, 4, 12, TreeTrunk); // 一根树干：行 12 是顶面，行 13 空（无叶盖）

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 20f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 0; // 立即尝试降落

            RunSteps(world, creatures, 200);

            Assert.IsTrue(bird.IsGrounded, "树干顶应可降落");
            Assert.AreEqual(BirdBrain.StatePerched, bird.AiState, "落地后应转入栖息");
            Assert.AreEqual(13f, bird.Y, 1e-3f, "树干顶在第 12 行 ⇒ 脚底应停在 13");
        }

        /// <summary>§17.3-④ 树冠在 <see cref="BirdBrain.LandProbeCells"/> 格之外 ⇒ 不降落、保持 Fly 并走 2 秒重试。
        /// 脚底行 37 时相对树冠（行 12）高度 = 24，正好是探针距离上界；探针自脚底下一行向下只扫 24 格（到行 13），
        /// 因此恰好看不到行 12 的树冠（§18 起带顶 50、探针仍 24，本用例钉住 Y 以保持这一隔离）。</summary>
        [Test]
        public void Landing_CanopyOutOfProbeRange_DoesNotLand()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 4, 44, 12, 12, TreeLeaf);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 37f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 0; // 立即尝试降落

            Assert.IsFalse(
                CreatureSensors.ProbeSurfaceBelow(
                    world, 24, 36, BirdBrain.LandProbeCells, out _, out _, out _),
                "行 12 的树冠应落在 24 格探针范围之外");

            // 钉住 Y：相对高度 24 恰是「树冠在探针外」的边界高度，放任竖直自由度下掉一格就会把树冠
            // 纳入探测范围而降落（那已由既有冠顶用例覆盖）。这里只隔离「探针范围外不落」这一条。
            for (int i = 0; i < 120; i++)
            {
                bird.Y = 37f;
                creatures.Step(world);
            }

            Assert.IsFalse(bird.IsGrounded, "树冠在 LandProbeCells 之外时不应降落");
            Assert.AreEqual(BirdBrain.StateFly, bird.AiState, "应保持巡航并每 2 秒重试");
        }

        // ============================== §18 带顶 50 + 天花板钳制 ==============================

        /// <summary>§18 用例的世界高度：H = 100 ⇒ 脚底最高 <c>100 − <see cref="BirdBrain.CeilingMarginCells"/></c>、
        /// 可达最大相对高度 <c>87 − surfaceRow</c>，与 <c>docs/plan-bird-ai.md</c> §18.1 的算例同口径。</summary>
        private const int OpenWorldHeight = 100;

        /// <summary>§18.4-1（低地形 S = 0）：带顶名义 50 完整可达——跑 3000 帧后应出现过 <c>altitude &gt; 24</c>（旧上限），
        /// 且全程 <c>altitude ≤ min(50, 87) + 1</c>、目标恒在 <c>[MinAltitudeCells, maxAltitude]</c>。
        /// 钉住 X 让基准面恒为石底行 0，隔离水平漂移带来的基准变化。</summary>
        [Test]
        public void AltitudeBand_OpenSky_ReachesAboveOldCap()
        {
            ParticleWorld world = new ParticleWorld(W, OpenWorldHeight, SeedValue);
            FillRows(world, 0, 0, Stone); // 石底行 0 ⇒ surfaceRow = 0 ⇒ maxAltitude = min(50, 87) = 50
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 2f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000; // 不进入降落分支

            int maxAltitude = Mathf.Min(
                BirdBrain.MaxAltitudeCells,
                OpenWorldHeight - BirdBrain.CeilingMarginCells - 1);
            Assert.AreEqual(50, maxAltitude, "低地形下带顶应完整取到名义 50");

            bool sawAboveOldCap = false;
            for (int i = 0; i < 3000; i++)
            {
                bird.X = 24f;
                creatures.Step(world);

                int altitude = AltitudeAboveSurface(world, bird);
                Assert.GreaterOrEqual(
                    bird.TargetAltitudeCells, BirdBrain.MinAltitudeCells, $"第 {i} 帧目标不应低于下限");
                Assert.LessOrEqual(
                    bird.TargetAltitudeCells, maxAltitude, $"第 {i} 帧目标不应超过可达上限 {maxAltitude}");
                Assert.LessOrEqual(altitude, maxAltitude + 1, $"第 {i} 帧不应冲出带上限（±1 物理容差）");
                sawAboveOldCap |= altitude > 24;
            }

            Assert.IsTrue(sawAboveOldCap, "3000 帧内应漂到旧上限 24 以上（带顶已放宽到 50）");
        }

        /// <summary>§18.4-2（高地形 S = 61）：可达净空只有 <c>87 − 61 = 26</c> 格 ⇒ 目标必须逐帧被钳制在 26 以内、
        /// 脚底不得明显越过天花板，且不出现「顶天花板 ⇒ 目标重置为 6 ⇒ 砸回带底」的反复。
        /// 说明：2 Hz 思考间隔内物理竖直位移可达 <c>FlyVSpeed × ThinkEvery = 0.8</c> 格，因此脚底允许 1 格容差；
        /// 「反复」按**进入天花板条件 <c>Y &gt; 世界高 − CeilingMarginCells</c> 的次数**计数（≤ 2 次兜底），
        /// 而不是按 <c>TargetAltitudeCells == MinAltitudeCells</c> 的帧数——合法随机目标本来就会抽到下限并被保持整段下降行程。</summary>
        [Test]
        public void AltitudeBand_HighTerrain_ClampedByCeiling()
        {
            ParticleWorld world = new ParticleWorld(W, OpenWorldHeight, SeedValue);
            const int surfaceRow = 61;
            FillRows(world, 0, surfaceRow, Stone); // 地表顶行 = 61
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 24f, 70f);
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;

            int clampedMax = OpenWorldHeight - BirdBrain.CeilingMarginCells - 1 - surfaceRow;
            Assert.AreEqual(26, clampedMax, "S = 61 时可达净空只有 26 格（远小于名义带顶 50）");

            int ceilingEntries = 0;
            bool wasAtCeiling = false;
            for (int i = 0; i < 400; i++)
            {
                bird.X = 24f;
                creatures.Step(world);

                Assert.GreaterOrEqual(
                    bird.TargetAltitudeCells, BirdBrain.MinAltitudeCells, $"第 {i} 帧目标不应低于下限");
                Assert.LessOrEqual(
                    bird.TargetAltitudeCells, clampedMax, $"第 {i} 帧目标应被钳制在可达上限 {clampedMax}");
                Assert.LessOrEqual(
                    bird.Y,
                    world.Height - BirdBrain.CeilingMarginCells + 1f,
                    $"第 {i} 帧脚底不应明显越过天花板（≤ 1 格物理余量）");

                bool atCeiling = bird.Y > world.Height - BirdBrain.CeilingMarginCells;
                if (atCeiling && !wasAtCeiling)
                {
                    ceilingEntries++;
                }

                wasAtCeiling = atCeiling;
            }

            Assert.LessOrEqual(ceilingEntries, 2, "带顶已被钳到净空，天花板只应兜底（≤ 2 次），不应反复触发");
        }

        /// <summary>§18.4-3 高空看不见地面猎物：鸟钉在 <c>altitude = 40 &gt; HuntDetectCells(24)</c>，猎物钉在地面、
        /// 同列 Idle ⇒ 盒中心竖直差 40 格，400 帧内绝不进入 <see cref="BirdBrain.StateHunt"/>。</summary>
        [Test]
        public void Hunt_AtHighAltitude_SeesNothing_NoHunt()
        {
            ParticleWorld world = new ParticleWorld(W, OpenWorldHeight, SeedValue);
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 20f, 41f); // 脚底行 41 ⇒ altitude = 40
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 20f, 1f);
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            for (int i = 0; i < 400; i++)
            {
                bird.X = 20f;
                bird.Y = 41f; // 钉住高度：altitude 恒 40，高于捕食视野 24
                prey.X = 20f;
                prey.HungerTicks = 0;
                creatures.Step(world);
                Assert.AreNotEqual(
                    BirdBrain.StateHunt, bird.AiState, $"第 {i} 帧高空不该发现地面猎物（视野仍为 24）");
            }
        }

        /// <summary>§18.4-3 对照：同场景把鸟降到 <c>altitude = 20 &lt; HuntDetectCells(24)</c> ⇒ 捕食预算
        /// （<c>HuntTimeoutTicks × 10</c> 帧）内进入过 <see cref="BirdBrain.StateHunt"/>。
        /// 猎物 Idle + 钉饥饿 / 计时，排除觅食扰动。</summary>
        [Test]
        public void Hunt_AtLowAltitude_DetectsAndHunts()
        {
            ParticleWorld world = new ParticleWorld(W, OpenWorldHeight, SeedValue);
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 20f, 21f); // 脚底行 21 ⇒ altitude = 20
            bird.AiState = BirdBrain.StateFly;
            bird.StateTicksLeft = 100000;
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 20f, 1f);
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;

            bool entered = false;
            for (int i = 0; i < BirdBrain.HuntTimeoutTicks * 10 && !entered; i++)
            {
                bird.X = 20f;
                bird.Y = 21f; // 钉住高度：altitude 恒 20，落进捕食视野
                prey.X = 20f;
                prey.HungerTicks = 0;
                if (bird.AiState == BirdBrain.StateFly)
                {
                    bird.StateTicksLeft = 100000; // 不降落，保持巡航扫视野
                }

                creatures.Step(world);
                entered = bird.AiState == BirdBrain.StateHunt;
            }

            Assert.IsTrue(entered, "低空（altitude 20）应在捕食预算内发现并进入 Hunt");
        }

        // ============================== 生物质觅食（v9） ==============================

        /// <summary>饥饿 + 世界有生物质 ⇒ 优先生物质、不捕猎：正下方一格生物质在第 10 个思考帧被清除，
        /// 同列视野内的 Idle 兔子全程不诱发 Hunt（世界级门控）。</summary>
        [Test]
        public void Hungry_WithBiomass_EatsCell_AndPrefersBiomassOverPrey()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 20f, 7f);
            world.SetWithShade(20, 6, Biomass, 0);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 20f, 1f);
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;
            bird.HungerTicks = BirdBrain.HungryThreshold;

            Assert.AreEqual(1, world.BiomassCount, "夹具应先有一格生物质");
            bool enteredHunt = false;
            for (int i = 0; i < 11; i++)
            {
                bird.X = 20f;
                bird.Y = 7f;
                prey.X = 20f;
                prey.HungerTicks = 0; // 钉住饥饿：兔子不转入觅食移动
                creatures.Step(world);
                enteredHunt |= bird.AiState == BirdBrain.StateHunt;
            }

            Assert.IsFalse(enteredHunt, "世界仍有生物质时饥饿鸟不得进入 Hunt（即便视野里有猎物）");
            Assert.AreEqual(0, world.BiomassCount, "第 10 个思考帧应吃掉正下方的生物质");
            Assert.AreEqual(0, CountType(world, Biomass), "场上不应再剩生物质格");
            Assert.Less(bird.HungerTicks, BirdBrain.HungryThreshold, "进食应恢复饥饿值");
        }

        /// <summary>无生物质的世界里饥饿鸟回退到既有捕猎：视野内的 Idle 兔子在捕食预算内诱发 Hunt。</summary>
        [Test]
        public void Hungry_WithoutBiomass_FallsBackToHunt()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 20f, 7f);
            RabbitCreature prey = creatures.Spawn(new RabbitCreature(), world, 20f, 1f);
            prey.AiState = RabbitBrain.StateIdle;
            prey.StateTicksLeft = 100000;
            bird.HungerTicks = BirdBrain.HungryThreshold;

            bool entered = false;
            for (int i = 0; i < BirdBrain.HuntTimeoutTicks * 10 && !entered; i++)
            {
                bird.X = 20f;
                bird.Y = 7f;
                prey.X = 20f;
                prey.HungerTicks = 0; // 钉住饥饿：兔子保持 Idle、不移动
                if (bird.AiState == BirdBrain.StateFly)
                {
                    bird.StateTicksLeft = 100000; // 不降落，保持巡航扫视野
                }

                creatures.Step(world);
                entered = bird.AiState == BirdBrain.StateHunt;
            }

            Assert.AreEqual(0, world.BiomassCount, "本用例没有生物质");
            Assert.IsTrue(entered, "世界没有生物质时饥饿鸟应回退到既有捕猎");
        }

        /// <summary>不饥饿的鸟不碰生物质：正下方一格生物质 100 帧后仍在，状态不是 SeekFood / Eat。</summary>
        [Test]
        public void NotHungry_DoesNotEatBiomass()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 20f, 7f);
            world.SetWithShade(20, 6, Biomass, 0);
            bird.HungerTicks = 0;

            Assert.AreEqual(1, world.BiomassCount, "夹具应先有一格生物质");
            for (int i = 0; i < 100; i++)
            {
                bird.X = 20f;
                bird.Y = 7f;
                Assert.AreNotEqual(BirdBrain.StateSeekFood, bird.AiState, $"第 {i} 帧不饥饿的鸟不该进入觅食");
                Assert.AreNotEqual(BirdBrain.StateEat, bird.AiState, $"第 {i} 帧不饥饿的鸟不该进食");
                creatures.Step(world);
            }

            Assert.AreEqual(1, world.BiomassCount, "不饥饿时生物质应原样保留");
            Assert.AreEqual(1, CountType(world, Biomass), "生物质格数应仍为 1");
            Assert.AreNotEqual(BirdBrain.StateSeekFood, bird.AiState);
            Assert.AreNotEqual(BirdBrain.StateEat, bird.AiState);
        }
    }
}
