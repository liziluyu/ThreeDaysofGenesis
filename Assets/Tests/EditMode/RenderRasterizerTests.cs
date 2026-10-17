using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// **worker-safe 光栅化器**的聚焦验收（见 docs/design-threaded-simulation-rendering.md 阶段 2「像素路径」）：
    /// 像素缓冲已经是项目自有的纯值类型（<see cref="RenderColor"/>），光栅化只用纯数学
    /// （<see cref="RenderColorMath"/>）——本类的每一条都以 **Unity 自己的实现为基准**
    /// （<c>Color32.Lerp</c> / <c>Mathf.Lerp</c> / <c>(byte)(通道 × 系数)</c>）算出期望值，
    /// 逐字节比对光栅化输出，因此「少了 Unity 依赖」与「像素逐位不变」两件事被同时钉住。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>描边</b>（<see cref="ParticleTypeInfo.HasOutline"/>）：基底色 × <see cref="ParticlePixelBuffer.OutlineFactor"/>；</item>
    /// <item><b>液面高光</b>（<see cref="ParticleTypeInfo.HasSurfaceHighlight"/>）：基底色向高光色插值 <see cref="ParticlePixelBuffer.SurfaceHighlightFactor"/>；</item>
    /// <item><b>湿度云</b>：空气格底色向云色插值（湿度归一化）；</item>
    /// <item><b>热力叠加</b>：向纯红插值（<c>T / 满量程</c>）；</item>
    /// <item><b>昼夜明暗</b>：只缩「画了东西」的像素的 RGB，不缩天空色、不动 alpha；</item>
    /// <item><b>纯数学等价</b>：<see cref="RenderColorMath"/> 对 Unity 实现做扫描比对（含边界 t）；</item>
    /// <item><b>零分配</b>：光栅化与上屏转换的稳态循环逐字节零堆分配。</item>
    /// </list>
    /// 走生产类型表（<see cref="ParticleRegistration.RegisterAll"/>），因此用的就是石头 / 沙 / 水 / 大气这些真实调色板。
    /// </remarks>
    public sealed class RenderRasterizerTests
    {
        /// <summary>描边用例的背景色（与 <c>SandStoneParticleTests</c> 同一取值）。</summary>
        private static readonly RenderColor Background = new RenderColor(9, 8, 7, 255);

        /// <summary>云色（<see cref="ParticlePixelBuffer"/> 里云滴染色的字面值：满湿格的期望像素）。</summary>
        private static readonly RenderColor CloudColor = new RenderColor(244, 248, 252, 255);

        /// <summary>热力叠加的最热色（满量程格的期望像素）。</summary>
        private static readonly RenderColor HotColor = new RenderColor(255, 0, 0, 255);

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        /// <summary>该格按调色板与 shade 应显示的基底色（Unity 授权的源调色板，未经任何调制）。</summary>
        private static Color32 BaseColorAt(ParticleWorld world, int x, int y)
        {
            Cell cell = world.GetCell(x, y);
            return ParticleRegistry.Types[cell.typeId].Info.Palette[cell.shade];
        }

        /// <summary>把一帧纯值像素光栅化出来（调用方给背景色与亮度）。</summary>
        private static RenderColor[] Rasterize(ParticleWorld world, RenderColor background, float brightness)
        {
            var pixels = new RenderColor[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background, brightness);
            return pixels;
        }

        /// <summary>
        /// 描边：与空气交界的格调暗 <see cref="ParticlePixelBuffer.OutlineFactor"/>、四邻全是实物的中心格保持基底色。
        /// 期望值用 Unity 源调色板亲手算（浮点乘后截断，alpha 不变），因此这条同时钉住了「调色板镜像逐位相同」。
        /// </summary>
        [Test]
        public void Outline_DimsBaseColorByOutlineFactor_ForAirBoundaryOnly()
        {
            var world = new ParticleWorld(5, 5, 7u);
            for (int y = 1; y <= 3; y++)
            {
                for (int x = 1; x <= 3; x++)
                {
                    world.Set(x, y, ParticleIds.Sand);
                }
            }

            RenderColor[] pixels = Rasterize(world, Background, 1f);

            Color32 interior = BaseColorAt(world, 2, 2);
            Assert.AreEqual(RenderColorConversion.FromColor32(interior), pixels[world.Index(2, 2)],
                "四邻全是沙的中心格 = 原样基底色");

            Color32 corner = BaseColorAt(world, 1, 1);
            var expected = new Color32(
                (byte)(corner.r * ParticlePixelBuffer.OutlineFactor),
                (byte)(corner.g * ParticlePixelBuffer.OutlineFactor),
                (byte)(corner.b * ParticlePixelBuffer.OutlineFactor),
                corner.a);
            Assert.AreEqual(RenderColorConversion.FromColor32(expected), pixels[world.Index(1, 1)],
                "与空气交界的角格 = 基底色 × 描边系数（Unity 同一算式）");
        }

        /// <summary>
        /// 液面高光：正上方无实物的水格向 <see cref="ParticlePixelBuffer.SurfaceHighlightColor"/> 插值，
        /// 被另一格水压住的格保持基底色。期望值直接用 Unity <c>Color32.Lerp</c> 算——这是纯数学实现必须逐位对齐的真基准。
        /// </summary>
        [Test]
        public void SurfaceHighlight_BlendsLiquidTopCellByUnityLerp()
        {
            var world = new ParticleWorld(4, 4, 7u);
            world.Set(2, 1, ParticleIds.Water);
            world.Set(2, 0, ParticleIds.Water);

            RenderColor[] pixels = Rasterize(world, Background, 1f);

            Color32 surface = BaseColorAt(world, 2, 1);
            Color32 expected = Color32.Lerp(
                surface,
                RenderColorConversion.ToColor32(ParticlePixelBuffer.SurfaceHighlightColor),
                ParticlePixelBuffer.SurfaceHighlightFactor);
            Assert.AreEqual(RenderColorConversion.FromColor32(expected), pixels[world.Index(2, 1)],
                "液面水格 = 基底色向高光色插值（与 Color32.Lerp 逐位一致）");

            Assert.AreEqual(RenderColorConversion.FromColor32(BaseColorAt(world, 2, 0)), pixels[world.Index(2, 0)],
                "被水压住的格不是液面，保持基底色");
        }

        /// <summary>
        /// 湿度云：湿度到顶的空气格就是云色本身（字面值），半程格按湿度向云色插值——
        /// 期望值用 Unity <c>Color32.Lerp</c> 与光栅化器同一套归一化比例算。
        /// </summary>
        [Test]
        public void Cloud_BlendsHumidAirTowardCloudColor()
        {
            var world = new ParticleWorld(3, 1, 7u);
            world.Set(0, 0, ParticleIds.Atmosphere);
            world.Set(1, 0, ParticleIds.Atmosphere);
            world.Set(2, 0, ParticleIds.Atmosphere);
            world.Humidity.Set(1, 0, HumidityField.CloudFullHumidity);              // 满湿（阈值之上）⇒ 纯云色
            float half = HumidityField.VisibleThreshold
                + (HumidityField.CloudFullHumidity - HumidityField.VisibleThreshold) * 0.5f;
            world.Humidity.Set(2, 0, half);                                          // 半程

            RenderColor[] pixels = Rasterize(world, Background, 1f);

            Assert.AreEqual(Background, pixels[0], "干燥空气仍是背景色");
            Assert.AreEqual(CloudColor, pixels[1], "满湿格 = 云色（字面值）");

            float t = (half - HumidityField.VisibleThreshold)
                / (HumidityField.CloudFullHumidity - HumidityField.VisibleThreshold);
            Color32 expected = Color32.Lerp(
                RenderColorConversion.ToColor32(Background),
                RenderColorConversion.ToColor32(CloudColor),
                t);
            Assert.AreEqual(RenderColorConversion.FromColor32(expected), pixels[2],
                "半湿格 = 背景向云色插值（与 Color32.Lerp 逐位一致）");
        }

        /// <summary>
        /// 热力叠加：满量程格是纯红（字面值），半程格是基底色向纯红插值；没有热量的格不受影响。
        /// </summary>
        [Test]
        public void HeatOverlay_BlendsTowardPureRed()
        {
            var world = new ParticleWorld(3, 1, 7u);
            world.Heat.DebugHeatOverlay = true;
            world.Heat.DebugFullScaleTemperature = 10f;
            world.Heat.SetHeat(1, 0, 10f);   // 空气热容 1 ⇒ 满量程
            world.Heat.SetHeat(2, 0, 5f);    // 半程

            RenderColor[] pixels = Rasterize(world, Background, 1f);

            Assert.AreEqual(Background, pixels[0], "没有热量的格不受叠加影响");
            Assert.AreEqual(HotColor, pixels[1], "满量程格 = 纯红（字面值）");

            Color32 expected = Color32.Lerp(
                RenderColorConversion.ToColor32(Background),
                RenderColorConversion.ToColor32(HotColor),
                0.5f);
            Assert.AreEqual(RenderColorConversion.FromColor32(expected), pixels[2],
                "半程格 = 基底色向纯红插值（与 Color32.Lerp 逐位一致）");
        }

        /// <summary>
        /// 昼夜明暗：同一世界跑两遍（白天 / 夜晚 0.35），**画了东西的像素** RGB 恰好等于白天值乘系数（浮点乘后截断、
        /// 截断发生在每一个中间结果上的连锁也一并钉住），alpha 不动；裸天空格则与白天逐位相同。
        /// 世界里同时放进描边（石头）、液面高光（水）、云（湿大气）三种「画了东西」的像素。
        /// </summary>
        [Test]
        public void NightBrightness_DimsPaintedPixelsOnly()
        {
            var world = new ParticleWorld(4, 3, 7u);
            world.Set(0, 1, ParticleIds.Stone);         // 孤立固体 ⇒ 描边
            world.Set(2, 1, ParticleIds.Water);         // 上方是空气 ⇒ 液面高光
            world.Set(3, 0, ParticleIds.Atmosphere);
            world.Humidity.Set(3, 0, HumidityField.CloudFullHumidity);   // 云

            const float NightBrightness = 0.35f;
            RenderColor[] day = Rasterize(world, Background, 1f);
            RenderColor[] night = Rasterize(world, Background, NightBrightness);

            int painted = 0;
            for (int i = 0; i < day.Length; i++)
            {
                RenderColor lit = day[i];
                RenderColor dim = night[i];
                Assert.AreEqual(lit.a, dim.a, $"下标 {i}：亮度只缩 RGB，不动 alpha");

                if (lit == Background)
                {
                    Assert.AreEqual(Background, dim, $"下标 {i}：裸天空不参与昼夜变暗");
                    continue;
                }

                painted++;
                var expected = new RenderColor(
                    (byte)(lit.r * NightBrightness),
                    (byte)(lit.g * NightBrightness),
                    (byte)(lit.b * NightBrightness),
                    lit.a);
                Assert.AreEqual(expected, dim, $"下标 {i}：画了东西的像素 = 白天值 × 夜晚亮度");
            }

            Assert.AreEqual(3, painted, "前提：这个世界里恰好有 3 个画了东西的像素（描边 / 液面 / 云）");
        }

        /// <summary>
        /// 纯数学对 Unity 实现的**扫描等价**：连续 t（含边界外的 ±）与多组颜色上，
        /// <see cref="RenderColorMath.Lerp(in RenderColor, in RenderColor, float)"/> 与 <c>Color32.Lerp</c> 逐位相同，
        /// <see cref="RenderColorMath.Lerp(float, float, float)"/> 与 <c>Mathf.Lerp</c> 逐位相同，
        /// <see cref="RenderColorMath.ScaleRgb"/> 与旧路径的 <c>(byte)(通道 × 系数)</c> 逐位相同。
        /// </summary>
        [Test]
        public void PureMath_MatchesUnityImplementationsExactly()
        {
            var pairs = new[]
            {
                (new Color32(0, 0, 0, 0), new Color32(255, 255, 255, 255)),
                (new Color32(9, 8, 7, 255), new Color32(244, 248, 252, 255)),
                (new Color32(200, 228, 245, 255), new Color32(41, 87, 133, 200)),
                (new Color32(17, 200, 3, 255), new Color32(17, 200, 3, 255)),
            };

            for (int p = 0; p < pairs.Length; p++)
            {
                RenderColor a = RenderColorConversion.FromColor32(pairs[p].Item1);
                RenderColor b = RenderColorConversion.FromColor32(pairs[p].Item2);

                for (float t = -0.25f; t <= 1.2501f; t += 0.01f)
                {
                    Assert.AreEqual(
                        RenderColorConversion.FromColor32(Color32.Lerp(pairs[p].Item1, pairs[p].Item2, t)),
                        RenderColorMath.Lerp(a, b, t),
                        $"颜色对 {p}、t = {t}：颜色插值必须与 Color32.Lerp 逐位一致");

                    Assert.AreEqual(
                        Mathf.Lerp(0.4f, 1f, t),
                        RenderColorMath.Lerp(0.4f, 1f, t),
                        0f,
                        $"t = {t}：标量插值必须与 Mathf.Lerp 逐位一致");
                }
            }

            float[] scales = { 0f, 0.35f, 0.55f, 0.6f, 0.5f, 0.9f, 1f, 1.2f };
            for (int p = 0; p < pairs.Length; p++)
            {
                RenderColor color = RenderColorConversion.FromColor32(pairs[p].Item1);
                for (int s = 0; s < scales.Length; s++)
                {
                    Color32 source = pairs[p].Item1;
                    float k = scales[s];
                    var expected = new RenderColor(
                        (byte)(source.r * k),
                        (byte)(source.g * k),
                        (byte)(source.b * k),
                        source.a);
                    Assert.AreEqual(expected, RenderColorMath.ScaleRgb(color, k),
                        $"颜色对 {p}、系数 {k}：RGB 缩放必须与旧算式逐位一致");
                }
            }
        }

        /// <summary>
        /// 发布上来的像素帧与昼夜混合也逐位等价于 Unity 路径：帧里的天空色 = <c>Color32.Lerp(夜晚色, 白天色, 日照度)</c>、
        /// 亮度 = <c>Mathf.Lerp(夜晚亮度, 1, 日照度)</c>，而画了东西的像素 = 基底色 × 该亮度（含描边 / 液面高光之后的结果）。
        /// 这条走的是生产采集路径（<c>FramePump</c> → <see cref="SimulationFrameCollector.CollectRender"/>）。
        /// </summary>
        [Test]
        public void RenderFrame_DayNightBlend_MatchesTheUnityColor32Path()
        {
            var world = new ParticleWorld(5, 4, 7u);

            // 亮相位 20 刻 / 暗相位 20 刻 ⇒ 淡入淡出各 2 刻；推进 1 个逻辑帧后日照度恰为 1/2。
            world.Light.Enabled = true;
            world.Light.LitTicks = 20;
            world.Light.DarkTicks = 20;
            world.Light.Reset();
            world.StepLogic();
            Assert.AreEqual(0.5f, world.Light.Daylight, 1e-6f, "前提：日照度正好在昼夜中间");

            // 网格在昼夜读数定好之后再布置：下面是纯观察，不会再推进世界。
            for (int y = 0; y <= 2; y++)
            {
                for (int x = 1; x <= 3; x++)
                {
                    world.Set(x, y, ParticleIds.Sand);      // 3×3 沙块：内部格 (2,1) 四邻全是沙 ⇒ 不描边
                }
            }

            world.Set(0, 3, ParticleIds.Water);              // 顶行 ⇒ 上方越界算液面 ⇒ 高光
            world.Set(4, 3, ParticleIds.Atmosphere);
            world.Humidity.Set(4, 3, HumidityField.CloudFullHumidity);   // 满湿 ⇒ 云色

            var light = new LightSettings(
                true, 20, 20, 5f, LightController.DefaultCloudExtinction,
                new RenderColor(0xCC, 0xE5, 0xFF, 255), new RenderColor(0x2A, 0x22, 0x3A, 255), 0.4f);

            var frames = new FramePump { Light = light };
            SimulationFrame frame = frames.Publish(world);

            float daylight = frame.Render.Daylight;
            float brightness = Mathf.Lerp(Mathf.Clamp01(light.NightBrightness), 1f, daylight);
            Assert.AreEqual(brightness, frame.Render.Brightness, 0f, "帧里的整场亮度 = Mathf.Lerp(夜晚亮度, 1, 日照度)");

            Color32 background = Color32.Lerp(
                RenderColorConversion.ToColor32(light.NightBackground),
                RenderColorConversion.ToColor32(light.DayBackground),
                daylight);
            Assert.AreEqual(RenderColorConversion.FromColor32(background), frame.Render.Pixels[world.Index(4, 0)],
                "天空格 = Color32.Lerp(夜晚色, 白天色, 日照度)，且不随亮度再缩一次");

            // 沙块贴空气的左下角：先描边、再按夜晚亮度变暗（两道截断都在 Unity 同一算式下复现）。
            Color32 edge = BaseColorAt(world, 1, 0);
            Color32 outlined = new Color32(
                (byte)(edge.r * ParticlePixelBuffer.OutlineFactor),
                (byte)(edge.g * ParticlePixelBuffer.OutlineFactor),
                (byte)(edge.b * ParticlePixelBuffer.OutlineFactor),
                edge.a);
            var expectedEdge = new RenderColor(
                (byte)(outlined.r * brightness),
                (byte)(outlined.g * brightness),
                (byte)(outlined.b * brightness),
                outlined.a);
            Assert.AreEqual(expectedEdge, frame.Render.Pixels[world.Index(1, 0)],
                "贴空气的沙：描边 → 再按夜晚亮度变暗（逐位一致）");

            Color32 interior = BaseColorAt(world, 2, 1);
            var expectedInterior = new RenderColor(
                (byte)(interior.r * brightness),
                (byte)(interior.g * brightness),
                (byte)(interior.b * brightness),
                interior.a);
            Assert.AreEqual(expectedInterior, frame.Render.Pixels[world.Index(2, 1)],
                "沙块内部格：无描边，基底色 × 夜晚亮度");

            Color32 water = BaseColorAt(world, 0, 3);
            Color32 highlighted = Color32.Lerp(
                water,
                RenderColorConversion.ToColor32(ParticlePixelBuffer.SurfaceHighlightColor),
                ParticlePixelBuffer.SurfaceHighlightFactor);
            var expectedWater = new RenderColor(
                (byte)(highlighted.r * brightness),
                (byte)(highlighted.g * brightness),
                (byte)(highlighted.b * brightness),
                highlighted.a);
            Assert.AreEqual(expectedWater, frame.Render.Pixels[world.Index(0, 3)],
                "液面水格：先高光、再按夜晚亮度变暗");

            var expectedCloud = new RenderColor(
                (byte)(CloudColor.r * brightness),
                (byte)(CloudColor.g * brightness),
                (byte)(CloudColor.b * brightness),
                CloudColor.a);
            Assert.AreEqual(expectedCloud, frame.Render.Pixels[world.Index(4, 3)],
                "满湿空气格：云色 → 同样按夜晚亮度变暗");
        }

        /// <summary>
        /// 稳态**零分配**：光栅化循环与「纯值像素 → Unity 上传缓冲」的转换循环都不产生堆分配
        /// （像素缓冲、类型标志表、上传缓冲都是预先备好、跨帧复用的）。
        /// </summary>
        [Test]
        public void RasterizeAndUpload_SteadyState_DoesNotAllocate()
        {
            var world = new ParticleWorld(64, 32, 7u);
            for (int x = 0; x < 64; x++)
            {
                world.Set(x, 0, ParticleIds.Sand);
            }

            var pixels = new RenderColor[world.CellCount];
            var upload = new Color32[world.CellCount];

            // 预热：类型标志表按类型表引用缓存，第一次调用才是重建表的那一次（唯一一次分配）。
            ParticlePixelBuffer.Fill(world, pixels, Background, 0.8f);
            RenderColorConversion.CopyToColor32(pixels, upload);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++)
            {
                ParticlePixelBuffer.Fill(world, pixels, Background, 0.8f);
            }

            long baked = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++)
            {
                RenderColorConversion.CopyToColor32(pixels, upload);
            }

            long after = System.GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, baked - before, "光栅化稳态 64 帧必须零分配");
            Assert.AreEqual(0L, after - baked, "上传转换稳态 64 帧必须零分配");

            Assert.Throws<System.ArgumentException>(
                () => RenderColorConversion.CopyToColor32(pixels, new Color32[world.CellCount - 1]),
                "上传缓冲没跟着帧尺寸重建时必须显式报错，而不是静默写坏像素");
        }
    }
}
