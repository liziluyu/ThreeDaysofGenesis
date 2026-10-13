namespace TapTap2026.Simulation
{
    /// <summary>
    /// 鱼的行为状态机：**静态无状态**——决策状态写在 <see cref="Creature"/> 的黑板槽位上。
    /// </summary>
    /// <remarks>
    /// <para><b>状态（<see cref="Creature.AiState"/>）：</b>0=Swim（水中巡游）、1=Stranded（离水搁浅，只能扑腾）。</para>
    /// <para><b>节奏：</b>只应被 <see cref="Creature.ShouldThink"/> 放行后以 2 Hz 调用；RNG 只在思考帧消耗。</para>
    /// <para><b>竖直行为：</b>中心格上方 <see cref="SurfaceClearanceCells"/> 格内有非水（含越界）就下潜，
    /// 否则离底 ≤ 2 格上浮，再否则 <c>IntentVy = 0</c>——鱼是
    /// **中性浮力**（浮力 == 重力，净竖直加速度为 0），意图为 0 时竖直速度被水阻衰减并**保持当前深度**，
    /// 鱼因此停在水体中段。下潜带 = 图案高度 + 1 格余量：上方水量不足 8 格就下潜 ⇒ 鱼稳定停在
    /// 「中心格 ≤ 单面下 7 格」，图案（<see cref="ArtHeightCells"/> = 7 格）整条没入水下。
    /// 代价：水深 &lt; 8 格时它会贴底（那时图案必然出水，无解），且它再也不会主动靠到水面。</para>
    /// <para><b>搁浅：</b>离水后只能每隔 15~35 帧扑腾一跳；连续离水超过 <see cref="Creature.BeachedTicksLimit"/>
    /// 帧由运动层判搁浅而死。</para>
    /// <para><b>v1 非目标：</b>不吃草、不繁殖、不寻路。</para>
    /// </remarks>
    public static class FishBrain
    {
        /// <summary>状态：水中巡游。</summary>
        public const byte StateSwim = 0;

        /// <summary>状态：离水搁浅（扑腾等待回水或死亡）。</summary>
        public const byte StateStranded = 1;

        /// <summary>水平加速度（格 / 逻辑帧²）。</summary>
        public const float Accel = 0.01f;

        /// <summary>水中竖直加速度（格 / 逻辑帧²）：竖直速度向 <see cref="VerticalSpeed"/> 逼近的步长。
        /// 它决定**能追到多快的目标**：物理每帧再乘 <c>1 − WaterDrag</c>，稳态竖直位移/帧 =
        /// <c>(1 − WaterDrag) · min(VerticalSpeed, SwimAccel / WaterDrag)</c>。取 0.02 时被
        /// <c>SwimAccel / WaterDrag = 0.0333</c> 封顶（= 0.0133 格/帧 = 0.267 格/秒，2026-10-12 实测踩过），
        /// 取 0.1 才让 <see cref="VerticalSpeed"/> 成为上限（= 0.04 格/帧 = 0.8 格/秒）。</summary>
        public const float SwimAccel = 0.1f;

        /// <summary>扑腾一跳的竖直脉冲（格 / 逻辑帧）。</summary>
        public const float HopVelocity = 0.5f;

        /// <summary>巡游速度（格 / 逻辑帧）。</summary>
        public const float SwimSpeed = 0.05f;

        /// <summary>下潜 / 上浮的竖直**目标**速度（格 / 逻辑帧）。物理每帧再乘 <c>1 − WaterDrag</c>，
        /// 因此稳态竖直位移/帧 = <c>(1 − WaterDrag) · min(本值, SwimAccel / WaterDrag)</c>；
        /// <see cref="SwimAccel"/> 取 0.1 时本值（0.1）成为上限 ⇒ 0.4 × 0.1 = 0.04 格/帧
        /// （≈0.8 格/秒 @20 逻辑帧/秒，与水平巡游同量级）。</summary>
        public const float VerticalSpeed = 0.1f;

        /// <summary>鱼图案身高（格）：`Assets/Images/Animals/fish/1/fish.png` 的 4 帧切片都是 11×7 px，
        /// 且 1 源像素 = 1 格 ⇒ 图形底边（= 碰撞盒底边）之上 7 格都是可见身体。
        /// **改美术尺度或换图时要同步本值**：<c>CreatureSpriteFacingTests</c> 有一条从 PNG 实测的守卫
        /// （取各切片不透明像素行数最大值断言等于本值）。</summary>
        public const int ArtHeightCells = 7;

        /// <summary>下潜判定带（格）= 图案高度 + 1 格余量。中心格上方这么多格内只要有一格不是水就下潜。</summary>
        public const int SurfaceClearanceCells = ArtHeightCells + 1;

        /// <summary>一次思考：在水里就巡游，离水就搁浅扑腾。</summary>
        /// <param name="creature">思考的鱼。</param>
        /// <param name="creatures">所属动物世界（RNG 来源）。</param>
        /// <param name="world">粒子世界（只读感知）。</param>
        public static void Think(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            if (creature.IsSubmerged)
            {
                ThinkSwim(creature, creatures, world);
            }
            else
            {
                ThinkStranded(creature, creatures);
            }
        }

        /// <summary>水中巡游：1/3 概率换向；按上方余量 / 离底距离决定下潜 / 上浮 / 自由漂。</summary>
        private static void ThinkSwim(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            creature.AiState = StateSwim;

            // 水平：1/3 概率换向，其余思考帧保持当前方向（转向前先减速，因此不会瞬间镜像）。
            if (creatures.NextInt(3) == 0)
            {
                int dir = creatures.NextInt(2) == 0 ? -1 : 1;
                creature.IntentVx = dir * SwimSpeed;
            }

            // 竖直：向上探 SurfaceClearanceCells 格（含越界，越界视为非水）。
            int cx = FloorToInt(creature.X);
            int cy = FloorToInt(creature.Y + creature.Height * 0.5f);
            bool nearSurface = false;
            for (int k = 1; k <= SurfaceClearanceCells; k++)
            {
                if (!CreatureTerrain.IsWater(world, cx, cy + k))
                {
                    nearSurface = true;
                    break;
                }
            }

            bool nearBottom = !CreatureTerrain.IsWater(world, cx, cy - 1)
                || !CreatureTerrain.IsWater(world, cx, cy - 2);

            if (nearSurface)
            {
                creature.IntentVy = -VerticalSpeed; // 上方余量不足 → 下潜（浅于 8 格时会一路贴底）
            }
            else if (nearBottom)
            {
                creature.IntentVy = VerticalSpeed; // 近底 → 上浮
            }
            else
            {
                creature.IntentVy = 0f; // 中段 → 保持当前深度（中性浮力 ⇒ 无竖直驱动）
            }
        }

        /// <summary>搁浅：停住水平移动，按 15~35 帧的间隔扑腾一跳。</summary>
        private static void ThinkStranded(Creature creature, CreatureWorld creatures)
        {
            creature.IntentVx = 0f;
            creature.IntentVy = 0f;

            if (creature.AiState != StateStranded)
            {
                // 刚离水：进入搁浅并摇下一次扑腾的间隔。
                creature.AiState = StateStranded;
                creature.StateTicksLeft = 15 + creatures.NextInt(21);
                return;
            }

            if (creature.StateTicksLeft <= 0)
            {
                creature.WantHop = true;
                creature.StateTicksLeft = 15 + creatures.NextInt(21);
            }
        }

        /// <summary>向下取整（不走 Mathf，保持决策路径零 UnityEngine 依赖）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }
    }
}
