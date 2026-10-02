using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 粒子所属的类别标签。跨类型的通用规则（下落、置换、流动、静止判定）**只看标签，不看具体类型**，
    /// 因此新增一种固体不需要改任何已有类型的代码。
    /// </summary>
    /// <remarks>
    /// <para>目前只有三个标签：<see cref="Solid"/> / <see cref="Liquid"/> / <see cref="Gas"/>。
    /// 空气（<c>typeId == 0</c>）不属于任何标签，rank 视为 0。</para>
    /// <para>类别之间存在固定的「轻重」次序，用于判断谁能让开谁：</para>
    /// <code>Solid(3) &gt; Liquid(2) &gt; Gas(1) &gt; 空气(0)</code>
    /// <para>规则：<b>高 rank 可以置换低 rank</b>。固体因此落穿液体与气体、液体落穿气体，
    /// 而同类之间互不置换——这正是沙能堆成 45° 三角堆、石头能堆成更陡的尖堆的原因。</para>
    /// </remarks>
    [Flags]
    public enum ParticleTags : byte
    {
        /// <summary>无标签。属于设计缺陷：任何类型都应至少带一个类别标签。</summary>
        None = 0,

        /// <summary>固体：堆叠、有安息角，沉入液体/气体下方。</summary>
        Solid = 1 << 0,

        /// <summary>液体：可自由横流，沉入气体下方。</summary>
        Liquid = 1 << 1,

        /// <summary>气体：可被固体/液体置换，通常自行向上升腾。</summary>
        Gas = 1 << 2,
    }
}
