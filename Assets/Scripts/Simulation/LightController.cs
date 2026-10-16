using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 光照控制器：一个按时序通断的光源，照亮时向**每一列最上面的非气体粒子**（固体或液体——光打到水面就停）注入热量。
    /// 纯 C#、无 Unity 依赖，由 <see cref="ParticleWorld.StepLogic"/> 每逻辑帧驱动一次（与模拟同频、确定性）。
    /// 每列「挡光顶」与「光路湿度」以增量缓存维护（见 <see cref="Attach"/>），施热因此是 O(宽) 的。
    /// </summary>
    /// <remarks>
    /// <para>通断是**占空比循环**：<see cref="Enabled"/> 打开后，先亮相位持续 <see cref="LitTicks"/> 刻，
    /// 再暗相位持续 <see cref="DarkTicks"/> 刻，如此往复（白天 / 黑夜、呼吸式频闪）。</para>
    /// <list type="bullet">
    /// <item><see cref="LitTicks"/> = 0：从不照亮（只有黑夜）。</item>
    /// <item><see cref="DarkTicks"/> = 0：一直照亮（只有白天）。</item>
    /// <item><see cref="Enabled"/> = false：总开关关闭，不注入热量、相位也不推进。</item>
    /// </list>
    /// <para>照亮时对每个 x 向该列**最上方的固/液格**（增量维护的「挡光顶」）加 <see cref="Intensity"/> 的热量 Q
    /// （不是温度——升温多少还取决于该物质的热容）。光只穿过气体：海洋是表面被晒热、表面蒸发，
    /// 而不是被穿透过热海底。</para>
    /// <para><b>每列缓存**：挡光顶与「光路上的湿度」都是增量维护的——世界在 <c>Set</c> / <c>SetPhase</c>
    /// （类型换掉）与 <c>Swap</c>（两格换位）之后通知本控制器，湿度场在每次真值改动后报增量，因此施热是
    /// O(宽) 的逐列循环，**没有任何竖直扫描**；只有缓存顶格被抹掉时才重扫那一列（罕见）。首次挂到世界上时
    /// 建一次缓存（O(W·H)）。</para>
    /// <para>**材料光照吸收率**：到达格子的能量还要乘上该材料的光照吸收率
    /// <see cref="ParticleTypeInfo.LightAbsorption"/> ∈ [0,1]（1 = 完全吸收；0 = 全反射、光照不升温）。
    /// 用来表达「浅色 / 反光的表面（如草）在阳光下升温更慢」——草取 0.8（略减弱）。</para>
    /// <para>**云会遮光**：施热用每列缓存好的**光路**湿度（该列最上方挡光格之上的湿度总和，
    /// 由每次湿度真值改动后的增量通知维护），
    /// 按 Beer–Lambert 衰减，到达地表的能量是
    /// <c>Intensity × exp(-<see cref="CloudExtinction"/> × 光路湿度)</c>（即 <see cref="GetCloudTransmittance"/>）。
    /// 因此云厚的那一列地表凉、云薄的那一列地表热——这给水循环加了一条负反馈
    /// （云多 → 地表冷 → 蒸发少 → 云少）。<see cref="CloudExtinction"/> = 0 即关掉这条。
    /// 只算光路上的湿度，意味着洞穴 / 顶棚下方的地表不会再被上方云层连带遮黑。</para>
    /// <para>简化：一次性均匀照到整个网格，没有方向、也不按距离或角度衰减。需要阴影 / 衰减再另行扩展。</para>
    /// <para>另导出 <see cref="Daylight"/>（0 = 夜、1 = 昼）给渲染层做整场明暗与天空配色：白天保持上限、
    /// 夜晚保持下限，破晓 / 黄昏各用「下一时段长度的 1/<see cref="DaylightFadeDivisor"/>」刻平滑过渡
    /// （下一段是 600 刻的白天 ⇒ 60 刻淡入）。它与施热无关，只是时序的读数。</para>
    /// </remarks>
    public sealed class LightController
    {
        /// <summary><see cref="CloudExtinction"/> 的默认值（每单位列云量）。</summary>
        public const float DefaultCloudExtinction = 0.05f;

        private bool _enabled;
        private int _litTicks = 60;
        private int _darkTicks = 60;
        private float _intensity = 5f;
        private float _cloudExtinction = DefaultCloudExtinction;
        private ParticleWorld _world;
        private int _elapsed;

        // ── 每列增量缓存 ──
        // 与 ParticleWorld 网格同宽（宽 / 高不变，故一次分配、随世界挂载复用；换世界尺寸不同则重建）。
        private int[] _columnTops;         // 每列**最上方挡光格**（Solid | Liquid）的 y；-1 = 该列全是气体 / 空
        private float[] _columnThickness;  // 每列**光路上**的湿度总量 = Σ humidity(y > _columnTops[x])
        private int _cachedWidth;          // 缓存当前的列数（0 = 尚未建缓存，对应 _world == null）
        private int _cachedHeight;         // 缓存当前的高度（换世界尺寸时用于重建）

        /// <summary>是否光照（总开关）。关闭时不注入热量，相位也不推进。</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>亮相位持续刻数（&gt;= 0）。0 = 从不照亮。</summary>
        public int LitTicks
        {
            get { return _litTicks; }
            set { _litTicks = value < 0 ? 0 : value; }
        }

        /// <summary>暗相位持续刻数（&gt;= 0）。0 = 一直照亮。</summary>
        public int DarkTicks
        {
            get { return _darkTicks; }
            set { _darkTicks = value < 0 ? 0 : value; }
        }

        /// <summary>光照强度：亮相位每刻向每列最上面的非气体粒子注入的热量 Q。NaN / 无穷被归零。</summary>
        public float Intensity
        {
            get { return _intensity; }
            set { _intensity = (float.IsNaN(value) || float.IsInfinity(value)) ? 0f : value; }
        }

        /// <summary>
        /// 云层消光系数（每单位「列云层厚度」）。到达地表的能量按**每列**衰减：
        /// <c>透过率 = exp(-CloudExtinction × 光路上的湿度总量)</c>（Beer–Lambert）。
        /// 0 = 云不遮光（旧行为）。负数 / NaN 归零。
        /// </summary>
        public float CloudExtinction
        {
            get { return _cloudExtinction; }
            set { _cloudExtinction = (float.IsNaN(value) || value < 0f) ? 0f : value; }
        }

        /// <summary>
        /// 第 <paramref name="x"/> 列的**云层厚度** = 光从网格顶竖直下落到第一个固/液格这一段路上
        /// 累加的湿度（只有非固非液格持有湿度，所以就是光程上的云量）。
        /// 缓存建好（见 <see cref="Attach"/>）之后是 O(1) 读；**尚未挂到世界**上（从未 <see cref="Step"/> 过）时
        /// 与旧行为一致返回 0，越界同样返回 0。
        /// </summary>
        public float GetCloudThickness(int x)
        {
            ParticleWorld world = _world;
            if (world == null || (uint)x >= (uint)world.Width)
            {
                return 0f;
            }

            if (_columnTops != null && _cachedWidth == world.Width && _cachedHeight == world.Height)
            {
                return _columnThickness[x];
            }

            // 未挂世界：按需扫描（与缓存建立时同一口径）。
            int width = world.Width;
            HumidityField humidity = world.Humidity;
            float thickness = 0f;
            for (int y = world.Height - 1; y >= 0; y--)
            {
                if (ParticleRules.BlocksLight(world, x, y))
                {
                    break;
                }

                thickness += humidity.GetAt(y * width + x);
            }

            return thickness;
        }

        /// <summary>第 <paramref name="x"/> 列当前的透光率 ∈ (0, 1]：<c>exp(-CloudExtinction × 云层厚度)</c>。
        /// 消光系数为 0 或无云时恒为 1。</summary>
        public float GetCloudTransmittance(int x)
        {
            return CloudTransmittance(GetCloudThickness(x));
        }

        /// <summary>给定光路湿度总量的透光率 ∈ (0, 1]：<c>exp(-CloudExtinction × 厚度)</c>；消光为 0 或无云时恒为 1。</summary>
        private float CloudTransmittance(float thickness)
        {
            if (_cloudExtinction <= 0f || thickness <= 0f)
            {
                return 1f;
            }

            return (float)Math.Exp(-_cloudExtinction * thickness);
        }

        /// <summary>开启后已推进的刻数（只读，用于观测相位）。</summary>
        public int Elapsed
        {
            get { return _elapsed; }
        }

        /// <summary>当前是否处于亮相位（且已打开）。</summary>
        public bool IsLit
        {
            get
            {
                if (!_enabled || _litTicks <= 0)
                {
                    return false;
                }

                if (_darkTicks <= 0)
                {
                    return true;
                }

                return _elapsed % (_litTicks + _darkTicks) < _litTicks;
            }
        }

        /// <summary>
        /// 破晓 / 黄昏的过渡长度 = **下一时段**长度的 1/<see cref="DaylightFadeDivisor"/>。
        /// 例：下一段是 600 刻的白天，破晓就用 60 刻淡入；黄昏的下一段是黑夜，就用 DarkTicks / 10 刻淡出。
        /// </summary>
        public const int DaylightFadeDivisor = 10;

        /// <summary>
        /// 归一化日照度 ∈ [0, 1]：1 = 白天（上限），0 = 夜晚（下限）。
        /// 自动得出：破晓 / 黄昏各用「**下一时段**长度的 1/<see cref="DaylightFadeDivisor"/>」刻平滑过渡，
        /// 其余时间恒定在上限 / 下限。暗相位、<see cref="LitTicks"/> = 0 时恒为 0。
        /// </summary>
        /// <remarks>
        /// 纯导出量：不推进任何状态，也不参与模拟——渲染层靠它把整场像素按亮度缩放（白天上限、夜晚下限）
        /// 并在昼 / 夜背景色之间取色。
        /// 光照关闭（<see cref="Enabled"/> = false）时恒为 1：没有昼夜循环就没有明暗变化。
        /// </remarks>
        public float Daylight
        {
            get
            {
                if (!_enabled)
                {
                    return 1f;
                }

                if (_litTicks <= 0)
                {
                    return 0f;
                }

                if (_darkTicks <= 0)
                {
                    return 1f; // 一直白天，没有「到来 / 结束」可言
                }

                int cycle = _litTicks + _darkTicks;
                int position = _elapsed % cycle;
                if (position >= _litTicks)
                {
                    return 0f; // 暗相位
                }

                int fadeIn = _litTicks / DaylightFadeDivisor;   // 破晓：下一时段是白天
                int fadeOut = _darkTicks / DaylightFadeDivisor; // 黄昏：下一时段是黑夜

                // 夜远长于昼时（如昼 10 / 夜 1000），黄昏的坡道会盖住整个白天——夹到半天以内。
                // 破晓无需夹：LitTicks / 10 恒 <= LitTicks / 2。
                int halfDay = _litTicks / 2;
                if (fadeOut > halfDay)
                {
                    fadeOut = halfDay;
                }

                if (fadeIn > 0 && position < fadeIn)
                {
                    return position / (float)fadeIn; // 破晓：从下限升到上限
                }

                int remaining = _litTicks - position;
                if (fadeOut > 0 && remaining <= fadeOut)
                {
                    return remaining / (float)fadeOut; // 黄昏：从上限降回下限
                }

                return 1f;
            }
        }

        /// <summary>
        /// 把本控制器挂到 <paramref name="world"/> 上并建缓存（O(W·H)，只此一次）。
        /// 由 <see cref="Step"/> 在首次（或换了世界时）调用；缓存建好之后，类型与湿度的每一次改动都由世界 /
        /// 湿度场增量通知（<see cref="OnTypesReplaced"/> / <see cref="OnColumnsSwapped"/> / <see cref="OnHumidityChanged"/>），
        /// <see cref="Step"/> 自身不再做任何扫描。
        /// </summary>
        /// <remarks>
        /// **缓存建立必须基于世界加载完成之后的状态**（<see cref="ParticleWorld"/> 的构造 / <c>Set</c> 都早于
        /// 第一次 <c>StepLogic</c>）。同一世界重复调用是空操作。
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 为 null。</exception>
        public void Attach(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (ReferenceEquals(_world, world) && _columnTops != null)
            {
                return; // 同一世界重复挂载是空操作：缓存一直由增量通知保持精确。
            }

            _world = world;
            int width = world.Width;
            int height = world.Height;
            _cachedWidth = width;
            _cachedHeight = height;
            if (_columnTops == null || _columnTops.Length != width)
            {
                _columnTops = new int[width];
                _columnThickness = new float[width];
            }

            HumidityField humidity = world.Humidity;
            for (int x = 0; x < width; x++)
            {
                float thickness = 0f;
                int top = -1;
                for (int y = height - 1; y >= 0; y--)
                {
                    if (ParticleRules.BlocksLight(world, x, y))
                    {
                        top = y;
                        break;
                    }

                    thickness += humidity.GetAt(y * width + x);
                }

                _columnTops[x] = top;
                _columnThickness[x] = thickness;
            }
        }

        /// <summary>
        /// 世界刚把 (x,y) 的类型由 <paramref name="oldTypeId"/> 换成 <paramref name="newTypeId"/>（写完格子后调用）。
        /// 只维护该列缓存：新格挡光且高过当前顶 ⇒ O(1) 抬顶；原顶被抹成不挡光 ⇒ 罕见地重扫该列（摊销 O(1)）。
        /// 被抬 / 被抹过的新顶之上的光路湿度随之加减，因此这一步是**精确**的，不需要标脏。
        /// </summary>
        public void OnTypesReplaced(ushort oldTypeId, ushort newTypeId, int x, int y)
        {
            if (_columnTops == null || (uint)x >= (uint)_cachedWidth || (uint)y >= (uint)_cachedHeight)
            {
                return;
            }

            bool wasBlocking = BlocksLight(oldTypeId);
            bool nowBlocking = BlocksLight(newTypeId);
            int currentTop = _columnTops[x];

            if (nowBlocking)
            {
                // 新格挡光：只有高于当前顶（或本列原本无顶）才改变「光打到哪一格」，因而只需 O(1) 移位。
                if (currentTop < 0 || y > currentTop)
                {
                    SetTop(x, y);
                }

                return;
            }

            // 新格不挡光：只有它原本就是这一列的顶，才需要重扫（否则顶上那格还在，光路不变）。
            if (wasBlocking && currentTop == y)
            {
                Rescan(x);
            }
        }

        /// <summary>
        /// 世界刚把 (x0,y0) 与 (x1,y1) 两格整体换位（写完格子与湿度之后调用）。
        /// 被移动的挡光格可能改变**光路本身**，故受影响的两列（同行时是同一列，只处理一次）整体重扫；
        /// 两格都不挡光时换位只是把湿度在两格之间搬了家，那由湿度场的增量通知
        /// （<see cref="OnHumidityChanged"/>）按新旧值精确平移，这里无需动作。
        /// </summary>
        public void OnColumnsSwapped(ushort type0, ushort type1, int x0, int y0, int x1, int y1)
        {
            if (_columnTops == null)
            {
                return;
            }

            bool movedBlocker = BlocksLight(type0) || BlocksLight(type1);
            HandleSwapColumn(x0, movedBlocker);
            if (x1 != x0)
            {
                HandleSwapColumn(x1, movedBlocker);
            }
        }

        /// <summary>
        /// 湿度场刚把 <paramref name="index"/> 处的湿度改了 <paramref name="delta"/>（提交后调用）。
        /// 只有该格在**光路上**（高于该列缓存的挡光顶）才平移该列的和——挡光顶及其下方的湿度不参与光路，
        /// 调温器的缓存因此保持精确。越界 / 未挂世界时是空操作。
        /// </summary>
        public void OnHumidityChanged(int index, float delta)
        {
            int width = _cachedWidth;
            if (_columnTops == null || width <= 0 || (uint)index >= (uint)(width * _cachedHeight))
            {
                return;
            }

            int y = index / width;
            int x = index - y * width;

            // 光路 = 挡光顶**之上**（含顶格本身：顶格是气体 / 空时它也在光路上；挡光格自身湿不参与）。
            if (y > _columnTops[x])
            {
                _columnThickness[x] += delta;
            }
        }

        /// <summary>换位后单列的缓存维护。<paramref name="movedBlocker"/> 为 true（任一换位格挡光）时无法从增量推出新光路，重扫。</summary>
        private void HandleSwapColumn(int x, bool movedBlocker)
        {
            // 有挡光格参与换位：光路本身可能改变（谁挡光、挡光顶之上有哪些湿度都变了），增量推不出来 ⇒ 重扫该列。
            // 两格都不挡光时换位只是把湿度在两格之间搬了家 —— 那由湿度场的换位通知（<see cref="OnHumidityChanged"/>）
            // 按新旧值精确平移，这里无需动作。
            if (movedBlocker)
            {
                Rescan(x);
            }
        }

        /// <summary>
        /// 把该列的挡光顶抬 / 降到 <paramref name="y"/>：只结算**原顶与新顶之间**那几格对光路的影响
        /// （光路是「顶之上」的集合，两个集合的差就是这一段），因此抬顶 / 降顶都不是整列重扫。
        /// </summary>
        private void SetTop(int x, int y)
        {
            ParticleWorld world = _world;
            int oldTop = _columnTops[x];
            float thickness = _columnThickness[x];

            if (y > oldTop)
            {
                // 新顶更高（含原本无顶的 -1）：原顶与它之间（含新顶自身）原本在光路上，现在被挡在外面。
                for (int h = oldTop + 1; h <= y; h++)
                {
                    thickness -= world.Humidity.GetAt(h * world.Width + x);
                }
            }
            else
            {
                // 新顶更低：新顶与旧顶之间（含旧顶——挡光格不持湿度，那一项恒为 0）重新回到光路上。
                for (int h = y + 1; h <= oldTop; h++)
                {
                    thickness += world.Humidity.GetAt(h * world.Width + x);
                }
            }

            _columnTops[x] = y;
            _columnThickness[x] = thickness;
        }

        /// <summary>重扫第 <paramref name="x"/> 列：从顶向下找最上方挡光格，并累加它之上的湿度（挡光顶被抹掉时才会走到）。</summary>
        private void Rescan(int x)
        {
            ParticleWorld world = _world;
            if (world == null)
            {
                return;
            }

            int width = world.Width;
            HumidityField humidity = world.Humidity;
            int top = -1;
            float thickness = 0f;
            for (int y = world.Height - 1; y >= 0; y--)
            {
                if (ParticleRules.BlocksLight(world, x, y))
                {
                    top = y;
                    break;
                }

                thickness += humidity.GetAt(y * width + x);
            }

            _columnTops[x] = top;
            _columnThickness[x] = thickness;
        }

        /// <summary>typeId 是否挡光（Solid / Liquid）。空气与越界 id 为 false——写入路径上只有这些 id 会出现。</summary>
        private bool BlocksLight(ushort typeId)
        {
            if (typeId == ParticleRegistry.AirTypeId || _world == null)
            {
                return false;
            }

            ParticleType[] types = _world.Types;
            if (typeId >= types.Length)
            {
                return false;
            }

            ParticleType type = types[typeId];
            return type != null && (type.Info.Tags & (ParticleTags.Solid | ParticleTags.Liquid)) != 0;
        }

        /// <summary>把相位拨回起点（下一个逻辑帧从亮相位开始）。不触碰每列缓存——缓存描述的是**世界**的状态。</summary>
        public void Reset()
        {
            _elapsed = 0;
        }

        /// <summary>
        /// 推进一逻辑帧：照亮时向每列最上面的非气体粒子（固体或液体）注入按**光路云量**衰减后的
        /// <see cref="Intensity"/> 热量。
        /// 由 <see cref="ParticleWorld.StepLogic"/> 调用；正常玩法不要手动调用，否则会额外施热、破坏确定性。
        /// </summary>
        public void Step(ParticleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            Attach(world); // 首帧建缓存（O(W·H)），此后是空操作。

            if (!_enabled)
            {
                return;
            }

            if (IsLit && _intensity != 0f)
            {
                ApplyHeat(world, _intensity);
            }

            _elapsed++;
        }

        /// <summary>
        /// (x,y) 材料的光照吸收率（<see cref="ParticleTypeInfo.LightAbsorption"/>）：光照注入本格时按它缩放。
        /// 空气 / 越界 / 空槽一律返回 1（空气不是光程终点，正常不会走到这里）。
        /// </summary>
        private static float LightAbsorptionAt(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            if (id == ParticleRegistry.AirTypeId)
            {
                return 1f;
            }

            ParticleType[] types = world.Types;
            if (id >= types.Length || types[id] == null)
            {
                return 1f;
            }

            return types[id].Info.LightAbsorption;
        }

        /// <summary>
        /// 施热：O(宽) 的逐列循环，**没有竖直扫描**——每列的挡光顶与光路湿度直接读缓存。
        /// 该列没有挡光格（缓存顶 = -1）时不注入热量（与旧行为一致：不透明的空列收不到光照）。
        /// </summary>
        private void ApplyHeat(ParticleWorld world, float amount)
        {
            HeatMap heat = world.Heat;
            int width = world.Width;
            int[] tops = _columnTops;
            float[] thickness = _columnThickness;

            for (int x = 0; x < width; x++)
            {
                int y = tops[x];
                if (y < 0)
                {
                    continue;
                }

                // 云层遮蔽：光路湿度取缓存（= 该列挡光顶之上的湿度总和），按 Beer–Lambert 衰减
                // （透过率 = exp(-k × 光路湿度)）。洞内 / 顶棚下方的湿度不在光路上，因此不遮光。
                // 再按本格材料的光照吸收率缩放（浅色 / 反光表面升温更慢；草用 0.8）。
                float reach = amount * CloudTransmittance(thickness[x]) * LightAbsorptionAt(world, x, y);
                if (reach > 0f)
                {
                    heat.AddHeat(x, y, reach);
                }
            }
        }
    }
}
