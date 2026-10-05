using UnityEngine;

namespace TapTap2026.Simulation
{
    // WetSoilParticle —— 湿土（固体标签；摩擦度/粘性沿用土的 0.5/0.5，堆形与干土一致）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正上方一格（受热上行的目标 / 蒸发口）、正下方一格（常温下渗的首选目标）、
    //               同行的左右各一格（正下方被挡住时的侧向渗流），
    //               以及 StepSolid 的「正下、左下/右下、斜滑反侧同一行」
    // 交互的 ParticleIds：Soil —— 水交给干土时把那格换成 WetSoil、本格变回 Soil（双向，走 ParticleIds）
    //
    // 湿土 = 干土 + 一份水。它每逻辑帧（20 Hz）先试着把这份水交给邻格（OnLogic），
    // 交不出去就留在原地；运动（下落 / 斜落）在运动帧由 OnMotion 负责，两者互不干扰。
    //
    // 三条路，成功即本格变干（水要么进了邻格的干土，要么进了空气，湿土不会凭空变干）：
    //   · 受热上行（T ≥ DryingTemperature）：水优先往上走，同样有 1/PercolationDenominator（= 50%）
    //     的概率门（与下渗对称，消除热源消失时相邻两格的往返）——上方是干土就把那格变成湿土；
    //     上方是空气且空气没吸饱，就像水粒子慢蒸发一样把一份水汽摊进空气（不可见，走湿度场），
    //     但干燥要从土壤吸走热容差潜热（DryingLatentHeatPerDegree × T = 1.2T，随温度线性；
    //     与水↔大气的 HumidityField.LatentHeat（5.5T）不同源）。
    //   · 常温迁移（T < DryingTemperature）：**有压才渗**——只有正上方是水或湿土（被上方的水柱 /
    //     湿层压着，即有水头压力）时，才以每逻辑帧 1/PercolationDenominator（= 50%）的概率把水
    //     交给**正下方**的干土（首选）；正下方被石头/沙/水/湿土挡住时，按随机顺序改试同行左右干土。
    //     正上方没有水/湿土的孤立湿土**永久持水**（田间持水量：毛管力拽住水，重力渗不动它）——
    //     湿润不会自己沉底，雨停即持；它退出循环的方式是被太阳晒干（上面的受热上行路径）。
    //     连续降雨时链条效果是湿润锋面整体下移一格、且身后的格子保持湿润，而不是湿斑穿柱而过。
    //   · 两条都走不通：水留在土里，本格保持湿土（例如被石头/沙/水围住的湿土）。
    //
    // 只有**干土**（ParticleIds.Soil）会接水——石头、沙、水都不行：它们是别的物质，没有"湿"的形态。
    // 换格走 SetPhase，但**水带着它的热量一起走**：搬运时把 ΔQ = T(源) × 1.2 从源格挪到目标格
    // （1.2 = 湿土热容 2.0 − 干土热容 0.8，即那一份水的热容份额），总热量严格守恒、两侧温度不跳。
    public sealed class WetSoilParticle : IParticleType
    {
        /// <summary>
        /// 干燥温度阈值：湿土温度达到它，水就改往**上**走（受热上行）——上方是干土则润湿之，
        /// 上方是空气则蒸发进空气。低于它时水按重力方向下渗（正下方优先，被挡住则侧向）。
        /// 取 60（水：慢蒸发 20 / 沸腾 80）：白天晒热的湿土表层会往上走并在表面蒸发，
        /// 埋在地下、温度接近环境值的湿土则一路往下渗——这正是"表层被晒干、水往深处走"的观感。
        /// </summary>
        public const float DryingTemperature = 60f;

        /// <summary>下渗概率分母：每逻辑帧有 <c>1/N</c> 的概率把水交给正下方的干土。N = 2 → 50%（需求值）。</summary>
        public const uint PercolationDenominator = 2u;

        /// <summary>水在湿土里携带的热容份额：湿土热容 2.0 − 干土热容 0.8 = 1.2。
        /// 水迁移时按 <c>ΔQ = T(源) × 1.2</c> 搬热（热随物质走），使两侧温度不因换格而跳。</summary>
        private const float WaterShareHeatCapacity = 1.2f;

