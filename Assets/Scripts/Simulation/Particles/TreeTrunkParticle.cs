using UnityEngine;

namespace TapTap2026.Simulation
{
    // TreeTrunkParticle —— 树干（固体 + Organic；顶格按物种参数向上爬升、再按有序偏移表造冠）
    //
    // flags 私有位：bit3-5 = 物种（CellFlags.TreeSpeciesMask，与种子/树叶共享的「一格一类型」共享位）；
    //        bit6-7 = 生长阶段（CellFlags.TreeStageMask：0 爬升 / 1 造冠 / 2 成熟）。
    //        读用掩码、清用 ClearMask、写用 | 组合，绝不整体赋值 flags（会破坏 bit0 处理标记）。
    // 邻居查询范围：正下 1 格（扎根）、正上 1 格（顶格判定 / 爬升目标）、树冠偏移表最远 SupportRadius 格
    //               （造冠放置时查目标与八邻）；随机刻撒种时以顶格为中心、半径 SeedScatterRadius 格圆盘
    //               （含落点正下方 1 格判基质）。（本类型 OnMotion 为空，不参与运动休眠的阅读盒）。
    // 交互的 ParticleIds：Soil / WetSoil / HumusSoil / Sand（扎根判据：三土 + 沙 + 树干——已扎根的树
    //                     不因土壤被水冲成沙而整树级联倒下）、TreeTrunk（爬升 / 级联倒下）、
    //                     TreeLeaf（造冠产物 / 允许顶开的生长障碍；级联倒下时调
    //                     TreeLeafParticle.NotifyTrunkRemoved 主动让整冠失活）、
    //                     TreeSeed（撒种产物：成熟顶格在随机刻里撒下的同物种种子）。
    //
    // 行为（死亡与生长在 OnLogic，每逻辑帧——死亡判定对**每一格**执行、生长只有顶格干活；繁殖在 OnRandomTick）：
    //   ① 温度致死：本格温度 > 50（同草线）就死这一格。**低温致死取 0**（同水生植物）：环境温度
    //      可以低到 0，树木不应在夜晚 / 高空中冻死；未来做「冬季枯死」再调高。
    //      **温度致死只清本格、不通知树叶**：剩余树干仍支撑上段，上段下一次级联时自会通知。
    //   ② 失去扎根：正下方既不是三土 / 沙也不是树干 → 本格与上方整段连续树干一起 Clear（级联倒下；
    //      每格各自按 20% 有机衰变留腐殖质）。**先跑完整段 Clear、再逐格调
    //      TreeLeafParticle.NotifyTrunkRemoved（顺序不能反：支撑检查必须读到「整段已清空」，否则
    //      上段树干会继续支撑树冠、叶子漏失活）。即使 RandomTicksPerStep = 0（随机刻关闭），
    //      砍掉下部 / 挖掉根后整冠也会在同一次逻辑帧内集体失活，随后靠运动帧落叶。**
    //   ③ 顶格判定：正上方还是树干 → 不是顶格，直接 return（每帧 1 次读，零随机数零写入）。
    //   ④ 阶段 0（爬升）：h = 向下数连续树干 + 1。
    //      - h < H 且正上方**越界** → **封顶转造冠**（WriteStage(StageCanopy)；冠项越界的自然被
    //        跳过）——不再永远停在 stage 0 裸干。
    //      - h < H 且正上方被固 / 液挡（石头 / 水 / 沙等）→ 本轮不动、不消耗随机数，腾空后可继续；
    //        **正上方是树叶时可顶开继续爬升（穿冠）**——照常摇 NextUInt() % N == 0 再 Set 覆盖那格
    //        （树干与树叶都带 Organic，世界对「有机 → 有机」不触发 20% 有机衰变，无额外摇骰、不留腐殖质）。
    //      - 目标可放时 Set 树干（带物种位 / 树皮色片）；h ≥ H 转阶段 1（不消耗随机数，本帧不再造叶）。
    //   ⑤ 阶段 1（造冠）：按偏移表取「第一个未填充且当前可放」项——可放 = 非固非液 +
    //      八邻已有树干 / **未失活**树叶（防浮空；失活叶即将飘落、不算支撑，避免新叶贴着落叶堆
    //      长出后立刻失去支撑失活）；**偏移表越界项视为已填充**（跳过、不参与 allFilled=false）；
    //      被石头 / 水 / 别的树挡住的未填充项**跳过不卡死**，其余照长；
    //      全表填满 → 阶段 2（成熟，此后 OnLogic 零随机数消耗）；被挡项后来腾空会被继续补上。
    //   ⑥ OnRandomTick 繁殖：成熟顶格每次命中 1/4 概率在半径 5 圆盘撒一颗同物种树木种子
    //      （先判后摇：合格格恰耗 1 随机数，非顶格 / 未成熟零消耗）。
    //   成熟后 OnLogic 不再补叶（零随机数硬契约，繁殖消耗只发生在 OnRandomTick）；整树被砍时
    //   上段浮空树会在下一帧因失去下格而倒下。
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

