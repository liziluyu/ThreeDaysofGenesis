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
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSimulationDriver : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationSettings _settings;
        [SerializeField] private ParticleRenderer _renderer;

        [Tooltip("关掉只停逻辑推进，仍可手动上屏。")]
        [SerializeField] private bool _simulate = true;

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

        /// <summary>把一个逻辑帧的固定步长立即上屏（暂停状态下改网格后可用）。</summary>
        public void PresentNow()
        {
            Present();
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

            ParticleSimulationSettings settings = Settings;
            _world = new ParticleWorld(settings.Width, settings.Height, settings.Seed);
            _accumulator = 0f;
            _stepsSincePresent = 0;
        }

        private void Start()
        {
            Present();
        }

        private void Update()
        {
            if (_simulate && _world != null)
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
