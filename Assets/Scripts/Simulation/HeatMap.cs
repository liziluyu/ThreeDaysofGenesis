using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 与 <see cref="ParticleWorld"/> 网格同形的一张热图：每格存**热量 Q**（<see cref="float"/>），
    /// 温度由热量与该格物质的热容换算：<c>T = Q / C</c>。
    /// <b>热量由物质携带</b>：粒子移动（<see cref="ParticleWorld.Swap"/>）时 Q 随粒子搬家；
    /// 生成 / 抹除（<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.Clear"/>）换上的是新物质，
    /// 其 Q 为 <see cref="AmbientHeat"/>（0）——旧物质连带它的热一起离场。
    /// </summary>
    /// <remarks>
    /// <para>存储：扁平 <c>float[]</c>（Q），下标 = <c>y * Width + x</c>，与 <see cref="ParticleWorld.Cells"/> 一致。</para>
    /// <para>温度是导出量，不单独存储：<c>T = Q / C</c>，C 取自该格粒子类型的
    /// <see cref="ParticleTypeInfo.HeatCapacity"/>；空气 / 越界用 <see cref="AirHeatCapacity"/>。</para>
    /// <para>热传导：<see cref="Step"/> 做一次全格四邻扩散（显式差分）。
    /// 每对相邻格按 <c>ΔQ = Conductivity * (T_邻 - T_本)</c> 交换热量，两格大小相等方向相反——
    /// 所以**界内是守恒的**，热量只是在格与格之间搬家。
    /// 扩散用的是本 tick 开始时的温度场（先算差、再整体写回），与扫描顺序无关、结果确定。</para>
    /// <para>散热：同一次 <see cref="Step"/> 里，最上面一行还向**界外上方的虚空**流失热量
    /// （<c>ΔQ = -VoidHeatLoss * T</c>，虚空恒为 <see cref="AmbientHeat"/>）。
    /// 这是热量离开世界的唯一出口——没有它，光照注入的热只增不减；
    /// 有它之后，持续受热的世界会收敛到稳态温度 <c>T ≈ 输入功率 / VoidHeatLoss</c>。</para>
    /// <para>简化 / 不完美：单一全局导热系数，不区分材料导热率；无对流、无相变潜热；
    /// 散热只发生在网格顶行（不是任意暴露表面）；<c>Q</c> 不钳制（负值表示冷）。这些留给具体粒子类型自行实现。</para>
    /// <para>稳定性：显式扩散要求 <c>(Conductivity * 邻居数 + VoidHeatLoss) / 热容 &lt; 2</c>。
    /// 两者上限都是 <see cref="MaxConductivity"/> = 0.24，配合热容 ≳ 0.5 的取值即可稳定；
    /// 若把热容设得极小又拉满导热，会看到温度来回振荡。</para>
    /// </remarks>
    public sealed class HeatMap
    {
        /// <summary>环境热量：热图初值、越界读的返回值，也是新生成物质的初始热量。</summary>
        public const float AmbientHeat = 0f;

        /// <summary>空气的热容。空气没有类型条目，比重、导热都按这个常量算。</summary>
        public const float AirHeatCapacity = 1f;

        /// <summary><see cref="Conductivity"/> 的上限，取它是为了显式扩散稳定（见类型注释）。</summary>
        public const float MaxConductivity = 0.24f;

        /// <summary><see cref="Conductivity"/> 的默认值。</summary>
        public const float DefaultConductivity = 0.15f;

        /// <summary><see cref="VoidHeatLoss"/> 的默认值。</summary>
        public const float DefaultVoidHeatLoss = 0.1f;

        /// <summary><see cref="DebugFullScaleTemperature"/> 的默认值。</summary>
        public const float DefaultDebugFullScaleTemperature = 10f;

        private readonly int _width;
        private readonly int _height;
        private readonly Cell[] _cells;
        private readonly ParticleType[] _types;
        private readonly float[] _inverseCapacity;

        private float[] _heat;
        private float[] _scratch;
        private float _conductivity = DefaultConductivity;
        private float _voidHeatLoss = DefaultVoidHeatLoss;

        /// <param name="width">宽度（格），范围 <c>1..ParticleWorld.MaxSide</c>。</param>
        /// <param name="height">高度（格），范围 <c>1..ParticleWorld.MaxSide</c>。</param>
        /// <param name="cells">世界的格子数组（同宽高）。热图只读它来查每格物质的热容。</param>
        /// <param name="types">类型表（同 <see cref="ParticleWorld.Types"/>），用于查 <see cref="ParticleTypeInfo.HeatCapacity"/>。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一边超出 <c>1..ParticleWorld.MaxSide</c>。</exception>
        public HeatMap(int width, int height, Cell[] cells, ParticleType[] types)
        {
            if (width < 1 || width > ParticleWorld.MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, $"width 必须在 1..{ParticleWorld.MaxSide} 之间。");
            }

            if (height < 1 || height > ParticleWorld.MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, $"height 必须在 1..{ParticleWorld.MaxSide} 之间。");
            }

            if (cells == null || cells.Length != width * height)
            {
                throw new ArgumentException("cells 长度必须等于 width * height。", nameof(cells));
            }

            if (types == null || types.Length == 0 || types[0] != null)
            {
                throw new ArgumentException("types 不能为空，且下标 0（空气）必须为 null。", nameof(types));
            }

            _width = width;
            _height = height;
            _cells = cells;
            _types = types;
            _heat = new float[width * height];
            _scratch = new float[width * height];
            _inverseCapacity = new float[width * height];
        }

        /// <summary>热图宽（格数），与世界网格一致。</summary>
        public int Width
        {
            get { return _width; }
        }

        /// <summary>热图高（格数），与世界网格一致。</summary>
        public int Height
        {
            get { return _height; }
        }

        /// <summary>格子总数。热量缓冲为 4 字节/格。</summary>
        public int CellCount
        {
            get { return _heat.Length; }
        }

        /// <summary>
        /// 底层热量缓冲（Q，不是温度）。<b>只读契约</b>：外部只允许读，
        /// 写入一律走 <see cref="SetHeat"/> / <see cref="AddHeat"/>。
        /// </summary>
        public float[] Values
        {
            get { return _heat; }
        }

        /// <summary>
        /// 热传导系数（无量纲），范围 <c>[0, <see cref="MaxConductivity"/>]</c>。
        /// 每对相邻格每 tick 交换的热量占比；0 = 关闭传导。超出范围会被夹紧。
        /// </summary>
        public float Conductivity
        {
            get { return _conductivity; }
            set { _conductivity = value < 0f ? 0f : (value > MaxConductivity ? MaxConductivity : value); }
        }

        /// <summary>
        /// 顶行向「虚空」散热的系数（无量纲），范围 <c>[0, <see cref="MaxConductivity"/>]</c>；0 = 关闭（封闭系统）。
        /// </summary>
        /// <remarks>
        /// 每一 tick，网格最上面一行（<c>y = Height - 1</c>）都会向界外上方的虚空流失热量，
        /// 流失量正比于该格温度（虚空恒为 <see cref="AmbientHeat"/>，即一个无限大的冷库）。
        /// 这是热量离开世界的唯一出口：没有它，光照注入的热只增不减；有了它，世界会收敛到一个稳态温度。
        /// 与 <see cref="Conductivity"/> 独立——不会被它缩放，也不影响界内的守恒。
        /// </remarks>
        public float VoidHeatLoss
        {
            get { return _voidHeatLoss; }
            set { _voidHeatLoss = value < 0f ? 0f : (value > MaxConductivity ? MaxConductivity : value); }
        }

        /// <summary>
        /// <b>调试开关</b>：为 true 时渲染层（<see cref="ParticlePixelBuffer.Fill"/>）把每格温度叠成热力色——
        /// 越热越红，空气格也会显示。**只影响显示，不参与模拟**，随时可开可关。
        /// 世界重建（改网格尺寸）会连热图一起换新，届时由外部接线（如 <c>ParticleSimulationDriver</c>）重新写入。
        /// </summary>
        public bool DebugHeatOverlay { get; set; }

        /// <summary>
        /// 调试热力叠加的**满量程温度**：温度达到它即为纯红，0 为不叠色。
        /// <c>&lt;= 0</c> 时叠加整体失效（不会除零）。量纲自定，与玩法约定的温度单位一致。
        /// </summary>
        public float DebugFullScaleTemperature { get; set; } = DefaultDebugFullScaleTemperature;

        /// <summary>把 (x,y) 换算成 <see cref="Values"/> 的下标。不检查越界。</summary>
        public int Index(int x, int y)
        {
            return y * _width + x;
        }

        /// <summary>(x,y) 是否在热图内。</summary>
        public bool IsInBounds(int x, int y)
        {
            return (uint)x < (uint)_width && (uint)y < (uint)_height;
        }

        /// <summary>读取 (x,y) 的热量 Q。越界返回 <see cref="AmbientHeat"/>。</summary>
        public float GetHeat(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return AmbientHeat;
            }

            return _heat[y * _width + x];
        }

        /// <summary>读取 (x,y) 上物质的热容 C。越界 / 空气返回 <see cref="AirHeatCapacity"/>。</summary>
        public float GetCapacity(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return AirHeatCapacity;
            }

            return CapacityOf(_cells[y * _width + x].typeId);
        }

        /// <summary>读取 (x,y) 的温度 <c>T = Q / C</c>。越界返回 <see cref="AmbientHeat"/>。</summary>
        public float GetTemperature(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return AmbientHeat;
            }

            int index = y * _width + x;
            return _heat[index] / CapacityOf(_cells[index].typeId);
        }

        /// <summary>
        /// 按扁平下标读取温度（渲染层逐格映射用，省去反算 x / y）。
        /// 下标越界返回 <see cref="AmbientHeat"/>。
        /// </summary>
        public float GetTemperatureAt(int index)
        {
            if ((uint)index >= (uint)_heat.Length)
            {
                return AmbientHeat;
            }

            return _heat[index] / CapacityOf(_cells[index].typeId);
        }

        /// <summary>把 (x,y) 的热量无条件写成 <paramref name="heat"/>（Q，不是温度）。</summary>
        /// <returns>坐标在界内则 true；越界什么都不做并返回 false。</returns>
        public bool SetHeat(int x, int y, float heat)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            _heat[y * _width + x] = heat;
            return true;
        }

        /// <summary>给 (x,y) 的热量累加 <paramref name="delta"/>（Q；热源 / 热汇的常用写法）。</summary>
        /// <returns>坐标在界内则 true；越界什么都不做并返回 false。</returns>
        public bool AddHeat(int x, int y, float delta)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            _heat[y * _width + x] += delta;
            return true;
        }

        /// <summary>
        /// 一次全格四邻热传导 + 顶行向虚空散热。由 <see cref="ParticleWorld.Step"/> 每 tick 调用一次，
        /// 粒子类型不需要、也不应该自己调用（否则重复扩散）。
        /// <see cref="Conductivity"/> 与 <see cref="VoidHeatLoss"/> 都为 0 时是空操作。
        /// </summary>
        public void Step()
        {
            float conductivity = _conductivity;
            float voidLoss = _voidHeatLoss;
            if (conductivity <= 0f && voidLoss <= 0f)
            {
                return;
            }

            float[] heat = _heat;
            float[] next = _scratch;
            float[] inverseCapacity = _inverseCapacity;
            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int height = _height;

            for (int i = 0; i < cells.Length; i++)
            {
                inverseCapacity[i] = 1f / CapacityOf(cells[i].typeId, types);
            }

            int topRow = height - 1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                bool radiatesUp = y == topRow;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    float selfTemperature = heat[i] * inverseCapacity[i];
                    float delta = 0f;

                    if (x > 0)
                    {
                        delta += heat[i - 1] * inverseCapacity[i - 1] - selfTemperature;
                    }

                    if (x + 1 < width)
                    {
                        delta += heat[i + 1] * inverseCapacity[i + 1] - selfTemperature;
                    }

                    if (y > 0)
                    {
                        delta += heat[i - width] * inverseCapacity[i - width] - selfTemperature;
                    }

                    if (y + 1 < height)
                    {
                        delta += heat[i + width] * inverseCapacity[i + width] - selfTemperature;
                    }

                    float updated = heat[i] + conductivity * delta;

                    // 顶行向虚空散热：界外上方是恒温 AmbientHeat 的无限热库，热量只出不进。
                    // 与传导独立（不乘 conductivity），否则就失去「独立于导热率」的意义。
                    if (radiatesUp)
                    {
                        updated -= voidLoss * selfTemperature;
                    }

                    next[i] = updated;
                }
            }

            // 双缓冲交换：本 tick 的读全部来自旧场，写全部落到新场，因此与扫描顺序无关。
            _heat = next;
            _scratch = heat;
        }

        /// <summary><see cref="ParticleWorld"/> 在 <c>Set</c>/<c>Clear</c> 换上全新物质时把该格热量重置为环境值。</summary>
        internal void ResetHeatAt(int index)
        {
            _heat[index] = AmbientHeat;
        }

        /// <summary><see cref="ParticleWorld.Swap"/> 交换两格时同步搬运热量（热由物质携带）。</summary>
        internal void SwapHeat(int a, int b)
        {
            float swap = _heat[a];
            _heat[a] = _heat[b];
            _heat[b] = swap;
        }

        private float CapacityOf(ushort typeId)
        {
            return CapacityOf(typeId, _types);
        }

        private static float CapacityOf(ushort typeId, ParticleType[] types)
        {
            if (typeId == ParticleRegistry.AirTypeId || typeId >= types.Length)
            {
                return AirHeatCapacity;
            }

            ParticleType type = types[typeId];
            return type == null ? AirHeatCapacity : type.Info.HeatCapacity;
        }
    }
}
