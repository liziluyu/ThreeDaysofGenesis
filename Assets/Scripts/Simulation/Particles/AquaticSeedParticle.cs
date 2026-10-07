using UnityEngine;

namespace TapTap2026.Simulation
{
    // AquaticSeedParticle —— 水生态种子（固体粉末；沉到水底，被淹没且扎根于软底时吸水发芽）
    //
    // flags 私有位：bit5-6 = 「被随机刻命中但无发芽条件」的连续计数（CellFlags.SeedStrikeMask /
    //        SeedStrikeShift，与草种子**共用**同一组位——一格在同一时刻只属于一个类型，互不冲突）。
    //        读用 SeedStrikeMask、清用 SeedStrikeClearMask、写用 | 组合，绝不整体赋值 flags（会破坏 bit0）。
    // 邻居查询范围：正上方一格（吸水对象 / 是否被淹没）、正下方一格（是否扎根于软底）；运动为 StepSolid 的
    //        「正下、左下/右下、斜滑反侧同一行」。
    // 交互的 ParticleIds：Water —— 发芽时被吸收的那格水（1:1，每格植物 ⇔ 一格水）；
    //                     Atmosphere —— 被吸掉的水格变成它（随后被上方落下来的水补位）；
    //                     AquaticPlant —— 发芽后自身变成它；
    //                     Humus —— 连续 3 次无发芽条件时霉烂成它；
    //                     Soil / WetSoil / HumusSoil / Sand —— 软底扎根判据（具体类型反应）
    //
    // 行为：
    //   运动（OnMotion，运动帧 60 Hz）：受重力的固体粉末，走 ParticleRules.StepSolid（摩擦/粘性 0.5），
    //       沉穿水体、落到水底基质上即静止。
    //   发芽（OnRandomTick，随机刻）：命中时先判「**正上方是水**（被淹没）**且正下方是软底**」——满足才发芽：
    //       **头顶那格水被吸收**（SetPhase 成大气；上方的水下一个运动帧就会落下来补位），
    //       自身 SetPhase 成 1 格水生植物。**发芽只产生一格植物**——长高由植物自己的随机刻逐格驱动
    //       （见 AquaticPlantParticle），不再是旧版的一次性位移式抽茎。
    //       水账：发芽消耗一格水、之后每格茎消耗一格水、植物死亡每格归还一格水——严格 1:1 对称，
    //       不需要任何持水标记 / 计数器。
    //   霉烂：无发芽条件时累计一次「空转」命中（计数存本格 flags bit5-6）；连续
    //       MaxStrikesBeforeHumus（3）次都无发芽条件就 SetPhase 成腐殖质，且**不消耗随机数**。
    //   随机刻默认关闭（world.RandomTicksPerStep = 0），生产值由 ParticleSimulationSettings 注入。
    public sealed class AquaticSeedParticle : IParticleType
    {
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
            tags: ParticleTags.Solid,
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
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3),
            hasLogic: false); // OnLogic 严格空体：发芽在随机刻、运动在 OnMotion

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
            // 先判断（后摇骰）：正上方是水（被淹没）、正下方扎根于软底才发芽；否则累计空转、不消耗随机数。
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

            // 发芽：头顶那格水被吸收（SetPhase 成大气；上方的水下一个运动帧就会落下来补位），
            // 自身变成 1 格植物。顺序无关紧要，两步都是原子相变。
            world.SetPhase(x, y + 1, ParticleIds.Atmosphere);
            world.SetPhase(x, y, ParticleIds.AquaticPlant);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set/SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 被覆盖时无残留效果（不带 Organic 标签：不走 20% 腐殖质衰变；种子的腐殖质只来自霉烂）。
        }

        /// <summary>
        /// 发芽条件：正上方是水（被淹没，也是发芽要吸收的那格水）+ 正下方是软底（扎根）。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不满足。
        /// </summary>
        private static bool CanGerminate(ParticleWorld world, int x, int y)
        {
            if (world.GetCell(x, y + 1).typeId != ParticleIds.Water)
            {
                return false;
            }

            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || belowId == ParticleIds.Sand;
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
