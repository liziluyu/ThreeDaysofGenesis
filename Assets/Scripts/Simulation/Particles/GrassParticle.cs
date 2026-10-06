using UnityEngine;

namespace TapTap2026.Simulation
{
    // GrassParticle —— 草（固体：长在原地不动；随机刻把附近的沙变成土、并以 10% 概率撒下新草种子）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：随机刻时以自身为中心、半径 2 格（GrassParticle.SandConversionRadius /
    //               SeedSpawnRadius）的圆盘，以及候选空地下方 1 格（判固体支撑）
    // 交互的 ParticleIds：Sand → Soil（把沙变成土）；GrassSeed（撒下新草种子）
    //
    // 行为：
    //   随机刻（OnRandomTick），按固定顺序依次做五件事（死亡一旦触发就清掉整段草并 return）：
    //       ① **被压住就死**：正上方是**固体或液体、且不是草**时，草被压死——沙 / 土 / 石 / 湿土 / 腐殖土 /
    //          腐殖质、草种子（固体）与**水**（液体）都算压，同株草摞在草上不算压。死亡沿**竖直方向向下**
    //          传播，把下面整段连续的草一并清掉。
    //       ② **没扎根就死**：正下方不是土 / 湿土 / 腐殖土（也不是同株草的下段）时，这一格草失去支撑而死；
    //          死亡沿**竖直方向向上**传播，把上面整段连续的草一并清掉（根在下面，土没了整株一起死）。
    //       ③ **温度致死**：本格温度 > LethalHeatTemperature（50）或 < LethalColdTemperature（3）时这一格
    //          草死亡（只死这一格；相邻草格各有自己的温度，会随传导各自判定）。
    //       三种死法都走 Clear，因而各自按 20% 概率留下腐殖质。
    //       ④ 撒种（10% 概率）：落点下方必须是**可托住种子的固体**——固体且**不是草**，因此不会撒到别的草头上。
    //       ⑤ 固沙。
    //   逻辑帧（OnLogic）与运动帧（OnMotion）都**空**：草长在原地，不落、不流；死亡判定放在随机刻里
    //       （见上），不必每逻辑帧空跑。它不是 IsStatic：
    //       随机刻会跳过静态类型（ParticleWorld.RunRandomTicks 检查 IsStatic），所以草必须是普通类型
    //       才能收到随机刻；靠空的 OnMotion（而不是 IsStatic）保证它不移动。草带 Solid 标签且与同 rank
    //       的固体（沙 / 土 / 石）互不置换（CanDisplace 要求严格更高的 rank），因此上方的沙会停在草上，
    //       草也不会被同类顶掉。
    //   随机刻（OnRandomTick），每次命中按固定顺序做两件事：
    //       ① 撒种（10% 概率）：先摇一次骰（NextUInt() % SeedSpawnChanceDenominator == 0），
    //          命中才在半径 2 格的圆盘内找**第一个「下方 1 格是固体的空地」**（固定扫描序：y 升序、
    //          同一行 x 升序，只取 dx²+dy² ≤ 半径²），在那里生成一颗新的草种子（Set，新物质）。
    //          「空地」= 空气或气体（ParticleRules.IsGasOrEmpty）——世界被大气填满，若只认 typeId 0
    //          则永远找不到落点；「下方是固体」用 ParticleRules.IsSolid（石头/沙/土/湿土/草都算）。
    //          摇骰每次都发生（消耗 1 个随机数），找不到落点就什么都不做。
    //       ② 固沙（无概率门）：在半径 2 格的圆盘内找**第一个沙**，把它 SetPhase 成土。
    //       两步都不做即草这一帧无事发生。随机刻默认关闭（world.RandomTicksPerStep = 0，见 ParticleWorld），
    //       生产值由 ParticleSimulationSettings._randomTicksPerStep 注入。
    public sealed class GrassParticle : IParticleType
    {
        /// <summary>把附近沙变为土的半径（格，欧氏距离）：只处理 dx²+dy² ≤ 半径² 的格。</summary>
        public const int SandConversionRadius = 2;

        /// <summary>撒下新草种子时搜索的半径（格，欧氏距离）：只处理 dx²+dy² ≤ 半径² 的格。</summary>
        public const int SeedSpawnRadius = 2;

        /// <summary>撒种概率的分母：每次随机刻命中时 <c>NextUInt() % 本值 == 0</c> 才尝试撒种，即 1/10 = 10%。</summary>
        public const uint SeedSpawnChanceDenominator = 10u;

        /// <summary>致死高温（温度严格**高于**它即死）。温度是导出量 <c>T = Q / 热容</c>。</summary>
        public const float LethalHeatTemperature = 50f;

