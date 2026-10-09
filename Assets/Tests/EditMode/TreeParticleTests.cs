using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 树木系统（种子 / 树干 / 树叶 + 五物种静态表）：分级发芽（松/柏耐旱、杨/橡喜湿、柳须湿土）、
    /// 概率化发芽（比草种子慢）、爬升到 H、按有序偏移表造冠（被挡项跳过、腾空补长、全填满转成熟且
    /// 成熟后零随机流消耗）、无根级联倒下、三种树叶死法、整树静止与确定性重放。
    /// </summary>
    /// <remarks>
    /// 跨类型相变依赖 <see cref="ParticleIds"/> 回填，因此全部走生产注册路径
    /// （<see cref="ParticleRegistration.RegisterAll"/>）。随机刻 / 逻辑帧用 <c>OnRandomTick</c> /
    /// <c>OnLogic</c> 直接调用（仓库既有惯例），生长用本文件的「树干列驱动器」复现逻辑帧扫描
    /// （y 降序逐格 OnLogic；世界在 Set 后把新格标记为本帧已处理，因此每帧至多长一格）。
    /// 默认世界里 Q = 0（T = 0）——树木致死低温取 0（同水生植物），所以生长用例无需预热热场。
    /// </remarks>
    public sealed class TreeParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private const uint SeedValue = 20261007u;

        private static ushort SeedId
        {
            get { return ParticleIds.TreeSeed; }
        }

        private static ushort TrunkId
        {
            get { return ParticleIds.TreeTrunk; }
        }

        private static ushort LeafId
        {
            get { return ParticleIds.TreeLeaf; }
        }

        // ============================== 通用辅助 ==============================

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int i = 0; i < world.CellCount; i++)
            {
                if (world.Cells[i].typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }

        private static int ReadSpecies(ParticleWorld world, int x, int y)
        {
            return (world.GetCell(x, y).flags & CellFlags.TreeSpeciesMask) >> CellFlags.TreeSpeciesShift;
        }

        private static int ReadStage(ParticleWorld world, int x, int y)
        {
            return (world.GetCell(x, y).flags & CellFlags.TreeStageMask) >> CellFlags.TreeStageShift;
        }

        private static void RandomTick(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnRandomTick(world, x, y);
        }

        private static void LogicTick(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnLogic(world, x, y);
        }

        /// <summary>从种子格 (x,yBase) 向上数连续树干，返回最顶格 y；没有树干返回 -1。</summary>
        private static int TopTrunk(ParticleWorld world, int x, int yBase)
        {
            int top = -1;
            for (int y = yBase; y < world.Height; y++)
            {
                if (world.GetCell(x, y).typeId != TrunkId)
                {
                    break;
                }

                top = y;
            }

            return top;
        }

        /// <summary>复现逻辑帧扫描里树干那一列的部分（y 降序逐格 OnLogic；非顶格只读一次即返回）。
        /// 整列扫到底、不因中段断开而提前收工——砍断后的悬空段也必须被驱动到（世界全网格扫描的等价物）。</summary>
        private static void StepTrunkColumn(ParticleWorld world, int x, int yBase)
        {
            for (int y = world.Height - 1; y >= yBase; y--)
            {
                if (world.GetCell(x, y).typeId == TrunkId)
                {
                    LogicTick(world, TrunkId, x, y);
                }
            }
        }

        private static void GrowFrames(ParticleWorld world, int x, int yBase, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                StepTrunkColumn(world, x, yBase);
            }
        }

        /// <summary>反复驱动树干列，直到顶部树干进入成熟阶段。返回消耗的帧数；-1 = 树干消失；-2 = 超上限未成熟。</summary>
        private static int GrowToMaturity(ParticleWorld world, int x, int yBase, int maxFrames = 20000)
        {
            for (int frame = 0; frame < maxFrames; frame++)
            {
                int top = TopTrunk(world, x, yBase);
                if (top < 0)
                {
                    return -1;
                }

                if (ReadStage(world, x, top) == TreeTrunkParticle.StageMature)
                {
                    return frame;
                }

                StepTrunkColumn(world, x, yBase);
            }

            return -2;
        }

        /// <summary>按物种基质档铺好底（喜湿附带一颗湿土邻居、最喜水正下方湿土），放种并盖上物种位。</summary>
        private static void PlantSeed(ParticleWorld world, int x, int y, int species)
        {
            switch (TreeSpecies.Info(species).Substrate)
            {
                case TreeSpecies.SubstrateTier.Wet:
                    world.Set(x, y - 1, ParticleIds.WetSoil);
                    break;
                case TreeSpecies.SubstrateTier.Moist:
                    world.Set(x, y - 1, ParticleIds.Soil);
                    world.Set(x + 1, y - 1, ParticleIds.WetSoil);
                    break;
                default:
                    world.Set(x, y - 1, ParticleIds.Soil);
                    break;
            }

            world.Set(x, y, SeedId);
            TreeSpecies.Stamp(world, x, y, species, TreeSpecies.SeedColorsPerSpecies);
        }

        /// <summary>反复随机刻直到 (x,y) 的种子发芽成树干（上限内必达，否则断言失败）。</summary>
        private static void Germinate(ParticleWorld world, int x, int y)
        {
            for (int i = 0; i < 400 && world.GetCell(x, y).typeId == SeedId; i++)
            {
                RandomTick(world, SeedId, x, y);
            }

            Assert.AreEqual(TrunkId, world.GetCell(x, y).typeId, "种子应在有限次随机刻内发芽");
        }

        /// <summary>[0,1) 的 FNV-1a 状态哈希（整帧重放的逐周期比对用）。</summary>
        private static uint StateHash(ParticleWorld world)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < world.CellCount; i++)
            {
                Cell cell = world.Cells[i];
                hash = (hash ^ cell.typeId) * 16777619u;
                hash = (hash ^ cell.shade) * 16777619u;
                hash = (hash ^ cell.flags) * 16777619u;
            }

            return hash;
        }

        // ============================== 注册与契约 ==============================

        [Test]
        public void ProductionRegistration_AppendsTreeTypes()
        {
            Assert.AreNotEqual(0, (int)SeedId, "TreeSeed 必须被回填（非 0）");
            Assert.AreEqual((int)ParticleIds.Fire + 1, (int)SeedId,
                "树木种子必须正好追加在火之后：注册顺序是存档兼容性的一部分");
            Assert.AreEqual((int)SeedId + 1, (int)TrunkId, "树干紧追加在种子之后");
            Assert.AreEqual((int)TrunkId + 1, (int)LeafId, "树叶紧追加在树干之后");
        }

        [Test]
        public void InfoContract_TagsGravityStaticHasLogicAndPalettes()
        {
            ParticleTypeInfo seed = ParticleRegistry.Types[SeedId].Info;
            Assert.IsTrue((seed.Tags & ParticleTags.Solid) != 0);
            Assert.IsTrue((seed.Tags & ParticleTags.Organic) != 0);
            Assert.IsTrue(seed.AffectedByGravity, "种子受重力");
            Assert.IsFalse(seed.IsStatic);
            Assert.IsFalse(seed.HasLogic, "种子 OnLogic 严格空体（发芽在随机刻）");
            Assert.AreEqual(10, seed.Palette.Length, "种子调色板 = 5 物种 × 2 色");

            ParticleTypeInfo trunk = ParticleRegistry.Types[TrunkId].Info;
            Assert.IsTrue((trunk.Tags & ParticleTags.Solid) != 0);
            Assert.IsTrue((trunk.Tags & ParticleTags.Organic) != 0);
            Assert.IsFalse(trunk.AffectedByGravity, "树干不受重力");
            Assert.IsFalse(trunk.IsStatic, "树干必须非静态，否则收不到 OnLogic / 随机刻");
            Assert.IsTrue(trunk.HasLogic, "树干生长与死亡都在 OnLogic");
            Assert.AreEqual(0.8f, trunk.LightAbsorption, "树干遮光率不得超过草的 0.8（日光稳态 ≈47° < 50° 致死线）");
            Assert.AreEqual(20, trunk.Palette.Length, "树干调色板 = 5 物种 × 4 色");

            ParticleTypeInfo leaf = ParticleRegistry.Types[LeafId].Info;
            Assert.IsTrue((leaf.Tags & ParticleTags.Solid) != 0);
            Assert.IsTrue((leaf.Tags & ParticleTags.Organic) != 0);
            Assert.IsFalse(leaf.AffectedByGravity, "树叶不受重力");
            Assert.IsFalse(leaf.IsStatic, "树叶必须非静态，否则收不到随机刻");
            Assert.IsFalse(leaf.HasLogic, "树叶 OnLogic 严格空体（死法都在随机刻）");
            Assert.AreEqual(0.8f, leaf.LightAbsorption, "树叶遮光率照抄草的 0.8");
            Assert.AreEqual(20, leaf.Palette.Length, "树叶调色板 = 5 物种 × 4 色");

            Color32[][] palettes = { seed.Palette, trunk.Palette, leaf.Palette };
            int[] colorsPerSpecies = { TreeSpecies.SeedColorsPerSpecies, TreeSpecies.TrunkColorsPerSpecies, TreeSpecies.LeafColorsPerSpecies };
            for (int p = 0; p < palettes.Length; p++)
            {
                Assert.AreEqual(0, palettes[p].Length % (TreeSpecies.Count * colorsPerSpecies[p]),
                    "调色板长度必须是 5×每物种色数的整数倍（Stamp 折片无偏的前提）");
                for (int i = 0; i < palettes[p].Length; i++)
                {
                    Assert.AreEqual(255, palettes[p][i].a, "树木调色板全部不透明");
                }
            }
        }

        [Test]
        public void SpeciesTable_IsWellFormed()
        {
            string[] expectedNames = { "杨树", "橡树", "松树", "柏树", "柳树" };
            int[] expectedCrownLengths = { 19, 52, 40, 39, 40 };

            Assert.AreEqual(5, TreeSpecies.Count);
            for (int s = 0; s < TreeSpecies.Count; s++)
            {
                TreeSpecies.TreeSpeciesInfo info = TreeSpecies.Info(s);
                Assert.AreEqual(expectedNames[s], info.Name);
                Assert.GreaterOrEqual(info.TrunkHeight, 1, "H ≥ 1");
                Assert.GreaterOrEqual(info.GrowthDenominator, 1u, "N ≥ 1");
                Assert.GreaterOrEqual(info.GerminationDenominator, 1u, "D ≥ 1");
                Assert.AreEqual(expectedCrownLengths[s], info.Crown.Length, "树冠表长度锁定（BuildCrown 回归）");

                // 偏移必须唯一。
                var seen = new System.Collections.Generic.HashSet<int>();
                for (int i = 0; i < info.Crown.Length; i++)
                {
                    int key = (info.Crown[i].Dx << 8) | (info.Crown[i].Dy & 0xFF);
                    Assert.IsTrue(seen.Add(key), $"物种 {info.Name} 的偏移 ({info.Crown[i].Dx},{info.Crown[i].Dy}) 重复");
                }

                // 派生量核对：半宽 / 最低最高 dy / 支撑半径（独立复算）。
                int maxHalfWidth = 0;
                int minDy = 0;
                int maxDy = 0;
                int radius = 0;
                for (int i = 0; i < info.Crown.Length; i++)
                {
                    int dx = info.Crown[i].Dx;
                    int dy = info.Crown[i].Dy;
                    int absDx = dx < 0 ? -dx : dx;
                    if (absDx > maxHalfWidth)
                    {
                        maxHalfWidth = absDx;
                    }

                    if (dy < minDy)
                    {
                        minDy = dy;
                    }

                    if (dy > maxDy)
                    {
                        maxDy = dy;
                    }

                    int clampedDy = dy > 0 ? 0 : (dy < -(info.TrunkHeight - 1) ? -(info.TrunkHeight - 1) : dy);
                    int vertical = dy - clampedDy;
                    int distance = absDx > vertical ? absDx : vertical;
                    if (distance > radius)
                    {
                        radius = distance;
                    }
                }

                Assert.AreEqual(maxHalfWidth, info.MaxHalfWidth);
                Assert.AreEqual(minDy, info.MinCrownDy);
                Assert.AreEqual(maxDy, info.MaxCrownDy);
                Assert.AreEqual(radius, info.SupportRadius, "支撑半径 = 冠格到树干列的最大最小 Chebyshev 距离");
            }
        }

        [Test]
        public void SpeciesCrown_IsSelfSupporting()
        {
            // 纯表测试（防浮空叶的根）：每项必须与树干列 (0, dy ∈ [-(H-1), 0]) 或表中更早的一项相邻。
            for (int s = 0; s < TreeSpecies.Count; s++)
            {
                TreeSpecies.TreeSpeciesInfo info = TreeSpecies.Info(s);
                for (int i = 0; i < info.Crown.Length; i++)
                {
                    int dx = info.Crown[i].Dx;
                    int dy = info.Crown[i].Dy;
                    bool supported = false;
                    for (int k = -(info.TrunkHeight - 1); k <= 0 && !supported; k++)
                    {
                        supported = Chebyshev(dx, dy, 0, k) <= 1;
                    }

                    for (int j = 0; j < i && !supported; j++)
                    {
                        supported = Chebyshev(dx, dy, info.Crown[j].Dx, info.Crown[j].Dy) <= 1;
                    }

                    Assert.IsTrue(supported,
                        $"物种 {info.Name} 的第 {i} 项 ({dx},{dy}) 不满足自支撑不变量（会浮空）");
                }
            }
        }

        private static int Chebyshev(int ax, int ay, int bx, int by)
        {
            int dx = ax - bx;
            int dy = ay - by;
            if (dx < 0)
            {
                dx = -dx;
            }

            if (dy < 0)
            {
                dy = -dy;
            }

            return dx > dy ? dx : dy;
        }

        // ============================== 种子：分级发芽 ==============================

        [Test]
        public void Seed_GerminatesOnSpeciesTierSubstrate()
        {
            // 耐旱（松/柏）：任意三土即可。这里各验干土与腐殖土。
            ushort[] droughtSoils = { ParticleIds.Soil, ParticleIds.HumusSoil };
            int[] droughtSpecies = { TreeSpecies.Pine, TreeSpecies.Cypress };
            foreach (int species in droughtSpecies)
            {
                foreach (ushort soil in droughtSoils)
                {
                    ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
                    world.Set(2, 3, soil);
                    world.Set(2, 4, SeedId);
                    TreeSpecies.Stamp(world, 2, 4, species, TreeSpecies.SeedColorsPerSpecies);
                    Germinate(world, 2, 4);
                }
            }

            // 喜湿（杨/橡）：三土 + 八邻有湿土。
            int[] moistSpecies = { TreeSpecies.Poplar, TreeSpecies.Oak };
            foreach (int species in moistSpecies)
            {
                ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
                world.Set(2, 3, ParticleIds.Soil);
                world.Set(3, 3, ParticleIds.WetSoil); // 八邻之一。
                world.Set(2, 4, SeedId);
                TreeSpecies.Stamp(world, 2, 4, species, TreeSpecies.SeedColorsPerSpecies);
                Germinate(world, 2, 4);
            }

            // 最喜水（柳）：正下方必须恰为湿土。
            ParticleWorld willow = new ParticleWorld(5, 8, SeedValue);
            willow.Set(2, 3, ParticleIds.WetSoil);
            willow.Set(2, 4, SeedId);
            TreeSpecies.Stamp(world: willow, x: 2, y: 4, species: TreeSpecies.Willow, colorsPerSpecies: TreeSpecies.SeedColorsPerSpecies);
            Germinate(willow, 2, 4);
        }

        [Test]
        public void Seed_MoistSpecies_WithoutWetNeighbour_RotAfterThreeStrikes()
        {
            // 杨 / 橡：正下方是干土且八邻无湿土 → 条件不满足，3 次打击霉烂成腐殖质。
            int[] moistSpecies = { TreeSpecies.Poplar, TreeSpecies.Oak };
            foreach (int species in moistSpecies)
            {
                ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
                world.Set(2, 3, ParticleIds.Soil);
                world.Set(2, 4, SeedId);
                TreeSpecies.Stamp(world, 2, 4, species, TreeSpecies.SeedColorsPerSpecies);

                RandomTick(world, SeedId, 2, 4);
                Assert.AreEqual(SeedId, world.GetCell(2, 4).typeId, "第 1 次空转仍保留");
                RandomTick(world, SeedId, 2, 4);
                Assert.AreEqual(SeedId, world.GetCell(2, 4).typeId, "第 2 次空转仍保留");
                RandomTick(world, SeedId, 2, 4);
                Assert.AreEqual(ParticleIds.Humus, world.GetCell(2, 4).typeId, "连续 3 次无发芽条件应霉烂");
            }
        }

        [Test]
        public void Seed_Willow_RequiresWetSoilDirectlyBelow()
        {
            // 柳：即使八邻有湿土，正下方不是湿土也不发芽（与喜湿档的区别）。
            ParticleWorld dry = new ParticleWorld(5, 8, SeedValue);
            dry.Set(2, 3, ParticleIds.Soil);
            dry.Set(3, 3, ParticleIds.WetSoil);
            dry.Set(2, 4, SeedId);
            TreeSpecies.Stamp(dry, 2, 4, TreeSpecies.Willow, TreeSpecies.SeedColorsPerSpecies);
            for (int i = 0; i < 3; i++)
            {
                RandomTick(dry, SeedId, 2, 4);
            }

            Assert.AreEqual(ParticleIds.Humus, dry.GetCell(2, 4).typeId, "正下方非湿土：柳不发芽并霉烂");
        }

        [Test]
        public void Seed_DoesNotGerminateUnderSolid()
        {
            // 正上方是固体（石）：空间门不满足，不发芽并霉烂（避免长成动不了的木桩）。
            ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
            world.Set(2, 3, ParticleIds.Soil);
            world.Set(2, 5, ParticleIds.Stone);
            world.Set(2, 4, SeedId);
            TreeSpecies.Stamp(world, 2, 4, TreeSpecies.Pine, TreeSpecies.SeedColorsPerSpecies);
            for (int i = 0; i < 3; i++)
            {
                RandomTick(world, SeedId, 2, 4);
            }

            Assert.AreEqual(ParticleIds.Humus, world.GetCell(2, 4).typeId, "石下不发芽，3 次打击后霉烂");
        }

        [Test]
        public void Seed_FailedConditions_DoNotConsumeRandomStream()
        {
            ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
            world.Set(2, 3, ParticleIds.Soil);
            world.Set(2, 4, SeedId);
            TreeSpecies.Stamp(world, 2, 4, TreeSpecies.Poplar, TreeSpecies.SeedColorsPerSpecies);

            ParticleWorld reference = new ParticleWorld(5, 8, SeedValue);
            reference.Set(2, 3, ParticleIds.Soil);
            reference.Set(2, 4, SeedId);
            TreeSpecies.Stamp(reference, 2, 4, TreeSpecies.Poplar, TreeSpecies.SeedColorsPerSpecies);

            RandomTick(world, SeedId, 2, 4); // 空转两次：不该消耗随机数。
            RandomTick(world, SeedId, 2, 4);

            Assert.AreEqual(reference.NextUInt(), world.NextUInt(), "无发芽条件的空转不得推进随机流");
        }

        [Test]
        public void Seed_GerminationIsProbabilistic_NotInstantLikeGrass()
        {
            // 条件满足时还要掷 1/D 的发芽骰：取 20 个平行世界，至少有一个「首次好命中」没有发芽
            //（D=2 时单个世界不发芽概率 1/2，20 个全发芽概率 2^-20 —— 确定性世界下这个断言稳定成立）。
            int notGerminatedOnFirstHit = 0;
            for (uint i = 0; i < 20; i++)
            {
                ParticleWorld world = new ParticleWorld(5, 8, SeedValue + i);
                world.Set(2, 3, ParticleIds.Soil);
                world.Set(2, 4, SeedId);
                TreeSpecies.Stamp(world, 2, 4, TreeSpecies.Pine, TreeSpecies.SeedColorsPerSpecies);

                RandomTick(world, SeedId, 2, 4);
                if (world.GetCell(2, 4).typeId == SeedId)
                {
                    notGerminatedOnFirstHit++;
                }
            }

            Assert.GreaterOrEqual(notGerminatedOnFirstHit, 1,
                "发芽必须是概率化的（比草种子的命中即发慢）：20 个世界里至少一个首次好命中不发芽");
        }

        [Test]
        public void Seed_FailedRolls_DoNotStrikeOrRot()
        {
            // 掷骰未中不计打击：条件持续满足时哪怕命中很多次（D=4），也只会发芽、绝不霉烂。
            ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
            PlantSeed(world, 2, 4, TreeSpecies.Oak);
            for (int i = 0; i < 400 && world.GetCell(2, 4).typeId == SeedId; i++)
            {
                RandomTick(world, SeedId, 2, 4);
                Assert.AreNotEqual(ParticleIds.Humus, world.GetCell(2, 4).typeId,
                    "条件满足时掷骰未中不计打击，不得霉烂");
            }

            Assert.AreEqual(TrunkId, world.GetCell(2, 4).typeId, "上限内必发芽");
        }

        [Test]
        public void Seed_GerminationStampsSpeciesAndShade()
        {
            // 柳（物种 4）：SetPhase 清私有位后由 Stamp 重写——树干物种位与树皮色片（16..19）都要对。
            ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
            PlantSeed(world, 2, 4, TreeSpecies.Willow);
            Germinate(world, 2, 4);

            Assert.AreEqual(TreeSpecies.Willow, ReadSpecies(world, 2, 4), "发芽后树干应保留物种位");
            int shade = world.GetCell(2, 4).shade;
            Assert.GreaterOrEqual(shade, TreeSpecies.Willow * TreeSpecies.TrunkColorsPerSpecies);
            Assert.Less(shade, (TreeSpecies.Willow + 1) * TreeSpecies.TrunkColorsPerSpecies,
                "树干 shade 应落在本物种的色片内");
        }

        // ============================== 树干：生长 ==============================

        /// <summary>48×48 世界种一棵树（含基质档），返回世界与种植点。
        /// 种子床直接铺在**世界底板**上（yBase = 1）：种子与（喜湿秧的）湿土都是受重力的粉末，
        /// 放在空中会自行沉降/侧滑；贴着下边界则天然稳定，StepMotion 用例里全网格都不动。</summary>
        private static ParticleWorld BuildTreeWorld(int species, out int x, out int yBase)
        {
            x = 24;
            yBase = 1;
            ParticleWorld world = new ParticleWorld(48, 48, SeedValue);
            PlantSeed(world, x, yBase, species);
            Germinate(world, x, yBase);
            return world;
        }

        [Test]
        public void Trunk_GrowsToSpeciesHeight_AllSpecies()
        {
            for (int species = 0; species < TreeSpecies.Count; species++)
            {
                TreeSpecies.TreeSpeciesInfo info = TreeSpecies.Info(species);
                ParticleWorld world = BuildTreeWorld(species, out int x, out int yBase);
                Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0,
                    $"物种 {info.Name} 应在上限内成熟");

                Assert.AreEqual(info.TrunkHeight, TopTrunk(world, x, yBase) - yBase + 1,
                    $"物种 {info.Name} 的树干应正好 H 格");
                for (int y = yBase; y < yBase + info.TrunkHeight; y++)
                {
                    Assert.AreEqual(species, ReadSpecies(world, x, y), $"物种 {info.Name} 树干格 ({x},{y}) 物种位");
                }
            }
        }

        [Test]
        public void Trunk_GrowsAtMostOneCellPerFrame()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Poplar, out int x, out int yBase);
            int previous = TopTrunk(world, x, yBase) - yBase + 1;
            for (int frame = 0; frame < 400; frame++)
            {
                StepTrunkColumn(world, x, yBase);
                int current = TopTrunk(world, x, yBase) - yBase + 1;
                Assert.LessOrEqual(current - previous, 1, $"第 {frame} 帧树干增长不得超过 1 格");
                Assert.GreaterOrEqual(current - previous, 0, "树干不得缩短（本用例不触发死亡）");
                previous = current;
            }
        }

        [Test]
        public void Trunk_StopsWhenBlockedAbove()
        {
            ParticleWorld world = new ParticleWorld(48, 48, SeedValue);
            int x = 24;
            int yBase = 8;
            world.Set(x, yBase + 3, ParticleIds.Stone); // 树干目标上方 3 格处挡石。
            PlantSeed(world, x, yBase, TreeSpecies.Pine);
            Germinate(world, x, yBase);

            GrowFrames(world, x, yBase, 2000);

            Assert.AreEqual(3, TopTrunk(world, x, yBase) - yBase + 1, "被挡时树干停在挡块下方");
            Assert.AreEqual(ParticleIds.Stone, world.GetCell(x, yBase + 3).typeId, "挡块不得被覆盖");
            Assert.AreEqual(0, CountType(world, LeafId), "没到 H 不造冠");
            Assert.AreNotEqual(TreeTrunkParticle.StageMature, ReadStage(world, x, yBase + 2), "顶部应停在爬升阶段");
        }

        [Test]
        public void Trunk_Growth_IsDeterministicReplay()
        {
            ParticleWorld a = BuildTreeWorld(TreeSpecies.Pine, out int x, out int yBase);
            ParticleWorld b = BuildTreeWorld(TreeSpecies.Pine, out int x2, out int yBase2);
            Assert.AreEqual(x, x2);
            Assert.AreEqual(yBase, yBase2);

            for (int frame = 0; frame < 900; frame++)
            {
                StepTrunkColumn(a, x, yBase);
                StepTrunkColumn(b, x2, yBase2);
            }

            Assert.AreEqual(TreeTrunkParticle.StageMature, ReadStage(a, x, TopTrunk(a, x, yBase)), "重放场景应已成熟");
            AssertGridsEqual(a, b);
        }

        private static void AssertGridsEqual(ParticleWorld a, ParticleWorld b)
        {
            for (int i = 0; i < a.CellCount; i++)
            {
                Assert.AreEqual(a.Cells[i].typeId, b.Cells[i].typeId, $"格 {i} typeId 应逐格一致");
                Assert.AreEqual(a.Cells[i].shade, b.Cells[i].shade, $"格 {i} shade 应逐格一致");
                Assert.AreEqual(a.Cells[i].flags, b.Cells[i].flags, $"格 {i} flags 应逐格一致");
            }
        }

        // ============================== 树干：死亡 ==============================

        [Test]
        public void Trunk_LosesRoot_CascadesUpward()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Poplar, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);

            world.Clear(x, yBase - 1); // 挖掉底下的土。
            StepTrunkColumn(world, x, yBase); // 一个逻辑帧内：基格失去扎根 → 整段级联倒下。

            Assert.AreEqual(0, CountType(world, TrunkId), "失去扎根应清掉整根树干");
            Assert.Greater(CountType(world, LeafId), 0, "树叶不当场消失（等自己的随机刻枯落）");
        }

        [Test]
        public void Trunk_UprootedSegment_FallsOnlyWhenDisconnected()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Pine, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);
            int top = TopTrunk(world, x, yBase);
            int cut = yBase + 6;

            world.Clear(x, cut); // 拦腰砍断：上段悬空。
            StepTrunkColumn(world, x, yBase);

            Assert.AreEqual(cut - yBase, CountType(world, TrunkId), "下段保留、上段整段倒下");
            Assert.AreEqual(TrunkId, world.GetCell(x, yBase).typeId);
            Assert.AreNotEqual(TrunkId, world.GetCell(x, top).typeId, "上段顶部应已消失");
        }

        [Test]
        public void Trunk_DiesOfHeat()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Poplar, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);
            int top = TopTrunk(world, x, yBase);

            world.Heat.SetHeat(x, top, 61.2f); // 热容 1.2 → T = 51 > 50。
            LogicTick(world, TrunkId, x, top);

            Assert.AreNotEqual(TrunkId, world.GetCell(x, top).typeId, "过热（T=51）应致死顶部树干");
            Assert.AreEqual(TreeSpecies.Poplar, ReadSpecies(world, x, top - 1), "下方树干不受影响");
        }

        // ============================== 树冠 ==============================

        [Test]
        public void Crown_MatchesSpeciesTableExactly_AllSpecies()
        {
            for (int species = 0; species < TreeSpecies.Count; species++)
            {
                TreeSpecies.TreeSpeciesInfo info = TreeSpecies.Info(species);
                ParticleWorld world = BuildTreeWorld(species, out int x, out int yBase);
                Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0, $"物种 {info.Name} 应成熟");

                int top = TopTrunk(world, x, yBase);
                Assert.AreEqual(info.Crown.Length, CountType(world, LeafId),
                    $"物种 {info.Name} 的叶数应正好等于表长（不多不少）");
                for (int i = 0; i < info.Crown.Length; i++)
                {
                    int lx = x + info.Crown[i].Dx;
                    int ly = top + info.Crown[i].Dy;
                    Assert.AreEqual(LeafId, world.GetCell(lx, ly).typeId,
                        $"物种 {info.Name} 的树冠项 {i} ({info.Crown[i].Dx},{info.Crown[i].Dy}) 应为树叶");
                    Assert.AreEqual(species, ReadSpecies(world, lx, ly), "树叶应带本树物种位");
                    int shade = world.GetCell(lx, ly).shade;
                    Assert.GreaterOrEqual(shade, species * TreeSpecies.LeafColorsPerSpecies);
                    Assert.Less(shade, (species + 1) * TreeSpecies.LeafColorsPerSpecies, "叶色应在本物种色片内");
                }

                // 无浮空叶：每片叶的八邻至少一格是树干 / 树叶（表自支撑在运行时兑现）。
                for (int ly = 0; ly < world.Height; ly++)
                {
                    for (int lx = 0; lx < world.Width; lx++)
                    {
                        if (world.GetCell(lx, ly).typeId != LeafId)
                        {
                            continue;
                        }

                        bool hasTreeNeighbour = false;
                        for (int dy = -1; dy <= 1 && !hasTreeNeighbour; dy++)
                        {
                            for (int dx = -1; dx <= 1 && !hasTreeNeighbour; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                {
                                    continue;
                                }

                                ushort id = world.GetCell(lx + dx, ly + dy).typeId;
                                hasTreeNeighbour = id == TrunkId || id == LeafId;
                            }
                        }

                        Assert.IsTrue(hasTreeNeighbour, $"物种 {info.Name}：({lx},{ly}) 的树叶浮空");
                    }
                }
            }
        }

        [Test]
        public void Crown_ShapeMetrics_DistinguishSpecies()
        {
            // 表侧指标（排序）：高 松 22 > 杨 19 > 橡 14 = 柏 14 > 柳 13；宽 橡 9 = 松 9 > 柳 7 > 柏 5 > 杨 3。
            int Total(int s)
            {
                TreeSpecies.TreeSpeciesInfo i = TreeSpecies.Info(s);
                return i.TrunkHeight + i.MaxCrownDy;
            }

            int Width(int s)
            {
                return 2 * TreeSpecies.Info(s).MaxHalfWidth + 1;
            }

            Assert.Greater(Total(TreeSpecies.Pine), Total(TreeSpecies.Poplar), "松最高");
            Assert.Greater(Total(TreeSpecies.Poplar), Total(TreeSpecies.Oak), "杨高于橡");
            Assert.AreEqual(Total(TreeSpecies.Oak), Total(TreeSpecies.Cypress), "橡与柏同高");
            Assert.Greater(Total(TreeSpecies.Oak), Total(TreeSpecies.Willow), "柳最矮");
            Assert.AreEqual(Width(TreeSpecies.Oak), Width(TreeSpecies.Pine), "橡与松最宽");
            Assert.Greater(Width(TreeSpecies.Willow), Width(TreeSpecies.Cypress), "柳比柏宽");
            Assert.Greater(Width(TreeSpecies.Cypress), Width(TreeSpecies.Poplar), "柏比杨宽");

            // 生长侧指标（每物种种一棵真树来验）：松锥形半宽随高单调不增、柏从低处长起、柳带垂枝、杨最窄。
            for (int species = 0; species < TreeSpecies.Count; species++)
            {
                ParticleWorld world = BuildTreeWorld(species, out int x, out int yBase);
                Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);
                int top = TopTrunk(world, x, yBase);

                int maxAbsDx = 0;
                int minRelativeDy = 0;
                bool hasDroopCell = false;
                int[] halfWidthByDy = new int[TreeSpecies.Info(species).MaxCrownDy + 1];
                for (int ly = 0; ly < world.Height; ly++)
                {
                    for (int lx = 0; lx < world.Width; lx++)
                    {
                        if (world.GetCell(lx, ly).typeId != LeafId)
                        {
                            continue;
                        }

                        int dx = lx - x;
                        int dy = ly - top;
                        int absDx = dx < 0 ? -dx : dx;
                        if (absDx > maxAbsDx)
                        {
                            maxAbsDx = absDx;
                        }

                        if (dy < minRelativeDy)
                        {
                            minRelativeDy = dy;
                        }

                        if (dy < 0 && absDx >= 2)
                        {
                            hasDroopCell = true;
                        }

                        if (dy >= 0 && absDx > halfWidthByDy[dy])
                        {
                            halfWidthByDy[dy] = absDx;
                        }
                    }
                }

                switch (species)
                {
                    case TreeSpecies.Pine:
                        for (int dy = 1; dy < halfWidthByDy.Length; dy++)
                        {
                            Assert.LessOrEqual(halfWidthByDy[dy], halfWidthByDy[dy - 1],
                                $"松冠半宽应随高单调不增（dy={dy}）");
                        }

                        break;
                    case TreeSpecies.Cypress:
                        Assert.LessOrEqual(minRelativeDy, -2, "柏的树冠应从树干下部开始（存在 dy ≤ -2 的叶）");
                        break;
                    case TreeSpecies.Willow:
                        Assert.IsTrue(hasDroopCell, "柳应存在下垂的枝条（dy < 0 且 |dx| ≥ 2 的叶）");
                        break;
                    case TreeSpecies.Poplar:
                        Assert.AreEqual(1, maxAbsDx, "杨是最窄的柱形冠（半宽 1）");
                        break;
                    default:
                        Assert.AreEqual(4, maxAbsDx, "橡是最宽的圆冠（半宽 4）");
                        break;
                }
            }
        }

        [Test]
        public void Crown_BlockedCellIsSkipped_AndRefilledAfterUnblocked()
        {
            ParticleWorld world = new ParticleWorld(48, 48, SeedValue);
            int x = 24;
            int yBase = 8;
            int tipY = yBase + TreeSpecies.Info(TreeSpecies.Poplar).TrunkHeight - 1;
            world.Set(x - 1, tipY, ParticleIds.Stone); // 堵住杨冠第一项 (-1,0) 的目标。

            PlantSeed(world, x, yBase, TreeSpecies.Poplar);
            Germinate(world, x, yBase);
            GrowFrames(world, x, yBase, 5000);

            int expected = TreeSpecies.Info(TreeSpecies.Poplar).Crown.Length;
            Assert.AreEqual(expected - 1, CountType(world, LeafId), "被挡的表项跳过，其余照长");
            Assert.AreNotEqual(TreeTrunkParticle.StageMature, ReadStage(world, x, tipY),
                "有未填充项时不得转成熟");
            Assert.AreEqual(ParticleIds.Stone, world.GetCell(x - 1, tipY).typeId, "挡块不得被覆盖");

            world.Clear(x - 1, tipY); // 腾空被挡项。
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0, "腾空后应补长完并转成熟");
            Assert.AreEqual(expected, CountType(world, LeafId), "补长后叶数应齐全");
            Assert.AreEqual(LeafId, world.GetCell(x - 1, tipY).typeId, "被挡项应被补上");
        }

        // ============================== 树叶 ==============================

        [Test]
        public void Leaf_ShedsWhenTrunkGone()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Poplar, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);
            int top = TopTrunk(world, x, yBase);

            for (int y = yBase; y <= top; y++)
            {
                world.Clear(x, y); // 直接清光树干（绕开级联，专测树叶支撑判定）。
            }

            for (int ly = 0; ly < world.Height; ly++)
            {
                for (int lx = 0; lx < world.Width; lx++)
                {
                    if (world.GetCell(lx, ly).typeId == LeafId)
                    {
                        RandomTick(world, LeafId, lx, ly);
                    }
                }
            }

            Assert.AreEqual(0, CountType(world, LeafId), "树干消失后树叶应在各自随机刻上枯落");
        }

        [Test]
        public void Leaf_IsPressedFromAbove_ButLeafOnLeafIsNot()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Poplar, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);
            int top = TopTrunk(world, x, yBase);

            int topLeafY = top + 7; // 杨冠最高项 (0,7)。
            world.Set(x, topLeafY + 1, ParticleIds.Stone); // 压住顶叶。
            RandomTick(world, LeafId, x, topLeafY);
            Assert.AreNotEqual(LeafId, world.GetCell(x, topLeafY).typeId, "正上方是固体应被压死");

            int lowerLeafY = top + 1; // (0,1)，正上方 (0,2) 是树叶。
            Assert.AreEqual(LeafId, world.GetCell(x, lowerLeafY + 1).typeId, "前置：正上方应是树叶");
            RandomTick(world, LeafId, x, lowerLeafY);
            Assert.AreEqual(LeafId, world.GetCell(x, lowerLeafY).typeId, "树叶叠树叶不算压");
        }

        [Test]
        public void Leaf_DiesOfHeat()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Poplar, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);
            int top = TopTrunk(world, x, yBase);
            int leafY = top + 7;

            world.Heat.SetHeat(x, leafY, 41.6f); // 热容 0.8 → T = 52 > 50。
            RandomTick(world, LeafId, x, leafY);

            Assert.AreNotEqual(LeafId, world.GetCell(x, leafY).typeId, "过热（T=52）应致树叶死亡");
        }

        // ============================== 整体 ==============================

        [Test]
        public void Tree_DoesNotMove()
        {
            ParticleWorld world = BuildTreeWorld(TreeSpecies.Willow, out int x, out int yBase);
            Assert.GreaterOrEqual(GrowToMaturity(world, x, yBase), 0);

            ushort[] before = new ushort[world.CellCount];
            for (int i = 0; i < world.CellCount; i++)
            {
                before[i] = world.Cells[i].typeId;
            }

            for (int i = 0; i < 100; i++)
            {
                world.StepMotion();
            }

            for (int i = 0; i < world.CellCount; i++)
            {
                Assert.AreEqual(before[i], world.Cells[i].typeId, $"格 {i} 不应移动");
            }
        }

        [Test]
        public void Tree_SeedFallsAndRests_OnSoil()
        {
            ParticleWorld world = new ParticleWorld(5, 8, SeedValue);
            world.Set(2, 0, ParticleIds.Soil);
            world.Set(2, 4, SeedId);
            TreeSpecies.Stamp(world, 2, 4, TreeSpecies.Pine, TreeSpecies.SeedColorsPerSpecies);

            for (int i = 0; i < 20; i++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(ParticleRegistry.AirTypeId, world.GetCell(2, 4).typeId, "种子应下落");
            Assert.AreEqual(SeedId, world.GetCell(2, 1).typeId, "种子应停在土面上");
            Assert.AreNotEqual(TrunkId, world.GetCell(2, 1).typeId, "只跑运动帧不应发芽");
        }

        [Test]
        public void Tree_Mature_ConsumesNoRandomStream()
        {
            ParticleWorld a = BuildTreeWorld(TreeSpecies.Cypress, out int x, out int yBase);
            ParticleWorld b = BuildTreeWorld(TreeSpecies.Cypress, out int x2, out int yBase2);
            int framesA = GrowToMaturity(a, x, yBase);
            int framesB = GrowToMaturity(b, x2, yBase2);
            Assert.AreEqual(framesA, framesB, "同种子的两棵柏树应逐帧同步成熟");
            Assert.GreaterOrEqual(framesA, 0);

            // 成熟后继续空转 2000 帧（只驱动 A）：随机流不得被推进。
            GrowFrames(a, x, yBase, 2000);

            Assert.AreEqual(b.NextUInt(), a.NextUInt(), "成熟后不得消耗随机流");
        }

        [Test]
        public void Tree_FullStepLogicReplay_IsIdentical()
        {
            // 真实 StepLogic 路径（混合扫描 + 随机刻）的整帧重放：两个同种子世界逐周期比对状态哈希，
            // 同时确认种子确实发芽、树确实长成（叶数 = 松树表长）。
            ParticleWorld a = BuildReplayWorld();
            ParticleWorld b = BuildReplayWorld();

            for (int cycle = 0; cycle < 800; cycle++)
            {
                a.StepMotion();
                b.StepMotion();
                a.StepMotion();
                b.StepMotion();
                a.StepLogic();
                b.StepLogic();
                Assert.AreEqual(StateHash(a), StateHash(b), $"第 {cycle} 周期的状态应逐位一致");
            }

            Assert.AreEqual(14, CountType(a, TrunkId), "重放世界里的松树应已长满树干");
            Assert.AreEqual(40, CountType(a, LeafId), "重放世界里的松树应已长满树冠");
        }

        private static ParticleWorld BuildReplayWorld()
        {
            var world = new ParticleWorld(30, 30, SeedValue);
            world.RandomTicksPerStep = 48; // 小世界里高频随机刻：种子几帧内就能发芽。
            world.Light.Enabled = false;
            world.Set(4, 0, ParticleIds.Soil);
            world.Set(4, 1, SeedId);
            TreeSpecies.Stamp(world, 4, 1, TreeSpecies.Pine, TreeSpecies.SeedColorsPerSpecies);
            return world;
        }
    }
}
