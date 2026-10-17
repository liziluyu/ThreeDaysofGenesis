namespace TapTap2026.Simulation
{
    /// <summary>
    /// 鸟的行为状态机：**静态无状态**——决策状态写在 <see cref="Creature"/> 的黑板槽位上。
    /// </summary>
    /// <remarks>
    /// <para><b>状态（<see cref="Creature.AiState"/>）：</b>0 = Perched（栖息）、1 = Fly（空中巡航）、
    /// 2 = Flee（逃命）、3 = Hunt（捕食——随机袭击兔 / 鱼，见 <c>docs/plan-bird-ai.md</c> §11）、
    /// 4 = SeekFood（生物质觅食）、5 = Eat（进食生物质）。</para>
    /// <para><b>节奏：</b>只应被 <see cref="Creature.ShouldThink"/> 放行后以 2 Hz 调用；RNG
    /// （<see cref="CreatureWorld.NextInt"/>）只在思考帧消耗。</para>
    /// <para><b>落水自救（最高优先级）：</b><see cref="Creature.IsSubmerged"/> 时只把 <c>IntentVy</c> 抬成
    /// <see cref="SwimAccel"/> 的强起飞意图、不再进别的状态（鸟没有扑腾态）——配合基类的正浮力
    /// （Buoyancy 0.30 &gt; Gravity 0.24）自动上浮，出水后自然回到 Perched / Fly。</para>
    /// <para><b>威胁（火焰）：</b>照抄 <see cref="RabbitBrain"/> 的逃命模式——进入用
    /// <see cref="Creature.ThreatRadius"/>、脱离用 <c>+ <see cref="FleeExitMargin"/></c> 的滞回半径，
    /// 远离最近那团火、同列时只摇一次骰选边，进入时写最短逃命时长 <see cref="FleeMinTicks"/>；
    /// 最短时长走完且火出圈才回 <see cref="StateFly"/>。竖直一律向上冲（<see cref="FleeClimbSpeed"/>）。</para>
    /// <para><b>Fly（巡航）：</b>水平沿用鱼的**墙感知换向**（前方 <see cref="AheadProbeCells"/> 格的体高行内
    /// 全是非固体才保持方向、**不摇骰**；受阻才 1/3 摇骰换向）。竖直走**随机目标高度的上下巡航**（v3，见
    /// <c>docs/plan-bird-ai.md</c> §12）：以 <see cref="CreatureSensors.ProbeSurfaceBelow"/> 探到的正下方
    /// 第一个可栖面 / 水为基准，目标高度 <see cref="Creature.TargetAltitudeCells"/> ∈
    /// [<see cref="MinAltitudeCells"/>, <see cref="MaxAltitudeCells"/>]（**名义**带顶 50；实际每张地图按
    /// <c>Min(MaxAltitudeCells, 世界高 − CeilingMarginCells − 1 − surfaceRow)</c> 的**可达净空**逐帧钳制，见
    /// <c>docs/plan-bird-ai.md</c> §18），到达即用
    /// <see cref="CreatureWorld.NextInt"/> 重掷；<c>Y &gt; 世界高 − <see cref="CeilingMarginCells"/></c> 是**天花板硬边界**
    ///（优先于高度带，无条件下压并把目标收敛到下限）；探不到基准（整列扫到世界底）则悬停。捕食视野与降落探针
    /// **仍为 24 格、不随带顶放大**（§18.2：鸟只有漂到低空才可能发现猎物 / 落树）。巡航计时归零且
    /// 正下方 <see cref="LandProbeCells"/> 格内有**可降落的树**（树叶 / 树干，见 <see cref="CreatureTerrain.IsFoliage"/>）
    /// ⇒ 下压 <c>IntentVy</c> 直到 <see cref="Creature.IsGrounded"/>（转 Perched）。**地面不落**（<c>docs/plan-bird-ai.md</c> §17）：
    /// 地面虽可站但不算树，无树的图上鸟因此永不落地，下方没有树则重置一个 <see cref="LandRetryTicks"/> 短计时继续巡航找落点。</para>
    /// <para><b>Perched（栖息）：</b><c>IntentVx = IntentVy = 0</c>；计时（<see cref="PerchTicksMin"/> +
    /// [0, <see cref="PerchTicksRange"/>]）归零即起飞（随机水平方向 + <c>IntentVy = FlyVSpeed</c>）。</para>
    /// <para><b>v1 非目标：</b>不繁殖、不筑巢、不做全局寻路。</para>
    /// <para><b>v9 生物质觅食：</b>饥饿（<see cref="HungryThreshold"/>）且冷却结束时，若世界还有生物质
    /// （<see cref="ParticleWorld.BiomassCount"/> &gt; 0）就**只**找生物质——<see cref="StateSeekFood"/> 朝
    /// 局部 <see cref="FoodDetectCells"/> 圆盘内最近的 <see cref="ParticleIds.Biomass"/> 飞，
    /// 进入 <see cref="EatRangeCells"/> 内转 <see cref="StateEat"/>，下一思考帧按固定邻格序（正下、正前、正上、左、右）
    /// 找到第一格并用 <see cref="ParticleWorld.Clear"/> 吃掉；不直接写 <c>Cells</c>。
    /// 进食恢复 <see cref="FoodRestoreTicks"/> 饥饿帧并置 <see cref="FeedingCooldown"/> 冷却。
    /// <b>全局门控：</b><see cref="ParticleWorld.BiomassCount"/> &gt; 0 时饥饿鸟**不进入 Hunt**（哪怕视野里有兔 / 鱼）；
    /// 世界生物质耗尽后才回退到既有捕猎。鸟仍然 <see cref="Creature.StarvesWithoutFood"/> = false（不因饥饿而死）。</para>
    /// <para><b>Hunt（捕食，v2；死亡骰 v5）：</b>只在 Fly 态思考帧触发——视野 <see cref="HuntDetectCells"/> 格内
    /// （盒中心欧氏距离，与放弃判据同一口径）存在猎物（兔 / 鱼）才摇 <c>1 / <see cref="HuntDenominator"/></c>
    /// 的骰，命中即转 <see cref="StateHunt"/> 并锁定 <see cref="Creature.HuntTarget"/>；**视野内无猎物不摇骰**
    /// （随机流不漂移）。已提交的 Hunt **不因饥饿打断**（饥饿分支让位于 ThinkHunt）。**世界级门控**：
    /// <see cref="ParticleWorld.BiomassCount"/> &gt; 0 时饥饿鸟的巡航搜索用
    /// <c>ThinkFly(allowHunt: false)</c>，捕猎触发根本不求值。**锁定目标的那一刻**再摇一次死亡骰
    /// （<c>1 / HuntDeathDenominator</c>）并锁存到
    /// <see cref="Creature.HuntKillLatched"/>（见 <c>docs/plan-bird-ai.md</c> §14）——追击动作不变，接触时由
    /// 运动层 ⑥b 段（AABB 重叠）**只读锁存**结算死活，运动层因此零随机数依赖，动物随机流仍只在思考帧消耗。
    /// 此后 Brain 看到「仍在本态但目标已空」即撤离回 Fly。放弃条件：目标死亡 / 移出世界、逃出 <see cref="HuntAbortCells"/>、
    /// 超时 <see cref="HuntTimeoutTicks"/>。优先级 <b>落水自救 &gt; 火焰逃命（可打断 Hunt）&gt; Hunt &gt; 生物质觅食 &gt; Fly / Perched</b>——
    /// 唯一的例外是「Hunt 态且目标是鱼」时豁免落水自救反射（鸟可以扎进水里叼鱼）。</para>
    /// </remarks>
    public static class BirdBrain
    {
        /// <summary>状态：栖息（停在可栖面上或悬停等待起飞）。</summary>
        public const byte StatePerched = 0;

        /// <summary>状态：空中巡航（高度带 + 墙感知换向；可转降落）。</summary>
        public const byte StateFly = 1;

        /// <summary>状态：逃命（背向火、向上冲，豁免最短时长内不出圈）。</summary>
        public const byte StateFlee = 2;

        /// <summary>状态：捕食（追击 <see cref="Creature.HuntTarget"/>，见 §11）。</summary>
        public const byte StateHunt = 3;

        /// <summary>状态：寻找生物质（朝局部最近的一格 <see cref="ParticleIds.Biomass"/> 飞）。</summary>
        public const byte StateSeekFood = 4;

        /// <summary>状态：进食生物质（停住并清除邻格一格生物质）。</summary>
        public const byte StateEat = 5;

        /// <summary>开始寻找生物质的饥饿阈值（逻辑帧，30 秒）。</summary>
        public const int HungryThreshold = 600;

        /// <summary>吃一格生物质恢复的饥饿帧数（与兔子吃草 / 花同值）。</summary>
        public const int FoodRestoreTicks = 420;

        /// <summary>两次进食之间的最小间隔（逻辑帧）。</summary>
        public const int FeedingCooldown = 20;

        /// <summary>生物质搜索半径（格）：局部圆盘扫描，与 <see cref="HuntDetectCells"/> 同量级。</summary>
        public const int FoodDetectCells = 24;

        /// <summary>可进食的格差范围：<c>|target − floor(pos)| ≤ 本值</c>（脚下 / 前方 / 左右）。</summary>
        public const int EatRangeCells = 1;

        /// <summary>目标连续不可接近的思考帧上限，超过即丢弃目标并重新扫描最近格。</summary>
        public const int FoodTargetFailLimit = 40;

        /// <summary>水平加速度（格 / 逻辑帧²）。</summary>
        public const float Accel = 0.02f;

        /// <summary>水中竖直加速度（格 / 逻辑帧²）：落水强起飞意图的逼近步长（同时也是意图上限幅值）。</summary>
        public const float SwimAccel = 0.1f;

        /// <summary>空中竖直加速度（格 / 逻辑帧²）：高度带 / 降落竖直意图的逼近步长。</summary>
        public const float FlyAccel = 0.05f;

        /// <summary>巡航水平速度（格 / 逻辑帧）。</summary>
        public const float FlySpeed = 0.30f;

        /// <summary>巡航竖直速度（格 / 逻辑帧）：上抬 / 下压 / 起飞的目标竖直速度。
        /// **刻意不随水平「移速」放大**：目标高度按「相对高度 == 目标」重摇，2 Hz 思考间隔内每帧 0.08 只走
        /// 0.8 格 ⇒ 等值才能命中；放大到 0.4 时单次思考间隔跨越 4 格、等值永不成立，鸟会锁死在头一次摇到的
        /// 高度上、「目标高度随机巡航」退化成固定高度来回抖（见 BirdCreatureTests 高度带用例）。</summary>
        public const float FlyVSpeed = 0.08f;

        /// <summary>逃命水**平**速度（格 / 逻辑帧）。</summary>
        public const float FleeSpeed = 0.60f;

        /// <summary>逃命上冲速度（格 / 逻辑帧）。</summary>
        public const float FleeClimbSpeed = 0.50f;

        /// <summary>巡航时维持的最小离面高度（格）：正下方可栖面 / 水面距此以内即上抬，避免贴面飞行。</summary>
        public const int MinAltitudeCells = 6;

        /// <summary>巡航高度上限（格）：相对正下方第一个可栖面 / 水的**名义**最大高度。**带顶名义 50，实际每个地图
        /// 由可达净空钳制**——运行时的 <c>maxAltitude = Min(本值, 世界高 − <see cref="CeilingMarginCells"/> − 1 − surfaceRow)</c>
        /// （不小于 <see cref="MinAltitudeCells"/>）；摇骰与「目标越界重摇」都用它，否则摇到顶到天花板的不可达目标
        /// 会在天花板分支与带底之间反复抖，见 <c>docs/plan-bird-ai.md</c> §18。</summary>
        public const int MaxAltitudeCells = 50;

        /// <summary>天花板余量（格）：<c>Y &gt; 世界高 − 本值</c> 即下压，避免顶到世界上界。</summary>
        public const int CeilingMarginCells = 12;

        /// <summary>降落探测距离（格）：正下方这么多格内出现**可降落的树**（树叶 / 树干，见 <see cref="CreatureTerrain.IsFoliage"/>）才下压降落。
        /// **§18 起刻意不随带顶 50 放大、保持 24**（用户口径）：高于 24 时探不到落点，鸟只有漂低时才降落。</summary>
        public const int LandProbeCells = 24;

        /// <summary>墙感知换向的前探格数：当前方向前方这么多格的体高行内全是非固体才认为畅通、保持原方向。</summary>
        public const int AheadProbeCells = 3;

        /// <summary>逃命脱离的半径滞回余量（格）：已在 Flee 时把威胁扫描半径放大这么多（进入用
        /// <see cref="Creature.ThreatRadius"/>，脱离用 <c>+ 本值</c>）。</summary>
        public const int FleeExitMargin = 3;

        /// <summary>最短逃命时长（逻辑帧 ≈ 2 秒）：进入逃命时写进 <see cref="Creature.StateTicksLeft"/>，
        /// 期间即使火已出圈也保持飞行，避免刚出圈就被随机方向带回。</summary>
        public const int FleeMinTicks = 40;

        /// <summary>栖息计时下限（逻辑帧 ≈ 5 秒）。</summary>
        public const int PerchTicksMin = 100;

        /// <summary>栖息计时随机幅度（逻辑帧）：实际计时 = <see cref="PerchTicksMin"/> + [0, 本值] ≈ 5~15 秒。</summary>
        public const int PerchTicksRange = 200;

        /// <summary>巡航计时下限（逻辑帧 ≈ 10 秒）。</summary>
        public const int CruiseTicksMin = 200;

        /// <summary>巡航计时随机幅度（逻辑帧）：实际计时 = <see cref="CruiseTicksMin"/> + [0, 本值] ≈ 10~30 秒。</summary>
        public const int CruiseTicksRange = 400;

        /// <summary>降落未找到落点时的短巡航计时（逻辑帧 ≈ 2 秒）。</summary>
        public const int LandRetryTicks = 40;

        /// <summary>捕食视野（格）：Fly 态思考帧上，盒中心欧氏距离 ≤ 本值才把猎物纳入候选并摇骰。
        /// **§18 起刻意不随带顶 50 放大、保持 24**（用户口径）：高度 &gt; 24 时看不见地面猎物，鸟只有漂低才捕食。</summary>
        public const int HuntDetectCells = 24;

        /// <summary>捕食触发的骰子分母：视野内有猎物时 <c>1 / 本值</c> 概率发起（视野内期望约 20 秒一次）。</summary>
        public const int HuntDenominator = 40;

        /// <summary>追击放弃距离（格）：与目标的盒中心欧氏距离 &gt; 本值即放弃（与 <see cref="HuntDetectCells"/> 同口径）。
        /// 大于视野半径 ⇒ 追击中短暂甩出视野不会立刻放弃。</summary>
        public const int HuntAbortCells = 32;

        /// <summary>追击超时（逻辑帧 ≈ 10 秒）：进入 Hunt 时写进 <see cref="Creature.StateTicksLeft"/>，归零即放弃。</summary>
        public const int HuntTimeoutTicks = 200;

        /// <summary>追击水平速度（格 / 逻辑帧）：比巡航快，逼近猎物。</summary>
        public const float HuntSpeed = 0.50f;

        /// <summary>俯冲竖直速度（格 / 逻辑帧）：比巡航竖直速度（<see cref="FlyVSpeed"/>）快，保证从带顶
        /// （<see cref="HuntDetectCells"/> / <see cref="LandProbeCells"/> 同一量级）发起的下压也能在
        /// <see cref="HuntTimeoutTicks"/> 内够到猎物。</summary>
        public const float HuntDiveSpeed = 1.0f;

        /// <summary>
        /// 鸟可见身体的顶行相对碰撞盒底边的偏移（格）：由**生成美术数据**算出
        /// （<c>CreatureArtData.Bird.MaxOpaqueRow − PivotRowPixels</c>，当前 = 10；美术变更后重跑生成菜单即随动，
        /// 本注释不写死数值以免与生成物脱节）。墙探针据此探「整个可见身体」占的行，
        /// 与 <see cref="FishBrain.BodyTopOffsetCells"/> 同一口径。
        /// </summary>
        public const int BodyTopOffsetCells = CreatureArtData.Bird.MaxOpaqueRow - CreatureArtData.Bird.PivotRowPixels;

        /// <summary>一次思考：落水自救 &gt; 威胁逃命 &gt; Hunt 捕食（不因饥饿打断）&gt; 饥饿时的生物质觅食
        /// （世界仍有生物质）&gt; Fly 巡航 &gt; Perched 栖息。世界没有生物质时饥饿鸟回退到既有 Hunt。</summary>
        /// <param name="creature">思考的鸟。</param>
        /// <param name="creatures">所属动物世界（RNG 来源）。</param>
        /// <param name="world">粒子世界（只读感知）。</param>
        public static void Think(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            // 落水自救：最高优先级——淹没时只管向上冲（强起飞意图），不再进别的状态。
            // 水平意图不动：出水前不摇骰、不换状态，配合基类正浮力几帧内即出水。
            // 唯一例外：**Hunt 态且目标是鱼**时豁免（鸟俯冲入水叼鱼）；抓到 / 放弃后反射自动恢复。
            bool divingForFish = creature.AiState == StateHunt && creature.HuntTarget is FishCreature;
            if (creature.IsSubmerged && !divingForFish)
            {
                creature.IntentVy = SwimAccel;
                return;
            }

            int cx = FloorToInt(creature.X);
            int cy = FloorToInt(creature.Y + creature.Height * 0.5f);

            bool fleeing = creature.AiState == StateFlee;
            int radius = fleeing ? Creature.ThreatRadius + FleeExitMargin : Creature.ThreatRadius;
            if (CreatureSensors.FindNearestFire(world, cx, cy, radius, out int fireX, out int fireY))
            {
                // Flee 可打断 Hunt：进逃命即清掉捕猎目标（离开 Hunt 态必须断开引用，防悬挂）。
                creature.HuntTarget = null;
                // 逃命优先：朝**最近**那团火的反方向飞（取最近而非扫描序先见者，理由同 RabbitBrain）。
                if (fireX > cx)
                {
                    creature.IntentVx = -FleeSpeed;
                }
                else if (fireX < cx)
                {
                    creature.IntentVx = FleeSpeed;
                }
                else if (!fleeing || creature.IntentVx == 0f)
                {
                    // 火恰在同列：只在刚进入逃命（或当前无逃跑方向）时摇一次骰选一侧，否则保持既有方向。
                    int dir = creatures.NextInt(2) == 0 ? -1 : 1;
                    creature.IntentVx = -dir * FleeSpeed;
                }

                creature.IntentVy = FleeClimbSpeed;
                creature.AiState = StateFlee;
                if (!fleeing)
                {
                    creature.StateTicksLeft = FleeMinTicks;
                }

                return;
            }

            if (fleeing)
            {
                // 火已（暂时）出圈：最短逃命时长未走完 → 按当前方向继续飞（不改意图）；走完 → 回 Fly 巡航。
                if (creature.StateTicksLeft > 0)
                {
                    return;
                }

                EnterFly(creature, creatures);
                return;
            }

            if (creature.AiState == StateHunt)
            {
                // 已提交的追击不因饥饿打断：击杀后下一次思考会看到尸体生物质。
                ThinkHunt(creature, creatures);
                return;
            }

            bool hungryAndReady = creature.HungerTicks >= HungryThreshold && creature.FeedingCooldownTicks == 0;
            bool hasBiomass = world.BiomassCount > 0;
            if (hungryAndReady && hasBiomass)
            {
                ThinkBiomass(creature, creatures, world, cx, cy);
                return;
            }

            // 本帧没走生物质觅食（不饥饿 / 冷却中 / 世界已无生物质）：SeekFood / Eat 是必须复位的过渡态，
            // 否则下面的状态分派会把它们当成 Perched（default 分支）。复位后继续既有状态机
            //（Fly 走既有 Hunt 触发，Perched 沿用栖息逻辑）。
            if (creature.AiState == StateSeekFood || creature.AiState == StateEat)
            {
                EnterFly(creature, creatures);
            }

            if (creature.AiState == StateFly)
            {
                ThinkFly(creature, creatures, world, cx);
                return;
            }

            ThinkPerched(creature, creatures);
        }

        /// <summary>栖息：停住；计时归零即起飞。</summary>
        private static void ThinkPerched(Creature creature, CreatureWorld creatures)
        {
            creature.AiState = StatePerched;
            creature.IntentVx = 0f;
            creature.IntentVy = 0f;

            if (creature.StateTicksLeft <= 0)
            {
                EnterFly(creature, creatures);
            }
        }

        /// <summary>空中巡航：墙感知换向（水平）+ 随机目标高度的上下巡航 / **计时降落树叶 / 树干**（地面不落；竖直，见
        /// <c>docs/plan-bird-ai.md</c> §12 / §17）。</summary>
        /// <param name="creature">思考的鸟。</param>
        /// <param name="creatures">所属动物世界（RNG 来源）。</param>
        /// <param name="world">粒子世界（只读感知）。</param>
        /// <param name="cx">感知中心格 x。</param>
        /// <param name="allowHunt">是否允许本帧摇捕食骰并转入 Hunt（默认 true，既有调用逐位不变）。
        /// 饥饿且世界仍有生物质时为 false——「有生物质就不捕猎」的全局门控。</param>
        /// <param name="allowLanding">是否允许计时降落（默认 true，既有调用逐位不变）。
        /// 搜索生物质时为 false，避免半途落树中断觅食。</param>
        private static void ThinkFly(
            Creature creature, CreatureWorld creatures, ParticleWorld world, int cx,
            bool allowHunt = true, bool allowLanding = true)
        {
            // 已落地（主动降落完成）→ 转栖息并摇下一次起飞计时。
            if (creature.IsGrounded)
            {
                EnterPerched(creature, creatures);
                return;
            }

            // 捕食触发：**只有**视野内真有猎物时才摇 1/HuntDenominator 的骰——命中即转 Hunt。
            // 视野内无猎物则连骰子都不摇（本特性因此不改变无猎物场景的随机流，既有鸟测试逐位不变）。
            // 注意 `&&` 的短路：TryDetectPrey 为假时 NextInt 不会被求值。
            if (allowHunt
                && TryDetectPrey(creature, creatures, out Creature prey)
                && creatures.NextInt(HuntDenominator) == 0)
            {
                EnterHunt(creature, prey, creatures);
                ThinkHunt(creature, creatures);
                return;
            }

            int footRow = FloorToInt(creature.Y);
            int bodyTopRow = footRow + BodyTopOffsetCells;

            // 水平：墙感知换向——前方 AheadProbeCells 格（体高行内）无固体 ⇒ 保持方向（顺带把刚起飞 /
            // 刚停住的 IntentVx = 0 起步成 FlySpeed，方向取 Facing）；受阻才 1/3 摇骰换向。
            int dir = creature.IntentVx > 0f ? 1 : (creature.IntentVx < 0f ? -1 : (creature.Facing >= 0 ? 1 : -1));
            if (IsOpenAhead(world, cx, footRow, bodyTopRow, dir))
            {
                creature.IntentVx = dir * FlySpeed;
            }
            else if (creatures.NextInt(3) == 0)
            {
                int newDir = creatures.NextInt(2) == 0 ? -1 : 1;
                creature.IntentVx = newDir * FlySpeed;
            }

            // 降落：巡航计时归零且正下方 LandProbeCells 格内有**可降落的树**（树叶 / 树干）⇒ 停止水平、下压直到落地。
            // 地面虽然可栖（perchable = true）但不算树 ⇒ 不落（v7，见 docs/plan-bird-ai.md §17）。
            if (allowLanding && creature.StateTicksLeft <= 0)
            {
                if (CreatureSensors.ProbeSurfaceBelow(
                        world, cx, footRow - 1, LandProbeCells, out _, out bool perchable, out bool foliage)
                    && perchable && foliage)
                {
                    creature.IntentVx = 0f;
                    creature.IntentVy = -FlyVSpeed;
                    return;
                }

                // 下方没有树：重置短计时继续巡航找落点（不消耗额外随机数）——无树的图上鸟因此永不落地。
                creature.StateTicksLeft = LandRetryTicks;
            }

            // 天花板是硬边界：优先于高度带，无条件下压，并把目标收敛到下限（不摇骰）。
            if (creature.Y > world.Height - CeilingMarginCells)
            {
                creature.TargetAltitudeCells = MinAltitudeCells;
                creature.IntentVy = -FlyVSpeed;
                return;
            }

            // 高度带（v3，见 docs/plan-bird-ai.md §12）：正下方第一个可栖面 / 水 = 高度基准
            //（基准**含地面与水**，不随 §17 的降落口径收窄，否则带宽在地面上无处安放）；
            // 扫到世界底仍无基准 ⇒ 悬停（与 v2 一致，不摇骰）。
            if (!CreatureSensors.ProbeSurfaceBelow(
                    world, cx, footRow - 1, world.Height, out int surfaceRow, out _, out _))
            {
                creature.IntentVy = 0f;
                return;
            }

            int altitude = (footRow - 1) - surfaceRow;
            // 上限自适应天花板（§18）：目标必须能在 Y <= 世界高 − CeilingMarginCells 的净空内达到，
            // 否则摇到不可达目标就会「爬顶 -> 天花板分支把目标重置为下限 -> 砸回带底」地反复抖。
            int maxAltitude = Max(
                MinAltitudeCells,
                Min(MaxAltitudeCells, world.Height - CeilingMarginCells - 1 - surfaceRow));
            // 目标无效（含新实体默认 0 / 超出当前净空）或已达成 ⇒ 重摇 ∈ [Min, maxAltitude]。
            int targetAltitude = creature.TargetAltitudeCells;
            if (targetAltitude < MinAltitudeCells
                || targetAltitude > maxAltitude
                || altitude == targetAltitude)
            {
                targetAltitude = MinAltitudeCells + creatures.NextInt(maxAltitude - MinAltitudeCells + 1);
                creature.TargetAltitudeCells = targetAltitude;
            }

            if (altitude < targetAltitude)
            {
                creature.IntentVy = FlyVSpeed;
                return;
            }

            if (altitude > targetAltitude)
            {
                creature.IntentVy = -FlyVSpeed;
                return;
            }

            creature.IntentVy = 0f; // 重摇恰好抽到当前高度：悬停一个思考帧，下帧再摇
        }

        /// <summary>饥饿生物质觅食分支：已在 Eat 态则执行进食；否则统一走 SeekFood 决策。</summary>
        /// <remarks>
        /// 只有 <see cref="Think"/> 在「饥饿且冷却结束、且 <see cref="ParticleWorld.BiomassCount"/> &gt; 0」时进来，
        /// 因此这里不需要再判饥饿 / 冷却。RNG 仍只在思考帧消耗（<see cref="EnterFly"/> 的两次抽取）。
        /// </remarks>
        private static void ThinkBiomass(
            Creature creature, CreatureWorld creatures, ParticleWorld world, int cx, int cy)
        {
            if (creature.AiState == StateEat)
            {
                if (ThinkEatBiomass(creature, creatures, world))
                {
                    return;
                }

                // 邻格没有生物质（已被吃掉 / 被埋）：回 SeekFood，下一思考帧重新扫描。
                creature.AiState = StateSeekFood;
                creature.FoodTargetX = Creature.NoFoodTarget;
                creature.FoodTargetY = Creature.NoFoodTarget;
                return;
            }

            if (creature.AiState != StateSeekFood)
            {
                // 从 Fly / Perched 进入觅食：清掉旧目标，交给 ThinkSeekBiomass 统一扫描。
                creature.AiState = StateSeekFood;
                creature.FoodTargetX = Creature.NoFoodTarget;
                creature.FoodTargetY = Creature.NoFoodTarget;
                creature.FoodTargetFailTicks = 0;
            }

            ThinkSeekBiomass(creature, creatures, world, cx, cy);
        }

        /// <summary>
        /// SeekFood 的每思考帧决策：目标失效则重扫；进入进食范围转 Eat；否则朝目标飞；
        /// 连续 <see cref="FoodTargetFailLimit"/> 帧未达成即丢弃目标、重扫最近格（世界里还有生物质就不放弃觅食）。
        /// </summary>
        private static void ThinkSeekBiomass(
            Creature creature, CreatureWorld creatures, ParticleWorld world, int cx, int cy)
        {
            if (!ResolveBiomassTarget(creature, world, cx, cy))
            {
                SearchWithoutTarget(creature, creatures, world, cx);
                return;
            }

            // 最多走两遍：第二遍只发生在「失败计数超限 ⇒ 丢目标 + 重扫」之后，避免递归 / 死循环。
            for (int attempt = 0; ; attempt++)
            {
                int tx = creature.FoodTargetX;
                int ty = creature.FoodTargetY;
                int feetX = FloorToInt(creature.X);
                int feetY = FloorToInt(creature.Y);

                if (Abs(tx - feetX) <= EatRangeCells && Abs(ty - feetY) <= EatRangeCells)
                {
                    creature.FoodTargetFailTicks = 0;
                    creature.AiState = StateEat;
                    // 停住：水平与竖直意图都清零。鸟的竖直意图是持续状态（FlyAccel 逼近），
                    // 只清 IntentVx 的话下压 / 上抬会在进食思考间隔里继续生效、可能漂出进食范围。
                    creature.IntentVx = 0f;
                    creature.IntentVy = 0f;
                    return;
                }

                creature.FoodTargetFailTicks++;
                if (creature.FoodTargetFailTicks > FoodTargetFailLimit && attempt == 0)
                {
                    // 目标长期不可达（被埋 / 卡在墙后）：丢弃并重扫最近的生物质。
                    creature.FoodTargetX = Creature.NoFoodTarget;
                    creature.FoodTargetY = Creature.NoFoodTarget;
                    creature.FoodTargetFailTicks = 0;
                    if (!ResolveBiomassTarget(creature, world, cx, cy))
                    {
                        SearchWithoutTarget(creature, creatures, world, cx);
                        return;
                    }

                    continue;
                }

                // 水平朝目标列；竖直以脚底行 ± EatRangeCells 为死区（范围内不产生竖直意图）。
                if (tx > creature.X)
                {
                    creature.IntentVx = FlySpeed;
                }
                else if (tx < creature.X)
                {
                    creature.IntentVx = -FlySpeed;
                }
                else
                {
                    creature.IntentVx = 0f;
                }

                int footRow = FloorToInt(creature.Y);
                if (ty > footRow + EatRangeCells)
                {
                    creature.IntentVy = FlyVSpeed;
                }
                else if (ty < footRow - EatRangeCells)
                {
                    creature.IntentVy = -FlyVSpeed;
                }
                else
                {
                    creature.IntentVy = 0f;
                }

                return;
            }
        }

        /// <summary>
        /// 确保 <see cref="Creature.FoodTargetX"/> / <see cref="Creature.FoodTargetY"/> 指向一格仍在界的生物质；
        /// 目标无效时用 <see cref="CreatureSensors.FindNearestBiomass"/>（半径 <see cref="FoodDetectCells"/>）
        /// 重扫，扫到即写入目标并清零失败计数。纯感知：不写世界、不消耗随机流。
        /// </summary>
        private static bool ResolveBiomassTarget(Creature creature, ParticleWorld world, int cx, int cy)
        {
            int tx = creature.FoodTargetX;
            int ty = creature.FoodTargetY;
            bool targetValid = tx != Creature.NoFoodTarget
                && world.IsInBounds(tx, ty)
                && world.GetCell(tx, ty).typeId == ParticleIds.Biomass;
            if (targetValid)
            {
                return true;
            }

            if (!CreatureSensors.FindNearestBiomass(world, cx, cy, FoodDetectCells, out tx, out ty))
            {
                creature.FoodTargetX = Creature.NoFoodTarget;
                creature.FoodTargetY = Creature.NoFoodTarget;
                return false;
            }

            creature.FoodTargetX = tx;
            creature.FoodTargetY = ty;
            creature.FoodTargetFailTicks = 0;
            return true;
        }

        /// <summary>
        /// 视野圆盘内已没有生物质：栖息 / 贴地的鸟给起飞意图离地搜索；已在空中则继续巡航，
        /// 但**禁止捕猎与降落**（世界仍有生物质 ⇒ 饥饿鸟不触发 Hunt，也不该半途落树中断觅食）。
        /// </summary>
        private static void SearchWithoutTarget(
            Creature creature, CreatureWorld creatures, ParticleWorld world, int cx)
        {
            if (creature.IsGrounded)
            {
                EnterFly(creature, creatures);
                return;
            }

            ThinkFly(creature, creatures, world, cx, allowHunt: false, allowLanding: false);
        }

        /// <summary>
        /// Eat 的每思考帧决策：停住，按固定枚举顺序在邻格找第一格生物质并清除；找不到返回 false
        /// （调用方回 SeekFood 重扫）。顺序写死（正下、正前、正上、左、右），与 <see cref="RabbitBrain"/> 同序。
        /// </summary>
        private static bool ThinkEatBiomass(Creature creature, CreatureWorld creatures, ParticleWorld world)
        {
            creature.IntentVx = 0f;
            creature.IntentVy = 0f;

            int cx = FloorToInt(creature.X);
            int cy = FloorToInt(creature.Y);
            int front = creature.Facing >= 0 ? 1 : -1;

            return TryEatBiomass(creature, creatures, world, cx, cy - 1)       // 正下方
                || TryEatBiomass(creature, creatures, world, cx + front, cy)   // 正前方（朝向侧）
                || TryEatBiomass(creature, creatures, world, cx, cy + 1)       // 正上方
                || TryEatBiomass(creature, creatures, world, cx - 1, cy)       // 左
                || TryEatBiomass(creature, creatures, world, cx + 1, cy);      // 右
        }

        /// <summary>若 <c>(gx,gy)</c> 是生物质则清除一格、恢复饥饿、置冷却、清目标并起飞，返回 true。
        /// <see cref="ParticleWorld.Clear"/> 是吃生物质的**唯一**写入路径（不直接改 <c>Cells</c>）。</summary>
        private static bool TryEatBiomass(
            Creature creature, CreatureWorld creatures, ParticleWorld world, int gx, int gy)
        {
            if (!world.IsInBounds(gx, gy) || world.GetCell(gx, gy).typeId != ParticleIds.Biomass)
            {
                return false;
            }

            world.Clear(gx, gy);
            creature.HungerTicks -= FoodRestoreTicks;
            if (creature.HungerTicks < 0)
            {
                creature.HungerTicks = 0;
            }

            creature.FeedingCooldownTicks = FeedingCooldown;
            creature.FoodTargetX = Creature.NoFoodTarget;
            creature.FoodTargetY = Creature.NoFoodTarget;
            creature.FoodTargetFailTicks = 0;
            EnterFly(creature, creatures);
            return true;
        }

        /// <summary>视野扫描：在 <paramref name="creatures"/> 里找**最近的**猎物（兔 / 鱼）。按生成序扫描，
        /// 只有严格更近才替换 ⇒ 距离平手时保留先生成者（生成序小者）。纯只读、零分配、不消耗随机流。</summary>
        /// <param name="creature">观察者（鸟）。</param>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="nearest">命中的最近猎物；未命中为 null。</param>
        /// <returns>视野 <see cref="HuntDetectCells"/> 格内存在猎物时 true。</returns>
        private static bool TryDetectPrey(Creature creature, CreatureWorld creatures, out Creature nearest)
        {
            const float detectSq = HuntDetectCells * HuntDetectCells;
            nearest = null;
            float bestSq = 0f;
            bool found = false;

            int count = creatures.Count;
            for (int i = 0; i < count; i++)
            {
                Creature other = creatures.Creatures[i];
                if (other == null || ReferenceEquals(other, creature) || !other.Alive)
                {
                    continue;
                }

                if (!(other is RabbitCreature) && !(other is FishCreature))
                {
                    continue;
                }

                float sq = DistanceSquared(creature, other);
                if (sq <= detectSq && (!found || sq < bestSq))
                {
                    bestSq = sq;
                    nearest = other;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>进入 Hunt：锁定目标、写超时计时（<see cref="Creature.StateTicksLeft"/> 复用为追击超时），
        /// 并在**锁定这一刻**摇一次死亡骰锁存到 <see cref="Creature.HuntKillLatched"/>
        /// （v5，见 <c>docs/plan-bird-ai.md</c> §14）——之后追击动作不变、接触时由运动层只读锁存结算。</summary>
        private static void EnterHunt(Creature creature, Creature prey, CreatureWorld creatures)
        {
            creature.AiState = StateHunt;
            creature.HuntTarget = prey;
            creature.StateTicksLeft = HuntTimeoutTicks;
            // 死亡骰：锁定目标时摇一次并锁存，之后追击动作不变、接触时按锁存结果结算。
            // 分母 <= 1 ⇒ 必杀且不摇骰（保持「必死」物种的随机流不变）；骰子放在思考帧，守住
            // 「动物随机流只在思考帧消耗」的纪律，运动层因此零随机数依赖。
            creature.HuntKillLatched = prey.HuntDeathDenominator <= 1
                || creatures.NextInt(prey.HuntDeathDenominator) == 0;
        }

        /// <summary>Hunt 态思考帧：先判撤离 / 放弃，否则重瞄目标转向。
        /// 判据顺序即语义——「目标已空」只可能来自运动层 ⑥b 刚把它咬死，所以先判它并走**撤离**出口。</summary>
        private static void ThinkHunt(Creature creature, CreatureWorld creatures)
        {
            Creature target = creature.HuntTarget;

            // 运动层刚在本帧 ⑥b 把目标咬死（HuntTarget 已被清空）⇒ 撤离：攻击完就飞走，不停留。
            if (target == null)
            {
                ExitHunt(creature, creatures, retreat: true);
                return;
            }

            // 目标已死 / 已被移出世界 ⇒ 放弃。
            if (!target.Alive || !Contains(creatures, target))
            {
                ExitHunt(creature, creatures, retreat: false);
                return;
            }

            // 超时（<see cref="HuntTimeoutTicks"/>）或追丢（逃出 <see cref="HuntAbortCells"/>）⇒ 放弃。
            if (creature.StateTicksLeft <= 0 || DistanceSquared(creature, target) > HuntAbortCells * HuntAbortCells)
            {
                ExitHunt(creature, creatures, retreat: false);
                return;
            }

            // 追击转向：水平朝目标所在列（同列保持原方向），竖直按目标高低下压 / 缓升。
            int targetCol = FloorToInt(target.X);
            int selfCol = FloorToInt(creature.X);
            if (targetCol > selfCol)
            {
                creature.IntentVx = HuntSpeed;
            }
            else if (targetCol < selfCol)
            {
                creature.IntentVx = -HuntSpeed;
            }

            creature.IntentVy = target.Y < creature.Y ? -HuntDiveSpeed : FlyVSpeed * 0.5f;
        }

        /// <summary>离开 Hunt 态的唯一出口：回 Fly、清目标（防悬挂）、摇新巡航计时（与起飞同口径）。
        /// <paramref name="retreat"/> 为真（刚击杀的撤离）时立刻向上冲 <c>IntentVy = FlyVSpeed</c>（不停留）；
        /// 为假（放弃）时不起飞冲，<c>IntentVy = 0</c> 交给高度带接管。</summary>
        private static void ExitHunt(Creature creature, CreatureWorld creatures, bool retreat)
        {
            creature.HuntTarget = null;
            creature.HuntKillLatched = false; // 复位命中锁存，防残留影响下一次追击
            creature.AiState = StateFly;
            creature.StateTicksLeft = CruiseTicksMin + creatures.NextInt(CruiseTicksRange);
            creature.IntentVy = retreat ? FlyVSpeed : 0f;
        }

        /// <summary>目标是否仍在世界里（针对「被移除 ⇒ 放弃」这一条；正常死亡在移除前 <see cref="Creature.Alive"/> 即为假）。</summary>
        private static bool Contains(CreatureWorld creatures, Creature target)
        {
            int count = creatures.Count;
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(creatures.Creatures[i], target))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>两实体**碰撞盒中心**的欧氏距离平方（省一次开方）。触发视野与放弃判据共用本口径。</summary>
        private static float DistanceSquared(Creature a, Creature b)
        {
            float dx = b.X - a.X;
            float dy = (b.Y + b.Height * 0.5f) - (a.Y + a.Height * 0.5f);
            return (dx * dx) + (dy * dy);
        }

        /// <summary>进入 Fly：随机水平方向、向上起飞、摇巡航计时（≈10~30 秒）。</summary>
        private static void EnterFly(Creature creature, CreatureWorld creatures)
        {
            creature.AiState = StateFly;
            creature.StateTicksLeft = CruiseTicksMin + creatures.NextInt(CruiseTicksRange);
            int dir = creatures.NextInt(2) == 0 ? -1 : 1;
            creature.IntentVx = dir * FlySpeed;
            creature.IntentVy = FlyVSpeed;
        }

        /// <summary>进入 Perched：停住并摇下一次起飞计时（≈5~15 秒）。</summary>
        private static void EnterPerched(Creature creature, CreatureWorld creatures)
        {
            creature.AiState = StatePerched;
            creature.IntentVx = 0f;
            creature.IntentVy = 0f;
            creature.StateTicksLeft = PerchTicksMin + creatures.NextInt(PerchTicksRange);
        }

        /// <summary>当前方向前方 <see cref="AheadProbeCells"/> 格是否一路畅通：体高行（脚底行 .. 体顶行）内
        /// 每一格都**不是固体**才算畅通（树叶不挡水平，因此可穿冠）。越界格一律视为非固体。</summary>
        private static bool IsOpenAhead(ParticleWorld world, int cx, int footRow, int bodyTopRow, int dir)
        {
            for (int k = 1; k <= AheadProbeCells; k++)
            {
                int x = cx + dir * k;
                for (int row = footRow; row <= bodyTopRow; row++)
                {
                    if (CreatureTerrain.IsSolid(world, x, row))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>向下取整（不走 Mathf，保持决策路径零 UnityEngine 依赖）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        /// <summary>整数绝对值（同 <see cref="Min"/>：手写以守住决策路径的零依赖）。</summary>
        private static int Abs(int value)
        {
            return value < 0 ? -value : value;
        }

        /// <summary>两整数取小（与 <c>FishBrain.Min</c> 同做法：手写以保持决策路径零 UnityEngine / 零 System 依赖）。</summary>
        private static int Min(int a, int b)
        {
            return a < b ? a : b;
        }

        /// <summary>两整数取大（同 <see cref="Min"/>：手写以守住决策路径的零依赖）。</summary>
        private static int Max(int a, int b)
        {
            return a > b ? a : b;
        }
    }
}
