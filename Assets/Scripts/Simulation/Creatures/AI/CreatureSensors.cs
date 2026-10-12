namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物的只读感知探针集合：把「看一眼周围」收敛成若干纯函数，全部对世界只读、零分配、不消耗随机流。
    /// </summary>
    /// <remarks>
    /// <para><b>地形口径统一：</b>一律走 <see cref="CreatureTerrain"/> 的 <c>IsSolid</c> / <c>IsWater</c>，
    /// 不新造判定——「动物撞不撞得上」只有一处定义。</para>
    /// <para><b>确定性：</b>扫描顺序固定（y 升序、x 升序）——「先见者」以及「最近者平手时取谁」都由它决定，
    /// 结果与调用次数无关；配合「RNG 只在思考帧消耗」保证同 seed 逐位复现。</para>
    /// <para><b>成本：</b>威胁扫描的格读随**调用方传入的**半径增长（逃命进入 = <see cref="Creature.ThreatRadius"/>：
    /// 设计值 6 = 113 格；调试期临时放大到 16 = 797 格；脱离再加 <see cref="RabbitBrain.FleeExitMargin"/>），
    /// 食物扫描 113 格读——两者仅 2 Hz 的思考帧发生；
    /// 地面探针 2 格读（每帧，仅贴地时）；墙探针 ≤3 格读（仅**上一步物理的横向积分被挡住**且有前向意图时——
    /// 触发条件来自物理事实而不是速度代理量，见 <see cref="Creature.ApplyLocomotion"/> 第 ④ 段）。</para>
    /// </remarks>
    public static class CreatureSensors
    {
        /// <summary>
        /// 以 (<paramref name="cx"/>, <paramref name="cy"/>) 为中心扫半径 <paramref name="radius"/> 的圆盘找**最近**的火，
        /// 找到则输出其格坐标。
        /// </summary>
        /// <remarks>
        /// <para><b>最近优先：</b>比较**距离平方**取最小，平手时保留先扫描者（扫描顺序固定为 y 升序、同行 x 升序，
        /// 因此平手结果也确定）。取最近而不是取先见者是逃命方向的正确性要求：固定扫描序总先命中视野里
        /// **最低一行**的火，散开的火场因此会把动物推向更远的那团火。</para>
        /// <para><b>半径由调用方传入：</b>逃命用**同一条探针、两个半径**——进入用 <see cref="Creature.ThreatRadius"/>、
        /// 脱离用 <see cref="Creature.ThreatRadius"/> + <see cref="RabbitBrain.FleeExitMargin"/>（滞回）；
        /// 探针自取半径的话滞回只能在调用方用两次不同扫描拼出来。</para>
        /// <para>越界格跳过。本函数对世界只读、零分配、不消耗随机流——「先判后摇」由调用方（Brain）负责。</para>
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="cx">扫描中心格 x。</param>
        /// <param name="cy">扫描中心格 y。</param>
        /// <param name="radius">扫描半径（格，圆盘判据 <c>dx² + dy² ≤ radius²</c>）。</param>
        /// <param name="fireX">最近的火格 x（未找到时为 0）。</param>
        /// <param name="fireY">最近的火格 y（未找到时为 0）。</param>
        /// <returns>是否在圆盘内找到火。</returns>
        public static bool FindNearestFire(ParticleWorld world, int cx, int cy, int radius, out int fireX, out int fireY)
        {
            fireX = 0;
            fireY = 0;
            bool found = false;
            int bestDistSq = int.MaxValue;
            int radiusSq = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = cy + dy;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int distSq = dx * dx + dy * dy;
                    if (distSq > radiusSq)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    if (!world.IsInBounds(x, y) || world.GetCell(x, y).typeId != ParticleIds.Fire)
                    {
                        continue;
                    }

                    // 严格小于 ⇒ 平手保留先扫描者（固定扫描序因此决定结果）。
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        fireX = x;
                        fireY = y;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// 以 (<paramref name="cx"/>, <paramref name="cy"/>) 为中心扫半径 6 的圆盘找最近的 <see cref="ParticleIds.Grass"/>，
        /// 找到则输出其格坐标。
        /// </summary>
        /// <remarks>
        /// 扫描顺序固定为 y 升序、同行 x 升序；比较**距离平方**取最小，平手时保留先扫描者。
        /// 越界格跳过。本函数对世界只读、零分配、不消耗随机流——「先判后摇」由调用方（Brain）负责。
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="cx">扫描中心格 x。</param>
        /// <param name="cy">扫描中心格 y。</param>
        /// <param name="grassX">最近的草格 x（未找到时为 0）。</param>
        /// <param name="grassY">最近的草格 y（未找到时为 0）。</param>
        /// <returns>是否在半径 6 圆盘内找到草。</returns>
        public static bool FindNearestGrass(ParticleWorld world, int cx, int cy, out int grassX, out int grassY)
        {
            grassX = 0;
            grassY = 0;
            bool found = false;
            int bestDistSq = int.MaxValue;
            const int radius = 6;
            int radiusSq = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = cy + dy;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int distSq = dx * dx + dy * dy;
                    if (distSq > radiusSq)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    if (!world.IsInBounds(x, y) || world.GetCell(x, y).typeId != ParticleIds.Grass)
                    {
                        continue;
                    }

                    // 严格小于 ⇒ 平手保留先扫描者（固定扫描序因此决定结果）。
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        grassX = x;
                        grassY = y;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>探前方一格的脚下 2 格内是否有固体支撑（悬崖守卫用）：返回 false 即前方是悬空 / 深坑。</summary>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="x">脚底中点 x。</param>
        /// <param name="y">脚底中点 y。</param>
        /// <param name="facing">前进方向（+1 右 / −1 左）。</param>
        /// <returns>前方列脚下 2 格（含）内有任一固体即 true。</returns>
        public static bool ProbeGroundAhead(ParticleWorld world, float x, float y, int facing)
        {
            int aheadColumn = FloorToInt(x) + (facing >= 0 ? 1 : -1);
            int row = FloorToInt(y) - 1;
            if (CreatureTerrain.IsSolid(world, aheadColumn, row))
            {
                return true;
            }

            return CreatureTerrain.IsSolid(world, aheadColumn, row - 1);
        }

        /// <summary>前方一格的阻挡形态（<see cref="ProbeWallAhead"/> 的三态返回）。</summary>
        public enum WallAhead
        {
            /// <summary>前方格不是固体：开阔地 / 可通行空间——调用方**什么都不该做**。</summary>
            None,

            /// <summary>前方格是固体，但越过身体高度后其上两格都非固体：一格高、可跳上去的墙。</summary>
            Climbable,

            /// <summary>前方格是固体且不可翻越（≥两格高 / 实心体）——调用方据此掉头。</summary>
            Blocked,
        }

        /// <summary>
        /// 探前方一格拦着什么：<see cref="WallAhead.None"/> = 前方格非固体（开阔地）；
        /// <see cref="WallAhead.Climbable"/> = 前方格是固体、且越过身体高度后其上两格都是非固体（一格高可翻越）；
        /// <see cref="WallAhead.Blocked"/> = 前方格是固体但不可翻越（≥两格高 / 实心体）。
        /// </summary>
        /// <remarks>
        /// 「没有墙」与「墙太高」是**两种不同的事实**：布尔探针把两者合并成一个 false，调用方就只能对
        /// 「确实被挡住」一律执行同一个动作（掉头）。三态返回让调用方对 <see cref="WallAhead.None"/> 做
        /// **真正的空操作**，见 <see cref="Creature.ApplyLocomotion"/> 第 ④ 段。
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="x">脚底中点 x。</param>
        /// <param name="y">脚底中点 y。</param>
        /// <param name="height">碰撞盒高度（格），用于确定身体顶行。</param>
        /// <param name="facing">前进方向（+1 右 / −1 左）。</param>
        /// <returns>前方阻挡的成因；越界格一律视为非固体（因此越界只会得到 <see cref="WallAhead.None"/>）。</returns>
        public static WallAhead ProbeWallAhead(ParticleWorld world, float x, float y, float height, int facing)
        {
            int aheadColumn = FloorToInt(x) + (facing >= 0 ? 1 : -1);
            int bodyRow = FloorToInt(y);
            if (!CreatureTerrain.IsSolid(world, aheadColumn, bodyRow))
            {
                return WallAhead.None;
            }

            int topRow = FloorToInt(y + height - 1e-4f);
            bool climbable = !CreatureTerrain.IsSolid(world, aheadColumn, topRow + 1)
                && !CreatureTerrain.IsSolid(world, aheadColumn, topRow + 2);
            return climbable ? WallAhead.Climbable : WallAhead.Blocked;
        }

        /// <summary>向下取整（不走 Mathf，保持感知路径零 UnityEngine 依赖）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }
    }
}
