using System;
using System.Collections.Generic;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物世界的唯一持有者：一个按生成顺序排列的 <see cref="List{T}"/> + 自己的一条 xorshift32 随机流。
    /// 与 <see cref="ParticleWorld"/> **并列**、同线程、每逻辑帧（20 Hz）推进一次，互不写对方的状态。
    /// </summary>
    /// <remarks>
    /// <para><b>迭代顺序 = 生成顺序：</b>列表只追加、只按倒序移除，因此同一 seed + 同一串操作
    /// 下每一步的结果完全确定（粒子层与动物层都要求可复现）。</para>
    /// <para><b>独立的随机流：</b>动物用一个自己的 xorshift32（算法与
    /// <see cref="ParticleWorld.NextUInt"/> 逐字一致），**绝不消耗世界的随机流**。
    /// 这样动物的存在与否不会扰动粒子模拟的任何一次摇骰——两层的确定性互相隔离，
    /// 加一只兔子不会让整片森林长歪。</para>
    /// <para><b>对世界近乎只读：</b>逐帧推进阶段只查询 <see cref="ParticleWorld"/>，不写任何格；
    /// 唯一的写发生在死亡清扫阶段——由 <see cref="CreatureDeathMaterializer"/> 按轮廓写入生物质，
    /// 且只走 <see cref="ParticleWorld.SetWithShade"/>（未来的打洞 / 吃草也必须走 Set / Clear / SetPhase）。</para>
    /// <para><b>为什么用列表而不是数组池：</b>个体数量与粒子数不在一个量级（个位数到几十），
    /// 每帧 O(n) 遍历的成本可忽略，换取的是「生成 / 移除对渲染层与测试都直观」。</para>
    /// </remarks>
    public sealed class CreatureWorld
    {
        /// <summary>seed == 0 时的替换值（黄金比例常数，避免全零种子退化成恒零序列）。</summary>
        private const uint FallbackSeed = 0x9E3779B9u;

        /// <summary>实体表：下标即生成顺序，也是每帧的处理顺序。</summary>
        private readonly List<Creature> _creatures = new List<Creature>();

        /// <summary>出生队列（母, 父）：Brain 在思考帧入队，<see cref="Step"/> 在死亡清扫**之后**统一应用。
        /// 为什么不在 ② 遍历里直接 <see cref="Spawn{T}"/>：② 的上界是动态的 <c>_creatures.Count</c>，
        /// 边遍历边追加会让新生儿**同帧**被推进（年龄 / 物理 / 甚至当天再繁殖）；入队延后到③之后，
        /// 新生儿从下一帧才开始活动，且“这一帧的出生”与“这一帧的死亡”互不穿插。</summary>
        private readonly List<(Creature mother, Creature father)> _pendingBirths =
            new List<(Creature mother, Creature father)>();

        /// <summary>本世界的 xorshift32 状态。</summary>
        private uint _rng;

        /// <summary>已推进的逻辑帧数。</summary>
        private int _logicTicks;

        /// <summary>累计生成序号：用于把 <see cref="Creature.SensePhase"/> 钉成 <c>序号 % ThinkEvery</c>，
        /// 使同批个体的思考帧错开。只增不减——移除个体不会让后来者复用旧相位（相位一旦钉死就终身不变）。</summary>
        private int _spawnCounter;

        /// <summary>用 seed 建立动物世界（与世界随机流分离：驱动层传 <c>settings.Seed ^ 0x9E3779B9u</c>）。</summary>
        /// <param name="seed">随机种子；为 0 时替换为 <c>0x9E3779B9</c>。</param>
        public CreatureWorld(uint seed)
        {
            _rng = seed == 0u ? FallbackSeed : seed;
        }

        /// <summary>当前实体数量。</summary>
        public int Count
        {
            get { return _creatures.Count; }
        }

        /// <summary>统计指定物种的实体数（<c>for</c> + <c>is T</c>，零分配）。
        /// 繁殖的软上限 / 下限与概率因子都读它；死亡个体在清扫前仍留在表里，因此思考帧看到的
        /// 是“本帧开始时的种群”，与出生应用阶段（清扫之后）各复核一次。</summary>
        /// <remarks>名字不叫 <c>Count&lt;T&gt;</c>：C# 不允许属性与方法同名（<c>Count</c> 属性已存在），
        /// 故用 <c>CountOf&lt;T&gt;</c>，语义不变。</remarks>
        /// <typeparam name="T">具体物种类型。</typeparam>
        /// <returns>表内属于 <typeparamref name="T"/> 的实体数。</returns>
        public int CountOf<T>() where T : Creature
        {
            int count = 0;
            for (int i = 0; i < _creatures.Count; i++)
            {
                if (_creatures[i] is T)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>已推进的逻辑帧数（渲染层用它检测「刚跨过一个逻辑帧」以重置插值计时）。</summary>
        public int LogicTicks
        {
            get { return _logicTicks; }
        }

        /// <summary>实体表，顺序 = 生成顺序 = 迭代顺序（确定性）。只读视图，调用方不得改写。</summary>
        public IReadOnlyList<Creature> Creatures
        {
            get { return _creatures; }
        }

        /// <summary>新实体生成并入世界后触发（参数为实体）。</summary>
        public event Action<Creature> CreatureSpawned;

        /// <summary>实体死亡被清扫出世界后触发（参数为实体与死因）。</summary>
        public event Action<Creature, CreatureDeathCause> CreatureDied;

        /// <summary>
        /// 把实体夹进世界边界后放置、加入世界。置 <c>Prev = Cur</c>、<c>Alive = true</c>、
        /// 调 <see cref="Creature.OnSpawn"/>，然后发 <see cref="CreatureSpawned"/>。
        /// </summary>
        /// <remarks>
        /// 夹取范围与物理阶段一致：<c>X ∈ [Width/2, world.Width − Width/2]</c>、
        /// <c>Y ∈ [0, world.Height − Height]</c>；世界比实体还小时退化为居中 / 贴地。
        /// 生成即入表，因此**同一逻辑帧内**后续的 <see cref="Step"/> 就会推进它。
        /// </remarks>
        /// <typeparam name="T">具体物种类型（原样返回，省去调用方转型）。</typeparam>
        /// <param name="creature">待放置的实体。</param>
        /// <param name="world">粒子世界（用于取边界）。</param>
        /// <param name="x">期望位置 x（格）。</param>
        /// <param name="y">期望位置 y（格）。</param>
        /// <returns>传入的同一个实体。</returns>
        public T Spawn<T>(T creature, ParticleWorld world, float x, float y) where T : Creature
        {
            if (creature == null)
            {
                throw new ArgumentNullException(nameof(creature));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            float halfWidth = creature.Width * 0.5f;
            float minX = halfWidth;
            float maxX = world.Width - halfWidth;
            if (maxX < minX)
            {
                // 世界窄于实体：退化为中心对齐。
                minX = world.Width * 0.5f;
                maxX = minX;
            }

            float maxY = world.Height - creature.Height;
            if (maxY < 0f)
            {
                maxY = 0f;
            }

            creature.X = Clamp(x, minX, maxX);
            creature.Y = Clamp(y, 0f, maxY);
            creature.PrevX = creature.X;
            creature.PrevY = creature.Y;
            creature.Alive = true;

            // 性别与思考相位共用**同一个**生成序号：先按奇偶写 Sex，再用同一值取相位，最后才自增。
            // 顺序即契约——若先自增再取 Sex / 相位，两者会错开一位；而分开用两个计数器则无法保证
            // “序号 0 既是最早生成者、也是第一批思考相位 0”。性别不摇骰 ⇒ 初始种群天然 50/50。
            int spawnIndex = _spawnCounter;
            creature.Sex = (byte)(spawnIndex & 1);
            creature.SensePhase = (byte)(spawnIndex % Creature.ThinkEvery);
            _spawnCounter = spawnIndex + 1;

            _creatures.Add(creature);
            creature.OnSpawn(this, world);
            CreatureSpawned?.Invoke(creature);
            return creature;
        }

        /// <summary>登记一次出生请求（母亲、父亲），**不做任何校验**——是否还能生（母亲已死 / 物种到上限）
        /// 留到 <see cref="Step"/> 的应用阶段复核，避免在遍历中途改表，也让“同一帧里的多次请求”
        /// 严格按入队顺序结算。仅应在 Brain 的思考帧调用（概率与冷却由 <see cref="CreatureReproduction"/> 负责）。</summary>
        /// <param name="mother">母亲（新生儿位置取她的位置）。</param>
        /// <param name="father">父亲（本阶段只用于语义记录）。</param>
        public void RequestBirth(Creature mother, Creature father)
        {
            _pendingBirths.Add((mother, father));
        }

        /// <summary>
        /// 推进一个逻辑帧。四个阶段：① 全体快照 <c>Prev = Cur</c>；② 按生成顺序逐只
        /// <see cref="Creature.OnLogic"/> → <see cref="Creature.ApplyLocomotion"/> → <see cref="Creature.StepPhysics"/>
        /// → <c>AgeTicks++</c> → 繁殖冷却递减；
        /// ③ 倒序清扫 <c>!Alive</c> 的实体，先由 <see cref="CreatureDeathMaterializer"/> 写入生物质，
        /// 再调 <see cref="Creature.OnDeath"/>、发 <see cref="CreatureDied"/>，最后从列表移除；
        /// ③b 按入队顺序应用出生请求（母亲存活 + 物种未到上限才生成）。
        /// </summary>
        /// <remarks>
        /// <para>驱动层在 <c>ParticleWorld.StepLogic()</c> **之后**调用本方法，保证动物看到的是本帧
        /// 已经结算完毕的地形。</para>
        /// <para>快照必须在全体运动之前一次性做完：若边快照边推进，排在后面的实体会把「前面实体
        /// 本帧的位移」也算进自己的插值起点。</para>
        /// <para>②里被同帧前面的实体 <see cref="Creature.Kill"/> 掉的个体**跳过推进、也不计年龄**：
        /// 尸体不该继续跑物理，清扫阶段照常发它的死亡事件。</para>
        /// <para>③倒序遍历 → 移除下标 i 不影响尚未访问的更小下标；事件在移除**之前**发，
        /// 便于监听者读到最后的位置 / 状态。</para>
        /// <para>③ 的死亡物质化只走显式 shade 的 <see cref="ParticleWorld.SetWithShade"/>（不抽样颜色），
        /// 因此**不消耗世界的随机流**；除这一处外，本方法对 <paramref name="world"/> 只读。</para>
        /// <para>③b 放在死亡清扫之后：出生请求在②的思考帧入队，若当场生成会让新生儿**同帧**被②推进；
        /// 延后到这一步，新生儿年龄从 0 开始、下一帧才动，且死亡先结算 ⇒ 上限看的是本帧末的真实种群。</para>
        /// </remarks>
        /// <param name="world">粒子世界（推进阶段只读；死亡清扫阶段写入生物质）。</param>
        public void Step(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            // ① 快照：Prev = Cur。
            for (int i = 0; i < _creatures.Count; i++)
            {
                Creature c = _creatures[i];
                c.PrevX = c.X;
                c.PrevY = c.Y;
            }

            // ② 逐只推进（顺序 = 生成顺序）。
            for (int i = 0; i < _creatures.Count; i++)
            {
                Creature c = _creatures[i];
                if (!c.Alive)
                {
                    continue;
                }

                c.OnLogic(this, world);
                c.ApplyLocomotion(world);
                c.StepPhysics(world);
                c.AgeTicks++;
                if (c.BreedCooldownTicks > 0)
                {
                    c.BreedCooldownTicks--;
                }
            }

            // ③ 倒序清扫死亡。
            for (int i = _creatures.Count - 1; i >= 0; i--)
            {
                Creature c = _creatures[i];
                if (c.Alive)
                {
                    continue;
                }

                // 死亡物质化：按物种轮廓写入生物质（**必须先于** OnDeath / CreatureDied，
                // 使监听者看到的是「尸体已经物质化」的世界；顺序即契约，见设计 §16.1）。
                CreatureDeathMaterializer.Materialize(world, c);

                c.OnDeath(this, world, c.PendingDeathCause);
                CreatureDied?.Invoke(c, c.PendingDeathCause);
                _creatures.RemoveAt(i);
            }

            // ③b 应用出生队列（死亡清扫之后）：新生儿因此**本帧不被推进**（年龄 / 物理 / 繁殖都从下一帧开始），
            // 且“同帧死亡”已经先扣掉——上限判定看的是本帧结束时的真实种群。
            ApplyPendingBirths(world);

            _logicTicks++;
        }

        /// <summary>按入队顺序结算出生请求：母亲已死或该物种已到上限 ⇒ 跳过（应用时复核）；
        /// 否则在母亲位置生成同物种新生儿，并重置年龄 / 繁殖冷却 / 捕食计数。
        /// 物种分派用 <c>is</c> 手写三类——只有三个物种，反射或泛型工厂只会让调用点更难读。
        /// 上限常量集中在 <see cref="CreatureReproduction"/>（数值单一来源）。</summary>
        /// <param name="world">粒子世界（只为 <see cref="Spawn{T}"/> 取边界）。</param>
        private void ApplyPendingBirths(ParticleWorld world)
        {
            for (int i = 0; i < _pendingBirths.Count; i++)
            {
                Creature mother = _pendingBirths[i].mother;
                if (!mother.Alive)
                {
                    continue;
                }

                if (mother is RabbitCreature)
                {
                    if (CountOf<RabbitCreature>() >= CreatureReproduction.RabbitMaxPopulation)
                    {
                        continue;
                    }

                    RabbitCreature baby = Spawn(new RabbitCreature(), world, mother.X, mother.Y);
                    baby.AgeTicks = 0;
                    baby.BreedCooldownTicks = CreatureReproduction.RabbitInitialCooldownTicks;
                    baby.PreyEaten = 0;
                }
                else if (mother is FishCreature)
                {
                    if (CountOf<FishCreature>() >= CreatureReproduction.FishMaxPopulation)
                    {
                        continue;
                    }

                    FishCreature baby = Spawn(new FishCreature(), world, mother.X, mother.Y);
                    baby.AgeTicks = 0;
                    baby.BreedCooldownTicks = CreatureReproduction.FishInitialCooldownTicks;
                    baby.PreyEaten = 0;
                }
                else if (mother is BirdCreature)
                {
                    if (CountOf<BirdCreature>() >= CreatureReproduction.BirdMaxPopulation)
                    {
                        continue;
                    }

                    BirdCreature baby = Spawn(new BirdCreature(), world, mother.X, mother.Y);
                    baby.AgeTicks = 0;
                    baby.BreedCooldownTicks = CreatureReproduction.BirdInitialCooldownTicks;
                    baby.PreyEaten = 0;
                }
            }

            _pendingBirths.Clear();
        }

        /// <summary>清空全部实体（世界重建用）。**不发死亡事件**，也不重置随机流与逻辑帧计数。</summary>
        public void Clear()
        {
            _creatures.Clear();
        }

        /// <summary>确定性随机数（xorshift32），与 <see cref="ParticleWorld.NextUInt"/> 同算法。</summary>
        /// <returns>下一个 32 位随机数。</returns>
        public uint NextUInt()
        {
            uint x = _rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _rng = x;
            return x;
        }

        /// <summary>[0, maxExclusive) 内的确定性随机整数；maxExclusive &lt;= 0 时返回 0。</summary>
        /// <param name="maxExclusive">上界（不含）。</param>
        /// <returns>随机下标。</returns>
        public int NextInt(int maxExclusive)
        {
            return maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);
        }

        /// <summary>把值夹到 [min, max]（不引 Mathf / Math，保持物理层零 UnityEngine 依赖）。</summary>
        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
