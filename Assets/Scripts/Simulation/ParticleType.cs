namespace TapTap2026.Simulation
{
    /// <summary>
    /// 注册表条目：类型属性 + 已提取的委托。由 <see cref="ParticleRegistry"/> 在启动时构造，之后不可变。
    /// 运行期按 <c>typeId</c> 数组寻址拿到本对象，直接调用其委托字段；
    /// <see cref="ParticleWorld"/> 与渲染层只认识这个类型，不认识 <see cref="IParticleType"/>。
    /// </summary>
    public sealed class ParticleType
    {
        /// <summary>本类型的 id，等于注册顺序，从 1 开始（0 保留给空气）。</summary>
        public readonly ushort Id;

        /// <summary>类型属性。</summary>
        public readonly ParticleTypeInfo Info;

        /// <summary>运动帧（60 Hz）委托，提取自注册时的行为单例。</summary>
        public readonly ParticleMotionDelegate OnMotion;

        /// <summary>逻辑帧（20 Hz）委托，提取自注册时的行为单例。</summary>
        public readonly ParticleLogicDelegate OnLogic;

        /// <summary>随机刻委托，提取自注册时的行为单例。</summary>
        public readonly ParticleRandomTickDelegate OnRandomTick;

        /// <summary>生成委托。</summary>
        public readonly ParticleSpawnDelegate OnSpawn;

        /// <summary>死亡委托。</summary>
        public readonly ParticleKillDelegate OnKill;

        public ParticleType(
            ushort id,
            ParticleTypeInfo info,
            ParticleMotionDelegate onMotion,
            ParticleLogicDelegate onLogic,
            ParticleRandomTickDelegate onRandomTick,
            ParticleSpawnDelegate onSpawn,
            ParticleKillDelegate onKill)
        {
            Id = id;
            Info = info;
            OnMotion = onMotion;
            OnLogic = onLogic;
            OnRandomTick = onRandomTick;
            OnSpawn = onSpawn;
            OnKill = onKill;
        }
    }
}
