using System;
using System.Globalization;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 一个游戏阶段的配置（JSON 顶层）。阶段定义「演什么对话、给多大的沙箱、允许投什么、怎么算过关」。
    /// 由 <c>GameController</c> 按需读取（每阶段一个 JSON 文件，文件名去扩展名即阶段 id）。
    /// </summary>
    /// <remarks>
    /// 解析走 <c>UnityEngine.JsonUtility</c>，与对话数据同款；不支持字典 / 多态，此处用不到。
    /// 缺省字段保留 C# 字段初始值（因此可空约束用 <c>NaN</c> / <c>-1</c> 这类哨兵值表达「不约束」）。
    /// </remarks>
    [Serializable]
    public sealed class GamePhaseData
    {
        /// <summary>阶段 id（= 文件名去扩展名，由 <c>GameController</c> 加载时回填；JSON 里不写）。</summary>
        public string id;

        /// <summary>对话注册表里的对话 id；null / 空 = 本阶段无对话。</summary>
        public string dialogueId;

        /// <summary>重复进入本阶段时播放的对话 id（注册表 id）；null / 空 = 重复进入时不播对话。</summary>
        public string repeatDialogueId;

        /// <summary>下一阶段 id；null / 空 = 最终阶段（过关后不再启动新阶段）。</summary>
        public string nextPhaseId;

        /// <summary>
        /// 关卡简要描述（纯 UI 元数据，供关卡选择界面悬停时显示）；null / 空 = 本阶段没有可显示的描述。
        /// 流程层（<c>GameController</c> / <c>VictoryEvaluator</c>）不读本字段。
        /// </summary>
        public string description;

        /// <summary>沙箱宽度（格）；必须 ≥ 1。</summary>
        public int sandboxWidth;

        /// <summary>沙箱高度（格）；必须 ≥ 1。</summary>
        public int sandboxHeight;

        /// <summary>
        /// 本阶段可投放的粒子白名单（按 <c>DebugName</c> 或类名匹配）。
        /// null / 空 = 不限制，沿用编辑器默认白名单。
        /// </summary>
        public string[] availableParticles;

        /// <summary>
        /// 胜利条件；null = 本阶段没有胜利条件（沙盒阶段：高速模式跑完后不判定、不推进流程，见 <c>GameController</c>）。
        /// 由 <c>GamePhaseParser</c> 归一：省略的 / 全哨兵的 / <c>particles</c> 为空的 <c>victory</c> 一律为 null。
        /// </summary>
        public VictoryCondition victory;

        /// <summary>
        /// 选本阶段开场要播的对话 id：首次进入（<paramref name="isRepeat"/> = false）用 <see cref="dialogueId"/>；
        /// 重复进入（true）用 <see cref="repeatDialogueId"/>。返回值 null / 空 = **不播对话**。
        /// </summary>
        public string ResolveOpeningDialogueId(bool isRepeat)
        {
            return isRepeat ? repeatDialogueId : dialogueId;
        }
    }

    /// <summary>
    /// 一个阶段的胜利条件。所有非哨兵约束必须同时满足才算过关。
    /// </summary>
    /// <remarks>
    /// 哨兵值：温度 / 湿度用 <see cref="float.NaN"/> 表示「不约束」，计数用 <c>-1</c> 表示「不约束」
    /// （<c>JsonUtility</c> 不支持 <c>long</c>，故计数一律用 <c>int</c>）。
    /// 边界含等号：实测值 ≥ min 且 ≤ max 即通过。
    /// </remarks>
    [Serializable]
    public sealed class VictoryCondition
    {
        /// <summary>地表最低温度要求（对最近一次采样值判）；<see cref="float.NaN"/> = 不约束。</summary>
        public float minSurfaceTemperature = float.NaN;

        /// <summary>地表最高温度要求；<see cref="float.NaN"/> = 不约束。</summary>
        public float maxSurfaceTemperature = float.NaN;

        /// <summary>世界平均湿度下限；<see cref="float.NaN"/> = 不约束。</summary>
        public float minAverageHumidity = float.NaN;

        /// <summary>世界平均湿度上限；<see cref="float.NaN"/> = 不约束。</summary>
        public float maxAverageHumidity = float.NaN;

        /// <summary>逐类型的产生 / 死亡计数约束；null / 空 = 无计数约束。</summary>
        public ParticleCountCondition[] particles;
    }

    /// <summary>
    /// 单个粒子类型的产生 / 死亡 / 存量约束。每个字段 <c>&lt; 0</c> 表示该项不约束。
    /// 存量对高速结束那一刻的全网格盘点判（见 <c>VictoryEvaluator</c>）。
    /// </summary>
    [Serializable]
    public sealed class ParticleCountCondition
    {
        /// <summary>粒子类型名（<c>DebugName</c> 或类名，匹配规则同 <c>ParticleEditor</c> 的白名单）。</summary>
        public string type;

        /// <summary>产生数下限；<c>&lt; 0</c> = 不约束（<c>JsonUtility</c> 不支持 <c>long</c>，计数用 <c>int</c>）。</summary>
        public int minBirths = -1;

        /// <summary>产生数上限；<c>&lt; 0</c> = 不约束。</summary>
        public int maxBirths = -1;

        /// <summary>死亡数下限；<c>&lt; 0</c> = 不约束。</summary>
        public int minDeaths = -1;

        /// <summary>死亡数上限；<c>&lt; 0</c> = 不约束。</summary>
        public int maxDeaths = -1;

        /// <summary>结束时存量下限（对高速结束那一刻的全网格存量判）；<c>&lt; 0</c> = 不约束。</summary>
        public int minCount = -1;

        /// <summary>结束时存量上限（对高速结束那一刻的全网格存量判）；<c>&lt; 0</c> = 不约束。</summary>
        public int maxCount = -1;
    }

    /// <summary>
    /// 阶段 JSON 的解析入口（<c>UnityEngine.JsonUtility</c>）。
    /// 失败不抛异常、不打日志，由调用方（<c>GameController</c>）决定如何报错——便于 EditMode 测试。
    /// </summary>
    public static class GamePhaseParser
    {
        /// <summary>
        /// 把 JSON 解析成 <see cref="GamePhaseData"/>。失败返回 false 并给 <paramref name="error"/>。
        /// 可空字段（<c>dialogueId</c> / <c>repeatDialogueId</c> / <c>nextPhaseId</c> / <c>description</c>）一律不校验：
        /// 省略 → 保持 C# 字段初始值 null，显式空串 → <c>""</c>；两者由调用方一视同仁地当作「无该内容」
        /// （<c>description</c> 即本阶段没有可显示的描述）。
        /// 缺字段保留 C# 字段初始值；额外校验沙箱宽高 ≥ 1、<c>victory.particles</c> 条目 type 非空。
        /// 省略的 / 全哨兵（界限全 <see cref="float.NaN"/> 且无 particles）的 / <c>particles</c> 为空的 <c>victory</c>
        /// 一律归一为 null，表示本阶段没有胜利条件（沙盒阶段：高速模式跑完后不判定、不推进流程）。
        /// </summary>
        public static bool TryParse(string json, out GamePhaseData phase, out string error)
        {
            phase = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "阶段 JSON 为空。";
                return false;
            }

            GamePhaseData parsed;
            try
            {
                parsed = JsonUtility.FromJson<GamePhaseData>(json);
            }
            catch (Exception exception)
            {
                error = "阶段 JSON 解析失败：" + exception.Message;
                return false;
            }

            if (parsed == null)
            {
                error = "阶段 JSON 解析结果为空。";
                return false;
            }

            if (parsed.sandboxWidth < 1 || parsed.sandboxHeight < 1)
            {
                error = string.Format(
                    CultureInfo.InvariantCulture,
                    "阶段 {0} 的沙箱尺寸非法：{1}×{2}（宽高都必须 ≥ 1）。",
                    parsed.id ?? "<null>",
                    parsed.sandboxWidth,
                    parsed.sandboxHeight);
                return false;
            }

            if (parsed.victory != null && parsed.victory.particles != null)
            {
                for (int i = 0; i < parsed.victory.particles.Length; i++)
                {
                    ParticleCountCondition condition = parsed.victory.particles[i];
                    if (condition == null || string.IsNullOrEmpty(condition.type))
                    {
                        error = string.Format(
                            CultureInfo.InvariantCulture,
                            "阶段 {0} 的 victory.particles[{1}] 缺少 type。",
                            parsed.id ?? "<null>",
                            i);
                        return false;
                    }
                }
            }

            // JsonUtility 对**省略**的嵌套可序列化类会构造一个「全哨兵」实例而不是留 null（本工程实测，2022.3.62f1）。
            // 归一为 null，恢复「省略 victory = 本阶段没有胜利条件」的语义（沙盒阶段靠它区分）。
            if (parsed.victory != null && IsUnconstrainedVictory(parsed.victory))
            {
                parsed.victory = null;
            }

            phase = parsed;
            return true;
        }

        /// <summary>
        /// 判定一个胜利条件是否「什么都没约束」（全哨兵 = 无约束）：四个温度 / 湿度界限
        /// （<see cref="VictoryCondition.minSurfaceTemperature"/> 等）全为 <see cref="float.NaN"/>，
        /// 且 <see cref="VictoryCondition.particles"/> 为 null 或长度为 0。
        /// <c>JsonUtility</c> 对**省略**的嵌套可序列化类会构造出这种全哨兵实例（本工程实测，2022.3.62f1），
        /// <see cref="TryParse"/> 靠本判定把它归一为 null = 本阶段没有胜利条件（沙盒阶段）。
        /// </summary>
        private static bool IsUnconstrainedVictory(VictoryCondition victory)
        {
            return victory != null
                && float.IsNaN(victory.minSurfaceTemperature)
                && float.IsNaN(victory.maxSurfaceTemperature)
                && float.IsNaN(victory.minAverageHumidity)
                && float.IsNaN(victory.maxAverageHumidity)
                && (victory.particles == null || victory.particles.Length == 0);
        }
    }
}
