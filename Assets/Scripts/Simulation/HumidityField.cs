using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 与 <see cref="ParticleWorld"/> 网格同形的**湿度场**：每格一个湿度值（<see cref="float"/>，
    /// 单位 = 一个水格的质量）。水汽是空气的一种状态——**云就是湿度足够高的空气**，不是一种粒子。
    /// </summary>
    /// <remarks>
    /// <para><b>只有非固非液的格持有湿度</b>（气体 / 空）。水与固体是屏障：湿度不进入它们、也不在其内部传播，
    /// 因此海洋只通过"蒸发出水汽"参与循环，而不会自己变成一团湿土。</para>
    /// <para><b>湿度像热量一样传播，但带温度定向</b>（本场与 <see cref="HeatMap"/> 的关键区别）：
    /// <list type="bullet">
    /// <item><b>扩散</b>：高湿度 → 低湿度（与热传导同形）。</item>
    /// <item><b>水平：冷 → 热</b>。同一高度上，湿度向更热的格子迁移（热处空气上升、冷处空气下沉，
    /// 把水汽往暖侧辐合）。</item>
    /// <item><b>竖直：越热越向上</b>。格子温度越高，它的湿度越倾向于往上走——这正是"云飘在天上"的动力：
    /// 暖水面蒸出的水汽被持续抬升，升到高空变冷、凝结，就是云底在那里的原因。</item>
    /// </list>
    /// 两条定向项都以**成对反对称**的形式结算（<c>a→b</c> 与 <c>b→a</c> 等量反向），因此界内严格守恒。</para>
    /// <para><b>云的生成与消散是涌现的**：水蒸发把湿度加进空气，湿度按上面的规则被输送，
    /// 高到阈值又在低温处凝结成雨（<see cref="ParticleWorld"/> 的降水 pass）。没有"云滴粒子"、
    /// 没有聚团/碰并规则，云就是湿度场里的一片高值区——对应真实世界里"云是流动的稳态，不是物体"。</para>
    /// <para><b>简化**：不区分水滴与冰晶；不做 LCL 的显式建模（云底是"凝结温度面"的涌现）；湿度不做上界截断
    /// （截断会丢质量；降水负责把高值区消化掉）。</para>
    /// </remarks>
    public sealed class HumidityField
    {
        /// <summary>一个水格蒸发/凝结出的湿度量。所有相变方向都用它，保证水总量守恒。</summary>
        public const float Unit = 1f;

        /// <summary>扩散系数：高湿度向低湿度摊开的比例（与热传导同形）。</summary>
        public const float DefaultDiffusion = 0.01f;

        /// <summary>水平温度定向系数：同一高度上湿度向更热格子迁移的强度（每单位温差）。</summary>
        public const float DefaultHorizontalBias = 0.05f;

        /// <summary>竖直温度定向系数：越热的格子湿度越向上的强度（每 tick、每单位归一化温度）。
        /// 实际向上流出的比例是 <c>RiseBias × T / <see cref="RiseTemperatureScale"/></c>。</summary>
        public const float DefaultRiseBias = 0.5f;

        /// <summary>竖直升腾的温度归一化尺度：温度达到它时，一格每 tick 最多把全部湿度往上送。</summary>
        public const float RiseTemperatureScale = 100f;

        /// <summary><see cref="RiseTemperatureScale"/> 的倒数（热路径用乘法）。</summary>
        private const float InverseRiseTemperature = 1f / RiseTemperatureScale;

        /// <summary>
        /// 湿度达到它就**不再向上抬升**——"凝结后的气块失去浮力"的等效表达。
        /// 取略高于 <see cref="VisibleThreshold"/> 的"厚云"参考值：湿度浓到看得清（成云）之后就不再抬升，
        /// 云于是在那个高度铺开；若不设上限，水汽会一路顶到天花板、堆成贴着顶行的薄盖子。
        /// </summary>
        public const float RiseStopHumidity = 0.2f;

        /// <summary>降水阈值：湿度达到它、且温度低于露点时开始"碰并"成雨。
        /// 低于 <see cref="Unit"/> 是有意的——雨滴由本格与邻居的水汽凑成（见 <c>ParticleWorld.StepRain</c>），
        /// 所以不必要求单格先攒满一个整水滴（实测天空里很难达到 1.0）。</summary>
        public const float RainThreshold = 0.4f;

        /// <summary>露点：温度低于它的饱和空气才凝结（成云/降雨）。</summary>
        public const float DewPoint = 30f;

        /// <summary>蒸发上限：本格湿度达到它就停止蒸发（饱和水汽压的等效表达）。
        /// 没有它，海洋会一路蒸干——雨水落回海面又立刻被蒸走，永远攒不成海。
        /// 取小值让"空气一湿就停"，蒸发因此自动与降水/输送达成平衡。</summary>
        public const float EvaporationCeiling = 0.1f;

        /// <summary>渲染起点：湿度低于它完全不显示（否则整片天空都会蒙一层灰）。</summary>
        public const float VisibleThreshold = 0.06f;

        /// <summary>渲染满白：湿度达到它即纯白。取 <see cref="VisibleThreshold"/> 与
        /// <see cref="RiseStopHumidity"/> 之间的一个"厚云"参考值——若沿用 1.0，
        /// 天空里实际能达到的湿度（~0.1~0.2 封顶）永远渲成近乎透明，"高空的云看不见"就是这么来的；
        /// 取太小又会满屏惨白（实测：0.12 时近纯白格 1866 个，0.16 时降到 52 个而云层依旧清楚）。</summary>
        public const float CloudFullHumidity = 0.16f;

        /// <summary>降水速率（每 tick、每单位湿度凝结成雨的概率）：湿度越高掉得越快（负反馈，云量因此稳定）。</summary>
        public const float RainRate = 0.4f;

        /// <summary>**不可见**蒸发的播撒半径：4 = 9×9 一大片，每格只分到 <c>Unit/49 ≈ 0.02</c>，
        /// 远低于 <see cref="VisibleThreshold"/>（0.06）——所以这条路蒸出来的水汽**看不见**。
        /// 水的常温慢蒸发与湿土受热干燥都走它（可见水汽只由沸腾产生）。</summary>
        public const int InvisibleSpreadRadius = 4;

        /// <summary><see cref="InvisibleSpreadRadius"/> 下的饱和上限：播撒范围内**任一**气体格达到它就不再蒸发。
        /// 取 0.02，使「最湿的一格 + 本次份额」仍低于 <see cref="VisibleThreshold"/>——这条路因此**永远不可见**。
        /// 它同时是「饱和水汽压」的物理节流：边界层湿了就蒸发不动，等输送把水汽带走再继续。</summary>
        public const float InvisibleSpreadCap = 0.02f;

        private readonly int _width;
        private readonly int _height;
        private float[] _humidity;
        private float[] _scratch;
        private float[] _budget;

        private float _diffusion = DefaultDiffusion;
        private float _horizontalBias = DefaultHorizontalBias;
        private float _riseBias = DefaultRiseBias;

        /// <param name="width">宽度（格），范围 <c>1..ParticleWorld.MaxSide</c>。</param>
        /// <param name="height">高度（格），范围 <c>1..ParticleWorld.MaxSide</c>。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一边超出 <c>1..ParticleWorld.MaxSide</c>。</exception>
        public HumidityField(int width, int height)
        {
            if (width < 1 || width > ParticleWorld.MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, $"width 必须在 1..{ParticleWorld.MaxSide} 之间。");
            }

            if (height < 1 || height > ParticleWorld.MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, $"height 必须在 1..{ParticleWorld.MaxSide} 之间。");
            }

            _width = width;
            _height = height;
            _humidity = new float[width * height];
        }

        /// <summary>场宽（格数），与世界网格一致。</summary>
        public int Width
        {
            get { return _width; }
        }

        /// <summary>场高（格数），与世界网格一致。</summary>
        public int Height
        {
            get { return _height; }
        }

        /// <summary>格子总数。</summary>
        public int CellCount
        {
            get { return _humidity.Length; }
        }

        /// <summary>扩散系数，夹在 <c>[0, 1]</c>。</summary>
        public float Diffusion
        {
            get { return _diffusion; }
            set { _diffusion = ClampUnit(value); }
        }

        /// <summary>水平温度定向系数（湿度向更热处迁移的强度），夹在 <c>[0, 1]</c>。</summary>
        public float HorizontalBias
        {
            get { return _horizontalBias; }
            set { _horizontalBias = ClampUnit(value); }
        }

        /// <summary>竖直温度定向系数（越热越向上），夹在 <c>[0, 1]</c>。</summary>
        public float RiseBias
        {
            get { return _riseBias; }
            set { _riseBias = ClampUnit(value); }
        }

        /// <summary>把 (x,y) 换算成下标。不检查越界。</summary>
        public int Index(int x, int y)
        {
            return y * _width + x;
        }

        /// <summary>(x,y) 是否在场内。</summary>
        public bool IsInBounds(int x, int y)
        {
            return (uint)x < (uint)_width && (uint)y < (uint)_height;
        }

        /// <summary>读取 (x,y) 的湿度。越界返回 0。</summary>
        public float Get(int x, int y)
        {
            return IsInBounds(x, y) ? _humidity[y * _width + x] : 0f;
        }

        /// <summary>按扁平下标读取湿度（渲染层逐格映射用）。越界返回 0。</summary>
        public float GetAt(int index)
        {
            return (uint)index < (uint)_humidity.Length ? _humidity[index] : 0f;
        }

        /// <summary>把 (x,y) 的湿度无条件写成 <paramref name="value"/>（下界 0，无上界）。</summary>
        /// <returns>坐标在界内则 true。</returns>
        public bool Set(int x, int y, float value)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            _humidity[y * _width + x] = Floor(value);
            return true;
        }

        /// <summary>按扁平下标累加湿度。越界无操作。</summary>
        public void AddAt(int index, float delta)
        {
            if ((uint)index >= (uint)_humidity.Length)
            {
                return;
            }

            _humidity[index] = Floor(_humidity[index] + delta);
        }

        /// <summary>给 (x,y) 累加湿度（下界 0）。越界无操作。</summary>
        /// <returns>坐标在界内则 true。</returns>
        public bool Add(int x, int y, float delta)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            _humidity[y * _width + x] = Floor(_humidity[y * _width + x] + delta);
            return true;
        }

        /// <summary>全场湿度总量（水守恒断言用）。</summary>
        public float Sum()
        {
            float total = 0f;
            for (int i = 0; i < _humidity.Length; i++)
            {
                total += _humidity[i];
            }

            return total;
        }

        /// <summary>
        /// 把**一份水汽**（<see cref="Unit"/>）均摊给 (centerX,centerY) 周围半径 <paramref name="radius"/> 内的
        /// 气体 / 空格，每格分到 <c>Unit / 接收格数</c>——播撒，而不是集中，因此摊得多薄取决于半径。
        /// 返回实际接收的格数：<c>0</c> 表示周围没有任何气体，这份水汽无处可去，
        /// **调用方必须先确认有接收者**（否则水汽凭空消失，破坏水总量守恒）。
        /// 越界格不算接收者（<see cref="ParticleRules.IsGasOrEmpty"/> 对越界返回 true，故此处显式判界）。
        /// </summary>
        /// <remarks>蒸发（水 / 湿土）把水汽交给大气的唯一入口；与 <see cref="Add"/> 一样下界截断、无上界。</remarks>
        public int SpreadUnitIntoAir(ParticleWorld world, int centerX, int centerY, int radius)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (radius < 0)
            {
                radius = 0;
            }

            int receivers = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (IsAir(world, centerX + dx, centerY + dy))
                    {
                        receivers++;
                    }
                }
            }

            if (receivers == 0)
            {
                return 0;
            }

            float share = Unit / receivers;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (IsAir(world, centerX + dx, centerY + dy))
                    {
                        Add(centerX + dx, centerY + dy, share);
                    }
                }
            }

            return receivers;
        }

        /// <summary>
        /// (centerX,centerY) 周围半径 <paramref name="radius"/> 内的气体 / 空格，是否**任一格**的湿度已达
        /// <paramref name="cap"/>——「空气已经吸不动了」的饱和判据（越界格不计）。
        /// 蒸发前先问它：既不往饱和空气里继续灌水汽，也是「蒸发出来的水汽不可见」的保证
        /// （见 <see cref="InvisibleSpreadCap"/>）。
        /// </summary>
        public bool IsAirSaturated(ParticleWorld world, int centerX, int centerY, int radius, float cap)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (radius < 0)
            {
                radius = 0;
            }

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = centerX + dx;
                    int y = centerY + dy;
                    if (IsAir(world, x, y) && Get(x, y) >= cap)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>界内且是气体 / 空格（湿度的唯一载体）。</summary>
        private static bool IsAir(ParticleWorld world, int x, int y)
        {
            return world.IsInBounds(x, y) && ParticleRules.IsGasOrEmpty(world, x, y);
        }

        /// <summary>
        /// 把每列湿度总量写进 <paramref name="destination"/>（长度须 ≥ <see cref="Width"/>），
        /// 即**每列的云层厚度**——光竖直穿过的整条路径上的云量（只有非固非液格持有湿度，
        /// 而光路上的格恰好都是气体/空，故整列求和就是光程上的云量）。
        /// 由 <see cref="LightController"/> 每 tick 维护、用来决定该列地表能收到多少光。
        /// </summary>
        /// <exception cref="ArgumentNullException">目标缓冲为 null。</exception>
        /// <exception cref="ArgumentException">目标缓冲短于场宽。</exception>
        public void ColumnSums(float[] destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destination.Length < _width)
            {
                throw new ArgumentException(
                    $"目标缓冲长度 {destination.Length} 小于场宽 {_width}。", nameof(destination));
            }

            int width = _width;
            int height = _height;
            for (int x = 0; x < width; x++)
            {
                destination[x] = _humidity[x];
            }

            for (int y = 1; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    destination[x] += _humidity[row + x];
                }
            }
        }

        /// <summary>
        /// 一次湿度输送：扩散 + 温度定向（水平冷→热、竖直越热越向上），只在气体 / 空格之间进行。
        /// 由 <see cref="ParticleWorld.Step"/> 每 tick 调用一次，界内严格守恒（成对反对称结算）。
        /// </summary>
        public void Step(ParticleWorld world)
        {
            float diffusion = _diffusion;
            float horizontalBias = _horizontalBias;
            float riseBias = _riseBias;
            if (diffusion <= 0f && horizontalBias <= 0f && riseBias <= 0f)
            {
                return;
            }

            float[] current = _humidity;
            float[] next = _scratch ?? (_scratch = new float[_humidity.Length]);
            Array.Copy(current, next, current.Length);

            // 每个格子的"可给出额度"：一对邻居一条出边，两条出边可能都想把该格搬空，
            // 用额度逐个扣减就不会出现负值——而负值一旦被下界截断就会**凭空造出湿度**
            // （实测：不做额度控制时每 3000 tick 多出 158 个水格）。
            float[] budget = _budget ?? (_budget = new float[_humidity.Length]);
            Array.Copy(current, budget, current.Length);

            HeatMap heat = world.Heat;
            int width = _width;
            int height = _height;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    // 只有非固非液的格持有湿度：固/液是屏障，不参与输送。
                    if (!IsMoistAir(world, x, y))
                    {
                        continue;
                    }

                    int a = row + x;

                    // 水平：右邻（每对只结算一次）。湿度向更热的一侧迁移（"同高度从冷迁往热"）。
                    if (x + 1 < width && IsMoistAir(world, x + 1, y))
                    {
                        int b = a + 1;
                        float flow = diffusion * (budget[a] - budget[b])
                            + horizontalBias * (heat.GetTemperatureAt(b) - heat.GetTemperatureAt(a))
                            * 0.5f * (budget[a] + budget[b]);
                        Transfer(next, budget, a, b, flow);
                    }

                    // 竖直：上邻。**温度越高的格子，湿度越倾向于向上传播**——这是一次"上风"平流：
                    // 本格按自身温度把一部分湿度往上送，与上面那格热不热无关
                    // （所以"下面冷"不会把湿度吸下来；扩散项仍然对称，负责摊平）。
                    //
                    // 但**已经凝成云的空气不再上升**（真实里凝结后的气块失去浮力）：若不加这一条，
                    // 水汽会一路顶到天花板、堆成贴着顶行的薄盖子，而顶行又是向虚空散热的最冷处，
                    // 于是"云只有薄薄一层、雨像从虚空里落下来"。加门控后云会在**第一次达到可见浓度的高度**
                    // 铺成一层，那才是云的形状。
                    if (y + 1 < height && IsMoistAir(world, x, y + 1) && budget[a] < RiseStopHumidity)
                    {
                        int b = a + width;
                        float self = budget[a];
                        float upward = riseBias * (heat.GetTemperatureAt(a) * InverseRiseTemperature) * self;
                        float flow = diffusion * (self - budget[b]) + upward;
                        Transfer(next, budget, a, b, flow);
                    }
                }
            }

            _humidity = next;
            _scratch = current;
        }

        /// <summary>
        /// 结算一对邻居之间的一笔湿度流动（正 = a→b），流量上限由**给出方**剩余额度决定，
        /// 因此任何格子都不会被搬成负数；同一笔流量等量地从给出方扣、给接收方加，界内总量严格不变。
        /// </summary>
        private static void Transfer(float[] next, float[] budget, int a, int b, float flow)
        {
            if (flow > 0f)
            {
                if (flow > budget[a])
                {
                    flow = budget[a];
                }

                budget[a] -= flow;
                next[a] -= flow;
                next[b] += flow;
            }
            else if (flow < 0f)
            {
                float amount = -flow;
                if (amount > budget[b])
                {
                    amount = budget[b];
                }

                budget[b] -= amount;
                next[a] += amount;
                next[b] -= amount;
            }
        }

        private static bool IsMoistAir(ParticleWorld world, int x, int y)
        {
            return ParticleRules.IsGasOrEmpty(world, x, y);
        }

        private static float ClampUnit(float value)
        {
            if (float.IsNaN(value) || value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }

        private static float Floor(float value)
        {
            if (float.IsNaN(value))
            {
                return 0f;
            }

            return value < 0f ? 0f : value;
        }

        /// <summary><see cref="ParticleWorld.Set"/> 换上全新物质时把该格湿度清零——
        /// 固/液不持有湿度，新物质也不含旧物质的水汽（相变路径用 <c>SetPhase</c> 显式搬运，不走这里）。</summary>
        internal void ResetAt(int index)
        {
            _humidity[index] = 0f;
        }

        /// <summary><see cref="ParticleWorld.Swap"/> 交换两格时同步搬运湿度（湿度随风平移的机制）。</summary>
        internal void Swap(int a, int b)
        {
            float swap = _humidity[a];
            _humidity[a] = _humidity[b];
            _humidity[b] = swap;
        }
    }
}
