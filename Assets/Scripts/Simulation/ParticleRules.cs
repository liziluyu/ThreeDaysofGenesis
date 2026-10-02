namespace TapTap2026.Simulation
{
    /// <summary>
    /// 无状态的共享移动规则。粒子类型在自己的 <c>OnUpdate</c> 里按需调用，不持有任何每格状态。
    /// 写操作型方法（<c>Try*</c>）遵循：先判断、后无条件执行、成功即返回 true（调用方应立刻 return）；
    /// 纯换算 <see cref="SlideClearance"/> 与整段固体运动 <see cref="StepSolid"/> 见各自的注释。
    /// </summary>
    public static class ParticleRules
    {
        /// <summary>空气的 rank。</summary>
        public const int AirRank = 0;

        /// <summary>气体的 rank。</summary>
        public const int GasRank = 1;

        /// <summary>液体的 rank。</summary>
        public const int LiquidRank = 2;

        /// <summary>固体的 rank。固体不得与同类互相穿行（见 <see cref="TryFlowHorizontal"/>）。</summary>
        public const int SolidRank = 3;

        /// <summary>
        /// 类别标签的轻重次序：<c>Solid(3) &gt; Liquid(2) &gt; Gas(1) &gt; 空气(0)</c>。
        /// 一个字节能带多个标签时取最高的一个。
        /// </summary>
        public static int TagRank(ParticleTags tags)
        {
            if ((tags & ParticleTags.Solid) != 0)
            {
                return SolidRank;
            }

            if ((tags & ParticleTags.Liquid) != 0)
            {
                return LiquidRank;
            }

            if ((tags & ParticleTags.Gas) != 0)
            {
                return GasRank;
            }

            return AirRank;
        }

        /// <summary>(x,y) 上的粒子是否带 <see cref="ParticleTags.Solid"/> 标签。越界与空气均为 false。</summary>
        public static bool IsSolid(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            if (id == ParticleRegistry.AirTypeId)
            {
                return false;
            }

            ParticleType[] types = world.Types;
            if (id >= types.Length)
            {
                return false;
            }

            ParticleType type = types[id];
            return type != null && (type.Info.Tags & ParticleTags.Solid) != 0;
        }

        /// <summary>
        /// <paramref name="toX"/>,<paramref name="toY"/> 能否被 <paramref name="fromX"/>,<paramref name="fromY"/> 上的粒子占据。
        /// 空气可进入；静态格不可；否则比较类别标签，高 rank 可以置换低 rank（固体沉穿液体与气体，同类互不置换）。
        /// </summary>
        public static bool CanDisplace(ParticleWorld world, int fromX, int fromY, int toX, int toY)
        {
            // 越界的目标格不能被写入。GetCell 把越界当空气，若不显式拒绝，本方法会对越界返回 true，
            // 调用方以为「能移动」，随后的 Swap/Set 却静默失败，粒子白卡一 tick。
            if (!world.IsInBounds(toX, toY))
            {
                return false;
            }

            ushort otherId = world.GetCell(toX, toY).typeId;
            if (otherId == ParticleRegistry.AirTypeId)
            {
                return true;
            }

            ushort selfId = world.GetCell(fromX, fromY).typeId;
            ParticleType[] types = world.Types;
            if (selfId >= types.Length || otherId >= types.Length)
            {
                return false;
            }

            ParticleType self = types[selfId];
            ParticleType other = types[otherId];
            if (self == null || other == null || other.Info.IsStatic)
            {
                return false;
            }

            return TagRank(self.Info.Tags) > TagRank(other.Info.Tags);
        }

        /// <summary>正下方可进入则交换，下落一格。</summary>
        public static bool TryFall(ParticleWorld world, int x, int y)
        {
            int targetY = y - 1;
            if (!world.IsInBounds(x, targetY) || !CanDisplace(world, x, y, x, targetY))
            {
                return false;
            }

            world.Swap(x, y, x, targetY);
            return true;
        }

        /// <summary>
        /// 正下方被挡住时尝试斜下方，左右顺序随机以消除方向偏置。
        /// 固体请改用 <see cref="StepSolid"/>：它把 <see cref="ParticleTypeInfo.Friction"/> 换算成这里的净空。
        /// </summary>
        /// <param name="clearance">
        /// 斜下方从落点起、连续需要能进入的格数，也就是这类材料的安息角：
        /// 1 = 落点能进入就滑（1:1，约 45°，粉末默认）；2 = 落点与它下方那格都要能进入（2:1，约 63°）；
        /// 10 = 连续 10 格净空（10:1，约 84°，石头）。再大就更陡，直到材料完全不失稳。越界格一律当作不可进入。
        /// <c>&lt;= 0</c> 表示不斜滑（摩擦度 1 的固体），直接返回 false。
        /// </param>
        public static bool TryDiagonalFall(ParticleWorld world, int x, int y, int clearance = 1)
        {
            // 净空 <= 0：材料完全不失稳，永不沿坡滚落。调用方（如 StepSolid）靠它表达「摩擦度 1」。
            if (clearance <= 0 || !world.IsInBounds(x, y - 1))
            {
                return false;
            }

            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TrySlide(world, x, y, -1, clearance) || TrySlide(world, x, y, 1, clearance)
                : TrySlide(world, x, y, 1, clearance) || TrySlide(world, x, y, -1, clearance);
        }

        /// <summary>
        /// 固体粒子的标准运动：正下可进入则下落一格；正下被挡住时按<b>摩擦度</b>决定的安息角斜滑；
        /// 都动不了时，<b>粘性</b>材料会被磨平孤立的尖顶（见 <see cref="TryBluntCrest"/>）。
        /// </summary>
        /// <remarks>
        /// 每个固体类型的 <c>OnUpdate</c> 都应当是这一行；两个参数都从注册表读，类型无需自己抄常量。
        /// 分工：摩擦度定侧翼坡度（材料自身的静摩擦，孤立一颗也生效），粘性定尖顶形状（靠邻域形态，糊状物站不住尖点）。
        /// </remarks>
        public static void StepSolid(ParticleWorld world, int x, int y)
        {
            if (TryFall(world, x, y))
            {
                return;
            }

            ParticleType self = SelfType(world, x, y);
            if (self == null)
            {
                return;
            }

            int clearance = SlideClearance(self.Info.Friction);
            if (clearance > 0 && TryDiagonalFall(world, x, y, clearance))
            {
                return;
            }

            TryBluntCrest(world, x, y, self.Info.Stickiness);
        }

        /// <summary>
        /// 尖顶不稳定：自己上方与同排两侧都是空（也就是一根 1 格宽的孤立尖点）时，
        /// 以 <paramref name="stickiness"/> 的概率向随机一侧倒下，把尖顶磨成 2~4 格宽的小平顶。
        /// </summary>
        /// <remarks>
        /// <para>这是「糊状物站不住尖点」的局部实现：<b>只磨 1 格宽的尖</b>。坡面上的粒子（同排一侧是上坡、另一侧是空）
        /// 与平台内部的粒子（同排两侧都有同伴）都不满足判据，因此侧翼完全由摩擦度决定，整座堆也不会被摊平。</para>
        /// <para>落点必须有支撑（<see cref="ParticleWorld.IsAir"/> 为 false，世界底面算支撑）才倒；
        /// 没支撑说明那是下落而不是倒塌，交给 <see cref="TryFall"/> 处理，不在这里制造悬空格。</para>
        /// <para><paramref name="stickiness"/> 为 0 的类型一次额外邻居读都不做（先判 0 再读邻域），
        /// 因此非粘性固体的行为与纯摩擦度完全一致。粘性为 0..1 的每 tick 倒塌概率，越大越快，稳态顶盖宽度基本一致。</para>
        /// </remarks>
        /// <returns>是否发生了一次倒塌（调用方应立刻 return）。</returns>
        public static bool TryBluntCrest(ParticleWorld world, int x, int y, float stickiness)
        {
            if (stickiness <= 0f || !world.IsAir(x, y + 1))
            {
                return false;
            }

            if (!world.IsAir(x - 1, y) || !world.IsAir(x + 1, y))
            {
                return false;
            }

            if (world.NextUInt() % 100u >= (uint)(stickiness * 100f))
            {
                return false;
            }

            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TryToppleSide(world, x, y, -1) || TryToppleSide(world, x, y, 1)
                : TryToppleSide(world, x, y, 1) || TryToppleSide(world, x, y, -1);
        }

        /// <summary>向 <paramref name="deltaX"/> 一侧倒下：同位侧邻必须是空、且落点下方有支撑（世界底面算支撑）。</summary>
        private static bool TryToppleSide(ParticleWorld world, int x, int y, int deltaX)
        {
            int targetX = x + deltaX;
            if (!world.IsAir(targetX, y))
            {
                return false;
            }

            // 越界下方 = 世界底面 = 支撑；否则必须是实心格，避免制造悬空粒子。
            if (world.IsInBounds(targetX, y - 1) && world.IsAir(targetX, y - 1))
            {
                return false;
            }

            world.Swap(x, y, targetX, y);
            return true;
        }

        /// <summary>纯摩擦度的斜落净空，也就是这类材料的安息角档位。</summary>
        /// <returns>
        /// <c>0</c> 表示完全不斜滑（<paramref name="friction"/> &gt;= 1）；否则 &gt;= 1。
        /// 0 → 1（45°，像沙）、0.5 → 2（2:1）、0.9 → 10（10:1，像石头）；越接近 1 越陡。
        /// </returns>
        public static int SlideClearance(float friction)
        {
            if (friction >= 1f)
            {
                return 0;
            }

            if (friction <= 0f)
            {
                return 1;
            }

            // 用 double 折算并 +0.5 取整：0.9f 在 float 里略小于 0.9，1/(1-0.9f) 才刚好落在 10 这一档。
            double slope = 1.0 / (1.0 - friction);
            int clearance = (int)(slope + 0.5);
            return clearance < 1 ? 1 : clearance;
        }

        /// <summary>读本格类型条目。空气 / 越界 / 空槽一律返回 <c>null</c>。</summary>
        private static ParticleType SelfType(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            ParticleType[] types = world.Types;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return null;
            }

            return types[id];
        }

        /// <summary>
        /// 横向流动：向一侧逐格扫描，最多 <paramref name="maxSpread"/> 格，移动到扫描到的第一个空气格。
        /// 左右顺序随机；扫描途中遇到不可穿过的格（静态格、或比自己重的格）即视为阻断。
        /// 因此单 tick 可能横向跳 1..<paramref name="maxSpread"/> 格——这就是「最大横流格数」的含义。
        /// </summary>
        /// <remarks>需要「落点下方也必须有空间」这类额外约束时，请在自己类型里内联扫描。</remarks>
        public static bool TryFlowHorizontal(ParticleWorld world, int x, int y, int maxSpread)
        {
            if (maxSpread <= 0)
            {
                return false;
            }

            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TryFlowSide(world, x, y, -1, maxSpread) || TryFlowSide(world, x, y, 1, maxSpread)
                : TryFlowSide(world, x, y, 1, maxSpread) || TryFlowSide(world, x, y, -1, maxSpread);
        }

        private static bool TrySlide(ParticleWorld world, int x, int y, int deltaX, int clearance)
        {
            int targetX = x + deltaX;
            int targetY = y - 1;
            for (int step = 0; step < clearance; step++)
            {
                // CanDisplace 内部已经拒绝越界，因此净空链上的每一格不必再单独判 IsInBounds。
                if (!CanDisplace(world, x, y, targetX, targetY - step))
                {
                    return false;
                }
            }

            world.Swap(x, y, targetX, targetY);
            return true;
        }

        private static bool TryFlowSide(ParticleWorld world, int x, int y, int direction, int maxSpread)
        {
            for (int step = 1; step <= maxSpread; step++)
            {
                int targetX = x + direction * step;
                if (!world.IsInBounds(targetX, y) || !CanFlowThrough(world, x, y, targetX, y))
                {
                    return false;
                }

                if (!world.IsAir(targetX, y))
                {
                    continue;
                }

                world.Swap(x, y, targetX, y);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 「穿过」判定：空气、更轻的格、以及同 rank 的格可以；静态格与更重的格不行。
        /// 同 rank 只对**流体**（Liquid / Gas）放行——固体穿过同类固体等于凭空钻过去，
        /// 被穿过的格子原地不动，会破坏「同类互不置换 ⇒ 沙堆成形」的直觉。
        /// </summary>
        private static bool CanFlowThrough(ParticleWorld world, int fromX, int fromY, int toX, int toY)
        {
            ushort otherId = world.GetCell(toX, toY).typeId;
            if (otherId == ParticleRegistry.AirTypeId)
            {
                return true;
            }

            ushort selfId = world.GetCell(fromX, fromY).typeId;
            ParticleType[] types = world.Types;
            if (selfId >= types.Length || otherId >= types.Length)
            {
                return false;
            }

            ParticleType self = types[selfId];
            ParticleType other = types[otherId];
            if (self == null || other == null || other.Info.IsStatic)
            {
                return false;
            }

            ParticleTags selfTags = self.Info.Tags;
            int selfRank = TagRank(selfTags);
            int otherRank = TagRank(other.Info.Tags);

            if (otherRank < selfRank)
            {
                return true;
            }

            // 同 rank 的「穿行」只对流体（液/气）开放：固体不得横向钻过同类固体，
            // 否则会绕过「同类互不置换」，让固体穿过本该挡住它的同伴。
            return otherRank == selfRank && (selfTags & ParticleTags.Solid) == 0;
        }
    }
}
