using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 一种粒子类型的完整属性。注册时读一次后即视为不可变；运行期只读，任何类型都不得修改。
    /// </summary>
    public readonly struct ParticleTypeInfo
    {
        /// <summary>
        /// 类别标签（Solid / Liquid / Gas）与属性标签（<see cref="ParticleTags.Organic"/>）。跨类型的通用规则只看它，不看具体类型。
        /// 至少应带一个**类别**标签（Solid / Liquid / Gas）；<see cref="ParticleTags.Organic"/> 是属性标签，不参与 rank，可与类别标签并用。
        /// </summary>
        public readonly ParticleTags Tags;

        /// <summary>
        /// 密度。同体积下越大越重。
        /// 跨类别「谁让开谁」由 <see cref="Tags"/> 的类别次序决定（<see cref="ParticleRules.CanDisplace"/>）；
        /// **同一类别内部**由本字段再排序：气体互相换位时按 <see cref="ParticleRules.CanGasEnter"/> 比较，
        /// 只有 <c>自己.Density &gt;= 目标.Density</c> 才推得动对方——重的气体沉穿轻的、轻的气体挤不动重的，
        /// 同密度（同类）之间照常混合。固体 / 液体的内置规则目前不读本字段。
        /// </summary>
        public readonly float Density;

        /// <summary>
        /// 热容 C（必须为有限正数）。热量与温度的换算是 <c>T = Q / C</c>（见 <see cref="HeatMap"/>）：
        /// 同一份热量下热容越大、升温越慢；反之散热越快。单位自定，只要各类型之间相对大小合理即可。
        /// 空气没有类型条目，用 <see cref="HeatMap.AirHeatCapacity"/>。
        /// </summary>
        public readonly float HeatCapacity;

        /// <summary>
        /// 相对导热率 σ，范围 <c>[0, 1]</c>（1 = 最强，0 = 绝热）。相邻两格每 tick 交换的热量按
        /// <c>Conductivity · (σ_本 + σ_邻) / 2 · (T_邻 − T_本)</c> 计算——界面导热率取两侧材料的**算术平均**，
        /// 因此高导热材料能把热摊开、低导热材料把热憋住（见 <see cref="HeatMap"/>）。
        /// 全部材料都取 1 时等价于旧的「单一全局导热系数」行为。空气没有类型条目，用 <see cref="HeatMap.AirThermalConductivity"/>。
        /// </summary>
        public readonly float ThermalConductivity;

        /// <summary>
        /// 相对辐射率 ε，范围 <c>[0, 1]</c>（1 = 黑体，0 = 对红外完全透明）。
        /// 只被 <see cref="HeatMap.Step"/> 读取：暴露于天空的格按
        /// <c>ΔQ = -RadiativeLoss · ε · T</c> 向外辐射。取 1 等价于旧的「所有材料同一个辐射系数」行为。
        /// </summary>
        /// <remarks>
        /// 与导热率 σ 是**两个独立的旋钮**：σ 决定热在格与格之间传得多快，ε 决定热从暴露面漏掉多少。
        /// 因此 ε 是「热在竖直方向能铺多远」的主要杠杆——空气（N₂/O₂）对红外透明，ε 取小值后
        /// 整根气柱不再逐格漏热，地表的热才能一路铺到网格顶（见 <see cref="HeatMap"/>）。
        /// 空气没有类型条目，用 <see cref="HeatMap.AirThermalEmissivity"/>。
        /// </remarks>
        public readonly float ThermalEmissivity;

        /// <summary>是否受重力。由该类型自己的 <c>OnMotion</c> 决定如何使用。</summary>
        public readonly bool AffectedByGravity;

        /// <summary>静态类型（墙体等）。扫描时直接跳过，永不进入 <c>OnMotion</c> / <c>OnLogic</c>。</summary>
        public readonly bool IsStatic;

        /// <summary>最大横流格数：单 tick 内单侧最多蔓延多少格。水的带闸门横流（<see cref="WaterParticle"/>）只横移 1 格，把它用作「落差视野」的扫描范围。</summary>
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

        /// <summary>
        /// 光照吸收率 ∈ [0, 1]：光照向本格注入热量时按它缩放（1 = 完全吸收、按 <see cref="LightController"/>
        /// 的强度升温；0 = 全反射、光照不升温）。只被 <see cref="LightController"/> 读取：
        /// 到达的每刻热量 = <c>Intensity × 云透过率 × LightAbsorption</c>。用来表达「浅色 / 反光的表面
        /// （如草）在阳光下升温更慢」。**不影响热传导与辐射**（那两个各有 σ / ε）。
        /// </summary>
        public readonly float LightAbsorption;

        /// <summary>调色板，长度 1..256。<see cref="Cell.shade"/> 直接作为下标。</summary>
        public readonly Color32[] Palette;

        /// <summary>仅用于编辑器与日志显示。**永不出现在模拟热路径**。</summary>
        public readonly string DebugName;

        public ParticleTypeInfo(
            ParticleTags tags,
            float density,
            float heatCapacity,
            float thermalConductivity,
            float thermalEmissivity,
            bool affectedByGravity,
            bool isStatic,
            byte maxFlowSpread,
            float friction,
            float stickiness,
            Color32[] palette,
            string debugName,
            float lightAbsorption = 1f)
        {
            Tags = tags;
            Density = density;
            HeatCapacity = heatCapacity;
            ThermalConductivity = thermalConductivity;
            ThermalEmissivity = thermalEmissivity;
            AffectedByGravity = affectedByGravity;
            IsStatic = isStatic;
            MaxFlowSpread = maxFlowSpread;
            Friction = friction;
            Stickiness = stickiness;
            Palette = palette;
            DebugName = debugName;
            LightAbsorption = lightAbsorption;
        }
    }
}
