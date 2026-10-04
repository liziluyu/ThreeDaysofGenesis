using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 全局模拟参数。放在 <c>Assets/Data/</c> 下作为资产配置，不要在 MonoBehaviour 里硬编码。
    /// </summary>
    [CreateAssetMenu(menuName = "TapTap2026/Particle Simulation Settings", fileName = "ParticleSimulationSettings")]
    public sealed class ParticleSimulationSettings : ScriptableObject
    {
        /// <summary><see cref="MaxStepsPerFrame"/> 的硬上限，与 Inspector 的 [Range] 保持一致。</summary>
        public const int MaxStepsPerFrameLimit = 16;

        [SerializeField, Min(1)] private int _width = 320;
        [SerializeField, Min(1)] private int _height = 180;

        [Tooltip("每秒推进多少个**运动帧**（粒子的下落 / 流动 / 浮力位移）。与渲染帧率无关。")]
        [SerializeField, Min(1f)] private float _motionRate = 60f;

        [Tooltip("每秒推进多少个**逻辑帧**（相变 / 反应 / 热传导 / 湿度 / 降水 / 光照 / 随机刻）。" +
                 "按运动帧速率取整折算：每 round(MotionRate / LogicRate) 个运动帧跑一次逻辑帧。")]
        [SerializeField, Min(1f)] private float _logicRate = 20f;

        [Tooltip("单帧内最多追多少个运动帧，防止卡顿后的死亡螺旋。")]
        [SerializeField, Range(1, MaxStepsPerFrameLimit)] private int _maxStepsPerFrame = 4;

        [Tooltip("每多少个运动帧上屏一次。1 = 每个运动帧后渲染一次（默认）。调大可让多个运动帧只渲染一次。")]
        [SerializeField, Min(1)] private int _stepsPerPresent = 1;

        [SerializeField] private int _seed = 123456789;

        [Header("随机刻")]
        [Tooltip("每逻辑帧随机抽取并触发 OnRandomTick 的格数（0 = 关闭）。类似 Minecraft 的 random tick：" +
                 "不是全网格扫描，而是每逻辑帧只随机挑这么多格，命中非空气、非静态的格才触发。适合生长 / 蔓延 / 渗流等慢过程。" +
                 "世界按 CellCount 夹紧。生产值 48 ≈ Minecraft 的密度（3 格 / 4096）：320×180 约等效 42。")]
        [SerializeField, Min(0)] private int _randomTicksPerStep = 48;

        [Header("热力学")]
        [Tooltip("热传导系数：每对相邻格每逻辑帧交换的热量占比。越大越等温（热量摊得越平）；" +
                 "越小热越憋在源头，竖直温差越大。")]
        [SerializeField, Range(0f, HeatMap.MaxConductivity)] private float _conductivity = HeatMap.DefaultConductivity;

        [Tooltip("顶行向虚空散热的系数：温度越高散得越快。0 = 关闭顶行散热。")]
        [SerializeField, Range(0f, HeatMap.MaxConductivity)] private float _voidHeatLoss = HeatMap.DefaultVoidHeatLoss;

        [Tooltip("辐射散热系数（只作用于暴露面：顶行，或正上方是气体/空）：每逻辑帧流失 RadiativeLoss×T 的热量。" +
                 "它同时决定地表的降温速度与热在空中的穿透深度（≈ √(g/λ)）：越大热越憋在低空、地表越热；" +
                 "越小热铺得越高、整根气柱越暖。改它要连光照强度一起改。0 = 关闭（热量只从顶行离开）。")]
        [SerializeField, Range(0f, HeatMap.MaxConductivity)] private float _radiativeLoss = 0.05f;

        [Header("材料导热率（相对值 0..1：1 = 最强，0 = 绝热）")]
        [Tooltip("空气（typeId 0）的导热率。空气没有类型条目，只能在这里给；决定热从地表/水面漏进上方气柱的难易。")]
        [SerializeField, Range(0f, HeatMap.MaxThermalConductivity)] private float _airThermalConductivity = HeatMap.DefaultAirThermalConductivity;

        [Tooltip("空气（typeId 0）的相对辐射率 ε。空气没有类型条目，只能在这里给；" +
                 "它决定「热在竖直方向能铺多远」——气柱每格都是暴露面，ε 越大热越早被抽干、只停在地表附近。" +
                 "0.1（生产值，N₂/O₂ 对红外透明）时整根气柱不再逐格漏热，地表的热能一路铺到网格顶。" +
                 "1 = 与固/液同等的黑体行为（旧行为）。")]
        [SerializeField, Range(0f, HeatMap.MaxThermalEmissivity)] private float _airThermalEmissivity = 0.1f;

        public int Width
        {
            get { return Mathf.Clamp(_width, 1, ParticleWorld.MaxSide); }
        }

        public int Height
        {
            get { return Mathf.Clamp(_height, 1, ParticleWorld.MaxSide); }
        }

        /// <summary>每秒推进多少个运动帧（粒子的下落 / 流动 / 浮力位移）。</summary>
        public float MotionRate
        {
            get { return Mathf.Max(1f, _motionRate); }
        }

        /// <summary>每秒推进多少个逻辑帧（相变 / 反应 / 热传导 / 湿度 / 降水 / 光照 / 随机刻）。</summary>
        public float LogicRate
        {
            get { return Mathf.Max(1f, _logicRate); }
        }

        /// <summary>
        /// 每多少个运动帧跑一次逻辑帧：<c>round(MotionRate / LogicRate)</c>，至少 1。
        /// 默认 60 / 20 = 3（运动 60 Hz、逻辑 20 Hz）。逻辑帧速率高于运动帧时按每帧一次处理（=1）。
        /// </summary>
        public int LogicTicksPerMotionTick
        {
            get
            {
                int every = Mathf.RoundToInt(MotionRate / LogicRate);
                return every < 1 ? 1 : every;
            }
        }

        /// <summary>单帧内最多追几个运动帧。手改 YAML 越界时由 getter 兜底夹紧。</summary>
        public int MaxStepsPerFrame
        {
            get { return Mathf.Clamp(_maxStepsPerFrame, 1, MaxStepsPerFrameLimit); }
        }

        /// <summary>每多少个运动帧上屏一次。1 = 每个运动帧后渲染一次（默认）。</summary>
        public int StepsPerPresent
        {
            get { return Mathf.Max(1, _stepsPerPresent); }
        }

        public uint Seed
        {
            get { return unchecked((uint)_seed); }
        }

        /// <summary>每逻辑帧随机抽取并触发随机刻的格数；负数按 0 处理，上界由世界按 <c>CellCount</c> 夹紧。</summary>
        public int RandomTicksPerStep
        {
            get { return Mathf.Max(0, _randomTicksPerStep); }
        }

        /// <summary>热传导系数，夹在 <c>[0, HeatMap.MaxConductivity]</c>（该上限来自显式扩散的稳定性）。</summary>
        public float Conductivity
        {
            get { return Mathf.Clamp(_conductivity, 0f, HeatMap.MaxConductivity); }
        }

        /// <summary>顶行向虚空散热的系数，夹在 <c>[0, HeatMap.MaxConductivity]</c>。</summary>
        public float VoidHeatLoss
        {
            get { return Mathf.Clamp(_voidHeatLoss, 0f, HeatMap.MaxConductivity); }
        }

        /// <summary>辐射散热系数（只作用于暴露面），夹在 <c>[0, HeatMap.MaxConductivity]</c>。</summary>
        public float RadiativeLoss
        {
            get { return Mathf.Clamp(_radiativeLoss, 0f, HeatMap.MaxConductivity); }
        }

        /// <summary>空气的相对导热率，夹在 <c>[0, HeatMap.MaxThermalConductivity]</c>。</summary>
        public float AirThermalConductivity
        {
            get { return Mathf.Clamp(_airThermalConductivity, 0f, HeatMap.MaxThermalConductivity); }
        }

        /// <summary>空气的相对辐射率 ε，夹在 <c>[0, HeatMap.MaxThermalEmissivity]</c>。</summary>
        public float AirThermalEmissivity
        {
            get { return Mathf.Clamp(_airThermalEmissivity, 0f, HeatMap.MaxThermalEmissivity); }
        }
    }
}
