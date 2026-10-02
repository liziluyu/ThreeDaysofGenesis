namespace TapTap2026.Simulation
{
    /// <summary>
    /// 无状态的共享移动规则。粒子类型在自己的 <c>OnUpdate</c> 里按需调用，不持有任何每格状态。
    /// 所有方法都遵循：先判断、后无条件执行、成功即返回 true（调用方应立刻 return）。
    /// </summary>
    public static class ParticleRules
    {
        /// <summary>
        /// 类别标签的轻重次序：<c>Solid(3) &gt; Liquid(2) &gt; Gas(1) &gt; 空气(0)</c>。
        /// 一个字节能带多个标签时取最高的一个。
        /// </summary>
        public static int TagRank(ParticleTags tags)
        {
            if ((tags & ParticleTags.Solid) != 0)
            {
                return 3;
            }

            if ((tags & ParticleTags.Liquid) != 0)
            {
                return 2;
            }

            if ((tags & ParticleTags.Gas) != 0)
            {
                return 1;
            }

            return 0;
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

        /// <summary>正下方被挡住时尝试斜下方，左右顺序随机以消除方向偏置。</summary>
        public static bool TryDiagonalFall(ParticleWorld world, int x, int y)
        {
            if (!world.IsInBounds(x, y - 1))
            {
                return false;
            }

            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TrySlide(world, x, y, -1) || TrySlide(world, x, y, 1)
                : TrySlide(world, x, y, 1) || TrySlide(world, x, y, -1);
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

        private static bool TrySlide(ParticleWorld world, int x, int y, int deltaX)
        {
            int targetX = x + deltaX;
            int targetY = y - 1;
            if (!world.IsInBounds(targetX, targetY) || !CanDisplace(world, x, y, targetX, targetY))
            {
                return false;
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

        /// <summary>流体能否沿本行「穿过」这一格继续向外流：空气、同类（同 rank）以及更轻的格可以；静态格与更重的格不行。</summary>
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

            return TagRank(other.Info.Tags) <= TagRank(self.Info.Tags);
        }
    }
}
