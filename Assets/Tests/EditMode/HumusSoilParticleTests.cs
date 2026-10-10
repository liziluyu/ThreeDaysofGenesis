using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 腐殖土（<see cref="HumusSoilParticle"/>）的随机刻扩张与湿润腐殖土
    /// （<see cref="WetHumusSoilParticle"/>，与湿土逐条同构的湿润变体）。
    /// </summary>
    /// <remarks>
    /// <para>跨类型相变（土壤 → 腐殖土 / 湿润腐殖土、湿润腐殖土 → 干腐殖土）依赖 <see cref="ParticleIds"/>
    /// 回填，隔离类型表里这些字段是 0，因此走 <see cref="ParticleRegistration.RegisterAll"/> 的全局表。</para>
    /// <para>随机刻用 <see cref="ParticleType.OnRandomTick"/> 直接调用、逻辑帧用 <see cref="ParticleType.OnLogic"/>
    /// 直接调用（仓库既有惯例），保证确定、不依赖世界随机抽格；涉及概率门的迁移用「多次调用 + 窗口断言」。</para>
    /// </remarks>
    public sealed class HumusSoilParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ushort Soil
        {
            get { return ParticleIds.Soil; }
        }

        private static ushort WetSoil
        {
            get { return ParticleIds.WetSoil; }
        }

        private static ushort Humus
        {
            get { return ParticleIds.Humus; }
        }

        private static ushort HumusSoil
        {
            get { return ParticleIds.HumusSoil; }
        }

        private static ushort WetHumusSoil
        {
            get { return ParticleIds.WetHumusSoil; }
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
            return CountType(world, ParticleIds.Water) + CountType(world, WetSoil)
                + CountType(world, WetHumusSoil) + world.Humidity.Sum();
        }

        private static float TotalHeat(ParticleWorld world)
        {
            float total = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                total += world.Heat.Values[i];
            }

            return total;
        }

        private static void RandomTick(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnRandomTick(world, x, y);
        }

        private static void Logic(ParticleWorld world, ushort typeId, int x, int y)
        {
            ParticleRegistry.Types[typeId].OnLogic(world, x, y);
        }

        // ============================== 生产注册 ==============================

        /// <summary>
        /// 生产注册路径：RegisterAll 必须回填出非 0、与腐殖土 / 土 / 湿土都不重复的湿润腐殖土 id——
        /// 带 Solid 标签、受重力、非静态（收得到随机刻与逻辑帧）。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsSolidWetHumusSoilId()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, WetHumusSoil, "湿润腐殖土 id 应被回填");
            Assert.AreNotEqual(HumusSoil, WetHumusSoil);
            Assert.AreNotEqual(Soil, WetHumusSoil);
            Assert.AreNotEqual(WetSoil, WetHumusSoil);

            ParticleTypeInfo info = ParticleRegistry.Types[WetHumusSoil].Info;
            Assert.IsTrue((info.Tags & ParticleTags.Solid) != 0, "湿润腐殖土必须带 Solid 标签");
            Assert.IsTrue(info.AffectedByGravity, "湿润腐殖土受重力");
            Assert.IsFalse(info.IsStatic, "湿润腐殖土不是静态类型");
        }

        // ============================== 腐殖土的随机刻扩张 ==============================

        /// <summary>
        /// 正上方是腐殖质、附近有普通土壤：土壤变成腐殖土，上方那格腐殖质被吸收（Clear → 空气）。
        /// </summary>
        [Test]
        public void HumusSoil_Spread_TurnsNearbySoilAndConsumesHumusAbove()
        {
            var world = new ParticleWorld(9, 9, 20261010u);
            world.Set(4, 3, HumusSoil);
            world.Set(4, 4, Humus);       // 正上方是腐殖质
            world.Set(5, 3, Soil);        // 邻近的普通土壤

            RandomTick(world, HumusSoil, 4, 3);

            Assert.AreEqual(HumusSoil, world.GetCell(5, 3).typeId, "邻近土壤应被腐殖质改良成腐殖土");
            Assert.AreEqual((int)ParticleIds.Air, (int)world.GetCell(4, 4).typeId,
                "上方腐殖质应被吸收（Clear → 空气）");
            Assert.AreEqual(HumusSoil, world.GetCell(4, 3).typeId, "触发扩张的腐殖土自身不变");
        }

        /// <summary>
        /// 邻近是**湿润土壤**时产出的必须是**湿润腐殖土**（水原样带进湿润形态，水账不失），
        /// 不是普通腐殖土——否则水会在换形态时凭空消失。
        /// </summary>
        [Test]
        public void HumusSoil_Spread_OnWetSoil_ProducesWetHumusSoil_AndConservesWater()
        {
            var world = new ParticleWorld(9, 9, 20261010u);
            world.Set(4, 3, HumusSoil);
            world.Set(4, 4, Humus);
            world.Set(5, 3, WetSoil);

            float waterBefore = TotalWater(world);

            RandomTick(world, HumusSoil, 4, 3);

            Assert.AreEqual(WetHumusSoil, world.GetCell(5, 3).typeId,
                "湿润土壤应被改良成湿润腐殖土（水跟着物质走）");
            Assert.AreEqual((int)ParticleIds.Air, (int)world.GetCell(4, 4).typeId, "上方腐殖质被吸收");
            Assert.AreEqual(waterBefore, TotalWater(world), 1e-4f, "水总量守恒（湿态换湿态）");
        }

        /// <summary>
        /// **从近到远**选目标（欧氏距离），与扫描序无关：把一个**更近**的土壤放在扫描序**更晚**的位置，
        /// 仍应选中更近的那颗——若实现退化成「扫描序第一个」就会选错。
        /// </summary>
        [Test]
        public void HumusSoil_Spread_SelectsNearestSoil_NotFirstInScanOrder()
        {
            var world = new ParticleWorld(9, 9, 20261010u);
            world.Set(4, 4, HumusSoil);
            world.Set(4, 5, Humus);

            // 扫描序 dy 升序：dy = −2 那一行（更远，距离² = 4）先被扫到；
            // dy = 0 那一行（更近，距离² = 1）后扫到。正确实现必须选更近的 (3,4)。
            world.Set(4, 2, Soil);   // 远：dx = 0, dy = −2
            world.Set(3, 4, Soil);   // 近：dx = −1, dy = 0

            RandomTick(world, HumusSoil, 4, 4);

            Assert.AreEqual(HumusSoil, world.GetCell(3, 4).typeId, "应改良距离最近的土壤 (3,4)");
            Assert.AreEqual(Soil, world.GetCell(4, 2).typeId, "更远的土壤 (4,2) 不动");
        }

        /// <summary>正上方不是腐殖质时什么都不做（土壤不会被凭空改良）。</summary>
        [Test]
        public void HumusSoil_Spread_WithoutHumusAbove_DoesNothing()
        {
            var world = new ParticleWorld(9, 9, 20261010u);
            world.Set(4, 3, HumusSoil);
            world.Set(5, 3, Soil);
            // (4,4) 留空（空气）——不是腐殖质

            RandomTick(world, HumusSoil, 4, 3);

            Assert.AreEqual(Soil, world.GetCell(5, 3).typeId, "上方无腐殖质时不得改良任何土壤");
        }

        /// <summary>附近没有普通 / 湿润土壤时什么都不做：腐殖质留在原地等下一次随机刻。</summary>
        [Test]
        public void HumusSoil_Spread_WithoutNearbySoil_LeavesHumusInPlace()
        {
            var world = new ParticleWorld(9, 9, 20261010u);
            world.Set(4, 3, HumusSoil);
            world.Set(4, 4, Humus);
            // 半径 2 内没有土壤

            RandomTick(world, HumusSoil, 4, 3);

            Assert.AreEqual(Humus, world.GetCell(4, 4).typeId, "无可改良土壤时腐殖质不得被消耗");
        }

        /// <summary>
        /// **先判后写**：附近没有可改良的土壤时不消耗世界随机流——对两个完全相同世界中的一个调用随机刻，
        /// 之后两世界的下一个随机数必须相同（无目标时不换形态、也就不摇 shade 抽样）。
        /// 对照组里两世界都放同样的腐殖土 / 腐殖质，但**都不放土壤**。
        /// </summary>
        [Test]
        public void HumusSoil_Spread_WithoutTarget_DoesNotConsumeRandomStream()
        {
            var triggered = new ParticleWorld(9, 9, 20261010u);
            var control = new ParticleWorld(9, 9, 20261010u);
            foreach (ParticleWorld world in new[] { triggered, control })
            {
                world.Set(4, 3, HumusSoil);
                world.Set(4, 4, Humus);   // 有腐殖质但附近无土壤
            }

            RandomTick(triggered, HumusSoil, 4, 3);

            Assert.AreEqual(control.NextUInt(), triggered.NextUInt(),
                "无可改良目标时不得消耗世界随机流（先判后写）");
        }

        // ============================== 湿润腐殖土的逻辑（与湿土同构） ==============================

        /// <summary>
        /// **孤立湿润腐殖土永久持水**（田间持水量）：正上方是空气 = 无压，下方有干腐殖土也不渗。
        /// </summary>
        [Test]
        public void WetHumusSoil_WithoutPressureFromAbove_HoldsWaterForever()
        {
            var world = new ParticleWorld(1, 8, 20261010u);
            world.Set(0, 0, ParticleIds.Stone);
            for (int y = 1; y <= 5; y++)
            {
                world.Set(0, y, HumusSoil);
            }

            world.Set(0, 6, WetHumusSoil); // 上方是空气 = 无压

            for (int i = 0; i < 100; i++)
            {
                Logic(world, WetHumusSoil, 0, 6);
            }

            Assert.AreEqual(WetHumusSoil, world.GetCell(0, 6).typeId, "无压湿润腐殖土应原地持水，不往下渗");
            for (int y = 1; y <= 5; y++)
            {
                Assert.AreEqual(HumusSoil, world.GetCell(0, y).typeId, $"第 {y} 层应始终是干腐殖土");
            }
        }

        /// <summary>
        /// 常温（T &lt; 60）有压才渗：上方是水（水头压力）时，湿润腐殖土把水交给正下方的干腐殖土，
        /// 自己变回干腐殖土、目标变成湿润腐殖土（1:1 搬运，水与热都随物质走）。
        /// </summary>
        [Test]
        public void WetHumusSoil_UnderPressure_PercolatesDownToDryHumusSoil()
        {
            var world = new ParticleWorld(1, 8, 20261010u);
            world.Set(0, 0, ParticleIds.Stone);
            world.Set(0, 1, HumusSoil);
            world.Set(0, 2, WetHumusSoil);
            world.Set(0, 3, ParticleIds.Water);   // 水头压力

            int frames = 0;
            while (world.GetCell(0, 1).typeId != WetHumusSoil && frames < 300)
            {
                Logic(world, WetHumusSoil, 0, 2);
                frames++;
            }

            Assert.Less(frames, 300, "有压时水应在窗口内渗到正下方的干腐殖土");
            Assert.AreEqual(WetHumusSoil, world.GetCell(0, 1).typeId, "正下方干腐殖土接到水");
            Assert.AreEqual(HumusSoil, world.GetCell(0, 2).typeId, "给出水后自己变回干腐殖土");
        }

        /// <summary>
        /// 受热上行（T ≥ 60）：水改往**上**走——上方是干腐殖土就润湿之，本格变回干腐殖土。
        /// </summary>
        [Test]
        public void WetHumusSoil_WhenHeated_MovesWaterUpToDryHumusSoilAbove()
        {
            var world = new ParticleWorld(1, 8, 20261010u);
            world.Set(0, 0, ParticleIds.Stone);
            world.Set(0, 2, WetHumusSoil);
            world.Set(0, 3, HumusSoil);   // 上方是干腐殖土（非气体 ⇒ 只走上行转移、不蒸发）

            float capacity = ParticleRegistry.Types[WetHumusSoil].Info.HeatCapacity;
            world.Heat.SetHeat(0, 2, (WetHumusSoilParticle.DryingTemperature + 20f) * capacity);

            int frames = 0;
            while (world.GetCell(0, 3).typeId != WetHumusSoil && frames < 300)
            {
                Logic(world, WetHumusSoil, 0, 2);
                frames++;
            }

            Assert.Less(frames, 300, "受热时水应在窗口内上移到上方的干腐殖土");
            Assert.AreEqual(WetHumusSoil, world.GetCell(0, 3).typeId, "上方干腐殖土被润湿");
            Assert.AreEqual(HumusSoil, world.GetCell(0, 2).typeId, "给出水后自己变回干腐殖土");
        }

        // ============================== 湿润腐殖土算「土壤」（对其他类型可见） ==============================

        /// <summary>
        /// 湿润腐殖土对别的类型而言就是**土壤**：草扎根在它上面不会因失去扎根而死
        /// （对照组：扎在石头上则死）。温度置于草的存活区间（3 &lt; T &lt; 50）以隔离扎根这一条判据。
        /// </summary>
        [Test]
        public void WetHumusSoil_CountsAsSoil_ForGrassRooting()
        {
            var onWetHumus = new ParticleWorld(8, 8, 20261010u);
            onWetHumus.Set(4, 3, WetHumusSoil);
            onWetHumus.Set(4, 4, ParticleIds.Grass);
            onWetHumus.Heat.SetHeat(4, 4, 25f * ParticleRegistry.Types[ParticleIds.Grass].Info.HeatCapacity);

            RandomTick(onWetHumus, ParticleIds.Grass, 4, 4);

            Assert.AreEqual(ParticleIds.Grass, onWetHumus.GetCell(4, 4).typeId,
                "草扎根在湿润腐殖土上应存活（湿润腐殖土算土壤）");

            var onStone = new ParticleWorld(8, 8, 20261010u);
            onStone.Set(4, 3, ParticleIds.Stone);
            onStone.Set(4, 4, ParticleIds.Grass);
            onStone.Heat.SetHeat(4, 4, 25f * ParticleRegistry.Types[ParticleIds.Grass].Info.HeatCapacity);

            RandomTick(onStone, ParticleIds.Grass, 4, 4);

            Assert.AreNotEqual(ParticleIds.Grass, onStone.GetCell(4, 4).typeId,
                "对照组：草扎根在石头上应失去扎根而死");
        }

        /// <summary>水的随机刻冲刷把湿润腐殖土也当土壤处理（→ 沙）。</summary>
        [Test]
        public void Water_Erosion_TargetsWetHumusSoil()
        {
            var world = new ParticleWorld(9, 9, 20261010u);
            world.Set(4, 4, ParticleIds.Water);
            world.Set(5, 4, WetHumusSoil);

            int frames = 0;
            while (world.GetCell(5, 4).typeId == WetHumusSoil && frames < 300)
            {
                RandomTick(world, ParticleIds.Water, 4, 4);
                frames++;
            }

            Assert.Less(frames, 300, "水的冲刷应在窗口内命中湿润腐殖土");
            Assert.AreEqual(ParticleIds.Sand, world.GetCell(5, 4).typeId, "湿润腐殖土被冲刷成沙");
        }

        // ============================== 火：烘干湿润腐殖土 ==============================

        /// <summary>
        /// 火把相邻的湿润腐殖土烤成**干腐殖土**，潜热按湿润腐殖土→干腐殖土的热容差 1.2T 结算
        /// （与湿土→干土同值）：隔离结算（关传导 / 散热、火温置软上限本帧不注入），
        /// 全场总热的变化只剩这一步相变，且干化后温度仍为正。
        /// </summary>
        [Test]
        public void Fire_Drying_SubtractsWetToDryCapacityDifference_ForWetHumus()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Set(8, 0, WetHumusSoil);
            world.Heat.SetHeat(8, 0, 80f * ParticleRegistry.Types[WetHumusSoil].Info.HeatCapacity); // T = 80
            world.Set(8, 1, ParticleIds.Fire);
            world.Heat.SetHeat(8, 1, FireParticle.PeakTemperature);

            float heatBefore = TotalHeat(world);

            Logic(world, ParticleIds.Fire, 8, 1);

            Assert.AreEqual((int)HumusSoil, (int)world.GetCell(8, 0).typeId,
                "湿润腐殖土应被烤成干腐殖土（不是干土）");
            Assert.AreEqual(
                heatBefore - 1.2f * 80f, TotalHeat(world), 1e-3f,
                "干化潜热 = −1.2 × T（湿润腐殖土热容 2.2 − 干腐殖土热容 1.0）");
            Assert.AreEqual(80f, world.Heat.GetTemperature(8, 0), 1e-3f,
                "干化后温度等于原湿润腐殖土温度（热容差补齐 ⇒ 温度连续、恒为正）");
            Assert.AreEqual(1f, world.Humidity.Sum(), 1e-3f, "一份水汽进了湿度场（水总量守恒）");
        }
    }
}
