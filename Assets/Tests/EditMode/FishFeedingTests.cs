using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 鱼的进食与饥饿（<c>docs/design-creature-ai.md</c> §7.2「进食与饥饿」，2026-10-17）：
    /// 会饿死（上限 <see cref="FishCreature.HungerLimitFrames"/>）、只吃水草**顶格**、啃掉后顶格相变成水（水账 1:1）、
    /// 冷却节流、不消耗动物随机流。
    /// </summary>
    /// <remarks>
    /// <para><b>搭台口径与 <see cref="FishShallowWaterTests"/> 一致：</b>用小世界 + 手工摆地形（石底 + 一池水），
    /// 只驱动 <see cref="CreatureWorld.Step"/>（不推进粒子世界——水草因此不会生长 / 衰老，用例可判别）。
    /// 植物用 <see cref="ParticleWorld.Set"/> 直接摆成柱状株。</para>
    /// <para><b>顶格判据：</b>可吃格 = <see cref="ParticleIds.AquaticPlant"/> 且正上方不是同类。
    /// 本文件所有「三格株」都摆成连续柱：只有最上面那一格可吃，下方茎格永不被啃。</para>
    /// <para><b>鱼的身体口径：</b>碰撞盒 1 × 0.5 格、<c>X/Y</c> 是底边中点，碰撞盒中心行 =
    /// <c>floor(Y + 0.5 · Height)</c>（与 <c>FishBrain.TryFeed</c> 的「相邻」判据同一口径）。</para>
    /// </remarks>
    public sealed class FishFeedingTests
    {
        /// <summary>世界宽（格）：开路巡游 / 饿死用例要跑够长，留足横向空间。</summary>
        private const int W = 96;

        /// <summary>世界高（格）：石底 + 14 格水 + 空气层。</summary>
        private const int H = 48;

        /// <summary>石底占行 <c>0 .. BottomRow − 1</c>，水从 <see cref="BottomRow"/> 行开始。</summary>
        private const int BottomRow = 3;

        /// <summary>默认水深（格）：鱼（4 格图案）在其中能正常巡游 / 下沉，不会被浅水逻辑钉死。</summary>
        private const int PoolDepth = 14;

        /// <summary>固定 seed（与其它动物测试同一条 xorshift32 流，仅取值不同）。</summary>
        private const uint SeedValue = 20261017u;

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

        /// <summary>石底 + 一整池水（水行 <c>BottomRow .. BottomRow + depth − 1</c>，其余为空气）。</summary>
        private static ParticleWorld OpenPool(int depth)
        {
            ParticleWorld world = new ParticleWorld(W, H, SeedValue);
            FillRect(world, 0, W - 1, 0, BottomRow - 1, ParticleIds.Stone);
            FillRect(world, 0, W - 1, BottomRow, BottomRow + depth - 1, ParticleIds.Water);
            return world;
        }

        /// <summary>水柱中心的 Y（与 <see cref="FishShallowWaterTests"/> 同口径）。</summary>
        private static float PoolCenterY(int depth)
        {
            return BottomRow + (depth - 1) * 0.5f;
        }

        /// <summary>在 <paramref name="x"/> 列摆一株高 <paramref name="cells"/> 格的水草（自 <see cref="BottomRow"/> 起向上）。</summary>
        private static void PlantColumn(ParticleWorld world, int x, int cells)
        {
            FillRect(world, x, x, BottomRow, BottomRow + cells - 1, ParticleIds.AquaticPlant);
        }

        /// <summary>跑 <paramref name="frames"/> 个逻辑帧。</summary>
        private static void RunSteps(ParticleWorld world, CreatureWorld creatures, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                creatures.Step(world);
            }
        }

        /// <summary>逐帧推进直到植物格数下降（= 发生了一次进食）；返回是否在预算内吃到。</summary>
        private static bool StepUntilPlantEaten(
            ParticleWorld world, CreatureWorld creatures, int plantsBefore, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                creatures.Step(world);
                if (world.Counts.Count(ParticleIds.AquaticPlant) < plantsBefore)
                {
                    return true;
                }
            }

            return false;
        }

        // ============================== 1. 没有水草会饿死 ==============================

        /// <summary>
        /// 纯水缸（没有任何水草）里的鱼跑满 <see cref="FishCreature.HungerLimitFrames"/> 帧 ⇒ 饿死、
        /// 死因 <see cref="CreatureDeathCause.Starved"/>；少一帧时仍存活且饥饿计数正好是 <c>上限 − 1</c>。
        /// </summary>
        [Test]
        public void Fish_StarvesWithoutPlants()
        {
            ParticleWorld world = OpenPool(PoolDepth);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(
                new FishCreature(), world, W * 0.5f + 0.5f, PoolCenterY(PoolDepth));

            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) => cause = deathCause;

            RunSteps(world, creatures, FishCreature.HungerLimitFrames - 1);

            Assert.IsTrue(fish.Alive, "饥饿上限前 1 帧不应死亡（鱼不该被基类默认 1200 封顶提前饿死 / 也不该提前死）");
            Assert.AreEqual(FishCreature.HungerLimitFrames - 1, fish.HungerTicks, "饥饿计数应逐帧 +1，未被 1200 封顶");

            creatures.Step(world);

            Assert.IsFalse(fish.Alive, "无食物的鱼达到 HungerLimitFrames 应饿死");
            Assert.AreEqual(CreatureDeathCause.Starved, cause, "死因应为 Starved");
            Assert.AreEqual(0, creatures.Count, "死亡实体应已被清扫");
        }

        // ============================== 2. 只吃顶格 ==============================

        /// <summary>
        /// 三格株（行 <see cref="BottomRow"/> .. <see cref="BottomRow"/> + 2）旁的饿鱼吃掉**顶格**：
        /// 顶格变水、植物 −1、水 +1、饥饿下降、进入冷却；下方两格茎仍是植物（不啃下方茎）。
        /// </summary>
        [Test]
        public void Fish_EatsTopmostCellOnly()
        {
            const int plantX = 20;
            const int topRow = BottomRow + 2;
            ParticleWorld world = OpenPool(PoolDepth);
            PlantColumn(world, plantX, 3);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 24.5f, BottomRow);

            int plantsBefore = world.Counts.Count(ParticleIds.AquaticPlant);
            int waterBefore = world.Counts.Count(ParticleIds.Water);
            Assert.AreEqual(3, plantsBefore, "前置：三格株");

            fish.HungerTicks = 1000; // 越过 HungryThreshold(600)，但离上限很远

            bool ate = StepUntilPlantEaten(world, creatures, plantsBefore, 400);

            Assert.IsTrue(ate, "400 帧内应游到顶格旁并啃掉一格");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(plantX, topRow).typeId, "顶格应相变成水");
            Assert.AreEqual(ParticleIds.AquaticPlant, world.GetCell(plantX, BottomRow).typeId, "最下方茎格仍是植物");
            Assert.AreEqual(
                ParticleIds.AquaticPlant, world.GetCell(plantX, BottomRow + 1).typeId, "中间茎格仍是植物（不啃下方茎）");
            Assert.AreEqual(2, world.Counts.Count(ParticleIds.AquaticPlant), "植物格数应 −1");
            Assert.AreEqual(waterBefore + 1, world.Counts.Count(ParticleIds.Water), "被啃掉的那格变水（水账 1:1）");
            Assert.Less(fish.HungerTicks, 1000, "进食应把饥饿计数降回去");
            Assert.Greater(fish.FeedingCooldownTicks, 0, "进食后应进入冷却");
        }

        // ============================== 3. 半径内没有可吃目标 ==============================

        /// <summary>
        /// 水草在搜索半径（<see cref="FishBrain.FoodSearchRadius"/>）之外 ⇒ 鱼不吃、饥饿照常上涨、植物纹丝不动。
        /// </summary>
        [Test]
        public void Fish_NoPlantNearby_DoesNotEatNorResetHunger()
        {
            const int plantX = 40;
            ParticleWorld world = OpenPool(PoolDepth);
            PlantColumn(world, plantX, 3);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 10.5f, PoolCenterY(PoolDepth));

            int plantsBefore = world.Counts.Count(ParticleIds.AquaticPlant);
            const int frames = 40;
            fish.HungerTicks = FishBrain.HungryThreshold - 1; // 第一帧就跨过阈值 ⇒ 每个思考帧都会尝试寻食

            RunSteps(world, creatures, frames);

            Assert.AreEqual(FishBrain.HungryThreshold - 1 + frames, fish.HungerTicks, "找不到食物时饥饿计数应逐帧上涨（未被进食重置）");
            Assert.AreEqual(plantsBefore, world.Counts.Count(ParticleIds.AquaticPlant), "半径外的水草不应被啃");
            Assert.AreEqual(ParticleIds.AquaticPlant, world.GetCell(plantX, BottomRow + 2).typeId, "顶格仍应是植物");
        }

        // ============================== 4. 一次冷却只吃一格 ==============================

        /// <summary>
        /// 相邻有两格顶格（两株并排）：吃掉第一格后，在剩余冷却窗口内（15 帧 &lt; <see cref="FishBrain.FeedingCooldown"/>）
        /// 不再吃第二格——证明「每冷却只啃一格」的节流真实生效。
        /// </summary>
        [Test]
        public void Fish_EatsOneCellPerCooldown()
        {
            ParticleWorld world = OpenPool(PoolDepth);
            PlantColumn(world, 19, 2); // 顶格 (19, BottomRow + 1)
            PlantColumn(world, 21, 2); // 顶格 (21, BottomRow + 1)
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 20.5f, BottomRow);

            int plantsBefore = world.Counts.Count(ParticleIds.AquaticPlant);
            Assert.AreEqual(4, plantsBefore, "前置：两株各两格");
            fish.HungerTicks = 2000; // 远低于上限 2400（否则首帧就饿死），第一口后仍 1400 > 阈值 ⇒ 只有冷却能阻止第二口

            bool ate = StepUntilPlantEaten(world, creatures, plantsBefore, 60);
            Assert.IsTrue(ate, "第一口应很快吃到");

            int plantsAfterFirstBite = world.Counts.Count(ParticleIds.AquaticPlant);
            Assert.AreEqual(3, plantsAfterFirstBite, "第一口只啃一格");

            RunSteps(world, creatures, 15); // < FeedingCooldown ⇒ 下一次思考帧仍在冷却中

            Assert.AreEqual(3, world.Counts.Count(ParticleIds.AquaticPlant), "冷却窗口内不应啃第二格");
            Assert.Greater(fish.FeedingCooldownTicks, 0, "15 帧后冷却仍未走完");
        }

        // ============================== 5. 进食不消耗动物随机流 ==============================

        /// <summary>
        /// 饥饿的鱼吃到一格水草 ⇒ 同一 <see cref="CreatureWorld"/> 的随机流与同 seed、**从未驱动**的对照世界
        /// 逐位一致：寻食扫描 / 移动 / 啃食都不摇动物骰（世界随机流可被 <c>SetPhase</c> 消耗，不在本断言范围）。
        /// </summary>
        /// <remarks>首个思考帧本来会走搁浅分支摇一次 <c>NextInt(21)</c>；这里预置
        /// <see cref="Creature.FeedingCooldownTicks"/> 无关的搁浅黑板（<see cref="Creature.AiState"/> /
        /// <see cref="Creature.StateTicksLeft"/>），让那次既有消耗也不发生，从而能直接与「从未驱动」对照。</remarks>
        [Test]
        public void Fish_FeedingDoesNotConsumeAnimalRandomStream()
        {
            const int plantX = 20;
            ParticleWorld world = OpenPool(PoolDepth);
            PlantColumn(world, plantX, 3);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            // 鱼必须站在**水格**里（与茎同列会让碰撞盒中心落进植物格 ⇒ IsSubmerged = false ⇒ 走搁浅分支），
            // 且与目标顶格切比雪夫相邻：目标 (plantX, BottomRow+2)，鱼中心 (plantX+1, BottomRow+1)。
            FishCreature fish = creatures.Spawn(new FishCreature(), world, plantX + 1.5f, BottomRow + 1);

            int plantsBefore = world.Counts.Count(ParticleIds.AquaticPlant);
            fish.HungerTicks = 2000; // 低于饥饿上限（2400）⇒ 不会首帧饿死；仍远高于 HungryThreshold

            // 首帧 IsSubmerged 还是初值 false ⇒ 会走搁浅分支；预置「已在搁浅态且未到期」⇒ 该分支零摇骰。
            fish.AiState = FishBrain.StateStranded;
            fish.StateTicksLeft = 1000;

            bool ate = StepUntilPlantEaten(world, creatures, plantsBefore, 40);
            Assert.IsTrue(ate, "相邻的饿鱼应在一个思考帧内吃到底格上方那格水草");

            CreatureWorld neverDriven = new CreatureWorld(SeedValue);
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(
                    neverDriven.NextUInt(),
                    creatures.NextUInt(),
                    "鱼的进食路径不得消耗动物随机流（第 " + i + " 次抽样分叉）");
            }
        }

        // ============================== 6. 回归：与巡游互不干扰 ==============================

        /// <summary>
        /// 不饿的鱼不进入进食路径 ⇒ 行为与旧版巡游一致（本用例作为本文件的正面证据：
        /// 饥饿阈值以下鱼不会突然转向水草；真正的逐位回归由 <see cref="FishShallowWaterTests"/> 承担）。
        /// </summary>
        [Test]
        public void Fish_NotHungry_IgnoresPlants()
        {
            const int plantX = 20;
            ParticleWorld world = OpenPool(PoolDepth);
            PlantColumn(world, plantX, 3);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, plantX + 1f, BottomRow);

            int plantsBefore = world.Counts.Count(ParticleIds.AquaticPlant);
            fish.HungerTicks = 100; // 300 帧后仍 400 < HungryThreshold(600) ⇒ 全程不进入进食路径

            RunSteps(world, creatures, 300);

            Assert.AreEqual(plantsBefore, world.Counts.Count(ParticleIds.AquaticPlant), "未到 HungryThreshold 不应啃草");
            Assert.AreEqual(Creature.NoFoodTarget, fish.FoodTargetX, "不饿时不应留下食物目标");
            Assert.AreEqual(Creature.NoFoodTarget, fish.FoodTargetY, "不饿时不应留下食物目标");
        }
    }
}
