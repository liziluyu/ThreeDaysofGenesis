using UnityEngine;

namespace TapTap2026.Simulation
{
    // TreeLeafParticle —— 树叶（固体 + Organic；附着时定植原地，失活后竖直自由下落）
    //
    // flags 私有位：bit3-5 = 物种（CellFlags.TreeSpeciesMask，与种子 / 树干共享的「一格一类型」共享位）；
    //        bit6   = 失活标记（CellFlags.TreeLeafDetachedMask；与树干生长阶段 / 树木种子空转计数共用
    //                 bit6-7 这一组，按「一格一类型」互斥）。
    // 邻居查询范围：正上 1 格（被压判定）、以本格为中心半径 SupportRadius 的 Chebyshev 环（找树干）、
    //               八邻一圈（失活扩散）。OnMotion 只读正下 1 格 —— 默认运动阅读盒 (1,1,1) 覆盖。
    // 交互的 ParticleIds：TreeTrunk（支撑判据——整树倒下后树叶失活）、TreeLeaf（叠叶不算压 / 失活扩散）。
    // 对外入口：TreeTrunkParticle 的「失去扎根」级联在清完整段树干后逐格调 NotifyTrunkRemoved——
    //           即使随机刻关闭（RandomTicksPerStep = 0），砍树 / 挖根后整冠也会在同一次逻辑帧里
    //           集体失活（随后靠运动帧落叶），不会永久悬空。
    //
    // 行为：
    //   ① OnRandomTick（随机刻；OnLogic 空体 → hasLogic: false）：
    //      - 已失活 → Clear 死亡。失活叶只在「**再**被随机刻点到」时才死（不因覆盖 / 高温提前消失，
    //        落稳后仍是一张叶子，等自己那次随机刻收尸）。
    //      - 温度致死：本格温度 > 50（同草线）；**低温致死取 0**（同水生植物）：环境温度可低到 0，
    //        树叶不应在夜晚冻死。
    //      - 被压：正上方是固体（Solid 标签）且不是树叶 → 本格死亡（单格；液体不算压——树叶可以泡在水里，
    //        这一点与草的「液体也算压」有意不同）。
    //      - 失去支撑：以本格为中心、半径 = 本物种 SupportRadius 的 **Chebyshev 环由内向外扫描**，
    //        环内没有树干 → **不再立即死**，而是记录失活（flags bit6）并**立刻**让八邻一圈的树叶各自
    //        检测支撑——无支撑者同样失活、继续向外扩散：砍掉树干后整冠在一瞬间集体失活（不是逐片等
    //        随机刻），随后一起落叶。
    //      - 同一条支撑判据也由**树干级联倒下**主动触发（静态入口 NotifyTrunkRemoved，见上「对外入口」），
    //        因此整冠失活不依赖随机刻是否开启。
    //   ② OnMotion（运动帧 60 Hz）：失活的树叶竖直**自由下落**一格——**不参与滚落**（不做斜滑 / 横流），
    //      正下方是固体就停（空气 / 气体 / 液体都照落）。每帧至多移动一格。
    //   ③ 死亡：只发生在随机刻。失活叶落稳后（或下落途中）被随机刻命中才 Clear → 世界按 20% 有机衰变
    //      就地留腐殖质（腐殖质受重力，落地后会把土壤改造成腐殖土——涌现的「落叶归根」）。
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
            affectedByGravity: true,  // 仅**失活**叶据此自由下落；附着叶在 OnMotion 里直接返回（见类型头注释）
            isStatic: false,   // 必须非静态，否则收不到随机刻 / 运动帧（与草同款取舍）
            maxFlowSpread: 0,
            friction: 0f,      // 不走 ParticleRules.StepSolid（失活叶只竖直下落、不滚落），两值仅占位
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(TreeLeafParticle),
            // 遮光率照抄草的 0.8（热学红线：稳态 T ≈ 58.8×Absorption，0.8 ⇒ ≈47° < 50° 致死线）。
            lightAbsorption: 0.8f,
            hasLogic: false);  // OnLogic 严格空体：失活 / 死亡都在随机刻

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            if (!IsDetached(world, x, y))
            {
                return; // 附着在树上的叶长在原地（非静态 + 空决策）。
            }

            int belowY = y - 1;
            if (!world.IsInBounds(x, belowY) || ParticleRules.IsSolid(world, x, belowY))
            {
                return; // 触底 / 落在固体上：停止下落（不滚落）。
            }

            // 自由下落：只竖直换位一格；正下方非固体（空气 / 气体 / 液体）即落，落到固体上才停。
            world.Swap(x, y, x, belowY);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 树叶的失活与死亡都在随机刻（数量多，不宜每逻辑帧全量扫描支撑）。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 已失活：这一次随机刻就是它的死期（失活标记是本格唯一的 per-cell 状态，死亡走 Clear）。
            if (IsDetached(world, x, y))
            {
                world.Clear(x, y);
                return;
            }

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

            // ③ 失去支撑：支撑半径内找不到树干 → 失活并向八邻一圈扩散（不立即死，等随机刻收尸）。
            int species = TreeSpecies.Read(world, x, y);
            if (!HasTrunkWithin(world, x, y, TreeSpecies.Info(species).SupportRadius))
            {
                DetachAndSpread(world, x, y);
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 抽样；物种位与叶色片由创建方（树干造冠）调用
            // TreeSpecies.Stamp 盖上。flags 由 Set 整体写成边沿标记 → 新叶恒为「附着」（失活位为 0）。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 树叶被覆盖时无残留效果。
        }

        /// <summary>
        /// 树干被移除后主动通知 (x,y) 八邻一圈的树叶检查支撑（<see cref="TreeTrunkParticle"/> 的
        /// 失去扎根级联在**清完整段树干之后**逐格调用）：对每个「树叶且未失活」的格读物种、跑支撑检查，
        /// 无支撑则调 <see cref="DetachAndSpread"/>——递归会沿树冠自支撑链扩散到整冠，无需扩大半径。
        /// 无分配、不消耗随机流、不读温度；越界读空气，天然跳过。
        /// </summary>
        public static void NotifyTrunkRemoved(ParticleWorld world, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int nx = x + dx;
                    int ny = y + dy;
                    if (world.GetCell(nx, ny).typeId != ParticleIds.TreeLeaf || IsDetached(world, nx, ny))
                    {
                        continue;
                    }

                    int species = TreeSpecies.Read(world, nx, ny);
                    if (!HasTrunkWithin(world, nx, ny, TreeSpecies.Info(species).SupportRadius))
                    {
                        DetachAndSpread(world, nx, ny);
                    }
                }
            }
        }

        /// <summary>本格是否已失活（flags bit6）。越界读空气格 → false。</summary>
        private static bool IsDetached(ParticleWorld world, int x, int y)
        {
            return (world.GetCell(x, y).flags & CellFlags.TreeLeafDetachedMask) != 0;
        }

        /// <summary>
        /// 把 (x,y) 标记为失活，并**立刻**让八邻一圈的树叶各自检测支撑：无支撑者同样标记、继续向外扩散
        /// （递归即「周围一圈立刻检测」的级联——砍掉树干后整冠一瞬间集体失活）。已在标记中的格直接返回，
        /// 扩散不会绕圈死循环。
        /// </summary>
        /// <remarks>
        /// 每个被标记的格都请求一次运动唤醒（<see cref="ParticleWorld.WakeMotion"/>）：失活是在随机刻
        /// （扫描之外）改的 flags，若不唤醒，运动休眠会把这格当成未变化而跳过，叶子会永远挂在原地。
        /// 递归只走本格周围一圈、深度 ≤ 冠格数（最大 52），无分配、不消耗随机流。
        /// </remarks>
        private static void DetachAndSpread(ParticleWorld world, int x, int y)
        {
            if (IsDetached(world, x, y))
            {
                return;
            }

            world.At(x, y).flags |= CellFlags.TreeLeafDetachedMask;
            world.WakeMotion(x, y);

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int nx = x + dx;
                    int ny = y + dy;
                    if (world.GetCell(nx, ny).typeId != ParticleIds.TreeLeaf || IsDetached(world, nx, ny))
                    {
                        continue; // 越界读空气，天然跳过；已标记的格不重复扩散。
                    }

                    int species = TreeSpecies.Read(world, nx, ny);
                    if (!HasTrunkWithin(world, nx, ny, TreeSpecies.Info(species).SupportRadius))
                    {
                        DetachAndSpread(world, nx, ny);
                    }
                }
            }
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
