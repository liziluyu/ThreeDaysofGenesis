using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 水生植物 / 水生态种子：吸水发芽（1:1 消耗水）、随机刻逐格生长（留保命水）、四种死法还水、
    /// 被覆盖走水汽、撒种顶水不啃基质。
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

        /// <summary>水总量：水格 + 湿土格（每格含一份水）+ 湿度场 + 植物格（每格植物 ⇔ 一格被消耗的水）。
        /// 种子不计入（撒种是把水顶上去，水格数不变；种子本身不持水）。</summary>
        private static float TotalWater(ParticleWorld world)
        {
            return CountType(world, WaterId) + CountType(world, ParticleIds.WetSoil)
                + world.Humidity.Sum() + CountType(world, PlantId);
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
            Assert.IsFalse((seed.Tags & ParticleTags.Organic) != 0,
                "种子不带 Organic：否则发芽时 SetPhase(种子→植物) 会被 20% 腐殖质衰变截胡");
            Assert.IsTrue(seed.AffectedByGravity, "种子应受重力");

            ParticleTypeInfo plant = ParticleRegistry.Types[PlantId].Info;
            Assert.IsTrue((plant.Tags & ParticleTags.Solid) != 0);
            Assert.IsFalse((plant.Tags & ParticleTags.Organic) != 0,
                "植物不带 Organic：它的物质归水，死亡还水而不是留腐殖质");
            Assert.IsFalse(plant.AffectedByGravity, "植物不受重力");
            Assert.IsFalse(plant.IsStatic, "植物必须非静态，否则收不到随机刻");
        }

        // ============================== 种子：发芽（吸水） ==============================

        [Test]
        public void Seed_GerminatesByAbsorbingWaterAbove()
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
            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(1, 2).typeId, "头顶那格水被吸收成大气");
            Assert.AreEqual(WaterId, world.GetCell(1, 3).typeId, "再上方的水不动");
        }

        [Test]
        public void Seed_Germination_ConservesWaterTotal()
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

            Assert.AreEqual(before, TotalWater(world), 1e-3f,
                "发芽消耗一格水、产生一格植物：水+湿土+湿度+植物总量必须守恒");
        }

        [Test]
        public void Seed_Germinates_EvenWhenWaterReachesGridTop()
        {
            // 旧版位移式抽茎在水柱封顶时找不到接收格而停；吸水式发芽没有接收格概念。
            ParticleWorld world = new ParticleWorld(3, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 2, WaterId);
            world.Set(1, 3, WaterId);
            world.Set(1, 4, WaterId); // 水顶到网格顶。
            world.Set(1, 1, SeedId);

            RandomTick(world, SeedId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 1).typeId, "水柱到顶也能发芽");
            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(1, 2).typeId, "头顶水被吸收");
            Assert.AreEqual(3f, TotalWater(world), 1e-3f, "2 水 + 1 植物 = 3，守恒");
        }

        [Test]
        public void Seed_DoesNotGerminate_WhenNotSubmergedOrNotRooted()
        {
            // 上方不是水（未淹没）。
            ParticleWorld dry = new ParticleWorld(3, 6, SeedValue);
            dry.Set(1, 0, SandId);
            dry.Set(1, 1, SeedId);
            RandomTick(dry, SeedId, 1, 1);
            Assert.AreEqual(SeedId, dry.GetCell(1, 1).typeId, "未淹没不发芽");

            // 上方是水但正下方不是软底（未扎根）。
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

        // ============================== 植物：生长 ==============================

        [Test]
        public void Plant_GrowsOneCell_ConsumingOneWater()
        {
            ParticleWorld world = new ParticleWorld(3, 8, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            for (int y = 2; y <= 4; y++)
            {
                world.Set(1, y, WaterId);
            }

            float before = TotalWater(world);

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 2).typeId, "头顶水格应变成新茎");
            Assert.AreEqual(2, CountType(world, PlantId), "长一格");
            Assert.AreEqual(before, TotalWater(world), 1e-3f, "消耗一格水、长一格植物：守恒");
        }

        [Test]
        public void Plant_GrowthKeepsOneWaterCellAbove()
        {
            // 头顶只剩一格水：不能长——长了这格就被吸掉，植株露出水面会自我脱水。
            ParticleWorld world = new ParticleWorld(3, 6, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, WaterId);

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 1).typeId, "只剩一格保命水时不生长");
            Assert.AreEqual(WaterId, world.GetCell(1, 2).typeId, "保命水不动");
            Assert.AreEqual(1, CountType(world, PlantId));
        }

        [Test]
        public void Plant_GrowthStopsAtMaxHeight()
        {
            ParticleWorld world = new ParticleWorld(3, 8, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, PlantId);
            world.Set(1, 3, PlantId);
            world.Set(1, 4, WaterId);
            world.Set(1, 5, WaterId);

            RandomTick(world, PlantId, 1, 3); // 命中顶格：高度已达上限

            Assert.AreEqual(3, CountType(world, PlantId), "株高到顶不再长");
            Assert.AreEqual(WaterId, world.GetCell(1, 4).typeId);
        }

        // ============================== 植物：死法（还水） ==============================

        [Test]
        public void Plant_DehydratesWhenNoLiquidNeighbour_ReturnsWater()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId); // 八邻全是空气：脱水。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(WaterId, world.GetCell(1, 1).typeId, "脱水死应把这一格还成水");
        }

        [Test]
        public void Plant_IsPressedBySolidAbove_ReturnsWater()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, SandId); // 正上方是固体（非同类）：被压死。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(WaterId, world.GetCell(1, 1).typeId, "被压死应还成水");
            Assert.AreEqual(SandId, world.GetCell(1, 2).typeId, "压它的沙不动（本测试不跑运动帧）");
        }

        [Test]
        public void Plant_SurvivesWithLiquidAbove()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, WaterId); // 上方是液体：沉水常态，不算压。只有一格水，不触发生长。
            world.Heat.SetHeat(1, 1, 8f); // T = 10：隔离温度路径，专测"液体不算压"。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(PlantId, world.GetCell(1, 1).typeId, "上方是液体不应算压");
            Assert.AreEqual(WaterId, world.GetCell(1, 2).typeId, "保命水不被动");
        }

        [Test]
        public void Plant_UprootsAndReturnsWaterUpward()
        {
            ParticleWorld world = new ParticleWorld(5, 6, SeedValue);
            world.Set(1, 1, PlantId); // 正下方 (1,0) 是空气：没扎根。
            world.Set(1, 2, PlantId);
            world.Set(1, 3, PlantId);

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(WaterId, world.GetCell(1, 1).typeId);
            Assert.AreEqual(WaterId, world.GetCell(1, 2).typeId, "失去扎根应向上把整段茎还成水");
            Assert.AreEqual(WaterId, world.GetCell(1, 3).typeId);
        }

        [Test]
        public void Plant_DiesOfTemperature_ReturnsWater()
        {
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId);
            world.Set(1, 2, WaterId); // 有液体邻格：排除脱水路径，只测温度。
            world.Heat.SetHeat(1, 1, 60f); // 热容 0.8 → T = 75 > 50。

            RandomTick(world, PlantId, 1, 1);

            Assert.AreEqual(WaterId, world.GetCell(1, 1).typeId, "过热死应还成水");
        }

        [Test]
        public void Plant_OverwrittenBySolid_ReturnsWaterAsVapor()
        {
            // 被固体覆盖（Set 压顶）：本格会被写成沙，水只能走湿度场——水汽仍计入水账。
            ParticleWorld world = new ParticleWorld(5, 5, SeedValue);
            world.Set(1, 0, SandId);
            world.Set(1, 1, PlantId); // 八邻全是气体/空。

            float before = TotalWater(world);

            world.Set(1, 1, SandId); // 压埋。

            Assert.AreEqual(SandId, world.GetCell(1, 1).typeId, "被沙覆盖");
            Assert.AreEqual(before, TotalWater(world), 1e-3f, "那份水应进入湿度场，总量守恒");
            Assert.AreEqual(0, CountType(world, WaterId), "不产生液态水格");
            Assert.Greater(world.Humidity.Sum(), 0f, "湿度场应变多");
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

        // ============================== 植物：撒种（顶水，不啃基质） ==============================

        [Test]
        public void Plant_SpawnsSeed_DisplacingWaterUpward()
        {
            ParticleWorld world = new ParticleWorld(7, 7, SeedValue);
            world.Set(2, 0, SandId);
            world.Set(2, 1, PlantId);
            world.Set(1, 1, WaterId);
            world.Set(3, 1, WaterId);
            world.Set(2, 2, WaterId);
            // 唯一合法落点列 x=4，基质 (4,1)：相对植物 (2,1) 的偏移 (dx=2, dy=0)，落在半径 2 圆盘内。
            world.Set(4, 1, SandId);
            world.Set(4, 2, WaterId); // (4,3) 是空 → 被顶开的水的接收格。
            world.Heat.SetHeat(2, 1, 8f); // T = 10：隔离温度路径。

            for (int i = 0; i < 300 && CountType(world, SeedId) == 0; i++)
            {
                RandomTick(world, PlantId, 2, 1);
            }

            Assert.AreEqual(1, CountType(world, SeedId), "10% 概率的撒种应在有限次随机刻内落到合法落点");
            Assert.AreEqual(SeedId, world.GetCell(4, 2).typeId, "种子应出现在基质正上方的水格");
            Assert.AreEqual(WaterId, world.GetCell(4, 3).typeId, "被顶开的水应出现在水柱顶端之上（水位 +1）");
            Assert.AreEqual(SandId, world.GetCell(4, 1).typeId, "基质不被消耗");
        }

        [Test]
        public void Plant_FullCycle_ConservesWaterTotal()
        {
            // 端到端：种子 → 发芽（吸水）→ 补水落位 → 生长（吸水）→ 过热死（还水）→ 总量不变。
            // 两侧砌沙墙：防止补水落位期间水从无约束的通道里横流漏出去。
            ParticleWorld world = new ParticleWorld(3, 8, SeedValue);
            for (int y = 0; y < 8; y++)
            {
                // 用石头砌墙：沙摩擦为 0，会斜滑进水道（水道是空/水格、rank 更低），墙就不稳了。
                world.Set(0, y, ParticleIds.Stone);
                world.Set(2, y, ParticleIds.Stone);
            }

            world.Set(1, 0, SandId);
            world.Set(1, 1, SeedId);
            for (int y = 2; y <= 6; y++)
            {
                world.Set(1, y, WaterId);
            }

            float initial = TotalWater(world);

            RandomTick(world, SeedId, 1, 1); // 发芽：头顶水被吸收成大气
            Assert.AreEqual(initial, TotalWater(world), 1e-3f, "发芽后守恒");

            // 上方水逐格落下来补位（水沉穿大气泡）：4 帧足够让水柱在气泡上方重新坐实。
            for (int i = 0; i < 4; i++)
            {
                world.StepMotion();
            }

            RandomTick(world, PlantId, 1, 1); // 生长：头顶水格变茎
            Assert.AreEqual(initial, TotalWater(world), 1e-3f, "生长后守恒");
            Assert.AreEqual(2, CountType(world, PlantId), "发芽 1 格 + 生长 1 格");

            // 过热杀死整株：两格各判各的（温度路径不级联），每格还成水。
            for (int y = 1; y <= 2; y++)
            {
                world.Heat.SetHeat(1, y, 60f);
            }

            RandomTick(world, PlantId, 1, 1);
            RandomTick(world, PlantId, 1, 2);

            Assert.AreEqual(0, CountType(world, PlantId), "整株死光");
            Assert.AreEqual(initial, TotalWater(world), 1e-3f, "死亡还水后回到初始总量");
        }
    }
}
