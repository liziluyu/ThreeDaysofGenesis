using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;

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
                "\"particles\":[{\"type\":\"GrassParticle\",\"minBirths\":5,\"maxBirths\":50,\"minDeaths\":0,\"maxDeaths\":10,\"minCount\":2,\"maxCount\":20}]" +
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
        }

        /// <summary>可空字段（对话 / 下一阶段 / 白名单 / 胜利条件）缺省时保持初始值（null）；id 由加载方回填，解析器不填。</summary>
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
