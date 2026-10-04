using UnityEngine;

namespace TapTap2026.Simulation
{
    // WetSoilParticle —— 湿土（固体标签；摩擦度/粘性沿用土的 0.5/0.5，堆形与干土一致）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正上方一格（受热上行的目标 / 蒸发口）、正下方一格（常温下渗的目标），
    //               以及 StepSolid 的「正下、左下/右下、斜滑反侧同一行」
    // 交互的 ParticleIds：Soil —— 水交给干土时把那格换成 WetSoil、本格变回 Soil（双向，走 ParticleIds）
    //
    // 湿土 = 干土 + 一份水。它每逻辑帧（20 Hz）先试着把这份水交给邻格（OnLogic），
    // 交不出去就留在原地；运动（下落 / 斜落）在运动帧由 OnMotion 负责，两者互不干扰。
    //
    // 三条路，成功即本格变干（水要么进了邻格的干土，要么进了空气，湿土不会凭空变干）：
    //   · 受热上行（T ≥ DryingTemperature）：水优先往上走——上方是干土就把那格变成湿土；
    //     上方是空气且空气没吸饱，就像水粒子慢蒸发一样把一份水汽摊进空气（不可见，走湿度场）。
    //   · 常温下渗：每逻辑帧有 1/PercolationDenominator（= 50%）的概率把水交给**正下方**的干土，
    //     水因此沿重力方向一格格渗下去，直到柱底或再没有干土可接。
    //   · 两条都走不通：水留在土里，本格保持湿土（例如被石头/沙/水围住的湿土）。
    //
    // 只有**干土**（ParticleIds.Soil）会接水——石头、沙、水都不行：它们是别的物质，没有"湿"的形态。
    // 换格走 SetPhase：热量 Q 留在原格不搬（详见类尾注释与 docs 的"水在湿土里"一节）。
    public sealed class WetSoilParticle : IParticleType
    {
        /// <summary>
        /// 干燥温度阈值：湿土温度达到它，水就改往**上**走（受热上行）——上方是干土则润湿之，
        /// 上方是空气则蒸发进空气。低于它时水只按重力方向下渗。
        /// 取 60（水：慢蒸发 20 / 沸腾 80）：白天晒热的湿土表层会往上走并在表面蒸发，
        /// 埋在地下、温度接近环境值的湿土则一路往下渗——这正是"表层被晒干、水往深处走"的观感。
        /// </summary>
        public const float DryingTemperature = 60f;

        /// <summary>下渗概率分母：每逻辑帧有 <c>1/N</c> 的概率把水交给正下方的干土。N = 2 → 50%（需求值）。</summary>
        public const uint PercolationDenominator = 2u;

        private static readonly Color32[] Palette =
        {
            new Color32(48, 34, 22, 255),
            new Color32(62, 46, 30, 255),
            new Color32(76, 58, 38, 255),
            new Color32(92, 72, 50, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 2.0f,
            heatCapacity: 2.0f,   // 湿土：水（6.5）与干土（0.8）之间——比干土扛热得多
            thermalConductivity: 0.85f,   // 湿土：水（0.95）与干土（0.70）之间——湿土比干土导热好
            thermalEmissivity: 1.0f,      // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(WetSoilParticle));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 湿土的实体运动与干土完全相同（同样的 friction / stickiness ⇒ 同样的安息角）。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 1) 受热上行：水优先往**上**走。
            if (world.Heat.GetTemperature(x, y) >= DryingTemperature)
            {
                // 上方是干土 → 水上一格，本格变干。
                if (IsDrySoil(world, x, y + 1))
                {
                    GiveWater(world, x, y, x, y + 1);
                    return;
                }

                // 上方是空气且空气还没吸饱 → 像水粒子的慢蒸发一样摊一份水汽进空气（不可见），本格变干。
                // IsInBounds 不可省：越界会被 IsGasOrEmpty 当成"空气"，而 SpreadUnitIntoAir 不收越界格，
                // 那样会先把本格变干、却没有任何一格接到水汽（水凭空消失）。
                if (world.IsInBounds(x, y + 1)
                    && ParticleRules.IsGasOrEmpty(world, x, y + 1)
                    && !world.Humidity.IsAirSaturated(
                        world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap))
                {
                    world.SetPhase(x, y, ParticleIds.Soil);
                    world.Humidity.SpreadUnitIntoAir(world, x, y, HumidityField.InvisibleSpreadRadius);
                    return;
                }
            }

            // 2) 常温下渗：每逻辑帧 50% 的概率把水交给正下方的干土。
            if (world.NextUInt() % PercolationDenominator == 0u && IsDrySoil(world, x, y - 1))
            {
                GiveWater(world, x, y, x, y - 1);
            }
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 湿土暂无随机刻行为：下渗已由 OnLogic 每逻辑帧按概率处理。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set/SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 湿土被覆盖时无残留效果。
        }

        /// <summary>把本格的水交给 (toX,toY)：目标干土变成湿土、本格变回干土——1:1 搬运，水总量不变。</summary>
        private static void GiveWater(ParticleWorld world, int fromX, int fromY, int toX, int toY)
        {
            world.SetPhase(toX, toY, ParticleIds.WetSoil);
            world.SetPhase(fromX, fromY, ParticleIds.Soil);
        }

        /// <summary>
        /// (x,y) 是不是**干土**。必须同时判「不是空气」：隔离类型表里 <see cref="ParticleIds.Soil"/> 是 0
        /// （= 空气的 typeId），只比 id 会把空气当成干土，凭空造出湿土。
        /// </summary>
        private static bool IsDrySoil(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            return id != ParticleRegistry.AirTypeId && id == ParticleIds.Soil;
        }
    }
}
