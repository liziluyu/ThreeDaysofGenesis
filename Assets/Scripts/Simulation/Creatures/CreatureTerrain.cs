namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物层对世界的**只读地形视图**：把「这一格动物撞不撞得上 / 是不是水」的判断收敛到一处。
    /// </summary>
    /// <remarks>
    /// <para><b>碰撞白名单</b>：只有沙 / 石头 / 泥土家族（土、湿土、腐殖土、湿润腐殖土）挡动物。
    /// 草、花、树叶、种子等一律不挡——兔子从草里穿过。水也不是固体，但参与淹没 / 浮力判定。</para>
    /// <para>按 <see cref="ParticleIds"/> 常量比对而不是按标签：白名单是有意写死的短名单，
    /// 未来新增固体类型要**显式**决定要不要挡动物，而不是被标签机制悄悄带进来。</para>
    /// <para>纯查询：不写世界、不分配、不消耗随机流。</para>
    /// </remarks>
    public static class CreatureTerrain
    {
        /// <summary>本格是否是挡动物的固体（沙 / 石头 / 土 / 湿土 / 腐殖土 / 湿润腐殖土）。</summary>
        /// <param name="world">被查询的世界。</param>
        /// <param name="x">格坐标 x。</param>
        /// <param name="y">格坐标 y。</param>
        /// <returns>越界或不在白名单内返回 false。</returns>
        public static bool IsSolid(ParticleWorld world, int x, int y)
        {
            if (world == null || !world.IsInBounds(x, y))
            {
                return false;
            }

            ushort id = world.GetCell(x, y).typeId;
            return id == ParticleIds.Sand
                || id == ParticleIds.Stone
                || id == ParticleIds.Soil
                || id == ParticleIds.WetSoil
                || id == ParticleIds.HumusSoil
                || id == ParticleIds.WetHumusSoil;
        }

        /// <summary>
        /// 本格是否可作为**向下支撑面**（栖息面）：<see cref="IsSolid"/> ∪ {树叶, 树干}。
        /// </summary>
        /// <remarks>
        /// <b>只服务竖直支撑口径</b>——飞行 / 栖息物种（鸟）向下扫描时的落脚面唯一用这里；
        /// **向上顶头、水平阻挡、掩埋判定、尸体覆盖一律仍走 <see cref="IsSolid"/>**：
        /// 叶子永不挡上升、永不挡水平、不会把动物压死、尸体依旧直接覆盖树叶。
        /// 纯查询：不写世界、不分配、不消耗随机流。
        /// </remarks>
        /// <param name="world">被查询的世界。</param>
        /// <param name="x">格坐标 x。</param>
        /// <param name="y">格坐标 y。</param>
        /// <returns>越界或既非固体也非树叶 / 树干返回 false。</returns>
        public static bool IsPerchable(ParticleWorld world, int x, int y)
        {
            if (world == null || !world.IsInBounds(x, y))
            {
                return false;
            }

            if (IsSolid(world, x, y))
            {
                return true;
            }

            ushort id = world.GetCell(x, y).typeId;
            return id == ParticleIds.TreeLeaf || id == ParticleIds.TreeTrunk;
        }

        /// <summary>本格是否是水（淹没 / 浮力判据）。</summary>
        /// <param name="world">被查询的世界。</param>
        /// <param name="x">格坐标 x。</param>
        /// <param name="y">格坐标 y。</param>
        /// <returns>越界或不是水返回 false。</returns>
        public static bool IsWater(ParticleWorld world, int x, int y)
        {
            if (world == null || !world.IsInBounds(x, y))
            {
                return false;
            }

            return world.GetCell(x, y).typeId == ParticleIds.Water;
        }
    }
}
