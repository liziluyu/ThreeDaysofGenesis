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

        /// <summary>(x,y) 是否挡住光（带 Solid 或 Liquid 标签）：光照只穿过气体与空，打到水面/地面即停。
        /// 越界与空气均为 false。</summary>
        public static bool BlocksLight(ParticleWorld world, int x, int y)
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
            return type != null && (type.Info.Tags & (ParticleTags.Solid | ParticleTags.Liquid)) != 0;
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
        /// 固体粒子的标准运动，由本格的 <see cref="ParticleTypeInfo.Friction"/> 与
        /// <see cref="ParticleTypeInfo.Stickiness"/> 共同驱动。
        /// 正下可进入则下落一格（固体 rank 更高，因此能沉穿液体 / 气体）；正下被挡住时，
        /// 摩擦度决定安息角，粘性在「斜滑方向的反侧同一行有固体邻居」时把安息角抬得更高。
        /// </summary>
        /// <remarks>
        /// 每个固体类型的 <c>OnUpdate</c> 都应当是这一行；两个参数都从注册表读，类型无需自己抄常量。
        /// 两条规则的区别：摩擦度是材料自身的静摩擦（孤立一颗也生效），粘性靠邻居（旁边有固体才生效）。
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

            TryCohesiveFall(world, x, y, self.Info.Friction, self.Info.Stickiness);
        }

        /// <summary>
        /// 正下方被挡住时尝试斜落，左右顺序随机以消除方向偏置。
        /// <paramref name="stickiness"/> 只在「斜滑所朝方向的反侧同一行上有固体邻居」时计入——
        /// 粒子要**旁边有粒子**才粘得住；反侧无固体（孤立粒子）时完全按 <paramref name="friction"/> 滑落。
        /// </summary>
        /// <remarks>固体请用 <see cref="StepSolid"/>（它负责从注册表取这两个参数并处理直落）。</remarks>
        public static bool TryCohesiveFall(
            ParticleWorld world, int x, int y, float friction, float stickiness)
        {
            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TrySlope(world, x, y, -1, friction, stickiness) || TrySlope(world, x, y, 1, friction, stickiness)
                : TrySlope(world, x, y, 1, friction, stickiness) || TrySlope(world, x, y, -1, friction, stickiness);
        }

        /// <summary>
        /// 单侧斜滑：净空按 <see cref="SlideClearance(float, float, bool)"/> 算。
        /// 「粘住」的判据是斜滑**反侧同一行**的格子（也就是粒子正离开的那个侧邻）带 Solid 标签——
        /// 粘性为 0 的类型连这一次邻居查询都不做，因此行为与纯摩擦度完全一致。
        /// </summary>
        private static bool TrySlope(
            ParticleWorld world, int x, int y, int deltaX, float friction, float stickiness)
        {
            bool clinging = stickiness > 0f && IsSolid(world, x - deltaX, y);
            int clearance = SlideClearance(friction, stickiness, clinging);
            return clearance > 0 && TrySlide(world, x, y, deltaX, clearance);
        }

        /// <summary>纯摩擦度的斜落净空。等价于 <c>SlideClearance(friction, 0f, false)</c>。</summary>
        /// <returns>
        /// <c>0</c> 表示完全不斜滑（<paramref name="friction"/> &gt;= 1）；否则 &gt;= 1。
        /// 0 → 1（45°，像沙）、0.5 → 2（2:1）、0.9 → 10（10:1，像石头）；越接近 1 越陡。
        /// </returns>
        public static int SlideClearance(float friction)
        {
            return SlideClearance(friction, 0f, false);
        }

        /// <summary>
        /// 摩擦度与粘性共同决定斜落所需的连续净空格数，也就是安息角斜率（rise/run）：
        /// 摩擦度贡献 <c>1 / (1 - friction)</c>，粘住在邻居上时再叠加粘性贡献
        /// <c>1 / (1 - stickiness) - 1</c>（<c>stickiness = 0</c> 时为 0，故不改变纯摩擦度的结果）。
        /// </summary>
        /// <param name="clinging">
        /// 「斜滑反侧同一行上有固体邻居」。为 false 时 <paramref name="stickiness"/> 完全不参与。
        /// </param>
        /// <returns>
        /// <c>0</c> = 永不斜滑（<paramref name="friction"/> &gt;= 1，或粘住时 <paramref name="stickiness"/> &gt;= 1）；
        /// 否则 &gt;= 1，四舍五入到整数格。
        /// </returns>
        public static int SlideClearance(float friction, float stickiness, bool clinging)
        {
            if (friction >= 1f || (clinging && stickiness >= 1f))
            {
                return 0;
            }

            // 用 double 折算并 +0.5 取整：0.9f 在 float 里略小于 0.9，1/(1-0.9f) 才刚好落在 10 这一档。
            double slope = 1.0 / (1.0 - friction);
            if (clinging)
            {
                slope += 1.0 / (1.0 - stickiness) - 1.0;
            }

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

                // 落点判据是「能置换进去」而不是 IsAir：世界被大气粒子填满后没有 typeId 0 的格子，
                // 用 IsAir 会让液体永远找不到落点而不再横流。水换进大气格 = 两者互换位置，
                // 大气被挤到水原来的格子，之后靠浮力上浮。
                if (!CanDisplace(world, x, y, targetX, y))
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

        // ==================== 气体：浮力与上升（镜像下落系） ====================

        /// <summary>(x,y) 是否是气体可以换入的格子：空（typeId 0）或任何带 <see cref="ParticleTags.Gas"/> 标签的格。
        /// 这是**纯目标判据**（不看发起者），用于视线 / 通用查询；气体运动原语请用 <see cref="CanGasEnter"/> 落实密度次序。</summary>
        public static bool IsGasOrEmpty(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            if (id == ParticleRegistry.AirTypeId)
            {
                return true;
            }

            ParticleType[] types = world.Types;
            if (id >= types.Length)
            {
                return false;
            }

            ParticleType type = types[id];
            return type != null && (type.Info.Tags & ParticleTags.Gas) != 0;
        }

        /// <summary>
        /// (toX,toY) 能否被 (fromX,fromY) 上的粒子换入，供气体运动原语（上升 / 下沉 / 漂移 / 混合）使用。
        /// 空（typeId 0）恒可进入；目标是气体时按 <see cref="ParticleTypeInfo.Density"/> 比较：
        /// 只有 <c>自己.Density &gt;= 目标.Density</c> 才可换入——**重的气体能沉穿轻的，轻的挤不动重的**。
        /// 于是密度最低的环境大气（0.5）推不动云（0.8）与蒸汽（0.6）；同密度气体（同类之间）照常混合。
        /// 目标不是气体（固体 / 液体）或越界一律 false。
        /// </summary>
        /// <remarks>
        /// 用 <c>&gt;=</c> 而非 <c>&gt;</c>：同密度必须能互穿，否则同类气体（大气↔大气）连随机漂移都做不了。
        /// 这是「同 rank 气体互相换位 = 混合」的细化——rank 决定**类别**能否换位，密度决定**同类内**谁推得动谁。
        /// </remarks>
        public static bool CanGasEnter(ParticleWorld world, int fromX, int fromY, int toX, int toY)
        {
            if (!world.IsInBounds(toX, toY))
            {
                return false;
            }

            ushort otherId = world.GetCell(toX, toY).typeId;
            if (otherId == ParticleRegistry.AirTypeId)
            {
                return true;
            }

            ParticleType[] types = world.Types;
            if (otherId >= types.Length)
            {
                return false;
            }

            ParticleType other = types[otherId];
            if (other == null || (other.Info.Tags & ParticleTags.Gas) == 0)
            {
                return false;
            }

            ushort selfId = world.GetCell(fromX, fromY).typeId;
            if (selfId >= types.Length)
            {
                return false;
            }

            ParticleType self = types[selfId];
            if (self == null || (self.Info.Tags & ParticleTags.Gas) == 0)
            {
                return false;
            }

            return self.Info.Density >= other.Info.Density;
        }

        /// <summary>上一格能被换入（<see cref="CanGasEnter"/>：气体按密度次序）则交换，上升一格。</summary>
        public static bool TryRise(ParticleWorld world, int x, int y)
        {
            int targetY = y + 1;
            if (!CanGasEnter(world, x, y, x, targetY))
            {
                return false;
            }

            world.Swap(x, y, x, targetY);
            return true;
        }

        /// <summary>上方被挡住时尝试斜上方，左右顺序随机以消除方向偏置。</summary>
        public static bool TryDiagonalRise(ParticleWorld world, int x, int y)
        {
            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TryRiseSide(world, x, y, -1) || TryRiseSide(world, x, y, 1)
                : TryRiseSide(world, x, y, 1) || TryRiseSide(world, x, y, -1);
        }

        /// <summary>下一格能被换入（<see cref="CanGasEnter"/>：气体按密度次序）则交换，下沉一格（气体版的「下落」）。</summary>
        public static bool TrySink(ParticleWorld world, int x, int y)
        {
            int targetY = y - 1;
            if (!CanGasEnter(world, x, y, x, targetY))
            {
                return false;
            }

            world.Swap(x, y, x, targetY);
            return true;
        }

        /// <summary>下方被挡住时尝试斜下方（气体版），左右顺序随机。</summary>
        public static bool TryDiagonalSink(ParticleWorld world, int x, int y)
        {
            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TrySinkSide(world, x, y, -1) || TrySinkSide(world, x, y, 1)
                : TrySinkSide(world, x, y, 1) || TrySinkSide(world, x, y, -1);
        }

        /// <summary>
        /// 浮力运动：本格温度与四邻均温比较，热（> 均值 + <paramref name="epsilon"/>）则上升，
        /// 冷（&lt; 均值 - epsilon）则下沉，否则不动。竖直被挡时走斜向。
        /// 「热气上升、冷气下沉」——海陆风环流与对流的发动机。
        /// </summary>
        public static bool TryBuoyantStep(ParticleWorld world, int x, int y, float epsilon)
        {
            HeatMap heat = world.Heat;
            float mean = (heat.GetTemperature(x - 1, y) + heat.GetTemperature(x + 1, y)
                        + heat.GetTemperature(x, y - 1) + heat.GetTemperature(x, y + 1)) * 0.25f;
            float self = heat.GetTemperature(x, y);

            if (self > mean + epsilon)
            {
                return TryRise(world, x, y) || TryDiagonalRise(world, x, y);
            }

            if (self < mean - epsilon)
            {
                return TrySink(world, x, y) || TryDiagonalSink(world, x, y);
            }

            return false;
        }

        /// <summary>
        /// 水平热压流：仅当左右邻居恰有一侧更热（差值超过 <paramref name="epsilon"/>）时往暖侧换一格——
        /// 冷空气流向暖区（低空海风）。两侧都更热或都不更热时不动；换入受 <see cref="CanGasEnter"/> 的密度次序约束。
        /// </summary>
        public static bool TryThermalDrift(ParticleWorld world, int x, int y, float epsilon)
        {
            HeatMap heat = world.Heat;
            float self = heat.GetTemperature(x, y);
            bool leftWarmer = heat.GetTemperature(x - 1, y) > self + epsilon;
            bool rightWarmer = heat.GetTemperature(x + 1, y) > self + epsilon;

            if (leftWarmer == rightWarmer)
            {
                return false;
            }

            int direction = leftWarmer ? -1 : 1;
            if (!CanGasEnter(world, x, y, x + direction, y))
            {
                return false;
            }

            world.Swap(x, y, x + direction, y);
            return true;
        }

        /// <summary>随机水平漂移：向左或右（随机序）换一格可换入的格（<see cref="CanGasEnter"/>，气体按密度次序）。云的漂移、气体的混合用它。</summary>
        public static bool TryRandomDrift(ParticleWorld world, int x, int y)
        {
            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TryDriftSide(world, x, y, -1) || TryDriftSide(world, x, y, 1)
                : TryDriftSide(world, x, y, 1) || TryDriftSide(world, x, y, -1);
        }

        private static bool TryRiseSide(ParticleWorld world, int x, int y, int deltaX)
        {
            int targetX = x + deltaX;
            int targetY = y + 1;
            if (!CanGasEnter(world, x, y, targetX, targetY))
            {
                return false;
            }

            world.Swap(x, y, targetX, targetY);
            return true;
        }

        private static bool TrySinkSide(ParticleWorld world, int x, int y, int deltaX)
        {
            int targetX = x + deltaX;
            int targetY = y - 1;
            if (!CanGasEnter(world, x, y, targetX, targetY))
            {
                return false;
            }

            world.Swap(x, y, targetX, targetY);
            return true;
        }

        private static bool TryDriftSide(ParticleWorld world, int x, int y, int direction)
        {
            int targetX = x + direction;
            if (!CanGasEnter(world, x, y, targetX, y))
            {
                return false;
            }

            world.Swap(x, y, targetX, y);
            return true;
        }
    }
}
