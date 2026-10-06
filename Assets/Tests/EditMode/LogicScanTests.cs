using System;
using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 逻辑帧混合扫描（<see cref="ParticleWorld.StepLogic"/> 在 <see cref="ParticleWorld.MotionSleepEnabled"/>
    /// 开启时走 <c>ScanLogicSleeping</c>）与 <see cref="ParticleTypeInfo.HasLogic"/> 能力标志的测试：
    /// 透明性、**扫描中热注入的同帧可见性**、HasLogic 契约、以及「OnLogic 永不休眠」的回归。
    /// </summary>
    /// <remarks>
    /// <para>透明性走生产注册表（<see cref="ParticleRegistration.RegisterAll"/>）：需要水 / 火 / 草 / 湿土等真实类型的
    /// 随机行为（蒸发摇骰、熄灭档位骰、随机刻），才能覆盖「OnLogic 全量同序评估 ⇒ RNG 流不变」这一等价性依据。</para>
    /// <para>对比口径与 <c>MotionSleepTests</c> 相同：<see cref="CellFlags.ProcessedParityMask"/>（bit0）是「本帧已处理」的
    /// 瞬时标记，被休眠跳过的格保留旧奇偶，on/off 两个世界在 bit0 上本来就可能不同——这不是语义差异。</para>
    /// </remarks>
    public sealed class LogicScanTests
    {
        private const byte FlagsMask = CellFlags.ProcessedParityMask;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>
        /// 逻辑帧混合扫描透明性：同一确定性活跃场景（石头底 + 土层 + 草皮 + 一池水 + 湿土，光照开、随机刻 48、
        /// 热力学参数取生产资产值）分别以休眠开 / 关跑 300 个「3 运动帧 + 1 逻辑帧」周期，最终逐格
        /// （typeId / shade / 非 bit0 的 flags）与热场、湿度场必须完全一致。
        /// </summary>
        [Test]
        public void LogicScan_IsTransparentToAFullScan()
        {
            ParticleWorld sleeping = BuildActiveScene();
            ParticleWorld full = BuildActiveScene();
            full.MotionSleepEnabled = false;

            const int Cycles = 300;
            for (int t = 0; t < Cycles; t++)
            {
                sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepLogic();
                full.StepMotion(); full.StepMotion(); full.StepMotion(); full.StepLogic();
            }

            AssertWorldsEqual(full, sleeping, $"周期 {Cycles} 后");
        }

        /// <summary>
        /// 逻辑帧混合扫描的热可见性回归：**扫描进行中**的 <c>OnLogic</c> 热注入必须对**同帧稍后扫描**的格可见。
        /// </summary>
        /// <remarks>
        /// <para>场景复刻生产环境（320×180、全网格大气、陆地沙/土/草皮 + 海洋 + 水生植物、光照 600/600
        /// 强度 5、随机刻 48、生产热力学参数），预热 540 周期后在 y = 28（草皮上方那排大气）点一排火
        /// （x = 40..119），随后 300 周期**每周期**逐位比较。</para>
        /// <para>为什么必须这个形态：火每逻辑帧向本格注热 250Q，越过大气浮力的温度死区。全网格旧路径里，
        /// 扫描序在火之后的格当帧就按新温度决定浮力；混合扫描若把这次热变化留到帧末
        /// <c>FlushTemperatureDirty</c>（下一帧才唤醒），就会晚一帧决策。此前的活跃场景没有「点火后
        /// 被同帧注热、且立刻需要浮力决策」的大气格，因此没暴露这个洞——本测试是对它的定点回归
        /// （未接入扫描中脏格回调时，首个点火周期即发散）。</para>
        /// <para>每周期都比较（而非只在末尾比一次）：发散可能在火熄灭后被模拟自身抹平，只比末态会漏检。</para>
        /// </remarks>
        [Test]
        public void LogicScan_HeatInjectedDuringTheScanIsVisibleToLaterCells()
        {
            ParticleWorld sleeping = BuildFireScene();
            ParticleWorld full = BuildFireScene();
            full.MotionSleepEnabled = false;

            const int Warmup = 540;
            const int AfterIgnition = 300;
            for (int cycle = 0; cycle < Warmup + AfterIgnition; cycle++)
            {
                if (cycle >= Warmup)
                {
                    IgniteFireRow(sleeping);
                    IgniteFireRow(full);
                }

                sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepLogic();
                full.StepMotion(); full.StepMotion(); full.StepMotion(); full.StepLogic();

                if (cycle >= Warmup)
                {
                    // 逐周期比状态哈希（与 AssertWorldsEqual 覆盖同一批字段，无分配——300 周期 × 5.7 万格
                    // 若逐格带插值消息断言，光消息字符串就能拖垮测试）；哈希不等才落到逐格断言定位。
                    if (StateHash(sleeping) != StateHash(full))
                    {
                        AssertWorldsEqual(full, sleeping, $"点火后第 {cycle - Warmup + 1} 周期");
                    }
                }
            }

            AssertWorldsEqual(full, sleeping, $"点火后 {AfterIgnition} 周期");
        }

        /// <summary>
        /// <see cref="ParticleTypeInfo.HasLogic"/> 契约：隔离类型表里，<c>hasLogic: false</c> 的类型即便 <c>OnLogic</c>
        /// 有副作用也**永不被调用**（不写世界）；默认 <c>true</c> 的类型 <c>OnLogic</c> 照常每逻辑帧被调。
        /// 休眠开时第二帧走「只跑 OnLogic」分支，因此两次 <see cref="ParticleWorld.StepLogic"/> 覆盖
        /// 完整 <c>ProcessCell</c> 与混合扫描两条路径；休眠关时覆盖全网格旧路径。
        /// </summary>
        [Test]
        public void HasLogic_GatesTheLogicHookInBothScanPaths()
        {
            AssertLogicContract(sleepEnabled: true);
            AssertLogicContract(sleepEnabled: false);
        }

        /// <summary>
        /// 回归：休眠开启时温水蒸发仍发生——<c>OnLogic</c> 从不休眠。沸点之上的水在若干逻辑帧后必须
        /// 相变成大气并把水汽放进湿度场，证明逻辑帧混合扫描没有把 <c>OnLogic</c> 一起跳过。
        /// 取沸点之上的水（而非依赖 1/300 慢蒸发摇骰）以保证断言确定。
        /// </summary>
        [Test]
        public void LogicScan_WarmWaterStillEvaporatesWhileSleeping()
        {
            const int W = 8, H = 8;
            ParticleWorld world = new ParticleWorld(W, H, 2024u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Light.Enabled = false;
            Assert.IsTrue(world.MotionSleepEnabled, "前提：休眠默认开启");

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                }
            }

            // 水热容 6.5：T = Q / C = 85 ≥ BoilingPoint(80)，必定蒸发。
            world.Set(4, 4, ParticleIds.Water);
            world.Heat.SetHeat(4, 4, 6.5f * 85f);

            for (int t = 0; t < 3; t++)
            {
                world.StepLogic();
            }

            float total = 0f;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    total += world.Humidity.Get(x, y);
                }
            }

            Assert.Greater(total, 0f, "休眠开时温水仍应蒸发（OnLogic 未被休眠）");
            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(4, 4).typeId, "沸水应已相变成大气");
        }

        /// <summary>休眠开 / 关两种扫描路径下的 HasLogic 契约断言。</summary>
        private static void AssertLogicContract(bool sleepEnabled)
        {
            var silent = new SilentLogicProbe();   // hasLogic: false
            var writing = new WritingLogicProbe(); // 默认 hasLogic: true
            ParticleType[] types = TestTables.Build(silent, writing);

            ParticleWorld world = new ParticleWorld(6, 6, 777u, types);
            world.MotionSleepEnabled = sleepEnabled;
            world.Light.Enabled = false;
            world.Set(1, 1, 1); // SilentLogicProbe
            world.Set(4, 4, 2); // WritingLogicProbe

            world.StepLogic(); // 初态候选整张置位：两格都走完整 ProcessCell
            world.StepLogic(); // 无写入 ⇒ 候选为空：writing 格走「只跑 OnLogic」分支

            Assert.AreEqual(2, writing.LogicCount, $"sleep={sleepEnabled}: 默认 true 的 OnLogic 每逻辑帧都应被调");
            Assert.AreEqual(0, silent.LogicCount, $"sleep={sleepEnabled}: hasLogic=false 的 OnLogic 永不应被调");
            Assert.AreEqual(7, world.GetCell(4, 4).shade, $"sleep={sleepEnabled}: 默认 true 的 OnLogic 应已写世界");
            Assert.AreEqual(0, world.GetCell(1, 1).shade, $"sleep={sleepEnabled}: hasLogic=false 的格不应被写");
        }

        /// <summary>活跃场景：石头底 / 侧壁 + 沙层 + 土层（含湿土）+ 草皮 + 一池水 + 草种子；光照开、随机刻 48。</summary>
        private static ParticleWorld BuildActiveScene()
        {
            const int W = 40, H = 28;
            ParticleWorld world = new ParticleWorld(W, H, 987654321u);
            world.Heat.Conductivity = 0.24f;
            world.Heat.VoidHeatLoss = 0.12f;
            world.Heat.RadiativeLoss = 0.05f;
            world.RandomTicksPerStep = 48;
            world.Light.Enabled = true;

            ushort stone = ParticleIds.Stone, sand = ParticleIds.Sand, soil = ParticleIds.Soil;
            ushort wet = ParticleIds.WetSoil, water = ParticleIds.Water;
            ushort grass = ParticleIds.Grass, seed = ParticleIds.GrassSeed;

            for (int x = 0; x < W; x++)
            {
                world.Set(x, 0, stone);
                world.Set(x, 1, stone);
            }

            for (int y = 0; y < H; y++)
            {
                world.Set(0, y, stone);
                world.Set(W - 1, y, stone);
            }

            for (int x = 1; x < 15; x++)
            {
                for (int y = 2; y < 8; y++)
                {
                    world.Set(x, y, sand);
                }

                for (int y = 8; y < 11; y++)
                {
                    world.Set(x, y, soil);
                }

                world.Set(x, 11, grass);
            }

            for (int x = 3; x < 8; x++)
            {
                world.Set(x, 10, wet);
            }

            world.Set(5, 12, seed);
            world.Set(9, 12, seed);

            for (int x = 16; x < W - 1; x++)
            {
                for (int y = 2; y < 6; y++)
                {
                    world.Set(x, y, sand);
                }

                for (int y = 6; y < 18; y++)
                {
                    world.Set(x, y, water);
                }
            }

            for (int x = 17; x < 24; x++)
            {
                world.Set(x, 1, wet);
            }

            return world;
        }

        /// <summary>
        /// 火 / 大气可见性场景：320×180 生产网格，全网格大气 + 石头底 + 陆地（沙 / 土 / 草皮 y = 3..27）
        /// + 海洋（水 + 水生植物），光照 600/600 强度 5、随机刻 48、生产热力学参数。
        /// </summary>
        private static ParticleWorld BuildFireScene()
        {
            const int W = 320, H = 180;
            ParticleWorld world = new ParticleWorld(W, H, 123456789u);
            ApplyProductionParams(world);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                }
            }

            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < 3; y++)
                {
                    world.Set(x, y, ParticleIds.Stone);
                }
            }

            for (int x = 4; x < W / 2; x++)
            {
                for (int y = 3; y < 20; y++)
                {
                    world.Set(x, y, ParticleIds.Sand);
                }

                for (int y = 20; y < 26; y++)
                {
                    world.Set(x, y, ParticleIds.Soil);
                }

                for (int y = 26; y < 28; y++)
                {
                    world.Set(x, y, ParticleIds.Grass);
                }
            }

            for (int x = W / 2 + 8; x < W - 4; x++)
            {
                for (int y = 3; y < 36; y++)
                {
                    world.Set(x, y, ParticleIds.Water);
                }
            }

            for (int x = W / 2 + 12; x < W - 8; x += 7)
            {
                for (int y = 3; y < 10; y++)
                {
                    world.Set(x, y, ParticleIds.AquaticPlant);
                }
            }

            world.Light.Enabled = true;
            world.Light.LitTicks = 600;
            world.Light.DarkTicks = 600;
            world.Light.Intensity = 5f;
            world.Light.CloudExtinction = 0.05f;
            return world;
        }

        /// <summary>在 y = 28（草皮上方那排大气）点一排火，x = 40..119——压着陆地草皮、上方是大气。</summary>
        private static void IgniteFireRow(ParticleWorld world)
        {
            for (int x = 40; x < 120; x++)
            {
                world.Set(x, 28, ParticleIds.Fire);
            }
        }

        /// <summary>生产资产口径的热力学参数（<c>ParticleSimulationSettings.asset</c> 现值）。</summary>
        private static void ApplyProductionParams(ParticleWorld world)
        {
            world.RandomTicksPerStep = 48;
            world.Heat.Conductivity = 0.24f;
            world.Heat.VoidHeatLoss = 0.12f;
            world.Heat.RadiativeLoss = 0.05f;
            world.Heat.AdiabaticLapse = 0.1f;
            world.Heat.AirThermalConductivity = 1f;
            world.Heat.AirThermalEmissivity = 0.1f;
        }

        /// <summary>
        /// 世界状态哈希（FNV-1a）：覆盖与 <see cref="AssertWorldsEqual"/> 同一批字段（typeId / shade /
        /// 非 bit0 的 flags、热量位模式、湿度位模式），**无分配**，用于逐周期廉价比对。
        /// </summary>
        private static ulong StateHash(ParticleWorld world)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                Cell[] cells = world.Cells;
                for (int i = 0; i < cells.Length; i++)
                {
                    h = (h ^ cells[i].typeId) * 1099511628211UL;
                    h = (h ^ cells[i].shade) * 1099511628211UL;
                    h = (h ^ (ulong)(cells[i].flags & FlagsMask)) * 1099511628211UL;
                }

                float[] heat = world.Heat.Values;
                for (int i = 0; i < heat.Length; i++)
                {
                    h = (h ^ (ulong)BitConverter.SingleToInt32Bits(heat[i])) * 1099511628211UL;
                }

                for (int y = 0; y < world.Height; y++)
                {
                    for (int x = 0; x < world.Width; x++)
                    {
                        h = (h ^ (ulong)BitConverter.SingleToInt32Bits(world.Humidity.Get(x, y))) * 1099511628211UL;
                    }
                }

                return h;
            }
        }

        /// <summary>
        /// 逐位比较两个世界：Cells 口径同 <c>MotionSleepTests</c>（typeId / shade / 非 bit0 的 flags——
        /// bit0 是「本帧已处理」的瞬时标记，休眠跳过的格保留旧奇偶，本来就允许不同）+ 热场 + 湿度场。
        /// </summary>
        private static void AssertWorldsEqual(ParticleWorld expected, ParticleWorld actual, string label)
        {
            Assert.AreEqual(expected.Tick, actual.Tick, $"{label}：时基必须一致");
            Cell[] a = expected.Cells;
            Cell[] b = actual.Cells;
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].typeId, b[i].typeId, $"{label}：格子 {i} 的 typeId 不一致");
                Assert.AreEqual(a[i].shade, b[i].shade, $"{label}：格子 {i} 的 shade 不一致");
                Assert.AreEqual((byte)(a[i].flags & FlagsMask), (byte)(b[i].flags & FlagsMask), $"{label}：格子 {i} 的 flags 不一致");
            }

            float[] ha = expected.Heat.Values;
            float[] hb = actual.Heat.Values;
            for (int i = 0; i < ha.Length; i++)
            {
                Assert.AreEqual(ha[i], hb[i], 0f, $"{label}：格子 {i} 的热量不一致");
            }

            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                {
                    Assert.AreEqual(expected.Humidity.Get(x, y), actual.Humidity.Get(x, y), 0f, $"{label}：({x},{y}) 的湿度不一致");
                }
            }
        }

        /// <summary>隔离类型表的探针：<c>hasLogic: false</c>，但 <c>OnLogic</c> 有可见副作用（写 shade）。</summary>
        private sealed class SilentLogicProbe : IParticleType
        {
            private static readonly Color32[] Palette = { new Color32(1, 2, 3, 255) };

            internal int LogicCount;

            public ParticleTypeInfo Info
            {
                get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "SilentLogicProbe", hasLogic: false); }
            }

            public void OnMotion(ParticleWorld world, int x, int y)
            {
            }

            public void OnLogic(ParticleWorld world, int x, int y)
            {
                LogicCount++;
                world.At(x, y).shade = 7;
            }

            public void OnRandomTick(ParticleWorld world, int x, int y)
            {
            }

            public void OnSpawn(ParticleWorld world, int x, int y)
            {
            }

            public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
            {
            }
        }

        /// <summary>隔离类型表的探针：默认 <c>hasLogic: true</c>，<c>OnLogic</c> 有可见副作用（写 shade）。</summary>
        private sealed class WritingLogicProbe : IParticleType
        {
            private static readonly Color32[] Palette = { new Color32(9, 8, 7, 255) };

            internal int LogicCount;

            public ParticleTypeInfo Info
            {
                get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "WritingLogicProbe"); }
            }

            public void OnMotion(ParticleWorld world, int x, int y)
            {
            }

            public void OnLogic(ParticleWorld world, int x, int y)
            {
                LogicCount++;
                world.At(x, y).shade = 7;
            }

            public void OnRandomTick(ParticleWorld world, int x, int y)
            {
            }

            public void OnSpawn(ParticleWorld world, int x, int y)
            {
            }

            public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
            {
            }
        }
    }
}
