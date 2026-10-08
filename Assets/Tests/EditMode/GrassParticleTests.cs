using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 草的寿命系统：每逻辑帧衰老（年龄 = <see cref="Cell.shade"/> × <c>TicksPerShadeStep</c> + flags bit3-5），
    /// 每 <c>TicksPerShadeStep</c> 帧颜色向黄绿推进一档，年龄到 <see cref="GrassParticle.LifespanTicks"/>
    /// 寿终（走 <c>Clear</c> → 世界的有机衰变）。衰老不消耗世界随机流、不移动、不读邻居。
    /// </summary>
    /// <remarks>
    /// 走生产注册路径（跨类型的寿终残留是 <see cref="ParticleIds.Humus"/>，需要 id 回填）。
    /// 世界随机刻默认 0 ⇒ 压死 / 失去扎根 / 温度致死不会触发，本类隔离出的唯一变量是**衰老**。
    /// </remarks>
    public sealed class GrassParticleTests
    {
        private const uint SeedValue = 20261007u;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>8×8 世界里在 (4,4) 种一格草（悬空——本类不触发随机刻，扎根与否与本主题无关）。</summary>
        private static ParticleWorld BuildSingleGrass()
        {
            var world = new ParticleWorld(8, 8, SeedValue);
            world.Set(4, 4, ParticleIds.Grass);
            Assert.AreEqual(ParticleIds.Grass, world.GetCell(4, 4).typeId);
            return world;
        }

        [Test]
        public void Grass_StartsAtTheYoungestShade()
        {
            ParticleWorld world = BuildSingleGrass();
            Assert.AreEqual(0, world.GetCell(4, 4).shade, "新生草应从最年轻的一档（shade = 0）开始");
        }

        [Test]
        public void Grass_ShadeAdvancesOneStepPerTicksPerShadeStep()
        {
            ParticleWorld world = BuildSingleGrass();

            for (int i = 0; i < GrassParticle.TicksPerShadeStep - 1; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(0, world.GetCell(4, 4).shade, "不满一档时颜色档不动");

            world.StepLogic();
            Assert.AreEqual(1, world.GetCell(4, 4).shade, "满一档时颜色档 +1");
        }

        [Test]
        public void Grass_DiesOnTheLifespanTick()
        {
            ParticleWorld world = BuildSingleGrass();

            for (int i = 0; i < GrassParticle.LifespanTicks - 1; i++)
            {
                world.StepLogic();
            }

            Cell before = world.GetCell(4, 4);
            Assert.AreEqual(ParticleIds.Grass, before.typeId, "第 1199 逻辑帧仍在寿命内");
            Assert.AreEqual(GrassParticle.ShadeCount - 1, before.shade, "寿终前一帧应已到最老颜色档");

            world.StepLogic();

            ushort after = world.GetCell(4, 4).typeId;
            Assert.AreNotEqual(ParticleIds.Grass, after, "第 1200 逻辑帧应寿终");
            Assert.IsTrue(
                after == ParticleRegistry.AirTypeId || after == ParticleIds.Humus,
                $"寿终只允许空气或腐殖质（20% 有机衰变），实际 typeId={after}");
        }

        [Test]
        public void Grass_AgingDoesNotConsumeTheRandomStream()
        {
            ParticleWorld aged = BuildSingleGrass();
            ParticleWorld reference = BuildSingleGrass(); // 同布局、同种子，但不推进。
            for (int i = 0; i < 600; i++)
            {
                aged.StepLogic();
            }

            Assert.AreEqual(
                reference.NextUInt(), aged.NextUInt(),
                "衰老（及其所在逻辑帧的场 pass）不得消耗世界随机流");
        }

        [Test]
        public void Grass_RendersOlderAsYellowGreen()
        {
            Color32[] palette = ParticleRegistry.Types[ParticleIds.Grass].Info.Palette;
            Assert.AreEqual(GrassParticle.ShadeCount, palette.Length, "颜色档数应等于调色板长度");

            for (int i = 1; i < palette.Length; i++)
            {
                Assert.GreaterOrEqual(palette[i].r, palette[i - 1].r, $"调色板红分量应随年龄非递减（档 {i}）");
            }

            Assert.Greater(
                palette[palette.Length - 1].r - palette[palette.Length - 1].g,
                palette[0].r - palette[0].g,
                "最老档应比最年轻档更偏黄（红相对绿更高）");

            ParticleWorld young = BuildSingleGrass();
            ParticleWorld old = BuildSingleGrass();
            for (int i = 0; i < 600; i++)
            {
                old.StepLogic();
            }

            Color32 youngPixel = Render(young, 4, 4);
            Color32 oldPixel = Render(old, 4, 4);
            Assert.AreEqual(palette[young.GetCell(4, 4).shade], youngPixel, "渲染应按 shade 查调色板");
            Assert.AreEqual(palette[old.GetCell(4, 4).shade], oldPixel);
            Assert.Greater(
                oldPixel.r - oldPixel.g, youngPixel.r - youngPixel.g,
                "老草的像素应更偏黄绿（红相对绿更高）");
        }

        /// <summary>把世界映射成像素缓冲并读回 (x,y) 的单格颜色（纯 CPU，不需要 GPU）。</summary>
        private static Color32 Render(ParticleWorld world, int x, int y)
        {
            var pixels = new Color32[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, default(Color32));
            return pixels[world.Index(x, y)];
        }
    }
}
