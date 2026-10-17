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

        /// <summary>本世界的 xorshift32 状态。</summary>
        private uint _rng;

        /// <summary>已推进的逻辑帧数。</summary>
        private int _logicTicks;

        /// <summary>累计生成序号：用于把 <see cref="Creature.SensePhase"/> 钉成 <c>序号 % ThinkEvery</c>，
        /// 使同批个体的思考帧错开。只增不减——移除个体不会让后来者复用旧相位（相位一旦钉死就终身不变）。</summary>
        private int _spawnCounter;

        /// <summary>下一个渲染标识符（<see cref="Creature.Id"/>）：同一世界内单调递增，只增不减。
        /// 从 1 起：0 是「未赋值」哨兵（<c>CreatureRenderData</c> 的默认 <c>Id</c>），真实实体绝不占用。</summary>
        private int _nextId = 1;

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

            // 思考相位按生成序号钉死：同批个体因此错峰（最坏同相位 = 同批 30 只同帧思考，仍远低于一个世界 pass）。
            creature.SensePhase = (byte)(_spawnCounter++ % Creature.ThinkEvery);

            // 渲染标识符：渲染层用它（而不是对象引用）对账视图，帧里传值拷贝。
            creature.Id = _nextId++;

            _creatures.Add(creature);
            creature.OnSpawn(this, world);
            CreatureSpawned?.Invoke(creature);
            return creature;
        }

        /// <summary>
        /// 推进一个逻辑帧。三个阶段：① 全体快照 <c>Prev = Cur</c>；② 按生成顺序逐只
        /// <see cref="Creature.OnLogic"/> → <see cref="Creature.ApplyLocomotion"/> → <see cref="Creature.StepPhysics"/> → <c>AgeTicks++</c>；
        /// ③ 倒序清扫 <c>!Alive</c> 的实体，先由 <see cref="CreatureDeathMaterializer"/> 写入生物质，
        /// 再调 <see cref="Creature.OnDeath"/>、发 <see cref="CreatureDied"/>，最后从列表移除。
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

            _logicTicks++;
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
