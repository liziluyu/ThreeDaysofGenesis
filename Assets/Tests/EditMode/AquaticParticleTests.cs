using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 水生植物 / 水生态种子：沉水扎根、9D 位移式抽茎（不销毁水）、四种死法、随机刻撒种 / 霉烂。
    /// 跨类型相变依赖 <see cref="ParticleIds"/> 回填，因此走生产注册路径（<see cref="ParticleRegistration.RegisterAll"/>）。
    /// 随机刻用 <see cref="ParticleType.OnRandomTick"/> 直接调用，保证确定、不依赖世界随机抽格。
    /// </summary>
    public sealed class AquaticParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private const uint SeedValue = 20261005u;

        private static ushort WaterId
        {
            get { return ParticleIds.Water; }
        }

        private static ushort SandId
        {
            get { return ParticleIds.Sand; }
        }

        private static ushort SeedId
        {
            get { return ParticleIds.AquaticSeed; }
        }

        private static ushort PlantId
        {
            get { return ParticleIds.AquaticPlant; }
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

        /// <summary>水总量：水格 + 湿土格（每格含一份水）+ 湿度场。</summary>
        private static float TotalWater(ParticleWorld world)
        {
            return CountType(world, WaterId) + CountType(world, ParticleIds.WetSoil) + world.Humidity.Sum();
        }

        private static void RandomTick(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnRandomTick(world, x, y);
        }

        // ============================== 生产注册 ==============================

        [Test]
        public void ProductionRegistration_BackfillsAquaticIds()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, SeedId, "水生态种子 id 应被回填");
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, PlantId, "水生植物 id 应被回填");
            Assert.AreNotEqual(SeedId, PlantId);

            ParticleTypeInfo seed = ParticleRegistry.Types[SeedId].Info;
            Assert.IsTrue((seed.Tags & ParticleTags.Solid) != 0);
            Assert.IsTrue((seed.Tags & ParticleTags.Organic) != 0);
            Assert.IsTrue(seed.AffectedByGravity, "种子应受重力");

            ParticleTypeInfo plant = ParticleRegistry.Types[PlantId].Info;
            Assert.IsTrue((plant.Tags & ParticleTags.Solid) != 0);
            Assert.IsTrue((plant.Tags & ParticleTags.Organic) != 0);
            Assert.IsFalse(plant.AffectedByGravity, "植物不受重力");
            Assert.IsFalse(plant.IsStatic, "植物必须非静态，否则收不到随机刻");
        }

        // ============================== 种子：发芽 ==============================

        [Test]
        public void Seed_GerminatesWhenSubmergedOnSoftSubstrate()
        {
            ParticleWorld world = new ParticleWorld(3, 10, SeedValue);
            world.Set(1, 0, SandId);
            for (int y = 2; y <= 8; y++)
            {
                world.Set(1, y, WaterId);
            }

            world.Set(1, 1, SeedId);

            RandomTick(world, SeedId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 1).typeId, "自身应发芽成植物");
            Assert.AreEqual(PlantId, world.GetCell(1, 2).typeId, "应在液体里抽出至少一格茎");
        }

        [Test]
        public void Seed_DoesNotGerminate_WhenNotSubmergedOrNotRooted()
        {
            // 上方不是液体（未淹没）。
            ParticleWorld dry = new ParticleWorld(3, 6, SeedValue);
            dry.Set(1, 0, SandId);
            dry.Set(1, 1, SeedId);
            RandomTick(dry, SeedId, 1, 1);
            Assert.AreEqual(SeedId, dry.GetCell(1, 1).typeId, "未淹没不发芽");

            // 上方是液体但正下方不是软底（未扎根）。
            ParticleWorld loose = new ParticleWorld(3, 6, SeedValue);
            loose.Set(1, 2, WaterId);
            loose.Set(1, 1, SeedId);
            RandomTick(loose, SeedId, 1, 1);
            Assert.AreEqual(SeedId, loose.GetCell(1, 1).typeId, "未扎根不发芽");
        }

        [Test]
        public void Seed_RotsAfterThreeFailedStrikes()
        {
            ParticleWorld world = new ParticleWorld(3, 6, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, SeedId); // 上方是空气：始终不满足发芽条件。

            RandomTick(world, SeedId, 1, 1);
            Assert.AreEqual(SeedId, world.GetCell(1, 1).typeId, "第 1 次空转仍保留");
            RandomTick(world, SeedId, 1, 1);
            Assert.AreEqual(SeedId, world.GetCell(1, 1).typeId, "第 2 次空转仍保留");

            RandomTick(world, SeedId, 1, 1);
            Assert.AreEqual(ParticleIds.Humus, world.GetCell(1, 1).typeId, "连续 3 次无发芽条件应霉烂成腐殖质");
        }

        [Test]
        public void Seed_FailedStrikes_DoNotConsumeRandomStream()
        {
            ParticleWorld world = new ParticleWorld(3, 6, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, SeedId);

            ParticleWorld reference = new ParticleWorld(3, 6, SeedValue);
            reference.Set(1, 0, SandId);
            reference.Set(1, 1, SeedId);

            RandomTick(world, SeedId, 1, 1); // 空转一次：不该消耗随机数
            RandomTick(world, SeedId, 1, 1);

            Assert.AreEqual(reference.NextUInt(), world.NextUInt(), "无发芽条件的空转不得推进随机流");
        }

        // ============================== 种子：9D 位移抽茎 ==============================

        [Test]
        public void Seed_GrowthDisplacesWater_AndConservesTotal()
        {
            ParticleWorld world = new ParticleWorld(3, 10, SeedValue);
            world.Set(1, 0, SandId);
            for (int y = 2; y <= 8; y++)
            {
                world.Set(1, y, WaterId);
            }

            world.Set(1, 1, SeedId);
            float before = TotalWater(world);

            RandomTick(world, SeedId, 1, 1);

            Assert.AreEqual(before, TotalWater(world), 1e-3f, "位移式生长不销毁水：水总量必须守恒");
            Assert.AreEqual(WaterId, world.GetCell(1, 9).typeId, "被顶上去的水应出现在水柱顶端之上（水位上升一格）");
            Assert.AreEqual(PlantId, world.GetCell(1, 2).typeId, "目标格应变成植物");
        }

        [Test]
        public void Seed_GrowthStopsWhenWaterColumnIsCappedAtGridTop()
        {
            ParticleWorld world = new ParticleWorld(3, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 2, WaterId);
            world.Set(1, 3, WaterId);
            world.Set(1, 4, WaterId); // 水顶到网格顶，自动上方无接收格。
            world.Set(1, 1, SeedId);

            float before = TotalWater(world);

            RandomTick(world, SeedId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 1).typeId, "仍会发芽成植物");
            Assert.AreEqual(WaterId, world.GetCell(1, 2).typeId, "找不到接收格 → 不抽茎");
            Assert.AreEqual(before, TotalWater(world), 1e-3f, "水总量不变");
        }

        // ============================== 植物：死法 ==============================

        [Test]
        public void Plant_DehydratesWhenNoLiquidNeighbour()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId); // 八邻全是空气：脱水。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreNotEqual(PlantId, world.GetCell(1, 1).typeId, "八邻无液体应脱水而死");
        }

        [Test]
        public void Plant_IsPressedBySolidAbove()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, SandId); // 正上方是固体（非同类）：被压死。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreNotEqual(PlantId, world.GetCell(1, 1).typeId, "正上方是固体应被压死");
        }

        [Test]
        public void Plant_SurvivesWithLiquidAbove()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, WaterId); // 上方是液体：沉水常态，不算压。
            world.Heat.SetHeat(1, 1, 8f); // T = 10，落在 [3, 50] 内（否则环境 T = 0 会先被温度致死）。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 1).typeId, "上方是液体不应算压");
        }

        [Test]
        public void Plant_UprootsAndClearsStemUpward()
        {
            ParticleWorld world = new ParticleWorld(5, 6, SeedValue);
            world.Set(1, 1, PlantId); // 正下方 (1,0) 是空气：没扎根。
            world.Set(1, 2, PlantId);
            world.Set(1, 3, PlantId);

            RandomTick(world, PlantId, 1, 1);

            Assert.AreNotEqual(PlantId, world.GetCell(1, 1).typeId);
            Assert.AreNotEqual(PlantId, world.GetCell(1, 2).typeId, "失去扎根应向上清掉整段茎");
            Assert.AreNotEqual(PlantId, world.GetCell(1, 3).typeId);
        }

        [Test]
        public void Plant_DiesOfTemperature()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, WaterId); // 有液体邻格：排除脱水路径，只测温度。
            world.Heat.SetHeat(1, 1, 60f); // 热容 0.8 → T = 75 > 50。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreNotEqual(PlantId, world.GetCell(1, 1).typeId, "过热应致死");
        }

        [Test]
        public void Plant_DoesNotMove()
        {
            ParticleWorld world = new ParticleWorld(5, 6, SeedValue);
            world.Set(1, 3, PlantId); // 悬空也不应下落。

            for (int i = 0; i < 10; i++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(PlantId, world.GetCell(1, 3).typeId, "植物 OnMotion 为空：长在原地不动");
        }

        // ============================== 植物：撒种 ==============================

        [Test]
        public void Plant_SpawnsSeedOnRandomTick()
        {
            ParticleWorld world = new ParticleWorld(7, 7, SeedValue);
            world.Set(2, 0, SandId);
            world.Set(2, 1, PlantId);
            world.Set(1, 1, WaterId);
            world.Set(3, 1, WaterId);
            world.Set(2, 2, WaterId);
            // 唯一合法落点 (4,1)：非持水基质、上方是液体、下方是软底。
            world.Set(4, 0, SandId);
            world.Set(4, 1, SandId);
            world.Set(4, 2, WaterId);
            world.Heat.SetHeat(2, 1, 8f); // 植物存活所需温度（T = 10 ∈ [3, 50]）。

            for (int i = 0; i < 300 && CountType(world, SeedId) == 0; i++)
            {
                RandomTick(world, PlantId, 2, 1);
            }

            Assert.AreEqual(1, CountType(world, SeedId), "10% 概率的撒种应在有限次随机刻内落到合法落点");
            Assert.AreEqual(SeedId, world.GetCell(4, 1).typeId, "种子应落在 (4,1)");
        }

        [Test]
        public void Plant_DeathPreservesWaterTotal()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId); // 无液体邻格 → 脱水死。
            world.Set(3, 3, WaterId);

            float before = TotalWater(world);
            RandomTick(world, PlantId, 1, 1);
            Assert.AreNotEqual(PlantId, world.GetCell(1, 1).typeId);

            Assert.AreEqual(before, TotalWater(world), 1e-3f, "植物死亡不产不灭水");
        }
    }
}
