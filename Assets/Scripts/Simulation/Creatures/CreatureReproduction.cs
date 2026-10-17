using System.Collections.Generic;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 两性配对繁殖的**唯一决策点**与**唯一数值来源**：只在雌性的思考帧（2 Hz）调用一次，
    /// 负责「找最近的相邻异性同类 → 判条件 → 摇一次骰 → 给双方上冷却 → 入队」。
    /// 出生本身由 <see cref="CreatureWorld.Step"/> 末尾统一应用（见 <see cref="CreatureWorld.RequestBirth"/>）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么只有雌性摇骰：</b>一方发起、一次骰子对应一次出生请求——两只异性动物不会在同一帧
    /// 各摇一次而产生“一胎两子”或两次独立冷却写入。雄性只作为被挑选的目标，不消费随机流。</para>
    /// <para><b>随机流纪律：</b>骰子只在思考帧、且在所有前置条件通过之后摇一次；性别由生成序号奇偶决定、
    /// 不摇骰。因此动物随机流的新消耗点只有“雌性思考帧里真正发生的一次繁殖尝试”，
    /// 粒子世界随机流**零消耗**（本类对 <see cref="ParticleWorld"/> 只读）。</para>
    /// <para><b>整数概率：</b>阈值 = <c>分母 × (上限 − count) / 上限</c>（先整除再乘下限因子 2），
    /// 全程整数运算，避免浮点在不同平台上的末位差异破坏同 seed 逐位复现。</para>
    /// <para><b>物质不守恒（用户口径）：</b>出生不消耗世界物质，只受成年 / 冷却 / 条件 / 软上限约束。</para>
    /// </remarks>
    public static class CreatureReproduction
    {
        /// <summary>配对半径（格，两实体**中心**的欧氏距离）：≤ 本值才算“相邻”。</summary>
        public const int MateRadius = 2;

        /// <summary>雌性性别值（生成序号偶数为雌）。</summary>
        public const byte SexFemale = 0;

        /// <summary>雄性性别值（生成序号奇数为雄）。</summary>
        public const byte SexMale = 1;

        // ============================== 兔（陆生，验证体） ==============================
        // 成年 300 帧 = 15 秒（20 Hz），冷却 400 帧 = 20 秒，基础概率 1/20，上限 12，下限 2。
        // 兔繁殖最快：条件只需雌性刚吃过（饥饿计数 < 300 帧）且双方不在 Flee。

        /// <summary>兔成年年龄（逻辑帧）：15 秒 @20 Hz。</summary>
        public const int RabbitMaturityTicks = 300;

        /// <summary>兔繁殖冷却（逻辑帧）：20 秒 @20 Hz，出生后双方各自写入。</summary>
        public const int RabbitCooldownTicks = 400;

        /// <summary>兔基础概率分母：每次合格思考帧 1/20 = 5%。</summary>
        public const int RabbitProbabilityDenominator = 20;

        /// <summary>兔种群软上限。</summary>
        public const int RabbitMaxPopulation = 12;

        /// <summary>兔种群下限：低于它时概率因子 ×2。</summary>
        public const int RabbitMinPopulation = 2;

        /// <summary>兔新生儿初始冷却（逻辑帧，= 成年个体的冷却）：防止“出生即生”。</summary>
        public const int RabbitInitialCooldownTicks = RabbitCooldownTicks;

        /// <summary>兔雌性的“刚吃过”阈值：<see cref="Creature.HungerTicks"/> 必须小于它。
        /// 300 帧 = 15 秒，约等于一次进食提供的 <c>RabbitBrain.FoodRestoreTicks</c>（420）的大半，
        /// 含义是“最近 15 秒内吃过”，而不是饿到半死才繁殖。</summary>
        public const int RabbitFullHungerTicks = 300;

        // ============================== 鱼（水生） ==============================
        // 成年 400 帧 = 20 秒，冷却 600 帧 = 30 秒，基础概率 1/20，上限 12，下限 2。
        // 条件：双方完全没入水下、所在水柱 depth ≥ 7 格（够游开），且不在搁浅态。

        /// <summary>鱼成年年龄（逻辑帧）：20 秒。</summary>
        public const int FishMaturityTicks = 400;

        /// <summary>鱼繁殖冷却（逻辑帧）：30 秒。</summary>
        public const int FishCooldownTicks = 600;

        /// <summary>鱼基础概率分母：1/20。</summary>
        public const int FishProbabilityDenominator = 20;

        /// <summary>鱼种群软上限。</summary>
        public const int FishMaxPopulation = 12;

        /// <summary>鱼种群下限：低于它时概率因子 ×2。</summary>
        public const int FishMinPopulation = 2;

        /// <summary>鱼新生儿初始冷却（逻辑帧）。</summary>
        public const int FishInitialCooldownTicks = FishCooldownTicks;

        /// <summary>鱼繁殖所需的最小水柱深度（格）：与 <see cref="FishBrain.DiveUnlockMarginCells"/> 的
        /// “够摆下身体 + 3 格余量”同一量级（身体 4 行 + 3 格余量 = 7），保证出生点不是一汪浅水。</summary>
        public const int FishMinWaterDepth = 7;

        // ============================== 鸟（飞行捕食者，最慢） ==============================
        // 成年 1200 帧 = 60 秒，冷却 3000 帧 = 150 秒，基础概率 1/10，上限 4，下限 1。
        // 条件：双方各自至少完成过一次捕食，且都不在 Flee / Hunt 态。

        /// <summary>鸟成年年龄（逻辑帧）：60 秒——三个物种里最长，保证第一批鸟必须经历完整成长。</summary>
        public const int BirdMaturityTicks = 1200;

        /// <summary>鸟繁殖冷却（逻辑帧）：150 秒——最长冷却，对应“顶级捕食者繁殖最慢”。</summary>
        public const int BirdCooldownTicks = 3000;

        /// <summary>鸟基础概率分母：1/10（上限只有 4，若仍用 1/20 会过于稀少）。</summary>
        public const int BirdProbabilityDenominator = 10;

        /// <summary>鸟种群软上限。</summary>
        public const int BirdMaxPopulation = 4;

        /// <summary>鸟种群下限：低于它时概率因子 ×2。</summary>
        public const int BirdMinPopulation = 1;

        /// <summary>鸟新生儿初始冷却（逻辑帧）。</summary>
        public const int BirdInitialCooldownTicks = BirdCooldownTicks;

        /// <summary>鸟繁殖所需的捕食成功次数（双方各自）。</summary>
        public const int BirdRequiredPreyEaten = 1;

        /// <summary>物种分派（本文件内私有；三个物种用 <c>is</c> 手写，与出生应用阶段同一口径）。</summary>
        private enum Species
        {
            None = 0,
            Rabbit = 1,
            Fish = 2,
            Bird = 3,
        }

        /// <summary>容量因子：种群低于下限时 ×2，否则 ×1。公开给测试**不靠统计**地钉住“下限翻倍”。</summary>
        /// <param name="count">当前物种数量（含本帧将死未清扫者）。</param>
        /// <param name="minPopulation">该物种下限。</param>
        /// <returns>1 或 2。</returns>
        public static int CapacityFactor(int count, int minPopulation)
        {
            return count < minPopulation ? 2 : 1;
        }

        /// <summary>本次尝试的命中阈值（整数）：<c>分母 × (上限 − count) / 上限 × 容量因子</c>。
        /// 先整除再乘因子，保证与实现逐位一致；阈值可能 ≥ 分母（低种群时概率封顶为必中）。</summary>
        /// <param name="denominator">该物种基础概率分母。</param>
        /// <param name="maxPopulation">物种软上限。</param>
        /// <param name="count">当前物种数量。</param>
        /// <param name="minPopulation">物种下限。</param>
        /// <returns>摇骰 <c>NextInt(denominator) &lt; 本值</c> 即成功。</returns>
        public static int SuccessThreshold(int denominator, int maxPopulation, int count, int minPopulation)
        {
            if (denominator <= 0 || maxPopulation <= 0)
            {
                return 0;
            }

            int threshold = denominator * (maxPopulation - count) / maxPopulation;
            return threshold * CapacityFactor(count, minPopulation);
        }

        /// <summary>
        /// 雌性的一次繁殖尝试：找最近相邻异性同类、判定物种条件与软上限、摇一次骰；
        /// 命中则给双方写入冷却并把出生请求入队。
        /// </summary>
        /// <remarks>
        /// 前置顺序即随机流契约：先做全部零消耗判定（存活 / 性别 / 成年 / 冷却 / 物种条件 / 有配对者 / 未到上限），
        /// 只有全部通过才调用 <see cref="CreatureWorld.NextInt"/>。任何一步失败都不消耗随机数。
        /// </remarks>
        /// <param name="creature">发起尝试的个体（必须是雌性）。</param>
        /// <param name="creatures">所属动物世界（种群计数 + 随机流 + 出生队列）。</param>
        /// <param name="world">粒子世界（只读，用于鱼的水深判定）。</param>
        /// <returns>本次是否成功发起了一次出生请求（骰子命中并入队）。</returns>
        public static bool TryBreed(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            if (creature == null || creatures == null || world == null)
            {
                return false;
            }

            Species species = SpeciesOf(creature);
            if (species == Species.None)
            {
                return false;
            }

            if (!creature.Alive || creature.Sex != SexFemale)
            {
                return false;
            }

            int maturity;
            int cooldown;
            int denominator;
            int maxPopulation;
            int minPopulation;
            switch (species)
            {
                case Species.Rabbit:
                    maturity = RabbitMaturityTicks;
                    cooldown = RabbitCooldownTicks;
                    denominator = RabbitProbabilityDenominator;
                    maxPopulation = RabbitMaxPopulation;
                    minPopulation = RabbitMinPopulation;
                    break;
                case Species.Fish:
                    maturity = FishMaturityTicks;
                    cooldown = FishCooldownTicks;
                    denominator = FishProbabilityDenominator;
                    maxPopulation = FishMaxPopulation;
                    minPopulation = FishMinPopulation;
                    break;
                default:
                    maturity = BirdMaturityTicks;
                    cooldown = BirdCooldownTicks;
                    denominator = BirdProbabilityDenominator;
                    maxPopulation = BirdMaxPopulation;
                    minPopulation = BirdMinPopulation;
                    break;
            }

            if (creature.AgeTicks < maturity || creature.BreedCooldownTicks != 0)
            {
                return false;
            }

            if (!SelfSatisfiesSpeciesCondition(species, creature, world))
            {
                return false;
            }

            Creature mate = FindMate(creature, creatures, species, maturity, world);
            if (mate == null)
            {
                return false;
            }

            int count = CountSpecies(creatures, species);
            if (count >= maxPopulation)
            {
                return false;
            }

            int threshold = SuccessThreshold(denominator, maxPopulation, count, minPopulation);
            if (creatures.NextInt(denominator) >= threshold)
            {
                return false;
            }

            // 先上冷却再入队：同一思考帧里后续调用（以及父亲的任何路径）都会被冷却挡住，防一胎多子。
            creature.BreedCooldownTicks = cooldown;
            mate.BreedCooldownTicks = cooldown;
            creatures.RequestBirth(creature, mate);
            return true;
        }

        /// <summary>物种分派：只认三个具体物种，其它 <see cref="Creature"/> 派生类型不参与繁殖。</summary>
        private static Species SpeciesOf(Creature creature)
        {
            if (creature is RabbitCreature)
            {
                return Species.Rabbit;
            }

            if (creature is FishCreature)
            {
                return Species.Fish;
            }

            return creature is BirdCreature ? Species.Bird : Species.None;
        }

        /// <summary>按物种统计种群（走 <see cref="CreatureWorld.CountOf{T}"/>，零分配）。</summary>
        private static int CountSpecies(CreatureWorld creatures, Species species)
        {
            switch (species)
            {
                case Species.Rabbit:
                    return creatures.CountOf<RabbitCreature>();
                case Species.Fish:
                    return creatures.CountOf<FishCreature>();
                default:
                    return creatures.CountOf<BirdCreature>();
            }
        }

        /// <summary>发起者（雌性）自身的物种条件。</summary>
        private static bool SelfSatisfiesSpeciesCondition(Species species, Creature creature, ParticleWorld world)
        {
            switch (species)
            {
                case Species.Rabbit:
                    // 雌性“刚吃过” + 双方都不在 Flee（自身这一半）。
                    return creature.HungerTicks < RabbitFullHungerTicks
                        && creature.AiState != RabbitBrain.StateFlee;
                case Species.Fish:
                    return FishSatisfiesWaterCondition(creature, world);
                default:
                    return BirdSatisfiesCondition(creature);
            }
        }

        /// <summary>配对目标（雄性）的物种条件：与自身条件同一套判据（兔的“刚吃过”是雌性专属，不要求雄性）。</summary>
        private static bool MateSatisfiesSpeciesCondition(Species species, Creature mate, ParticleWorld world)
        {
            switch (species)
            {
                case Species.Rabbit:
                    return mate.AiState != RabbitBrain.StateFlee;
                case Species.Fish:
                    return FishSatisfiesWaterCondition(mate, world);
                default:
                    return BirdSatisfiesCondition(mate);
            }
        }

        /// <summary>鱼：完全没入水下、所在水柱 depth ≥ <see cref="FishMinWaterDepth"/>、且不在搁浅态。
        /// “完全没入”用体顶之上那一格仍是水判定（与 <see cref="FishBrain"/> 的触面口径一致）；
        /// depth 取**包含本体的那一段连续水柱**：向上用共享探针
        /// <see cref="CreatureSensors.ProbeSurfaceRowAbove"/> 找水面，向下扫到第一个非水行。</summary>
        private static bool FishSatisfiesWaterCondition(Creature fish, ParticleWorld world)
        {
            if (fish.AiState == FishBrain.StateStranded)
            {
                return false;
            }

            int column = FloorToInt(fish.X);
            int footRow = FloorToInt(fish.Y);
            int bodyTopRow = footRow + FishBrain.BodyTopOffsetCells;

            // 体顶之上那一格不是水（含越界）⇒ 身体露出水面，不算“完全没入”。
            int surfaceRow = CreatureSensors.ProbeSurfaceRowAbove(world, column, bodyTopRow + 1);
            if (surfaceRow <= bodyTopRow + 1)
            {
                return false;
            }

            int floorRow = ProbeFloorRowBelow(world, column, footRow - 1);
            int depth = surfaceRow - floorRow - 1;
            return depth >= FishMinWaterDepth;
        }

        /// <summary>鸟：双方各自至少完成过一次捕食，且都不在 Flee / Hunt 态。</summary>
        private static bool BirdSatisfiesCondition(Creature bird)
        {
            return bird.PreyEaten >= BirdRequiredPreyEaten
                && bird.AiState != BirdBrain.StateFlee
                && bird.AiState != BirdBrain.StateHunt;
        }

        /// <summary>
        /// 按生成序扫描最近的合格异性同类：中心欧氏距离 ≤ <see cref="MateRadius"/>、存活、成年、
        /// 冷却为 0、且满足该物种的“父方条件”。严格更近才替换、平手保留先生成者
        /// （与 <c>BirdBrain.TryDetectPrey</c> 同口径）。零分配，只读世界与实体表。
        /// </summary>
        private static Creature FindMate(
            Creature self, CreatureWorld creatures, Species species, int maturity, ParticleWorld world)
        {
            IReadOnlyList<Creature> all = creatures.Creatures;
            Creature best = null;
            float bestDistSq = float.MaxValue;
            int radiusSq = MateRadius * MateRadius;
            float selfCenterX = self.X;
            float selfCenterY = self.Y + self.Height * 0.5f;

            for (int i = 0; i < all.Count; i++)
            {
                Creature other = all[i];
                if (other == self || !other.Alive || other.Sex == self.Sex)
                {
                    continue;
                }

                if (SpeciesOf(other) != species || other.AgeTicks < maturity || other.BreedCooldownTicks != 0)
                {
                    continue;
                }

                if (!MateSatisfiesSpeciesCondition(species, other, world))
                {
                    continue;
                }

                float dx = other.X - selfCenterX;
                float dy = other.Y + other.Height * 0.5f - selfCenterY;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq)
                {
                    continue;
                }

                // 严格更近才替换 ⇒ 平手保留先生成者（扫描序 = 生成序）。
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = other;
                }
            }

            return best;
        }

        /// <summary>自 <paramref name="startRow"/> 向下扫到第一个非水格（越界算非水）⇒ 水柱底部的非水行。
        /// 与 <see cref="FishBrain"/> 的深度带测量同口径；最多扫 <c>world.Height</c> 格，必定终止（零分配）。</summary>
        private static int ProbeFloorRowBelow(ParticleWorld world, int x, int startRow)
        {
            int lastRow = startRow - world.Height;
            int y = startRow;
            while (y > lastRow && CreatureTerrain.IsWater(world, x, y))
            {
                y--;
            }

            return y;
        }

        /// <summary>向下取整（不走 Mathf，保持决策路径零 UnityEngine 依赖）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }
    }
}
