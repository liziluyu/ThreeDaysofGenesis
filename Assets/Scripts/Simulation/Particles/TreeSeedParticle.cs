using UnityEngine;

namespace TapTap2026.Simulation
{
    // TreeSeedParticle —— 树木种子（受重力的固体粉末；随机刻命中且「落土 + 物种基质档」满足时按物种概率发芽）
    //
    // flags 私有位：bit3-5 = 物种（CellFlags.TreeSpeciesMask，与树干/树叶共用的「一格一类型」共享位）；
    //        bit6-7 = 「被随机刻命中但没有发芽条件」的**连续**计数（CellFlags.TreeSeedStrikeMask，
    //        0..MaxStrikesBeforeHumus；发芽条件满足的分支会把它清零——「连续」语义即由此保证）。
    //        读用掩码、清用 ClearMask、写用 | 组合，绝不整体赋值
    //        flags（会破坏 bit0 处理标记）。
    // 邻居查询范围：正下 1 格（基质）、正上 1 格（空间）、八邻 3×3（仅 Moist 档查湿土 / 腐殖土）；
    //               运动为 StepSolid 的「正下、左下/右下、斜滑反侧同一行」。
    // 交互的 ParticleIds：Soil / WetSoil / HumusSoil（基质档判据）、TreeTrunk（发芽成的形态）、
    //                     Humus（连续 3 次无发芽条件时霉烂成它）。
    //
    // 行为：
    //   运动（OnMotion，运动帧 60 Hz）：ParticleRules.StepSolid（摩擦/粘性 0.5，堆形与土一致）。
    //   发芽（OnRandomTick，随机刻；比草种子慢、按物种分级，见 TreeSpecies.SubstrateTier 与
    //   GerminationDenominator）：
    //     ① 先判断（不消耗随机数）：正上方必须非固非液（ParticleRules.BlocksLight），再加基质档——
    //        耐旱（松/柏）：正下方是三土任一；喜湿（杨/橡）：三土且八邻 3×3 内有湿土或腐殖土；
    //        最喜水（柳）：正下方必须恰为湿土或腐殖土（腐殖土家族算够湿）。
    //     ② 不满足 → 打击计数 +1；**连续** MaxStrikesBeforeHumus（=3）次都不满足才 SetPhase 成腐殖质
    //        （与草种子 / 水生态种子同款霉烂规则）。
    //     ③ 满足 → **先把打击计数清零**（条件性写入：仅当计数 ≠ 0 时才写 flags，不消耗随机流；
    //        「连续」语义——中途一旦满足条件，之前累计的空转全部作废），再先查后掷
    //        world.NextUInt() % D == 0 才发芽：SetPhase 成树干后**立刻**
    //        TreeSpecies.Stamp 重写物种位与树皮色片（SetPhase 会把 flags 清成边沿标记）。
    //        掷骰未中不计打击、原地等待——因此发芽时间 ≥ 2 倍草种子的「命中即发」。
    //     ④ 腐殖土消耗（优待的代价）：腐殖土家族是本次发芽的**决定性墒情**时才消耗——最喜水（柳）正下方是
    //        **干腐殖土**（HumusSoil，非真湿土）；喜湿（杨/橡）八邻没有真湿土（WetSoil / WetHumusSoil）
    //        而靠腐殖土满足了加湿判据；耐旱（松/柏）从不依赖墒情、永不消耗。消耗同样在发芽成功后以种子格
    //        (x,y) 为起点调用 HumusSoilParticle.ConsumeFarthestBelow，把脚下连续土层里**最深**的一格腐殖土
    //        退化成普通土（HumusSoil → Soil、WetHumusSoil → WetSoil，SetPhase 保水保热）。被换成普通土的
    //        腐殖土若因此不再满足基质档属正常（先发芽、后消耗）。
    //   落草自解：种子是固体，停在草上会触发草自身的「被压」死亡（既有世界规则），种子随之下沉到
    //   土面再走上面的发芽路径，无需特判草。
    public sealed class TreeSeedParticle : IParticleType
    {
        /// <summary>连续多少次「被随机刻命中但没有发芽条件」后，种子霉烂成腐殖质。</summary>
        public const int MaxStrikesBeforeHumus = 3;