        /// <summary>湿土 → 干土的干燥潜热系数：湿土热容 2.0 − 干土热容 0.8 = 1.2，随温度线性。
        /// 与水 ↔ 大气的 <see cref="HumidityField.LatentHeat"/>（5.5T）**不同源**：5.5 是水的
        /// 汽化口径（C_水 6.5 − C_空气 1），干燥的相变双方是湿土与干土，温度连续只补热容差这一份。</summary>
        private const float DryingLatentHeatPerDegree = 1.2f;

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
            // 1) 受热上行：水优先往**上**走——只有热土才走这条路（热土里的水不下渗，见下方）。50% 概率门，
            //    与下渗同分母，用来打破「热源消失后水在相邻两格来回走」的时间对称。
            if (world.Heat.GetTemperature(x, y) >= DryingTemperature)
            {
                // 上方是干土 → 水上一格，本格变干（先判目标再摇骰：没有干土就不消耗 RNG）。
                if (IsDrySoil(world, x, y + 1)
                    && world.NextUInt() % PercolationDenominator == 0u)
                {
                    GiveWater(world, x, y, x, y + 1);
                }
                // 上方是空气且空气还没吸饱 → 像水粒子的慢蒸发一样摊一份水汽进空气（不可见），本格变干。
                // IsInBounds 不可省：越界会被 IsGasOrEmpty 当成"空气"，而 SpreadUnitIntoAir 不收越界格，
                // 那样会先把本格变干、却没有任何一格接到水汽（水凭空消失）。
                // C3：先确认播撒成功（接收格太少 / 份额过大时返回 0）再变干，否则水汽凭空消失。
                else if (world.IsInBounds(x, y + 1)
                    && ParticleRules.IsGasOrEmpty(world, x, y + 1)
                    && !world.Humidity.IsAirSaturated(
                        world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap)
                    && world.Humidity.SpreadUnitIntoAir(
                        world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadMaxShare) > 0)
                {
                    // C1 潜热：干燥要从土壤吸热——SetPhase 保留 Q，故相变完成后再对本格补一笔负潜热。
                    // 系数取**湿土→干土的热容差** 1.2（湿土 2.0 − 干土 0.8，随温度线性），**不是**
                    // 水的 HumidityField.LatentHeat（5.5T，水↔大气的口径）：错取 5.5T 会过冷、土温为负。
                    float temperature = world.Heat.GetTemperature(x, y);
                    world.SetPhase(x, y, ParticleIds.Soil);
                    world.Heat.AddHeat(x, y, -DryingLatentHeatPerDegree * temperature);
                }

                // 热土只走上行 / 蒸发：温度低于干燥阈值才允许下渗（B7），否则热源一消失就会立刻往回渗。
                return;
            }

            // 2) 常温迁移：**有压才渗**——正上方是水（Liquid 标签）或湿土才有水头压力；
            //    否则本格永久持水（田间持水量），湿斑不会自己沉底。越界上方读到空气 → 无压，天然安全。
            //    判压先于判目标与摇骰：无压时不消耗 RNG，随机流不随湿土格数漂移（B11 同一原则）。
            bool pressed = (ParticleRules.TagsAt(world, x, y + 1) & ParticleTags.Liquid) != 0
                || world.GetCell(x, y + 1).typeId == ParticleIds.WetSoil;
            if (!pressed)
            {
                return;
            }

            // **先判目标、再摇骰**（B11）——没有可接水的干土就不消耗 RNG，
            //    随机流因此不再随场上湿土格数漂移。
            bool downDry = IsDrySoil(world, x, y - 1);
            bool leftDry = IsDrySoil(world, x - 1, y);
            bool rightDry = IsDrySoil(world, x + 1, y);
            if (!downDry && !leftDry && !rightDry)
            {
                return;
            }

            if (world.NextUInt() % PercolationDenominator != 0u)
            {
                return;
            }

            // 正下方是干土 → 首选（水沿重力方向一格格渗下去）。
            if (downDry)
            {
                GiveWater(world, x, y, x, y - 1);
                return;
            }

            // 正下方被石头/沙/水/湿土挡住：按随机顺序试同行左右干土邻居（M10），
            // 水被挡住时也能侧向找到路，而不是原地卡死。
            if (leftDry && rightDry)
            {
                if ((world.NextUInt() & 1u) == 0u)
                {
                    GiveWater(world, x, y, x - 1, y);
                }
                else
                {
                    GiveWater(world, x, y, x + 1, y);
                }
            }
            else if (leftDry)
            {
                GiveWater(world, x, y, x - 1, y);
            }
            else
            {
                GiveWater(world, x, y, x + 1, y);
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
        /// <remarks>水**带着热一起走**：把 <c>ΔQ = T(源) × <see cref="WaterShareHeatCapacity"/></c> 从源格挪到目标格，
        /// 于是热容随物质一起变（湿土 2.0 ↔ 干土 0.8），两侧温度不再因换格而跳。总热量严格守恒。</remarks>
        private static void GiveWater(ParticleWorld world, int fromX, int fromY, int toX, int toY)
        {
            float carriedHeat = world.Heat.GetTemperature(fromX, fromY) * WaterShareHeatCapacity;
            world.Heat.AddHeat(toX, toY, carriedHeat);
            world.Heat.AddHeat(fromX, fromY, -carriedHeat);
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
