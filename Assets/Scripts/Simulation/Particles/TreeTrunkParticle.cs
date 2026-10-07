using UnityEngine;

namespace TapTap2026.Simulation
{
    // TreeTrunkParticle —— 树干（固体 + Organic；顶格按物种参数向上爬升、再按有序偏移表造冠）
    //
    // flags 私有位：bit3-5 = 物种（CellFlags.TreeSpeciesMask，与种子/树叶共享的「一格一类型」共享位）；
    //        bit6-7 = 生长阶段（CellFlags.TreeStageMask：0 爬升 / 1 造冠 / 2 成熟）。
    //        读用掩码、清用 ClearMask、写用 | 组合，绝不整体赋值 flags（会破坏 bit0 处理标记）。
    // 邻居查询范围：正下 1 格（扎根）、正上 1 格（顶格判定）、树冠偏移表最远 SupportRadius 格
    //               （造冠放置时查目标与八邻；本类型 OnMotion 为空，不参与运动休眠的阅读盒）。
    // 交互的 ParticleIds：Soil / WetSoil / HumusSoil（扎根判据，与草同款）、TreeTrunk（爬升 / 级联倒下）、
    //                     TreeLeaf（造冠产物 / 顶格判定不算叶）。
    //
    // 行为（全部在 OnLogic，每逻辑帧；死亡判定对**每一格**执行，生长只有顶格干活）：
    //   ① 温度致死：本格温度 > 50（同草线）就死这一格。**低温致死取 0**（同水生植物）：环境温度
    //      可以低到 0，树木不应在夜晚 / 高空中冻死；未来做「冬季枯死」再调高。
    //   ② 失去扎根：正下方既不是三土也不是树干 → 本格与上方整段连续树干一起 Clear（级联倒下；
    //      每格各自按 20% 有机衰变留腐殖质）。
    //   ③ 顶格判定：正上方还是树干 → 不是顶格，直接 return（每帧 1 次读，零随机数零写入）。
    //   ④ 阶段 0（爬升）：h = 向下数连续树干 + 1；h < H 时对上方目标先查后掷 NextUInt() % N == 0 →
    //      Set 树干（带物种位 / 树皮色片）；h ≥ H 转阶段 1（不消耗随机数，本帧不再造叶）。
    //   ⑤ 阶段 1（造冠）：按偏移表取「第一个未填充且当前可放」项——可放 = 界内 + 非固非液 +
    //      八邻已有树干/树叶（防浮空）；被石头 / 水 / 别的树挡住的未填充项**跳过不卡死**，其余照长；
    //      全表填满 → 阶段 2（成熟，此后零随机数消耗）；被挡项后来腾空会被继续补上。
    //   成熟后不再补叶（零随机数硬契约）；整树被砍时上段浮空树会在下一帧因失去下格而倒下。
    public sealed class TreeTrunkParticle : IParticleType
    {
        /// <summary>阶段 0：顶格向上爬升树干。</summary>
        public const int StageGrowing = 0;

        /// <summary>阶段 1：树干已到 H，按偏移表造冠。</summary>
        public const int StageCanopy = 1;

        /// <summary>阶段 2：树冠填满，成熟（此后零随机数消耗）。</summary>
        public const int StageMature = 2;

        /// <summary>致死高温：与草同线（50）。</summary>
        public const float LethalHeatTemperature = 50f;

        /// <summary>致死低温：取 0（同水生植物）——环境温可低到 0，树木不因寒冷死亡。</summary>
        public const float LethalColdTemperature = 0f;

