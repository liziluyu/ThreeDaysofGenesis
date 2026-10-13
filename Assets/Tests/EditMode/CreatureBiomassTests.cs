using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 死亡物质化（<see cref="CreatureDeathMaterializer"/> / <see cref="BiomassProfile"/>）与
    /// <see cref="CreatureWorld"/> 清扫阶段的接入契约：轮廓映射、物种选择、跳过规则、四种死因的事件顺序，
    /// 以及物质化后的生物质腐败闭环。
    /// </summary>
    /// <remarks>
    /// <para>地形与生物质都按 <see cref="ParticleIds"/> 常量引用，因此全部走生产注册路径
    /// （<see cref="ParticleRegistration.RegisterAll"/>，三参 <c>new ParticleWorld</c>）。</para>
    /// <para><b>为什么多数用例只调 <c>creatures.Step(world)</c> 而不调 <c>world.StepLogic()</c>：</b>
    /// 物质化发生在动物层的清扫阶段（<c>creatures.Step</c> 内部），与粒子世界是否推进无关。
    /// 只推进动物层能让地形 / 火 / 水保持静止，从而对「轮廓每一格此刻落在哪」做**逐格**精确断言；
    /// 只有闭环腐败测试（<see cref="Materialize_ThenDecays"/>）才需要同时推进粒子世界。</para>
    /// </remarks>
    public sealed class CreatureBiomassTests
    {
        private const int W = 32;
        private const int H = 32;
        private const uint SeedValue = 20261011u;

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Sand
        {
            get { return ParticleIds.Sand; }
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort Fire
        {
            get { return ParticleIds.Fire; }
        }

        private static ushort TreeTrunk
        {
            get { return ParticleIds.TreeTrunk; }
        }

        private static ushort Biomass
        {
            get { return ParticleIds.Biomass; }
        }

        private static ushort Humus
        {
            get { return ParticleIds.Humus; }
        }

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        // ============================== 共用脚手架 ==============================

        private static ParticleWorld NewWorld()
        {
            return new ParticleWorld(W, H, SeedValue);
        }

        /// <summary>把 [y0, y1] 行整行填成同一类型（Y 越大越高）。</summary>
        private static void FillRows(ParticleWorld world, int y0, int y1, ushort typeId)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    world.Set(x, y, typeId);
                }
            }
        }

        /// <summary>把 [x0, x1] × [y0, y1] 矩形（闭区间）整块填成同一类型。</summary>
        private static void FillRect(ParticleWorld world, int x0, int x1, int y0, int y1, ushort typeId)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    world.Set(x, y, typeId);
                }
            }
        }

        /// <summary>身体覆盖矩形的列 / 行闭区间，用**与 <c>Creature.IsFullyBuried</c> 同一套口径**算
        /// （<c>floor(x ∓ width/2 ± ε)</c> / <c>floor(y + ε .. y + height − ε)</c>），供压死测试逐格摆放。</summary>
        private static void BodyRect(Creature creature, out int colMin, out int colMax, out int rowMin, out int rowMax)
        {
            const float eps = 1e-4f;
            colMin = Mathf.FloorToInt(creature.X - creature.Width * 0.5f + eps);
            colMax = Mathf.FloorToInt(creature.X + creature.Width * 0.5f - eps);
            rowMin = Mathf.FloorToInt(creature.Y + eps);
            rowMax = Mathf.FloorToInt(creature.Y + creature.Height - eps);
        }

        /// <summary>一个完整逻辑帧（与驱动同序）：先推进粒子世界、再推进动物层。</summary>
        private static void StepFrame(ParticleWorld world, CreatureWorld creatures)
        {
            world.StepLogic();
            creatures.Step(world);
        }

        /// <summary>只推进动物层（地形静止）：用于对物质化落点做逐格精确断言。</summary>
        private static void StepCreatures(ParticleWorld world, CreatureWorld creatures)
        {
            creatures.Step(world);
        }

        /// <summary>统计全世界里某 typeId 的格数。</summary>
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

        /// <summary>按轮廓统计「落在界内」的格数（越界格本就不写），用于断言返回的写入数。</summary>
        private static int NonEmptyInBounds(BiomassProfile profile, int baseX, int baseY, int width, int height)
        {
            int count = 0;
            sbyte[] cells = profile.Cells;
            for (int dy = 0; dy < BiomassProfile.Size; dy++)
            {
                for (int dx = 0; dx < BiomassProfile.Size; dx++)
                {
                    if (cells[dy * BiomassProfile.Size + dx] < 0)
                    {
                        continue;
                    }

                    int x = baseX + dx;
                    int y = baseY + dy;
                    if (x >= 0 && x < width && y >= 0 && y < height)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>在给定位置生成并杀死一只兔子，再让清扫阶段跑一次（地形静止）。</summary>
        private static void KillRabbitAt(ParticleWorld world, CreatureWorld creatures, float x, float y, CreatureDeathCause cause)
        {
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, x, y);
            rabbit.Kill(cause);
            StepCreatures(world, creatures);
        }

        // ============================== 测试 ==============================

        /// <summary>
        /// 在已知位置杀死一只兔子：生物质格集合**恰等于**兔子 profile 映射到世界的坐标集合（逐一断言），
        /// 且每格的 <c>shade</c> 等于 profile 该格的颜色索引。
        /// </summary>
        [Test]
        public void Materialize_WritesProfileAlignedToSprite()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            KillRabbitAt(world, creatures, 16f, 20f, CreatureDeathCause.Unknown);

            // 死前被跳过推进（②不再跑它），因此位置仍是生成坐标：floor(16) - 8 = 8，floor(20) = 20。
            int baseX = Mathf.FloorToInt(16f) - BiomassProfile.Size / 2;
            int baseY = Mathf.FloorToInt(20f);

            sbyte[] cells = BiomassProfile.Rabbit.Cells;
            int expected = 0;
            for (int dy = 0; dy < BiomassProfile.Size; dy++)
            {
                for (int dx = 0; dx < BiomassProfile.Size; dx++)
                {
                    sbyte index = cells[dy * BiomassProfile.Size + dx];
                    if (index < 0)
                    {
                        continue;
                    }

                    int x = baseX + dx;
                    int y = baseY + dy;
                    Assert.IsTrue(world.IsInBounds(x, y), $"轮廓格 ({dx},{dy}) 应落在界内");

                    Cell cell = world.GetCell(x, y);
                    Assert.AreEqual(Biomass, cell.typeId, $"轮廓格 ({dx},{dy}) 应写成生物质");
                    Assert.AreEqual((byte)index, cell.shade, $"轮廓格 ({dx},{dy}) 的 shade 应等于 profile 索引");
                    expected++;
                }
            }

            Assert.AreEqual(48, expected, "兔子轮廓应有 48 个非空格（plan Task 3 逐字数据）");
            Assert.AreEqual(expected, CountType(world, Biomass), "场上生物质格数应恰等于轮廓格数（无多余写入）");
        }

        /// <summary>鱼死亡用鱼 profile：坐标集合与颜色索引逐格对齐，且格数为 51。</summary>
        [Test]
        public void Materialize_Fish_UsesFishProfile()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 20f);
            fish.Kill(CreatureDeathCause.Unknown);
            StepCreatures(world, creatures);

            int baseX = Mathf.FloorToInt(16f) - BiomassProfile.Size / 2;
            int baseY = Mathf.FloorToInt(20f);

            sbyte[] cells = BiomassProfile.Fish.Cells;
            int expected = 0;
            for (int dy = 0; dy < BiomassProfile.Size; dy++)
            {
                for (int dx = 0; dx < BiomassProfile.Size; dx++)
                {
                    sbyte index = cells[dy * BiomassProfile.Size + dx];
                    if (index < 0)
                    {
                        continue;
                    }

                    int x = baseX + dx;
                    int y = baseY + dy;
                    Assert.IsTrue(world.IsInBounds(x, y), $"鱼轮廓格 ({dx},{dy}) 应落在界内");

                    Cell cell = world.GetCell(x, y);
                    Assert.AreEqual(Biomass, cell.typeId, $"鱼轮廓格 ({dx},{dy}) 应写成生物质");
                    Assert.AreEqual((byte)index, cell.shade, $"鱼轮廓格 ({dx},{dy}) 的 shade 应等于 profile 索引");
                    expected++;
                }
            }

            Assert.AreEqual(51, expected, "鱼轮廓应有 51 个非空格（plan Task 3 逐字数据）");
            Assert.AreEqual(expected, CountType(world, Biomass), "场上生物质格数应恰等于鱼轮廓格数");
        }

        /// <summary>
        /// 三档覆盖规则的落点：**地形固体（石）直接写**、**水先挪到合格空位再写**（水守恒），
        /// 靠左边界的轮廓越界部分跳过。返回写入数等于期望值。
        /// </summary>
        [Test]
        public void Materialize_CoversSolidsAndRelocatesWater()
        {
            // 场景 A：轮廓内三格分别放石头 / 水 / 石头（都落在非空的轮廓格上）。
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            world.Set(15, 20, Stone);   // baseX=8,baseY=20 ⇒ dx=7,dy=0（profile 值 1）
            world.Set(13, 22, Water);   // dx=5,dy=2（profile 值 2）
            world.Set(12, 23, Stone);   // dx=4,dy=3（profile 值 2）

            int waterBefore = CountType(world, Water);
            Assert.AreEqual(1, waterBefore, "摆放后场上恰有一格水");

            KillRabbitAt(world, creatures, 16f, 20f, CreatureDeathCause.Unknown);

            // 地形固体（石）直写：压死的尸体必须可见。
            Assert.AreEqual(Biomass, world.GetCell(15, 20).typeId, "地形固体（石）应被尸体直接覆盖");
            Assert.AreEqual(Biomass, world.GetCell(12, 23).typeId, "地形固体（石）应被尸体直接覆盖");
            // 水格先腾空再写。
            Assert.AreEqual(Biomass, world.GetCell(13, 22).typeId, "水格应先把水挪走后被尸体覆盖");

            // 水挪到「按候选顺序第一格」：从 (13,22) 起，候选 (0,+1)=(13,23)、(0,+2)=(13,24) 都落在
            // 轮廓非空格上（稍后要被生物质覆盖，不作合格目标），下一个 (-1,0)=(12,22) 是界内空气且非轮廓格 ⇒ 落这里。
            Assert.AreEqual(Water, world.GetCell(12, 22).typeId, "水应按候选顺序挪到具体格 (12,22)");
            Assert.AreEqual(waterBefore, CountType(world, Water), "水必须先挪后写：总量守恒（一格不少）");
            Assert.AreEqual(48, CountType(world, Biomass), "三档下轮廓在界内全部 48 格都应写成生物质");

            // 场景 B：把兔子贴到左边界，轮廓左半越界。
            ParticleWorld edgeWorld = NewWorld();
            CreatureWorld edgeCreatures = new CreatureWorld(SeedValue);
            RabbitCreature edgeRabbit = edgeCreatures.Spawn(new RabbitCreature(), edgeWorld, 0.75f, 0f);
            edgeRabbit.Kill(CreatureDeathCause.Unknown);
            StepCreatures(edgeWorld, edgeCreatures);

            // floor(0.75) - 8 = -8 ⇒ dx 0..7 全部越界。
            int baseX = Mathf.FloorToInt(0.75f) - BiomassProfile.Size / 2;
            int baseY = Mathf.FloorToInt(0f);
            int expected = NonEmptyInBounds(BiomassProfile.Rabbit, baseX, baseY, edgeWorld.Width, edgeWorld.Height);

            Assert.AreEqual(29, expected, "兔子轮廓在左边界外应只剩 29 格落界内");
            Assert.AreEqual(expected, CountType(edgeWorld, Biomass), "越界格跳过，其余照常写入");

            // 轮廓左半（dx 0..7）越界被跳过：界内最小列 x=0 对应轮廓 dx=8，该列应恰有 9 格落地。
            int columnZero = 0;
            for (int y = 0; y < edgeWorld.Height; y++)
            {
                if (edgeWorld.GetCell(0, y).typeId == Biomass)
                {
                    columnZero++;
                }
            }

            Assert.AreEqual(9, columnZero, "越界跳过：界内最小列（x=0）应恰为轮廓 dx=8 的 9 格");
        }

        /// <summary>
        /// 「密闭水囊」：把尸体轮廓上的一格水在**半径 2 内**用石头围死 ⇒ 水无处可挪，该格保留水、不写生物质
        /// （写入数 = 48 − 被围住格数）；水总量不变（绝不删水）；且全程不抛异常。
        /// </summary>
        [Test]
        public void Materialize_SealedWaterPocket_KeepsWaterAndSkipsCell()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            // 选一处轮廓非空格放水：(16,28) ⇔ baseX=8,baseY=20 ⇒ dx=8,dy=8（profile 值 2）。
            const int waterX = 16;
            const int waterY = 28;
            world.Set(waterX, waterY, Water);

            // 半径 2（含斜角）内除水自己那格外全填石头：任何候选空位都是石头 ⇒ 挪不动、原地保留。
            for (int y = waterY - 2; y <= waterY + 2; y++)
            {
                for (int x = waterX - 2; x <= waterX + 2; x++)
                {
                    if (x == waterX && y == waterY)
                    {
                        continue;
                    }

                    world.Set(x, y, Stone);
                }
            }

            int waterBefore = CountType(world, Water);
            Assert.AreEqual(1, waterBefore, "围死前场上恰有一格水");

            Assert.DoesNotThrow(() => KillRabbitAt(world, creatures, 16f, 20f, CreatureDeathCause.Unknown));

            Assert.AreEqual(Water, world.GetCell(waterX, waterY).typeId, "被围死的水格仍应是水（挪不动就跳过，绝不删水）");
            Assert.AreEqual(waterBefore, CountType(world, Water), "水袋围死时水总量不变");
            Assert.AreEqual(48 - 1, CountType(world, Biomass), "被围住的一格未写成生物质，其余 47 格照常写入");
        }

        /// <summary>
        /// 压死后的尸体覆盖**沙**：按 <c>CreatureAiTests.Crushed_RabbitFullyBuriedByStoneDies</c> 的手法把兔子
        /// 连同一圈外壳埋进沙里 ⇒ 跑到 <see cref="CreatureDeathCause.Crushed"/> 死并完成清扫；死因 Crushed、
        /// 死亡事件恰好一次、出现生物质，且原覆盖身体的那几格**现在是生物质**（地形固体直写，尸体可见）。
        /// </summary>
        [Test]
        public void Crushed_RabbitBuriedBySand_LeavesBiomassWhereBodyWas()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);   // 地板（避免落沙穿底）
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);

            // 用与 Creature.IsFullyBuried 同一套口径把兔子整个埋进沙（外加一圈外壳堵死逃逸路线）。
            BodyRect(rabbit, out int colMin, out int colMax, out int rowMin, out int rowMax);
            FillRect(world, colMin - 1, colMax + 1, rowMin - 1, rowMax + 1, Sand);

            int deaths = 0;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                deaths++;
                cause = deathCause;
            };

            for (int i = 0; i < Creature.BuriedTicksLimit + 30; i++)
            {
                StepCreatures(world, creatures);
            }

            Assert.IsFalse(rabbit.Alive, "被沙完全活埋超过 BuriedTicksLimit 帧应被压死");
            Assert.AreEqual(1, deaths, "死亡事件应恰好一次");
            Assert.AreEqual(CreatureDeathCause.Crushed, cause, "死因应为 Crushed（压死）");
            Assert.AreEqual(0, creatures.Count, "死亡实体应已被清扫");

            Assert.Greater(CountType(world, Biomass), 0, "压死后应出现生物质");
            Assert.AreEqual(48, CountType(world, Biomass), "轮廓在界内全部 48 格都应写成生物质");

            // 原覆盖身体的那几格现在是生物质（而不是沙）——地形固体直写使尸体可见。
            // 位置取死亡后的实机坐标（清扫不清零 X/Y），保证与物质化映射同源。
            BodyRect(rabbit, out int dColMin, out int dColMax, out int dRowMin, out int dRowMax);
            for (int y = dRowMin; y <= dRowMax; y++)
            {
                for (int x = dColMin; x <= dColMax; x++)
                {
                    Assert.AreEqual(Biomass, world.GetCell(x, y).typeId, $"原覆盖身体的格 ({x},{y}) 现在应是生物质");
                }
            }
        }

        /// <summary>
        /// 跳过档不变：轮廓内放一格火、一格树干、一格**已有生物质** ⇒ 三者都不被覆盖（保持原类型），
        /// 其余轮廓格照常写入。
        /// </summary>
        [Test]
        public void Materialize_SkipsFireTrunkAndExistingBiomass()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            world.Set(15, 20, Fire);       // baseX=8,baseY=20 ⇒ dx=7,dy=0（profile 值 1）
            world.Set(14, 21, TreeTrunk);  // dx=6,dy=1（profile 值 2）
            world.Set(17, 26, Biomass);    // dx=9,dy=6（profile 值 1）

            KillRabbitAt(world, creatures, 16f, 20f, CreatureDeathCause.Unknown);

            Assert.AreEqual(Fire, world.GetCell(15, 20).typeId, "火不应被尸体覆盖（跳过档）");
            Assert.AreEqual(TreeTrunk, world.GetCell(14, 21).typeId, "树干不应被尸体覆盖（跳过档）");
            Assert.AreEqual(Biomass, world.GetCell(17, 26).typeId, "已有生物质不应被尸体覆盖（保持原格、不重抽颜色）");
            Assert.AreEqual(48 - 3 + 1, CountType(world, Biomass), "跳过火 / 树干 / 已有生物质：新写 45 格 + 原有 1 格 = 46");
        }

        /// <summary>
        /// 四种死因（Burned / Drowned / Beached / Starved）各跑一次：<see cref="CreatureWorld.CreatureDied"/>
        /// 恰好发一次、参数为原死因，且清扫后 <c>Creatures.Count == 0</c>；同时尸体已物质化。
        /// </summary>
        [Test]
        public void Death_RaisesEventOnceWithCause()
        {
            CreatureDeathCause[] causes =
            {
                CreatureDeathCause.Burned,
                CreatureDeathCause.Drowned,
                CreatureDeathCause.Beached,
                CreatureDeathCause.Starved,
            };

            for (int i = 0; i < causes.Length; i++)
            {
                CreatureDeathCause cause = causes[i];
                ParticleWorld world = NewWorld();
                CreatureWorld creatures = new CreatureWorld(SeedValue);

                int deaths = 0;
                Creature reported = null;
                CreatureDeathCause reportedCause = CreatureDeathCause.Unknown;
                creatures.CreatureDied += (creature, deathCause) =>
                {
                    deaths++;
                    reported = creature;
                    reportedCause = deathCause;
                };

                RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 20f);
                rabbit.Kill(cause);
                StepCreatures(world, creatures);

                Assert.AreEqual(1, deaths, $"{cause}：死亡事件应恰好发一次");
                Assert.AreSame(rabbit, reported, $"{cause}：死亡事件应携带该实体");
                Assert.AreEqual(cause, reportedCause, $"{cause}：死亡事件应携带原死因");
                Assert.AreEqual(0, creatures.Count, $"{cause}：清扫后实体表应为空");
                Assert.AreEqual(48, CountType(world, Biomass), $"{cause}：尸体应已按轮廓物质化（死因不参与轮廓选择）");
            }
        }

        /// <summary>世界重建（<see cref="CreatureWorld.Clear"/>）不触发物质化：清空后再 <c>Step</c> 场上无生物质。</summary>
        [Test]
        public void Death_IsIndependentOfWorldRebuild()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 20f);
            rabbit.Kill(CreatureDeathCause.Burned);
            creatures.Clear();

            StepCreatures(world, creatures);

            Assert.AreEqual(0, creatures.Count, "Clear 后实体表应为空");
            Assert.AreEqual(0, CountType(world, Biomass), "Clear（世界重建）不应产生生物质");

            // 再推进一帧确认路径持续无副作用。
            StepCreatures(world, creatures);
            Assert.AreEqual(0, CountType(world, Biomass), "后续帧同样不产生生物质");
        }

        /// <summary>
        /// 物质化后的生物质跑足够逻辑帧（落稳 31 帧 + 腐败期望）→ 场上不再残留生物质，
        /// 剩下只有石头（地板）、腐殖质或空气；且腐殖质不会再变成空气（一次腐败只结算一次）。
        /// </summary>
        [Test]
        public void Materialize_ThenDecays()
        {
            ParticleWorld world = NewWorld();
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FillRows(world, 0, 1, Stone);   // 地板：生物质落稳需要正下方带 Solid 支撑

            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 12f);
            rabbit.Kill(CreatureDeathCause.Starved);
            StepFrame(world, creatures);

            Assert.AreEqual(48, CountType(world, Biomass), "死亡帧应先完成物质化");

            // 落稳 31 帧 + 每格 1/200 的腐败门：6000 帧后每格未腐败概率 ≈ (199/200)^6000 ≈ e^-30。
            for (int i = 0; i < 6000; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(0, CountType(world, Biomass), "足够帧数后不应残留生物质");
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    ushort id = world.GetCell(x, y).typeId;
                    bool ok = id == ParticleIds.Air || id == Humus || id == Stone;
                    Assert.IsTrue(ok, $"腐败终态只应是空气 / 腐殖质 / 石头，但 ({x},{y}) 是 {id}");
                }
            }

            // 腐殖质是显式完成态（不带 Organic）：再跑一段不应把它变成空气，也不该重新长出生物质。
            int humusBefore = CountType(world, Humus);
            for (int i = 0; i < 500; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(humusBefore, CountType(world, Humus), "腐殖质不应再叠加衰变（不会再变成空气）");
            Assert.AreEqual(0, CountType(world, Biomass), "不应重新长出生物质");
        }
    }
}
