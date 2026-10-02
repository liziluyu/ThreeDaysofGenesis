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
            var badInfo = new ParticleTypeInfo(ParticleTags.Solid, 1f, false, false, 0, new Color32[0], "BadTableEntry");
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

        // ============================== 生产粒子：石头与沙 ==============================

        [Test]
        public void Stone_StacksIntoAVerticalColumn_WithoutLateralDrift()
        {
            const int width = 9;
            const int height = 12;
            var world = new ParticleWorld(width, height, 1u, ProductionTypes.Table);

            for (int x = 0; x < width; x++)
            {
                world.Set(x, 0, ProductionTypes.StoneId);
            }

            for (int y = 6; y < 10; y++)
            {
                world.Set(4, y, ProductionTypes.StoneId);
            }

            for (int i = 0; i < 60; i++)
            {
                world.Step();
            }

            // 石头只竖直下落：4 块全部垒在 x=4 的柱子上，一格都没侧移。
            for (int y = 1; y <= 4; y++)
            {
                Assert.AreEqual((int)ProductionTypes.StoneId, (int)world.GetCell(4, y).typeId, "柱子 y=" + y + " 处缺失");
            }

            for (int y = 5; y < height; y++)
            {
                Assert.IsTrue(world.IsAir(4, y), "柱子顶部以上 y=" + y + " 不该有粒子");
            }

            Assert.AreEqual(width + 4, CountType(world, ProductionTypes.StoneId));
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
    }
}
