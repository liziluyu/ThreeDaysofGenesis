using UnityEngine;

namespace TapTap2026.Simulation
{
    // GrassParticle —— 草（固体：长在原地不动；每逻辑帧衰老，随机刻把附近的沙变成土、并以 10% 概率撒下新草种子）
    //
    // flags 私有位：bit3-5（CellFlags.GrassAgeRemainderMask）—— 年龄的**逻辑帧余数** 0..TicksPerShadeStep-1。
    //              年龄 = Cell.shade × TicksPerShadeStep + 余数：shade 存高位（它同时就是**颜色档**，
    //              越老越接近黄绿），余数存低位——两段拼起来才够数到 LifespanTicks（1200 > 255），
    //              且不引入新的 per-cell 字段（Cell 仍 4 字节）。
    // 邻居查询范围：随机刻时以自身为中心、半径 2 格（GrassParticle.SandConversionRadius /
    //               SeedSpawnRadius）的圆盘，以及候选空地下方 1 格（判固体支撑）
    // 交互的 ParticleIds：Sand → Soil（把沙变成土）；GrassSeed（撒下新草种子）；
    //                     Flower（同一株：花是草株顶端的显色格——互不算压、互为扎根、级联死亡一起清）
    //
    // 行为：
    //   运动帧（OnMotion）**空**：草长在原地，不落、不流。它不是 IsStatic：
    //       随机刻会跳过静态类型（ParticleWorld.RunRandomTicks 检查 IsStatic），所以草必须是普通类型
    //       才能收到随机刻；靠空的 OnMotion（而不是 IsStatic）保证它不移动。草带 Solid 标签且与同 rank
    //       的固体（沙 / 土 / 石）互不置换（CanDisplace 要求严格更高的 rank），因此上方的沙会停在草上，
    //       草也不会被同类顶掉。
    //   逻辑帧（OnLogic）：**衰老**。每逻辑帧年龄 +1；每攒满 TicksPerShadeStep（5）帧就把 Cell.shade
    //       推进一档（颜色从年轻绿渐变到老黄绿），年龄到 LifespanTicks（1200 ≈ 60 秒 @20 Hz）即**寿终**——
    //       走 Clear，因而按 20% 概率就地留下腐殖质。衰老只用 shade 与 flags bit3-5（无新 per-cell 字段）、
    //       不消耗世界随机流、不读邻居、不移动。压死 / 失去扎根 / 温度致死仍在随机刻里（见下）。
    //   随机刻（OnRandomTick）每次命中按固定顺序执行，死亡一旦触发就清掉整段并 return（本帧不再撒种/固沙）：
    //       ① **被压住就死**：正上方是**固体或液体、且不是同一株**（沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、
    //          草种子与**水**都算压）时，草被压死，死亡沿**竖直方向向下**传播，把下面整段连续的草（含花）
    //          一并清掉；同一株的草 / 花摞在草上不算压。
    //       ② **没扎根就死**：正下方不是土 / 湿土 / 腐殖土（也不是同一株的下段）时，这一格草失去支撑而死，
    //          死亡沿**竖直方向向上**传播，把上面整段连续的草（含花）一并清掉（根在下面，土没了整株一起死）。
    //       ③ **温度致死**：本格温度 > LethalHeatTemperature（50）或 < LethalColdTemperature（3）时
    //          这一格草死亡（只死这一格；相邻草格各有自己的温度，级联随时间涌现）。
    //       三种死法都走 Clear，因而各自按 20% 概率留下腐殖质。
    //       ④ **撒种（10% 概率）**：先摇一次骰（NextUInt() % SeedSpawnChanceDenominator == 0，每次都摇、
    //          消耗 1 个随机数，保证确定性），命中才在半径 2 格圆盘内找**第一个「下方 1 格是可托住种子的
    //          固体的空地」**（固定扫描序：y 升序、同行 x 升序，只取 dx²+dy² ≤ 半径²），在那里 Set 一颗
    //          新草种子。「空地」= 空气或气体（ParticleRules.IsGasOrEmpty）——世界被大气填满，若只认
    //          typeId 0 则永远找不到落点；「可托住」= 固体且不是草（IsSeedSupport），不会撒到别的草头上。
    //          找不到落点就什么都不做。
    //       ⑤ **固沙（无概率门）**：在半径 2 格圆盘内找第一个沙，SetPhase 成土。
    //   随机刻默认关闭（world.RandomTicksPerStep = 0，见 ParticleWorld），生产值由
    //   ParticleSimulationSettings._randomTicksPerStep 注入。
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

