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

        /// <summary>
        /// 热容 C（必须为有限正数）。热量与温度的换算是 <c>T = Q / C</c>（见 <see cref="HeatMap"/>）：
        /// 同一份热量下热容越大、升温越慢；反之散热越快。单位自定，只要各类型之间相对大小合理即可。
        /// 空气没有类型条目，用 <see cref="HeatMap.AirHeatCapacity"/>。
        /// </summary>
        public readonly float HeatCapacity;

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
        /// 与 <see cref="Stickiness"/> 的差别：摩擦度是**材料自身**的静摩擦（孤零零一颗也有效果），
        /// 粘性只在**旁边有固体邻居**时生效。
        /// </remarks>
        public readonly float Friction;

        /// <summary>
        /// 粘性，范围 <c>[0, 1]</c>。只被固体运动规则读取（<see cref="ParticleRules.StepSolid"/>）：
        /// 斜滑方向**反侧同一行**上有固体邻居时，粒子粘住邻居，等效安息角被抬高——
        /// 也就是「旁边有粒子就不肯往下滚」。孤立粒子（反侧同排没有固体）不受粘性影响，仍按摩擦度滑落。
        /// </summary>
        /// <remarks>
        /// 粘性 <c>σ</c> 贡献的额外斜率是 <c>1 / (1 - σ) - 1</c>（<c>σ = 0</c> 时为 0），
        /// 与摩擦度的斜率相加后取整：<c>0</c> = 不影响；<c>0.5</c> = 多 1 档（沙 + 0.5 ≈ 土的 2:1）；
        /// <c>0.9</c> = 多 9 档；<c>1</c> = 只要旁边有固体就绝不滚落（形成陡壁 / 悬垂）。
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
            float heatCapacity,
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
            HeatCapacity = heatCapacity;
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
