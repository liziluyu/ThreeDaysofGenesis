using System.Runtime.InteropServices;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 单个粒子的全部数据。**唯一真源**：其它任何文件都不得复制该结构。
    /// 4 字节、无填充；整个网格是一个 <see cref="Cell"/>[]，下标 = y * Width + x。
    /// </summary>
    /// <remarks>
    /// 启用速度场时在此追加 <c>public sbyte vx;</c> 与 <c>public sbyte vy;</c>（变为 6 字节）。
    /// 因为所有跨格移动都走 <see cref="ParticleWorld.Swap"/> / <see cref="ParticleWorld.Set"/> 的整格拷贝，
    /// 追加字段不会波及任何粒子类型代码。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct Cell
    {
        /// <summary>0 = 空气；非 0 = <see cref="ParticleRegistry.Types"/> 的下标。</summary>
        public ushort typeId;

        /// <summary>状态位，见 <see cref="CellFlags"/>。bit0 由 <see cref="ParticleWorld"/> 独占管理。</summary>
        public byte flags;

        /// <summary>调色板索引。由 <see cref="ParticleWorld.Set"/> 按该类型调色板抽样，渲染阶段直接查表。</summary>
        public byte shade;
    }
}