        /// <summary>
        /// 寿命（逻辑帧）：年龄到此即寿终（1200 逻辑帧 ≈ 60 秒 @20 Hz）。「暂时设定」——改这里即可，
        /// 只需保证能被 <see cref="TicksPerShadeStep"/> 整除、且 <see cref="ShadeCount"/> ≤ 256。
        /// </summary>
        public const int LifespanTicks = 1200;

        /// <summary>
        /// 每推进一档 <see cref="Cell.shade"/>（颜色档）所需的逻辑帧数：年龄的量化粒度。
        /// 不足一档的余数存在 <see cref="CellFlags.GrassAgeRemainderMask"/>（3 位，故本值必须 ≤ 8）。
        /// </summary>
        public const int TicksPerShadeStep = 5;

        /// <summary>
        /// 颜色档数量 = 调色板长度：年龄 <c>[0, LifespanTicks)</c> 量化成这么多档
        /// （下标 0 = 最年轻、末档 = 最老）。受 <see cref="Cell.shade"/> 是 <c>byte</c>、且调色板 ≤ 256 限制。
        /// </summary>
        public const int ShadeCount = LifespanTicks / TicksPerShadeStep;

        /// <summary>最年轻草色（深绿）。</summary>
        private static readonly Color32 YoungColor = new Color32(58, 124, 50, 255);

        /// <summary>最老草色（黄绿：红 / 绿升高、蓝压低）。</summary>
        private static readonly Color32 OldColor = new Color32(170, 178, 66, 255);

        /// <summary>
        /// 年龄 → 颜色的**渐变**调色板：下标 = <see cref="Cell.shade"/>（= 年龄档），
        /// <c>Palette[0]</c> 最年轻、<c>Palette[ShadeCount-1]</c> 最老。草的 <c>shade</c> 因此是**派生量**
        /// （由 <see cref="OnLogic"/> 的衰老推进，而非 <c>Set</c> 的随机抽样——抽样值在 <see cref="OnSpawn"/> 被覆盖），
        /// 与火「shade = 温度分档」同一模式；渲染只读 shade 查表，故「越老越接近黄绿色」直接在画面上成立。
        /// </summary>
        private static readonly Color32[] Palette = BuildPalette();

