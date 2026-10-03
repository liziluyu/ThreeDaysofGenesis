using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 把光照参数从 Inspector 接到世界：光照总开关、亮相位刻数、暗相位刻数、强度。
    /// 光照逻辑本身住在 <see cref="ParticleWorld.Light"/>（纯 C#、每 tick 由 <see cref="ParticleWorld.Step"/> 驱动），
    /// 本组件只做配置与接线，不参与模拟。
    /// </summary>
    /// <remarks>
    /// <para>每帧把字段写回 <c>world.Light</c>：运行时在 Inspector 改参数即时生效；
    /// 世界因改尺寸被重建（<see cref="ParticleSimulationDriver.Resize"/>）后也会自动重新应用。</para>
    /// <para>通断是占空比循环：亮 <see cref="LitTicks"/> 刻 → 暗 <see cref="DarkTicks"/> 刻 → 循环。
    /// 亮的每一刻向每一列最上面的固体注入 <see cref="Intensity"/> 的热量。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleLightDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("是否光照。关闭时不注入热量，相位也不推进。")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("亮相位持续刻数。0 = 从不照亮（只有黑夜）。")]
        [SerializeField, Min(0)] private int _litTicks = 60;

        [Tooltip("暗相位持续刻数。0 = 一直照亮（只有白天）。")]
        [SerializeField, Min(0)] private int _darkTicks = 60;

        [Tooltip("亮度：亮相位每刻向「每一列最上面的固体」注入的热量 Q。升温多少取决于该物质的热容。")]
        [SerializeField] private float _intensity = 5f;

        /// <summary>当前生效的光照开关（Inspector 值）。</summary>
        public bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>当前生效的亮相位刻数。</summary>
        public int LitTicks
        {
            get { return _litTicks; }
        }

        /// <summary>当前生效的暗相位刻数。</summary>
        public int DarkTicks
        {
            get { return _darkTicks; }
        }

        /// <summary>当前生效的光照强度。</summary>
        public float Intensity
        {
            get { return _intensity; }
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
        }

        private void Update()
        {
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
        }
    }
}
