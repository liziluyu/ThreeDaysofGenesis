using UnityEngine;

namespace TapTap2026.Simulation
{
    // GrassSeedParticle —— 草种子（固体标签的粉末；**合并了原「水生态种子」**）
    //
    // 一个种子类型，按**生长环境**决定长成什么：随机刻命中时先看**正上方 SubmergedWaterCells（2）格**——
    // 两格全是水 ⇒ 沉水，按**水生植物**发芽（吸收头顶那格水、自身变成 1 格水生植物，长高交给植物自己逐格驱动）；
    // 否则按**草**发芽（八邻有湿土 / 腐殖土时抽出 1-3 格草）。两条路各有自己的资格判据，都不满足就累计空转，
    // 连续 MaxStrikesBeforeHumus（3）次霉烂成腐殖质。
    //
    // flags 私有位：bit5-6 = 「被随机刻命中但无发芽条件」的连续计数（CellFlags.SeedStrikeMask /
    //        SeedStrikeShift）。两种发芽去向共用同一个计数。读用 SeedStrikeMask、清用 SeedStrikeClearMask、
    //        写用 | 组合，绝不整体赋值 flags（会破坏 bit0 处理标记）。计数随 Cell 一起移动（Swap 搬整格），
    //        因此种子下落途中也会累积。
    // 邻居查询范围：发芽分支读正上方 2 格（是否沉水）+ 正下方 1 格（软底扎根）+ 八邻（湿土 / 腐殖土）；
    //               运动则为 StepSolid 的「正下、左下/右下、斜滑反侧同一行」。
    // 交互的 ParticleIds：WetSoil / WetHumusSoil / HumusSoil —— 八邻里有就以**草**发芽；
    //                     Water —— 正上方两格全是水且正下方是软底时按**水生植物**发芽（吸收头顶那格水）；
    //                     Atmosphere —— 被吸掉的那格水变成它；AquaticPlant —— 沉水发芽后自身变成它；
    //                     Grass —— 陆生发芽后自身与上方 1-3 格变成它；
    //                     Flower —— 陆生发芽后有 30%（3/10）概率把**株顶那格**草换成它；
    //                     Soil / WetSoil / HumusSoil / WetHumusSoil / Sand —— 沉水发芽的软底扎根判据；
    //                     Humus —— 连续 3 次被随机刻命中都无发芽条件时直接变成它
    //
    // **不带 Organic 标签**（原草种子带、原水生态种子不带，合并后取后者）：沉水发芽走
    // SetPhase(种子 → 水生植物)，而植物不带 Organic——若种子带，世界的 20% 有机衰变会把这个相变截胡成腐殖质。
    // 代价：种子被覆盖 / 被抹除时不再按 20% 留腐殖质（它的腐殖质只来自显式霉烂），且作为非有机固体会被
    // 落沙声的运动观察者计入（见 FallingSandSoundDriver 的口径说明）。
    //
    // 行为：
    //   运动（OnMotion，运动帧 60 Hz）：受重力的固体粉末，走 ParticleRules.StepSolid（摩擦/粘性 0.5，堆形与土一致）。
    //       落到地面 / 水底后就安分不动，等随机刻。
    //   发芽（OnRandomTick，随机刻）——先判去向，两条路都不满足则累计空转 / 霉烂：
    //       · 沉水（正上方两格全是水）：正下方是软底时吸收头顶那格水（SetPhase 成大气；上方的水下一个运动帧
    //         就会落下来补位）、自身 SetPhase 成 1 格水生植物；长高完全由植物自己的随机刻逐格驱动。
    //         沉水但没扎根软底则不发芽（进入空转路径）。
    //       · 陆生（未沉水）：八邻有湿土 / 腐殖土时以 1..MaxGrowthHeight 的随机高度把自己与**上方**那 1-3 格
    //         一并换成草（自己走 SetPhase 保留热量，上方各格走 Set；遇固 / 液停下、不覆盖已有物质），
    //         随后再摇一次骰：3/FlowerChanceDenominator（= 30%）命中就把**株顶那格**草 SetPhase 成花。
    //   腐殖土消耗（优待的代价，**仅陆生发芽**）：发芽成功且八邻没有**真湿土**（WetSoil / WetHumusSoil）时，
    //       本次发芽的墒情全靠腐殖土家族——调用 HumusSoilParticle.ConsumeFarthestBelow 把脚下连续土层里
    //       **最深**的一格腐殖土退化成普通土（HumusSoil → Soil、WetHumusSoil → WetSoil，SetPhase 保水保热）。
    //       八邻有真湿土时不消耗（腐殖土不是决定性因素）。
    //   生长要先判断、后摇骰：没有发芽条件时一步也不走、不消耗随机数（随机流不随场上种子数漂移）。
    //   霉烂：两条路都没有发芽条件时累计一次「空转」命中（计数存在本格 flags 的 bit5-6）；连续
    //       MaxStrikesBeforeHumus（3）次都无发芽条件就直接 SetPhase 成腐殖质——落错地方的种子不会永远赖着。
    //
    //   随机刻默认关闭（world.RandomTicksPerStep = 0，见 ParticleWorld），生产值由
    //   ParticleSimulationSettings._randomTicksPerStep 注入；数量按逻辑帧计。
    public sealed class GrassSeedParticle : IParticleType
    {
        /// <summary>生长高度下限：陆生发芽时种子**上方**至少长出这么多格草。</summary>
        public const int MinGrowthHeight = 1;

