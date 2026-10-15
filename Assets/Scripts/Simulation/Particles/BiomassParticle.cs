using UnityEngine;

namespace TapTap2026.Simulation
{
    // BiomassParticle —— 生物质（动物尸体的物质化产物；固体，**刻意不带 Organic**）
    //
    // flags 私有位：bit3-7（CellFlags.BiomassSettleMask）—— **连续落稳帧数** 0..SettleFrames(31)。
    //              颜色索引占用了 Cell.shade（继承死亡轮廓的颜色、不重抽），状态与颜色不能共用同一个字节，
    //              因此落稳计数整段放进 flags 的 bit3-7。读用掩码、清用 BiomassSettleClearMask、
    //              写用 | 组合，绝不整体赋值 flags（保留 bit0 处理标记与 bit1/2 通用位）。
    //              与草 / 水草的年龄余数（bit3-5）、树木（bit3-5 / bit6-7）**在位上重叠**——依据同
    //              CellFlags.BiomassSettleMask 的注释：一格同一时刻只属于一个类型，Set / SetPhase 换类型时
    //              整字节覆写 flags，旧类型的残留读不到。
    // 邻居查询范围：正下方 1 格（落稳判定的支撑）；ParticleRules.StepSolid 的斜滑净空链（摩擦 / 粘性 0.5 ⇒ 3 格）
    // 交互的 ParticleIds：Humus —— 腐败命中后 70% 分支的产物（另 30% 走 Clear 直接消散）
    //
    // 来源：动物死亡时由 CreatureDeathMaterializer 按死亡轮廓逐格写入（ParticleWorld.SetWithShade，
    //       显式带上 profile 的颜色索引）。不可在编辑器中手刷（ParticleEditor 的 _unplaceableTypes）。
    //
    // 行为：
    //   运动（OnMotion，60 Hz）：受重力的固体，一行 ParticleRules.StepSolid（摩擦 / 粘性 0.5 ⇒ 堆形与土一致）。
    //       第一版**不被水搬运**、不横向自由流动：只下落、被固体支撑并自然堆积（水不是支撑，会沉穿）。
    //       运动本身不消耗世界随机流。
    //   逻辑帧（OnLogic，20 Hz）：先**落稳判定**、后**腐败判定**，严格「先判后摇」——
    //       ① 正下方带 Solid 标签（含生物质本身）⇒ 落稳计数 +1 并饱和在 SettleFrames；否则计数清零并
    //          **立即 return**（在下落，一个随机数都不消耗）；
    //       ② 计数 < SettleFrames ⇒ return（还没落稳，同样不消耗随机数）；
    //       ③ 摇 NextUInt() % CorruptionDenominator（200 ⇒ 期望寿命 ≈ 10 秒 @20 Hz），未命中 ⇒ return；
    //       ④ 命中后再摇 NextUInt() % HumusBranchDenominator(10)：< 7 转腐殖质（SetPhase → Humus），
    //          否则 Clear 消散（质量允许以「散失」分支离开系统，不参与水量守恒）。
    //       **一次腐败只结算一次**：生物质不带 Organic，被覆盖 / 抹除时不会额外触发世界那道 20% 的通用腐殖质骰；
    //       产物 Humus 也不带 Organic，因此不会再叠加一次衰变。
    //   随机刻（OnRandomTick）：留空 —— 腐败是每逻辑帧的判定，随机刻为 0（测试与部分关卡配置）时尸体照样腐败。
    //   不带 Organic：见上（避免「同一份尸体既走 70/30 又走 20%」的重复结算）。
    public sealed class BiomassParticle : IParticleType
    {
        /// <summary>落稳计数的饱和值：达到它即视为「落稳已久」、进入可腐败状态（≈ 1.5 秒 @20 Hz）。</summary>
        public const int SettleFrames = 31;

        /// <summary>落稳后每逻辑帧的腐败门分母（<c>NextUInt() % 200 != 0</c> 就返回 ⇒ 期望寿命 ≈ 200 逻辑帧 = 10 秒）。</summary>
        public const uint CorruptionDenominator = 200u;

        /// <summary>腐败命中后转腐殖质的分支上界（<c>NextUInt() % 10 &lt; 7</c> ⇒ 70% 腐殖质）。</summary>
        public const uint HumusBranchNumerator = 7u;

        /// <summary>腐败命中后分支判定的分母（与 <see cref="HumusBranchNumerator"/> 组成 70% / 30%）。</summary>
        public const uint HumusBranchDenominator = 10u;

