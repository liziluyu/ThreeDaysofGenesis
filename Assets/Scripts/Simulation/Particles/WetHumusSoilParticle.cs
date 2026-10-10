using UnityEngine;

namespace TapTap2026.Simulation
{
    // WetHumusSoilParticle —— 湿润腐殖土（固体标签；腐殖土 + 一份水的形态）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正上方一格（受热上行的目标 / 蒸发口）、正下方一格（常温下渗的首选目标）、
    //               同行的左右各一格（正下方被挡住时的侧向渗流），
    //               以及 StepSolid 的「正下、左下/右下、斜滑反侧同一行」
    // 交互的 ParticleIds：HumusSoil —— 水交给干腐殖土时把那格换成 WetHumusSoil、本格变回 HumusSoil（双向）
    //
    // 行为**与 WetSoilParticle 逐条同构**（作者契约：「逻辑和普通的土壤没有区别」）——只是把干 / 湿两端
    // 从 Soil / WetSoil 换成 HumusSoil / WetHumusSoil，并按腐殖土自身的热容差把热量记账配平：
    //   · 受热上行（T ≥ DryingTemperature = 60）：上方是干腐殖土就润湿之；上方是空气且没饱和就像水粒子慢蒸发
    //     一样摊一份水汽进空气（不可见，走湿度场），从土壤吸走一份相变潜热（DryingLatentHeatPerDegree × T = 1.2T）。
    //   · 常温有压才渗（T < 60）：上方是液体或 WetHumusSoil（水头压力）才以每逻辑帧 50% 的概率把水交给
    //     正下方的干腐殖土（首选），被挡则按随机序改试同行左右干腐殖土（侧向渗流）。孤立湿润腐殖土永久持水。
    //   · 两条都走不通：保持湿润（例如被石头 / 沙 / 水围住的湿地）。
    // 水迁移时带着 T(源) × 1.2 的热量一起走（1.2 = WetHumusSoil 热容 2.2 − HumusSoil 热容 1.0），
    // 两侧温度不跳、总热严格守恒；水总量同样 1:1 搬运，不丢不增。
    //
    // 生成路径：腐殖土被随机刻命中、且上方是腐殖质时，把邻近的一格**湿润土壤**改成它（见
    // HumusSoilParticle.OnRandomTick）。不可在编辑器中画出。
    public sealed class WetHumusSoilParticle : IParticleType
    {
        /// <summary>
        /// 干燥温度阈值：湿润腐殖土温度达到它，水就改往**上**走（受热上行）。与
        /// <see cref="WetSoilParticle.DryingTemperature"/> 同值（60）——两种湿土同一种干燥行为。
        /// </summary>
        public const float DryingTemperature = WetSoilParticle.DryingTemperature;

        /// <summary>下渗概率分母：每逻辑帧有 <c>1/N</c> 的概率把水交给正下方的干腐殖土。N = 2 → 50%。</summary>
        public const uint PercolationDenominator = 2u;

        /// <summary>水在湿润腐殖土里携带的热容份额：WetHumusSoil 热容 2.2 − HumusSoil 热容 1.0 = 1.2。
        /// 水迁移时按 <c>ΔQ = T(源) × 1.2</c> 搬热（热随物质走），使两侧温度不因换格而跳。</summary>
        private const float WaterShareHeatCapacity = 1.2f;

        /// <summary>湿润腐殖土 → 干腐殖土的干燥潜热系数：热容差 2.2 − 1.0 = 1.2，随温度线性。
        /// 与 <see cref="HumidityField.LatentHeat"/>（5.5T，水↔大气的口径）**不同源**：干燥的相变双方是
        /// 湿润腐殖土与干腐殖土，温度连续、只补热容差这一份。</summary>
        private const float DryingLatentHeatPerDegree = 1.2f;

