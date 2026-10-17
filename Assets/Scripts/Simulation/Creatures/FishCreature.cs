using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 鱼：水生物种，动物层的第二个**验证体**。碰撞盒 1 格宽、半格高（比兔子小一圈，验证矮身实体的
    /// 淹没判定与水面行为），在水中既不浮也不沉、保持 <see cref="FishBrain"/> 意图维持的深度，离水则像石头一样落下。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么鱼不会沉（兔子会）：</b><see cref="Creature.StepPhysics"/> 在水里先乘阻力再叠加
    /// <see cref="Buoyancy"/>、随后减重力——鱼的浮力（0.24）**等于**重力（0.24），净竖直加速度为 0，
    /// 于是入水后竖直速度被 <see cref="WaterDrag"/> 逐步衰减到 0、**停在入水深度**（不浮不沉），
    /// 升降只由意图驱动；一出水面浮力不再叠加，只剩重力把它拉回。兔子相反（浮力 0.15 < 重力 0.24），
    /// 入水后缓慢下沉。两者都不是特判，是同一条物理公式配不同常数。</para>
    /// <para><b>行为：</b>决策全在静态 <see cref="FishBrain"/>（Swim / Stranded；**饥饿觅食是 Swim 的子行为**——饥饿过阈值时
    /// 追附近水草并咬掉整株最顶一格，饥饿饱和则饿死），本类只负责用
    /// <see cref="ShouldThink"/> 把思考降到 2 Hz，并把物种常量（加速度 / 游动 / 起跳 / 致死门控 / 寿命）接到虚门上；
    /// 逐帧的意图逼近、接触火 / 搁浅致死由基类 <see cref="Creature.ApplyLocomotion"/> 统一执行。</para>
    /// </remarks>
    public sealed class FishCreature : Creature
    {
        /// <summary>被鸟咬中后的死亡骰分母（1/本值 概率死亡）：3 ⇒ ≈33%，鱼在水里更难抓。</summary>
        public const int HuntDeathRoll = 3;

        /// <summary>寿命（逻辑帧）：6400 帧 = 320 秒 @20 逻辑帧/秒。
        /// <para><b>实测依据（2026-10-17 标定）</b>：相邻成年异性一对的出生间隔恒为繁殖冷却 600 帧
        /// （实测 600 整）⇒ <c>(6400 − 成年 400) / 600 = 10</c>，即一只鱼成年后约 10 次繁殖尝试。
        /// 鱼还多一道「双方必须完全没入 ≥7 格深的水且相邻」的门槛，实测自由游动时个体难得靠近、
        /// 种群几乎不扩张 ⇒ 寿命刻意取得比兔更长。同时必须显著长于既有阶段时长（最长 2000 帧）
        /// 与测试夹具的年龄预算（1000 + 600 帧），否则鱼会在关卡中途成批老死、把可胜阶段变成不可胜。</para></summary>
        public const int LifespanTicks = 6400;

        /// <summary>构造一条 1 × 0.5 格的鱼（位置由 <see cref="CreatureWorld.Spawn"/> 放置）。</summary>
        public FishCreature()
            : base(1f, 0.5f)
        {
            HuntDeathDenominator = HuntDeathRoll;
        }

        /// <summary>每逻辑帧的逻辑钩子：门控命中后调 <see cref="FishBrain.Think"/>（2 Hz）。
        /// 运动层 <see cref="Creature.ApplyLocomotion"/> 随后把意图转成速度。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public override void OnLogic(CreatureWorld creatures, ParticleWorld world)
        {
            if (ShouldThink(creatures))
            {
                FishBrain.Think(this, creatures, world);
                // 繁殖是思考帧的第二个决策（雌性才会真正摇骰；见 CreatureReproduction）。
                CreatureReproduction.TryBreed(this, creatures, world);
            }
        }

        /// <summary>水平加速度：复用 <see cref="FishBrain.Accel"/>。</summary>
        protected override float MoveAccel
        {
            get { return FishBrain.Accel; }
        }

        /// <summary>水中竖直加速度：复用 <see cref="FishBrain.SwimAccel"/>（离水后竖直意图自动失效）。</summary>
        protected override float SwimAccel
        {
            get { return FishBrain.SwimAccel; }
        }

        /// <summary>扑腾一跳的竖直脉冲：复用 <see cref="FishBrain.HopVelocity"/>。</summary>
        protected override float HopVelocity
        {
            get { return FishBrain.HopVelocity; }
        }

        /// <summary>水生物种：淹没不会溺死。</summary>
        protected override bool DrownsInWater
        {
            get { return false; }
        }

        /// <summary>连续离水超过 <see cref="Creature.BeachedTicksLimit"/> 帧即搁浅而死。</summary>
        protected override bool DiesWhenBeached
        {
            get { return true; }
        }

        /// <summary>鱼食水草：饥饿饱和（<see cref="Creature.MaxHungerTicks"/>）即饿死（2026-10-17 用户要求）。
        /// 与兔子共用同一条饱和判定：`HungerTicks` 每逻辑帧 +1、吃一格水草 −<see cref="FishBrain.FoodRestoreTicks"/>，
        /// 因此「附近有水草」是鱼能否越过 60 秒饥饿线的唯一变量。</summary>
        protected override bool StarvesWithoutFood
        {
            get { return true; }
        }

        /// <summary>鱼有固定寿命：<see cref="Creature.AgeTicks"/> 达到 <see cref="LifespanTicks"/> 即老死
        /// （确定性、无个体抖动；复用既有死亡链）。</summary>
        public override int MaxAgeTicks
        {
            get { return LifespanTicks; }
        }

        /// <summary>重力 0.24 格 / 逻辑帧²（与兔子同值：离水时的下坠感一致；比初版 0.08 快 3 倍）。</summary>
        protected override float Gravity
        {
            get { return 0.24f; }
        }

        /// <summary>最大下落速率 1.2 格 / 逻辑帧——与重力同倍率（比值仍 0.2），比兔子更轻更慢的终端速度。</summary>
        protected override float MaxFallSpeed
        {
            get { return 1.2f; }
        }

        /// <summary>水中每帧净浮力 +0.24：**等于** <see cref="Gravity"/>（0.24），两者在水中相抵——
        /// 净竖直加速度为 0，鱼既不上浮也不下沉，竖直速度只受 <see cref="WaterDrag"/> 衰减，
        /// 深度由 <see cref="FishBrain"/> 的下潜 / 上浮意图维持。兔子相反（0.15 &lt; 0.24），入水缓沉。</summary>
        protected override float Buoyancy
        {
            get { return 0.24f; }
        }

        /// <summary>水中速度保持系数折损 0.6（比兔子更黏）：游动迟滞、摆动收敛得快。</summary>
        protected override float WaterDrag
        {
            get { return 0.6f; }
        }

        /// <summary>调试名。</summary>
        public override string DebugName
        {
            get { return "鱼"; }
        }

        /// <summary>物种标识（渲染按它选鱼 idle 帧序列）。</summary>
        public override CreatureSpecies Species
        {
            get { return CreatureSpecies.Fish; }
        }

        /// <summary>占位色：蓝（真 sprite 接线前的调试色块；鱼对蓝色水面背景的辨识度比灰色高）。</summary>
        public override Color32 PlaceholderColor
        {
            get { return new Color32(86, 130, 180, 255); }
        }
    }
}