        /// <summary>
        /// 6 档灰阶调色板（由兔子 / 鱼精灵的灰阶按 0.78 变暗而来）。
        /// **顺序即死亡轮廓的颜色索引（<see cref="Cell.shade"/>），不得重排**。
        /// 开发期生成器与测试对拍用，勿改。
        /// </summary>
        public static readonly Color32[] Palette =
        {
            new Color32(23, 23, 23, 255),   // 0 最深（眼 / 耳）
            new Color32(45, 45, 45, 255),   // 1
            new Color32(59, 59, 59, 255),   // 2
            new Color32(74, 74, 74, 255),   // 3
            new Color32(101, 101, 101, 255),// 4
            new Color32(129, 129, 129, 255),// 5 最亮
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,   // **不带 Organic**：腐败由本类型自己结算，不再叠加世界的 20% 通用衰变骰
            density: 1.0f,
            heatCapacity: 1.0f,
            thermalConductivity: 0.5f,
            thermalEmissivity: 1.0f,
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: "生物质",
            // 运动阅读盒：0.5/0.5 的 StepSolid 斜滑净空链 3 格 ⇒ (1, 0, 3)（与土 / 腐殖质同款）。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3));
        // hasLogic 保持默认 true：OnLogic 有真实行为（落稳计数 + 腐败），逻辑帧扫描不得跳过本格。

        /// <summary>
        /// 读某格的连续落稳帧数（<c>flags</c> bit3-7，0..<see cref="SettleFrames"/>）。测试与自检用；
        /// 热路径请直接读 <see cref="ParticleWorld.At"/>。
        /// </summary>
        public static ushort SettleCount(ParticleWorld world, int x, int y)
        {
            return (ushort)((world.GetCell(x, y).flags & CellFlags.BiomassSettleMask)
                >> CellFlags.BiomassSettleShift);
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 受重力的固体：正下 → 斜滑净空链，单一决策、至多移动一次后立即由 StepSolid 内部 return。
            // 运动路径不消耗世界随机流（斜滑的左右顺序由坐标哈希决定，不走 RNG）。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            ref Cell cell = ref world.At(x, y);
            int count = (cell.flags & CellFlags.BiomassSettleMask) >> CellFlags.BiomassSettleShift;

            // ① 落稳判定（先判）：正下方带 Solid 标签（石头 / 沙 / 土家族 / 腐殖质 / 生物质本身……）才算有支撑。
            //    液体与气体不是支撑——生物质会沉穿水落到水底。
            if (ParticleRules.HasTag(world, x, y - 1, ParticleTags.Solid))
            {
                if (count < SettleFrames)
                {
                    count++;
                    cell.flags = (byte)((cell.flags & CellFlags.BiomassSettleClearMask)
                        | (count << CellFlags.BiomassSettleShift));
                }
            }
            else
            {
                // 没有支撑 = 在下落：计数清零并立即返回。**不消耗任何随机数**，随机流因此不随下落中的尸体数量漂移。
                cell.flags = (byte)(cell.flags & CellFlags.BiomassSettleClearMask);
                return;
            }

            // ② 还没落稳：不摇骰。这一条让「尸体还在飞」的帧与世界随机流完全解耦。
            if (count < SettleFrames)
            {
                return;
            }

            // ③ 腐败门：落稳后每逻辑帧 1/200。先判后摇——绝大多数帧在这里返回。
            if (world.NextUInt() % CorruptionDenominator != 0u)
            {
                return;
            }

            // ④ 命中：< 7/10 转腐殖质（显式完成态，Humus 不带 Organic ⇒ 不再叠加通用衰变骰），
            //    否则 Clear 消散。两种结局都是这一格物质的终点，不产生第二个产物。
            if (world.NextUInt() % HumusBranchDenominator < HumusBranchNumerator)
            {
                world.SetPhase(x, y, ParticleIds.Humus);
                return;
            }

            world.Clear(x, y);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 生物质不做随机刻：落稳与腐败都在逻辑帧里（随机刻为 0 时尸体仍会正常腐败）。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // 新格一律从 0 帧开始落稳：Set / SetWithShade 写 flags 时本就整字节覆写成当前 parity（bit3-7 = 0），
            // 这里再显式清一次，保证任何写入路径拿到的都是干净的落稳计数。
            ref Cell cell = ref world.At(x, y);
            cell.flags = (byte)(cell.flags & CellFlags.BiomassSettleClearMask);
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 不带 Organic、没有残留物：被覆盖 / 抹除时什么都不做（本格的写由世界接管）。
        }
    }
}
