using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 固定时间步长驱动 + 渲染调度。是**唯一**同时认识模拟与渲染的地方。
    /// </summary>
    /// <remarks>
    /// 解耦点：<see cref="ParticleWorld.StepMotion"/> / <see cref="ParticleWorld.StepLogic"/> 只管推进模拟，从不渲染；
    /// 渲染由本类在 <see cref="Update"/> 里按 <see cref="ParticleSimulationSettings.StepsPerPresent"/> 决定何时上屏。
    /// 默认值 1 = 每个运动帧后渲染一次；调大即可变成「多个运动帧渲染一次」，无需改动模拟或渲染代码。
    /// <para>两级节拍：运动帧 60 Hz（只跑粒子运动）、逻辑帧 20 Hz（相变 / 反应 / 热传导 / 湿度 / 降水 / 光照 / 随机刻）。
    /// 逻辑帧是运动帧节拍上的子节拍——每 <see cref="ParticleSimulationSettings.LogicTicksPerMotionTick"/>
    /// 个运动帧跑一次 <see cref="ParticleWorld.StepLogic"/>，其余只跑 <see cref="ParticleWorld.StepMotion"/>。</para>
    /// <para>沙箱尺寸：默认取 <see cref="ParticleSimulationSettings"/> 的宽高，本组件的「沙箱尺寸」字段非 0 时覆盖之。
    /// 运行中改尺寸（Inspector 或 <see cref="Resize"/>）会重建一个空世界，已有内容不保留。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSimulationDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationSettings _settings;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("是否推进模拟。关掉即暂停：运动帧与逻辑帧一起冻结，仍可用 PresentNow 手动上屏。")]
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
        private int _motionTicks;

        /// <summary>当前世界。在 <c>Awake</c> 之后可用。</summary>
        public ParticleWorld World
        {
            get { return _world; }
        }

        /// <summary>是否推进模拟（运动帧与逻辑帧）。运行时可切换（继续/暂停）。</summary>
        public bool Simulate
        {
            get { return _simulate; }
            set { _simulate = value; }
        }

        /// <summary>是否处于暂停：暂停时不推进任何帧（运动帧与逻辑帧一起冻结），网格内容不变化。</summary>
        public bool Paused
        {
            get { return !_simulate; }
            set { _simulate = !value; }
        }

        /// <summary>进入暂停：冻结所有帧（运动帧与逻辑帧）。幂等。由调用方决定何时暂停（例如输入处理）。</summary>
        public void Pause()
        {
            _simulate = false;
        }

        /// <summary>退出暂停：恢复推进所有帧。幂等。</summary>
        public void Resume()
        {
            _simulate = true;
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

        /// <summary>按 <see cref="RequestedWidth"/>/<see cref="RequestedHeight"/> 建世界并清空累加器，
        /// 然后把所有空格填成大气粒子——风与对流需要空气本身是会动、携带热量的物质。
        /// typeId 0 此后只作为「被抹除留下的真空」短暂出现，行为规则同样允许气体换入。</summary>
        private void CreateWorld()
        {
            ParticleSimulationSettings settings = Settings;
            _world = new ParticleWorld(RequestedWidth, RequestedHeight, settings.Seed);
            _accumulator = 0f;
            _stepsSincePresent = 0;
            _motionTicks = 0;

            if (ParticleIds.Atmosphere != ParticleRegistry.AirTypeId)
            {
                for (int y = 0; y < _world.Height; y++)
                {
                    for (int x = 0; x < _world.Width; x++)
                    {
                        _world.Set(x, y, ParticleIds.Atmosphere);
                    }
                }
            }
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
            heat.RadiativeLoss = current.RadiativeLoss;
            heat.AdiabaticLapse = current.AdiabaticLapse;
            heat.AirThermalConductivity = current.AirThermalConductivity;
            heat.AirThermalEmissivity = current.AirThermalEmissivity;
            heat.DebugHeatOverlay = _heatDebugOverlay;
            heat.DebugFullScaleTemperature = _heatDebugFullScale;
            _world.RandomTicksPerStep = current.RandomTicksPerStep;

            if (_simulate)
            {
                ParticleSimulationSettings settings = Settings;
                float stepSeconds = 1f / settings.MotionRate;
                int maxSteps = settings.MaxStepsPerFrame;
                int stepsPerPresent = settings.StepsPerPresent;
                int logicEvery = settings.LogicTicksPerMotionTick;

                _accumulator += Time.deltaTime;

                float maxLag = stepSeconds * maxSteps;
                if (_accumulator > maxLag)
                {
                    _accumulator = maxLag;
                }

                int stepped = 0;
                while (_accumulator >= stepSeconds && stepped < maxSteps)
                {
                    // 逻辑帧是运动帧节拍上的一个子节拍：每 logicEvery 个运动帧跑一次「逻辑 + 运动」，
                    // 其余运动帧只跑运动。因此运动帧 60 Hz、逻辑帧 = 60 / logicEvery。
                    _motionTicks++;
                    if (_motionTicks >= logicEvery)
                    {
                        _motionTicks = 0;
                        _world.StepLogic();
                    }
                    else
                    {
                        _world.StepMotion();
                    }

                    _accumulator -= stepSeconds;
                    stepped++;

                    // 渲染与模拟解耦的接缝：跑完这个运动帧之后是否上屏，只在这里决定。
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
