using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;
using UnityEngine.TestTools;

namespace TapTap2026.Tests
{
    public sealed class ParticleWorldTests
    {
        private const int W = 16;
        private const int H = 16;

        [SetUp]
        public void SetUp()
        {
            TestTypes.ResetCounters();
        }

        [TearDown]
        public void TearDown()
        {
            // 长模拟测试会临时打开「忽略失败日志」（见 Stickiness_RaisesTheReposeSlopeOfAPile），
            // 这里保证无论断言是否抛出都恢复默认，不泄漏到后续测试。
            LogAssert.ignoreFailingMessages = false;
        }

        private static ParticleWorld NewWorld(uint seed = 12345u)
        {
            return new ParticleWorld(W, H, seed, TestTypes.Table);
        }

        // ============================== 基本契约 ==============================

        [Test]
        public void NewWorld_IsAllAir_AndUsesRowMajorIndexing()
        {
            ParticleWorld world = NewWorld();

            Assert.AreEqual(W * H, world.CellCount);
            Assert.AreEqual(W, world.Width);
            Assert.AreEqual(H, world.Height);
            Assert.AreEqual(0, world.Tick);

            for (int i = 0; i < world.CellCount; i++)
            {
                Assert.AreEqual((int)ParticleRegistry.AirTypeId, (int)world.Cells[i].typeId);
            }

            Assert.AreEqual(2 * W + 3, world.Index(3, 2));
        }

        [Test]
        public void ReadAndWrite_OutOfBounds_AreSafe()
        {
            ParticleWorld world = NewWorld();

            Assert.IsFalse(world.IsInBounds(-1, 0));
            Assert.IsFalse(world.IsInBounds(0, H));
            Assert.AreEqual((int)ParticleRegistry.AirTypeId, (int)world.GetCell(-1, 0).typeId);
            Assert.AreEqual((int)ParticleRegistry.AirTypeId, (int)world.GetCell(W, 0).typeId);
            Assert.IsTrue(world.IsAir(-5, -5));

            Assert.IsFalse(world.Set(-1, 0, TestTypes.GrainId));
            Assert.IsFalse(world.Clear(W, 0));
            Assert.IsFalse(world.Swap(0, 0, -1, 0));
            Assert.IsFalse(world.Swap(0, 0, 0, H));

            for (int i = 0; i < world.CellCount; i++)
            {
                Assert.AreEqual((int)ParticleRegistry.AirTypeId, (int)world.Cells[i].typeId);
            }
        }

        // ============================== 热图（Q / 温度） ==============================

        [Test]
        public void Heat_DefaultsToAmbientEverywhere()
        {
            ParticleWorld world = NewWorld();

            Assert.AreEqual(W, world.Heat.Width);
            Assert.AreEqual(H, world.Heat.Height);
            Assert.AreEqual(W * H, world.Heat.CellCount);

            for (int i = 0; i < world.Heat.CellCount; i++)
            {
                Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.Values[i]);
            }
        }

