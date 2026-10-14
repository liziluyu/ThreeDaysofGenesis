using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物死亡的**物质化器**：把死亡动物的物种轮廓（<see cref="BiomassProfile"/>）按固定映射逐格写进
    /// 世界的生物质粒子（<see cref="ParticleIds.Biomass"/>）。静态、无状态、只写世界，出现在
    /// <see cref="CreatureWorld.Step"/> 的死亡清扫阶段、<c>OnDeath</c> / <c>CreatureDied</c> **之前**。
    /// </summary>
    /// <remarks>
    /// <para><b>职责边界：</b>只做一件事——按 profile 写 Biomass。不参与 AI 决策、不进动物物理热路径、
    /// 不读渲染资源、不分配任何托管对象（轮廓是一段静态 <c>sbyte[]</c>，候选偏移是一段静态 <c>int[]</c>，
    /// 遍历是纯索引循环）。</para>
    /// <para><b>只走世界 API：</b>写入一律 <see cref="ParticleWorld.SetWithShade"/>（原子、语义 = <c>Set</c>，
    /// 但带调用方给出的颜色索引）；挪水一律 <see cref="ParticleWorld.Swap"/>——它自己负责换格 + 热量 + 湿度
    /// 跟着物质走 + 绝热直减 + 写入版本 + 运动休眠唤醒；绝不直接写 <c>_cells</c>。</para>
    /// <para><b>定位映射</b>（与 <c>CreatureRenderer</c> 的摆放公式逐格对齐——sprite 的 pivot 点落在
    /// <c>(X, Y)</c>、水平居中原点 = <c>X</c>、1 源像素 = 1 格）：<c>世界 x = floor(creature.X) - 8 + dx</c>、
    /// <c>世界 y = floor(creature.Y) - PivotRowCells + dy</c>。其中 <c>dy 0 = 精灵画布底边</c>，
    /// <c>PivotRowCells</c> = 切片 pivot 距底边的像素行（<see cref="BiomassProfile.PivotRowCells"/>）——
    /// 减掉它后尸体与渲染身体**逐格对齐**（鱼 pivot = 底边上 5px ⇒ 尸体不再高 5 格，见 docs §22）。</para>
    /// <para><b>遍历顺序 = 契约：</b>两趟（先挪水、后写生物质）都固定按 <c>dy</c> 升序、同行 <c>dx</c> 升序
    /// 逐格处理，零随机数。同 seed 重放只有顺序一致才能逐格复现；<c>shade</c> 直接用 profile 索引，
    /// 不重抽（重抽会把尸体打散成随机噪点）。</para>
    /// <para><b>覆盖规则（三档）：</b></para>
    /// <list type="number">
    /// <item><description><b>直接写：</b>空气 / 大气 / 草 / 花 / 树叶（软植被允许尸体把它们压死），外加
    /// <see cref="CreatureTerrain.IsSolid"/> 为真的**地形固体**（沙 / 石头 / 土家族）——山崩压死的尸体正埋在
    /// 这些格子里，**必须可见**，否则尸体凭空消失（见 <see cref="CanCorpseOccupy"/>）。</description></item>
    /// <item><description><b>水：先挪开再写。</b>先把落在轮廓格上的水用 <see cref="ParticleWorld.Swap"/> 挪到
    /// 半径 ≤ 2 内的合格空位，再把生物质写进腾出来的格子；**绝不删水**（水守恒）。挪不动就跳过该格、
    /// 把水原样留在原地。</description></item>
    /// <item><description><b>跳过：</b>火 / 树干 / 已有生物质 / 其它未列类型（保持「新增类型必须显式决定」的风格）。</description></item>
    /// </list>
    /// <para><b>为什么挪水的目标格要排除本次轮廓的非空格：</b>写循环是 <c>dy</c> 升序，落在后面的轮廓格稍后
    /// 会被生物质覆盖；若把水挪到那些格子上，那口水会随即被覆盖而**凭空消失**，水守恒就破了。因此合格空位
    /// 必须同时满足：在界内、是空气 / 大气、且**不在本次要写的格集合内**。</para>
    /// </remarks>
    public static class CreatureDeathMaterializer
    {
        /// <summary>
        /// 挪水候选偏移（相对被挪格；半径 ≤ 2，确定性、零随机）：先正上方两格（尸体下沉把水往上挤），
        /// 再同高左右各两格，再下方两格，最后四个斜角。平面成对存（<c>dx, dy</c>）；越界 / 非空气 /
        /// 落在轮廓非空格上的候选继续往后找。
        /// </summary>
        private static readonly int[] WaterRelocateOffsets =
        {
            0, 1,
            0, 2,
            -1, 0,
            1, 0,
            -2, 0,
            2, 0,
            0, -1,
            0, -2,
            -1, 1,
            1, 1,
            -1, -1,
            1, -1,
        };

        /// <summary>
        /// 把 <paramref name="creature"/> 的死亡轮廓写入 <paramref name="world"/> 的生物质格。
        /// 死因不参与轮廓选择（四种死因轮廓相同），也**不消耗世界的随机流**（写入走显式 shade，不抽样；
        /// 挪水走 <see cref="ParticleWorld.Swap"/>，同样不抽样）。
        /// </summary>
        /// <param name="world">目标粒子世界。</param>
        /// <param name="creature">刚死亡的动物实体（读 <c>X</c> / <c>Y</c> 与物种）。</param>
        /// <returns>实际写入的格数（越界、跳过档、或挪不动的水所占的格不计）。</returns>
        public static int Materialize(ParticleWorld world, Creature creature)
        {
            if (world == null || creature == null)
            {
                return 0;
            }

            BiomassProfile profile = BiomassProfile.For(creature);
            sbyte[] cells = profile.Cells;

            // 精灵左列 → 世界列 floor(X) - Size/2；dy 0 = 精灵画布底边 ⇒ 世界行 floor(Y) - PivotRowCells。
            int baseX = (int)Mathf.Floor(creature.X) - BiomassProfile.Size / 2;
            int baseY = (int)Mathf.Floor(creature.Y) - profile.PivotRowCells;

            // 第一趟：把落在轮廓格上的水挪到合格空位（dy 升序、同行 dx 升序）。挪不动的保持原样——
            // 第二趟会因它仍是水而自然跳过（水原样保留，绝不删水）。
            for (int dy = 0; dy < BiomassProfile.Size; dy++)
            {
                int y = baseY + dy;
                int rowStart = dy * BiomassProfile.Size;
                for (int dx = 0; dx < BiomassProfile.Size; dx++)
                {
                    if (cells[rowStart + dx] < 0)
                    {
                        // 轮廓空格：不参与。
                        continue;
                    }

                    int x = baseX + dx;
                    if (!world.IsInBounds(x, y))
                    {
                        // 越界跳过。
                        continue;
                    }

                    if (CreatureTerrain.IsWater(world, x, y))
                    {
                        TryRelocateWater(world, x, y, baseX, baseY, cells);
                    }
                }
            }

            // 第二趟：逐格写生物质（同样的 dy / dx 升序 = 契约）。
            int written = 0;
            for (int dy = 0; dy < BiomassProfile.Size; dy++)
            {
                int y = baseY + dy;
                int rowStart = dy * BiomassProfile.Size;
                for (int dx = 0; dx < BiomassProfile.Size; dx++)
                {
                    sbyte paletteIndex = cells[rowStart + dx];
                    if (paletteIndex < 0)
                    {
                        // 轮廓空格：不写。
                        continue;
                    }

                    int x = baseX + dx;
                    if (!world.IsInBounds(x, y))
                    {
                        // 越界跳过。
                        continue;
                    }

                    if (!CanCorpseOccupy(world, x, y))
                    {
                        // 已挪走水后仍不是空气 / 大气 / 软植被 / 地形固体：跳过档（火 / 树干 / 已有生物质 /
                        // 挪不动的水 / 其它未列类型），保持原格不变。
                        continue;
                    }

                    if (world.SetWithShade(x, y, ParticleIds.Biomass, (byte)paletteIndex))
                    {
                        written++;
                    }
                }
            }

            return written;
        }

        /// <summary>
        /// 尝试把 (x,y) 上的水挪到半径 ≤ 2 内的合格空位（<see cref="ParticleWorld.Swap"/>）。
        /// 合格 = 在界内、<see cref="ParticleIds.Air"/> / <see cref="ParticleIds.Atmosphere"/>、
        /// 且**不在本次轮廓的非空格集合内**——否则那格稍后会被生物质覆盖，挪过去的水会凭空消失
        /// （水守恒被破坏）。找不到合格目标就什么都不做：该格保持水，由第二趟跳过。
        /// </summary>
        /// <param name="world">目标世界。</param>
        /// <param name="x">被挪水格的 x。</param>
        /// <param name="y">被挪水格的 y。</param>
        /// <param name="baseX">轮廓映射基准列（算目标格是否落在轮廓非空格上）。</param>
        /// <param name="baseY">轮廓映射基准行。</param>
        /// <param name="cells">轮廓数据（<c>dy * Size + dx</c>；≥ 0 = 该格要写）。</param>
        private static void TryRelocateWater(ParticleWorld world, int x, int y, int baseX, int baseY, sbyte[] cells)
        {
            for (int i = 0; i < WaterRelocateOffsets.Length; i += 2)
            {
                int targetX = x + WaterRelocateOffsets[i];
                int targetY = y + WaterRelocateOffsets[i + 1];
                if (!world.IsInBounds(targetX, targetY))
                {
                    continue;
                }

                ushort typeId = world.GetCell(targetX, targetY).typeId;
                if (typeId != ParticleIds.Air && typeId != ParticleIds.Atmosphere)
                {
                    continue;
                }

                if (IsOutlineCell(baseX, baseY, cells, targetX, targetY))
                {
                    continue;
                }

                world.Swap(x, y, targetX, targetY);
                return;
            }
        }

        /// <summary>目标格是否落在本次轮廓的**非空**格上（即稍后会被生物质写入，因而不适合接收水）。</summary>
        private static bool IsOutlineCell(int baseX, int baseY, sbyte[] cells, int x, int y)
        {
            int dx = x - baseX;
            int dy = y - baseY;
            if (dx < 0 || dx >= BiomassProfile.Size || dy < 0 || dy >= BiomassProfile.Size)
            {
                return false;
            }

            return cells[dy * BiomassProfile.Size + dx] >= 0;
        }

        /// <summary>
        /// 尸体轮廓能否**直接**覆盖本格（三档里的「直接写」档）：
        /// <list type="bullet">
        /// <item><description>空气 / 大气（气体）、草 / 花 / 树叶（软植被，允许尸体把它们压死——其
        /// <see cref="ParticleTags.Organic"/> 衰变由世界写入规则处理）；</description></item>
        /// <item><description><see cref="CreatureTerrain.IsSolid"/> 为真的地形固体（沙 / 石头 / 土家族）——
        /// 山崩压死的尸体必须可见。</description></item>
        /// </list>
        /// 其余（火 / 树干 / 已有生物质 / 水 / 其它未列类型）一律 false。
        /// </summary>
        /// <remarks>
        /// 气体与软植被按 <see cref="ParticleIds"/> 常量写死白名单（与 <see cref="CreatureTerrain"/> 同一风格）：
        /// 未来新增类型要**显式**决定能不能被尸体覆盖，而不是被某个泛化的标签规则悄悄带进来。地形固体这一档
        /// **复用 <see cref="CreatureTerrain.IsSolid"/> 的同一份白名单**，避免「挡动物的固体」与「能让尸体可见的
        /// 固体」两处清单漂移。水不走这一档：它由 <see cref="Materialize"/> 的第一趟先挪开。
        /// </remarks>
        private static bool CanCorpseOccupy(ParticleWorld world, int x, int y)
        {
            ushort typeId = world.GetCell(x, y).typeId;
            if (typeId == ParticleIds.Air
                || typeId == ParticleIds.Atmosphere
                || typeId == ParticleIds.Grass
                || typeId == ParticleIds.Flower
                || typeId == ParticleIds.TreeLeaf)
            {
                return true;
            }

            return CreatureTerrain.IsSolid(world, x, y);
        }
    }
}
