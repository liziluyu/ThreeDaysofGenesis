using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// **带该标签的类型**的运动量观察者：世界常驻持有（液体与固体各一个实例），用 O(1) 增量同时追踪
    /// 两个量——世界里匹配的类型总数（<see cref="TrackedCount"/>）与本帧移动过的匹配格数
    /// （<see cref="MovingCount"/>），供上层（水的流动声、固体的下落声）取二者当响度来源。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么用比例而不是绝对数</b>：绝对移动数随该物质的量涨（同一片静止水洼，水面越宽、
    /// 抖动越多），用它当响度会让大水塘无端更响。比例（本帧动了多少 ÷ 一共有多少）与尺寸无关：
    /// 静止水洼只有一薄层液面在动、比例低；瀑布整片在动、比例高。响度直接由它给出。</para>
    /// <para><b>为什么不用位置 / 方向</b>：物质没有能用来判断「值不值得发声」的位置或方向——当前流体算法
    /// 在平静水面上也会逐格穿行（横向抖动与竖直对流都有），任何按方向 / 按格位的过滤要么把抖动漏进去、
    /// 要么把真流动滤掉。</para>
    /// <para><b>比例的分母是「存量」，对静止成员占多数的标签会被稀释</b>：<see cref="MovingRatio"/>
    /// 除的是该标签的**全部**存量，而不是「可能动的那些」。对水这种「要么静、要么整片在流」的物质，
    /// 分母即水体、比例就是纯粹的流动强度，正好。但对固体（草 / 树干 / 树叶 / 花 / 水生植物常年定植不动），
    /// 存量里绝大多数格子**永远不会动**，比例会被这些静止成员稀释到近 0——哪怕真有沙石在落，比值也低到
    /// 打不开任何死区。因此固体的消费方（下落声）**用绝对 <see cref="MovingCount"/>（真实运动格数）而不是
    /// 比例**；水仍用比例，因为静水塘不该因为大而更吵。同一个观察者、两种读法，差别只在消费方。</para>
    /// <para><b>两个量的维护方式</b>：
    /// <see cref="MovingCount"/> 每次 <see cref="ParticleWorld.Swap"/> 后按换位的匹配端数累加（每帧由消费方
    /// <see cref="Reset"/>）；<see cref="TrackedCount"/> 由 <see cref="ParticleWorld.Set"/> /
    /// <see cref="ParticleWorld.SetPhase"/> 的旧/新类型差分增量维护（交换两格不改变存量，故
    /// <c>Swap</c> 不参与）。都是 O(1)、无分配。</para>
    /// <para><b>纯观察者契约</b>：零 RNG 消耗、零格子写入、零场写入——只读格子数组；读数方
    /// （<see cref="MovingCount"/> / <see cref="TrackedCount"/> / <see cref="MovingRatio"/> /
    /// <see cref="Reset"/>）同样无副作用。因此读不读它，世界与随机流都逐位一致。</para>
    /// </remarks>
    public sealed class MotionMonitor
    {
        private readonly Cell[] _cells;
        private readonly int _width;

        /// <summary>typeId → 是否匹配本观察者的标签掩码。构造时摊表，判定只剩一次数组读。</summary>
        private readonly bool[] _matchesByType;

        /// <summary>世界当前匹配类型总数（增量维护：<c>Set</c> / <c>SetPhase</c> 的旧/新类型差分）。</summary>
        private int _trackedCells;

        private long _count;

        /// <param name="world">被观察的世界（只读）。</param>
        /// <param name="tags">必须**全部**带上的标签（<c>(info.Tags &amp; tags) == tags</c>）。</param>
        /// <param name="excludedTags">带上其中**任何一个**即排除（<c>(info.Tags &amp; excludedTags) == 0</c>）。
        /// 例如固体运动观察者取 <see cref="ParticleTags.Solid"/> + 排除 <see cref="ParticleTags.Organic"/>，
        /// 只留会下落的无机粉末。</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 为 null。</exception>
        internal MotionMonitor(ParticleWorld world, ParticleTags tags, ParticleTags excludedTags = ParticleTags.None)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _cells = world.Cells;
            _width = world.Width;

            // 分类只看标签、不特判 typeId（新增类型不必改这里）。空气 / 空槽 / 越界 id 永远不匹配。
            ParticleType[] types = world.Types;
            var matches = new bool[types.Length];
            for (int i = 1; i < types.Length; i++)
            {
                ParticleType type = types[i];
                if (type != null)
                {
                    ParticleTags typeTags = type.Info.Tags;
                    matches[i] = (typeTags & tags) == tags && (typeTags & excludedTags) == 0;
                }
            }

            _matchesByType = matches;

            // 起点存量：构造时网格刚分配（全空气 ⇒ 0），扫一遍只为不依赖「构造期一定是空的」这个假设。
            int count = 0;
            for (int i = 0; i < _cells.Length; i++)
            {
                if (IsTracked(_cells[i].typeId))
                {
                    count++;
                }
            }

            _trackedCells = count;
        }

        /// <summary>世界当前匹配类型总数（O(1) 增量维护）。</summary>
        public int TrackedCount
        {
            get { return _trackedCells; }
        }

        /// <summary>自上次 <see cref="Reset"/> 以来移动过的匹配格数（0 = 本轮没有匹配的格移动）。</summary>
        public long MovingCount
        {
            get { return _count; }
        }

        /// <summary>本轮的**运动比例** = <see cref="MovingCount"/> ÷ <see cref="TrackedCount"/>，夹在 <c>[0, 1]</c>。
        /// 世界里没有匹配格时返回 0。与存量尺寸无关——对水这类「要么静、要么整片在流」的物质就是响度的输入；
        /// 对静止成员占多数的标签（固体）会被稀释，见类型注释，那种消费方应改用绝对 <see cref="MovingCount"/>。</summary>
        public float MovingRatio
        {
            get
            {
                if (_trackedCells <= 0)
                {
                    return 0f;
                }

                float ratio = _count / (float)_trackedCells;
                return ratio > 1f ? 1f : ratio;
            }
        }

        /// <summary>清空本轮移动计数（<see cref="TrackedCount"/> 是存量、不受影响）。读取方每帧读完即清。</summary>
        public void Reset()
        {
            _count = 0L;
        }

        /// <summary>
        /// 两格互换之后调用（<see cref="ParticleWorld.Swap"/>）：按交换后两个格位上的匹配数累加运动计数。
        /// 只计运动、不动存量——一次交换不改变存量。无方向、无格位过滤（两端各自判一次，两个都匹配就 +2）。
        /// </summary>
        internal void OnSwap(int x0, int y0, int x1, int y1)
        {
            if (IsTracked(_cells[y0 * _width + x0].typeId))
            {
                _count++;
            }

            if (IsTracked(_cells[y1 * _width + x1].typeId))
            {
                _count++;
            }
        }

        /// <summary>
        /// 本格类型刚被换掉（<see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> 写完新格之后调用）：
        /// 按旧 / 新类型的匹配与否差分维护 <see cref="TrackedCount"/>。
        /// </summary>
        internal void OnCellReplaced(ushort oldId, ushort newId)
        {
            if (IsTracked(oldId) == IsTracked(newId))
            {
                return;
            }

            if (IsTracked(newId))
            {
                _trackedCells++;
            }
            else
            {
                _trackedCells--;
            }
        }

        private bool IsTracked(ushort typeId)
        {
            return typeId < _matchesByType.Length && _matchesByType[typeId];
        }
    }
}
