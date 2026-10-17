using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 雷电（<see cref="LightningParticle"/>）的行为契约：**随机刻命中空气粒子**时按 2% 门 +
    /// 半径 3 圆盘总湿度 ≥ 3.0 起爆，自本格向下铺一整条主干（逐行坐标哈希 ±1 抖动、遇固 / 液即止 = 落点），
    /// 并在落点上方那排创建一团火；主干只写气体格（绝不覆盖固 / 液），shade 即闪光档、寿命 4 逻辑帧。
    /// </summary>
    /// <remarks>
    /// <para>起爆门挂在 <see cref="AtmosphereParticle.OnRandomTick"/>（随机刻只命中非空气、非静态的格，
    /// 被粒子化的「空气」正是大气），因此测试直接调该委托而非依赖世界的随机抽格——确定、可枚举。</para>
    /// <para>跨类型写入（雷电 → 火）依赖 <see cref="ParticleIds"/> 回填，因此全部走
    /// <see cref="ParticleRegistration.RegisterAll"/> 的生产类型表（<c>new ParticleWorld(w, h, seed)</c>），
    /// 与 <c>FireParticleTests</c> 一致。概率门一律用**窗口**断言（N 次尝试内必中 / 恒不中），
    /// 不绑定精确帧号。</para>
    /// </remarks>
    public sealed class LightningParticleTests
    {
        private const int W = 24;
        private const int H = 24;

        /// <summary>
        /// 尝试次数：2% 门在 2000 次内一次不中的概率 ≈ 0.98^2000 ≈ 2.8e-18，确定性成立（同种子同序列）。
        /// 门从 10% 调到 2% 后必须同步放大本窗口——沿用 300 次会有约 0.2% 的漏检率，测试随机变红。
        /// </summary>
        private const int MaxAttempts = 2000;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        // ============================== 夹具 ==============================

        /// <summary>24×24、全格等温（T = 100）、全格大气的世界，外加 y = 0 一整行石头地板。
        /// 温度取 100 是为了让 <see cref="HumidityField.SaturationHumidity"/>（= 51.2）远高于测试用的
        /// 湿度量级，从而**关掉降水**——否则云会自凝成水格、污染「闪电结构」这类断言。热输运全关，
        /// 等温场使大气没有浮力，世界除雷电外完全静止。</summary>
        private static ParticleWorld BuildWorld(uint seed, bool withFloor)
        {
            ParticleWorld world = new ParticleWorld(W, H, seed);
            world.RandomTicksPerStep = 0;
            world.Light.Enabled = false;
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                    world.Heat.SetHeat(x, y, 100f);
                }
            }

            if (withFloor)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Set(x, 0, ParticleIds.Stone);
                    world.Heat.SetHeat(x, 0, 100f);
                }
            }

            return world;
        }

        /// <summary>把以 (cx,cy) 为中心、半径 3 的圆盘（29 格）每格灌入 <paramref name="perCell"/> 湿度。</summary>
        private static void Saturate(ParticleWorld world, int cx, int cy, float perCell)
        {
            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (dx * dx + dy * dy <= 9)
                    {
                        world.Humidity.Set(cx + dx, cy + dy, perCell);
                    }
                }
            }
        }

        /// <summary>直接调大气（= 空气粒子）的随机刻委托——雷电起爆的**唯一**入口。</summary>
        private static void RandomTickAtmosphere(ParticleWorld world, int x, int y)
        {
            ParticleRegistry.Types[ParticleIds.Atmosphere].OnRandomTick(world, x, y);
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

        /// <summary>反复触发随机刻直到真的起爆（返回是否起爆），用于把「概率门」与「结构断言」解耦。</summary>
        private static bool StrikeOnce(ParticleWorld world, int x, int y)
        {
            for (int i = 0; i < MaxAttempts; i++)
            {
                int before = CountType(world, ParticleIds.Lightning);
                RandomTickAtmosphere(world, x, y);
                if (CountType(world, ParticleIds.Lightning) > before)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>某一行里雷电的 x（没有则 −1）。</summary>
        private static int LightningXInRow(ParticleWorld world, int y)
        {
            for (int x = 0; x < W; x++)
            {
                if (world.GetCell(x, y).typeId == ParticleIds.Lightning)
                {
                    return x;
                }
            }

            return -1;
        }

        // ============================== 注册 / 属性 ==============================

        /// <summary>生产注册路径回填出非 0 id、带 Gas 标签、非静态、且热学与大气同值（放电不注热、不额外辐射）。</summary>
        [Test]
        public void ProductionRegistration_BackfillsGasTaggedId()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, ParticleIds.Lightning, "雷电 id 必须被回填");

            ParticleType lightning = ParticleRegistry.Types[ParticleIds.Lightning];
            ParticleType atmosphere = ParticleRegistry.Types[ParticleIds.Atmosphere];
            Assert.IsTrue((lightning.Info.Tags & ParticleTags.Gas) != 0, "雷电必须是气体（要被固 / 液沉穿、不被大气推动）");
            Assert.IsFalse(lightning.Info.IsStatic, "雷电不是静态类型（要收逻辑帧衰老）");
            Assert.AreEqual(atmosphere.Info.HeatCapacity, lightning.Info.HeatCapacity, "热容与大气同值：替换掉的那几格空气热量口径不变");
            Assert.AreEqual(atmosphere.Info.ThermalEmissivity, lightning.Info.ThermalEmissivity, "辐射率与大气同值：雷电不额外漏热");
            Assert.Greater(lightning.Info.Density, atmosphere.Info.Density, "密度高于大气，环境大气才推不动它");
            Assert.AreEqual(LightningParticle.LifetimeTicks, lightning.Info.Palette.Length, "调色板长度 == 寿命档数（shade 即闪光档）");
        }

        // ============================== 起爆门 ==============================

        /// <summary>
        /// 起爆门：圆盘总湿度低于门槛（每格 0.10 ⇒ 2.90）时**永不**起爆，达标（每格 0.11 ⇒ 3.19）时
        /// 必在窗口内起爆；完全干燥的世界同样永不起爆。门槛常量本身也被断言，防止它被悄悄改动而测试仍过。
        /// </summary>
        [Test]
        public void RandomTick_StrikesOnlyAboveHumidityThreshold()
        {
            Assert.AreEqual(3f, LightningParticle.StrikeHumidityThreshold, "门槛常量是回归锚点");
            Assert.AreEqual(50u, LightningParticle.StrikeChanceDenominator, "起爆门分母 = 2% 是回归锚点");

            ParticleWorld dry = BuildWorld(11u, withFloor: true);
            for (int i = 0; i < MaxAttempts; i++)
            {
                RandomTickAtmosphere(dry, 12, 20);
            }

            Assert.AreEqual(0, CountType(dry, ParticleIds.Lightning), "干燥空气永不起爆");

            ParticleWorld thin = BuildWorld(11u, withFloor: true);
            Saturate(thin, 12, 20, 0.10f);   // 29 × 0.10 = 2.90 < 3.0
            for (int i = 0; i < MaxAttempts; i++)
            {
                RandomTickAtmosphere(thin, 12, 20);
            }

            Assert.AreEqual(0, CountType(thin, ParticleIds.Lightning), "圆盘总湿度 2.90 未达门槛，不得起爆");

            ParticleWorld thick = BuildWorld(11u, withFloor: true);
            Saturate(thick, 12, 20, 0.11f);  // 29 × 0.11 = 3.19 ≥ 3.0
            bool struck = StrikeOnce(thick, 12, 20);
            Assert.IsTrue(struck, "圆盘总湿度 3.19 达标，窗口内必起爆");
        }

        /// <summary>
        /// 随机流契约：门在湿度判定**之前**摇，因此每次被随机刻点到的空气格都恰好消耗 1 个随机数，
        /// 与湿度是否达标、是否起爆无关——随机流不随云量 / 地形漂移（同草「撒种先摇骰」的口径）。
        /// </summary>
        [Test]
        public void RandomTick_ConsumesExactlyOneDrawPerAttempt()
        {
            const int attempts = 50;

            ParticleWorld probed = BuildWorld(23u, withFloor: true);   // 无湿度：每次都在湿度门失败
            for (int i = 0; i < attempts; i++)
            {
                RandomTickAtmosphere(probed, 12, 20);
            }

            ParticleWorld reference = BuildWorld(23u, withFloor: true);
            for (int i = 0; i < attempts; i++)
            {
                reference.NextUInt();
            }

            Assert.AreEqual(
                reference.NextUInt(), probed.NextUInt(),
                "每次随机刻尝试必须恰消耗 1 个随机数（先摇骰、后判湿度）");
        }

        // ============================== 主干结构 ==============================

        /// <summary>
        /// 主干结构：自起爆格向下每一行**恰一格**雷电、相邻行 x 差 ≤ 1、一直铺到落点（首个非气体格）
        /// 的正上方；落点那格本身（石头地板）原样保留——雷电只写气体格。
        /// </summary>
        [Test]
        public void Strike_BuildsContiguousBoltStoppingAtFirstSolid()
        {
            ParticleWorld world = BuildWorld(7u, withFloor: true);
            Saturate(world, 12, 20, 0.12f);

            Assert.IsTrue(StrikeOnce(world, 12, 20), "饱和世界里必须起爆");

            // 地板 y = 0 是落点 ⇒ 雷电覆盖 y = 1..20，共 20 格；随后落点上方那排（y = 1）的火团
            // 覆盖掉主干最下面那格，因此存活 19 格（y = 2..20）。
            Assert.AreEqual(19, CountType(world, ParticleIds.Lightning), "主干覆盖起爆格到落点正上方");

            int previousX = int.MinValue;
            for (int y = 2; y <= 20; y++)
            {
                int x = LightningXInRow(world, y);
                Assert.GreaterOrEqual(x, 0, $"第 {y} 行必须有且恰有一格雷电");
                if (previousX != int.MinValue)
                {
                    Assert.LessOrEqual(
                        System.Math.Abs(x - previousX), 1,
                        $"相邻行抖动不得超过 1 格（第 {y} 行）");
                }

                previousX = x;
            }

            Assert.AreEqual(
                ParticleIds.Stone, world.GetCell(12, 0).typeId,
                "落点（石头地板）不得被雷电覆盖——主干只写气体格");
            Assert.AreEqual(-1, LightningXInRow(world, 0), "落点行不得出现雷电");
        }

        /// <summary>
        /// 主干**不覆盖液体**：水面即落点，雷电停在水面正上方，水格数不变（雷电只写气体格）。
        /// </summary>
        [Test]
        public void Strike_StopsAtLiquidWithoutOverwritingIt()
        {
            ParticleWorld world = BuildWorld(7u, withFloor: true);
            for (int x = 8; x <= 16; x++)
            {
                for (int y = 1; y <= 6; y++)
                {
                    world.Set(x, y, ParticleIds.Water);
                }
            }

            int waterBefore = CountType(world, ParticleIds.Water);
            Saturate(world, 12, 20, 0.12f);
            Assert.IsTrue(StrikeOnce(world, 12, 20), "水面以上的饱和气柱必须起爆");

            Assert.AreEqual(waterBefore, CountType(world, ParticleIds.Water), "雷电不得覆盖任何一格水");
            for (int y = 1; y <= 6; y++)
            {
                Assert.AreEqual(-1, LightningXInRow(world, y), $"水里不得有雷电（第 {y} 行）");
            }
        }

        /// <summary>
        /// 整根柱都是气体（网格底行也没有落点）时**不生成火**——没有接地就没有落点，
        /// 但主干照旧铺满到网格底行（起爆格 y=20 ⇒ 21 格雷电）。
        /// </summary>
        [Test]
        public void Strike_WithoutGround_LeavesNoFire()
        {
            ParticleWorld world = BuildWorld(7u, withFloor: false);
            Saturate(world, 12, 20, 0.12f);

            Assert.IsTrue(StrikeOnce(world, 12, 20), "无地板的世界同样要起爆");

            Assert.AreEqual(21, CountType(world, ParticleIds.Lightning), "主干一直铺到网格底行");
            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "没有接地落点就不生成火");
        }

        // ============================== 落点火团 ==============================

        /// <summary>
        /// 只在落点上方那**一排**生成一团火：每个火格都**直接坐在落点表面上**（正下方是那个挡住雷电的
        /// 固 / 液格），火格数 ≤ 3（落点 ± 1），全图再无别处的火。主干最下面那格（比落点高一格的
        /// 那一排）被火团覆盖——随机游走在这一排的位置与落点列相差不超过 1，必落在火团跨度内。
        /// </summary>
        [Test]
        public void Strike_CreatesFireClusterAtGroundContact()
        {
            ParticleWorld world = BuildWorld(7u, withFloor: true);
            Saturate(world, 12, 20, 0.12f);
            Assert.IsTrue(StrikeOnce(world, 12, 20), "必须起爆");

            // 地板 y = 0 是落点 ⇒ 火焰在 y = 1。
            int fireCount = 0;
            int minFireX = int.MaxValue;
            int maxFireX = int.MinValue;
            for (int x = 0; x < W; x++)
            {
                if (world.GetCell(x, 1).typeId != ParticleIds.Fire)
                {
                    continue;
                }

                fireCount++;
                if (x < minFireX)
                {
                    minFireX = x;
                }

                if (x > maxFireX)
                {
                    maxFireX = x;
                }

                Assert.AreEqual(
                    ParticleIds.Stone, world.GetCell(x, 0).typeId,
                    $"落点火团必须坐在接地表面上（火格 x={x} 的正下方应是落点那格）");
            }

            Assert.GreaterOrEqual(fireCount, 1, "接地落点必须生成一团火");
            Assert.LessOrEqual(fireCount, 3, "火团宽度不超过 3（落点 ± 1）");
            Assert.AreEqual(fireCount, CountType(world, ParticleIds.Fire), "火只出现在落点那一排");

            // 主干最下面**存活**的那格在落点上方两排、与落点列相差 ≤ 2（随机游走每排至多偏 1）。
            int boltBottomX = LightningXInRow(world, 2);
            Assert.GreaterOrEqual(boltBottomX, 0, "落点上方两排仍应有主干");
            Assert.LessOrEqual(
                System.Math.Abs(boltBottomX - (minFireX + maxFireX) / 2), 2,
                "主干末端必须落在落点火团附近（同一条闪电）");
        }

        // ============================== 寿命与渲染 ==============================

        /// <summary>
        /// 寿命：shade 即闪光档（起爆时全为 0 = 最亮），每逻辑帧整条一起推进一档，走到
        /// <see cref="LightningParticle.LifetimeTicks"/> 即整条消失。用无地板世界隔离掉火（火会在
        /// 附近自行蔓延 / 抛射火舌，干扰「所有雷电格 shade 一致」的断言）。
        /// </summary>
        [Test]
        public void Bolt_FadesAndVanishesWithinLifetime()
        {
            ParticleWorld world = BuildWorld(7u, withFloor: false);
            Saturate(world, 12, 20, 0.12f);
            Assert.IsTrue(StrikeOnce(world, 12, 20), "必须起爆");

            int bolts = CountType(world, ParticleIds.Lightning);
            Assert.Greater(bolts, 0, "起爆后世界必须有雷电");

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (world.GetCell(x, y).typeId == ParticleIds.Lightning)
                    {
                        Assert.AreEqual(0, world.GetCell(x, y).shade, "新生雷电从最亮档（shade = 0）开始");
                    }
                }
            }

            for (int frame = 1; frame < LightningParticle.LifetimeTicks; frame++)
            {
                world.StepLogic();
                Assert.AreEqual(bolts, CountType(world, ParticleIds.Lightning), $"第 {frame} 帧雷电仍在（寿命未到）");

                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        if (world.GetCell(x, y).typeId == ParticleIds.Lightning)
                        {
                            Assert.AreEqual((byte)frame, world.GetCell(x, y).shade, $"第 {frame} 帧闪光档应为 {frame}");
                        }
                    }
                }
            }

            world.StepLogic();
            Assert.AreEqual(0, CountType(world, ParticleIds.Lightning), "寿命到即整条消失");
        }

        /// <summary>
        /// 主干锚定在空气里：周围大气被上下温差驱动的浮力推着走时，雷电**不动**（OnMotion 为空、
        /// 密度高于大气使大气换不进它格）——闪电不会随风飘走。
        /// </summary>
        [Test]
        public void Bolt_IsNotDisplacedByAirMotion()
        {
            ParticleWorld world = BuildWorld(7u, withFloor: true);
            Saturate(world, 12, 20, 0.12f);
            Assert.IsTrue(StrikeOnce(world, 12, 20), "必须起爆");

            // 制造竖直温差：把雷电格烤热、四邻压冷 ⇒ 周围大气按浮力上升。
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    world.Heat.SetHeat(x, y, 0f);
                }
            }

            world.Heat.SetHeat(12, 20, 500f);
            for (int y = 2; y <= 20; y++)
            {
                int x = LightningXInRow(world, y);
                if (x >= 0)
                {
                    world.Heat.SetHeat(x, y, 500f);
                }
            }

            int boltsBefore = CountType(world, ParticleIds.Lightning);
            for (int frame = 0; frame < 3; frame++)
            {
                world.StepMotion();
            }

            Assert.AreEqual(boltsBefore, CountType(world, ParticleIds.Lightning), "运动帧不得增减雷电");
            for (int y = 2; y <= 20; y++)
            {
                Assert.GreaterOrEqual(LightningXInRow(world, y), 0, $"运动帧不得把雷电移出第 {y} 行");
            }
        }

        // ============================== 渲染契约 ==============================

        /// <summary>
        /// 「用黄色的雷电粒子作为视觉表现」：调色板每一档都是**黄色**（红 / 绿通道高、蓝通道明显低，
        /// 且红 ≥ 蓝）、不透明，并且越往后越暗（闪光衰减）；<see cref="ParticlePixelBuffer.Fill"/> 对
        /// 带雷电的世界按 shade 查到该色（每格 4 字节的 Cell → 像素映射无需任何渲染侧改动）。
        /// </summary>
        [Test]
        public void Bolt_RendersWithYellowPalette()
        {
            ParticleType lightning = ParticleRegistry.Types[ParticleIds.Lightning];
            Color32[] palette = lightning.Info.Palette;

            for (int i = 0; i < palette.Length; i++)
            {
                Color32 color = palette[i];
                Assert.AreEqual(255, color.a, $"第 {i} 档必须不透明（雷电要在天空中看得见）");
                Assert.GreaterOrEqual(color.r, 200, $"第 {i} 档红通道应是黄色系");
                Assert.GreaterOrEqual(color.g, 150, $"第 {i} 档绿通道应是黄色系");
                Assert.Less(color.b, 210, $"第 {i} 档蓝通道应明显低（否则偏白而非黄）");
                Assert.Greater(color.r, color.b, $"第 {i} 档应偏黄（红 > 蓝）");
                if (i > 0)
                {
                    Assert.Less(
                        (int)color.r + color.g + color.b,
                        (int)palette[i - 1].r + palette[i - 1].g + palette[i - 1].b,
                        $"第 {i} 档必须比第 {i - 1} 档暗（闪光随时间衰减）");
                }
            }

            // 像素映射：一整条雷电所在的世界，每个雷电格都画出它自己那一档的颜色。
            ParticleWorld world = BuildWorld(7u, withFloor: true);
            Saturate(world, 12, 20, 0.12f);
            Assert.IsTrue(StrikeOnce(world, 12, 20), "必须起爆");

            var pixels = new RenderColor[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, new RenderColor(9, 8, 7, 255));

            int painted = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Cell cell = world.GetCell(x, y);
                    if (cell.typeId != ParticleIds.Lightning)
                    {
                        continue;
                    }

                    painted++;
                    Assert.AreEqual(
                        RenderColorConversion.FromColor32(palette[cell.shade]), pixels[world.Index(x, y)],
                        $"雷电格 ({x},{y}) 应按 shade={cell.shade} 画出对应黄色档");
                }
            }

            Assert.Greater(painted, 0, "世界里应有雷电格参与像素映射");
        }

        // ============================== 确定性 ==============================

        /// <summary>
        /// 同种子同命令序列逐格可复现：两个同种子世界做同样次数的随机刻尝试、再跑同样多的逻辑帧，
        /// 最终 cells / 热量 / 湿度逐位一致（雷电不引入任何非确定性的状态）。
        /// </summary>
        [Test]
        public void Strike_ReplaysIdentically()
        {
            ParticleWorld a = BuildWorld(99u, withFloor: true);
            ParticleWorld b = BuildWorld(99u, withFloor: true);

            Saturate(a, 12, 20, 0.12f);
            Saturate(b, 12, 20, 0.12f);

            for (int i = 0; i < MaxAttempts; i++)
            {
                RandomTickAtmosphere(a, 12, 20);
                RandomTickAtmosphere(b, 12, 20);
            }

            Assert.Greater(CountType(a, ParticleIds.Lightning), 0, "窗口内应已起爆，否则重放断言无意义");

            for (int frame = 0; frame < 8; frame++)
            {
                a.StepLogic();
                b.StepLogic();

                for (int i = 0; i < a.CellCount; i++)
                {
                    Assert.AreEqual(
                        (int)b.Cells[i].typeId, (int)a.Cells[i].typeId,
                        $"第 {frame} 帧格 {i} 的 typeId 不一致");
                    Assert.AreEqual(
                        (int)b.Cells[i].shade, (int)a.Cells[i].shade,
                        $"第 {frame} 帧格 {i} 的 shade 不一致");
                    Assert.AreEqual(
                        b.Heat.Values[i], a.Heat.Values[i],
                        $"第 {frame} 帧格 {i} 的热量不一致");
                }

                for (int x = 0; x < a.Width; x++)
                {
                    for (int y = 0; y < a.Height; y++)
                    {
                        Assert.AreEqual(
                            b.Humidity.Get(x, y), a.Humidity.Get(x, y),
                            $"第 {frame} 帧 ({x},{y}) 的湿度不一致");
                    }
                }
            }
        }
    }
}
