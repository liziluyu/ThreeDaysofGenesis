using UnityEngine;

namespace TapTap2026.Simulation
{
    // SoilParticle —— 土壤（固体标签的粉末，安息角比沙陡一倍）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下 + 左下/右下（斜落要求落点起连续 2 格净空）
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：与沙一样带 Solid 标签、受重力、只做「下落 + 斜落」，因此同样是粉末，
    //       在平地与同类上安分不动。与沙的唯一区别在斜落的净空要求：
    //       土粒要连续 2 格净空才肯斜滑，45° 坡面上「落点正下方」被同坡粒子占住，土粒于是停住。
    //       结果沙堆是 1:1 的三角形（高 = 底/2），土堆是 2:1 的三角形（底长不变时高翻倍）。
    public sealed class SoilParticle : IParticleType
    {
        /// <summary>
        /// 斜落需要的连续净空格数（见 <see cref="ParticleRules.TryDiagonalFall"/>）：
        /// 1 = 45° 安息角（沙），2 ≈ 63°（土，2 格高 : 1 格宽）。调大即更陡，直到完全不失稳。
        /// </summary>
        private const int SlideClearance = 2;

        private static readonly Color32[] Palette =
        {
            new Color32(86, 62, 40, 255),
            new Color32(105, 78, 50, 255),
            new Color32(124, 96, 64, 255),
            new Color32(143, 114, 80, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.8f,
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            palette: Palette,
            debugName: nameof(SoilParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 与沙一致：单一决策，正下 -> 斜下，至多移动一次后立即 return。
            if (ParticleRules.TryFall(world, x, y))
            {
                return;
            }

            // 正下被挡住才斜落；2 格净空意味着「落点下方还被顶着」时视为稳定，
            // 所以坡面比沙的 45° 更陡，形成 2:1 的尖堆。
            ParticleRules.TryDiagonalFall(world, x, y, SlideClearance);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 土被覆盖时无残留效果。
        }
    }
}
