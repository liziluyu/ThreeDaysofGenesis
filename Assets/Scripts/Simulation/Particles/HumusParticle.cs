using UnityEngine;

namespace TapTap2026.Simulation
{
    // HumusParticle —— 腐殖质（固体；有机物的残体，短暂的中间态）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：八邻（3×3 去自身）——找第一个土壤
    // 交互的 ParticleIds：Soil → HumusSoil（把土壤变成腐殖土）
    //
    // 来源：任何带 ParticleTags.Organic 标签的粒子**死亡**（被其它类型覆盖或抹除）时，由
    //       ParticleWorld 按 OrganicDecayDenominator（1/5 = 20%）的概率就地把那一格变成腐殖质。
    //
    // 行为（都在逻辑帧 OnLogic，20 Hz）：
    //   以自身为中心、八邻内找**第一个土壤**（ParticleIds.Soil，固定扫描序：y 升序、同一行 x 升序），
    //   找到就把那格 SetPhase 成腐殖土（HumusSoil），然后自己消失（Clear → 空气）——腐殖质被土壤吸收，
    //   只在世上停留一帧。八邻内没有土壤时保持不动，等下一逻辑帧再试。
    //   运动（OnMotion，运动帧 60 Hz）：受重力的固体，走 ParticleRules.StepSolid（摩擦 / 粘性 0.5，堆形与土一致）；
    //   悬空时会先下落，落地后再找土壤——但逻辑帧先于同格运动，八邻有土时当帧就转化、不会先掉走。
    //
    //   腐殖质不可在编辑器中画出（它只能由有机物死亡产生），且寿命极短。
    public sealed class HumusParticle : IParticleType
    {
        /// <summary>找土壤的搜索半径（八邻，即切比雪夫半径 1）。</summary>
        public const int SoilSearchRadius = 1;

        private static readonly Color32[] Palette =
        {
            new Color32(46, 34, 24, 255),
            new Color32(58, 44, 30, 255),
            new Color32(38, 28, 20, 255),
            new Color32(52, 38, 26, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.0f,
            heatCapacity: 1.2f,   // 有机质比矿质土更扛热（含水/含碳）
            thermalConductivity: 0.30f,   // 腐殖质疏松、导热差
            thermalEmissivity: 1.0f,      // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(HumusParticle),
            // 运动阅读盒：0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3));

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            for (int dy = -SoilSearchRadius; dy <= SoilSearchRadius; dy++)
            {
                for (int dx = -SoilSearchRadius; dx <= SoilSearchRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (world.GetCell(x + dx, y + dy).typeId != ParticleIds.Soil)
                    {
                        continue;
                    }

                    // 同一份物质换形态：土壤 → 腐殖土，热量 Q 留在该格。
                    world.SetPhase(x + dx, y + dy, ParticleIds.HumusSoil);

                    // 腐殖质被土壤吸收：自己消失（Clear → 空气）。
                    world.Clear(x, y);
                    return;
                }
            }

            // 八邻没有土壤：保持不动，等下一逻辑帧再试（悬空时会先按固体下落）。
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 腐殖质不参与随机刻：转化是每逻辑帧的即时反应，不是慢过程。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 腐殖质被覆盖时无残留效果。
        }
    }
}
