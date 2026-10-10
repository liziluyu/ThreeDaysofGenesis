using System.Collections.Generic;
using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 花（<see cref="FlowerParticle"/>）：草种子发芽时以 1/10 概率把**株顶那格**草换成花。
    /// 花继承草的全部性质（Solid + Organic、长在原地、热学 / 光照同值、随机刻三条死法：被压 / 失去扎根 /
    /// 温度 &gt; 50 或 &lt; 3，死亡各按 20% 留腐殖质、可燃），差别只有颜色（更鲜艳、每朵随机、终生不变）
    /// 与两项刻意去掉的行为（不衰老、不撒种 / 不固沙）。
    /// </summary>
    /// <remarks>
    /// 走生产注册路径（跨类型的株内判据与腐殖质残留都依赖 <see cref="ParticleIds"/> 回填）。
    /// <para>**为什么直接调 <c>OnRandomTick</c>**：随机刻里的温度致死与草的低温线（&lt; 3°）会杀掉
    /// 裸世界的草株（世界默认 Q = 0 ⇒ T = 0），因此先给参与的格显式注热（Q = 20 ⇒ T = 25°）、
    /// 再直接调钩子，把变量隔离到「被压 / 扎根 / 温度」三条判据上，且不引入运动扫描与场 pass 的噪声。</para>
    /// <para>概率行为的断言用**窗口**（不绑精确计数）——见 <c>FireParticleTests</c> 头注（审计 FT1）。</para>
    /// </remarks>
    public sealed class FlowerParticleTests
    {
        private const uint SeedValue = 20261012u;

        /// <summary>注热到 T = 25°（花 / 草的热容 0.8）：高于草的低温致死线 3°、远低于高温线 50°。</summary>
        private const float SafeHeat = 20f;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        [Test]
        public void Flower_IsRegisteredAtTheFlowerIdWithGrassLikeTags()
        {
            ParticleType type = ParticleRegistry.Types[ParticleIds.Flower];

            Assert.AreEqual(nameof(FlowerParticle), type.Info.DebugName,
                "ParticleIds.Flower 必须指向花（注册清单与 id 下标对齐）");
            Assert.AreEqual(ParticleTags.Solid | ParticleTags.Organic, type.Info.Tags,
                "花继承草的标签：Solid + Organic（挡光、死亡按 20% 留腐殖质）");
            Assert.IsFalse(type.Info.IsStatic, "花必须非静态，否则收不到随机刻（死法全在随机刻里）");
            Assert.IsFalse(type.Info.AffectedByGravity, "花不落体（同草：靠空的 OnMotion 定植）");
            Assert.AreEqual(GrassParticle.LightAbsorptionFactor, type.Info.LightAbsorption,
                "光照吸收率与草同值（正午稳态温度仍低于 50° 致死线）");
        }

        [Test]
        public void Flower_PaletteIsVividVariedAndSampledRandomlyPerCell()
        {
            Color32[] palette = ParticleRegistry.Types[ParticleIds.Flower].Info.Palette;
            Assert.AreEqual(FlowerParticle.ColorCount, palette.Length, "花色数应等于调色板长度");

            for (int i = 0; i < palette.Length; i++)
            {
                int brightest = Mathf.Max(palette[i].r, Mathf.Max(palette[i].g, palette[i].b));
                Assert.GreaterOrEqual(brightest, 200, $"花色应鲜艳（最亮通道 ≥ 200，档 {i}）");

                for (int j = i + 1; j < palette.Length; j++)
                {
                    Assert.IsFalse(
                        palette[i].r == palette[j].r && palette[i].g == palette[j].g
                            && palette[i].b == palette[j].b,
                        $"花色应互不相同、多样（档 {i} 与 {j} 重复）");
                }
            }

            // 花色是**每格随机**的（shade 由世界的 Set / SetPhase 抽样），而不是像草那样被 OnSpawn 钉成固定档。
            var world = new ParticleWorld(40, 4, SeedValue);
            var shades = new HashSet<byte>();
            for (int x = 0; x < 40; x++)
            {
                world.SetPhase(x, 1, ParticleIds.Flower);
                shades.Add(world.GetCell(x, 1).shade);
            }

            Assert.GreaterOrEqual(shades.Count, 8, $"40 朵花应抽到多种花色（实际 {shades.Count} 种），花色必须随机");
        }

        /// <summary>
        /// 发芽开花：在每个独立世界里让**一颗**种子被随机刻命中一次（直接调钩子 ⇒ 命中即发芽），
        /// 统计 200 次独立发芽里「株顶是花」的比例，并用窗口断言（≈10%，容差放宽到 4%..19%）。
        /// 同时守几何不变量：一次发芽至多开一朵花，花**只**出现在株顶。
        /// </summary>
        [Test]
        public void Flower_BlossomsOnTheStalksTopCellAtAboutTenPercent()
        {
            const int trials = 200;
            int blooms = 0;

            for (uint i = 0; i < trials; i++)
            {
                ParticleWorld world = BuildSeedOnWetSoil(SeedValue + i);
                ParticleRegistry.Types[ParticleIds.GrassSeed].OnRandomTick(world, 4, 2);

                Assert.AreNotEqual(ParticleIds.GrassSeed, world.GetCell(4, 2).typeId,
                    "八邻有湿土 ⇒ 本次随机刻必须发芽（无概率门）");

                int flowers = 0;
                int flowerY = -1;
                for (int y = 2; y < 8; y++)
                {
                    if (world.GetCell(4, y).typeId == ParticleIds.Flower)
                    {
                        flowers++;
                        flowerY = y;
                    }
                }

                Assert.LessOrEqual(flowers, 1, "一次发芽至多开一朵花");

                if (flowers == 0)
                {
                    continue;
                }

                blooms++;
                Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, flowerY - 1).typeId,
                    "花长在草株顶端：正下方是草");
                Assert.AreNotEqual(ParticleIds.Grass, world.GetCell(4, flowerY + 1).typeId,
                    "花之上不应再有草（花就是株顶那一格）");
            }

            Assert.Greater(blooms, 0, $"{trials} 次发芽里应至少开一次花");
            Assert.GreaterOrEqual(blooms, trials * 4 / 100,
                $"开花应是约 10% 的概率事件（下限 4%），实际 {blooms}/{trials}");
            Assert.LessOrEqual(blooms, trials * 19 / 100,
                $"开花应是约 10% 的概率事件（上限 19%），实际 {blooms}/{trials}");
        }

        /// <summary>
        /// 花与草是**同一株**：花长在草顶上不算「压」，否则草会被自己的花当帧压死、整株一起塌掉。
        /// 这是花最关键的回归——<see cref="GrassParticle"/> 的「被压」判据必须把花当自己人。
        /// </summary>
        [Test]
        public void Flower_SharesTheStalkSoItDoesNotPressItsOwnGrass()
        {
            ParticleWorld world = BuildStalkWithFlower();

            for (int i = 0; i < 200; i++)
            {
                ParticleRegistry.Types[ParticleIds.Grass].OnRandomTick(world, 4, 3);
                ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(world, 4, 4);
            }

            Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, 3).typeId,
                "花是同一株的顶端，不算压——草不应被自己的花压死");
            Assert.AreEqual(ParticleIds.Flower, world.GetCell(4, 4).typeId,
                "草还在，花就不该因失去扎根而死");
        }

        [Test]
        public void Flower_DiesWhenCrushedAndTheStalkBelowIsClearedWithIt()
        {
            ParticleWorld world = BuildStalkWithFlower();
            world.Set(4, 5, ParticleIds.Stone); // 石头落在花上

            ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(world, 4, 4);

            ushort flowerCell = world.GetCell(4, 4).typeId;
            Assert.AreNotEqual(ParticleIds.Flower, flowerCell, "被石头压住的花应死亡");
            Assert.IsTrue(
                flowerCell == ParticleRegistry.AirTypeId || flowerCell == ParticleIds.Humus,
                $"花的死亡只允许空气或腐殖质（20% 有机衰变），实际 typeId={flowerCell}");
            Assert.AreNotEqual(ParticleIds.Grass, world.GetCell(4, 3).typeId,
                "死亡沿竖直方向向下传播：同一株的草段应随花一起被清掉");
            Assert.AreEqual(ParticleIds.Soil, world.GetCell(4, 2).typeId, "级联止于非同一株的格（土壤保留）");
        }

        [Test]
        public void Flower_DiesWhenItLosesItsRoot()
        {
            // 悬空：正下方是空气。
            var floating = new ParticleWorld(8, 8, SeedValue);
            floating.SetPhase(4, 4, ParticleIds.Flower);
            floating.Heat.SetHeat(4, 4, SafeHeat);

            ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(floating, 4, 4);

            Assert.AreNotEqual(ParticleIds.Flower, floating.GetCell(4, 4).typeId,
                "正下方既不是土 / 湿土 / 腐殖土、也不是同一株的花应失去扎根而死");

            // 站在石头上同样不算扎根（只有三土才算基质）。
            var onStone = new ParticleWorld(8, 8, SeedValue);
            onStone.Set(4, 3, ParticleIds.Stone);
            onStone.SetPhase(4, 4, ParticleIds.Flower);
            onStone.Heat.SetHeat(4, 4, SafeHeat);

            ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(onStone, 4, 4);

            Assert.AreNotEqual(ParticleIds.Flower, onStone.GetCell(4, 4).typeId,
                "石头不是扎根基质（与草同一条判据）");
        }

        [Test]
        public void Flower_DiesAtLethalTemperatures()
        {
            var hot = new ParticleWorld(8, 8, SeedValue);
            hot.Set(4, 3, ParticleIds.Soil);
            hot.SetPhase(4, 4, ParticleIds.Flower);
            hot.Heat.SetHeat(4, 4, 100f); // T = 125° > 50

            ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(hot, 4, 4);

            Assert.AreNotEqual(ParticleIds.Flower, hot.GetCell(4, 4).typeId, "过热（> 50°）的花应死亡");

            var cold = new ParticleWorld(8, 8, SeedValue);
            cold.Set(4, 3, ParticleIds.Soil);
            cold.SetPhase(4, 4, ParticleIds.Flower);
            cold.Heat.SetHeat(4, 4, 0f); // T = 0° < 3

            ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(cold, 4, 4);

            Assert.AreNotEqual(ParticleIds.Flower, cold.GetCell(4, 4).typeId,
                "过冷（< 3°）的花应死亡（与草同一条低温线）");
        }

        [Test]
        public void Flower_LeavesHumusOnDeath()
        {
            const int deaths = 40;
            var world = new ParticleWorld(8, 8, SeedValue);
            int humus = 0;
            int air = 0;

            for (int i = 0; i < deaths; i++)
            {
                world.Set(4, 3, ParticleIds.Soil);
                world.SetPhase(4, 4, ParticleIds.Flower);
                world.Heat.SetHeat(4, 4, 100f); // 过热致死：每次只死这一格
                ParticleRegistry.Types[ParticleIds.Flower].OnRandomTick(world, 4, 4);

                ushort id = world.GetCell(4, 4).typeId;
                if (id == ParticleIds.Humus)
                {
                    humus++;
                }
                else if (id == ParticleRegistry.AirTypeId)
                {
                    air++;
                }
                else
                {
                    Assert.Fail($"花的死亡只允许空气或腐殖质（20% 有机衰变），实际 typeId={id}");
                }

                world.Clear(4, 4);
            }

            Assert.Greater(humus, 0, $"{deaths} 次死亡里应至少留一次腐殖质（20% 有机衰变）");
            Assert.Greater(air, 0, "腐殖质是概率产物：不应每次死亡都留下（那说明衰减概率错了）");
        }

        /// <summary>
        /// 花继承草的可燃性：邻接的火把花当**燃料**（不摇熄灭骰、逐格吃掉），而不是当淬熄物（贴非可燃固体 1/5 快熄）。
        /// 若火把花当成了非可燃固体，火会在数帧内自己熄灭、花原样留着——本测试即失败。
        /// </summary>
        [Test]
        public void Flower_BurnsAsFuelForAnAdjacentFire()
        {
            var world = new ParticleWorld(16, 16, SeedValue);
            world.Set(4, 3, ParticleIds.Soil);      // 附着物（让这格是「附着火」）
            world.Set(3, 4, ParticleIds.Flower);   // 唯一的燃料
            world.Set(4, 4, ParticleIds.Fire);

            int frames = 0;
            while (frames < 200 && world.GetCell(3, 4).typeId == ParticleIds.Flower)
            {
                Assert.AreEqual(ParticleIds.Fire, world.GetCell(4, 4).typeId,
                    $"火在第 {frames} 帧就熄灭了：贴花应视为「有燃料」（不摇熄灭骰），而不是被淬熄");
                world.StepLogic();
                frames++;
            }

            Assert.Less(frames, 200, "邻接的火应在有限帧内点燃花（1/20 每逻辑帧）");
        }

        /// <summary>
        /// 花**不衰老**：草的年龄存在 <c>Cell.shade</c> + flags bit3-5，到 1200 逻辑帧寿终；
        /// 花的 <c>shade</c> 是花色，没有年龄存储、也没有寿命——1300 帧后仍在原地、花色不变。
        /// （悬空 + 随机刻默认关闭 ⇒ 三条随机刻死法都不触发，隔离出的唯一变量就是「有没有寿命」。）
        /// </summary>
        [Test]
        public void Flower_NeverAgesAndStaysPut()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.SetPhase(4, 4, ParticleIds.Flower);
            byte shade = world.GetCell(4, 4).shade;

            for (int i = 0; i < GrassParticle.LifespanTicks + 100; i++)
            {
                world.StepLogic();
            }

            Cell cell = world.GetCell(4, 4);
            Assert.AreEqual(ParticleIds.Flower, cell.typeId,
                $"花没有寿命（草在第 {GrassParticle.LifespanTicks} 帧已寿终），且不落体");
            Assert.AreEqual(shade, cell.shade, "花色一旦抽定就终生不变（shade 不是年龄档）");
        }

        /// <summary>8×8 世界里在 (4,2) 种一颗草种子，正下方是湿土（同时满足八邻有湿土）。</summary>
        private static ParticleWorld BuildSeedOnWetSoil(uint seed)
        {
            var world = new ParticleWorld(8, 8, seed);
            world.Set(4, 1, ParticleIds.WetSoil);
            world.Set(4, 2, ParticleIds.GrassSeed);
            return world;
        }

        /// <summary>
        /// 一株「草 + 顶端的花」：土壤 (4,2) → 草 (4,3) → 花 (4,4)，三格都注热到安全温度（T = 25°）。
        /// </summary>
        private static ParticleWorld BuildStalkWithFlower()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 2, ParticleIds.Soil);
            world.Set(4, 3, ParticleIds.Grass);
            world.SetPhase(4, 4, ParticleIds.Flower);
            world.Heat.SetHeat(4, 2, SafeHeat);
            world.Heat.SetHeat(4, 3, SafeHeat);
            world.Heat.SetHeat(4, 4, SafeHeat);
            return world;
        }
    }
}
