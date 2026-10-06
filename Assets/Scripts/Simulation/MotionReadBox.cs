using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 类型 <c>OnMotion</c> 对 <see cref="ParticleWorld.Cells"/> 的**轴对齐阅读盒**：
    /// 决策时最远读到的格范围 = <c>(x ± X, y − Down .. y + Up)</c>（格，相对本格，含边界）。
    /// </summary>
    /// <remarks>
    /// <para>运动休眠用它算写入唤醒：世界把类型表里所有**反转**后的盒求并集
    /// （读者盒 <c>(X, Up, Down)</c> ⇒ 写入唤醒盒 <c>(X, Up′ = Down, Down′ = Up)</c>），
    /// 写入时就按这个偏移集合置候选位——比「全体最大值的 Chebyshev 球」精确得多
    /// （生产类型表：球是 41×41 = 1681 格，并集只有 ~79 格）。</para>
    /// <para><b>不要把「读温度」算进盒里</b>：温度读距由世界的热脏路径（Q / 湿度变化格周围半径 2 刷子）
    /// 覆盖，与结构读距是两条独立通道。大气 <c>TryThermalDrift</c> 的 <c>x ± 2</c> 只读温度，所以它的盒是
    /// <c>(1, 1, 1)</c>；石头的 <c>TrySlope</c> 读的是净空链上的 `_cells`（结构），所以是 <c>(1, 0, 19)</c>。</para>
    /// <para>声明值必须 ≥ 实际读距（宁大勿小）：偏小会让读它/被它读的格在邻居变化后唤不醒，
    /// 模拟结果与全扫描路径不一致；偏大只是多求值几格。</para>
    /// </remarks>
    public readonly struct MotionReadBox
    {
        /// <summary>各分量的上限（格）。校验用；同时是求并集时布尔表的边长依据。</summary>
        public const byte MaxReach = 32;

        /// <summary>水平半径：读到 <c>x ± X</c>（含）。</summary>
        public readonly byte X;

        /// <summary>向上半径：读到 <c>y + Up</c>（含）。</summary>
        public readonly byte Up;

        /// <summary>向下半径：读到 <c>y − Down</c>（含）。</summary>
        public readonly byte Down;

        public MotionReadBox(byte x, byte up, byte down)
        {
            X = x;
            Up = up;
            Down = down;
        }

        /// <summary>
        /// 保守默认盒：八邻 <c>(1, 1, 1)</c>。`OnMotion` 留空或只做一次相邻判断的类型（草 / 水生植物 / 火）用它。
        /// </summary>
        /// <remarks>
        /// 用属性而非静态字段：`ParticleTypeInfo` 的构造函数参数默认值只能是编译期常量，
        /// 传 <c>default</c>（全 0）时由构造函数替换成本默认盒——全 0 的盒对任何非静态类型都无意义
        /// （它们至少会读本格或一个邻居），因此拿全 0 当「未指定」的哨兵。
        /// </remarks>
        public static MotionReadBox Default
        {
            get { return new MotionReadBox(1, 1, 1); }
        }

        /// <summary>全 0 = 「未指定」，构造函数会把它换成 <see cref="Default"/>。</summary>
        internal bool IsUnspecified
        {
            get { return X == 0 && Up == 0 && Down == 0; }
        }

        /// <summary>把本盒反转成**写入唤醒盒**：读者读到 <c>(x ± X, y − Down .. y + Up)</c>，
        /// 等价于「写在这些格里的任一格都要唤醒它」⇒ 唤醒盒为 <c>(X, Down, Up)</c>（Up / Down 互换）。</summary>
        internal MotionReadBox Reversed
        {
            get { return new MotionReadBox(X, Down, Up); }
        }

        /// <summary>平面打印，仅用于报错信息。</summary>
        public override string ToString()
        {
            return "(" + X + ", " + Up + ", " + Down + ")";
        }
    }
}
