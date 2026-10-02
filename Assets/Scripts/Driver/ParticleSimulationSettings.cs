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
        [SerializeField, Min(1)] private int _width = 320;
        [SerializeField, Min(1)] private int _height = 180;

        [Tooltip("每秒推进多少个逻辑帧。与渲染帧率无关。")]
        [SerializeField, Min(1f)] private float _tickRate = 60f;

        [Tooltip("单帧内最多追多少个逻辑帧，防止卡顿后的死亡螺旋。")]
        [SerializeField, Range(1, 16)] private int _maxStepsPerFrame = 4;

        [Tooltip("每多少个逻辑帧上屏一次。1 = 每个逻辑帧后渲染一次（默认）。调大可让多个逻辑帧只渲染一次。")]
        [SerializeField, Min(1)] private int _stepsPerPresent = 1;

        [SerializeField] private int _seed = 123456789;

        public int Width
        {
            get { return Mathf.Clamp(_width, 1, ParticleWorld.MaxSide); }
        }

        public int Height
        {
            get { return Mathf.Clamp(_height, 1, ParticleWorld.MaxSide); }
        }

        public float TickRate
        {
            get { return Mathf.Max(1f, _tickRate); }
        }

        public int MaxStepsPerFrame
        {
            get { return Mathf.Max(1, _maxStepsPerFrame); }
        }

        /// <summary>每多少个逻辑帧上屏一次。1 = 每个逻辑帧后渲染一次。</summary>
        public int StepsPerPresent
        {
            get { return Mathf.Max(1, _stepsPerPresent); }
        }

        public uint Seed
        {
            get { return unchecked((uint)_seed); }
        }
    }
}
