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
            TestTypes.EnsureRegistered();
            TestTypes.ResetCounters();
        }

        private static ParticleWorld NewWorld(uint seed = 12345u)
        {
            return new ParticleWorld(W, H, seed);
        }

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
        public void Set_SamplesShadeFromPalette_AndRaisesOnSpawn()
        {
            ParticleWorld world = NewWorld();
            world.Set(4, 4, TestTypes.GrainId);

            Cell cell = world.GetCell(4, 4);
            Assert.AreEqual((int)TestTypes.GrainId, (int)cell.typeId);
            Assert.AreEqual(1, TestTypes.Grain.SpawnCount);
            Assert.IsTrue(cell.shade < TestGrainParticle.Palette.Length);
        }

        [Test]
        public void Set_MarksCellAsUnprocessedForTheNextTick()
        {
            ParticleWorld world = NewWorld();
            world.Set(4, 4, TestTypes.GrainId);

            int expectedStamp = (world.Tick & 1) ^ 1;
            Assert.AreEqual(expectedStamp, LowBit(world.GetCell(4, 4)));

            world.Step();
            world.Set(4, 4, TestTypes.LiquidId);
            expectedStamp = (world.Tick & 1) ^ 1;
            Assert.AreEqual(expectedStamp, LowBit(world.GetCell(4, 4)));
        }

        private static int LowBit(Cell cell)
        {
            return cell.flags & CellFlags.ProcessedParity;
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
            ushort badId = (ushort)(ParticleRegistry.Types.Length + 10);

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
        public void SameSeedAndSameCommands_ProduceIdenticalGrids()
        {
            ParticleWorld a = new ParticleWorld(24, 24, 777u);
            ParticleWorld b = new ParticleWorld(24, 24, 777u);

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
        public void PixelBuffer_MapsShadeToPaletteAndAirToBackground()
        {
            ParticleWorld world = new ParticleWorld(4, 2, 1u);
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
        public void PixelBuffer_RejectsMismatchedBufferLength()
        {
            ParticleWorld world = new ParticleWorld(4, 2, 1u);
            var pixels = new Color32[world.CellCount - 1];

            Assert.Throws<ArgumentException>(() => ParticlePixelBuffer.Fill(world, pixels, default(Color32)));
        }
    }
}
