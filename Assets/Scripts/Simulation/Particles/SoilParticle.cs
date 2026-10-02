using UnityEngine;

namespace TapTap2026.Simulation
{
    // SoilParticle —— 土壤（固体标签的粉末，摩擦度 0.5）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下 + 左下/右下（斜落净空由摩擦度换算：0.5 → 2 格）
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：与沙一样带 Solid 标签、受重力、只做「下落 + 斜落」，因此同样是粉末，
    //       在平地与同类上安分不动。与沙的唯一区别是摩擦度更高：
    //       土粒要连续 2 格净空才肯斜滑，45° 坡面上「落点正下方」被同坡粒子占住，土粒于是停住。
    //       结果沙堆是 1:1 的三角形（高 = 底/2），土堆是 2:1 的三角形（底长不变时高翻倍）。
    public sealed class SoilParticle : IParticleType
    {
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
            friction: 0.5f,
            palette: Palette,
            debugName: nameof(SoilParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0.5 → 2 格净空：正下被挡住时，落点下方还被顶着就视为稳定，
            // 所以坡面比沙的 45° 更陡，形成 2:1 的尖堆。
            ParticleRules.StepSolid(world, x, y);
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
