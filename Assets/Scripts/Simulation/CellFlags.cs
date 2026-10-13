namespace TapTap2026.Simulation
{
    /// <summary>
    /// <see cref="Cell.flags"/> 的位定义。禁止在其它文件写裸数字。
    /// </summary>
    public static class CellFlags
    {
        /// <summary>
        /// bit0：本格最后一次被改动的 tick 奇偶——**历史标记**，<see cref="ParticleWorld"/> 的写操作负责维护。
        /// 「每格每 tick 至多被处理一次」改由世界内部的逐扫描位图（<c>ParticleWorld._processedBits</c>）判定：
        /// 运动休眠会跳过格，bit0 会残留成与当前 tick 同奇偶的旧值，单靠它判定会漏求值。
        /// 类型代码永远不要整体赋值 <c>flags</c>（会破坏 bit0 与其它位），也不要自己读它判断处理状态。
        /// </summary>
        public const byte ProcessedParity = 0x01;

        /// <summary>保留 bit0 时的掩码。</summary>
        public const byte ProcessedParityMask = 0xFE;

        /// <summary>bit1：燃烧中。类型无关的通用标记。</summary>
        public const byte Burning = 0x02;

        /// <summary>bit2：受外力 / 被推动。类型无关的通用标记。</summary>
        public const byte Forced = 0x04;

        /// <summary>bit3：类型私有。占用它的类型必须在自己文件头部登记。</summary>
        public const byte User3 = 0x08;

        /// <summary>bit4：类型私有。</summary>
        public const byte User4 = 0x10;

        /// <summary>bit5：类型私有。</summary>
        public const byte User5 = 0x20;

        /// <summary>bit6：类型私有。</summary>
        public const byte User6 = 0x40;

        /// <summary>bit7：类型私有。</summary>
        public const byte User7 = 0x80;

        /// <summary>
        /// bit3-4：**大气私有**（<see cref="AtmosphereParticle"/>）——最近一次水平热压漂移的方向记忆。
        /// 只占用这两位：读取用本掩码，清除用 <see cref="DriftDirectionClearMask"/>，写入用
        /// <c>|</c> 组合，绝不整体赋值 <c>flags</c>（尤其保留 bit0 处理标记）。
        /// 大气在自身文件头部登记了该占用。
        /// </summary>
        public const byte DriftDirectionMask = 0x18;

        /// <summary>
        /// 清除 <see cref="DriftDirectionMask"/> 的掩码（<c>~0x18 &amp; 0xFF</c>）。
        /// 用预计算的 byte 常量而非 <c>~DriftDirectionMask</c>：后者在常量折叠时是 int −25，
        /// 显式转 byte 会触发 CS0221（与 <see cref="ProcessedParityMask"/> 同一写法）。
        /// </summary>
        public const byte DriftDirectionClearMask = 0xE7;

        /// <summary><see cref="DriftDirectionMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int DriftDirectionShift = 3;

        /// <summary>
        /// bit5-6：**草种子私有**（<see cref="GrassSeedParticle"/>）——「被随机刻命中但没有发芽条件」的连续计数
        /// （0..<see cref="GrassSeedParticle.MaxStrikesBeforeHumus"/>；草种子的两种发芽去向共用同一个计数）。
        /// 与 <see cref="DriftDirectionMask"/> 同一写法：
        /// 读取用本掩码、清除用 <see cref="SeedStrikeClearMask"/>、写入用 <c>|</c> 组合，绝不整体赋值
        /// <c>flags</c>（尤其保留 bit0）。<see cref="GrassSeedParticle"/> 在文件头部登记了该占用。
        /// <para><b>与 <see cref="GrassAgeRemainderMask"/> 在 bit5-6 重叠</b>（<c>0x60 &amp; 0x78 = 0x60</c>）。
        /// 两组掩码语义互斥（种子的连续计数 vs 草的年龄余数），能共用 bit5-6 的**唯一**依据是
        /// 「一格的私有位只被当前类型解释」：同一时刻一格只属于一个类型，而 <see cref="ParticleWorld.Set"/> /
        /// <see cref="ParticleWorld.SetPhase"/> 换类型时**整字节覆写** <c>flags</c>（<c>cell.flags = _stamp</c>），
        /// 旧类型的私有位随之清零、新类型读不到旧类型残留。因此种子格的 bit5-6 永远是连续计数的高位、
        /// 草格的 bit5-6 永远是年龄余数的高位，二者不会同时有意义。任何**绕过 Set/SetPhase 直接改
        /// <c>typeId</c> 而不整字节覆写 <c>flags</c> 的新路径**都会打破这条前提——届时必须给两者
        /// 分派互不重叠的位。</para>
        /// </summary>
        public const byte SeedStrikeMask = 0x60;

        /// <summary><see cref="SeedStrikeMask"/> 的清除掩码（<c>~0x60 &amp; 0xFF</c>）。</summary>
        public const byte SeedStrikeClearMask = 0x9F;

        /// <summary><see cref="SeedStrikeMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int SeedStrikeShift = 5;

        /// <summary>
        /// bit3-5：**树木（种子 / 树干 / 树叶）私有**——物种编号 0..<see cref="TreeSpecies.Count"/>-1。
        /// 三种树木类型共用同一组位（一格在同一时刻只属于一个类型，与大气 bit3-4、草种子 bit5-6、
        /// **草** bit3-6 的年龄余数同理共享安全）。读用本掩码、清用 <see cref="TreeSpeciesClearMask"/>、
        /// 写用 <c>|</c> 组合，绝不整体赋值 <c>flags</c>（尤其保留 bit0）。三种类型各自在文件头部登记了该占用。
        /// </summary>
        public const byte TreeSpeciesMask = 0x38;

        /// <summary><see cref="TreeSpeciesMask"/> 的清除掩码（<c>~0x38 &amp; 0xFF</c>）。</summary>
        public const byte TreeSpeciesClearMask = 0xC7;

        /// <summary><see cref="TreeSpeciesMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int TreeSpeciesShift = 3;

        /// <summary>
        /// bit6-7：**树木种子私有**——「被随机刻命中但没有发芽条件」的连续计数（0..3）。
        /// 与草种子的 <see cref="SeedStrikeMask"/>（bit5-6）**不是**同一组：树木种子把
        /// bit3-5 让给了物种，计数改用 bit6-7（一格在同一时刻只有一个类型，互不冲突）。
        /// <see cref="TreeTrunkParticle"/> 用同一组位存生长阶段，两者不同时存在。
        /// </summary>
        public const byte TreeSeedStrikeMask = 0xC0;

        /// <summary><see cref="TreeSeedStrikeMask"/> 的清除掩码（<c>~0xC0 &amp; 0xFF</c>）。</summary>
        public const byte TreeSeedStrikeClearMask = 0x3F;

        /// <summary><see cref="TreeSeedStrikeMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int TreeSeedStrikeShift = 6;

        /// <summary>
        /// bit6-7：**树干私有**——生长阶段（0 爬升 / 1 造冠 / 2 成熟，见
        /// <see cref="TreeTrunkParticle.StageGrowing"/> 等）。与树木种子的打击计数同组位、
        /// 不同时存在（一格只有一个类型）。
        /// </summary>
        public const byte TreeStageMask = 0xC0;

        /// <summary><see cref="TreeStageMask"/> 的清除掩码（<c>~0xC0 &amp; 0xFF</c>）。</summary>
        public const byte TreeStageClearMask = 0x3F;

        /// <summary><see cref="TreeStageMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int TreeStageShift = 6;

        /// <summary>
        /// bit6：**树叶私有**——失活标记（0 = 附着在树上、定植不动；1 = 失去支撑、竖直自由下落）。
        /// 与 <see cref="TreeStageMask"/>（树干生长阶段）、<see cref="TreeSeedStrikeMask"/>（树木种子空转计数）
        /// **在 bit6 重叠**：三者都用 bit6-7 这一组，能共用的唯一依据与
        /// <see cref="GrassAgeRemainderMask"/> / <see cref="SeedStrikeMask"/> 共用 bit5-6 相同——「一格的私有位
        /// 只被当前类型解释」（同一时刻一格只属于一个类型；<see cref="ParticleWorld.Set"/> /
        /// <see cref="ParticleWorld.SetPhase"/> 换类型时整字节覆写 <c>flags</c>，旧类型残留读不到）。
        /// 树叶的失活只需要 1 位（bit6），bit7 仍空闲。
        /// 读用本掩码、写用 <c>|</c> 组合，绝不整体赋值 <c>flags</c>（保留 bit0 处理标记）。
        /// <see cref="TreeLeafParticle"/> 在文件头部登记了该占用。
        /// </summary>
        public const byte TreeLeafDetachedMask = 0x40;

        /// <summary>
        /// bit3-6：**草 / 水生植物私有**（<see cref="GrassParticle"/> / <see cref="AquaticPlantParticle"/>）
        /// ——年龄的**逻辑帧余数**（0..<c>TicksPerShadeStep − 1</c>）。
        /// 年龄 = <see cref="Cell.shade"/> × <c>TicksPerShadeStep</c> + 本余数：`shade` 存高档
        /// （它同时就是**颜色档**——渲染只读 `shade` 查表，所以「越老越黄」天然成立），余数存低档。
        /// 两段拼起来才够数到 <c>LifespanTicks</c>（草 2400 / 水草 1200，均 &gt; 255），且不引入新的 per-cell 字段（`Cell` 仍 4 字节）。
        /// 4 位 ⇒ <c>TicksPerShadeStep</c> ≤ 16：草取 10（余数 0..9，用满 4 位）、水草取 5（余数 0..4，只用到低 3 位）。
        /// 两种类型的 `OnLogic` 走同一套衰老算术，区别只在寿终动作：草 <c>Clear</c>（走 20% 有机衰变）、
        /// 水草 <c>SetPhase</c> 还成水。两者**共用**同一组位——依据与下面 bit5-6 的重叠同源：
        /// 一格同一时刻只属于一个类型，换类型时整字节覆写 <c>flags</c>。
        /// 与 <see cref="DriftDirectionMask"/> / <see cref="SeedStrikeMask"/> 同一写法：读取用本掩码、
        /// 清除用 <see cref="GrassAgeRemainderClearMask"/>、写入用 <c>|</c> 组合，绝不整体赋值 <c>flags</c>（保留 bit0）。
        /// <see cref="GrassParticle"/> / <see cref="AquaticPlantParticle"/> 各自在文件头部登记了该占用。
        /// <para><b>与 <see cref="SeedStrikeMask"/> 在 bit5-6 重叠</b>（<c>0x78 &amp; 0x60 = 0x60</c>）。
        /// 两组掩码语义互斥（年龄余数 vs 种子的连续计数），能共用 bit5-6 的**唯一**依据是
        /// 「一格的私有位只被当前类型解释」：同一时刻一格只属于一个类型，而 <see cref="ParticleWorld.Set"/> /
        /// <see cref="ParticleWorld.SetPhase"/> 换类型时**整字节覆写** <c>flags</c>（<c>cell.flags = _stamp</c>），
        /// 旧类型的私有位随之清零、新类型读不到旧类型残留。因此草 / 水草格的 bit5-6 永远是年龄余数的高位、
        /// 种子格的 bit5-6 永远是连续计数的高位，二者不会同时有意义。任何**绕过 Set/SetPhase 直接改
        /// <c>typeId</c> 而不整字节覆写 <c>flags</c> 的新路径**都会打破这条前提——届时必须给它们
        /// 分派互不重叠的位。</para>
        /// </summary>
        public const byte GrassAgeRemainderMask = 0x78;

        /// <summary><see cref="GrassAgeRemainderMask"/> 的清除掩码（<c>~0x78 &amp; 0xFF</c>）。</summary>
        public const byte GrassAgeRemainderClearMask = 0x87;

        /// <summary><see cref="GrassAgeRemainderMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int GrassAgeRemainderShift = 3;

        /// <summary>
        /// bit3-7：**生物质私有**（<see cref="BiomassParticle"/>）——「连续落稳帧数」0..
        /// <see cref="BiomassParticle.SettleFrames"/>（31）。
        /// <para>生物质的 <see cref="Cell.shade"/> 已被**颜色索引**占用（继承动物死亡轮廓的颜色，不重新抽样），
        /// 颜色与「落稳了几帧」不能共用同一个字节，因此落稳计数整段搬进 <c>flags</c> 的 bit3-7，
        /// 而不是新增一份与 <see cref="Cell"/> 平行的世界数组（那会把同步点扩散进
        /// <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> /
        /// <see cref="ParticleWorld.Clear"/> / <see cref="ParticleWorld.Swap"/> 四处）。</para>
        /// 读用本掩码、清用 <see cref="BiomassSettleClearMask"/>、写用 <c>|</c> 组合，
        /// 绝不整体赋值 <c>flags</c>（尤其保留 bit0 处理标记与 bit1 / bit2 通用位）。
        /// <para><b>与 <see cref="GrassAgeRemainderMask"/>（bit3-5）以及
        /// <see cref="TreeStageMask"/> / <see cref="TreeSeedStrikeMask"/> / <see cref="TreeLeafDetachedMask"/>
        /// （bit6-7）重叠</b>：能共用的依据仍是「一格的私有位只被当前类型解释」——同一时刻一格只属于一个类型，
        /// 而 <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.SetPhase"/> 换类型时**整字节覆写**
        /// <c>flags</c>（<c>cell.flags = _stamp</c>），旧类型的私有位随之清零、新类型读不到旧类型残留。
        /// 因此生物质格的 bit3-7 永远是落稳计数、草格的 bit3-5 永远是年龄余数，二者不会同时有意义。
        /// 任何**绕过 Set / SetPhase 直接改 <c>typeId</c> 而不整字节覆写 <c>flags</c> 的新路径**都会打破
        /// 这条前提——届时必须给它们分派互不重叠的位。</para>
        /// </summary>
        public const byte BiomassSettleMask = 0xF8;

        /// <summary><see cref="BiomassSettleMask"/> 的清除掩码（<c>~0xF8 &amp; 0xFF</c>）。</summary>
        public const byte BiomassSettleClearMask = 0x07;

        /// <summary><see cref="BiomassSettleMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int BiomassSettleShift = 3;

        /// <summary>风向记忆：无（尚未漂移，或记忆已被浮力等非水平运动清除）。</summary>
        public const byte DriftDirectionNone = 0;

        /// <summary>风向记忆：向左（−x）。</summary>
        public const byte DriftDirectionLeft = 1;

        /// <summary>风向记忆：向右（+x）。</summary>
        public const byte DriftDirectionRight = 2;

        /// <summary>把 bit0 写成 <paramref name="stamp"/>，其余位原样保留。</summary>
        public static byte WithProcessed(byte flags, byte stamp)
        {
            return (byte)((flags & ProcessedParityMask) | (stamp & ProcessedParity));
        }
    }
}
