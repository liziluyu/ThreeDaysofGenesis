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

        /// <summary>
        /// 行为类的类名（如 <c>StoneParticle</c>）——注册时由 <see cref="ParticleRegistry"/> 从行为单例取，
        /// 是**语言无关的类型标识**：白名单 / 阶段 JSON / 场景接线都按它匹配（见
        /// <c>ParticleEditor.ResolveTypeNames</c> / <c>VictoryEvaluator</c> / <c>ParticleEditor.SelectByName</c>），
        /// 界面上显示的是 <see cref="ParticleTypeInfo.DebugName"/>（中文名）。
        /// </summary>
        public readonly string ClassName;

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

        /// <summary>
        /// <see cref="ParticleTypeInfo.Palette"/> 的**纯值镜像**（长度与每个字节都相同，注册时一次；见
        /// <see cref="RenderColorConversion.FromPalette"/>）：worker 侧光栅化的唯一取色来源，
        /// 因此渲染路径不认识 Unity <c>Color32</c>。源调色板本身不改——授权面仍是 <c>Color32[]</c>。
        /// </summary>
        public readonly RenderColor[] RenderPalette;

        public ParticleType(
            ushort id,
            ParticleTypeInfo info,
            string className,
            ParticleMotionDelegate onMotion,
            ParticleLogicDelegate onLogic,
            ParticleRandomTickDelegate onRandomTick,
            ParticleSpawnDelegate onSpawn,
            ParticleKillDelegate onKill)
        {
            Id = id;
            Info = info;
            ClassName = className;
            OnMotion = onMotion;
            OnLogic = onLogic;
            OnRandomTick = onRandomTick;
            OnSpawn = onSpawn;
            OnKill = onKill;
            RenderPalette = RenderColorConversion.FromPalette(info.Palette);
        }
    }
}