        private static Color32[] BuildPalette()
        {
            var palette = new Color32[ShadeCount];
            for (int i = 0; i < ShadeCount; i++)
            {
                palette[i] = Color32.Lerp(YoungColor, OldColor, (float)i / (ShadeCount - 1));
            }

            return palette;
        }

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
            lightAbsorption: LightAbsorptionFactor); // HasLogic 取默认 true：OnLogic 现做衰老

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 草长在原地：运动帧什么都不做。之所以不用 IsStatic，是因为静态类型收不到随机刻
            // （ParticleWorld.RunRandomTicks 会跳过 IsStatic），而草需要随机刻来撒种、把附近的沙变成土。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 衰老：年龄 = Cell.shade × TicksPerShadeStep + flags bit3-5 的余数。每逻辑帧年龄 +1：
            // 未满一档只推进余数；攒满一档把 shade +1（颜色向黄绿推进一档）；shade 到顶即年龄到 LifespanTicks。
            ref Cell cell = ref world.At(x, y);
            int remainder = ((cell.flags & CellFlags.GrassAgeRemainderMask) >> CellFlags.GrassAgeRemainderShift) + 1;

            if (remainder < TicksPerShadeStep)
            {
                cell.flags = (byte)((cell.flags & CellFlags.GrassAgeRemainderClearMask)
                    | (remainder << CellFlags.GrassAgeRemainderShift));
                return;
            }

            int shade = cell.shade + 1;
            if (shade >= ShadeCount)
            {
                // 寿终：走 Clear —— 带 Organic 标签，世界按 20% 概率就地留下腐殖质（与其它死法同一条衰变规则）。
                world.Clear(x, y);
                return;
            }

            cell.shade = (byte)shade;
            cell.flags = (byte)(cell.flags & CellFlags.GrassAgeRemainderClearMask); // 余数归零
        }

        /// <summary>
        /// 死亡判定（①②③）与撒种 / 固沙（④⑤）都在随机刻里按固定顺序执行。死亡一旦触发就清掉整段同株（草 / 花）并返回，
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
        /// 从 (x,y) 起沿 <paramref name="step"/>（−1 = 向下、+1 = 向上）清掉整段**同一株**（草 / 花），遇非同一株即停。
        /// 每一格都走 <see cref="ParticleWorld.Clear"/>，因而各自按有机衰变规则留下腐殖质。
        /// </summary>
        private static void ClearCascade(ParticleWorld world, int x, int y, int step)
        {
            int columnY = y;
            while (IsStalkMember(world.GetCell(x, columnY).typeId))
            {
                world.Clear(x, columnY);
                columnY += step;
            }
        }

        /// <summary>
        /// (x,y) 上的粒子是否属于**同一株草**——草或它顶端的花（<see cref="FlowerParticle"/>）。
        /// 草种子发芽时有 10% 概率把株顶那格草换成花，因此一株草的顶端可能是花：两者互不算压、
        /// 互为扎根、级联死亡时一起清。
        /// </summary>
        private static bool IsStalkMember(ushort typeId)
        {
            return typeId == ParticleIds.Grass || typeId == ParticleIds.Flower;
        }

        /// <summary>
        /// (x,y) 是否扎根：正下方是土 / 湿土 / 腐殖土（<see cref="ParticleIds.Soil"/> /
        /// <see cref="ParticleIds.WetSoil"/> / <see cref="ParticleIds.HumusSoil"/>），或是**同一株的下段**
        /// （草 / 花——只有整株最底下那格需要土壤，上面的草段靠下段支撑）。空气 / 石头 / 沙 / 水等一律视为没扎根。
        /// </summary>
        private static bool IsRooted(ParticleWorld world, int x, int y)
        {
            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || IsStalkMember(belowId);
        }

        /// <summary>
        /// (x,y) 是否被压住：正上方是**固体或液体**且**不是同一株**（草 / 花）。
        /// 于是沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、**草种子**（固体）与**水**（液体）都算压；
        /// 同一株的草 / 花不算压（否则花会当场压死自己脚下那格草），空气与气体（大气）也不算。
        /// </summary>
        private static bool IsPressed(ParticleWorld world, int x, int y)
        {
            // 同一株（草 / 花）与空气明确不算压（草靠这一条避免自己压死自己；花靠这一条不压死自己那株草）。
            ushort aboveId = world.GetCell(x, y + 1).typeId;
            if (aboveId == ParticleRegistry.AirTypeId || IsStalkMember(aboveId))
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
            // 新生草从**最年轻**的一档开始：ParticleWorld.Set / SetPhase 的随机 shade 抽样被这里覆盖。
            // 年龄余数位（flags bit3-5）由写入原语清零（flags = 当前标记），无需处理。
            world.At(x, y).shade = 0;
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 草被覆盖时无残留效果。
        }

        /// <summary>
        /// (x,y) 能不能托住一颗草种子：是固体（<see cref="ParticleRules.IsSolid"/>）且**不是同一株**
        /// （草 <see cref="ParticleIds.Grass"/> / 花 <see cref="ParticleIds.Flower"/>）。排除同一株是为了
        /// **阻止草往别的草 / 花头上撒种**——否则落点会在草尖或花头上，种子悬在那里、掉落时又不会穿过同为
        /// Solid 的草。石头 / 沙 / 土 / 湿土 / 腐殖土等固体照常可托住。
        /// </summary>
        private static bool IsSeedSupport(ParticleWorld world, int x, int y)
        {
            return ParticleRules.IsSolid(world, x, y) && !IsStalkMember(world.GetCell(x, y).typeId);
        }

        /// <summary>
        /// 在半径 <see cref="SeedSpawnRadius"/> 格的**圆盘**内找第一个「下方 1 格是**可托住种子的固体**的空地」，
        /// 在那里生成一颗新草种子（<see cref="ParticleWorld.Set"/>，新物质）。
        /// 「空地」= 空气或气体（<see cref="ParticleRules.IsGasOrEmpty"/>）；「可托住」见
        /// <see cref="IsSeedSupport"/>（固体且非同一株，因此不会撒到别的草尖 / 花头上）。找到即返回 true（至多落一颗）。
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
