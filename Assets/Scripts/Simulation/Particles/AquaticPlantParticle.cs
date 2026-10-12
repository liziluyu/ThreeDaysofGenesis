using UnityEngine;

namespace TapTap2026.Simulation
{
    // AquaticPlantParticle —— 水生植物（沉水、扎根水底；逻辑帧衰老 + 随机刻逐格生长 + 四种死法；每格植物 ⇔ 一格水）
    //
    // flags 私有位：bit3-6（CellFlags.GrassAgeRemainderMask，4 位，与草**共用**同一组位；水草只用到低 3 位）
    //              —— 年龄的**逻辑帧余数**
    //              0..TicksPerShadeStep-1。年龄 = Cell.shade × TicksPerShadeStep + 余数：shade 存高位
    //              （它同时就是**颜色档**，越老越接近黄绿），余数存低位。能共用的依据与
    //              CellFlags.SeedStrikeMask 相同：同一时刻一格只属于一个类型，Set / SetPhase 换类型时
    //              **整字节覆写** flags，旧类型残留读不到。
    // 邻居查询范围：随机刻时以自身为中心、半径 2 格（SeedSpawnRadius）的圆盘；生长读正上方 2 格；
    //               死亡判定用正上方一格（①被压）、正下方一格（②扎根）、八邻（③脱水）
    // 交互的 ParticleIds：Water —— 生长消耗 / 死亡归还（1:1 对称，水守恒由构造保证）；
    //                     AquaticSeed —— 撒种的产物；Atmosphere —— 被覆盖时水经湿度场归还（水汽）；
    //                     Soil / WetSoil / HumusSoil / Sand —— 扎根 / 播撒判据（具体类型反应）
    //
    // 行为：
    //   它不是 IsStatic（静态类型收不到随机刻），靠**空的 OnMotion**（而不是静态标记）保持长在原地：草同款。
    //   **OnLogic 只做衰老**（与草同一模式）：每逻辑帧年龄 +1，每攒满 TicksPerShadeStep（5）帧把 Cell.shade
    //   推进一档（颜色由深绿渐变到黄绿），年龄到 LifespanTicks（1200 ≈ 60 秒 @20 Hz）即**寿终**——走 SetPhase
    //   还成水（每格植物 ⇔ 一格水；植物不带 Organic，所以**不**是 Clear / 腐殖质）。衰老只用 shade 与 flags
    //   bit3-6、不消耗世界随机流、不读邻居、不移动。其余全部行为写在随机刻（OnRandomTick），按固定顺序：
    //       ① **被压死**：正上方是**固体且非同类** → 死亡沿竖直向下传播，把下面整段连续的植物**还成水**。
    //          **液体（水）上方是"沉水"常态，不算压**（与草相反——草把液体算压）。
    //       ② **失去扎根**：正下方不是软底 {Soil, WetSoil, HumusSoil, Sand}（也不是同株下段）→ 死亡沿竖直
    //          向上传播，把上面整段连续的植物**还成水**。
    //       ③ **脱水致死**：本格**八邻内没有任何液体**（冒出水面 / 水被排空）→ 只把这一格还成水。
    //          每格各自判定，所以整株的死亡是**逐格涌现**的（从顶端开始，水下的部分只要八邻还有液体就活着）。
    //          已知代价：1 格宽的水道里，除最上面那格外，其余茎格的八邻全是**井壁 + 同株**、没有液体，
    //          会被逐格判成脱水——整株慢慢死光（宽水体里不存在这个问题）。
    //       ④ **温度致死**：本格温度 > LethalHeatTemperature（50）或 < LethalColdTemperature（0）→ 只把这一格
    //          还成水。
    //       **还水**（①~④）：SetPhase 成 Water——每格植物在诞生时消耗了一格水，死亡原样归还，1:1 对称；
    //          SetPhase 保留本格热量。植物**不带 Organic 标签**：物质归水，不再走 20% 腐殖质衰变
    //          （这也避免了发芽时 SetPhase 种子→植物被 20% 衰变截胡）。
    //       ⑤ **生长**：头顶是水、头顶上方**还有水**（始终给植株留一格保命水，防止自我脱水）、
    //          株高 < MaxPlantHeight → 头顶水格 SetPhase 成植物（消耗一格水、长高一格）。
    //          只有最顶上的茎格满足「头顶是水」，所以只有顶格会生长。
    //       ⑥ **撒种（10% 概率）**：每次都摇骰（消耗 1 个随机数，保证确定性）；命中才在半径 2 格圆盘内
    //          找第一个「**软底 + 正上方是水 + 水柱顶端之上有界内气体/空格**」的列：把顶端气体格 Set 成水
    //          （水位上升一格——被顶开的水有处可去），把基质正上方的水格 SetPhase 成水生态种子。
    //          **基质不消耗**（修掉旧版「每撒一次种海底少一格」）；水只是被顶上去，严格守恒。
    //   被覆盖（OnKill）：killerTypeId == Water 是我们自己的「死亡还水」路径（SetPhase 会触发 OnKill），
    //   直接返回不重复结算；其余（被固体压埋 / 被抹除 Clear）把这份水注入八邻第一个气体格的湿度场——
    //   水汽仍计入水账，水不丢；八邻全是固/液（极罕见）时留在本格湿度里，由世界的 EvacuateHumidity
    //   守恒兜底（守恒优先于不变量）。
    public sealed class AquaticPlantParticle : IParticleType
    {
        /// <summary>撒种时搜索落点的半径（格，欧氏距离）：只处理 dx²+dy² ≤ 半径² 的格。</summary>
        public const int SeedSpawnRadius = 2;