        private static readonly Color32[] Palette =
        {
            new Color32(20, 17, 14, 255),
            new Color32(27, 23, 19, 255),
            new Color32(15, 13, 11, 255),
            new Color32(34, 29, 24, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 2.1f,   // 比干腐殖土（1.9）略重
            heatCapacity: 2.2f,   // 腐殖土（1.0）+ 一份水（1.2）——比干腐殖土扛热得多
            thermalConductivity: 0.75f,   // 水（0.95）与干腐殖土（0.60）之间
            thermalEmissivity: 1.0f,      // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(WetHumusSoilParticle),
            // 运动阅读盒：与干腐殖土同款 0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 湿润腐殖土的实体运动与干腐殖土完全相同（同样的 friction / stickiness ⇒ 同样的安息角）。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 1) 受热上行：与 WetSoilParticle 逐条同构，只是端点换成腐殖土家族。
            if (world.Heat.GetTemperature(x, y) >= DryingTemperature)
            {
                // 上方是干腐殖土 → 水上一格，本格变干（先判目标再摇骰：没有干腐殖土就不消耗 RNG）。
                if (IsDryHumusSoil(world, x, y + 1)
                    && world.NextUInt() % PercolationDenominator == 0u)
                {
                    GiveWater(world, x, y, x, y + 1);
                }
                // 上方是空气且空气还没吸饱 → 像水粒子的慢蒸发一样摊一份水汽进空气（不可见），本格变干。
                // IsInBounds 不可省：越界会被 IsGasOrEmpty 当成"空气"，而 SpreadUnitIntoAir 不收越界格。
                // 先确认播撒成功（接收格太少 / 份额过大时返回 0）再变干，否则水汽凭空消失。
                else if (world.IsInBounds(x, y + 1)
                    && ParticleRules.IsGasOrEmpty(world, x, y + 1)
                    && !world.Humidity.IsAirSaturated(
                        world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap)
                    && world.Humidity.SpreadUnitIntoAir(
                        world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadMaxShare) > 0)
                {
                    float temperature = world.Heat.GetTemperature(x, y);
                    world.SetPhase(x, y, ParticleIds.HumusSoil);
                    world.Heat.AddHeat(x, y, -DryingLatentHeatPerDegree * temperature);
                }

                // 热土只走上行 / 蒸发：温度低于干燥阈值才允许下渗，否则热源一消失就会立刻往回渗。
                return;
            }

            // 2) 常温迁移：**有压才渗**——正上方是水（Liquid 标签）或湿腐殖土才有水头压力；
            //    否则本格永久持水（田间持水量），湿斑不会自己沉底。越界上方读到空气 → 无压，天然安全。
            //    判压先于判目标与摇骰：无压时不消耗 RNG，随机流不随湿地格数漂移。
            bool pressed = (ParticleRules.TagsAt(world, x, y + 1) & ParticleTags.Liquid) != 0
                || world.GetCell(x, y + 1).typeId == ParticleIds.WetHumusSoil;
            if (!pressed)
            {
                return;
            }

            // **先判目标、再摇骰**——没有可接水的干腐殖土就不消耗 RNG。
            bool downDry = IsDryHumusSoil(world, x, y - 1);
            bool leftDry = IsDryHumusSoil(world, x - 1, y);
            bool rightDry = IsDryHumusSoil(world, x + 1, y);
            if (!downDry && !leftDry && !rightDry)
            {
                return;
            }

            if (world.NextUInt() % PercolationDenominator != 0u)
            {
                return;
            }

            // 正下方是干腐殖土 → 首选（水沿重力方向一格格渗下去）。
            if (downDry)
            {
                GiveWater(world, x, y, x, y - 1);
                return;
            }

            // 正下方被石头 / 沙 / 水 / 湿腐殖土挡住：按随机顺序试同行左右干腐殖土邻居。
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
            // 湿润腐殖土暂无随机刻行为：下渗已由 OnLogic 每逻辑帧按概率处理。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set/SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 湿润腐殖土被覆盖时无残留效果。
        }

        /// <summary>把本格的水交给 (toX,toY)：目标干腐殖土变成湿润腐殖土、本格变回干腐殖土——1:1 搬运，水总量不变。</summary>
        /// <remarks>水**带着热一起走**：把 <c>ΔQ = T(源) × <see cref="WaterShareHeatCapacity"/></c> 从源格挪到目标格，
        /// 于是热容随物质一起变（湿润腐殖土 2.2 ↔ 干腐殖土 1.0），两侧温度不再因换格而跳。总热量严格守恒。</remarks>
        private static void GiveWater(ParticleWorld world, int fromX, int fromY, int toX, int toY)
        {
            float carriedHeat = world.Heat.GetTemperature(fromX, fromY) * WaterShareHeatCapacity;
            world.Heat.AddHeat(toX, toY, carriedHeat);
            world.Heat.AddHeat(fromX, fromY, -carriedHeat);
            world.SetPhase(toX, toY, ParticleIds.WetHumusSoil);
            world.SetPhase(fromX, fromY, ParticleIds.HumusSoil);
        }

        /// <summary>
        /// (x,y) 是不是**干腐殖土**。必须同时判「不是空气」：隔离类型表里 <see cref="ParticleIds.HumusSoil"/> 是 0
        /// （= 空气的 typeId），只比 id 会把空气当成干腐殖土，凭空造出湿润腐殖土。
        /// </summary>
        private static bool IsDryHumusSoil(ParticleWorld world, int x, int y)
        {
            ushort id = world.GetCell(x, y).typeId;
            return id != ParticleRegistry.AirTypeId && id == ParticleIds.HumusSoil;
        }
    }
}
