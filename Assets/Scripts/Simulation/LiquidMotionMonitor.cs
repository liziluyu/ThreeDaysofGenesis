using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 液体**运动量**的观察者：世界常驻持有，用 O(1) 增量同时追踪两个量——世界里的液体粒子总数
    /// （<see cref="LiquidCount"/>）与本帧移动过的液体粒子数（<see cref="MovingCount"/>），
    /// 供上层（水的流动声）取二者的**比例**当响度。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么用比例而不是绝对数</b>：绝对移动数随水体大小涨（同一片静止水洼，水面越宽、抖动越多），
    /// 用它当响度会让大水塘无端更响。比例（本帧动了多少 ÷ 一共有多少水）与尺寸无关：静止水洼只有一薄层
    /// 液面在动、比例低；瀑布整片在动、比例高。响度直接由它给出。</para>
    /// <para><b>为什么不用位置 / 方向</b>：液体没有能用来判断「值不值得发声」的位置或方向——当前流体算法
    /// 在平静水面上也会逐格穿行（横向抖动与竖直对流都有），任何按方向 / 按格位的过滤要么把抖动漏进去、
    /// 要么把真流动滤掉。</para>
    /// <para><b>两个量的维护方式</b>：
    /// <see cref="MovingCount"/> 每次 <see cref="ParticleWorld.Swap"/> 后按换位的液体端数累加（每帧由消费方
    /// <see cref="Reset"/>）；<see cref="LiquidCount"/> 由 <see cref="ParticleWorld.Set"/> /
    /// <see cref="ParticleWorld.SetPhase"/> 的旧/新类型差分增量维护（交换两格不改变液体总数，故
    /// <c>Swap</c> 不参与）。都是 O(1)、无分配。</para>
    /// <para><b>纯观察者契约</b>：零 RNG 消耗、零格子写入、零场写入——只读格子数组；读数方
    /// （<see cref="MovingCount"/> / <see cref="LiquidCount"/> / <see cref="MovingRatio"/> /
    /// <see cref="Reset"/>）同样无副作用。因此读不读它，世界与随机流都逐位一致。</para>
    /// </remarks>
    public sealed class LiquidMotionMonitor
    {
        private readonly Cell[] _cells;
        private readonly int _width;

        /// <summary>typeId → 是否带 <see cref="ParticleTags.Liquid"/>。构造时摊表，判定只剩一次数组读。</summary>
        private readonly bool[] _isLiquidByType;

        /// <summary>世界当前液体粒子总数（增量维护：<c>Set</c> / <c>SetPhase</c> 的旧/新类型差分）。</summary>
        private int _liquidCells;

        private long _count;

        /// <param name="world">被观察的世界（只读）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 为 null。</exception>
        internal LiquidMotionMonitor(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _cells = world.Cells;
            _width = world.Width;

            // 分类只看标签、不特判 typeId（新增液体不必改这里）。空气 / 空槽不是液体。
            ParticleType[] types = world.Types;
            var liquid = new bool[types.Length];
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType type = types[i];
                if (type != null)
                {
                    liquid[i] = (type.Info.Tags & ParticleTags.Liquid) != 0;
                }
            }

            _isLiquidByType = liquid;

            // 起点存量：构造时网格刚分配（全空气 ⇒ 0），扫一遍只为不依赖「构造期一定是空的」这个假设。
            int count = 0;
            for (int i = 0; i < _cells.Length; i++)
            {
                if (IsLiquid(_cells[i].typeId))
                {
                    count++;
                }
            }

            _liquidCells = count;
        }

        /// <summary>世界当前液体粒子总数（O(1) 增量维护）。</summary>
        public int LiquidCount
        {
            get { return _liquidCells; }
        }

        /// <summary>自上次 <see cref="Reset"/> 以来移动过的液体粒子数（0 = 本轮没有液体移动）。</summary>
        public long MovingCount
        {
            get { return _count; }
        }

        /// <summary>本轮的**运动比例** = <see cref="MovingCount"/> ÷ <see cref="LiquidCount"/>，夹在 <c>[0, 1]</c>。
        /// 世界里没有液体时返回 0。这就是响度的输入：与水体尺寸无关。</summary>
        public float MovingRatio
        {
            get
            {
                if (_liquidCells <= 0)
                {
                    return 0f;
                }

                float ratio = _count / (float)_liquidCells;
                return ratio > 1f ? 1f : ratio;
            }
        }

        /// <summary>清空本轮移动计数（<see cref="LiquidCount"/> 是存量、不受影响）。读取方每帧读完即清。</summary>
        public void Reset()
        {
            _count = 0L;
        }

        /// <summary>
        /// 两格互换之后调用（<see cref="ParticleWorld.Swap"/>）：按交换后两个格位上的液体数累加运动计数。
        /// 只计运动、不动存量——一次交换不改变液体总数。无方向、无格位过滤。
        /// </summary>
        internal void OnSwap(int x0, int y0, int x1, int y1)
        {
            if (IsLiquid(_cells[y0 * _width + x0].typeId))
            {
                _count++;
            }

            if (IsLiquid(_cells[y1 * _width + x1].typeId))
            {
                _count++;
            }
        }

        /// <summary>
        /// 本格类型刚被换掉（<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> 写完新格之后调用）：
        /// 按旧 / 新类型的液体与否差分维护 <see cref="LiquidCount"/>。
        /// </summary>
        internal void OnCellReplaced(ushort oldId, ushort newId)
        {
            if (IsLiquid(oldId) == IsLiquid(newId))
            {
                return;
            }

            if (IsLiquid(newId))
            {
                _liquidCells++;
            }
            else
            {
                _liquidCells--;
            }
        }

        private bool IsLiquid(ushort typeId)
        {
            return typeId < _isLiquidByType.Length && _isLiquidByType[typeId];
        }
    }
}
