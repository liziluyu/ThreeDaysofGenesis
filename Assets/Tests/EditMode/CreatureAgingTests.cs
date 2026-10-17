using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 动物衰老死亡（固定寿命 · 老死复用既有死亡链）的契约测试：
    /// 精确寿命帧语义、三个物种各用自己的寿命、老死路径的零随机数消耗、
    /// 同帧多死因的优先级（饿死优先），以及「阶段高速时长必须短于物种寿命」的配置守卫。
    /// </summary>
    /// <remarks>
    /// <para>地形按 <see cref="ParticleIds"/> 常量摆放，因此全部走生产注册路径
    /// （<see cref="ParticleRegistration.RegisterAll"/>，三参 <c>new ParticleWorld</c>）。</para>
    /// <para>除配置守卫外只调 <c>creatures.Step(world)</c> 而不调 <c>world.StepLogic()</c>：
    /// 动物层只读世界，地形 / 水静止后「寿命帧号」才不会受流体演化干扰。</para>
    /// <para>老死判定发生在 <see cref="Creature.ApplyLocomotion"/> 的 ⑪ 段，早于
    /// <see cref="CreatureWorld.Step"/> 的 <c>AgeTicks++</c>——因此夹具把年龄钉到
    /// <c>LifespanTicks − 1</c> 后，再推进一次仍存活、再推进一次才死亡。</para>
    /// </remarks>
    public sealed class CreatureAgingTests
    {
        private const int W = 32;
        private const int H = 32;
        private const uint SeedValue = 20261011u;

        private static ushort Stone
        {
            get { return ParticleIds.Stone; }
        }

        private static ushort Water
        {
            get { return ParticleIds.Water; }
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

        /// <summary>把兔子的行为钉死在 Idle 长稳态：不摇漫步骰、不觅食，保证随机流比对只覆盖老死路径。</summary>
        private static void PinRabbitToIdle(RabbitCreature rabbit)
        {
            rabbit.AiState = RabbitBrain.StateIdle;
            rabbit.StateTicksLeft = 100000;
            rabbit.HungerTicks = 0;
        }

        /// <summary>
        /// 断言「AgeTicks = lifespan − 1 时再推进一次仍存活、再推进一次老死」，
        /// 并核对死亡事件恰好一次、死因为 <see cref="CreatureDeathCause.OldAge"/>、尸体已清扫。
        /// </summary>
        private static void AssertAgesOutExactly(
            Creature creature, CreatureWorld creatures, ParticleWorld world, int lifespan, string context)
        {
            Assert.AreEqual(lifespan, creature.MaxAgeTicks, context + "：MaxAgeTicks 应等于该物种的寿命常量");

            int deaths = 0;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (died, deathCause) =>
            {
                deaths++;
                cause = deathCause;
            };

            creature.AgeTicks = lifespan - 1;
            creatures.Step(world);

            Assert.IsTrue(creature.Alive, context + "：寿命 − 1 帧时再推进一次仍应存活（判定在 AgeTicks++ 之前）");
            Assert.AreEqual(lifespan, creature.AgeTicks, context + "：这一帧之后 AgeTicks 应恰好等于寿命");
            Assert.AreEqual(0, deaths, context + "：寿命 − 1 帧的推进不应产生死亡事件");

            creatures.Step(world);

            Assert.IsFalse(creature.Alive, context + "：AgeTicks 达到寿命后再推进一次应老死");
            Assert.AreEqual(1, deaths, context + "：老死的死亡事件应恰好发一次");
            Assert.AreEqual(CreatureDeathCause.OldAge, cause, context + "：死因应为 OldAge");
            Assert.AreEqual(0, creatures.Count, context + "：老死尸体应在同一帧被清扫");
        }

        /// <summary>按类名（如 "RabbitCreature"）在可生成物种表里解析原型；查不到返回 null。</summary>
        private static Creature ResolveCreature(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            CreatureSpecies[] species = CreatureSpawns.Species;
            for (int i = 0; i < species.Length; i++)
            {
                Creature prototype = CreatureSpawns.Create(species[i]);
                if (prototype != null && prototype.GetType().Name == name)
                {
                    return prototype;
                }
            }

            return null;
        }

        // ============================== 1. 精确寿命帧 ==============================

        /// <summary>
        /// 兔子：<c>AgeTicks = LifespanTicks − 1</c> 推进一次仍存活且年龄变为寿命；
        /// 再推进一次老死——死因 <see cref="CreatureDeathCause.OldAge"/>、事件恰好一次、清理后 Count 为 0。
        /// </summary>
        [Test]
        public void Aging_DiesExactlyAtLifespan_WithOldAgeCause()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 3, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            int deaths = 0;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                deaths++;
                cause = deathCause;
            };

            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 20f);
            rabbit.AgeTicks = RabbitCreature.LifespanTicks - 1;

            creatures.Step(world);

            Assert.IsTrue(rabbit.Alive, "寿命 − 1 帧时再推进一次仍应存活：判定发生在 AgeTicks++ 之前的反射段");
            Assert.AreEqual(RabbitCreature.LifespanTicks, rabbit.AgeTicks, "这一帧之后 AgeTicks 应恰好等于寿命");
            Assert.AreEqual(0, deaths, "这一帧还不该产生死亡事件");

            creatures.Step(world);

            Assert.IsFalse(rabbit.Alive, "AgeTicks 已达到寿命，再推进一次应老死");
            Assert.AreEqual(1, deaths, "老死的死亡事件应恰好发一次");
            Assert.AreEqual(CreatureDeathCause.OldAge, cause, "死因应为 OldAge");
            Assert.AreEqual(0, creatures.Count, "老死尸体应在同一帧被清扫");
        }

        // ============================== 2. 每个物种用自己的寿命 ==============================

        /// <summary>
        /// 兔 / 鱼 / 鸟各跑一次精确寿命帧语义；三物种寿命常量互不相等且都 &gt; 0，
        /// 且 <see cref="Creature.MaxAgeTicks"/> 分别等于各自常量（opt-in 门没有漏接）。
        /// 鱼放进静水深柱、鸟放空中，避免溺死 / 搁浅路径抢先产生死因。
        /// </summary>
        [Test]
        public void Aging_EverySpeciesUsesItsOwnLifespan()
        {
            Assert.Greater(RabbitCreature.LifespanTicks, 0, "兔子寿命必须为正");
            Assert.Greater(FishCreature.LifespanTicks, 0, "鱼寿命必须为正");
            Assert.Greater(BirdCreature.LifespanTicks, 0, "鸟寿命必须为正");
            Assert.AreNotEqual(RabbitCreature.LifespanTicks, FishCreature.LifespanTicks, "兔 / 鱼寿命不应相同（防复制粘贴漏改）");
            Assert.AreNotEqual(RabbitCreature.LifespanTicks, BirdCreature.LifespanTicks, "兔 / 鸟寿命不应相同（防复制粘贴漏改）");
            Assert.AreNotEqual(FishCreature.LifespanTicks, BirdCreature.LifespanTicks, "鱼 / 鸟寿命不应相同（防复制粘贴漏改）");

            // 兔：石头地板上的陆地个体。
            {
                ParticleWorld world = NewWorld();
                FillRows(world, 0, 3, Stone);
                CreatureWorld creatures = new CreatureWorld(SeedValue);
                RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 20f);
                AssertAgesOutExactly(rabbit, creatures, world, RabbitCreature.LifespanTicks, "兔子");
            }

            // 鱼：够深的静水柱里（石头池底），不会触发搁浅 / 溺死。
            {
                ParticleWorld world = NewWorld();
                FillRows(world, 0, 3, Stone);
                FillRect(world, 4, 27, 4, 20, Water);
                CreatureWorld creatures = new CreatureWorld(SeedValue);
                FishCreature fish = creatures.Spawn(new FishCreature(), world, 16f, 8f);
                AssertAgesOutExactly(fish, creatures, world, FishCreature.LifespanTicks, "鱼");
            }

            // 鸟：空中悬停（FliesInAir 挂起重力），不接触水 / 地面。
            {
                ParticleWorld world = NewWorld();
                CreatureWorld creatures = new CreatureWorld(SeedValue);
                BirdCreature bird = creatures.Spawn(new BirdCreature(), world, 16f, 24f);
                AssertAgesOutExactly(bird, creatures, world, BirdCreature.LifespanTicks, "鸟");
            }
        }

        // ============================== 3. 不消耗随机流 ==============================

        /// <summary>
        /// 同 seed 的有 / 无兔子两套世界推进后，动物随机流与粒子随机流都必须逐位一致——
        /// 老死判定、<see cref="Creature.Kill"/>、死亡物质化全都不该抽随机数。
        /// </summary>
        /// <remarks>
        /// <para><b>动物流：</b>实验兔被钉在 Idle 长稳态（不摇漫步骰、无草可吃、400 帧远小于饿死阈值），
        /// 因此两边的唯一差异就是「第 2 帧老死」；400 帧后比较 32 次 <c>NextUInt</c>。</para>
        /// <para><b>粒子流：</b>只推进 2 帧（老死当帧清扫完成）后立刻比较。不要把窗口拉长到生物质落稳之后：
        /// 那时腐败骰（1/200）本就消耗世界随机流，会把这条断言变成假红。</para>
        /// </remarks>
        [Test]
        public void Aging_DoesNotConsumeRng()
        {
            // —— 动物随机流 ——
            ParticleWorld withAnimal = NewWorld();
            ParticleWorld withoutAnimal = NewWorld();
            FillRows(withAnimal, 0, 3, Stone);
            FillRows(withoutAnimal, 0, 3, Stone);
            withAnimal.RandomTicksPerStep = 0;
            withoutAnimal.RandomTicksPerStep = 0;

            CreatureWorld animalWorld = new CreatureWorld(SeedValue);
            CreatureWorld soloWorld = new CreatureWorld(SeedValue);

            RabbitCreature rabbit = animalWorld.Spawn(new RabbitCreature(), withAnimal, 16f, 20f);
            PinRabbitToIdle(rabbit);
            rabbit.AgeTicks = RabbitCreature.LifespanTicks - 1;

            for (int i = 0; i < 400; i++)
            {
                animalWorld.Step(withAnimal);
                soloWorld.Step(withoutAnimal);
            }

            Assert.AreEqual(0, animalWorld.Count, "兔子应在窗口内老死并被清扫（否则本用例没测到老死路径）");

            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(soloWorld.NextUInt(), animalWorld.NextUInt(), "老死判定不得消耗动物随机流");
            }

            // —— 粒子随机流：只比到死亡物质化那一帧 ——
            ParticleWorld worldWith = NewWorld();
            ParticleWorld worldWithout = NewWorld();
            FillRows(worldWith, 0, 3, Stone);
            FillRows(worldWithout, 0, 3, Stone);
            worldWith.RandomTicksPerStep = 0;
            worldWithout.RandomTicksPerStep = 0;

            CreatureWorld deathWorld = new CreatureWorld(SeedValue);
            CreatureWorld emptyWorld = new CreatureWorld(SeedValue);

            RabbitCreature oldRabbit = deathWorld.Spawn(new RabbitCreature(), worldWith, 16f, 20f);
            PinRabbitToIdle(oldRabbit);
            oldRabbit.AgeTicks = RabbitCreature.LifespanTicks - 1;

            for (int i = 0; i < 2; i++)
            {
                deathWorld.Step(worldWith);
                emptyWorld.Step(worldWithout);
            }

            Assert.AreEqual(0, deathWorld.Count, "第 2 帧应已完成老死清扫（否则随机流比对没覆盖死亡物质化）");

            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(worldWithout.NextUInt(), worldWith.NextUInt(), "老死与死亡物质化不得消耗世界随机流");
            }
        }

        // ============================== 4. 同帧多死因：饿死优先 ==============================

        /// <summary>
        /// 兔子同帧同时满足「年龄到寿命」与「饥饿饱和」：⑩ 饿死先于 ⑪ 老死执行，
        /// 死因应报更具体的 <see cref="CreatureDeathCause.Starved"/>。
        /// </summary>
        [Test]
        public void Aging_StarvationWinsOnSameFrame()
        {
            ParticleWorld world = NewWorld();
            FillRows(world, 0, 3, Stone);
            CreatureWorld creatures = new CreatureWorld(SeedValue);

            int deaths = 0;
            CreatureDeathCause cause = CreatureDeathCause.Unknown;
            creatures.CreatureDied += (creature, deathCause) =>
            {
                deaths++;
                cause = deathCause;
            };

            RabbitCreature rabbit = creatures.Spawn(new RabbitCreature(), world, 16f, 20f);
            rabbit.HungerTicks = Creature.MaxHungerTicks;
            rabbit.AgeTicks = RabbitCreature.LifespanTicks;

            creatures.Step(world);

            Assert.IsFalse(rabbit.Alive, "同帧满足饿死与老死时应死亡");
            Assert.AreEqual(1, deaths, "死亡事件应恰好发一次");
            Assert.AreEqual(
                CreatureDeathCause.Starved,
                cause,
                "⑩ 饿死先于 ⑪ 老死：同帧多因应报告更具体的近因");
            Assert.AreEqual(0, creatures.Count, "尸体应在同一帧被清扫");
        }

        // ============================== 5. 阶段高速时长守卫 ==============================

        /// <summary>
        /// 每份发货阶段 JSON 里带 <c>victory.creatures</c> 的物种，其结算前的高速时长
        /// （<c>simulationTicks</c>，0 时回落到设置资产）必须严格短于该物种寿命——
        /// 否则关卡会在中途失去动物、变成不可胜。
        /// </summary>
        [Test]
        public void Aging_StageHighSpeedRunsFitInsideLifespans()
        {
            ParticleSimulationSettings settings = AssetDatabase.LoadAssetAtPath<ParticleSimulationSettings>(
                "Assets/Data/ParticleSimulationSettings.asset");
            Assert.IsNotNull(settings, "设置资产缺失：Assets/Data/ParticleSimulationSettings.asset");

            string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets/Data/Phases" });
            Assert.IsNotEmpty(guids, "阶段目录里没有找到任何 JSON 资产");

            int checkedConditions = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                Assert.IsNotNull(asset, "阶段资产读取失败：" + path);

                Assert.IsTrue(
                    GamePhaseParser.TryParse(asset.text, out GamePhaseData phase, out string error),
                    "阶段 " + asset.name + " 解析失败：" + error);
                Assert.IsNotNull(phase, "阶段解析结果为空：" + asset.name);

                VictoryCondition victory = phase.victory;
                if (victory == null || victory.creatures == null)
                {
                    continue;
                }

                int effectiveTicks = phase.simulationTicks > 0
                    ? phase.simulationTicks
                    : settings.HighSpeedLogicFrames;

                for (int c = 0; c < victory.creatures.Length; c++)
                {
                    CreatureCountCondition condition = victory.creatures[c];
                    Assert.IsNotNull(condition, "阶段 " + asset.name + " 的 victory.creatures[" + c + "] 为空");

                    Creature prototype = ResolveCreature(condition.species);
                    Assert.IsNotNull(
                        prototype,
                        "阶段 " + asset.name + " 的 victory.creatures 里有未知物种：" + condition.species);

                    Assert.Less(
                        effectiveTicks,
                        prototype.MaxAgeTicks,
                        "阶段 " + asset.name + " 要求 " + condition.species + " 存活，但高速时长 "
                            + effectiveTicks + " 帧已达到其寿命 " + prototype.MaxAgeTicks
                            + " 帧——物种会在结算前老死");
                    checkedConditions++;
                }
            }

            Assert.Greater(checkedConditions, 0, "没有任何阶段带 victory.creatures——守卫用例没有生效");
        }
    }
}
