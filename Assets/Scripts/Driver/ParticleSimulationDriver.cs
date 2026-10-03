using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 固定时间步长驱动 + 渲染调度。是**唯一**同时认识模拟与渲染的地方。
    /// </summary>
    /// <remarks>
    /// 解耦点：<see cref="ParticleWorld.Step"/> 只管推进模拟，从不渲染；
    /// 渲染由本类在 <see cref="Update"/> 里按 <see cref="ParticleSimulationSettings.StepsPerPresent"/> 决定何时上屏。
    /// 默认值 1 = 每个逻辑帧后渲染一次；调大即可变成「多个逻辑帧渲染一次」，无需改动模拟或渲染代码。
    /// <para>沙箱尺寸：默认取 <see cref="ParticleSimulationSettings"/> 的宽高，本组件的「沙箱尺寸」字段非 0 时覆盖之。
    /// 运行中改尺寸（Inspector 或 <see cref="Resize"/>）会重建一个空世界，已有内容不保留。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSimulationDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationSettings _settings;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("关掉只停逻辑推进，仍可手动上屏。")]
        [SerializeField] private bool _simulate = true;

        [Header("沙箱尺寸（格，0 = 用 Settings 资产的值）")]
        [Tooltip("网格宽度。运行时在 Inspector 改这里（或改 Settings 资产）会重建一个空世界。")]
        [SerializeField, Min(0)] private int _widthOverride;

        [Tooltip("网格高度。运行时在 Inspector 改这里（或改 Settings 资产）会重建一个空世界。")]
        [SerializeField, Min(0)] private int _heightOverride;

        [Header("调试")]
        [Tooltip("热力叠加：把每格温度按「越热越红」叠在上屏像素上（空气格也显示）。只影响显示，不影响模拟。")]
        [SerializeField] private bool _heatDebugOverlay;

        [Tooltip("热力叠加的满量程温度：温度达到它即为纯红。<= 0 时不叠色。")]
        [SerializeField] private float _heatDebugFullScale = HeatMap.DefaultDebugFullScaleTemperature;

        private ParticleSimulationSettings _fallbackSettings;
        private ParticleWorld _world;
        private float _accumulator;
        private int _stepsSincePresent;

        /// <summary>当前世界。在 <c>Awake</c> 之后可用。</summary>
        public ParticleWorld World
        {
            get { return _world; }
        }

        /// <summary>是否推进逻辑。运行时可切换（暂停/继续）。</summary>
        public bool Simulate
        {
            get { return _simulate; }
            set { _simulate = value; }
        }

        /// <summary>把网格**当前**状态立即上屏一次（暂停中改了网格后可用）。本方法不推进逻辑帧。</summary>
        public void PresentNow()
        {
            Present();
        }

        /// <summary>
        /// 按新尺寸重建一个**空**世界并立即上屏一次（相机由渲染器重新框住整张网格）。**已有内容不保留。**
        /// </summary>
        /// <remarks>
        /// 等价于在 Inspector 里填「沙箱尺寸」字段：两者都只是把尺寸覆盖写回本组件，
        /// 下一帧 <see cref="Update"/> 检测到世界尺寸不符时同样重建。范围夹在 <c>1..ParticleWorld.MaxSide</c>。
        /// </remarks>
        public void Resize(int width, int height)
        {
            _widthOverride = Mathf.Clamp(width, 1, ParticleWorld.MaxSide);
            _heightOverride = Mathf.Clamp(height, 1, ParticleWorld.MaxSide);

            if (_world == null)
            {
                return;
            }

            CreateWorld();
            Present();
        }

        /// <summary>本帧生效的网格宽度：覆盖值 &gt; 0 时用它，否则用 <see cref="ParticleSimulationSettings.Width"/>。</summary>
        private int RequestedWidth
        {
            get
            {
                return _widthOverride > 0
                    ? Mathf.Clamp(_widthOverride, 1, ParticleWorld.MaxSide)
                    : Settings.Width;
            }
        }

        /// <summary>本帧生效的网格高度：覆盖值 &gt; 0 时用它，否则用 <see cref="ParticleSimulationSettings.Height"/>。</summary>
        private int RequestedHeight
        {
            get
            {
                return _heightOverride > 0
                    ? Mathf.Clamp(_heightOverride, 1, ParticleWorld.MaxSide)
                    : Settings.Height;
            }
        }

        private ParticleSimulationSettings Settings
        {
            get
            {
                if (_settings != null)
                {
                    return _settings;
                }

                if (_fallbackSettings == null)
                {
                    _fallbackSettings = ScriptableObject.CreateInstance<ParticleSimulationSettings>();
                    _fallbackSettings.name = "ParticleSimulationSettings (code defaults)";
                    Debug.LogWarning(
                        "[ParticleSimulationDriver] 未指定 ParticleSimulationSettings，使用代码内置默认值。");
                }

                return _fallbackSettings;
            }
        }

        private void Awake()
        {
            ParticleRegistration.RegisterAll();
            CreateWorld();
        }

        /// <summary>按 <see cref="RequestedWidth"/>/<see cref="RequestedHeight"/> 建一个空世界，并清空累加器。</summary>
        private void CreateWorld()
        {
            ParticleSimulationSettings settings = Settings;
            _world = new ParticleWorld(RequestedWidth, RequestedHeight, settings.Seed);
            _accumulator = 0f;
            _stepsSincePresent = 0;
        }

        private void Start()
        {
            Present();
        }

        private void OnDestroy()
        {
            // 代码兜底创建的 ScriptableObject 不受场景管理，必须自己销毁，否则每次进播放都泄漏一个实例。
            if (_fallbackSettings != null)
            {
                Destroy(_fallbackSettings);
                _fallbackSettings = null;
            }
        }

        private void Update()
        {
            if (_world == null)
            {
                return;
            }

            // Inspector 里改了「沙箱尺寸」（或 Settings 资产的宽高）后在此生效；尺寸一致时零成本跳过。
            if (_world.Width != RequestedWidth || _world.Height != RequestedHeight)
            {
                CreateWorld();
                Present();
            }

            // 热力学参数与调试叠加：每帧写回，运行时改 Inspector / 资产即时生效，重建世界后也自动重新应用。
            HeatMap heat = _world.Heat;
            ParticleSimulationSettings current = Settings;
            heat.Conductivity = current.Conductivity;
            heat.VoidHeatLoss = current.VoidHeatLoss;
            heat.DebugHeatOverlay = _heatDebugOverlay;
            heat.DebugFullScaleTemperature = _heatDebugFullScale;

            if (_simulate)
            {
                ParticleSimulationSettings settings = Settings;
                float stepSeconds = 1f / settings.TickRate;
                int maxSteps = settings.MaxStepsPerFrame;
                int stepsPerPresent = settings.StepsPerPresent;

                _accumulator += Time.deltaTime;

                float maxLag = stepSeconds * maxSteps;
                if (_accumulator > maxLag)
                {
                    _accumulator = maxLag;
                }

                int stepped = 0;
                while (_accumulator >= stepSeconds && stepped < maxSteps)
                {
                    _world.Step();
                    _accumulator -= stepSeconds;
                    stepped++;

                    // 渲染与模拟解耦的接缝：跑完这个逻辑帧之后是否上屏，只在这里决定。
                    _stepsSincePresent++;
                    if (_stepsSincePresent >= stepsPerPresent)
                    {
                        Present();
                    }
                }
            }
        }

        private void Present()
        {
            _stepsSincePresent = 0;
            if (_renderer != null)
            {
                _renderer.Present(_world);
            }
        }
    }
}
