using UnityEngine;

namespace TapTap2026.Simulation
{
    // HumusParticle —— 腐殖质（固体；有机物的残体，短暂的中间态）
    //
    // flags 私有位：bit3-6 = 随机刻命中累计计数（CellFlags.HumusHitsMask）；bit7 空闲
    //       读用 HumusHitsMask、清用 HumusHitsClearMask、写用 (flags & ClearMask) | ((hits << Shift) & Mask)，
    //       绝不整体赋值 flags（会破坏 bit0 处理标记）。计数随 Cell 一起移动（Swap 搬整格）。
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
    //   运动（OnMotion，运动帧 60 Hz）：比水重（density 1.2；冰 0.92 < 水 1.0 < 腐殖质 1.2）：正下方是液体时
    //   **无条件** Swap 下沉一格——不受地下水闸门（HasMomentum）约束：闸门只拦「静止的矿质堆积被水顶起」，
    //   而腐殖质是明确的沉水残体，即使生成在水里、或支撑（如冰）融化成水后这一格从未运动过，也要沉底。
    //   否则照常走 ParticleRules.StepSolid（摩擦 / 粘性 0.5，堆形与土一致）；
    //   悬空时会先下落，落地后再找土壤——但逻辑帧先于同格运动，八邻有土时当帧就转化、不会先掉走。
    //
    //   腐殖质不可在编辑器中画出（它只能由有机物死亡产生），且寿命极短。
    //
    //   随机刻（OnRandomTick）：腐殖质每被随机刻命中一次就把本格 flags 里的累计计数 +1；
    //   第 DecayHits（10）次命中时直接 Clear 成空气（分解）。不摇骰、不消耗世界随机数，
    //   也不调用 WakeMotion（计数不参与 OnMotion 决策）。该分解路径与 OnLogic 的「被土壤吸收」
    //   互不替代：两者谁先发生谁生效，腐烂腐殖质即使一直没碰到土壤也会自行消失。
    //   计数随格子移动（flags 随 Swap / Set 拷贝），下落到新位置后继续累计。
    public sealed class HumusParticle : IParticleType
    {
        /// <summary>找土壤的搜索半径（八邻，即切比雪夫半径 1）。</summary>
        public const int SoilSearchRadius = 1;

        /// <summary>被随机刻命中多少次后分解（第 10 次命中即 <see cref="ParticleWorld.Clear"/>）。</summary>
        public const int DecayHits = 10;

        private static readonly Color32[] Palette =
        {
            new Color32(46, 34, 24, 255),
            new Color32(58, 44, 30, 255),
            new Color32(38, 28, 20, 255),
            new Color32(52, 38, 26, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.2f,   // 比水（1.0）重：沉入水底（冰 0.92 < 水 1.0 < 生物质 0.8 的反面参照）
            heatCapacity: 1.2f,   // 有机质比矿质土更扛热（含水/含碳）
            thermalConductivity: 0.30f,   // 腐殖质疏松、导热差
            thermalEmissivity: 1.0f,      // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: "腐殖质",
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
            // 腐殖质比水重：正下方是液体时**无条件**下沉一格（Swap），不受地下水闸门约束——
            // 闸门只约束「静止的矿质堆积不被水顶起」；腐殖质是明确的沉水残体，即使是在水里
            // 生成、或支撑（如冰）融化成水后从未运动过的那一格也要沉底。
            if (ParticleRules.HasTag(world, x, y - 1, ParticleTags.Liquid))
            {
                world.Swap(x, y, x, y - 1);
                return;
            }

            ParticleRules.StepSolid(world, x, y);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 累计随机刻命中次数（存在本格 flags 的 bit3-6）。不摇骰、不消耗世界随机数：
            // 随机流不随场上腐殖质数量漂移。
            int hits = ReadHits(world, x, y) + 1;
            if (hits >= DecayHits)
            {
                // 第 DecayHits 次命中：分解成空气。腐殖质不带 Organic 标签，Clear 不会再产生腐殖质。
                world.Clear(x, y);
                return;
            }

            WriteHits(world, x, y, hits);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 腐殖质被覆盖时无残留效果。
        }

        /// <summary>读本格的随机刻命中累计计数（<see cref="CellFlags.HumusHitsMask"/>）。</summary>
        private static int ReadHits(ParticleWorld world, int x, int y)
        {
            return (world.At(x, y).flags & CellFlags.HumusHitsMask) >> CellFlags.HumusHitsShift;
        }

        /// <summary>写本格的随机刻命中累计计数，<c>&amp; ~</c> / <c>|</c> 组合保留含 bit0 处理标记在内的其它位。</summary>
        private static void WriteHits(ParticleWorld world, int x, int y, int hits)
        {
            byte flags = (byte)((world.At(x, y).flags & CellFlags.HumusHitsClearMask)
                | ((hits << CellFlags.HumusHitsShift) & CellFlags.HumusHitsMask));
            world.At(x, y).flags = flags;
        }
    }
}