        /// <summary>撒种概率的分母：每次随机刻命中时 <c>NextUInt() % 本值 == 0</c> 才尝试撒种，即 1/4 = 25%。</summary>
        public const uint SeedRainDenominator = 4u;

        /// <summary>撒种时搜索落点的半径（格，欧氏距离）：只处理 dx² + dy² ≤ 半径² 的格。
        /// 大于最大冠半宽 4，故种子落在冠外、不会撒到自己或别的树的冠里。</summary>
        public const int SeedScatterRadius = 5;

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

            // ② 失去扎根：下格既不是三土 / 沙也不是树干 → 本格与上方整段连续树干一起倒下。
            ushort below = world.GetCell(x, y - 1).typeId;
            if (below != ParticleIds.Soil && below != ParticleIds.WetSoil && below != ParticleIds.HumusSoil
                && below != ParticleIds.WetHumusSoil && below != ParticleIds.Sand
                && below != ParticleIds.TreeTrunk)
            {
                int clearY = y;
                while (world.GetCell(x, clearY).typeId == ParticleIds.TreeTrunk)
                {
                    world.Clear(x, clearY);
                    clearY++;
                }

                // 整段清完再逐格通知树叶：支撑检查必须看到「整段已空」，否则上段树干会继续支撑树冠。
                for (int cy = y; cy < clearY; cy++)
                {
                    TreeLeafParticle.NotifyTrunkRemoved(world, x, cy);
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
                    // 触顶封顶：上方越界无法再爬升 → 直接转造冠（冠项越界的自然被跳过）。
                    if (!world.IsInBounds(x, y + 1))
                    {
                        WriteStage(world, x, y, StageCanopy);
                        return;
                    }

                    // 先查后掷：目标被固 / 液挡 → 本轮不动（不消耗随机数，腾空后可继续）。
                    // 例外：目标是树叶时树干可顶开它继续爬升（穿冠）——树干与树叶都带 Organic，
                    // 世界对「有机 → 有机」不触发 20% 有机衰变（无额外摇骰、不留腐殖质）；
                    // 石头 / 水 / 沙等仍硬阻塞。
                    bool leafAbove = world.GetCell(x, y + 1).typeId == ParticleIds.TreeLeaf;
                    if (ParticleRules.BlocksLight(world, x, y + 1) && !leafAbove)
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
            // ① 非顶格不繁殖：正上方还是树干 → 不是顶格，直接返回（零随机流消耗）。
            if (world.GetCell(x, y + 1).typeId == ParticleIds.TreeTrunk)
            {
                return;
            }

            // ② 未成熟不繁殖（零随机流消耗）。
            if (ReadStage(world, x, y) != StageMature)
            {
                return;
            }

            // ③ 合格的成熟顶格每次命中**恰好摇 1 骰**：先判后摇、摇中才找落点。
            if (world.NextUInt() % SeedRainDenominator != 0u)
            {
                return;
            }

            // ④ 摇中：读物种，在半径 SeedScatterRadius 圆盘内找第一个落点撒一颗同物种种子（至多一颗；
            //    找不到落点什么都不做——骰已消耗）。
            TryScatterSeed(world, x, y);
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
        /// **界外项视为已填充**（跳过、不参与 allFilled=false，也不可放）。
        /// 可放 = 非固非液（<see cref="ParticleRules.BlocksLight"/>）+ 八邻已有树干 / 未失活树叶（防浮空）。
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
                if (!world.IsInBounds(tx, ty))
                {
                    continue; // 界外项视为已填充。
                }

                ushort id = world.GetCell(tx, ty).typeId;
                if (id == ParticleIds.TreeTrunk || id == ParticleIds.TreeLeaf)
                {
                    continue; // 已填充
                }

                allFilled = false;
                if (placeable < 0
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

        /// <summary>(x,y) 的**八邻**里是否有树干或**未失活**树叶（造冠放置的防浮空守卫）。
        /// 失活叶即将飘落、不算支撑（否则新叶会贴着落叶堆长出后立刻失活）。越界读空气，天然不算。</summary>
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

                    Cell cell = world.GetCell(x + dx, y + dy);
                    if (cell.typeId == ParticleIds.TreeTrunk
                        || (cell.typeId == ParticleIds.TreeLeaf
                            && (cell.flags & CellFlags.TreeLeafDetachedMask) == 0))
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

        /// <summary>
        /// 在半径 <see cref="SeedScatterRadius"/> 格的**圆盘**内、按固定扫描序（y 升序、同一行 x 升序，
        /// 只取 dx²+dy² ≤ 半径²）找第一个落点，在那里撒一颗**同物种**树木种子。
        /// 落点 = 空地（空气 / 气体，<see cref="ParticleRules.IsGasOrEmpty"/>）且正下方一格是可托住种子的固体
        /// （<see cref="IsSeedSupport"/>：固体且非树干 / 树叶 / 种子——不撒到自己或别树的干 / 叶 / 种子头上）。
        /// 找到即返回（至多一颗）。物种位与种子色片由 <see cref="TreeSpecies.Stamp"/> 盖上——<c>Set</c> 会把
        /// flags 清成边沿标记、重抽 shade，故 Stamp 必须在 Set 之后。
        /// </summary>
        private static void TryScatterSeed(ParticleWorld world, int x, int y)
        {
            int species = TreeSpecies.Read(world, x, y);

            for (int dy = -SeedScatterRadius; dy <= SeedScatterRadius; dy++)
            {
                for (int dx = -SeedScatterRadius; dx <= SeedScatterRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (dx * dx + dy * dy > SeedScatterRadius * SeedScatterRadius)
                    {
                        continue;
                    }

                    int sx = x + dx;
                    int sy = y + dy;

                    // 落点必须是界内空地（空气 / 气体），且正下方 1 格是**可托住种子的固体**。
                    if (!world.IsInBounds(sx, sy)
                        || !ParticleRules.IsGasOrEmpty(world, sx, sy)
                        || !IsSeedSupport(world, sx, sy - 1))
                    {
                        continue;
                    }

                    if (world.Set(sx, sy, ParticleIds.TreeSeed))
                    {
                        TreeSpecies.Stamp(world, sx, sy, species, TreeSpecies.SeedColorsPerSpecies);
                    }

                    return;
                }
            }
        }

        /// <summary>
        /// (x,y) 能不能托住一颗树木种子：是固体（<see cref="ParticleRules.IsSolid"/>）且**不是树木三件套**
        /// （树干 <see cref="ParticleIds.TreeTrunk"/> / 树叶 <see cref="ParticleIds.TreeLeaf"/> /
        /// 种子 <see cref="ParticleIds.TreeSeed"/>）。排除树木三件套是为了**不把种子撒到自己或别树的干 / 叶 /
        /// 种子头上**（与草撒种排除同一株同理）——否则种子会悬在树干 / 树冠 / 已落的种子上，掉落时又不会穿过
        /// 同为 Solid 的它们。石头 / 沙 / 土 / 湿土 / 腐殖土等固体照常可托住。
        /// </summary>
        private static bool IsSeedSupport(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            return id != ParticleIds.TreeTrunk && id != ParticleIds.TreeLeaf && id != ParticleIds.TreeSeed
                && ParticleRules.IsSolid(world, x, y);
        }
    }
}
