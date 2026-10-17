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
    /// 食物扫描 113 格读（兔子找草 / 花、鱼找水草）、生物质扫描随调用方半径（鸟为
    /// <see cref="BirdBrain.FoodDetectCells"/> = 24，圆盘 1809 格）——都仅 2 Hz 的思考帧发生；
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
        /// 以 (<paramref name="cx"/>, <paramref name="cy"/>) 为中心扫半径 6 的圆盘找最近的**可食植物**
        /// （<see cref="RabbitBrain.IsEdibleType"/>：草 / 花），找到则输出其格坐标。
        /// </summary>
        /// <remarks>
        /// <para>扫描顺序固定为 y 升序、同行 x 升序；比较**距离平方**取最小，平手时保留先扫描者。
        /// 越界格跳过。本函数对世界只读、零分配、不消耗随机流——「先判后摇」由调用方（Brain）负责。</para>
        /// <para><b>食谱单一来源：</b>成员判定一律走 <see cref="RabbitBrain.IsEdibleType"/>，本函数不另写
        /// 粒子类型比较——感知与进食因此永远看到同一份可食集合。</para>
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="cx">扫描中心格 x。</param>
        /// <param name="cy">扫描中心格 y。</param>
        /// <param name="foodX">最近的可食格 x（未找到时为 0）。</param>
        /// <param name="foodY">最近的可食格 y（未找到时为 0）。</param>
        /// <returns>是否在半径 6 圆盘内找到可食植物（草 / 花）。</returns>
        public static bool FindNearestGrassOrFlower(ParticleWorld world, int cx, int cy, out int foodX, out int foodY)
        {
            foodX = 0;
            foodY = 0;
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
                    if (!world.IsInBounds(x, y) || !RabbitBrain.IsEdibleType(world.GetCell(x, y).typeId))
                    {
                        continue;
                    }

                    // 严格小于 ⇒ 平手保留先扫描者（固定扫描序因此决定结果）。
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        foodX = x;
                        foodY = y;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// 以 (<paramref name="cx"/>, <paramref name="cy"/>) 为中心扫半径 <paramref name="radius"/> 的圆盘找**最近**的
        /// <see cref="ParticleIds.Biomass"/>，找到则输出其格坐标。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="FindNearestGrassOrFlower"/> **完全同口径**（只有半径来源不同）：扫描顺序固定为 y 升序、同行 x 升序；
        /// 比较**距离平方**取最小，平手时保留先扫描者。越界格跳过。本函数对世界只读、零分配、不消耗随机流——
        /// 「先判后摇」由调用方（Brain）负责。
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="cx">扫描中心格 x。</param>
        /// <param name="cy">扫描中心格 y。</param>
        /// <param name="radius">扫描半径（格，圆盘判据 <c>dx² + dy² ≤ radius²</c>）。</param>
        /// <param name="biomassX">最近的生物质格 x（未找到时为 0）。</param>
        /// <param name="biomassY">最近的生物质格 y（未找到时为 0）。</param>
        /// <returns>是否在圆盘内找到生物质。</returns>
        public static bool FindNearestBiomass(
            ParticleWorld world, int cx, int cy, int radius, out int biomassX, out int biomassY)
        {
            biomassX = 0;
            biomassY = 0;
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
                    if (!world.IsInBounds(x, y) || world.GetCell(x, y).typeId != ParticleIds.Biomass)
                    {
                        continue;
                    }

                    // 严格小于 ⇒ 平手保留先扫描者（固定扫描序因此决定结果）。
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        biomassX = x;
                        biomassY = y;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// 以 (<paramref name="cx"/>, <paramref name="cy"/>) 为中心扫半径 <paramref name="radius"/> 的圆盘，
        /// 找**最近的、仍泡在水里的** <see cref="ParticleIds.AquaticPlant"/>，找到则输出其格坐标。
        /// </summary>
        /// <remarks>
        /// <para><b>为什么要求「八邻有水」：</b>水草是固体格（本格不是水），「还在水下」只能从邻居判断——
        /// 八邻至少一格是水即视为可吃。这一条把被排干后仍立着的枯草挡在目标之外，避免鱼为了追一株
        /// 露出水面的草而顶向水面搁浅。</para>
        /// <para><b>取最近而不是取先见者：</b>与 <see cref="FindNearestFire"/> 同一口径——固定扫描序
        /// （y 升序、同行 x 升序）决定平手结果，严格小于才替换。水草是一整列连续格，取最近会让鱼选到
        /// 与其身体同高的那一格（而不是整列的顶端或底部），追击距离因此最短。</para>
        /// <para>越界格跳过。本函数对世界只读、零分配、不消耗随机流。</para>
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="cx">扫描中心格 x。</param>
        /// <param name="cy">扫描中心格 y。</param>
        /// <param name="radius">扫描半径（格，圆盘判据 <c>dx² + dy² ≤ radius²</c>）。</param>
        /// <param name="plantX">最近的水草格 x（未找到时为 0）。</param>
        /// <param name="plantY">最近的水草格 y（未找到时为 0）。</param>
        /// <returns>是否在圆盘内找到水下的水草。</returns>
        public static bool FindNearestAquaticPlant(
            ParticleWorld world, int cx, int cy, int radius, out int plantX, out int plantY)
        {
            plantX = 0;
            plantY = 0;
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
                    if (!world.IsInBounds(x, y)
                        || world.GetCell(x, y).typeId != ParticleIds.AquaticPlant
                        || !HasWaterNeighbour(world, x, y))
                    {
                        continue;
                    }

                    // 严格小于 ⇒ 平手保留先扫描者（固定扫描序因此决定结果）。
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        plantX = x;
                        plantY = y;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>(x,y) 的八邻里是否有水（水草是否仍泡在水里的判据）。越界由
        /// <see cref="CreatureTerrain.IsWater"/> 天然回答 false。</summary>
        private static bool HasWaterNeighbour(ParticleWorld world, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (CreatureTerrain.IsWater(world, x + dx, y + dy))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 自 <paramref name="startRow"/> 起**固定序向下**（y 逐格递减）扫描至多 <paramref name="maxCells"/> 格，
        /// 返回正下方第一个「**可栖面**（<see cref="CreatureTerrain.IsPerchable"/>：固体 ∪ 树叶 / 树干）或水」的行号；
        /// 扫到世界底或超出上限则视为**到底**（<paramref name="surfaceRow"/> = <see cref="int.MinValue"/>、返回 false）。
        /// 命中时顺带判定它是不是**合格栖息面**：命中格可栖且其正上方一格不是地形固体（上方敞开、不是实心体内部）。
        /// 同时给出**降落口径** <paramref name="foliage"/>：命中格是树叶 / 树干且上方一格非固体（只有树才能落）。
        /// </summary>
        /// <remarks>
        /// <para><b>为什么扫描停条件含树叶 / 树干（而不仅是固体 + 水）</b>：本探针同时服务两个调用方——
        /// 飞行高度带（探「正下方第一个落脚 / 落水面」）与降落（探「正下方可落点」）；只认固体 + 水的话，
        /// 鸟永远看不到树冠、无法降落在叶顶（设计 §5 明确要求「下方有树冠 ⇒ 停在叶顶」）。
        /// 因此停条件取「可栖面 ∪ 水」，<paramref name="perchable"/> 再把它细分为「能站的顶面」与「水面 / 实心体内部」。</para>
        /// <para><b><paramref name="perchable"/> 与 <paramref name="foliage"/> 的分工（<c>docs/plan-bird-ai.md</c> §17）</b>：
        /// <paramref name="perchable"/> 仍是**高度带基准**（含地面与水——带宽要在地面上也有处安放，绝不在此收窄）；
        /// <paramref name="foliage"/> 才是**降落口径**（只认树：树叶 / 树干，树干顶也算）。地面可站但不算树，
        /// 所以鸟「能基准到地面」不等于「能落到地面」。</para>
        /// <para>越界（列出界 / 行落到 0 以下）一律视为到底。本函数对世界只读、零分配、不消耗随机流。</para>
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="x">扫描列。</param>
        /// <param name="startRow">起始行（一般传脚底下一行 <c>floor(Y) − 1</c>）。</param>
        /// <param name="maxCells">最多向下扫描的格数（&le; 0 直接返回 false）。</param>
        /// <param name="surfaceRow">命中的落脚 / 落水面行（未命中时 <see cref="int.MinValue"/>）。</param>
        /// <param name="perchable">命中格是否是合格栖息面（可栖且上方一格非固体；未命中时 false）。</param>
        /// <param name="foliage">命中格是否是**可降落的树**（树叶 / 树干且上方一格非固体；未命中时 false）。</param>
        /// <returns>是否在扫描范围内命中可栖面或水。</returns>
        public static bool ProbeSurfaceBelow(
            ParticleWorld world, int x, int startRow, int maxCells, out int surfaceRow, out bool perchable,
            out bool foliage)
        {
            surfaceRow = int.MinValue;
            perchable = false;
            foliage = false;
            if (world == null || maxCells <= 0 || !world.IsInBounds(x, startRow))
            {
                return false;
            }

            int lastRow = startRow - maxCells + 1;
            for (int y = startRow; y >= lastRow; y--)
            {
                if (y < 0)
                {
                    // 扫到世界底：视为到底（脚下已无更低的支撑 / 水面）。
                    return false;
                }

                if (!CreatureTerrain.IsPerchable(world, x, y) && !CreatureTerrain.IsWater(world, x, y))
                {
                    continue;
                }

                surfaceRow = y;
                bool openAbove = !CreatureTerrain.IsSolid(world, x, y + 1);
                perchable = CreatureTerrain.IsPerchable(world, x, y) && openAbove;
                foliage = CreatureTerrain.IsFoliage(world, x, y) && openAbove;
                return true;
            }

            return false;
        }

        /// <summary>自 <paramref name="firstRow"/> 向上扫到第一个非水格（越界算非水）⇒ 水面行
        /// （体顶之上第一个非水行）。最多扫 <c>world.Height</c> 格：扫到世界顶就是越界、
        /// <see cref="CreatureTerrain.IsWater"/> 必返回 false，因此必定终止（零分配，只有局部 int）。</summary>
        /// <remarks>
        /// 这是鱼（<see cref="FishBrain"/> 的深度带 / 触面判定）与繁殖（<see cref="CreatureReproduction"/>
        /// 的“完全没入 + 水柱深度”判定）共用的**唯一**水面探针——两侧口径必须一致，
        /// 否则“鱼觉得自己的深度”和“繁殖觉得鱼的水深”会各说各话。
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="x">扫描列。</param>
        /// <param name="firstRow">起算行（体顶之上第一格）。</param>
        /// <returns>体顶之上第一个非水行（行号；越界 / 全水时停在 <c>firstRow + world.Height</c>）。</returns>
        public static int ProbeSurfaceRowAbove(ParticleWorld world, int x, int firstRow)
        {
            int lastRow = firstRow + world.Height;
            int y = firstRow;
            while (y < lastRow && CreatureTerrain.IsWater(world, x, y))
            {
                y++;
            }

            return y;
        }

        /// <summary>探前方一格的脚下 2 格内是否有固体支撑（悬崖守卫用）：返回 false 即前方是悬空 / 深坑。</summary>
        /// <remarks>
        /// <para><b>世界底 = 地板：</b>脚下那一行（<c>floor(y) − 1</c>）越出世界底（<c>row &lt; 0</c>）时返回 true——
        /// 物理层本就把世界底当地板：<see cref="Creature.StepPhysics"/> 把 <c>Y</c> 夹到 0，且 <c>Y ≤ 0 ⇒ IsGrounded</c>。
        /// 「世界底之下」不存在任何可以掉下去的悬崖，探针的口径必须与物理层一致；否则贴世界底行走时两行全部越界 ⇒
        /// <see cref="CreatureTerrain.IsSolid"/> 一律答「非固体」⇒ 悬崖守卫**每逻辑帧**反转 <c>IntentVx</c>、
        /// <c>Facing</c> 随之每帧翻面（20 Hz 级别的左右抽搐，回归见
        /// <c>CreatureAiTests.CliffGuard_AtWorldFloor_DoesNotFlipFacing</c>）。</para>
        /// <para><b>左右越界不在此列：</b>前方**列**越界仍按非固体处理——世界侧壁的掉头由
        /// <see cref="Creature.ApplyLocomotion"/> 第 ④ 段（<c>_blockedXAtWorldEdge</c> 边界夹取）负责，不经本探针。</para>
        /// </remarks>
        /// <param name="world">被查询的世界（只读）。</param>
        /// <param name="x">脚底中点 x。</param>
        /// <param name="y">脚底中点 y。</param>
        /// <param name="facing">前进方向（+1 右 / −1 左）。</param>
        /// <returns>前方列脚下 2 格（含）内有任一固体即 true；脚下那一行已越出世界底时也返回 true（世界底即地板）。</returns>
        public static bool ProbeGroundAhead(ParticleWorld world, float x, float y, int facing)
        {
            int aheadColumn = FloorToInt(x) + (facing >= 0 ? 1 : -1);
            int row = FloorToInt(y) - 1;
            if (row < 0)
            {
                // 世界底之下不存在可坠落的悬崖：与 StepPhysics「Y ≤ 0 ⇒ IsGrounded」的口径保持一致。
                return true;
            }

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
