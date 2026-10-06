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
            heatCapacity: 0.70f,   // 干沙：固体里热容最小，晒热得最快、凉得也最快
            thermalConductivity: 0.50f,   // 干沙：仍是固体里导热最差，但已调快（原 0.4）
            thermalEmissivity: 1.0f,     // 沙/岩/土都是近黑体，辐射率取 1（与旧行为一致）
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(SandParticle),
            // 运动阅读盒：摩擦/粘性 0 → 净空 1，只读正下 1 格与斜下 1 格 ⇒ (1, 0, 1)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 1),
            hasLogic: false); // OnLogic 严格空体：沙只有运动

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0：正下 -> 1:1 斜下，单一决策，至多移动一次后立即 return。
            // 不做横向自由流动：粉末的安息角完全由斜下滑落产生，
            //   - 平地：正下与两条斜下都是 Solid，静止；
            //   - 坡面：堆顶的沙沿坡斜滑，自然收敛成三角形。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 沙只有运动，没有相变 / 反应：逻辑帧留空。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 沙暂无随机刻行为：它只有运动，慢过程（固结等）尚未设计。
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
