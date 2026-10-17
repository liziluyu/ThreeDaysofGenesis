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
    // 邻居查询范围：正下方 1 格（落稳判定的支撑）+ 正上方 1 格（浮力判据）；ParticleRules.StepSolid 的斜滑净空链
    //              （摩擦 / 粘性 0.5 ⇒ 3 格）
    // 交互的 ParticleIds：Humus —— 腐败命中后 70% 分支的产物（另 30% 走 Clear 直接消散）
    //
    // 来源：动物死亡时由 CreatureDeathMaterializer 按死亡轮廓逐格写入（ParticleWorld.SetWithShade，
    //       显式带上 profile 的颜色索引）。不可在编辑器中手刷（ParticleEditor 的 _unplaceableTypes）。
    //
    // 行为：
    //   运动（OnMotion，60 Hz）：轻于水（density 0.8；浮力判据只认 Liquid 标签、不读密度，与冰的 0.92 同款写法），
    //       三段式、每帧至多一次位移：
    //       ① 正上方是液体 ⇒ Swap 上浮一格（水被挤下来）；
    //       ② 否则正下方是液体（已浮在液面）⇒ 停住，绝不沉穿水面；
    //       ③ 否则照常按固体下落 / 斜滑——ParticleRules.StepSolid（摩擦 / 粘性 0.5 ⇒ 堆形与土一致）。
    //       链路：尸体浮在水面 → 落稳 → 腐败 → 产出的腐殖质比水重、沉入水底。
    //       运动本身不消耗世界随机流。
    //   逻辑帧（OnLogic，20 Hz）：先**落稳判定**、后**腐败判定**，严格「先判后摇」——
    //       ① 落稳判定：正下方带 Solid 标签（含生物质本身），**或**正下方是液体且正上方不是液体（浮在液面）
    //          ⇒ 落稳计数 +1 并饱和在 SettleFrames；否则计数清零并 **立即 return**（在下落 / 仍在水里上浮，
    //          一个随机数都不消耗）；
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
            density: 0.8f,   // 比水（1.0）轻：浮在水面；浮力判据只认 Liquid 标签，不读密度（同冰 0.92 的写法）
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
            // 运动阅读盒：浮力读正上 1 格；斜滑净空链仍 3 格（0.5/0.5 的 StepSolid ⇒ (1, 1, 3)）。
            motionReadReach: new MotionReadBox(x: 1, up: 1, down: 3));
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
            // 浮力与固体运动：① 正上方是液体 ⇒ 上浮一格（与冰镜像，水被挤下来）；
            // ② 否则正下方是液体（已浮在液面）⇒ 停住，绝不沉穿水面；
            // ③ 否则照常按固体下落 / 斜滑。
            if (ParticleRules.HasTag(world, x, y + 1, ParticleTags.Liquid))
            {
                world.Swap(x, y, x, y + 1);
                return;
            }

            if (ParticleRules.HasTag(world, x, y - 1, ParticleTags.Liquid))
            {
                return;
            }

            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            ref Cell cell = ref world.At(x, y);
            int count = (cell.flags & CellFlags.BiomassSettleMask) >> CellFlags.BiomassSettleShift;

            // ① 落稳判定（先判）：固体支撑（石头 / 沙 / 土家族 / 腐殖质 / 生物质本身……）**或**浮在液面
            //    （正下方是液体且正上方不是液体）都算落稳。仍在水里上浮（上方也是液体）不算。
            bool supported = ParticleRules.HasTag(world, x, y - 1, ParticleTags.Solid)
                || (ParticleRules.HasTag(world, x, y - 1, ParticleTags.Liquid)
                    && !ParticleRules.HasTag(world, x, y + 1, ParticleTags.Liquid));
            if (supported)
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
                // 没有支撑 = 在下落、或仍在水里上浮：计数清零并立即返回。**不消耗任何随机数**，
                // 随机流因此不随下落 / 上浮中的尸体数量漂移。
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
