using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 把光照参数从 Inspector 接到世界与渲染器：光照总开关、亮相位刻数、暗相位刻数、强度、云层消光系数，
    /// 以及昼夜天空色与夜晚亮度。
    /// 光照逻辑本身住在 <see cref="ParticleWorld.Light"/>（纯 C#、每 tick 由 <see cref="ParticleWorld.Step"/> 驱动），
    /// 本组件只做配置与接线，不参与模拟。
    /// </summary>
    /// <remarks>
    /// <para>每帧把字段写回 <c>world.Light</c>、并把昼夜参数推给 <see cref="ParticleRenderer"/>：运行时在 Inspector
    /// 改参数即时生效；世界因改尺寸被重建（<see cref="ParticleSimulationDriver.Resize"/>）后也会自动重新应用。</para>
    /// <para>三个基本参数（<see cref="Intensity"/> / <see cref="LitTicks"/> / <see cref="DarkTicks"/>）**运行期可写**——
    /// 游戏内由 <see cref="ParticleEditor"/> 右上角的光照面板的三个输入框驱动；面板与 Inspector 写的是同一份字段。</para>
    /// <para>通断是占空比循环：亮 <see cref="LitTicks"/> 刻 → 暗 <see cref="DarkTicks"/> 刻 → 循环。
    /// 亮的每一刻向每一列最上面的非气体粒子（固体或液体——光打到水面就停）注入 <see cref="Intensity"/> 的热量。</para>
    /// <para>过渡长度不需要接线：由 <see cref="LightController.Daylight"/> 自动给出
    /// （= 下一时段长度的 1/<see cref="LightController.DaylightFadeDivisor"/>），渲染器据此在
    /// <see cref="DayBackground"/> / <see cref="NightBackground"/> 之间取色、并把粒子与云按
    /// <see cref="NightBrightness"/> 变暗。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleLightDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationDriver _driver;

        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("是否光照。关闭时不注入热量，相位也不推进。")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("亮相位持续刻数。0 = 从不照亮（只有黑夜）。")]
        [SerializeField, Min(0)] private int _litTicks = 60;

        [Tooltip("暗相位持续刻数。0 = 一直照亮（只有白天）。")]
        [SerializeField, Min(0)] private int _darkTicks = 60;

        [Tooltip("亮度：亮相位每刻向「每一列最上面的非气体粒子（固体或液体）」注入的热量 Q。升温多少取决于该物质的热容。")]
        [SerializeField] private float _intensity = 5f;

        [Tooltip("云层消光系数：每列湿度总量越大，到达地表的光照越弱（透过率 = exp(-系数 × 列湿度总量)）。" +
                 "0 = 云不遮光。列湿度总量量级为个位数，0.05 时厚云柱透过率约 0.7、0.1 时约 0.55。")]
        [SerializeField, Min(0f)] private float _cloudExtinction = LightController.DefaultCloudExtinction;

        [Header("昼夜天空色（渲染表现）")]
        [Tooltip("白天天空色（背景）。")]
        [SerializeField] private Color32 _dayBackground = ParticleRenderer.DefaultDayBackground;

        [Tooltip("夜晚天空色（背景）。")]
        [SerializeField] private Color32 _nightBackground = ParticleRenderer.DefaultNightBackground;

        [Tooltip("夜晚亮度倍率 0..1。白天恒为 1（现在的正常亮度），夜晚取本值；1 = 不随昼夜变暗。")]
        [SerializeField, Range(0f, 1f)] private float _nightBrightness = ParticleRenderer.DefaultNightBrightness;

        /// <summary>当前生效的光照开关（Inspector 值）。</summary>
        public bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>当前生效的亮相位刻数。**可写**——写的是本组件的字段，下一帧 <see cref="Update"/> 即写回
        /// <c>world.Light</c>（游戏内的光照面板走这条路；直接写世界会被本组件每帧覆盖）。
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

            if (_renderer == null)
            {
                _renderer = GetComponent<ParticleRenderer>();
            }

            if (_renderer == null)
            {
                _renderer = FindObjectOfType<ParticleRenderer>();
            }
        }

        private void Update()
        {
            // 昼夜表现与光照相位无关，先推给渲染器（驱动器 / 世界尚未就绪时也要生效）。
            ApplyRenderer();

            if (_driver == null)
            {
                return;
            }

            ParticleWorld world = _driver.World;
            if (world == null)
            {
                return;
            }

            Apply(world.Light);
        }

        /// <summary>把昼夜天空色与夜晚亮度推给渲染器；未接线（或没有渲染器）时是空操作。</summary>
        private void ApplyRenderer()
        {
            if (_renderer == null)
            {
                return;
            }

            _renderer.DayBackground = _dayBackground;
            _renderer.NightBackground = _nightBackground;
            _renderer.NightBrightness = _nightBrightness;
        }

        private void Apply(LightController light)
        {
            if (light == null)
            {
                return;
            }

            light.Enabled = _enabled;
            light.LitTicks = _litTicks;
            light.DarkTicks = _darkTicks;
            light.Intensity = _intensity;
            light.CloudExtinction = _cloudExtinction;
        }
    }
}
