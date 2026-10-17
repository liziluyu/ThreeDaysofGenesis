using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 把光照参数从 Inspector 接到模拟：光照总开关、亮相位刻数、暗相位刻数、强度、云层消光系数，
    /// 以及昼夜天空色与夜晚亮度。
    /// 光照逻辑本身住在 <see cref="LightController"/>（纯 C#、每 tick 由 <see cref="ParticleWorld.StepLogic"/> 驱动），
    /// 本组件只做配置与接线，不参与模拟。
    /// </summary>
    /// <remarks>
    /// <para><b>只提交变化</b>：每帧把字段收成一份 <see cref="LightSettings"/> 快照，**与上次提交的不同**时才
    /// <see cref="ParticleSimulationDriver.Submit"/> 一条 <see cref="SimulationCommandKind.SetLightSettings"/> 命令
    /// （否则命令队列会被每帧的无意义写入淹没）。驱动在下一个 tick 边界应用它，世界重建后也会自动重新应用。
    /// 昼夜天空色与夜晚亮度随同一份命令交给驱动——生成像素时按它们取天空色、定整场明暗，
    /// 因此本组件不再推给渲染器，也不再写世界。</para>
    /// <para>三个基本参数（<see cref="Intensity"/> / <see cref="LitTicks"/> / <see cref="DarkTicks"/>）**运行期可写**——
    /// 游戏内由 <see cref="ParticleEditor"/> 右上角的光照面板的三个输入框驱动；面板与 Inspector 写的是同一份字段。</para>
    /// <para>通断是占空比循环：亮 <see cref="LitTicks"/> 刻 → 暗 <see cref="DarkTicks"/> 刻 → 循环。
    /// 亮的每一刻向每一列最上面的非气体粒子（固体或液体——光打到水面就停）注入 <see cref="Intensity"/> 的热量。</para>
    /// <para>过渡长度不需要接线：由 <see cref="LightController.Daylight"/> 自动给出
    /// （= 下一时段长度的 1/<see cref="LightController.DaylightFadeDivisor"/>），生成像素的一侧据此在
    /// <see cref="DayBackground"/> / <see cref="NightBackground"/> 之间取色、并把粒子与云按
    /// <see cref="NightBrightness"/> 变暗。Inspector 上的两个天空色是 Unity <c>Color32</c>（可序列化面），
    /// 提交时逐字节转成纯值 <see cref="RenderColor"/> 快照——命令载荷因此不含 Unity 类型。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleLightDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("是否光照。关闭时不注入热量，相位也不推进。")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("亮相位持续刻数。0 = 从不照亮（只有黑夜）。")]
        [SerializeField, Min(0)] private int _litTicks = 300;

        [Tooltip("暗相位持续刻数。0 = 一直照亮（只有白天）。")]
        [SerializeField, Min(0)] private int _darkTicks = 300;

        [Tooltip("亮度：亮相位每刻向「每一列最上面的非气体粒子（固体或液体）」注入的热量 Q。升温多少取决于该物质的热容。")]
        [SerializeField] private float _intensity = 15f;

        [Tooltip("云层消光系数：每列湿度总量越大，到达地表的光照越弱（透过率 = exp(-系数 × 列湿度总量)）。" +
                 "0 = 云不遮光。列湿度总量量级为个位数，0.05 时厚云柱透过率约 0.7、0.1 时约 0.55。")]
        [SerializeField, Min(0f)] private float _cloudExtinction = LightController.DefaultCloudExtinction;

        [Header("昼夜天空色（渲染表现）")]
        [Tooltip("白天天空色（背景）。")]
        [SerializeField] private Color32 _dayBackground = RenderColorConversion.ToColor32(LightSettings.DefaultDayBackground);

        [Tooltip("夜晚天空色（背景）。")]
        [SerializeField] private Color32 _nightBackground = RenderColorConversion.ToColor32(LightSettings.DefaultNightBackground);

        [Tooltip("夜晚亮度倍率 0..1。白天恒为 1（现在的正常亮度），夜晚取本值；1 = 不随昼夜变暗。")]
        [SerializeField, Range(0f, 1f)] private float _nightBrightness = LightSettings.DefaultNightBrightness;

        /// <summary>当前生效的光照开关（Inspector 值）。</summary>
        public bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>当前生效的亮相位刻数。**可写**——写的是本组件的字段，下一帧 <see cref="Update"/> 把它作为
        /// 命令的一部分交给驱动（游戏内的光照面板走这条路）。
        /// 负数按 0 处理（钳制语义与 <see cref="LightController.LitTicks"/> 一致）。</summary>
        public int LitTicks
        {
            get { return _litTicks; }
            set { _litTicks = value < 0 ? 0 : value; }
        }

        /// <summary>当前生效的暗相位刻数。**可写**（见 <see cref="LitTicks"/>）；负数按 0 处理。</summary>
        public int DarkTicks
        {
            get { return _darkTicks; }
            set { _darkTicks = value < 0 ? 0 : value; }
        }

        /// <summary>当前生效的光照强度。**可写**（见 <see cref="LitTicks"/>）。
        /// NaN / 无穷按 0 处理（钳制语义与 <see cref="LightController.Intensity"/> 一致）。</summary>
        public float Intensity
        {
            get { return _intensity; }
            set { _intensity = (float.IsNaN(value) || float.IsInfinity(value)) ? 0f : value; }
        }

        /// <summary>当前生效的云层消光系数。</summary>
        public float CloudExtinction
        {
            get { return _cloudExtinction; }
        }

        /// <summary>当前生效的白天天空色。</summary>
        public Color32 DayBackground
        {
            get { return _dayBackground; }
        }

        /// <summary>当前生效的夜晚天空色。</summary>
        public Color32 NightBackground
        {
            get { return _nightBackground; }
        }

        /// <summary>当前生效的夜晚亮度倍率。</summary>
        public float NightBrightness
        {
            get { return _nightBrightness; }
        }

        /// <summary>上一次提交给驱动的配置（值类型比较：只有真的变了才入队）。</summary>
        private LightSettings _submitted;

        /// <summary>是否已经提交过一次（首帧无条件提交一次，把 Inspector 的配置钉进驱动）。</summary>
        private bool _submittedOnce;

        private void Awake()
        {
            if (_driver == null)
            {
                _driver = GetComponent<ParticleSimulationDriver>();
            }

            if (_driver == null)
            {
                _driver = FindObjectOfType<ParticleSimulationDriver>();
            }
        }

        private void Update()
        {
            if (_driver == null)
            {
                return;
            }

            var current = new LightSettings(
                _enabled,
                _litTicks,
                _darkTicks,
                _intensity,
                _cloudExtinction,
                RenderColorConversion.FromColor32(_dayBackground),
                RenderColorConversion.FromColor32(_nightBackground),
                _nightBrightness);

            if (_submittedOnce && current.Equals(_submitted))
            {
                return;
            }

            _submitted = current;
            _submittedOnce = true;
            _driver.Submit(SimulationCommand.SetLightSettings(current));
        }
    }
}
