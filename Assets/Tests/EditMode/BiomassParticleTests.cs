using System.Text.RegularExpressions;
using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;
using UnityEngine.TestTools;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 生物质（<see cref="BiomassParticle"/>）的行为契约：固体（**不带 Organic**）、受重力堆积、
    /// 落稳计数饱和后每逻辑帧 1/200 的 70/30 腐败，以及它依赖的两条世界 API
    /// （<see cref="ParticleWorld.SetWithShade"/> 与 <see cref="CellFlags.BiomassSettleMask"/>）。
    /// </summary>
    /// <remarks>
    /// <para>跨类型引用（<see cref="ParticleIds.Biomass"/> / <see cref="ParticleIds.Humus"/>）依赖注册回填，
    /// 因此全部走 <see cref="ParticleRegistration.RegisterAll"/> 的生产类型表（<c>new ParticleWorld(w, h, seed)</c>），
    /// 与 <c>LightningParticleTests</c> / <c>HumusSoilParticleTests</c> 一致。</para>
    /// <para>概率门不 mock 世界：腐败要精确命中「第一次判定」这一帧，做法是**扫 seed**——同一 seed 下从
    /// 建世界到落稳的随机流消耗完全确定，用一只同 seed 的**探针世界**复刻同样的历史即可读出真实世界
    /// 接下来两次抽取的值（见 <see cref="PeekCorruptionDraws"/>）。</para>
    /// </remarks>
    public sealed class BiomassParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ushort Biomass
        {
            get { return ParticleIds.Biomass; }
        }

        private static ParticleTypeInfo BiomassInfo
        {
            get { return ParticleRegistry.Types[ParticleIds.Biomass].Info; }
        }

        // ============================== 夹具 ==============================

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

        /// <summary>(x, ·) 列里生物质所在的 y（自下而上第一个），没有则 −1。</summary>
        private static int LowestBiomassY(ParticleWorld world, int x)
        {
            for (int y = 0; y < world.Height; y++)
            {
                if (world.GetCell(x, y).typeId == Biomass)
                {
                    return y;
                }
            }

            return -1;
        }

        private static void StepMotion(ParticleWorld world, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                world.StepMotion();
            }
        }

        // ============================== 注册 / 属性 ==============================

        /// <summary>
        /// 生产注册路径：<see cref="BiomassParticle"/> 只能**追加**在清单末尾（紧随雷电之后），
        /// 且带 Solid 但**不带 Organic**（自带腐败流程，不能再叠加世界那道 20% 通用衰变骰）、
        /// 受重力、非静态、<c>hasLogic</c> 为真、运动阅读盒 (1, 0, 3)、调色板 6 档。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsBiomassSolidWithoutOrganic()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, Biomass, "Biomass 必须被回填（非 0）");
            Assert.AreEqual((ushort)(ParticleIds.Lightning + 1), Biomass, "Biomass 必须紧随雷电追加在清单末尾");

            ParticleTypeInfo info = BiomassInfo;
            Assert.AreEqual("生物质", info.DebugName);
            Assert.IsTrue((info.Tags & ParticleTags.Solid) != 0, "生物质是固体");
            Assert.AreEqual(0, (int)(info.Tags & ParticleTags.Organic), "生物质不得带 Organic（避免重复衰变骰）");
            Assert.IsTrue(info.AffectedByGravity, "生物质受重力");
            Assert.IsFalse(info.IsStatic, "生物质必须参与扫描（否则不动、也收不到逻辑帧）");
            Assert.IsTrue(info.HasLogic, "OnLogic 有真实行为（落稳计数 + 腐败），必须保持 hasLogic: true");
            MotionReadBox reach = info.MotionReadReach;
            Assert.AreEqual(1, (int)reach.X, "运动阅读盒的水平半径");
            Assert.AreEqual(1, (int)reach.Up, "运动阅读盒的向上半径：浮力读正上 1 格");
            Assert.AreEqual(3, (int)reach.Down, "运动阅读盒的向下半径（0.5/0.5 的斜滑净空链 3 格）");
            Assert.AreEqual(6, info.Palette.Length, "调色板是 6 档灰阶（死亡轮廓的颜色索引 0..5）");
            Assert.IsFalse(info.HasOutline, "生物质不描边");
        }

        // ============================== SetWithShade ==============================

        /// <summary>
        /// <see cref="ParticleWorld.SetWithShade"/>：写入调用方给的颜色索引（不随机抽样、不消耗随机流）；
        /// <c>shade</c> 越界则整次写入被拒绝、该格一字节不动，且每个世界至多报一条错。
        /// </summary>
        [Test]
        public void SetWithShade_WritesExplicitShade()
        {
            ParticleWorld world = new ParticleWorld(4, 4, 20261011u);

            Assert.IsTrue(world.SetWithShade(1, 1, Biomass, 4), "显式 shade 应写入成功");
            Assert.AreEqual(Biomass, world.GetCell(1, 1).typeId);
            Assert.AreEqual(4, (int)world.GetCell(1, 1).shade, "shade 必须等于调用方给的值");

            Assert.IsTrue(world.SetWithShade(2, 1, Biomass, 0), "shade 0（最深档）也是合法值");
            Assert.AreEqual(0, (int)world.GetCell(2, 1).shade);

            // 不消耗世界随机流：两只同 seed 的世界，只有一只调过 SetWithShade，后续序列仍逐位一致。
            ParticleWorld untouched = new ParticleWorld(4, 4, 20261011u);
            ParticleWorld writer = new ParticleWorld(4, 4, 20261011u);
            writer.SetWithShade(1, 1, Biomass, 4);
            writer.SetWithShade(2, 1, Biomass, 0);
            for (int i = 0; i < 8; i++)
            {
                Assert.AreEqual(untouched.NextUInt(), writer.NextUInt(), "SetWithShade 不得消耗世界随机流");
            }

            // shade 越界：拒绝写入并报错一次；该格保持不变（仍是空气）。
            ParticleWorld rejecting = new ParticleWorld(4, 4, 20261011u);
            byte outOfRange = (byte)BiomassInfo.Palette.Length;   // 6 档 ⇒ 6 越界
            LogAssert.Expect(LogType.Error, new Regex("SetWithShade 的 shade"));
            Assert.IsFalse(rejecting.SetWithShade(1, 1, Biomass, outOfRange), "越界 shade 应被拒绝");
            Assert.IsTrue(rejecting.IsAir(1, 1), "越界 shade 必须整次拒绝，该格不得被写入");

            // 「每世界至多一条 LogError」：第二次越界写入静默拒绝（若再报错，多出的 Error 会让本测试失败）。
            Assert.IsFalse(rejecting.SetWithShade(2, 2, Biomass, 255));
            Assert.IsTrue(rejecting.IsAir(2, 2));
        }

        // ============================== 下落与堆积 ==============================

        private static ParticleWorld BuildPileWorld(uint seed)
        {
            ParticleWorld world = new ParticleWorld(7, 12, seed);
            world.RandomTicksPerStep = 0;
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, ParticleIds.Stone);
            }

            world.Set(4, 7, Biomass);
            world.Set(4, 8, Biomass);
            world.Set(5, 8, Biomass);
            return world;
        }

        /// <summary>
        /// 三格悬空生物质在运动帧里落到石面上并堆起来：每一格都踩在固体上（无悬空）、最低一格压在石面上，
        /// 且同 seed 两次运行的整张网格逐位一致。
        /// </summary>
        [Test]
        public void Biomass_FallsAndPilesOnGround()
        {
            ParticleWorld first = BuildPileWorld(4321u);
            ParticleWorld second = BuildPileWorld(4321u);

            StepMotion(first, 240);
            StepMotion(second, 240);

            Assert.AreEqual(3, CountType(first, Biomass), "三格生物质都应还在（只跑了运动帧，不会腐败）");

            int lowest = int.MaxValue;
            for (int y = 0; y < first.Height; y++)
            {
                for (int x = 0; x < first.Width; x++)
                {
                    if (first.GetCell(x, y).typeId != Biomass)
                    {
                        continue;
                    }

                    Assert.IsTrue(ParticleRules.IsSolid(first, x, y - 1), $"({x},{y}) 的生物质应踩在固体上（不得悬空）");
                    if (y < lowest)
                    {
                        lowest = y;
                    }
                }
            }

            Assert.AreEqual(1, lowest, "最低一格生物质应直接落在石面（y = 1，石面在 y = 0）");
            Assert.IsTrue(first.GetCell(4, 8).typeId != Biomass, "原来悬空的位置应已腾空（确实下落过）");

            // 逐位可复现：同 seed 两次运行的每一格（类型 / flags / shade）都必须一致。
            for (int i = 0; i < first.CellCount; i++)
            {
                Assert.AreEqual(first.Cells[i].typeId, second.Cells[i].typeId, $"第 {i} 格的 typeId 应逐位一致");
                Assert.AreEqual(first.Cells[i].flags, second.Cells[i].flags, $"第 {i} 格的 flags 应逐位一致");
                Assert.AreEqual(first.Cells[i].shade, second.Cells[i].shade, $"第 {i} 格的 shade 应逐位一致");
            }
        }

        /// <summary>
        /// 生物质比水轻、**浮在水面**（水只被挤开、不被湮灭）：落入水柱时停在液面上一格；
        /// 在水里生成的生物质沿水柱上浮到液面。沙 / 石仍是支撑（生物质停在它们上面、不覆盖它们）。
        /// </summary>
        [Test]
        public void Biomass_FloatsOnWater_ButNotThroughRock()
        {
            // 单列世界：水不会横向摊开，浮沉路径完全确定。
            ParticleWorld floating = new ParticleWorld(1, 8, 77u);
            floating.Set(0, 0, ParticleIds.Stone);
            floating.Set(0, 1, ParticleIds.Water);
            floating.Set(0, 2, ParticleIds.Water);
            floating.Set(0, 3, ParticleIds.Water);
            floating.StepMotion();                        // tick ≥ 1：此后放置的物质视作「运动中」
            floating.Set(0, 5, Biomass);
            StepMotion(floating, 60);

            Assert.AreEqual(Biomass, floating.GetCell(0, 4).typeId, "水是浮力面：生物质应停在液面上一格（y = 4，水面在 y = 3）");
            Assert.AreEqual(ParticleIds.Water, floating.GetCell(0, 3).typeId, "水面那格仍是水");
            Assert.AreEqual(ParticleIds.Stone, floating.GetCell(0, 0).typeId, "石面不得被覆盖");
            Assert.AreEqual(3, CountType(floating, ParticleIds.Water), "水只被排开、不得被湮灭");

            // 在水里生成（顶掉 y = 1 那格水）：应沿水柱上浮到液面 (0, 3)。
            ParticleWorld rising = new ParticleWorld(1, 8, 77u);
            rising.Set(0, 0, ParticleIds.Stone);
            rising.Set(0, 1, ParticleIds.Water);
            rising.Set(0, 2, ParticleIds.Water);
            rising.Set(0, 3, ParticleIds.Water);
            rising.Set(0, 1, Biomass);
            StepMotion(rising, 60);

            Assert.AreEqual(Biomass, rising.GetCell(0, 3).typeId, "水里的生物质应上浮到液面（y = 3）");
            Assert.AreEqual(ParticleIds.Stone, rising.GetCell(0, 0).typeId, "石面不得被覆盖");
            Assert.AreEqual(2, CountType(rising, ParticleIds.Water), "水只被换位、不得被湮灭");

            ParticleWorld blocked = new ParticleWorld(1, 8, 77u);
            blocked.Set(0, 0, ParticleIds.Stone);
            blocked.Set(0, 1, ParticleIds.Sand);
            blocked.StepMotion();
            blocked.Set(0, 5, Biomass);
            StepMotion(blocked, 60);

            Assert.AreEqual(Biomass, blocked.GetCell(0, 2).typeId, "沙不可穿：生物质应停在沙上");
            Assert.AreEqual(ParticleIds.Sand, blocked.GetCell(0, 1).typeId, "沙不得被覆盖");
            Assert.AreEqual(ParticleIds.Stone, blocked.GetCell(0, 0).typeId, "石面不得被覆盖");
        }

        /// <summary>
        /// 浮在液面的生物质**算落稳**：落稳计数逐帧 +1 并饱和在 <see cref="BiomassParticle.SettleFrames"/>。
        /// 因此水面上的尸体会正常腐败（饱和当帧起就可能摇腐败骰）。
        /// 直接调 <see cref="ParticleType.OnLogic"/>：避开逻辑帧里水的热 / 湿场对落稳判定的干扰。
        /// </summary>
        [Test]
        public void Biomass_FloatingOnWater_CountsAsSettled()
        {
            ParticleWorld world = new ParticleWorld(1, 5, 4242u);
            world.RandomTicksPerStep = 0;
            world.Set(0, 0, ParticleIds.Stone);
            world.Set(0, 1, ParticleIds.Water);
            world.Set(0, 2, ParticleIds.Water);
            world.Set(0, 3, Biomass);

            StepMotion(world, 4);
            Assert.AreEqual(Biomass, world.GetCell(0, 3).typeId, "前置：生物质应浮在水面 (0, 3)");

            ParticleType biomass = ParticleRegistry.Types[ParticleIds.Biomass];
            for (int frame = 1; frame < BiomassParticle.SettleFrames; frame++)
            {
                biomass.OnLogic(world, 0, 3);
                Assert.AreEqual(Biomass, world.GetCell(0, 3).typeId, $"第 {frame} 帧还没饱和，不得腐败");
                Assert.AreEqual(frame, (int)BiomassParticle.SettleCount(world, 0, 3), $"第 {frame} 逻辑帧的落稳计数应为 {frame}");
            }

            // 饱和当帧起就可能摇腐败骰：计数饱和在 31，或这格已经不是生物质（腐败已结算）。
            biomass.OnLogic(world, 0, 3);
            Assert.IsTrue(
                (int)BiomassParticle.SettleCount(world, 0, 3) == BiomassParticle.SettleFrames
                    || world.GetCell(0, 3).typeId != Biomass,
                "浮在液面应算落稳：计数应饱和在 31，或该格已完成腐败");
        }

        // ============================== 落稳与随机流 ==============================

        private static ParticleWorld BuildFallingWorld(uint seed, bool withBiomass)
        {
            ParticleWorld world = new ParticleWorld(7, 60, seed);
            world.RandomTicksPerStep = 0;
            world.Light.Enabled = false;
            if (withBiomass)
            {
                // 用 SetWithShade 放置：显式 shade 不抽样，两只世界的随机流因此从同一位置起跑。
                world.SetWithShade(3, 55, Biomass, 3);
            }

            return world;
        }

        /// <summary>
        /// 悬空生物质（正下方没有支撑）**一个随机数都不消耗**：与「同一只世界但少一格生物质」逐帧比对
        /// <see cref="ParticleWorld.NextUInt"/>，整段序列逐位一致。
        /// </summary>
        /// <remarks>只调 <see cref="ParticleWorld.StepLogic"/>：它自带一次运动扫描（候选格先 OnLogic 再 OnMotion），
        /// 因此生物质确实在下落，而它在这 30 帧里始终没有支撑（世界底部没有地板）。</remarks>
        [Test]
        public void OnLogic_WhileFalling_DoesNotConsumeRng()
        {
            ParticleWorld reference = BuildFallingWorld(20261011u, false);
            ParticleWorld falling = BuildFallingWorld(20261011u, true);

            for (int frame = 0; frame < 30; frame++)
            {
                reference.StepLogic();
                falling.StepLogic();

                Assert.AreEqual(reference.NextUInt(), falling.NextUInt(), $"第 {frame} 帧之后的随机流应逐位一致");
            }

            int y = LowestBiomassY(falling, 3);
            Assert.Greater(y, 0, "生物质应仍悬在半空（世界够高，30 帧落不到底）");
            Assert.Less(y, 55, "生物质应确实下落过，本测试覆盖的才是「下落中」这条路径");
            Assert.AreEqual(0, (int)BiomassParticle.SettleCount(falling, 3, y), "悬空不得累计落稳帧数");
        }

        /// <summary>
        /// 落到石面上后落稳计数逐帧 +1 并饱和在 <see cref="BiomassParticle.SettleFrames"/>（= 31）；
        /// 满 31 帧之前不会腐败。
        /// </summary>
        [Test]
        public void SettleCounter_SaturatesAt31()
        {
            ParticleWorld world = new ParticleWorld(3, 5, 909u);
            world.RandomTicksPerStep = 0;
            world.Set(1, 0, ParticleIds.Stone);
            world.SetWithShade(1, 1, Biomass, 2);

            for (int frame = 1; frame <= BiomassParticle.SettleFrames; frame++)
            {
                world.StepLogic();

                Assert.AreEqual(frame, (int)BiomassParticle.SettleCount(world, 1, 1), $"第 {frame} 逻辑帧的落稳计数应为 {frame}");
                Assert.AreEqual(Biomass, world.GetCell(1, 1).typeId, "满 31 帧之前不得腐败");
            }

            Assert.AreEqual(BiomassParticle.SettleFrames, (int)BiomassParticle.SettleCount(world, 1, 1), "落稳计数应饱和在 31");

            // 饱和后继续跑：计数不再增长（这一帧起才开始摇腐败骰，因此两种结局都合法）。
            for (int frame = 0; frame < 3; frame++)
            {
                world.StepLogic();
                Assert.IsTrue(BiomassParticle.SettleCount(world, 1, 1) == BiomassParticle.SettleFrames
                        || world.GetCell(1, 1).typeId != Biomass,
                    "饱和后的计数不得再增长（除非这一帧已经腐败）");
            }
        }

        // ============================== 腐败（扫 seed 命中固定分支） ==============================

        /// <summary>
        /// 一只「生物质已落稳在石面上」的小世界（中心格正下方是石头）。
        /// 同一个 seed 下从建世界到落稳的随机流消耗完全确定（每格 <c>Set</c> 抽一次 shade、
        /// <c>SetWithShade</c> 与落稳帧都不抽），因此下一次 <c>OnLogic</c> 会摇到哪两个数是唯一确定的。
        /// </summary>
        private static ParticleWorld BuildSettledBiomassWorld(uint seed, out int bx, out int by)
        {
            ParticleWorld world = new ParticleWorld(3, 3, seed);
            world.RandomTicksPerStep = 0;
            for (int x = 0; x < 3; x++)
            {
                world.Set(x, 0, ParticleIds.Stone);
            }

            bx = 1;
            by = 1;
            world.SetWithShade(bx, by, Biomass, 3);
            for (int i = 0; i < BiomassParticle.SettleFrames; i++)
            {
                world.StepLogic();
            }

            return world;
        }

        /// <summary>用一只**同 seed 的探针世界**复刻同样的历史，读出真实世界接下来两次抽取会拿到的值。</summary>
        private static void PeekCorruptionDraws(uint seed, out uint gate, out uint branch)
        {
            ParticleWorld probe = BuildSettledBiomassWorld(seed, out _, out _);
            gate = probe.NextUInt();
            branch = probe.NextUInt();
        }

        /// <summary>扫 seed 找一个「第一次腐败判定命中、且分支落在指定侧」的种子；找不到返回 0。</summary>
        private static uint FindSeed(bool expectHumus)
        {
            for (uint seed = 1u; seed <= 200000u; seed++)
            {
                PeekCorruptionDraws(seed, out uint gate, out uint branch);
                if (gate % BiomassParticle.CorruptionDenominator != 0u)
                {
                    continue;
                }

                bool humus = branch % BiomassParticle.HumusBranchDenominator < BiomassParticle.HumusBranchNumerator;
                if (humus == expectHumus)
                {
                    return seed;
                }
            }

            return 0u;
        }

        /// <summary>腐败命中且分支 &lt; 7/10 → 该格显式相变成腐殖质（且只产出一格）。</summary>
        [Test]
        public void Decay_ConvertsToHumus_ByRng()
        {
            uint seed = FindSeed(true);
            Assert.AreNotEqual(0u, seed, "应能扫到一个「腐败门命中且分支 < 7/10」的种子");

            ParticleWorld world = BuildSettledBiomassWorld(seed, out int bx, out int by);
            Assert.AreEqual(BiomassParticle.SettleFrames, (int)BiomassParticle.SettleCount(world, bx, by), "前置：生物质应已落稳");

            world.StepLogic();   // 落稳后的第一次腐败判定

            Assert.AreEqual(ParticleIds.Humus, world.GetCell(bx, by).typeId, "分支 < 7/10 ⇒ 该格变腐殖质");
            Assert.AreEqual(1, CountType(world, ParticleIds.Humus), "一次腐败只结算一格腐殖质");
            Assert.AreEqual(0, CountType(world, Biomass), "本格已不再是生物质");
        }

        /// <summary>腐败命中且分支 ≥ 7/10 → 该格直接消散（Clear，不留腐殖质）。</summary>
        [Test]
        public void Decay_Vanishes_ByRng()
        {
            uint seed = FindSeed(false);
            Assert.AreNotEqual(0u, seed, "应能扫到一个「腐败门命中且分支 ≥ 7/10」的种子");

            ParticleWorld world = BuildSettledBiomassWorld(seed, out int bx, out int by);
            Assert.AreEqual(BiomassParticle.SettleFrames, (int)BiomassParticle.SettleCount(world, bx, by), "前置：生物质应已落稳");

            world.StepLogic();

            Assert.IsTrue(world.IsAir(bx, by), "分支 ≥ 7/10 ⇒ 该格消散为空");
            Assert.AreEqual(0, CountType(world, ParticleIds.Humus), "消散分支不留腐殖质");
            Assert.AreEqual(0, CountType(world, Biomass));
        }

        /// <summary>
        /// 生物质**不带 Organic**：被石头覆盖 / 被 <see cref="ParticleWorld.Clear"/> 抹除时都不会触发
        /// 世界那道 20% 的通用腐殖质骰（200 格规模下若带 Organic 几乎必然出现腐殖质）；
        /// 腐殖质被覆盖同样不产生第二个产物。
        /// </summary>
        [Test]
        public void Decay_DoesNotStackOrganicDecay()
        {
            Assert.AreEqual(0, (int)(BiomassInfo.Tags & ParticleTags.Organic), "生物质不得带 Organic 标签");

            ParticleWorld buried = new ParticleWorld(200, 3, 181818u);
            for (int x = 0; x < 200; x++)
            {
                Assert.IsTrue(buried.Set(x, 1, Biomass));
            }

            for (int x = 0; x < 200; x++)
            {
                Assert.IsTrue(buried.Set(x, 1, ParticleIds.Stone));
            }

            Assert.AreEqual(0, CountType(buried, ParticleIds.Humus), "覆盖生物质不得触发通用 Organic 衰变骰");

            ParticleWorld erased = new ParticleWorld(200, 3, 181818u);
            for (int x = 0; x < 200; x++)
            {
                erased.Set(x, 1, Biomass);
            }

            for (int x = 0; x < 200; x++)
            {
                Assert.IsTrue(erased.Clear(x, 1));
            }

            Assert.IsTrue(erased.IsAir(0, 1));
            Assert.AreEqual(0, CountType(erased, ParticleIds.Humus), "抹除生物质不得触发通用 Organic 衰变骰");

            ParticleWorld coveredHumus = new ParticleWorld(4, 4, 181818u);
            coveredHumus.Set(1, 1, ParticleIds.Humus);
            coveredHumus.Set(1, 1, ParticleIds.Stone);
            Assert.AreEqual(ParticleIds.Stone, coveredHumus.GetCell(1, 1).typeId);
            Assert.AreEqual(0, CountType(coveredHumus, ParticleIds.Humus), "覆盖腐殖质不得再产出第二格腐殖质");
        }

        // ============================== 世界存量 ==============================

        /// <summary>
        /// <see cref="ParticleWorld.BiomassCount"/>：空世界为 0；<see cref="ParticleWorld.Set"/> /
        /// <see cref="ParticleWorld.SetWithShade"/> 写入生物质递增；换成非生物质（Set / SetPhase）递减；
        /// <see cref="ParticleWorld.Clear"/> 递减；<see cref="ParticleWorld.Swap"/> 不改变任何类型的存量。
        /// 计数来自 <see cref="ParticleCountMonitor"/> 的增量记账，不需要逐帧扫描。
        /// </summary>
        [Test]
        public void BiomassCount_TracksWorldStock()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 20261011u);
            Assert.AreEqual(0, world.BiomassCount, "空世界没有生物质");

            Assert.IsTrue(world.SetWithShade(1, 1, Biomass, 3), "SetWithShade 应写入生物质");
            Assert.AreEqual(1, world.BiomassCount, "SetWithShade 写入生物质应 +1");

            Assert.IsTrue(world.Set(2, 1, Biomass));
            Assert.AreEqual(2, world.BiomassCount, "Set 写入生物质应 +1");

            Assert.IsTrue(world.Set(2, 1, ParticleIds.Stone));
            Assert.AreEqual(1, world.BiomassCount, "Set 换成非生物质应 −1");

            Assert.IsTrue(world.SetPhase(1, 1, ParticleIds.Stone));
            Assert.AreEqual(0, world.BiomassCount, "SetPhase 换成非生物质应 −1");

            Assert.IsTrue(world.Set(3, 2, Biomass));
            Assert.AreEqual(1, world.BiomassCount);
            Assert.IsTrue(world.Clear(3, 2));
            Assert.AreEqual(0, world.BiomassCount, "Clear（= Set 空气）应 −1");

            Assert.IsTrue(world.Set(4, 4, Biomass));
            Assert.IsTrue(world.Set(5, 4, ParticleIds.Stone));
            Assert.AreEqual(1, world.BiomassCount);
            Assert.IsTrue(world.Swap(4, 4, 5, 4));
            Assert.AreEqual(Biomass, world.GetCell(5, 4).typeId, "Swap 应把生物质换到另一端");
            Assert.AreEqual(1, world.BiomassCount, "Swap 只交换两格，不改变存量");
        }

        // ============================== 渲染映射 ==============================

        /// <summary>
        /// <see cref="ParticlePixelBuffer.Fill"/> 对生物质格只做 shade → 调色板查表：像素色必须等于
        /// <c>Info.Palette[shade]</c>（生物质不描边、无液面高光，因此没有任何额外调制）。
        /// </summary>
        [Test]
        public void Palette_MapsToPixelBuffer()
        {
            ParticleTypeInfo info = BiomassInfo;
            Assert.AreEqual(6, info.Palette.Length);

            ParticleWorld world = new ParticleWorld(6, 3, 246u);
            for (int shade = 0; shade < info.Palette.Length; shade++)
            {
                Assert.IsTrue(world.SetWithShade(shade, 1, Biomass, (byte)shade));
            }

            var pixels = new RenderColor[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, new RenderColor(9, 8, 7, 255));

            for (int shade = 0; shade < info.Palette.Length; shade++)
            {
                Assert.AreEqual(
                    RenderColorConversion.FromColor32(info.Palette[shade]),
                    pixels[world.Index(shade, 1)],
                    $"shade {shade} 的像素色应等于调色板对应档");
            }
        }
    }
}
