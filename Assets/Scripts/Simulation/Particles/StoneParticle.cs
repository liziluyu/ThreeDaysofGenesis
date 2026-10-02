using UnityEngine;

namespace TapTap2026.Simulation
{
    // StoneParticle —— 石头（固体标签，摩擦度 0.9，10:1 的陡尖堆）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下 + 左下/右下（斜落净空由摩擦度换算：0.9 → 10 格）
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：带 Solid 标签且受重力。正下可进入就下落；否则尝试斜下，但斜落要 10 格净空
    //       （落点起连续 10 格都能进入）。于是堆体相邻列的高差最多 10 —— 安息角 10:1（约 84°），
    //       比沙的 1:1、土的 2:1 陡得多；同时又和它们一样会自己塌成堆，而不是垒成直柱。
    //       它也不是 IsStatic——静态类型（墙体）永不参与扫描。
    public sealed class StoneParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(104, 104, 104, 255),
            new Color32(118, 118, 118, 255),
            new Color32(132, 132, 132, 255),
            new Color32(146, 146, 146, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 3.0f,
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.9f,
            palette: Palette,
            debugName: nameof(StoneParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 先试正下：正下是 Solid 标签就停住（同类石头、沙、将来的其它固体都算），
            // 是液体/气体/空气则落穿它。摩擦度 0.9 → 10 格净空才肯斜落，
            // 意味着「落点往下 10 格内有东西顶着」时视为稳定，坡面因此能站在 10:1 的陡度上。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 石头被覆盖时无残留效果。
        }
    }
}