        /// <summary>撒种概率的分母：每次随机刻命中时 <c>NextUInt() % 本值 == 0</c> 才尝试撒种，即 1/10 = 10%。</summary>
        public const uint SeedSpawnChanceDenominator = 10u;

        /// <summary>整株最大高度（格）：逐格生长到此为止（沿用旧版一次性抽茎的上限，保持观感）。</summary>
        public const int MaxPlantHeight = 3;

        /// <summary>致死高温（温度严格**高于**它即死）。温度是导出量 <c>T = Q / 热容</c>；与草同值。</summary>
        public const float LethalHeatTemperature = 50f;

        /// <summary>致死低温（温度严格**低于**它即死）。**取 0 而非草的 3**：模拟里环境热量为 0，未被加热的水 / 水下温度就是
        /// <c>T = 0</c>，且水下无光（光停在水面）——取 3 会让水草一挨到未受热的水就冻死。取 0 则只有被主动冷却到
        /// <c>T &lt; 0</c> 的水（模拟里几乎不出现）才会冻死。</summary>
        public const float LethalColdTemperature = 0f;

        /// <summary>光照吸收率：水下收不到光（光停在水面），取值无实际影响，仅与草保持一致的量级。</summary>
        public const float LightAbsorptionFactor = 0.8f;

        /// <summary>
        /// 寿命（逻辑帧）：年龄到此即寿终（1200 逻辑帧 ≈ 60 秒 @20 Hz，与草同档）。「暂时设定」——改这里即可，
        /// 只需保证能被 <see cref="TicksPerShadeStep"/> 整除、且 <see cref="ShadeCount"/> ≤ 256。
        /// </summary>
        public const int LifespanTicks = 1200;

        /// <summary>
        /// 每推进一档 <see cref="Cell.shade"/>（颜色档）所需的逻辑帧数：年龄的量化粒度。
        /// 不足一档的余数存在 <see cref="CellFlags.GrassAgeRemainderMask"/>（4 位，故本值必须 ≤ 16；水草取 5）。
        /// </summary>
        public const int TicksPerShadeStep = 5;

        /// <summary>
        /// 颜色档数量 = 调色板长度：年龄 <c>[0, LifespanTicks)</c> 量化成这么多档
        /// （下标 0 = 最年轻、末档 = 最老）。受 <see cref="Cell.shade"/> 是 <c>byte</c>、且调色板 ≤ 256 限制。
        /// </summary>
        public const int ShadeCount = LifespanTicks / TicksPerShadeStep;

        /// <summary>最年轻水草色（深绿）。</summary>
        private static readonly Color32 YoungColor = new Color32(30, 84, 58, 255);

        /// <summary>最老水草色（黄绿：红 / 绿升高、蓝压低，整株发黄凋敝）。</summary>
        private static readonly Color32 OldColor = new Color32(140, 152, 84, 255);

