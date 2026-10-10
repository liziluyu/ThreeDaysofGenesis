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
