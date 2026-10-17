namespace TapTap2026.Simulation
{
    /// <summary>
    /// 可生成的动物物种表：**投放选择（编辑器按钮 / 右键循环）与命令应用（生成实体）共用的一份真源**。
    /// 下标即 <see cref="SimulationCommand.Index"/> 的语义（生成 / 投放命令里的物种下标），
    /// 顺序即编辑器里动物项的排列顺序。
    /// </summary>
    /// <remarks>
    /// 物种全集是闭集（兔 / 鱼 / 鸟）：新增物种时在 <see cref="Species"/> 与 <see cref="Create(int)"/> 各补一行，
    /// 编译器会逼着两处对齐。物种元数据（显示名 / 占位色）的唯一真源仍在各物种类里，这里不抄第二份。
    /// </remarks>
    public static class CreatureSpawns
    {
        /// <summary>全部可生成物种（下标 = 命令里的物种下标）。</summary>
        public static readonly CreatureSpecies[] Species =
        {
            CreatureSpecies.Rabbit,
            CreatureSpecies.Fish,
            CreatureSpecies.Bird,
        };

        /// <summary>可生成物种数量。</summary>
        public static int Count
        {
            get { return Species.Length; }
        }

        /// <summary>按物种下标建一只实体（越界返回 null，不抛）。</summary>
        public static Creature Create(int speciesIndex)
        {
            if ((uint)speciesIndex >= (uint)Species.Length)
            {
                return null;
            }

            return Create(Species[speciesIndex]);
        }

        /// <summary>按物种建一只实体（<see cref="CreatureSpecies.None"/> 返回 null）。</summary>
        public static Creature Create(CreatureSpecies species)
        {
            switch (species)
            {
                case CreatureSpecies.Rabbit:
                    return new RabbitCreature();

                case CreatureSpecies.Fish:
                    return new FishCreature();

                case CreatureSpecies.Bird:
                    return new BirdCreature();

                default:
                    return null;
            }
        }
    }
}