        /// <summary>致死低温（温度严格**低于**它即死）。</summary>
        public const float LethalColdTemperature = 3f;

        /// <summary>
        /// 光照吸收率（<see cref="ParticleTypeInfo.LightAbsorption"/>）：草的浅色表面反光，受阳光升温**略慢**
        /// ——取 0.8（比裸土 / 石低 20%）。只影响光照注入，不影响热传导与辐射。
        /// </summary>
        public const float LightAbsorptionFactor = 0.8f;

        private static readonly Color32[] Palette =
        {
            new Color32(56, 122, 48, 255),
            new Color32(74, 148, 60, 255),
            new Color32(92, 168, 74, 255),
            new Color32(110, 186, 88, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic,
            density: 0.6f,
            heatCapacity: 0.80f,   // 与干土同档：草面/地面的升温速度一致
            thermalConductivity: 0.60f,
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: false,
            isStatic: false,     // 必须非静态，否则收不到随机刻；靠空的 OnMotion 保持不动
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(GrassParticle),
            lightAbsorption: LightAbsorptionFactor,
            hasLogic: false); // OnLogic 严格空体：死亡 / 撒种 / 固沙都在随机刻

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 草长在原地：运动帧什么都不做。之所以不用 IsStatic，是因为静态类型收不到随机刻
            // （ParticleWorld.RunRandomTicks 会跳过 IsStatic），而草需要随机刻来撒种、把附近的沙变成土。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 草的逻辑帧什么都不做：死亡判定（压死 / 失去扎根 / 温度）已挪进随机刻（见 OnRandomTick）——
            // 草本来就不会移动，每逻辑帧空跑一遍没有意义；死亡变成「缓慢、按概率」的过程，与撒种 / 固沙
            // 同频。运动帧的 OnMotion 同样为空，草因此完全靠随机刻驱动。
        }

        /// <summary>
        /// 死亡判定（①②③）与撒种 / 固沙（④⑤）都在随机刻里按固定顺序执行。死亡一旦触发就清掉整段草并返回，
        /// 本帧不再撒种 / 固沙。
        /// </summary>
        private void RunRandomTick(ParticleWorld world, int x, int y)
        {
            // ① 被压住就死：正上方是**固体或液体、且不是草**（沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、草种子、
            // 水）时，草被压死。判据用标签 Solid|Liquid，只把「草」本身排除——同株草摞在草上不算压。
            if (IsPressed(world, x, y))
            {
                // 死亡沿**竖直方向向下**传播：从被压的这一格起，清掉下面整段连续的草（同一株被压垮）。
                // 每一格都走 Clear —— 有机物死亡由 ParticleWorld.MaybeLeaveHumus 按 20% 概率就地留下腐殖质。
                ClearCascade(world, x, y, -1);
                return;
            }

            // ② 失去扎根就死：正下方不是土 / 湿土 / 腐殖土（也不是同株草的下段）时，这一格草失去支撑而死。
            //    死亡沿**竖直方向向上**传播：草是一株，根在下——下面的土一旦没了，上面整段草一起死。
            if (!IsRooted(world, x, y))
            {
                ClearCascade(world, x, y, 1);
                return;
            }

            // ③ 温度致死：过冷（< LethalColdTemperature）或过热（> LethalHeatTemperature）时这一格草死亡
            //    （Clear；同样按 20% 概率留下腐殖质）。只判这一格——相邻草格各有自己的温度，会随热传导
            //    各自判定，级联是随时间涌现的，不需要显式传播。
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature > LethalHeatTemperature || temperature < LethalColdTemperature)
            {
                world.Clear(x, y);
                return;
            }

            // ④ 撒种（10% 概率）：先摇骰（每次都消耗 1 个随机数，保证确定性），命中才搜索落点。
            if (world.NextUInt() % SeedSpawnChanceDenominator == 0u)
            {
                TrySpawnSeed(world, x, y);
            }

            // ⑤ 固沙（无概率门）：把半径 2 格圆盘内的第一个沙变成土。
            ConvertNearestSand(world, x, y);
        }

        /// <summary>
        /// 从 (x,y) 起沿 <paramref name="step"/>（−1 = 向下、+1 = 向上）清掉整段连续的草，遇非草即停。
        /// 每一格都走 <see cref="ParticleWorld.Clear"/>，因而各自按有机衰变规则留下腐殖质。
        /// </summary>
        private static void ClearCascade(ParticleWorld world, int x, int y, int step)
        {
            int columnY = y;
            while (world.GetCell(x, columnY).typeId == ParticleIds.Grass)
            {
                world.Clear(x, columnY);
                columnY += step;
            }
        }

