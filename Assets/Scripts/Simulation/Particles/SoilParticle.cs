using UnityEngine;

namespace TapTap2026.Simulation
{
    // SoilParticle —— 土壤（固体标签的粉末，摩擦度 0.5 + 粘性 0.5）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下、以及斜滑反侧同一行（粘性判据：旁边有没有固体）
    // 交互的 ParticleIds：无 —— 全部走 ParticleRules 的标签规则，不特判具体类型
    //
    // 行为：带 Solid 标签、受重力、只做「下落 + 斜落」，因此同样是粉末，在平地与同类上安分不动。
    //       与沙的区别是两条规则叠加：
    //         - 摩擦度 0.5：土粒要连续 2 格净空才肯斜滑（落点正下方被顶着就停），单独作用时是 2:1 的堆；
    //         - 粘性 0.5：旁边有固体邻居（斜滑反侧同一行）时，安息角再抬一档到 3 格净空，堆体约 3:1；
    //                     孤立、旁边没粒子的土粒不受粘性影响，仍按 2 格净空滑落。
    //       于是土堆明显比 1:1 的沙堆陡，又远不及 10:1 的石头堆——「成块的土」而不是「滑溜的沙」。
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
            heatCapacity: 1.5f,   // 湿土：热容大，升温慢、保温久（对齐现实比热容 ~1.5）
            thermalConductivity: 0.5f,   // 湿土：导热中等（保温 → 内部温差明显）
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(SoilParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0.5 → 2 格净空；旁边有固体邻居时粘性 0.5 再抬一档到 3 格：
            // 正下被挡住时若落点下方还被顶着就视为稳定，于是坡面比沙的 45° 陡得多，约 3:1。
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
