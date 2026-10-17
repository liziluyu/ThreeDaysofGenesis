namespace TapTap2026.Simulation
{
    /// <summary>
    /// 鱼的行为状态机：**静态无状态**——决策状态写在 <see cref="Creature"/> 的黑板槽位上。
    /// </summary>
    /// <remarks>
    /// <para><b>状态（<see cref="Creature.AiState"/>）：</b>0=Swim（水中巡游）、1=Stranded（离水搁浅，只能扑腾）。</para>
    /// <para><b>节奏：</b>只应被 <see cref="Creature.ShouldThink"/> 放行后以 2 Hz 调用；RNG 只在思考帧消耗。</para>
    /// <para><b>竖直行为（体顶单格探测 + 下潜锁存 + 深度带随机上下游）：</b>可见身体铺在世界行 <c>floor(Y) + 0 .. floor(Y) + BodyTopOffsetCells</c>
    /// （偏移由生成美术数据给出）。体顶之上**那一格**不是水（含越界）即视为触面 → 置
    /// <see cref="Creature.VerticalDiveLatched"/> 并下潜；体顶之上**连续 <see cref="DiveUnlockMarginCells"/> 格全是水** → 解锁。
    /// 解锁后进入**深度带随机上下游**（v6，见 <c>docs/plan-bird-ai.md</c> §15）：参考量 <c>depth</c> = 体顶之上的连续水格数，
    /// 目标带 = <c>[MinDepthCells, min(MaxDepthCells, geoMax)]</c>（<c>geoMax</c> = 鱼贴底时能有的 depth = 深 <c>d</c> 的池里
    /// <c>d − 4</c>，由两次有界探针现场量出）。比目标深就上浮、比目标浅就下潜、**到达（含重摇恰好抽中当前深度）就悬停一个
    /// 思考帧并在该帧重摇**——鱼于是沿着带**持续缓漂**，不再下潜到某个深度就冻住。带下限取 3（= 解锁余量）⇒ 目标深度永远
    /// 给自己留出解锁所需的 3 格水，**不可能浮出水面**；池浅到 <c>geoMax &lt; MinDepthCells</c>（<c>d ≤ 6</c>）时**整段游层跳过**，
    /// 行为与 v5 逐字相同（永久锁潜贴底、零额外摇骰）。
    /// 两条阈值（触面 / 解锁）之间因此有 <see cref="DiveUnlockMarginCells"/> 格宽的滞回窗口，锁潜是**硬规则**、优先于带内游层。
    /// 中性浮力仍是底色（浮力 == 重力，净竖直加速度为 0，<c>IntentVy = 0</c> 时竖直速度被水阻衰减并保持当前深度）；
    /// 水深 d ≤ 6 的池连身体加余量都摆不下，鱼触面后永久锁潜贴底（那时图案出水无解），并由运动层把它钉成「浅水伏底」不再爬行。</para>
    /// <para><b>为什么必须是锁存（2026-10-14 修复）：</b>旧实现每个思考帧都拿「盒中心上方 8 格内有没有非水」实时比较，
    /// 于是「下潜带」与「近底上浮带」在浅水里重叠或紧邻：水深 d ≤ 8 时下潜恒真（贴底趴着），d ∈ {9,10} 时两条带
    /// 只差一格 ⇒ 逐思考帧在「上浮 / 下潜」之间交替（实测 600 帧换号 50~52 次）。锁存把下潜变成有记忆的状态，
    /// 只剩「锁潜 → 解锁」一条单向通道，交替消失。</para>
    /// <para><b>水平行为（墙体感知换向）：</b>先探当前方向前方 <see cref="AheadProbeCells"/> 格（体高行内）是否全水：
    /// 畅通就保持原方向（**不摇骰**，顺带为刚出生 / 刚停下的个体起步），只有前方受阻才照旧 1/3 摇骰换向。
    /// 旧实现无条件摇骰，开阔水域里 600 帧抽 12 次（纯骰子，与池形无关）；叠加基类的「锁潜 + 贴地 ⇒ 停住水平意图」
    /// 后，浅水贴底的鱼不再沿坑洼池底爬行撞坎掉头（实测狭池带坎 600 帧 21 次 → 0 次）。</para>
    /// <para><b>搁浅：</b>离水后只能每隔 15~35 帧扑腾一跳；连续离水超过 <see cref="Creature.BeachedTicksLimit"/>
    /// 帧由运动层判搁浅而死。</para>
    /// <para><b>进食（2026-10-17，docs §7.2）：</b>饥饿达到 <see cref="HungryThreshold"/> 且不在
    /// <see cref="FeedingCooldown"/> 时，在半径 <see cref="FoodSearchRadius"/> 内找**最近的水草顶格**
    /// （<see cref="ParticleIds.AquaticPlant"/> 且正上方不是同类），游到 <see cref="EatReachCells"/> 内啃掉一格：
    /// 该格 <c>SetPhase</c> 成水（水账 1:1）、<see cref="Creature.HungerTicks"/> − <see cref="FoodRestoreTicks"/>、
    /// 进冷却。该路径优先于深度带、且不摇动物随机数（见 <see cref="TryFeed"/>）。仍然**不做**全局寻路。</para>
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

        /// <summary>鱼可见身体的顶行相对碰撞盒底边的偏移（格）：由**生成美术数据**算出
        /// （<c>MaxOpaqueRow − PivotRowPixels</c> ＝ 8 − 5 ＝ 3）。切片 pivot 贴碰撞盒底边（脚底），
        /// 因此身体铺在世界行 <c>floor(Y) + 0 .. floor(Y) + 本值</c>（尸体物质化同口径）。
        /// 「水面判定 = 体顶 + 1」与「整条身体没入水下 ⇔ 体顶之上那一格是水」于是是同一件事。</summary>
        public const int BodyTopOffsetCells = CreatureArtData.Fish.MaxOpaqueRow - CreatureArtData.Fish.PivotRowPixels;

        /// <summary>鱼图案身高（格）：**生成数据**（<see cref="CreatureArtData.Fish"/>），不是手写常量——
        /// 美术变更后跑 <c>Tools/Animals/Regenerate Creature Art Data</c> 重生成即可
        /// （<c>CreatureArtDataTests</c> 会现场从 PNG 复算并对拍，忘重跑即红）。
        /// 保留这个名字是为了给「图案高度」一个语义化的引用点。</summary>
        public const int ArtHeightCells = CreatureArtData.Fish.HeightCells;

        /// <summary>下潜锁存的**解锁余量**（格）：体顶之上连续这么多格全是水才解除锁潜；
        /// 反过来，锁潜的海拔带 = 体顶之上水量 &lt; 本值。两条阈值之间因此有本值格宽的滞回窗口。
        /// <para><b>选型（3）依据</b>：设池底固体行 = <c>b−1</c>、最上一格水 = <c>b+d−1</c>（水深 <c>d</c>），
        /// 则贴底（<c>floor(Y) = b</c>）时解锁的条件是 <c>d ≥ BodyTopOffsetCells + 本值 + 1 = 本值 + 4</c>：
        /// 本值 3 ⇒ <c>d ≥ 7</c>（池至少要给得起「4 格身体 + 3 格余量」），于是 <c>d ≤ 6</c> 的池触面即永久贴底
        /// （那时图案出水本来也无解），<c>d ≥ 7</c> 才有稳态休息区。<b>实测（FishShallowWaterTests 各档 600 帧轨迹）</b>：
        /// 本值 2 ⇒ <c>d ≤ 5</c> 贴底、<c>d ≥ 7</c> 休息，但 <b><c>d = 6</c> 反而抖起来</b>——上浮终点（<c>Y ≈ b+1.79</c>）
        /// 正好落进只差 1 格的滞回窗口，鱼在池底与近水面之间来回（竖直意图 19 次换号 / 600 帧，其中后 300 帧 10 次，
        /// 全程从未贴地）；本值 4 ⇒ 连 <c>d = 7</c> 这种明明给得起身体 + 3 格余量的池也被推进永久贴底档
        /// （<c>groundedFrames = 516</c>、<c>endY = 3.0</c>、锁存恒真）。
        /// 3 是三者中唯一「无任何抖动、且不牺牲最浅可休息池」的值：滞回窗口 3 格宽，贴底阈正好是
        /// 「池给不给得起身体 + 3 格余量」这条物理上自洽的界线。</para></summary>
        public const int DiveUnlockMarginCells = 3;

        /// <summary>深度带下限（格）：取 <see cref="DiveUnlockMarginCells"/>——上层永远留出解锁所需的 3 格余量，
        /// 目标深度因此不可能把体顶推到水面之上（带的最低点恰好是解锁判据的临界点：<c>depth == 本值</c> 时
        /// 体顶之上正好连续 3 格水，锁定与带内游层无缝衔接）。见 <c>docs/plan-bird-ai.md</c> §15.1。</summary>
        public const int MinDepthCells = DiveUnlockMarginCells;

        /// <summary>深度带上限（格，受池子几何上限钳制）：8 − 3 = 振幅 5 格（屏高 5%、约 1.25× 鱼图案高 4 行）。
        /// 比鸟的高度带（<see cref="BirdBrain.MinAltitudeCells"/>~<see cref="BirdBrain.MaxAltitudeCells"/>，振幅 18）
        /// 小一档——鱼是缓漂不是掠飞。见 <c>docs/plan-bird-ai.md</c> §15.1。</summary>
        public const int MaxDepthCells = 8;

        /// <summary>墙感知换向的前探格数：当前方向前方这么多格的**体高行内**全是水才认为畅通、保持原方向不摇骰。</summary>
        public const int AheadProbeCells = 3;

        /// <summary>饥饿阈值（逻辑帧）：<see cref="Creature.HungerTicks"/> 达到它才开始寻食（600 ≈ 30 秒 @20 Hz）。
        /// 取鱼饥饿上限 <see cref="FishCreature.HungerLimitFrames"/>（2400）的 1/4：从「开始找」到「饿死」留 90 秒，
        /// 足够以 0.05 格/帧的游速走完搜索半径 6 并啃到一口；也不至于刚出生就满池找草。</summary>
        public const int HungryThreshold = 600;

        /// <summary>每格水草恢复的饥饿帧数（600 = 30 秒）：与 <see cref="HungryThreshold"/> 同值 ⇒ 吃完一口立刻回到
        /// 「不饿」区间，下一次要再饿满 600 帧（30 秒）才会重新寻食，形成可持续的「啃—长」节奏。</summary>
        public const int FoodRestoreTicks = 600;

        /// <summary>寻食扫描半径（格）：以鱼为中心的圆盘内找**最近的顶格**水草。
        /// 与兔子局部目标选择同档（<c>docs/design-creature-ai.md</c> §12 明确不做全局寻路）——13 × 13 = 113 格读 /
        /// 思考帧，只占一个世界 pass 的 0.2%，且只在「饿且不在冷却」时才走。</summary>
        public const int FoodSearchRadius = 6;

        /// <summary>进食冷却（逻辑帧）：20 ≈ 1 秒 @20 Hz。相邻可能有两格以上的顶格（不同株），
        /// 冷却把「一帧啃一格」节流成「一秒一口」，避免思考帧里把一整片草瞬间啃平。</summary>
        public const int FeedingCooldown = 20;

        /// <summary>吃到目标所需的最大切比雪夫距离（格）：1 = 目标格与碰撞盒**中心格**相邻或重合。
        /// 鱼碰撞盒只有 1 × 0.5 格，中心落在目标格或其八邻即视觉上已贴到草尖。</summary>
        public const int EatReachCells = 1;

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

        /// <summary>水中巡游：前方畅通就保持当前方向、受阻才 1/3 摇骰换向；竖直按体顶单格探测 + 下潜锁存决定
        /// 锁潜（一路下潜）/ **深度带随机上下游**（比目标深就上浮、比目标浅就下潜、到达即悬停并重摇；
        /// 池浅到摆不下带时整段跳过，落到近底上抬 / 自由漂）。</summary>
        private static void ThinkSwim(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            creature.AiState = StateSwim;

            // 进食优先于巡游与深度带（docs §7.2 的判据顺序：Stranded > 进食 > 深度带）。
            // 返回 true 的帧（寻食移动 / 啃掉一格）直接结束本帧思考：寻食意图覆盖水平墙感知与竖直深度带，
            // 否则「朝目标游」会被深度带每思考帧改写，鱼永远够不到草尖。
            if (TryFeed(creature, creatures, world))
            {
                return;
            }

            int cx = FloorToInt(creature.X);
            int footRow = FloorToInt(creature.Y);
            int bodyTopRow = footRow + BodyTopOffsetCells;

            // 水平：墙感知换向——先探当前方向前方 AheadProbeCells 格（体高行内）是不是全水。
            // 畅通 ⇒ 保持原方向（顺带把刚出生 / 刚停住的 IntentVx = 0 起步成 SwimSpeed，方向取 Facing）；
            // 受阻（坎 / 墙 / 岸 / 世界边界——越界一律记为受阻）⇒ 才回到 1/3 摇骰换向。
            // **随机流变化点**：畅通路径不再消耗 RNG（旧实现每个思考帧无条件摇一次 NextInt(3)、命中再摇 NextInt(2)）；
            // 换向路径的两次摇骰与旧实现逐字相同。鱼的方向因此与池形相关而与骰子无关——开阔水域不再左右抽。
            int dir = creature.IntentVx > 0f ? 1 : (creature.IntentVx < 0f ? -1 : (creature.Facing >= 0 ? 1 : -1));
            if (IsOpenAhead(world, cx, footRow, bodyTopRow, dir))
            {
                creature.IntentVx = dir * SwimSpeed;
            }
            else if (creatures.NextInt(3) == 0)
            {
                int newDir = creatures.NextInt(2) == 0 ? -1 : 1;
                creature.IntentVx = newDir * SwimSpeed;
            }

            // 竖直：体顶之上那一格不是水（含越界）即「触面」——锁存下潜；体顶之上连续 DiveUnlockMarginCells
            // 格全是水才解锁。两条判据都在思考帧上读写同一个锁存位 ⇒ 天然是 Schmitt 滞回，中间那段海拔
            // 保持上一次的结论（旧的实时比较在这里没有记忆，于是浅水里逐思考帧来回切，见类 remarks）。
            if (!CreatureTerrain.IsWater(world, cx, bodyTopRow + 1))
            {
                creature.VerticalDiveLatched = true;
            }
            else if (HasWaterAbove(world, cx, bodyTopRow + 1))
            {
                creature.VerticalDiveLatched = false;
            }

            int centerRow = FloorToInt(creature.Y + creature.Height * 0.5f);
            bool nearBottom = !CreatureTerrain.IsWater(world, cx, centerRow - 1)
                || !CreatureTerrain.IsWater(world, cx, centerRow - 2);

            // 深度带随机上下游（v6，见 docs/plan-bird-ai.md §15）：参考量 depth = 体顶之上的连续水格数（水面到体顶的距离），
            // 目标带 = [MinDepthCells, min(MaxDepthCells, geoMax)]。geoMax = 鱼贴底时能有的 depth（深 d 的池里 = d − 4），
            // 由「体顶之上的水面行」与「脚底之下的池底行」两次有界探针现场量出（都只用局部 int 与 while ⇒ 零分配、必定终止）。
            int surfaceRow = CreatureSensors.ProbeSurfaceRowAbove(world, cx, bodyTopRow + 1);
            int floorRow = ProbeFloorRowBelow(world, cx, footRow - 1);
            int maxDepth = Min(MaxDepthCells, surfaceRow - floorRow - 2 - BodyTopOffsetCells);

            // 判据顺序：锁潜 > 带内上下游 > 近底上抬 > 自由漂（锁潜是硬规则，优先于游层）。
            if (creature.VerticalDiveLatched)
            {
                creature.IntentVy = -VerticalSpeed; // 锁潜：一路下潜（浅到摆不下余量的池 ⇒ 永久贴底，运动层再让它伏底不动）
            }
            else if (maxDepth >= MinDepthCells)
            {
                // 带内上下游：比目标深就上浮、比目标浅就下潜、到达即悬停一帧并重摇。
                // 「到达即重摇」而不是逐帧摇：否则竖直意图会在带内逐思考帧抖动（旧双带实现 600 帧换号 52 次的坑，见类 remarks）。
                // 目标无效（< 下限 / > 上限）或已达成（depth == target）时才摇一次；只在本分支摇骰，
                // 池浅到 maxDepth < MinDepthCells 时整段跳过、一粒骰子不摇 ⇒ 浅池行为与 v5 逐字相同。
                int depth = surfaceRow - 1 - bodyTopRow;
                int target = creature.TargetDepthCells;
                if (target < MinDepthCells || target > maxDepth || depth == target)
                {
                    target = MinDepthCells + creatures.NextInt(maxDepth - MinDepthCells + 1);
                    creature.TargetDepthCells = target;
                }

                if (depth > target)
                {
                    creature.IntentVy = VerticalSpeed; // 比目标深 ⇒ 上浮
                }
                else if (depth < target)
                {
                    creature.IntentVy = -VerticalSpeed; // 比目标浅 ⇒ 下潜
                }
                else
                {
                    creature.IntentVy = 0f; // 重摇恰好抽中当前深度：悬停一个思考帧，下个思考帧再摇
                }
            }
            else if (nearBottom)
            {
                // 兜底（带已覆盖解锁后的全部情形：解锁 ⇒ 体顶之上 3 格水 ⇒ geoMax ≥ MinDepthCells，故此处只在
                // 池子几何量不出带时可达）。保留原语义：未锁潜且近底 → 抬离池底。
                creature.IntentVy = VerticalSpeed;
            }
            else
            {
                creature.IntentVy = 0f; // 自由漂：中性浮力保持当前深度
            }
        }

        /// <summary>
        /// 进食：饿且不在冷却中才寻食 / 吃。返回 true 表示本思考帧已被进食占用（调用方必须直接结束思考），
        /// false 表示照常走巡游 + 深度带。
        /// </summary>
        /// <remarks>
        /// <para><b>判据顺序：</b>① 不饿 / 冷却中 ⇒ 清目标、false（不扫描、不摇骰）；② 已有目标先校验
        /// （仍在半径内且仍是顶格），失效即清；③ 无目标 ⇒ 半径 <see cref="FoodSearchRadius"/> 的圆盘找最近顶格，
        /// 找不到 ⇒ false；④ 目标相邻 ⇒ 啃一格（<c>SetPhase</c> 成水，1:1 水账）；⑤ 否则朝目标写意图并 true。</para>
        /// <para><b>随机流：</b>搜索是纯扫描、移动只写意图 ⇒ 本路径**一粒动物骰都不摇**；只有 ④ 的
        /// <see cref="ParticleWorld.SetPhase"/> 会消耗世界随机流（与兔子进食同属既有例外清单）。</para>
        /// <para><b>为什么寻食移动要 return true：</b>深度带 / 墙感知每思考帧都会改写竖直 / 水平意图，
        /// 若寻食意图再被它们覆盖，鱼会在目标附近来回改写、永远够不到草尖。</para>
        /// </remarks>
        private static bool TryFeed(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            if (creature.HungerTicks < HungryThreshold || creature.FeedingCooldownTicks != 0)
            {
                ClearFoodTarget(creature);
                return false;
            }

            int cx = FloorToInt(creature.X);
            int centerRow = FloorToInt(creature.Y + creature.Height * 0.5f);
            int targetX = creature.FoodTargetX;
            int targetY = creature.FoodTargetY;

            // 目标校验：超出半径（鱼已游走）或已不是顶格（被别的鱼啃掉 / 长高）都作废，本帧重新扫描。
            if (targetX == Creature.NoFoodTarget
                || targetY == Creature.NoFoodTarget
                || !IsWithinFoodSearchRadius(targetX - cx, targetY - centerRow)
                || !IsEdibleTopCell(world, targetX, targetY))
            {
                ClearFoodTarget(creature);
                if (!TryFindNearestEdibleTopCell(world, cx, centerRow, out targetX, out targetY))
                {
                    return false;
                }

                creature.FoodTargetX = targetX;
                creature.FoodTargetY = targetY;
            }

            if (Abs(targetX - cx) <= EatReachCells && Abs(targetY - centerRow) <= EatReachCells)
            {
                // 啃掉顶格：相变成水（每格植物 ⇔ 一格水，用 SetPhase 不用 Clear，水账 1:1 不破）。
                world.SetPhase(targetX, targetY, ParticleIds.Water);
                int hunger = creature.HungerTicks - FoodRestoreTicks;
                creature.HungerTicks = hunger > 0 ? hunger : 0;
                creature.FeedingCooldownTicks = FeedingCooldown;
                ClearFoodTarget(creature);
                return true;
            }

            // 寻食移动：水平朝目标列、竖直朝目标行（覆盖深度带；目标本身在水下 ⇒ 不会把它带出水面）。
            float dxf = targetX - creature.X;
            int dirX = dxf > 0f ? 1 : (dxf < 0f ? -1 : 0);
            creature.IntentVx = dirX * SwimSpeed;

            if (targetY < centerRow)
            {
                creature.IntentVy = -VerticalSpeed;
            }
            else if (targetY > centerRow)
            {
                creature.IntentVy = VerticalSpeed;
            }
            else
            {
                creature.IntentVy = 0f;
            }

            return true;
        }

        /// <summary>目标是否仍在以鱼为中心、半径 <see cref="FoodSearchRadius"/> 的圆盘内（欧氏距离平方，含边界）。</summary>
        private static bool IsWithinFoodSearchRadius(int dx, int dy)
        {
            return dx * dx + dy * dy <= FoodSearchRadius * FoodSearchRadius;
        }

        /// <summary>可吃格 = 水生植物且**正上方不是**水生植物（整株最上面那一格）；下方茎格永不被吃。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不是植物。</summary>
        private static bool IsEdibleTopCell(ParticleWorld world, int x, int y)
        {
            return world.GetCell(x, y).typeId == ParticleIds.AquaticPlant
                && world.GetCell(x, y + 1).typeId != ParticleIds.AquaticPlant;
        }

        /// <summary>以 (cx, cy) 为中心扫半径 <see cref="FoodSearchRadius"/> 的圆盘，找**最近**的顶格。
        /// 固定扫描序（dy 升序、dx 升序）+「严格更近才替换」⇒ 距离平手保留先生成序，结果完全确定；
        /// 纯读、零分配、无随机数。</summary>
        private static bool TryFindNearestEdibleTopCell(
            ParticleWorld world, int cx, int cy, out int targetX, out int targetY)
        {
            int bestX = 0;
            int bestY = 0;
            int bestDistSq = int.MaxValue;
            for (int dy = -FoodSearchRadius; dy <= FoodSearchRadius; dy++)
            {
                for (int dx = -FoodSearchRadius; dx <= FoodSearchRadius; dx++)
                {
                    int distSq = dx * dx + dy * dy;
                    if (distSq > FoodSearchRadius * FoodSearchRadius || distSq >= bestDistSq)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    int y = cy + dy;
                    if (IsEdibleTopCell(world, x, y))
                    {
                        bestX = x;
                        bestY = y;
                        bestDistSq = distSq;
                    }
                }
            }

            targetX = bestX;
            targetY = bestY;
            return bestDistSq != int.MaxValue;
        }

        /// <summary>清空食物目标（写哨兵 <see cref="Creature.NoFoodTarget"/>）。</summary>
        private static void ClearFoodTarget(Creature creature)
        {
            creature.FoodTargetX = Creature.NoFoodTarget;
            creature.FoodTargetY = Creature.NoFoodTarget;
        }

        /// <summary>整数绝对值（不引 Math / Mathf，保持决策路径零 BCL 依赖）。</summary>
        private static int Abs(int value)
        {
            return value < 0 ? -value : value;
        }

        /// <summary>当前方向前方 <see cref="AheadProbeCells"/> 格是否一路畅通：体高行（脚底行 .. 体顶行）内
        /// 每一格都是水才算畅通；越界格探针一律回答「非水」，因此世界边界 / 岸边等价于受阻。</summary>
        /// <param name="world">粒子世界（只读）。</param>
        /// <param name="cx">脚底中点所在格 x。</param>
        /// <param name="footRow">脚底行（<c>floor(Y)</c>）。</param>
        /// <param name="bodyTopRow">体顶行（<c>footRow + BodyTopOffsetCells</c>）。</param>
        /// <param name="dir">当前前进方向（+1 右 / −1 左）。</param>
        /// <returns>前方 AheadProbeCells 格的体高行内全为水时 true。</returns>
        private static bool IsOpenAhead(ParticleWorld world, int cx, int footRow, int bodyTopRow, int dir)
        {
            for (int k = 1; k <= AheadProbeCells; k++)
            {
                int x = cx + dir * k;
                for (int row = footRow; row <= bodyTopRow; row++)
                {
                    if (!CreatureTerrain.IsWater(world, x, row))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>自 <paramref name="firstRow"/> 起连续 <see cref="DiveUnlockMarginCells"/> 格是否全是水
        /// （解锁下潜锁存的判据）。</summary>
        /// <param name="world">粒子世界（只读）。</param>
        /// <param name="cx">脚底中点所在格 x。</param>
        /// <param name="firstRow">起算行（体顶之上第一格）。</param>
        /// <returns>连续 DiveUnlockMarginCells 格全为水时 true。</returns>
        private static bool HasWaterAbove(ParticleWorld world, int cx, int firstRow)
        {
            for (int k = 0; k < DiveUnlockMarginCells; k++)
            {
                if (!CreatureTerrain.IsWater(world, cx, firstRow + k))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>自 <paramref name="startRow"/> 向下扫到第一个非水格（越界算非水）⇒ 池底行（脚底之下的第一个非水行）。
        /// 与 <see cref="CreatureSensors.ProbeSurfaceRowAbove"/> 对称，同样必定终止。</summary>
        /// <param name="world">粒子世界（只读）。</param>
        /// <param name="x">脚底中点所在格 x。</param>
        /// <param name="startRow">起算行（脚底之下第一格）。</param>
        /// <returns>脚底之下第一个非水行。</returns>
        private static int ProbeFloorRowBelow(ParticleWorld world, int x, int startRow)
        {
            int lastRow = startRow - world.Height;
            int y = startRow;
            while (y > lastRow && CreatureTerrain.IsWater(world, x, y))
            {
                y--;
            }

            return y;
        }

        /// <summary>两整数取小（不引 Mathf / Math，保持决策路径零 UnityEngine / 零 BCL 依赖）。</summary>
        private static int Min(int a, int b)
        {
            return a < b ? a : b;
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
