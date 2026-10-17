using System;
using System.Threading;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 帧环发布器：<see cref="SlotCount"/> 个预分配槽位（写 / 显示 / 空闲），把「模拟产出」与「主线程消费」
    /// 之间隔出一层**显式所有权**——消费方永远只能拿到已发布的完整帧，拿不到活世界，也拿不到正在被写的槽。
    /// </summary>
    /// <remarks>
    /// <para><b>所有权规则</b>：唯一写者只写 <see cref="BeginWrite"/> 返回的那个槽，写完后必须
    /// <see cref="Publish"/> 才对外可见；<see cref="Current"/> 在每次发布后指向新槽。因为写指针只在发布时前进一格，
    /// 而槽数 ≥ 2，所以**正在写的槽永远不是当前显示的槽**；槽数 3 意味着一个已发布的帧在它被复用之前，
    /// 至少还要再发布 2 次——也就是「发布帧在两次后续发布之内保持不变」，这正是「先发布、后突变」的确定性测试所依赖的松弛度。</para>
    /// <para><b>尺寸变更</b>：像素数组按尺寸分配，**绝不跨尺寸复用**；世界重建时整体换一个新的发布器
    /// （见 <c>SimulationWorker</c>），旧帧从此不再更新。</para>
    /// <para><b>发布 / 获取语义（阶段 3）</b>：写者是 worker 线程、读者是主线程，两者之间只有一条显式的
    /// 发布 / 获取通道——<see cref="Publish"/> 用 <see cref="Volatile.Write{T}(ref T, T)"/>（release）把刚写完的槽
    /// 设为当前帧，<see cref="Current"/> 用 <see cref="Volatile.Read{T}(ref T)"/>（acquire）取得它。
    /// 因此读者**要么看到上一帧、要么看到完整的新帧**，绝不会看到「写到一半的帧」；
    /// 写指针与序号是写线程私有状态，读者一概不碰（<see cref="Sequence"/> 随帧一起通过同一道 release 屏障可见）。</para>
    /// </remarks>
    public sealed class SimulationFramePublisher
    {
        /// <summary>槽位数（写 / 显示 / 空闲）：≥ 3 才能保证已发布帧在复用前至少还有 2 次发布的松弛。</summary>
        public const int SlotCount = 3;

        private readonly SimulationFrame[] _slots = new SimulationFrame[SlotCount];

        /// <summary>当前已发布的帧；null = 还没发布过任何帧。**只经 <see cref="Volatile"/> 读写**（发布 / 获取语义）。</summary>
        private SimulationFrame _current;

        /// <summary>下一个要写的槽下标（只在 <see cref="Publish"/> 时前进）。**写线程私有**。</summary>
        private int _writeIndex;

        /// <summary>已发布的帧数（即最新帧的 <see cref="SimulationFrame.Sequence"/>）。**写线程私有**。</summary>
        private long _sequence;

        /// <summary>世界世代号（写进此后发布的每一帧）。</summary>
        private readonly int _generation;

        /// <param name="width">网格宽（格 / 像素），至少 1。</param>
        /// <param name="height">网格高（格 / 像素），至少 1。</param>
        /// <param name="generation">世界世代号（每次重建世界 +1），写进每一帧供消费方识别换世界。</param>
        /// <exception cref="ArgumentOutOfRangeException">宽或高小于 1。</exception>
        public SimulationFramePublisher(int width, int height, int generation)
        {
            if (width < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = new SimulationFrame(width, height);
            }

            _generation = generation;
        }

        /// <summary>网格宽（格 / 像素）。</summary>
        public int Width
        {
            get { return _slots[0].World.Width; }
        }

        /// <summary>网格高（格 / 像素）。</summary>
        public int Height
        {
            get { return _slots[0].World.Height; }
        }

        /// <summary>世界世代号（会写进此后发布的每一帧）。</summary>
        public int Generation
        {
            get { return _generation; }
        }

        /// <summary>是否已发布过帧（在此之前 <see cref="Current"/> 为 null）。</summary>
        public bool HasPublished
        {
            get { return Volatile.Read(ref _current) != null; }
        }

        /// <summary>最新已发布的完整帧；还没发布过任何帧时为 null。消费方只读它（acquire 语义）。</summary>
        public SimulationFrame Current
        {
            get { return Volatile.Read(ref _current); }
        }

        /// <summary>取得本轮的写入槽（唯一写者可写它）。**只有写完并 <see cref="Publish"/> 之后**它才对外可见。</summary>
        public SimulationFrame BeginWrite()
        {
            return _slots[_writeIndex];
        }

        /// <summary>发布刚才 <see cref="BeginWrite"/> 取到的槽：序号 +1、世代号对齐，然后 release 地把写指针交给读者。</summary>
        public void Publish()
        {
            SimulationFrame frame = _slots[_writeIndex];
            frame.Sequence = ++_sequence;
            frame.Generation = _generation;

            // release：此前对帧内容的全部写入，对任何先看到这一帧的线程都可见（见类注释）。
            Volatile.Write(ref _current, frame);

            _writeIndex = _writeIndex + 1 == SlotCount ? 0 : _writeIndex + 1;
        }
    }
}
