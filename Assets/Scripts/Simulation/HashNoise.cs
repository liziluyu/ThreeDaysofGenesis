namespace TapTap2026.Simulation
{
    /// <summary>
    /// 无状态的 (x, y, tick) 坐标哈希。<c>OnMotion</c> 路径上的「随机决策」（左右顺序等）一律走它，
    /// **不再消耗世界随机流**（<see cref="ParticleWorld.NextUInt"/>）：只有纯函数哈希才能保证
    /// 「本帧哪些粒子被求值」不影响其它粒子的决策——运动休眠跳过静止粒子后，确定性契约
    /// （同种子同命令逐格可复现）仍然成立。<c>OnLogic</c> / <c>OnRandomTick</c> 仍用世界流。
    /// </summary>
    /// <remarks>
    /// <see cref="Hash32"/> 的混合函数就是从 <see cref="FireParticle.OnLogic"/> 的火焰哈希
    /// （shade 抖动与游离火摇曳）原样提取的：<c>salt = 0</c> 时逐位不变，火焰行为因此不受本次改动影响。
    /// 不同调用点传不同 salt 即得互不相关的流。热路径纯整数运算、零分配。
    /// </remarks>
    internal static class HashNoise
    {
        private const uint XPrime = 73856093u;
        private const uint YPrime = 19349663u;
        private const uint TickPrime = 83492791u;

        /// <summary>salt 的折叠常量（黄金比例奇数），把 salt 摊到整个 32 位；salt = 0 时为恒等。</summary>
        private const uint SaltPrime = 2654435761u;

        // Murmur3 fmix32 的两个奇数乘子，只在 Bit() 的雪崩里用（见 Bit 的说明）。
        private const uint AvalanchePrimeA = 0x85EBCA6Bu;
        private const uint AvalanchePrimeB = 0xC2B2AE35u;

        /// <summary>
        /// (x, y, tick) 的 32 位坐标哈希。<paramref name="salt"/> = 0 时与火焰 v2 的历史哈希逐位相同。
        /// tick 每步（运动或逻辑帧）自增，同一格的哈希逐帧变化。
        /// </summary>
        public static uint Hash32(int x, int y, int tick, uint salt)
        {
            uint spatial = (uint)x * XPrime ^ (uint)y * YPrime;
            uint noise = spatial ^ (uint)tick * TickPrime;
            return noise ^ salt * SaltPrime;
        }

        /// <summary>
        /// 取一个 50/50 的方向位：<c>true</c> = 先朝负方向（左右语义由调用点定义）。
        /// </summary>
        /// <remarks>
        /// 不能直接取 <see cref="Hash32"/> 的 bit0：三个乘子都是奇数，乘法不进位到低位，
        /// bit0 只等于 <c>x ^ y ^ tick</c> 的奇偶——直接用会让方向按对角奇偶分片（肉眼可见的偏置）。
        /// 因此先跑一轮 Murmur3 fmix32 雪崩（双射），把高位携带的坐标信息铺回低位，再取 bit0。
        /// </remarks>
        public static bool Bit(int x, int y, int tick, uint salt)
        {
            uint h = Hash32(x, y, tick, salt);
            h ^= h >> 16;
            h *= AvalanchePrimeA;
            h ^= h >> 13;
            h *= AvalanchePrimeB;
            h ^= h >> 16;
            return (h & 1u) == 0u;
        }
    }
}
