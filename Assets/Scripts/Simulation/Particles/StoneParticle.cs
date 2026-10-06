using UnityEngine;

namespace TapTap2026.Simulation
{
    // StoneParticle —— 石头（固体标签，摩擦度 0.9 + 高粘性 0.9）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下 + 左下/右下 + 斜滑反侧同一行（粘性判据：旁边有没有固体）
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：带 Solid 标签且受重力。正下可进入就下落；否则尝试斜下：
    //         - 摩擦度 0.9：斜落要连续 10 格净空（单独作用时是 10:1，约 84°）；
    //         - 粘性 0.9：坡面外侧（斜滑反侧同排有固体、也就是「靠着一整块石头」）时净空再加 9 格
    //                     到 19 格 —— 外侧几乎滑不动，于是石头站成近乎垂直的垛，而不是塌成锥。
    //       孤立、旁边没石头的石块不受粘性影响，仍按 10 格净空滚落。
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
            heatCapacity: 0.80f,   // 石头：从 0.9 调到 0.8，升温更快
            thermalConductivity: 0.80f,   // 石头：固体里导热最快（原 0.7，接近水）
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.9f,
            stickiness: 0.9f,
            palette: Palette,
            debugName: nameof(StoneParticle),
            // 运动阅读盒：TrySlope 的净空链。粘住（同排反侧有固体）时
            // clearance = 1/(1−0.9) + 1/(1−0.9) − 1 = 19，TrySlide 逐格读 (x ± 1, y−1 … y−19)
            // 的 _cells；不粘住时为 10。水平只读斜滑那一侧（X = 1）、向上不读（Up = 0）。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 19));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0.9 → 10 格净空；靠着同排的石头（坡面外侧）时粘性 0.9 再抬到 19 格，
            // 所以石头垛外侧几乎不滚落，只有孤立、旁边没石头的石块才按 10 格净空滑下去。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 石头只有运动与热学，没有相变 / 反应：逻辑帧留空。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 石头暂无随机刻行为：它只有运动与热学，慢过程（风化等）尚未设计。
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
