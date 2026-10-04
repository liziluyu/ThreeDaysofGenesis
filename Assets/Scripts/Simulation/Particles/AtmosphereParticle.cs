using UnityEngine;

namespace TapTap2026.Simulation
{
    // AtmosphereParticle —— 大气（气体标签，空气本身粒子化：风与对流、以及**湿度**的载体）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：四邻（浮力比温）、上下/斜向各 1 格、同行左右各 1 格
    // 交互的 ParticleIds：无 —— 只比温度，不特判类型
    //
    // 行为：浮力（比四邻热 → 升，冷 → 沉）→ 水平热压流（流向暖侧）。
    //       热由物质携带：空气 Swap 时热量跟着走，这就是对流；低空冷空气流向暖的陆地 = 海风。
    //       世界初始化时由 ParticleSimulationDriver 把所有空格填成大气，因此 typeId 0
    //       在运行中的世界里只作为「被抹除后留下的真空」短暂出现，行为规则同样允许气体换入。
    //
    //       这里**故意不做随机漂移**：那是数值噪声、不是风，而它搬运的是整格（含热量与湿度），
    //       会给云层注入逐 tick 的可见抖动（实测可见度翻转 178 → 57 每 tick）。气体的混合改由
    //       HumidityField 的扩散与温度定向平流负责——真实的混合是场的行为，不是随机换位。
    //
    // 湿度：云不存在于此类型里，也不存在于任何粒子类型里——云是**湿度场**（HumidityField）里的高值区。
    //       空气 Swap 时湿度跟着走（见 ParticleWorld.Swap），于是云随风平移、被湍流混合、自然连片；
    //       水蒸发往湿度场里加水汽（WaterParticle.Evaporate），低温饱和处由 ParticleWorld.StepRain 凝结成雨。
    //       因此本类型只管"空气怎么动"，不管云——这正是"云是空气的一种状态，不是一种粒子"。
    //
    // 命名说明：ParticleIds.Air 已被 typeId 0（空）占用，因此 id 字段叫 Atmosphere。
    public sealed class AtmosphereParticle : IParticleType
    {
        /// <summary>浮力/热压流的温度死区：与邻居温差小于它就不动，避免在均匀温度场里抖动。</summary>
        public const float BuoyancyEpsilon = 1f;

        // 调色板全透明：ParticlePixelBuffer 把 alpha 0 映射为背景色，大气不可见，它就是「天空」。
        // 云不靠这张调色板显示——ParticlePixelBuffer 按湿度值把格子染白（见 HumidityField）。
        // 热力叠加（HeatMap.DebugHeatOverlay）仍会把热格染红——隐形大气因此成了风的可视化层。
        private static readonly Color32[] Palette =
        {
            new Color32(158, 196, 228, 0),
            new Color32(164, 200, 230, 0),
            new Color32(170, 204, 232, 0),
            new Color32(176, 208, 234, 0),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Gas,
            density: 0.5f,
            heatCapacity: 1.0f,
            thermalConductivity: 1.0f,   // 大气：与 HeatMap.DefaultAirThermalConductivity 一致（气体传导最强）
            thermalEmissivity: 0.1f,     // 大气（N₂/O₂）对红外透明：气柱不逐格漏热，地表的热才能铺到网格顶
            affectedByGravity: false,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(AtmosphereParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            if (ParticleRules.TryBuoyantStep(world, x, y, BuoyancyEpsilon))
            {
                return;
            }

            if (ParticleRules.TryThermalDrift(world, x, y, BuoyancyEpsilon))
            {
                return;
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 大气被覆盖时无残留效果（固/液的湿气由 ParticleWorld.EvacuateHumidity 推给邻居）。
        }
    }
}
