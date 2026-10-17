using System.Collections.Generic;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 模拟命令的种类。命令是**值类型、无闭包、无 UnityEngine.Object 引用**的纯数据，
    /// 因此可以在主线程生成、在下一个 tick 边界按 <see cref="SimulationCommand.Sequence"/> 顺序应用，
    /// 将来也能原样搬给 worker（见 docs/design-threaded-simulation-rendering.md）。
    /// </summary>
    public enum SimulationCommandKind : byte
    {
        /// <summary>以 (X, Y) 为圆心、<see cref="SimulationCommand.Radius"/> 为半径投放粒子。
        /// <see cref="SimulationCommand.Flag"/> = 单颗投放（只写中心格 + 盖树木物种）。</summary>
        PaintDisk = 0,

        /// <summary>以 (X, Y) 为圆心、<see cref="SimulationCommand.Radius"/> 为半径清成空气。</summary>
        EraseDisk = 1,

        /// <summary>在 (X, Y) 格生成一只实体（脚底中心落在该格中心）。<see cref="SimulationCommand.Index"/> = 物种下标。</summary>
        SpawnCreature = 2,

        /// <summary>推进 / 冻结模拟。<see cref="SimulationCommand.Flag"/> = 暂停。</summary>
        SetPaused = 3,

        /// <summary>按 (<see cref="SimulationCommand.Width"/>, <see cref="SimulationCommand.Height"/>) 重建一个空世界。</summary>
        Resize = 4,

        /// <summary>把整个网格清成空气（不改变尺寸），用于播种前的清场。</summary>
        ClearAll = 5,

        /// <summary>把 (X, Y) 起、<see cref="SimulationCommand.Width"/> × <see cref="SimulationCommand.Height"/> 的矩形填成
        /// <see cref="SimulationCommand.TypeId"/>（演示内容播种用）。</summary>
        FillRect = 6,

        /// <summary>应用光照配置（<see cref="SimulationCommand.Light"/>）。</summary>
        SetLightSettings = 7,

        /// <summary>应用热力学 / 随机刻 / 调试叠加配置（<see cref="SimulationCommand.Settings"/>）。</summary>
        SetSimulationSettings = 8,

        /// <summary>请求 (X, Y) 格的探针读数：结果随下一次发布的帧带回（见 <see cref="WorldFrame.Probe"/>）。</summary>
        ProbeCell = 9,

        /// <summary>进入高速模式（幂等；暂停中进入会先解除暂停）。</summary>
        EnterHighSpeed = 10,

        /// <summary>退出高速模式（幂等；倍速钉回 1×，仍保持暂停）。</summary>
        ExitHighSpeed = 11,

        /// <summary>立即发布一帧（暂停中改了世界后手动上屏用）。</summary>
        PresentNow = 12,

        /// <summary>设置后续高速模式的时长（逻辑帧）；<see cref="SimulationCommand.Index"/> 为时长，≤ 0 = 回到设置资产默认值；
        /// 只影响下一次 <see cref="SimulationCommandKind.EnterHighSpeed"/> 创建的会话。</summary>
        SetHighSpeedLogicFrames = 13,
    }

    /// <summary>
    /// 一条模拟命令：值类型、无引用载荷、可自由复制。字段按 <see cref="Kind"/> 分组解释
    /// （同一时刻只有一组有意义）——这样一条命令只有一个固定大小的结构体，队列可以预分配、零装箱。
    /// </summary>
    public struct SimulationCommand
    {
        /// <summary>单调递增的序号，由 <see cref="SimulationCommandQueue.Enqueue"/> 赋值。
        /// 命令日志的顺序即重放 / 确定性测试的权威输入序列。</summary>
        public long Sequence;

        /// <summary>命令种类（决定下面哪些字段有意义）。</summary>
        public SimulationCommandKind Kind;

        /// <summary>格坐标 x / 矩形原点 x。</summary>
        public int X;

        /// <summary>格坐标 y / 矩形原点 y。</summary>
        public int Y;

        /// <summary>目标宽（<see cref="SimulationCommandKind.Resize"/> / <see cref="SimulationCommandKind.FillRect"/>）。</summary>
        public int Width;

        /// <summary>目标高（<see cref="SimulationCommandKind.Resize"/> / <see cref="SimulationCommandKind.FillRect"/>）。</summary>
        public int Height;

        /// <summary>笔刷半径（<see cref="SimulationCommandKind.PaintDisk"/> / <see cref="SimulationCommandKind.EraseDisk"/>）。</summary>
        public int Radius;

        /// <summary>物种下标：树木物种（投放）/ 动物物种（生成）。</summary>
        public int Index;

        /// <summary>粒子类型 id（投放 / 填矩形）。</summary>
        public ushort TypeId;

        /// <summary>布尔载荷：单颗投放（PaintDisk）/ 是否暂停（SetPaused）。</summary>
        public bool Flag;

        /// <summary>光照配置载荷（<see cref="SimulationCommandKind.SetLightSettings"/>）。</summary>
        public LightSettings Light;

        /// <summary>热力学 / 随机刻 / 调试配置载荷（<see cref="SimulationCommandKind.SetSimulationSettings"/>）。</summary>
        public SimulationTickSettings Settings;

        /// <summary>投放粒子：填满以 (cx, cy) 为圆心的实心圆；<paramref name="singlePlacement"/> = 只落中心一颗。</summary>
        /// <param name="speciesIndex">单颗投放树木种子时要盖上物种索引。</param>
        public static SimulationCommand PaintDisk(
            int cx, int cy, int radius, ushort typeId, bool singlePlacement, int speciesIndex)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.PaintDisk,
                X = cx,
                Y = cy,
                Radius = radius,
                TypeId = typeId,
                Flag = singlePlacement,
                Index = speciesIndex,
            };
        }

        /// <summary>擦除：把以 (cx, cy) 为圆心的实心圆清成空气。</summary>
        public static SimulationCommand EraseDisk(int cx, int cy, int radius)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.EraseDisk,
                X = cx,
                Y = cy,
                Radius = radius,
            };
        }

        /// <summary>生成实体：脚底中心落在 (cx + 0.5, cy)。</summary>
        public static SimulationCommand SpawnCreature(int speciesIndex, int cx, int cy)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.SpawnCreature,
                Index = speciesIndex,
                X = cx,
                Y = cy,
            };
        }

        /// <summary>暂停 / 恢复。</summary>
        public static SimulationCommand SetPaused(bool paused)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.SetPaused,
                Flag = paused,
            };
        }

        /// <summary>按新尺寸重建一个空世界（宽高相同也重建——重来要的是空世界）。</summary>
        public static SimulationCommand Resize(int width, int height)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.Resize,
                Width = width,
                Height = height,
            };
        }

        /// <summary>把整个网格清成空气。</summary>
        public static SimulationCommand ClearAll()
        {
            return new SimulationCommand { Kind = SimulationCommandKind.ClearAll };
        }

        /// <summary>填矩形。</summary>
        public static SimulationCommand FillRect(int x, int y, int width, int height, ushort typeId)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.FillRect,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                TypeId = typeId,
            };
        }

        /// <summary>应用光照配置。</summary>
        public static SimulationCommand SetLightSettings(in LightSettings settings)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.SetLightSettings,
                Light = settings,
            };
        }

        /// <summary>应用热力学 / 随机刻 / 调试叠加配置。</summary>
        public static SimulationCommand SetSimulationSettings(in SimulationTickSettings settings)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.SetSimulationSettings,
                Settings = settings,
            };
        }

        /// <summary>请求一格的探针读数。</summary>
        public static SimulationCommand ProbeCell(int x, int y)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.ProbeCell,
                X = x,
                Y = y,
            };
        }

        /// <summary>进入高速模式。</summary>
        public static SimulationCommand EnterHighSpeed()
        {
            return new SimulationCommand { Kind = SimulationCommandKind.EnterHighSpeed };
        }

        /// <summary>退出高速模式。</summary>
        public static SimulationCommand ExitHighSpeed()
        {
            return new SimulationCommand { Kind = SimulationCommandKind.ExitHighSpeed };
        }

        /// <summary>立即发布一帧。</summary>
        public static SimulationCommand PresentNow()
        {
            return new SimulationCommand { Kind = SimulationCommandKind.PresentNow };
        }

        /// <summary>设置后续高速模式的时长（逻辑帧）；≤ 0 = 回到设置资产默认值。</summary>
        public static SimulationCommand SetHighSpeedLogicFrames(int logicFrames)
        {
            return new SimulationCommand
            {
                Kind = SimulationCommandKind.SetHighSpeedLogicFrames,
                Index = logicFrames,
            };
        }
    }

    /// <summary>
    /// 有序命令队列：单生产者（主线程的输入 / UI）入队、单消费者（唯一的 world 写者）在 tick 边界整体抽干。
    /// 入队即定序（<see cref="SimulationCommand.Sequence"/> 单调递增），消费严格按入队顺序——
    /// 因此同一串命令在任何实现下都作用出逐位相同的世界。
    /// </summary>
    public sealed class SimulationCommandQueue
    {
        /// <summary>待应用命令（按入队顺序；抽干时整体清空，不逐条搬移）。</summary>
        private readonly List<SimulationCommand> _pending = new List<SimulationCommand>();

        /// <summary>下一条待取出的下标（前面的都已消费）。</summary>
        private int _head;

        /// <summary>已分配的最大序号。</summary>
        private long _sequence;

        /// <summary>待应用命令数。</summary>
        public int PendingCount
        {
            get { return _pending.Count - _head; }
        }

        /// <summary>已分配的最大序号（= 下一条命令拿到的序号是它 +1）。</summary>
        public long Sequence
        {
            get { return _sequence; }
        }

        /// <summary>入队一条命令并给它钉上单调递增的序号（入队顺序 = 应用顺序）。</summary>
        public void Enqueue(SimulationCommand command)
        {
            command.Sequence = ++_sequence;
            _pending.Add(command);
        }

        /// <summary>按入队顺序取出一条命令；队列空时返回 false。抽干后内部列表整体清空。</summary>
        public bool TryDequeue(out SimulationCommand command)
        {
            if (_head >= _pending.Count)
            {
                command = default(SimulationCommand);
                return false;
            }

            command = _pending[_head++];
            if (_head == _pending.Count)
            {
                _pending.Clear();
                _head = 0;
            }

            return true;
        }

        /// <summary>丢弃全部待应用命令（世界重建等「旧命令不再有意义」的场合用）。</summary>
        public void Clear()
        {
            _pending.Clear();
            _head = 0;
        }
    }
}
