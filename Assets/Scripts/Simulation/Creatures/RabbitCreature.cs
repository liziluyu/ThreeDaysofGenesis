using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 兔子：陆生物种，动物层的**验证体**。碰撞盒 1.5 × 1 格（比一格宽的兔子跨两列，能验证
    /// 覆盖列判定的正确性），受重力、贴地、入水缓慢下沉。
    /// </summary>
    /// <remarks>
    /// 本阶段**纯物理、零行为**：不覆写任何钩子（<see cref="Creature.OnSpawn"/> /
    /// <see cref="Creature.OnLogic"/> / <see cref="Creature.OnDeath"/> 全用基类空体），
    /// 因此兔子的运动完全由 <see cref="Creature.StepPhysics"/> + 重力决定。
    /// 移动 / 觅食 / 繁殖留给后续阶段——接口已经预留好。
    /// </remarks>
    public sealed class RabbitCreature : Creature
    {
        /// <summary>构造一只 1.5 × 1 格的兔子（位置由 <see cref="CreatureWorld.Spawn"/> 放置）。</summary>
        public RabbitCreature()
            : base(1.5f, 1f)
        {
        }

        /// <summary>重力 0.08 格 / 逻辑帧²（20 Hz 下约 1.6 格/秒²，落地手感偏「轻」）。</summary>
        protected override float Gravity
        {
            get { return 0.08f; }
        }

        /// <summary>最大下落速率 0.8 格 / 逻辑帧——矮小动物不追求高终端速度。</summary>
        protected override float MaxFallSpeed
        {
            get { return 0.8f; }
        }

        /// <summary>水中每帧净浮力 −0.01：入水后缓慢下沉，不会立刻浮上来。</summary>
        protected override float Buoyancy
        {
            get { return -0.01f; }
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