        /// <summary>生长高度上限：陆生发芽时种子**上方**最多长出这么多格草。</summary>
        public const int MaxGrowthHeight = 3;

        /// <summary>
        /// 判定「沉水」需要正上方连续多少格水（= 2）。两格是水生植物「头顶是水 + 上方还有一格保命水」
        /// 的最低要求：只有这样才能保证发芽后植株不会立刻露出水面。越界由 <see cref="ParticleWorld.GetCell"/>
        /// 返回空气，天然不满足。
        /// </summary>
        public const int SubmergedWaterCells = 2;

        /// <summary>
        /// 开花概率的分母：陆生发芽后 <c>NextUInt() % 本值 &lt; <see cref="FlowerChanceNumerator"/></c>
        /// 就把**株顶那格**草换成 <see cref="ParticleIds.Flower"/>，即 3/10 = **30%**（见 <see cref="OnRandomTick"/>）。
        /// </summary>
        public const uint FlowerChanceDenominator = 10u;

        /// <summary>开花概率的分子：与 <see cref="FlowerChanceDenominator"/> 一起构成 3/10 = 30%。</summary>
        public const uint FlowerChanceNumerator = 3u;

        /// <summary>
        /// 连续多少次「被随机刻命中但没有发芽条件」后，种子直接变成腐殖质。
        /// 计数存在本格 <c>flags</c> 的 bit5-6（<see cref="CellFlags.SeedStrikeMask"/>），随格子一起移动。
        /// </summary>
        public const int MaxStrikesBeforeHumus = 3;

        private static readonly Color32[] Palette =
        {
            new Color32(120, 96, 52, 255),
            new Color32(140, 118, 64, 255),
            new Color32(96, 108, 48, 255),
            new Color32(112, 128, 56, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,   // **不带 Organic**：见文件头「为什么不带 Organic」
            density: 1.2f,
            heatCapacity: 0.75f,
            thermalConductivity: 0.55f,
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,   // 与土同档：种子落地成堆、不滚远
            stickiness: 0.5f,
            palette: Palette,
            debugName: "草种子",
            // 运动阅读盒：0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3),
            hasLogic: false); // OnLogic 严格空体：发芽在随机刻、运动在 OnMotion

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 草种子只有运动与随机刻发芽，没有每逻辑帧的相变 / 反应。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 先判去向：正上方两格全是水 ⇒ 沉水，按水生植物发芽；否则按草发芽。
            // 两条路各自判资格，都不满足则落到末尾累计空转（先判断、后摇骰：无资格一步也不走、不消耗随机数）。
            if (IsSubmerged(world, x, y))
            {
                if (IsRootedOnSoftBed(world, x, y))
                {
                    // 吸水发芽：头顶那格水被吸收（SetPhase 成大气；上方的水下一个运动帧就会落下来补位），
                    // 自身 SetPhase 成 1 格植物。顺序无关紧要，两步都是原子相变。
                    world.SetPhase(x, y + 1, ParticleIds.Atmosphere);
                    world.SetPhase(x, y, ParticleIds.AquaticPlant);
                    return;
                }
            }
            else if (HasWetSoilNeighbour(world, x, y))
            {
                GerminateGrass(world, x, y);
                return;
            }

            // 无发芽条件（沉水但没扎根软底，或没沉水又没有湿土 / 腐殖土邻格）：累计一次「空转」命中——数到
            // MaxStrikesBeforeHumus（3）次就霉烂、直接变成腐殖质；否则只把计数写回、原地等待（不消耗随机数）。
            int strikes = ReadStrikes(world, x, y) + 1;
            if (strikes >= MaxStrikesBeforeHumus)
            {
                // 同一份物质换形态：草种子 → 腐殖质（腐殖质非有机，不再触发有机衰变）。
                world.SetPhase(x, y, ParticleIds.Humus);
                return;
            }

            WriteStrikes(world, x, y, strikes);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 草种子被覆盖时无残留效果（不带 Organic：不走 20% 腐殖质衰变；它的腐殖质只来自霉烂）。
        }

