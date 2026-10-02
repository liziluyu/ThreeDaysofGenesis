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
            var badInfo = new ParticleTypeInfo(ParticleTags.Solid, 1f, false, false, 0, 0f, 0f, new Color32[0], "BadTableEntry");
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

        // ============================== 生产粒子：石头 / 沙 / 土 ==============================

        [Test]
        public void Stone_PileIsTenTimesAsSteepAsSandPile()
        {
            const int width = 41;
            const int height = 40;
            const int grains = 100;

            ParticleWorld sand = DropPile(width, height, grains, ProductionTypes.SandId);
            ParticleWorld soil = DropPile(width, height, grains, ProductionTypes.SoilId);
            ParticleWorld stone = DropPile(width, height, grains, ProductionTypes.StoneId);

            Assert.AreEqual(grains, CountType(sand, ProductionTypes.SandId), "沙粒数量必须守恒");
            Assert.AreEqual(grains, CountType(soil, ProductionTypes.SoilId), "土粒数量必须守恒");
            Assert.AreEqual(grains + width, CountType(stone, ProductionTypes.StoneId), "石块总数必须守恒（石头地板 + 投放的石块）");

            // 安息角斜率 = 堆高 / 半径（= 2·堆高/底宽）：沙 ~1（1:1，45°），土 ~3（摩擦 + 粘性），石头 ~10（10:1，84°）。
            float sandSlope = Slope(sand, ProductionTypes.SandId);
            float soilSlope = Slope(soil, ProductionTypes.SoilId);
            float stoneSlope = Slope(stone, ProductionTypes.StoneId);

            Assert.That(stoneSlope, Is.InRange(8.0f, 12.0f), $"石头堆斜率应接近 10（10:1），实际 {stoneSlope:F2}");
            Assert.Greater(stoneSlope, soilSlope * 2.5f, "石头堆必须明显比土堆（约 3:1）陡");
            Assert.Greater(stoneSlope, sandSlope * 7f, "石头堆必须明显比沙堆（1:1）陡");

            // 堆顶必须留在投放行以下：否则投放的石块会直接盖在堆顶（Set 同类型是空操作）而被吞掉，
            // 上面的守恒与斜率断言都会失去意义。
            Assert.Less(PileHeight(stone, ProductionTypes.StoneId), height - 1, "石头堆顶触到投放行了，世界太矮");

            // 已收敛：再跑 100 tick 逐格不变（这么陡的坡面也不会永远侧滑）。
            var snapshot = (Cell[])stone.Cells.Clone();
            for (int i = 0; i < 100; i++)
            {
                stone.Step();
            }

            for (int i = 0; i < stone.CellCount; i++)
            {
                Assert.AreEqual((int)snapshot[i].typeId, (int)stone.Cells[i].typeId, "下标 " + i + " 的石头堆仍在移动");
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
            int width, int height, int grains, float friction, float stickiness)
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
