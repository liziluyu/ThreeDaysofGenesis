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
    /// <para><b>行为：</b>决策全在静态 <see cref="RabbitBrain"/>（Idle / Wander / Flee），本类只负责
    /// 用 <see cref="ShouldThink"/> 把思考降到 2 Hz，并把物种常量（加速度 / 起跳 / 致死门控）接到虚门上；
    /// 逐帧的意图逼近、悬崖守卫、撞墙翻越、环境致死由基类 <see cref="Creature.ApplyLocomotion"/> 统一执行。</para>
    /// </remarks>
    public sealed class RabbitCreature : Creature
    {
        /// <summary>被鸟咬中后的死亡骰分母（1/本值 概率死亡）：2 ⇒ 50%，开阔地被扑中有一半机会挣脱。</summary>
        public const int HuntDeathRoll = 2;

        /// <summary>构造一只 1.5 × 1 格的兔子（位置由 <see cref="CreatureWorld.Spawn"/> 放置）。</summary>
        public RabbitCreature()
            : base(1.5f, 1f)
        {
            HuntDeathDenominator = HuntDeathRoll;
        }

        /// <summary>每逻辑帧的逻辑钩子：门控命中后调 <see cref="RabbitBrain.Think"/>（2 Hz）。
        /// 运动层 <see cref="Creature.ApplyLocomotion"/> 随后把意图转成速度。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public override void OnLogic(CreatureWorld creatures, ParticleWorld world)
        {
            if (ShouldThink(creatures))
            {
                RabbitBrain.Think(this, creatures, world);
            }
        }

        /// <summary>陆上水平加速度：复用 <see cref="RabbitBrain.Accel"/>（数值单一来源在 Brain）。</summary>
        protected override float MoveAccel
        {
            get { return RabbitBrain.Accel; }
        }

        /// <summary>兔子能跨上一格高的坎（陆生小型动物，靠它翻过沙堆 / 台阶；鱼不具备）。</summary>
        protected override bool CanStepUp
        {
            get { return true; }
        }

        /// <summary>兔子不会主动游：竖直意图不生效，水中竖直完全交给浮力 / 重力。</summary>
        protected override float SwimAccel
        {
            get { return 0f; }
        }

        /// <summary>起跳脉冲速度：复用 <see cref="RabbitBrain.HopVelocity"/>。</summary>
        protected override float HopVelocity
        {
            get { return RabbitBrain.HopVelocity; }
        }

        /// <summary>连续淹没超过 <see cref="Creature.DrownTicksLimit"/> 帧即溺死。</summary>
        protected override bool DrownsInWater
        {
            get { return true; }
        }

        /// <summary>陆地物种：离水不会死。</summary>
        protected override bool DiesWhenBeached
        {
            get { return false; }
        }

        /// <summary>兔子食草：饥饿饱和（<see cref="Creature.MaxHungerTicks"/>）即饿死。</summary>
        protected override bool StarvesWithoutFood
        {
            get { return true; }
        }

        /// <summary>逃命优先：Flee 态跳过悬崖守卫，宁可冲下坑也不回头撞火。</summary>
        protected override bool IgnoresCliffGuard
        {
            get { return AiState == RabbitBrain.StateFlee; }
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