        private static readonly Color32[] Palette =
        {
            // 物种分片（每物种 TreeSpecies.TrunkColorsPerSpecies = 4 色；长度必须是 5×4 的整数倍）：
            // 杨（灰白）/ 橡（棕）/ 松（红棕）/ 柏（灰褐）/ 柳（深褐）。
            new Color32(176, 172, 158, 255),
            new Color32(192, 188, 174, 255),
            new Color32(208, 204, 190, 255),
            new Color32(222, 218, 206, 255),
            new Color32(86, 66, 44, 255),
            new Color32(100, 78, 52, 255),
            new Color32(114, 90, 60, 255),
            new Color32(128, 102, 70, 255),
            new Color32(110, 66, 46, 255),
            new Color32(124, 76, 54, 255),
            new Color32(138, 88, 62, 255),
            new Color32(152, 100, 72, 255),
            new Color32(96, 84, 68, 255),
            new Color32(110, 96, 78, 255),
            new Color32(124, 110, 90, 255),
            new Color32(138, 124, 102, 255),
            new Color32(72, 56, 40, 255),
            new Color32(84, 66, 48, 255),
            new Color32(96, 76, 56, 255),
            new Color32(108, 88, 66, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic,
            density: 0.9f,
            heatCapacity: 1.2f,
            thermalConductivity: 0.7f,
            thermalEmissivity: 1.0f, // 近黑体
            affectedByGravity: false,
            isStatic: false,   // 必须非静态，否则收不到 OnLogic / 随机刻（与草同款取舍）
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(TreeTrunkParticle),
            // 遮光率不得超过草的 0.8：稳态温 T ≈ 10×LightAbsorption/(voidHeatLoss+radiativeLoss)
            // = 58.8×Absorption，取 0.8 ⇒ ≈47° < 50° 致死线（ParticleWorldTests 的稳态契约）。
            lightAbsorption: 0.8f,
            hasLogic: true);   // 生长与死亡都在 OnLogic

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 树干长在原地不动（非静态 + 空 OnMotion，同草 / 水生植物）。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // ① 温度致死：只死本格；上段会在下一帧因下格变空而失去扎根。
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature > LethalHeatTemperature || temperature < LethalColdTemperature)
            {
                world.Clear(x, y);
                return;
            }

            // ② 失去扎根：下格既不是三土也不是树干 → 本格与上方整段连续树干一起倒下。
            ushort below = world.GetCell(x, y - 1).typeId;
            if (below != ParticleIds.Soil && below != ParticleIds.WetSoil && below != ParticleIds.HumusSoil
                && below != ParticleIds.TreeTrunk)
            {
                int clearY = y;
                while (world.GetCell(x, clearY).typeId == ParticleIds.TreeTrunk)
                {
                    world.Clear(x, clearY);
                    clearY++;
                }

                return;
            }

            // ③ 生长只在顶格发生：正上方还是树干 → 不是顶格。
            if (world.GetCell(x, y + 1).typeId == ParticleIds.TreeTrunk)
            {
                return;
            }

            int stage = ReadStage(world, x, y);
            if (stage == StageMature)
            {
                return;
            }

            int species = TreeSpecies.Read(world, x, y);
            TreeSpecies.TreeSpeciesInfo info = TreeSpecies.Info(species);

            if (stage == StageGrowing)
            {
                int height = CountTrunkBelow(world, x, y) + 1;
                if (height < info.TrunkHeight)
                {
                    // 先查后掷：目标越界 / 被固液挡 → 本轮不动（不消耗随机数，腾空后可继续）。
                    if (!world.IsInBounds(x, y + 1) || ParticleRules.BlocksLight(world, x, y + 1))
                    {
                        return;
                    }

                    if (world.NextUInt() % info.GrowthDenominator != 0u)
                    {
                        return;
                    }

                    if (world.Set(x, y + 1, ParticleIds.TreeTrunk))
                    {
                        TreeSpecies.Stamp(world, x, y + 1, species, TreeSpecies.TrunkColorsPerSpecies);
                    }

                    return;
                }

                // 到高即转造冠（不消耗随机数）；本帧不再造叶。
                WriteStage(world, x, y, StageCanopy);
                return;
            }

            // ④ 阶段 1（造冠）：取「第一个未填充且当前可放」的表项。
            int placeable = FindFirstPlaceableCrown(world, x, y, info, out bool allFilled);
            if (allFilled)
            {
                WriteStage(world, x, y, StageMature);
                return;
            }

