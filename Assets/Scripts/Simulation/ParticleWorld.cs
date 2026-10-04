using System;
using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 粒子世界的唯一持有者：一个连续的 <see cref="Cell"/>[] + (x,y) 直接算下标寻址 + 三个原子写操作。
    /// 纯 C# 类：不继承 MonoBehaviour、不持有任何 Unity 对象、**不认识渲染**。
    /// </summary>
    /// <remarks>
    /// <para>坐标：x ∈ [0, Width)，y ∈ [0, Height)，原点在左下角，y 向上增大，重力方向为 -y。</para>
    /// <para>下标：<c>index = y * Width + x</c>。</para>
    /// <para>扫描：行从 y = Height-1 到 0；同一行内 x 方向逐 tick 交替。</para>
    /// <para>越界读返回空气格；越界写是定义好的空操作（返回 false，不抛异常）。</para>
    /// <para>世界不做合法性判断：是否允许替换某个邻居由类型自己的逻辑决定。</para>
    /// </remarks>
    public sealed class ParticleWorld
    {
        /// <summary>单边最大格数。</summary>
        public const int MaxSide = 4096;

        private const uint DefaultSeed = 0x9E3779B9u;

        /// <summary>四邻偏移（<see cref="EvacuateHumidity"/> 用；提成静态数组避免每次降雨都分配）。</summary>
        private static readonly int[] NeighbourX = { -1, 1, 0, 0 };
        private static readonly int[] NeighbourY = { 0, 0, -1, 1 };

        private readonly int _width;
        private readonly int _height;
        private readonly Cell[] _cells;
        private readonly HeatMap _heat;
        private readonly HumidityField _humidity;
        private readonly LightController _light;
        private readonly ParticleType[] _types;

        private int _tick;
        private uint _rng;
        private int _randomTicksPerStep;
        private byte _stamp;
        private Cell _outside;
        private bool _unknownTypeReported;

        /// <summary>使用全局注册表 <see cref="ParticleRegistry.Types"/> 创建世界。</summary>
        public ParticleWorld(int width, int height, uint seed)
            : this(width, height, seed, ParticleRegistry.Types)
        {
        }

        /// <summary>
        /// 使用指定的类型表创建世界。传入自定义表可以让单元测试完全不碰全局注册表，
        /// 从而在隔离环境里单独验证一个粒子类型。
        /// </summary>
        /// <param name="types">下标 0 必须是 <c>null</c>（空气），其余为 <see cref="ParticleType"/>。</param>
        public ParticleWorld(int width, int height, uint seed, ParticleType[] types)
        {
            if (width < 1 || width > MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, $"width 必须在 1..{MaxSide} 之间。");
            }

            if (height < 1 || height > MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, $"height 必须在 1..{MaxSide} 之间。");
            }

            if (types == null || types.Length == 0 || types[0] != null)
            {
                throw new ArgumentException("类型表长度至少为 1，且下标 0（空气）必须为 null。", nameof(types));
            }

            // 自定义表同样要过注册表那套校验：否则空调色板会让 Set 抛 DivideByZero、或让渲染层越界。
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType entry = types[i];
                if (entry == null)
                {
                    continue;
                }

                try
                {
                    ParticleRegistry.ValidateTypeInfo(entry.Info, entry.Info.DebugName);
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArgumentException($"types[{i}] 非法：{exception.Message}", nameof(types), exception);
                }
            }

            _width = width;
            _height = height;
            _types = types;
            _cells = new Cell[width * height];
            _humidity = new HumidityField(width, height);
            _heat = new HeatMap(width, height, _cells, types, _humidity);
            _light = new LightController();
            _tick = 0;
            _rng = seed == 0u ? DefaultSeed : seed;
            _stamp = OutsideStamp;
        }

        /// <summary>类型表。<c>Types[0]</c> 为 <c>null</c>。</summary>
        public ParticleType[] Types
        {
            get { return _types; }
        }

        /// <summary>
        /// 底层格子数组。<b>只读契约</b>：外部只允许读，任何写入都必须走
        /// <see cref="Set"/> / <see cref="Clear"/> / <see cref="Swap"/>，否则会绕过钩子与处理标记。
        /// </summary>
        public Cell[] Cells
        {
            get { return _cells; }
        }

        /// <summary>
        /// 与网格同形的热图（每格一个热量 Q），世界持有并随世界重建。
        /// 热量**由物质携带**：<see cref="Swap"/> 会把 Q 一起搬走；<see cref="Set"/> / <see cref="Clear"/>
        /// 换上的是新物质，其热量重置为环境值。温度是导出量（<c>T = Q / C</c>）。
        /// 每逻辑帧由 <see cref="StepLogic"/> 做一次全格四邻热传导；粒子类型通过它查询 / 修改任意位置的温度与热量。
        /// </summary>
        public HeatMap Heat
        {
            get { return _heat; }
        }

        /// <summary>
        /// 与网格同形的**湿度**场（每格一个水汽质量），世界持有并随世界重建。
        /// 只有非固非液的格持有湿度；水蒸发提升湿度，湿度像热量一样传播并带温度定向
        /// （水平冷→热、竖直越热越向上），高值低温处凝结成雨。
        /// 湿度随 <see cref="Swap"/> 被气流搬走（云随风平移），随 <see cref="Set"/> / <see cref="Clear"/> 清零。
        /// 蒸发 / 凝结 / 降水都是 1:1 搬运，因此水总量守恒。见 <see cref="HumidityField"/>。
        /// </summary>
        public HumidityField Humidity
        {
            get { return _humidity; }
        }

        /// <summary>
        /// 光照控制器：按时序通断，照亮时向每列最上面的非气体粒子（固体或液体——光打到水面就停）注入热量。
        /// 每逻辑帧由 <see cref="StepLogic"/> 驱动；游戏侧通过 <c>world.Light.Enabled</c> 等属性配置。
        /// </summary>
        public LightController Light
        {
            get { return _light; }
        }

        /// <summary>网格宽（格数）。</summary>
        public int Width
        {
            get { return _width; }
        }

        /// <summary>网格高（格数）。</summary>
        public int Height
        {
            get { return _height; }
        }

        /// <summary>格子总数。</summary>
        public int CellCount
        {
            get { return _cells.Length; }
        }

        /// <summary>已完成的 tick 数量。步进过程中它还是「本次 Step 之前」的值。</summary>
        public int Tick
        {
            get { return _tick; }
        }

        /// <summary>下一次（或正在进行的）<see cref="StepLogic"/> 的扫描方向奇偶：false = x 升序，true = x 降序。</summary>
        public bool Parity
        {
            get { return (_tick & 1) != 0; }
        }

        /// <summary>
        /// 每逻辑帧随机抽取并触发随机刻的格数（0 = 关闭，默认）。类似 Minecraft 的 random tick：
        /// 不是全网格扫描，而是每逻辑帧只随机挑这么多格，命中非空气、非静态的格就调用其类型的
        /// <c>OnRandomTick</c>。适合「缓慢、按概率发生」的过程（生长、蔓延、渗流、腐化）。
        /// </summary>
        /// <remarks>
        /// 抽取**有放回**（同一格可能在同一 tick 被抽中多次），且用的是世界同一条确定性随机流，
        /// 因此计数与普通粒子规则的随机序列共享。赋值夹在 <c>[0, CellCount]</c>。
        /// </remarks>
        public int RandomTicksPerStep
        {
            get { return _randomTicksPerStep; }
            set
            {
                int max = _cells.Length;
                if (value < 0)
                {
                    _randomTicksPerStep = 0;
                }
                else
                {
                    _randomTicksPerStep = value > max ? max : value;
                }
            }
        }

        /// <summary>
        /// 步进之外的写入标记：等于**下一次 Step** 奇偶取反，即「本格尚未被本次 Step 处理」。
        /// </summary>
        private byte OutsideStamp
        {
            get { return (byte)((_tick & 1) ^ 1); }
        }

        /// <summary>把 (x,y) 换算成 <see cref="Cells"/> 的下标。不检查越界。</summary>
        public int Index(int x, int y)
        {
            return y * _width + x;
        }

        /// <summary>(x,y) 是否在网格内。</summary>
        public bool IsInBounds(int x, int y)
        {
            return (uint)x < (uint)_width && (uint)y < (uint)_height;
        }

        /// <summary>(x,y) 是否为空气/越界。</summary>
        public bool IsAir(int x, int y)
        {
            return GetCell(x, y).typeId == ParticleRegistry.AirTypeId;
        }

        /// <summary>读取格子（值拷贝，4 字节）。越界返回空气格。</summary>
        public Cell GetCell(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return default;
            }

            return _cells[y * _width + x];
        }

        /// <summary>
        /// 就地读写本格（flags / shade / 将来的 vx,vy）。
        /// 越界时返回一个共享的只读暂存格，写入它不会影响网格。
        /// </summary>
        /// <remarks>
        /// 移动之后坐标即失效：任何改变自身位置的写操作之后，禁止继续使用旧的 (x, y) 或之前的 ref。
        /// 类型代码只能 OR 位：<c>At(x,y).flags |= CellFlags.Burning;</c>，整体赋值会破坏 bit0。
        /// </remarks>
        public ref Cell At(int x, int y)
        {
            if (IsInBounds(x, y))
            {
                return ref _cells[y * _width + x];
            }

            _outside = default;
            return ref _outside;
        }

        /// <summary>
        /// 交换两个格子。位置变化**只能**通过它完成：不产生任何对象创建或销毁。
        /// 两个格子的 bit0 都会被写成当前 tick 的标记，因此被移动的粒子不会在本 tick 被二次处理。
        /// **不触发** <c>OnSpawn</c> / <c>OnKill</c>；<c>shade</c> 随格子一起移动，不重新抽样。
        /// </summary>
        /// <returns>两个坐标都在界内则 true；任一越界则什么都不做并返回 false。</returns>
        public bool Swap(int x0, int y0, int x1, int y1)
        {
            if (!IsInBounds(x0, y0) || !IsInBounds(x1, y1))
            {
                return false;
            }

            int a = y0 * _width + x0;
            int b = y1 * _width + x1;
            if (a == b)
            {
                return true;
            }

            Cell swap = _cells[a];
            _cells[a] = _cells[b];
            _cells[b] = swap;
            _heat.SwapHeat(a, b);

            // 湿度跟着物质走：云因此随风平移。固/液永远持有 0 湿度（不变量），
            // 所以水与气体换位时搬过去的是气体那侧的湿度，水不会把水汽带走。
            _humidity.Swap(a, b);

            byte stamp = _stamp;
            _cells[a].flags = CellFlags.WithProcessed(_cells[a].flags, stamp);
            _cells[b].flags = CellFlags.WithProcessed(_cells[b].flags, stamp);
            return true;
        }

        /// <summary>
        /// 把 (x,y) 无条件写成 <paramref name="typeId"/>。
        /// </summary>
        /// <remarks>
        /// 执行顺序严格为：读受害者 → 受害者 <c>OnKill(world, x, y, typeId)</c>（此刻网格上仍是受害者本体）
        /// → 写入新格（typeId / flags = 当前标记 / shade 重新抽样）→ 新类型 <c>OnSpawn</c>。
        /// 因此 <c>OnKill</c> 内对本格的写会被随后写入的新格覆盖，残留物必须写到邻居格。
        /// 同类型重复 Set 是空操作，不触发任何钩子、不消耗随机数。
        /// </remarks>
        /// <returns>坐标越界或 typeId 未注册则 false（非法 typeId 只报错一次）。</returns>
        public bool Set(int x, int y, ushort typeId)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            if (typeId != ParticleRegistry.AirTypeId && (typeId >= _types.Length || _types[typeId] == null))
            {
                ReportUnknownType(typeId);
                return false;
            }

            int index = y * _width + x;
            ushort previousId = _cells[index].typeId;
            if (previousId == typeId)
            {
                return true;
            }

            if (previousId != ParticleRegistry.AirTypeId && previousId < _types.Length)
            {
                ParticleType victim = _types[previousId];
                if (victim != null)
                {
                    victim.OnKill(this, x, y, typeId);
                }
            }

            ref Cell cell = ref _cells[index];
            cell.typeId = typeId;
            cell.flags = _stamp;

            // 新物质替换掉旧物质：热由物质携带，旧热随旧物质离场，新物质从环境热量开始。
            _heat.ResetHeatAt(index);

            // 同理，悬浮水滴属于旧物质；新物质不含凝结物（相变路径会先读出旧值再显式写回）。
            _humidity.ResetAt(index);

            if (typeId == ParticleRegistry.AirTypeId)
            {
                cell.shade = 0;
                return true;
            }

            ParticleType type = _types[typeId];
            Color32[] palette = type.Info.Palette;
            cell.shade = (byte)(NextUInt() % (uint)palette.Length);
            type.OnSpawn(this, x, y);
            return true;
        }

        /// <summary>抹除 (x,y)：等价于 <c>Set(x, y, AirTypeId)</c>，会触发原类型的 <c>OnKill(world, x, y, 0)</c>。</summary>
        public bool Clear(int x, int y)
        {
            return Set(x, y, ParticleRegistry.AirTypeId);
        }

        /// <summary>
        /// **相变专用**：把 (x,y) 换成 <paramref name="typeId"/>，并把旧格的**热量 Q 与凝结物**原样带过去，
        /// 随后触发新类型的 <c>OnSpawn</c>（新类型因此可以在 OnSpawn 里接管凝结物、释放潜热等）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="Set"/> 的区别正在于「携带」：<c>Set</c> 视为换上**全新物质**（Q 与凝结物清零），
        /// 而相变是**同一份物质换了个形态**——水/蒸汽/云之间的质量与热量必须留在原格、逐格守恒。
        /// 典型用法是蒸发：<c>SetPhase(x, y, Steam)</c> 保留 Q，新蒸汽因为热容骤降而变得更烫（浮力来源）。
        /// 抛给新类型 <c>OnSpawn</c> 的理由是它可能还要改这一格（例如空气接管云滴、凝结物 +1）。
        /// 它**不会**替相变双方做守恒记账，那由各自的类型代码负责（见 <c>WaterParticle</c> 与 <see cref="StepRain"/>）。
        /// </remarks>
        /// <returns>坐标越界或 typeId 未注册则 false（非法 typeId 只报错一次）。</returns>
        public bool SetPhase(int x, int y, ushort typeId)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            if (typeId != ParticleRegistry.AirTypeId && (typeId >= _types.Length || _types[typeId] == null))
            {
                ReportUnknownType(typeId);
                return false;
            }

            int index = y * _width + x;
            ushort previousId = _cells[index].typeId;
            if (previousId == typeId)
            {
                return true;
            }

            if (previousId != ParticleRegistry.AirTypeId && previousId < _types.Length)
            {
                ParticleType victim = _types[previousId];
                if (victim != null)
                {
                    victim.OnKill(this, x, y, typeId);
                }
            }

            float heat = _heat.GetHeat(x, y);
            float humidity = _humidity.Get(x, y);

            ref Cell cell = ref _cells[index];
            cell.typeId = typeId;
            cell.flags = _stamp;

            // Q 与凝结物跟着物质走，不重置（这正是与 Set 的区别）。
            _heat.SetHeat(x, y, heat);
            _humidity.Set(x, y, humidity);

            if (typeId == ParticleRegistry.AirTypeId)
            {
                cell.shade = 0;
            }
            else
            {
                Color32[] palette = _types[typeId].Info.Palette;
                cell.shade = (byte)(NextUInt() % (uint)palette.Length);
            }

            if (typeId != ParticleRegistry.AirTypeId)
            {
                _types[typeId].OnSpawn(this, x, y);
            }

            return true;
        }

        /// <summary>
        /// 降水：温度低于露点的饱和空气（湿度 ≥ <see cref="HumidityField.RainThreshold"/>）
        /// 按 <c>湿度 × RainRate</c> 的概率凝结成雨。由 <see cref="StepLogic"/> 每逻辑帧调用一次。
        /// </summary>
        /// <remarks>
        /// 湿度越高掉得越快，这是让云量收敛的负反馈（否则水汽会一路攒成满天薄雾）。
        /// 凝结把一整个水格的质量从湿度场搬成一个 Water 粒子（1:1，水守恒）；格子变成液体后会**把它剩余的
        /// 水汽推给周围的气体邻居**——固/液不持有湿度这条不变量因此得以保持，同时不丢质量。
        /// </remarks>
        private void StepRain()
        {
            Cell[] cells = _cells;
            int width = _width;
            int height = _height;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    if (!ParticleRules.IsGasOrEmpty(this, x, y))
                    {
                        continue; // 只有气体 / 空持有湿度
                    }

                    float humidity = _humidity.GetAt(i);
                    float temperature = _heat.GetTemperatureAt(i);
                    if (temperature > HumidityField.DewPoint)
                    {
                        continue; // 暖处的饱和空气只是闷热，不会下雨
                    }

                    // 饱和湿度随温度下降而下降（饱和水汽压随温度陡降的线性近似）：
                    // 越冷的地方越容易下雨。没有这一条，高位冷空气与海面用同一个阈值，
                    // 要么海面先下雨（云散）、要么高空的云永远攒不到阈值（海洋被蒸干）。
                    float coldFactor = temperature / HumidityField.DewPoint;
                    if (coldFactor < 0.1f)
                    {
                        coldFactor = 0.1f;
                    }

                    if (humidity < HumidityField.RainThreshold * coldFactor)
                    {
                        continue;
                    }

                    // 碰并：雨滴由**本格 + 气体邻居**的水汽凑成（现实中雨滴靠碰并长大，不是凭空出现）。
                    // 先看凑不凑得够一个整水滴——凑不够就这一 tick 不降，绝不能"取不足 1 却生成 1"（凭空造水）。
                    if (GatherableHumidity(x, y) < HumidityField.Unit)
                    {
                        continue;
                    }

                    float roll = (NextUInt() >> 8) * (1f / 16777216f);
                    if (roll >= humidity * HumidityField.RainRate)
                    {
                        continue;
                    }

                    // 本格剩下的水汽（湿度高于一个水滴时）马上要变成液体，而固/液不持有湿度——
                    // 若四周全是液/固（封闭气泡），残余无处安放，降这一滴就会把水汽永久困在水里。
                    // 所以先确认收得下，收不下就这一 tick 不降（而不是降完再漏）。
                    if (_humidity.GetAt(i) - HumidityField.Unit > 0f && CountMoistReceivers(x, y) == 0)
                    {
                        continue;
                    }

                    // 本格优先，再从气体邻居补足。
                    float need = HumidityField.Unit;
                    float self = _humidity.GetAt(i);
                    float takeSelf = self < need ? self : need;
                    _humidity.AddAt(i, -takeSelf);
                    need -= takeSelf;

                    for (int dy = -1; dy <= 1 && need > 0f; dy++)
                    {
                        for (int dx = -1; dx <= 1 && need > 0f; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            int nx = x + dx;
                            int ny = y + dy;
                            if (!IsInBounds(nx, ny) || !ParticleRules.IsGasOrEmpty(this, nx, ny))
                            {
                                continue;
                            }

                            float available = _humidity.Get(nx, ny);
                            float take = available < need ? available : need;
                            _humidity.Add(nx, ny, -take);
                            need -= take;
                        }
                    }

                    // 本格剩下的水汽（湿度高于一个水滴时）推给邻居：固/液不持有湿度。
                    EvacuateHumidity(x, y, i);
                    SetPhase(x, y, ParticleIds.Water);
                }
            }
        }

        /// <summary>
        /// (x,y) 及其**八邻**的气体格上总共有多少湿度（降雨"碰并"的可用料）。
        /// 用 3×3 而不是四邻：云层里每格湿度只有 ~0.15，四邻凑不满一个整水滴，
        /// 于是只有湿度最高的顶行才下得了雨——雨就显得"从虚空里落下来"。八邻让整层云都能下雨。
        /// </summary>
        private float GatherableHumidity(int x, int y)
        {
            float total = 0f;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(this, nx, ny))
                    {
                        total += _humidity.Get(nx, ny);
                    }
                }
            }

            return total;
        }

        /// <summary>(x,y) 四邻中能接收湿度的气体格数（0 = 封闭：残余湿度无处安放）。</summary>
        private int CountMoistReceivers(int x, int y)
        {
            int receivers = 0;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NeighbourX[k];
                int ny = y + NeighbourY[k];
                if (IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(this, nx, ny))
                {
                    receivers++;
                }
            }

            return receivers;
        }

        /// <summary>把 (x,y) 残留的湿度**全部**推给周围的气体邻居（推不动的部分留在原地）。
        /// 用于「气体 → 固/液」的相变：固/液不持有湿度——若留下残余，等它日后蒸发时会与新增的水汽
        /// 叠加，凭空造水（实测：每 4000 tick 多出 0.3 个水格）。</summary>
        private void EvacuateHumidity(int x, int y, int index)
        {
            float residual = _humidity.GetAt(index);
            if (residual <= 0f)
            {
                return;
            }

            int receivers = CountMoistReceivers(x, y);
            if (receivers == 0)
            {
                return; // 四面都不是气体：无处可去，只能留在原地（调用方须先确认收得下）
            }

            float share = residual / receivers;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NeighbourX[k];
                int ny = y + NeighbourY[k];
                if (IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(this, nx, ny))
                {
                    _humidity.Add(nx, ny, share);
                }
            }

            _humidity.AddAt(index, -share * receivers);
        }

        /// <summary>确定性随机数（xorshift32）。类型代码一律用它，禁止 UnityEngine.Random。</summary>
        public uint NextUInt()
        {
            uint x = _rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _rng = x;
            return x;
        }

        /// <summary>[0, maxExclusive) 内的确定性随机整数；maxExclusive &lt;= 0 时返回 0。</summary>
        public int NextInt(int maxExclusive)
        {
            return maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);
        }

        /// <summary>[0, maxExclusive) 内的确定性随机字节；maxExclusive == 0 时返回 0。</summary>
        public byte NextByte(byte maxExclusive)
        {
            return maxExclusive == 0 ? (byte)0 : (byte)(NextUInt() % maxExclusive);
        }

        /// <summary>
        /// 推进一个**运动帧**（60 Hz 的基本节拍）：只做一次全网格扫描，对每个粒子调用其
        /// <see cref="ParticleType.OnMotion"/>。**不跑相变、热传导、湿度、降水、光照或随机刻**——
        /// 那些都属于逻辑帧，见 <see cref="StepLogic"/>。
        /// </summary>
        /// <remarks>
        /// 与渲染解耦：本方法不接触任何渲染资源，由上层决定跑完几次之后才上屏一次。
        /// 运动帧是世界的唯一时基：<see cref="Tick"/> 无论走哪一个入口都每帧自增一次，
        /// 扫描方向与 bit0 处理标记也随之交替。
        /// </remarks>
        public void StepMotion()
        {
            try
            {
                Scan(logic: false);
                _tick++;
            }
            finally
            {
                // 某个 OnMotion 抛异常时 _tick 未自增，此处按「旧 tick」恢复写入标记，
                // 使异常后新建的粒子仍会被下一帧处理；正常路径则按自增后的 tick 恢复。
                _stamp = OutsideStamp;
            }
        }

        /// <summary>
        /// 推进一个**逻辑帧**（20 Hz）：在照常做一次运动扫描的基础上，先对每个粒子调用其
        /// <see cref="ParticleType.OnLogic"/>（相变 / 反应），扫描后再推进随机刻与整场的
        /// 热传导、湿度输送、降水与光照。
        /// </summary>
        /// <remarks>
        /// 分段：<b>①粒子扫描</b>（每格先 <c>OnLogic</c>、未被相变换走的再 <c>OnMotion</c>）→
        /// <b>①′随机刻</b>（随机抽 <see cref="RandomTicksPerStep"/> 个格，命中非空气非静态的格就调 <c>OnRandomTick</c>）→
        /// <b>②热传导 + 顶行向虚空散热</b>（<see cref="HeatMap.Step"/>）→
        /// <b>③湿度输送</b>（<see cref="HumidityField.Step"/>：扩散 + 水平冷→热 + 竖直越热越向上）→
        /// <b>③′降水</b>（温度低于露点的饱和空气凝结成雨）→
        /// <b>④光照</b>（<see cref="LightController.Step"/>，向每列最上面的非气体粒子施热）。
        /// 因此逻辑帧注入的热 / 湿度会在下一个逻辑帧被粒子读到、并参与下一轮扩散。
        /// 上层应让逻辑帧落在运动帧的节拍上——每 3 个运动帧跑一次本方法，即 60 Hz 运动 / 20 Hz 逻辑。
        /// </remarks>
        public void StepLogic()
        {
            try
            {
                Scan(logic: true);

                // ①′ 随机刻：与扫描同属「块行为」，因此放在 _tick 自增之前、
                // 让 OnRandomTick 读到的 world.Tick 与 OnLogic / OnMotion 一致；热/湿度/光照仍在其后。
                RunRandomTicks();

                _tick++;

                // 网格扫描之后：先做一次全格四邻热传导（含辐射/虚空散热），再让光照向每列最上面的非气体粒子施热。
                // 全程在 try 内，异常（如某类型钩子抛出）时本帧的 _tick 不自增，
                // 传导 / 湿度 / 降水 / 光照一并不生效，与「异常后粒子仍会在下一帧被处理」的约定一致。
                _heat.Step();
                _humidity.Step(this);
                StepRain();
                _light.Step(this);
            }
            finally
            {
                // 某个钩子抛异常时 _tick 未自增，此处按「旧 tick」恢复写入标记，
                // 使异常后新建的粒子仍会被下一帧处理；正常路径则按自增后的 tick 恢复。
                _stamp = OutsideStamp;
            }
        }

        /// <summary>
        /// 一次全网格扫描，世界的唯一处理入口。行从高到低、x 方向按 <see cref="Tick"/> 奇偶交替；
        /// <paramref name="logic"/> 为 true 时每格先跑一次逻辑钩子。
        /// </summary>
        private void Scan(bool logic)
        {
            byte stamp = (byte)(_tick & 1);
            _stamp = stamp;

            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int height = _height;
            bool reversed = (_tick & 1) != 0;

            for (int y = height - 1; y >= 0; y--)
            {
                int rowBase = y * width;
                if (reversed)
                {
                    for (int x = width - 1; x >= 0; x--)
                    {
                        ProcessCell(x, y, rowBase + x, stamp, logic, cells, types);
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        ProcessCell(x, y, rowBase + x, stamp, logic, cells, types);
                    }
                }
            }
        }

        /// <summary>
        /// ①′ 随机刻：均匀有放回地抽 <see cref="RandomTicksPerStep"/> 个格，命中非空气、非静态的格就调其类型委托。
        /// 不看、也不写 bit0 处理标记——随机刻独立于扫描，同一格可以在同一次 <see cref="StepLogic"/> 里被抽中多次。
        /// </summary>
        private void RunRandomTicks()
        {
            int picks = _randomTicksPerStep;
            if (picks <= 0)
            {
                return;
            }

            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int count = cells.Length;

            for (int i = 0; i < picks; i++)
            {
                int index = (int)(NextUInt() % (uint)count);
                ushort id = cells[index].typeId;
                if (id == ParticleRegistry.AirTypeId || id >= types.Length)
                {
                    continue;
                }

                ParticleType type = types[id];
                if (type == null || type.Info.IsStatic)
                {
                    continue;
                }

                int y = index / width;
                type.OnRandomTick(this, index - y * width, y);
            }
        }

        private void ProcessCell(
            int x, int y, int index, byte stamp, bool logic, Cell[] cells, ParticleType[] types)
        {
            ushort id = cells[index].typeId;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return;
            }

            ParticleType type = types[id];
            if (type == null || type.Info.IsStatic)
            {
                return;
            }

            if (CellFlags.IsProcessed(cells[index].flags, stamp))
            {
                return;
            }

            cells[index].flags = CellFlags.WithProcessed(cells[index].flags, stamp);

            if (logic)
            {
                // 逻辑钩子先跑（相变 / 反应）。它若把本格换成了别的类型，说明这一格本帧已经「用掉」了：
                // 新格已由 Set / SetPhase 标成已处理，旧类型的运动规则也不再适用于新物质——本帧跳过运动。
                type.OnLogic(this, x, y);
                if (cells[index].typeId != id)
                {
                    return;
                }
            }

            type.OnMotion(this, x, y);
        }

        private void ReportUnknownType(ushort typeId)
        {
            if (_unknownTypeReported)
            {
                return;
            }

            _unknownTypeReported = true;
            Debug.LogError(
                $"[ParticleWorld] 未注册的 typeId {typeId} 被写入网格，已忽略。请确认 ParticleRegistration.RegisterAll() 已执行。");
        }
    }
}
