using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 火粒子的行为契约：接触点燃邻接的草（逐步蔓延直至烧光）、每逻辑帧放热抬升周围温度
    /// （贴着的沙 / 石会被烤热）、**失去附着物会下沉并在空中快速熄灭**、遇到石头 / 沙 / 水 / 湿土
    /// 快速熄灭、遇到湿土先将其烤成干土、每逻辑帧视觉跳动（shade 随机游走）；投放资格（不覆盖实体、
    /// 不浮空、**火焰不算附着物**）；以及生产注册路径（追加注册的 Gas、调色板不透明）。
    /// </summary>
    /// <remarks>
    /// 全部用**生产类型表**（<c>new ParticleWorld(w, h, seed)</c>）：火的点燃目标按
    /// <see cref="ParticleIds.Grass"/>（生产 id）判定，隔离类型表里草不是这个 id，因此不适用——
    /// 与 WaterParticleTests 的蒸发用例同一取舍（跨类型反应依赖 ParticleIds 回填时必须走生产表）。
    /// 随机刻默认 0 ⇒ 草不会自己死，测试完全由固定种子确定。
    /// 概率行为的种子校准：坠落熄灭（种子 9 → 第 7 帧）、烘焙淬熄（种子 6 → 第 29 帧）、
    /// 遇水淬熄（种子 14 → 第 17 帧）、砂石淬熄（种子 2026 → 第 55 帧）用**精确帧断言**；
    /// 帧数已把场上所有随机消耗者（移动原语的 leftFirst 摇骰、受热后的蒸发骰等）计入实际运行结果——
    /// 引擎全程确定性，同代码同种子必然复现。其余概率断言只依赖「若干帧窗口内采样到的值」。
    /// 若日后调整概率常量或随机消耗，只需重新校准这些数字，断言结构不变。
    /// </remarks>
    public sealed class FireParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
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

        /// <summary>
        /// 生产注册路径：RegisterAll 必须回填 <see cref="ParticleIds.Fire"/>（非 0），且它是追加注册的气体：
        /// id 大于现有最后一种（腐殖土）、带 Gas 标签、非 IsStatic（静态类型收不到任何行为钩子）、
        /// 调色板非空且全部不透明（alpha == 0 会被渲染层当透明粒子——火必须可见）。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsAppendedGasFireId()
        {
            Assert.AreNotEqual(0, (int)ParticleIds.Fire, "Fire 必须被回填（非 0）");
            Assert.Greater(
                (int)ParticleIds.Fire, (int)ParticleIds.AquaticPlant,
                "火必须追加在全部已有类型（含水生植物）之后：注册顺序是存档兼容性的一部分");

            ParticleTypeInfo fire = ParticleRegistry.Types[ParticleIds.Fire].Info;
            Assert.IsTrue((fire.Tags & ParticleTags.Gas) != 0, "火应带 Gas 标签");
            Assert.IsFalse(fire.IsStatic, "火必须参与扫描：IsStatic 会跳过全部行为钩子");

            Assert.IsNotNull(fire.Palette, "火焰调色板不能为空");
            Assert.Greater(fire.Palette.Length, 0, "火焰调色板至少一档");
            for (int i = 0; i < fire.Palette.Length; i++)
            {
                Assert.AreNotEqual(
                    0, (int)fire.Palette[i].a,
                    $"火焰调色板第 {i} 档 alpha 为 0：渲染层会把它当透明粒子，火会隐形");
            }
        }

        /// <summary>
        /// 失去附着物的火（空世界里）：不允许浮空燃烧——每逻辑帧下沉一格（气体版下落），并在空中
        /// 以 1/5 每帧的坠落熄灭骰快速熄灭。种子 9 实测校准：第 7 帧熄灭、共下沉 6 格。
        /// </summary>
        [Test]
        public void Fire_Unsupported_FallsThroughAir_ThenExtinguishes()
        {
            var world = new ParticleWorld(16, 16, 9u);
            const int x = 8;
            const int y = 10;
            world.Set(x, y, ParticleIds.Fire);

            int steps = 0;
            while (CountType(world, ParticleIds.Fire) > 0 && steps < 500)
            {
                world.StepLogic();
                steps++;

                if (steps == 1)
                {
                    Assert.AreEqual(
                        (int)ParticleIds.Fire, (int)world.GetCell(x, y - 1).typeId,
                        "失去附着物的火应下沉一格（OnMotion 的 TrySink）");
                }
            }

            Assert.AreEqual(7, steps, "种子校准：坠落中的火第 7 帧被 20% 熄灭骰熄灭");
            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "坠落的火不应留下残火");

            // 下沉过的轨迹（原格到熄灭处）都应恢复空气。
            for (int i = y - 6; i <= y; i++)
            {
                Assert.AreEqual(
                    (int)ParticleIds.Air, (int)world.GetCell(x, i).typeId,
                    $"坠落轨迹上的格 ({x},{i}) 应为空气");
            }
        }

        /// <summary>
        /// 放热：火每逻辑帧向本格注入热量、由世界自动向四邻扩散——火托在**土**上（土不在淬熄集合里）。
        /// 第一帧后四邻温度必大于环境（注入先于熄灭骰，即使当帧熄灭，热也已写入并同帧扩散）；
        /// 随后数帧里火格温度显著升高、四邻被烤到草致死线（50）以上。
        /// </summary>
        [Test]
        public void Fire_RaisesOwnAndNeighbourTemperature()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            const int x = 8;
            const int y = 1;
            world.Set(x, 0, ParticleIds.Soil);
            world.Set(x, y, ParticleIds.Fire);

            world.StepLogic();

            Assert.Greater(world.Heat.GetTemperature(x - 1, y), 0f, "第一帧后左邻应被加热");
            Assert.Greater(world.Heat.GetTemperature(x + 1, y), 0f, "第一帧后右邻应被加热");
            Assert.Greater(world.Heat.GetTemperature(x, y - 1), 0f, "第一帧后下邻应被加热");
            Assert.Greater(world.Heat.GetTemperature(x, y + 1), 0f, "第一帧后上邻应被加热");
            Assert.Greater(world.Heat.GetTemperature(x, y), 50f, "第一帧后火格温度应已显著高于环境");

            // 连续采样：火格在存活期间越来越热；四邻对称，取左邻为代表。
            float maxFireTemperature = 0f;
            float maxNeighbourTemperature = 0f;
            for (int i = 0; i < 12; i++)
            {
                if (world.GetCell(x, y).typeId == ParticleIds.Fire)
                {
                    float own = world.Heat.GetTemperature(x, y);
                    if (own > maxFireTemperature)
                    {
                        maxFireTemperature = own;
                    }
                }

                float neighbour = world.Heat.GetTemperature(x - 1, y);
                if (neighbour > maxNeighbourTemperature)
                {
                    maxNeighbourTemperature = neighbour;
                }

                world.StepLogic();
            }

            Assert.Greater(maxFireTemperature, 100f, "火格温度应远超环境（显著升温）");
            Assert.Greater(maxNeighbourTemperature, 50f, "四邻应被烤到草致死线（50）以上");
        }

        /// <summary>
        /// 自然熄灭：固定种子下**托在土上**、无燃料的火在有限步内自灭，终态本格是**空气**而非腐殖质
        /// （火不带 Organic 标签，腐殖质只属于有机物死亡的世界规则）。
        /// </summary>
        [Test]
        public void Fire_BurnsOut_Naturally_CellBecomesAir()
        {
            var world = new ParticleWorld(16, 16, 1u);
            world.Set(8, 0, ParticleIds.Soil);   // 土托底：有附着物 → 不坠落，走普通熄灭骰
            world.Set(8, 1, ParticleIds.Fire);

            for (int i = 0; i < 2000 && CountType(world, ParticleIds.Fire) > 0; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "火的寿命有限：2000 逻辑帧内必熄灭");
            Assert.AreEqual(
                (int)ParticleIds.Air, (int)world.GetCell(8, 1).typeId,
                "熄灭后本格是空气（火非有机，不会变成腐殖质）");
            Assert.AreEqual((int)ParticleIds.Soil, (int)world.GetCell(8, 0).typeId, "托底的土不受影响");
        }

        /// <summary>
        /// 蔓延：火沿八邻把一片草逐步烧光（有燃料时不摇熄灭骰，邻接的燃料必被逐格吃掉），
        /// 随后全部火自然熄灭。
        /// 断言：过程中火数曾大于 1（确实蔓延开了）；草最终耗尽；火最终耗尽；
        /// 终态全网格只剩空气或腐殖质（草 → 火的 Set 会触发世界「有机死亡 20% 留腐殖质」规则，
        /// 烧过的草地会零星留下腐殖质——这正是允许终态出现腐殖质的原因）。
        /// 种子 2026 在这一布局下的完整燃烧过程已离线模拟校验（约 900 逻辑帧烧尽）。
        /// </summary>
        [Test]
        public void Fire_SpreadsThroughGrass_ThenEverythingBurnsOut()
        {
            var world = new ParticleWorld(32, 16, 2026u);
            for (int y = 1; y <= 6; y++)
            {
                for (int x = 6; x <= 24; x++)
                {
                    world.Set(x, y, ParticleIds.Grass);
                }
            }

            world.Set(5, 3, ParticleIds.Fire);

            int maxFire = 0;
            for (int i = 0; i < 8000; i++)
            {
                int fire = CountType(world, ParticleIds.Fire);
                if (fire > maxFire)
                {
                    maxFire = fire;
                }

                if (fire == 0 && CountType(world, ParticleIds.Grass) == 0)
                {
                    break;
                }

                world.StepLogic();
            }

            Assert.Greater(maxFire, 1, "火应蔓延：过程中火数曾大于 1");
            Assert.AreEqual(0, CountType(world, ParticleIds.Grass), "可燃物（草）应被烧光");
            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "烧光之后火应全部自然熄灭");

            for (int i = 0; i < world.CellCount; i++)
            {
                ushort typeId = world.Cells[i].typeId;
                Assert.IsTrue(
                    typeId == ParticleIds.Air || typeId == ParticleIds.Humus,
                    $"终态只允许空气或腐殖质，却发现 typeId={typeId}（格 {i % world.Width},{i / world.Width}）");
            }
        }

        /// <summary>
        /// 投放资格 ①：**不覆盖 / 不替换任何实体方块或粒子**——草（固）、水（液）、石头（固）
        /// 所在格一律不可投放；实体上方的空气格可以投放。
        /// </summary>
        [Test]
        public void Fire_Placement_DoesNotOverwriteMatter()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Set(5, 5, ParticleIds.Grass);
            world.Set(7, 5, ParticleIds.Water);
            world.Set(9, 5, ParticleIds.Stone);

            Assert.IsFalse(FireParticle.CanPlaceAt(world, 5, 5), "不能覆盖草（实体粒子）");
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 7, 5), "不能覆盖水（实体粒子）");
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 9, 5), "不能覆盖石头（实体方块）");
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 5, 6), "实体上方的空气格可以投放");
        }

        /// <summary>
        /// 投放资格 ②：**不许浮空**——目标格八邻内必须有附着物（固 / 液的一格；**火焰不算附着物**）；
        /// 同时覆盖：大气格（气体）可投放、越界不可投放。
        /// </summary>
        [Test]
        public void Fire_Placement_NeedsAttachment_NoFloating()
        {
            var world = new ParticleWorld(16, 16, 2026u);

            // 开场整场是空气：没有附着物，哪都放不了（先于任何 Set 检查）。
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 8, 8), "八邻全空（浮空）不可投放");

            world.Set(5, 5, ParticleIds.Stone);
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 6, 5), "正交贴着石头可投放");
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 6, 6), "斜贴着石头可投放");
            Assert.IsFalse(
                FireParticle.CanPlaceAt(world, 7, 7), "离附着物两格（浮空）不可投放");

            // 火焰不算附着物：与已有的火相邻（正交 / 斜角）也不可投放——不许火柱链式投放。
            world.Set(8, 8, ParticleIds.Fire);
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 8, 9), "火焰不算附着物：正交火链不可投放");
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 9, 9), "火焰不算附着物：斜向火链不可投放");

            // 大气格（气体）可投放：大气是可替换的环境气。
            world.Set(12, 12, ParticleIds.Atmosphere);
            world.Set(12, 11, ParticleIds.Stone);
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 12, 12), "大气格（有附着物时）可投放");

            Assert.IsFalse(FireParticle.CanPlaceAt(world, -1, 0), "越界不可投放");
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 16, 0), "越界不可投放");
        }

        /// <summary>
        /// 火贴着沙 / 石会被**热传导**烤热（热容小、导热快，几帧就到上百度）；同时火因贴着
        /// 石头 / 沙（非草木实体）走**快熄骰**快速熄灭——「烤热」与「速灭」两个效果并存。
        /// 种子 2026 实测校准：快熄骰第 55 帧命中（此前一直在放热）。
        /// </summary>
        [Test]
        public void Fire_HeatsAdjacentSandAndStone()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Set(7, 0, ParticleIds.Stone);
            world.Set(7, 1, ParticleIds.Stone);   // 石柱（高摩擦，稳定）
            world.Set(8, 0, ParticleIds.Sand);
            world.Set(9, 0, ParticleIds.Sand);    // 底行的沙（不会移动）
            world.Set(8, 1, ParticleIds.Fire);    // 火：正下方是沙、左方是石

            for (int i = 0; i < 6; i++)
            {
                world.StepLogic();
            }

            Assert.Greater(world.Heat.GetTemperature(8, 0), 50f, "火正下方的沙应被热传导烤热");
            Assert.Greater(world.Heat.GetTemperature(7, 1), 50f, "火左方的石应被热传导烤热");

            int steps = 6;
            while (CountType(world, ParticleIds.Fire) > 0 && steps < 400)
            {
                world.StepLogic();
                steps++;
            }

            Assert.AreEqual(55, steps, "种子校准：贴着石头 / 沙的火第 55 帧被快熄骰熄灭");
        }

        /// <summary>
        /// 遇到湿土：**先把它烤成干土**（水汽走湿度场的不可见播撒，水总量守恒），同时火被加速熄灭。
        /// 种子 6 实测校准：第 1 帧烤干第一格湿土后，局部空气即达不可见蒸发上限（InvisibleSpreadCap）、
        /// 后续两格被节流保留（守恒优先于干化速度）；火第 29 帧熄灭（快熄骰 + 普通熄灭骰的实测帧数）。
        /// </summary>
        [Test]
        public void Fire_DriesAdjacentWetSoil_ThenIsQuenched()
        {
            var world = new ParticleWorld(16, 16, 6u);
            world.Set(7, 0, ParticleIds.WetSoil);
            world.Set(8, 0, ParticleIds.WetSoil);
            world.Set(9, 0, ParticleIds.WetSoil);
            world.Set(8, 1, ParticleIds.Fire);

            float waterBefore = CountType(world, ParticleIds.WetSoil) * HumidityField.Unit + world.Humidity.Sum();

            int steps = 0;
            while (CountType(world, ParticleIds.Fire) > 0 && steps < 500)
            {
                world.StepLogic();
                steps++;
            }

            Assert.AreEqual(29, steps, "种子校准：贴湿土的火第 29 帧熄灭");
            Assert.AreEqual(1, CountType(world, ParticleIds.Soil), "第一格湿土被火烤成了干土");
            Assert.AreEqual(
                2, CountType(world, ParticleIds.WetSoil),
                "第一格烤干后水汽摊在局部空气里、达到不可见蒸发上限，后续干化被节流保留——守恒优先");

            float waterAfter = CountType(world, ParticleIds.WetSoil) * HumidityField.Unit + world.Humidity.Sum();
            Assert.AreEqual(waterBefore, waterAfter, 1e-3f, "水总量守恒（湿土里的水进了湿度场）");
        }

        /// <summary>
        /// 遇到水：**火加速熄灭**（1/20 每帧的快熄骰）。种子 14 实测校准：第 17 帧被淬灭。
        /// 火只熄灭自己、不写对方——第一帧后水原样保留（此后水可能被火的热量触发水自身的慢蒸发，
        /// 那属于水循环域的行为，本测试不锁定其后续形态）。
        /// </summary>
        [Test]
        public void Fire_NextToWater_IsQuenchedQuickly()
        {
            var world = new ParticleWorld(16, 16, 14u);
            world.Set(8, 0, ParticleIds.Water);
            world.Set(7, 1, ParticleIds.Fire);   // 斜上方是水（八邻相邻）

            int steps = 0;
            while (CountType(world, ParticleIds.Fire) > 0 && steps < 500)
            {
                world.StepLogic();
                steps++;

                if (steps == 1)
                {
                    Assert.AreEqual(
                        (int)ParticleIds.Water, (int)world.GetCell(8, 0).typeId,
                        "火不写对方：第一帧后水原样保留（之后受热蒸发是水自身的机制）");
                }
            }

            Assert.AreEqual(17, steps, "种子校准：贴水的火第 17 帧被快熄骰淬灭");
        }

        /// <summary>
        /// 跳动（视觉）：火的 shade 每逻辑帧沿调色板随机游走 ±1（撞界弹回；确定性哈希、
        /// 不消耗世界随机流），存活期间必然发生变化、且始终夹在调色板范围内。火托在土上
        /// （不受淬熄 / 坠落干扰）。
        /// </summary>
        [Test]
        public void Fire_Flickers_ShadeMovesWithinPalette()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Set(8, 0, ParticleIds.Soil);
            world.Set(8, 1, ParticleIds.Fire);

            int paletteLength = ParticleRegistry.Types[ParticleIds.Fire].Info.Palette.Length;
            byte initial = world.GetCell(8, 1).shade;
            bool changed = false;
            for (int i = 0; i < 20 && world.GetCell(8, 1).typeId == ParticleIds.Fire; i++)
            {
                world.StepLogic();
                byte shade = world.GetCell(8, 1).shade;
                Assert.Less((int)shade, paletteLength, "shade 必须夹在调色板长度内");
                if (shade != initial)
                {
                    changed = true;
                }
            }

            Assert.IsTrue(changed, "存活 20 帧内火的 shade 应发生变化（跳动效果）");
        }
    }
}
