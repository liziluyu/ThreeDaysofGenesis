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
