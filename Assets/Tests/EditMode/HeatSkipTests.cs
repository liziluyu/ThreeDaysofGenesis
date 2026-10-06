using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 热传导**不动点跳过**（<see cref="HeatMap.Step"/> 入口）的测试：场已到不动点且所有输入未变时整趟跳过，
    /// 一旦有热注入 / 写入 / 参数变化就必须恢复执行；跳过路径对结果必须**逐位透明**。
    /// </summary>
    /// <remarks>
    /// 隔离热输运的小世界：稳定石地基 + 上方空气，只调 <see cref="ParticleWorld.StepLogic"/>（不跑运动帧），
    /// 因此除测试显式注入外没有任何写入，热场一开始就停在环境值（<see cref="HeatMap.AmbientHeat"/>）——
    /// 是「上一趟恒等 + 输入未变 ⇒ 可跳过」最干净的用例。跳过开关 <see cref="HeatMap.FixpointSkipEnabled"/>
    /// 用于与「每帧重算」做 A/B 对照。
    /// </remarks>
    public sealed class HeatSkipTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>静止且热场已达不动点的世界：8×6，底 3 行稳定石地基，无光、无随机刻、无散热（封闭系统）。</summary>
        private static ParticleWorld BuildStillWorld()
        {
            const int W = 8, H = 6;
            ParticleWorld world = new ParticleWorld(W, H, 20261006u);
            world.RandomTicksPerStep = 0;
            world.Light.Enabled = false;
            world.Heat.Conductivity = 0.24f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Heat.AdiabaticLapse = 0f;
            world.Heat.AirThermalConductivity = 1f;
            world.Heat.AirThermalEmissivity = 1f;

            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < 3; y++)
                {
                    world.Set(x, y, ParticleIds.Stone);
                }
            }

            return world;
        }

        /// <summary>推进 <see cref="ParticleWorld.StepLogic"/> 直到热 pass 跳过（或达到上限），返回用掉的帧数。</summary>
        private static int StepUntilSkipped(ParticleWorld world, int maxFrames)
        {
            int frames = 0;
            while (frames < maxFrames && !world.Heat.LastStepSkipped)
            {
                world.StepLogic();
                frames++;
            }

            return frames;
        }

        /// <summary>
        /// 静止世界：首帧必须真跑（不能被默认指纹误判成「已收敛」），随后热 pass 应整趟跳过并保持。
        /// </summary>
        [Test]
        public void HeatSkip_StillWorldSkipsAfterFirstRun()
        {
            ParticleWorld world = BuildStillWorld();

            world.StepLogic();
            Assert.IsFalse(world.Heat.LastStepSkipped, "构造后首帧必须真跑热 pass");

            world.StepLogic();
            Assert.IsTrue(world.Heat.LastStepSkipped, "静止且已达不动点：热 pass 应整趟跳过");

            for (int i = 0; i < 5; i++)
            {
                world.StepLogic();
                Assert.IsTrue(world.Heat.LastStepSkipped, "持续静止应持续跳过");
            }
        }

        /// <summary>
        /// 注入热量后跳过必须失效：注热会改动 Q，热 pass 必须重新执行（否则这份热会被漏掉、并逐帧累积）；
        /// 热场重新收敛后应再次进入跳过。
        /// </summary>
        [Test]
        public void HeatSkip_ResumesAfterHeatInjectionThenSkipsAgain()
        {
            ParticleWorld world = BuildStillWorld();
            world.StepLogic();
            Assert.IsTrue(StepUntilSkipped(world, 1) <= 1 && world.Heat.LastStepSkipped, "先到达跳过");

            world.Heat.AddHeat(4, 4, 50f);
            world.StepLogic();
            Assert.IsFalse(world.Heat.LastStepSkipped, "注入热量后必须重新执行热 pass（不能把刚注入的热漏掉）");

            int frames = StepUntilSkipped(world, 20000);
            Assert.IsTrue(world.Heat.LastStepSkipped, $"热场再次收敛后应重新跳过（用了 {frames} 帧）");
        }

        /// <summary>
        /// 写入格子（改类型）后跳过必须失效：逐格热容 / σ / ε 与天空暴露判据都可能变，热 pass 必须重跑；
        /// 收敛后应再次进入跳过。
        /// </summary>
        [Test]
        public void HeatSkip_ResumesAfterCellWriteThenSkipsAgain()
        {
            ParticleWorld world = BuildStillWorld();
            world.StepLogic();
            Assert.IsTrue(StepUntilSkipped(world, 1) <= 1 && world.Heat.LastStepSkipped, "先到达跳过");

            // 在地基内部换一格类型：Q 不变，但该格热容变 ⇒ 热 pass 的输入变了，必须重跑。
            world.Set(4, 1, ParticleIds.Sand);
            world.StepLogic();
            Assert.IsFalse(world.Heat.LastStepSkipped, "改格子类型后必须重跑热 pass");

            int frames = StepUntilSkipped(world, 20000);
            Assert.IsTrue(world.Heat.LastStepSkipped, $"收敛后应重新跳过（用了 {frames} 帧）");
        }

        /// <summary>
        /// 光照与跳过的交互：亮帧在**帧末**注入的热必须由下一帧热 pass 传导——那一帧绝不能跳过；
        /// 暗相里热场摊平收敛后，光照系统在场也不妨碍进入跳过。
        /// </summary>
        [Test]
        public void HeatSkip_LitPulseIsConductedThenSkipsInDark()
        {
            ParticleWorld world = BuildStillWorld();
            world.Light.Enabled = true;
            world.Light.LitTicks = 1;
            world.Light.DarkTicks = 1000000; // 首帧照亮一次，之后长期暗相
            world.Light.Intensity = 5f;

            world.StepLogic(); // 亮帧：热 pass 先跑，光在帧末注热
            world.StepLogic(); // 注热必须由这一帧的热 pass 处理
            Assert.IsFalse(world.Heat.LastStepSkipped, "灯注入的热必须由下一帧热 pass 传导，不能跳过");

            int frames = StepUntilSkipped(world, 20000);
            Assert.IsTrue(world.Heat.LastStepSkipped, $"暗相收敛后应进入跳过（用了 {frames} 帧）");
        }

        /// <summary>
        /// 跳过透明性：同一确定性的加热世界，跳过开 / 关各跑到热场收敛，热场、湿度场与格子必须逐位一致；
        /// 跳过路径若不等价于每帧重算，这里立刻暴露。
        /// </summary>
        [Test]
        public void HeatSkip_IsTransparentToAlwaysRecomputing()
        {
            ParticleWorld skipping = BuildStillWorld();
            ParticleWorld full = BuildStillWorld();
            full.Heat.FixpointSkipEnabled = false;

            // 同一处注入同样多的热，两条路径分别演化。
            skipping.Heat.AddHeat(4, 4, 50f);
            full.Heat.AddHeat(4, 4, 50f);

            int skippedFrames = 0;
            for (int t = 0; t < 4000; t++)
            {
                skipping.StepLogic();
                full.StepLogic();
                if (skipping.Heat.LastStepSkipped)
                {
                    skippedFrames++;
                }

                Assert.IsFalse(full.Heat.LastStepSkipped, "关闭跳过时不应报告跳过");
            }

            Assert.Greater(skippedFrames, 0, "跳过世界应确实进入过跳过（否则本对照没有意义）");

            float[] a = skipping.Heat.Values;
            float[] b = full.Heat.Values;
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(b[i], a[i], 0f, $"格子 {i} 的热量在跳过/重算两条路径下不一致");
            }

            for (int y = 0; y < skipping.Height; y++)
            {
                for (int x = 0; x < skipping.Width; x++)
                {
                    Assert.AreEqual(full.Humidity.Get(x, y), skipping.Humidity.Get(x, y), 0f, $"({x},{y}) 的湿度不一致");
                    Assert.AreEqual(
                        full.GetCell(x, y).typeId, skipping.GetCell(x, y).typeId, $"({x},{y}) 的 typeId 不一致");
                }
            }
        }
    }
}