        /// <summary>
        /// 陆生发芽：自身 SetPhase 成草，**上方** 1..MaxGrowthHeight 格的空气 / 气体一并换成草
        /// （遇固 / 液立即停下、不覆盖已有物质，顶到边界同理），随后 30% 概率把**株顶那格**草换成花。
        /// </summary>
        private static void GerminateGrass(ParticleWorld world, int x, int y)
        {
            int height = MinGrowthHeight + world.NextInt(MaxGrowthHeight - MinGrowthHeight + 1);

            // 自己：同一份物质换形态（草），热量随物质留在原格。
            world.SetPhase(x, y, ParticleIds.Grass);

            // 同步记下**株顶**——最后一次成功写入草的那格；上方全被挡住时株顶就是自己那格（整株只有一格草）。
            int topY = y;
            for (int step = 1; step <= height; step++)
            {
                int targetY = y + step;
                if (!world.IsInBounds(x, targetY) || ParticleRules.BlocksLight(world, x, targetY))
                {
                    break;
                }

                world.Set(x, targetY, ParticleIds.Grass);
                topY = targetY;
            }

            // 开花（30% = 3/10）：把株顶那格草换成花——同一株的物质换形态（SetPhase 保留本格热量），
            // 花因此继承了草的性质、只有花色不同（见 FlowerParticle）。每次发芽恰摇一次（消耗 1 个随机数）。
            if (world.NextUInt() % FlowerChanceDenominator < FlowerChanceNumerator)
            {
                world.SetPhase(x, topY, ParticleIds.Flower);
            }

            // 腐殖土消耗（优待的代价）：八邻没有真湿土（WetSoil / WetHumusSoil）时，本次发芽的墒情全靠
            // 腐殖土家族——把脚下连续土层里最深的一格腐殖土退化成普通土。此刻 (x,y) 已是草（或开花的株顶），
            // helper 从 (x, y-1) 起竖直向下扫土层，正确。有真湿土时不消耗（腐殖土不是决定性因素）。
            if (!HasTrueWetSoilNeighbour(world, x, y))
            {
                HumusSoilParticle.ConsumeFarthestBelow(world, x, y);
            }
        }

        /// <summary>
        /// (x,y) 是否**沉水**：正上方连续 <see cref="SubmergedWaterCells"/>（2）格全是水。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不满足。
        /// </summary>
        private static bool IsSubmerged(ParticleWorld world, int x, int y)
        {
            for (int step = 1; step <= SubmergedWaterCells; step++)
            {
                if (world.GetCell(x, y + step).typeId != ParticleIds.Water)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// (x,y) 是否扎根于**软底**：正下方 ∈ {Soil, WetSoil, HumusSoil, WetHumusSoil, Sand}——
        /// 与 <see cref="AquaticPlantParticle"/> 自己的扎根判据同一集合，避免「发芽即注定失去扎根」。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不满足。
        /// </summary>
        private static bool IsRootedOnSoftBed(ParticleWorld world, int x, int y)
        {
            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || belowId == ParticleIds.WetHumusSoil
                || belowId == ParticleIds.Sand;
        }

        /// <summary>读本格的「空转」计数（<see cref="CellFlags.SeedStrikeMask"/>）。</summary>
        private static int ReadStrikes(ParticleWorld world, int x, int y)
        {
            return (world.At(x, y).flags & CellFlags.SeedStrikeMask) >> CellFlags.SeedStrikeShift;
        }

        /// <summary>写本格的「空转」计数，<c>&amp; ~</c> / <c>|</c> 组合保留含 bit0 处理标记在内的其它位。</summary>
        private static void WriteStrikes(ParticleWorld world, int x, int y, int strikes)
        {
            byte flags = (byte)((world.At(x, y).flags & CellFlags.SeedStrikeClearMask)
                | ((strikes << CellFlags.SeedStrikeShift) & CellFlags.SeedStrikeMask));
            world.At(x, y).flags = flags;
        }

        /// <summary>
        /// (x,y) 的**八邻**（3×3 去掉自身）里是否有湿土 / 腐殖土（腐殖土保水，视作自带墒情）。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不算。
        /// </summary>
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
                    if (id == ParticleIds.WetSoil || id == ParticleIds.WetHumusSoil || id == ParticleIds.HumusSoil)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// (x,y) 的**八邻**（3×3 去掉自身）里是否有**真湿土**（WetSoil / WetHumusSoil，不含干腐殖土）。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不算。用于判断腐殖土家族是否是本次发芽
        /// 的决定性墒情——有真湿土时不消耗脚下腐殖土（见 <see cref="GerminateGrass"/>）。
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
