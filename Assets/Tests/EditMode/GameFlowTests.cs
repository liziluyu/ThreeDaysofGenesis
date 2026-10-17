using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 游戏流程的纯 C# 内核：阶段 JSON 解析（<see cref="GamePhaseParser"/>）与胜利条件判定
    /// （<see cref="VictoryEvaluator"/>）。流程编排、对话联动、存档、高速模式轮询在 <c>GameController</c> 里，
    /// 不进 EditMode。
    /// </summary>
    /// <remarks>
    /// 粒子名解析依赖**全局注册表**，故 <see cref="SetUp"/> 里先 <see cref="ParticleRegistration.RegisterAll"/>
    /// 并统一用 <see cref="ParticleIds"/> 取 typeId。
    /// <para>胜利条件省略 / 全哨兵 / <c>particles</c> 为空的写法一律归一为 <c>null</c> = 本阶段没有胜利条件
    /// （沙盒阶段）；只要有一条非 NaN 界限或非空 <c>particles</c> 就保留原实例。</para>
    /// <para><c>description</c> 是纯 UI 元数据（关卡选择界面悬停显示），可空且不参与任何校验：
    /// 省略 → <c>null</c>、显式空串 → <c>""</c>，都由调用方一视同仁地当作「本阶段没有描述」。</para>
    /// </remarks>
    public sealed class GameFlowTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        // ============================== 解析 ==============================

        /// <summary>合法 JSON 的全字段都能解析出来（含嵌套胜利条件与计数条目）。</summary>
        [Test]
        public void TryParse_ValidJson_ParsesAllFields()
        {
            const string json =
                "{" +
                "\"dialogueId\":\"example\"," +
                "\"nextPhaseId\":\"phase_02\"," +
                "\"sandboxWidth\":160," +
                "\"sandboxHeight\":90," +
                "\"availableParticles\":[\"SandParticle\",\"WaterParticle\",\"SoilParticle\",\"GrassSeedParticle\"]," +
                "\"victory\":{" +
                "\"minSurfaceTemperature\":20," +
                "\"maxSurfaceTemperature\":40," +
                "\"minAverageHumidity\":0.05," +
                "\"maxAverageHumidity\":0.5," +
                "\"particles\":[{\"type\":\"GrassParticle\",\"minBirths\":5,\"maxBirths\":50,\"minDeaths\":0,\"maxDeaths\":10,\"minCount\":2,\"maxCount\":20,\"failureText\":\"草没能存活下来\"}]" +
                "}" +
                "}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);
            Assert.IsNull(error);

            Assert.AreEqual("example", phase.dialogueId);
            Assert.AreEqual("phase_02", phase.nextPhaseId);
            Assert.AreEqual(160, phase.sandboxWidth);
            Assert.AreEqual(90, phase.sandboxHeight);
            Assert.AreEqual(4, phase.availableParticles.Length);
            Assert.AreEqual("SandParticle", phase.availableParticles[0]);
            Assert.AreEqual("GrassSeedParticle", phase.availableParticles[3]);

            Assert.IsNotNull(phase.victory);
            Assert.AreEqual(20f, phase.victory.minSurfaceTemperature);
            Assert.AreEqual(40f, phase.victory.maxSurfaceTemperature);
            Assert.AreEqual(0.05f, phase.victory.minAverageHumidity);
            Assert.AreEqual(0.5f, phase.victory.maxAverageHumidity);

            Assert.AreEqual(1, phase.victory.particles.Length);
            ParticleCountCondition condition = phase.victory.particles[0];
            Assert.AreEqual("GrassParticle", condition.type);
            Assert.AreEqual(5, condition.minBirths);
            Assert.AreEqual(50, condition.maxBirths);
            Assert.AreEqual(0, condition.minDeaths);
            Assert.AreEqual(10, condition.maxDeaths);
            Assert.AreEqual(2, condition.minCount);
            Assert.AreEqual(20, condition.maxCount);
            Assert.AreEqual("草没能存活下来", condition.failureText);
        }

        /// <summary>可空字段（对话 / 下一阶段 / 白名单 / 胜利条件）缺省时保持初始值（null）；id 由加载方回填，解析器不填。
        /// 胜利条件省略即归一为 null = 无胜利条件。</summary>
        [Test]
        public void TryParse_MissingOptionalFields_KeepsDefaults()
        {
            GamePhaseData phase;
            string error;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out phase, out error),
                error);

            Assert.IsNull(phase.id);
            Assert.IsNull(phase.dialogueId);
            Assert.IsNull(phase.nextPhaseId);
            Assert.IsNull(phase.availableParticles);
            Assert.IsNull(phase.victory);
        }

        /// <summary>可空语义钉死：<c>dialogueId</c> 省略 → null；显式空串 → <c>""</c>。
        /// 两者都让 <c>GameController</c> 的 <c>IsNullOrEmpty</c> 判定为「无对话」而跳过。</summary>
        [Test]
        public void TryParse_EmptyOrOmittedDialogueId_MeansNoDialogue()
        {
            string error;

            GamePhaseData omitted;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out omitted, out error),
                error);
            Assert.IsNull(omitted.dialogueId);
            Assert.IsTrue(string.IsNullOrEmpty(omitted.dialogueId));

            GamePhaseData blank;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"dialogueId\":\"\",\"sandboxWidth\":10,\"sandboxHeight\":10}", out blank, out error),
                error);
            Assert.AreEqual(string.Empty, blank.dialogueId);
            Assert.IsTrue(string.IsNullOrEmpty(blank.dialogueId));
        }

        /// <summary>可空语义钉死：<c>availableParticles</c> 省略 → null；显式空数组 → 长度为 0。
        /// 两者都让 <c>SetPlacementFilter</c> 视为「不限制」、恢复默认白名单。</summary>
        [Test]
        public void TryParse_EmptyOrOmittedAvailableParticles_MeansNoRestriction()
        {
            string error;

            GamePhaseData omitted;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out omitted, out error),
                error);
            Assert.IsNull(omitted.availableParticles);

            GamePhaseData empty;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"availableParticles\":[],\"sandboxWidth\":10,\"sandboxHeight\":10}", out empty, out error),
                error);
            Assert.IsNotNull(empty.availableParticles);
            Assert.AreEqual(0, empty.availableParticles.Length);
        }

        /// <summary><c>simulationTicks</c> 写出即原样解析；省略 → 0（= 用设置资产的默认高速时长）。</summary>
        [Test]
        public void TryParse_SimulationTicks_RoundTripsAndDefaultsToZero()
        {
            string error;

            GamePhaseData specified;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"simulationTicks\":2000,\"sandboxWidth\":10,\"sandboxHeight\":10}", out specified, out error),
                error);
            Assert.AreEqual(2000, specified.simulationTicks);

            GamePhaseData omitted;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out omitted, out error),
                error);
            Assert.AreEqual(0, omitted.simulationTicks);
        }

        /// <summary>负的模拟时长是配置错误（会倒退 tick 计数），解析失败并给出错误描述。</summary>
        [Test]
        public void TryParse_NegativeSimulationTicks_Fails()
        {
            GamePhaseData phase;
            string error;
            Assert.IsFalse(
                GamePhaseParser.TryParse(
                    "{\"simulationTicks\":-1,\"sandboxWidth\":10,\"sandboxHeight\":10}", out phase, out error),
                "负的 simulationTicks 必须解析失败");
            Assert.IsNull(phase);
            StringAssert.Contains("simulationTicks", error);
        }

        /// <summary>动物白名单的可空语义：省略 → null（不限制）；显式空数组 → 长度为 0（本阶段没有动物）。</summary>
        [Test]
        public void TryParse_AvailableCreatures_OmittedMeansUnrestricted()
        {
            string error;

            GamePhaseData omitted;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out omitted, out error),
                error);
            Assert.IsNull(omitted.availableCreatures, "省略 = 不限制动物");

            GamePhaseData empty;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"availableCreatures\":[],\"sandboxWidth\":10,\"sandboxHeight\":10}", out empty, out error),
                error);
            Assert.IsNotNull(empty.availableCreatures, "显式空数组不能归一成 null");
            Assert.AreEqual(0, empty.availableCreatures.Length);

            GamePhaseData listed;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"availableCreatures\":[\"RabbitCreature\"],\"sandboxWidth\":10,\"sandboxHeight\":10}",
                    out listed,
                    out error),
                error);
            Assert.AreEqual("RabbitCreature", listed.availableCreatures[0]);
        }

        /// <summary>只有动物约束的胜利条件不能被归一成 null（只有全哨兵才是「没有胜利条件」）。</summary>
        [Test]
        public void TryParse_CreatureOnlyVictory_IsKept()
        {
            const string json =
                "{\"sandboxWidth\":10,\"sandboxHeight\":10," +
                "\"victory\":{\"creatures\":[{\"species\":\"RabbitCreature\",\"minCount\":1,\"failureText\":\"兔子没能存活下来\"}]}}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsNotNull(phase.victory, "只有动物约束的 victory 必须保留");
            Assert.AreEqual(1, phase.victory.creatures.Length);
            Assert.AreEqual("RabbitCreature", phase.victory.creatures[0].species);
            Assert.AreEqual(1, phase.victory.creatures[0].minCount);
            Assert.AreEqual(-1, phase.victory.creatures[0].maxCount, "未写的上限保持 -1 = 不约束");
            Assert.AreEqual("兔子没能存活下来", phase.victory.creatures[0].failureText);
        }

        /// <summary>动物约束缺 <c>species</c> 是配置错误，解析失败。</summary>
        [Test]
        public void TryParse_CreatureWithoutSpecies_Fails()
        {
            const string json =
                "{\"sandboxWidth\":10,\"sandboxHeight\":10," +
                "\"victory\":{\"creatures\":[{\"minCount\":1}]}}";

            GamePhaseData phase;
            string error;
            Assert.IsFalse(GamePhaseParser.TryParse(json, out phase, out error), "缺 species 的动物约束必须解析失败");
            Assert.IsNull(phase);
            StringAssert.Contains("creatures", error);
        }

        /// <summary>可空语义钉死：<c>repeatDialogueId</c> 显式写出 → 解析出该值；省略 → null
        /// （与 <c>dialogueId</c> 同款，都让「重进不播对话」的判定成立）。</summary>
        [Test]
        public void TryParse_RepeatDialogueId_RoundTrips()
        {
            string error;

            GamePhaseData explicitId;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"dialogueId\":\"phase_01_intro\",\"repeatDialogueId\":\"phase_01_retry\"," +
                    "\"sandboxWidth\":10,\"sandboxHeight\":10}", out explicitId, out error),
                error);
            Assert.AreEqual("phase_01_intro", explicitId.dialogueId);
            Assert.AreEqual("phase_01_retry", explicitId.repeatDialogueId);

            GamePhaseData omitted;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out omitted, out error),
                error);
            Assert.IsNull(omitted.repeatDialogueId);
        }

        /// <summary>关卡简要描述是纯 UI 元数据：写出即原样解析出来，供关卡选择界面悬停显示。</summary>
        [Test]
        public void TryParse_Description_RoundTrips()
        {
            const string json = "{\"description\":\"入职日：认识沙盒。\",\"sandboxWidth\":10,\"sandboxHeight\":10}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.AreEqual("入职日：认识沙盒。", phase.description);
        }

        /// <summary>可空语义钉死：<c>description</c> 省略 → null；显式空串 → <c>""</c>。
        /// 两者都让调用方的 <c>IsNullOrEmpty</c> 判定为「没有描述」而隐藏文本。</summary>
        [Test]
        public void TryParse_OmittedOrEmptyDescription_MeansNoDescription()
        {
            string error;

            const string omittedJson = "{\"sandboxWidth\":10,\"sandboxHeight\":10}";
            GamePhaseData omitted;
            Assert.IsTrue(GamePhaseParser.TryParse(omittedJson, out omitted, out error), error);
            Assert.IsNull(omitted.description);
            Assert.IsTrue(string.IsNullOrEmpty(omitted.description));

            const string blankJson = "{\"description\":\"\",\"sandboxWidth\":10,\"sandboxHeight\":10}";
            GamePhaseData blank;
            Assert.IsTrue(GamePhaseParser.TryParse(blankJson, out blank, out error), error);
            Assert.AreEqual(string.Empty, blank.description);
            Assert.IsTrue(string.IsNullOrEmpty(blank.description));
        }

        /// <summary>胜利条件里的缺省约束保持哨兵值：温度 / 湿度为 NaN、计数为 -1（都表示「不约束」）。</summary>
        [Test]
        public void TryParse_OmittedConstraints_KeepSentinels()
        {
            const string json =
                "{\"sandboxWidth\":10,\"sandboxHeight\":10," +
                "\"victory\":{\"particles\":[{\"type\":\"SandParticle\"}]}}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsTrue(float.IsNaN(phase.victory.minSurfaceTemperature));
            Assert.IsTrue(float.IsNaN(phase.victory.maxSurfaceTemperature));
            Assert.IsTrue(float.IsNaN(phase.victory.minAverageHumidity));
            Assert.IsTrue(float.IsNaN(phase.victory.maxAverageHumidity));

            ParticleCountCondition condition = phase.victory.particles[0];
            Assert.AreEqual(-1, condition.minBirths);
            Assert.AreEqual(-1, condition.maxBirths);
            Assert.AreEqual(-1, condition.minDeaths);
            Assert.AreEqual(-1, condition.maxDeaths);
            Assert.AreEqual(-1, condition.minCount);
            Assert.AreEqual(-1, condition.maxCount);
        }

        /// <summary>省略 <c>victory</c> → 归一为 null（本阶段没有胜利条件），而非留一个空实例。</summary>
        [Test]
        public void TryParse_OmittedVictory_NormalizesToNull()
        {
            const string json = "{\"sandboxWidth\":10,\"sandboxHeight\":10}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsNull(phase.victory);
        }

        /// <summary><c>victory</c> 为空对象（四个界限全 NaN 且无 <c>particles</c>）→ 归一为 null。</summary>
        [Test]
        public void TryParse_EmptyVictoryObject_NormalizesToNull()
        {
            const string json = "{\"victory\":{},\"sandboxWidth\":10,\"sandboxHeight\":10}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsNull(phase.victory);
        }

        /// <summary><c>victory.particles</c> 为空数组（界限也全 NaN）→ 归一为 null。</summary>
        [Test]
        public void TryParse_EmptyVictoryParticles_NormalizesToNull()
        {
            const string json = "{\"victory\":{\"particles\":[]},\"sandboxWidth\":10,\"sandboxHeight\":10}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsNull(phase.victory);
        }

        /// <summary>只写了一条界限 → 保留实例（不得被当成全哨兵归一掉），未写的界限仍是 NaN 哨兵。</summary>
        [Test]
        public void TryParse_PartiallySpecifiedVictory_IsKept()
        {
            const string json =
                "{\"victory\":{\"minSurfaceTemperature\":20},\"sandboxWidth\":10,\"sandboxHeight\":10}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsNotNull(phase.victory);
            Assert.AreEqual(20f, phase.victory.minSurfaceTemperature);
            Assert.IsTrue(float.IsNaN(phase.victory.maxSurfaceTemperature));
            Assert.IsTrue(float.IsNaN(phase.victory.minAverageHumidity));
            Assert.IsTrue(float.IsNaN(phase.victory.maxAverageHumidity));
        }

        /// <summary>只有 <c>particles</c>（界限全 NaN）→ 保留实例，不得被误判成全哨兵。</summary>
        [Test]
        public void TryParse_ParticlesWithoutBounds_IsKept()
        {
            const string json =
                "{\"victory\":{\"particles\":[{\"type\":\"SandParticle\"}]}," +
                "\"sandboxWidth\":10,\"sandboxHeight\":10}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.IsNotNull(phase.victory);
            Assert.AreEqual(1, phase.victory.particles.Length);
        }

        /// <summary>沙箱宽或高小于 1 → 解析失败并给出错误。</summary>
        [Test]
        public void TryParse_NonPositiveSandboxSize_Fails()
        {
            GamePhaseData phase;
            string error;
            Assert.IsFalse(
                GamePhaseParser.TryParse("{\"sandboxWidth\":0,\"sandboxHeight\":10}", out phase, out error));
            Assert.IsNull(phase);
            StringAssert.Contains("沙箱尺寸", error);
        }

        /// <summary>JSON 语法坏 → 解析失败（包住 <c>JsonUtility</c> 的异常，不外抛）。</summary>
        [Test]
        public void TryParse_MalformedJson_Fails()
        {
            GamePhaseData phase;
            string error;
            Assert.IsFalse(GamePhaseParser.TryParse("{ not json", out phase, out error));
            Assert.IsNull(phase);
            Assert.IsNotNull(error);
        }

        /// <summary>粒子计数条目缺 type → 解析失败并指出下标。</summary>
        [Test]
        public void TryParse_EmptyParticleType_Fails()
        {
            const string json =
                "{\"sandboxWidth\":10,\"sandboxHeight\":10," +
                "\"victory\":{\"particles\":[{\"minBirths\":1}]}}";

            GamePhaseData phase;
            string error;
            Assert.IsFalse(GamePhaseParser.TryParse(json, out phase, out error));
            Assert.IsNull(phase);
            StringAssert.Contains("type", error);
        }

        // ============================== 开场对话选择 ==============================

        /// <summary>首次进入（isRepeat = false）→ 用 <c>dialogueId</c>。</summary>
        [Test]
        public void ResolveOpeningDialogueId_FirstEntry_ReturnsDialogueId()
        {
            var phase = new GamePhaseData
            {
                dialogueId = "phase_01_intro",
                repeatDialogueId = "phase_01_retry",
            };

            Assert.AreEqual("phase_01_intro", phase.ResolveOpeningDialogueId(false));
        }

        /// <summary>重复进入（isRepeat = true）→ 用 <c>repeatDialogueId</c>。</summary>
        [Test]
        public void ResolveOpeningDialogueId_Repeat_ReturnsRepeatId()
        {
            var phase = new GamePhaseData
            {
                dialogueId = "phase_01_intro",
                repeatDialogueId = "phase_01_retry",
            };

            Assert.AreEqual("phase_01_retry", phase.ResolveOpeningDialogueId(true));
        }

        /// <summary>重复进入但没配 <c>repeatDialogueId</c> → null（= 不播对话），不会回落到 <c>dialogueId</c>。</summary>
        [Test]
        public void ResolveOpeningDialogueId_RepeatWithoutRepeatId_ReturnsNull()
        {
            var phase = new GamePhaseData { dialogueId = "phase_01_intro" };

            Assert.IsNull(phase.ResolveOpeningDialogueId(true));
        }

        // ============================== 结算对话选择 ==============================

        /// <summary>合法 JSON 的两个结算对话 id 都能解析出来（与 <c>dialogueId</c> 同款：不校验、只透传）。</summary>
        [Test]
        public void TryParse_ResultDialogues_RoundTrip()
        {
            const string json =
                "{\"sandboxWidth\":10,\"sandboxHeight\":10," +
                "\"successDialogueId\":\"phase_01_success\"," +
                "\"failureDialogueId\":\"phase_01_failure\"}";

            GamePhaseData phase;
            string error;
            Assert.IsTrue(GamePhaseParser.TryParse(json, out phase, out error), error);

            Assert.AreEqual("phase_01_success", phase.successDialogueId);
            Assert.AreEqual("phase_01_failure", phase.failureDialogueId);
        }

        /// <summary>可空语义钉死：两个结算对话 id 省略 → null；显式空串 → <c>""</c>。
        /// 两者都让 <c>StartResultDialogue</c> 的 <c>IsNullOrEmpty</c> 判定为「没有结算对话」而立刻切阶段。</summary>
        [Test]
        public void TryParse_OmittedOrEmptyResultDialogues_MeanNoResultDialogue()
        {
            string error;

            GamePhaseData omitted;
            Assert.IsTrue(
                GamePhaseParser.TryParse("{\"sandboxWidth\":10,\"sandboxHeight\":10}", out omitted, out error),
                error);
            Assert.IsNull(omitted.successDialogueId);
            Assert.IsNull(omitted.failureDialogueId);
            Assert.IsTrue(string.IsNullOrEmpty(omitted.successDialogueId));
            Assert.IsTrue(string.IsNullOrEmpty(omitted.failureDialogueId));

            GamePhaseData blank;
            Assert.IsTrue(
                GamePhaseParser.TryParse(
                    "{\"successDialogueId\":\"\",\"failureDialogueId\":\"\",\"sandboxWidth\":10,\"sandboxHeight\":10}",
                    out blank,
                    out error),
                error);
            Assert.AreEqual(string.Empty, blank.successDialogueId);
            Assert.AreEqual(string.Empty, blank.failureDialogueId);
            Assert.IsTrue(string.IsNullOrEmpty(blank.successDialogueId));
            Assert.IsTrue(string.IsNullOrEmpty(blank.failureDialogueId));
        }

        /// <summary>过关给 <c>successDialogueId</c>、未过关给 <c>failureDialogueId</c>；
        /// 只配未过关的那种 → 过关时返回 null（= 不播结算对话），不会两边串味。</summary>
        [Test]
        public void ResolveResultDialogueId_ReturnsConfiguredIdPerOutcome()
        {
            var phase = new GamePhaseData
            {
                successDialogueId = "phase_01_success",
                failureDialogueId = "phase_01_failure",
            };

            Assert.AreEqual("phase_01_success", phase.ResolveResultDialogueId(true));
            Assert.AreEqual("phase_01_failure", phase.ResolveResultDialogueId(false));

            var failureOnly = new GamePhaseData { failureDialogueId = "phase_01_failure" };

            Assert.IsNull(failureOnly.ResolveResultDialogueId(true));
            Assert.AreEqual("phase_01_failure", failureOnly.ResolveResultDialogueId(false));
        }

        // ============================== 判定 ==============================

        /// <summary>全部约束都满足 → true，reason 为通过摘要。</summary>
        [Test]
        public void Evaluate_AllConstraintsPass_ReturnsTrue()
        {
            var condition = new VictoryCondition
            {
                minSurfaceTemperature = 20f,
                maxSurfaceTemperature = 30f,
                minAverageHumidity = 0.1f,
                maxAverageHumidity = 0.5f,
                particles = new[]
                {
                    new ParticleCountCondition
                    {
                        type = "GrassParticle",
                        minBirths = 5,
                        maxBirths = 50,
                        minDeaths = 0,
                        maxDeaths = 10,
                    },
                },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 12L, 3L));

            string reason;
            Assert.IsTrue(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            StringAssert.Contains("全部条件满足", reason);
        }

        /// <summary>地表温度低于下限 → false，reason 含实测值与要求值。</summary>
        [Test]
        public void Evaluate_MinSurfaceTemperatureBelow_Fails()
        {
            var condition = new VictoryCondition { minSurfaceTemperature = 20f };

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, Snapshot(12.3f, float.NaN), out reason));
            StringAssert.Contains("地表温度", reason);
            StringAssert.Contains("12.3", reason);
            StringAssert.Contains("低于要求", reason);
            StringAssert.Contains("20.0", reason);
        }

        /// <summary>地表温度高于上限 → false，reason 含实测值与要求值。</summary>
        [Test]
        public void Evaluate_MaxSurfaceTemperatureAbove_Fails()
        {
            var condition = new VictoryCondition { maxSurfaceTemperature = 30f };

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, Snapshot(35f, float.NaN), out reason));
            StringAssert.Contains("地表温度", reason);
            StringAssert.Contains("35.0", reason);
            StringAssert.Contains("高于要求", reason);
        }

        /// <summary>平均湿度低于下限 → false，reason 含实测值与要求值。</summary>
        [Test]
        public void Evaluate_MinAverageHumidityBelow_Fails()
        {
            var condition = new VictoryCondition { minAverageHumidity = 0.2f };

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, Snapshot(float.NaN, 0.1f), out reason));
            StringAssert.Contains("平均湿度", reason);
            StringAssert.Contains("0.100", reason);
            StringAssert.Contains("低于要求", reason);
            StringAssert.Contains("0.200", reason);
        }

        /// <summary>平均湿度高于上限 → false，reason 含实测值与要求值。</summary>
        [Test]
        public void Evaluate_MaxAverageHumidityAbove_Fails()
        {
            var condition = new VictoryCondition { maxAverageHumidity = 0.2f };

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, Snapshot(float.NaN, 0.35f), out reason));
            StringAssert.Contains("平均湿度", reason);
            StringAssert.Contains("0.350", reason);
            StringAssert.Contains("高于要求", reason);
        }

        /// <summary>边界含等号：实测恰等于上下限 / 计数恰等于要求 → 通过。</summary>
        [Test]
        public void Evaluate_BoundaryValuesInclusive_Passes()
        {
            var condition = new VictoryCondition
            {
                minSurfaceTemperature = 20f,
                maxSurfaceTemperature = 20f,
                minAverageHumidity = 0.2f,
                maxAverageHumidity = 0.2f,
                particles = new[]
                {
                    new ParticleCountCondition { type = "GrassParticle", minBirths = 5, maxBirths = 5 },
                },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                20f,
                0.2f,
                Count(ParticleIds.Grass, 5L, 0L));

            string reason;
            Assert.IsTrue(VictoryEvaluator.Evaluate(condition, snapshot, out reason), reason);
        }

        /// <summary>有温度约束但快照未采样（NaN）→ false，reason 指出未采样。</summary>
        [Test]
        public void Evaluate_NanTemperatureWithConstraint_Fails()
        {
            var condition = new VictoryCondition { minSurfaceTemperature = 20f };

            string reason;
            Assert.IsFalse(
                VictoryEvaluator.Evaluate(condition, Snapshot(float.NaN, float.NaN), out reason));
            StringAssert.Contains("未采样", reason);
        }

        /// <summary>没有胜利条件（condition 为 null）→ false，reason 说明本阶段没有胜利条件。</summary>
        [Test]
        public void Evaluate_NullCondition_Fails()
        {
            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(null, Snapshot(25f, 0.3f), out reason));
            StringAssert.Contains("没有胜利条件", reason);
        }

        /// <summary>计数条目里的粒子名在注册表里查不到 → false，reason 指出配置错误。</summary>
        [Test]
        public void Evaluate_UnknownParticleName_Fails()
        {
            var condition = new VictoryCondition
            {
                particles = new[] { new ParticleCountCondition { type = "NoSuchParticle", minBirths = 1 } },
            };

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, Snapshot(25f, 0.3f), out reason));
            StringAssert.Contains("配置错误", reason);
            StringAssert.Contains("NoSuchParticle", reason);
        }

        /// <summary>多条约束同时不满足 → reason 只报**第一条**（按温度 → 湿度 → 计数的顺序）。</summary>
        [Test]
        public void Evaluate_FirstUnmetConditionReported()
        {
            var condition = new VictoryCondition
            {
                minSurfaceTemperature = 20f,   // 通过（实测 25）
                minAverageHumidity = 0.5f,     // 第一条未满足
                particles = new[]
                {
                    new ParticleCountCondition { type = "GrassParticle", minBirths = 100 }, // 也会失败
                },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 3L, 0L));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            StringAssert.Contains("平均湿度", reason);
            StringAssert.DoesNotContain("GrassParticle", reason);
        }

        /// <summary>某类型产生数低于下限 → false，reason 含类型名与实测 / 要求值。</summary>
        [Test]
        public void Evaluate_ParticleBirthsBelowMin_Fails()
        {
            var condition = new VictoryCondition
            {
                particles = new[] { new ParticleCountCondition { type = "GrassParticle", minBirths = 10 } },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 3L, 0L));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            StringAssert.Contains("GrassParticle", reason);
            StringAssert.Contains("产生", reason);
            StringAssert.Contains("低于要求", reason);
        }

        /// <summary>某类型死亡数高于上限 → false，reason 含类型名与实测 / 要求值。</summary>
        [Test]
        public void Evaluate_ParticleDeathsAboveMax_Fails()
        {
            var condition = new VictoryCondition
            {
                particles = new[] { new ParticleCountCondition { type = "WaterParticle", maxDeaths = 2 } },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Water, 0L, 5L));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            StringAssert.Contains("WaterParticle", reason);
            StringAssert.Contains("死亡", reason);
            StringAssert.Contains("高于要求", reason);
        }

        /// <summary>条目配了 <c>failureText</c> → 任一界限不满足都只返回该文案（面向玩家的失败原因），不附实测值。</summary>
        [Test]
        public void Evaluate_ParticleConditionFailure_UsesConfiguredFailureText()
        {
            var condition = new VictoryCondition
            {
                particles = new[]
                {
                    new ParticleCountCondition
                    {
                        type = "GrassParticle",
                        minCount = 10,
                        failureText = "草没能存活下来",
                    },
                },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 3L, 0L, 3L));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            Assert.AreEqual("草没能存活下来", reason, "配了 failureText 就只给该文案");
        }

        /// <summary>存量约束（minCount / maxCount）都满足 → true。</summary>
        [Test]
        public void Evaluate_FinalCountWithinRange_Passes()
        {
            var condition = new VictoryCondition
            {
                particles = new[]
                {
                    new ParticleCountCondition { type = "GrassParticle", minCount = 5, maxCount = 20 },
                },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 0L, 0L, 12L));

            string reason;
            Assert.IsTrue(VictoryEvaluator.Evaluate(condition, snapshot, out reason), reason);
        }

        /// <summary>结束时存量低于 minCount → false，reason 形如「GrassParticle 存量 3 低于要求 10。」。</summary>
        [Test]
        public void Evaluate_FinalCountBelowMin_Fails()
        {
            var condition = new VictoryCondition
            {
                particles = new[] { new ParticleCountCondition { type = "GrassParticle", minCount = 10 } },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 99L, 99L, 3L));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            StringAssert.Contains("GrassParticle", reason);
            StringAssert.Contains("存量", reason);
            StringAssert.Contains("3", reason);
            StringAssert.Contains("低于要求", reason);
            StringAssert.Contains("10", reason);
        }

        /// <summary>结束时存量高于 maxCount → false，reason 用「高于要求」。</summary>
        [Test]
        public void Evaluate_FinalCountAboveMax_Fails()
        {
            var condition = new VictoryCondition
            {
                particles = new[] { new ParticleCountCondition { type = "GrassParticle", maxCount = 10 } },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 0L, 0L, 25L));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason));
            StringAssert.Contains("GrassParticle", reason);
            StringAssert.Contains("存量", reason);
            StringAssert.Contains("25", reason);
            StringAssert.Contains("高于要求", reason);
        }

        /// <summary>边界含等号：存量恰等于 minCount（也是 maxCount）→ 通过。</summary>
        [Test]
        public void Evaluate_FinalCountAtBoundary_Passes()
        {
            var condition = new VictoryCondition
            {
                particles = new[]
                {
                    new ParticleCountCondition { type = "GrassParticle", minCount = 10, maxCount = 10 },
                },
            };

            HighSpeedSnapshot snapshot = Snapshot(
                25f,
                0.3f,
                Count(ParticleIds.Grass, 0L, 0L, 10L));

            string reason;
            Assert.IsTrue(VictoryEvaluator.Evaluate(condition, snapshot, out reason), reason);
        }

        /// <summary>动物条目配了 <c>failureText</c> → 失败理由就是该文案。</summary>
        [Test]
        public void Evaluate_CreatureConditionFailure_UsesConfiguredFailureText()
        {
            var condition = new VictoryCondition
            {
                creatures = new[]
                {
                    new CreatureCountCondition
                    {
                        species = "RabbitCreature",
                        minCount = 1,
                        failureText = "兔子没能存活下来",
                    },
                },
            };

            string reason;
            Assert.IsFalse(
                VictoryEvaluator.Evaluate(condition, SnapshotWithCreatures(Alive(CreatureSpecies.Rabbit, 0)), out reason));
            Assert.AreEqual("兔子没能存活下来", reason);
        }

        /// <summary>
        /// 动物存活数约束：每个物种都要达到 minCount 才过关；不足即失败且理由点名该物种与实测存活数。
        /// 存活数由调用方（结算帧的实体表）统计——判定本身只读快照。
        /// </summary>
        [Test]
        public void Evaluate_CreatureAliveBelowMin_Fails()
        {
            var condition = new VictoryCondition
            {
                creatures = new[]
                {
                    new CreatureCountCondition { species = "RabbitCreature", minCount = 1 },
                    new CreatureCountCondition { species = "FishCreature", minCount = 1 },
                    new CreatureCountCondition { species = "BirdCreature", minCount = 1 },
                },
            };

            HighSpeedSnapshot snapshot = SnapshotWithCreatures(
                Alive(CreatureSpecies.Rabbit, 2),
                Alive(CreatureSpecies.Fish, 1),
                Alive(CreatureSpecies.Bird, 0));

            string reason;
            Assert.IsFalse(VictoryEvaluator.Evaluate(condition, snapshot, out reason), "鸟的存活数为 0，必须判负");
            StringAssert.Contains("BirdCreature", reason);
            StringAssert.Contains("存活", reason);
            StringAssert.Contains("低于要求", reason);
        }

        /// <summary>每个物种都达到下限即过关；快照里没有的物种按 0 存活算（不是「跳过」）。</summary>
        [Test]
        public void Evaluate_CreatureAliveMeetsMin_Passes()
        {
            var condition = new VictoryCondition
            {
                creatures = new[]
                {
                    new CreatureCountCondition { species = "RabbitCreature", minCount = 1 },
                    new CreatureCountCondition { species = "FishCreature", minCount = 1 },
                },
            };

            HighSpeedSnapshot snapshot = SnapshotWithCreatures(
                Alive(CreatureSpecies.Rabbit, 1),
                Alive(CreatureSpecies.Fish, 1));

            string reason;
            Assert.IsTrue(VictoryEvaluator.Evaluate(condition, snapshot, out reason), reason);

            HighSpeedSnapshot missingFish = SnapshotWithCreatures(Alive(CreatureSpecies.Rabbit, 1));
            Assert.IsFalse(
                VictoryEvaluator.Evaluate(condition, missingFish, out reason),
                "快照缺该物种 = 0 存活，必须判负");
        }

        /// <summary>物种名也接受 DebugName（中文名），与粒子白名单的「类名或显示名」规则一致。</summary>
        [Test]
        public void Evaluate_CreatureSpeciesMatchesDebugName()
        {
            var condition = new VictoryCondition
            {
                creatures = new[] { new CreatureCountCondition { species = "兔子", minCount = 1 } },
            };

            string reason;
            Assert.IsTrue(
                VictoryEvaluator.Evaluate(condition, SnapshotWithCreatures(Alive(CreatureSpecies.Rabbit, 1)), out reason),
                reason);
        }

        /// <summary>物种名查不到 = 配置错误（不是玩家没达成），理由明确指出。</summary>
        [Test]
        public void Evaluate_UnknownCreatureSpecies_FailsAsConfigError()
        {
            var condition = new VictoryCondition
            {
                creatures = new[] { new CreatureCountCondition { species = "DragonCreature", minCount = 1 } },
            };

            string reason;
            Assert.IsFalse(
                VictoryEvaluator.Evaluate(condition, SnapshotWithCreatures(Alive(CreatureSpecies.Rabbit, 5)), out reason));
            StringAssert.Contains("配置错误", reason);
            StringAssert.Contains("DragonCreature", reason);
        }

        // ============================== 阶段配置（真资产） ==============================

        /// <summary>
        /// 五份实际发货的阶段 JSON 必须能解析、每个投放 / 胜利约束里的名字都要在注册表里查得到，
        /// 且解锁是**递进的**（后一关的投放名单包含前一关的全部）——名字拼错会让某类粒子永远无法投放 / 判负，
        /// 递进被打断则是关卡设计的静默退化。
        /// </summary>
        [Test]
        public void ShippedPhases_ParseAndResolveEveryName()
        {
            string[] ids = { "phase_01", "phase_02", "phase_03", "phase_04", "phase_05" };
            var parsed = new GamePhaseData[ids.Length];

            for (int i = 0; i < ids.Length; i++)
            {
                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/Phases/" + ids[i] + ".json");
                Assert.IsNotNull(asset, "阶段资产缺失：" + ids[i]);

                Assert.IsTrue(GamePhaseParser.TryParse(asset.text, out parsed[i], out string error),
                    "阶段 " + ids[i] + " 解析失败：" + error);
                Assert.Greater(parsed[i].simulationTicks, 0, "阶段 " + ids[i] + " 必须明确写出模拟时长");

                string[] particles = parsed[i].availableParticles;
                if (particles != null)
                {
                    for (int p = 0; p < particles.Length; p++)
                    {
                        Assert.IsTrue(ResolvesParticle(particles[p]),
                            "阶段 " + ids[i] + " 的 availableParticles 里有未知类型：" + particles[p]);
                    }
                }

                string[] creatures = parsed[i].availableCreatures;
                if (creatures != null)
                {
                    for (int c = 0; c < creatures.Length; c++)
                    {
                        Assert.IsTrue(ResolvesCreature(creatures[c]),
                            "阶段 " + ids[i] + " 的 availableCreatures 里有未知物种：" + creatures[c]);
                    }
                }

                VictoryCondition victory = parsed[i].victory;
                if (victory == null)
                {
                    continue;
                }

                if (victory.particles != null)
                {
                    for (int p = 0; p < victory.particles.Length; p++)
                    {
                        Assert.IsTrue(ResolvesParticle(victory.particles[p].type),
                            "阶段 " + ids[i] + " 的 victory.particles 里有未知类型：" + victory.particles[p].type);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(victory.particles[p].failureText),
                            "阶段 " + ids[i] + " 的 victory.particles[" + p + "] 缺 failureText（高速结束后唯一显示的失败文案）");
                    }
                }

                if (victory.creatures != null)
                {
                    for (int c = 0; c < victory.creatures.Length; c++)
                    {
                        Assert.IsTrue(ResolvesCreature(victory.creatures[c].species),
                            "阶段 " + ids[i] + " 的 victory.creatures 里有未知物种：" + victory.creatures[c].species);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(victory.creatures[c].failureText),
                            "阶段 " + ids[i] + " 的 victory.creatures[" + c + "] 缺 failureText（高速结束后唯一显示的失败文案）");
                    }
                }
            }

            // 递进解锁：后一关的粒子名单包含前一关的全部（前三关按设计逐关加料）。
            for (int i = 1; i <= 3; i++)
            {
                Assert.IsNotNull(parsed[i - 1].availableParticles, "阶段 " + ids[i - 1] + " 应当明确写出粒子名单");
                Assert.IsNotNull(parsed[i].availableParticles, "阶段 " + ids[i] + " 应当明确写出粒子名单");

                for (int p = 0; p < parsed[i - 1].availableParticles.Length; p++)
                {
                    CollectionAssert.Contains(parsed[i].availableParticles, parsed[i - 1].availableParticles[p],
                        "阶段 " + ids[i] + " 必须保留上一关已解锁的类型 " + parsed[i - 1].availableParticles[p]);
                }
            }

            // 阶段链线性连通：每个 nextPhaseId 都指向下一份已登记的阶段。
            for (int i = 0; i < ids.Length - 1; i++)
            {
                Assert.AreEqual(ids[i + 1], parsed[i].nextPhaseId, "阶段 " + ids[i] + " 的 nextPhaseId 应当指向下一关");
            }

            Assert.IsNull(parsed[ids.Length - 1].victory, "最后一关（沙盒）没有胜利条件");
            Assert.IsNull(parsed[ids.Length - 1].nextPhaseId, "最后一关是终点");
            Assert.IsNull(parsed[ids.Length - 1].availableParticles, "沙盒不限制粒子");
            Assert.IsNull(parsed[ids.Length - 1].availableCreatures, "沙盒不限制动物");
        }

        private static bool ResolvesParticle(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            ParticleType[] types = ParticleRegistry.Types;
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType type = types[i];
                if (type != null && (type.Info.DebugName == name || type.ClassName == name))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ResolvesCreature(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            CreatureSpecies[] species = CreatureSpawns.Species;
            for (int i = 0; i < species.Length; i++)
            {
                Creature prototype = CreatureSpawns.Create(species[i]);
                if (prototype != null && (prototype.DebugName == name || prototype.GetType().Name == name))
                {
                    return true;
                }
            }

            return false;
        }

        // ============================== 工具 ==============================
        private static HighSpeedSnapshot Snapshot(float temperature, float humidity, params ParticleTypeCount[] counts)
        {
            return new HighSpeedSnapshot
            {
                surfaceTemperature = temperature,
                averageHumidity = humidity,
                counts = counts,
            };
        }

        private static HighSpeedSnapshot SnapshotWithCreatures(params CreatureAliveCount[] creatures)
        {
            return new HighSpeedSnapshot
            {
                surfaceTemperature = 25f,
                averageHumidity = 0.3f,
                counts = new ParticleTypeCount[0],
                creatures = creatures,
            };
        }

        private static CreatureAliveCount Alive(CreatureSpecies species, int alive)
        {
            return new CreatureAliveCount
            {
                species = species,
                alive = alive,
            };
        }

        private static ParticleTypeCount Count(ushort typeId, long births, long deaths, long finalCount = 0L)
        {
            return new ParticleTypeCount
            {
                typeId = typeId,
                births = births,
                deaths = deaths,
                finalCount = finalCount,
            };
        }
    }
}
