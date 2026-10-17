using System.Collections.Generic;
using System.Globalization;
using TapTap2026.Simulation;

namespace TapTap2026.Game
{
    /// <summary>
    /// 单个粒子类型的产生 / 死亡计数快照（<see cref="VictoryEvaluator"/> 的输入）。
    /// </summary>
    public struct ParticleTypeCount
    {
        /// <summary>类型 id（<c>ParticleRegistry</c> 的下标，1 起）。</summary>
        public ushort typeId;

        /// <summary>本次高速模式内该类型的产生次数。</summary>
        public long births;

        /// <summary>本次高速模式内该类型的死亡次数。</summary>
        public long deaths;

        /// <summary>高速结束那一刻该类型的存量格数（帧里的 <c>HighSpeedFrame.GetFinalCount</c>）；无会话 / 未结算 = 0。</summary>
        public long finalCount;
    }

    /// <summary>单个动物物种在结算那一刻的存活数（<see cref="VictoryEvaluator"/> 的输入）。</summary>
    public struct CreatureAliveCount
    {
        public CreatureSpecies species;
        public int alive;
    }

    /// <summary>
    /// 一次高速模式结算时的世界快照（<see cref="VictoryEvaluator"/> 的输入）。
    /// 温度 / 湿度尚未采样时为 <see cref="float.NaN"/>。
    /// </summary>
    public struct HighSpeedSnapshot
    {
        /// <summary>最近一次采样的地表平均温度；未采样 = <see cref="float.NaN"/>。</summary>
        public float surfaceTemperature;

        /// <summary>最近一次采样的世界平均湿度；未采样 = <see cref="float.NaN"/>。</summary>
        public float averageHumidity;

        /// <summary>全部已注册类型的产生 / 死亡 / 存量（含全为 0 的条目）；null / 空表示无计数数据。</summary>
        public ParticleTypeCount[] counts;

        /// <summary>结算那一刻各动物物种的存活数；null / 空表示无动物数据（按存活 0 判）。</summary>
        public CreatureAliveCount[] creatures;
    }

    /// <summary>
    /// 胜利条件判定（纯 C#，不碰 Input / UI / 协程，可在 EditMode 测试）。
    /// 按固定顺序检查：地表温度 min → max → 湿度 min → max → 按数组顺序逐条粒子约束
    /// （产生 min → max → 死亡 min → max → 存量 min → max）→ 按数组顺序逐条动物约束
    /// （存活 min → max），
    /// 返回**第一条**未满足项的描述作为 <c>reason</c>。某条目配了 <c>failureText</c> 时，
    /// 它的任一界限不满足都直接返回该文案（面向玩家的「失败原因」），否则回退到带实测值的详细描述。
    /// </summary>
    public static class VictoryEvaluator
    {
        /// <summary>
        /// 判定快照是否满足 <paramref name="condition"/>。
        /// <paramref name="condition"/> 为 null → false（本阶段没有胜利条件）；
        /// 全部约束通过 → true，<paramref name="reason"/> 为通过摘要；
        /// 任一不满足 → false，<paramref name="reason"/> 为第一条未满足项的描述。
        /// 边界含等号（≤ min / ≤ max 均通过）；有温度 / 湿度约束而快照对应值为 <see cref="float.NaN"/> → false。
        /// </summary>
        public static bool Evaluate(VictoryCondition condition, HighSpeedSnapshot snapshot, out string reason)
        {
            if (condition == null)
            {
                reason = "本阶段没有胜利条件。";
                return false;
            }

            var summary = new List<string>();

            float temperature = snapshot.surfaceTemperature;
            if (!float.IsNaN(condition.minSurfaceTemperature))
            {
                if (float.IsNaN(temperature))
                {
                    reason = "地表温度未采样，无法判定温度约束。";
                    return false;
                }

                if (temperature < condition.minSurfaceTemperature)
                {
                    reason = string.Format(
                        CultureInfo.InvariantCulture,
                        "地表温度 {0}° 低于要求 {1}°。",
                        FormatTemperature(temperature),
                        FormatTemperature(condition.minSurfaceTemperature));
                    return false;
                }

                summary.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "地表温度 {0}° ≥ {1}°",
                    FormatTemperature(temperature),
                    FormatTemperature(condition.minSurfaceTemperature)));
            }

