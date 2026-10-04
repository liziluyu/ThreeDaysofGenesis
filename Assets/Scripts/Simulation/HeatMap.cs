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
    /// 每对相邻格的界面导热率是 <c>Conductivity · (σ_本 + σ_邻)/2</c>（σ 为各材料的
    /// <see cref="ParticleTypeInfo.ThermalConductivity"/>，两侧取算术平均），交换的热量为
    /// <c>ΔQ = 界面导热率 · (T_邻 - T_本)</c>，两格大小相等方向相反——所以**界内是守恒的**，热量只是在格与格之间搬家。
    /// 因为界面导热率 ≤ <c>Conductivity</c>，旧的「单一全局导热系数」行为就是 σ 全为 1 的特例。
    /// 扩散用的是本逻辑帧开始时的温度场（先算差、再整体写回），与扫描顺序无关、结果确定。</para>
    /// <para>散热：同一次 <see cref="Step"/> 里有两条并存的散热路——
    /// ① 最上面一行向**界外上方的虚空**流失热量（<c>ΔQ = -VoidHeatLoss * T</c>，虚空恒为 <see cref="AmbientHeat"/>）；
    /// ② <b>辐射</b>（<c>ΔQ = -RadiativeLoss * ε * T</c>，ε 为该格材料的<see cref="ParticleTypeInfo.ThermalEmissivity"/>；
    /// 默认关闭，生产由 Settings 资产给 0.05）——
    /// 但<b>只作用于暴露于天空的格</b>（顶行，或正上方是气体/空）；埋在地下的格四周都是物质、净辐射≈0，不参与辐射，
    /// 其热量只能靠传导往上散——热因此能真正传进地下（若埋着的格也照漏，热会在 1~2 格内被抽干，只停在地表）。
    /// 已知简化：暴露面只查正上方一格，洞穴内的空气因此也按暴露面处理；且 <c>T &lt; 0</c> 时辐射项
    /// <c>-λ·ε·T</c> 符号反转成加热（等价向 0 环境松弛）——无害，但「只散热」仅对 <c>T &gt; 0</c> 成立。</para>
    /// <para><b>ε 是「热在竖直方向能铺多远」的旋钮</b>：空气柱每一格都是暴露面、都在散热，整柱的漏热总量正比于空气的 ε。
    /// ε=1 时（旧行为）空气的热 20 格内就耗尽，地表之上像有一道断崖；大气取 ε=0.1 后
    /// （N₂/O₂ 对红外透明）气柱几乎不逐格漏热，温度从地表到网格顶是单调、平滑的衰减。
    /// 用 ε 而不是全局 <see cref="RadiativeLoss"/> 来调这件事，好处是只动空气——固/液/蒸汽/云都留 ε=1，
    /// 它们的表面温度与光照强度都不必跟着改。</para>
    /// <para>简化 / 不完美：无显式对流（上升 / 下沉的绝热降温由 <see cref="AdiabaticLapse"/> 近似）；辐射是线性近似（真实是 T⁴）；ε 只有固/液/气这一个维度；
    /// 虚空散热只发生在网格顶行；<c>Q</c> 不钳制（负值表示冷）。这些留给具体粒子类型自行实现。</para>
    /// <para>稳定性：显式扩散要求 <c>(Conductivity * 邻居数 + VoidHeatLoss + RadiativeLoss) / 热容 &lt; 2</c>。
    /// 各系数上限都是 <see cref="MaxConductivity"/> = 0.24，配合热容 ≳ 0.5 的取值即可稳定；
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

        /// <summary><see cref="RadiativeLoss"/> 的默认值。代码默认 0（关闭，保持旧行为与既有热测试契约）；
        /// 生产值由 <c>ParticleSimulationSettings</c> 资产注入（当前 0.05）。</summary>
        public const float DefaultRadiativeLoss = 0f;

        /// <summary>
        /// <see cref="AdiabaticLapse"/> 的默认值（°/格）：气体每竖直移动一格，温度的等效变化量
        /// （上升变冷、下沉变暖）。它是真实干绝热直减率（≈10 °C/km）在「格」尺度上的等效。
        /// </summary>
        public const float DefaultAdiabaticLapse = 0.1f;

        /// <summary><see cref="DebugFullScaleTemperature"/> 的默认值。</summary>
        public const float DefaultDebugFullScaleTemperature = 10f;

        /// <summary>每材料相对导热率 σ 的上限（见 <see cref="ParticleTypeInfo.ThermalConductivity"/>）。</summary>
        public const float MaxThermalConductivity = 1f;

        /// <summary>每材料相对辐射率 ε 的上限（见 <see cref="ParticleTypeInfo.ThermalEmissivity"/>）。</summary>
        public const float MaxThermalEmissivity = 1f;

        /// <summary>
        /// 每单位湿度（= 1 水格）给所在格增加的附加热容（悬浮水汽的热惯性）：承载湿度的空气因此比干空气
        /// 更难升 / 降温（<c>C = CapacityOf(typeId) + humidity × 本常量</c>）。
        /// 相变本身的放热 / 吸热由各相变路径在相变完成后对本格 <c>AddHeat(±潜热)</c> 结算
        /// （见 <see cref="HumidityField.LatentHeatPerUnit"/>）；本常量只负责让湿度改变格子的热容，
        /// 不改变界内热量总量（<c>Q</c> 仍然守恒）。
        /// </summary>
        public const float HumidityHeatCapacity = 1f;

        /// <summary>
        /// 空气相对导热率 σ 的默认值。空气没有类型条目（typeId 0），其导热率不在注册表里，
        /// 由本常量（及可写属性 <see cref="AirThermalConductivity"/>）给出。默认 1（与旧行为一致：空气不额外削弱传导）。
        /// </summary>
        public const float DefaultAirThermalConductivity = 1f;

        /// <summary>
        /// 空气相对辐射率 ε 的默认值。默认 1（不改既有单测契约：暴露的空气格仍按 <c>RadiativeLoss · T</c> 辐射）。
        /// 生产值由 <c>ParticleSimulationSettings</c> 资产注入（当前 0.1：N₂/O₂ 对红外透明，
        /// 气柱因此不再逐格漏热，地表的热能一路铺到网格顶）。</summary>
        public const float DefaultAirThermalEmissivity = 1f;

        private readonly int _width;
        private readonly int _height;
        private readonly Cell[] _cells;
        private readonly ParticleType[] _types;
        private readonly HumidityField _humidity;
        private readonly float[] _inverseCapacity;
        private readonly float[] _conductivityOf;
        private readonly float[] _emissivityOf;

        private float[] _heat;
        private float[] _scratch;
        private float _conductivity = DefaultConductivity;
        private float _voidHeatLoss = DefaultVoidHeatLoss;
        private float _radiativeLoss = DefaultRadiativeLoss;
        private float _adiabaticLapse = DefaultAdiabaticLapse;
        private float _airThermalConductivity = DefaultAirThermalConductivity;
        private float _airThermalEmissivity = DefaultAirThermalEmissivity;

        /// <param name="width">宽度（格），范围 <c>1..ParticleWorld.MaxSide</c>。</param>
        /// <param name="height">高度（格），范围 <c>1..ParticleWorld.MaxSide</c>。</param>
        /// <param name="cells">世界的格子数组（同宽高）。热图只读它来查每格物质的热容。</param>
        /// <param name="types">类型表（同 <see cref="ParticleWorld.Types"/>），用于查 <see cref="ParticleTypeInfo.HeatCapacity"/>。</param>
        /// <param name="humidity">同形的湿度场（用于把水汽的热容计入）；<c>null</c> 表示不含湿度场（既有单测与孤立用法）。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一边超出 <c>1..ParticleWorld.MaxSide</c>。</exception>
        public HeatMap(int width, int height, Cell[] cells, ParticleType[] types, HumidityField humidity = null)
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
            _humidity = humidity;
            _heat = new float[width * height];
            _scratch = new float[width * height];
            _inverseCapacity = new float[width * height];
            _conductivityOf = new float[width * height];
            _emissivityOf = new float[width * height];
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
        /// 每对相邻格每逻辑帧交换的热量占比；0 = 关闭传导。超出范围会被夹紧。
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
        /// 每一逻辑帧，网格最上面一行（<c>y = Height - 1</c>）都会向界外上方的虚空流失热量，
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
        /// 辐射散热系数（无量纲），范围 <c>[0, <see cref="MaxConductivity"/>]</c>；0 = 关闭。
        /// <b>只作用于暴露于天空的格</b>（顶行，或正上方是气体/空），且按该格材料的
        /// <see cref="ParticleTypeInfo.ThermalEmissivity"/> ε 缩放：<c>ΔQ = -RadiativeLoss × ε × T</c>。
        /// 埋在地下的格四周都是物质、净辐射≈0，不辐射——否则刚传进地下几格的热量会被逐格抽干，热就只停在地表。
        /// 它是**所有**暴露面的散热尺度；想只调「热在空气里能铺多高」而不动固/液，请改
        /// <see cref="AirThermalEmissivity"/>（生产 0.1）而不是这个全局值。
        /// </summary>
        public float RadiativeLoss
        {
            get { return _radiativeLoss; }
            set { _radiativeLoss = value < 0f ? 0f : (value > MaxConductivity ? MaxConductivity : value); }
        }

        /// <summary>
        /// 绝热直减率（°/格），非负。气体每竖直移动一格，其温度按竖直位移调整：
        /// <c>ΔT = -AdiabaticLapse × Δy</c>（Δy = 新 y − 旧 y；上升 Δy &gt; 0 → 变冷，下沉 Δy &lt; 0 → 变暖）。
        /// 结算成热量即 <c>ΔQ = -AdiabaticLapse × Δy × C</c>（C 为该格热容，空气用 <see cref="AirHeatCapacity"/>），
        /// 由 <see cref="ParticleWorld.Swap"/> 在交换后对带 <see cref="ParticleTags.Gas"/> 标签的格子执行。
        /// 这是真实干绝热直减率（≈10 °C/km）的等效，给大气补上一层随高度变化的竖直温度梯度。0 = 关闭。
        /// </summary>
        public float AdiabaticLapse
        {
            get { return _adiabaticLapse; }
            set { _adiabaticLapse = value < 0f ? 0f : value; }
        }

        /// <summary>
        /// 空气（typeId 0）的相对导热率 σ，范围 <c>[0, <see cref="MaxThermalConductivity"/>]</c>；超出夹紧。
        /// 空气没有类型条目，其导热率只能在这里给；它决定热从地表/水面漏进上方气柱的难易。
        /// </summary>
        public float AirThermalConductivity
        {
            get { return _airThermalConductivity; }
            set { _airThermalConductivity = value < 0f ? 0f : (value > MaxThermalConductivity ? MaxThermalConductivity : value); }
        }

        /// <summary>
        /// 空气（typeId 0）的相对辐射率 ε，范围 <c>[0, <see cref="MaxThermalEmissivity"/>]</c>；超出夹紧。默认 1。
        /// 空气没有类型条目，其辐射率只能在这里给；它决定热能不能穿过整根气柱散出去。
        /// 取小值（生产 0.1）后气柱几乎不逐格漏热，竖直方向的温度衰减变平缓、能一路铺到网格顶。
        /// </summary>
        public float AirThermalEmissivity
        {
            get { return _airThermalEmissivity; }
            set { _airThermalEmissivity = value < 0f ? 0f : (value > MaxThermalEmissivity ? MaxThermalEmissivity : value); }
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

            return GetCapacityAt(y * _width + x);
        }

        /// <summary>
        /// 读取扁平下标处的热容：**物质热容 + 湿度携带的附加热容（悬浮水汽）**
        /// （<c>CapacityOf(typeId) + humidity * HumidityHeatCapacity</c>）。
        /// 湿度让承载它的空气热惯性变大——水汽比干空气更难升 / 降温（相变的潜热另由
        /// <see cref="HumidityField.LatentHeatPerUnit"/> 在相变路径显式结算，见 <see cref="HumidityHeatCapacity"/>）。
        /// 下标越界返回 <see cref="AirHeatCapacity"/>。
        /// </summary>
        public float GetCapacityAt(int index)
        {
            if ((uint)index >= (uint)_heat.Length)
            {
                return AirHeatCapacity;
            }

            return CapacityOf(_cells[index].typeId) + HumidityOf(index) * HumidityHeatCapacity;
        }

        private float HumidityOf(int index)
        {
            return _humidity == null ? 0f : _humidity.GetAt(index);
        }

        /// <summary>读取 (x,y) 的温度 <c>T = Q / C</c>。越界返回 <see cref="AmbientHeat"/>。</summary>
        public float GetTemperature(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return AmbientHeat;
            }

            int index = y * _width + x;
            return _heat[index] / GetCapacityAt(index);
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

            return _heat[index] / GetCapacityAt(index);
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
        /// 一次全格四邻热传导 + 顶行向虚空散热 + 暴露面辐射散热（按材料 ε 缩放）。由 <see cref="ParticleWorld.StepLogic"/> 每逻辑帧调用一次，
        /// 粒子类型不需要、也不应该自己调用（否则重复扩散）。
        /// <see cref="Conductivity"/>、<see cref="VoidHeatLoss"/> 与 <see cref="RadiativeLoss"/> 都为 0 时是空操作。
        /// </summary>
        public void Step()
        {
            float conductivity = _conductivity;
            float voidLoss = _voidHeatLoss;
            float radiativeLoss = _radiativeLoss;
            if (conductivity <= 0f && voidLoss <= 0f && radiativeLoss <= 0f)
            {
                return;
            }

            float[] heat = _heat;
            float[] next = _scratch;
            float[] inverseCapacity = _inverseCapacity;
            float[] conductivityOf = _conductivityOf;
            float[] emissivityOf = _emissivityOf;
            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int height = _height;

            for (int i = 0; i < cells.Length; i++)
            {
                ushort id = cells[i].typeId;
                inverseCapacity[i] = 1f / (CapacityOf(id, types) + HumidityOf(i) * HumidityHeatCapacity);
                conductivityOf[i] = ConductivityOf(id, types);
                emissivityOf[i] = EmissivityOf(id, types);
            }

            float halfScale = conductivity * 0.5f;
            int topRow = height - 1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                bool topRowExposed = y == topRow;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    float selfTemperature = heat[i] * inverseCapacity[i];
                    float selfConductivity = conductivityOf[i];
                    float delta = 0f;

                    // 每对邻居的界面导热率 = Conductivity · (σ_本 + σ_邻)/2（算术平均，对称、界内守恒）。
                    // σ 全为 1 时退化成旧的 Conductivity·(T_邻 − T_本)，与改动前逐格一致。
                    if (x > 0)
                    {
                        int j = i - 1;
                        delta += halfScale * (selfConductivity + conductivityOf[j])
                            * (heat[j] * inverseCapacity[j] - selfTemperature);
                    }

                    if (x + 1 < width)
                    {
                        int j = i + 1;
                        delta += halfScale * (selfConductivity + conductivityOf[j])
                            * (heat[j] * inverseCapacity[j] - selfTemperature);
                    }

                    if (y > 0)
                    {
                        int j = i - width;
                        delta += halfScale * (selfConductivity + conductivityOf[j])
                            * (heat[j] * inverseCapacity[j] - selfTemperature);
                    }

                    if (y + 1 < height)
                    {
                        int j = i + width;
                        delta += halfScale * (selfConductivity + conductivityOf[j])
                            * (heat[j] * inverseCapacity[j] - selfTemperature);
                    }

                    // 辐射：只有**暴露于天空**的格才向 0K 虚空漏热，且按本格材料的辐射率 ε 缩放
                    // （ΔQ = -RadiativeLoss · ε · T）。埋在地下的格四周都是物质、净辐射≈0，若也照漏就会
                    // 把刚传进地下几格的热量抽干——这正是「热只停在地表」的根因。
                    // 暴露 = 顶行，或正上方是气体/空。已知简化：只查正上方一格，洞穴内空气也按暴露面处理。
                    // T < 0 时 -λ·ε·T 变成加热（符号反转）——等价于向 0 环境松弛，无害，但「只散热」仅对 T > 0 成立。
                    // ε 小的材料（大气：N₂/O₂ 对红外透明）不逐格漏热，整根气柱因此不再把热抽干，
                    // 地表的热能一路铺到网格顶（这是 λ 之外、真正与「地表温度」解耦的那个旋钮）。
                    bool exposed = topRowExposed || IsSkyExposed(cells, types, i + width);
                    float updated = heat[i] + delta - (exposed ? radiativeLoss * emissivityOf[i] * selfTemperature : 0f);

                    // 顶行向虚空散热：界外上方是恒温 AmbientHeat 的无限热库，热量只出不进。
                    // 与传导独立（不乘 conductivity），否则就失去「独立于导热率」的意义。
                    if (topRowExposed)
                    {
                        updated -= voidLoss * selfTemperature;
                    }

                    next[i] = updated;
                }
            }

            // 双缓冲交换：本逻辑帧的读全部来自旧场，写全部落到新场，因此与扫描顺序无关。
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

        /// <summary>读取 (x,y) 上物质的相对导热率 σ。越界 / 空气返回 <see cref="AirThermalConductivity"/>。</summary>
        public float GetConductivity(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return _airThermalConductivity;
            }

            return ConductivityOf(_cells[y * _width + x].typeId, _types);
        }

        private float ConductivityOf(ushort typeId, ParticleType[] types)
        {
            if (typeId == ParticleRegistry.AirTypeId || typeId >= types.Length)
            {
                return _airThermalConductivity;
            }

            ParticleType type = types[typeId];
            return type == null ? _airThermalConductivity : type.Info.ThermalConductivity;
        }

        /// <summary>读取 (x,y) 上材料的相对辐射率 ε。越界 / 空气返回 <see cref="AirThermalEmissivity"/>。</summary>
        public float GetEmissivity(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return _airThermalEmissivity;
            }

            return EmissivityOf(_cells[y * _width + x].typeId, _types);
        }

        private float EmissivityOf(ushort typeId, ParticleType[] types)
        {
            if (typeId == ParticleRegistry.AirTypeId || typeId >= types.Length)
            {
                return _airThermalEmissivity;
            }

            ParticleType type = types[typeId];
            return type == null ? _airThermalEmissivity : type.Info.ThermalEmissivity;
        }

        /// <summary>
        /// 正上方那格（<paramref name="aboveIndex"/>）是否是气体/空——即本格是否「暴露于天空」。
        /// 辐射只作用于暴露面：埋着的格四周都是同温物质、净辐射≈0，不该向虚空漏热。
        /// 越界 / 未注册 / 空槽一律当作空气（暴露）。
        /// 已知简化：只看正上方一格——洞穴内的空气也算暴露、会被辐射抽热（接受现状，不重构）。
        /// </summary>
        private static bool IsSkyExposed(Cell[] cells, ParticleType[] types, int aboveIndex)
        {
            ushort id = cells[aboveIndex].typeId;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return true;
            }

            ParticleType above = types[id];
            return above == null || (above.Info.Tags & ParticleTags.Gas) != 0;
        }
    }
}
