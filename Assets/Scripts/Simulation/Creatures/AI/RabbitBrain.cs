namespace TapTap2026.Simulation
{
    /// <summary>
    /// 兔子的行为状态机：**静态无状态**——所有决策状态都写在 <see cref="Creature"/> 的黑板槽位上，
    /// Brain 只是「思考函数」的容器，天然没有跨个体共享状态。
    /// </summary>
    /// <remarks>
    /// <para><b>状态（<see cref="Creature.AiState"/>）：</b>0=Idle 站立、1=Wander 漫步、2=Flee 逃命、
    /// 3=SeekFood 觅食、4=Eat 进食。</para>
    /// <para><b>节奏：</b>只应被 <see cref="Creature.ShouldThink"/> 放行后以 2 Hz 调用；每次思考内部
    /// 才会写意图、摇时长——RNG（<see cref="CreatureWorld.NextInt"/>）因此只在思考帧消耗。</para>
    /// <para><b>运动：</b>真正把意图变成速度、做悬崖守卫 / 撞墙翻越的是每帧的
    /// <see cref="Creature.ApplyLocomotion"/>——本类不做任何逐帧工作（饥饿计数也由它推进）。</para>
    /// <para><b>优先级：</b>火焰威胁 &gt; 饥饿觅食 &gt; 既有 Idle / Wander。</para>
    /// <para><b>食物：</b>吃 <see cref="ParticleIds.Grass"/> 与 <see cref="ParticleIds.Flower"/>（可食集合由
    /// <see cref="IsEdibleType"/> 单点定义；草种子 / 腐殖质 / 土壤不吃），一次一格；
    /// 局部半径 6 目标选择，不做全局寻路。</para>
    /// </remarks>
    public static class RabbitBrain
    {
        /// <summary>状态：站立（<see cref="Creature.IntentVx"/> = 0）。</summary>
        public const byte StateIdle = 0;

        /// <summary>状态：漫步（一个方向走一小段）。</summary>
        public const byte StateWander = 1;

        /// <summary>状态：逃命（背向火跑，豁免悬崖守卫）。</summary>
        public const byte StateFlee = 2;

        /// <summary>状态：觅食（朝局部可食植物目标移动）。</summary>
        public const byte StateSeekFood = 3;

        /// <summary>状态：进食（停住并清除邻格一格可食植物）。</summary>
        public const byte StateEat = 4;

        /// <summary>开始寻找食物的饥饿阈值（逻辑帧，30 秒）。</summary>
        public const int HungryThreshold = 600;

        /// <summary>食物优先级高于漫步的饥饿阀值（逻辑帧，50 秒）。</summary>
        public const int CriticalHungerThreshold = 1000;

        /// <summary>吃一格可食植物（草 / 花）恢复的饥饿帧数。</summary>
        public const int FoodRestoreTicks = 420;

        /// <summary>两次进食之间的最小间隔（逻辑帧）。</summary>
        public const int FeedingCooldown = 20;

        /// <summary>目标连续不可接近 / 找不到的思考帧上限，超过即放弃目标。</summary>
        public const int FoodTargetFailLimit = 40;

        /// <summary>可进食的格差范围：<c>|target − floor(pos)| ≤ 1</c>（脚下 / 前方 / 左右）。</summary>
        public const int EatRangeX = 1;

        /// <summary>进食时的邻格搜索半径（格）。</summary>
        public const int EatSearchRadius = 1;

        /// <summary>
        /// 兔子食谱的**唯一定义点**：<see cref="ParticleIds.Grass"/> 与 <see cref="ParticleIds.Flower"/>
        /// 可食，其余粒子（草种子 / 腐殖质 / 土壤等）一律不可食。
        /// </summary>
        /// <remarks>
        /// 感知扫描（<see cref="CreatureSensors.FindNearestGrassOrFlower"/>）、目标有效性复核与进食判定
        /// （<see cref="TryEat"/>）三处都必须调用本方法——不允许各写一份类型比较，否则「看得到」与
        /// 「吃得到」会漂移成两份食谱。纯比较、零分配、不消耗随机数。
        /// </remarks>
        /// <param name="typeId">待判定的粒子类型。</param>
        /// <returns>是否属于兔子可食集合（草 / 花）。</returns>
        public static bool IsEdibleType(ushort typeId)
        {
            return typeId == ParticleIds.Grass || typeId == ParticleIds.Flower;
        }

        /// <summary>陆上水平加速度（格 / 逻辑帧²）。</summary>
        public const float Accel = 0.02f;

        /// <summary>起跳脉冲竖直速度（格 / 逻辑帧）。</summary>
        public const float HopVelocity = 0.75f;

        /// <summary>漫步速度（格 / 逻辑帧）。</summary>
        public const float WalkSpeed = 0.06f;

        /// <summary>逃命速度（格 / 逻辑帧）。</summary>
        public const float FleeSpeed = 0.15f;

        /// <summary>逃命**脱离**的半径滞回余量（格）：已在 Flee 时把威胁扫描半径放大这么多。
        /// 一进圈就立刻回 Wander 的话，刚出圈又会被随机漫步方向带回边界上蹭——两条半径把抖动夹在中间。</summary>
        public const int FleeExitMargin = 3;

        /// <summary>最短逃命时长（逻辑帧 ≈ 2 秒）：进入逃命时写进 <see cref="Creature.StateTicksLeft"/>。
        /// 期间即使火已出圈也保持既有逃跑方向，避免「刚出圈就被随机方向带回去」。</summary>
        public const int FleeMinTicks = 40;

        /// <summary>
        /// 一次思考：先扫威胁（有火就逃，**最近**那团火决定方向）；否则在饥饿且冷却结束时走觅食分支；
        /// 再否则按既有状态转移。
        /// </summary>
        /// <remarks>
        /// <para><b>逃命两条半径（滞回）：</b>进入用 <see cref="Creature.ThreatRadius"/>、脱离用
        /// <c>ThreatRadius + <see cref="FleeExitMargin"/></c>——同一条探针、只换半径，因此火在入圈半径上
        /// 晃动时不会让兔子在 Flee / Wander 之间来回跳。</para>
        /// <para><b>最短逃命时长：</b>刚进入时写 <see cref="FleeMinTicks"/>；火出圈后计时未走完就按当前方向
        /// 继续跑（不改意图），走完才回 Wander。</para>
        /// <para><b>RNG：</b>只有「刚进入且火与中心同列（没有背向可言）」以及「无逃跑方向」才摇一次骰；
        /// 其余思考帧一律不消耗随机数。</para>
        /// </remarks>
        /// <param name="creature">思考的兔子。</param>
        /// <param name="creatures">所属动物世界（RNG 来源）。</param>
        /// <param name="world">粒子世界（只读感知）。</param>
        public static void Think(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            int cx = FloorToInt(creature.X);
            int cy = FloorToInt(creature.Y + creature.Height * 0.5f);

            bool fleeing = creature.AiState == StateFlee;
            int radius = fleeing ? Creature.ThreatRadius + FleeExitMargin : Creature.ThreatRadius;
            if (CreatureSensors.FindNearestFire(world, cx, cy, radius, out int fireX, out int fireY))
            {
                // 逃命优先：朝**最近**那团火的反方向跑（取最近而不是取扫描序先见者——散开火场时
                // 先见者往往在更低的行 / 更远处，会把人推向更近的火）。
                if (fireX > cx)
                {
                    creature.IntentVx = -FleeSpeed;
                }
                else if (fireX < cx)
                {
                    creature.IntentVx = FleeSpeed;
                }
                else if (!fleeing || creature.IntentVx == 0f)
                {
                    // 火恰在同列（正上 / 正下方）时没有「背向」可言：只在**刚进入逃命**（或当前没有逃跑方向）时
                    // 摇一次骰选一侧，否则 IntentVx = 0 会让兔子在火正下方站住。
                    // 已经跑起来又同列时**保持既有方向**——每个思考帧重摇会让兔子每 0.5 秒翻一次身。
                    int dir = creatures.NextInt(2) == 0 ? -1 : 1;
                    creature.IntentVx = -dir * FleeSpeed;
                }

                creature.AiState = StateFlee;
                if (!fleeing)
                {
                    creature.StateTicksLeft = FleeMinTicks;
                }

                return;
            }

            if (fleeing)
            {
                // 火已（暂时）出圈：最短逃命时长未走完 → 按当前方向继续跑，不改意图（刚出圈就转向
                // 会被随机漫步方向带回边界蹭）；走完 → 回 Wander（重新摇时长与方向）。
                if (creature.StateTicksLeft > 0)
                {
                    return;
                }

                EnterWander(creature, creatures);
                return;
            }

            // 饥饿觅食优先于既有 Idle / Wander（低于火焰逃命）：过阈值且冷却结束才进入 / 继续觅食分支。
            if (creature.HungerTicks >= HungryThreshold && creature.FeedingCooldownTicks == 0)
            {
                ThinkFood(creature, creatures, world, cx, cy);
                return;
            }

            switch (creature.AiState)
            {
                case StateWander:
                    if (creature.StateTicksLeft <= 0)
                    {
                        EnterIdle(creature, creatures);
                    }

                    break;

                default: // Idle 及任何尚未初始化的状态（Flee 已在上面的逃命分支内全部 return）
                    if (creature.StateTicksLeft <= 0)
                    {
                        EnterNextAfterIdle(creature, creatures);
                    }

                    break;
            }
        }

        /// <summary>觅食分支：已在 Eat 态则执行进食；否则（Idle / Wander / SeekFood）统一走 SeekFood 决策。</summary>
        private static void ThinkFood(Creature creature, CreatureWorld creatures, ParticleWorld world, int cx, int cy)
        {
            if (creature.AiState == StateEat)
            {
                ThinkEat(creature, creatures, world);
                return;
            }

            if (creature.AiState != StateSeekFood)
            {
                // 从 Idle / Wander 进入觅食：清掉旧目标，交给 ThinkSeekFood 统一扫描（寻找失败也走同一套计数）。
                creature.AiState = StateSeekFood;
                creature.FoodTargetX = Creature.NoFoodTarget;
                creature.FoodTargetY = Creature.NoFoodTarget;
                creature.FoodTargetFailTicks = 0;
            }

            ThinkSeekFood(creature, creatures, world, cx, cy);
        }

        /// <summary>
        /// SeekFood 的每思考帧决策：目标失效则重扫；进入进食范围转 Eat；否则朝目标走；
        /// 连续 <see cref="FoodTargetFailLimit"/> 帧未接近即放弃目标、回 Wander。
        /// </summary>
        private static void ThinkSeekFood(Creature creature, CreatureWorld creatures, ParticleWorld world, int cx, int cy)
        {
            int tx = creature.FoodTargetX;
            int ty = creature.FoodTargetY;

            bool targetValid = tx != Creature.NoFoodTarget
                && world.IsInBounds(tx, ty)
                && IsEdibleType(world.GetCell(tx, ty).typeId);

            if (!targetValid)
            {
                if (!CreatureSensors.FindNearestGrassOrFlower(world, cx, cy, out tx, out ty))
                {
                    // 无候选：计数并在超过上限后放弃。期间不写意图、不消耗随机数（先判后摇）。
                    creature.FoodTargetX = Creature.NoFoodTarget;
                    creature.FoodTargetY = Creature.NoFoodTarget;
                    creature.FoodTargetFailTicks++;
                    if (creature.FoodTargetFailTicks > FoodTargetFailLimit)
                    {
                        creature.FoodTargetFailTicks = 0;
                        EnterWander(creature, creatures);
                    }

                    return;
                }

                creature.FoodTargetX = tx;
                creature.FoodTargetY = ty;
                creature.FoodTargetFailTicks = 0;
            }

            int feetX = FloorToInt(creature.X);
            int feetY = FloorToInt(creature.Y);
            if (Abs(tx - feetX) <= EatRangeX && Abs(ty - feetY) <= EatRangeX)
            {
                // 已在进食范围：停住，下一思考帧由 ThinkEat 清除一格可食植物。
                creature.FoodTargetFailTicks = 0;
                creature.AiState = StateEat;
                creature.IntentVx = 0f;
                return;
            }

            creature.FoodTargetFailTicks++;
            if (creature.FoodTargetFailTicks > FoodTargetFailLimit)
            {
                // 目标长期不可达（如墙后）：放弃目标、随机换向、回 Wander。
                creature.FoodTargetX = Creature.NoFoodTarget;
                creature.FoodTargetY = Creature.NoFoodTarget;
                creature.FoodTargetFailTicks = 0;
                EnterWander(creature, creatures);
                return;
            }

            if (tx > creature.X)
            {
                creature.IntentVx = WalkSpeed;
            }
            else if (tx < creature.X)
            {
                creature.IntentVx = -WalkSpeed;
            }
            else
            {
                // 同列但竖直差仍大于 1：随机选一侧靠近，避免原地不动。
                int dir = creatures.NextInt(2) == 0 ? -1 : 1;
                creature.IntentVx = dir * WalkSpeed;
            }
        }

        /// <summary>
        /// Eat 的每思考帧决策：停住，按固定枚举顺序在半径 <see cref="EatSearchRadius"/> 内找第一格
        /// **可食植物**（<see cref="IsEdibleType"/>：草 / 花）并清除；
        /// 找不到则回 SeekFood 重新扫描。顺序写死（正下、正前、正上、左、右），不依赖哈希 / 迭代顺序。
        /// </summary>
        private static void ThinkEat(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            creature.IntentVx = 0f;

            int cx = FloorToInt(creature.X);
            int cy = FloorToInt(creature.Y);
            int front = creature.Facing >= 0 ? 1 : -1;

            if (TryEat(creature, creatures, world, cx, cy - 1)      // 正下方
                || TryEat(creature, creatures, world, cx + front, cy)  // 正前方（朝向侧）
                || TryEat(creature, creatures, world, cx, cy + 1)   // 正上方
                || TryEat(creature, creatures, world, cx - 1, cy)   // 左
                || TryEat(creature, creatures, world, cx + 1, cy))  // 右
            {
                return;
            }

            // 附近没有可食植物（草 / 花）：回 SeekFood 重新扫描（清目标，下一思考帧由 ThinkSeekFood 重扫）。
            creature.AiState = StateSeekFood;
            creature.FoodTargetX = Creature.NoFoodTarget;
            creature.FoodTargetY = Creature.NoFoodTarget;
        }

        /// <summary>若 <c>(gx,gy)</c> 是可食植物（草 / 花，见 <see cref="IsEdibleType"/>）则清除一格、
        /// 恢复饥饿、置冷却、回 Wander，并返回 true。</summary>
        private static bool TryEat(Creature creature, CreatureWorld creatures, ParticleWorld world, int gx, int gy)
        {
            if (!world.IsInBounds(gx, gy) || !IsEdibleType(world.GetCell(gx, gy).typeId))
            {
                return false;
            }

            world.Clear(gx, gy);
            creature.HungerTicks -= FoodRestoreTicks;
            if (creature.HungerTicks < 0)
            {
                creature.HungerTicks = 0;
            }

            creature.FeedingCooldownTicks = FeedingCooldown;
            creature.FoodTargetX = Creature.NoFoodTarget;
            creature.FoodTargetY = Creature.NoFoodTarget;
            creature.FoodTargetFailTicks = 0;
            EnterWander(creature, creatures);
            return true;
        }

        /// <summary>Idle 到期后的转移：小概率（1/4）原地再发呆一段，否则起身漫步。</summary>
        private static void EnterNextAfterIdle(Creature creature, CreatureWorld creatures)
        {
            if (creatures.NextInt(4) == 0)
            {
                EnterIdle(creature, creatures);
            }
            else
            {
                EnterWander(creature, creatures);
            }
        }

        /// <summary>进入 Idle：停住，持续 20 + [0, 60] 帧（1~4 秒）。</summary>
        private static void EnterIdle(Creature creature, CreatureWorld creatures)
        {
            creature.AiState = StateIdle;
            creature.IntentVx = 0f;
            creature.StateTicksLeft = 20 + creatures.NextInt(61);
        }

        /// <summary>进入 Wander：等概率选向，持续 20 + [0, 40] 帧（1~3 秒）。</summary>
        private static void EnterWander(Creature creature, CreatureWorld creatures)
        {
            int dir = creatures.NextInt(2) == 0 ? -1 : 1;
            creature.AiState = StateWander;
            creature.IntentVx = dir * WalkSpeed;
            creature.StateTicksLeft = 20 + creatures.NextInt(41);
        }

        /// <summary>向下取整（不走 Mathf，保持决策路径零 UnityEngine 依赖）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        /// <summary>整数绝对值（不走 Math，保持决策路径零 UnityEngine 依赖）。</summary>
        private static int Abs(int value)
        {
            return value < 0 ? -value : value;
        }
    }
}