        [Test]
        public void Heat_SetHeatAndGetHeat_RoundTripsPerCell_WithRowMajorIndexing()
        {
            ParticleWorld world = NewWorld();

            Assert.IsTrue(world.Heat.SetHeat(3, 2, 42.5f));
            Assert.IsTrue(world.Heat.SetHeat(0, 0, -10f));
            Assert.IsTrue(world.Heat.SetHeat(W - 1, H - 1, 900f));

            Assert.AreEqual(42.5f, world.Heat.GetHeat(3, 2));
            Assert.AreEqual(-10f, world.Heat.GetHeat(0, 0));
            Assert.AreEqual(900f, world.Heat.GetHeat(W - 1, H - 1));
            Assert.AreEqual(42.5f, world.Heat.Values[2 * W + 3], "下标必须与 Cell 一致：y * Width + x");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(3, 3), "不同格之间不得串值");
        }

        [Test]
        public void Heat_OutOfBounds_ReadsAmbient_AndWritesAreNoOps()
        {
            ParticleWorld world = NewWorld();
            world.Heat.SetHeat(0, 0, 7f);

            Assert.IsFalse(world.Heat.IsInBounds(-1, 0));
            Assert.IsFalse(world.Heat.IsInBounds(0, H));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(-1, 0));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(W, 0));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(0, -1));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(0, H));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetTemperature(-1, 0));

            Assert.IsFalse(world.Heat.SetHeat(-1, 0, 100f));
            Assert.IsFalse(world.Heat.SetHeat(W, 0, 100f));
            Assert.IsFalse(world.Heat.AddHeat(0, H, 100f));

            Assert.AreEqual(7f, world.Heat.GetHeat(0, 0));
            for (int i = 1; i < world.Heat.CellCount; i++)
            {
                Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.Values[i], "越界写不得影响界内任何格");
            }
        }

        [Test]
        public void Heat_AddHeatAccumulatesOntoTheCellsCurrentValue()
        {
            ParticleWorld world = NewWorld();
            world.Heat.SetHeat(5, 5, 10f);

            Assert.IsTrue(world.Heat.AddHeat(5, 5, 2.5f));
            Assert.IsTrue(world.Heat.AddHeat(5, 5, -0.5f));

            Assert.AreEqual(12f, world.Heat.GetHeat(5, 5));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(5, 6));
        }

        [Test]
        public void Heat_TemperatureIsHeatDividedByTheCellsHeatCapacity()
        {
            ParticleWorld world = NewWorld();
            world.Set(5, 5, TestTypes.HeatId);   // 测试热源：热容 2
            world.Heat.SetHeat(5, 5, 10f);
            world.Heat.SetHeat(6, 6, 10f);       // 空气：热容 1

            Assert.AreEqual(2f, world.Heat.GetCapacity(5, 5));
            Assert.AreEqual(5f, world.Heat.GetTemperature(5, 5), "T = Q / C = 10 / 2");
            Assert.AreEqual(1f, world.Heat.GetCapacity(6, 6), "空气用 AirHeatCapacity");
            Assert.AreEqual(10f, world.Heat.GetTemperature(6, 6), "同一份热量，热容小则温度高");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetTemperature(7, 7));
        }

        [Test]
        public void Heat_TemperatureByIndex_MatchesCoordinates()
        {
            ParticleWorld world = NewWorld();
            world.Set(3, 2, TestTypes.HeatId); // 热容 2
            world.Heat.SetHeat(3, 2, 10f);

            int index = world.Index(3, 2);
            Assert.AreEqual(5f, world.Heat.GetTemperatureAt(index), "T = Q / C");
            Assert.AreEqual(world.Heat.GetTemperature(3, 2), world.Heat.GetTemperatureAt(index));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetTemperatureAt(-1));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetTemperatureAt(world.CellCount));
        }

        [Test]
        public void Heat_SwapCarriesHeatWithTheParticle()
        {
            ParticleWorld world = NewWorld();
            world.Set(5, 12, TestTypes.GrainId);
            world.Heat.SetHeat(5, 12, 300f);

            world.Swap(5, 12, 5, 9);

            Assert.AreEqual(300f, world.Heat.GetHeat(5, 9), "热由物质携带：Swap 把 Q 一起搬走");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(5, 12), "旧位置不留下热量");
        }

        [Test]
        public void Heat_FallingParticleKeepsItsHeat()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f; // 关掉传导，单独验证「热随物质走」
            world.Set(5, 12, TestTypes.GrainId);
            world.Heat.SetHeat(5, 12, 300f);

            for (int i = 0; i < 3; i++)
            {
                world.Step();
            }

            Assert.AreEqual((int)TestTypes.GrainId, (int)world.GetCell(5, 9).typeId, "颗粒应已落到 (5,9)");
            Assert.AreEqual(300f, world.Heat.GetHeat(5, 9), "颗粒带着热一起下落");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(5, 12), "离开的位置不留热");
        }

        [Test]
        public void Heat_ReplacingTheMaterialResetsItsHeatToAmbient()
        {
            ParticleWorld world = NewWorld();

            world.Heat.SetHeat(4, 4, 55f);
            world.Set(4, 4, TestTypes.GrainId);
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(4, 4), "生成新物质：从环境热量开始");

            world.Heat.SetHeat(4, 4, 55f);
            world.Set(4, 4, TestTypes.GrainId); // 同类型重复 Set 是空操作
            Assert.AreEqual(55f, world.Heat.GetHeat(4, 4), "同类型重复 Set 不替换物质，热量保留");

            world.Clear(4, 4);
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(4, 4), "抹除：旧物质连同它的热一起离场");
        }

        [Test]
        public void Heat_ConductionSpreadsHeatToFourNeighboursAndConservesTotal()
        {
            ParticleWorld world = NewWorld();
            world.Heat.VoidHeatLoss = 0f;  // 隔离出「界内传导守恒」这一条
            world.Heat.SetHeat(5, 5, 10f); // 全空气：热容 1，故 T = Q

            world.Step();

            // 中心向外散一格：0.15 * 4 邻居 * (0 - 10) = -6 → 4；每个邻居 +0.15 * (10 - 0) = 1.5。
            Assert.AreEqual(4f, world.Heat.GetHeat(5, 5), 1e-4f);
            Assert.AreEqual(1.5f, world.Heat.GetHeat(4, 5), 1e-4f);
            Assert.AreEqual(1.5f, world.Heat.GetHeat(6, 5), 1e-4f);
            Assert.AreEqual(1.5f, world.Heat.GetHeat(5, 4), 1e-4f);
            Assert.AreEqual(1.5f, world.Heat.GetHeat(5, 6), 1e-4f);
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(4, 4), 1e-4f, "只扩散到四邻，不含对角");
            Assert.AreEqual(10f, SumHeat(world), 1e-3f, "传导只搬运热量，总量守恒");
        }

        [Test]
        public void Heat_ConductionIsOffWhenConductivityIsZero()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f;
            world.Heat.SetHeat(5, 5, 10f);

            world.Step();

            Assert.AreEqual(10f, world.Heat.GetHeat(5, 5));
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(4, 5));
        }

        [Test]
        public void Heat_TopRowRadiatesIntoTheVoid_OtherRowsDoNot()
        {
            ParticleWorld world = new ParticleWorld(1, 3, 1u, TestTypes.Table);
            world.Heat.Conductivity = 0f;   // 只看散热这一条
            world.Heat.VoidHeatLoss = 0.1f;
            world.Heat.SetHeat(0, 2, 100f); // 顶行（y = Height-1）
            world.Heat.SetHeat(0, 1, 100f); // 内部行

            world.Step();

            Assert.AreEqual(90f, world.Heat.GetHeat(0, 2), 1e-4f, "顶行按温度散掉 0.1 * T");
            Assert.AreEqual(100f, world.Heat.GetHeat(0, 1), 1e-4f, "内部行不接触虚空，热量不变");
        }

        [Test]
        public void Heat_RadiativeLossCoolsEveryCell_NotJustTheTopRow()
        {
            ParticleWorld world = new ParticleWorld(1, 3, 1u, TestTypes.Table);
            world.Heat.Conductivity = 0f;   // 只看辐射这一条
            world.Heat.VoidHeatLoss = 0f;   // 关掉顶行虚空，把两个散热口分开
            world.Heat.RadiativeLoss = 0.05f;
            world.Heat.SetHeat(0, 2, 100f); // 顶行
            world.Heat.SetHeat(0, 1, 100f); // 内部行：虚空散热够不着，辐射必须够得着

            world.Step();

            Assert.AreEqual(95f, world.Heat.GetHeat(0, 1), 1e-4f, "内部行按 0.05 * T 辐射散热");
            Assert.AreEqual(95f, world.Heat.GetHeat(0, 2), 1e-4f, "顶行同样辐射（与虚空散热叠加而非替代）");
        }

        [Test]
        public void Heat_RadiativeLossCapsHeatedCellTemperature()
        {
            // 持续注入功率 P 的格子应收敛到 T = P / λ，而不是无限升温：
            // 这是「地面被阳光晒到约 100° 就停」的微观机制。
            ParticleWorld world = new ParticleWorld(1, 1, 1u, TestTypes.Table);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0.05f;

            for (int i = 0; i < 2000; i++)
            {
                // 先辐射、后注入：刚注入的热本 tick 不打折，平衡点才是精确的 P / λ。
                world.Heat.Step();
                world.Heat.AddHeat(0, 0, 5f);
            }

            Assert.AreEqual(100f, world.Heat.GetTemperature(0, 0), 1f, "稳态温度 ≈ 注入功率 / 辐射系数");
        }

        [Test]
        public void Heat_VoidHeatLossKeepsDrainingTheTopRowEveryTick()
        {
            ParticleWorld world = new ParticleWorld(1, 1, 1u, TestTypes.Table);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0.2f;
            world.Heat.SetHeat(0, 0, 100f);

            world.Step();
            Assert.AreEqual(80f, world.Heat.GetHeat(0, 0), 1e-4f);

            world.Step();
            Assert.AreEqual(64f, world.Heat.GetHeat(0, 0), 1e-4f, "是每 tick 持续流失，不是一次性");

            for (int i = 0; i < 40; i++)
            {
                world.Step();
            }

            Assert.Greater(world.Heat.GetHeat(0, 0), 0f, "是按比例衰减，不会变成负数");
        }

        [Test]
        public void Heat_VoidHeatLossZero_ClosesTheTopBoundary()
        {
            ParticleWorld world = new ParticleWorld(1, 3, 1u, TestTypes.Table);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.SetHeat(0, 2, 100f);

            for (int i = 0; i < 10; i++)
            {
                world.Step();
            }

            Assert.AreEqual(100f, world.Heat.GetHeat(0, 2), 1e-4f, "0 = 封闭系统，顶行不再流失");
        }

        [Test]
        public void Heat_VoidHeatLossIsClampedToTheStableRange()
        {
            ParticleWorld world = NewWorld();

            world.Heat.VoidHeatLoss = -5f;
            Assert.AreEqual(0f, world.Heat.VoidHeatLoss);

            world.Heat.VoidHeatLoss = 99f;
            Assert.AreEqual(HeatMap.MaxConductivity, world.Heat.VoidHeatLoss);
        }

        [Test]
        public void Heat_LightSettlesAtASteadyTemperatureInsteadOfHeatingForever()
        {
            // 1x1 世界：唯一那格既是顶行、又是每列最上面的固体。每 tick 先散掉 0.1 * T，再被光照注入 5。
            // 稳态：0.1 * T = 5 ⇒ T = 强度 / 散热系数 = 50。
            ParticleWorld world = new ParticleWorld(1, 1, 1u, TestTypes.Table);
            world.Set(0, 0, TestTypes.StaticId); // 热容 1
            world.Heat.VoidHeatLoss = 0.1f;
            world.Light.Enabled = true;
            world.Light.LitTicks = 100000;
            world.Light.DarkTicks = 0;
            world.Light.Intensity = 5f;

            for (int i = 0; i < 50; i++)
            {
                world.Step();
            }

            float early = world.Heat.GetTemperature(0, 0);

            for (int i = 0; i < 450; i++)
            {
                world.Step();
            }

            float settled = world.Heat.GetTemperature(0, 0);

            Assert.AreEqual(50f, settled, 0.01f, "稳态温度 = 光照强度 / 虚空散热系数");
            Assert.Less(early, settled, "单调爬升到稳态，而不是无止境增长");
            Assert.Less(settled, 51f, "有出口，热量不再只增不减");
        }

        [Test]
        public void HeatMap_RejectsBadDimensionsAndBuffers()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeatMap(0, 1, new Cell[0], TestTypes.Table));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeatMap(1, 0, new Cell[0], TestTypes.Table));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeatMap(-1, 1, new Cell[0], TestTypes.Table));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new HeatMap(ParticleWorld.MaxSide + 1, 1, new Cell[ParticleWorld.MaxSide + 1], TestTypes.Table));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new HeatMap(1, ParticleWorld.MaxSide + 1, new Cell[ParticleWorld.MaxSide + 1], TestTypes.Table));

            Assert.Throws<ArgumentException>(() => new HeatMap(2, 1, new Cell[1], TestTypes.Table), "cells 长度必须匹配");
            Assert.Throws<ArgumentException>(() => new HeatMap(1, 1, null, TestTypes.Table));
            Assert.Throws<ArgumentException>(() => new HeatMap(1, 1, new Cell[1], null));

            var largest = new HeatMap(ParticleWorld.MaxSide, 1, new Cell[ParticleWorld.MaxSide], TestTypes.Table);
            Assert.AreEqual(ParticleWorld.MaxSide, largest.CellCount);
        }

        [Test]
        public void HeatParticle_ReadsAndWritesHeatDuringStep()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f; // 排除传导干扰，只看粒子自身的读写
            world.Set(5, 5, TestTypes.HeatId);
            world.Heat.SetHeat(5, 5, 10f);
            TestHeatParticle.Delta = 2f;

            world.Step();

            Assert.AreEqual(12f, world.Heat.GetHeat(5, 5), "粒子的 OnUpdate 必须能改世界热图");
            Assert.AreEqual(10f, TestHeatParticle.LastObservedHeat, "粒子先读到写入前的值");

            world.Step();

            Assert.AreEqual(14f, world.Heat.GetHeat(5, 5));
            Assert.AreEqual(12f, TestHeatParticle.LastObservedHeat);
            Assert.AreEqual((int)TestTypes.HeatId, (int)world.GetCell(5, 5).typeId, "热源粒子不移动");
        }

        // ============================== 光照 ==============================

        [Test]
        public void Light_HeatsTheTopmostNonGasOfEachColumn()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f;
            world.Set(0, 0, TestTypes.StaticId);  // 第 0 列：最上面的非气体就是 (0,0)
            world.Set(1, 0, TestTypes.StaticId);  // 第 1 列：只有上面的 (1,3) 该被照到
            world.Set(1, 3, TestTypes.StaticId);
            world.Set(2, 0, TestTypes.StaticId);  // 第 2 列：液体挡光——水面 (2,1) 被加热，下面的固体照不到
            world.Set(2, 1, TestTypes.LiquidId);  // 贴着固体放：Step 先扫描后光照，悬空的液体会先下落
            // 关笼子：TestLiquid 会斜滑 + 横流（maxFlowSpread 4），两侧不围死会在扫描阶段跑出第 2 列。
            // (1,1) 不影响第 1 列（最上面的仍是 (1,3)）；第 3 列没有断言。
            world.Set(1, 1, TestTypes.StaticId);
            world.Set(3, 0, TestTypes.StaticId);
            world.Set(3, 1, TestTypes.StaticId);

            world.Light.Enabled = true;
            world.Light.LitTicks = 100;
            world.Light.DarkTicks = 100;
            world.Light.Intensity = 7f;

            world.Step();

            Assert.IsTrue(world.Light.IsLit);
            Assert.AreEqual(7f, world.Heat.GetHeat(0, 0), 1e-4f);
            Assert.AreEqual(7f, world.Heat.GetHeat(1, 3), 1e-4f, "取每列最上面的非气体");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(1, 0), 1e-4f, "下方的固体不会被照到");
            Assert.AreEqual(7f, world.Heat.GetHeat(2, 1), 1e-4f, "液体挡光：水面被加热（海洋表面蒸发的前提）");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(2, 0), 1e-4f, "水下的固体照不到");
        }

        [Test]
        public void Light_FollowsItsLitAndDarkDutyCycle()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f;
            world.Set(0, 0, TestTypes.StaticId);
            world.Light.Enabled = true;
            world.Light.LitTicks = 2;
            world.Light.DarkTicks = 2;
            world.Light.Intensity = 1f;

            world.Step(); // 第 1 刻：亮
            world.Step(); // 第 2 刻：亮
            Assert.AreEqual(2f, world.Heat.GetHeat(0, 0), 1e-4f, "亮相位每刻都在加热");

            world.Step(); // 第 3 刻：暗
            world.Step(); // 第 4 刻：暗
            Assert.AreEqual(2f, world.Heat.GetHeat(0, 0), 1e-4f, "暗相位不加热");

            world.Step(); // 第 5 刻：新周期，重新亮
            Assert.AreEqual(3f, world.Heat.GetHeat(0, 0), 1e-4f, "周期循环回亮相位");
            Assert.AreEqual(5, world.Light.Elapsed);
        }

        [Test]
        public void Light_DisabledOrZeroIntensity_InjectsNoHeat()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f;
            world.Set(0, 0, TestTypes.StaticId);
            world.Light.Intensity = 5f;
            world.Light.LitTicks = 10;
            world.Light.DarkTicks = 10;

            world.Light.Enabled = false;
            world.Step();
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(0, 0), 1e-4f, "总开关关闭");

            world.Light.Enabled = true;
            world.Light.Intensity = 0f;
            world.Step();
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(0, 0), 1e-4f, "强度 0");
        }

        [Test]
        public void Light_WhenDisabled_DoesNotAdvanceItsPhase()
        {
            ParticleWorld world = NewWorld();
            world.Heat.Conductivity = 0f;
            world.Set(0, 0, TestTypes.StaticId);
            world.Light.LitTicks = 1;
            world.Light.DarkTicks = 1;
            world.Light.Intensity = 1f;
            world.Light.Enabled = false;

            for (int i = 0; i < 4; i++)
            {
                world.Step();
            }

            Assert.AreEqual(0, world.Light.Elapsed, "关闭时不推进相位");
            Assert.AreEqual(HeatMap.AmbientHeat, world.Heat.GetHeat(0, 0), 1e-4f);

            world.Light.Enabled = true;
            world.Step();
            Assert.AreEqual(1f, world.Heat.GetHeat(0, 0), 1e-4f, "重新打开从亮相位开始");
        }

        [Test]
        public void CanDisplace_RejectsOutOfBoundsTarget()
        {
            ParticleWorld world = NewWorld();
            world.Set(0, 0, TestTypes.GrainId);

            // 越界目标必须为 false：GetCell 把越界当空气，若不显式拒绝会误判为「可以移动」，而 Swap 会静默失败。
            Assert.IsFalse(ParticleRules.CanDisplace(world, 0, 0, -1, 0));
            Assert.IsFalse(ParticleRules.CanDisplace(world, 0, 0, 0, -1));
            Assert.IsTrue(ParticleRules.CanDisplace(world, 0, 0, 1, 0));
        }

        [Test]
        public void TryDiagonalFall_ClearanceIsTheStackHeightThatMakesASlopeStick()
        {
            // 局面：可动粒子在 (2,2)，两条斜下 (1,1)/(3,1) 都空，但它们正下方 (1,0)/(3,0) 被静态格顶着。
            // 落点只有 1 格净空 —— 沙（clearance 1）会滑，土（clearance 2）视为稳定。
            ParticleWorld sandLike = NewWorld();
            sandLike.Set(2, 2, TestTypes.SolidFlowId);
            sandLike.Set(1, 0, TestTypes.StaticId);
            sandLike.Set(3, 0, TestTypes.StaticId);

            Assert.IsTrue(ParticleRules.TryDiagonalFall(sandLike, 2, 2, 1), "落点可进入就该滑（45° 安息角）");
            Assert.IsTrue(sandLike.IsAir(2, 2));

            ParticleWorld stoneLike = NewWorld();
            stoneLike.Set(2, 2, TestTypes.SolidFlowId);
            stoneLike.Set(1, 0, TestTypes.StaticId);
            stoneLike.Set(3, 0, TestTypes.StaticId);

            Assert.IsFalse(ParticleRules.TryDiagonalFall(stoneLike, 2, 2, 2), "落点下方被顶住时不该滑（更陡的安息角）");
            Assert.AreEqual((int)TestTypes.SolidFlowId, (int)stoneLike.GetCell(2, 2).typeId);

            // 清掉一侧的顶托：那一侧净空 2 格，石头也该滑下去。
            stoneLike.Clear(1, 0);

            Assert.IsTrue(ParticleRules.TryDiagonalFall(stoneLike, 2, 2, 2));
            Assert.AreEqual((int)TestTypes.SolidFlowId, (int)stoneLike.GetCell(1, 1).typeId);
            Assert.IsTrue(stoneLike.IsAir(2, 2));
        }

        [Test]
        public void SlideClearance_MapsFrictionToIntegerReposeSlope()
        {
            Assert.AreEqual(1, ParticleRules.SlideClearance(0f), "摩擦度 0 = 1:1（45°，像沙）");
            Assert.AreEqual(2, ParticleRules.SlideClearance(0.5f), "摩擦度 0.5 = 2:1（63°）");
            Assert.AreEqual(10, ParticleRules.SlideClearance(0.9f), "摩擦度 0.9 = 10:1（84°，像石头）");
            Assert.AreEqual(0, ParticleRules.SlideClearance(1f), "摩擦度 1 = 永不斜滑");
            Assert.AreEqual(0, ParticleRules.SlideClearance(2f), "越界摩擦度按「永不斜滑」处理，不静默退化成 45°");

            // 摩擦度越高，安息角越陡（净空非递减）——不会出现「摩擦更大反而更滑」的反转。
            int previous = 1;
            for (int step = 0; step < 10; step++)
            {
                int clearance = ParticleRules.SlideClearance(step / 10f);
                Assert.GreaterOrEqual(clearance, previous, $"摩擦度 {step / 10f} 的净空比更低的摩擦度还小");
                previous = clearance;
            }

            // 净空 0 / 负值是不斜滑的哨兵值，TryDiagonalFall 不得把它当成最松的 1 格。
            ParticleWorld direct = NewWorld();
            direct.Set(2, 2, TestTypes.SolidFlowId);
            Assert.IsFalse(ParticleRules.TryDiagonalFall(direct, 2, 2, 0));
            Assert.IsFalse(ParticleRules.TryDiagonalFall(direct, 2, 2, -3));
            Assert.AreEqual((int)TestTypes.SolidFlowId, (int)direct.GetCell(2, 2).typeId);
        }

        [Test]
        public void SlideClearance_StickinessOnlyAddsSlopeWhenClinging()
        {
            // 纯摩擦度：与单参重载一致，粘性不参与。
            Assert.AreEqual(1, ParticleRules.SlideClearance(0f, 0f, false));
            Assert.AreEqual(1, ParticleRules.SlideClearance(0f, 0.9f, false), "反侧无固体邻居时粘性不得生效");
            Assert.AreEqual(10, ParticleRules.SlideClearance(0.9f, 0.9f, false));

            // 粘住邻居：粘性把安息角斜率往上叠（1/(1-σ) - 1 档）。
            Assert.AreEqual(1, ParticleRules.SlideClearance(0f, 0f, true), "粘性 0 即使粘住也不改变斜率");
            Assert.AreEqual(2, ParticleRules.SlideClearance(0f, 0.5f, true), "沙 + 粘性 0.5 ≈ 2:1");
            Assert.AreEqual(3, ParticleRules.SlideClearance(0.5f, 0.5f, true), "摩擦 0.5 + 粘性 0.5 = 3:1");
            Assert.AreEqual(10, ParticleRules.SlideClearance(0f, 0.9f, true));

            // 粘性 1 = 粘住就永不斜滑；但没得粘时仍按摩擦度滑。
            Assert.AreEqual(0, ParticleRules.SlideClearance(0f, 1f, true));
            Assert.AreEqual(1, ParticleRules.SlideClearance(0f, 1f, false));

            // 摩擦度 1 与粘性无关：任何组合都静止。
            Assert.AreEqual(0, ParticleRules.SlideClearance(1f, 0f, true));
            Assert.AreEqual(0, ParticleRules.SlideClearance(1f, 0.5f, true));

            // 粘性越高越陡（单调），不会出现「更粘反而更松」。
            int previous = 0;
            for (int step = 0; step < 10; step++)
            {
                int clearance = ParticleRules.SlideClearance(0f, step / 10f, true);
                Assert.GreaterOrEqual(clearance, previous, $"粘性 {step / 10f} 的净空比更低的粘性还小");
                previous = clearance;
            }
        }

        [Test]
        public void Cohesion_ClingsToLateralNeighbour_ButAnIsolatedGrainStillSlides()
        {
            // 同一个柱顶局面（静态柱顶到 y=2，粒子坐 (2,3)，正下被挡，左右两条斜下都是 1 格落差），
            // 唯一变量是「同排左右有没有固体邻居」。粘性 1 的意义就是「旁边有粒子才粘得住」：
            // 有邻居 → 两个方向都粘住，永不滚落；没有 → 按摩擦度 0 的 1:1 滑落。
            ParticleWorld clinging = CohesionCradle(withSideNeighbours: true);
            ParticleWorld isolated = CohesionCradle(withSideNeighbours: false);

            for (int i = 0; i < 20; i++)
            {
                clinging.Step();
                isolated.Step();
            }

            Assert.AreEqual((int)TestTypes.CohesionId, (int)clinging.GetCell(2, 3).typeId, "同排两侧都有固体邻居，粘性 1 应粘住不滚");
            Assert.IsTrue(clinging.IsAir(1, 2), "粘住的粒子不得滑到 (1,2)");
            Assert.IsTrue(clinging.IsAir(3, 2), "粘住的粒子不得滑到 (3,2)");

            Assert.IsTrue(isolated.IsAir(2, 3), "孤立粒子同排无固体邻居，粘性不生效，应按摩擦度滑落");
            Assert.AreEqual(1, CountType(isolated, TestTypes.CohesionId), "滑落的粒子数量必须守恒");
            Assert.IsTrue(
                isolated.GetCell(1, 0).typeId == TestTypes.CohesionId ||
                isolated.GetCell(3, 0).typeId == TestTypes.CohesionId,
                "滑落的孤立粒子应停在柱脚旁的地面上");
        }

        /// <summary>粘性测试台：静态柱 (2,0..2) 顶着一颗粘性固体在 (2,3)；可选在 (1,3)/(3,3) 加同排固体邻居。</summary>
        private static ParticleWorld CohesionCradle(bool withSideNeighbours)
        {
            ParticleWorld world = NewWorld();
            world.Set(2, 0, TestTypes.StaticId);
            world.Set(2, 1, TestTypes.StaticId);
            world.Set(2, 2, TestTypes.StaticId);

            if (withSideNeighbours)
            {
                world.Set(1, 3, TestTypes.StaticId);
                world.Set(3, 3, TestTypes.StaticId);
            }

            world.Set(2, 3, TestTypes.CohesionId);
            return world;
        }

        [Test]
        public void FrictionOneSolid_NeverRollsOff_EvenWithAClearDrop()
        {
            // 摩擦度 1 的固体坐在静态柱顶，正下被挡；两条斜下连到地面的整列都空。
            // 任何 < 1 的摩擦度都会沿坡滚下去，唯独摩擦度 1 必须原地不动。
            ParticleWorld world = NewWorld();
            world.Set(2, 0, TestTypes.StaticId);
            world.Set(2, 1, TestTypes.StaticId);
            world.Set(2, 2, TestTypes.StaticId);
            world.Set(2, 3, TestTypes.FrictionOneId);

            for (int i = 0; i < 20; i++)
            {
                world.Step();
            }

            Assert.AreEqual((int)TestTypes.FrictionOneId, (int)world.GetCell(2, 3).typeId, "下方有固体时摩擦度 1 的固体不得滚落");
            Assert.IsTrue(world.IsAir(1, 2), "摩擦度 1 不得沿坡滑到 (1,2)");
            Assert.IsTrue(world.IsAir(3, 2), "摩擦度 1 不得沿坡滑到 (3,2)");

            // 摩擦只影响斜滑，不影响正下：下方是空气时照常直落。
            ParticleWorld falling = NewWorld();
            falling.Set(2, 3, TestTypes.FrictionOneId);
            falling.Step();

            Assert.AreEqual((int)TestTypes.FrictionOneId, (int)falling.GetCell(2, 2).typeId, "摩擦度 1 不应阻止直落");
        }

        // ============================== 写操作与钩子 ==============================

        [Test]
        public void Set_SamplesShade_FromTheTypesOwnPalette()
        {
            ParticleWorld world = NewWorld();
            for (int x = 0; x < W; x++)
            {
                world.Set(x, 8, TestTypes.GrainId);
            }

            var seen = new bool[TestGrainParticle.Palette.Length];
            for (int x = 0; x < W; x++)
            {
                int shade = world.GetCell(x, 8).shade;
                Assert.IsTrue(shade >= 0 && shade < TestGrainParticle.Palette.Length, "shade 越出调色板范围：" + shade);
                seen[shade] = true;
            }

            int distinct = 0;
            for (int i = 0; i < seen.Length; i++)
            {
                if (seen[i])
                {
                    distinct++;
                }
            }

            Assert.IsTrue(distinct > 1, W + " 次生成只出现 1 种 shade，说明并不是按调色板抽样");
            Assert.AreEqual(W, TestTypes.Grain.SpawnCount);
        }

        [Test]
        public void Set_BetweenSteps_CreatesAParticleThatMovesOnTheVeryNextStep()
        {
            ParticleWorld world = NewWorld();
            world.Step();
            world.Set(5, 8, TestTypes.GrainId);

            world.Step();

            Assert.AreEqual((int)TestTypes.GrainId, (int)world.GetCell(5, 7).typeId);
            Assert.IsTrue(world.IsAir(5, 8));
        }

        [Test]
        public void Step_WhenAnOnUpdateThrows_StillLeavesNewParticlesProcessableNextTick()
        {
            ParticleWorld world = NewWorld();
            world.Set(5, 8, TestTypes.ThrowerId);
            TestThrowerParticle.Armed = true;

            Assert.Throws<InvalidOperationException>(() => world.Step());

            TestThrowerParticle.Armed = false;
            world.Clear(5, 8);
            world.Set(1, 8, TestTypes.WalkerId);

            world.Step();

            Assert.AreEqual((int)TestTypes.WalkerId, (int)world.GetCell(2, 8).typeId, "异常后新建的粒子被误判为本 tick 已处理");
        }

        [Test]
        public void Set_SameTypeTwice_IsNoOp()
        {
            ParticleWorld world = NewWorld();
            world.Set(4, 4, TestTypes.GrainId);
            byte shade = world.GetCell(4, 4).shade;

            world.Set(4, 4, TestTypes.GrainId);

            Assert.AreEqual(1, TestTypes.Grain.SpawnCount);
            Assert.AreEqual(0, TestTypes.Grain.KillCount);
            Assert.AreEqual((int)shade, (int)world.GetCell(4, 4).shade);
        }

        [Test]
        public void Clear_RaisesOnKillWithAirAsKiller()
        {
            ParticleWorld world = NewWorld();
            world.Set(4, 4, TestTypes.GrainId);

            world.Clear(4, 4);

            Assert.AreEqual(1, TestTypes.Grain.KillCount);
            Assert.AreEqual((int)ParticleRegistry.AirTypeId, (int)TestTypes.Grain.LastKillerId);
            Assert.AreEqual((int)TestTypes.GrainId, (int)TestTypes.Grain.VictimIdSeenByKill);
            Assert.IsTrue(world.IsAir(4, 4));
        }

        [Test]
        public void Set_Overwrite_CallsOnKillWhileVictimIsStillInTheGrid()
        {
            ParticleWorld world = NewWorld();
            world.Set(4, 4, TestTypes.GrainId);

            world.Set(4, 4, TestTypes.LiquidId);

            Assert.AreEqual(1, TestTypes.Grain.KillCount);
            Assert.AreEqual((int)TestTypes.LiquidId, (int)TestTypes.Grain.LastKillerId);
            Assert.AreEqual((int)TestTypes.GrainId, (int)TestTypes.Grain.VictimIdSeenByKill);
            Assert.AreEqual((int)TestTypes.LiquidId, (int)world.GetCell(4, 4).typeId);
        }

        [Test]
        public void Set_UnregisteredTypeId_IsRejected()
        {
            ParticleWorld world = NewWorld();
            ushort badId = (ushort)(world.Types.Length + 10);

            LogAssert.Expect(LogType.Error, new Regex("未注册的 typeId"));

            Assert.IsFalse(world.Set(4, 4, badId));
            Assert.IsTrue(world.IsAir(4, 4));
        }

        [Test]
        public void Swap_MovesCellDataWithoutRaisingHooks()
        {
            ParticleWorld world = NewWorld();
            world.Set(4, 4, TestTypes.GrainId);
            byte shade = world.GetCell(4, 4).shade;
            int spawns = TestTypes.Grain.SpawnCount;

            Assert.IsTrue(world.Swap(4, 4, 9, 9));

            Assert.IsTrue(world.IsAir(4, 4));
            Assert.AreEqual((int)TestTypes.GrainId, (int)world.GetCell(9, 9).typeId);
            Assert.AreEqual((int)shade, (int)world.GetCell(9, 9).shade);
            Assert.AreEqual(spawns, TestTypes.Grain.SpawnCount);
            Assert.AreEqual(0, TestTypes.Grain.KillCount);
        }

        // ============================== 扫描语义 ==============================

        [Test]
        public void Swap_BlocksReprocessingWithinTheSameTick()
        {
            ParticleWorld world = NewWorld();
            world.Set(0, 8, TestTypes.WalkerId);

            world.Step();

            Assert.AreEqual((int)TestTypes.WalkerId, (int)world.GetCell(1, 8).typeId);
            Assert.IsTrue(world.IsAir(0, 8));
            Assert.AreEqual(1, TestTypes.Walker.UpdateCount);
        }

        [Test]
        public void GravityFallsExactlyOneCellPerTick()
        {
            ParticleWorld world = NewWorld();
            world.Set(5, 12, TestTypes.GrainId);

            for (int tick = 1; tick <= 6; tick++)
            {
                world.Step();

                Assert.AreEqual((int)TestTypes.GrainId, (int)world.GetCell(5, 12 - tick).typeId);
                Assert.IsTrue(world.IsAir(5, 12 - tick + 1));
                Assert.AreEqual(tick, world.Tick);
            }
        }

        [Test]
        public void StaticType_NeverReceivesOnUpdate()
        {
            ParticleWorld world = NewWorld();
            world.Set(5, 12, TestTypes.StaticId);

            for (int i = 0; i < 10; i++)
            {
                world.Step();
            }

            Assert.AreEqual(0, TestTypes.Static.UpdateCount);
            Assert.AreEqual((int)TestTypes.StaticId, (int)world.GetCell(5, 12).typeId);
            Assert.AreEqual(10, world.Tick);
            Assert.IsFalse(world.Parity);
        }

        [Test]
        public void FlowHorizontal_SolidDoesNotPassThroughSameKindSolid()
        {
            ParticleWorld world = new ParticleWorld(4, 3, 1u, TestTypes.Table);
            world.Set(0, 1, TestTypes.SolidFlowId);
            world.Set(1, 1, TestTypes.SolidFlowId);

            // (2,1) 是空气，但路径上的同类固体挡住了：固体不得从同伴身上「穿」过去。
            Assert.IsFalse(ParticleRules.TryFlowHorizontal(world, 0, 1, 4));
            Assert.AreEqual((int)TestTypes.SolidFlowId, (int)world.GetCell(0, 1).typeId);
            Assert.AreEqual((int)TestTypes.SolidFlowId, (int)world.GetCell(1, 1).typeId);
        }

        [Test]
        public void FlowHorizontal_LiquidPassesThroughSameKindLiquidTowardsAir()
        {
            ParticleWorld world = new ParticleWorld(4, 2, 1u, TestTypes.Table);
            world.Set(0, 1, TestTypes.LiquidId);
            world.Set(1, 1, TestTypes.LiquidId);

            Assert.IsTrue(ParticleRules.TryFlowHorizontal(world, 0, 1, 4));

            Assert.IsTrue(world.IsAir(0, 1));
            Assert.AreEqual((int)TestTypes.LiquidId, (int)world.GetCell(1, 1).typeId);
            Assert.AreEqual((int)TestTypes.LiquidId, (int)world.GetCell(2, 1).typeId);
        }

        // ============================== 确定性与注册表 ==============================

        [Test]
        public void SameSeedAndSameCommands_ProduceIdenticalGrids()
        {
            ParticleWorld a = new ParticleWorld(24, 24, 777u, TestTypes.Table);
            ParticleWorld b = new ParticleWorld(24, 24, 777u, TestTypes.Table);

            for (int y = 12; y < 20; y++)
            {
                for (int x = 0; x < 24; x++)
                {
                    a.Set(x, y, TestTypes.LiquidId);
                    b.Set(x, y, TestTypes.LiquidId);
                }
            }

            for (int i = 0; i < 200; i++)
            {
                a.Step();
                b.Step();
            }

            for (int i = 0; i < a.CellCount; i++)
            {
                Assert.AreEqual((int)a.Cells[i].typeId, (int)b.Cells[i].typeId, "typeId 在下标 " + i + " 处不一致");
                Assert.AreEqual((int)a.Cells[i].shade, (int)b.Cells[i].shade, "shade 在下标 " + i + " 处不一致");
                Assert.AreEqual((int)a.Cells[i].flags, (int)b.Cells[i].flags, "flags 在下标 " + i + " 处不一致");
            }
        }

        [Test]
        public void Registry_RejectsInvalidPaletteWithoutChangingCount()
        {
            int before = ParticleRegistry.Count;

            Assert.Throws<InvalidOperationException>(() => ParticleRegistry.Register(new TestBadPaletteParticle()));

            Assert.AreEqual(before, ParticleRegistry.Count);
        }

        [Test]
        public void Registry_RejectsFrictionOutsideUnitRange()
        {
            int before = ParticleRegistry.Count;

            Assert.Throws<InvalidOperationException>(() => ParticleRegistry.Register(new TestBadFrictionParticle()));

            Assert.AreEqual(before, ParticleRegistry.Count);
        }

        [Test]
        public void Registry_RejectsStickinessOutsideUnitRange()
        {
            int before = ParticleRegistry.Count;

            Assert.Throws<InvalidOperationException>(() => ParticleRegistry.Register(new TestBadStickinessParticle()));

            Assert.AreEqual(before, ParticleRegistry.Count);
        }

        [Test]
        public void Registry_RejectsTypeWithoutCategoryTag()
        {
            int before = ParticleRegistry.Count;

            Assert.Throws<InvalidOperationException>(() => ParticleRegistry.Register(new TestNoTagParticle()));

            Assert.AreEqual(before, ParticleRegistry.Count);
        }

        [Test]
        public void RegisterBatch_WithOneInvalidType_RegistersNothing()
        {
            int before = ParticleRegistry.Count;

            Assert.Throws<InvalidOperationException>(() =>
                ParticleRegistry.RegisterBatch(new TestWalkerParticle(), new TestBadPaletteParticle()));

            Assert.AreEqual(before, ParticleRegistry.Count, "批量注册失败后不得留下半注册表");
        }

        [Test]
        public void CustomTypeTable_WithInvalidEntry_ThrowsAtConstruction()
        {
            var badInfo = new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, false, false, 0, 0f, 0f, new Color32[0], "BadTableEntry");
            var bad = new ParticleType(1, badInfo, null, null, null);

            Assert.Throws<ArgumentException>(() => new ParticleWorld(2, 2, 1u, new ParticleType[] { null, bad }));
        }

        // ============================== 像素映射 ==============================

        [Test]
        public void PixelBuffer_MapsShadeToPaletteAndAirToBackground()
        {
            ParticleWorld world = new ParticleWorld(4, 2, 1u, TestTypes.Table);
            world.Set(1, 0, TestTypes.GrainId);
            world.Set(2, 0, TestTypes.LiquidId);

            var background = new Color32(9, 8, 7, 255);
            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background);

            Assert.AreEqual(background, pixels[world.Index(0, 0)]);
            Assert.AreEqual(background, pixels[world.Index(3, 1)]);
            Assert.AreEqual(TestGrainParticle.Palette[world.GetCell(1, 0).shade], pixels[world.Index(1, 0)]);
            Assert.AreEqual(TestLiquidParticle.Palette[world.GetCell(2, 0).shade], pixels[world.Index(2, 0)]);
        }

        [Test]
        public void PixelBuffer_FallsBackToFirstPaletteColorWhenShadeIsOutOfRange()
        {
            ParticleWorld world = new ParticleWorld(2, 1, 1u, TestTypes.Table);
            world.Set(0, 0, TestTypes.LiquidId);
            world.At(0, 0).shade = 250;

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, default(Color32));

            Assert.AreEqual(TestLiquidParticle.Palette[0], pixels[0]);
        }

        [Test]
        public void PixelBuffer_RejectsMismatchedBufferLength()
        {
            ParticleWorld world = new ParticleWorld(4, 2, 1u, TestTypes.Table);
            var pixels = new Color32[world.CellCount - 1];

            Assert.Throws<ArgumentException>(() => ParticlePixelBuffer.Fill(world, pixels, default(Color32)));
        }

        [Test]
        public void PixelBuffer_HeatOverlay_TintsHotterCellsRedder()
        {
            ParticleWorld world = new ParticleWorld(4, 1, 1u, TestTypes.Table);
            world.Set(0, 0, TestTypes.GrainId);
            world.Set(1, 0, TestTypes.GrainId);
            world.Set(2, 0, TestTypes.GrainId);

            var background = new Color32(10, 20, 30, 255);
            var plain = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, plain, background);

            world.Heat.DebugHeatOverlay = true;
            world.Heat.DebugFullScaleTemperature = 10f;
            world.Heat.SetHeat(1, 0, 10f); // 颗粒热容 1 ⇒ T = 10 ⇒ 满量程：纯红
            world.Heat.SetHeat(2, 0, 5f);  // T = 5 ⇒ 半程

            var tinted = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, tinted, background);

            Assert.AreEqual(plain[0], tinted[0], "没有热量的格不受叠加影响");
            Assert.AreEqual(255, (int)tinted[1].r);
            Assert.AreEqual(0, (int)tinted[1].g);
            Assert.AreEqual(0, (int)tinted[1].b);
            Assert.Greater((int)tinted[2].r, (int)plain[2].r, "半程：红分量被抬高");
            Assert.Less((int)tinted[2].g, (int)plain[2].g, "半程：绿分量被压低");
            Assert.Greater((int)tinted[1].r, (int)tinted[2].r, "越热越红");
            Assert.Less((int)tinted[1].g, (int)tinted[2].g);
        }

        [Test]
        public void PixelBuffer_HeatOverlay_AlsoTintsAirCells()
        {
            ParticleWorld world = new ParticleWorld(2, 1, 1u, TestTypes.Table);
            var background = new Color32(10, 20, 30, 255);
            world.Heat.DebugHeatOverlay = true;
            world.Heat.DebugFullScaleTemperature = 1f;
            world.Heat.SetHeat(1, 0, 1f); // 空气热容 1 ⇒ T = 1 ⇒ 满量程

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background);

            Assert.AreEqual(background, pixels[0]);
            Assert.AreEqual(255, (int)pixels[1].r, "热力叠加是整场的，空气格也显示");
            Assert.AreEqual(0, (int)pixels[1].g);
        }

        [Test]
        public void PixelBuffer_HeatOverlay_IsIgnoredWhenFullScaleIsNonPositive()
        {
            ParticleWorld world = new ParticleWorld(1, 1, 1u, TestTypes.Table);
            var background = new Color32(10, 20, 30, 255);
            world.Heat.DebugHeatOverlay = true;
            world.Heat.DebugFullScaleTemperature = 0f;
            world.Heat.SetHeat(0, 0, 100f);

            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background);

            Assert.AreEqual(background, pixels[0], "满量程 <= 0 时叠加失效（也不会除零）");
        }

        // ============================== 生产粒子：石头 / 沙 / 土 ==============================

        /// <summary>
        /// 摩擦度这一层的安息角契约（把粘性关掉，σ=0）：沙 1:1、土 2:1、石 10:1。
        /// 同几何、同种子、同 100 颗逐颗投放、同石头地板，唯一变量是摩擦度。
        /// 生产石头的粘性很高，堆得比 10:1 更陡，那是粘性的账——见 <see cref="Stone_HighStickinessPilesMuchSteeperThanDryStone"/>。
        /// </summary>
        [Test]
        public void DrySolid_ReposeSlopeFollowsFriction()
        {
            const int width = 41;
            const int height = 40;
            const int grains = 100;

            ParticleWorld sand = DropTunablePile(width, height, grains, 0f, 0f);
            ParticleWorld soil = DropTunablePile(width, height, grains, 0.5f, 0f);
            ParticleWorld stone = DropTunablePile(width, height, grains, 0.9f, 0f);

            Assert.AreEqual(grains, CountType(sand, TunableId), "沙粒数量必须守恒");
            Assert.AreEqual(grains, CountType(soil, TunableId), "土粒数量必须守恒");
            Assert.AreEqual(grains, CountType(stone, TunableId), "石粒数量必须守恒");

            // 安息角斜率 = 堆高 / 半径（= 2·堆高/底宽）：沙 1（1:1，45°）、土 2（2:1，63°）、石 10（10:1，84°）。
            float sandSlope = Slope(sand, TunableId);
            float soilSlope = Slope(soil, TunableId);
            float stoneSlope = Slope(stone, TunableId);

            Assert.That(sandSlope, Is.InRange(0.7f, 1.2f), $"沙堆斜率应接近 1（1:1），实际 {sandSlope:F2}");
            Assert.That(soilSlope, Is.InRange(1.6f, 2.4f), $"土堆斜率应接近 2（2:1），实际 {soilSlope:F2}");
            Assert.That(stoneSlope, Is.InRange(8.0f, 12.0f), $"石堆斜率应接近 10（10:1），实际 {stoneSlope:F2}");
            Assert.Greater(stoneSlope, soilSlope * 4f, "石堆必须明显比土堆（2:1）陡");
            Assert.Greater(stoneSlope, sandSlope * 7f, "石堆必须明显比沙堆（1:1）陡");

            // 堆顶必须留在投放行以下：否则投放的颗粒会直接盖在堆顶（Set 同类型是空操作）而被吞掉。
            Assert.Less(PileHeight(stone, TunableId), height - 1, "石堆顶触到投放行了，世界太矮");

            // 已收敛：再跑 100 tick 逐格不变（这么陡的坡面也不会永远侧滑）。
            var snapshot = (Cell[])stone.Cells.Clone();
            for (int i = 0; i < 100; i++)
            {
                stone.Step();
            }

            for (int i = 0; i < stone.CellCount; i++)
            {
                Assert.AreEqual((int)snapshot[i].typeId, (int)stone.Cells[i].typeId, "下标 " + i + " 的石堆仍在移动");
            }
        }

        /// <summary>
        /// 石头的高粘性（0.9）：靠着同排的石头时斜落净空从 10 抬到 19，于是石头垛站得比
        /// 「同摩擦度、但无粘性」的干石对照陡得多，而且仍然收敛、颗粒守恒。
        /// 同几何、同种子、同 50 颗逐颗投放，唯一变量是粘性。
        /// </summary>
        [Test]
        public void Stone_HighStickinessPilesMuchSteeperThanDryStone()
        {
            const int width = 41;
            const int height = 80;
            const int grains = 50;

            ParticleWorld dry = DropTunablePile(width, height, grains, 0.9f, 0f, ticksPerGrain: 80, settleTicks: 600);
            ParticleWorld sticky = DropTunablePile(width, height, grains, 0.9f, 0.9f, ticksPerGrain: 80, settleTicks: 600);

            Assert.AreEqual(grains, CountType(dry, TunableId), "干石对照的颗粒数量必须守恒");
            Assert.AreEqual(grains, CountType(sticky, TunableId), "高粘性石头的颗粒数量必须守恒");

            float drySlope = Slope(dry, TunableId);
            float stickySlope = Slope(sticky, TunableId);

            Assert.That(drySlope, Is.InRange(8.0f, 12.0f), $"无粘性对照应接近 10:1，实际 {drySlope:F2}");
            Assert.Greater(stickySlope, drySlope * 1.3f, $"高粘性必须让石头垛明显更陡，实际 {stickySlope:F2} vs {drySlope:F2}");

            // 堆顶必须留在投放行以下（高粘性会把垛拉得很高，世界得够高）。
            Assert.Less(PileHeight(sticky, TunableId), height - 1, "高粘性石堆顶触到投放行了，世界太矮");

            // 已收敛。
            var snapshot = (Cell[])sticky.Cells.Clone();
            for (int i = 0; i < 100; i++)
            {
                sticky.Step();
            }

            for (int i = 0; i < sticky.CellCount; i++)
            {
                Assert.AreEqual((int)snapshot[i].typeId, (int)sticky.Cells[i].typeId, "下标 " + i + " 的高粘性石堆仍在移动");
            }
        }

        /// <summary>
        /// 粘性的用户可见效果：旁边有粒子的堆体因此更陡。
        /// 同尺寸、同种子、同投放节奏，唯一变量是粘性（摩擦度都为 0）：
        /// 0 → 1:1（45°）、0.5 → 2:1、0.9 → 10:1。
        /// </summary>
        [Test]
        public void Stickiness_RaisesTheReposeSlopeOfAPile()
        {
            // 本测试跑 ~27 s 的真实模拟，期间 Unity-MCP 插件掉线会打一条 Error 日志，
            // 测试框架会把它当成「未处理的失败日志」而误判本测试失败（断言其实全过）。
            // 该日志与模拟无关，因此在测试窗口内忽略失败日志；[TearDown] 会恢复默认。
            // 彻底消除应修 MCP 令牌（见 AGENTS.md 的 MCP 一节）。
            LogAssert.ignoreFailingMessages = true;

            const int width = 81;
            const int height = 90;
            const int grains = 600;

            ParticleWorld loose = DropTunablePile(width, height, grains, 0f, 0f);
            ParticleWorld sticky = DropTunablePile(width, height, grains, 0f, 0.5f);
            ParticleWorld verySticky = DropTunablePile(width, height, grains, 0f, 0.9f);

            // 地板是石头（id 1），堆料是可调固体（TunableId）：守恒只看堆料。
            Assert.AreEqual(grains, CountType(loose, TunableId), "颗粒数量必须守恒");
            Assert.AreEqual(grains, CountType(sticky, TunableId), "颗粒数量必须守恒");
            Assert.AreEqual(grains, CountType(verySticky, TunableId), "颗粒数量必须守恒");

            float looseSlope = Slope(loose, TunableId);
            float stickySlope = Slope(sticky, TunableId);
            float veryStickySlope = Slope(verySticky, TunableId);

            Assert.That(looseSlope, Is.InRange(0.7f, 1.2f), $"无粘性堆应接近 1:1（45°），实际 {looseSlope:F2}");
            Assert.That(stickySlope, Is.InRange(1.6f, 2.4f), $"粘性 0.5 的堆应接近 2:1，实际 {stickySlope:F2}");
            Assert.That(veryStickySlope, Is.InRange(8.0f, 12.0f), $"粘性 0.9 的堆应接近 10:1，实际 {veryStickySlope:F2}");

            Assert.Greater(stickySlope, looseSlope * 1.5f, "粘性必须明显抬高安息角");
            Assert.Greater(veryStickySlope, stickySlope * 3f, "粘性越高堆体越陡");

            // 堆顶必须留在投放行以下，否则投放的颗粒会直接盖在堆顶（Set 同类型是空操作）而被吞掉。
            Assert.Less(PileHeight(verySticky, TunableId), height - 1, "堆顶触到投放行了，世界太矮");

            // 已收敛：再跑 100 tick 逐格不变（粘性不会让堆体永远抖动）。
            var snapshot = (Cell[])verySticky.Cells.Clone();
            for (int i = 0; i < 100; i++)
            {
                verySticky.Step();
            }

            for (int i = 0; i < verySticky.CellCount; i++)
            {
                Assert.AreEqual((int)snapshot[i].typeId, (int)verySticky.Cells[i].typeId, "下标 " + i + " 的粘性堆仍在移动");
            }
        }

        /// <summary>可调摩擦度 / 粘性的固体类型，由 <see cref="TestTables.Build"/> 分配为 id 2（id 1 是地板石头）。</summary>
        private const ushort TunableId = 2;

        /// <summary>
        /// 与 <see cref="DropPile"/> 同几何、同节奏、同种子、同石头地板，只把堆料换成可调参数的固体，
        /// 从而把「材料参数」隔离成唯一变量。
        /// </summary>
        private static ParticleWorld DropTunablePile(
            int width, int height, int grains, float friction, float stickiness,
            int ticksPerGrain = 40, int settleTicks = 400)
        {
            ParticleType[] table = TestTables.Build(
                new StoneParticle(),
                new TestTunableSolidParticle(friction, stickiness));
            var world = new ParticleWorld(width, height, 20261002u, table);

            for (int x = 0; x < width; x++)
            {
                world.Set(x, 0, 1);
            }

            int centerX = width / 2;
            for (int grain = 0; grain < grains; grain++)
            {
                world.Set(centerX, height - 1, TunableId);
                for (int tick = 0; tick < ticksPerGrain; tick++)
                {
                    world.Step();
                }
            }

            for (int i = 0; i < settleTicks; i++)
            {
                world.Step();
            }

            return world;
        }

        [Test]
        public void Solid_StopsExactlyOnTopOfAnotherSolid()
        {
            const int width = 5;
            const int height = 6;
            var world = new ParticleWorld(width, height, 1u, ProductionTypes.Table);

            for (int x = 0; x < width; x++)
            {
                world.Set(x, 0, ProductionTypes.StoneId);
            }

            world.Set(2, 5, ProductionTypes.StoneId);

            for (int i = 0; i < 30; i++)
            {
                world.Step();
            }

            // 同类固体互不置换：落石停在已有固体正上方，不会再往下挤。
            Assert.AreEqual((int)ProductionTypes.StoneId, (int)world.GetCell(2, 1).typeId);
            Assert.IsTrue(world.IsAir(2, 2));
            Assert.AreEqual(width + 1, CountType(world, ProductionTypes.StoneId));
        }

        [Test]
        public void Sand_FormsAStablePileAndConservesGrainCount()
        {
            const int width = 40;
            const int height = 24;
            var world = new ParticleWorld(width, height, 20261002u, ProductionTypes.Table);

            for (int x = 0; x < width; x++)
            {
                world.Set(x, 0, ProductionTypes.StoneId);
            }

            int placed = 0;
            for (int y = 14; y < 22; y++)
            {
                for (int x = 16; x < 26; x++)
                {
                    world.Set(x, y, ProductionTypes.SandId);
                    placed++;
                }
            }

            for (int i = 0; i < 400; i++)
            {
                world.Step();
            }

            Assert.AreEqual(placed, CountType(world, ProductionTypes.SandId), "沙粒数量必须守恒");

            // 不存在悬空沙粒：每一粒下方都必须是非空气。
            for (int y = 1; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (world.GetCell(x, y).typeId == ProductionTypes.SandId)
                    {
                        Assert.AreNotEqual(
                            (int)ParticleRegistry.AirTypeId,
                            (int)world.GetCell(x, y - 1).typeId,
                            $"({x},{y}) 的沙粒悬空");
                    }
                }
            }

            // 已收敛：再跑 100 tick 逐格不变（堆体不是永远在抖）。
            var snapshot = (Cell[])world.Cells.Clone();
            for (int i = 0; i < 100; i++)
            {
                world.Step();
            }

            for (int i = 0; i < world.CellCount; i++)
            {
                Assert.AreEqual((int)snapshot[i].typeId, (int)world.Cells[i].typeId, "下标 " + i + " 的沙堆仍在移动");
            }
        }

        /// <summary>
        /// 土堆比沙堆陡，而且比**同摩擦度、但没粘性**的堆还要陡——那条差额就是粘性。
        /// 同尺寸、同种子、同 120 颗逐颗投放、同石头地板，唯一变量是材料参数。
        /// </summary>
        [Test]
        public void SoilPile_StickinessRaisesItsReposeSlopeAboveItsFriction()
        {
            const int width = 41;
            const int height = 40;
            const int grains = 120;

            ParticleWorld sand = DropPile(width, height, grains, ProductionTypes.SandId);
            ParticleWorld soil = DropPile(width, height, grains, ProductionTypes.SoilId);
            ParticleWorld frictionOnly = DropTunablePile(width, height, grains, 0.5f, 0f);

            Assert.AreEqual(grains, CountType(sand, ProductionTypes.SandId), "沙粒数量必须守恒");
            Assert.AreEqual(grains, CountType(soil, ProductionTypes.SoilId), "土粒数量必须守恒");

            // 安息角斜率 = 堆高 / 半底宽：沙 1:1（45°）；摩擦度 0.5 单独给 2:1；粘性 0.5 再抬到约 3:1。
            float sandSlope = Slope(sand, ProductionTypes.SandId);
            float soilSlope = Slope(soil, ProductionTypes.SoilId);
            float frictionOnlySlope = Slope(frictionOnly, TunableId);

            Assert.That(sandSlope, Is.InRange(0.7f, 1.2f), $"沙堆斜率应接近 1，实际 {sandSlope:F2}");
            Assert.That(frictionOnlySlope, Is.InRange(1.6f, 2.4f), $"同摩擦度无粘性的堆应接近 2:1，实际 {frictionOnlySlope:F2}");
            Assert.That(soilSlope, Is.InRange(2.5f, 3.2f), $"土堆斜率应接近 3（2:1 的摩擦 + 粘性），实际 {soilSlope:F2}");

            Assert.Greater(soilSlope, frictionOnlySlope * 1.25f, "粘性必须把土堆抬得明显比同摩擦度更陡");
            Assert.Greater(soilSlope, sandSlope * 2.2f, "土堆必须明显比沙堆陡");

            // 已收敛：再跑 100 tick 逐格不变。
            var snapshot = (Cell[])soil.Cells.Clone();
            for (int i = 0; i < 100; i++)
            {
                soil.Step();
            }

            for (int i = 0; i < soil.CellCount; i++)
            {
                Assert.AreEqual((int)snapshot[i].typeId, (int)soil.Cells[i].typeId, "下标 " + i + " 的土堆仍在移动");
            }
        }

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

        private static float SumHeat(ParticleWorld world)
        {
            float sum = 0f;
            float[] values = world.Heat.Values;
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return sum;
        }

        private static int RowCount(ParticleWorld world, ushort typeId, int y)
        {
            int count = 0;
            for (int x = 0; x < world.Width; x++)
            {
                if (world.GetCell(x, y).typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>在石头地板上逐颗投放同一种颗粒，跑到收敛；返回结果世界。</summary>
        private static ParticleWorld DropPile(int width, int height, int grains, ushort typeId)
        {
            var world = new ParticleWorld(width, height, 20261002u, ProductionTypes.Table);
            for (int x = 0; x < width; x++)
            {
                // y=0 的石头无法侧滑（斜落要再往下、越界），等价于一块地面。
                world.Set(x, 0, ProductionTypes.StoneId);
            }

            int centerX = width / 2;
            for (int grain = 0; grain < grains; grain++)
            {
                world.Set(centerX, height - 1, typeId);
                for (int tick = 0; tick < 40; tick++)
                {
                    world.Step();
                }
            }

            for (int i = 0; i < 400; i++)
            {
                world.Step();
            }

            return world;
        }

        private static int PileHeight(ParticleWorld world, ushort typeId)
        {
            for (int y = world.Height - 1; y > 0; y--)
            {
                if (RowCount(world, typeId, y) > 0)
                {
                    return y;
                }
            }

            return 0;
        }

        /// <summary>安息角斜率：堆高 / 半底宽（即堆高 / 半径）。1 = 45°，2 = 2 格高 : 1 格宽，10 ≈ 84°。</summary>
        private static float Slope(ParticleWorld world, ushort typeId)
        {
            int baseWidth = RowCount(world, typeId, 1);
            return baseWidth == 0 ? 0f : 2f * PileHeight(world, typeId) / baseWidth;
        }
    }
}
