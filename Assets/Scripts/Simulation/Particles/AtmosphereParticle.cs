using UnityEngine;

namespace TapTap2026.Simulation
{
    // AtmosphereParticle —— 大气（气体标签，空气本身粒子化：风与对流的载体）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：四邻（浮力比温）、上下/斜向各 1 格、同行左右各 1 格
    // 交互的 ParticleIds：无 —— 只比温度，不特判类型
    //
    // 行为：浮力（比四邻热 → 升，冷 → 沉）→ 水平热压流（流向暖侧）→ 随机漂移。
    //       热由物质携带：空气 Swap 时热量跟着走，这就是对流；低空冷空气流向暖的陆地 = 海风。
    //       世界初始化时由 ParticleSimulationDriver 把所有空格填成大气，因此 typeId 0
    //       在运行中的世界里只作为「被抹除后留下的真空」短暂出现，行为规则同样允许气体换入。
    //
    // 命名说明：ParticleIds.Air 已被 typeId 0（空）占用，因此 id 字段叫 Atmosphere。
    public sealed class AtmosphereParticle : IParticleType
    {
        /// <summary>浮力/热压流的温度死区：与邻居温差小于它就不动，避免在均匀温度场里抖动。</summary>
        public const float BuoyancyEpsilon = 1f;

        // 调色板全透明：ParticlePixelBuffer 把 alpha 0 映射为背景色，大气不可见，它就是「天空」。
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

            ParticleRules.TryRandomDrift(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 大气被覆盖时无残留效果。
        }
    }
}
