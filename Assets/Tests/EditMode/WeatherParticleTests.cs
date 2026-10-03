using NUnit.Framework;
using UnityEngine;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 气象系统测试：气体浮力原语（隔离类型表）+ 水 ⇌ 蒸汽 ⇌ 云 ⇌ 雨的相变与守恒（生产注册路径）。
    /// </summary>
    /// <remarks>
    /// 相变测试必须走 <see cref="ParticleRegistration.RegisterAll"/> 的全局表：跨类型转换靠
    /// <see cref="ParticleIds"/> 的回填字段找到对方 typeId，隔离表里这些字段是 0（契约允许的唯一例外路径，
    /// 与 <c>SandStoneParticleTests</c> 的取舍一致）。浮力原语测试直接调用 <see cref="ParticleRules"/>，
    /// 不经过 OnUpdate，因此不受相变阈值干扰。
    /// </remarks>
    public sealed class WeatherParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
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

        // ==================== 浮力原语（隔离类型表，直调规则） ====================

        /// <summary>热气上升：比四邻热的气体 TryBuoyantStep 后上移一格。</summary>
        [Test]
        public void BuoyantStep_HotGasRises()
        {
            ParticleWorld world = new ParticleWorld(
                8, 8, 7u, TestTables.Build(new AtmosphereParticle(), new SteamParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Heat.SetHeat(4, 4, 200f); // 热容 1 → T=200，四邻 T=0

            Assert.IsTrue(ParticleRules.TryBuoyantStep(world, 4, 4, 1f), "热气应能上升");
            Assert.AreEqual(atmosphere, world.GetCell(4, 5).typeId, "热气上移一格");
            Assert.AreEqual(200f, world.Heat.GetHeat(4, 5), "热量随气体搬家");
        }

        /// <summary>冷气下沉：比四邻冷的气体 TryBuoyantStep 后下移一格。</summary>
        [Test]
        public void BuoyantStep_ColdGasSinks()
        {
            ParticleWorld world = new ParticleWorld(
                8, 8, 7u, TestTables.Build(new AtmosphereParticle(), new SteamParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            // 四邻烤热（各 Q=200，T=200），自己 T=0 → 冷气下沉。
            world.Heat.SetHeat(3, 4, 200f);
            world.Heat.SetHeat(5, 4, 200f);
            world.Heat.SetHeat(4, 3, 200f);
            world.Heat.SetHeat(4, 5, 200f);

            Assert.IsTrue(ParticleRules.TryBuoyantStep(world, 4, 4, 1f), "冷气应能下沉");
            Assert.AreEqual(atmosphere, world.GetCell(4, 3).typeId, "冷气下移一格");
        }

        /// <summary>死区：与邻居温差在 epsilon 内不动。</summary>
        [Test]
        public void BuoyantStep_WithinEpsilon_Stays()
        {
            ParticleWorld world = new ParticleWorld(
                8, 8, 7u, TestTables.Build(new AtmosphereParticle(), new SteamParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Heat.SetHeat(4, 4, 0.5f); // T=0.5，四邻 0，差 0.5 < epsilon 1

            Assert.IsFalse(ParticleRules.TryBuoyantStep(world, 4, 4, 1f), "温差在死区内不应动");
            Assert.AreEqual(atmosphere, world.GetCell(4, 4).typeId);
        }

        /// <summary>水平热压流：冷空气流向更暖的一侧（低空海风的微观规则）。</summary>
        [Test]
        public void ThermalDrift_FlowsTowardWarmerSide()
        {
            ParticleWorld world = new ParticleWorld(
                8, 8, 7u, TestTables.Build(new AtmosphereParticle(), new SteamParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Set(5, 4, atmosphere);
            world.Heat.SetHeat(5, 4, 100f); // 右侧暖

            Assert.IsTrue(ParticleRules.TryThermalDrift(world, 4, 4, 1f), "应流向暖侧");
            Assert.AreEqual(atmosphere, world.GetCell(5, 4).typeId);
        }

        // ==================== 相变（生产注册路径） ====================

        /// <summary>水到沸点蒸发成蒸汽，热量随相变携带（蒸汽热容小，同一份热更烫）。</summary>
        [Test]
        public void Water_Evaporates_WhenAtBoilingPoint()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Heat.Conductivity = 0f; // 关掉传导：本测试只看相变，否则同一 tick 的扩散会把 Q 摊走
            world.Set(4, 4, ParticleIds.Water);
            float capacity = ParticleRegistry.Types[ParticleIds.Water].Info.HeatCapacity;
            world.Heat.SetHeat(4, 4, WaterParticle.BoilingPoint * capacity); // T = Q / C = 沸点

            world.Step();

            Assert.AreEqual(ParticleIds.Steam, world.GetCell(4, 4).typeId, "水到沸点应变蒸汽");
            Assert.AreEqual(WaterParticle.BoilingPoint * capacity, world.Heat.GetHeat(4, 4), "相变携带热量");
        }

        /// <summary>沸点以下的水不蒸发，照常下落。</summary>
        [Test]
        public void Water_BelowBoiling_DoesNotEvaporate()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Set(4, 4, ParticleIds.Water);
            world.Heat.SetHeat(4, 4, 4f); // T = 1，远低于沸点

            world.Step();

            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 3).typeId, "水正常下落");
            Assert.AreEqual(0, CountType(world, ParticleIds.Steam), "不产生蒸汽");
        }

        /// <summary>蒸汽降到露点冷凝成云，热量携带。</summary>
        [Test]
        public void Steam_Condenses_WhenAtOrBelowDewPoint()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Heat.Conductivity = 0f; // 同上：只看相变
            world.Set(4, 6, ParticleIds.Steam);
            world.Heat.SetHeat(4, 6, 10f); // T=10 ≤ 露点 45
            world.Step();

            Assert.AreEqual(ParticleIds.Cloud, world.GetCell(4, 6).typeId, "蒸汽到露点应变云");
            Assert.AreEqual(10f, world.Heat.GetHeat(4, 6), "相变携带热量");
        }

        /// <summary>散滴聚团：同排相隔 5 格（搜索半径内）的云滴会互相靠拢，收敛成相邻的一团。</summary>
        [Test]
        public void Cloud_Strays_GatherIntoACluster()
        {
            ParticleWorld world = new ParticleWorld(16, 8, 7u);
            world.Heat.Conductivity = 0f;
            world.Set(3, 4, ParticleIds.Cloud);
            world.Set(8, 4, ParticleIds.Cloud);
            world.Heat.SetHeat(3, 4, 20f); // 冷区（T=10 ≤ 露点），不会被温度规则蒸散
            world.Heat.SetHeat(8, 4, 20f);

            for (int i = 0; i < 60; i++)
            {
                world.Step();
            }

            int cloudX1 = -1;
            int cloudY1 = -1;
            int cloudX2 = -1;
            int cloudY2 = -1;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId != ParticleIds.Cloud)
                    {
                        continue;
                    }

                    if (cloudX1 < 0)
                    {
                        cloudX1 = x;
                        cloudY1 = y;
                    }
                    else
                    {
                        cloudX2 = x;
                        cloudY2 = y;
                    }
                }
            }

            Assert.AreEqual(2, CountType(world, ParticleIds.Cloud), "云量守恒");
            Assert.GreaterOrEqual(cloudX2, 0, "两滴云都应还在世界里");
            int dx = cloudX2 > cloudX1 ? cloudX2 - cloudX1 : cloudX1 - cloudX2;
            int dy = cloudY2 > cloudY1 ? cloudY2 - cloudY1 : cloudY1 - cloudY2;
            int distance = dx > dy ? dx : dy;
            Assert.LessOrEqual(distance, 2, "散滴应聚拢成相邻的一团（云会缓沉，所以按全网格的切比雪夫距离断言）");
            Assert.AreEqual(0, CountType(world, ParticleIds.Steam), "冷区不应蒸成蒸汽（会闪变回去）");
        }

        /// <summary>云漂回暖区蒸回蒸汽（云边消散）。</summary>
        [Test]
        public void Cloud_ReEvaporates_WhenWarm()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Set(4, 4, ParticleIds.Cloud);
            world.Heat.SetHeat(4, 4, (SteamParticle.DewPoint + 30f) * 2f); // 云热容 2 → T = 露点 + 30

            world.Step();

            Assert.AreEqual(ParticleIds.Steam, world.GetCell(4, 4).typeId, "暖区云应蒸回蒸汽");
        }

        /// <summary>碰并：足够密集的云群长时间后产雨；水+蒸汽+云总量严格守恒（1:1 相变，无源无汇）。</summary>
        [Test]
        public void Cloud_Cluster_Coalesces_IntoRain_ConservingMass()
        {
            ParticleWorld world = new ParticleWorld(16, 16, 7u);
            // 3x3 冷云团悬在半空。
            for (int y = 8; y <= 10; y++)
            {
                for (int x = 6; x <= 8; x++)
                {
                    world.Set(x, y, ParticleIds.Cloud);
                    world.Heat.SetHeat(x, y, 20f); // 云热容 2 → T=10，冷区
                }
            }

            int before = CountType(world, ParticleIds.Cloud);
            for (int i = 0; i < 200; i++)
            {
                world.Step();
            }

            int wet = CountType(world, ParticleIds.Water) + CountType(world, ParticleIds.Cloud)
                      + CountType(world, ParticleIds.Steam);
            Assert.AreEqual(before, wet, "水+云+蒸汽总量守恒");
            Assert.Greater(CountType(world, ParticleIds.Water), 0, "200 tick 内应碰并出雨");
        }

        /// <summary>生产注册路径：蒸汽/云/大气回填出互不相同的非 0 id，都带 Gas 标签、非静态。</summary>
        [Test]
        public void ProductionRegistration_BackfillsGasTaggedIds()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, ParticleIds.Steam);
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, ParticleIds.Cloud);
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, ParticleIds.Atmosphere);
            Assert.AreNotEqual(ParticleIds.Steam, ParticleIds.Cloud);
            Assert.AreNotEqual(ParticleIds.Steam, ParticleIds.Atmosphere);
            Assert.AreNotEqual(ParticleIds.Cloud, ParticleIds.Atmosphere);

            AssertGas(ParticleRegistry.Types[ParticleIds.Steam], "蒸汽");
            AssertGas(ParticleRegistry.Types[ParticleIds.Cloud], "云");
            AssertGas(ParticleRegistry.Types[ParticleIds.Atmosphere], "大气");
            Assert.AreEqual(1f, ParticleRegistry.Types[ParticleIds.Atmosphere].Info.HeatCapacity,
                "大气热容必须等于 HeatMap.AirHeatCapacity，空气格与大气粒子温度口径才一致");
        }

        /// <summary>透明粒子契约：调色板 alpha == 0 的格（大气）在像素缓冲里显示为背景色，与空气格一致。</summary>
        [Test]
        public void TransparentPalette_RendersAsBackground()
        {
            ParticleWorld world = new ParticleWorld(4, 4, 7u);
            world.Set(1, 1, ParticleIds.Atmosphere);

            var background = new Color32(9, 8, 7, 255);
            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background);

            Assert.AreEqual(background, pixels[world.Index(1, 1)], "透明大气格应显示背景色");
            Assert.AreEqual(background, pixels[world.Index(2, 2)], "空气格本来就是背景色（对照）");
        }

        private static void AssertGas(ParticleType type, string name)
        {
            Assert.IsTrue((type.Info.Tags & ParticleTags.Gas) != 0, name + "必须带 Gas 标签");
            Assert.IsFalse(type.Info.IsStatic, name + "不是静态类型");
        }
    }
}
