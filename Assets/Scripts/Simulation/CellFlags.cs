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
        /// bit5-6：**草种子 / 水生态种子私有**——「被随机刻命中但没有发芽条件」的连续计数
        /// （0..<see cref="GrassSeedParticle.MaxStrikesBeforeHumus"/>）。两种种子**共用**同一组位
        /// （一格在同一时刻只属于一个类型，互不冲突）。与 <see cref="DriftDirectionMask"/> 同一写法：
        /// 读取用本掩码、清除用 <see cref="SeedStrikeClearMask"/>、写入用 <c>|</c> 组合，绝不整体赋值
        /// <c>flags</c>（尤其保留 bit0）。<see cref="GrassSeedParticle"/> 与 <see cref="AquaticSeedParticle"/>
        /// 各自在文件头部登记了该占用。
        /// </summary>
        public const byte SeedStrikeMask = 0x60;

        /// <summary><see cref="SeedStrikeMask"/> 的清除掩码（<c>~0x60 &amp; 0xFF</c>）。</summary>
        public const byte SeedStrikeClearMask = 0x9F;

        /// <summary><see cref="SeedStrikeMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int SeedStrikeShift = 5;

        /// <summary>
        /// bit3-5：**树木（种子 / 树干 / 树叶）私有**——物种编号 0..<see cref="TreeSpecies.Count"/>-1。
        /// 三种树木类型共用同一组位（一格在同一时刻只属于一个类型，与大气 bit3-4、草种子 bit5-6
        /// 同理共享安全）。读用本掩码、清用 <see cref="TreeSpeciesClearMask"/>、写用 <c>|</c> 组合，
        /// 绝不整体赋值 <c>flags</c>（尤其保留 bit0）。三种类型各自在文件头部登记了该占用。
        /// </summary>
        public const byte TreeSpeciesMask = 0x38;

        /// <summary><see cref="TreeSpeciesMask"/> 的清除掩码（<c>~0x38 &amp; 0xFF</c>）。</summary>
        public const byte TreeSpeciesClearMask = 0xC7;

        /// <summary><see cref="TreeSpeciesMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int TreeSpeciesShift = 3;

        /// <summary>
        /// bit6-7：**树木种子私有**——「被随机刻命中但没有发芽条件」的连续计数（0..3）。
        /// 与草种子 / 水生态种子的 <see cref="SeedStrikeMask"/>（bit5-6）**不是**同一组：树木种子把
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
