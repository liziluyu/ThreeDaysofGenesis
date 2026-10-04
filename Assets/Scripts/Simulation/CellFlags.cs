namespace TapTap2026.Simulation
{
    /// <summary>
    /// <see cref="Cell.flags"/> 的位定义。禁止在其它文件写裸数字。
    /// </summary>
    public static class CellFlags
    {
        /// <summary>
        /// bit0：本格最后一次被改动的 tick 奇偶。
        /// <see cref="ParticleWorld"/> 独占管理，用于「每格每 tick 至多被处理一次」，
        /// 因此不需要每 tick 做一次 O(N) 清零 pass。类型代码永远不要整体赋值 <c>flags</c>。
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
        /// bit5-6：**草种子私有**（<see cref="GrassSeedParticle"/>）——「被随机刻命中但没有发芽条件」的
        /// 连续计数（0..<see cref="GrassSeedParticle.MaxStrikesBeforeHumus"/>）。与
        /// <see cref="DriftDirectionMask"/> 同一写法：读取用本掩码、清除用
        /// <see cref="SeedStrikeClearMask"/>、写入用 <c>|</c> 组合，绝不整体赋值 <c>flags</c>（尤其保留 bit0）。
        /// 草种子在自身文件头部登记了该占用。
        /// </summary>
        public const byte SeedStrikeMask = 0x60;

        /// <summary><see cref="SeedStrikeMask"/> 的清除掩码（<c>~0x60 &amp; 0xFF</c>）。</summary>
        public const byte SeedStrikeClearMask = 0x9F;

        /// <summary><see cref="SeedStrikeMask"/> 在 <c>flags</c> 里的右移位数。</summary>
        public const int SeedStrikeShift = 5;

        /// <summary>风向记忆：无（尚未漂移，或记忆已被浮力等非水平运动清除）。</summary>
        public const byte DriftDirectionNone = 0;

        /// <summary>风向记忆：向左（−x）。</summary>
        public const byte DriftDirectionLeft = 1;

        /// <summary>风向记忆：向右（+x）。</summary>
        public const byte DriftDirectionRight = 2;

        /// <summary>该格在本 tick（由 <paramref name="stamp"/> 标识）是否已被处理过。</summary>
        public static bool IsProcessed(byte flags, byte stamp)
        {
            return (flags & ProcessedParity) == (stamp & ProcessedParity);
        }

        /// <summary>把 bit0 写成 <paramref name="stamp"/>，其余位原样保留。</summary>
        public static byte WithProcessed(byte flags, byte stamp)
        {
            return (byte)((flags & ProcessedParityMask) | (stamp & ProcessedParity));
        }
    }
}
