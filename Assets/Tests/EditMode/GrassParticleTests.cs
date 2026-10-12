using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 草的寿命系统：每逻辑帧衰老（年龄 = <see cref="Cell.shade"/> × <c>TicksPerShadeStep</c> + flags bit3-6），
    /// 每 <c>TicksPerShadeStep</c> 帧颜色向黄绿推进一档，年龄到 <see cref="GrassParticle.LifespanTicks"/>
    /// 寿终（走 <c>Clear</c> → 世界的有机衰变）。衰老不消耗世界随机流、不移动、不读邻居。
    /// </summary>
    /// <remarks>
    /// 走生产注册路径（跨类型的寿终残留是 <see cref="ParticleIds.Humus"/>，需要 id 回填）。
    /// 世界随机刻默认 0 ⇒ 压死 / 失去扎根 / 温度致死不会触发，衰老相关用例隔离出的唯一变量是**衰老**。
    /// 随机刻用例则直接调 <c>OnRandomTick</c>（仓库既有惯例），覆盖草种子的腐殖土发芽优待与草在腐殖土
    /// 家族脚下的放宽温度窗口（T = 2° 存活、52° 存活、56° 致死），搭景时保证上方无压、下方扎根。
    /// 腐殖土窗口把草**救活**时还会消耗脚下连续土层里最深的一格腐殖土（退化为普通土）——这也是随机刻用例。
    /// </remarks>
    public sealed class GrassParticleTests
    {
        private const uint SeedValue = 20261007u;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>8×8 世界里在 (4,4) 种一格草（悬空——本类不触发随机刻，扎根与否与本主题无关）。</summary>
        private static ParticleWorld BuildSingleGrass()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 4, ParticleIds.Grass);
            Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, 4).typeId);
            return world;
        }

        [Test]
        public void Grass_StartsAtTheYoungestShade()
        {
            ParticleWorld world = BuildSingleGrass();
            Assert.AreEqual(0, world.GetCell(4, 4).shade, "新生草应从最年轻的一档（shade = 0）开始");
        }

        [Test]
        public void Grass_ShadeAdvancesOneStepPerTicksPerShadeStep()
        {
            ParticleWorld world = BuildSingleGrass();

            for (int i = 0; i < GrassParticle.TicksPerShadeStep - 1; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(0, world.GetCell(4, 4).shade, "不满一档时颜色档不动");

            world.StepLogic();
            Assert.AreEqual(1, world.GetCell(4, 4).shade, "满一档时颜色档 +1");
        }

        [Test]
        public void Grass_DiesOnTheLifespanTick()
        {
            ParticleWorld world = BuildSingleGrass();

            for (int i = 0; i < GrassParticle.LifespanTicks - 1; i++)
            {
                world.StepLogic();
            }

            Cell before = world.GetCell(4, 4);
            Assert.AreEqual(ParticleIds.Grass, before.typeId, $"第 {GrassParticle.LifespanTicks - 1} 逻辑帧仍在寿命内");
            Assert.AreEqual(GrassParticle.ShadeCount - 1, before.shade, "寿终前一帧应已到最老颜色档");

            world.StepLogic();

            ushort after = world.GetCell(4, 4).typeId;
            Assert.AreNotEqual(ParticleIds.Grass, after, $"第 {GrassParticle.LifespanTicks} 逻辑帧应寿终");
            Assert.IsTrue(
                after == ParticleRegistry.AirTypeId || after == ParticleIds.Humus,
                $"寿终只允许空气或腐殖质（20% 有机衰变），实际 typeId={after}");
        }

        [Test]
        public void Grass_AgingDoesNotConsumeTheRandomStream()
        {
            ParticleWorld aged = BuildSingleGrass();
            ParticleWorld reference = BuildSingleGrass(); // 同布局、同种子，但不推进。
            for (int i = 0; i < 600; i++)
            {
                aged.StepLogic();
            }

            Assert.AreEqual(
                reference.NextUInt(), aged.NextUInt(),
                "衰老（及其所在逻辑帧的场 pass）不得消耗世界随机流");
        }

        [Test]
        public void Grass_RendersOlderAsYellowGreen()
        {
            Color32[] palette = ParticleRegistry.Types[ParticleIds.Grass].Info.Palette;
            Assert.AreEqual(GrassParticle.ShadeCount, palette.Length, "颜色档数应等于调色板长度");

            for (int i = 1; i < palette.Length; i++)
            {
                Assert.GreaterOrEqual(palette[i].r, palette[i - 1].r, $"调色板红分量应随年龄非递减（档 {i}）");
            }

            Assert.Greater(
                palette[palette.Length - 1].r - palette[palette.Length - 1].g,
                palette[0].r - palette[0].g,
                "最老档应比最年轻档更偏黄（红相对绿更高）");

            ParticleWorld young = BuildSingleGrass();
            ParticleWorld old = BuildSingleGrass();
            for (int i = 0; i < 600; i++)
            {
                old.StepLogic();
            }

            Color32 youngPixel = Render(young, 4, 4);
            Color32 oldPixel = Render(old, 4, 4);
            Assert.AreEqual(palette[young.GetCell(4, 4).shade], youngPixel, "渲染应按 shade 查调色板");
            Assert.AreEqual(palette[old.GetCell(4, 4).shade], oldPixel);
            Assert.Greater(
                oldPixel.r - oldPixel.g, youngPixel.r - youngPixel.g,
                "老草的像素应更偏黄绿（红相对绿更高）");
        }

        // ============================== 草种子发芽优待（A1） ==============================

        /// <summary>
        /// 草种子发芽优待：**八邻只有干腐殖土**、没有任何湿土时，随机刻命中即发芽成草
        /// （腐殖土保水，视作自带墒情——<see cref="GrassSeedParticle"/> 无发芽概率门）。
        /// 对照组：八邻只有干土（无湿土 / 腐殖土）时不发芽，只累计一次空转（既有路径）。
        /// </summary>
        [Test]
        public void GrassSeed_GerminatesFromDryHumusSoilNeighbour()
        {
            var humus = new ParticleWorld(8, 8, SeedValue);
            humus.Set(4, 4, ParticleIds.GrassSeed);
            humus.Set(5, 4, ParticleIds.HumusSoil); // 唯一的八邻，且是**干**腐殖土

            RandomTick(humus, ParticleIds.GrassSeed, 4, 4);

            ushort grown = humus.GetCell(4, 4).typeId;
            Assert.AreNotEqual(ParticleIds.GrassSeed, grown,
                "八邻有干腐殖土 ⇒ 本次随机刻必须发芽（无概率门）");
            Assert.IsTrue(
                grown == ParticleIds.Grass || grown == ParticleIds.Flower,
                $"发芽后本格应是草（或开花的株顶），实际 typeId={grown}");

            var dry = new ParticleWorld(8, 8, SeedValue);
            dry.Set(4, 4, ParticleIds.GrassSeed);
            dry.Set(3, 4, ParticleIds.Soil); // 八邻只有干土，无湿土 / 腐殖土
            dry.Set(5, 4, ParticleIds.Soil);

            RandomTick(dry, ParticleIds.GrassSeed, 4, 4);

            Assert.AreEqual(ParticleIds.GrassSeed, dry.GetCell(4, 4).typeId,
                "对照组：八邻只有干土 ⇒ 不发芽（进入既有空转路径，仅累计 1 次打击）");
        }

        // ============================== 草的温度耐受（B） ==============================

        /// <summary>8×8 世界里在 (4,4) 种一格扎根在 <paramref name="belowTypeId"/> 上的草，并注入温度。</summary>
        private static ParticleWorld BuildGrassOn(ushort belowTypeId, float temperature)
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 3, belowTypeId);
            world.Set(4, 4, ParticleIds.Grass);
            world.Heat.SetHeat(4, 4, temperature * ParticleRegistry.Types[ParticleIds.Grass].Info.HeatCapacity);
            return world;
        }

        /// <summary>
        /// 腐殖土保温：草脚下是腐殖土时低温耐受放宽到 0°——T = 2° 存活
        /// （对照组：脚下是普通土时 T = 2° 仍按 <see cref="GrassParticle.LethalColdTemperature"/>（3）致死）。
        /// 搭景保证上方无压（空气）、下方扎根，温度是唯一变量。
        /// </summary>
        [Test]
        public void Grass_OnHumusSoil_SurvivesTwoDegrees_WhereasOnSoilItDies()
        {
            ParticleWorld onHumus = BuildGrassOn(ParticleIds.HumusSoil, 2f);
            RandomTick(onHumus, ParticleIds.Grass, 4, 4);
            Assert.AreEqual(ParticleIds.Grass, onHumus.GetCell(4, 4).typeId,
                "草脚下是腐殖土：T = 2° 应存活（放宽窗口 < 0°）");

            ParticleWorld onSoil = BuildGrassOn(ParticleIds.Soil, 2f);
            RandomTick(onSoil, ParticleIds.Grass, 4, 4);
            Assert.AreNotEqual(ParticleIds.Grass, onSoil.GetCell(4, 4).typeId,
                "对照组：草脚下是普通土：T = 2° < 3° 应冷致死");
        }

        /// <summary>
        /// 腐殖土家族脚下草的高温耐受放宽到 55°：T = 52° 存活、T = 56° 死亡（> 55 才死，严格性不变）。
        /// </summary>
        [Test]
        public void Grass_OnHumusSoil_ToleratesUpToFiftyFiveDegrees()
        {
            ParticleWorld below = BuildGrassOn(ParticleIds.HumusSoil, 52f);
            RandomTick(below, ParticleIds.Grass, 4, 4);
            Assert.AreEqual(ParticleIds.Grass, below.GetCell(4, 4).typeId,
                "草脚下是腐殖土：T = 52° 应存活（放宽窗口 > 55°）");

            ParticleWorld above = BuildGrassOn(ParticleIds.HumusSoil, 56f);
            RandomTick(above, ParticleIds.Grass, 4, 4);
            Assert.AreNotEqual(ParticleIds.Grass, above.GetCell(4, 4).typeId,
                "草脚下是腐殖土：T = 56° > 55° 应热致死");
        }

        // ============================== 草种子发芽的腐殖土消耗（A2） ==============================

        /// <summary>
        /// 草种子发芽的腐殖土消耗：八邻只有干腐殖土（无真湿土）时，发芽成功会按「最深优先」把脚下连续
        /// 土层里**最深**的一格腐殖土退化成普通土；较浅、决定发芽的那格八邻腐殖土保持不动。
        /// </summary>
        [Test]
        public void GrassSeed_GerminatesOnHumusSoil_AndConsumesDeepestHumusSoil()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 4, ParticleIds.GrassSeed);
            world.Set(4, 3, ParticleIds.HumusSoil); // 决定发芽的八邻（浅处腐殖土）
            world.Set(4, 2, ParticleIds.Soil);      // 连续土层的中段
            world.Set(4, 1, ParticleIds.HumusSoil); // 连续土层里最深的腐殖土 → 应被消耗

            RandomTick(world, ParticleIds.GrassSeed, 4, 4);

            ushort grown = world.GetCell(4, 4).typeId;
            Assert.IsTrue(
                grown == ParticleIds.Grass || grown == ParticleIds.Flower,
                $"八邻有干腐殖土 ⇒ 本次随机刻必须发芽，实际 typeId={grown}");
            Assert.AreEqual(ParticleIds.Soil, world.GetCell(4, 1).typeId,
                "脚下连续土层里最深的腐殖土应退化成普通土");
            Assert.AreEqual(ParticleIds.HumusSoil, world.GetCell(4, 3).typeId,
                "较浅的腐殖土（决定发芽的八邻）不应被消耗");
        }

        /// <summary>
        /// 八邻有**真湿土**（WetSoil）时腐殖土不是决定性墒情：发芽成功但脚下腐殖土一格都不消耗。
        /// </summary>
        [Test]
        public void GrassSeed_WithTrueWetSoilNeighbour_GerminatesWithoutConsumingHumusSoil()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 4, ParticleIds.GrassSeed);
            world.Set(4, 3, ParticleIds.HumusSoil); // 八邻的干腐殖土
            world.Set(4, 2, ParticleIds.HumusSoil); // 更深的腐殖土
            world.Set(5, 4, ParticleIds.WetSoil);   // 真湿土：腐殖土不再是决定性因素

            RandomTick(world, ParticleIds.GrassSeed, 4, 4);

            ushort grown = world.GetCell(4, 4).typeId;
            Assert.IsTrue(
                grown == ParticleIds.Grass || grown == ParticleIds.Flower,
                $"八邻有湿土 ⇒ 应发芽，实际 typeId={grown}");
            Assert.AreEqual(ParticleIds.HumusSoil, world.GetCell(4, 3).typeId,
                "有真湿土时不消耗：浅处腐殖土保持不动");
            Assert.AreEqual(ParticleIds.HumusSoil, world.GetCell(4, 2).typeId,
                "有真湿土时不消耗：深处腐殖土也保持不动");
        }

        // ============================== 腐殖土优待的消耗（C） ==============================

        /// <summary>
        /// 消耗型优待：草脚下是**连续土层** [(4,3)=腐殖土 → (4,2)=土 → (4,1)=腐殖土]，
        /// T = 2° 只有腐殖土窗口能救活 ⇒ 存活，且消耗**最深（离植株最远）**那一格 (4,1)（变普通土）；
        /// 紧贴脚下的 (4,3) 仍是腐殖土（距离近的一格不先被吃）。
        /// </summary>
        [Test]
        public void Grass_OnHumusSoil_SurvivingColdConsumesTheDeepestHumusSoil()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 1, ParticleIds.HumusSoil); // 最深（离植株最远）
            world.Set(4, 2, ParticleIds.Soil);      // 连续土层里的普通土：扫描穿过
            world.Set(4, 3, ParticleIds.HumusSoil); // 紧贴脚下
            world.Set(4, 4, ParticleIds.Grass);
            world.Heat.SetHeat(4, 4, 2f * ParticleRegistry.Types[ParticleIds.Grass].Info.HeatCapacity);

            RandomTick(world, ParticleIds.Grass, 4, 4);

            Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, 4).typeId,
                "脚下是腐殖土家族：T = 2° 应被救活（放宽窗口 < 0°）");
            Assert.AreEqual(ParticleIds.Soil, world.GetCell(4, 1).typeId,
                "最深的腐殖土 (4,1) 应被消耗、退化为普通土");
            Assert.AreEqual(ParticleIds.HumusSoil, world.GetCell(4, 3).typeId,
                "紧贴脚下的腐殖土 (4,3) 保留（消耗的是最深那一格）");
        }

        /// <summary>
        /// 普通窗口内存活（T = 25°）**不**触发消耗：腐殖土优待没被用上，连续土层原样保留。
        /// </summary>
        [Test]
        public void Grass_OnHumusSoil_SurvivingWithinNaturalWindowDoesNotConsume()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 1, ParticleIds.HumusSoil);
            world.Set(4, 2, ParticleIds.Soil);
            world.Set(4, 3, ParticleIds.HumusSoil);
            world.Set(4, 4, ParticleIds.Grass);
            world.Heat.SetHeat(4, 4, 25f * ParticleRegistry.Types[ParticleIds.Grass].Info.HeatCapacity);

            RandomTick(world, ParticleIds.Grass, 4, 4);

            Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, 4).typeId,
                "T = 25° 在普通窗口内存活（无需腐殖土放宽）");
            Assert.AreEqual(ParticleIds.HumusSoil, world.GetCell(4, 1).typeId,
                "普通窗口下存活不消耗腐殖土");
            Assert.AreEqual(ParticleIds.HumusSoil, world.GetCell(4, 3).typeId,
                "普通窗口下存活不消耗腐殖土");
        }

        /// <summary>
        /// 脚下单层湿润腐殖土、T = 52°（普通窗口下会热死，腐殖土窗口下存活）⇒ 存活并消耗该格：
        /// 湿润腐殖土 → 湿土（<see cref="ParticleWorld.SetPhase"/> 保水），世界水账不变。
        /// </summary>
        [Test]
        public void Grass_OnWetHumusSoil_SurvivingHeatConsumesItIntoWetSoilAndConservesWater()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 3, ParticleIds.WetHumusSoil);
            world.Set(4, 4, ParticleIds.Grass);
            world.Heat.SetHeat(4, 4, 52f * ParticleRegistry.Types[ParticleIds.Grass].Info.HeatCapacity);

            float waterBefore = TotalWater(world);

            RandomTick(world, ParticleIds.Grass, 4, 4);

            Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, 4).typeId,
                "脚下是湿腐殖土：T = 52° 应被救活（放宽窗口 > 55°）");
            Assert.AreEqual(ParticleIds.WetSoil, world.GetCell(4, 3).typeId,
                "湿腐殖土被消耗后退化为湿土（仍是水格）");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-4f,
                "换形态水账不变（湿腐殖土与湿土同样计入水账）");
        }

        /// <summary>把世界映射成像素缓冲并读回 (x,y) 的单格颜色（纯 CPU，不需要 GPU）。</summary>
        private static Color32 Render(ParticleWorld world, int x, int y)
        {
            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, default(Color32));
            return pixels[world.Index(x, y)];
        }

        private static void RandomTick(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnRandomTick(world, x, y);
        }

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>水总量：水格 + 湿土格 + 湿润腐殖土格（各含一份水）+ 湿度场。</summary>
        private static float TotalWater(ParticleWorld world)
        {
            return CountType(world, ParticleIds.Water) + CountType(world, ParticleIds.WetSoil)
                + CountType(world, ParticleIds.WetHumusSoil) + world.Humidity.Sum();
        }
    }
}
