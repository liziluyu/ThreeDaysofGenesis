using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 高速模式的**纯观察者**：由 <see cref="HighSpeedSession.Enter"/> 挂到 <see cref="ParticleWorld"/> 的
    /// 监视器挂点，在世界每次改动格子时增量记账——每种粒子的产生 / 死亡数（O(1)）与每列地表高度（O(1) 摊销）。
    /// </summary>
    /// <remarks>
    /// <para><b>确定性契约</b>：监视器零 RNG 消耗、零格子写入、零场写入——它只读格子数组，
    /// 因此挂 / 摘监视器前后，同种子同命令序列推进出的世界逐位一致（回归见 <c>HighSpeedSessionTests</c>）。</para>
    /// <para><b>地表判据与光照同款</b>：<c>TagRank(tags) &gt;= LiquidRank</c>（Solid / Liquid 挡光即算地表），
    /// 只看标签、不特判 typeId。标签在运行期不可变（见 docs §12「明确禁止」），故构造时摊成
    /// <c>bool[]</c> 查找表，写钩子上只剩一次数组读。</para>
    /// <para><b>计数语义</b>：<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> /
    /// <see cref="ParticleWorld.Clear"/> 里 old ≠ new 才计一次死亡 + 一次产生（空气也是一条计数项，
    /// 显示时过滤）；<see cref="ParticleWorld.Swap"/> 只是搬运、**不计数**，只更新两端列的地表高度。</para>
    /// <para>热路径零分配：所有缓冲在构造时一次性分配，钩子只有数组读写与整数比较。</para>
    /// </remarks>
    internal sealed class HighSpeedMonitor
    {
        private readonly Cell[] _cells;
        private readonly int _width;
        private readonly int _height;

        /// <summary>typeId → 是否挡光（Solid / Liquid）。构造时摊表，写钩子上一次数组读。</summary>
        private readonly bool[] _nonGasByType;

        /// <summary>每列最上方非气体格的 y；<c>-1</c> = 该列全是气体 / 空。</summary>
        private readonly int[] _surfaceTop;

        private readonly long[] _births;
        private readonly long[] _deaths;

        /// <param name="world">被观察的世界（只读）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 为 null。</exception>
        public HighSpeedMonitor(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _cells = world.Cells;
            _width = world.Width;
            _height = world.Height;

            ParticleType[] types = world.Types;
            var nonGas = new bool[types.Length];
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType type = types[i];
                nonGas[i] = type != null && ParticleRules.TagRank(type.Info.Tags) >= ParticleRules.LiquidRank;
            }

            _nonGasByType = nonGas;
            _surfaceTop = new int[world.Width];
            _births = new long[types.Length];
            _deaths = new long[types.Length];

            // 挂上来的这一刻全列扫一遍，把地表高度拉成真值（此后全靠增量维护）。
            for (int x = 0; x < _width; x++)
            {
                _surfaceTop[x] = ScanColumn(x, _height - 1);
            }
        }

        /// <summary>
        /// 本格类型刚被换掉（写完新格之后调用）：old ≠ new 时记一次死亡 + 一次产生，并更新该列地表高度。
        /// </summary>
        public void OnCellReplaced(ushort oldId, ushort newId, int x, int y)
        {
            if (oldId != newId)
            {
                if (oldId < _deaths.Length)
                {
                    _deaths[oldId]++;
                }

                if (newId < _births.Length)
                {
                    _births[newId]++;
                }
            }

            UpdateColumn(x, y, newId);
        }

        /// <summary>
        /// 两格互换之后调用：**不计数**（移动不是生灭），只把两端列的地表高度按交换后的真实内容拉回真值。
        /// </summary>
        public void OnSwap(int x0, int y0, int x1, int y1)
        {
            UpdateColumn(x0, y0, _cells[y0 * _width + x0].typeId);
            UpdateColumn(x1, y1, _cells[y1 * _width + x1].typeId);
        }

        /// <summary>typeId 的产生数（空气 = 0 也计数；越界返回 0）。</summary>
        public long GetBirths(int typeId)
        {
            return (uint)typeId < (uint)_births.Length ? _births[typeId] : 0L;
        }

        /// <summary>typeId 的死亡数（空气 = 0 也计数；越界返回 0）。</summary>
        public long GetDeaths(int typeId)
        {
            return (uint)typeId < (uint)_deaths.Length ? _deaths[typeId] : 0L;
        }

        /// <summary>该列最上方非气体格的 y；<c>-1</c> = 该列全是气体 / 空。只读，越界返回 -1。</summary>
        public int SurfaceTop(int x)
        {
            return (uint)x < (uint)_width ? _surfaceTop[x] : -1;
        }

        /// <summary>清零产生 / 死亡计数（<see cref="HighSpeedSession.Exit"/> 摘监视器时调用）。</summary>
        public void ResetCounters()
        {
            Array.Clear(_births, 0, _births.Length);
            Array.Clear(_deaths, 0, _deaths.Length);
        }

        /// <summary>
        /// 单格类型变化后的地表维护：本格是固体 / 液体且高过当前地表就抬升（O(1)）；
        /// 本格原是地表又变成气体 / 空就向下重扫该列（罕见，摊销 O(1)）。
        /// </summary>
        private void UpdateColumn(int x, int y, ushort typeId)
        {
            if (IsNonGas(typeId))
            {
                if (y > _surfaceTop[x])
                {
                    _surfaceTop[x] = y;
                }

                return;
            }

            if (y == _surfaceTop[x])
            {
                _surfaceTop[x] = ScanColumn(x, y - 1);
            }
        }

        /// <summary>从 <paramref name="fromY"/> 起沿 −y 找该列最上方的非气体格；找不到返回 -1。</summary>
        private int ScanColumn(int x, int fromY)
        {
            for (int y = fromY; y >= 0; y--)
            {
                if (IsNonGas(_cells[y * _width + x].typeId))
                {
                    return y;
                }
            }

            return -1;
        }

        /// <summary>typeId 是否挡光（Solid / Liquid）；空气、空槽一律 false。一次数组读，无分配。</summary>
        private bool IsNonGas(ushort typeId)
        {
            return typeId < _nonGasByType.Length && _nonGasByType[typeId];
        }
    }
}
