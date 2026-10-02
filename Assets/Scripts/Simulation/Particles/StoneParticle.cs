using UnityEngine;

namespace TapTap2026.Simulation
{
    // StoneParticle —— 石头（固体标签，竖直堆叠）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：仅正下
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：带 Solid 标签且受重力。正下是 Solid 标签的粒子时停下；否则下落。
    //       与沙的区别只有一点：**不做斜下滑落**，所以石头会竖直垒成柱子，
    //       而不是像沙那样摊成三角形。它也不是 IsStatic——静态类型（墙体）永不参与扫描。
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
            palette: Palette,
            debugName: nameof(StoneParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 只试正下：正下是 Solid 标签就停住（同类石头、沙、将来的其它固体都算），
            // 是液体/气体/空气则落穿它。没有斜下分支 = 保持竖直，不侧滑。
            ParticleRules.TryFall(world, x, y);
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
