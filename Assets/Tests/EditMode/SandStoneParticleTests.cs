using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 石头 / 沙子 / 土壤的**生产注册路径**与沙堆形状测试。
    /// </summary>
    /// <remarks>
    /// 与 <c>ParticleWorldTests</c> 的石头/沙/土测试互补，不重复：
    /// <list type="bullet">
    /// <item>那边用 <c>ProductionTypes.Table</c> 的隔离类型表验证世界机制（斜落净空判据、石头堆比沙堆更尖、同类互阻、沙与土的守恒收敛、土堆斜率约为沙的两倍）；</item>
    /// <item>这里走<b>全局注册表</b>（<see cref="ParticleRegistration.RegisterAll"/> + <see cref="ParticleIds"/>），
    /// 验证生产注册确实回填了 id 且标签正确——这是隔离表覆盖不到的路径；</item>
    /// <item>并补上那边没有的<b>安息角剖面不变量</b>（下大上小、逐行单调收窄）。</item>
    /// </list>
    /// </remarks>
    public sealed class SandStoneParticleTests
    {
        // 注册在 SetUp 里完成，故用属性而非 const。
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

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static void FillFloor(ParticleWorld world, ushort typeId)
        {
            for (int x = 0; x < world.Width; x++)
            {
                world.Set(x, 0, typeId);
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

        /// <summary>
        /// 生产注册路径：<see cref="ParticleRegistration.RegisterAll"/> 必须回填出三个不同的非 0 id，
        /// 且石头/沙/土都带 Solid 标签、都不是 <c>IsStatic</c>（石头曾是静态类型，那会让它悬空不动）。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsSolidTaggedNonStaticIds()
        {
            Assert.AreNotEqual(0, (int)Stone, "Stone 必须被回填（非 0）");
            Assert.AreNotEqual(0, (int)Sand, "Sand 必须被回填（非 0）");
            Assert.AreNotEqual(0, (int)Soil, "Soil 必须被回填（非 0）");
            Assert.AreNotEqual((int)Stone, (int)Sand, "石头与沙必须拿到不同 id");
            Assert.AreNotEqual((int)Sand, (int)Soil, "沙与土必须拿到不同 id");
            Assert.AreNotEqual((int)Stone, (int)Soil, "石头与土必须拿到不同 id");

            ParticleTypeInfo stone = ParticleRegistry.Types[Stone].Info;
            ParticleTypeInfo sand = ParticleRegistry.Types[Sand].Info;
            ParticleTypeInfo soil = ParticleRegistry.Types[Soil].Info;

            Assert.IsTrue((stone.Tags & ParticleTags.Solid) != 0, "石头应带 Solid 标签");
            Assert.IsTrue((sand.Tags & ParticleTags.Solid) != 0, "沙应带 Solid 标签");
            Assert.IsTrue((soil.Tags & ParticleTags.Solid) != 0, "土应带 Solid 标签");
            Assert.IsTrue(stone.AffectedByGravity, "石头应受重力");
            Assert.IsFalse(stone.IsStatic, "石头必须参与扫描：IsStatic 会让它悬空不动");
            Assert.IsTrue(sand.AffectedByGravity, "沙应受重力");
            Assert.IsFalse(sand.IsStatic, "沙必须参与扫描");
            Assert.IsTrue(soil.AffectedByGravity, "土应受重力");
            Assert.IsFalse(soil.IsStatic, "土必须参与扫描");
        }

        /// <summary>
        /// 安息角剖面不变量：沙逐颗落下后必须收敛成下大上小的三角堆——
        /// 每一行都不比它下面一行宽，且堆顶严格窄于堆底（平摊成一层会让两者相等）。
        /// 断言只取每行的粒数，不依赖左右随机顺序的逐格结果。
        /// </summary>
        [Test]
        public void Sand_PileIsTriangular_WiderAtTheBase()
        {
            const int grains = 40;
            var world = new ParticleWorld(31, 24, 2026u);
            FillFloor(world, Stone);

            int centerX = world.Width / 2;
            for (int k = 0; k < grains; k++)
            {
                world.Set(centerX, world.Height - 1, Sand);
                for (int i = 0; i < 40; i++)
                {
                    world.Step();
                }
            }

            for (int i = 0; i < 200; i++)
            {
                world.Step();
            }

            Assert.AreEqual(grains, CountType(world, Sand), "沙粒总数应守恒");

            int previous = int.MaxValue;
            int rows = 0;
            int baseRow = RowCount(world, Sand, 1);
            for (int y = 1; y < world.Height; y++)
            {
                int row = RowCount(world, Sand, y);
                if (row == 0)
                {
                    break;
                }

                Assert.LessOrEqual(row, previous, $"y={y} 这一行比它下面一行还宽：沙堆不是下大上小");
                previous = row;
                rows++;
            }

            Assert.GreaterOrEqual(rows, 2, "沙堆应不止一行高");
            Assert.Less(previous, baseRow, "堆顶必须严格窄于堆底：否则沙子只是平摊，没有形成三角堆");
        }
    }
}
