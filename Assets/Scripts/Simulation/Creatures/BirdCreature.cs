using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 鸟：**第一个飞行动物**，动物层的第三个验证体。碰撞盒 1 × 1 格（比兔子 / 鱼都紧凑，验证
    /// 「图案大、碰撞盒小」的飞行实体），在空中不受重力、以 <see cref="BirdBrain"/> 的意图维持高度并悬停，
    /// 落水靠正浮力自动上浮出水后重新起飞。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么鸟能悬停（兔子不能）：</b><see cref="Creature.StepPhysics"/> 对
    /// <see cref="Creature.FliesInAir"/> 的物种在空中**不累加重力**（同鱼在水里的中性浮力），
    /// 竖直速度只被 <see cref="BirdBrain"/> 的意图驱动——<c>IntentVy = 0</c> 即高度不变。
    /// v1 的「降落」因此一律是主动意图（下压 <c>IntentVy</c> 到触地），不存在被动坠落。</para>
    /// <para><b>落水自救：</b>入水后走常规则——<see cref="Buoyancy"/>（0.30）**大于** <see cref="Gravity"/>（0.24），
    /// 净受力向上 ⇒ 自动上浮到身体中心出水；<see cref="SwimAccel"/>（0.1）让水中的竖直意图可用（强起飞）。
    /// 出水即回到飞行挂起。 <see cref="DrownsInWater"/> = true 只是兜底（连续淹没 160 帧溺死，
    /// 正常情况下几帧内就出水，<c>SubmergedTicks</c> 随即清零）。</para>
    /// <para><b>栖息：</b><see cref="PerchesOnFoliage"/> = true ⇒ 向下支撑扫描把树叶 / 树干也算作可栖面，
    /// 鸟能停在树冠叶顶；但叶子不挡水平 / 上升（<see cref="CreatureTerrain.IsSolid"/> 不变），
    /// 因此鸟**水平飞穿树冠不受阻**。</para>
    /// <para><b>行为：</b>决策全在静态 <see cref="BirdBrain"/>（Perched / Fly / Flee），本类只负责用
    /// <see cref="Creature.ShouldThink"/> 把思考降到 2 Hz，并把物种常量（加速度 / 飞行 / 致死门控）接到虚门上。</para>
    /// <para><b>v1 非目标：</b>不进食（<see cref="StarvesWithoutFood"/> = false）、不繁殖、不筑巢。</para>
    /// </remarks>
    public sealed class BirdCreature : Creature
    {
        /// <summary>构造一只 1 × 1 格的鸟（位置由 <see cref="CreatureWorld.Spawn"/> 放置）。</summary>
        public BirdCreature()
            : base(1f, 1f)
        {
        }

        /// <summary>每逻辑帧的逻辑钩子：门控命中后调 <see cref="BirdBrain.Think"/>（2 Hz）。
        /// 运动层 <see cref="Creature.ApplyLocomotion"/> 随后把意图转成速度。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public override void OnLogic(CreatureWorld creatures, ParticleWorld world)
        {
            if (ShouldThink(creatures))
            {
                BirdBrain.Think(this, creatures, world);
            }
        }

        /// <summary>水平加速度：复用 <see cref="BirdBrain.Accel"/>（数值单一来源在 Brain）。</summary>
        protected override float MoveAccel
        {
            get { return BirdBrain.Accel; }
        }

        /// <summary>水中竖直加速度：复用 <see cref="BirdBrain.SwimAccel"/>（落水后强起飞意图的逼近步长）。</summary>
        protected override float SwimAccel
        {
            get { return BirdBrain.SwimAccel; }
        }

        /// <summary>空中竖直加速度：复用 <see cref="BirdBrain.FlyAccel"/>（悬停 / 升降意图的逼近步长）。</summary>
        protected override float FlyAccel
        {
            get { return BirdBrain.FlyAccel; }
        }

        /// <summary>飞行物种：空中挂起重力（悬停），竖直意图在两栖（水中 / 空中）都生效。</summary>
        protected override bool FliesInAir
        {
            get { return true; }
        }

        /// <summary>鸟可栖息在树叶 / 树干顶（只放宽向下支撑口径）。</summary>
        protected override bool PerchesOnFoliage
        {
            get { return true; }
        }

        /// <summary>飞行不需要跨坎（贴近地面时也靠起飞离地）。</summary>
        protected override bool CanStepUp
        {
            get { return false; }
        }

        /// <summary>连续淹没超过 <see cref="Creature.DrownTicksLimit"/> 帧即溺死（兜底；正浮力通常几帧内就出水）。</summary>
        protected override bool DrownsInWater
        {
            get { return true; }
        }

        /// <summary>鸟不因离水而死（飞行物种落水后自救；搁浅判定不适用）。</summary>
        protected override bool DiesWhenBeached
        {
            get { return false; }
        }

        /// <summary>v1 不进食：不会因饥饿而死。</summary>
        protected override bool StarvesWithoutFood
        {
            get { return false; }
        }

        /// <summary>重力 0.24 格 / 逻辑帧²（与兔 / 鱼同值；只在入水与主动坠落时起作用，空中被挂起）。</summary>
        protected override float Gravity
        {
            get { return 0.24f; }
        }

        /// <summary>最大下落速率 2.4 格 / 逻辑帧（与重力同倍率；入水与主动下压的终端速度上限）。</summary>
        protected override float MaxFallSpeed
        {
            get { return 2.4f; }
        }

        /// <summary>水中每帧净浮力 +0.30：**大于** <see cref="Gravity"/>（0.24），净受力向上 ⇒
        /// 落水后自动上浮到身体中心出水；出水后浮力不再叠加、且空中重力挂起 ⇒ 转而由意图维持飞行。</summary>
        protected override float Buoyancy
        {
            get { return 0.30f; }
        }

        /// <summary>水中速度保持系数折损 0.5（与兔子同值：出水自救不必过分黏滞）。</summary>
        protected override float WaterDrag
        {
            get { return 0.5f; }
        }

        /// <summary>调试名。</summary>
        public override string DebugName
        {
            get { return "鸟"; }
        }

        /// <summary>物种标识（渲染按它选鸟 idle 帧序列）。</summary>
        public override CreatureSpecies Species
        {
            get { return CreatureSpecies.Bird; }
        }

        /// <summary>占位色：金黄（与兔棕、鱼蓝区分开；真 sprite 接线前的调试色块）。</summary>
        public override Color32 PlaceholderColor
        {
            get { return new Color32(232, 190, 62, 255); }
        }
    }
}
