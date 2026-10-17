using NUnit.Framework;
using UnityEngine;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 气象系统测试：气体浮力/热压原语（隔离类型表）+ **湿度场**的水循环与守恒（生产注册路径）。
    /// </summary>
    /// <remarks>
    /// 水循环测试必须走 <see cref="ParticleRegistration.RegisterAll"/> 的全局表：蒸发/降雨靠
    /// <see cref="ParticleIds"/> 的回填字段找到对方 typeId，隔离表里这些字段是 0（契约允许的唯一例外路径，
    /// 与 <c>SandStoneParticleTests</c> 的取舍一致）。浮力原语测试直接调用 <see cref="ParticleRules"/>，
    /// 不经过运动 / 逻辑钩子，因此不受阈值干扰。
    /// <para>云不再是粒子：湿度 = <see cref="HumidityField"/> 里的一个标量，云就是湿度足够高的空气。</para>
    /// </remarks>
    public sealed class WeatherParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>水总量 = 水格数 + 蒸发成的水汽（湿度场总量）；用于守恒断言。</summary>
        private static float TotalWater(ParticleWorld world)
        {
            return CountType(world, ParticleIds.Water) + world.Humidity.Sum();
        }

        // ==================== 浮力原语（隔离类型表，直调规则） ====================

        /// <summary>热气上升：比四邻热的气体 TryBuoyantStep 后上移一格，热量随气体搬家并按绝热直减率降温（C6）。</summary>
        [Test]
        public void BuoyantStep_HotGasRises()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Heat.SetHeat(4, 4, 200f); // 热容 1 → T=200，四邻 T=0

            Assert.IsTrue(ParticleRules.TryBuoyantStep(world, 4, 4, 1f), "热气应能上升");
            Assert.AreEqual(atmosphere, world.GetCell(4, 5).typeId, "热气上移一格");
            // 上升 Δy=+1、大气热容 1 → ΔQ = −AdiabaticLapse × 1 × 1（绝热上升冷却）。
            Assert.AreEqual(200f - world.Heat.AdiabaticLapse, world.Heat.GetHeat(4, 5), 1e-4f,
                "热量随气体搬家，并按绝热直减率降温");
        }

        /// <summary>冷气下沉：比四邻冷的气体 TryBuoyantStep 后下移一格。</summary>
        [Test]
        public void BuoyantStep_ColdGasSinks()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            // 四邻烤热（各 Q=200，T=200），自己 T=0 → 冷气下沉。
            world.Heat.SetHeat(3, 4, 200f);
            world.Heat.SetHeat(5, 4, 200f);
            world.Heat.SetHeat(4, 3, 200f);
            world.Heat.SetHeat(4, 5, 200f);

            Assert.IsTrue(ParticleRules.TryBuoyantStep(world, 4, 4, 1f), "冷气应能下沉");
            Assert.AreEqual(atmosphere, world.GetCell(4, 3).typeId, "冷气下移一格");
        }

        /// <summary>死区：与邻居温差在 epsilon 内不动。</summary>
        [Test]
        public void BuoyantStep_WithinEpsilon_Stays()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Heat.SetHeat(4, 4, 0.5f); // T=0.5，四邻 0，差 0.5 < epsilon 1

            Assert.IsFalse(ParticleRules.TryBuoyantStep(world, 4, 4, 1f), "温差在死区内不应动");
            Assert.AreEqual(atmosphere, world.GetCell(4, 4).typeId);
        }

        /// <summary>水平热压流：冷空气流向更暖的一侧（低空海风的微观规则）。</summary>
        [Test]
        public void ThermalDrift_FlowsTowardWarmerSide()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Set(5, 4, atmosphere);
            world.Heat.SetHeat(5, 4, 100f); // 右侧暖

            Assert.IsTrue(ParticleRules.TryThermalDrift(world, 4, 4, 1f), "应流向暖侧");
            Assert.AreEqual(atmosphere, world.GetCell(5, 4).typeId);
        }

        // ==================== 湿度场：蒸发、输送、降水、守恒 ====================

        /// <summary>
        /// 水到沸点蒸发：本格变成大气，**附近（3×3 邻域）的湿度被抬升**（总量一个水格），
        /// 且蒸发**带走潜热**（<see cref="HumidityField.LatentHeat"/>，随温度线性）使本格变冷。
        /// </summary>
        [Test]
        public void Water_Evaporates_WhenAtBoilingPoint_RaisingNearbyHumidity()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Heat.Conductivity = 0f;    // 本测试只看相变
            world.Humidity.Diffusion = 0f;   // 关掉输送，只看"蒸发这一刻"播撒了多少
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0f;
            world.Set(4, 4, ParticleIds.Water);
            float capacity = ParticleRegistry.Types[ParticleIds.Water].Info.HeatCapacity;
            world.Heat.SetHeat(4, 4, WaterParticle.BoilingPoint * capacity); // T = 沸点

            world.StepLogic();

            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(4, 4).typeId, "水到沸点应变成大气");
            Assert.AreEqual(HumidityField.Unit, world.Humidity.Sum(), 1e-3f, "一个水格应蒸发出一个水格的湿度");
            Assert.Greater(world.Humidity.Get(5, 4), 0f, "邻近格子被提高（附近的湿度）");
            Assert.GreaterOrEqual(world.Humidity.Get(5, 4), HumidityField.VisibleThreshold,
                "沸腾的水汽集中在原地，必须**看得见**（这正是「水蒸气只在沸腾时出现」里「出现」的那一半）");
            Assert.AreEqual(WaterParticle.BoilingPoint * capacity - HumidityField.LatentHeat(WaterParticle.BoilingPoint),
                world.Heat.GetHeat(4, 4), 1e-3f, "相变带走潜热（蒸发冷却，随温度线性）");
        }

        /// <summary>沸点以下的水不蒸发，照常下落，也不产生湿度。</summary>
        [Test]
        public void Water_BelowBoiling_DoesNotEvaporate()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Set(4, 4, ParticleIds.Water);
            world.Heat.SetHeat(4, 4, 4f); // T = 1，远低于沸点

            world.StepLogic();

            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 3).typeId, "水正常下落");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-3f, "不产生湿度");
        }

        /// <summary>
        /// 慢蒸发**不可见**、且只在液面发生：一整盆水保持在慢蒸发温区（20 ≤ T &lt; 80）跑够久，
        /// 头顶那片（很小、又关掉输送的）空气也不得被湿度染到可见阈值；水里也不得出现气泡。
        /// 天空刻意做小、输送全关，好让"累积"成为唯一变数——这正是 <c>HumidityField.InvisibleSpreadCap</c> 的把关点：
        /// 没有它，反复慢蒸发会把空气越攒越湿，很快就现出白汽。
        /// </summary>
        [Test]
        public void SlowEvaporation_IsInvisible_AndHappensOnlyAtTheSurface()
        {
            const int width = 40;
            const int height = 34; // 水 y=0..23，空气 y=24..33（天很小，累积很快）
            ParticleWorld world = new ParticleWorld(width, height, 7u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Light.Enabled = false;
            world.Humidity.Diffusion = 0f;
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0f; // 关掉一切输送：只看"慢蒸发会不会攒出可见水汽"

            float capacity = ParticleRegistry.Types[ParticleIds.Water].Info.HeatCapacity;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (y < 24)
                    {
                        world.Set(x, y, ParticleIds.Water);
                        world.Heat.SetHeat(x, y, 30f * capacity); // T = 30，慢蒸发温区
                    }
                    else
                    {
                        world.Set(x, y, ParticleIds.Atmosphere);
                    }
                }
            }

            for (int t = 0; t < 8000; t++)
            {
                world.StepLogic();
            }

            for (int i = 0; i < world.CellCount; i++)
            {
                ushort id = world.Cells[i].typeId;
                if (id == ParticleRegistry.AirTypeId || id == ParticleIds.Atmosphere)
                {
                    Assert.Less(world.Humidity.GetAt(i), HumidityField.VisibleThreshold,
                        "慢蒸发不得产生可见水汽（下标 " + i + "）");
                }
            }

            for (int x = 0; x < width; x++)
            {
                bool sawGas = false;
                for (int y = 0; y < height; y++)
                {
                    ushort id = world.GetCell(x, y).typeId;
                    if (id == ParticleIds.Atmosphere)
                    {
                        sawGas = true;
                    }
                    else if (id == ParticleIds.Water)
                    {
                        Assert.IsFalse(sawGas, "水下方不得出现气泡（慢蒸发只在液面，列 " + x + "）");
                    }
                }
            }
        }

        /// <summary>
        /// 水平方向：湿度**由冷处迁往热处**（同一高度）。这是需求里"同高度倾向于从冷的地方迁移至热的地方"。
        /// </summary>
        [Test]
        public void Humidity_MigratesFromColdToHot_Horizontally()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(4, 4, atmosphere);
            world.Set(5, 4, atmosphere);
            world.Humidity.Diffusion = 0f;      // 关掉扩散，只留温度定向
            world.Humidity.RiseBias = 0f;
            world.Humidity.HorizontalBias = 0.1f;
            world.Humidity.Set(4, 4, 1f);       // 左边湿
            world.Heat.SetHeat(5, 4, 100f);     // 右边热

            world.Humidity.Step(world);         // 直接把湿度输送跑一 tick

            Assert.Greater(world.Humidity.Get(5, 4), 0f, "湿度应向更热的一侧迁移");
            Assert.Less(world.Humidity.Get(4, 4), 1f, "冷侧湿度下降");
        }

        /// <summary>
        /// 竖直方向：**温度越高的格子，湿度越倾向于向上传播**。这是水汽被抬升、云能出现在高空的动力。
        /// </summary>
        [Test]
        public void Humidity_RisesFromHotterCells_Upward()
        {
            ParticleWorld world = new ParticleWorld(1, 3, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(0, 0, atmosphere);
            world.Set(0, 1, atmosphere);
            world.Set(0, 2, atmosphere);
            world.Humidity.Diffusion = 0f;
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0.5f;
            world.Humidity.Set(0, 1, 0.15f);    // 中层湿（低于 RiseStopHumidity，故仍能上升）
            world.Heat.SetHeat(0, 1, 100f);     // 且中层热 → 湿度应向上走（归一化尺度 100）
            world.Heat.SetHeat(0, 2, 0f);       // 上层冷
            world.Heat.SetHeat(0, 0, 0f);       // 下层也冷：证明"下面冷"不会把湿度吸下来

            world.Humidity.Step(world);

            Assert.Greater(world.Humidity.Get(0, 2), 0f, "更热的格子应把湿度往上送");
            Assert.AreEqual(0f, world.Humidity.Get(0, 0), 1e-4f, "下层的冷格子不该把湿度吸下来（竖直是向上平流，不是向热迁移）");
            Assert.Less(world.Humidity.Get(0, 1), 0.15f, "原格湿度下降");
        }

        /// <summary>
        /// 只有非固非液的格持有湿度：低温饱和空气降雨后本格变成水（液体），
        /// 剩余的湿度被推给周围的气体邻居（不丢质量，也不让液体持有湿度）。
        /// </summary>
        [Test]
        public void Rain_CondensesSaturatedColdAir_IntoWater_AndLiquidHoldsNoHumidity()
        {
            ParticleWorld world = new ParticleWorld(8, 8, 7u);
            world.Heat.Conductivity = 0f;
            world.Humidity.Diffusion = 0f;
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0f;
            world.Set(4, 4, ParticleIds.Atmosphere);
            world.Set(3, 4, ParticleIds.Atmosphere);   // 留给残余湿度的气体邻居
            world.Humidity.Set(4, 4, 3f);              // 很湿
            world.Heat.SetHeat(4, 4, 5f);              // 冷：T = 5/(1+3) = 1.25 ≤ 露点

            float before = TotalWater(world);
            for (int i = 0; i < 200 && CountType(world, ParticleIds.Water) == 0; i++)
            {
                world.StepLogic();
            }

            Assert.Greater(CountType(world, ParticleIds.Water), 0, "饱和的冷空气应凝结成雨");
            Assert.AreEqual(before, TotalWater(world), 1e-2f, "水总量守恒（降雨把湿度 1:1 搬成水）");

            // 不变量：固/液格永远不持有湿度（降雨时残余水汽被推给气体邻居）。
            for (int i = 0; i < world.CellCount; i++)
            {
                ushort id = world.Cells[i].typeId;
                bool nonGas = id != ParticleRegistry.AirTypeId
                    && (id >= world.Types.Length || world.Types[id] == null
                        || (world.Types[id].Info.Tags & ParticleTags.Gas) == 0);
                if (nonGas)
                {
                    Assert.AreEqual(0f, world.Humidity.GetAt(i), 1e-4f, "固/液格不得持有湿度（下标 " + i + "）");
                }
            }
        }

        /// <summary>湿度随风平移：湿度**由空气携带**，空气被 Swap 搬走时湿度跟着走（云飘在天上）。</summary>
        [Test]
        public void Humidity_IsAdvectedByTheAir_WhenTheAirMoves()
        {
            ParticleWorld world = new ParticleWorld(8, 1, 7u);
            world.Set(3, 0, ParticleIds.Atmosphere);
            world.Humidity.Set(3, 0, 1f);

            world.Swap(3, 0, 5, 0);

            Assert.AreEqual(1f, world.Humidity.Get(5, 0), 1e-4f, "湿度应随空气一起搬到新格");
            Assert.AreEqual(0f, world.Humidity.Get(3, 0), 1e-4f, "原格不该留下湿度");
        }

        /// <summary>
        /// 水循环端到端守恒：蒸发/输送/降水全程 1:1，
        /// 「水格数 + 湿度总量」在整段模拟里不变（湿度场内部只是搬运，不产不灭）。
        /// </summary>
        [Test]
        public void WaterCycle_ConservesTotalWater()
        {
            ParticleWorld world = new ParticleWorld(24, 32, 20261004u);
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 24; x++)
                {
                    world.Set(x, y, y <= 1 ? ParticleIds.Stone : ParticleIds.Atmosphere);
                }
            }

            for (int y = 2; y <= 5; y++)
            {
                for (int x = 2; x < 22; x++)
                {
                    world.Set(x, y, ParticleIds.Water);
                }
            }

            world.Heat.Conductivity = 0.24f;
            world.Heat.VoidHeatLoss = 0.12f;
            world.Heat.RadiativeLoss = 0.05f;
            world.Heat.AirThermalEmissivity = 0.1f;
            world.Light.Enabled = true;
            world.Light.Intensity = 10f;
            world.Light.LitTicks = int.MaxValue;
            world.Light.DarkTicks = 0;

            float before = TotalWater(world);
            for (int t = 0; t < 4000; t++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(before, TotalWater(world), 1e-2f, "水总量在水循环里必须守恒（1:1 相变，无源无汇）");
        }

        /// <summary>湿度场内部的输送（扩散 + 温度定向）也严格守恒——只搬运，不产不灭。</summary>
        [Test]
        public void HumidityTransport_ConservesTotal()
        {
            ParticleWorld world = new ParticleWorld(16, 16, 5u);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                    world.Heat.SetHeat(x, y, (x * 7 + y * 13) % 90); // 造一个温度场，把两条定向项都激活
                }
            }

            world.Humidity.Set(8, 8, 5f);
            world.Humidity.Set(3, 12, 2f);
            float before = TotalWater(world);   // 水总量 = 水格 + 湿度（降雨会把湿度搬成水，故必须一起算）

            for (int t = 0; t < 300; t++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(before, TotalWater(world), 1e-2f, "湿度输送必须守恒（只搬运，不产不灭）");
        }

        /// <summary>生产注册路径：大气回填出非 0 id、带 Gas 标签、非静态。</summary>
        [Test]
        public void ProductionRegistration_BackfillsGasTaggedId()
        {
            Assert.AreNotEqual(ParticleRegistry.AirTypeId, ParticleIds.Atmosphere);

            ParticleType atmosphere = ParticleRegistry.Types[ParticleIds.Atmosphere];
            Assert.IsTrue((atmosphere.Info.Tags & ParticleTags.Gas) != 0, "大气必须带 Gas 标签");
            Assert.IsFalse(atmosphere.Info.IsStatic, "大气不是静态类型");
            Assert.AreEqual(1f, atmosphere.Info.HeatCapacity,
                "大气热容必须等于 HeatMap.AirHeatCapacity，空气格与大气粒子温度口径才一致");
        }

        /// <summary>透明粒子契约：调色板 alpha == 0 的格（大气）在像素缓冲里显示为背景色，与空气格一致。</summary>
        [Test]
        public void TransparentPalette_RendersAsBackground()
        {
            ParticleWorld world = new ParticleWorld(4, 4, 7u);
            world.Set(1, 1, ParticleIds.Atmosphere);

            var background = new RenderColor(9, 8, 7, 255);
            var pixels = new RenderColor[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background);

            Assert.AreEqual(background, pixels[world.Index(1, 1)], "透明大气格应显示背景色");
            Assert.AreEqual(background, pixels[world.Index(2, 2)], "空气格本来就是背景色（对照）");
        }

        /// <summary>云就是"很湿的空气"：湿度足够高时像素被染白，湿度的深浅给出云的浓淡。</summary>
        [Test]
        public void Cloud_HumidityTintsThePixelWhiteer()
        {
            ParticleWorld world = new ParticleWorld(4, 1, 7u);
            world.Set(0, 0, ParticleIds.Atmosphere);
            world.Set(1, 0, ParticleIds.Atmosphere);
            world.Set(2, 0, ParticleIds.Atmosphere);
            world.Humidity.Set(1, 0, HumidityField.RainThreshold);              // 满湿 → 纯白
            world.Humidity.Set(2, 0, HumidityField.VisibleThreshold * 1.5f);    // 淡云

            var background = new RenderColor(9, 8, 7, 255);
            var pixels = new RenderColor[world.CellCount];
            ParticlePixelBuffer.Fill(world, pixels, background);

            Assert.AreEqual(background, pixels[0], "干燥空气仍是背景色");
            Assert.Greater(pixels[1].r, background.r, "满湿的格子被染白");
            Assert.Greater(pixels[1].r, pixels[2].r, "越湿越白");
            Assert.Greater(pixels[2].r, background.r, "淡云也比背景亮");
        }

        /// <summary>云遮光：每列的云层厚度（该列湿度之和）越大，传到该列地表的能量越少（Beer–Lambert）。</summary>
        [Test]
        public void Light_CloudCoverAttenuatesTheHeatReachingTheSurface()
        {
            ParticleWorld clear = LightingColumn(0f);
            ParticleWorld thin = LightingColumn(0.25f);
            ParticleWorld thick = LightingColumn(1f);

            clear.StepLogic();
            thin.StepLogic();
            thick.StepLogic();

            float cloudless = clear.Heat.GetHeat(0, 0);
            float thinHeat = thin.Heat.GetHeat(0, 0);
            float thickHeat = thick.Heat.GetHeat(0, 0);

            Assert.AreEqual(ColumnLightIntensity, cloudless, 1e-3f, "无云时地表拿满光照");
            Assert.Less(thinHeat, cloudless, "有云就比无云弱");
            Assert.Less(thickHeat, thinHeat, "云越厚，传到地表的光照越弱");

            float expected = ColumnLightIntensity
                * (float)System.Math.Exp(-LightController.DefaultCloudExtinction * 0.5f);
            Assert.AreEqual(expected, thinHeat, 1e-3f, "透过率 = exp(-CloudExtinction × 列湿度总量)");
            Assert.AreEqual(0.5f, thin.Light.GetCloudThickness(0), 1e-3f, "云层厚度 = 该列湿度之和");
        }

        /// <summary>云遮光是**逐列**的：一列有云不会连累旁边那列（云层厚度按列维护，不是全局平均）。</summary>
        [Test]
        public void Light_CloudCoverIsPerColumn()
        {
            ParticleType[] types = TestTables.Build(new AtmosphereParticle(), new TestStaticParticle());
            var world = new ParticleWorld(2, 3, 7u, types);
            for (int x = 0; x < 2; x++)
            {
                world.Set(x, 0, 2); // 地表
                world.Set(x, 1, 1); // 空气
                world.Set(x, 2, 1);
            }

            world.Humidity.Set(1, 1, 0.25f); // 只有右列有云
            world.Humidity.Set(1, 2, 0.25f);
            IsolateLighting(world);

            world.StepLogic();

            Assert.AreEqual(ColumnLightIntensity, world.Heat.GetHeat(0, 0), 1e-3f, "无云的那一列照常拿满光照");
            Assert.Less(world.Heat.GetHeat(1, 0), ColumnLightIntensity, "有云的那一列地表被削弱");
            Assert.AreEqual(0f, world.Light.GetCloudThickness(0), 1e-3f, "左列无云");
            Assert.AreEqual(0.5f, world.Light.GetCloudThickness(1), 1e-3f, "右列云层厚度 = 0.25 + 0.25");
        }

        // ==================== 审计回归：饱和曲线（C2）、份额门控（B2）、RiseStop 门控（F1） ====================

        /// <summary>饱和湿度曲线：露点处恰为 <see cref="HumidityField.RainThreshold"/>，且随温度按每 10° 倍增/减半。</summary>
        [Test]
        public void SaturationHumidity_AnchorsAtDewPoint_AndHalvesEveryTenDegrees()
        {
            Assert.AreEqual(HumidityField.RainThreshold,
                HumidityField.SaturationHumidity(HumidityField.DewPoint), 1e-4f, "露点处饱和湿度 = RainThreshold");
            Assert.Less(HumidityField.SaturationHumidity(HumidityField.DewPoint - 10f), HumidityField.RainThreshold,
                "更冷 → 门槛更低（更易凝结）");
            Assert.Greater(HumidityField.SaturationHumidity(HumidityField.DewPoint + 10f), HumidityField.RainThreshold,
                "更热 → 门槛更高（更难凝结）");
            Assert.AreEqual(HumidityField.SaturationHumidity(0f) * 2f, HumidityField.SaturationHumidity(10f), 1e-4f,
                "每升高 10° 饱和湿度翻倍");
        }

        /// <summary>
        /// 审计 B2：份额门控。9×9 石墙口袋只剩 2 个气体格时，半径 4 的单格份额 = 1/2 = 0.5，
        /// 远超 <see cref="HumidityField.InvisibleSpreadMaxShare"/>（0.04）→ 返回 0 且**一格都不播撒**；
        /// 传 maxShare = 0（沸腾的可见路径）时不受限、正常播撒。
        /// </summary>
        [Test]
        public void SpreadUnitIntoAir_RejectsWhenShareExceedsMax()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 7u);
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    world.Set(x, y, ParticleIds.Stone);
                }
            }

            world.Set(4, 2, ParticleIds.Atmosphere);
            world.Set(3, 2, ParticleIds.Atmosphere);

            int gated = world.Humidity.SpreadUnitIntoAir(
                world, 4, 1, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadMaxShare);

            Assert.AreEqual(0, gated, "接收格仅 2、份额 0.5 超限：必须返回 0");
            Assert.AreEqual(0f, world.Humidity.Sum(), 1e-4f, "返回 0 时不得有任何一格接到水汽");

            int unlimited = world.Humidity.SpreadUnitIntoAir(
                world, 4, 1, HumidityField.InvisibleSpreadRadius, 0f);

            Assert.AreEqual(2, unlimited, "maxShare = 0 表示不限（可见路径）");
            Assert.AreEqual(HumidityField.Unit, world.Humidity.Sum(), 1e-3f, "放行时总量仍是一个水格");
        }

        /// <summary>
        /// 审计 B2 行为回归：同样的石墙口袋里的水长期处于慢蒸发温区，份额超限使每次慢蒸发都失败——
        /// 水必须**原样保留**，口袋里也不得出现可见水汽（旧实现会把 0.5 直接摊出可见白汽并当帧可降雨）。
        /// </summary>
        [Test]
        public void SlowEvaporation_InStonePocket_RetainsWater()
        {
            ParticleWorld world = new ParticleWorld(9, 9, 7u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Light.Enabled = false;
            world.Humidity.Diffusion = 0f;
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0f;
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    world.Set(x, y, ParticleIds.Stone);
                }
            }

            world.Set(4, 1, ParticleIds.Water);
            world.Set(4, 2, ParticleIds.Atmosphere); // 唯一的两个气体格（正上方及其左侧）
            world.Set(3, 2, ParticleIds.Atmosphere);
            float capacity = ParticleRegistry.Types[ParticleIds.Water].Info.HeatCapacity;
            world.Heat.SetHeat(4, 1, 30f * capacity); // T = 30，慢蒸发温区且恒定（导热已关）

            for (int t = 0; t < 4000; t++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(1, CountType(world, ParticleIds.Water), "份额超限 → 不蒸发，水必须保留");
            for (int i = 0; i < world.CellCount; i++)
            {
                if (world.Cells[i].typeId == ParticleIds.Atmosphere)
                {
                    Assert.Less(world.Humidity.GetAt(i), HumidityField.VisibleThreshold,
                        "半封闭口袋里不得凭空出现可见水汽（下标 " + i + "）");
                }
            }
        }

        /// <summary>
        /// 审计 F1：<see cref="HumidityField.RiseStopHumidity"/> 只门控竖直**平流**项。
        /// 一格宽水井里湿度恰好达到门槛时，对称**扩散**项必须照常结算——
        /// 旧实现把整对竖直交换冻死，湿度会永远卡在原地（并渲染成常驻白列）。
        /// </summary>
        [Test]
        public void RiseStop_DoesNotFreezeVerticalDiffusion()
        {
            ParticleWorld world = new ParticleWorld(1, 2, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(0, 0, atmosphere);
            world.Set(0, 1, atmosphere);
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0f;      // 关掉平流，只留扩散
            world.Humidity.Diffusion = 0.1f;
            world.Humidity.Set(0, 0, HumidityField.RiseStopHumidity); // 恰好达到抬升门槛
            world.Heat.SetHeat(0, 0, 0f);
            world.Heat.SetHeat(0, 1, 0f);

            world.Humidity.Step(world);

            Assert.Greater(world.Humidity.Get(0, 1), 0f, "扩散项必须越过 RiseStop 门控（否则整对交换永冻）");
            Assert.AreEqual(HumidityField.RiseStopHumidity, world.Humidity.Sum(), 1e-4f, "越过门控后仍严格守恒");
        }

        /// <summary>
        /// 审计 F1 的另一半：同一门槛下，**平流**项必须被门控——已达抬升浓度的空气不再被温度顶上去
        /// （否则水汽会一路顶到天花板堆成贴顶薄盖）。
        /// </summary>
        [Test]
        public void RiseStop_GatesVerticalAdvection()
        {
            ParticleWorld world = new ParticleWorld(1, 2, 7u, TestTables.Build(new AtmosphereParticle()));
            const ushort atmosphere = 1;
            world.Set(0, 0, atmosphere);
            world.Set(0, 1, atmosphere);
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.Diffusion = 0f;     // 关掉扩散，只留平流
            world.Humidity.RiseBias = 0.5f;
            world.Humidity.Set(0, 0, HumidityField.RiseStopHumidity);
            world.Heat.SetHeat(0, 0, 200f);    // 归一化温度 2.0：不门控时向上流 0.2，会被看见

            world.Humidity.Step(world);

            Assert.AreEqual(0f, world.Humidity.Get(0, 1), 1e-4f, "已达抬升门槛 → 平流被门控，不再上升");
            Assert.AreEqual(HumidityField.RiseStopHumidity, world.Humidity.Get(0, 0), 1e-4f, "湿度原地不动");
        }

        /// <summary><see cref="HumidityField.Step"/> 与类内其它公开方法一致：world 为 null 抛 <see cref="System.ArgumentNullException"/>。</summary>
        [Test]
        public void Step_NullWorld_Throws()
        {
            var field = new HumidityField(2, 2);
            Assert.Throws<System.ArgumentNullException>(() => field.Step(null));
        }

        // ==================== 光照的每列缓存：挡光顶 / 光路湿度增量维护 ====================

        /// <summary>
        /// 挡光顶的抬升 / 下降 / 露出：世界在写入后增量维护每列缓存，施热因此始终落在**当前**最上方的固/液格；
        /// 撤掉上顶会露出下面那层挡光格，整列都变成气体时一格都不受光。
        /// </summary>
        [Test]
        public void Light_ColumnCacheFollowsTheTopBlockerRisingFallingAndBeingRevealed()
        {
            ParticleWorld world = LightWorld(1, 4);

            Assert.AreEqual(ColumnLightIntensity, LightHeatDelta(world, 0, 0), 1e-3f, "光落在唯一的地表格上");

            // 抬顶：在更高处放一块固体，光改打到新顶，旧地表被挡住。
            world.Set(0, 2, 2);
            Assert.AreEqual(2, ReferenceBlockerY(world, 0), "挡光顶抬到 y=2");
            Assert.AreEqual(ColumnLightIntensity, LightHeatDelta(world, 0, 2), 1e-3f, "施热跟到新顶");
            Assert.AreEqual(0f, LightHeatDelta(world, 0, 0), 1e-3f, "被挡在下面的旧地表不再受光");

            // 降顶：撤掉上面那块 → 露出下面的挡光格。
            world.Clear(0, 2);
            Assert.AreEqual(0, ReferenceBlockerY(world, 0), "挡光顶回到 y=0");
            Assert.AreEqual(ColumnLightIntensity, LightHeatDelta(world, 0, 0), 1e-3f, "撤掉上顶后露出下面的地表");
            Assert.AreEqual(0f, LightHeatDelta(world, 0, 2), 1e-3f, "原顶位置已是气体，不受光");

            // 整列都是气体：没有挡光格，一格都不注入热量（与旧行为一致）。
            world.Clear(0, 0);
            Assert.AreEqual(-1, ReferenceBlockerY(world, 0));
            float before = ColumnHeatTotal(world, 0);
            world.StepLogic();
            Assert.AreEqual(before, ColumnHeatTotal(world, 0), 1e-3f, "全列无挡光格：光不落在任何格上");
            AssertColumnThicknessMatchesLightPath(world);
        }

        /// <summary>
        /// 同列内的类型换位 / 换型：<see cref="ParticleWorld.Swap"/> 把挡光格在列内搬家、<see cref="ParticleWorld.SetPhase"/>
        /// 把最上面的气体就地变成固体，两者都必须让缓存跟着走（挡光顶与光路湿度）。
        /// </summary>
        [Test]
        public void Light_ColumnCacheFollowsTypeSwapsWithinTheColumn()
        {
            ParticleWorld world = LightWorld(1, 4);
            world.Humidity.Set(0, 3, 0.25f); // 光路（顶格）上的云

            Assert.AreEqual(0f, world.Light.GetCloudThickness(0), 1e-4f,
                "尚未挂到世界（没跑过逻辑帧）时恒为 0 的旧契约");

            world.StepLogic(); // 建缓存
            float attenuated = ColumnLightIntensity
                * (float)System.Math.Exp(-LightController.DefaultCloudExtinction * 0.25f);
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(0), 1e-4f, "光路上的湿度计入列云厚");
            Assert.AreEqual(attenuated, LightHeatDelta(world, 0, 0), 1e-3f, "受光处按光路云量衰减");

            // 同列换位：固体从 y=0 搬到 y=1（原位换成气体）。光路湿度没动，挡光顶跟着物质上去。
            world.Swap(0, 0, 0, 1);
            Assert.AreEqual(1, ReferenceBlockerY(world, 0), "挡光顶跟物质走");
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(0), 1e-4f, "云还在光路上");
            Assert.AreEqual(attenuated, LightHeatDelta(world, 0, 1), 1e-3f, "光打在新位置");
            Assert.AreEqual(0f, LightHeatDelta(world, 0, 0), 1e-3f, "原位只剩气体，不受光");

            // 换型：最上面的气体就地变固体（相变），顶升到 y=3、其水汽被推给下方气体（不再在光路上）。
            world.SetPhase(0, 3, 2);
            Assert.AreEqual(3, ReferenceBlockerY(world, 0), "相变后的固体成为新顶");
            Assert.AreEqual(0f, world.Light.GetCloudThickness(0), 1e-4f, "水汽已退出光路（新顶之上无湿度）");
            Assert.AreEqual(ColumnLightIntensity, LightHeatDelta(world, 0, 3), 1e-3f, "新顶之上无云，拿满光照");
            AssertColumnThicknessMatchesLightPath(world);
        }

        /// <summary>
        /// 只算**光路**：顶棚（中间那层挡光格）之下的洞穴湿度不遮光，顶棚之上的云才遮光。
        /// </summary>
        [Test]
        public void Light_CloudThicknessCountsOnlyTheLightPathAboveTheBlocker()
        {
            ParticleWorld world = LightWorld(1, 4);
            world.Set(0, 2, 2);                       // 顶棚：y=1 因此成为洞穴
            world.Humidity.Set(0, 1, 0.25f);          // 洞穴里的湿气
            world.Humidity.Set(0, 3, 0.25f);          // 顶棚之上的云

            world.StepLogic();

            Assert.AreEqual(2, ReferenceBlockerY(world, 0), "光打在顶棚上");
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(0), 1e-4f, "洞穴湿度不计入光路");
            float expected = ColumnLightIntensity
                * (float)System.Math.Exp(-LightController.DefaultCloudExtinction * 0.25f);
            Assert.AreEqual(expected, LightHeatDelta(world, 0, 2), 1e-3f, "顶棚只被自己上面的云衰减");
            Assert.AreEqual(0f, LightHeatDelta(world, 0, 0), 1e-3f, "洞穴下的地表不受光");
        }

        /// <summary>
        /// 湿度的位置口径：光路（挡光顶**之上**）的改动进出列云厚；挡光顶自身及其下方（洞穴 / 地下水）的改动不影响。
        /// </summary>
        [Test]
        public void Light_ColumnCacheIgnoresHumidityAtOrBelowTheBlocker()
        {
            ParticleWorld world = LightWorld(1, 5); // y=0 是地表，y=1..4 是大气
            world.StepLogic();                     // 建缓存（挡光顶 = y=0）

            world.Humidity.Set(0, 3, 0.25f);       // 光路上
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(0), 1e-4f, "光路上的湿度计入");

            world.Humidity.Set(0, 0, 0.25f);       // 挡光格自身：口径上是「顶」而非「顶之上」
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(0), 1e-4f, "挡光格自身的湿度不遮光");

            world.Humidity.Set(0, 1, 0.25f);       // 顶之上（低处但仍在光路上）
            Assert.AreEqual(0.5f, world.Light.GetCloudThickness(0), 1e-4f, "顶之上的湿度都计入");
            AssertColumnThicknessMatchesLightPath(world);

            // 抬顶到 y=4（那格没有湿度，不留疏散歧义）：y1 / y3 的湿度全部退出光路。
            world.Set(0, 4, 2);
            Assert.AreEqual(4, ReferenceBlockerY(world, 0), "挡光顶抬到 y=4");
            Assert.AreEqual(0f, world.Light.GetCloudThickness(0), 1e-4f, "新顶之上没有湿度");
            AssertColumnThicknessMatchesLightPath(world);

            // 降顶回 y=0：中间那几格的湿度重新回到光路上，逐格精确补回。
            world.Clear(0, 4);
            Assert.AreEqual(0, ReferenceBlockerY(world, 0), "挡光顶回落地表");
            Assert.AreEqual(0.5f, world.Light.GetCloudThickness(0), 1e-4f, "y=1 与 y=3 的湿度重新计为云");
            AssertColumnThicknessMatchesLightPath(world);
        }

        /// <summary>
        /// 跨列换位：云跟着气体搬家、挡光格搬到邻列，两列的缓存都要跟着变（含挡光格参与换位时对那一列的重扫）。
        /// </summary>
        [Test]
        public void Light_ColumnCacheFollowsSwapsAcrossColumns()
        {
            ParticleWorld world = LightWorld(2, 3);
            world.Humidity.Set(0, 2, 0.25f); // 左列顶上的云
            world.StepLogic();

            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(0), 1e-4f);
            Assert.AreEqual(0f, world.Light.GetCloudThickness(1), 1e-4f, "右列无云");

            // 两格都是气体：云随风整体搬到右列。
            world.Swap(0, 2, 1, 2);
            Assert.AreEqual(0f, world.Light.GetCloudThickness(0), 1e-4f, "云跟着气体走了");
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(1), 1e-4f, "落在右列顶上");
            AssertColumnThicknessMatchesLightPath(world);

            // 挡光格跨界换位：左列在 y=1 放一块固体，再与右列 y=1 的空气互换 ——
            // 固体搬到右列（右列顶抬到 y=1），左列拿回空气（顶回落到地表）；云一直在右列光路的 y=2 上。
            world.Set(0, 1, 2);
            world.StepLogic();
            Assert.AreEqual(1, ReferenceBlockerY(world, 0), "左列加了固体，顶抬到 y=1");
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(1), 1e-4f, "云在右列光路上");

            world.Swap(0, 1, 1, 1);
            Assert.AreEqual(0, ReferenceBlockerY(world, 0), "左列顶回落到地表");
            Assert.AreEqual(1, ReferenceBlockerY(world, 1), "右列顶抬到换来的固体");
            Assert.AreEqual(0f, world.Light.GetCloudThickness(0), 1e-4f, "左列顶之上没有湿度");
            Assert.AreEqual(0.25f, world.Light.GetCloudThickness(1), 1e-4f, "云仍在新顶之上，照旧遮光");
            AssertColumnThicknessMatchesLightPath(world);
        }

        /// <summary>
        /// 湿度输送与降雨也必须把改动报到缓存：多逻辑帧后逐列比对公开读数与独立 oracle（这是缓存契约的核心）。
        /// </summary>
        [Test]
        public void Light_ColumnCacheStaysExactThroughHumidityTransportAndRain()
        {
            ParticleWorld world = new ParticleWorld(6, 6, 7u);
            for (int x = 0; x < 6; x++)
            {
                world.Set(x, 0, ParticleIds.Stone); // 不透光的地板
                for (int y = 1; y < 6; y++)
                {
                    world.Set(x, y, ParticleIds.Atmosphere);
                }
            }

            // 一片水汽（总量 < 1，避免首帧就碰并成雨；输送与抬升会把它摊开、部分上冻降雨）。
            for (int x = 1; x <= 3; x++)
            {
                for (int y = 4; y <= 5; y++)
                {
                    world.Humidity.Set(x, y, 0.2f);
                }
            }

            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Heat.RadiativeLoss = 0f;
            world.Heat.AirThermalConductivity = 0f;
            world.Light.Enabled = true;
            world.Light.LitTicks = 100000;
            world.Light.DarkTicks = 0;
            world.Light.Intensity = ColumnLightIntensity;

            for (int tick = 0; tick < 40; tick++)
            {
                world.StepLogic();
                AssertColumnThicknessMatchesLightPath(world);
            }
        }

        private const float ColumnLightIntensity = 5f;

        /// <summary>
        /// 受光测试世界：每列底格是静态地表（<see cref="TestStaticParticle"/>，光照吸收率 1），上面全是大气；
        /// 光照打开、其余一切热量 / 湿度输运关掉，断言因此只反映「光照 × 光路云量」。
        /// </summary>
        private static ParticleWorld LightWorld(int width, int height)
        {
            ParticleType[] types = TestTables.Build(new AtmosphereParticle(), new TestStaticParticle());
            var world = new ParticleWorld(width, height, 7u, types);
            for (int x = 0; x < width; x++)
            {
                world.Set(x, 0, 2); // 地表
                for (int y = 1; y < height; y++)
                {
                    world.Set(x, y, 1); // 大气
                }
            }

            IsolateLighting(world);
            return world;
        }

        /// <summary>1 列 3 高的受光世界：底格是地表（静态固体、热容 1），上面两格空气；两格空气各含湿度 humidityPerAirCell。</summary>
        private static ParticleWorld LightingColumn(float humidityPerAirCell)
        {
            ParticleWorld world = LightWorld(1, 3);
            world.Humidity.Set(0, 1, humidityPerAirCell);
            world.Humidity.Set(0, 2, humidityPerAirCell);
            return world;
        }

        /// <summary>参考真值（**独立 oracle**）：按公开口径逐格扫描出的该列挡光顶（Solid | Liquid）；-1 = 全列无挡光格。</summary>
        private static int ReferenceBlockerY(ParticleWorld world, int x)
        {
            for (int y = world.Height - 1; y >= 0; y--)
            {
                if (ParticleRules.BlocksLight(world, x, y))
                {
                    return y;
                }
            }

            return -1;
        }

        /// <summary>参考真值：该列**光路**（挡光顶之上）的湿度总和。</summary>
        private static float ReferenceCloudThickness(ParticleWorld world, int x)
        {
            float thickness = 0f;
            for (int y = world.Height - 1; y > ReferenceBlockerY(world, x); y--)
            {
                thickness += world.Humidity.Get(x, y);
            }

            return thickness;
        }

        /// <summary>逐列比对公开读数 <see cref="LightController.GetCloudThickness"/> 与独立 oracle——缓存的唯一契约就是这个口径。</summary>
        private static void AssertColumnThicknessMatchesLightPath(ParticleWorld world)
        {
            for (int x = 0; x < world.Width; x++)
            {
                Assert.AreEqual(ReferenceCloudThickness(world, x), world.Light.GetCloudThickness(x), 1e-4f,
                    $"第 {x} 列的列云厚必须等于光路上的湿度总和");
            }
        }

        /// <summary>本刻该格**由光照新注入**的热量 = StepLogic 前后的 Q 差（其它热输运已在 <see cref="IsolateLighting"/> 里关掉）。</summary>
        private static float LightHeatDelta(ParticleWorld world, int x, int y)
        {
            float before = world.Heat.GetHeat(x, y);
            world.StepLogic();
            return world.Heat.GetHeat(x, y) - before;
        }

        /// <summary>整列热量之和（用于断言「全列都是气体 ⇒ 一格都不受光」）。</summary>
        private static float ColumnHeatTotal(ParticleWorld world, int x)
        {
            float total = 0f;
            for (int y = 0; y < world.Height; y++)
            {
                total += world.Heat.GetHeat(x, y);
            }

            return total;
        }

        /// <summary>关掉除光照以外的一切热量/湿度输运，让断言只反映「光照 × 云层透过率」。</summary>
        private static void IsolateLighting(ParticleWorld world)
        {
            HeatMap heat = world.Heat;
            heat.Conductivity = 0f;
            heat.VoidHeatLoss = 0f;
            heat.RadiativeLoss = 0f;
            heat.AirThermalConductivity = 0f;
            heat.AdiabaticLapse = 0f; // 气体竖直换位的绝热直减也是一条热输运路径：要单独看光照就必须关掉。
            world.Humidity.Diffusion = 0f;
            world.Humidity.HorizontalBias = 0f;
            world.Humidity.RiseBias = 0f;
            world.Light.Enabled = true;
            world.Light.LitTicks = 100000;
            world.Light.DarkTicks = 0;
            world.Light.Intensity = ColumnLightIntensity;
        }
    }
}
