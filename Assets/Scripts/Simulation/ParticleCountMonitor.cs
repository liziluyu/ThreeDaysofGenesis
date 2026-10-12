using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 粒子**存量**的观察者：世界常驻持有，用 O(1) 增量同时追踪两个量——每种类型的世界存量
    /// （<see cref="Count"/>）与自上次读取以来新写入的格数（<see cref="ConsumeCreated"/>）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不用逐帧全网格扫描</b>：存量消费方（火焰持续声一类）每帧都要读，全网格扫一遍
    /// 是 O(格数)；而类型改变只发生在 <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/>
    /// 两个写入原语里，每次只差一格。把差分放在写入点，读取侧就退化成一次数组读——热路径零分配、
    /// 与网格尺寸无关。</para>
    /// <para><b>为什么 <c>Swap</c> 不参与</b>：两格互换不改变任何类型的存量（甲格变乙、乙格变甲，
    /// 两端类型都没变），也不产生「新格」，故既不改存量也不算新增。</para>
    /// <para><b>为什么空气永不计数</b>：空气（typeId 0 / <see cref="ParticleRegistry.AirTypeId"/>）
    /// 是「空」而不是一种物质，把整张空网格算进存量只会让每种用途都要减去一个巨量常数；
    /// 下标 0 的槽因此永远是 0，<see cref="Count"/> / <see cref="ConsumeCreated"/> 对空气自然返回 0。</para>
    /// <para><b>纯观察者契约</b>：零 RNG 消耗、零格子写入、零场写入——本类只读 <see cref="ParticleWorld.Cells"/>
    /// 与类型表；<see cref="Count"/> / <see cref="ConsumeCreated"/> 也无副作用（后者只清自己的
    /// <c>created</c> 槽，不碰存量、不碰别的类型）。因此读不读它，世界与随机流都逐位一致。</para>
    /// <para><b>越界口径</b>：<c>typeId &gt;= world.Types.Length</c> 时读返回 0、写忽略——不得抛异常。
    /// 调用方（测试 / 表现层）可能拿着旧类型表的下标来问，抛异常会把一个无害的查询变成崩溃。</para>
    /// </remarks>
    public sealed class ParticleCountMonitor
    {
        /// <summary>typeId → 当前存量。下标 0（空气）恒为 0。</summary>
        private readonly int[] _stock;

        /// <summary>typeId → 自上次 <see cref="ConsumeCreated"/> 以来新写入的格数。下标 0（空气）恒为 0。</summary>
        private readonly int[] _created;

        /// <param name="world">被观察的世界（只读）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 为 null。</exception>
        internal ParticleCountMonitor(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            ParticleType[] types = world.Types;
            _stock = new int[types.Length];
            _created = new int[types.Length];

            // 起点存量：构造期网格通常刚分配（全空气 ⇒ 0），扫一遍只为不依赖「构造期一定是空的」这个假设——
            // 将来若有「从存档 / 布局直接填充格子数组再交给监视器」的构造路径，这里才是对的。
            Cell[] cells = world.Cells;
            int[] stock = _stock;
            for (int i = 0; i < cells.Length; i++)
            {
                ushort id = cells[i].typeId;
                if (id != ParticleRegistry.AirTypeId && id < stock.Length)
                {
                    stock[id]++;
                }
            }
        }

        /// <summary>
        /// 该类型当前的世界存量（O(1) 增量维护）。空气或越界 typeId 返回 0。纯读，无副作用。
        /// </summary>
        public int Count(ushort typeId)
        {
            return typeId < _stock.Length ? _stock[typeId] : 0;
        }

        /// <summary>
        /// 自上次对本类型调用本方法以来**新写入**的格数：读后清零，且只清本类型的槽。
        /// 存量（<see cref="Count"/>）不受影响；空气或越界 typeId 返回 0。用于「这一帧新冒出了几格火」这类
        /// 一次性事件统计——由消费方决定读取节奏。
        /// </summary>
        public int ConsumeCreated(ushort typeId)
        {
            if (typeId >= _created.Length)
            {
                return 0;
            }

            int created = _created[typeId];
            _created[typeId] = 0;
            return created;
        }

        /// <summary>
        /// 本格类型刚被换掉（<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> 写完新格
        /// 之后调用）时按旧 / 新类型做 O(1) 差分：旧类型存量 −1、新类型存量 +1 且新增计数 +1（两端都跳过空气
        /// 与越界 id）。交换两格不改变任何存量，故 <see cref="ParticleWorld.Swap"/> 不调用本方法。
        /// </summary>
        internal void OnCellReplaced(ushort oldId, ushort newId)
        {
            int[] stock = _stock;
            if (oldId != ParticleRegistry.AirTypeId && oldId < stock.Length)
            {
                stock[oldId]--;
            }

            if (newId != ParticleRegistry.AirTypeId && newId < stock.Length)
            {
                stock[newId]++;
                _created[newId]++;
            }
        }
    }
}
