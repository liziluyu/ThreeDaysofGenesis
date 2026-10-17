namespace TapTap2026.Simulation
{
    /// <summary>
    /// 光照配置的**值类型快照**：既作 <see cref="SimulationCommandKind.SetLightSettings"/> 的载荷，
    /// 也是驱动持有的当前配置（世界重建后要原样重新应用）。字段语义与 <see cref="LightController"/> 一一对应，
    /// 后三项是昼夜表现（背景色与亮度倍率），由生成像素的一侧消费。
    /// </summary>
    /// <remarks>
    /// <para><b>纯值</b>：本结构不含任何 Unity 类型（天空色用项目自有的 <see cref="RenderColor"/>），
    /// 因此它能作为队列载荷传给未来的模拟 worker，并在 worker 上用纯数学生成天空色 / 明暗。</para>
    /// <para>Inspector 上的配置面（<c>ParticleLightDriver</c>）仍是 Unity <c>Color32</c> 序列化字段——
    /// 转换发生在提交命令那一刻（见 <see cref="RenderColorConversion.FromColor32"/>）。</para>
    /// </remarks>
    public struct LightSettings
    {
        /// <summary>白天天空色的代码默认值（唯一的配置面是 <c>ParticleLightDriver</c>，这里只是「没有配置面时」的兜底）。</summary>
        public static readonly RenderColor DefaultDayBackground = new RenderColor(0xCC, 0xE5, 0xFF, 255);

        /// <summary>夜晚天空色的代码默认值。</summary>
        public static readonly RenderColor DefaultNightBackground = new RenderColor(0x2A, 0x22, 0x3A, 255);

        /// <summary>夜晚亮度倍率的代码默认值（1 = 不随昼夜变暗）。</summary>
        public const float DefaultNightBrightness = 0.5f;

        /// <summary>亮相位刻数的代码默认值。</summary>
        public const int DefaultLitTicks = 60;

        /// <summary>暗相位刻数的代码默认值。</summary>
        public const int DefaultDarkTicks = 60;

        /// <summary>光照强度的代码默认值（亮相位每刻向地表注入的热量）。</summary>
        public const float DefaultIntensity = 5f;

        /// <summary>总开关：关闭时不注入热量、相位也不推进。</summary>
        public bool Enabled;

        /// <summary>亮相位持续刻数。</summary>
        public int LitTicks;

        /// <summary>暗相位持续刻数。</summary>
        public int DarkTicks;

        /// <summary>亮度：亮相位每刻向「每一列最上面的非气体粒子」注入的热量 Q。</summary>
        public float Intensity;

        /// <summary>云层消光系数：透过率 = exp(-系数 × 列湿度总量)。</summary>
        public float CloudExtinction;

        /// <summary>白天天空色（背景，纯值）。</summary>
        public RenderColor DayBackground;

        /// <summary>夜晚天空色（背景，纯值）。</summary>
        public RenderColor NightBackground;

        /// <summary>夜晚亮度倍率，夹在 [0, 1]；1 = 不随昼夜变暗。</summary>
        public float NightBrightness;

        /// <summary>代码内置默认配置（与 <c>ParticleLightDriver</c> 的 Inspector 默认值一致）。</summary>
        public static readonly LightSettings Default = new LightSettings
        {
            Enabled = true,
            LitTicks = DefaultLitTicks,
            DarkTicks = DefaultDarkTicks,
            Intensity = DefaultIntensity,
            CloudExtinction = LightController.DefaultCloudExtinction,
            DayBackground = DefaultDayBackground,
            NightBackground = DefaultNightBackground,
            NightBrightness = DefaultNightBrightness,
        };

        public LightSettings(
            bool enabled,
            int litTicks,
            int darkTicks,
            float intensity,
            float cloudExtinction,
            RenderColor dayBackground,
            RenderColor nightBackground,
            float nightBrightness)
        {
            Enabled = enabled;
            LitTicks = litTicks;
            DarkTicks = darkTicks;
            Intensity = intensity;
            CloudExtinction = cloudExtinction;
            DayBackground = dayBackground;
            NightBackground = nightBackground;
            NightBrightness = nightBrightness;
        }

        /// <summary>逐字段相等（**精确**比较：这是「配置变了没有」的判据，不做容差）。</summary>
        public bool Equals(LightSettings other)
        {
            return Enabled == other.Enabled
                && LitTicks == other.LitTicks
                && DarkTicks == other.DarkTicks
                && Intensity == other.Intensity
                && CloudExtinction == other.CloudExtinction
                && DayBackground.r == other.DayBackground.r
                && DayBackground.g == other.DayBackground.g
                && DayBackground.b == other.DayBackground.b
                && DayBackground.a == other.DayBackground.a
                && NightBackground.r == other.NightBackground.r
                && NightBackground.g == other.NightBackground.g
                && NightBackground.b == other.NightBackground.b
                && NightBackground.a == other.NightBackground.a
                && NightBrightness == other.NightBrightness;
        }
    }
}
