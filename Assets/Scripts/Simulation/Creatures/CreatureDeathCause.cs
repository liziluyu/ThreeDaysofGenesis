namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物的死因。由 <see cref="Creature.Kill"/> 记录，在世界清扫死亡时随
    /// <see cref="CreatureWorld.CreatureDied"/> 一起送出（渲染 / 统计 / 未来的遗骸生成用）。
    /// </summary>
    /// <remarks>
    /// 枚举值一经确定**只能追加、不得重排**（与 <see cref="ParticleIds"/> 的注册顺序同理）。
    /// 本阶段只有物理，实际只会产生 <see cref="Unknown"/>；其余取值是给后续 AI / 环境交互预留的。
    /// </remarks>
    public enum CreatureDeathCause
    {
        /// <summary>未知 / 未标注（本阶段的默认值）。</summary>
        Unknown = 0,

        /// <summary>溺死。</summary>
        Drowned,

        /// <summary>烧死。</summary>
        Burned,

        /// <summary>被压死 / 撞击致死。</summary>
        Crushed,

        /// <summary>饿死。</summary>
        Starved,

        /// <summary>老死。</summary>
        OldAge,

        /// <summary>搁浅而死（鱼离水过久）。</summary>
        Beached,

        /// <summary>被猎杀（鸟捕食兔 / 鱼——<see cref="Creature.HuntTarget"/> 接触致死）。</summary>
        Hunted
    }
}
