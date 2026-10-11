using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 鱼：水生物种，动物层的第二个**验证体**。碰撞盒 1 格宽、半格高（比兔子小一圈，验证矮身实体的
    /// 淹没判定与水面行为），在水中受净浮力缓慢上浮、到水面附近反复出水入水地停住，离水则像石头一样落下。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么能「浮」而兔子会沉：</b><see cref="Creature.StepPhysics"/> 在水里先乘阻力再叠加
    /// <see cref="Buoyancy"/>、随后减重力——鱼的浮力（+0.09）**大于**重力（0.08），净加速度向上 +0.01 格/帧²，
    /// 于是缓慢上浮；一出水面中心格就不在水里，浮力消失、只剩重力把它拉回，于是停在水面上下小幅摆动。
    /// 兔子相反（浮力 −0.01），入水后缓慢下沉。两者都不是特判，是同一条物理公式配不同常数。</para>
    /// <para>本阶段**纯物理、零行为**：三个钩子（<see cref="Creature.OnSpawn"/> /
    /// <see cref="Creature.OnLogic"/> / <see cref="Creature.OnDeath"/>）全用基类空体，
    /// 游动 / 觅食 / 繁殖留给后续阶段——接口已经预留好。</para>
    /// </remarks>
    public sealed class FishCreature : Creature
    {
        /// <summary>构造一条 1 × 0.5 格的鱼（位置由 <see cref="CreatureWorld.Spawn"/> 放置）。</summary>
        public FishCreature()
            : base(1f, 0.5f)
        {
        }

        /// <summary>重力 0.08 格 / 逻辑帧²（与兔子同值：离水时的下坠感一致）。</summary>
        protected override float Gravity
        {
            get { return 0.08f; }
        }

        /// <summary>最大下落速率 0.4 格 / 逻辑帧——比兔子更轻更慢的终端速度。</summary>
        protected override float MaxFallSpeed
        {
            get { return 0.4f; }
        }

        /// <summary>水中每帧净浮力 +0.09：**大于** <see cref="Gravity"/>（0.08），净加速度向上，缓慢浮向水面。</summary>
        protected override float Buoyancy
        {
            get { return 0.09f; }
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

        /// <summary>占位色：蓝（真 sprite 接线前的调试色块；鱼对蓝色水面背景的辨识度比灰色高）。</summary>
        public override Color32 PlaceholderColor
        {
            get { return new Color32(86, 130, 180, 255); }
        }
    }
}