        /// <summary>
        /// 年龄 → 颜色的**渐变**调色板：下标 = <see cref="Cell.shade"/>（= 年龄档），
        /// <c>Palette[0]</c> 最年轻、<c>Palette[ShadeCount-1]</c> 最老。植物的 <c>shade</c> 因此是**派生量**
        /// （由 <see cref="OnLogic"/> 的衰老推进，而非 Set 的随机抽样——抽样值在 <see cref="OnSpawn"/> 被覆盖），
        /// 与草同一模式；渲染只读 shade 查表，故「越老越接近黄绿色」直接在画面上成立。
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
            tags: ParticleTags.Solid,
            density: 0.6f,
            heatCapacity: 0.80f,   // 与草/干土同档
            thermalConductivity: 0.60f,
            thermalEmissivity: 1.0f,     // 近黑体（水下不暴露，无实际影响）
            affectedByGravity: false,
            isStatic: false,     // 必须非静态，否则收不到随机刻；靠空的 OnMotion 保持不动
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(AquaticPlantParticle),
            lightAbsorption: LightAbsorptionFactor); // HasLogic 取默认 true：OnLogic 现做衰老

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 沉水植物长在原地：运动帧什么都不做。不用 IsStatic 是因为静态类型收不到随机刻。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 衰老：年龄 = Cell.shade × TicksPerShadeStep + flags bit3-6 的余数（水草只用到低 3 位）。每逻辑帧年龄 +1：
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
                // 寿终：还成水（每格植物 ⇔ 一格水，1:1 守恒）。走 SetPhase 而非 Clear——植物不带 Organic，
                // 它的物质归水、不留腐殖质；SetPhase 保留本格热量。
                world.SetPhase(x, y, ParticleIds.Water);
                return;
            }

            cell.shade = (byte)shade;
            cell.flags = (byte)(cell.flags & CellFlags.GrassAgeRemainderClearMask); // 余数归零
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // ① 被压死：正上方是固体且非同类 → 向下把整段还成水。
            if (IsPressed(world, x, y))
            {
                ReturnAsWaterCascade(world, x, y, -1);
                return;
            }

            // ② 失去扎根：正下方不是软底（也不是同株下段）→ 向上把整段还成水。
            if (!IsRooted(world, x, y))
            {
                ReturnAsWaterCascade(world, x, y, 1);
                return;
            }

            // ③ 脱水致死：八邻没有任何液体 → 只把这一格还成水（顶端逐格死，水下的部分存活）。
            if (!HasLiquidNeighbour(world, x, y))
            {
                world.SetPhase(x, y, ParticleIds.Water);
                return;
            }

            // ④ 温度致死：只判这一格，相邻格各判各的。
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature > LethalHeatTemperature || temperature < LethalColdTemperature)
            {
                world.SetPhase(x, y, ParticleIds.Water);
                return;
            }

            // ⑤ 生长：头顶是水、头顶上方还有水（留一格保命水）、株高未到上限 → 头顶水格变成新茎。
            if (world.GetCell(x, y + 1).typeId == ParticleIds.Water
                && world.GetCell(x, y + 2).typeId == ParticleIds.Water
                && ColumnHeightBelow(world, x, y) < MaxPlantHeight)
            {
                world.SetPhase(x, y + 1, ParticleIds.AquaticPlant);
                return;
            }

            // ⑥ 撒种（10% 概率）：骰子每次都摇（保证确定性），命中才搜索落点。
            if (world.NextUInt() % SeedSpawnChanceDenominator == 0u)
            {
                TrySpawnSeed(world, x, y);
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // 新生植物从**最年轻**的一档开始：ParticleWorld.Set / SetPhase 的随机 shade 抽样被这里覆盖
            // （生长新长出的每一格也走这里，因此每格茎各有自己的年龄）。年龄余数位（flags bit3-5）由写入
            // 原语清零（flags = 当前标记），无需处理。
            world.At(x, y).shade = 0;
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // killerTypeId == Water：我们自己的「死亡还水」SetPhase 路径会触发本回调——
            // 水不可能合法覆盖固体植物（rank 2 < 3），所以这个 killer 只可能来自还水，直接返回。
            if (killerTypeId == ParticleIds.Water)
            {
                return;
            }

            // 被其它物质覆盖（固体压埋）或被抹除（Clear）：本格随后会被写入新类型，水不能写回本格，
            // 把这份水注入八邻第一个气体格的湿度场（水汽仍计入水账）。
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (ParticleRules.IsGasOrEmpty(world, x + dx, y + dy))
                    {
                        world.Humidity.Add(x + dx, y + dy, HumidityField.Unit);
                        return;
                    }
                }
            }

            // 八邻全是固/液（极罕见）：守恒优先——留在本格湿度里，世界的 EvacuateHumidity 会推走。
            world.Humidity.Add(x, y, HumidityField.Unit);
        }

        /// <summary>
        /// 从 (x,y) 起沿 <paramref name="step"/>（−1 = 向下、+1 = 向上）把整段连续的水生植物还成水，
        /// 遇非本类型即停。每格 SetPhase 成 Water：死亡归还它在诞生时消耗的那一格水（1:1）。
        /// </summary>
        private static void ReturnAsWaterCascade(ParticleWorld world, int x, int y, int step)
        {
            int columnY = y;
            while (world.GetCell(x, columnY).typeId == ParticleIds.AquaticPlant)
            {
                world.SetPhase(x, columnY, ParticleIds.Water);
                columnY += step;
            }
        }

        /// <summary>从本格向下数连续的同株茎段数（含本格）：株高 = 本格高度，用于生长上限。</summary>
        private static int ColumnHeightBelow(ParticleWorld world, int x, int y)
        {
            int height = 0;
            int columnY = y;
            while (world.GetCell(x, columnY).typeId == ParticleIds.AquaticPlant)
            {
                height++;
                columnY--;
            }

            return height;
        }

        /// <summary>
        /// (x,y) 是否扎根：正下方 ∈ 软底 {Soil, WetSoil, HumusSoil, Sand}，或是**同株下段**
        /// （只有整株最底下那格需要基质，上面的茎段靠下段支撑）。空气 / 气体 / 水 / 石一律视为没扎根。
        /// </summary>
        private static bool IsRooted(ParticleWorld world, int x, int y)
        {
            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || belowId == ParticleIds.WetHumusSoil
                || belowId == ParticleIds.Sand
                || belowId == ParticleIds.AquaticPlant;
        }

        /// <summary>
        /// (x,y) 是否被压住：正上方是**固体**且**不是同类植物**。
        /// 于是沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、水生态种子（固体）都算压；
        /// **液体（水）不算压**（沉水常态）、同株茎的上一段不算压、气体 / 空气不算压。
        /// </summary>
        private static bool IsPressed(ParticleWorld world, int x, int y)
        {
            ushort aboveId = world.GetCell(x, y + 1).typeId;
            if (aboveId == ParticleIds.AquaticPlant)
            {
                return false;
            }

            return (ParticleRules.TagsAt(world, x, y + 1) & ParticleTags.Solid) != 0;
        }

        /// <summary>(x,y) 的**八邻**（3×3 去掉自身）里是否有带 <see cref="ParticleTags.Liquid"/> 标签的格。
        /// 越界由 <see cref="ParticleWorld.GetCell"/> 返回空气，天然不算。</summary>
        private static bool HasLiquidNeighbour(ParticleWorld world, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (ParticleRules.HasTag(world, x + dx, y + dy, ParticleTags.Liquid))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>软底判据：该格 ∈ {Soil, WetSoil, HumusSoil, Sand}。</summary>
        private static bool IsSoftSubstrate(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            return id == ParticleIds.Soil
                || id == ParticleIds.WetSoil
                || id == ParticleIds.HumusSoil
                || id == ParticleIds.WetHumusSoil
                || id == ParticleIds.Sand;
        }

        /// <summary>
        /// 在半径 <see cref="SeedSpawnRadius"/> 格的**圆盘**内找第一个「软底 + 正上方是水 +
        /// 水柱顶端之上有界内气体/空格」的列：把水柱顶端之上的气体格 <see cref="ParticleWorld.Set"/> 成水
        /// （水位上升一格），再把基质正上方的水格 <see cref="ParticleWorld.SetPhase"/> 成
        /// <see cref="ParticleIds.AquaticSeed"/>。水只是被顶上去、不销毁；基质格不动（海底不被啃）。
        /// 固定扫描序：dy 升序、同一行 dx 升序。找到即返回 true（至多一颗）。
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

                    // 落点列：软底 + 正上方是水。
                    if (!IsSoftSubstrate(world, nx, ny)
                        || world.GetCell(nx, ny + 1).typeId != ParticleIds.Water)
                    {
                        continue;
                    }

                    // 向上找连续水柱的顶端，顶端之上必须是界内气体/空格（被顶开的水的接收格）。
                    int top = ny + 1;
                    while (world.IsInBounds(nx, top + 1)
                        && world.GetCell(nx, top + 1).typeId == ParticleIds.Water)
                    {
                        top++;
                    }

                    int receiverY = top + 1;
                    if (!world.IsInBounds(nx, receiverY)
                        || !ParticleRules.IsGasOrEmpty(world, nx, receiverY))
                    {
                        continue; // 水柱封顶：被顶开的水没地方去，换下一列。
                    }

                    world.Set(nx, receiverY, ParticleIds.Water);
                    world.SetPhase(nx, ny + 1, ParticleIds.AquaticSeed);
                    return true;
                }
            }

            return false;
        }
    }
}
