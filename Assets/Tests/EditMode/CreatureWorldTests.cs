using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 动物实体层（<see cref="CreatureWorld"/> / <see cref="RabbitCreature"/>）的物理骨架测试：
    /// 重力下落与落地吸附、静置的逐位稳定、淹没判定与水中缓沉、碰撞白名单（草不挡动物 / 石头挡）、
    /// 水平碰撞与边界夹取、动物层对世界随机流的零消耗、同种子确定性重放、越界生成的边界安全、
    /// 死亡清扫与事件契约、生成事件与坐标夹取，以及合成水生体的浮力不变量（浮力 &gt; 重力 ⇒ 上浮到水面、
    /// 浮力 == 重力 ⇒ 保持深度、两者离水照落）。
    /// </summary>
    /// <remarks>
    /// <b>被测对象：</b>物理骨架断言（重力 / 静置 / 淹没 / 碰撞 / 边界）一律用**无 AI 的被动物理体**
    /// （<c>PassiveBody</c> / <c>PassiveSwimmer</c>，尺寸同物种、浮力由构造参数给定、钩子全空）——物种自带 AI 后会自主移动，
    /// 拿它们当被动物理体会让「静置 X 不漂移」这类断言随决策漂移；物种行为由 <c>CreatureAiTests</c> 覆盖。
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

        // ============================== 测试专用被动物理体 ==============================

        /// <summary>
        /// 无 AI 的被动物理体：与兔子**同尺寸、同物理常数**（重力 0.24 / 终端 2.4 / 浮力 0.15 / 水阻 0.5），
        /// 但三个钩子全用基类空体——因此意图恒为 0、永不自主移动。
        /// </summary>
        /// <remarks>
        /// 本文件测的是**物理骨架**（落地吸附 / 静置逐位稳定 / 边界夹取 / 水中缓沉）。物种现在带 AI
        /// （<c>RabbitBrain</c> / <c>FishBrain</c>），拿 <see cref="RabbitCreature"/> 当被动物理体会让
        /// 「静置 X 不漂移」这类断言随漫步决策漂移；物种行为另由 <c>CreatureAiTests</c> 覆盖。
        /// </remarks>
        private sealed class PassiveBody : Creature
        {
            public PassiveBody()
                : base(1.5f, 1f)
            {
            }

            protected override float Gravity
            {
                get { return 0.24f; }
            }

            protected override float MaxFallSpeed
            {
                get { return 2.4f; }
            }

            protected override float Buoyancy
            {
                get { return 0.15f; }
            }

            public override string DebugName
            {
                get { return "被动物理体"; }
            }

            public override UnityEngine.Color32 PlaceholderColor
            {
                get { return new UnityEngine.Color32(150, 100, 60, 255); }
            }
        }

        /// <summary>
        /// 无 AI 的**合成**水生体：尺寸 1 × 0.5（重力 0.24 / 终端 1.2 / 水阻 0.6），浮力由构造参数给定
        /// （默认 0.25 = **大于**重力）。它已不对应任何具体物种（鱼的浮力现为 0.24 = 中性），只用来钉
        /// 「浮力 vs 重力决定上浮 / 保持 / 下沉」这条**物理公式**不变量：浮力 &gt; 重力 ⇒ 水中上浮、
        /// 浮力 == 重力 ⇒ 保持深度；两者离水都照落。
        /// </summary>
        private sealed class PassiveSwimmer : Creature
        {
            private readonly float _buoyancy;

            /// <param name="buoyancy">每帧净浮力（默认 0.25 &gt; 重力 0.24）。</param>
            public PassiveSwimmer(float buoyancy = 0.25f)
                : base(1f, 0.5f)
            {
                _buoyancy = buoyancy;
            }

            protected override float Gravity
            {
                get { return 0.24f; }
            }

            protected override float MaxFallSpeed
            {
                get { return 1.2f; }
            }

            protected override float Buoyancy
            {
                get { return _buoyancy; }
            }

            protected override float WaterDrag
            {
                get { return 0.6f; }
            }

            public override string DebugName
            {
                get { return "被动水生体"; }
            }

            public override UnityEngine.Color32 PlaceholderColor
            {
                get { return new UnityEngine.Color32(86, 130, 180, 255); }
            }
        }

        // ============================== 重力与落地 ==============================

        /// <summary>
        /// 沙地上空生成的被动物理体受重力下落，落地时吸附到沙面顶（第 3 行之上 → Y = 4）并判为着地、
        /// 竖直速度归零；再步进 50 帧位置逐位不变（无随机、无累积漂移）。
        /// </summary>
        [Test]
        public void Gravity_RabbitFallsAndRestsOnSandTop()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 3, Sand);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            PassiveBody body = creatures.Spawn(new PassiveBody(), world, 16f, 10f);

            for (int i = 0; i < 60; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(4f, body.Y, "脚底应吸附在沙面顶（沙占 0-3 行）");
            Assert.IsTrue(body.IsGrounded, "落稳后脚下应有支撑");
            Assert.AreEqual(0f, body.VelocityY, "落地后竖直速度应归零");

            float restX = body.X;
            float restY = body.Y;
            for (int i = 0; i < 50; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(restX, body.X, "静置后 X 不应漂移");
            Assert.AreEqual(restY, body.Y, "静置后 Y 不应漂移");
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
            PassiveBody body = creatures.Spawn(new PassiveBody(), world, 16f, 1f);

            for (int i = 0; i < 100; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(16f, body.X, "静置本体的 X 应逐位不变");
            Assert.AreEqual(1f, body.Y, "静置本体的 Y 应逐位不变（停在石头面顶）");
            Assert.IsTrue(body.IsGrounded, "静置中应始终判为着地");
        }

        // ============================== 淹没与缓沉 ==============================

        /// <summary>
        /// 水池中的被动物理体应判为淹没，并以远低于自由落体的速度缓慢下沉（每帧降幅不超过 0.25 格，
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
            PassiveBody body = creatures.Spawn(new PassiveBody(), world, 16f, 3f);

            bool sawSubmerged = false;
            float prevY = body.Y;
            for (int i = 0; i < 120 && !body.IsGrounded; i++)
            {
                StepFrame(world, creatures);

                if (body.IsSubmerged)
                {
                    sawSubmerged = true;
                }

                if (!body.IsGrounded)
                {
                    float drop = prevY - body.Y;
                    Assert.GreaterOrEqual(drop, 0f, "水中不应上浮（浮力小于重力）");
                    Assert.LessOrEqual(drop, 0.25f, "水中下沉应远慢于自由落体（水阻 + 缓沉浮力）");
                }

                prevY = body.Y;
            }

            Assert.IsTrue(sawSubmerged, "沉底前应至少有一个逻辑帧判为淹没");
            Assert.IsTrue(body.IsGrounded, "本体最终应沉到池底");
            Assert.AreEqual(1f, body.Y, "池底为石头第 0 行，脚底应停在其顶面");
        }

        // ============================== 碰撞白名单 ==============================

        /// <summary>
        /// 草不参与动物碰撞：土（0-3 行）+ 草（第 4 行）之上落下的被动体穿过草格、停在<b>土层顶</b>（Y = 4）。
        /// 对照：把第 4 行换成石头后，本体停在<b>石头层顶</b>（Y = 5）——同一落点差异只可能来自白名单。
        /// </summary>
        [Test]
        public void CollisionWhitelist_GrassIsPassable_ButStoneBlocks()
        {
            ParticleWorld grassWorld = NewWorld();
            FillRows(grassWorld, 0, 3, Soil);
            FillRows(grassWorld, 4, 4, Grass);
            CreatureWorld grassCreatures = new CreatureWorld(SeedValue);
            PassiveBody inGrass = grassCreatures.Spawn(new PassiveBody(), grassWorld, 16f, 8f);
            for (int i = 0; i < 60; i++)
            {
                StepFrame(grassWorld, grassCreatures);
            }

            Assert.AreEqual(4f, inGrass.Y, "草不挡动物：脚底应穿过草格、停在土层顶（第 3 行之上）");

            ParticleWorld stoneWorld = NewWorld();
            FillRows(stoneWorld, 0, 3, Soil);
            FillRows(stoneWorld, 4, 4, Stone);
            CreatureWorld stoneCreatures = new CreatureWorld(SeedValue);
            PassiveBody onStone = stoneCreatures.Spawn(new PassiveBody(), stoneWorld, 16f, 8f);
            for (int i = 0; i < 60; i++)
            {
                StepFrame(stoneWorld, stoneCreatures);
            }

            Assert.AreEqual(5f, onStone.Y, "石头挡动物：脚底应停在石头层顶（第 4 行之上）");
        }

        // ============================== 水平碰撞与边界 ==============================

        /// <summary>
        /// 石墙（第 16 列，1-8 行）挡住右移的被动物理体：脚底中心被挡在墙左缘之外（X ≤ 16 − 半宽 = 15.25），
        /// 且顶住后不再前进；反向给左移速度则被世界左边界夹住（X = Width/2 = 0.75）并保持不动。
        /// </summary>
        [Test]
        public void HorizontalCollision_WallStopsRightward_BoundaryClampsLeftward()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 0, Stone);
            FillRect(world, 16, 16, 1, 8, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            PassiveBody runner = creatures.Spawn(new PassiveBody(), world, 10f, 1f);
            runner.VelocityX = 0.5f;
            for (int i = 0; i < 40; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.Greater(runner.X, 10f, "右移的本体应确实前进过");
            Assert.LessOrEqual(runner.X, 16f - RabbitHalfWidth + Tol, "右缘应被石墙左面挡住");

            float stopped = runner.X;
            for (int i = 0; i < 20; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.AreEqual(stopped, runner.X, Tol, "顶住石墙后不应继续前进");

            PassiveBody lefty = creatures.Spawn(new PassiveBody(), world, 10f, 1f);
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
        /// <remarks>
        /// 本测试**刻意不铺任何地形**：兔子被夹到 Y = 0 后由 <c>Y &lt;= 0</c> 直接判为着地（见
        /// <see cref="Creature.StepPhysics"/> 收尾段），不需要地板；若铺一层石地板，兔子身体会嵌在石格内、
        /// 连续 <see cref="Creature.BuriedTicksLimit"/> 帧后被压死而提前离场，届时下面的三只 / 界内断言会失去意义。
        /// </remarks>
        [Test]
        public void BoundarySafety_OutOfBoundsSpawnsStayInside()
        {
            ParticleWorld world = NewWorld();
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

            Assert.AreEqual(3, creatures.Count, "空世界里没有任何固体，三只兔子都不应被压死 / 清扫");

            TestContext.WriteLine(
                "BoundarySafety 实测：Count = " + creatures.Count
                + "；atLeftBottom = (" + atLeftBottom.X + ", " + atLeftBottom.Y + ")"
                + "；farRightTop = (" + farRightTop.X + ", " + farRightTop.Y + ")"
                + "；huggingLeft = (" + huggingLeft.X + ", " + huggingLeft.Y + ")");

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

        // ============================== 水中浮力（合成水生体） ==============================

        /// <summary>
        /// 中性浮力（浮力 == 重力）的合成水生体**保持深度**：水柱第 0..15 行，从 Y = 4 出发，
        /// 净竖直加速度为 0 ⇒ 300 帧后仍停在入水深度附近（浮力 0.25 &gt; 重力 0.24 则会升到 ≈15.75）。
        /// </summary>
        [Test]
        public void NeutralBuoyancy_HoldsDepth()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            PassiveSwimmer swimmer = creatures.Spawn(new PassiveSwimmer(0.24f), world, 16f, 4f);

            for (int i = 0; i < 300; i++)
            {
                StepFrame(world, creatures);
            }

            Assert.LessOrEqual(UnityEngine.Mathf.Abs(swimmer.Y - 4f), 0.5f,
                "浮力 == 重力时不应上浮 / 下沉，应保持入水深度（浮力 0.25 会升到 ≈15.75）");
            Assert.AreEqual(1, creatures.Count, "保持深度的合成水生体不应被清扫");
        }

        /// <summary>
        /// 浮力 &gt; 重力的合成水生体**上浮**：水柱第 0..15 行，从 Y = 2 出发，若干帧后应升到水面附近并停在那里——
        /// 浮力 0.25 **大于**重力 0.24（净 +0.01），这是「浮力 &gt; 重力 ⇒ 上浮」这条**物理公式**不变量的可观察后果。
        /// 同时断言它不会冲出水柱：身体中心一离开水格，浮力消失，只剩重力把它拉回。
        /// </summary>
        [Test]
        public void Buoyancy_SyntheticSwimmerRisesThroughWaterAndStopsAtSurface()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 15, Water);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            PassiveSwimmer swimmer = creatures.Spawn(new PassiveSwimmer(), world, 16f, 2f);

            float startY = swimmer.Y;
            float peakY = startY;
            for (int i = 0; i < 1200; i++)
            {
                StepFrame(world, creatures);
                if (swimmer.Y > peakY)
                {
                    peakY = swimmer.Y;
                }
            }

            // 水格是第 0..15 行，水面 = 第 15 行顶面（y = 16）。身体中心在水里的条件是 floor(Y + 0.25) <= 15。
            Assert.Greater(swimmer.Y, startY + 10f, "合成水生体应自水中持续上浮（浮力大于重力），而不是原地不动或下沉");
            Assert.GreaterOrEqual(swimmer.Y, 14.5f, "应到达水面附近");
            Assert.LessOrEqual(peakY, 16.5f, "不应冲出水柱太远——离开水格后没有浮力");
            Assert.AreEqual(1, creatures.Count, "上浮过程中不应被清扫掉");
        }

        /// <summary>
        /// 离水的被动水生体像普通重物一样落下，落到沙面顶（第 3 行之上 → Y = 4）并判为着地、竖直速度归零——
        /// 水生行为来自「水里才有浮力」这一条，而不是无条件悬停。
        /// </summary>
        [Test]
        public void OutOfWater_FishFallsAndRestsOnSandTop()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 3, Sand);
            CreatureWorld creatures = new CreatureWorld(SeedValue);
            PassiveSwimmer fish = creatures.Spawn(new PassiveSwimmer(), world, 16f, 12f);

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
