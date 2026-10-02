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

        private readonly int _width;
        private readonly int _height;
        private readonly Cell[] _cells;
        private readonly ParticleType[] _types;

        private int _tick;
        private uint _rng;
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

        /// <summary>下一次（或正在进行的）<see cref="Step"/> 的扫描方向奇偶：false = x 升序，true = x 降序。</summary>
        public bool Parity
        {
            get { return (_tick & 1) != 0; }
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
        /// 驱动一次完整网格扫描。与渲染解耦：本方法不接触任何渲染资源，
        /// 由上层决定跑完几次 Step 之后才上屏一次。
        /// </summary>
        public void Step()
        {
            byte stamp = (byte)(_tick & 1);
            _stamp = stamp;

            Cell[] cells = _cells;
            ParticleType[] types = _types;
            int width = _width;
            int height = _height;
            bool reversed = (_tick & 1) != 0;

            try
            {
                for (int y = height - 1; y >= 0; y--)
                {
                    int rowBase = y * width;
                    if (reversed)
                    {
                        for (int x = width - 1; x >= 0; x--)
                        {
                            ProcessCell(x, y, rowBase + x, stamp, cells, types);
                        }
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            ProcessCell(x, y, rowBase + x, stamp, cells, types);
                        }
                    }
                }

                _tick++;
            }
            finally
            {
                // 某个 OnUpdate 抛异常时 _tick 未自增，此处按「旧 tick」恢复写入标记，
                // 使异常后新建的粒子仍会被下一次 Step 处理；正常路径则按自增后的 tick 恢复。
                _stamp = OutsideStamp;
            }
        }

        private void ProcessCell(int x, int y, int index, byte stamp, Cell[] cells, ParticleType[] types)
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
            type.OnUpdate(this, x, y);
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
