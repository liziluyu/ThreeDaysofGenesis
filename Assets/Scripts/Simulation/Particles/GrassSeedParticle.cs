using UnityEngine;

namespace TapTap2026.Simulation
{
    // GrassSeedParticle —— 草种子（固体标签的粉末；随机刻命中且八邻有湿土时抽成草）
    //
    // flags 私有位：bit5-6 = 「被随机刻命中但无发芽条件」的连续计数（CellFlags.SeedStrikeMask /
    //        SeedStrikeShift，已在 CellFlags.cs 登记为「草种子私有」）。读用 SeedStrikeMask、
    //        清用 SeedStrikeClearMask、写用 | 组合，绝不整体赋值 flags（会破坏 bit0 处理标记）。
    //        计数随 Cell 一起移动（Swap 搬整格），因此种子下落途中也会累积。
    // 邻居查询范围：八邻 3×3（随机刻生长时找湿土）；
    //               运动则为 StepSolid 的「正下、左下/右下、斜滑反侧同一行」
    // 交互的 ParticleIds：WetSoil —— 八邻里只要有它就生长；
    //                     Grass —— 自己与上方 1-3 格被替换成它；
    //                     Humus —— 连续 3 次被随机刻命中都无发芽条件时直接变成它
    //
    // 行为：
    //   运动（OnMotion，运动帧 60 Hz）：受重力的固体粉末，走 ParticleRules.StepSolid（摩擦/粘性 0.5，堆形与土一致）。
    //       落到地面 / 湿土上之后就安分不动，等随机刻。
    //   生长（OnRandomTick，随机刻）：被随机刻抽中时先查**八邻**（含斜向与正下方）有没有湿土。
    //       有则以 1..MaxGrowthHeight 的随机高度，把自己与**上方**那 1-3 格一并换成草：
    //       自己走 SetPhase（同一份物质换形态，保留热量），上方各格走 Set（空气/气体 → 新物质）。
    //       上方遇到固 / 液（ParticleRules.BlocksLight 判据）就停下——草只在空气 / 气体里抽出，
    //       不会凭空吃掉石头、水或别的粒子；顶到网格上边界同理停下。
    //
    //   生长要先判断、后摇骰：没有湿土邻居时一步也不走、不消耗随机数（随机流不随场上种子数漂移）。
    //   霉烂：没有发芽条件时累计一次「空转」命中（计数存在本格 flags 的 bit5-6）；连续
    //   MaxStrikesBeforeHumus（3）次都无发芽条件就直接 SetPhase 成腐殖质——落错地方的种子不会永远赖着。
    //
    //   随机刻默认关闭（world.RandomTicksPerStep = 0，见 ParticleWorld），生产值由
    //   ParticleSimulationSettings._randomTicksPerStep 注入；数量按逻辑帧计。
    public sealed class GrassSeedParticle : IParticleType
    {
        /// <summary>生长高度下限：种子**上方**至少长出这么多格草。</summary>
        public const int MinGrowthHeight = 1;

        /// <summary>生长高度上限：种子**上方**最多长出这么多格草。</summary>
        public const int MaxGrowthHeight = 3;

        /// <summary>
        /// 连续多少次「被随机刻命中但没有发芽条件」后，种子直接变成腐殖质。
        /// 计数存在本格 <c>flags</c> 的 bit5-6（<see cref="CellFlags.SeedStrikeMask"/>），随格子一起移动。
        /// </summary>
        public const int MaxStrikesBeforeHumus = 3;

        private static readonly Color32[] Palette =
        {
            new Color32(120, 96, 52, 255),
            new Color32(140, 118, 64, 255),
            new Color32(96, 108, 48, 255),
            new Color32(112, 128, 56, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic,
            density: 1.2f,
            heatCapacity: 0.75f,
            thermalConductivity: 0.55f,
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,   // 与土同档：种子落地成堆、不滚远
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(GrassSeedParticle),
            // 运动阅读盒：0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 草种子只有运动与随机刻生长，没有每逻辑帧的相变 / 反应。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 先判断：八邻没有湿土 = 没有发芽条件。累计一次「空转」命中——数到 MaxStrikesBeforeHumus
            // （3）次就霉烂、直接变成腐殖质；否则只把计数写回、原地等待（不消耗随机数）。
            if (!HasWetSoilNeighbour(world, x, y))
            {
                int strikes = ReadStrikes(world, x, y) + 1;
                if (strikes >= MaxStrikesBeforeHumus)
                {
                    // 同一份物质换形态：草种子 → 腐殖质（腐殖质非有机，不再触发有机衰变）。
                    world.SetPhase(x, y, ParticleIds.Humus);
                    return;
                }

                WriteStrikes(world, x, y, strikes);
                return;
            }

            int height = MinGrowthHeight + world.NextInt(MaxGrowthHeight - MinGrowthHeight + 1);

            // 自己：同一份物质换形态（草），热量随物质留在原格。
            world.SetPhase(x, y, ParticleIds.Grass);

            // 上方 1..height 格：长进空气 / 气体；遇到固 / 液立即停下（不覆盖已有物质），顶到边界同理。
            for (int step = 1; step <= height; step++)
            {
                int targetY = y + step;
                if (!world.IsInBounds(x, targetY) || ParticleRules.BlocksLight(world, x, targetY))
                {
                    break;
                }

                world.Set(x, targetY, ParticleIds.Grass);
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 草种子被覆盖时无残留效果。
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

        /// <summary>(x,y) 的**八邻**（3×3 去掉自身）里是否有湿土。越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不算。</summary>
        private static bool HasWetSoilNeighbour(ParticleWorld world, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (world.GetCell(x + dx, y + dy).typeId == ParticleIds.WetSoil)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