        /// <summary>
        /// (x,y) 是否扎根：正下方是土 / 湿土 / 腐殖土（<see cref="ParticleIds.Soil"/> /
        /// <see cref="ParticleIds.WetSoil"/> / <see cref="ParticleIds.HumusSoil"/>），或是**同株草的下段**
        /// （只有整株最底下那格需要土壤，上面的草段靠下段支撑）。空气 / 石头 / 沙 / 水等一律视为没扎根。
        /// </summary>
        private static bool IsRooted(ParticleWorld world, int x, int y)
        {
            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || belowId == ParticleIds.Grass;
        }

        /// <summary>
        /// (x,y) 是否被压住：正上方是**固体或液体**且**不是草**。
        /// 于是沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、**草种子**（固体）与**水**（液体）都算压；
        /// 同株草摞在草上不算压，空气与气体（大气）也不算。
        /// </summary>
        private static bool IsPressed(ParticleWorld world, int x, int y)
        {
            // 同株草与空气明确不算压（草靠这一条避免自己压死自己；空气不是物质）。
            ushort aboveId = world.GetCell(x, y + 1).typeId;
            if (aboveId == ParticleRegistry.AirTypeId || aboveId == ParticleIds.Grass)
            {
                return false;
            }

            // 固体（含草种子等有机物）与液体都算压；气体不算。
            ParticleTags above = ParticleRules.TagsAt(world, x, y + 1);
            return (above & (ParticleTags.Solid | ParticleTags.Liquid)) != 0;
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            RunRandomTick(world, x, y);
        }

        /// <summary>
        /// 把半径 <see cref="SandConversionRadius"/> 格**圆盘**内的第一个沙变成土（固定扫描序：
        /// y 升序、同一行 x 升序）。同一份物质换形态：走 <see cref="ParticleWorld.SetPhase"/>，热量 Q 原样留在该格。
        /// </summary>
        private static void ConvertNearestSand(ParticleWorld world, int x, int y)
        {
            for (int dy = -SandConversionRadius; dy <= SandConversionRadius; dy++)
            {
                for (int dx = -SandConversionRadius; dx <= SandConversionRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (dx * dx + dy * dy > SandConversionRadius * SandConversionRadius)
                    {
                        continue;
                    }

                    if (world.GetCell(x + dx, y + dy).typeId != ParticleIds.Sand)
                    {
                        continue;
                    }

                    world.SetPhase(x + dx, y + dy, ParticleIds.Soil);
                    return;
                }
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 草被覆盖时无残留效果。
        }

        /// <summary>
        /// (x,y) 能不能托住一颗草种子：是固体（<see cref="ParticleRules.IsSolid"/>）且**不是草**
        /// （<see cref="ParticleIds.Grass"/>）。排除草是为了**阻止草往别的草头上撒种**——否则落点会在
        /// 已有草的顶上，种子悬在草尖上、掉落时又不会穿过同为 Solid 的草，观感与逻辑都不合理。
        /// 石头 / 沙 / 土 / 湿土 / 腐殖土等无机（及腐殖土）固体照常可托住。
        /// </summary>
        private static bool IsSeedSupport(ParticleWorld world, int x, int y)
        {
            return ParticleRules.IsSolid(world, x, y) && world.GetCell(x, y).typeId != ParticleIds.Grass;
        }

        /// <summary>
        /// 在半径 <see cref="SeedSpawnRadius"/> 格的**圆盘**内找第一个「下方 1 格是**可托住种子的固体**的空地」，
        /// 在那里生成一颗新草种子（<see cref="ParticleWorld.Set"/>，新物质）。
        /// 「空地」= 空气或气体（<see cref="ParticleRules.IsGasOrEmpty"/>）；「可托住」见
        /// <see cref="IsSeedSupport"/>（固体且非草，因此不会撒到别的草头上）。找到即返回 true（至多落一颗）。
        /// </summary>
        private static bool TrySpawnSeed(ParticleWorld world, int x, int y)
        {
            for (int dy = -SeedSpawnRadius; dy <= SeedSpawnRadius; dy++)
            {
                for (int dx = -SeedSpawnRadius; dx <= SeedSpawnRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (dx * dx + dy * dy > SeedSpawnRadius * SeedSpawnRadius)
                    {
                        continue;
                    }

                    int nx = x + dx;
                    int ny = y + dy;

                    // 落点必须是空地（空气 / 气体），且正下方 1 格是**可托住种子的固体**（种子能停住、不会落走）。
                    if (!ParticleRules.IsGasOrEmpty(world, nx, ny) || !IsSeedSupport(world, nx, ny - 1))
                    {
                        continue;
                    }

                    world.Set(nx, ny, ParticleIds.GrassSeed);
                    return true;
                }
            }

            return false;
        }
    }
}
