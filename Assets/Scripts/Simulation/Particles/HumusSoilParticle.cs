using UnityEngine;

namespace TapTap2026.Simulation
{
    // HumusSoilParticle —— 腐殖土（固体标签；土壤吸了腐殖质后的形态）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：无（自身不读邻居）
    // 交互的 ParticleIds：无 —— 由 HumusParticle 转化 ParticleIds.Soil 生成，自身不再变化
    //
    // 行为：与干土同款粉末运动（ParticleRules.StepSolid，摩擦 / 粘性 0.5 ⇒ 约 3:1 安息角），
    //       没有相变 / 反应 / 随机刻——腐殖土是稳定的终态。它最显眼的是**颜色**：比干土、湿土都更黑
    //       （富有机质），是「土壤被腐殖质改良」的可见结果。
    //
    //       不可在编辑器中画出（只能由腐殖质转化土壤产生）。
    public sealed class HumusSoilParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(30, 24, 18, 255),
            new Color32(40, 32, 24, 255),
            new Color32(24, 19, 14, 255),
            new Color32(48, 38, 28, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.9f,   // 比干土（1.8）略重
            heatCapacity: 1.0f,
            thermalConductivity: 0.60f,
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(HumusSoilParticle));

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 腐殖土是稳定终态：无相变 / 反应。
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 与干土同款粉末运动：摩擦度 0.5 → 约 3:1 安息角。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 腐殖土无随机刻行为。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 腐殖土被覆盖时无残留效果。
        }
    }
}
