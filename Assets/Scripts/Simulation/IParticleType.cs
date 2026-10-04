namespace TapTap2026.Simulation
{
    /// <summary>粒子类型的**运动**逻辑：只在运动帧（60 Hz）被调用。</summary>
    /// <param name="world">世界。只通过它读写网格。</param>
    /// <param name="x">调用时刻该粒子的 x（就是 world.Index 反算出来的坐标）。</param>
    /// <param name="y">调用时刻该粒子的 y。</param>
    public delegate void ParticleMotionDelegate(ParticleWorld world, int x, int y);

    /// <summary>粒子类型的**其它**逻辑：只在逻辑帧（20 Hz）被调用。</summary>
    /// <param name="world">世界。只通过它读写网格。</param>
    /// <param name="x">调用时刻该粒子的 x。</param>
    /// <param name="y">调用时刻该粒子的 y。</param>
    public delegate void ParticleLogicDelegate(ParticleWorld world, int x, int y);

    /// <summary>
    /// 随机刻逻辑：每个逻辑帧由世界随机抽中的格触发，与 <see cref="ParticleMotionDelegate"/> / <see cref="ParticleLogicDelegate"/> 相互独立。
    /// </summary>
    public delegate void ParticleRandomTickDelegate(ParticleWorld world, int x, int y);

    /// <summary>粒子被生成（<see cref="ParticleWorld.Set"/> 创建新类型格）之后的初始化逻辑。</summary>
    public delegate void ParticleSpawnDelegate(ParticleWorld world, int x, int y);

    /// <summary>粒子被其它类型或空气覆盖之前的死亡逻辑。</summary>
    /// <param name="killerTypeId">覆盖者类型；0 表示被抹除（Clear）。</param>
    public delegate void ParticleKillDelegate(ParticleWorld world, int x, int y, ushort killerTypeId);

    /// <summary>
    /// 粒子类型的**作者期契约**：一个类型 = 一个文件 = 一个 <c>public sealed class</c>，
    /// 放在 <c>Assets/Scripts/Simulation/Particles/</c>，不继承任何基类、不是 MonoBehaviour / ScriptableObject。
    /// </summary>
    /// <remarks>
    /// 这个接口**只用于**作者期约束和注册期提取委托（见 <see cref="ParticleRegistry.Register"/>）。
    /// 运行期持有的是注册表里的 <see cref="ParticleType"/>（数据 + 委托），
    /// 从不持有 <see cref="IParticleType"/> 引用、也从不通过接口调方法——热路径没有接口虚分派。
    /// </remarks>
    public interface IParticleType
    {
        /// <summary>类型属性。注册时读一次，运行期不可变。</summary>
        ParticleTypeInfo Info { get; }

        /// <summary>
        /// 运动逻辑：每个**运动帧**（60 Hz）扫描到该类型且该格本帧尚未被处理时调用。
        /// 只负责「移动」——下落、斜落、横流、浮力位移，通过 <c>Swap</c> 实现。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="OnLogic"/> 的关系：逻辑帧上先跑 <see cref="OnLogic"/> 再跑本方法；
        /// 若 <see cref="OnLogic"/> 已把本格换成别的类型（相变 / 反应），本帧不再调用本方法。
        /// 运动帧上只调用本方法，不做任何相变 / 反应——那些都属于 <see cref="OnLogic"/>。
        /// 禁止在本方法里做相变（<c>SetPhase</c> / <c>Set</c> / <c>Clear</c> 本格）之外的跨类型反应；
        /// 一个粒子一帧只做一次决策、至多移动一次，移动后立即返回。
        /// </remarks>
        void OnMotion(ParticleWorld world, int x, int y);

        /// <summary>
        /// 其它逻辑：每个**逻辑帧**（20 Hz）扫描到该类型且该格本帧尚未被处理时调用，
        /// 且在同格的 <see cref="OnMotion"/> **之前**。相变、反应、邻域写入都放这里。
        /// </summary>
        /// <remarks>
        /// 本方法**不得移动粒子**（不要调用 <c>Swap</c>）——移动是 <see cref="OnMotion"/> 的职责。
        /// 若本方法把本格换成别的类型（例如水沸腾成大气），本帧的运动会被跳过，新格已标记为本帧已处理。
        /// 不需要其它逻辑的类型请留空——留空是常态，不要为了「凑齐」把运动塞进来。
        /// </remarks>
        void OnLogic(ParticleWorld world, int x, int y);

        /// <summary>
        /// 随机刻：每个逻辑帧世界随机抽 <see cref="ParticleWorld.RandomTicksPerStep"/> 个格，
        /// 命中本类型且非静态时调用。用于「缓慢、按概率发生」的过程——生长、蔓延、渗流、腐化等，
        /// 它们不该每 tick 全网格扫描，而应像 Minecraft 的随机刻那样零星发生。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="OnMotion"/> / <see cref="OnLogic"/> 相互独立：**不检查、也不写** bit0 处理标记，
        /// 因此同一次逻辑帧内同一格可能被抽中多次。空气格与 <see cref="ParticleTypeInfo.IsStatic"/> 的格永不触发（与扫描一致）。
        /// 随机源就是世界的确定性随机流（<see cref="ParticleWorld.NextUInt"/>），与类型代码共享同一序列。
        /// 没有随机刻行为的类型请留空——不要把本该由 <see cref="OnLogic"/> 做的事塞进来。
        /// </remarks>
        void OnRandomTick(ParticleWorld world, int x, int y);

        /// <summary><see cref="ParticleWorld.Set"/> 创建出该类型格之后立即调用。</summary>
        void OnSpawn(ParticleWorld world, int x, int y);

        /// <summary>该类型格被其它类型或空气覆盖时、在写入新格之前调用。</summary>
        void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId);
    }
}
