using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 一格的探针读数快照（值类型）：坐标、类型 id 与三个导出量。**不含** <see cref="Cell"/> 引用——
    /// 消费方（HUD）只看这份拷贝，看不到也改不到活世界。
    /// </summary>
    public struct CellProbe
    {
        /// <summary>这一格是否有效（越界 / 世界尚未发布过探针时为 false）。</summary>
        public bool Valid;

        /// <summary>格坐标 x。</summary>
        public int X;

        /// <summary>格坐标 y。</summary>
        public int Y;

        /// <summary>类型 id（空气为 <see cref="ParticleRegistry.AirTypeId"/>）。</summary>
        public ushort TypeId;

        /// <summary>温度（导出量 T = Q / 热容）。</summary>
        public float Temperature;

        /// <summary>热量（格子里存的那份 Q）。</summary>
        public float Heat;

        /// <summary>湿度（固 / 液恒为 0）。</summary>
        public float Humidity;
    }

    /// <summary>
    /// 一只实体的**渲染 DTO**（值类型）：渲染层需要的位置 / 朝向 / 动画倍率 / 占位色，
    /// 没有任何 <see cref="Creature"/> 引用（活实体是 world 私有的可变对象）。
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> 稳定标识一只实体，渲染层据此对账视图——不用对象引用，因此帧可以在 worker 上生成。
    /// <see cref="AnimationSpeedFactor"/> 在生成侧算好（它要读 AI 状态，那是 world 私有的）：
    /// 渲染层只做「帧索引 × 倍率」。
    /// </remarks>
    public struct CreatureRenderData
    {
        /// <summary>稳定标识（同一世界内单调递增、从 1 起；世界重建后从头开始，用 <see cref="SimulationFrame.Generation"/> 区分）。
        /// 0 = 未赋值哨兵，真实实体恒 &gt; 0。</summary>
        public int Id;

        /// <summary>物种（决定美术帧序列；<see cref="CreatureSpecies.None"/> = 未接线物种，退回占位块）。</summary>
        public CreatureSpecies Species;

        /// <summary>本逻辑帧位置 x（碰撞盒底边中点）。</summary>
        public float X;

        /// <summary>本逻辑帧位置 y（碰撞盒底边中点）。</summary>
        public float Y;

        /// <summary>上一逻辑帧位置 x（插值起点）。</summary>
        public float PrevX;

        /// <summary>上一逻辑帧位置 y（插值起点）。</summary>
        public float PrevY;

        /// <summary>碰撞盒宽（格，占位块尺寸）。</summary>
        public float Width;

        /// <summary>碰撞盒高（格，占位块尺寸）。</summary>
        public float Height;

        /// <summary>朝向：+1 朝右、−1 朝左、0 = 未定。</summary>
        public int Facing;

        /// <summary>行为状态（语义按物种解释，见各 Brain 的 <c>State*</c> 常数；鸟用它选帧组——
        /// 渲染层按 <c>CreatureRenderer.ResolveBirdFrame</c> 换组）。基类只搬运、不解释。</summary>
        public byte AiState;

        /// <summary>动画帧速倍率（逃命 ×3、移动 ×2、静止 ×1）。</summary>
        public float AnimationSpeedFactor;

        /// <summary>占位块颜色（真 sprite 接线时不用）。**项目自有的纯值颜色**（<see cref="RenderColor"/>），
        /// 与像素帧同一口径：帧载荷里不出现任何 Unity 类型，因此帧可以由 worker 线程整份生成。</summary>
        public RenderColor PlaceholderColor;
    }

    /// <summary>
    /// 上屏像素帧：**渲染真源**。像素数组归本帧槽所有、跨帧复用，且是**项目自有的纯值像素**
    /// （<see cref="RenderColor"/>，不引用 Unity 类型）——因此这一帧可以由模拟 worker 生产，
    /// 渲染层只负责把它整块拷进 Unity 上传缓冲（<c>SetPixels32</c> / <c>Apply</c>）。
    /// </summary>
    public sealed class RenderFrame
    {
        /// <summary>像素缓冲（行主序，长度 = <see cref="Width"/> × <see cref="Height"/>），本帧槽持有、跨帧复用。</summary>
        public RenderColor[] Pixels { get; private set; }

        /// <summary>网格宽（格 / 像素）。</summary>
        public int Width { get; private set; }

        /// <summary>网格高（格 / 像素）。</summary>
        public int Height { get; private set; }

        /// <summary>发布这一刻的已推进 tick 数（诊断 / 测试可观测量）。</summary>
        public int Tick { get; internal set; }

        /// <summary>发布这一刻的日照度（0 = 夜、1 = 昼）。</summary>
        public float Daylight { get; internal set; }

        /// <summary>
        /// 发布这一刻的**整场明暗倍率**（<c>Lerp(夜晚亮度, 1, 日照度)</c>，夹在 [0, 1]）：
        /// 帧发布侧算好写在这里，粒子像素与实体 sprite 因此用同一个倍率变暗（不重复算公式）。
        /// </summary>
        public float Brightness { get; internal set; }

        internal RenderFrame(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new RenderColor[width * height];
        }
    }

    /// <summary>
    /// 世界面向帧：尺寸、tick、日照度、暂停状态，以及**最后一次被请求的**探针读数。
    /// 不暴露 <see cref="ParticleWorld.Cells"/> 或任何活世界引用。
    /// </summary>
    public sealed class WorldFrame
    {
        /// <summary>网格宽（格）。</summary>
        public int Width { get; internal set; }

        /// <summary>网格高（格）。</summary>
        public int Height { get; internal set; }

        /// <summary>发布这一刻的已推进 tick 数。</summary>
        public int Tick { get; internal set; }

        /// <summary>发布这一刻的日照度（0 = 夜、1 = 昼）。</summary>
        public float Daylight { get; internal set; }

        /// <summary>发布这一刻模拟是否暂停（暂停时运动帧与逻辑帧一起冻结）。</summary>
        public bool Paused { get; internal set; }

        /// <summary>最后一次被请求的探针读数；<see cref="CellProbe.Valid"/> 为 false 表示本次发布没有有效读数。</summary>
        public CellProbe Probe { get; internal set; }
    }

    /// <summary>
    /// 实体渲染帧：连续的 <see cref="CreatureRenderData"/> 快照 + 插值所需的逻辑帧读数。
    /// 消费方按 <see cref="Count"/> 与 <see cref="Creature(int)"/> 只读遍历（拿到的总是值拷贝），
    /// 因此帧内容不会被外部改写。
    /// </summary>
    public sealed class CreatureFrame
    {
        private CreatureRenderData[] _buffer = new CreatureRenderData[0];

        /// <summary>本帧的实体数量（= 世界当前存活实体数）。</summary>
        public int Count { get; internal set; }

        /// <summary>发布这一刻动物世界已推进的逻辑帧数（渲染层据此检测「刚跨过一个逻辑帧」）。</summary>
        public int LogicTicks { get; internal set; }

        /// <summary>逻辑帧速率（Hz）：渲染层把插值比例按它推进。</summary>
        public float LogicRate { get; internal set; }

        /// <summary>只读取出第 <paramref name="index"/> 只实体的渲染 DTO（值拷贝）。</summary>
        public CreatureRenderData Creature(int index)
        {
            return _buffer[index];
        }

        /// <summary>准备本帧的容量（复用缓冲，只在实体数增长时扩容）。</summary>
        internal void Begin(int count)
        {
            if (_buffer.Length < count)
            {
                _buffer = new CreatureRenderData[count];
            }

            Count = count;
        }

        /// <summary>写入第 <paramref name="index"/> 只实体。</summary>
        internal void Set(int index, in CreatureRenderData data)
        {
            _buffer[index] = data;
        }
    }

    /// <summary>
    /// 音频观察帧：本帧（自上次发布以来）的运动 / 存量 / 新建计数。由 world 的**唯一写者**在发布前抽干
    /// 监视器得到，因此每个消费方都只是读同一份快照——谁先读谁后读都一样，谁也不清账。
    /// </summary>
    public sealed class AudioFrame
    {
        private readonly int[] _creatureSpawns = new int[CreatureWorld.SpeciesSlotCount];
        private readonly int[] _creatureDeaths = new int[CreatureWorld.SpeciesSlotCount];

        /// <summary>液体运动比例（本帧动过的液体 ÷ 液体总数，夹在 [0, 1]）。</summary>
        public float LiquidMovingRatio { get; internal set; }

        /// <summary>固体运动格数（本帧发生换位的无机固体端数）。</summary>
        public long SolidMovingCount { get; internal set; }

        /// <summary>火格存量（持续现象的响度来源，不是「本帧新增」）。</summary>
        public int FireCount { get; internal set; }

        /// <summary>自上次发布以来新写入的雷电格数（一次性事件的判据）。</summary>
        public int LightningCreated { get; internal set; }

        /// <summary>本帧该物种的出生数（自上次发布以来，折叠前的原始计数；0 = 没有出生）。
        /// 声音驱动按「每物种每帧最多一声」折叠，因此只判 &gt; 0。</summary>
        public int CreatureSpawnCount(CreatureSpecies species)
        {
            int index = (int)species;
            return index >= 0 && index < _creatureSpawns.Length ? _creatureSpawns[index] : 0;
        }

        /// <summary>本帧该物种的死亡数（口径同 <see cref="CreatureSpawnCount"/>）。</summary>
        public int CreatureDeathCount(CreatureSpecies species)
        {
            int index = (int)species;
            return index >= 0 && index < _creatureDeaths.Length ? _creatureDeaths[index] : 0;
        }

        /// <summary>写入本帧的物种事件计数（采集侧调用；逐槽覆盖写，数组长度不足的槽按 0）。</summary>
        internal void SetCreatureEvents(int[] spawns, int[] deaths)
        {
            for (int i = 0; i < _creatureSpawns.Length; i++)
            {
                _creatureSpawns[i] = i < spawns.Length ? spawns[i] : 0;
                _creatureDeaths[i] = i < deaths.Length ? deaths[i] : 0;
            }
        }

        /// <summary>把物种事件计数清零（无动物世界时）。</summary>
        internal void ClearCreatureEvents()
        {
            System.Array.Clear(_creatureSpawns, 0, _creatureSpawns.Length);
            System.Array.Clear(_creatureDeaths, 0, _creatureDeaths.Length);
        }
    }

    /// <summary>
    /// 高速模式帧：状态机读数 + 统计快照。消费方（HUD / 结算 / 胜利判定 / 静音闸门）只读它，
    /// 看不到 <see cref="HighSpeedSession"/>（会话是 world 的私有一员）。
    /// </summary>
    public sealed class HighSpeedFrame
    {
        private long[] _births = new long[0];
        private long[] _deaths = new long[0];
        private long[] _finalCounts = new long[0];

        /// <summary>会话是否进行中（进入后、确认退出前恒为 true，结算完成后仍为 true）。</summary>
        public bool Active { get; internal set; }

        /// <summary>是否已跑满时长（<see cref="Active"/> 仍为 true，直到退出）。</summary>
        public bool Completed { get; internal set; }

        /// <summary>已推进的逻辑帧数。</summary>
        public int ElapsedLogicTicks { get; internal set; }

        /// <summary>本次高速模式的时长（逻辑帧）。</summary>
        public int DurationLogicTicks { get; internal set; }

        /// <summary>实测吞吐倍率（EMA 平滑；0 = 还没有有效样本）。</summary>
        public float EffectiveSpeed { get; internal set; }

        /// <summary>最近一次采样的地表平均温度；未采样为 <see cref="float.NaN"/>。</summary>
        public float LatestSurfaceTemperature { get; internal set; }

        /// <summary>最近一次采样的世界平均湿度；未采样为 <see cref="float.NaN"/>。</summary>
        public float LatestAverageHumidity { get; internal set; }

        /// <summary>typeId 的产生数（越界 / 非活动会话为 0）。</summary>
        public long GetBirths(int typeId)
        {
            return (uint)typeId < (uint)_births.Length ? _births[typeId] : 0L;
        }

        /// <summary>typeId 的死亡数（越界 / 非活动会话为 0）。</summary>
        public long GetDeaths(int typeId)
        {
            return (uint)typeId < (uint)_deaths.Length ? _deaths[typeId] : 0L;
        }

        /// <summary>typeId 在跑满时长那一刻的存量格数（越界 / 未结算为 0）。</summary>
        public long GetFinalCount(int typeId)
        {
            return (uint)typeId < (uint)_finalCounts.Length ? _finalCounts[typeId] : 0L;
        }

        /// <summary>准备计数缓冲（按类型表长度一次扩到位，此后零分配复用）。</summary>
        internal void BeginCounts(int typeCount)
        {
            if (_births.Length < typeCount)
            {
                _births = new long[typeCount];
                _deaths = new long[typeCount];
                _finalCounts = new long[typeCount];
            }
        }

        /// <summary>写入一个类型的产生 / 死亡 / 终态存量。</summary>
        internal void SetCount(int typeId, long births, long deaths, long finalCount)
        {
            _births[typeId] = births;
            _deaths[typeId] = deaths;
            _finalCounts[typeId] = finalCount;
        }
    }

    /// <summary>
    /// 一次发布的**完整世界状态快照**：像素 + 世界面向数据 + 实体 + 音频观察 + 高速状态。
    /// 每个字段都带序号（<see cref="Sequence"/>，单调递增）与世代（<see cref="Generation"/>，世界每次重建 +1），
    /// 消费方据此判断「这一帧是不是新的」「世界是不是换了」。
    /// </summary>
    /// <remarks>
    /// 写者只有一个（world 的唯一持有者），且只写「尚未发布」的槽位；已发布的帧在若干次后续发布之内保持不变
    /// （见 <see cref="SimulationFramePublisher"/>）。消费方拿到的永远是完整帧——不存在「半个 tick 的世界」。
    /// </remarks>
    public sealed class SimulationFrame
    {
        /// <summary>发布序号（同一发布者内单调递增，从 1 开始）。</summary>
        public long Sequence { get; internal set; }

        /// <summary>世界世代：每次重建世界 +1。消费方据此丢弃上一世代的视图 / 缓存。</summary>
        public int Generation { get; internal set; }

        /// <summary>上屏像素帧。</summary>
        public RenderFrame Render { get; }

        /// <summary>世界面向帧（尺寸 / tick / 日照 / 暂停 / 探针）。</summary>
        public WorldFrame World { get; }

        /// <summary>实体渲染帧。</summary>
        public CreatureFrame Creatures { get; }

        /// <summary>音频观察帧。</summary>
        public AudioFrame Audio { get; }

        /// <summary>高速模式帧。</summary>
        public HighSpeedFrame HighSpeed { get; }

        internal SimulationFrame(int width, int height)
        {
            Render = new RenderFrame(width, height);
            World = new WorldFrame { Width = width, Height = height };
            Creatures = new CreatureFrame();
            Audio = new AudioFrame();
            HighSpeed = new HighSpeedFrame();
        }
    }
}
