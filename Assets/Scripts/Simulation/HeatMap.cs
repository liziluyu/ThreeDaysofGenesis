using System;
#if UNITY_2022_3_OR_NEWER
using Unity.Burst;
#endif

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 与 <see cref="ParticleWorld"/> 网格同形的一张热图：每格存**热量 Q**（<see cref="float"/>），
    /// 温度由热量与该格物质的热容换算：<c>T = Q / C</c>。
    /// <b>热量由物质携带</b>：粒子移动（<see cref="ParticleWorld.Swap"/>）时 Q 随粒子搬家；
    /// 生成 / 抹除（<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.Clear"/>）换上的新物质
    /// **继承该格原有热量**——旧物质离场，它的热视作就地留给了新物质；同一份物质换形态（<c>SetPhase</c>）则 Q 原样保留。
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
#if UNITY_2022_3_OR_NEWER
    // 类级 [BurstCompile] 是 Burst ILPP 的发现门（它只进入带该特性的类型）；真正被编译的仍只有
    // 带方法级 [BurstCompile] 的 HeatStepKernel。net9 独立编译（LogicBench）下属性被剥掉。
    [BurstCompile]
#endif
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
        /// （见 <see cref="HumidityField.LatentHeat"/>）；本常量只负责让湿度改变格子的热容，
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

        // ── 按 typeId 索引的场 pass 参数表（世界构造时预生成一次）──
        // 热传导是 O(W·H) 的逐格循环，逐格回调 ParticleType 对象（`types[id].Info.HeatCapacity`）
        // 既慢又不可 Burst（托管对象）。这里把类型表的三个热学参数摊平成扁平 float[]，
        // 内核只按 cells[i].typeId 查表；空气（typeId 0）与空槽项在每次 Step 前用可变属性刷新。
        private readonly float[] _capacityByType;
        private readonly float[] _conductivityByType;
        private readonly float[] _emissivityByType;

        /// <summary>按 typeId 索引的「正上方是它算暴露于天空」表（1 = 空气 / 气体 / 空槽）。</summary>
        private readonly byte[] _skyExposedByType;

        /// <summary>按 typeId 索引的「该类型是气体 / 空（湿度的合法载体）」表（1 = 是）。湿度场 pass 用。</summary>
        private readonly byte[] _moistAirByType;

        private float[] _heat;
        private float[] _scratch;

        /// <summary>
        /// 本逻辑阶段内 Q 发生过变化（精确 <c>Δ ≠ 0</c>，无阈值）的格标记，下标与网格一致。
        /// <c>1</c> = 脏，<c>0</c> = 干净（<c>byte[]</c> 而非 <c>bool[]</c>：Burst 内核要求扁平可 blittable 的缓冲）。
        /// 运动休眠的**热唤醒源**：运动决策读温度的类型（水对流、大气浮力 / 热压流）只有在温度变了
        /// 才可能改主意，而热只在逻辑阶段变。世界在 <see cref="ParticleWorld.StepLogic"/> 末尾消费本记录、
        /// 把这些格按半径 2 刷子并入候选位图，随后 <see cref="ClearDirty"/>。
        /// </summary>
        private byte[] _dirty;
        private bool _anyDirty;
        private float _conductivity = DefaultConductivity;
        private float _voidHeatLoss = DefaultVoidHeatLoss;
        private float _radiativeLoss = DefaultRadiativeLoss;
        private float _adiabaticLapse = DefaultAdiabaticLapse;
        private float _airThermalConductivity = DefaultAirThermalConductivity;
        private float _airThermalEmissivity = DefaultAirThermalEmissivity;

        // ── 不动点跳过（Step 入口）──
        // 热传导是 Q 的确定性函数 F(Q, humidity, cells, 参数)。若上一趟 Step 没改动任何 Q（恒等），
        // 且此后所有输入都没变（外部未改 Q、世界写入版本 / 湿度版本 / 参数版本全等），那么本趟输出必然
        // 与已存的场逐位相同——重跑一遍只会得到同样的结果，于是整趟跳过（不交换双缓冲、不动 dirty）。
        // 关键：必须用「上一趟是恒等」这一条兜底。热 pass 自己的改动会被世界的 FlushTemperatureDirty
        // 在帧末消费并清空 _anyDirty，所以「_anyDirty 为假」只说明**本帧内**没有变化，
        // 不能说明场已收敛；若上一趟仍在松弛（非恒等），必须继续跑到达不动点为止。
        private bool _lastStepWasIdentity;
        private int _lastQVersion = int.MinValue;
        private int _lastCellsVersion = int.MinValue;
        private int _lastHumidityVersion = int.MinValue;
        private int _lastParamVersion = int.MinValue;

        /// <summary>
        /// 外部热写入版本：<see cref="SetHeat"/> / <see cref="AddHeat"/> / <see cref="SwapHeat"/> 每次真正改动 Q 时自增。
        /// <b>不能</b>用 <see cref="_anyDirty"/> 代替它——<c>_anyDirty</c> 由世界在每逻辑帧末尾消费并清空
        /// （<see cref="ClearDirty"/>），因此它只说明「本帧内有过变化」，无法回答「自上次热 pass 以来 Q 变没变」。
        /// 光照 / 降水 / 潜热都在热 pass **之后**才注热，若只看 <c>_anyDirty</c> 会在下一帧误判为「无变化」而跳过，
        /// 把刚注入的热漏掉、并任其逐帧累积。本版本号只增不减，直到热 pass 明确缓存过它。
        /// </summary>
        private int _qVersion;

        /// <summary>
        /// 影响热 pass 输出的**可变参数**版本：<see cref="Conductivity"/> / <see cref="VoidHeatLoss"/> /
        /// <see cref="RadiativeLoss"/> / <see cref="AirThermalConductivity"/> / <see cref="AirThermalEmissivity"/>
        /// 任一被改成不同值时自增（值没变的赋值不递增）。不动点跳过靠它判断参数有无变化。
        /// <see cref="AdiabaticLapse"/> 不在此列：它只在 <see cref="ParticleWorld.Swap"/> 里结算，不走热 pass。
        /// </summary>
        private int _paramVersion;

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
            _dirty = new byte[width * height];
            _inverseCapacity = new float[width * height];
            _conductivityOf = new float[width * height];
            _emissivityOf = new float[width * height];

            // 场 pass 参数表：按 typeId 摊平类型表的三个热学参数 + 两张分类表，
            // 热路径不再逐格回调 ParticleType（也就同时满足 Burst 只认 blittable 数据的要求）。
            int typeCount = types.Length;
            _capacityByType = new float[typeCount];
            _conductivityByType = new float[typeCount];
            _emissivityByType = new float[typeCount];
            _skyExposedByType = new byte[typeCount];
            _moistAirByType = new byte[typeCount];
            for (int id = 0; id < typeCount; id++)
            {
                ParticleType type = types[id];
                if (type == null)
                {
                    // typeId 0（空气）与空槽：热学参数走空气常量 / 空气可变属性；
                    // 暴露表按 IsSkyExposed 的语义（空气 / 空槽都算暴露），载体表按 IsGasOrEmpty 的语义（只有空气算）。
                    _capacityByType[id] = AirHeatCapacity;
                    _skyExposedByType[id] = 1;
                    _moistAirByType[id] = id == 0 ? (byte)1 : (byte)0;
                    continue;
                }

                ParticleTypeInfo info = type.Info;
                _capacityByType[id] = info.HeatCapacity;
                _conductivityByType[id] = info.ThermalConductivity;
                _emissivityByType[id] = info.ThermalEmissivity;
                bool gas = (info.Tags & ParticleTags.Gas) != 0;
                _skyExposedByType[id] = gas ? (byte)1 : (byte)0;
                _moistAirByType[id] = gas ? (byte)1 : (byte)0;
            }

            RefreshAirTypeTable();
        }

        /// <summary>把空气（typeId 0）与空槽项的可变热学参数刷新成当前值（σ / ε 可由驱动每帧改写）。O(typeCount)。</summary>
        private void RefreshAirTypeTable()
        {
            for (int id = 0; id < _types.Length; id++)
            {
                if (id == 0 || _types[id] == null)
                {
                    _capacityByType[id] = AirHeatCapacity;
                    _conductivityByType[id] = _airThermalConductivity;
                    _emissivityByType[id] = _airThermalEmissivity;
                }
            }
        }

        /// <summary>按 typeId 索引的物质热容表（不含湿度附加项）。供湿度场 pass 换算温度时复用。</summary>
        internal float[] CapacityByType
        {
            get { return _capacityByType; }
        }

        /// <summary>按 typeId 索引的「气体 / 空」表（1 = 湿度的合法载体）。供湿度场 pass 复用。</summary>
        internal byte[] MoistAirByType
        {
            get { return _moistAirByType; }
        }

        /// <summary>类型表长度（下标上限）。</summary>
        internal int TypeCount
        {
            get { return _types.Length; }
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
        /// 脏格标记缓冲（下标 = <c>y * Width + x</c>，<c>1</c> = 自上次 <see cref="ClearDirty"/> 以来 Q 变化过，<c>0</c> = 干净）。
        /// <b>只读契约</b>：外部只允许读——置位由 <see cref="SetHeat"/> / <see cref="AddHeat"/> / <see cref="SwapHeat"/> /
        /// <see cref="Step"/> 内部完成，消费后由世界调用 <see cref="ClearDirty"/>。用于运动休眠的热唤醒。
        /// 类型是 <c>byte[]</c> 而非 <c>bool[]</c>：Burst 内核直接以 <c>byte*</c> 置位（见 <see cref="Step"/>）。
        /// </summary>
        public byte[] Dirty
        {
            get { return _dirty; }
        }

        /// <summary>是否任一格有未消费的热变化（<c>false</c> = 世界可跳过整趟脏格扫描）。</summary>
        public bool AnyDirty
        {
            get { return _anyDirty; }
        }

        /// <summary>
        /// 最近一次 <see cref="Step"/> 是否整趟跳过（场已到不动点且所有输入未变，直接返回）。只读，供测试 / 诊断。
        /// </summary>
        public bool LastStepSkipped { get; private set; }

        /// <summary>
        /// 不动点跳过开关（默认 <c>true</c>）。关掉后 <see cref="Step"/> 每帧都重跑，不做整趟跳过——
        /// 用于与跳过路径做**逐位对照**（跳过必须对结果完全透明）。与
        /// <see cref="ParticleWorld.MotionSleepEnabled"/> 同为 A/B 回归开关，运行中随时可改。
        /// </summary>
        public bool FixpointSkipEnabled { get; set; } = true;

        /// <summary>
        /// 世界格子的写入版本（<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> /
        /// <see cref="ParticleWorld.Swap"/> 各递增一次）。由 <see cref="ParticleWorld"/> 在每逻辑帧调
        /// <see cref="Step"/> 之前写入；热 pass 只用它判断「格子的类型/布局自上次以来有没有变」——
        /// 类型一变，逐格热容 / σ / ε 与天空暴露判据都可能变，热 pass 必须重跑。
        /// </summary>
        internal int CellsVersion { get; set; }

        /// <summary>湿度场的内容版本（湿度一变温度就变，热 pass 必须重跑）。无湿度场时恒为 0。</summary>
        private int HumidityVersion
        {
            get { return _humidity == null ? 0 : _humidity.Version; }
        }

        /// <summary>清空脏格标记。由世界在把脏格并入候选位图之后调用；无脏格时是空操作。</summary>
        public void ClearDirty()
        {
            if (!_anyDirty)
            {
                return;
            }

            Array.Clear(_dirty, 0, _dirty.Length);
            _anyDirty = false;
        }

        /// <summary>把扁平下标标成脏格（Q 已精确变化）。热路径：一次字节读 + 两次写，无分配。</summary>
        private void MarkDirty(int index)
        {
            if (_dirty[index] == 0)
            {
                _dirty[index] = 1;
                _anyDirty = true;
            }

            _qVersion++;
        }

        /// <summary>
        /// 热传导系数（无量纲），范围 <c>[0, <see cref="MaxConductivity"/>]</c>。
        /// 每对相邻格每逻辑帧交换的热量占比；0 = 关闭传导。超出范围会被夹紧。
        /// </summary>
        public float Conductivity
        {
            get { return _conductivity; }
            set { SetParam(ref _conductivity, value < 0f ? 0f : (value > MaxConductivity ? MaxConductivity : value)); }
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
            set { SetParam(ref _voidHeatLoss, value < 0f ? 0f : (value > MaxConductivity ? MaxConductivity : value)); }
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
            set { SetParam(ref _radiativeLoss, value < 0f ? 0f : (value > MaxConductivity ? MaxConductivity : value)); }
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
            set { SetParam(ref _airThermalConductivity, value < 0f ? 0f : (value > MaxThermalConductivity ? MaxThermalConductivity : value)); }
        }

        /// <summary>
        /// 空气（typeId 0）的相对辐射率 ε，范围 <c>[0, <see cref="MaxThermalEmissivity"/>]</c>；超出夹紧。默认 1。
        /// 空气没有类型条目，其辐射率只能在这里给；它决定热能不能穿过整根气柱散出去。
        /// 取小值（生产 0.1）后气柱几乎不逐格漏热，竖直方向的温度衰减变平缓、能一路铺到网格顶。
        /// </summary>
        public float AirThermalEmissivity
        {
            get { return _airThermalEmissivity; }
            set { SetParam(ref _airThermalEmissivity, value < 0f ? 0f : (value > MaxThermalEmissivity ? MaxThermalEmissivity : value)); }
        }

        /// <summary>
        /// 可变热学参数的统一写入口：值真变了才赋值并递增 <see cref="_paramVersion"/>（不变则什么都不做）。
        /// 热 pass 的不动点跳过据此判断参数有无变化。
        /// </summary>
        private void SetParam(ref float field, float value)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _paramVersion++;
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
        /// <see cref="HumidityField.LatentHeat"/> 在相变路径显式结算，见 <see cref="HumidityHeatCapacity"/>）。
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

            int index = y * _width + x;
            if (_heat[index] != heat)
            {
                _heat[index] = heat;
                MarkDirty(index);
            }

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

            int index = y * _width + x;
            float updated = _heat[index] + delta;
            if (updated != _heat[index])
            {
                _heat[index] = updated;
                MarkDirty(index);
            }

            return true;
        }

        /// <summary>
        /// 一次全格四邻热传导 + 顶行向虚空散热 + 暴露面辐射散热（按材料 ε 缩放）。由 <see cref="ParticleWorld.StepLogic"/> 每逻辑帧调用一次，
        /// 粒子类型不需要、也不应该自己调用（否则重复扩散）。
        /// <see cref="Conductivity"/>、<see cref="VoidHeatLoss"/> 与 <see cref="RadiativeLoss"/> 都为 0 时是空操作。
        /// </summary>
        /// 核心循环在 <see cref="HeatStepKernel"/>：Unity 下由 Burst ILPP 把对该方法的直接调用重写为
        /// DirectCall 分发（编辑器 JIT / 构建 AOT；<c>FloatMode.Strict</c>——禁用 FMA 收缩与浮点重排，
        /// 与 managed 路径逐位一致，满足确定性契约）；独立编译（obj/LogicBench 的 net9）时属性被 #if 剥掉，
        /// 同一个方法体直接以 managed 执行。两条路径共用一份源码，不存在第二份实现。
        /// 内核的顺序循环体已抽成两个 per-cell 静态方法（<see cref="HeatStepParamsCell"/> / <see cref="HeatStepConductCell"/>）。
        /// <b>不做并行</b>：阶段 D 实测热 pass 全量仅 ≈0.2ms/帧（逻辑帧的 1.5~3%），
        /// 改 `IJobParallelFor` 后 workers=31 与 workers=0 无可分辨差异（313 批调度开销吃掉全部收益），故刻意保持串行直调。
        /// 逐格参数（热容倒数 / σ / ε）由内核就地按 <see cref="_capacityByType"/> 等按 typeId 索引的表算出，
        /// 不再逐格回调 <see cref="ParticleType"/>。
        public unsafe void Step()
        {
            float conductivity = _conductivity;
            float voidLoss = _voidHeatLoss;
            float radiativeLoss = _radiativeLoss;

            // 不动点跳过：上一趟是恒等，且自那以来外部未改过 Q、格子写入版本 / 湿度版本 / 参数版本全等
            // → 本趟输出必然与已存的场逐位相同，直接返回（不交换双缓冲、不动 dirty）。
            // 数学依据：Step 是 (Q, 湿度, cells, 参数) 的确定性函数；上一趟输入与输出相同（恒等），
            // 而后所有输入都未变，故再跑一遍得到同一个输出——重算与跳过逐位等价。
            if (FixpointSkipEnabled
                && _lastStepWasIdentity
                && _lastQVersion == _qVersion
                && _lastCellsVersion == CellsVersion
                && _lastHumidityVersion == HumidityVersion
                && _lastParamVersion == _paramVersion)
            {
                LastStepSkipped = true;
                return;
            }

            LastStepSkipped = false;

            if (conductivity <= 0f && voidLoss <= 0f && radiativeLoss <= 0f)
            {
                // 全关时 Step 是恒等映射：记录指纹（含恒等标记），让后续帧能进入跳过。
                _lastStepWasIdentity = true;
                CacheFingerprint();
                return;
            }

            // 空气（typeId 0）与空槽项的 σ / ε 是可变的（驱动每帧写回），Step 前刷一次表。
            RefreshAirTypeTable();

            float[] heatArr = _heat;
            float[] nextArr = _scratch;
            float[] humidityArr = _humidity == null ? null : _humidity.RawBuffer;
            int hasHumidity = _humidity == null ? 0 : 1;
            int anyDirty;

            fixed (float* heat = heatArr)
            fixed (float* next = nextArr)
            fixed (float* inverseCapacity = _inverseCapacity)
            fixed (float* conductivityOf = _conductivityOf)
            fixed (float* emissivityOf = _emissivityOf)
            fixed (byte* dirty = _dirty)
            fixed (Cell* cells = _cells)
            fixed (float* humidity = humidityArr)
            fixed (float* capacityByType = _capacityByType)
            fixed (float* conductivityByType = _conductivityByType)
            fixed (float* emissivityByType = _emissivityByType)
            fixed (byte* skyExposedByType = _skyExposedByType)
            {
                anyDirty = HeatStepKernel(
                    heat, next, inverseCapacity, conductivityOf, emissivityOf, dirty, _anyDirty ? 1 : 0,
                    cells, humidity, hasHumidity,
                    capacityByType, conductivityByType, emissivityByType, skyExposedByType,
                    _types.Length, _width, _height,
                    conductivity, voidLoss, radiativeLoss,
                    AirHeatCapacity, _airThermalConductivity, _airThermalEmissivity, HumidityHeatCapacity);
            }

            // 双缓冲交换：本逻辑帧的读全部来自旧场，写全部落到新场，因此与扫描顺序无关。
            _heat = nextArr;
            _scratch = heatArr;
            _anyDirty = anyDirty != 0;

            // 记录本趟是否为恒等（无任何格变化 → 场已收敛，下一帧若输入未变即可整趟跳过）与输入指纹。
            _lastStepWasIdentity = anyDirty == 0;
            CacheFingerprint();
        }

        /// <summary>记录本次 <see cref="Step"/> 消费掉的输入指纹，供下一帧的不动点跳过比对。</summary>
        private void CacheFingerprint()
        {
            _lastQVersion = _qVersion;
            _lastCellsVersion = CellsVersion;
            _lastHumidityVersion = HumidityVersion;
            _lastParamVersion = _paramVersion;
        }

        /// <summary>
        /// 热传导 / 散热内核（Burst 入口）。顺序调 <see cref="HeatStepParamsCell"/> 与 <see cref="HeatStepConductCell"/>，
        /// 只含指针与标量，逐格参数按 typeId 索引的扁平表取得。
        /// 返回「本帧后是否有任何脏格」（入参 OR 本次置位），与 <see cref="Dirty"/> 的记账语义一致。
        /// </summary>
        /// <remarks>
        /// 算术逐句照搬原 managed 实现（含双缓冲、四邻顺序、暴露面判据），因此 <c>FloatMode.Strict</c> 下
        /// 与旧路径逐位一致；<see cref="ParticleWorld"/> 的 EditMode 热学断言即是对照证据。
        /// </remarks>
