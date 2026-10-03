using UnityEngine;

namespace TapTap2026.Simulation
{
    // SandParticle —— 沙（固体标签的粉末，摩擦度 0、无粘性）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下、右下（斜落净空由摩擦度换算：0 → 1 格）
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：带 Solid 标签，运动完全由 ParticleRules.StepSolid 按 Info.Friction 驱动。
    //       摩擦度 0 = 最松的 1:1 安息角（45°），因此堆积会形成三角形沙堆。
    //       沙与沙同属 Solid，互不置换，所以在平地上安分不动、只在坡面斜滑。
    public sealed class SandParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(194, 178, 128, 255),
            new Color32(208, 190, 140, 255),
            new Color32(222, 204, 152, 255),
            new Color32(236, 218, 168, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.5f,
            heatCapacity: 0.8f,   // 干沙：热容小，晒热得快、凉得也快
            thermalConductivity: 0.4f,   // 干沙：导热最差，热憋在表层（最保温）
            thermalEmissivity: 1.0f,     // 沙/岩/土都是近黑体，辐射率取 1（与旧行为一致）
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(SandParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0：正下 -> 1:1 斜下，单一决策，至多移动一次后立即 return。
            // 不做横向自由流动：粉末的安息角完全由斜下滑落产生，
            //   - 平地：正下与两条斜下都是 Solid，静止；
            //   - 坡面：堆顶的沙沿坡斜滑，自然收敛成三角形。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 沙被覆盖时无残留效果。
        }
    }
}
