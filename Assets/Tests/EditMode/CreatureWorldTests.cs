using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 动物实体层（<see cref="CreatureWorld"/> / <see cref="RabbitCreature"/>）的物理骨架测试：
    /// 重力下落与落地吸附、静置的逐位稳定、淹没判定与水中缓沉、碰撞白名单（草不挡动物 / 石头挡）、
    /// 水平碰撞与边界夹取、动物层对世界随机流的零消耗、同种子确定性重放、越界生成的边界安全、
    /// 死亡清扫与事件契约、生成事件与坐标夹取，以及鱼的物种差异（浮力 > 重力 ⇒ 水中上浮到水面、离水照落）。
    /// </summary>
    /// <remarks>
    /// 碰撞白名单按 <see cref="ParticleIds"/> 常量比对（沙 / 石头 / 土 / 湿土 / 腐殖土 / 湿润腐殖土 / 水），
    /// 因此全部走<b>生产注册路径</b>（<see cref="ParticleRegistration.RegisterAll"/>）。
    /// 世界用三参构造 <c>new ParticleWorld(w, h, seed)</c>，地形直接 <c>world.Set</c> 摆放；
    /// 一个逻辑帧 = <c>world.StepLogic()</c> 后接一次 <c>creatures.Step(world)</c>（与驱动同序）。
    /// 落地是吸附到整数行边界（行 y 的顶面 = y + 1），故落地位置可用 float 精确相等断言；
    /// 运动中的位置一律用 1e-4 容差。
    /// </remarks>
    public sealed class CreatureWorldTests
    {
        private const int W = 32;
        private const int H = 32;
        private const uint SeedValue = 20261011u;

        /// <summary>兔子碰撞盒半宽（格）。</summary>
        private const float RabbitHalfWidth = 0.75f;

        /// <summary>兔子碰撞盒高度（格）。</summary>
        private const float RabbitHeight = 1f;

        /// <summary>位置比较容差（运动中）。</summary>
        private const float Tol = 1e-4f;

        // 注册在 SetUp 里完成，故用属性而非 const。
        private static ushort Sand
        {
            get { return ParticleIds.Sand; }
        }

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Soil
        {
            get { return ParticleIds.Soil; }
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
        }

        private static ushort Grass
        {
            get { return ParticleIds.Grass; }
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

        /// <summary>把 [x0, x1] × [y0, y1] 矩形填成同一类型（闭区间）。</summary>
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

        /// <summary>一个逻辑帧：先推进世界、再推进动物层（与驱动的调用顺序一致）。</summary>
        private static void StepFrame(ParticleWorld world, CreatureWorld creatures)
        {
            world.StepLogic();
            creatures.Step(world);
        }

        /// <summary>逐格比对两张网格的 typeId；首个不等处直接失败并给出下标。</summary>
        private static void AssertTypeIdsMatch(ParticleWorld expected, ParticleWorld actual, string context)
        {
            for (int i = 0; i < expected.CellCount; i++)
            {
                ushort a = expected.Cells[i].typeId;
                ushort b = actual.Cells[i].typeId;
                if (a != b)
                {
                    Assert.Fail($"{context}：第 {i} 格 typeId 不同（{a} vs {b}）——动物层不该消耗世界的随机流");
                }
            }
        }

        /// <summary>实体必须始终落在世界边界内（脚底中点的合法区间）。</summary>
        private static void AssertInsideBounds(Creature creature, ParticleWorld world, string context)
        {
            Assert.GreaterOrEqual(creature.X, RabbitHalfWidth - Tol, $"{context}：X 不应越过左边界");
            Assert.LessOrEqual(creature.X, world.Width - RabbitHalfWidth + Tol, $"{context}：X 不应越过右边界");
            Assert.GreaterOrEqual(creature.Y, -Tol, $"{context}：Y 不应小于 0");
            Assert.LessOrEqual(creature.Y, world.Height - RabbitHeight + Tol, $"{context}：Y 不应越过顶边");
        }

        // ============================== 重力与落地 ==============================

        /// <summary>
        /// 沙地上空生成的兔子受重力下落，落地时吸附到沙面顶（第 3 行之上 → Y = 4）并判为着地、
        /// 竖直速度归零；再步进 50 帧位置逐位不变（无随机、无累积漂移）。
        /// </summary>
        [Test]
        public void Gravity_RabbitFallsAndRestsOnSandTop()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 3, Sand);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 10f);

            for (int i = 0; i < 60; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(4f, rabbit.Y, "脚底应吸附在沙面顶（沙占 0-3 行）");
            Assert.IsTrue(rabbit.IsGrounded, "落稳后脚下应有支撑");
            Assert.AreEqual(0f, rabbit.VelocityY, "落地后竖直速度应归零");

            float restX = rabbit.X;
            float restY = rabbit.Y;
            for (int i = 0; i < 50; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(restX, rabbit.X, "静置后 X 不应漂移");
            Assert.AreEqual(restY, rabbit.Y, "静置后 Y 不应漂移");
        }

        // ============================== 静置稳定 ==============================

        /// <summary>
        /// 石头地面上静置 100 帧：X / Y 逐位不变（重力每帧被支撑消解、无水平驱动、无随机累积）。
        /// </summary>
        [Test]
        public void Rest_OnStoneFloor_IsBitExactFor100Ticks()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 1f);

            for (int i = 0; i < 100; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(16f, rabbit.X, "静置兔子的 X 应逐位不变");
            Assert.AreEqual(1f, rabbit.Y, "静置兔子的 Y 应逐位不变（停在石头面顶）");
            Assert.IsTrue(rabbit.IsGrounded, "静置中应始终判为着地");
        }

        // ============================== 淹没与缓沉 ==============================

        /// <summary>
        /// 水池中的兔子应判为淹没，并以远低于自由落体的速度缓慢下沉（每帧降幅不超过 0.25 格，
        /// 自由落体会立刻到 0.8），最终沉到池底石面并判为着地。
        /// </summary>
        [Test]
        public void Submerged_RabbitSinksSlowlyToPoolBottom()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 0, 0, 1, 7, Stone);
            FillRect(world, W - 1, W - 1, 1, 7, Stone);
            FillRect(world, 1, W - 2, 1, 6, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 3f);

            bool sawSubmerged = false;
            float prevY = rabbit.Y;
            for (int i = 0; i < 120 && !rabbit.IsGrounded; i++)
            {
                StepFrame(world, creatures);

                if (rabbit.IsSubmerged)
                {
                    sawSubmerged = true;
                }

                if (!rabbit.IsGrounded)
                {
                    float drop = prevY - rabbit.Y;
                    Assert.GreaterOrEqual(drop, 0f, "水中不应上浮（浮力小于重力）");
                    Assert.LessOrEqual(drop, 0.25f, "水中下沉应远慢于自由落体（水阻 + 缓沉浮力）");
                }

                prevY = rabbit.Y;
            }

            Assert.IsTrue(sawSubmerged, "沉底前应至少有一个逻辑帧判为淹没");
            Assert.IsTrue(rabbit.IsGrounded, "兔子最终应沉到池底");
            Assert.AreEqual(1f, rabbit.Y, "池底为石头第 0 行，脚底应停在其顶面");
        }

        // ============================== 碰撞白名单 ==============================

        /// <summary>
        /// 草不参与动物碰撞：土（0-3 行）+ 草（第 4 行）之上落下的兔子穿过草格、停在<b>土层顶</b>（Y = 4）。
        /// 对照：把第 4 行换成石头后，兔子停在<b>石头层顶</b>（Y = 5）——同一落点差异只可能来自白名单。
        /// </summary>
        [Test]
        public void CollisionWhitelist_GrassIsPassable_ButStoneBlocks()
        {
            ParticleWorld grassWorld = NewWorld();
            FillRows(grassWorld, 0, 3, Soil);
            FillRows(grassWorld, 4, 4, Grass);
            CreatureWorld grassCreatures = new CreatureWorld(SeedValue);
            RabbitCreature inGrass = grassCreatures.Spawn(new RabbitCreature(), grassWorld, 16f, 8f);
            for (int i = 0; i < 60; i++)
            {
                StepFrame(grassWorld, grassCreatures);
            }

            Assert.AreEqual(4f, inGrass.Y, "草不挡动物：脚底应穿过草格、停在土层顶（第 3 行之上）");

            ParticleWorld stoneWorld = NewWorld();
            FillRows(stoneWorld, 0, 3, Soil);
            FillRows(stoneWorld, 4, 4, Stone);
            CreatureWorld stoneCreatures = new CreatureWorld(SeedValue);
            RabbitCreature onStone = stoneCreatures.Spawn(new RabbitCreature(), stoneWorld, 16f, 8f);
            for (int i = 0; i < 60; i++)
            {
                StepFrame(stoneWorld, stoneCreatures);
            }

            Assert.AreEqual(5f, onStone.Y, "石头挡动物：脚底应停在石头层顶（第 4 行之上）");
        }

        // ============================== 水平碰撞与边界 ==============================

        /// <summary>
        /// 石墙（第 16 列，1-8 行）挡住右移的兔子：脚底中心被挡在墙左缘之外（X ≤ 16 − 半宽 = 15.25），
        /// 且顶住后不再前进；反向给左移速度则被世界左边界夹住（X = Width/2 = 0.75）并保持不动。
        /// </summary>
        [Test]
        public void HorizontalCollision_WallStopsRightward_BoundaryClampsLeftward()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 16, 16, 1, 8, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            RabbitCreature runner = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            runner.VelocityX = 0.5f;
            for (int i = 0; i < 40; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.Greater(runner.X, 10f, "右移的兔子应确实前进过");
            Assert.LessOrEqual(runner.X, 16f - RabbitHalfWidth + Tol, "右缘应被石墙左面挡住");

            float stopped = runner.X;
            for (int i = 0; i < 20; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(stopped, runner.X, Tol, "顶住石墙后不应继续前进");

            RabbitCreature lefty = creatures.Spawn(new RabbitCreature(), world, 10f, 1f);
            lefty.VelocityX = -0.5f;
            for (int i = 0; i < 40; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(RabbitHalfWidth, lefty.X, Tol, "左移应被世界左边界夹在 Width/2");
            float clamped = lefty.X;
            for (int i = 0; i < 20; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(clamped, lefty.X, Tol, "贴住左边界后不应继续移动");
        }

        // ============================== 随机流隔离 ==============================

        /// <summary>
        /// 两个同种子同地形的世界跑 60 个逻辑帧：一个只 <c>StepLogic</c>，另一个每帧额外
        /// <c>creatures.Step(world)</c>（场内有一只兔子）。逐格 typeId 必须完全一致——
        /// 动物层不消耗世界随机流（世界随机流若被多摇一次，沙堆会立刻分叉）。
        /// </summary>
        [Test]
        public void RngIsolation_CreatureStepDoesNotDisturbWorldRandomStream()
        {
            ParticleWorld solo = NewWorld();
            ParticleWorld withRabbit = NewWorld();
            FillRows(solo, 0, 0, Stone);
            FillRows(withRabbit, 0, 0, Stone);
            FillRect(solo, 8, 24, 20, 24, Sand);
            FillRect(withRabbit, 8, 24, 20, 24, Sand);

            CreatureWorld creatures = new CreatureWorld(SeedValue);
            creatures.Spawn(new RabbitCreature(), withRabbit, 16f, 28f);

            for (int i = 0; i < 60; i++)
            {
                solo.StepLogic();
                withRabbit.StepLogic();
                creatures.Step(withRabbit);
            }

            AssertTypeIdsMatch(solo, withRabbit, "加入动物层后世界状态应逐格不变");
        }

        // ============================== 确定性重放 ==============================

        /// <summary>
        /// 同种子两个 CreatureWorld：相同的生成序列 + 相同步数 → 每只兔子的 X / Y 精确相等
        /// （浮点四则 + floor，无随机、无累积误差）。
        /// </summary>
        [Test]
        public void Determinism_SameSeedAndSpawns_ReplayBitExact()
        {
            ParticleWorld worldA = NewWorld();
            ParticleWorld worldB = NewWorld();
            FillRows(worldA, 0, 0, Stone);
            FillRows(worldB, 0, 0, Stone);

            CreatureWorld a = new CreatureWorld(SeedValue);
            CreatureWorld b = new CreatureWorld(SeedValue);
            float[] spawnX = { 10f, 16f, 22f };
            float[] spawnY = { 15f, 20f, 25f };
            for (int i = 0; i < spawnX.Length; i++)
            {
                a.Spawn(new RabbitCreature(), worldA, spawnX[i], spawnY[i]);
                b.Spawn(new RabbitCreature(), worldB, spawnX[i], spawnY[i]);
            }

            for (int i = 0; i < 60; i++)
            {
                worldA.StepLogic();
                a.Step(worldA);
                worldB.StepLogic();
                b.Step(worldB);
            }

            Assert.AreEqual(a.Count, b.Count, "同生成序列下实体数应一致");
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a.Creatures[i].X, b.Creatures[i].X, $"第 {i} 只兔子的 X 应逐位一致");
                Assert.AreEqual(a.Creatures[i].Y, b.Creatures[i].Y, $"第 {i} 只兔子的 Y 应逐位一致");
            }
        }

        // ============================== 边界安全 ==============================

        /// <summary>
        /// 贴左边界、贴底生成，以及把坐标给到远远越界处：Spawn 应把坐标夹进边界内；
        /// 随后步进 30 帧不抛异常，位置恒在 X ∈ [Width/2, Width − Width/2]、Y ∈ [0, Height − Height] 内。
        /// </summary>
        [Test]
        public void BoundarySafety_OutOfBoundsSpawnsStayInside()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            RabbitCreature atLeftBottom = creatures.Spawn(new RabbitCreature(), world, -10f, -5f);
            Assert.AreEqual(RabbitHalfWidth, atLeftBottom.X, Tol, "越界 X 应夹到左边界 Width/2");
            Assert.AreEqual(0f, atLeftBottom.Y, Tol, "越界 Y 应夹到 0");

            RabbitCreature farRightTop = creatures.Spawn(new RabbitCreature(), world, world.Width + 10f, world.Height + 10f);
            Assert.AreEqual(world.Width - RabbitHalfWidth, farRightTop.X, Tol, "越界 X 应夹到右边界 Width − Width/2");
            Assert.AreEqual(world.Height - RabbitHeight, farRightTop.Y, Tol, "越界 Y 应夹到顶边 Height − Height");

            RabbitCreature huggingLeft = creatures.Spawn(new RabbitCreature(), world, RabbitHalfWidth, 0f);

            for (int i = 0; i < 30; i++)
            {
                StepFrame(world, creatures);
            }

            for (int i = 0; i < creatures.Count; i++)
            {
                AssertInsideBounds(creatures.Creatures[i], world, $"第 {i} 只兔子");
            }

            Assert.AreEqual(RabbitHalfWidth, huggingLeft.X, Tol, "贴左边界的兔子不应被挤出界");
            Assert.GreaterOrEqual(huggingLeft.Y, -Tol, "贴底生成的兔子不应穿出世界底部");
        }

        // ============================== 死亡清扫与事件 ==============================

        /// <summary>
        /// <c>Kill</c> 立即置 <c>Alive = false</c>，但实体在本帧仍在列表里；下一次 <c>Step</c> 才把它移除，
        /// 且 <c>CreatureDied</c> 恰好发一次、携带该实体与正确 cause。二次 <c>Kill</c> 幂等：不多发事件。
        /// </summary>
        [Test]
        public void Death_KillMarksNow_SweepsNextStep_AndRaisesOnce()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 1f);

            int deaths = 0;
            Creature reported = null;
            CreatureDeathCause reportedCause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, cause) =>
            {
                deaths++;
                reported = creature;
                reportedCause = cause;
            };

            rabbit.Kill(CreatureDeathCause.Starved);
            rabbit.Kill(CreatureDeathCause.Starved);

            Assert.IsFalse(rabbit.Alive, "Kill 后应立刻标记为不存活");
            Assert.AreEqual(1, creatures.Count, "死亡实体在本帧（清扫前）应仍在列表里");

            StepFrame(world, creatures);

            Assert.AreEqual(0, creatures.Count, "下一次 Step 后死亡实体应从列表移除");
            Assert.AreEqual(1, deaths, "死亡事件应恰好发一次（二次 Kill 幂等）");
            Assert.AreSame(rabbit, reported, "死亡事件应携带被清扫的实体");
            Assert.AreEqual(CreatureDeathCause.Starved, reportedCause, "死亡事件应携带 Kill 时给定的 cause");
        }

        // ============================== 生成事件与夹取 ==============================

        /// <summary>
        /// <c>Spawn</c> 恰好发一次 <c>CreatureSpawned</c> 并携带返回的实体；越界坐标被夹进世界边界，
        /// 且生成时 <c>Prev</c> 与 <c>Cur</c> 一致（渲染插值不跳变）、实体存活并已入列。
        /// </summary>
        [Test]
        public void Spawn_RaisesOnceWithEntity_AndClampsIntoBounds()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            int spawns = 0;
            Creature seen = null;
            creatures.CreatureSpawned += creature =>
            {
                spawns++;
                seen = creature;
            };

            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, -3f, -2f);

            Assert.AreEqual(1, spawns, "Spawn 应恰好发一次生成事件");
            Assert.AreSame(rabbit, seen, "生成事件应携带返回的实体");
            Assert.AreEqual(RabbitHalfWidth, rabbit.X, Tol, "越界 X 应被夹进左边界");
            Assert.AreEqual(0f, rabbit.Y, Tol, "越界 Y 应被夹到 0");
            Assert.AreEqual(rabbit.X, rabbit.PrevX, "生成时 Prev 应与 Cur 一致");
            Assert.AreEqual(rabbit.Y, rabbit.PrevY, "生成时 Prev 应与 Cur 一致");
            Assert.IsTrue(rabbit.Alive, "生成的实体应存活");
            Assert.AreEqual(1, creatures.Count, "生成的实体应已入列");
        }

        // ============================== 鱼（水生物种） ==============================

        /// <summary>
        /// 水中的鱼**上浮**：水柱第 0..15 行，鱼从 Y = 2 出发，若干帧后应升到水面附近并停在那里——
        /// 净浮力（+0.09）大于重力（0.08），这是「浮力 > 重力」这条设计不变量的可观察后果。
        /// 同时断言它不会冲出水柱：身体中心一离开水格，浮力消失，只剩重力把它拉回。
        /// </summary>
        [Test]
        public void Buoyancy_FishRisesThroughWaterAndStopsAtSurface()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 2f);

            float startY = fish.Y;
            float peakY = startY;
            for (int i = 0; i < 1200; i++)
            {
                StepFrame(world, creatures);
                if (fish.Y > peakY)
                {
                    peakY = fish.Y;
                }
            }

            // 水格是第 0..15 行，水面 = 第 15 行顶面（y = 16）。身体中心在水里的条件是 floor(Y + 0.25) <= 15。
            Assert.Greater(fish.Y, startY + 10f, "鱼应自水中持续上浮（浮力大于重力），而不是原地不动或下沉");
            Assert.GreaterOrEqual(fish.Y, 14.5f, "鱼应到达水面附近");
            Assert.LessOrEqual(peakY, 16.5f, "鱼不应冲出水柱太远——离开水格后没有浮力");
            Assert.AreEqual(1, creatures.Count, "上浮过程中鱼不应被清扫掉");
        }

        /// <summary>
        /// 离水的鱼像普通重物一样落下，落到沙面顶（第 3 行之上 → Y = 4）并判为着地、竖直速度归零——
        /// 鱼的水性行为来自「水里才有浮力」这一条，而不是无条件悬停。
        /// </summary>
        [Test]
        public void OutOfWater_FishFallsAndRestsOnSandTop()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 3, Sand);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 12f);

            for (int i = 0; i < 200; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(4f, fish.Y, Tol, "鱼应落在沙面顶（第 3 行之上）");
            Assert.AreEqual(0f, fish.VelocityY, Tol, "落地后竖直速度应归零");
            Assert.IsTrue(fish.IsGrounded, "落地后应判为着地");
            Assert.IsFalse(fish.IsSubmerged, "空气里不该判为淹没");
        }
    }
}