#if UNITY_2022_3_OR_NEWER
        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
#endif
        private static unsafe int HeatStepKernel(
            float* heat, float* next,
            float* inverseCapacity, float* conductivityOf, float* emissivityOf,
            byte* dirty, int anyDirtyIn,
            Cell* cells, float* humidity, int hasHumidity,
            float* capacityByType, float* conductivityByType, float* emissivityByType, byte* skyExposedByType,
            int typeCount, int width, int height,
            float conductivity, float voidLoss, float radiativeLoss,
            float airCapacity, float airConductivity, float airEmissivity, float humidityHeatCapacity)
        {
            int cellCount = width * height;

            // ① 逐格参数：热容倒数 / σ / ε。越界 id 与空槽走空气标量（表里下标 0 / 空槽已刷成空气值）。
            for (int i = 0; i < cellCount; i++)
            {
                HeatStepParamsCell(
                    i, cells, humidity, hasHumidity,
                    capacityByType, conductivityByType, emissivityByType, typeCount,
                    airCapacity, airConductivity, airEmissivity, humidityHeatCapacity,
                    inverseCapacity, conductivityOf, emissivityOf);
            }

            int dirtyAny = anyDirtyIn;
            float halfScale = conductivity * 0.5f;
            int topRow = height - 1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                bool topRowExposed = y == topRow;
                for (int x = 0; x < width; x++)
                {
                    if (HeatStepConductCell(
                        row + x, x, y, width, height, topRowExposed, halfScale,
                        heat, next, dirty, inverseCapacity, conductivityOf, emissivityOf,
                        cells, typeCount, skyExposedByType, radiativeLoss, voidLoss))
                    {
                        dirtyAny = 1;
                    }
                }
            }

            return dirtyAny;
        }

        /// <summary>
        /// 阶段 ① 的单格体：按 typeId 查扁平参数表，算出下标 <paramref name="i"/> 那一格的 <c>1/C</c> / σ / ε。
        /// 只读 <paramref name="cells"/> / <paramref name="humidity"/> 与类型表，只写本格自己的项——格间零依赖，
        /// 由 <see cref="HeatStepKernel"/> 顺序调用；抽出单格体是为了让 net9 与 Unity 共用同一份实现。
        /// </summary>
        private static unsafe void HeatStepParamsCell(
            int i, Cell* cells, float* humidity, int hasHumidity,
            float* capacityByType, float* conductivityByType, float* emissivityByType, int typeCount,
            float airCapacity, float airConductivity, float airEmissivity, float humidityHeatCapacity,
            float* inverseCapacity, float* conductivityOf, float* emissivityOf)
        {
            ushort id = cells[i].typeId;
            bool air = id >= typeCount;
            float capacity = air ? airCapacity : capacityByType[id];
            if (hasHumidity != 0)
            {
                capacity += humidity[i] * humidityHeatCapacity;
            }

            inverseCapacity[i] = 1f / capacity;
            conductivityOf[i] = air ? airConductivity : conductivityByType[id];
            emissivityOf[i] = air ? airEmissivity : emissivityByType[id];
        }

        /// <summary>
        /// 阶段 ② 的单格体：四邻传导 + 暴露面辐射 + 顶行虚空散热，结果写进 <paramref name="next"/> 的同一格，
        /// 并返回该格 Q 是否变化（调用方据此置「有脏格」）。
        /// <b>纯 gather</b>——只读旧场（<paramref name="heat"/> 与阶段 ① 写好的逐格参数表）、只写自己那一格，
        /// 不存在跨格写，因此任意线程 / batch 切分结果逐位相同。
        /// </summary>
        private static unsafe bool HeatStepConductCell(
            int i, int x, int y, int width, int height, bool topRowExposed, float halfScale,
            float* heat, float* next, byte* dirty,
            float* inverseCapacity, float* conductivityOf, float* emissivityOf,
            Cell* cells, int typeCount, byte* skyExposedByType,
            float radiativeLoss, float voidLoss)
        {
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
            // 暴露 = 顶行，或正上方是气体/空（越界 / 空槽也算暴露，与 IsSkyExposed 同语义）。
            // T < 0 时 -λ·ε·T 变成加热（符号反转）——等价于向 0 环境松弛，无害，但「只散热」仅对 T > 0 成立。
            bool exposed = topRowExposed;
            if (!exposed)
            {
                ushort aboveId = cells[i + width].typeId;
                exposed = aboveId >= typeCount || skyExposedByType[aboveId] != 0;
            }

            float updated = heat[i] + delta - (exposed ? radiativeLoss * emissivityOf[i] * selfTemperature : 0f);

            // 顶行向虚空散热：界外上方是恒温 AmbientHeat 的无限热库，热量只出不进。
            // 与传导独立（不乘 conductivity），否则就失去「独立于导热率」的意义。
            if (topRowExposed)
            {
                updated -= voidLoss * selfTemperature;
            }

            next[i] = updated;

            // 精确比较 Δ ≠ 0（无阈值）：本格 Q 变了就记脏，供世界的运动休眠热唤醒使用。
            // 均匀温场里 delta = 0，比较恒 false，因此不产生任何脏格、不拖慢稳态。
            if (updated != heat[i])
            {
                dirty[i] = 1;
                return true;
            }

            return false;
        }



        /// <summary><see cref="ParticleWorld.Swap"/> 交换两格时同步搬运热量（热由物质携带）。</summary>
        internal void SwapHeat(int a, int b)
        {
            float swap = _heat[a];
            _heat[a] = _heat[b];
            _heat[b] = swap;

            // 两格 Q 都变了才记脏（相等时交换是空操作）。Swap 的候选位已由世界按写路径置位，
            // 这里记脏只是让热唤醒的记录保持一致（不依赖调用顺序）。
            if (swap != _heat[a])
            {
                MarkDirty(a);
                MarkDirty(b);
            }
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
    }
}
