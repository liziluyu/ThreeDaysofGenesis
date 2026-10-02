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
            Color32[] palette,
            string debugName)
        {
            Tags = tags;
            Density = density;
            AffectedByGravity = affectedByGravity;
            IsStatic = isStatic;
            MaxFlowSpread = maxFlowSpread;
            Palette = palette;
            DebugName = debugName;
        }
    }
}