            if (!float.IsNaN(condition.maxSurfaceTemperature))
            {
                if (float.IsNaN(temperature))
                {
                    reason = "地表温度未采样，无法判定温度约束。";
                    return false;
                }

                if (temperature > condition.maxSurfaceTemperature)
                {
                    reason = string.Format(
                        CultureInfo.InvariantCulture,
                        "地表温度 {0}° 高于要求 {1}°。",
                        FormatTemperature(temperature),
                        FormatTemperature(condition.maxSurfaceTemperature));
                    return false;
                }

                summary.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "地表温度 {0}° ≤ {1}°",
                    FormatTemperature(temperature),
                    FormatTemperature(condition.maxSurfaceTemperature)));
            }

            float humidity = snapshot.averageHumidity;
            if (!float.IsNaN(condition.minAverageHumidity))
            {
                if (float.IsNaN(humidity))
                {
                    reason = "平均湿度未采样，无法判定湿度约束。";
                    return false;
                }

                if (humidity < condition.minAverageHumidity)
                {
                    reason = string.Format(
                        CultureInfo.InvariantCulture,
                        "平均湿度 {0} 低于要求 {1}。",
                        FormatHumidity(humidity),
                        FormatHumidity(condition.minAverageHumidity));
                    return false;
                }

                summary.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "平均湿度 {0} ≥ {1}",
                    FormatHumidity(humidity),
                    FormatHumidity(condition.minAverageHumidity)));
            }

            if (!float.IsNaN(condition.maxAverageHumidity))
            {
                if (float.IsNaN(humidity))
                {
                    reason = "平均湿度未采样，无法判定湿度约束。";
                    return false;
                }

                if (humidity > condition.maxAverageHumidity)
                {
                    reason = string.Format(
                        CultureInfo.InvariantCulture,
                        "平均湿度 {0} 高于要求 {1}。",
                        FormatHumidity(humidity),
                        FormatHumidity(condition.maxAverageHumidity));
                    return false;
                }

                summary.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "平均湿度 {0} ≤ {1}",
                    FormatHumidity(humidity),
                    FormatHumidity(condition.maxAverageHumidity)));
            }

            if (condition.particles != null)
            {
                for (int i = 0; i < condition.particles.Length; i++)
                {
                    ParticleCountCondition particleCondition = condition.particles[i];
                    if (particleCondition == null)
                    {
                        continue;
                    }

                    ushort typeId;
                    if (!TryResolveTypeId(particleCondition.type, out typeId))
                    {
                        reason = string.Format(
                            CultureInfo.InvariantCulture,
                            "配置错误：粒子类型 \"{0}\" 未在注册表中找到。",
                            particleCondition.type ?? "<null>");
                        return false;
                    }

                    long births;
                    long deaths;
                    long finalCount;
                    GetCounts(snapshot.counts, typeId, out births, out deaths, out finalCount);
                    string displayName = particleCondition.type;

                    if (particleCondition.minBirths >= 0 && births < particleCondition.minBirths)
                    {
                        reason = FailureText(
                            particleCondition.failureText, displayName, "产生", births, particleCondition.minBirths, true);
                        return false;
                    }

                    if (particleCondition.maxBirths >= 0 && births > particleCondition.maxBirths)
                    {
                        reason = FailureText(
                            particleCondition.failureText, displayName, "产生", births, particleCondition.maxBirths, false);
                        return false;
                    }

                    if (particleCondition.minDeaths >= 0 && deaths < particleCondition.minDeaths)
                    {
                        reason = FailureText(
                            particleCondition.failureText, displayName, "死亡", deaths, particleCondition.minDeaths, true);
                        return false;
                    }

                    if (particleCondition.maxDeaths >= 0 && deaths > particleCondition.maxDeaths)
                    {
                        reason = FailureText(
                            particleCondition.failureText, displayName, "死亡", deaths, particleCondition.maxDeaths, false);
                        return false;
                    }

                    if (particleCondition.minCount >= 0 && finalCount < particleCondition.minCount)
                    {
                        reason = FailureText(
                            particleCondition.failureText, displayName, "存量", finalCount, particleCondition.minCount, true);
                        return false;
                    }

                    if (particleCondition.maxCount >= 0 && finalCount > particleCondition.maxCount)
                    {
                        reason = FailureText(
                            particleCondition.failureText, displayName, "存量", finalCount, particleCondition.maxCount, false);
                        return false;
                    }

                    if (particleCondition.minBirths >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 产生 {1} ≥ {2}",
                            displayName,
                            births,
                            particleCondition.minBirths));
                    }

                    if (particleCondition.maxBirths >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 产生 {1} ≤ {2}",
                            displayName,
                            births,
                            particleCondition.maxBirths));
                    }

                    if (particleCondition.minDeaths >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 死亡 {1} ≥ {2}",
                            displayName,
                            deaths,
                            particleCondition.minDeaths));
                    }

                    if (particleCondition.maxDeaths >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 死亡 {1} ≤ {2}",
                            displayName,
                            deaths,
                            particleCondition.maxDeaths));
                    }

                    if (particleCondition.minCount >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 存量 {1} ≥ {2}",
                            displayName,
                            finalCount,
                            particleCondition.minCount));
                    }

                    if (particleCondition.maxCount >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 存量 {1} ≤ {2}",
                            displayName,
                            finalCount,
                            particleCondition.maxCount));
                    }
                }
            }

            if (condition.creatures != null)
            {
                for (int i = 0; i < condition.creatures.Length; i++)
                {
                    CreatureCountCondition creatureCondition = condition.creatures[i];
                    if (creatureCondition == null)
                    {
                        continue;
                    }

                    CreatureSpecies species;
                    if (!TryResolveCreatureSpecies(creatureCondition.species, out species))
                    {
                        reason = string.Format(
                            CultureInfo.InvariantCulture,
                            "配置错误：动物物种 \"{0}\" 未在物种表中找到。",
                            creatureCondition.species ?? "<null>");
                        return false;
                    }

                    int alive = GetAliveCount(snapshot.creatures, species);
                    string displayName = creatureCondition.species;

                    if (creatureCondition.minCount >= 0 && alive < creatureCondition.minCount)
                    {
                        reason = FailureText(
                            creatureCondition.failureText, displayName, "存活", alive, creatureCondition.minCount, true);
                        return false;
                    }

                    if (creatureCondition.maxCount >= 0 && alive > creatureCondition.maxCount)
                    {
                        reason = FailureText(
                            creatureCondition.failureText, displayName, "存活", alive, creatureCondition.maxCount, false);
                        return false;
                    }

                    if (creatureCondition.minCount >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 存活 {1} ≥ {2}",
                            displayName,
                            alive,
                            creatureCondition.minCount));
                    }

                    if (creatureCondition.maxCount >= 0)
                    {
                        summary.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 存活 {1} ≤ {2}",
                            displayName,
                            alive,
                            creatureCondition.maxCount));
                    }
                }
            }

            reason = summary.Count == 0
                ? "全部条件满足（本阶段无约束）。"
                : "全部条件满足：" + string.Join("；", summary);
            return true;
        }

        /// <summary>
        /// 一条未满足约束的失败原因：配置了 <paramref name="failureText"/> 就原样用它（面向玩家的文案），
        /// 否则拼出带实测值与要求值的详细描述（调试 / 日志口径）。
        /// </summary>
        private static string FailureText(
            string failureText,
            string displayName,
            string metric,
            long value,
            int requirement,
            bool below)
        {
            if (!string.IsNullOrEmpty(failureText))
            {
                return failureText;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4}。",
                displayName,
                metric,
                value,
                below ? "低于要求" : "高于要求",
                requirement);
        }

        /// <summary>
        /// 按 <see cref="ParticleTypeInfo.DebugName"/>（中文名）或 <see cref="ParticleType.ClassName"/>（类名）
        /// 在注册表里查类型 id（与 <c>ParticleEditor.ResolveTypeNames</c> 同规则：任一匹配即可）。查不到返回 false。
        /// </summary>
        private static bool TryResolveTypeId(string name, out ushort typeId)
        {
            typeId = 0;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            ParticleType[] types = ParticleRegistry.Types;
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType type = types[i];
                if (type == null)
                {
                    continue;
                }

                if (type.Info.DebugName == name || type.ClassName == name)
                {
                    typeId = (ushort)i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 按 Creature 子类类名（<c>GetType().Name</c>，如 "RabbitCreature"）
        /// 或 <see cref="Creature.DebugName"/>（如 "兔子"）在物种表里查物种。查不到返回 false。
        /// </summary>
        private static bool TryResolveCreatureSpecies(string name, out CreatureSpecies species)
        {
            species = CreatureSpecies.None;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            CreatureSpecies[] all = CreatureSpawns.Species;
            for (int i = 0; i < all.Length; i++)
            {
                Creature prototype = CreatureSpawns.Create(all[i]);
                if (prototype == null)
                {
                    continue;
                }

                if (prototype.GetType().Name == name || prototype.DebugName == name)
                {
                    species = all[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>取快照中某物种的存活数；快照缺该物种时记 0。</summary>
        private static int GetAliveCount(CreatureAliveCount[] creatures, CreatureSpecies species)
        {
            int alive = 0;
            if (creatures == null)
            {
                return alive;
            }

            for (int i = 0; i < creatures.Length; i++)
            {
                if (creatures[i].species == species)
                {
                    alive += creatures[i].alive;
                }
            }

            return alive;
        }

        /// <summary>取快照中某类型的产生 / 死亡 / 存量；快照缺该类型时都记 0。</summary>
        private static void GetCounts(
            ParticleTypeCount[] counts,
            ushort typeId,
            out long births,
            out long deaths,
            out long finalCount)
        {
            births = 0L;
            deaths = 0L;
            finalCount = 0L;

            if (counts == null)
            {
                return;
            }

            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i].typeId == typeId)
                {
                    births = counts[i].births;
                    deaths = counts[i].deaths;
                    finalCount = counts[i].finalCount;
                    return;
                }
            }
        }

        /// <summary>温度显示：一位小数（InvariantCulture，避免逗号小数点）。</summary>
        private static string FormatTemperature(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>湿度显示：三位小数（与 <c>ParticleEditor</c> 的 HUD 一致）。</summary>
        private static string FormatHumidity(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
