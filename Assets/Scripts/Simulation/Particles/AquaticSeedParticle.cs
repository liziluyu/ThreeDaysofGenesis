using UnityEngine;

namespace TapTap2026.Simulation
{
    // AquaticSeedParticle —— 水生态种子（固体粉末；沉到水底，被淹没且扎根于软底时发芽抽茎）
    //
    // flags 私有位：bit5-6 = 「被随机刻命中但无发芽条件」的连续计数（CellFlags.SeedStrikeMask /
    //        SeedStrikeShift，与草种子**共用**同一组位——一格在同一时刻只属于一个类型，互不冲突）。
    //        读用 SeedStrikeMask、清用 SeedStrikeClearMask、写用 | 组合，绝不整体赋值 flags（会破坏 bit0）。
    // 邻居查询范围：正上方一格（是否被淹没）、正下方一格（是否扎根于软底）；运动为 StepSolid 的
    //        「正下、左下/右下、斜滑反侧同一行」；发芽抽茎时沿同列向上扫描连续水柱。
    // 交互的 ParticleIds：Water —— 抽茎的位移对象（把水顶上去，不销毁）；
    //                     AquaticPlant —— 发芽后自身与其上方茎段变成它；
    //                     Humus —— 连续 3 次无发芽条件时霉烂成它；
    //                     Soil / WetSoil / HumusSoil / Sand —— 软底扎根判据（具体类型反应）
    //
    // 行为：
    //   运动（OnMotion，运动帧 60 Hz）：受重力的固体粉末，走 ParticleRules.StepSolid（摩擦/粘性 0.5），
    //       沉穿水体、落到水底基质上即静止。
    //   生长（OnRandomTick，随机刻）：命中时先判**正上方是液体**（被淹没）**且正下方是软底**——满足才发芽：
    //       摇出高度 height ∈ [MinGrowthHeight, MaxGrowthHeight]，自身 SetPhase 成水生植物（原为基质格，
    //       不涉及水），再自下而上向上抽茎 height 格。
    //       **抽茎是位移式的（不销毁水）**：目标格必须是 Water，把**连续水柱顶端之上**的气体/空格 Set 成水
    //       （水位上升一格），再把目标格 SetPhase 成植物——水总量因此严格守恒（水只是被顶上去）。
    //       遇非液体（固体 / 气体）或水柱封顶找不到接收格即停，保留已抽出的部分。
    //   霉烂：无发芽条件时累计一次「空转」命中（计数存本格 flags bit5-6）；连续
    //       MaxStrikesBeforeHumus（3）次都无发芽条件就 SetPhase 成腐殖质，且**不消耗随机数**。
    //   随机刻默认关闭（world.RandomTicksPerStep = 0），生产值由 ParticleSimulationSettings 注入。
    public sealed class AquaticSeedParticle : IParticleType
    {
        /// <summary>发芽时抽茎的最小高度（格）。</summary>
        public const int MinGrowthHeight = 1;

        /// <summary>发芽时抽茎的最大高度（格）。</summary>
        public const int MaxGrowthHeight = 3;

        /// <summary>连续多少次「无发芽条件」的随机刻命中后霉烂成腐殖质。</summary>
        public const int MaxStrikesBeforeHumus = 3;

