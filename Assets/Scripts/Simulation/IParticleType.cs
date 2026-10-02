namespace TapTap2026.Simulation
{
    /// <summary>粒子类型在扫描到自身时的更新逻辑。</summary>
    /// <param name="world">世界。只通过它读写网格。</param>
    /// <param name="x">调用时刻该粒子的 x（就是 world.Index 反算出来的坐标）。</param>
    /// <param name="y">调用时刻该粒子的 y。</param>
    public delegate void ParticleUpdateDelegate(ParticleWorld world, int x, int y);

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

        /// <summary>扫描到该类型且该格本 tick 尚未被处理时调用，每 tick 至多一次。</summary>
        void OnUpdate(ParticleWorld world, int x, int y);

        /// <summary><see cref="ParticleWorld.Set"/> 创建出该类型格之后立即调用。</summary>
        void OnSpawn(ParticleWorld world, int x, int y);

        /// <summary>该类型格被其它类型或空气覆盖时、在写入新格之前调用。</summary>
        void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId);
    }
}
