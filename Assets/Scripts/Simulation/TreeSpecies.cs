namespace TapTap2026.Simulation
{
    /// <summary>
    /// 树木物种的**静态参数表**与物种 flags 位的读写 helper。
    /// 编号即 <see cref="CellFlags.TreeSpeciesMask"/>（flags bit3-5）的取值（0..<see cref="Count"/>-1），
    /// **只能追加、不能插队**：物种编号会随数据（存档 / 回放）留在世界里。
    /// 一棵树的物种写在**每一格**（种子 / 树干 / 树叶）的 flags 私有位上，因此树被切开后每一段
    /// 仍然知道自己是什么树、冠形与叶色从哪来。
    /// </summary>
    /// <remarks>
    /// <para>表是只读的：外部通过 <see cref="Count"/> / <see cref="Info"/> / <see cref="NameOf"/> 访问，
    /// 不暴露可变数组；调树种手感只需改这张表，不必动粒子代码。</para>
    /// <para>**树冠偏移表不变量**：数组顺序 = 生长顺序；行按 dy 升序（低的先长），同行 dx = 0 优先、
    /// 再按 |dx| 升序（负先正后）；除首项外的每一项必须与树干列 (0, dy ∈ [-(H-1), 0]) 或表中更早的
    /// 某一项在 Chebyshev 距离 1 以内（**自支撑不变量**，由 TreeParticleTests 守）——树干造冠时
    /// 「第一个可放的未填充项」的八邻必有已有树格，不会长出浮空叶。</para>
    /// <para>**调色板长度不变量**：种子 / 树干 / 树叶的调色板长度必须分别是
    /// <see cref="SeedColorsPerSpecies"/> / <see cref="TrunkColorsPerSpecies"/> / <see cref="LeafColorsPerSpecies"/>
    /// 的整数倍——<see cref="Stamp"/> 把 world 已抽样的 shade 折进本物种色片（<c>shade % k</c>）时才无偏，
    /// 且不额外消耗随机流。</para>
    /// </remarks>
    public static class TreeSpecies
    {
        /// <summary>物种总数。</summary>
        public const int Count = 5;

        /// <summary>杨树：高瘦柱形窄冠、生长最快、喜湿（发芽需八邻有湿土）。</summary>
        public const int Poplar = 0;

        /// <summary>橡树：矮干宽圆冠、生长最慢、喜湿（发芽需八邻有湿土）。</summary>
        public const int Oak = 1;

        /// <summary>松树：高裸干 + 层状锥形冠、耐旱（任意土即可发芽）。</summary>
        public const int Pine = 2;

        /// <summary>柏树：贴干的窄塔形冠（从低处长起）、耐旱（任意土即可发芽）。</summary>
        public const int Cypress = 3;

        /// <summary>柳树：圆冠 + 边缘垂枝、最喜水（正下方必须是湿土）。</summary>
        public const int Willow = 4;

        /// <summary>种子调色板每物种的色数（总长须为其整数倍）。</summary>
        public const byte SeedColorsPerSpecies = 2;

        /// <summary>树干调色板每物种的色数（总长须为其整数倍）。</summary>
        public const byte TrunkColorsPerSpecies = 4;

        /// <summary>树叶调色板每物种的色数（总长须为其整数倍）。</summary>
        public const byte LeafColorsPerSpecies = 4;

        /// <summary>发芽基质档：落土（三土之一）为基线，越喜水的树种要求越湿。</summary>
        public enum SubstrateTier : byte
        {
            /// <summary>耐旱：正下方是三土（土 / 湿土 / 腐殖土）任一即可。</summary>
            Drought = 0,

            /// <summary>喜湿：正下方是三土，且八邻 3×3 内还要有湿土。</summary>
            Moist = 1,

            /// <summary>最喜水：正下方必须恰为湿土。</summary>
            Wet = 2,
        }

        /// <summary>树冠偏移（相对树干顶格 (0,0)；Dx 为 0 的项即正上方那一列）。</summary>
        public readonly struct CrownOffset
        {
            /// <summary>水平偏移（负左正右）。</summary>
            public readonly sbyte Dx;

            /// <summary>竖直偏移（正上负下；负值＝贴干线或垂枝）。</summary>
            public readonly sbyte Dy;

            public CrownOffset(int dx, int dy)
            {
                Dx = (sbyte)dx;
                Dy = (sbyte)dy;
            }
        }

        /// <summary>单个物种的全部参数（派生量在构造时算好，热路径读取零成本）。</summary>
        public readonly struct TreeSpeciesInfo
        {
            /// <summary>中文名（HUD 与文档用，不参与模拟热路径）。</summary>
            public readonly string Name;

            /// <summary>H：树干格数（含发芽那一格）。</summary>
            public readonly int TrunkHeight;

            /// <summary>N：顶格每逻辑帧 1/N 概率长一格（干或叶）。</summary>
            public readonly uint GrowthDenominator;

            /// <summary>D：随机刻命中且条件满足时 1/D 概率发芽（草种子是命中即发＝1，树更慢）。</summary>
            public readonly uint GerminationDenominator;

            /// <summary>发芽基质档。</summary>
            public readonly SubstrateTier Substrate;

            /// <summary>树冠偏移表：**数组顺序 = 生长顺序**（表自支撑，见类型头注释）。</summary>
            public readonly CrownOffset[] Crown;

            /// <summary>树叶「附近有树干」的支撑半径：任一树冠格到树干列的最小 Chebyshev 距离的最大值。</summary>
            public readonly int SupportRadius;

            /// <summary>冠最大半宽（= max |Dx|）。</summary>
            public readonly int MaxHalfWidth;

            /// <summary>冠最低 dy（柏 / 柳为负＝贴干线 / 垂枝）。</summary>
            public readonly int MinCrownDy;

            /// <summary>冠最高 dy。</summary>
            public readonly int MaxCrownDy;

            public TreeSpeciesInfo(
                string name, int trunkHeight, uint growthDenominator, uint germinationDenominator,
                SubstrateTier substrate, CrownOffset[] crown)
            {
                Name = name;
                TrunkHeight = trunkHeight;
                GrowthDenominator = growthDenominator;
                GerminationDenominator = germinationDenominator;
                Substrate = substrate;
                Crown = crown;

                int maxHalfWidth = 0;
                int minDy = 0;
                int maxDy = 0;
                int radius = 0;
                for (int i = 0; i < crown.Length; i++)
                {
                    int dx = crown[i].Dx;
                    int dy = crown[i].Dy;
                    int absDx = dx < 0 ? -dx : dx;
                    if (absDx > maxHalfWidth)
                    {
                        maxHalfWidth = absDx;
                    }

                    if (dy < minDy)
                    {
                        minDy = dy;
                    }

                    if (dy > maxDy)
                    {
                        maxDy = dy;
                    }

                    // 到树干列 (0, dy ∈ [-(H-1), 0]) 的最小 Chebyshev 距离：
                    // dy ≥ 0 的格取 max(|dx|, dy)；贴干的 dy < 0 格取 |dx|；树干不及处按竖直差计。
                    int clampedDy = dy > 0 ? 0 : (dy < -(trunkHeight - 1) ? -(trunkHeight - 1) : dy);
                    int vertical = dy - clampedDy;
                    int distance = absDx > vertical ? absDx : vertical;
                    if (distance > radius)
                    {
                        radius = distance;
                    }
                }

                MaxHalfWidth = maxHalfWidth;
                MinCrownDy = minDy;
                MaxCrownDy = maxDy;
                SupportRadius = radius;
            }
        }

        /// <summary>
        /// 五物种参数表（顺序 = 物种编号，只能追加）。
        /// 生长时长 ≈ (H + 叶数)·N / 20 Hz：杨 ≈ 11s 最快，橡 ≈ 41s 最慢，松 ≈ 19s、柏 ≈ 18s、柳 ≈ 24s。
        /// </summary>
        private static readonly TreeSpeciesInfo[] Table =
        {
            // 杨树：高瘦柱形窄冠（顶格两侧各 1 格 + 正上 7 格收尖），H=12，最快 N=7，喜湿 D=3。
            new TreeSpeciesInfo("杨树", 12, 7u, 3u, SubstrateTier.Moist, BuildCrown(
                (0, 1, 1), (1, 0, 1), (2, 0, 1), (3, 0, 1), (4, 0, 1), (5, 0, 1), (6, 0, 0), (7, 0, 0))),

            // 橡树：矮干宽圆冠（半宽 2,3,3,4,4,3,2,1,0），H=6，最慢 N=14，喜湿 D=4。
            new TreeSpeciesInfo("橡树", 6, 14u, 4u, SubstrateTier.Moist, BuildCrown(
                (0, 1, 2), (1, 0, 3), (2, 0, 3), (3, 0, 4), (4, 0, 4), (5, 0, 3), (6, 0, 2), (7, 0, 1), (8, 0, 0))),

            // 松树：高裸干 + 层状锥形冠（半宽 4,3,3,2,2,1,1,0,0 随高单调不增），H=14，N=7，耐旱 D=2。
            new TreeSpeciesInfo("松树", 14, 7u, 2u, SubstrateTier.Drought, BuildCrown(
                (0, 1, 4), (1, 0, 3), (2, 0, 3), (3, 0, 2), (4, 0, 2), (5, 0, 1), (6, 0, 1), (7, 0, 0), (8, 0, 0))),

            // 柏树：贴干的窄塔形（从顶格下方 3 格起、贴干两列 + 纺锤冠），H=5，N=8，耐旱 D=2。
            new TreeSpeciesInfo("柏树", 5, 8u, 2u, SubstrateTier.Drought, BuildCrown(
                (-3, 1, 1), (-2, 1, 1), (-1, 1, 1), (0, 1, 1), (1, 0, 1), (2, 0, 2), (3, 0, 2),
                (4, 0, 2), (5, 0, 2), (6, 0, 1), (7, 0, 1), (8, 0, 0), (9, 0, 0))),

            // 柳树：圆冠 + 边缘垂枝（垂枝行必须排在支撑行之后），H=7，N=10，最喜水 D=2。
            new TreeSpeciesInfo("柳树", 7, 10u, 2u, SubstrateTier.Wet, BuildCrown(
                (0, 1, 2), (1, 0, 3), (2, 0, 3), (3, 0, 3), (4, 0, 2), (5, 0, 1), (6, 0, 0),
                (-1, 2, 3), (-2, 3, 3))),
        };

        /// <summary>物种总数（= 表长）。</summary>
        public static int SpeciesCount
        {
            get { return Table.Length; }
        }

        /// <summary>取第 <paramref name="species"/> 个物种的参数（越界夹到合法区间）。</summary>
        public static TreeSpeciesInfo Info(int species)
        {
            return Table[ClampSpecies(species)];
        }

        /// <summary>取物种中文名（越界夹到合法区间）。</summary>
        public static string NameOf(int species)
        {
            return Table[ClampSpecies(species)].Name;
        }

        /// <summary>
        /// 读本格物种（flags bit3-5）。越界 / 空槽读到 0（杨树）；残留非法值（≥ Count）夹到 0。
        /// </summary>
        public static int Read(ParticleWorld world, int x, int y)
        {
            int species = (world.At(x, y).flags & CellFlags.TreeSpeciesMask) >> CellFlags.TreeSpeciesShift;
            return species < Table.Length ? species : 0;
        }

        /// <summary>
        /// 给刚创建 / 刚相变的格盖上物种：写 flags bit3-5（掩码读写，保留 bit0 与其它位），并把 world
        /// 已抽样的 shade 折进**本物种的调色板分片**（<c>shade = species·k + 旧 shade % k</c>，无偏、
        /// 不额外消耗随机流）。
        /// **必须在 Set / SetPhase 之后调用**——这两个原语会把 flags 清成边沿标记、重抽 shade。
        /// </summary>
        /// <param name="colorsPerSpecies">本类型每物种的色数（种子 2 / 树干 4 / 树叶 4）。</param>
        public static void Stamp(ParticleWorld world, int x, int y, int species, int colorsPerSpecies)
        {
            int clamped = ClampSpecies(species);
            ref Cell cell = ref world.At(x, y);
            cell.flags = (byte)((cell.flags & CellFlags.TreeSpeciesClearMask)
                | ((clamped << CellFlags.TreeSpeciesShift) & CellFlags.TreeSpeciesMask));
            cell.shade = (byte)(clamped * colorsPerSpecies + cell.shade % colorsPerSpecies);
        }

        /// <summary>
        /// 把「行规格」展开成树冠偏移数组。行规格 <c>(dy, minAbs, maxAbs)</c> 表示该行含
        /// dx ∈ [minAbs, maxAbs] 的正负两侧（minAbs = 0 时先放 dx = 0，再按 |dx| 升序、负先正后）。
        /// 行按**给出的顺序**（= 生长顺序）展开，不做任何排序。
        /// </summary>
        private static CrownOffset[] BuildCrown(params (int dy, int minAbs, int maxAbs)[] rows)
        {
            int count = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                count += rows[i].minAbs == 0
                    ? 1 + 2 * rows[i].maxAbs
                    : 2 * (rows[i].maxAbs - rows[i].minAbs + 1);
            }

            var crown = new CrownOffset[count];
            int index = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                int dy = rows[i].dy;
                if (rows[i].minAbs == 0)
                {
                    crown[index++] = new CrownOffset(0, dy);
                }

                for (int k = rows[i].minAbs > 1 ? rows[i].minAbs : 1; k <= rows[i].maxAbs; k++)
                {
                    crown[index++] = new CrownOffset(-k, dy);
                    crown[index++] = new CrownOffset(k, dy);
                }
            }

            return crown;
        }

        private static int ClampSpecies(int species)
        {
            if (species < 0)
            {
                return 0;
            }

            return species >= Table.Length ? Table.Length - 1 : species;
        }
    }
}
