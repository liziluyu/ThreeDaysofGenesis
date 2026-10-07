using UnityEngine;

namespace TapTap2026.Simulation
{
    // TreeLeafParticle —— 树叶（固体 + Organic；长在原地，三种死法都在随机刻）
    //
    // flags 私有位：bit3-5 = 物种（CellFlags.TreeSpeciesMask，与种子/树干共享的「一格一类型」共享位）；
    //        bit6-7 本类型不用（保留 0）。
    // 邻居查询范围：正上 1 格（被压判定）、以本格为中心半径 SupportRadius 的 Chebyshev 环（找树干）。
    // 交互的 ParticleIds：TreeTrunk（支撑判据——整树倒下后树叶因找不到树干而枯落）、TreeLeaf（叠叶不算压）。
    //
    // 行为（OnRandomTick，随机刻；OnMotion / OnLogic 均为空体 → hasLogic: false）：
    //   ① 温度致死：本格温度 > 50（同草线）就死这一格；**低温致死取 0**（同水生植物）：
    //      环境温度可低到 0，树叶不应在夜晚冻死。
    //   ② 被压：正上方是固体（Solid 标签）且不是树叶 → 本格死亡（单格；液体不算压——树叶可以泡在水里，
    //      这一点与草的「液体也算压」有意不同）。
    //   ③ 失去支撑：以本格为中心、半径 = 本物种 SupportRadius 的 **Chebyshev 环由内向外扫描**，
    //      环内没有树干 → 本格死亡（命中即返回，内侧叶通常 1~2 环就命中）。
    //   三种死法都走 Clear → 世界按 20% 有机衰变就地留腐殖质（腐殖质受重力，落地后会把土壤改造成
    //   腐殖土——涌现的「落叶归根」）。
    public sealed class TreeLeafParticle : IParticleType
    {
        /// <summary>致死高温：与草同线（50）。</summary>
        public const float LethalHeatTemperature = 50f;

        /// <summary>致死低温：取 0（同水生植物）——环境温可低到 0，树叶不因寒冷死亡。</summary>
        public const float LethalColdTemperature = 0f;

        private static readonly Color32[] Palette =
        {
            // 物种分片（每物种 TreeSpecies.LeafColorsPerSpecies = 4 色；长度必须是 5×4 的整数倍）：
            // 杨（浅黄绿）/ 橡（深绿）/ 松（深蓝绿）/ 柏（灰绿）/ 柳（亮青绿）。
            new Color32(168, 196, 96, 255),
            new Color32(182, 210, 112, 255),
            new Color32(196, 222, 130, 255),
            new Color32(210, 234, 148, 255),
            new Color32(44, 92, 48, 255),
            new Color32(58, 110, 58, 255),
            new Color32(72, 128, 68, 255),
            new Color32(86, 146, 80, 255),
            new Color32(36, 84, 72, 255),
            new Color32(48, 100, 86, 255),
            new Color32(60, 118, 100, 255),
            new Color32(74, 136, 116, 255),
            new Color32(78, 104, 84, 255),
            new Color32(92, 120, 98, 255),
            new Color32(106, 136, 112, 255),
            new Color32(120, 152, 126, 255),
            new Color32(110, 170, 120, 255),
            new Color32(126, 188, 136, 255),
            new Color32(142, 204, 152, 255),
            new Color32(158, 220, 168, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic,
            density: 0.6f,
            heatCapacity: 0.80f,
            thermalConductivity: 0.60f,
            thermalEmissivity: 1.0f, // 近黑体
            affectedByGravity: false,
            isStatic: false,   // 必须非静态，否则收不到随机刻（与草同款取舍）
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(TreeLeafParticle),
            // 遮光率照抄草的 0.8（热学红线：稳态 T ≈ 58.8×Absorption，0.8 ⇒ ≈47° < 50° 致死线）。
            lightAbsorption: 0.8f,
            hasLogic: false);  // OnLogic 严格空体：死亡都在随机刻

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 树叶长在树上不动（非静态 + 空 OnMotion）。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 树叶的三种死法都在随机刻（数量多，不宜每逻辑帧全量扫描）。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // ① 温度致死。
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature > LethalHeatTemperature || temperature < LethalColdTemperature)
            {
                world.Clear(x, y);
                return;
            }

            // ② 被压：正上方是固体（含树干，不含树叶自己）就死；液体不压（树叶可以泡在水里）。
            if (world.GetCell(x, y + 1).typeId != ParticleIds.TreeLeaf
                && ParticleRules.IsSolid(world, x, y + 1))
            {
                world.Clear(x, y);
                return;
            }

            // ③ 失去支撑：支撑半径内找不到树干 → 枯落（整树被砍后树叶逐个落净）。
            int species = TreeSpecies.Read(world, x, y);
            if (!HasTrunkWithin(world, x, y, TreeSpecies.Info(species).SupportRadius))
            {
                world.Clear(x, y);
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 抽样；物种位与叶色片由创建方（树干造冠）调用
            // TreeSpecies.Stamp 盖上。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 树叶被覆盖时无残留效果。
        }

        /// <summary>
        /// 以 (x,y) 为中心、Chebyshev 环由内向外找树干：半径 <paramref name="radius"/> 内存在
        /// <see cref="ParticleIds.TreeTrunk"/> 即 true。命中即返回（内侧叶通常 1~2 环就命中）；
        /// 越界读空气，天然不算。
        /// </summary>
        private static bool HasTrunkWithin(ParticleWorld world, int x, int y, int radius)
        {
            for (int r = 1; r <= radius; r++)
            {
                // 上下两行（含角点）。
                for (int dx = -r; dx <= r; dx++)
                {
                    if (world.GetCell(x + dx, y + r).typeId == ParticleIds.TreeTrunk
                        || world.GetCell(x + dx, y - r).typeId == ParticleIds.TreeTrunk)
                    {
                        return true;
                    }
                }

                // 左右两列（去掉上下两行已扫的角点）。
                for (int dy = -r + 1; dy <= r - 1; dy++)
                {
                    if (world.GetCell(x - r, y + dy).typeId == ParticleIds.TreeTrunk
                        || world.GetCell(x + r, y + dy).typeId == ParticleIds.TreeTrunk)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
