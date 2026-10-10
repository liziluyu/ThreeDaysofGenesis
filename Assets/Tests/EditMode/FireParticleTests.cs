using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 火粒子 v2 的行为契约（见 <c>docs/design-fire.md</c>）：每逻辑帧按本格温度回写 shade（颜色即温度）、
    /// **仅附着火**注入热量并概率点燃邻接的草、接触烤干邻接湿土（潜热按湿土→干土热容差 1.2T，
    /// 水汽走湿度场、总水守恒）；熄灭按互斥档位（压顶立灭 / **被包围时窒息立灭** / 游离火冷却即灭或 1/8 /
    /// **有燃料则不摇熄灭骰** / 贴液体 1/2 / 贴任何非可燃固体 1/5——附着火没有「烧尽骰」，
    /// 无燃料必在数帧内熄灭）；附着火上方是气体且本格 ≥200° 时 1/10 抛射火舌；
    /// **游离火不再下沉**，改为 OnLogic 末尾按坐标哈希上升 + 摇曳（不消耗随机流）。投放资格（不覆盖实体、
    /// 不浮空、锚点含火链）与生产注册路径不变。
    /// </summary>
    /// <remarks>
    /// <para>跨类型相变（草→火、湿土→干土）依赖 <see cref="ParticleIds"/> 回填，因此全部走
    /// <see cref="ParticleRegistration.RegisterAll"/> 的生产类型表（<c>new ParticleWorld(w, h, seed)</c>），
    /// 与 <c>WetSoilParticleTests</c> 的取舍一致。随机刻默认 0 ⇒ 草不会自己死。</para>
    /// <para>概率行为的断言一律用**窗口**（几何分布的合理区间），不绑定精确帧号——帧号是整个 RNG 序列的
    /// 偶然产物（审计 FT1）。确定性契约由「同世界同命令重放两次逐帧一致」与「先判后摇」的随机流消耗
    /// 断言守卫；v2 的上升选向 / shade 抖动全是坐标与 Tick 的哈希，不消耗世界随机流。</para>
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

        /// <summary>水总量 = 水格 + 湿土格（每格含一份水）+ 湿度场总量；守恒断言一律用它。</summary>
        private static float TotalWater(ParticleWorld world)
        {
            return CountType(world, ParticleIds.Water) + CountType(world, ParticleIds.WetSoil) + world.Humidity.Sum();
        }

        /// <summary>全场热量 Q 之和（守恒断言用）。</summary>
        private static float TotalHeat(ParticleWorld world)
        {
            float total = 0f;
            for (int i = 0; i < world.CellCount; i++)
            {
                total += world.Heat.Values[i];
            }

            return total;
        }

        /// <summary>模拟 ParticleEditor.Paint 的圆形笔刷（同序：dy 由低到高、dx 由小到大、圆形裁剪），
        /// 逐格走火的投放资格判定并落子，返回落点格数。</summary>
        private static int PaintCircle(ParticleWorld world, int cx, int cy, int radius)
        {
            int radiusSquared = radius * radius;
            int written = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radiusSquared)
                    {
                        continue;
                    }

                    if (!FireParticle.CanPlaceAt(world, cx + dx, cy + dy))
                    {
                        continue;
                    }

                    if (world.Set(cx + dx, cy + dy, ParticleIds.Fire))
                    {
                        written++;
                    }
                }
            }

            return written;
        }

        /// <summary>在一个 16×16、全格等温（Q = 100）的世界里放一排游离火，供上升 / 重放用例共用。
        /// 关掉虚空散热以免出现温度梯度：等温世界里无法传导，游离火只受哈希上升影响。</summary>
        private static ParticleWorld BuildUniformHeatFireField(uint seed, out int startY, out int[] fireXs)
        {
            const int size = 16;
            startY = 2;
            fireXs = new[] { 4, 6, 8, 10, 12 };

            var world = new ParticleWorld(size, size, seed);
            world.Heat.VoidHeatLoss = 0f;
            world.Light.Enabled = false;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    world.Heat.SetHeat(x, y, 100f);
                }
            }

            for (int i = 0; i < fireXs.Length; i++)
            {
                world.Set(fireXs[i], startY, ParticleIds.Fire);
            }

            return world;
        }

        /// <summary>喂了燃料（草）的附着火探针：火托在土上、左邻是草——有燃料 ⇒ 不摇熄灭骰，
        /// 好让抛射门的随机流消耗可精确枚举。aboveType 决定正上方是气体（空气）/ 火 / 实体。</summary>
        private static ParticleWorld BuildTongueProbe(uint seed, ushort aboveType)
        {
            var world = new ParticleWorld(9, 9, seed);
            world.Set(4, 3, ParticleIds.Soil);
            world.Set(3, 4, ParticleIds.Grass);
            world.Set(4, 4, ParticleIds.Fire);
            if (aboveType != ParticleRegistry.AirTypeId)
            {
                world.Set(4, 5, aboveType);
            }

            world.Heat.SetHeat(4, 4, 250f);
            return world;
        }

        /// <summary>把某布局的世界构造完（含全部 Set 的 shade 抽样消耗）之后，读出接下来 4 个随机数。</summary>
        private static uint[] ReadRngStream(uint seed, ushort aboveType)
        {
            ParticleWorld world = BuildTongueProbe(seed, aboveType);
            var stream = new uint[4];
            for (int i = 0; i < stream.Length; i++)
            {
                stream[i] = world.NextUInt();
            }

            return stream;
        }

        /// <summary>把 (4,4) 封死的密闭腔：5×5 树干块（2..6 × 2..6）中心换成火——八邻全是固 / 液、
        /// 一格空气 / 气体通风口都没有（<paramref name="withGrass"/> 时左邻改成一格草，用于验证燃料豁免）。
        /// 与 <see cref="ReadSealedCavityRngStream"/> 必须逐条同序构造，才能用随机流证明「窒息档不摇骰」。
        /// 底格用树干而非石头 / 沙 / 水：否则会先命中「压顶立灭」档、测不到窒息档。
        /// 直接调火的 <c>OnLogic</c>（不经世界扫描），树干因此不会自己执行死亡 / 生长逻辑。</summary>
        private static ParticleWorld BuildSealedCavity(uint seed, bool withGrass)
        {
            var world = new ParticleWorld(9, 9, seed);
            for (int y = 2; y <= 6; y++)
            {
                for (int x = 2; x <= 6; x++)
                {
                    world.Set(x, y, ParticleIds.TreeTrunk);
                }
            }

            if (withGrass)
            {
                world.Set(3, 4, ParticleIds.Grass);
            }

            world.Set(4, 4, ParticleIds.Fire);
            return world;
        }

        /// <summary>同序构造密闭腔（<c>withGrass: false</c>）后读出接下来 4 个随机数——
        /// 供「窒息档一个随机数都不消耗」的断言比对。</summary>
        private static uint[] ReadSealedCavityRngStream(uint seed)
        {
            ParticleWorld world = BuildSealedCavity(seed, withGrass: false);
            var stream = new uint[4];
            for (int i = 0; i < stream.Length; i++)
            {
                stream[i] = world.NextUInt();
            }

            return stream;
        }

        /// <summary>
        /// 生产注册路径：RegisterAll 必须回填 <see cref="ParticleIds.Fire"/>（非 0），且它正好是
        /// <see cref="ParticleIds.AquaticPlant"/> 之后的**下一个**追加类型（注册顺序 = 存档兼容性，
        /// 只能追加、不能插队）；带 Gas 标签、非 IsStatic、调色板非空且全部不透明。
        /// </summary>
        [Test]
        public void ProductionRegistration_BackfillsAppendedGasFireId()
        {
            Assert.AreNotEqual(0, (int)ParticleIds.Fire, "Fire 必须被回填（非 0）");
            Assert.AreEqual(
                (int)ParticleIds.Fire, (int)ParticleIds.AquaticPlant + 1,
                "火必须正好追加在全部已有类型（含水生植物）之后：注册顺序是存档兼容性的一部分");

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
        /// 放热：附着火每逻辑帧向本格注入 <see cref="FireParticle.FireHeatPerTick"/>（250 Q，火热容 1.0）。
        /// 关掉传导以隔离注入量——即使当帧被熄灭，Clear 也继承本格热量，读数不变。
        /// 另一路（默认传导）验证热量确实经世界扩散烤到四邻。
        /// </summary>
        [Test]
        public void Fire_InjectsHeatEachTickAndWarmsNeighbours()
        {
            var isolated = new ParticleWorld(16, 16, 2026u);
            isolated.Set(8, 0, ParticleIds.Soil);
            isolated.Set(8, 1, ParticleIds.Fire);
            isolated.Heat.Conductivity = 0f;   // 隔离注入：不传导、不辐射（默认 0）、不在顶行
            isolated.StepLogic();
            Assert.AreEqual(
                FireParticle.FireHeatPerTick, isolated.Heat.GetHeat(8, 1), 1e-3f,
                "附着火每逻辑帧注入 FireHeatPerTick=250（Clear 继承热量，故读数与存活无关）");

            var spread = new ParticleWorld(16, 16, 2026u);
            spread.Set(8, 0, ParticleIds.Soil);
            spread.Set(8, 1, ParticleIds.Fire);
            spread.StepLogic();
            Assert.Greater(spread.Heat.GetTemperature(7, 1), 0f, "第一帧后左邻应被加热");
            Assert.Greater(spread.Heat.GetTemperature(9, 1), 0f, "第一帧后右邻应被加热");
            Assert.Greater(spread.Heat.GetTemperature(8, 0), 0f, "第一帧后下邻（土）应被加热");
            Assert.Greater(spread.Heat.GetTemperature(8, 2), 0f, "第一帧后上邻应被加热");
            Assert.Greater(spread.Heat.GetTemperature(8, 1), 50f, "第一帧后火格温度应已显著高于环境");
        }

        /// <summary>
        /// 自然熄灭：固定种子下托在土上、无燃料的火在有限步内自灭，终态本格是**空气**而非腐殖质
        /// （火不带 Organic 标签，腐殖质只属于有机物死亡的世界规则）；托底的土不受影响。
        /// </summary>
        [Test]
        public void Fire_BurnsOut_Naturally_CellBecomesAir()
        {
            var world = new ParticleWorld(16, 16, 1u);
            world.Set(8, 0, ParticleIds.Soil);
            world.Set(8, 1, ParticleIds.Fire);

            for (int i = 0; i < 2000 && CountType(world, ParticleIds.Fire) > 0; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "火的寿命有限：2000 逻辑帧内必熄灭（含抛射出的火舌）");
            Assert.AreEqual(
                (int)ParticleIds.Air, (int)world.GetCell(8, 1).typeId,
                "熄灭后本格是空气（火非有机，不会变成腐殖质）");
            Assert.AreEqual((int)ParticleIds.Soil, (int)world.GetCell(8, 0).typeId, "托底的土不受影响");
        }

        /// <summary>
        /// 蔓延：火沿八邻烧掉草块的大部，随后全部火自然熄灭。**烧不光整片草是涌现特性**（设计拍板）：
        /// 点燃走 <c>Set(草→火)</c> 会触发世界「有机死亡 20% 留腐殖质」规则——20% 的点燃是哑弹，
        /// 留下的腐殖质不是燃料，在草块里形成空洞；v2 的游离火上升后不再回填空洞，火线在空洞处
        /// 断代，被围死的零星草格成为残株（v1 靠下沉火无意修复了空洞，v2 不再有此效果）。
        /// 断言：过程中火数曾大于 1（确在蔓延）；烧掉的草 ≥ 3/4（断代只留零星残株）；
        /// 火最终全部熄灭；终态只允许空气 / 腐殖质 / 残草。
        /// </summary>
        [Test]
        public void Fire_SpreadsThroughGrass_ThenBurnsOut()
        {
            var world = new ParticleWorld(32, 16, 2026u);
            for (int y = 1; y <= 6; y++)
            {
                for (int x = 6; x <= 24; x++)
                {
                    world.Set(x, y, ParticleIds.Grass);
                }
            }

            const int initialGrass = 19 * 6;
            world.Set(5, 3, ParticleIds.Fire);

            int maxFire = 0;
            for (int i = 0; i < 8000; i++)
            {
                int fire = CountType(world, ParticleIds.Fire);
                if (fire > maxFire)
                {
                    maxFire = fire;
                }

                if (fire == 0)
                {
                    break;
                }

                world.StepLogic();
            }

            int grassLeft = CountType(world, ParticleIds.Grass);
            Assert.Greater(maxFire, 1, "火应蔓延：过程中火数曾大于 1");
            Assert.LessOrEqual(grassLeft, initialGrass / 4,
                $"火线断代只允许零星残株：{initialGrass} 格草应烧掉 ≥ 3/4，实际剩 {grassLeft}");
            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "火（含游离火舌）应全部自然熄灭");

            for (int i = 0; i < world.CellCount; i++)
            {
                ushort typeId = world.Cells[i].typeId;
                Assert.IsTrue(
                    typeId == ParticleIds.Air || typeId == ParticleIds.Humus || typeId == ParticleIds.Grass,
                    $"终态只允许空气、腐殖质或残草，却发现 typeId={typeId}（格 {i % world.Width},{i / world.Width}）");
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
        /// 投放资格 ②：**不许浮空**——目标格八邻内必须有锚点（固 / 液的一格，或另一格火：
        /// 投放层允许火链，见圆刷回归测试）。同时覆盖：大气格（气体）可投放、越界不可投放。
        /// （刷出的无支撑火在 v2 里会上升 + 摇曳并很快熄灭，见游离火用例。）
        /// </summary>
        [Test]
        public void Fire_Placement_NeedsAttachment_NoFloating()
        {
            var world = new ParticleWorld(16, 16, 2026u);

            Assert.IsFalse(FireParticle.CanPlaceAt(world, 8, 8), "八邻全空（浮空）不可投放");

            world.Set(5, 5, ParticleIds.Stone);
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 6, 5), "正交贴着石头可投放");
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 6, 6), "斜贴着石头可投放");
            Assert.IsFalse(
                FireParticle.CanPlaceAt(world, 7, 7), "离附着物两格（浮空）不可投放");

            // 投放锚点含火链：与已有的火相邻（正交 / 斜角）也可以投放——圆刷靠它连锁铺满整团火。
            // 物理燃烧的附着物仍不含火（刷出的无支撑火是游离火，会上升 / 快熄，见上升测试）。
            world.Set(8, 8, ParticleIds.Fire);
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 8, 9), "正交火链（投放锚点）可投放");
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 9, 9), "斜向火链（投放锚点）可投放");

            world.Set(12, 12, ParticleIds.Atmosphere);
            world.Set(12, 11, ParticleIds.Stone);
            Assert.IsTrue(FireParticle.CanPlaceAt(world, 12, 12), "大气格（有附着物时）可投放");

            Assert.IsFalse(FireParticle.CanPlaceAt(world, -1, 0), "越界不可投放");
            Assert.IsFalse(FireParticle.CanPlaceAt(world, 16, 0), "越界不可投放");
        }

        /// <summary>
        /// 圆刷回归（「无论笔刷多大只有薄薄一层」的修复）：半径 2 的实心圆（13 格）悬在地面上方，
        /// 按编辑器笔刷顺序逐格判定——底格先锚到地面，上面的格子再锚到已落下的火，13 格全落；
        /// 高空同一半径没有任何锚点，一格都放不下（不许浮空）。
        /// </summary>
        [Test]
        public void Fire_Placement_CircleBrush_FillsBlobOnGround_NoneInAir()
        {
            var world = new ParticleWorld(32, 16, 2026u);
            for (int gx = 13; gx <= 17; gx++)
            {
                world.Set(gx, 0, ParticleIds.Grass);
            }

            Assert.AreEqual(13, PaintCircle(world, 15, 3, 2), "贴地圆刷应连锁铺满整团火（13 格全落）");
            Assert.AreEqual(0, PaintCircle(world, 15, 10, 2), "悬空圆刷（无锚点）应一格都放不下");
        }

        /// <summary>
        /// 火贴着（侧邻）沙 / 石会被**热传导**烤热；同时火因贴着沙 / 石（不可燃固体）走
        /// 1/<see cref="FireParticle.QuenchDenominator"/>（5）淬熄骰逐渐熄灭——「烤热」与「速灭」并存。
        /// **正下方**是石头 / 沙 / 水时会当帧直接熄灭（见压顶 / FD1 用例），故沙石放在火的两侧、火托在土上。
        /// </summary>
        [Test]
        public void Fire_HeatsAdjacentSandAndStone()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Set(8, 0, ParticleIds.Soil);
            world.Set(7, 0, ParticleIds.Stone);
            world.Set(7, 1, ParticleIds.Stone);
            world.Set(9, 0, ParticleIds.Stone);
            world.Set(10, 0, ParticleIds.Stone);
            world.Set(9, 1, ParticleIds.Sand);
            world.Set(8, 1, ParticleIds.Fire);

            for (int i = 0; i < 6; i++)
            {
                world.StepLogic();
            }

            Assert.Greater(world.Heat.GetTemperature(9, 1), 20f, "火右方的沙应被热传导烤热");
            Assert.Greater(world.Heat.GetTemperature(7, 1), 20f, "火左方的石应被热传导烤热");

            int steps = 6;
            while (CountType(world, ParticleIds.Fire) > 0 && steps < 400)
            {
                world.StepLogic();
                steps++;
            }

            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "贴着石头 / 沙（侧邻）的火应被 1/5 淬熄骰熄灭");
        }

        /// <summary>
        /// **任何非可燃固体**都淬熄（本档由 Solid 标签统一命中，草 / 花除外——它们是燃料）：干土 / 腐殖土 /
        /// 腐殖质 / 树干 / 树叶这些旧实现里不在淬熄集合内的材料，现在同样走 1/QuenchDenominator（5）。
        /// 直接调 <c>OnLogic</c>（不经世界扫描）以免材料的自身行为 / 落体干扰，40 帧窗口即可判定
        /// （1/5 每帧，40 帧未命中的概率 1.3e-4；旧实现的 1/120 烧尽骰期望 120 帧 ≈ 6 秒）。
        /// </summary>
        [Test]
        public void Fire_AnyNonFlammableSolid_QuenchesWithinWindow()
        {
            ushort[] supports =
            {
                ParticleIds.Soil,
                ParticleIds.Humus,
                ParticleIds.HumusSoil,
                ParticleIds.WetHumusSoil,
                ParticleIds.TreeTrunk,
                ParticleIds.TreeLeaf,
                ParticleIds.Stone,
                ParticleIds.Sand,
                ParticleIds.WetSoil,
            };

            for (int i = 0; i < supports.Length; i++)
            {
                var world = new ParticleWorld(16, 16, 2026u);
                world.Set(8, 0, supports[i]);
                world.Set(8, 1, ParticleIds.Fire);

                int frames = 0;
                while (world.GetCell(8, 1).typeId == ParticleIds.Fire && frames < 40)
                {
                    ParticleRegistry.Types[ParticleIds.Fire].OnLogic(world, 8, 1);
                    frames++;
                }

                Assert.AreEqual(
                    (int)ParticleIds.Air, (int)world.GetCell(8, 1).typeId,
                    $"贴在 typeId={supports[i]} 上的火应在 40 逻辑帧内被淬熄（旧实现要赖 6 秒烧尽骰）");
            }
        }

        /// <summary>
        /// **被包围 → 当帧窒息**：八邻全是固 / 液（一格空气 / 气体通风口都没有）且无燃料时，
        /// 火在一个逻辑帧内熄灭、且**不摇骰**（随机流消耗为 0 —— 用「后续随机数与未动过的对照世界相同」证明）；
        /// 旧实现没有这一档，被物质埋死 / 封在密闭腔里的火会靠 1/120 烧尽骰拖着烧 6 秒。
        /// 密封腔的**底**故意用土（不是石头 / 沙 / 水），否则会先命中「压顶立灭」档、测不到窒息档。
        /// </summary>
        [Test]
        public void Fire_EnclosedByMatter_SuffocatesImmediatelyWithoutRoll()
        {
            const uint seed = 909u;
            uint[] stream = ReadSealedCavityRngStream(seed);

            ParticleWorld world = BuildSealedCavity(seed, withGrass: false);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(world, 4, 4);

            Assert.AreEqual(
                (int)ParticleIds.Air, (int)world.GetCell(4, 4).typeId,
                "被固 / 液包死且没有燃料的火应在一个逻辑帧内窒息熄灭");
            Assert.AreEqual(
                stream[0], world.NextUInt(),
                "窒息立灭不摇骰：随机流一个数都没消耗（旧路径会摇烧尽骰）");
        }

        /// <summary>
        /// **燃料是窒息的豁免**：同样的密闭腔，只在八邻里放一格草 ⇒ 火留下来继续烧燃料
        /// （一个逻辑帧后仍在本格）。这是「火线能一路烧进草块内部」的前提——草块里的火八邻常全是
        /// 固 / 液（草），没有这层豁免就会被自己人闷灭。
        /// </summary>
        [Test]
        public void Fire_EnclosedButFuelled_KeepsBurning()
        {
            ParticleWorld world = BuildSealedCavity(303u, withGrass: true);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(world, 4, 4);

            Assert.AreEqual(
                (int)ParticleIds.Fire, (int)world.GetCell(4, 4).typeId,
                "八邻有草（燃料）时不算闷死：火应留在原地继续烧");
        }

        /// <summary>
        /// 长在土上的草皮照样能烧光（生产形态回归）：19×6 的草块铺在**土**上，火从侧边点起——
        /// 火线贴着土走，「有燃料就不摇熄灭骰」保证火不被脚下的土按灭（新实现把干土收进淬熄集合，
        /// 这条豁免是「烧光草地」验收继续成立的前提）。断言与浮空草块的用例同口径：
        /// 过程中火数曾大于 1、烧掉 ≥ 3/4、火最终全部熄灭，终态只余空气 / 腐殖质 / 残草。
        /// </summary>
        [Test]
        public void Fire_BurnsGrassPlantedOnSoil_GroundFireLineSurvives()
        {
            var world = new ParticleWorld(32, 16, 2026u);
            for (int x = 6; x <= 24; x++)
            {
                world.Set(x, 0, ParticleIds.Soil);
                for (int y = 1; y <= 6; y++)
                {
                    world.Set(x, y, ParticleIds.Grass);
                }
            }

            const int initialGrass = 19 * 6;
            world.Set(5, 3, ParticleIds.Fire);

            int maxFire = 0;
            for (int i = 0; i < 8000; i++)
            {
                int fire = CountType(world, ParticleIds.Fire);
                if (fire > maxFire)
                {
                    maxFire = fire;
                }

                if (fire == 0)
                {
                    break;
                }

                world.StepLogic();
            }

            Assert.Greater(maxFire, 1, "火应蔓延：过程中火数曾大于 1");
            Assert.LessOrEqual(
                CountType(world, ParticleIds.Grass), initialGrass / 4,
                "贴着土的火线仍应烧掉 ≥ 3/4 草皮（有燃料就不该被脚下的土淬熄）");
            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "火最终应全部熄灭");
        }

        /// <summary>
        /// 被实体**压顶**直接熄灭：石头落在火上 → 世界 rank 规则把火置换到石头正上方（Swap）→
        /// 火在下一逻辑帧立即熄灭（"正下方是石头 / 沙 / 液体 → 当帧立灭、不摇骰"）。
        /// 全过程不消耗随机数与种子无关。
        /// </summary>
        [Test]
        public void Fire_CrushedFromAbove_IsExtinguishedImmediately()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Set(8, 0, ParticleIds.Soil);
            world.Set(8, 1, ParticleIds.Fire);
            world.Set(8, 2, ParticleIds.Stone);

            world.StepLogic();
            Assert.AreEqual(
                (int)ParticleIds.Fire, (int)world.GetCell(8, 2).typeId,
                "石头落进火格：火被置换到石头上方一格（世界 rank 规则）");
            Assert.AreEqual((int)ParticleIds.Stone, (int)world.GetCell(8, 1).typeId, "石头落到火原来的位置");

            world.StepLogic();
            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "被实体压顶的火应在下一逻辑帧立即熄灭");
            Assert.AreEqual((int)ParticleIds.Air, (int)world.GetCell(8, 2).typeId, "熄灭后那格应为空气");
        }

        /// <summary>
        /// FD1（是设计不是 bug）：正下方是石头 / 沙 / 液体时当帧立灭——**火无法停留在石头 / 沙 / 水面上**。
        /// 手刷在实体 / 液面上的火，第一个逻辑帧即被压顶直接熄灭（不摇骰、与种子无关）。
        /// </summary>
        [Test]
        public void Fire_CannotRestOnStoneSandOrWater_TopCrushExtinguishes()
        {
            ushort[] supports = { ParticleIds.Stone, ParticleIds.Sand, ParticleIds.Water };
            for (int i = 0; i < supports.Length; i++)
            {
                var world = new ParticleWorld(16, 16, 2026u);
                world.Set(8, 0, supports[i]);
                world.Set(8, 1, ParticleIds.Fire);

                world.StepLogic();

                Assert.AreEqual(
                    0, CountType(world, ParticleIds.Fire),
                    $"正下方是 typeId={supports[i]}：火应被压顶规则当帧立灭");
            }
        }

        /// <summary>
        /// 遇到湿土：**先把它烤成干土**（水汽走湿度场的不可见播撒，水总量守恒），同时火被 1/5 淬熄骰熄灭。
        /// 断言用窗口（不绑精确帧号）：至少一格湿土变干、湿土不会凭空消失、干土温度恒为正（FB1 的原始漏检点：
        /// 旧 5.5T 潜热会让土温变负）、水总量守恒。
        /// </summary>
        [Test]
        public void Fire_DriesAdjacentWetSoil_ThenIsQuenched()
        {
            var world = new ParticleWorld(16, 16, 6u);
            for (int x = 7; x <= 9; x++)
            {
                world.Set(x, 0, ParticleIds.WetSoil);
                world.Heat.SetHeat(x, 0, 40f * 2f);   // 湿土 T = 40：只为给出正温起点，低于自干阈值 60
            }

            world.Set(8, 1, ParticleIds.Fire);
            world.Heat.SetHeat(8, 1, FireParticle.PeakTemperature);   // 已到软上限：本帧不再注入，隔离干化结算

            float waterBefore = TotalWater(world);

            int steps = 0;
            while (CountType(world, ParticleIds.Fire) > 0 && steps < 500)
            {
                world.StepLogic();
                steps++;
            }

            Assert.Less(steps, 500, "贴湿土的火应被淬熄骰熄灭（窗口内）");
            Assert.GreaterOrEqual(CountType(world, ParticleIds.Soil), 1, "贴湿土的火至少把一格湿土烤成了干土");
            Assert.AreEqual(
                3, CountType(world, ParticleIds.Soil) + CountType(world, ParticleIds.WetSoil),
                "湿土不会凭空消失：只会在湿土 / 干土之间转形态");

            for (int i = 0; i < world.CellCount; i++)
            {
                if (world.Cells[i].typeId == ParticleIds.Soil)
                {
                    float temperature = world.Heat.GetTemperatureAt(i);
                    Assert.Greater(temperature, 0f, $"干化后土温必须为正（下标 {i}），否则潜热系数仍错");
                }
            }

            Assert.AreEqual(waterBefore, TotalWater(world), 1e-2f, "水总量守恒（湿土里的水进了湿度场，没有凭空生灭）");
        }

        /// <summary>
        /// FB1 回归（隔离结算）：火把湿土烤成干土时，潜热必须按**湿土→干土的热容差** 1.2T 结算——
        /// 关掉传导 / 散热、把火温置于软上限（本帧不注入），于是全场总热的变化只剩这一步相变：
        /// <c>ΔQ = −1.2 × T(湿土)</c>，且干化后的土温仍为正（= 原湿土温度，热容差补齐、温度连续）。
        /// </summary>
        [Test]
        public void Fire_Drying_SubtractsWetToDryCapacityDifference()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Heat.Conductivity = 0f;
            world.Heat.VoidHeatLoss = 0f;
            world.Set(8, 0, ParticleIds.WetSoil);
            world.Heat.SetHeat(8, 0, 80f * 2f);   // 湿土 T = 80（> 干燥阈值 60，可被烤干）
            world.Set(8, 1, ParticleIds.Fire);
            world.Heat.SetHeat(8, 1, FireParticle.PeakTemperature);

            float heatBefore = TotalHeat(world);

            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(world, 8, 1);

            Assert.AreEqual((int)ParticleIds.Soil, (int)world.GetCell(8, 0).typeId, "湿土应被烤成干土");
            Assert.AreEqual(
                heatBefore - 1.2f * 80f, TotalHeat(world), 1e-3f,
                "干化潜热 = −1.2 × T（湿土热容 2.0 − 干土热容 0.8），不是水↔大气的 5.5T");
            Assert.AreEqual(
                80f, world.Heat.GetTemperature(8, 0), 1e-3f,
                "干化后土温等于原湿土温度（热容差补齐 ⇒ 温度连续、恒为正）");
            Assert.AreEqual(1f, world.Humidity.Sum(), 1e-3f, "一份水汽进了湿度场（水总量守恒）");
        }

        /// <summary>
        /// 遇到水：火走**液体独立快熄档** 1/<see cref="FireParticle.WaterQuenchDenominator"/>（2），
        /// 比贴不可燃固体的 1/5 更快（水是最强灭火剂）。火只熄灭自己、不写对方——第一帧后水原样保留。
        /// 期望帧数 ≈ 2，断言用宽松窗口（不绑精确帧号）。
        /// </summary>
        [Test]
        public void Fire_NextToWater_IsQuenchedQuickly()
        {
            var world = new ParticleWorld(16, 16, 14u);
            world.Set(8, 0, ParticleIds.Water);
            world.Set(7, 1, ParticleIds.Fire);

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

            Assert.Greater(steps, 0, "火至少活过一个逻辑帧");
            Assert.LessOrEqual(steps, 60, "贴水的火走 1/2 快熄档：期望 2 帧，快于贴不可燃固体的 1/5");
        }

        /// <summary>
        /// 游离火上升（§9.1）：无支撑火（八邻无固 / 液）**不再下沉**，改为按坐标哈希每逻辑帧上升 / 摇曳。
        /// 在一个全格等温的世界里放一排游离火，逐帧统计存活火的质心 y：
        /// 质心必须严格上升过、且从未低于起点（只升不降）。
        /// </summary>
        [Test]
        public void Fire_FreeFireRises_NeverSinks()
        {
            ParticleWorld world = BuildUniformHeatFireField(2026u, out int startY, out _);

            float maxCentroid = startY;
            float minCentroid = startY;
            bool everAlive = false;
            for (int frame = 0; frame < 60; frame++)
            {
                world.StepLogic();

                int count = 0;
                float sumY = 0f;
                for (int i = 0; i < world.CellCount; i++)
                {
                    if (world.Cells[i].typeId == ParticleIds.Fire)
                    {
                        count++;
                        sumY += i / world.Width;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                everAlive = true;
                float centroid = sumY / count;
                if (centroid > maxCentroid)
                {
                    maxCentroid = centroid;
                }

                if (centroid < minCentroid)
                {
                    minCentroid = centroid;
                }
            }

            Assert.IsTrue(everAlive, "窗口内游离火应至少存活一帧");
            Assert.Greater(maxCentroid, (float)startY, "游离火应主动上升：存活火质心 y 严格高于起点");
            Assert.GreaterOrEqual(minCentroid, (float)startY, "游离火绝不下沉：存活火质心 y 从不低于起点");
        }

        /// <summary>
        /// 冷却即灭（§9.1 的收束机制）：游离火本格温度低于 <see cref="FireParticle.EmberTemperature"/>（40）
        /// 时当帧直接熄灭、不摇骰。给一格游离火设 T = 20，一个逻辑帧后必须消失（与种子无关）。
        /// </summary>
        [Test]
        public void Fire_FreeFireBelowEmberTemperature_ExtinguishesImmediately()
        {
            var world = new ParticleWorld(16, 16, 9u);
            world.Set(8, 8, ParticleIds.Fire);
            world.Heat.SetHeat(8, 8, 20f);   // 游离火 T = 20 < 40（无附着 ⇒ 不注入）

            world.StepLogic();

            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "游离火温度低于熄影线应立灭（不摇骰）");
            Assert.AreEqual((int)ParticleIds.Air, (int)world.GetCell(8, 8).typeId, "熄灭后本格是空气");
        }

        /// <summary>
        /// 冷却即灭的收束性：无支撑火靠余热上升、随传导冷却，最终**全部消失**，网格残余火为 0，
        /// 且不会变成腐殖质（火非有机）。
        /// </summary>
        [Test]
        public void Fire_FreeFireEventuallyVanishes_LeavingNoResidual()
        {
            var world = new ParticleWorld(16, 16, 9u);
            world.Set(8, 8, ParticleIds.Fire);
            world.Heat.SetHeat(8, 8, 120f);   // 有足够余热先上升几帧

            for (int i = 0; i < 400 && CountType(world, ParticleIds.Fire) > 0; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "游离火余热散尽必然收束：窗口内网格残余火为 0");
            Assert.AreEqual(0, CountType(world, ParticleIds.Humus), "火非有机，熄灭不产生腐殖质");
        }

        /// <summary>
        /// 颜色即温度（§9.1）：shade 是本格温度的分档派生量（≥300→0 … &lt;40→5，+ ±1 哈希抖动）。
        /// 用喂了燃料（草）的附着火探针直接调逻辑钩子（不摇熄灭骰）：热格（T=350）shade ≤ 1（亮档），
        /// 冷格（T=10）shade ≥ 4（暗档），且热格比冷格更亮。
        /// </summary>
        [Test]
        public void Fire_ShadeIsTemperatureBand()
        {
            var hot = new ParticleWorld(9, 9, 2026u);
            hot.Set(4, 3, ParticleIds.Soil);
            hot.Set(3, 4, ParticleIds.Grass);
            hot.Set(4, 4, ParticleIds.Fire);
            hot.Heat.SetHeat(4, 4, 350f);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(hot, 4, 4);
            int hotShade = hot.GetCell(4, 4).shade;

            var cold = new ParticleWorld(9, 9, 2026u);
            cold.Set(4, 3, ParticleIds.Soil);
            cold.Set(3, 4, ParticleIds.Grass);
            cold.Set(4, 4, ParticleIds.Fire);
            cold.Heat.SetHeat(4, 4, 10f);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(cold, 4, 4);
            int coldShade = cold.GetCell(4, 4).shade;

            Assert.LessOrEqual(hotShade, 1, "热格（T=350）shade 应落在亮档（≤ 1）");
            Assert.GreaterOrEqual(coldShade, 4, "冷格（T=10）shade 应落在暗红档（≥ 4）");
            Assert.Less(hotShade, coldShade, "越热越亮：shade 必须随温度单调反向");
        }

        /// <summary>
        /// 火舌抛射（§9.1，正向）：附着火正上方是气体、本格温度 ≥ 200° 时以 1/10 每帧吐火舌。
        /// 一排 5 格附着火（上方全是空气），窗口内上方必然出现火格（游离火舌）。
        /// </summary>
        [Test]
        public void Fire_TongueLaunches_WhenGasAbove_WithinWindow()
        {
            var world = new ParticleWorld(16, 16, 7u);
            for (int x = 6; x <= 10; x++)
            {
                world.Set(x, 0, ParticleIds.Soil);
                world.Set(x, 1, ParticleIds.Fire);
            }

            bool launched = false;
            for (int frame = 0; frame < 200 && !launched; frame++)
            {
                world.StepLogic();
                for (int i = 0; i < world.CellCount; i++)
                {
                    if (world.Cells[i].typeId == ParticleIds.Fire && i / world.Width >= 2)
                    {
                        launched = true;
                        break;
                    }
                }
            }

            Assert.IsTrue(launched, "附着火上方是气体且够热：窗口内应抛出火舌（上方出现火格）");
        }

        /// <summary>
        /// 火舌抛射的门控（§9.3 的「先判后摇」）：
        /// ① 正上方是**火**时跳过抛射骰（火舌不叠火舌）——用随机流消耗精确验证：整个 OnLogic 只消耗
        ///    ⑦ 蔓延的 1 个随机数（现场景喂了燃料、无淬熄物，熄灭骰不摇）；
        /// ② 正上方是**气体**时抛射骰一定会摇（多消耗 1 个，命中再 +1 用于新火格抽样）；
        /// ③ 正上方是**实体**时跳过抛射，实体不被火覆盖。
        /// </summary>
        [Test]
        public void Fire_TongueGate_SkipsWhenFireAbove_RollsWhenGas_AndNeverOverwritesSolid()
        {
            const uint seed = 4242u;

            uint[] fireAboveStream = ReadRngStream(seed, ParticleIds.Fire);
            ParticleWorld fireAbove = BuildTongueProbe(seed, ParticleIds.Fire);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(fireAbove, 4, 4);
            Assert.AreEqual(
                fireAboveStream[1], fireAbove.NextUInt(),
                "上方是火：抛射骰被跳过，本帧只消耗 ⑦ 蔓延的 1 个随机数");

            uint[] gasAboveStream = ReadRngStream(seed, ParticleRegistry.AirTypeId);
            ParticleWorld gasAbove = BuildTongueProbe(seed, ParticleRegistry.AirTypeId);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(gasAbove, 4, 4);
            uint next = gasAbove.NextUInt();
            Assert.IsTrue(
                next == gasAboveStream[2] || next == gasAboveStream[3],
                "上方是气体：抛射骰必定被摇（消耗 2 或 3 个随机数）");

            ParticleWorld solidAbove = BuildTongueProbe(seed, ParticleIds.Stone);
            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(solidAbove, 4, 4);
            Assert.AreEqual(
                (int)ParticleIds.Stone, (int)solidAbove.GetCell(4, 5).typeId,
                "上方是实体：抛射被跳过，实体不被火覆盖");
            Assert.AreEqual(
                (int)ParticleIds.Air, (int)solidAbove.GetCell(4, 6).typeId,
                "上方是实体：更高处也不会冒出火");
        }

        /// <summary>
        /// 确定性重放（§9.2 / FT1 的真正守卫）：同种子、同命令的两个世界逐帧必须完全一致——
        /// 包括火格坐标（typeId）与 shade（颜色）。上升选向 / 摇曳 / shade 抖动全是坐标与 Tick 的哈希，
        /// 运动帧对火零操作、不消耗随机流，因此这条断言把「火的行为可复现」钉死在帧级别。
        /// </summary>
        [Test]
        public void Fire_DeterministicReplay_IsFrameIdentical()
        {
            ParticleWorld a = BuildUniformHeatFireField(777u, out int startY, out _);
            ParticleWorld b = BuildUniformHeatFireField(777u, out _, out _);

            bool anyRose = false;
            for (int frame = 0; frame < 40; frame++)
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

                for (int i = 0; i < a.CellCount; i++)
                {
                    if (a.Cells[i].typeId == ParticleIds.Fire && i / a.Width > startY)
                    {
                        anyRose = true;
                    }
                }
            }

            Assert.IsTrue(anyRose, "重放世界里的游离火应确实上升过（否则场景是死的，重放断言无意义）");
        }

        /// <summary>
        /// FB2 回归（单例）：用火覆盖一格**含湿的空气**时，旧实现走 <c>ResetAt</c> 直接湮灭水汽；
        /// 新语义是任何覆盖旧气体的写入都先把残余水汽疏散给四邻气体格。断言总水不变、本格清零。
        /// </summary>
        [Test]
        public void Fire_CoveringAtmosphere_EvacuatesHumidity()
        {
            var world = new ParticleWorld(8, 8, 2026u);
            world.Set(4, 4, ParticleIds.Atmosphere);
            world.Humidity.Set(4, 4, 0.4f);

            world.Set(4, 4, ParticleIds.Fire);

            Assert.AreEqual(0f, world.Humidity.Get(4, 4), 1e-4f, "火格（气体）不保留原水汽");
            Assert.AreEqual(0.4f, world.Humidity.Sum(), 1e-4f, "水汽被疏散给四邻而不是湮灭（守恒优先）");
            Assert.AreEqual(0.1f, world.Humidity.Get(3, 4), 1e-4f, "四邻各得 1/4");
            Assert.AreEqual(0.1f, world.Humidity.Get(5, 4), 1e-4f);
            Assert.AreEqual(0.1f, world.Humidity.Get(4, 3), 1e-4f);
            Assert.AreEqual(0.1f, world.Humidity.Get(4, 5), 1e-4f);
        }

        /// <summary>
        /// FB2 回归（全程）：从**点火**（火覆盖含湿空气）到**自灭**（火 Clear 成空气）的每一个逻辑帧，
        /// 世界总水（水格 + 湿土格 + 湿度场）都严格守恒——点火路径不再漏、自灭路径不再湮灭。
        /// </summary>
        [Test]
        public void Fire_BurningAndSelfExtinguishing_ConservesWorldWaterEveryFrame()
        {
            var world = new ParticleWorld(8, 8, 2026u);
            world.Set(4, 3, ParticleIds.Soil);
            world.Set(4, 4, ParticleIds.Atmosphere);
            world.Humidity.Set(4, 4, 0.5f);

            float before = TotalWater(world);   // 点火前的总水

            world.Set(4, 4, ParticleIds.Fire);   // 点火：覆盖含湿空气
            Assert.AreEqual(
                before, TotalWater(world), 1e-2f,
                "点火覆盖含湿空气的当帧就必须守恒（旧实现走 ResetAt 会直接湮灭本格水汽）");

            int frames = 0;
            while (CountType(world, ParticleIds.Fire) > 0 && frames < 2000)
            {
                world.StepLogic();
                frames++;
                Assert.AreEqual(
                    before, TotalWater(world), 1e-2f,
                    $"第 {frames} 帧总水不守恒：点火 / 自灭路径可能仍在湮灭水汽");
            }

            Assert.AreEqual(0, CountType(world, ParticleIds.Fire), "无燃料的附着火最终自灭");
        }

        /// <summary>
        /// FT2 生产参数补盲：按生产参数（Conductivity 0.24 / VoidHeatLoss 0.12 / RadiativeLoss 0.05 /
        /// ε_air 0.1）运行时，整片火格温度被软上限挡住、不会失控（单帧最多超出一份注入量），
        /// 且火把贴着的土烤到草致死线（50°）以上——热学量级在生产参数下成立。
        /// </summary>
        /// <remarks>
        /// 软上限的机制是「本格温度 ≥ <see cref="FireParticle.PeakTemperature"/> 就不再注入」，
        /// 而注入发生在判定**之后**：跨过 400 的那一帧会先叠加一份
        /// <see cref="FireParticle.FireHeatPerTick"/>（+250）再靠传导摊走。孤立单格火（四邻是空气或土）
        /// 的实测平衡温度在 300° 出头，但若正上方恰有一格保温的相邻物（例如抛出的火舌滞留一帧），
        /// 该帧可能短暂越过 400。因此这里断言的是**有界性**（≤ 软上限 + 一份单帧注入）而不是硬 400，
        /// 硬门本身由 <see cref="Fire_HeatInjectionStopsAtPeakTemperature"/> 单独钉死。
        /// </remarks>
        [Test]
        public void Fire_ProductionParameters_BoundTemperatureAndReachLethalHeat()
        {
            var world = new ParticleWorld(16, 16, 2026u);
            world.Set(8, 0, ParticleIds.Soil);
            world.Set(8, 1, ParticleIds.Fire);
            world.Heat.Conductivity = 0.24f;       // 生产导热率
            world.Heat.VoidHeatLoss = 0.12f;       // 生产虚空散热
            world.Heat.RadiativeLoss = 0.05f;      // 生产辐射率
            world.Heat.AirThermalEmissivity = 0.1f;
            world.Light.Enabled = false;

            float maxFireTemperature = 0f;
            float maxSurfaceTemperature = 0f;
            for (int frame = 0; frame < 60; frame++)
            {
                world.StepLogic();
                for (int i = 0; i < world.CellCount; i++)
                {
                    ushort id = world.Cells[i].typeId;
                    if (id == ParticleIds.Fire)
                    {
                        float temperature = world.Heat.GetTemperatureAt(i);
                        if (temperature > maxFireTemperature)
                        {
                            maxFireTemperature = temperature;
                        }
                    }
                    else if (id != ParticleRegistry.AirTypeId)
                    {
                        float temperature = world.Heat.GetTemperatureAt(i);
                        if (temperature > maxSurfaceTemperature)
                        {
                            maxSurfaceTemperature = temperature;
                        }
                    }
                }
            }

            Assert.LessOrEqual(
                maxFireTemperature, FireParticle.PeakTemperature + FireParticle.FireHeatPerTick,
                "生产参数下整片火格温度必须有界：软上限 + 至多一份单帧注入，不得失控升温");
            Assert.GreaterOrEqual(
                maxSurfaceTemperature, 50f,
                "火应把贴着的物质烤到草致死线（50°）以上，否则火线烧不动草");
        }

        /// <summary>
        /// 软上限的门本身：本格温度 ≥ <see cref="FireParticle.PeakTemperature"/> 时本帧不注入热量
        /// （关掉传导 / 散热隔离观察）；低于上限时按 <see cref="FireParticle.FireHeatPerTick"/> 注入。
        /// </summary>
        [Test]
        public void Fire_HeatInjectionStopsAtPeakTemperature()
        {
            var atCap = new ParticleWorld(16, 16, 2026u);
            atCap.Set(8, 0, ParticleIds.Soil);
            atCap.Set(8, 1, ParticleIds.Fire);
            atCap.Heat.Conductivity = 0f;
            atCap.Heat.SetHeat(8, 1, FireParticle.PeakTemperature);

            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(atCap, 8, 1);
            Assert.AreEqual(
                FireParticle.PeakTemperature, atCap.Heat.GetHeat(8, 1), 1e-3f,
                "本格已达软上限：本帧不再注入");

            var belowCap = new ParticleWorld(16, 16, 2026u);
            belowCap.Set(8, 0, ParticleIds.Soil);
            belowCap.Set(8, 1, ParticleIds.Fire);
            belowCap.Heat.Conductivity = 0f;
            belowCap.Heat.SetHeat(8, 1, FireParticle.PeakTemperature - 1f);

            ParticleRegistry.Types[ParticleIds.Fire].OnLogic(belowCap, 8, 1);
            Assert.AreEqual(
                FireParticle.PeakTemperature - 1f + FireParticle.FireHeatPerTick,
                belowCap.Heat.GetHeat(8, 1), 1e-3f,
                "低于软上限：本帧按 FireHeatPerTick 注入");
        }
    }
}
