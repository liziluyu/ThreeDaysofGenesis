using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 一种粒子类型的完整属性。注册时读一次后即视为不可变；运行期只读，任何类型都不得修改。
    /// </summary>
    public readonly struct ParticleTypeInfo
    {
        /// <summary>
        /// 类别标签（Solid / Liquid / Gas）。跨类型的通用规则只看它，不看具体类型。
        /// 至少应带一个标签。
        /// </summary>
        public readonly ParticleTags Tags;

        /// <summary>
        /// 密度。同体积下越大越重。
        /// **注意**：内置移动规则（<see cref="ParticleRules"/>）不读取本字段，谁能让开谁由
        /// <see cref="Tags"/> 的类别次序决定；本字段保留给需要同一类别内部再排序的类型自行实现。
        /// </summary>
        public readonly float Density;

        /// <summary>是否受重力。由该类型自己的 <c>OnUpdate</c> 决定如何使用。</summary>
        public readonly bool AffectedByGravity;

        /// <summary>静态类型（墙体等）。扫描时直接跳过，永不进入 <c>OnUpdate</c>。</summary>
        public readonly bool IsStatic;

        /// <summary>最大横流格数：单 tick 内单侧最多蔓延多少格。</summary>
        public readonly byte MaxFlowSpread;

        /// <summary>
        /// 摩擦度，范围 <c>[0, 1]</c>。只被固体运动规则读取（<see cref="ParticleRules.StepSolid"/>）：
        /// 正下被挡住时，摩擦度决定粒子肯不肯沿坡斜滑，也就是安息角。
        /// </summary>
        /// <remarks>
        /// 安息角斜率（rise/run）= <c>1 / (1 - Friction)</c>，实现上四舍五入到整数格净空：
        /// <c>0</c> = 最松，1:1（45°，像沙）；<c>0.5</c> ≈ 2:1（63°）；<c>0.9</c> ≈ 10:1（84°，像石头）；
        /// <c>1</c>（含更大值）= 下方有固体就完全静止，永不沿坡滚落。非固体类型该值不参与任何规则。
        /// 与 <see cref="Stickiness"/> 的分工：摩擦度管**侧翼坡度**（材料自身的静摩擦，孤立一颗也生效），
        /// 粘性管**尖顶圆钝度**（糊状物站不住尖点，靠邻域形态生效）。
        /// </remarks>
        public readonly float Friction;

        /// <summary>
        /// 粘性，范围 <c>[0, 1]</c>。只被固体运动规则读取（<see cref="ParticleRules.StepSolid"/>）：
        /// 每 tick 把**孤立的 1 格宽尖顶**推倒的概率——糊状物站不住尖点，顶端会塌成 2~4 格宽的小平顶。
        /// </summary>
        /// <remarks>
        /// <c>0</c> = 干散材料：尖顶保持尖锐（像沙堆、石堆），一次额外邻居读都不做；
        /// 越接近 <c>1</c> 尖顶塌得越快。它<b>不改变侧翼坡度</b>——侧翼永远由 <see cref="Friction"/> 决定，
        /// 所以「粘性」只塑造尖顶的圆钝度，不会让整座堆变陡或变平。
        /// 非固体类型该值不参与任何规则。
        /// </remarks>
        public readonly float Stickiness;

        /// <summary>调色板，长度 1..256。<see cref="Cell.shade"/> 直接作为下标。</summary>
        public readonly Color32[] Palette;

        /// <summary>仅用于编辑器与日志显示。**永不出现在模拟热路径**。</summary>
        public readonly string DebugName;

        public ParticleTypeInfo(
            ParticleTags tags,
            float density,
            bool affectedByGravity,
            bool isStatic,
            byte maxFlowSpread,
            float friction,
            float stickiness,
            Color32[] palette,
            string debugName)
        {
            Tags = tags;
            Density = density;
            AffectedByGravity = affectedByGravity;
            IsStatic = isStatic;
            MaxFlowSpread = maxFlowSpread;
            Friction = friction;
            Stickiness = stickiness;
            Palette = palette;
            DebugName = debugName;
        }
    }
}
