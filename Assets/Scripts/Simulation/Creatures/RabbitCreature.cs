using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 兔子：陆生物种，动物层的**验证体**。碰撞盒 1.5 × 1 格（比一格宽的兔子跨两列，能验证
    /// 覆盖列判定的正确性），受重力、贴地、入水缓慢下沉。
    /// </summary>
    /// <remarks>
    /// <para><b>时间尺度：</b>空中运动（<see cref="Gravity"/> / <see cref="MaxFallSpeed"/>）按 3 倍于初版的
    /// 速率配置，落体因此快 3 倍而**运动形状不变**（两者同比缩放，达到终端速度所需的帧数不变）；
    /// 水中速率经 <see cref="Buoyancy"/> 重算后与初版逐位一致。改重力时这两个常数要一起动——
    /// 只提重力会让终端速度成为真正的上限、看不出效果。</para>
    /// <para>本阶段**纯物理、零行为**：不覆写任何钩子（<see cref="Creature.OnSpawn"/> /
    /// <see cref="Creature.OnLogic"/> / <see cref="Creature.OnDeath"/> 全用基类空体），
    /// 因此兔子的运动完全由 <see cref="Creature.StepPhysics"/> + 重力决定。
    /// 移动 / 觅食 / 繁殖留给后续阶段——接口已经预留好。</para>
    /// </remarks>
    public sealed class RabbitCreature : Creature
    {
        /// <summary>构造一只 1.5 × 1 格的兔子（位置由 <see cref="CreatureWorld.Spawn"/> 放置）。</summary>
        public RabbitCreature()
            : base(1.5f, 1f)
        {
        }

        /// <summary>重力 0.24 格 / 逻辑帧²（20 Hz 下约 4.8 格/秒²）——比初版（0.08）快 3 倍，落地不再有滞空感。</summary>
        protected override float Gravity
        {
            get { return 0.24f; }
        }

        /// <summary>最大下落速率 2.4 格 / 逻辑帧——与 <see cref="Gravity"/> 同倍率提高（比值仍是 0.1 格/帧²，
        /// 即「几帧内达到终端速度」与初版一致），否则终端速度会成为落体速度的真正上限、让重力提高看不出效果。</summary>
        protected override float MaxFallSpeed
        {
            get { return 2.4f; }
        }

        /// <summary>水中每帧净浮力 +0.15：**小于** <see cref="Gravity"/>（0.24），水里净受力仍向下、缓慢下沉。
        /// 重力提高 3 倍后本值同步重算，使水中下沉速率（<c>(Gravity − Buoyancy) / WaterDrag</c> = 0.18 格/帧）
        /// 与初版**逐位一致**——「水里慢慢沉」是既有契约，不该被陆上重力的调整牵连。</summary>
        protected override float Buoyancy
        {
            get { return 0.15f; }
        }

        /// <summary>调试名。</summary>
        public override string DebugName
        {
            get { return "兔子"; }
        }

        /// <summary>占位色：棕。</summary>
        public override Color32 PlaceholderColor
        {
            get { return new Color32(150, 100, 60, 255); }
        }
    }
}
