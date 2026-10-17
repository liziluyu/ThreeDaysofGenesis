using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 两性配对繁殖（<see cref="CreatureReproduction"/> / <see cref="CreatureWorld.RequestBirth"/> /
    /// <see cref="CreatureWorld.CountOf{T}"/>）的测试：逐条覆盖 <c>docs/plan-creature-reproduction.md</c> §7 的 1-11——
    /// 性别奇偶、相邻异性出生、同性 / 超距 / 未成年 / 冷却中 / 上限不生、下限因子、三个物种的附加条件、
    /// 新生儿本帧不推进、同 seed 出生帧一致、以及两层随机流隔离。
    /// </summary>
    /// <remarks>
    /// <para><b>被测入口：</b>出生用例同时走两条路径——测试里直接调 <see cref="CreatureReproduction.TryBreed"/>
    /// 控制摇骰时机，并且每帧都跑一次 <see cref="CreatureWorld.Step"/>，因此物种 <c>OnLogic</c> 里那行
    /// <c>TryBreed</c> 也被真实执行（同一帧里先入队者胜出、冷却挡住第二次）。否定用例只调
    /// <c>TryBreed</c> 而不跑 Step，把条件钉死在“始终不满足”上。</para>
    /// <para><b>地形静止：</b>只调 <c>creatures.Step(world)</c>、不调 <c>world.StepLogic()</c>——动物层对世界只读，
    /// 这样水 / 石头不会自己流走，用例只看繁殖决策。</para>
    /// <para><b>固定条件：</b>直接写公开黑板槽位（<c>Sex</c> / <c>AgeTicks</c> / <c>BreedCooldownTicks</c> /
    /// <c>PreyEaten</c> / <c>HungerTicks</c>），不靠概率统计去“撞”状态；唯一带随机性的出生用例给足帧数预算
    /// （基础命中率 50%~80%），且同 seed 可复现。</para>
    /// </remarks>
    public sealed class CreatureReproductionTests
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
            for (int y = y0; y <= y1; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    world.Set(x, y, typeId);
                }
            }
        }

        /// <summary>铺一层石头地板，让兔子有东西站（脚底 y = 3）。</summary>
        private static void AddRabbitGround(ParticleWorld world)
        {
            FillRows(world, 0, 2, Stone);
        }

        /// <summary>把一只兔子设为“成年 + 冷却好 + 刚吃过 + Idle 且不会自己切换状态”。</summary>
        /// <remarks><c>StateTicksLeft</c> 给大值 ⇒ <see cref="RabbitBrain"/> 的 Idle 分支不会重摇下一段状态，
        /// 兔子原地不动、也不额外消耗随机数，配对距离因此稳定。</remarks>
        private static void MakeRabbitReady(Creature rabbit, byte sex)
        {
            rabbit.Sex = sex;
            rabbit.AgeTicks = 1000;
            rabbit.BreedCooldownTicks = 0;
            rabbit.HungerTicks = 0;
            rabbit.AiState = RabbitBrain.StateIdle;
            rabbit.StateTicksLeft = 100000;
        }

        private static void MakeFishReady(Creature fish, byte sex)
        {
            fish.Sex = sex;
            fish.AgeTicks = 1000;
            fish.BreedCooldownTicks = 0;
            fish.AiState = FishBrain.StateSwim;
        }

        private static void MakeBirdReady(Creature bird, byte sex, int preyEaten)
        {
            bird.Sex = sex;
            bird.AgeTicks = 3000;
            bird.BreedCooldownTicks = 0;
            bird.PreyEaten = preyEaten;
            bird.AiState = BirdBrain.StateFly;
        }

        /// <summary>
        /// 放一对相邻个体并跑到第一次出生：每帧把位置钉回 (x, y) / (x+1, y)、调一次
        /// <see cref="CreatureReproduction.TryBreed"/>，再推进一帧动物世界（物种 <c>OnLogic</c> 也会尝试）。
        /// </summary>
        /// <returns>第一次出生发生的逻辑帧号；预算内没出生返回 −1。</returns>
        private static int RunUntilBirth(
            ParticleWorld world,
            CreatureWorld creatures,
            Creature female,
            Creature male,
            float x,
            float y,
            int maxFrames,
            System.Action<Creature, Creature> beforeFrame)
        {
            int birthTick = -1;
            System.Action<Creature> onSpawn = c =>
            {
                if (birthTick < 0)
                {
                    birthTick = creatures.LogicTicks;
                }
            };

            creatures.CreatureSpawned += onSpawn;
            try
            {
                for (int i = 0; i < maxFrames && birthTick < 0; i++)
                {
                    female.X = x;
                    female.Y = y;
                    male.X = x + 1f;
                    male.Y = y;

                    if (beforeFrame != null)
                    {
                        beforeFrame(female, male);
                    }

                    CreatureReproduction.TryBreed(female, creatures, world);
                    creatures.Step(world);
                }
            }
            finally
            {
                creatures.CreatureSpawned -= onSpawn;
            }

            return birthTick;
        }

        /// <summary>对同一对个体反复尝试、断言始终不繁殖（不推进世界 ⇒ 条件与位置严格不变）。</summary>
        private static void AssertNeverBreeds(Creature female, CreatureWorld creatures, ParticleWorld world, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                Assert.IsFalse(
                    CreatureReproduction.TryBreed(female, creatures, world),
                    $"第 {i} 次尝试不应发起出生请求");
            }
        }

        /// <summary>逐格比对两张网格的 typeId（动物层不应写粒子世界）。</summary>
        private static void AssertCellsMatch(ParticleWorld expected, ParticleWorld actual, string context)
        {
            for (int i = 0; i < expected.CellCount; i++)
            {
                ushort a = expected.Cells[i].typeId;
                ushort b = actual.Cells[i].typeId;
                if (a != b)
                {
                    Assert.Fail($"{context}：第 {i} 格 typeId 不同（{a} vs {b}）——动物层不该消耗 / 改写世界");
                }
            }
        }

        // ============================== 1. 性别奇偶 ==============================

        [Test]
        public void Sex_AssignedBySpawnOrderParity_AndSharesSensePhaseCounter()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature first = creatures.Spawn(new RabbitCreature(), world, 10f, 3f);
            Creature second = creatures.Spawn(new RabbitCreature(), world, 12f, 3f);
            Creature third = creatures.Spawn(new RabbitCreature(), world, 14f, 3f);

            Assert.AreEqual(CreatureReproduction.SexFemale, first.Sex, "生成序号 0（偶）应为雌");
            Assert.AreEqual(CreatureReproduction.SexMale, second.Sex, "生成序号 1（奇）应为雄");
            Assert.AreEqual(CreatureReproduction.SexFemale, third.Sex, "生成序号 2（偶）应为雌");

            Assert.AreEqual(0, first.SensePhase, "性别与相位共用同一序号：第 0 只相位 0");
            Assert.AreEqual(1, second.SensePhase, "第 1 只相位 1");
            Assert.AreEqual(2, third.SensePhase, "第 2 只相位 2");
        }

        // ============================== 2. 相邻异性出生 ==============================

        [Test]
        public void AdjacentOppositeSex_BirthsExactlyOnce_AndCountIncreasesByOne()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);

            int spawned = 0;
            System.Action<Creature> onSpawn = c => spawned++;
            creatures.CreatureSpawned += onSpawn;
            try
            {
                int birthTick = RunUntilBirth(world, creatures, female, male, 20f, 3f, 600, ResetRabbitFrame);
                Assert.GreaterOrEqual(birthTick, 0, "相邻成年异性在预算内应出生一次");
            }
            finally
            {
                creatures.CreatureSpawned -= onSpawn;
            }

            Assert.AreEqual(1, spawned, "本用例内 CreatureSpawned 应恰好一次");
            Assert.AreEqual(3, creatures.Count, "初始 2 只 + 1 个新生儿");
        }

        /// <summary>兔子逐帧重置：位置已在 <see cref="RunUntilBirth"/> 里钉住，这里只保证“刚吃过”。</summary>
        private static void ResetRabbitFrame(Creature female, Creature male)
        {
            female.HungerTicks = 0;
        }

        // ============================== 3. 同性 ==============================

        [Test]
        public void SameSexPair_NeverBreeds()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature first = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature second = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(first, CreatureReproduction.SexFemale);
            MakeRabbitReady(second, CreatureReproduction.SexFemale);

            AssertNeverBreeds(first, creatures, world, 2000);
            Assert.AreEqual(2, creatures.Count);
        }

        // ============================== 4. 距离 > MateRadius ==============================

        [Test]
        public void MateBeyondRadius_NeverBreeds()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), world, 23f, 3f); // 中心距 3 > MateRadius 2
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);

            AssertNeverBreeds(female, creatures, world, 2000);
            Assert.AreEqual(2, creatures.Count);

            // 贴到恰好等于 MateRadius 时应能找到配对者（距离判定是 ≤，不是 <）。
            male.X = 22f;
            bool requested = false;
            for (int i = 0; i < 200 && !requested; i++)
            {
                requested = CreatureReproduction.TryBreed(female, creatures, world);
                if (requested)
                {
                    break;
                }

                female.BreedCooldownTicks = 0;
            }

            Assert.IsTrue(requested, "中心距 == MateRadius 应在概率预算内成功");
        }

        // ============================== 5. 未成年 / 冷却中 ==============================

        [Test]
        public void UnderageOrCoolingDown_NeverBreeds()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);

            // 雌性未成年。
            CreatureWorld underageFemaleWorld = new CreatureWorld(SeedValue);
            Creature youngFemale = underageFemaleWorld.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature adultMale = underageFemaleWorld.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(youngFemale, CreatureReproduction.SexFemale);
            MakeRabbitReady(adultMale, CreatureReproduction.SexMale);
            youngFemale.AgeTicks = CreatureReproduction.RabbitMaturityTicks - 1;
            AssertNeverBreeds(youngFemale, underageFemaleWorld, world, 2000);

            // 雄性未成年。
            CreatureWorld underageMaleWorld = new CreatureWorld(SeedValue);
            Creature adultFemale = underageMaleWorld.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature youngMale = underageMaleWorld.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(adultFemale, CreatureReproduction.SexFemale);
            MakeRabbitReady(youngMale, CreatureReproduction.SexMale);
            youngMale.AgeTicks = CreatureReproduction.RabbitMaturityTicks - 1;
            AssertNeverBreeds(adultFemale, underageMaleWorld, world, 2000);

            // 雌性冷却中。
            CreatureWorld coolingFemaleWorld = new CreatureWorld(SeedValue);
            Creature coolingFemale = coolingFemaleWorld.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature mateOne = coolingFemaleWorld.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(coolingFemale, CreatureReproduction.SexFemale);
            MakeRabbitReady(mateOne, CreatureReproduction.SexMale);
            coolingFemale.BreedCooldownTicks = 1;
            AssertNeverBreeds(coolingFemale, coolingFemaleWorld, world, 2000);

            // 雄性冷却中。
            CreatureWorld coolingMaleWorld = new CreatureWorld(SeedValue);
            Creature readyFemale = coolingMaleWorld.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature coolingMale = coolingMaleWorld.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(readyFemale, CreatureReproduction.SexFemale);
            MakeRabbitReady(coolingMale, CreatureReproduction.SexMale);
            coolingMale.BreedCooldownTicks = 1;
            AssertNeverBreeds(readyFemale, coolingMaleWorld, world, 2000);
        }

        // ============================== 6. 上限 ==============================

        [Test]
        public void AtPopulationCap_NeverBreeds()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);

            while (creatures.Count < CreatureReproduction.RabbitMaxPopulation)
            {
                creatures.Spawn(new RabbitCreature(), world, 30f, 3f);
            }

            Assert.AreEqual(CreatureReproduction.RabbitMaxPopulation, creatures.CountOf<RabbitCreature>());
            AssertNeverBreeds(female, creatures, world, 2000);
            Assert.AreEqual(CreatureReproduction.RabbitMaxPopulation, creatures.Count);
        }

        [Test]
        public void PendingBirth_IsRecheckedAgainstCap_WhenApplied()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature mother = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature father = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(mother, CreatureReproduction.SexFemale);
            MakeRabbitReady(father, CreatureReproduction.SexMale);

            // 先入队（登记时未到上限），再把种群填到上限 —— 应用阶段必须复核并跳过。
            creatures.RequestBirth(mother, father);
            while (creatures.Count < CreatureReproduction.RabbitMaxPopulation)
            {
                creatures.Spawn(new RabbitCreature(), world, 30f, 3f);
            }

            int spawned = 0;
            System.Action<Creature> onSpawn = c => spawned++;
            creatures.CreatureSpawned += onSpawn;
            try
            {
                creatures.Step(world);
            }
            finally
            {
                creatures.CreatureSpawned -= onSpawn;
            }

            Assert.AreEqual(0, spawned, "应用阶段发现已到上限，应跳过该出生");
            Assert.AreEqual(CreatureReproduction.RabbitMaxPopulation, creatures.Count);
        }

        // ============================== 7. 下限因子 ==============================

        [Test]
        public void BelowMinPopulation_FactorIsTwo_AndThresholdFormulaMatches()
        {
            const int min = 2;
            const int max = 12;
            const int denom = 20;

            Assert.AreEqual(2, CreatureReproduction.CapacityFactor(min - 1, min), "低于下限时因子应为 2");
            Assert.AreEqual(1, CreatureReproduction.CapacityFactor(min, min), "等于下限时因子应为 1");
            Assert.AreEqual(1, CreatureReproduction.CapacityFactor(min + 1, min), "高于下限时因子应为 1");

            // count < min：20 × (12 − 1) / 12 = 18，再 ×2 = 36（整数运算，先整除再乘因子）。
            Assert.AreEqual(36, CreatureReproduction.SuccessThreshold(denom, max, 1, min));
            // count == min：20 × (12 − 2) / 12 = 16，不翻倍。
            Assert.AreEqual(16, CreatureReproduction.SuccessThreshold(denom, max, 2, min));
            // count == max：阈值 0（调用方本就不会摇骰，帮助函数自己也给出 0）。
            Assert.AreEqual(0, CreatureReproduction.SuccessThreshold(denom, max, max, min));
        }

        // ============================== 8. 物种附加条件 ==============================

        [Test]
        public void HungryRabbit_NeverBreeds()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);
            female.HungerTicks = CreatureReproduction.RabbitFullHungerTicks; // 恰好到阈值 ⇒ 不算“刚吃过”

            AssertNeverBreeds(female, creatures, world, 2000);
        }

        [Test]
        public void FishInShallowWater_NeverBreeds()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 5, Water); // 水深 6 < FishMinWaterDepth(7)
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new FishCreature(), world, 20f, 1.5f);
            Creature male = creatures.Spawn(new FishCreature(), world, 21f, 1.5f);
            MakeFishReady(female, CreatureReproduction.SexFemale);
            MakeFishReady(male, CreatureReproduction.SexMale);

            AssertNeverBreeds(female, creatures, world, 2000);
        }

        [Test]
        public void FishInDeepWater_Breeds()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 11, Water); // 水深 12 ≥ 7
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new FishCreature(), world, 20f, 1.5f);
            Creature male = creatures.Spawn(new FishCreature(), world, 21f, 1.5f);
            MakeFishReady(female, CreatureReproduction.SexFemale);
            MakeFishReady(male, CreatureReproduction.SexMale);

            int birthTick = RunUntilBirth(world, creatures, female, male, 20f, 1.5f, 600, ResetFishFrame);
            Assert.GreaterOrEqual(birthTick, 0, "深水里相邻成年异性鱼应出生");
            Assert.AreEqual(3, creatures.Count);
        }

        private static void ResetFishFrame(Creature female, Creature male)
        {
            female.AiState = FishBrain.StateSwim;
            male.AiState = FishBrain.StateSwim;
        }

        [Test]
        public void BirdWithoutPreyEaten_NeverBreeds_ThenBreedsAfterBothHaveOneKill()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new BirdCreature(), world, 20f, 30f);
            Creature male = creatures.Spawn(new BirdCreature(), world, 21f, 30f);
            MakeBirdReady(female, CreatureReproduction.SexFemale, 0);
            MakeBirdReady(male, CreatureReproduction.SexMale, 0);

            AssertNeverBreeds(female, creatures, world, 2000);

            // 双方各补一次捕食成功：只有“双方都 ≥1”才放行。
            female.PreyEaten = 1;
            AssertNeverBreeds(female, creatures, world, 2000);

            male.PreyEaten = 1;
            int birthTick = RunUntilBirth(world, creatures, female, male, 20f, 30f, 600, ResetBirdFrame);
            Assert.GreaterOrEqual(birthTick, 0, "双方各 ≥1 次击杀后应能出生");
            Assert.AreEqual(3, creatures.Count);
        }

        private static void ResetBirdFrame(Creature female, Creature male)
        {
            female.AiState = BirdBrain.StateFly;
            male.AiState = BirdBrain.StateFly;
        }

        // ============================== 9. 新生儿本帧不推进 ==============================

        [Test]
        public void Newborn_StartsAtAgeZeroWithInitialCooldown_AndAgesOnlyOnNextFrame()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);

            int birthTick = RunUntilBirth(world, creatures, female, male, 20f, 3f, 600, ResetRabbitFrame);
            Assert.GreaterOrEqual(birthTick, 0);

            Creature newborn = creatures.Creatures[creatures.Count - 1];
            Assert.AreEqual(0, newborn.AgeTicks, "出生帧年龄必须仍为 0（出生应用在②之后）");
            Assert.AreEqual(
                CreatureReproduction.RabbitInitialCooldownTicks,
                newborn.BreedCooldownTicks,
                "新生儿带初始冷却，防止出生即生");
            Assert.AreEqual(0, newborn.PreyEaten, "新生儿捕食计数归零");

            creatures.Step(world);
            Assert.AreEqual(1, newborn.AgeTicks, "下一帧才 +1");
        }

        // ============================== 10. 确定性 / 随机流隔离 ==============================

        [Test]
        public void SameSeed_ProducesSameBirthTick()
        {
            int first = RunRabbitPairAndGetBirthTick();
            int second = RunRabbitPairAndGetBirthTick();

            Assert.GreaterOrEqual(first, 0);
            Assert.AreEqual(first, second, "同 seed 的出生帧号必须逐位一致");
        }

        private static int RunRabbitPairAndGetBirthTick()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            Creature female = creatures.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);

            return RunUntilBirth(world, creatures, female, male, 20f, 3f, 600, ResetRabbitFrame);
        }

        [Test]
        public void Reproduction_DoesNotConsumeParticleRandomStreamOrWriteCells()
        {
            ParticleWorld withAnimals = NewWorld();
            ParticleWorld untouched = NewWorld();
            AddRabbitGround(withAnimals);
            AddRabbitGround(untouched);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            Creature female = creatures.Spawn(new RabbitCreature(), withAnimals, 20f, 3f);
            Creature male = creatures.Spawn(new RabbitCreature(), withAnimals, 21f, 3f);
            MakeRabbitReady(female, CreatureReproduction.SexFemale);
            MakeRabbitReady(male, CreatureReproduction.SexMale);

            // 跑到至少出生一次，确保“真正发生了繁殖”的路径也被覆盖。
            int birthTick = RunUntilBirth(withAnimals, creatures, female, male, 20f, 3f, 600, ResetRabbitFrame);
            Assert.GreaterOrEqual(birthTick, 0);

            AssertCellsMatch(untouched, withAnimals, "繁殖");
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(
                    untouched.NextUInt(),
                    withAnimals.NextUInt(),
                    "动物繁殖不得消耗粒子世界的随机流");
            }
        }

        [Test]
        public void RejectedBreedAttempts_DoNotConsumeCreatureRandomStream()
        {
            ParticleWorld world = NewWorld();
            AddRabbitGround(world);
            CreatureWorld probe = new CreatureWorld(SeedValue);
            CreatureWorld reference = new CreatureWorld(SeedValue);

            Creature first = probe.Spawn(new RabbitCreature(), world, 20f, 3f);
            Creature second = probe.Spawn(new RabbitCreature(), world, 21f, 3f);
            MakeRabbitReady(first, CreatureReproduction.SexFemale);
            MakeRabbitReady(second, CreatureReproduction.SexFemale); // 同性 ⇒ 无配对者

            for (int i = 0; i < 500; i++)
            {
                Assert.IsFalse(CreatureReproduction.TryBreed(first, probe, world));
            }

            // 前置条件失败（无配对者）必须一个随机数都不摇：两条同 seed 流的下一步逐位一致。
            for (int i = 0; i < 16; i++)
            {
                Assert.AreEqual(reference.NextUInt(), probe.NextUInt(), "被拒绝的尝试不应消耗动物随机流");
            }
        }
    }
}
