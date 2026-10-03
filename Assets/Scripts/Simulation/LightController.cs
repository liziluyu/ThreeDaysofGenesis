using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 光照控制器：一个按时序通断的光源，照亮时向**每一列最上面的非气体粒子**（固体或液体——光打到水面就停）注入热量。
    /// 纯 C#、无 Unity 依赖，由 <see cref="ParticleWorld.Step"/> 每 tick 驱动一次（与模拟同频、确定性）。
    /// </summary>
    /// <remarks>
    /// <para>通断是**占空比循环**：<see cref="Enabled"/> 打开后，先亮相位持续 <see cref="LitTicks"/> 刻，
    /// 再暗相位持续 <see cref="DarkTicks"/> 刻，如此往复（白天 / 黑夜、呼吸式频闪）。</para>
    /// <list type="bullet">
    /// <item><see cref="LitTicks"/> = 0：从不照亮（只有黑夜）。</item>
    /// <item><see cref="DarkTicks"/> = 0：一直照亮（只有白天）。</item>
    /// <item><see cref="Enabled"/> = false：总开关关闭，不注入热量、相位也不推进。</item>
    /// </list>
    /// <para>照亮时对每个 x 从 (x, Height-1) 向下扫，遇到第一个带 <see cref="ParticleTags.Solid"/> 或
    /// <see cref="ParticleTags.Liquid"/> 标签的格，就往那格加 <see cref="Intensity"/> 的热量 Q
    /// （不是温度——升温多少还取决于该物质的热容）。光只穿过气体：海洋是表面被晒热、表面蒸发，
    /// 而不是被穿透过热海底。</para>
    /// <para>简化：一次性均匀照到整个网格，没有方向、也不按距离或角度衰减。需要阴影 / 衰减再另行扩展。</para>
    /// </remarks>
    public sealed class LightController
    {
        private bool _enabled;
        private int _litTicks = 60;
        private int _darkTicks = 60;
        private float _intensity = 5f;
        private int _elapsed;

        /// <summary>是否光照（总开关）。关闭时不注入热量，相位也不推进。</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>亮相位持续刻数（&gt;= 0）。0 = 从不照亮。</summary>
        public int LitTicks
        {
            get { return _litTicks; }
            set { _litTicks = value < 0 ? 0 : value; }
        }

        /// <summary>暗相位持续刻数（&gt;= 0）。0 = 一直照亮。</summary>
        public int DarkTicks
        {
            get { return _darkTicks; }
            set { _darkTicks = value < 0 ? 0 : value; }
        }

        /// <summary>光照强度：亮相位每刻向每列最上面的非气体粒子注入的热量 Q。NaN / 无穷被归零。</summary>
        public float Intensity
        {
            get { return _intensity; }
            set { _intensity = (float.IsNaN(value) || float.IsInfinity(value)) ? 0f : value; }
        }

        /// <summary>开启后已推进的刻数（只读，用于观测相位）。</summary>
        public int Elapsed
        {
            get { return _elapsed; }
        }

        /// <summary>当前是否处于亮相位（且已打开）。</summary>
        public bool IsLit
        {
            get
            {
                if (!_enabled || _litTicks <= 0)
                {
                    return false;
                }

                if (_darkTicks <= 0)
                {
                    return true;
                }

                return _elapsed % (_litTicks + _darkTicks) < _litTicks;
            }
        }

        /// <summary>把相位拨回起点（下一个 tick 从亮相位开始）。</summary>
        public void Reset()
        {
            _elapsed = 0;
        }

        /// <summary>
        /// 推进一 tick：照亮时向每列最上面的非气体粒子（固体或液体）注入 <see cref="Intensity"/> 热量。
        /// 由 <see cref="ParticleWorld.Step"/> 调用；正常玩法不要手动调用，否则会额外施热、破坏确定性。
        /// </summary>
        public void Step(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!_enabled)
            {
                return;
            }

            if (IsLit && _intensity != 0f)
            {
                ApplyHeat(world, _intensity);
            }

            _elapsed++;
        }

        private static void ApplyHeat(ParticleWorld world, float amount)
        {
            HeatMap heat = world.Heat;
            int width = world.Width;
            int height = world.Height;

            for (int x = 0; x < width; x++)
            {
                for (int y = height - 1; y >= 0; y--)
                {
                    if (ParticleRules.BlocksLight(world, x, y))
                    {
                        heat.AddHeat(x, y, amount);
                        break;
                    }
                }
            }
        }
    }
}