        private static readonly Color32[] Palette =
        {
            new Color32(58, 68, 40, 255),
            new Color32(72, 84, 52, 255),
            new Color32(86, 100, 64, 255),
            new Color32(100, 116, 78, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic,
            density: 1.5f,
            heatCapacity: 0.80f,
            thermalConductivity: 0.60f,
            thermalEmissivity: 1.0f,
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,      // 与草种子/土同款粉末堆形
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(AquaticSeedParticle),
            // 运动阅读盒：0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 受重力粉末：下沉到水底基质上停住（Solid 沉穿 Liquid）。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 只有运动与随机刻发芽，逻辑帧为空。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 先判断（后摇骰）：正上方被液体淹没、正下方扎根于软底才发芽；否则累计空转、不消耗随机数。
            if (!CanGerminate(world, x, y))
            {
                int strikes = ReadStrikes(world, x, y) + 1;
                if (strikes >= MaxStrikesBeforeHumus)
                {
                    // 同一份物质换形态：种子 → 腐殖质（腐殖质非有机，不再触发有机衰变）。
                    world.SetPhase(x, y, ParticleIds.Humus);
                    return;
                }

                WriteStrikes(world, x, y, strikes);
                return;
            }

            int height = MinGrowthHeight + world.NextInt(MaxGrowthHeight - MinGrowthHeight + 1);

            // 自身：同一份物质换形态（原为水底基质格，不涉及水），热量随物质留在原格。
            world.SetPhase(x, y, ParticleIds.AquaticPlant);

            // 自下而上抽茎：每格重新确认水柱顶端，遇非液体 / 封顶即停并保留已抽出的部分。
            for (int step = 1; step <= height; step++)
            {
                if (!GrowOneCell(world, x, y + step))
                {
                    break;
                }
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 被覆盖时无残留效果。
        }

        /// <summary>
        /// 发芽条件：正上方是液体（被淹没，保证发芽后第一个植物格沉水）+ 正下方是软底（扎根）。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不满足。
        /// </summary>
        private static bool CanGerminate(ParticleWorld world, int x, int y)
        {
            if (!ParticleRules.HasTag(world, x, y + 1, ParticleTags.Liquid))
            {
                return false;
            }

            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || belowId == ParticleIds.Sand;
        }

        /// <summary>
        /// 位移式抽茎一格：目标 (x,targetY) 必须是 <see cref="ParticleIds.Water"/>；把该列**连续水柱顶端之上**
        /// 的气体/空格 <see cref="ParticleWorld.Set"/> 成水（水位上升一格），再把目标格 SetPhase 成植物。
        /// 水只是被顶上去、不销毁，因此水总量不变。找不到接收格（水柱封顶 / 顶到网格顶）时返回 false。
        /// </summary>
        private static bool GrowOneCell(ParticleWorld world, int x, int targetY)
        {
            if (!world.IsInBounds(x, targetY) || world.GetCell(x, targetY).typeId != ParticleIds.Water)
            {
                return false;
            }

            // 向上找连续水柱的顶端（用 typeId 判 Water，不用 IsGasOrEmpty）。
            int top = targetY;
            while (world.IsInBounds(x, top + 1) && world.GetCell(x, top + 1).typeId == ParticleIds.Water)
            {
                top++;
            }

            int receiverY = top + 1;
            if (!world.IsInBounds(x, receiverY) || !ParticleRules.IsGasOrEmpty(world, x, receiverY))
            {
                return false; // 水柱封顶或顶到网格顶：无处安放被顶上去的水，停止生长。
            }

            world.Set(x, receiverY, ParticleIds.Water);
            world.SetPhase(x, targetY, ParticleIds.AquaticPlant);
            return true;
        }

        /// <summary>读本格的「空转」计数（<see cref="CellFlags.SeedStrikeMask"/>）。</summary>
        private static int ReadStrikes(ParticleWorld world, int x, int y)
        {
            return (world.At(x, y).flags & CellFlags.SeedStrikeMask) >> CellFlags.SeedStrikeShift;
        }

        /// <summary>写本格的「空转」计数，<c>&amp; ~</c> / <c>|</c> 组合保留含 bit0 处理标记在内的其它位。</summary>
        private static void WriteStrikes(ParticleWorld world, int x, int y, int strikes)
        {
            byte flags = (byte)((world.At(x, y).flags & CellFlags.SeedStrikeClearMask)
                | ((strikes << CellFlags.SeedStrikeShift) & CellFlags.SeedStrikeMask));
            world.At(x, y).flags = flags;
        }
    }
}
