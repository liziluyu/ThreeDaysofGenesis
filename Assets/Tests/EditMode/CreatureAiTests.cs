using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 动物 AI（<see cref="RabbitBrain"/> / <see cref="FishBrain"/> / <see cref="CreatureSensors"/> /
    /// <see cref="Creature.ApplyLocomotion"/>）的行为测试：思考相位错峰、确定性重放、世界随机流隔离、
    /// 运动层意图限幅与朝向迟滞、悬崖折返、行走撞墙（本帧横向被挡信号 + 三态墙探针：
    /// 一格高起跳 / 不可翻越掉头 / 世界边界掉头 / 开阔地不反向意图）、威胁扫描与逃命、
    /// 接触火 / 溺死 / 搁浅、死亡物质化对世界随机流的
    /// 消耗，以及鱼的水面约束与 Flee 退出。
    /// </summary>
    /// <remarks>
    /// <para>全部按设计定稿 <c>docs/design-creature-ai.md</c> §11 编写。地形用生产注册路径
    /// （<see cref="ParticleRegistration.RegisterAll"/>）以便按 <see cref="ParticleIds"/> 常量摆放；
    /// 火也走生产表（同 <c>FireParticleTests</c>）。</para>
    /// <para>机制类测试（意图限幅 / 朝向迟滞 / 悬崖）用 <see cref="ProbeCreature"/>——它没有 Brain，
    /// 意图不会被决策覆盖，且能通过派生类暴露 protected 的 <see cref="Creature.ShouldThink"/>；
    /// 物种类测试（思考错峰 / 逃命 / 溺死 / 搁浅 / 水面约束）用真实物种。</para>
    /// <para>为避免粒子世界演化干扰（火被熄灭 / 水流动），多数测试只调 <c>creatures.Step(world)</c>
    /// 而不调 <c>world.StepLogic()</c>——动物层本就只读世界。</para>
    /// </remarks>
    public sealed class CreatureAiTests
    {
        private const int W = 32;
        private const int H = 32;
        private const uint SeedValue = 20261011u;
        private const float Tol = 1e-4f;

        private static ushort Sand
        {
            get { return ParticleIds.Sand; }
        }

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

        private static ushort Grass
        {
            get { return ParticleIds.Grass; }
        }

        private static ushort Flower
        {
            get { return ParticleIds.Flower; }
        }

        private static ushort GrassSeed
        {
            get { return ParticleIds.GrassSeed; }
        }

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
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

        /// <summary>
        /// 跑到「**下一次 <c>creatures.Step</c> 恰好是思考帧**」的位置：思考门控读的是 Step **开始前**的
        /// <c>LogicTicks</c>（Step 末尾才自增），因此要对齐到 <c>LogicTicks % ThinkEvery == SensePhase</c>——
        /// 相位 0 即 <c>≡ 0 (mod ThinkEvery)</c>，不是 <c>≡ ThinkEvery − 1</c>（差一格会让那次 Step 的 Brain
        /// 根本不跑，断言于是空过）。
        /// </summary>
        private static void RunToBeforeThinkFrame(ParticleWorld world, CreatureWorld creatures)
        {
            RunSteps(world, creatures, Creature.ThinkEvery - creatures.LogicTicks % Creature.ThinkEvery);
        }

        /// <summary>感知扫描中心所在的行（<c>floor(Y + Height/2)</c>，与 <c>RabbitBrain.Think</c> 同一口径）。</summary>
        private static int BodyCenterRow(Creature creature)
        {
            return Mathf.FloorToInt(creature.Y + creature.Height * 0.5f);
        }

        /// <summary>身体覆盖矩形的列 / 行闭区间，用**与 <c>Creature.IsFullyBuried</c> 同一套口径**算
        /// （<c>floor(x ∓ width/2 ± ε)</c> / <c>floor(y + ε .. y + height − ε)</c>），供压死测试逐格摆放。</summary>
        private static void BodyRect(Creature creature, out int colMin, out int colMax, out int rowMin, out int rowMax)
        {
            const float eps = 1e-4f;
            colMin = Mathf.FloorToInt(creature.X - creature.Width * 0.5f + eps);
            colMax = Mathf.FloorToInt(creature.X + creature.Width * 0.5f - eps);
            rowMin = Mathf.FloorToInt(creature.Y + eps);
            rowMax = Mathf.FloorToInt(creature.Y + creature.Height - eps);
        }

        /// <summary>
        /// 把 <paramref name="creature"/> 连同一圈外壳都填成 <paramref name="typeId"/>：身体每一格都被盖住
        /// （满足压死判据），外壳则堵死横向 / 竖直逃逸路线——只盖住覆盖矩形本身时，兔子会在宽限期内走出去、
        /// 判据永远攒不满，测试就不再确定。
        /// </summary>
        private static void BuryCreature(ParticleWorld world, Creature creature, ushort typeId)
        {
            BodyRect(creature, out int colMin, out int colMax, out int rowMin, out int rowMax);
            FillRect(world, colMin - 1, colMax + 1, rowMin - 1, rowMax + 1, typeId);
        }

        /// <summary>
        /// 把唯一一团火重新钉在兔子**当前列的正上方 <paramref name="distance"/> 格**（先清掉上一次钉的那格），
        /// 并把新格写回 <paramref name="fireX"/> / <paramref name="fireY"/>（初值传 −1 表示还没摆过火）。
        /// </summary>
        /// <remarks>
        /// 距离按**扫描中心格**计（<c>dx = 0</c>、<c>dy = distance</c> ⇒ 距离平方恰为 <c>distance²</c>），
        /// 因此断言可以用 <see cref="Creature.ThreatRadius"/> 相对表达而不用写死半径数字。
        /// 兔子会横向跑开，所以在每个思考帧前重新钉一次才能让「同列」这一条件持续成立。
        /// </remarks>
        private static void PinFireAbove(ParticleWorld world, Creature rabbit, int distance, ref int fireX, ref int fireY)
        {
            if (fireX >= 0)
            {
                world.Clear(fireX, fireY);
            }

            fireX = Mathf.FloorToInt(rabbit.X);
            fireY = BodyCenterRow(rabbit) + distance;
            world.Set(fireX, fireY, Fire);
        }

        private static void ClearRect(ParticleWorld world, int x0, int x1, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    world.Clear(x, y);
                }
            }
        }

        /// <summary>统计全世界里某 typeId 的格数（测试断言「恰好清除一格」用）。</summary>
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

        /// <summary>无大脑的探针实体：意图不会被决策覆盖，并暴露 protected 的 <see cref="Creature.ShouldThink"/>。</summary>
        private sealed class ProbeCreature : Creature
        {
            private readonly float _accel;
            private readonly float _gravity;
            private readonly float _hop;

            public ProbeCreature(float width, float height, float accel)
                : this(width, height, accel, 0f, 0f)
            {
            }

            public ProbeCreature(float width, float height, float accel, float gravity, float hop)
                : base(width, height)
            {
                _accel = accel;
                _gravity = gravity;
                _hop = hop;
            }

            protected override float Gravity
            {
                get { return _gravity; }
            }

            protected override float MaxFallSpeed
            {
                get { return 10f; }
            }

            protected override float MoveAccel
            {
                get { return _accel; }
            }

            protected override float HopVelocity
            {
                get { return _hop; }
            }

            /// <summary>是否跳过悬崖守卫（对齐兔子 Flee 态 <see cref="Creature.IgnoresCliffGuard"/>）。默认 false。</summary>
            public bool SkipCliffGuard;

            protected override bool IgnoresCliffGuard
            {
                get { return SkipCliffGuard; }
            }

            /// <summary>是否具备跨坎能力（对齐兔子的 <see cref="Creature.CanStepUp"/>）。默认 false。</summary>
            public bool StepUp;

            protected override bool CanStepUp
            {
                get { return StepUp; }
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

        /// <summary>兔子的重力（格 / 逻辑帧²）：<see cref="Creature.Gravity"/> 是 protected 取不到，这里抄同值常量。</summary>
        private const float RabbitGravity = 0.24f;

        /// <summary>
        /// 生成一个**贴地向右行走的探针**：宽 1.5 / 高 1（对齐兔子）、加速度与起跳取自
        /// <see cref="RabbitBrain"/>、重力同兔子（0.24）。
        /// </summary>
        /// <remarks>没有 Brain ⇒ 意图不会被决策覆盖，因而能稳定地撞进 <see cref="Creature.StepPhysics"/>
        /// 的真实横向碰撞（撞墙信号由此进入 ④ 段）——「行走撞墙」场景需要的就是这种不会被决策打断的行走。</remarks>
        private static ProbeCreature SpawnWalkRight(ParticleWorld world, CreatureWorld creatures, float x)
        {
            ProbeCreature walker = creatures.Spawn(
                new ProbeCreature(1.5f, 1f, RabbitBrain.Accel, RabbitGravity, RabbitBrain.HopVelocity),
                world,
                x,
                1f);
            walker.VelocityX = RabbitBrain.WalkSpeed;
            walker.IntentVx = RabbitBrain.WalkSpeed;
            return walker;
        }

        // ============================== 1. 思考相位错峰 ==============================

        /// <summary>
        /// 连生 3 只兔子相位应为 0/1/2；门控只在 <c>LogicTicks % ThinkEvery == SensePhase</c> 的帧放行。
        /// </summary>
        [Test]
        public void ThinkPhase_StaggersBySpawnOrder_AndGateHitsOnlyOnPhaseFrame()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature r0 = creatures.Spawn(new RabbitCreature(), world, 4f, 5f);
            RabbitCreature r1 = creatures.Spawn(new RabbitCreature(), world, 8f, 5f);
            RabbitCreature r2 = creatures.Spawn(new RabbitCreature(), world, 12f, 5f);

            Assert.AreEqual((byte)0, r0.SensePhase, "第 1 只兔子相位应为 0");
            Assert.AreEqual((byte)1, r1.SensePhase, "第 2 只兔子相位应为 1");
            Assert.AreEqual((byte)2, r2.SensePhase, "第 3 只兔子相位应为 2");

            ParticleWorld gateWorld = NewWorld();
            FillRows(gateWorld, 0, 0, Stone);
            CreatureWorld gate = new CreatureWorld(SeedValue);
            ProbeCreature probe = gate.Spawn(new ProbeCreature(1f, 1f, 0f), gateWorld, 5f, 1f);
            probe.SensePhase = 3;

            for (int tick = 0; tick < 30; tick++)
            {
                bool expected = tick % Creature.ThinkEvery == 3;
                Assert.AreEqual(expected, probe.ExposeShouldThink(gate), $"第 {tick} 逻辑帧的门控结果应命中相位 3");
                gate.Step(gateWorld);
            }
        }

        // ============================== 2. 确定性重放 ==============================

        /// <summary>
        /// 同 seed 的两个 CreatureWorld + 同种子两个 ParticleWorld，各放一兔一鱼跑 300 逻辑帧，
        /// 每只的位置 / 速度 / 状态 / 朝向 / 致死计时器逐位一致。
        /// </summary>
        [Test]
        public void Determinism_SameSeed_ReplayBitExact()
        {
            ParticleWorld wa = NewWorld();
            ParticleWorld wb = NewWorld();
            FillRows(wa, 0, 3, Sand);
            FillRows(wb, 0, 3, Sand);
            FillRect(wa, 8, 24, 4, 14, Water);
            FillRect(wb, 8, 24, 4, 14, Water);

            CreatureWorld ca = new CreatureWorld(SeedValue);
            CreatureWorld cb = new CreatureWorld(SeedValue);
            RabbitCreature rabbitA = ca.Spawn(new RabbitCreature(), wa, 8f, 20f);
            FishCreature fishA = ca.Spawn(new FishCreature(), wa, 16f, 8f);
            RabbitCreature rabbitB = cb.Spawn(new RabbitCreature(), wb, 8f, 20f);
            FishCreature fishB = cb.Spawn(new FishCreature(), wb, 16f, 8f);

            for (int i = 0; i < 300; i++)
            {
                wa.StepLogic();
                ca.Step(wa);
                wb.StepLogic();
                cb.Step(wb);
            }

            Assert.AreEqual(ca.Count, cb.Count, "同 seed 同操作下存活数应一致");
            AssertCreatureEqual(rabbitA, rabbitB, "兔子");
            AssertCreatureEqual(fishA, fishB, "鱼");
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
            Assert.AreEqual(a.BeachedTicks, b.BeachedTicks, $"{context} BeachedTicks 应逐位一致");
        }

        // ============================== 3. 不消耗世界 RNG ==============================

        /// <summary>
        /// 有 / 无动物两种跑法各自推进 300 帧后，对两个 ParticleWorld 抽样同样次数的 <c>NextUInt</c>
        /// 必须逐位一致——若动物偷摇一次世界骰，粒子流会立刻分叉。
        /// </summary>
        /// <remarks>
        /// <para><b>场景必须在窗口内无死亡、无进食</b>：动物死亡会物质化出生物质，生物质落稳后每逻辑帧摇一次
        /// 腐败骰（消耗世界随机流，见 <c>DeathMaterialization_DoesConsumeWorldStream_Deterministically</c>）；
        /// 兔子进食（<c>world.Clear</c> 一格草）也会触发既有世界规则的骰。两者都是动物层**合法**写世界引起的
        /// 副作用，却会让本测试的「逐位一致」变成假失败。为此把场景改成隔离的：</para>
        /// <list type="bullet">
        /// <item>鱼关进**四周石砌、水灌满水的封闭水舱**（底 / 盖 / 两堵侧墙把水封死在舱内）：水不会流走，
        /// 鱼既游不出去、也永远淹没（<c>BeachedTicks</c> 归零），搁浅致死的门永远打不开；</item>
        /// <item>兔子放在水舱**外面**的实心沙地上；侧墙从舱底一直砌到舱盖，兔子既走不进去、
        /// 也（起跳远低于墙高）跳不过去，因此永远不可能淹没，溺死门打不开；</item>
        /// <item>全场**一格草都没有**，兔子的 <c>Eat</c>（动物层唯一的写格路径）无从触发；</item>
        /// <item>300 帧远小于饿死阈值 1200 帧，兔子不可能饿死。</item>
        /// </list>
        /// <para>于是窗口内两种动物都不写世界、不死亡（实测：鱼全程淹没、兔子 <c>SubmergedTicks</c> 恒 0、
        /// 无生物质产生，300 帧内两条随机流逐位一致）。</para>
        /// </remarks>
        [Test]
        public void RngIsolation_CreaturesDoNotConsumeWorldStream()
        {
            ParticleWorld solo = NewWorld();
            ParticleWorld withAnimals = NewWorld();
            FillRows(solo, 0, 3, Sand);
            FillRows(withAnimals, 0, 3, Sand);

            // 封闭水舱：底 y = 4、盖 y = 14、侧墙 x = 8 / 24（y 5..13），内部 x 9..23 × y 5..13 灌满水。
            FillRect(solo, 8, 24, 4, 4, Stone);
            FillRect(withAnimals, 8, 24, 4, 4, Stone);
            FillRect(solo, 8, 24, 14, 14, Stone);
            FillRect(withAnimals, 8, 24, 14, 14, Stone);
            FillRect(solo, 8, 8, 5, 13, Stone);
            FillRect(withAnimals, 8, 8, 5, 13, Stone);
            FillRect(solo, 24, 24, 5, 13, Stone);
            FillRect(withAnimals, 24, 24, 5, 13, Stone);
            FillRect(solo, 9, 23, 5, 13, Water);
            FillRect(withAnimals, 9, 23, 5, 13, Water);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            creatures.Spawn(new RabbitCreature(), withAnimals, 4f, 20f);
            creatures.Spawn(new FishCreature(), withAnimals, 16f, 10f);

            for (int i = 0; i < 300; i++)
            {
                solo.StepLogic();
                withAnimals.StepLogic();
                creatures.Step(withAnimals);
            }

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(solo.NextUInt(), withAnimals.NextUInt(), "动物层不该消耗世界随机流");
            }
        }

        // ================== 3b. 死亡物质化 → 腐败，会消耗世界随机流 ==================

        /// <summary>
        /// 死亡 → 生物质 → 腐败这条链**确实**会消耗世界随机流（设计变更后的新契约，见
        /// <c>docs/design-creature-ai.md</c> §16.1）：B 世界（兔子在窗口内溺死）与 A 世界（无动物）跑到生物质
        /// 落稳并开始摇腐败骰之后，两次 <c>NextUInt</c> 抽样必须不同；同 seed 的两个 B 世界则逐位一致（可重放）。
        /// </summary>
        /// <remarks>
        /// <para><b>场景：</b>实心沙地 + 两堵石墙围出的水池（水 x 13..19 × y 4..6）。兔子放进水里后缓慢下沉贴底，
        /// 身体中心持续没顶 ⇒ 第 161 帧（<see cref="Creature.DrownTicksLimit"/> + 1）溺死；清扫阶段
        /// <see cref="CreatureDeathMaterializer"/> 把尸体轮廓逐格写成生物质，生物质落稳满
        /// <see cref="BiomassParticle.SettleFrames"/> 帧后每逻辑帧摇一次腐败骰——这条骰消耗世界随机流。
        /// 400 帧足够覆盖「溺死 → 落稳 → 开始摇腐败」（实测：第 161 帧溺死于 (19.25, 4)，第 192 帧起 A / B 分叉）。</para>
        /// <para>两个 B 世界同 seed、同操作序列，逐位一致说明新行为没有引入额外 / 未受控的随机源。</para>
        /// </remarks>
        [Test]
        public void DeathMaterialization_DoesConsumeWorldStream_Deterministically()
        {
            ParticleWorld withoutAnimal = NewWorld();
            ParticleWorld withDeath1 = NewWorld();
            ParticleWorld withDeath2 = NewWorld();
            FillRows(withoutAnimal, 0, 3, Sand);
            FillRows(withDeath1, 0, 3, Sand);
            FillRows(withDeath2, 0, 3, Sand);
            FillRect(withoutAnimal, 12, 12, 4, 10, Stone);
            FillRect(withDeath1, 12, 12, 4, 10, Stone);
            FillRect(withDeath2, 12, 12, 4, 10, Stone);
            FillRect(withoutAnimal, 20, 20, 4, 10, Stone);
            FillRect(withDeath1, 20, 20, 4, 10, Stone);
            FillRect(withDeath2, 20, 20, 4, 10, Stone);
            FillRect(withoutAnimal, 13, 19, 4, 6, Water);
            FillRect(withDeath1, 13, 19, 4, 6, Water);
            FillRect(withDeath2, 13, 19, 4, 6, Water);

            CreatureWorld deathWorld1 = new CreatureWorld(SeedValue);
            CreatureWorld deathWorld2 = new CreatureWorld(SeedValue);
            deathWorld1.Spawn(new RabbitCreature(), withDeath1, 16f, 5f);
            deathWorld2.Spawn(new RabbitCreature(), withDeath2, 16f, 5f);

            for (int i = 0; i < 400; i++)
            {
                withoutAnimal.StepLogic();
                withDeath1.StepLogic();
                withDeath2.StepLogic();
                deathWorld1.Step(withDeath1);
                deathWorld2.Step(withDeath2);
            }

            Assert.AreEqual(0, deathWorld1.Count, "兔子应在窗口内溺死并被清扫（否则本测试根本没测到死亡物质化）");
            Assert.Greater(
                CountType(withDeath1, ParticleIds.Biomass) + CountType(withDeath1, ParticleIds.Humus),
                0,
                "尸体应已物质化成生物质（或其腐败残渣腐殖质）");
            Assert.AreEqual(
                0,
                CountType(withoutAnimal, ParticleIds.Biomass) + CountType(withoutAnimal, ParticleIds.Humus),
                "无动物的 A 世界不该凭空出现生物质 / 腐殖质");

            bool diverged = false;
            for (int i = 0; i < 64; i++)
            {
                uint a = withoutAnimal.NextUInt();
                uint b1 = withDeath1.NextUInt();
                uint b2 = withDeath2.NextUInt();
                Assert.AreEqual(b1, b2, "同 seed 下两个死亡场景的粒子随机流应逐位一致（新行为可重放）");
                if (a != b1)
                {
                    diverged = true;
                }
            }

            Assert.IsTrue(diverged, "生物质腐败应消耗世界随机流：B 的抽取序列必须与 A 分叉");
        }

        // ============================== 4. 意图限幅 ==============================

        /// <summary>从静止给 <c>IntentVx = 0.06</c>：每帧增量不超过 Accel(0.02)，且最终恰好等于 0.06（不超调）。</summary>
        [Test]
        public void IntentRamp_ClampedByAccel_AndSettlesExactly()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature probe = creatures.Spawn(new ProbeCreature(1f, 1f, 0.02f), world, 5f, 1f);
            probe.IntentVx = 0.06f;

            float prev = probe.VelocityX;
            for (int i = 0; i < 8; i++)
            {
                creatures.Step(world);
                float delta = probe.VelocityX - prev;
                Assert.LessOrEqual(delta, 0.02f + 1e-6f, "每帧水平速度增量不应超过 Accel");
                prev = probe.VelocityX;
            }

            Assert.AreEqual(0.06f, probe.VelocityX, 1e-6f, "意图逼近到位后应恰好等于目标，不超调");
        }

        // ============================== 5. 朝向迟滞 ==============================

        /// <summary>意图在 ±0.0005 抖动时不翻面；持续 −0.06 后 <c>Facing</c> 才变 −1。</summary>
        [Test]
        public void FacingHysteresis_TinyIntentDoesNotFlip_PersistentIntentDoes()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature probe = creatures.Spawn(new ProbeCreature(1f, 1f, 0.02f), world, 5f, 1f);

            probe.IntentVx = 0.0005f;
            creatures.Step(world);
            Assert.AreEqual(1, probe.Facing, "微小正向意图不该改变朝向");

            probe.IntentVx = -0.0005f;
            creatures.Step(world);
            Assert.AreEqual(1, probe.Facing, "微小反向意图不该改变朝向");

            probe.IntentVx = -0.06f;
            creatures.Step(world);
            Assert.AreEqual(-1, probe.Facing, "持续反向意图应把朝向翻到 −1");
        }

        // ============================== 6. 悬崖折返 ==============================

        /// <summary>平台边缘的漫步者会被悬崖守卫折返，且全程不曾掉下平台。</summary>
        [Test]
        public void CliffGuard_ReversesIntent_AndNeverFalls()
        {
            ParticleWorld world = NewWorld();
            FillRect(world, 0, 15, 0, 9, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature probe = creatures.Spawn(new ProbeCreature(1f, 1f, 0.02f), world, 12f, 10f);
            probe.IntentVx = 0.06f;

            float minY = probe.Y;
            bool reversed = false;
            for (int i = 0; i < 240; i++)
            {
                creatures.Step(world);
                if (probe.Y < minY)
                {
                    minY = probe.Y;
                }

                if (probe.IntentVx < 0f)
                {
                    reversed = true;
                }
            }

            Assert.IsTrue(reversed, "走到平台边缘后意图应被悬崖守卫折返");
            Assert.GreaterOrEqual(minY, 10f - 1e-3f, "悬崖守卫应让漫步者永远不会掉下平台");
            Assert.LessOrEqual(probe.X, 16f, "漫步者不应越过平台右缘");
        }

        // ============================== 7. 威胁方向与逃命 ==============================

        /// <summary>火在右侧时探针返回右侧那格（<c>fireX &gt; cx</c>），兔子在下一个思考帧反向逃跑（<c>IntentVx &lt; 0</c>）。</summary>
        [Test]
        public void Threat_ProbeReportsRight_AndRabbitFleesLeft()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(13, 1, Fire);

            bool found = CreatureSensors.FindNearestFire(
                world,
                10,
                1,
                Creature.ThreatRadius,
                out int fireX,
                out int fireY);
            Assert.IsTrue(found, "右侧 3 格的火应被扫到");
            Assert.AreEqual(13, fireX, "应返回那格火的 x");
            Assert.AreEqual(1, fireY, "应返回那格火的 y");
            Assert.Greater(fireX - 10, 0, "火在右侧 → 返回火格的 dx > 0 ⇒ 兔子该朝反方向（左）逃");

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            creatures.Step(world);

            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "感知到火应进入 Flee 态");
            Assert.Less(rabbit.IntentVx, 0f, "应背向右侧的火向左逃（IntentVx < 0）");
        }

        /// <summary>火恰与扫描中心同列（<c>fireX == cx</c>）时没有「背向」可言——兔子仍须跑起来，不能站在原地逃命。</summary>
        [Test]
        public void Threat_SameColumn_RabbitStillRuns()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(10, 3, Fire); // 与兔子同列、正上方 2 格

            bool found = CreatureSensors.FindNearestFire(
                world,
                10,
                1,
                Creature.ThreatRadius,
                out int fireX,
                out int fireY);
            Assert.IsTrue(found, "同列上方的火应被扫到");
            Assert.AreEqual(10, fireX, "同列 ⇒ 火格 dx == 0，方向无意义，由调用方摇一侧");
            Assert.AreEqual(3, fireY, "应返回正上方那格火的 y");

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            creatures.Step(world);

            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "同列有火也应进入 Flee 态");
            Assert.AreEqual(RabbitBrain.FleeSpeed, Mathf.Abs(rabbit.IntentVx), Tol,
                "同列时应随机选一侧以逃命速度跑开，绝不原地不动");
        }

        /// <summary>
        /// **最近优先**：远处那团火放在**更低的行**（固定扫描序会先命中它），近处那团与兔子同排、在右侧。
        /// 兔子必须背离**近处**那团火向左逃——旧探针返回先见者，会朝右跑向更近的火。
        /// </summary>
        [Test]
        public void Threat_NearestFire_DecidesFleeDirection()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 2, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 3f);

            world.Set(11, 2, Fire); // 更远（dx = −5, dy = −1）、行更低 → 固定扫描序先命中
            world.Set(19, 3, Fire); // 更近（dx = +3, dy = 0）、与兔子同排

            creatures.Step(world);

            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "两团火都在进入半径内，应进入逃命");
            Assert.Less(rabbit.IntentVx, 0f, "应背离**最近**（同排右侧）那团火向左逃");
            Assert.AreEqual(-1, rabbit.Facing, "朝向也应随逃跑方向翻到 −1");
        }

        /// <summary>
        /// **同列不重摇**：把火逐帧钉在兔子当前列的正上方（「同列」因此在连续 6 个思考帧上持续成立），
        /// 进入逃命后逃跑方向必须稳定——旧实现每个思考帧都重摇一次，兔子会在火下方每 0.5 秒翻一次身。
        /// </summary>
        [Test]
        public void Threat_SameColumn_KeepsFleeDirectionAcrossThinks()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 1f);
            int fireX = -1;
            int fireY = -1;

            PinFireAbove(world, rabbit, 2, ref fireX, ref fireY);
            creatures.Step(world);
            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "同列有火应进入 Flee");

            float fleeVx = rabbit.IntentVx;
            Assert.AreEqual(RabbitBrain.FleeSpeed, Mathf.Abs(fleeVx), Tol, "同列应摇一侧、以逃命速度跑开");

            int facing = rabbit.Facing;
            Assert.AreNotEqual(0, facing, "逃跑方向已定，朝向不该为 0");

            int facingFlips = 0;
            for (int think = 0; think < 6; think++)
            {
                for (int tick = 0; tick < Creature.ThinkEvery; tick++)
                {
                    PinFireAbove(world, rabbit, 2, ref fireX, ref fireY);
                    creatures.Step(world);

                    Assert.Greater(rabbit.IntentVx * fleeVx, 0f, "同列持续成立时不该把逃跑方向摇反");
                    if (rabbit.Facing != facing)
                    {
                        facingFlips++;
                        facing = rabbit.Facing;
                    }
                }
            }

            Assert.LessOrEqual(facingFlips, 1, "逃跑方向稳定 ⇒ 朝向最多翻一次（入口那次）");
        }

        /// <summary>
        /// **脱离滞回 + 最短时长**：圈内（<c>ThreatRadius − 2</c>）有火 → 进 Flee；最短逃命时长走完后，
        /// 火挪到 <c>ThreatRadius + 1</c>（进入半径外、脱离半径内）仍保持 Flee；
        /// 再挪到滞回之外（<c>ThreatRadius + FleeExitMargin + 1</c>）才回 Wander。
        /// </summary>
        [Test]
        public void Threat_FleeHysteresis_ExitsOnlyBeyondMargin()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 1f);
            int fireX = -1;
            int fireY = -1;

            PinFireAbove(world, rabbit, Creature.ThreatRadius - 2, ref fireX, ref fireY);
            creatures.Step(world);
            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "进入半径内（−2 格）的火应触发逃命");
            Assert.AreEqual(
                RabbitBrain.FleeMinTicks - 1,
                rabbit.StateTicksLeft,
                "进入逃命应写上最短逃命时长（入场逻辑帧的运动层已递减 1）");

            // 先让最短逃命时长走完：此后「仍是 Flee」只能归因于滞回，而不是计时器还没到点。
            RunSteps(world, creatures, RabbitBrain.FleeMinTicks);
            Assert.AreEqual(0, rabbit.StateTicksLeft, "最短逃命时长此时应已走完");
            float fleeVx = rabbit.IntentVx;

            RunToBeforeThinkFrame(world, creatures);
            PinFireAbove(world, rabbit, Creature.ThreatRadius + 1, ref fireX, ref fireY);
            creatures.Step(world);
            Assert.AreEqual(
                RabbitBrain.StateFlee,
                rabbit.AiState,
                "进入半径外一格、脱离半径内（+1 格）的火不该让兔子退出逃命");
            Assert.AreEqual(fleeVx, rabbit.IntentVx, Tol, "滞回期间应保持逃跑方向");

            RunToBeforeThinkFrame(world, creatures);
            PinFireAbove(world, rabbit, Creature.ThreatRadius + RabbitBrain.FleeExitMargin + 1, ref fireX, ref fireY);
            creatures.Step(world);
            Assert.AreEqual(
                RabbitBrain.StateWander,
                rabbit.AiState,
                "火出（放大后的）脱离半径、且最短逃命时长已走完 → 回 Wander");
        }

        /// <summary>
        /// **最短逃命时长**：圈内（<c>ThreatRadius − 2</c>）的火让兔子进入 Flee，火随即清掉；
        /// 最短逃命时长走完前既不退出也不改方向，走完后的第一个思考帧才回 Wander。
        /// </summary>
        [Test]
        public void Threat_FleeLastsAtLeastFleeMinTicks()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 1f);
            int fireX = -1;
            int fireY = -1;

            PinFireAbove(world, rabbit, Creature.ThreatRadius - 2, ref fireX, ref fireY);
            creatures.Step(world);
            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "进入半径内的火应触发逃命");

            // 火立刻清掉（此后连滞回半径内也没有任何火）。
            world.Clear(fireX, fireY);
            float fleeVx = rabbit.IntentVx;
            Assert.AreNotEqual(0f, fleeVx, "同列的火应已摇出一侧逃跑方向");

            bool exited = false;
            for (int i = 0; i < RabbitBrain.FleeMinTicks + Creature.ThinkEvery; i++)
            {
                creatures.Step(world);
                if (rabbit.AiState != RabbitBrain.StateFlee)
                {
                    exited = true;
                    Assert.GreaterOrEqual(
                        creatures.LogicTicks,
                        RabbitBrain.FleeMinTicks,
                        "退出逃命不得早于最短逃命时长");
                    break;
                }

                Assert.AreEqual(fleeVx, rabbit.IntentVx, Tol, "最短逃命时长内不该改逃跑方向");
            }

            Assert.IsTrue(exited, "火清掉后兔子应在最短逃命时长走完时退出 Flee");
            Assert.AreEqual(RabbitBrain.StateWander, rabbit.AiState, "无威胁退出后回 Wander");
        }

        /// <summary>
        /// **进入半径不因滞回放大**：兔子未处于 Flee 时，<c>ThreatRadius + 1</c> 处的火不触发逃命
        /// （滞回只放大脱离半径，不放大进入半径）。
        /// </summary>
        [Test]
        public void Threat_FireOutsideEntryRadius_DoesNotStartFlee()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 1f);

            world.Set(16, BodyCenterRow(rabbit) + Creature.ThreatRadius + 1, Fire);
            creatures.Step(world);

            Assert.AreNotEqual(RabbitBrain.StateFlee, rabbit.AiState, "进入半径之外一格的火不该触发逃命");
            Assert.That(Mathf.Abs(rabbit.IntentVx), Is.Not.EqualTo(RabbitBrain.FleeSpeed).Within(Tol),
                "也不该写入逃命速度");
        }

        // ============================== 8. 接触火即死 ==============================

        /// <summary>身体中心格是火 → 同一逻辑帧内被烧死，死因为 Burned，并在该帧清扫出世界。</summary>
        [Test]
        public void TouchFire_DiesBurned()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature probe = creatures.Spawn(new ProbeCreature(1f, 1f, 0f), world, 10f, 1f);

            world.Set(10, 1, Fire);

            int deaths = 0;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                deaths++;
                cause = deathCause;
            };

            creatures.Step(world);

            Assert.IsFalse(probe.Alive, "中心格是火时应立刻死亡");
            Assert.AreEqual(1, deaths, "应在同一帧发出一次死亡事件");
            Assert.AreEqual(CreatureDeathCause.Burned, cause, "死因应为 Burned");
            Assert.AreEqual(0, creatures.Count, "死亡实体应已被清扫");
        }

        // ============================== 9. 兔子溺死 ==============================

        /// <summary>
        /// 没顶超过 160 帧的兔子溺死（Drowned）；只没顶一小段就捞出（排水）的兔子则安然无恙。
        /// </summary>
        [Test]
        public void Drown_SubmergedTooLongDies_RescuedSurvives()
        {
            ParticleWorld doomedWorld = NewWorld();
            FillRows(doomedWorld, 0, 0, Stone);
            FillRect(doomedWorld, 0, W - 1, 1, 20, Water);
            CreatureWorld doomedCreatures = new CreatureWorld(SeedValue);
            RabbitCreature doomed = doomedCreatures.Spawn(new RabbitCreature(), doomedWorld, 16f, 10f);

            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            doomedCreatures.CreatureDied += (creature, deathCause) => cause = deathCause;

            RunSteps(doomedWorld, doomedCreatures, doomed.DrownTicksLimit + 60);

            Assert.AreEqual(0, doomedCreatures.Count, "长时间没顶的兔子应已溺死");
            Assert.AreEqual(CreatureDeathCause.Drowned, cause, "死因应为 Drowned");

            ParticleWorld rescueWorld = NewWorld();
            FillRows(rescueWorld, 0, 0, Stone);
            FillRect(rescueWorld, 0, W - 1, 1, 20, Water);
            CreatureWorld rescueCreatures = new CreatureWorld(SeedValue);
            RabbitCreature rescued = rescueCreatures.Spawn(new RabbitCreature(), rescueWorld, 16f, 10f);

            RunSteps(rescueWorld, rescueCreatures, 100);
            ClearRect(rescueWorld, 0, W - 1, 1, 20);
            RunSteps(rescueWorld, rescueCreatures, rescued.DrownTicksLimit + 60);

            Assert.IsTrue(rescued.Alive, "中途捞出（排水）后不应再溺死");
            Assert.AreEqual(1, rescueCreatures.Count, "被救的兔子应仍存活");
        }

        // ============================== 10. 鱼搁浅 ==============================

        /// <summary>
        /// 无水环境里的鱼连续离水超过 200 帧后搁浅而死（Beached）；中途回水的鱼计时清零、不会死。
        /// </summary>
        [Test]
        public void Beached_FishOutOfWaterTooLongDies_ReturnedToWaterSurvives()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            creatures.Spawn(new FishCreature(), world, 8f, 5f);

            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) => cause = deathCause;

            RunSteps(world, creatures, Creature.BeachedTicksLimit + 2);

            Assert.AreEqual(0, creatures.Count, "长期离水的鱼应搁浅而死");
            Assert.AreEqual(CreatureDeathCause.Beached, cause, "死因应为 Beached");

            ParticleWorld rescueWorld = NewWorld();
            FillRows(rescueWorld, 0, 0, Stone);
            CreatureWorld rescueCreatures = new CreatureWorld(SeedValue);
            FishCreature rescued = rescueCreatures.Spawn(new FishCreature(), rescueWorld, 8f, 5f);

            RunSteps(rescueWorld, rescueCreatures, 100);
            FillRect(rescueWorld, 0, W - 1, 1, 12, Water);
            RunSteps(rescueWorld, rescueCreatures, Creature.BeachedTicksLimit + 60);

            Assert.IsTrue(rescued.Alive, "中途回水后搁浅计时清零，鱼不应死");
            Assert.AreEqual(1, rescueCreatures.Count, "被救的鱼应仍存活");
        }

        // ============================== 10b. 压死（掩埋致死） ==============================

        /// <summary>
        /// 兔子被石头**完全埋住**（身体覆盖矩形每一格都是静止固体）→ 连续超过 <see cref="Creature.BuriedTicksLimit"/>
        /// 帧后压死，死因 <see cref="CreatureDeathCause.Crushed"/>；埋不满宽限期（<c>BuriedTicksLimit − 2</c> 帧）时仍存活。
        /// </summary>
        [Test]
        public void Crushed_RabbitFullyBuriedByStoneDies()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);

            // 用与实现同一套口径把兔子整个埋进石头（外加一圈外壳堵死逃逸路线）。
            BuryCreature(world, rabbit, Stone);

            int deaths = 0;
            int deathFrame = -1;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                deaths++;
                cause = deathCause;
                deathFrame = creatures.LogicTicks + 1;
            };

            int graceFrames = Creature.BuriedTicksLimit - 2;
            RunSteps(world, creatures, graceFrames);

            Assert.IsTrue(rabbit.Alive, "被埋不足 BuriedTicksLimit 帧（宽限期内）不应被压死");
            Assert.AreEqual(graceFrames, rabbit.BuriedTicks, "被埋帧数应每逻辑帧 +1");
            Assert.AreEqual(0, deaths, "宽限期内不应发出死亡事件");

            RunSteps(world, creatures, Creature.BuriedTicksLimit + 30 - graceFrames);

            Assert.IsFalse(rabbit.Alive, "持续被活埋超过 BuriedTicksLimit 帧应被压死");
            Assert.AreEqual(1, deaths, "死亡事件应恰好一次");
            Assert.AreEqual(CreatureDeathCause.Crushed, cause, "死因应为 Crushed（压死）");
            Assert.AreNotEqual(CreatureDeathCause.Starved, cause, "短时间不会被饿死，死因不该是 Starved");
            Assert.AreEqual(0, creatures.Count, "死亡实体应已被清扫");

            TestContext.WriteLine(
                $"Crushed_RabbitFullyBuriedByStoneDies 实测死亡帧 = {deathFrame}（BuriedTicksLimit = {Creature.BuriedTicksLimit}）");
        }

        /// <summary>
        /// 鱼被沙**完全埋住** → 连续超过 <see cref="Creature.BuriedTicksLimit"/> 帧后压死，死因
        /// <see cref="CreatureDeathCause.Crushed"/>（而非 <see cref="CreatureDeathCause.Beached"/>——搁浅阈值 200 帧，两者可区分）。
        /// </summary>
        [Test]
        public void Crushed_FishFullyBuriedBySandDies()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 8f, 1f);
            BuryCreature(world, fish, Sand);

            int deathFrame = -1;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                cause = deathCause;
                deathFrame = creatures.LogicTicks + 1;
            };

            RunSteps(world, creatures, 40);

            Assert.IsFalse(fish.Alive, "被活埋的鱼应被压死");
            Assert.AreEqual(CreatureDeathCause.Crushed, cause, "死因应为 Crushed");
            Assert.AreNotEqual(CreatureDeathCause.Beached, cause, "离水仅约 10 帧，远不到 200 帧搁浅阈值，死因不该是 Beached");

            TestContext.WriteLine($"Crushed_FishFullyBuriedBySandDies 实测死亡帧 = {deathFrame}");
        }

        /// <summary>
        /// 只盖住兔子覆盖矩形的**部分格**（其余是空气）→ 不构成掩埋 → 跑 300 帧仍存活（证明「全覆盖」才是判据）。
        /// </summary>
        [Test]
        public void Buried_PartiallyCovered_Survives()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);

            BodyRect(rabbit, out int colMin, out int colMax, out int rowMin, out int rowMax);
            Assert.Greater(colMax, colMin, "兔子身体应横跨 ≥ 2 列（否则「部分覆盖」前提不成立）");

            // 只留一格石头（用 Stone 避免下落）；兔子横跨两列，因此永远盖不满覆盖矩形。
            world.Set(colMin, rowMin, Stone);

            RunSteps(world, creatures, 300);

            Assert.IsTrue(rabbit.Alive, "只覆盖部分格不构成掩埋，兔子应存活");
            Assert.AreEqual(1, creatures.Count, "兔子应仍在世界中");
        }

        /// <summary>
        /// 鱼完全泡在水里（覆盖格全是水）→ 水不是固体，不算被埋 → 跑 300 帧仍存活
        /// （鱼不溺死，且始终没顶因此搁浅计时常零）。
        /// </summary>
        [Test]
        public void Buried_WaterDoesNotCount()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 0, W - 1, 1, 20, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 10f);

            RunSteps(world, creatures, 300);

            Assert.IsTrue(fish.Alive, "水不是固体，完全泡在水里不算被埋");
            Assert.AreEqual(0, fish.BuriedTicks, "水从不计入被埋帧数");
            Assert.AreEqual(1, creatures.Count, "鱼应仍在世界中");
        }

        // ============================== 11. 鱼的竖直行为（中性浮力） ==============================

        /// <summary>巡游中的鱼跑 200 帧，**可见身体始终整条没入水面之下**——身体 = 世界行
        /// <c>floor(Y) + 0 .. floor(Y) + BodyTopOffsetCells</c>（生成美术数据：切片 pivot 贴脚底、共 4 格高），
        /// 水面判定探的就是「体顶之上那一格」，因此「不出水面」与「体顶之上是水」是同一件事。
        /// 中性浮力下中段 <c>IntentVy = 0</c>、鱼保持出生深度，既不上浮出水也不沉底。</summary>
        [Test]
        public void FishCruise_BodyStaysUnderWater_AndHoldsDepth()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 8f);

            int lastWaterRow = 15; // 水占 0..15 行，水面上方第一格是第 16 行
            for (int i = 0; i < 200; i++)
            {
                creatures.Step(world);
                Assert.LessOrEqual(
                    Mathf.FloorToInt(fish.Y) + FishBrain.BodyTopOffsetCells,
                    lastWaterRow,
                    "鱼体顶不应越出水面（第 " + i + " 帧）");
            }

            Assert.LessOrEqual(Mathf.Abs(fish.Y - 8f), 1f, "中段的鱼应保持出生深度（净竖直加速度为 0）");
            Assert.AreEqual(1, creatures.Count, "巡游中的鱼不应被清扫");
        }

        /// <summary>水体中段的鱼（中性浮力）：跑 300 帧只靠意图保持深度，不因残余浮力上浮到水面。
        /// 新规则下「中段」= 体顶之上那一格是水、且体顶之上连续 3 格也全是水：水占 0..15 行，
        /// spawn Y = 5 ⇒ 体顶行 = 8、探针行 9 → 12 全在水里，既不锁潜也不上浮。</summary>
        [Test]
        public void Fish_MidWater_HoldsDepth_AndDoesNotRiseToSurface()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 5f);

            RunSteps(world, creatures, 300);

            Assert.LessOrEqual(Mathf.Abs(fish.Y - 5f), 1f,
                "中段的鱼应保持入水深度（净竖直加速度为 0），不该浮向水面");
            Assert.LessOrEqual(fish.Y, 6f,
                "中段的鱼不应升到接近水面（旧浮力常数会一路浮到 ≈15.75）");
            Assert.IsFalse(fish.VerticalDiveLatched, "体顶之上水量充足时不该锁潜");
            Assert.LessOrEqual(fish.Y + FishBrain.ArtHeightCells, 16f,
                "整条鱼身体应留在水下（底边 + 身体高度 4 ≤ 水面行之下）");
            Assert.IsTrue(fish.Alive, "保持深度的鱼不应被清扫");
        }

        /// <summary>近水面的鱼应主动深潜到「体顶之上连续 3 格全为水」的休息带后停住——不再停在近水面，
        /// 也不一路沉底。解锁带即 <c>floor(Y) + BodyTopOffsetCells + DiveUnlockMarginCells ≤ 15</c> ⇔
        /// <c>floor(Y) ≤ 9</c>，故稳态 Y ∈ [9, 10)（思考周期 10 帧 × 0.04 格/帧 ⇒ 最多多降 0.36 格，实测 ≈9.6）。</summary>
        [Test]
        public void Fish_NearSurface_DivesInsteadOfRising()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 15.5f);

            RunSteps(world, creatures, 400);

            Assert.LessOrEqual(fish.Y, 10f, "近水面的鱼应真的深潜到休息带内（Y < 10），而不是停在近水面");
            Assert.GreaterOrEqual(fish.Y, 9f, "鱼应停在休息带内，而不是一路沉底");
            Assert.IsFalse(fish.VerticalDiveLatched, "降到休息带后应解锁，改由中性浮力保持深度");
            Assert.LessOrEqual(fish.Y + FishBrain.ArtHeightCells, 16f,
                "整条鱼身体应留在水下（底边 + 身体高度 4 ≤ 水面行之下）");
            Assert.IsTrue(fish.Alive, "下潜中的鱼不应被清扫");

            float settledY = fish.Y;
            RunSteps(world, creatures, 200);
            Assert.LessOrEqual(Mathf.Abs(fish.Y - settledY), 0.5f,
                "到达目标深度后应稳定保持，不再继续下沉");
        }

        /// <summary>贴底的鱼应主动上浮离开水底（从 spawn Y = 0.2 抬到 ≈1.79），离开后停在停机带内而不升到水面。
        /// 停机带即 <c>nearBottom</c> 消失处：水占 0..15 行、<c>cy = floor(Y + 0.25)</c>，
        /// <c>cy ≥ 2</c>（⇔ Y ≥ 1.75）时 <c>cy−1 / cy−2</c> 都在水里，上浮意图归零 ⇒ 稳态 Y ∈ [1.75, 2.75)；
        /// 这里体顶之上水量充足（既不锁潜也不会触面），与上浮互不干扰。</summary>
        [Test]
        public void Fish_NearBottom_RisesAwayFromFloor()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 0.2f);

            RunSteps(world, creatures, 400);

            Assert.GreaterOrEqual(fish.Y, 1.7f, "贴底的鱼应上浮离开水底（从 0.2 抬到停机带 ≥1.75）");
            Assert.LessOrEqual(fish.Y, 3f, "上浮后应停在停机带内，而不是一路升到水面");
            Assert.IsFalse(fish.VerticalDiveLatched, "池底处体顶之上水量充足（3 格全水）⇒ 不该锁潜");
            Assert.IsTrue(fish.Alive, "上浮中的鱼不应被清扫");
        }

        /// <summary>用户的原始场景：鱼从空中投放，落水后应先锁潜再深潜到「体顶之上连续 3 格全为水」的休息带，
        /// 使整条 4 格高的身体没入水下，而不是像旧规则那样停在近水面。实测稳态 Y ≈9.6~10。</summary>
        [Test]
        public void Fish_DroppedFromAir_SettlesFullySubmerged()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 20f);

            RunSteps(world, creatures, 600);

            Assert.IsTrue(fish.Alive, "落水后应存活，不该搁浅或溺死");
            Assert.LessOrEqual(fish.Y + FishBrain.ArtHeightCells, 16f,
                "落水后整条鱼身体应没入水下（底边 + 身体高度 4 ≤ 水面行之下）");
            Assert.LessOrEqual(fish.Y, 10f, "落水后应停在休息带内（Y < 10），而不是悬在近水面");
            Assert.GreaterOrEqual(fish.Y, 9f, "落水后应停住，而不是一路沉到池底");
        }

        // ============================== 12. Flee 退出 ==============================

        /// <summary>
        /// 火被移除后兔子最终退出 Flee（回到 Wander / Idle）——但**不得早于最短逃命时长**
        /// （<see cref="RabbitBrain.FleeMinTicks"/>）：刚出圈就转向会被随机漫步方向带回边界蹭。
        /// </summary>
        [Test]
        public void Flee_ExitsWhenThreatRemoved()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(13, 1, Fire);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);

            for (int i = 0; i < 20 && rabbit.AiState != RabbitBrain.StateFlee; i++)
            {
                creatures.Step(world);
            }

            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "有火时应进入 Flee 态");

            world.Clear(13, 1);

            for (int i = 0; i < RabbitBrain.FleeMinTicks + Creature.ThinkEvery
                && rabbit.AiState == RabbitBrain.StateFlee; i++)
            {
                creatures.Step(world);
            }

            Assert.AreNotEqual(
                RabbitBrain.StateFlee,
                rabbit.AiState,
                "火移除后兔子应在最短逃命时长走完时退出 Flee");
        }

        // ============================== 14. 饥饿递增并触发觅食 ==============================

        /// <summary>饥饿逐逻辑帧 +1；过阈值且冷度为 0 时进入 <see cref="RabbitBrain.StateSeekFood"/>。</summary>
        [Test]
        public void Hunger_IncreasesAndTriggersSeekFood()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 14, 14, 1, 1, Grass);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);

            creatures.Step(world);
            Assert.AreEqual(1, rabbit.HungerTicks, "每逻辑帧饥饿 +1");
            creatures.Step(world);
            Assert.AreEqual(2, rabbit.HungerTicks, "饥饿应逐帧累积");

            rabbit.HungerTicks = RabbitBrain.HungryThreshold;
            for (int i = 0; i < 20 && rabbit.AiState != RabbitBrain.StateSeekFood; i++)
            {
                creatures.Step(world);
            }

            Assert.AreEqual(RabbitBrain.StateSeekFood, rabbit.AiState, "过饥饿阈值且冷度为 0 时应进入 SeekFood");
        }

        // ============================== 15. 最近草格与固定扫描序 ==============================

        /// <summary>取距离平方最小者；等距保留先扫描者（y 升序、同行 x 升序）；半径 6 外不计。</summary>
        [Test]
        public void FindNearestGrass_PicksNearest_ThenFixedOrder()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(11, 1, Grass); // 距 (10,1) 的 dist² = 1
            world.Set(13, 1, Grass); // dist² = 9

            int gx;
            int gy;
            bool found = CreatureSensors.FindNearestGrass(world, 10, 1, out gx, out gy);
            Assert.IsTrue(found, "半径 6 内两格草应被找到");
            Assert.AreEqual(11, gx, "应取更近的草格 x");
            Assert.AreEqual(1, gy, "应取更近的草格 y");

            ParticleWorld tieWorld = NewWorld();
            FillRows(tieWorld, 0, 0, Stone);
            tieWorld.Set(9, 1, Grass);  // dist² = 1
            tieWorld.Set(11, 1, Grass); // dist² = 1（平手）
            bool tieFound = CreatureSensors.FindNearestGrass(tieWorld, 10, 1, out gx, out gy);
            Assert.IsTrue(tieFound, "等距两格草应被找到");
            Assert.AreEqual(9, gx, "等距时应保留固定扫描序（同行 x 升序）先到者");

            ParticleWorld farWorld = NewWorld();
            FillRows(farWorld, 0, 0, Stone);
            farWorld.Set(17, 1, Grass); // dx = 7 > 6
            Assert.IsFalse(CreatureSensors.FindNearestGrass(farWorld, 10, 1, out gx, out gy),
                "半径 6 之外的草不应被看到");
        }

        // ============================== 16. 进食清除一格草并恢复饥饿 ==============================

        /// <summary>兔子在草旁进入 Eat：恰好清除一格草、饥饿下降、冷却 &gt; 0，并回到 Wander。</summary>
        [Test]
        public void Eat_ClearsOneGrassTile_AndRestoresHunger()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(11, 1, Grass); // 兔子 (10,1) 正前方一格
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            rabbit.HungerTicks = RabbitBrain.HungryThreshold;
            rabbit.AiState = RabbitBrain.StateSeekFood;
            rabbit.FoodTargetX = 11;
            rabbit.FoodTargetY = 1;

            int grassBefore = CountType(world, Grass);
            Assert.AreEqual(1, grassBefore, "场景应只有一格草");

            for (int i = 0; i < 200 && rabbit.Alive && world.GetCell(11, 1).typeId == Grass; i++)
            {
                creatures.Step(world);
            }

            // 目标格被清除（结算路径走 world.Clear ⇒ 通用有机衰变有小概率就地留腐殖质，故断言「不再是草」而非强制空气）。
            Assert.AreNotEqual(Grass, world.GetCell(11, 1).typeId, "目标草格应被清除");
            Assert.AreEqual(0, CountType(world, Grass), "只应清除一格草，且场上不留草");
            Assert.Less(rabbit.HungerTicks, RabbitBrain.HungryThreshold, "进食应把饥饿降到觅食阈值以下");
            Assert.Greater(rabbit.HungerTicks, 0, "600 的饥饿减 420 后应仍为正（未触底）");
            Assert.Greater(rabbit.FeedingCooldownTicks, 0, "进食后应进入冷却");
            Assert.AreEqual(RabbitBrain.StateWander, rabbit.AiState, "进食后应回到 Wander");
        }

        // ============================== 17. 不吃花 / 草种子 ==============================

        /// <summary>周围只有花与草种子时，进食态不写格、不消耗动物 / 世界随机流。</summary>
        [Test]
        public void Eat_DoesNotEatFlowerOrSeed()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(11, 1, Flower);
            world.Set(9, 1, GrassSeed);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            rabbit.HungerTicks = RabbitBrain.HungryThreshold;
            rabbit.AiState = RabbitBrain.StateEat;

            // 同 seed 的参考世界：构造路径逐位一致 ⇒ 未写入时两条随机流应停在同一位点。
            ParticleWorld refWorld = NewWorld();
            FillRows(refWorld, 0, 0, Stone);
            refWorld.Set(11, 1, Flower);
            refWorld.Set(9, 1, GrassSeed);
            CreatureWorld refCreatures = new CreatureWorld(SeedValue);

            for (int i = 0; i < 40; i++)
            {
                creatures.Step(world);
            }

            Assert.AreEqual(Flower, world.GetCell(11, 1).typeId, "花不应被吃");
            Assert.AreEqual(GrassSeed, world.GetCell(9, 1).typeId, "草种子不应被吃");
            Assert.AreEqual(refWorld.NextUInt(), world.NextUInt(), "未进食不应消耗世界随机流");
            Assert.AreEqual(refCreatures.NextUInt(), creatures.NextUInt(), "未进食不应消耗动物随机流");
        }

        // ============================== 18. 目标不可达时放弃 ==============================

        /// <summary>目标被两格高墙封死 → 超过 <see cref="RabbitBrain.FoodTargetFailLimit"/> 后放弃并回 Wander。</summary>
        [Test]
        public void SeekFood_GivesUpAfterFailLimit()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 2, Stone);
            FillRect(world, 13, 13, 3, 4, Stone); // 两格高墙挡住去路
            world.Set(15, 3, Grass);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 3f);
            rabbit.HungerTicks = RabbitBrain.HungryThreshold;
            rabbit.AiState = RabbitBrain.StateSeekFood;
            rabbit.FoodTargetX = 15;
            rabbit.FoodTargetY = 3;

            int steps = 0;
            while (rabbit.Alive
                && (rabbit.AiState == RabbitBrain.StateSeekFood || rabbit.AiState == RabbitBrain.StateEat)
                && steps < 1000)
            {
                creatures.Step(world);
                steps++;
            }

            Assert.AreEqual(RabbitBrain.StateWander, rabbit.AiState,
                "目标长期不可达时应在 FailLimit 后放弃并回 Wander");
            Assert.Less(steps, 1000, "应在有限步数内放弃，不永远停在 SeekFood");
        }

        // ============================== 19. 火焰优先级高于饥饿 ==============================

        /// <summary>饥饿超阈值 + 半径 6 内有火 → 进入 Flee，不停下进食。</summary>
        [Test]
        public void FireThreat_BeatsHunger()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(13, 1, Fire);
            world.Set(9, 1, Grass);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            rabbit.HungerTicks = RabbitBrain.HungryThreshold + 100;
            rabbit.AiState = RabbitBrain.StateSeekFood;

            for (int i = 0; i < 20 && rabbit.AiState != RabbitBrain.StateFlee; i++)
            {
                creatures.Step(world);
            }

            Assert.AreEqual(RabbitBrain.StateFlee, rabbit.AiState, "火焰威胁应压过饥饿，先进 Flee");
        }

        // ============================== 20. 饥饿饱和饿死 ==============================

        /// <summary>饥饿饱和到 <see cref="Creature.MaxHungerTicks"/> → Kill(Starved)，事件恰好一次。</summary>
        [Test]
        public void Starvation_KillsWithStarvedCause()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            rabbit.HungerTicks = Creature.MaxHungerTicks - 3;

            int deaths = 0;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                deaths++;
                cause = deathCause;
            };

            RunSteps(world, creatures, 10);

            Assert.IsFalse(rabbit.Alive, "饥饿饱和应饿死");
            Assert.AreEqual(1, deaths, "死亡事件应恰好一次");
            Assert.AreEqual(CreatureDeathCause.Starved, cause, "死因应为 Starved");
            Assert.AreEqual(0, creatures.Count, "死亡实体应已被清扫");
        }

        // ============================== 21. 饥饿不消耗世界随机流 ==============================

        /// <summary>未达饥饿阈值时，有 / 无动物的粒子世界随机流逐位一致（草地场景）。</summary>
        [Test]
        public void Hunger_DoesNotConsumeWorldRng()
        {
            ParticleWorld solo = NewWorld();
            ParticleWorld withAnimals = NewWorld();
            FillRows(solo, 0, 3, Sand);
            FillRows(withAnimals, 0, 3, Sand);
            FillRect(solo, 12, 18, 4, 4, Grass);
            FillRect(withAnimals, 12, 18, 4, 4, Grass);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), withAnimals, 8f, 4f);
            rabbit.HungerTicks = 0;

            for (int i = 0; i < 300; i++)
            {
                solo.StepLogic();
                withAnimals.StepLogic();
                creatures.Step(withAnimals);
            }

            Assert.Less(rabbit.HungerTicks, RabbitBrain.HungryThreshold,
                "本测试应保持在饥饿阈值以下（否则会进入觅食分支）");

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(solo.NextUInt(), withAnimals.NextUInt(), "未达阈值时动物层不该消耗世界随机流");
            }
        }

        // ============================== 22. 行走撞墙：本帧横向被挡信号 + 三态墙探针 ==============================

        /// <summary>
        /// 根因回归（本 bug 的核心）：开阔石地面上兔子**正朝火走**，第一个思考帧后应始终背向火，
        /// 中途翻面 ≤ 1 次、净位移向左。
        /// </summary>
        /// <remarks>
        /// <para><b>旧实现必失败</b>：④ 段曾以「<c>|VelocityX| &lt; 0.005</c>」为入口——任何主动转向都会让
        /// <c>VelocityX</c> 穿过零点（<c>MoveTowards</c> 步长 0.02：0.06→0.04→0.02→<b>0.00</b>），
        /// 0.00 那一帧落进窗口，而探针回答「前方非固体」；旧代码把「没有墙」也当成掉头，于是每帧把意图翻回去，
        /// 兔子在开阔地上反复转身（60 帧内十余次翻面）、净位移反而朝火增大——三条断言全数落空。</para>
        /// <para>现在入口换成物理事实「本帧横向被挡」（撞固体 / 被世界边界夹取）：开阔地上该信号恒为空
        /// ⇒ ④ 段整段空操作，速度自然穿过零点完成转向，与转向幅度、帧率都无关。</para>
        /// </remarks>
        [Test]
        public void FleeOnOpenGround_KeepsFleeingIntent_AcrossVelocityZeroCrossing()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            world.Set(18, 1, Fire); // 兔子右侧 6 格（= 设计扫描半径边界）、在威胁扫描半径内

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 12f, 1f);
            rabbit.VelocityX = RabbitBrain.WalkSpeed; // 初态：正朝火走
            rabbit.IntentVx = RabbitBrain.WalkSpeed;
            rabbit.AiState = RabbitBrain.StateWander;
            rabbit.Facing = 1;

            float startX = rabbit.X;
            int facingFlips = 0;
            int prevFacing = rabbit.Facing;
            for (int i = 0; i < 60; i++)
            {
                creatures.Step(world);
                if (rabbit.Facing != prevFacing)
                {
                    facingFlips++;
                    prevFacing = rabbit.Facing;
                }

                Assert.Less(rabbit.IntentVx, 0f,
                    $"第 {i} 帧的意图应仍背向火：开阔地上速度过零不得被误判成撞墙");
            }

            Assert.AreEqual(-1, rabbit.Facing, "最终应面朝左（背向火）");
            Assert.LessOrEqual(facingFlips, 1, "转向只该发生一次，不该每帧反复翻面");
            Assert.Less(rabbit.X, startX - 1f, "净位移应明显向左（远离火），而不是朝火走");
        }

        /// <summary>
        /// 新不变量：<c>VelocityX</c> 恰好为 0、仍有前向意图、前方无墙 → 意图不得被翻号
        /// （<c>VelocityX == 0</c> 不是「被挡住」的证据）。把「速度过零」与「撞墙」彻底解耦。
        /// </summary>
        /// <remarks>探针的 <c>MoveAccel = 0</c> 使 <c>VelocityX</c> 恒为 0、也从不产生横向位移
        /// ⇒ 既没撞到固体也不会被边界夹取，撞墙信号恒为空，④ 段一律空操作——本测试因此钉住
        /// 「速度是不是零与有没有被挡住无关」这一新口径。</remarks>
        [Test]
        public void OpenGround_ZeroVelocity_DoesNotReverseIntent()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature probe = creatures.Spawn(new ProbeCreature(1f, 1f, 0f), world, 5f, 1f);
            probe.VelocityX = 0f;
            probe.IntentVx = 0.06f;

            RunSteps(world, creatures, 30);

            Assert.AreEqual(0.06f, probe.IntentVx, Tol,
                "开阔地上「速度恰为 0」不是撞墙，不得反向意图");
        }

        /// <summary>
        /// 回归保护：没有任何障碍时向右正常行走 40 帧，<c>IntentVx</c> 恒为正、水平速度恒为行走速度。
        /// </summary>
        /// <remarks>与 <see cref="OpenGround_ZeroVelocity_DoesNotReverseIntent"/> 互补：那条钉的是
        /// 「速度恰为 0」的场景，这条钉的是**正常行走**（速度恒为 <see cref="RabbitBrain.WalkSpeed"/>）的场景——
        /// 两者都不是撞墙，④ 段都必须空操作。</remarks>
        [Test]
        public void WalkingOnOpenGround_KeepsForwardIntent()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature walker = SpawnWalkRight(world, creatures, 4f);

            for (int i = 0; i < 40; i++)
            {
                creatures.Step(world);
                Assert.Greater(walker.IntentVx, 0f, $"第 {i} 帧：开阔地上正常行走不得反向意图");
                Assert.AreEqual(RabbitBrain.WalkSpeed, walker.VelocityX, Tol,
                    $"第 {i} 帧：开阔地上水平速度应稳定在行走速度（不被 ④ 段打断）");
            }
        }

        /// <summary>
        /// 正常行走撞上**一格高的坎**（越过身体高度后其上两格非固体）→ **横向积分里贴地抬升一格并继续走**
        /// （<see cref="Creature.CanStepUp"/> = true，兔子具备）：既不掉头，也不原地弹跳。
        /// </summary>
        /// <remarks>
        /// <para><b>为什么跨坎写在物理层而不是运动层</b>：只靠贴地抬升、不加水平位移的话，兔子在**贴墙前**
        /// 就抬了格，重力立刻把它拉回原地（坎还在前方一列），于是「抬格 → 落下 → 被挡 → 再抬格」反复，
        /// 永远踏不上去。正确做法是在横向扫描撞到坎的那一帧抬格并**继续走完本帧的位移**（位移因此连续）。</para>
        /// <para><b>为什么不跳过去</b>：起跳能提供的横移 = 行走速度 × 「脚高于坎顶」的帧数；`gravity 0.24` +
        /// `HopVelocity 0.75` 约 1.4 帧 ≈ 0.08 格，而越过一格坎需要约 2.5 格（半宽 0.75 + 坎宽 1 + 对侧 0.75）
        /// —— 差一个量级，弹道抬到真能过坎需 ≈2.5 格高的跳跃。</para>
        /// <para><b>观察量</b>：某帧 <c>Y</c> 抬起一格（&gt; 1.5），且最终 <c>X</c> 越过坎所在列（x = 6）。</para>
        /// </remarks>
        [Test]
        public void WalkingIntoClimbableStep_StepsUpAndKeepsWalking()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 6, 6, 1, 1, Stone); // 前方一格高的坎：其上行第 2、3 格都是非固体
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature walker = SpawnWalkRight(world, creatures, 4f);
            walker.StepUp = true; // 对齐兔子：具备跨坎能力

            bool steppedUp = false;
            bool passed = false;
            for (int i = 0; i < 120 && !passed; i++)
            {
                creatures.Step(world);
                Assert.Greater(walker.IntentVx, 0f, $"第 {i} 帧：一格高的坎不该掉头（应改为跨上去）");
                steppedUp |= walker.Y > 1.5f;
                passed = walker.X > 7f;
            }

            Assert.IsTrue(steppedUp, "正常行走撞上一格高的坎应贴地跨上去（Y 抬起一格）");
            Assert.IsTrue(passed, "跨上台阶后应继续前进、越过坎所在列（x = 6）");
        }

        /// <summary>
        /// 正常行走撞上**不可翻越的两格高**石墙 → 探针答 <see cref="CreatureSensors.WallAhead.Blocked"/>
        /// → <c>IntentVx</c> 变号，且此后不再紧贴墙面。
        /// </summary>
        /// <remarks><b>修复前必失败</b>：同一格高墙那条——速度进不了旧窗口 ⇒ ④ 段不可达，意图恒为 +0.06、
        /// 兔子永远顶着墙。</remarks>
        [Test]
        public void WalkingIntoBlockedWall_ReversesIntentAndWalksAway()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 6, 6, 1, 2, Stone); // 两格高墙
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature walker = SpawnWalkRight(world, creatures, 4f);

            bool reversed = false;
            for (int i = 0; i < 40 && !reversed; i++)
            {
                creatures.Step(world);
                reversed = walker.IntentVx < 0f;
            }

            Assert.IsTrue(reversed, "正常行走撞上不可翻越的墙应在若干帧内掉头（IntentVx 变号）");

            float wallGapX = walker.X; // 掉头瞬间应正贴着墙（盒宽 1.5、墙面 x = 6 ⇒ X ≈ 5.25）
            RunSteps(world, creatures, 20);

            Assert.Less(walker.VelocityX, 0f, "掉头后水平速度应朝反方向");
            Assert.Less(walker.X, wallGapX - 0.5f, "掉头后应离开墙面，而不是继续顶着墙");
        }

        /// <summary>
        /// 正常行走撞上**世界右边界**（<c>X</c> 被夹取）→ 掉头；不得永远顶着边界。
        /// </summary>
        /// <remarks>
        /// <para><b>为什么要跳过悬崖守卫</b>：贴地行走到世界边界前 1 列时，守卫探的「前方列」是 32
        /// （越界 ⇒ 非固体 ⇒ 视为无支撑），因此守卫总是**先**把意图折返，横向夹取根本走不到。
        /// 真正会被边界夹住的正是豁免守卫的兔子 Flee 态（<c>Creature.IgnoresCliffGuard</c> = true），
        /// 本用例因此打开探针的守卫豁免开关复现该处境——否则「X 恰好停在边界上」这一前提不成立，
        /// 用例也钉不住边界分支本身。</para>
        /// <para><b>修复前必失败</b>：旧入口是「速度接近零」，贴边界后 ① 每帧仍加速 0.02 ⇒ 永不触发，
        /// 意图恒为 +0.06、永远顶着边界；何况边界外侧那格越出世界，墙探针只会回答
        /// <see cref="CreatureSensors.WallAhead.None"/>，与「开阔地不动」共用分支 —— 即便入口条件换成物理事实，
        /// 也必须靠「成因 = 被边界夹取」单独分流才会掉头。</para>
        /// <para><b>观察量</b>：<c>IntentVx</c> 变号；且掉头前 <c>X</c> 恰好停在世界右边界
        /// （<c>W − Width/2</c>）上，说明确实被夹取过（而不是被别的原因折返）。</para>
        /// </remarks>
        [Test]
        public void WalkingIntoWorldEdge_ReversesIntentInsteadOfPushingForever()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            ProbeCreature walker = SpawnWalkRight(world, creatures, W - 4f); // 边界前 4 格出发
            walker.SkipCliffGuard = true;

            float farRightX = walker.X;
            bool reversed = false;
            for (int i = 0; i < 120 && !reversed; i++)
            {
                creatures.Step(world);
                if (walker.X > farRightX)
                {
                    farRightX = walker.X;
                }

                reversed = walker.IntentVx < 0f;
            }

            float edgeX = W - walker.Width * 0.5f; // 世界宽 32、盒宽 1.5 ⇒ 最右可站 X = 31.25
            Assert.IsTrue(reversed, "撞到世界右边界后应掉头（IntentVx 变号），不得永远顶着边界");
            Assert.AreEqual(edgeX, farRightX, Tol, "掉头前应确实被世界右边界夹住（X 恰好停在边界上）");

            // 掉头那一帧 ① 已经按旧意图加速过，物理仍会把它顶回边界（X 不变），因此要再跑几帧才看得到离开。
            RunSteps(world, creatures, 10);

            Assert.Less(walker.VelocityX, 0f, "掉头后水平速度应朝反方向");
            Assert.Less(walker.X, edgeX - 0.3f, "掉头后应离开边界，而不是继续顶着边界");
        }
    }
}
