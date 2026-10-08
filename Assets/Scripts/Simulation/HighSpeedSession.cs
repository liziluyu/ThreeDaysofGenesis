using System;
using System.Collections.Generic;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 高速模式（High-Speed Mode）的**内核状态机与统计**：纯 C#（不引用 UnityEngine），
    /// 因此既能在 Unity 里被驱动调用，也能在 net9 测试工程下单独编译与测试。
    /// 输入与显示在 <c>ParticleEditor</c>，驱动只做转发（契约见 docs/ParticleTypeInterface.md「高速模式」）。
    /// </summary>
    /// <remarks>
    /// <para>一次高速模式的生命周期：<see cref="Enter"/> → 每逻辑帧 <see cref="OnLogicTick"/> →
    /// 达到 <see cref="DurationLogicTicks"/> 置 <see cref="Completed"/> → 驱动自动暂停 →
    /// 玩家确认后 <see cref="Exit"/>（倍速钉回 1×，仍保持暂停）。</para>
    /// <para>期间做三件事：①挂 <see cref="HighSpeedMonitor"/> 增量统计每种粒子的产生 / 死亡（O(1)，不重扫网格）；
    /// ②把倍速钉到满档 <see cref="SpeedController.MaxSpeed"/>×（退出时回 1×——普通编辑模式不允许调速度）；
    /// ③每 <see cref="SampleInterval"/> 逻辑帧采一次地表平均温度与世界平均湿度（O(W) / O(1)）。</para>
    /// <para><b>确定性</b>：监视器是纯观察者——零 RNG 消耗、零格子写入，挂 / 摘它不改变世界的推进结果。</para>
    /// <para>热路径零分配：采样缓冲在 <see cref="Enter"/> 时按 <c>时长 / 采样间隔</c> 一次预留；
    /// <see cref="OnLogicTick"/> 只有一次整数取模与两次数组读。</para>
    /// </remarks>
    public sealed class HighSpeedSession
    {
        /// <summary>采样间隔（逻辑帧）：每这么多逻辑帧采一次地表平均温度与世界平均湿度。</summary>
        public const int SampleInterval = 10;

        private readonly ParticleWorld _world;
        private readonly int _duration;
        private readonly List<float> _surfaceTemperatures;
        private readonly List<float> _averageHumidities;

        private HighSpeedMonitor _monitor;
        private bool _active;
        private bool _completed;
        private int _elapsed;
        private float _latestSurfaceTemperature = float.NaN;
        private float _latestAverageHumidity = float.NaN;

        /// <param name="world">被观察的世界（监视器只读它，绝不写入）。</param>
        /// <param name="durationLogicTicks">本次高速模式的时长（逻辑帧）；钳到 ≥ 1。</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 为 null。</exception>
        public HighSpeedSession(ParticleWorld world, int durationLogicTicks)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _world = world;
            _duration = durationLogicTicks < 1 ? 1 : durationLogicTicks;
            _surfaceTemperatures = new List<float>(_duration / SampleInterval + 1);
            _averageHumidities = new List<float>(_duration / SampleInterval + 1);
        }

        /// <summary>进入后、退出前恒为 true。</summary>
        public bool Active
        {
            get { return _active; }
        }

        /// <summary>已达到 <see cref="DurationLogicTicks"/>（<see cref="Active"/> 仍为 true，直到 <see cref="Exit"/>）。</summary>
        public bool Completed
        {
            get { return _completed; }
        }

        /// <summary>已推进的逻辑帧数（进入后从 0 开始；完成后不再增长）。</summary>
        public int ElapsedLogicTicks
        {
            get { return _elapsed; }
        }

        /// <summary>本次高速模式的时长（逻辑帧）。</summary>
        public int DurationLogicTicks
        {
            get { return _duration; }
        }

        /// <summary>已采样的样本数（每 <see cref="SampleInterval"/> 逻辑帧一个，满程 = 时长 / 10）。</summary>
        public int SampleCount
        {
            get { return _surfaceTemperatures.Count; }
        }

        /// <summary>最近一次采样的地表平均温度；尚未采样时为 <see cref="float.NaN"/>。</summary>
        public float LatestSurfaceTemperature
        {
            get { return _latestSurfaceTemperature; }
        }

        /// <summary>最近一次采样的世界平均湿度（湿度总量 / 格数）；尚未采样时为 <see cref="float.NaN"/>。</summary>
        public float LatestAverageHumidity
        {
            get { return _latestAverageHumidity; }
        }

        /// <summary>历次采样的地表平均温度（按时间序，长度 = <see cref="SampleCount"/>）。</summary>
        public IReadOnlyList<float> SurfaceTemperatureSamples
        {
            get { return _surfaceTemperatures; }
        }

        /// <summary>历次采样的世界平均湿度（按时间序，长度 = <see cref="SampleCount"/>）。</summary>
        public IReadOnlyList<float> AverageHumiditySamples
        {
            get { return _averageHumidities; }
        }

        /// <summary>
        /// 进入高速模式：挂上监视器、把 <paramref name="speed"/> 直接钉到**满档 <see cref="SpeedController.MaxSpeed"/>×**
        /// （手动档；跑不满由预算封顶，实测吞吐看 <see cref="SpeedController.EffectiveSpeed"/>），并从零开始计时。
        /// **幂等**：已 <see cref="Active"/> 时什么都不做。
        /// </summary>
        /// <param name="speed">驱动持有的倍速控制器；进入即钉满档，<see cref="Exit"/> 时回 1×。</param>
        public void Enter(SpeedController speed)
        {
            if (_active)
            {
                return;
            }

            speed.SetSpeed(SpeedController.MaxSpeed);

            _elapsed = 0;
            _completed = false;
            _surfaceTemperatures.Clear();
            _averageHumidities.Clear();
            _latestSurfaceTemperature = float.NaN;
            _latestAverageHumidity = float.NaN;

            // 构造即把每列地表全扫一遍（此后全靠增量维护），再挂到世界的写钩子上。
            _monitor = new HighSpeedMonitor(_world);
            _world._monitor = _monitor;
            _active = true;
        }

        /// <summary>
        /// 退出高速模式：摘掉监视器、把倍速钉回 1×（普通编辑模式恒为 1×），并清空统计与计时。
        /// **幂等**：未 <see cref="Active"/> 时什么都不做。
        /// </summary>
        /// <param name="speed">与 <see cref="Enter"/> 同一个倍速控制器。</param>
        public void Exit(SpeedController speed)
        {
            if (!_active)
            {
                return;
            }

            _world._monitor = null;
            _monitor.ResetCounters();
            _monitor = null;

            speed.SetSpeed(SpeedController.MinSpeed);

            _active = false;
            _completed = false;
            _elapsed = 0;
            _surfaceTemperatures.Clear();
            _averageHumidities.Clear();
            _latestSurfaceTemperature = float.NaN;
            _latestAverageHumidity = float.NaN;
        }

        /// <summary>
        /// 推进一个逻辑帧：计数、每 <see cref="SampleInterval"/> 帧采一次样、到时长置 <see cref="Completed"/>。
        /// 由驱动在**世界跑完这一个逻辑帧之后**调用。未进入或已完成时是空操作（完成后不再计数 / 采样）。
        /// </summary>
        public void OnLogicTick()
        {
            if (!_active || _completed)
            {
                return;
            }

            _elapsed++;
            if (_elapsed % SampleInterval == 0)
            {
                Sample();
            }

            if (_elapsed >= _duration)
            {
                _completed = true;
            }
        }

        /// <summary>typeId 的产生数（空气 = 0 也计数，显示时过滤；越界或未进入时返回 0）。</summary>
        public long GetBirths(int typeId)
        {
            return _monitor == null ? 0L : _monitor.GetBirths(typeId);
        }

        /// <summary>typeId 的死亡数（空气 = 0 也计数，显示时过滤；越界或未进入时返回 0）。</summary>
        public long GetDeaths(int typeId)
        {
            return _monitor == null ? 0L : _monitor.GetDeaths(typeId);
        }

        /// <summary>
        /// 采一次样：**地表平均温度** = Σ T(top[x]) / W（没有非气体格的列贡献 0，除数恒为网格宽），
        /// **世界平均湿度** = 湿度总量 / (W·H)。前者 O(W)：只读每列的地表格温度，不重扫网格；
        /// 后者 O(1)：读 <see cref="HumidityField"/> 增量维护的总量。
        /// </summary>
        private void Sample()
        {
            int width = _world.Width;
            int height = _world.Height;
            HeatMap heat = _world.Heat;

            float sum = 0f;
            for (int x = 0; x < width; x++)
            {
                int y = _monitor.SurfaceTop(x);
                if (y >= 0)
                {
                    sum += heat.GetTemperature(x, y);
                }
            }

            _latestSurfaceTemperature = sum / width;
            _latestAverageHumidity = (float)(_world.Humidity.Total / ((double)width * height));
            _surfaceTemperatures.Add(_latestSurfaceTemperature);
            _averageHumidities.Add(_latestAverageHumidity);
        }
    }
}
