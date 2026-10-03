using UnityEngine;

namespace TapTap2026.Simulation
{
    // CloudParticle —— 云滴（气体标签，小到落不下来的水滴：悬浮、聚团、碰并成雨）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：八邻（碰并/湿度计数）、四邻（温度）、上下左右各 CohesionSearchRange 格的射线（找最近同伴）
    // 交互的 ParticleIds：Steam（T > 露点 → 蒸回蒸汽，云边消散）、Water（碰并 → 雨）
    //
    // 行为（固定优先级，成功即 return）：
    //   1. 蒸发：T > SteamParticle.DewPoint（漂回暖区）→ 变回蒸汽；
    //   2. 碰并：八邻内云/水 ≥ CoalescenceNeighbours 且概率触发 → 变成水（雨），
    //      雨走 Liquid 规则直落、穿过云（rank 2 > 1）；
    //   3. 散滴聚团：八邻内没有同伴（wet == 0）时，沿上下左右四条射线各扫 CohesionSearchRange 格，
    //      朝最近的云/水走一格——散落的冷凝点因此互相靠拢、滚成云团；
    //      找不到任何同伴才按 1/SlowSinkDenominator 概率下沉（落进暖区由规则 1 蒸散，不留永久散点）；
    //   4. 团内随风流：已成团（wet > 0）的云滴按 1/ClusterDriftDenominator 概率做热压漂移
    //      （TryThermalDrift，与大气同一套风向）——同一片温度场里的云因此整体平移而不是各自乱走；
    //   5. 缓慢下沉：每 tick 1/SlowSinkDenominator 概率下沉一格（微小末速度）。
    //   云不做浮力判断：已冷凝的液滴不再「热气上升」，悬浮来自「几乎不沉」。
    //   密度 0.8 是全部气体里最高的：按 ParticleRules.CanGasEnter，密度更低的大气（0.5）/蒸汽（0.6）
    //   挤不进云格，因此填满世界的环境大气不会再把云逐格推走（否则整片云会被随机漂移推得剧烈左右摇摆）；
    //   云的水平移动只来自自身规则（聚团 + 1/4 热压漂移），下沉仍可穿过更轻的气体。
    //   相变热量处理同水/蒸汽：手动携带 Q 跨越 Set。
    public sealed class CloudParticle : IParticleType
    {
        /// <summary>碰并触发所需的八邻内云/水格数下限。</summary>
        public const int CoalescenceNeighbours = 3;

        /// <summary>满足邻居条件后，每 tick 碰并成雨的概率分母（1/N）。调小到 8：云堆够了很快下雨。</summary>
        public const int CoalescenceDenominator = 8;

        /// <summary>每 tick 缓慢下沉一格的概率分母（1/N）。调小到 120：云不会永远挂在天上。</summary>
        public const int SlowSinkDenominator = 120;

        /// <summary>散滴找同伴的射线搜索距离：上下左右各扫这么多格（撞到固体停止，命中即取最近者）。</summary>
        public const int CohesionSearchRange = 12;

        /// <summary>成团的云滴随风流漂移的概率分母（1/N）。</summary>
        public const int ClusterDriftDenominator = 4;

