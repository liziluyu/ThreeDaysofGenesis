using UnityEngine;

namespace TapTap2026.Simulation
{
    // WaterParticle —— 水（液体标签，摩擦度/粘性不读，填 0；热容 6.5，约为固体的 7~10 倍）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下、同行左右邻格、左右各 MaxFlowSpread 格（落差视野扫描）、
    //               正下连续最多 CanopyPassMaxDepth 格（穿冠扫描，见行为第 0 段）、
    //               正上方连续最多 IceParticle.ColumnScanDepth 格（结冰的深度扫描，见冷端相变）
    // 交互的 ParticleIds：TreeLeaf（穿冠判据——正下方连续叶柱；失活落叶同样算）、
    //                    Ice（结冰目标形态，见随机刻的冷端相变）——
    //                    蒸发把水汽交给湿度场（HumidityField），水汽不以粒子形式存在
    //
    // 行为：相变分**冷热两端**，且走**两条不同的时基**（温度区间不相交：冷端 ≤ FreezingPoint(4.0)、
    //       热端 ≥ SlowEvaporationTemperature(20)）：
    //       冷端——**结冰**（TryFreeze）**走随机刻**（OnRandomTick）：它是"缓慢、按概率发生"的过程，
    //         稀有度由世界的随机刻抽取率给出（生产 48 格/逻辑帧）。水面（正上方界内气体/空、
    //         且正下方落定）在低温下冻结成冰——挨着冰照冰点 4.0 蔓延，没有冰邻格要过冷到 3.5 成核，
    //         正上方压着冰则按几何梯度 `4.0·0.5^d` 逐层向下加厚（越深越接近环境 0）。
    //         整条门槛再乘**光照冷度缩放**（LightColdScale：强度 8~12 = ×1；更弱 → 最多 ×1.2「更易结冰」；
    //         更强 → 最低 ×0.6「更难结冰」，两端都随强度连续变化）。
    //         结冰放热（潜热 + 热容差，见 IceParticle.FusionTemperatureStep）。
    //       热端——**蒸发**（走逻辑帧 OnLogic，下列两条路）：
    //         · 沸腾（T ≥ 沸点）：把 1 份水汽**集中**在原地一小片 → 每格浓度高 → 渲染成可见的白色汽团；
    //         · 慢蒸发（T ≥ 常温阈值、按概率、且**正上方是界内气体**）：把同样 1 份水汽**摊到 9×9 一大片**
    //           （半径 4）→ 每格低于可见阈值 → 看不见，只让湿度稳步升高。摊不够薄（接收格太少，
    //           单格份额会超过 InvisibleSpreadMaxShare）时**不蒸发、水保留**——否则半封闭地形会凭空
    //           冒出可见水汽（审计 B2）。
    //       这就是"水蒸气只在沸腾时出现"：可见的水汽只由沸腾产生，慢蒸发是隐形的。
    //       潜热：蒸发（沸腾与慢蒸发）从水里**带走** HumidityField.LatentHeat(T)（随温度线性）的热量，
    //       海面因此被蒸发冷却；这份潜热由湿度单位携带、随降雨按凝结温度归还（见 HumidityField.LatentHeat）。
    //       运动为「一道穿冠门 + 四段固定优先级」，任一成功即 return：
    //         0. 穿冠（雨水穿冠）：正下方一格是树叶（ParticleIds.TreeLeaf）时，沿本列竖直向下扫过
    //            **连续**叶柱（最多 CanopyPassMaxDepth = 8 格，覆盖最大连续叶柱；超限放弃），
    //            停在第一格非树叶；该格在界内且是气体 / 空 → Swap 本格与它：水落到冠下、
    //            被顶开的气体换上冠顶（质量守恒，Swap 自带候选唤醒与气体绝热结算）。
    //            叶柱下方是干 / 土 / 水 / 越界等一律放弃，随即回到下面四段——水从冠沿横流走掉是
    //            既有行为，保持不变。失活叶（下落中 / 已落地的树叶）同样可穿：水从落叶堆上渗下去，
    //            无需特判。纯确定性扫描，**零随机流消耗**（运动路径铁律）、零分配；
    //         1. TryFall：正下能进（空气 / 气体）就下落一格；同类水与固体都挡住；
    //         2. TryDiagonalFall（clearance 1）：正下被挡就沿坡斜滑——液体无安息角；
    //         3. TryFlow：每 tick 只横移 1 格（60 Hz 下足够快，不像多格瞬移那样刺眼），
    //            左右随机序，且必须过两道闸门之一——(a) 身后有推：反侧同行邻格是液体，
    //            连片水体把多余的水送往两侧、让低于水面的洞被逐格填平（摊平全靠它）；
    //            (b) 落差视野：本侧 MaxFlowSpread 格内存在「正下方还能继续落」的格，
    //            往低处 / 洞口铺（越界不算落差，底行上的水滴也能停住）。
    //            两闸都不满足 = 往哪边流都不会更低（孤立水滴躺在平地 / 满水面上）→ 静止。
    //            已知几何限制：水面「差一格」的态可能永久静止——该洞距任何一格水都超过
    //            MaxFlowSpread、且洞两侧的水都没触发「身后有推」时，远处的孤水看不到更低处。
    //            洞距 ≤ MaxFlowSpread 时孤水会追上来填掉它；差一格态是极限环的一侧（另一侧就是
    //            「孤立水滴在平地上静止」），两者同源，不是 bug。
    //            旧的「见空格就跳」让平地水滴以 60 Hz 原地随机瞬移、永不静止（观感回归）；
    //         4. TryLiquidConvect：前三段都动不了时，试一次液体热对流——比正上方暖 ε
    //            （LiquidConvectionEpsilon）就上浮一格（海洋翻转：白天顶加热是稳定分层、不翻转，
    //            热靠传导存进水柱；夜晚表面冷、深处暖，暖水持续上浮把存的热带回表面，
    //            是海面夜间比陆地暖、陆风得以涌现的机制）。
    //       四段都走不通则本 tick 静止（比如躺在水底）。
    //       跨类型交互全部由标签 rank 产生：固体（rank 3）沉穿水，水被置换上浮——
    //       但**静止**的固体不能（见 ParticleRules.CanDisplace 的地下水闸门）；水因此能在石头 / 沙里
    //       成为不动的「地下水」（土例外：干土会按既有土壤反应把正上方的水吸成湿土）；
    //       同类水互不置换但横流时可互相穿过（CanFlowThrough 对流体放行同 rank）。
    //
    //       随机刻（OnRandomTick）做两件事，按此顺序：
    //         ① **结冰**（冷端相变，见上）：够冷的水面冻成冰，冻上了就 return；
    //         ② **冲刷**：找到半径 SandifyingRadius（2）格圆盘内的**第一个**土壤 / 湿土 /
    //            腐殖土后，按 1/ErosionChanceDenominator（= 1/5 = 20%）的概率把它 SetPhase 成沙
    //            （固定扫描序 y 升序、同行 x 升序；先判目标再摇骰，无土可冲时不消耗随机流）。
    //       随机刻默认关闭（`ParticleWorld.RandomTicksPerStep = 0`）：两条行为都只在生产的
    //       随机刻格数注入后才发生，生产值由 Settings 注入（现配 48）。
    public sealed class WaterParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(52, 108, 196, 255),
            new Color32(62, 122, 210, 255),
            new Color32(74, 136, 222, 255),
            new Color32(88, 150, 232, 255),
        };

        /// <summary>
        /// 水的热容 C = 6.5：固体（0.8）的 8 倍上下——水升温最慢、最「扛热」，海陆温差（海风）
        /// 与水面昼夜惰性因此更明显。用命名常量而不是字面量：冰的相变结算要按**水相热容**算潜热
        /// （见 <see cref="IceParticle.HeatCapacityDelta"/> 族的推导），两处必须是同一个数。
        /// </summary>
        public const float HeatCapacity = 6.5f;

        /// <summary>
        /// 沸点：本格温度达到它就剧烈蒸发成水汽（并把水汽集中成可见的白色汽团）。
        /// 现为 80：空气辐射率降到 0.1 之后水面整体变暖（水盆可达 ~99°、浅水洼 ~70°），
        /// 40 会让浅水在阳光下当场爆沸晒干。80 之下**浅水洼晒不到沸点**（只慢蒸发），
        /// 只有深水最热的局部能在正午擦到沸点——剧烈蒸发留给真正的热源与极浅的薄水。
        /// 注意水温还取决于热容：热容越大，同样的光照 Q 下升温越慢（见 <c>WaterParticle</c> 的 <c>heatCapacity</c>）。
        /// </summary>
        public const float BoilingPoint = 80f;

        /// <summary>常温蒸发下限：达到它之后即使不到沸点也缓慢蒸发（现实里水在任何温度都蒸发）。
        /// 取 20（远低于沸点 80）：水面常温就在 20~30° 附近，因此低层大气始终有稳定的水汽来源，
        /// 这也是水循环的主要供料路径——剧烈蒸发只在真正过热时补充。</summary>
        public const float SlowEvaporationTemperature = 20f;

        /// <summary>常温蒸发概率分母（1/N 每逻辑帧；逻辑帧频率低于运动帧，等效速率按逻辑帧算）。</summary>
        public const int SlowEvaporationDenominator = 300;

        /// <summary>剧烈蒸发（沸腾）的播撒半径：1 = 3×3 一小片，每格浓度高 → 渲染成**可见**的白色汽团。</summary>
        private const int BoilingSpreadRadius = 1;

        /// <summary>TryFlow 的 (x,y,Tick) 坐标哈希 salt（见 <see cref="HashNoise"/>）。
        /// 取 6：与 ParticleRules 的五个运动 salt 互不相同，横流方向与斜落 / 斜滑等流互不相关。</summary>
        private const uint FlowSalt = 6u;

        /// <summary>
        /// 穿冠扫描的最大深度：正下方**连续**树叶超过这么多格就不传送。
        /// 取 8 覆盖最大的连续叶柱——杨树冠 dx = 0 那一列是 7 格叶，留一格余量。
        /// 同时决定运动阅读盒的下探距离（<c>down = 1 + 本值</c>）。
        /// </summary>
        public const int CanopyPassMaxDepth = 8;

        /// <summary>
        /// 液体热对流的温度死区：本格比正上方的水暖超过它，本格才上浮（对流搅动）。
        /// 取 0.5：水的导热极好（σ = 0.95）、水体内部本就接近等温，格间残留温差往往只有零点几度；
        /// 阈值偏大（如 2）会让柱内积起逆温层、翻转迟滞，深处的热迟迟回不到表面；
        /// 偏小（如 0.1）又会让等温面上的浮点噪声触发无意义的换位。0.5 让「上冷下暖」一出现就翻转。
        /// </summary>
        public const float LiquidConvectionEpsilon = 0.5f;

        /// <summary>
        /// 水面结冰阈值：水面（正上方是界内气体 / 空、且正下方落定）温度降到它，冰就能沿已有冰缘蔓延过来。
        /// 取 4.0：**实测标定**——玩家世界里水体只在刚放置那一刻是 0°，夜间稳定在 **3° 上下**
        /// （见 `IceParticleTests.ColdPond_*` 的探针思路与文档 §13），所以「够冷」的门必须落在 3° 之上：
        /// 4.0 让夜间水面必定进得来，而白天水面 20~30°（见 <see cref="SlowEvaporationTemperature"/>）
        /// 永远进不来。世界本身的温度下界仍是 0（<see cref="HeatMap.AmbientHeat"/>），深度梯度因此
        /// 只能用几何衰减逼近它（见 <see cref="FreezeDepthDecay"/>）。
        /// </summary>
        public const float FreezingPoint = 4f;

        /// <summary>
        /// 成核超冷：**没有**冰邻格的水面要再冷这么多才可能自己冻出第一个点
        /// （4.0 − 0.5 = 3.5，见 <see cref="TryFreeze"/> 的成核分支）。
        /// 现实里纯水需要过冷才自发成核，已有冰缘则只是「长大」——这条差异让结冰从零星几点开始，
        /// 而不是整片水面在同一刻齐刷刷冻住（「从一点或一部分开始结冰」）。
        /// 取 0.5：夜间水温在 3° 上下浮动，0.5° 的差足够让「最先凉下来的那几格」先成核，
        /// 又不会因为门太高而整夜冻不出来；稀有度主要由随机刻的命中率把着（见 <see cref="OnRandomTick"/>）。
        /// </summary>
        public const float NucleationSupercooling = 0.5f;

        /// <summary>
        /// 结冰的深度梯度：每深一层（正上方多一格冰），需要的温度乘这个系数（几何衰减）。
        /// 取 0.5：阈值 4.0 → 2.0 → 1.0 → 0.5 → …（见 <see cref="FreezeThresholdAtDepth"/>）。
        /// 只能用几何衰减而不是线性减法——温度下界是环境 0，线性梯度几层后就穿到 0 以下、把厚度硬卡死；
        /// 几何梯度是「越深越接近环境 0」，而冰下的水会一直被抽冷下去，因此最后一档永远够得着：
        /// 厚度没有上限（用户要求），只是越来越慢。
        /// </summary>
        public const float FreezeDepthDecay = 0.5f;

        /// <summary>
        /// 冰下第 <paramref name="depth"/> 层（正上方压着 depth 格冰）的结冰阈值 =
        /// <c>FreezingPoint · FreezeDepthDecay^depth</c>。<paramref name="depth"/> 由
        /// <see cref="IceParticle.DepthBelowIceTop"/> 数出，上限 <see cref="IceParticle.ColumnScanDepth"/>
        /// （超出按最后一档处理——扫描有界，梯度不无界细分）。
        /// <para>这是**不含光照缩放**的纯梯度；运行时请用 <see cref="FreezeThresholdAt"/>。</para>
        /// </summary>
        public static float FreezeThresholdAtDepth(int depth)
        {
            float threshold = FreezingPoint;
            for (int i = 0; i < depth; i++)
            {
                threshold *= FreezeDepthDecay;
            }

            return threshold;
        }

        /// <summary>光照强度的**适宜区间下沿**：玩家把强度调在 [8, 12] 内时冷度缩放 = 1（结冰 / 融化门槛取标定值）。</summary>
        public const float LightComfortIntensityMin = 8f;

        /// <summary>适宜区间上沿（与 <see cref="LightComfortIntensityMin"/> 一起构成「温度适宜」的平稳段）。</summary>
        public const float LightComfortIntensityMax = 12f;

        /// <summary>
        /// 冷度缩放的**上限**：光照最弱（强度 0）时，冰点与融点整体乘它——更早结冰、现有冰更难化。
        /// 取 1.2 而不是更大：迟滞带 <c>[4.0·s, 6.0·s]</c> 必须始终宽于相变造成的 ±1.0° 摆动
        /// （4.0·1.2 + 1.0 = 5.8 &lt; 6.0·1.2 = 7.2 ✓）；再大就会让同一格反复相变。
        /// </summary>
        public const float LightColdScaleMax = 1.2f;

        /// <summary>
        /// 冷度缩放的**下限**：光照强到适宜上沿的 2 倍（= 24）及以上时取它——更难结冰、现有冰更快化。
        /// 取 0.6：迟滞带缩到 2.0·0.6 = 1.2°，仍宽于 ±1.0 的摆动（融点 3.6 − 1.0 = 2.6 &gt; 冰点 2.4 ✓）。
        /// </summary>
        public const float LightColdScaleMin = 0.6f;

        /// <summary>
        /// 光照强度 → **冷度缩放**：整个水 ⇄ 冰相变（结冰门槛、成核门槛、加厚梯度、融化门槛）都乘它。
        /// 强度落在适宜区间 [8, 12] 内 → 1（标定值不变）；更弱 → 线性升到 <see cref="LightColdScaleMax"/>
        /// （「越弱越像冬天」：更早冻、更难化）；更强 → 线性降到 <see cref="LightColdScaleMin"/>，
        /// 在 2×上沿（24）饱和（「越强越像夏天」：难冻、快化）。两侧都**随强度连续变化**。
        /// </summary>
        /// <remarks>
        /// 看的是**配置强度**（光照面板上玩家设的「强度」，也就是这个世界的气候设定），
        /// 不是瞬时亮度：夜间灯光关闭时瞬时亮度恒为 0，按瞬时值缩放会让「夜晚 = 最冷」压过昼夜循环本身。
        /// 玩家想更冷就把强度调低、想更热就调高——这正是把气候旋钮交出去的地方。
        /// </remarks>
        public static float LightColdScale(float lightIntensity)
        {
            if (lightIntensity >= LightComfortIntensityMin && lightIntensity <= LightComfortIntensityMax)
            {
                return 1f; // 适宜区间：结冰 / 融化门槛取标定值
            }

            if (lightIntensity < LightComfortIntensityMin)
            {
                // 0 → 1（最冷），8 → 0（回到适宜）；负数（非法输入）按 0 处理。
                float t = 1f - Mathf.Max(lightIntensity, 0f) / LightComfortIntensityMin;
                return 1f + (LightColdScaleMax - 1f) * t;
            }

            // 12 → 0，24（= 2×上沿）→ 1 并在更热处饱和。
            float hot = (lightIntensity - LightComfortIntensityMax) / LightComfortIntensityMax;
            if (hot > 1f)
            {
                hot = 1f;
            }

            return 1f + (LightColdScaleMin - 1f) * hot;
        }

        /// <summary>
        /// 本世界当前生效的**结冰阈值**（第 <paramref name="depth"/> 层，含光照冷度缩放）：
        /// <c>FreezeThresholdAtDepth(depth) · LightColdScale(world.Light.Intensity)</c>。
        /// <c>depth = 0</c> 即「沿冰缘蔓延」那一档的门槛；加厚用 depth ≥ 1。世界的实际判定与测试共用它。
        /// </summary>
        public static float FreezeThresholdAt(ParticleWorld world, int depth)
        {
            return FreezeThresholdAtDepth(depth) * LightColdScale(world.Light.Intensity);
        }

        /// <summary>
        /// 本世界当前生效的**成核门槛**（无冰邻格时的过冷门，含光照冷度缩放）：
        /// <c>(FreezingPoint − NucleationSupercooling) · LightColdScale(…)</c>。
        /// </summary>
        public static float FreezeNucleationThresholdAt(ParticleWorld world)
        {
            return (FreezingPoint - NucleationSupercooling) * LightColdScale(world.Light.Intensity);
        }

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Liquid,
            density: 1.0f,
            heatCapacity: HeatCapacity,   // 水：热容再调大——升温最慢、最"扛热"，海陆温差（海风）与水面昼夜惰性更明显
            thermalConductivity: 0.95f,   // 水：导热最好，水体内部接近等温
            thermalEmissivity: 1.0f,      // 水是近黑体（红外吸收极强）：水面照常向外散热
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 5,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: "水",
            // 运动阅读盒：TryFlowSide 的「落差视野」逐格扫到 x ± MaxFlowSpread(= 5) 并读 (scanX, y−1)
            // 是否有气体；TryLiquidConvect 读 (x, y+1) 是否也是液体；穿冠扫描沿正下扫过连续叶柱、
            // 并读叶柱下的第一格 ⇒ 向下 = 1（正下）+ CanopyPassMaxDepth(= 8) = 9。
            motionReadReach: new MotionReadBox(x: 5, up: 1, down: 1 + CanopyPassMaxDepth),
            hasSurfaceHighlight: true); // 渲染提亮液面：正上方不是液体的水格向浅蓝白插值

        /// <summary>
        /// 水蒸发：**提升附近的湿度**（以本格为中心、半径 <paramref name="radius"/> 的气体/空格，总量与
        /// <see cref="HumidityField.Unit"/> 相等），本格变成大气。湿度场随后按「扩散 + 水平冷→热 + 竖直越热越向上」把水汽输送走。
        /// 水汽没有粒子形态（现实里水汽本来就不可见）——云是湿度场里的高值区，见 <see cref="HumidityField"/>。
        /// <para>顺序是「**播撒必成功才相变**」：本格相变后就是气体，也算一个接收格，故先按
        /// <c>接收格数 + 1</c> 预判份额；超出 <paramref name="maxShare"/> 时**不播撒、不蒸发、返回 false**（水保留）。
        /// 预判通过后再 <see cref="ParticleWorld.SetPhase"/>（本格变大气，多出的那个接收者正是本格），
        /// 随后播撒——此时 <see cref="HumidityField.SpreadUnitIntoAir"/> 数到的接收格数与本格预判一致，必定成功。
        /// 本格分到的那一份湿度因 <see cref="ParticleWorld.SetPhase"/> 保留湿度而留在原地，
        /// 沸腾的汽团因此仍以原地为中心保持可见。</para>
        /// <para>相变完成后按蒸发时的温度带走蒸馏潜热 <see cref="HumidityField.LatentHeat"/>：蒸发让本格变冷。</para>
        /// </summary>
        /// <param name="radius">播撒半径：1 = 3×3（每格浓度高，<b>看得见</b>）；越大摊得越薄（每格低于可见阈值，<b>看不见</b>）。</param>
        /// <param name="maxShare">单格最大份额；&gt; 0 且 <c>Unit/接收格数</c> 超过它时放弃本次蒸发。0 = 不限。</param>
        /// <returns>是否真的发生了蒸发（false = 接收格不足，水原样保留）。</returns>
        private static bool Evaporate(ParticleWorld world, int x, int y, int radius, float maxShare)
        {
            // 本格（相变后）也是接收者，故 +1：沸腾的汽团仍以原地为中心。
            int receivers = CountGasReceivers(world, x, y, radius) + 1;
            if (maxShare > 0f && HumidityField.Unit / receivers > maxShare)
            {
                // 接收格太少、摊不薄——这份水汽若播下去会立刻可见。不做，把水留给调用方（守恒优先）。
                return false;
            }

            // SetPhase（而非 Set）保留 Q 与湿度：本格变大气后仍是水汽的合法载体。
            float temperature = world.Heat.GetTemperature(x, y);
            world.SetPhase(x, y, ParticleIds.Atmosphere);
            world.Humidity.SpreadUnitIntoAir(world, x, y, radius, maxShare);
            world.Heat.AddHeat(x, y, -HumidityField.LatentHeat(temperature));
            return true;
        }

        /// <summary>
        /// (x,y) 周围半径 <paramref name="radius"/> 内的气体 / 空格数，**不含本格**
        /// （本格相变后才算接收者，由调用方另行 +1）。越界格不计。
        /// </summary>
        private static int CountGasReceivers(ParticleWorld world, int centerX, int centerY, int radius)
        {
            if (radius < 0)
            {
                radius = 0;
            }

            int receivers = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int nx = centerX + dx;
                    int ny = centerY + dy;
                    if (world.IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(world, nx, ny))
                    {
                        receivers++;
                    }
                }
            }

            return receivers;
        }

        /// <summary>
        /// 慢蒸发能不能发生：正上方必须是**界内**的气体/空（**只在液面蒸发**，水下的水不会凭空变成气泡；
        /// 顶行的水不蒸发——越界会被 <see cref="ParticleRules.IsGasOrEmpty"/> 当成"空气"，那是审计 B3 的 bug），
        /// 且撒播范围内的空气还没吸饱（见 <see cref="HumidityField.IsAirSaturated"/>）。
        /// 后者是"慢蒸发不可见"的保证：加完这一次的份额，最湿的一格仍低于可见阈值。
        /// </summary>
        private static bool CanEvaporateSlowly(ParticleWorld world, int x, int y)
        {
            if (!world.IsInBounds(x, y + 1) || !ParticleRules.IsGasOrEmpty(world, x, y + 1))
            {
                return false;
            }

            return !world.Humidity.IsAirSaturated(
                world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 相变优先于运动。**结冰不在这里**——它走随机刻（见 OnRandomTick / TryFreeze）：
            // 结冰是「缓慢、按概率发生」的过程，交给世界的随机刻抽取，逻辑帧只管蒸发。
            // 于是热端（蒸发 ≥ SlowEvaporationTemperature = 20）与冷端（结冰 ≤ FreezingPoint = 4.0）
            // 天然不打架：这里只看热端。
            float temperature = world.Heat.GetTemperature(x, y);

            // 蒸发会带走一份潜热（见 HumidityField.LatentHeat），
            // 于是水面被蒸发冷却——这正是海风形成所需的冷源。

            // 剧烈蒸发：达到沸点必定蒸发（沸腾是唯一会产生**可见**水汽的路径）。
            bool boiling = temperature >= BoilingPoint;

            // 常温蒸发：不到沸点但够暖时以低概率蒸发——现实里水在任何温度都蒸发，海洋不用被烤沸也在供水。
            bool slow = !boiling
                && temperature >= SlowEvaporationTemperature
                && world.NextUInt() % SlowEvaporationDenominator == 0u;

            // 慢蒸发只在**水面**、且周围空气还够干时发生（见 CanEvaporateSlowly）——
            // 这是"慢蒸发不可见"的保证。
            if (slow && !CanEvaporateSlowly(world, x, y))
            {
                slow = false;
            }

            // 沸腾也要看空气吸饱没有（饱和水汽压）：吸饱了就等输送把水汽带走再继续。
            // 看的是**水面之上的空气**——液体格按不变量永远持有 0 湿度，查自己等于没查。
            if (boiling && world.Humidity.Get(x, y + 1) >= HumidityField.EvaporationCeiling)
            {
                boiling = false;
            }

            if (boiling)
            {
                // 沸腾：水汽集中原地 → 可见汽团。设计上可见，故不限份额（maxShare = 0）。
                // 换气失败（周围全是固体、连本格都算不进接收者——正常情况下不会发生）就不相变，水保留。
                Evaporate(world, x, y, BoilingSpreadRadius, 0f);
            }
            else if (slow)
            {
                // 慢蒸发：摊成一大片 → 看不见。接收格不足（Unit/接收格数 > InvisibleSpreadMaxShare）
                // 时播撒返回 0、本格不相变：半封闭地形（石墙口袋）因此不会凭空冒出可见水汽（审计 B2）。
                Evaporate(world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadMaxShare);
            }
        }

        /// <summary>
        /// 冷端相变：水 → 冰，**由随机刻驱动**（<see cref="OnRandomTick"/> 先调它）。
        /// 三种情形共用同一台机器——
        /// **成核**（水面、四邻无冰：要过冷，先冻出零星几点）、
        /// **蔓延**（水面、四邻有冰：沿冰缘向外铺开）、
        /// **加厚**（正上方是冰的第 d 层：越深需要的温度越接近环境 0）。
        /// <para>稀有度**不再叠概率骰**：随机刻本身每逻辑帧只抽 <see cref="ParticleWorld.RandomTicksPerStep"/>
        /// 格（生产 48 / 57600 格），命中本就是低频事件；条件满足即冻。</para>
        /// <para>三条门槛都乘**光照冷度缩放** <see cref="LightColdScale"/>（玩家把光照强度调低 ⇒ 更易结冰、
        /// 调高 ⇒ 更难结冰，8~12 之间保持标定值），见 <see cref="FreezeThresholdAt"/> /
        /// <see cref="FreezeNucleationThresholdAt"/>。</para>
        /// <para>水面（d = 0）另有两道几何门：正上方必须是**界内**气体 / 空（越界不算天空，
        /// 与 <see cref="Evaporate"/> 的审计 B3 同一处理），且正下方不是气体 / 空（「落定」要求——
        /// 否则雨滴会在空中冻成悬浮冰晶；正下方越界视作落在世界底，算落定）。</para>
        /// <para>条件全满足才 <see cref="ParticleWorld.SetPhase"/> 换形态（保留 Q），再按
        /// <see cref="IceParticle.FusionTemperatureStep"/> 与 <see cref="IceParticle.HeatCapacityDelta"/>
        /// **放热**：本格升温 1°，这份热随后由传导摊给邻居（「结冰放热、影响周围温度」）。</para>
        /// <para>世界未注册冰时（隔离类型表 / 未走生产注册）直接跳过：那种世界里不存在「冰」这种物质，
        /// 而 <see cref="ParticleWorld.SetPhase"/> 对未注册 id 会拒绝写入并报错。</para>
        /// <para>全程只读判定 + 一次写入：**不消耗世界随机流**（随机刻的抽取已在世界层完成），
        /// 也不分配。</para>
        /// </summary>
        private static void TryFreeze(ParticleWorld world, int x, int y, float temperature)
        {
            if (!IceParticle.IsRegisteredIn(world))
            {
                return;
            }

            int depth = IceParticle.DepthBelowIceTop(world, x, y);
            float threshold;

            if (depth > 0)
            {
                // 加厚：正上方已经是冰（已在冰下，无需再判「落定」——水体内部必然是落定的）。
                threshold = FreezeThresholdAt(world, depth);
            }
            else
            {
                // 水面：正上方必须看得见天（**界内**的气体 / 空），顶行的水不算（越界不当空气）。
                if (!world.IsInBounds(x, y + 1) || !ParticleRules.IsGasOrEmpty(world, x, y + 1))
                {
                    return;
                }

                if (world.IsInBounds(x, y - 1) && ParticleRules.IsGasOrEmpty(world, x, y - 1))
                {
                    return; // 落定要求：悬空的水滴不结冰
                }

                // 挨着冰只是「长大」（照冰点），没有冰邻格才要过冷；两者都乘上光照冷度缩放。
                threshold = HasIceNeighbor(world, x, y)
                    ? FreezeThresholdAt(world, 0)
                    : FreezeNucleationThresholdAt(world);
            }

            if (temperature > threshold)
            {
                return; // 不够冷：不改世界
            }

            // 结冰放热：同一份物质换形态（Q 保留），先把**热容差**的热量抽走让温度连续
            // （水 6.5 → 冰 3.5 会让温度凭空跳），再按**冰相热容**补上凝固潜热
            // （+C_冰·FusionTemperatureStep，本格升温 1°）。与 IceParticle 的融化结算严格互逆。
            world.SetPhase(x, y, ParticleIds.Ice);
            world.Heat.AddHeat(
                x, y, IceParticle.HeatCapacity * IceParticle.FusionTemperatureStep
                    - IceParticle.HeatCapacityDelta * temperature);
        }

        /// <summary>
        /// 四邻里的左 / 右 / 正下是否有冰（正上方有冰的情形已被
        /// <see cref="IceParticle.DepthBelowIceTop"/> 归进 depth &gt; 0）。
        /// 越界与未注册的冰 id（隔离类型表里 <see cref="ParticleIds.Ice"/> 是 0 = 空气）一律 false。
        /// </summary>
        private static bool HasIceNeighbor(ParticleWorld world, int x, int y)
        {
            return IsIce(world, x - 1, y) || IsIce(world, x + 1, y) || IsIce(world, x, y - 1);
        }

        private static bool IsIce(ParticleWorld world, int x, int y)
        {
            if (!world.IsInBounds(x, y))
            {
                return false;
            }

            ushort iceId = ParticleIds.Ice;
            return iceId != ParticleRegistry.AirTypeId && world.GetCell(x, y).typeId == iceId;
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 穿冠门：水落在树叶上时先试穿过整个叶柱（雨水穿冠），成功即 return。
            // 失败就回到下面的四段固定优先级——水从冠沿横流走掉是既有行为，不变。
            // 「不是空气」的前置判据同 WetSoilParticle.IsDrySoil：隔离类型表里 ParticleIds.TreeLeaf 是 0
            // （= 空气的 typeId），只比 id 会把空气当成树叶、把水送穿整根空气柱。
            ushort belowId = world.GetCell(x, y - 1).typeId;
            if (belowId != ParticleRegistry.AirTypeId
                && belowId == ParticleIds.TreeLeaf
                && TryPassThroughCanopy(world, x, y))
            {
                return;
            }

            if (ParticleRules.TryFall(world, x, y))
            {
                return;
            }

            if (ParticleRules.TryDiagonalFall(world, x, y))
            {
                return;
            }

            // 竖直都走不动时先试横流，再试热对流：横流返回 bool，用 || 短路——
            // 流动成功就不再对流，优先级严格保持在下落 / 斜落 / 横流之后。
            if (TryFlow(world, x, y)
                || ParticleRules.TryLiquidConvect(world, x, y, LiquidConvectionEpsilon))
            {
                return;
            }
        }

        /// <summary>
        /// 穿冠：自 (x, y−1) 起竖直向下扫过**连续**的树叶格（最多 <see cref="CanopyPassMaxDepth"/> 格），
        /// 停在第一格非树叶；该格在界内且是气体 / 空时把水与它 <see cref="ParticleWorld.Swap"/>——
        /// 水穿到叶柱下方、被顶开的气体换上冠顶（质量守恒；Swap 自带候选唤醒与气体绝热结算）。
        /// <para>叶柱下方是固体 / 液体 / 越界，或连续树叶超过深度上限时返回 false，水留在冠上
        /// （调用方随即回到下落 / 斜滑 / 横流 / 对流四段）。</para>
        /// <para>失活叶（下落中 / 已落地的树叶）同样可穿——水从落叶堆上渗下去，无需特判。</para>
        /// <para>纯确定性扫描：零随机流消耗（运动路径铁律）、零分配。</para>
        /// </summary>
        /// <remarks>只应在 (x, y−1) 确为树叶时调用（调用方的穿冠门已判定，含「非空气」的隔离表保护）。</remarks>
        private static bool TryPassThroughCanopy(ParticleWorld world, int x, int y)
        {
            int targetY = y - 1;
            int depth = 0;
            while (world.GetCell(x, targetY).typeId == ParticleIds.TreeLeaf)
            {
                depth++;
                if (depth > CanopyPassMaxDepth)
                {
                    return false; // 连续叶柱超过上限：不传送（叶柱下有什么都不管）
                }

                targetY--;
            }

            // 叶柱一直长到世界底行（越界）与固体 / 液体都挡下，只有气体 / 空能让水落进去。
            if (!world.IsInBounds(x, targetY) || !ParticleRules.IsGasOrEmpty(world, x, targetY))
            {
                return false;
            }

            world.Swap(x, y, x, targetY);
            return true;
        }

        /// <summary>
        /// 横流：每 tick 只横移 1 格，左右顺序随机以消除方向偏置。
        /// 两道闸门（见类注释 stage 3）都不满足时静止——孤立水滴躺在平地 / 满水面上时
        /// 往哪边流都不会更低，流动只会带来原地抖动。
        /// </summary>
        private bool TryFlow(ParticleWorld world, int x, int y)
        {
            bool leftFirst = HashNoise.Bit(x, y, world.Tick, FlowSalt);
            return leftFirst
                ? TryFlowSide(world, x, y, -1) || TryFlowSide(world, x, y, 1)
                : TryFlowSide(world, x, y, 1) || TryFlowSide(world, x, y, -1);
        }

        private bool TryFlowSide(ParticleWorld world, int x, int y, int direction)
        {
            int targetX = x + direction;
            if (!ParticleRules.CanDisplace(world, x, y, targetX, y))
            {
                return false;
            }

            // 闸门一「身后有推」：反侧同行邻格是液体 → 被推着走。
            // 跨类型判断只走标签 rank，不特判 typeId。
            if (ParticleRules.TagRank(ParticleRules.TagsAt(world, x - direction, y)) == ParticleRules.LiquidRank)
            {
                world.Swap(x, y, targetX, y);
                return true;
            }

            // 闸门二「落差视野」：本侧同行 MaxFlowSpread 格内存在「正下方还能继续落
            // （气体 / 空）」的格 → 往低处铺。视野途中被穿不过的格（固体）阻断即停。
            // 正下方越界不算落差（IsGasOrEmpty 把越界当空气，见 CanEvaporateSlowly 的审计 B3），
            // 否则躺在世界底行上的水滴会永远往两侧流。
            for (int step = 1; step <= Info.MaxFlowSpread; step++)
            {
                int scanX = x + direction * step;
                if (!world.IsInBounds(scanX, y) || !ParticleRules.CanFlowThrough(world, x, y, scanX, y))
                {
                    return false;
                }

                if (world.IsInBounds(scanX, y - 1) && ParticleRules.IsGasOrEmpty(world, scanX, y - 1))
                {
                    world.Swap(x, y, targetX, y);
                    return true;
                }
            }

            return false;
        }

        /// <summary>把附近土变成沙的搜索半径（格，欧氏距离）：只处理 dx²+dy² ≤ 半径² 的格。</summary>
        public const int SandifyingRadius = 2;

        /// <summary>冲刷概率的分母：找到目标后再摇一次 <c>NextUInt() % 本值 == 0</c> 才真的冲刷，即 1/5 = 20%。</summary>
        public const uint ErosionChanceDenominator = 5u;

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // ① 冷端相变优先：够冷的水面冻结成冰（TryFreeze 自己做完全部判定，
            //    没冻成也不消耗随机流）；冻上了就 return——刚冻出来的冰不该在同一帧再走冲刷。
            //    结冰走随机刻而不是逻辑帧：它是「缓慢、按概率发生」的过程，稀有度交给世界的抽取率
            //    （RandomTicksPerStep，生产 48/57600 格 ≈ 每格每 ~60 秒被点到一次）。
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature <= FreezeThresholdAt(world, 0))
            {
                TryFreeze(world, x, y, temperature);
                if (world.GetCell(x, y).typeId == ParticleIds.Ice)
                {
                    return; // 本格刚冻上：不再走冲刷
                }
            }

            // ② 冲刷：先找目标（不消耗随机数）：半径 SandifyingRadius 格圆盘内的**第一个**土壤 / 湿土 / 腐殖土。
            // 固定扫描序（y 升序、同一行 x 升序）保证确定性（同种子逐格可复现）。
            for (int dy = -SandifyingRadius; dy <= SandifyingRadius; dy++)
            {
                for (int dx = -SandifyingRadius; dx <= SandifyingRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (dx * dx + dy * dy > SandifyingRadius * SandifyingRadius)
                    {
                        continue;
                    }

                    ushort targetId = world.GetCell(x + dx, y + dy).typeId;
                    if (targetId != ParticleIds.Soil
                        && targetId != ParticleIds.WetSoil
                        && targetId != ParticleIds.HumusSoil
                        && targetId != ParticleIds.WetHumusSoil)
                    {
                        continue;
                    }

                    // 找到目标才摇骰（先判目标、再摇骰 ⇒ 无土可冲时不消耗随机流，确定性不随土量漂移）。
                    // 掷中才冲刷：20% 概率（ErosionChanceDenominator = 5）。
                    if (world.NextUInt() % ErosionChanceDenominator == 0u)
                    {
                        // 同一份物质换形态：土壤 / 湿土 / 腐殖土 → 沙，热量 Q 留在该格。
                        world.SetPhase(x + dx, y + dy, ParticleIds.Sand);
                    }

                    return;
                }
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 水被覆盖时无残留效果。
        }
    }
}