        private static readonly Color32[] Palette =
        {
            // 物种分片（每物种 TreeSpecies.SeedColorsPerSpecies = 2 色；长度必须是 5×2 的整数倍）：
            // 杨 / 橡 / 松 / 柏 / 柳。
            new Color32(140, 130, 96, 255),
            new Color32(160, 150, 112, 255),
            new Color32(110, 88, 56, 255),
            new Color32(130, 104, 68, 255),
            new Color32(92, 74, 52, 255),
            new Color32(110, 90, 64, 255),
            new Color32(86, 80, 66, 255),
            new Color32(104, 96, 80, 255),
            new Color32(100, 86, 62, 255),
            new Color32(120, 104, 76, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic,
            density: 1.2f,
            heatCapacity: 0.75f,
            thermalConductivity: 0.55f,
            thermalEmissivity: 1.0f, // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,   // 与土同档：种子落地成堆、不滚远
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(TreeSeedParticle),
            // 运动阅读盒：0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)（与草种子一致）。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3),
            hasLogic: false); // OnLogic 严格空体：发芽在随机刻、运动在 OnMotion

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 树木种子只有运动与随机刻发芽，没有每逻辑帧的相变 / 反应。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // ① 先读物种：SetPhase 会把 flags 清成边沿标记，物种必须先取出来。
            int species = TreeSpecies.Read(world, x, y);

            // ② 先判断后摇骰：条件不满足走打击路径（零随机数消耗）。
            if (!CanGerminate(world, x, y, species))
            {
                int strikes = ReadStrikes(world, x, y) + 1;
                if (strikes >= MaxStrikesBeforeHumus)
                {
                    // 同一份物质换形态：种子 → 腐殖质（腐殖质非有机，不再触发有机衰变）。
                    world.SetPhase(x, y, ParticleIds.Humus);
                    return;
                }

                WriteStrikes(world, x, y, strikes);
                return;
            }

            // ③ 条件满足先把「连续空转」计数清零（条件性写入、不消耗随机流），再摇发芽骰。
            if (ReadStrikes(world, x, y) != 0)
            {
                WriteStrikes(world, x, y, 0);
            }

            if (world.NextUInt() % TreeSpecies.Info(species).GerminationDenominator != 0u)
            {
                return;
            }

            // ④ 同一份物质换形态（树干，热量留在原格），随后立刻重写物种位与树皮色片。
            world.SetPhase(x, y, ParticleIds.TreeTrunk);
            TreeSpecies.Stamp(world, x, y, species, TreeSpecies.TrunkColorsPerSpecies);

            // ⑤ 腐殖土消耗（优待的代价）：仅在腐殖土家族是本次发芽的决定性墒情时消耗——最喜水（柳）正下方是
            // 干腐殖土（HumusSoil，非真湿土）；喜湿（杨/橡）八邻无真湿土（靠腐殖土满足加湿判据）；耐旱
            // （松/柏）从不依赖墒情。此刻 (x,y) 已是树干，helper 从 (x, y-1) 起竖直向下扫土层，正确。
            bool humusDecisive;
            switch (TreeSpecies.Info(species).Substrate)
            {
                case TreeSpecies.SubstrateTier.Wet:
                    humusDecisive = world.GetCell(x, y - 1).typeId == ParticleIds.HumusSoil;
                    break;
                case TreeSpecies.SubstrateTier.Moist:
                    humusDecisive = !HasTrueWetSoilNeighbour(world, x, y);
                    break;
                default: // 耐旱：正下方是三土任一即可，从不依赖墒情。
                    humusDecisive = false;
                    break;
            }

            if (humusDecisive)
            {
                HumusSoilParticle.ConsumeFarthestBelow(world, x, y);
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 按本类型调色板抽样；物种位与色片由创建方
            // （发芽路径或 ParticleEditor 投放）调用 TreeSpecies.Stamp 盖上——OnSpawn 里拿不到物种。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 树木种子被覆盖时无残留效果。
        }

        /// <summary>
        /// 发芽条件（先判断、零随机数）：正上方非固非液 + 正下方按物种基质档。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不满足「落土」。
        /// </summary>
        private static bool CanGerminate(ParticleWorld world, int x, int y, int species)
        {
            // 空间门：与树干生长门一致——避免在石头下 / 水下发芽成一个永远长不动的木桩
            // （真发生了会走打击 → 霉烂路径），也挡住网格顶行。
            if (!world.IsInBounds(x, y + 1) || ParticleRules.BlocksLight(world, x, y + 1))
            {
                return false;
            }

            ushort below = world.GetCell(x, y - 1).typeId;
            bool soilBelow = below == ParticleIds.Soil || below == ParticleIds.WetSoil
                || below == ParticleIds.HumusSoil || below == ParticleIds.WetHumusSoil;

            switch (TreeSpecies.Info(species).Substrate)
            {
                case TreeSpecies.SubstrateTier.Wet:
                    // 腐殖土家族优待：干腐殖土也算够湿（此前只认湿土）。
                    return below == ParticleIds.WetSoil || below == ParticleIds.WetHumusSoil
                        || below == ParticleIds.HumusSoil;
                case TreeSpecies.SubstrateTier.Drought:
                    return soilBelow;
                default:
                    return soilBelow && HasWetSoilNeighbour(world, x, y);
            }
        }

        /// <summary>读本格的「空转」计数（<see cref="CellFlags.TreeSeedStrikeMask"/>）。</summary>
        private static int ReadStrikes(ParticleWorld world, int x, int y)
        {
            return (world.At(x, y).flags & CellFlags.TreeSeedStrikeMask) >> CellFlags.TreeSeedStrikeShift;
        }

        /// <summary>写本格的「空转」计数，<c>&amp; ~</c> / <c>|</c> 组合保留含 bit0 在内的其它位。</summary>
        private static void WriteStrikes(ParticleWorld world, int x, int y, int strikes)
        {
            world.At(x, y).flags = (byte)((world.At(x, y).flags & CellFlags.TreeSeedStrikeClearMask)
                | ((strikes << CellFlags.TreeSeedStrikeShift) & CellFlags.TreeSeedStrikeMask));
        }

        /// <summary>(x,y) 的**八邻**（3×3 去掉自身）里是否有湿土或腐殖土。越界读空气，天然不算。</summary>
        private static bool HasWetSoilNeighbour(ParticleWorld world, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    ushort id = world.GetCell(x + dx, y + dy).typeId;
                    if (id == ParticleIds.WetSoil || id == ParticleIds.WetHumusSoil
                        || id == ParticleIds.HumusSoil)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// (x,y) 的**八邻**（3×3 去掉自身）里是否有**真湿土**（WetSoil / WetHumusSoil，不含干腐殖土）。
        /// 越界读空气，天然不算。用于判断腐殖土家族是否是本次发芽的决定性墒情——喜湿（Moist）档在八邻
        /// 无真湿土却仍发芽时说明靠腐殖土满足了加湿判据，要消耗脚下腐殖土（见 <see cref="OnRandomTick"/>）。
        /// </summary>
        private static bool HasTrueWetSoilNeighbour(ParticleWorld world, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    ushort id = world.GetCell(x + dx, y + dy).typeId;
                    if (id == ParticleIds.WetSoil || id == ParticleIds.WetHumusSoil)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