        private static readonly Color32[] Palette =
        {
            new Color32(236, 240, 244, 255),
            new Color32(240, 244, 248, 255),
            new Color32(244, 247, 250, 255),
            new Color32(248, 250, 252, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Gas,
            density: 0.8f,
            heatCapacity: 2.0f,
            thermalConductivity: 0.9f,   // 云滴：接近空气的强传导
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(CloudParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            float heat = world.Heat.GetHeat(x, y);

            // 1. 漂回暖区：蒸回蒸汽（云边消散）。
            if (world.Heat.GetTemperature(x, y) > SteamParticle.DewPoint)
            {
                world.Set(x, y, ParticleIds.Steam);
                world.Heat.SetHeat(x, y, heat);
                return;
            }

            int wet = CountWetNeighbours(world, x, y);

            // 2. 碰并：邻居够密才成雨——雨从云底一片一片掉下来，而不是满天零星闪变。
            if (wet >= CoalescenceNeighbours
                && world.NextUInt() % CoalescenceDenominator == 0u)
            {
                world.Set(x, y, ParticleIds.Water);
                world.Heat.SetHeat(x, y, heat);
                return;
            }

            // 3. 散滴聚团：没有同伴就朝半径内最近的云/水靠拢；找不到同伴才缓慢下沉
            //    （落进暖区由规则 1 蒸散，不留永久散点）。
            if (wet == 0)
            {
                if (TryCohesiveStep(world, x, y))
                {
                    return;
                }

                if (world.NextUInt() % SlowSinkDenominator == 0u)
                {
                    ParticleRules.TrySink(world, x, y);
                }

                return;
            }

            // 4. 团内随风流：与大气同一套热压漂移，让整片云一起平移而不是各自乱走。
            if (world.NextUInt() % ClusterDriftDenominator == 0u
                && ParticleRules.TryThermalDrift(world, x, y, SteamParticle.BuoyancyEpsilon))
            {
                return;
            }

            // 5. 微小末速度：极低概率下沉一格。
            if (world.NextUInt() % SlowSinkDenominator == 0u)
            {
                ParticleRules.TrySink(world, x, y);
            }
        }

        /// <summary>
        /// 聚团：沿上下左右四条射线各扫 <see cref="CohesionSearchRange"/> 格，找到**最近的**云/水后朝它走一格。
        /// 射线撞到非气体（固体/液体）即停止（被地形挡住看不见后面的同伴）；命中取全局最近者。
        /// 找不到任何同伴返回 false。
        /// </summary>
        private static bool TryCohesiveStep(ParticleWorld world, int x, int y)
        {
            int bestDx = 0;
            int bestDy = 0;
            int bestDistance = int.MaxValue;

            // 四方向：右、左、上、下。
            for (int direction = 0; direction < 4; direction++)
            {
                int stepX = direction == 0 ? 1 : (direction == 1 ? -1 : 0);
                int stepY = direction == 2 ? 1 : (direction == 3 ? -1 : 0);

                for (int step = 1; step <= CohesionSearchRange; step++)
                {
                    int sampleX = x + stepX * step;
                    int sampleY = y + stepY * step;
                    if (!world.IsInBounds(sampleX, sampleY))
                    {
                        break;
                    }

                    ushort id = world.GetCell(sampleX, sampleY).typeId;
                    if (id == ParticleIds.Cloud || id == ParticleIds.Water)
                    {
                        if (step < bestDistance)
                        {
                            bestDistance = step;
                            bestDx = stepX * step;
                            bestDy = stepY * step;
                        }

                        break;
                    }

                    if (!ParticleRules.IsGasOrEmpty(world, sampleX, sampleY))
                    {
                        break; // 被地形挡住，这条射线看不到后面的同伴
                    }
                }
            }

            if (bestDistance == int.MaxValue)
            {
                return false;
            }

            int moveX = bestDx == 0 ? 0 : (bestDx > 0 ? 1 : -1);
            int moveY = bestDy == 0 ? 0 : (bestDy > 0 ? 1 : -1);
            return TrySwapWithGas(world, x, y, moveX, moveY)
                || TrySwapWithGas(world, x, y, moveX, 0)
                || TrySwapWithGas(world, x, y, 0, moveY);
        }

        /// <summary>目标格可被换入（<see cref="ParticleRules.CanGasEnter"/>：气体按密度次序；云 0.8 是密度最高的气体）则换位。</summary>
        private static bool TrySwapWithGas(ParticleWorld world, int x, int y, int stepX, int stepY)
        {
            if (stepX == 0 && stepY == 0)
            {
                return false;
            }

            int targetX = x + stepX;
            int targetY = y + stepY;
            if (!ParticleRules.CanGasEnter(world, x, y, targetX, targetY))
            {
                return false;
            }

            world.Swap(x, y, targetX, targetY);
            return true;
        }

        /// <summary>数八邻内「云或水」的格数（碰并的湿度条件）。</summary>
        private static int CountWetNeighbours(ParticleWorld world, int x, int y)
        {
            int count = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    ushort id = world.GetCell(x + dx, y + dy).typeId;
                    if (id == ParticleIds.Cloud || id == ParticleIds.Water)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 云被覆盖时无残留效果。
        }
    }
}
