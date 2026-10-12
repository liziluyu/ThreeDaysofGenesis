using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 粒子所属的类别标签。跨类型的通用规则（下落、置换、流动、静止判定）**只看标签，不看具体类型**，
    /// 因此新增一种固体不需要改任何已有类型的代码。
    /// </summary>
    /// <remarks>
    /// <para>三个**类别**标签 <see cref="Solid"/> / <see cref="Liquid"/> / <see cref="Gas"/> 决定 rank 次序；
    /// 另有 <see cref="Organic"/> 这类**属性**标签（不参与 rank，只供具体规则判断，例如「有机物死亡留下腐殖质」）。
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

        /// <summary>
        /// 有机物（属性标签，**不参与 rank**）：草、草种子等生物来源的物质。
        /// 携带它的格被覆盖 / 抹除时，由 <see cref="ParticleWorld"/> 按
        /// <see cref="ParticleWorld.OrganicDecayDenominator"/> 的概率就地留下腐殖质（<see cref="ParticleIds.Humus"/>）。
        /// 必须与至少一个类别标签（Solid / Liquid / Gas）同时使用。
        /// </summary>
        Organic = 1 << 3,

        /// <summary>岩石（属性标签，**不参与 rank**）：矿物类的硬质固体（当前只有石头）。与 Organic 并列——
        /// 落石声用它把「石」从「松散固体」里分出来。必须与至少一个类别标签同时使用。</summary>
        Rock = 1 << 4,
    }
}
