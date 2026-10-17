namespace TapTap2026.Simulation
{
    /// <summary>
    /// 每个 tick 生效的模拟配置的**值类型快照**：热力学参数、随机刻密度与热力调试叠加。
    /// 它既是 <see cref="SimulationCommandKind.SetSimulationSettings"/> 的载荷，也是驱动持有的当前配置
    /// （世界重建后要原样重新应用）。
    /// </summary>
    /// <remarks>
    /// <para>字段语义与 <c>ParticleSimulationSettings</c> 资产一一对应（资产在 Unity 侧，只能主线程采样；
    /// 采样成这里的纯值快照之后，模拟侧就不再认识 Unity 对象——这是 worker 化的前置条件之一）。</para>
    /// <para>驱动只在快照真的变化时入队一条命令，因此命令队列不会被每帧的无意义写入淹没。</para>
    /// </remarks>
    public struct SimulationTickSettings
    {
        /// <summary>每逻辑帧随机抽取并触发随机刻的格数（0 = 关闭）。</summary>
        public int RandomTicksPerStep;

        /// <summary>热传导系数。</summary>
        public float Conductivity;

        /// <summary>顶行向虚空散热的系数。</summary>
        public float VoidHeatLoss;

        /// <summary>辐射散热系数（只作用于暴露面）。</summary>
        public float RadiativeLoss;

        /// <summary>绝热直减率（°/格）。</summary>
        public float AdiabaticLapse;

        /// <summary>空气（typeId 0）的导热率。</summary>
        public float AirThermalConductivity;

        /// <summary>空气（typeId 0）的相对辐射率 ε。</summary>
        public float AirThermalEmissivity;

        /// <summary>热力调试叠加：把每格温度按「越热越红」叠在上屏像素上。</summary>
        public bool DebugHeatOverlay;

        /// <summary>热力叠加的满量程温度（&lt;= 0 时不叠色）。</summary>
        public float DebugFullScaleTemperature;

        public SimulationTickSettings(
            int randomTicksPerStep,
            float conductivity,
            float voidHeatLoss,
            float radiativeLoss,
            float adiabaticLapse,
            float airThermalConductivity,
            float airThermalEmissivity,
            bool debugHeatOverlay,
            float debugFullScaleTemperature)
        {
            RandomTicksPerStep = randomTicksPerStep;
            Conductivity = conductivity;
            VoidHeatLoss = voidHeatLoss;
            RadiativeLoss = radiativeLoss;
            AdiabaticLapse = adiabaticLapse;
            AirThermalConductivity = airThermalConductivity;
            AirThermalEmissivity = airThermalEmissivity;
            DebugHeatOverlay = debugHeatOverlay;
            DebugFullScaleTemperature = debugFullScaleTemperature;
        }

        /// <summary>逐字段相等（**精确**比较：这是「配置变了没有」的判据，不做容差）。</summary>
        public bool Equals(SimulationTickSettings other)
        {
            return RandomTicksPerStep == other.RandomTicksPerStep
                && Conductivity == other.Conductivity
                && VoidHeatLoss == other.VoidHeatLoss
                && RadiativeLoss == other.RadiativeLoss
                && AdiabaticLapse == other.AdiabaticLapse
                && AirThermalConductivity == other.AirThermalConductivity
                && AirThermalEmissivity == other.AirThermalEmissivity
                && DebugHeatOverlay == other.DebugHeatOverlay
                && DebugFullScaleTemperature == other.DebugFullScaleTemperature;
        }
    }
}