            if (placeable < 0)
            {
                // 有未填充项但当前全被挡：等腾空，零随机数消耗。
                return;
            }

            if (world.NextUInt() % info.GrowthDenominator != 0u)
            {
                return;
            }

            int targetX = x + info.Crown[placeable].Dx;
            int targetY = y + info.Crown[placeable].Dy;
            if (world.Set(targetX, targetY, ParticleIds.TreeLeaf))
            {
                TreeSpecies.Stamp(world, targetX, targetY, species, TreeSpecies.LeafColorsPerSpecies);
            }
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 生长与死亡都在 OnLogic（每逻辑帧、反应即时），随机刻无行为。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 抽样；物种位与色片由创建方（发芽 / 生长路径）
            // 调用 TreeSpecies.Stamp 盖上——OnSpawn 里拿不到物种。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 树干被覆盖时无残留效果（树叶的枯落由树叶自己的支撑判定负责）。
        }

        /// <summary>
        /// 造冠扫描：返回「第一个未填充且当前可放」的表内下标（没有可放项时 -1）；
        /// <paramref name="allFilled"/> = 全表均已填充（←→ 可转成熟）。
        /// 可放 = 界内 + 非固非液（<see cref="ParticleRules.BlocksLight"/>）+ 八邻已有树干/树叶（防浮空）。
        /// 被挡的未填充项跳过不复位，其余项照长；它腾空后会被继续补上。
        /// </summary>
        private static int FindFirstPlaceableCrown(
            ParticleWorld world, int x, int y, TreeSpecies.TreeSpeciesInfo info, out bool allFilled)
        {
            allFilled = true;
            int placeable = -1;

            for (int i = 0; i < info.Crown.Length; i++)
            {
                int tx = x + info.Crown[i].Dx;
                int ty = y + info.Crown[i].Dy;
                ushort id = world.GetCell(tx, ty).typeId;
                if (id == ParticleIds.TreeTrunk || id == ParticleIds.TreeLeaf)
                {
                    continue; // 已填充
                }

                allFilled = false;
                if (placeable < 0
                    && world.IsInBounds(tx, ty)
                    && !ParticleRules.BlocksLight(world, tx, ty)
                    && HasTreeNeighbour(world, tx, ty))
                {
                    placeable = i;
                    return placeable; // 「第一个可放的未填充项」已确定，无需扫完
                }
            }

            return placeable;
        }

        /// <summary>向下数连续树干格数（不含本格）。只在顶格、阶段 0 执行（≤ H 次读）。</summary>
        private static int CountTrunkBelow(ParticleWorld world, int x, int y)
        {
            int count = 0;
            int cursorY = y - 1;
            while (world.GetCell(x, cursorY).typeId == ParticleIds.TreeTrunk)
            {
                count++;
                cursorY--;
            }

            return count;
        }

        /// <summary>(x,y) 的**八邻**里是否有树干或树叶（造冠放置的防浮空守卫）。越界读空气，天然不算。</summary>
        private static bool HasTreeNeighbour(ParticleWorld world, int x, int y)
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
                    if (id == ParticleIds.TreeTrunk || id == ParticleIds.TreeLeaf)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>读本格的生长阶段（<see cref="CellFlags.TreeStageMask"/>）。</summary>
        private static int ReadStage(ParticleWorld world, int x, int y)
        {
            return (world.At(x, y).flags & CellFlags.TreeStageMask) >> CellFlags.TreeStageShift;
        }

        /// <summary>写本格的生长阶段，掩码读写保留含 bit0 与物种位在内的其它位。</summary>
        private static void WriteStage(ParticleWorld world, int x, int y, int stage)
        {
            world.At(x, y).flags = (byte)((world.At(x, y).flags & CellFlags.TreeStageClearMask)
                | ((stage << CellFlags.TreeStageShift) & CellFlags.TreeStageMask));
        }
    }
}
