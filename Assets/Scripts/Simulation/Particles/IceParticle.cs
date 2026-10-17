using UnityEngine;

namespace TapTap2026.Simulation
{
    // IceParticle —— 冰（固体标签，摩擦度/粘性不读，填 0；热容 3.5 = 水的一半）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正上方连续最多 ColumnScanDepth（8）格（深度扫描，判「离冰顶面几层」）
    // 交互的 ParticleIds：Water（融化的目标形态）——结冰入口在水那侧
    //                    （WaterParticle.OnRandomTick 的冷端分支，FreezingPoint 一族常量在那边）
    //
    // 行为：**原地不动的固体**。OnMotion 空体（不落、不浮），也不用 IsStatic——静态类型收不到随机刻
    //       （RunRandomTicks 跳过 IsStatic），而冰要靠随机刻融化。
    //       **融化写在 OnRandomTick**：随机刻命中本格时，正上方连续冰格数 d = 「离冰顶面几层」
    //       （0 = 冰面，即最上面那格冰）；温度达到 MeltThresholdAt(world, d) = (MeltingPoint + MeltDepthStep·d)
    //       · 光照冷度缩放（结冰侧「越深越冷」的镜像：越深越难融，线性、无上限）就**当帧化回水**——
    //       随机刻本身已经提供了稀有度（几十格/逻辑帧里的一格），不再叠概率骰。
    //       光照冷度缩放与结冰侧共用（WaterParticle.LightColdScale）：光照弱 ⇒ 融点抬高（冰更难化）。
    //       OnLogic 因此是严格空体（hasLogic: false，逻辑帧混合扫描整段跳过）：冰的温度变化靠世界的
    //       热传导与暴露面辐射推进，相变只在随机刻发生。
    //       相变结算（与结冰侧严格互为逆运算）：SetPhase 保留 Q，而热容从 3.5 变 6.5 会让温度凭空跳，
    //       所以先把**热容差** HeatCapacityDelta·T 补回去（温度连续），再按**水相热容**抽走凝固潜热
    //       （−C_水·FusionTemperatureStep，本格降温 1°）。结冰侧（水 → 冰）按同一公式反号**放热**
    //       （+C_冰·s，本格升温 1°），见 WaterParticle.TryFreeze——因此「冻结再融化」往返逐位守恒
    //       （Q 与温度都复原，见 IceParticleTests 的往返用例）。
    //       迟滞：结冰门 4.0 / 融化门 6.0，带宽大于潜热 + 热容差造成的温度摆动（±1.0），
    //       同一格不会在固化与融化之间振荡。
    //       渲染：HasOutline + outlineLighten —— 固液 / 背景交界处**向浅色提亮**（冰面反光的观感），
    //       与其它固体的「调暗描边」不同（见 ParticlePixelBuffer.OutlineLightColor）。
    //       与水的具体类型对反应（ParticleIds 双向引用），因此**必须走生产注册表**；写入前检查两端
    //       都真的在本世界的类型表里，隔离类型表 / 未注册世界直接跳过——SetPhase 对未注册 id
    //       会拒绝写入并打一条错误日志（见 ParticleWorld.SetPhase 的参数校验）。
    //       不可在编辑器中投放（ParticleEditor 的 _unplaceableTypes，只能由水在低温下冻结生成）。
    public sealed class IceParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(222, 240, 250, 255),
            new Color32(198, 226, 243, 255),
            new Color32(176, 210, 235, 255),
            new Color32(154, 196, 226, 255),
        };

        /// <summary>
        /// 凝固潜热，以**温度步长 s** 表示：水 → 冰让本格**升温 s**（放热），冰 → 水让本格**降温 s**（吸热）。
        /// 取 1.0（「相变一次、本格温度 ±1°」，最好配平也最好断言）。
        /// <para>结算量是 <c>本相热容 · s</c>，因此两个方向的 Q 不相等（结冰放 C_冰·s = 3.5、
        /// 融化吸 C_水·s = 6.5）——这正是「冻结→融化往返**逐位守恒**」的必要条件：
        /// 若两侧都用同一个 Q 常数，热容差项会按相变前后不同的温度各算一次（冻结后在冰上、
        /// 融化前在冰上），往返会净产热 <c>s·ΔC·C_冰/C_水</c>（一个隐形热泵）。</para>
        /// <para>1.0 的尺度：既看得见「结冰放热 / 融冰吸热」对周围温度的影响（热随后由传导摊给邻居，
        /// 一整个冰面同时冻结会明显拖慢下层继续冻结），又小于迟滞带
        /// [<see cref="WaterParticle.FreezingPoint"/> 4.0, <see cref="MeltingPoint"/> 6.0]，不会自激振荡。</para>
        /// </summary>
        public const float FusionTemperatureStep = 1f;

        /// <summary>冰的热容 C = 3.5（水 6.5 的一半；真实冰 2.09 / 水 4.18）。热学表与相变结算共用这一个真源。</summary>
        public const float HeatCapacity = 3.5f;

        /// <summary>
        /// 水与冰的热容差（<see cref="WaterParticle.HeatCapacity"/> − <see cref="HeatCapacity"/> = 3.0）。
        /// <see cref="ParticleWorld.SetPhase"/> 保留 Q，而 T = Q / 热容：直接换形态会让温度凭空跳
        /// （水 4° 变冰会跳到 7.4°）。因此相变时先把这份热量补齐 / 抽走（±ΔC·T）让温度连续，
        /// 再单独结算潜热。与 WetSoilParticle 的 <c>WaterShareHeatCapacity</c>（湿土 2.0 − 干土 0.8）同一手法。
        /// </summary>
        public const float HeatCapacityDelta = 3f;

        /// <summary>
        /// 深度扫描上限：向上最多数这么多格连续冰来定「离冰顶面几层」。
        /// 同时也是 WaterParticle 结冰侧与<see cref="MeltThresholdAtDepth">融化侧</see>的深度上限
        /// （超过按最后一档处理），因此放在本类型上共用。
        /// 取 8：与水的穿冠深度同量级；更深的冰层在温度上已贴着环境 0（结冰阈值 4.0·0.5^8 ≈ 0.016，
        /// 融化阈值 6 + 2·8 = 22），再细分没有意义，保留扫描有界（扫描成本 = 冰下冷水格的 O(d)）。
        /// </summary>
        public const int ColumnScanDepth = 8;

        /// <summary>
        /// 表层冰（d = 0）的融化阈值。取 6：世界的环境平衡温度是 0，白天日照下的冰面稳态远高于它
        /// （光照强度 × 光吸收 / 辐射率），因此「阳光一照就化」；而与结冰门 4.0 之间留出 2.0° 的迟滞带
        /// （运行时两侧都乘同一份光照冷度缩放，带宽随之整体缩放，见 <see cref="MeltThresholdAt"/>）。
        /// </summary>
        public const float MeltingPoint = 6f;

        /// <summary>
        /// 融化深度梯度：每深一层，需要的温度再高这么多。取 2：结冰「越深越冷」的镜像
        /// （越深越难融），线性、无上限——深水层要等暖锋一路传导下来才化得掉。
        /// </summary>
        public const float MeltDepthStep = 2f;

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 0.92f,       // 只作语义（固体的内置规则不读密度）：冰比水轻
            heatCapacity: HeatCapacity,   // 冰：水（6.5）的一半——真实冰 2.09 / 水 4.18
            thermalConductivity: 0.85f,   // 导热好：冷锋能从冰面一路往下走，暖锋同理
            thermalEmissivity: 1.0f,      // 近黑体：暴露的冰面夜间继续向外辐射抽冷，驱动加厚
            affectedByGravity: false,
            isStatic: false,      // 必须非静态，否则收不到随机刻（融化）；靠空的 OnMotion 保持原地不动
            maxFlowSpread: 0,
            friction: 0f,         // 不移动，摩擦 / 粘性都不读
            stickiness: 0f,
            palette: Palette,
            debugName: "冰",
            lightAbsorption: 0.4f,  // 冰面反光：日照下升温比水慢，白天融化不至于瞬间
            hasLogic: false,        // OnLogic 严格空体（融化在随机刻里），逻辑帧混合扫描整段跳过
            hasOutline: true,       // 固体：与空气 / 水的交界描边
            outlineLighten: true);  // ……但冰用**浅色**描边（冰面反光）：向 OutlineLightColor 提亮而非调暗

        /// <summary>
        /// 离冰顶面几层：自 (x, y+1) 起向上数**连续**冰格，最多 <see cref="ColumnScanDepth"/> 格。
        /// 0 = 本格就是最上面的冰（冰面），d = 上方还压着 d 格冰。
        /// 冰与水的两侧（融化阈值、结冰阈值）都用同一把尺子，因此放在冰这一侧共用。
        /// 纯确定性扫描：不消耗随机流、零分配。
        /// </summary>
        public static int DepthBelowIceTop(ParticleWorld world, int x, int y)
        {
            ushort iceId = ParticleIds.Ice;
            int depth = 0;
            for (int step = 1; step <= ColumnScanDepth; step++)
            {
                if (!world.IsInBounds(x, y + step) || world.GetCell(x, y + step).typeId != iceId)
                {
                    break;
                }

                depth++;
            }

            return depth;
        }

        /// <summary>
        /// 第 <paramref name="depth"/> 层（离冰顶面几层）的融化阈值 = MeltingPoint + MeltDepthStep·depth。
        /// 线性、无上限：越深越难融（结冰侧「越深越冷」的镜像）。
        /// <para>这是**不含光照缩放**的纯梯度；运行时请用 <see cref="MeltThresholdAt"/>。</para>
        /// </summary>
        public static float MeltThresholdAtDepth(int depth)
        {
            return MeltingPoint + MeltDepthStep * depth;
        }

        /// <summary>
        /// 本世界当前生效的**融化阈值**（含光照冷度缩放，与结冰侧共用同一把尺子）：
        /// <c>MeltThresholdAtDepth(depth) · WaterParticle.LightColdScale(world.Light.Intensity)</c>。
        /// 光照弱 ⇒ 融点整体抬高（现有冰更难化）；光照强 ⇒ 压低（更快化）。
        /// <para>两侧共用缩放是**迟滞带完整性的要求**：只缩放结冰侧会让冰点向上顶到融点，
        /// 同一格在固 / 液之间反复相变（见 <see cref="WaterParticle.LightColdScaleMax"/> 的推导）。</para>
        /// </summary>
        public static float MeltThresholdAt(ParticleWorld world, int depth)
        {
            return MeltThresholdAtDepth(depth) * WaterParticle.LightColdScale(world.Light.Intensity);
        }

        /// <summary>
        /// 本世界的类型表里是否真的有冰那一格（<see cref="ParticleIds.Ice"/> 未越界且已注册）。
        /// 隔离类型表（测试的 <c>TestTables.Build</c>：id 按传入顺序 1..n 顺排、表长 = 类型数 + 1）里
        /// <see cref="ParticleIds.Ice"/> 是 0 或上一次生产注册留下的越界 id——那种世界里不存在「冰」这种物质，
        /// 相变两端都必须先过这道门：<see cref="ParticleWorld.SetPhase"/> 对未注册 id 会拒绝写入并
        /// <c>Debug.LogError</c>（Unity 测试把意外的 error 日志算失败），而且结冰判定会白白消耗世界随机流、
        /// 打乱隔离表用例的确定性。判据与 SetPhase 自己的校验一致（越界 / 空槽）。
        /// <para>前提与全工程一致：<c>typeId</c> 即注册顺序，跨类型写只在生产注册表（或按同一顺序自建的
        /// 类型表）里成立——隔离表若要测冰必须自己把冰摆到 <see cref="ParticleIds.Ice"/> 那一格。</para>
        /// <para>随机刻路径同样要先过这道门：随机刻的抽取由世界完成（不消耗本钩子的随机流），
        /// 命中本就稀有，不该再浪费在被拒绝的写入上。</para>
        /// </summary>
        public static bool IsRegisteredIn(ParticleWorld world)
        {
            ushort iceId = ParticleIds.Ice;
            return iceId != ParticleRegistry.AirTypeId && iceId < world.Types.Length && world.Types[iceId] != null;
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 严格空体：冰的相变（融化）写在 OnRandomTick 里，Info 因此声明 hasLogic: false，
            // 逻辑帧混合扫描整段跳过本类型的这一钩子。温度本身由世界的热传导 / 暴露面辐射推进，
            // 这里没有任何要读的东西。
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 冰长在原地：运动帧什么都不做。之所以不用 IsStatic，是因为静态类型收不到随机刻
            //（ParticleWorld.RunRandomTicks 会跳过 IsStatic 的格），而冰需要随机刻来融化。
        }

        /// <summary>
        /// 融化：随机刻命中本格时，正上方连续冰格数 d = 「离冰顶面几层」（0 = 冰面）；温度达到
        /// <see cref="MeltThresholdAtDepth"/>(d) = <see cref="MeltingPoint"/> + <see cref="MeltDepthStep"/>·d
        /// 就**当帧**化回水——随机刻本身已经提供了稀有度（每逻辑帧世界只抽
        /// <see cref="ParticleWorld.RandomTicksPerStep"/> 格），不再叠概率骰。
        /// <para>达标检查全部是只读的比较（先判后写、写后即 return）：不满足条件的命中不改世界、
        /// 也不消耗世界随机流。</para>
        /// <para>吸热融化：同一份物质换形态（Q 保留），先把热容差的热量补回去让温度连续，
        /// 再按**水相热容**抽走凝固潜热（−C_水·s）——本格降温 s 度，热量随后由传导从邻居补回来
        /// （「融冰吸热」）。与结冰侧严格互为逆运算，见 <see cref="FusionTemperatureStep"/>。</para>
        /// </summary>
        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature < MeltThresholdAt(world, 0))
            {
                return; // 连表层（含光照缩放）都没到融化门：不必扫深度
            }

            // 本世界必须同时有冰（自己）与水（目标形态）——具体类型对反应的隔离表保护，
            // 判据同 IsRegisteredIn：越界 / 空槽的 id 写不得（SetPhase 会拒绝并报错）。
            ushort waterId = ParticleIds.Water;
            if (waterId == ParticleRegistry.AirTypeId
                || waterId >= world.Types.Length
                || world.Types[waterId] == null)
            {
                return;
            }

            int depth = DepthBelowIceTop(world, x, y);
            if (temperature < MeltThresholdAt(world, depth))
            {
                return; // 越深越难融：这一层还没暖够
            }

            world.SetPhase(x, y, waterId);
            world.Heat.AddHeat(
                x, y, HeatCapacityDelta * temperature - WaterParticle.HeatCapacity * FusionTemperatureStep);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 冰被覆盖 / 清除时无残留效果（不带 Organic，不参与通用腐殖质衰变）。
        }
    }
}
