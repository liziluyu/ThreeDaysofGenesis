using UnityEngine;

namespace TapTap2026.Simulation
{
    // SteamParticle —— 蒸汽（气体标签，温度驱动的浮力运动 + 冷凝成云）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：四邻（浮力比温）、上/斜上（上升）、下/斜下（下沉）、同行左右各 1 格
    // 交互的 ParticleIds：Cloud —— 本格温度 ≤ DewPoint 时冷凝成云滴（相变时手动携带热量）
    //
    // 行为：开头先查温度做相变；之后是气体运动——浮力（TryBuoyantStep：比四邻热就升、
    //       冷就沉）→ 水平热压流（TryThermalDrift：流向暖侧）→ 随机漂移。
    //       「气体」不等于「永远向上」：热蒸汽上升，冷蒸汽下沉，这是海陆风与对流的发动机。
    public sealed class SteamParticle : IParticleType
    {
        /// <summary>露点：本格温度降到它就冷凝成云滴。与沸点 60 保持 15° 间距，循环路径短、节奏快。</summary>
        public const float DewPoint = 45f;

        /// <summary>浮力/热压流的温度死区：与邻居温差小于它就不动，避免在均匀温度场里抖动。</summary>
        public const float BuoyancyEpsilon = 1f;

        private static readonly Color32[] Palette =
        {
            new Color32(180, 200, 224, 255),
            new Color32(190, 208, 230, 255),
            new Color32(200, 216, 236, 255),
            new Color32(210, 224, 240, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Gas,
            density: 0.6f,
            heatCapacity: 2.0f,
            affectedByGravity: false,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(SteamParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 相变优先于运动。Set 换类型会把热量重置为环境值（契约 §6：热由物质携带），
            // 先把 Q 抄下来、变完再写回——云滴带着蒸汽的（冷）热量出生。
            float heat = world.Heat.GetHeat(x, y);
            if (world.Heat.GetTemperature(x, y) <= DewPoint)
            {
                world.Set(x, y, ParticleIds.Cloud);
                world.Heat.SetHeat(x, y, heat);
                return;
            }

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
            // 蒸汽被覆盖时无残留效果。
        }
    }
}
