using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 光照控制器：一个按时序通断的光源，照亮时向**每一列最上面的非气体粒子**（固体或液体——光打到水面就停）注入热量。
    /// 纯 C#、无 Unity 依赖，由 <see cref="ParticleWorld.StepLogic"/> 每逻辑帧驱动一次（与模拟同频、确定性）。
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
    /// <para>**材料光照吸收率**：到达格子的能量还要乘上该材料的光照吸收率
    /// <see cref="ParticleTypeInfo.LightAbsorption"/> ∈ [0,1]（1 = 完全吸收；0 = 全反射、光照不升温）。
    /// 用来表达「浅色 / 反光的表面（如草）在阳光下升温更慢」——草取 0.8（略减弱）。</para>
    /// <para>**云会遮光**：施热时自顶向下逐格累加**光路上**的湿度（遇第一个固/液格即止），
    /// 按 Beer–Lambert 衰减，到达地表的能量是
    /// <c>Intensity × exp(-<see cref="CloudExtinction"/> × 光路湿度)</c>（即 <see cref="GetCloudTransmittance"/>）。
    /// 因此云厚的那一列地表凉、云薄的那一列地表热——这给水循环加了一条负反馈
    /// （云多 → 地表冷 → 蒸发少 → 云少）。<see cref="CloudExtinction"/> = 0 即关掉这条。
    /// 只算光路上的湿度，意味着洞穴 / 顶棚下方的地表不会再被上方云层连带遮黑。</para>
    /// <para>简化：一次性均匀照到整个网格，没有方向、也不按距离或角度衰减。需要阴影 / 衰减再另行扩展。</para>
    /// <para>另导出 <see cref="Daylight"/>（0 = 夜、1 = 昼）给渲染层做整场明暗：白天保持上限、夜晚保持下限，
    /// 只在白天开始 / 结束的 <see cref="TransitionTicks"/> 刻里平滑过渡。它与施热无关，只是时序的读数。</para>
    /// </remarks>
    public sealed class LightController
    {
        /// <summary><see cref="CloudExtinction"/> 的默认值（每单位列云量）。</summary>
        public const float DefaultCloudExtinction = 0.05f;

        private bool _enabled;
        private int _litTicks = 60;
        private int _darkTicks = 60;
        private float _intensity = 5f;
        private int _transitionTicks = 60;
        private float _cloudExtinction = DefaultCloudExtinction;
        private ParticleWorld _world;
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

        /// <summary>
        /// 云层消光系数（每单位「列云层厚度」）。到达地表的能量按**每列**衰减：
        /// <c>透过率 = exp(-CloudExtinction × 光路上的湿度总量)</c>（Beer–Lambert）。
        /// 0 = 云不遮光（旧行为）。负数 / NaN 归零。
        /// </summary>
        public float CloudExtinction
        {
            get { return _cloudExtinction; }
            set { _cloudExtinction = (float.IsNaN(value) || value < 0f) ? 0f : value; }
        }

        /// <summary>
        /// 第 <paramref name="x"/> 列的**云层厚度** = 光从网格顶竖直下落到第一个固/液格这一段路上
        /// 累加的湿度（只有非固非液格持有湿度，所以就是光程上的云量）。按需计算；越界 / 尚未
        /// <see cref="Step"/> 过返回 0。
        /// </summary>
        public float GetCloudThickness(int x)
        {
            ParticleWorld world = _world;
            if (world == null || (uint)x >= (uint)world.Width)
            {
                return 0f;
            }

            int width = world.Width;
            HumidityField humidity = world.Humidity;
            float thickness = 0f;
            for (int y = world.Height - 1; y >= 0; y--)
            {
                if (ParticleRules.BlocksLight(world, x, y))
                {
                    break;
                }

                thickness += humidity.GetAt(y * width + x);
            }

            return thickness;
        }

        /// <summary>第 <paramref name="x"/> 列当前的透光率 ∈ (0, 1]：<c>exp(-CloudExtinction × 云层厚度)</c>。
        /// 消光系数为 0 或无云时恒为 1。</summary>
        public float GetCloudTransmittance(int x)
        {
            return CloudTransmittance(GetCloudThickness(x));
        }

        /// <summary>给定光路湿度总量的透光率 ∈ (0, 1]：<c>exp(-CloudExtinction × 厚度)</c>；消光为 0 或无云时恒为 1。</summary>
        private float CloudTransmittance(float thickness)
        {
            if (_cloudExtinction <= 0f || thickness <= 0f)
            {
                return 1f;
            }

            return (float)Math.Exp(-_cloudExtinction * thickness);
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

        /// <summary>
        /// 昼夜过渡刻数（&gt;= 0）：白天开始与结束各用这么多刻从暗平滑到亮（反之亦然）。
        /// 实际生效值被夹在 <c>[0, LitTicks / 2]</c>，避免升 / 降坡重叠。
        /// </summary>
        public int TransitionTicks
        {
            get { return _transitionTicks; }
            set { _transitionTicks = value < 0 ? 0 : value; }
        }

        /// <summary>
        /// 归一化日照度 ∈ [0, 1]：1 = 白天（上限），0 = 夜晚（下限），中间的过渡只出现在白天开始 / 结束的
        /// <see cref="TransitionTicks"/> 刻里。暗相位、<see cref="LitTicks"/> = 0 时恒为 0。
        /// </summary>
        /// <remarks>
        /// 纯导出量：不推进任何状态，也不参与模拟——渲染层靠它把整场像素按亮度缩放（白天上限、夜晚下限）。
        /// 光照关闭（<see cref="Enabled"/> = false）时恒为 1：没有昼夜循环就没有明暗变化。
        /// </remarks>
        public float Daylight
        {
            get
            {
                if (!_enabled)
                {
                    return 1f;
                }

                if (_litTicks <= 0)
                {
                    return 0f;
                }

                if (_darkTicks <= 0)
                {
                    return 1f; // 一直白天，没有「到来 / 结束」可言
                }

                int cycle = _litTicks + _darkTicks;
                int position = _elapsed % cycle;
                if (position >= _litTicks)
                {
                    return 0f; // 暗相位
                }

                int half = _litTicks / 2;
                int ramp = _transitionTicks < half ? _transitionTicks : half;
                if (ramp <= 0)
                {
                    return 1f; // 无过渡：白天一到就是上限
                }

                if (position < ramp)
                {
                    return position / (float)ramp; // 破晓：从下限升到上限
                }

                int remaining = _litTicks - position;
                if (remaining <= ramp)
                {
                    return remaining / (float)ramp; // 黄昏：从上限降回下限
                }

                return 1f;
            }
        }

        /// <summary>把相位拨回起点（下一个逻辑帧从亮相位开始）。</summary>
        public void Reset()
        {
            _elapsed = 0;
        }

        /// <summary>
        /// 推进一逻辑帧：照亮时向每列最上面的非气体粒子（固体或液体）注入按**光路云量**衰减后的
        /// <see cref="Intensity"/> 热量。
        /// 由 <see cref="ParticleWorld.StepLogic"/> 调用；正常玩法不要手动调用，否则会额外施热、破坏确定性。
        /// </summary>
        public void Step(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _world = world; // 供 GetCloudThickness / GetCloudTransmittance 按需扫描，不参与施热。

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

        /// <summary>
        /// (x,y) 材料的光照吸收率（<see cref="ParticleTypeInfo.LightAbsorption"/>）：光照注入本格时按它缩放。
        /// 空气 / 越界 / 空槽一律返回 1（空气不是光程终点，正常不会走到这里）。
        /// </summary>
        private static float LightAbsorptionAt(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            if (id == ParticleRegistry.AirTypeId)
            {
                return 1f;
            }

            ParticleType[] types = world.Types;
            if (id >= types.Length || types[id] == null)
            {
                return 1f;
            }

            return types[id].Info.LightAbsorption;
        }

        private void ApplyHeat(ParticleWorld world, float amount)
        {
            HeatMap heat = world.Heat;
            HumidityField humidity = world.Humidity;
            int width = world.Width;
            int height = world.Height;

            for (int x = 0; x < width; x++)
            {
                // 云层遮蔽：自顶向下边走边累加**光路上**的湿度（遇第一个固/液格即止），
                // 到达该格的能量按 Beer–Lambert 衰减（透过率 = exp(-k × 光路湿度)）。
                // 只算光路上，洞内 / 顶棚下方的湿度因此不遮光。
                float thickness = 0f;
                for (int y = height - 1; y >= 0; y--)
                {
                    if (ParticleRules.BlocksLight(world, x, y))
                    {
                        // 按本格材料的光照吸收率缩放（浅色 / 反光表面升温更慢；草用 0.8）。
                        float reach = amount * CloudTransmittance(thickness) * LightAbsorptionAt(world, x, y);
                        if (reach > 0f)
                        {
                            heat.AddHeat(x, y, reach);
                        }

                        break;
                    }

                    thickness += humidity.GetAt(y * width + x);
                }
            }
        }
    }
}
