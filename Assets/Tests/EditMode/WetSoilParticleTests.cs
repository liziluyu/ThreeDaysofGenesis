using NUnit.Framework;
using UnityEngine;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 湿土（<see cref="WetSoilParticle"/>）与土的吸水反应：水被土喝掉、
    /// 湿土把这份水往下渗 / 受热往上送 / 从表面蒸发进空气，以及生产注册路径的回填与标签。
    /// </summary>
    /// <remarks>
    /// <para>跨类型相变（土 ↔ 湿土）依赖 <see cref="ParticleIds.WetSoil"/> / <see cref="ParticleIds.Soil"/>
    /// 的回填，隔离类型表里这些字段是 0，因此本组测试走 <see cref="ParticleRegistration.RegisterAll"/>
    /// 的全局表——与 <c>WeatherParticleTests</c> 的取舍一致。</para>
    /// <para>反应都在**逻辑帧**（<see cref="ParticleWorld.StepLogic"/>）里跑，运动在**运动帧**（<see cref="ParticleWorld.StepMotion"/>）；
    /// 前者也含一次运动扫描，所以只关心反应时用 1 格宽的井（水无处横流、土无处斜落），结果完全确定。</para>
    /// <para>水总量 = 水格 + 湿土格（每格湿土含一份水）+ 湿度场总量，守恒断言一律用它。</para>
    /// </remarks>
    public sealed class WetSoilParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Sand
        {
            get { return ParticleIds.Sand; }
        }

        private static ushort Soil
        {
            get { return ParticleIds.Soil; }
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort WetSoil
        {
            get { return ParticleIds.WetSoil; }
        }

        private static float WetSoilHeatCapacity
        {
            get { return ParticleRegistry.Types[WetSoil].Info.HeatCapacity; }
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
            return CountType(world, Water) + CountType(world, WetSoil) + world.Humidity.Sum();
        }

        private static float Luminance(Color32 color)
        {
            return 0.299f * color.r + 0.587f * color.g + 0.114f * color.b;
        }

        /// <summary>把 (x,y) 加热到干燥阈值以上。</summary>
        private static void HeatAboveDryingPoint(ParticleWorld world, int x, int y)
        {
            world.Heat.SetHeat(x, y, WetSoilParticle.DryingTemperature * WetSoilHeatCapacity + 1f);
        }

        /// <summary>
        /// 生产注册路径：RegisterAll 必须回填出非 0 且不与其它类型重复的湿土 id，带 Solid 标签、受重力、非静态。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsSolidWetSoilId()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, WetSoil, "湿土 id 应被回填");
            Assert.AreNotEqual(Stone, WetSoil);
            Assert.AreNotEqual(Sand, WetSoil);
            Assert.AreNotEqual(Soil, WetSoil);
            Assert.AreNotEqual(Water, WetSoil);
            Assert.AreNotEqual(ParticleIds.Atmosphere, WetSoil);

            ParticleTypeInfo info = ParticleRegistry.Types[WetSoil].Info;
            Assert.IsTrue((info.Tags & ParticleTags.Solid) != 0, "湿土必须带 Solid 标签（仍是固体粉末）");
            Assert.IsTrue(info.AffectedByGravity, "湿土受重力");
            Assert.IsFalse(info.IsStatic, "湿土不是静态类型");
        }

        /// <summary>水把正下方的土变成湿土，**自己随即被吸干**，水格的热量并进湿土（总热量不变）。</summary>
        [Test]
        public void Water_WetsSoilBeneathIt_AndIsConsumed()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, Water);
            world.Heat.SetHeat(0, 1, 3f);
            world.Heat.SetHeat(0, 2, 5f);
            float heatBefore = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                heatBefore += world.Heat.Values[i];
            }

            float waterBefore = TotalWater(world);
            world.StepLogic();

            Assert.AreEqual(WetSoil, world.GetCell(0, 1).typeId, "正上方是水，土应吸饱水变成湿土");
            Assert.AreEqual(0, CountType(world, Water), "水润湿一格后应被吸干");
            Assert.IsTrue(world.IsAir(0, 2), "水原来那一格应空出来");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-4f, "水总量守恒（水格 → 湿土格）");

            float heatAfter = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                heatAfter += world.Heat.Values[i];
            }

            Assert.AreEqual(heatBefore, heatAfter, 1e-4f, "水被吸走时它的热量应并进湿土（总热量守恒）");
        }

        /// <summary>
        /// 常温下渗：湿土把这份水**一格格往下送**（每逻辑帧 50%），自身变干；
        /// 全程只有一份水在动（湿土格数恒为 1），最后停在土柱底部。
        /// </summary>
        [Test]
        public void WetSoil_PercolatesDownOneCell_KeepingASingleWaterUnit()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            for (int y = 1; y <= 6; y++)
            {
                world.Set(0, y, Soil);
            }

            world.Set(0, 6, WetSoil);

            for (int i = 0; i < 60; i++)
            {
                world.StepLogic();
                Assert.AreEqual(1, CountType(world, WetSoil), $"第 {i} 帧仍应只有一份水（在湿土里）");
                Assert.AreEqual(1f, TotalWater(world), 1e-3f, $"第 {i} 帧水总量守恒");
            }

            Assert.AreEqual(WetSoil, world.GetCell(0, 1).typeId, "水应渗到土柱底部（石头之上）");
            Assert.AreEqual(0, CountType(world, Water), "水渗下去的过程中没有重新变成水格");
            for (int y = 2; y <= 6; y++)
            {
                Assert.AreEqual(Soil, world.GetCell(0, y).typeId, $"第 {y} 层应已变回干土");
            }
        }

        /// <summary>
        /// 受热上行：湿土温度达到阈值时，水改往上走——润湿上方那格干土，自身变干。
        /// </summary>
        [Test]
        public void HotWetSoil_WetsTheDrySoilAbove_AndTurnsDry()
        {
            ParticleWorld world = new ParticleWorld(1, 8, 20261004u);
            world.Set(0, 0, Stone);
            world.Set(0, 1, Soil);
            world.Set(0, 2, WetSoil);
            world.Set(0, 3, Soil);
            HeatAboveDryingPoint(world, 0, 2);

            world.StepLogic();

            Assert.AreEqual(WetSoil, world.GetCell(0, 3).typeId, "水应上行一格，润湿上方的干土");
            Assert.AreEqual(Soil, world.GetCell(0, 2).typeId, "原湿土应变干");
            Assert.AreEqual(Soil, world.GetCell(0, 1).typeId, "水不应同时往下走");
            Assert.AreEqual(1f, TotalWater(world), 1e-3f, "水总量守恒");
        }

        /// <summary>
        /// 受热蒸发：湿土上方是空气且空气没吸饱时，像水粒子慢蒸发一样把一份水汽摊进空气，自身变干。
        /// **直接调该类型的逻辑钩子**（不走整帧），因此测的就是"播撒本身"：摊出来的水汽每格都低于可见阈值。
        /// </summary>
        [Test]
        public void HotWetSoil_UnderAir_EvaporatesInvisibly()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(4, 1, WetSoil);
            HeatAboveDryingPoint(world, 4, 1);

            ParticleRegistry.Types[WetSoil].OnLogic(world, 4, 1);

            Assert.AreEqual(Soil, world.GetCell(4, 1).typeId, "蒸发后本格变干土");
            Assert.AreEqual(0, CountType(world, WetSoil), "湿土已消失");
            Assert.AreEqual(HumidityField.Unit, world.Humidity.Sum(), 1e-3f, "一份水汽（水总量守恒）");
            Assert.Greater(world.Humidity.Get(4, 2), 0f, "正上方的空气接到水汽");

            for (int i = 0; i < world.CellCount; i++)
            {
                Assert.Less(world.Humidity.GetAt(i), HumidityField.VisibleThreshold,
                    "土壤干燥蒸发必须不可见（下标 " + i + "）");
            }
        }

        /// <summary>
        /// 同一条蒸发路径在完整逻辑帧里的表现：湿土变干、水汽进了空气、水总量守恒。
        /// （整帧里湿度场随后会按自己的规则输送、在热处辐合——那是"云"的成因，不属于蒸发播撒的可见性契约。）
        /// </summary>
        [Test]
        public void HotWetSoil_UnderAir_EvaporatesThroughAFrame()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(4, 1, WetSoil);
            HeatAboveDryingPoint(world, 4, 1);
            float waterBefore = TotalWater(world);

            world.StepLogic();

            Assert.AreEqual(Soil, world.GetCell(4, 1).typeId, "蒸发后本格变干土");
            Assert.AreEqual(0, CountType(world, WetSoil), "湿土已消失");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-3f, "水总量守恒（湿土格 → 水汽）");
            Assert.Greater(world.Humidity.Sum(), 0f, "水汽进了空气");
        }

        /// <summary>
        /// 上方空气已经吸饱（≥ <see cref="HumidityField.InvisibleSpreadCap"/>）时蒸不动：
        /// 湿土保持湿土，湿度不增加（饱和水汽压的节流）。
        /// </summary>
        [Test]
        public void HotWetSoil_UnderSaturatedAir_DoesNotEvaporate()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(4, 1, WetSoil);
            world.Humidity.Set(4, 2, HumidityField.InvisibleSpreadCap);
            HeatAboveDryingPoint(world, 4, 1);
            float waterBefore = TotalWater(world);

            world.StepLogic();

            Assert.AreEqual(WetSoil, world.GetCell(4, 1).typeId, "空气吸饱了就不该蒸发，湿土保持不变");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-3f, "水总量守恒");
        }

        /// <summary>
        /// 没有干土可接、也不是热天：水留在土里。石头/沙/水都接不住水（它们是别的物质，没有"湿"的形态）；
        /// 常温下上方是空气也**不蒸发**——蒸发是受热才发生的事。
        /// </summary>
        [Test]
        public void WetSoil_WithNoDrySoilNeighbour_NeverDries()
        {
            ParticleWorld world = new ParticleWorld(3, 8, 20261004u);
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, Stone);
            }

            world.Set(0, 1, Sand);
            world.Set(1, 1, WetSoil);
            world.Set(2, 1, Stone);
            // 上方是空气：常温下不该蒸发。

            for (int i = 0; i < 40; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(WetSoil, world.GetCell(1, 1).typeId, "四周没有干土、温度也低，湿土不该变干");
            Assert.AreEqual(1, CountType(world, WetSoil), "那一份水仍在湿土里");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "常温湿土不蒸发");
        }

        /// <summary>湿土仍是固体：悬空时照样逐帧下落（运动帧负责，与逻辑帧的水迁移互不干扰）。</summary>
        [Test]
        public void WetSoil_StillFallsLikeASolid()
        {
            ParticleWorld world = new ParticleWorld(3, 10, 20261004u);
            world.Set(1, 0, Stone);
            world.Set(1, 7, WetSoil);

            world.StepMotion();
            Assert.AreEqual(WetSoil, world.GetCell(1, 6).typeId, "运动帧应直落一格");
            Assert.AreEqual(1, CountType(world, WetSoil), "水没有丢");
        }

        /// <summary>湿土的调色板逐档都比干土暗——「颜色更深」是用户可见的契约。</summary>
        [Test]
        public void WetSoilPalette_IsDarkerThanDrySoil()
        {
            Color32[] soil = ParticleRegistry.Types[Soil].Info.Palette;
            Color32[] wet = ParticleRegistry.Types[WetSoil].Info.Palette;

            Assert.AreEqual(soil.Length, wet.Length, "两种土的调色板档数应一致（逐档对应）");
            for (int i = 0; i < soil.Length; i++)
            {
                Assert.Less(Luminance(wet[i]), Luminance(soil[i]), $"第 {i} 档湿土应比干土深");
            }
        }
    }
}
