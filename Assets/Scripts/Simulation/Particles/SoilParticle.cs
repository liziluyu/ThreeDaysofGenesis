using UnityEngine;

namespace TapTap2026.Simulation
{
    // SoilParticle —— 土壤（固体标签的粉末，摩擦度 0.5 + 粘性 0.5）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下；尖顶磨平再看正上 + 同排左右
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：带 Solid 标签、受重力、只做「下落 + 斜落 + 磨平尖顶」，在平地与同类上安分不动。
    //         - 摩擦度 0.5：斜落要连续 2 格净空 ⇒ 侧翼是 2:1（约 63°），比沙的 45° 陡；
    //         - 粘性 0.5：孤立的 1 格宽尖顶会被推倒 ⇒ 顶部塌成 2~4 格宽的小平顶，不再是一根尖刺。
    //       于是土堆是「2:1 的陡坡 + 平头」，像一坨成块的湿土，而不是沙那种尖锥。
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
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(SoilParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0.5 → 2 格净空（侧翼 2:1）；粘性 0.5 → 孤立的 1 格宽尖顶会被推倒，顶部成小平顶。
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
