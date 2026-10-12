using UnityEngine;

namespace TapTap2026.Simulation
{
    // FlowerParticle —— 花（草株顶端的显色格：固体 + Organic，长在原地不动；由草种子发芽时按 30% 开出）
    //
    // flags 私有位：**不占用**。花色存在 Cell.shade（= 调色板下标），由 ParticleWorld.Set / SetPhase 随机抽样——
    //       本类型**不**在 OnSpawn 覆写它（草把抽样钉成 0 以表达「新生 = 最年轻的绿」，花则要保留随机花色）。
    // 邻居查询范围：正上 1 格（被压判定）、正下 1 格（扎根判定）——都只在随机刻读。
    // 交互的 ParticleIds：Grass / Flower —— 「同一株」的判据：正上方是它们不算压、正下方是它们算扎根，
    //                     死亡级联沿「同一株」逐格清（花就是这株草最上面那一格）；
    //                     Soil / WetSoil / HumusSoil —— 扎根基质；
    //                     Humus —— 自己死亡后由世界的 20% 有机衰变规则就地留下（不是本类型的代码）。
    //
    // 与草的关系：花**继承草的全部性质**，差别只有颜色（更鲜艳、每朵随机、终生不变）与两项刻意去掉的行为：
    //   * **不衰老**：草的年龄存在 Cell.shade + flags bit3-5（shade 同时就是颜色档）；花的 shade 就是花色，
    //     因此没有年龄存储、也没有 LifespanTicks——花的 OnLogic 是严格空体（Info.HasLogic = false，
    //     逻辑帧扫描整段跳过），生死只由随机刻的三条死法决定。
    //   * **不繁殖 / 不改造**：撒种与固沙是草株的行为（GrassParticle.OnRandomTick ④⑤），花只是顶端显色格。
    // 其余一切照抄草：
    //   来源（随机刻，由草种子驱动）：草种子发芽见 GrassSeedParticle.OnRandomTick——发芽后以
    //       GrassSeedParticle.FlowerChanceNumerator / FlowerChanceDenominator（3/10 = 30%）把**株顶那格**草 SetPhase 成花。
    //   运动帧（OnMotion）**空**：花长在原地，不落、不流。它不是 IsStatic——静态类型收不到随机刻
    //       （ParticleWorld.RunRandomTicks 会跳过它），而花的死法全在随机刻里，因此必须是普通类型。
    //   随机刻（OnRandomTick）：与草同款三条死法、同判据同顺序（死亡一旦触发就清掉整段同株并 return）：
    //       ① **被压死**：正上方是**固体或液体、且不是同一株**（草 / 花）→ 沿 −y 清掉本格起的整段同株；
    //       ② **失去扎根**：正下方不是土 / 湿土 / 腐殖土、也不是同一株 → 沿 +y 清掉整段同株；
    //       ③ **温度致死**：本格温度 > LethalHeatTemperature（50）或 < LethalColdTemperature（3）→ 只死这一格；
    //          但脚下是腐殖土家族（HumusSoil / WetHumusSoil）时窗口放宽到 55 / 0（腐殖土保温保水，宿主更耐温）。
    //          腐殖土窗口把这一格**救活**（普通窗口下必死、腐殖土窗口下存活）时，消耗脚下连续土层里**最深**
    //          的一格腐殖土（退化为普通土）——腐殖土的优待是消耗型资源。
    //       三种死法都走 Clear，因而各自按 20% 概率留下腐殖质（花带 Organic 标签）。
    //   热学与光学：热容 / 导热率 / 辐射率 / 光照吸收率全部与草同值——鲜艳的花色不是「更吸热」，
    //       正午稳态温度仍低于 50° 致死线（与草共享同一条热学红线，见 LightAbsorptionFactor）。
    //   随机刻默认关闭（world.RandomTicksPerStep = 0，见 ParticleWorld），生产值由
    //   ParticleSimulationSettings._randomTicksPerStep 注入。
    public sealed class FlowerParticle : IParticleType
    {
        /// <summary>花色数 = 调色板长度。<see cref="Cell.shade"/> 由世界生成时按它均匀随机抽样，因此每朵花的花色随机。</summary>
        public const int ColorCount = 12;

        /// <summary>致死高温，与草同线（温度严格**高于**它即死）。温度是导出量 <c>T = Q / 热容</c>。</summary>
        public const float LethalHeatTemperature = 50f;

        /// <summary>致死低温，与草同线（温度严格**低于**它即死）。</summary>
        public const float LethalColdTemperature = 3f;

        /// <summary>脚下是腐殖土家族（腐殖土 / 湿腐殖土）时的致死高温：腐殖土保温保水，宿主植物的耐受窗口更宽。</summary>
        public const float HumusLethalHeatTemperature = 55f;

        /// <summary>脚下是腐殖土家族时的致死低温：比普通土的 3° 更低（更耐冷）。</summary>
        public const float HumusLethalColdTemperature = 0f;

        /// <summary>
        /// 光照吸收率：与草同取 0.8（直接引用草上的常量，避免两处漂移）——花受阳光升温的速度与草一致，
        /// 稳态温度仍低于 50° 致死线，因此鲜艳的花色不会让它在正午被晒死。
        /// </summary>
        public const float LightAbsorptionFactor = GrassParticle.LightAbsorptionFactor;

        /// <summary>
        /// 花色板：一组互不相似的**鲜艳**花色（与草那条「深绿 → 黄绿」的年龄渐变不同）。
        /// 生成时世界从其中**均匀随机**抽一个下标存进 <see cref="Cell.shade"/>，此后终生不变——
        /// 渲染只读 shade 查表，所以「每朵花色随机、鲜艳多样」不需要改渲染管线。
        /// </summary>
        private static readonly Color32[] Palette =
        {
            new Color32(222, 48, 60, 255),    // 绯红
            new Color32(238, 96, 140, 255),   // 粉红
            new Color32(250, 170, 200, 255),  // 浅粉
            new Color32(245, 128, 60, 255),   // 橙
            new Color32(255, 200, 120, 255),  // 杏黄
            new Color32(246, 214, 62, 255),   // 明黄
            new Color32(150, 95, 225, 255),   // 紫
            new Color32(215, 90, 205, 255),   // 品红
            new Color32(225, 80, 120, 255),   // 玫红
            new Color32(95, 120, 235, 255),   // 蓝
            new Color32(110, 190, 235, 255),  // 天蓝
            new Color32(240, 240, 250, 255),  // 白
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid | ParticleTags.Organic, // 与草同标签：挡光、死亡按 20% 留腐殖质
            density: 0.6f,
            heatCapacity: 0.80f,           // 与草同档：花 / 草表面升温速度一致
            thermalConductivity: 0.60f,    // 与草同档
            thermalEmissivity: 1.0f,       // 近黑体（与草同档）
            affectedByGravity: false,
            isStatic: false,     // 必须非静态，否则收不到随机刻；靠空的 OnMotion 保持不动（同草）
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(FlowerParticle),
            lightAbsorption: LightAbsorptionFactor,
            hasLogic: false); // OnLogic 严格空体：花不衰老、无相变，死法全在随机刻

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 花长在草株顶端不动：运动帧什么都不做（同草，非静态 + 空 OnMotion）。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 严格空体：花不衰老（shade 是花色而不是年龄档，没有寿命存储），也没有每逻辑帧的反应。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            RunRandomTick(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // 刻意留空：花色已由 ParticleWorld.Set / SetPhase 按调色板随机抽样存进 Cell.shade。
            // 草在这里把 shade 钉成 0（新生 = 最年轻的绿），花要的正是那个随机值，因此不覆写。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 花被覆盖 / 死亡时无残留效果（腐殖质由世界的有机衰变规则处理）。
        }

        /// <summary>
        /// 三条死法（① 被压 / ② 失去扎根 / ③ 温度），顺序与判据与草一致。死亡一旦触发就清掉整段同株并返回。
        /// ③ 的温度窗口按脚下是否腐殖土家族（腐殖土 / 湿腐殖土）在 50 / 3 与 55 / 0 之间选择；
        /// 腐殖土窗口**救活**这一格时，亦消耗脚下连续土层里最深的一格腐殖土（退化为普通土）。
        /// 花没有撒种 / 固沙两步（那是草株的行为）。
        /// </summary>
        private static void RunRandomTick(ParticleWorld world, int x, int y)
        {
            // ① 被压死：正上方是**固体或液体、且不是同一株**（草 / 花）才算压——同一株的草不算压
            //    （花就长在草株顶上，草 / 花互不算压是「花不会压死自己那株草」的关键）。
            if (IsPressed(world, x, y))
            {
                // 死亡沿**竖直方向向下**传播：从这一格起清掉下面整段同株（被压垮的一整株）。
                ClearCascade(world, x, y, -1);
                return;
            }

            // ② 失去扎根就死：正下方不是土 / 湿土 / 腐殖土（也不是同一株的下段）时失去支撑而死。
            //    死亡沿**竖直方向向上**传播：根在下面，土没了整株一起死。
            if (!IsRooted(world, x, y))
            {
                ClearCascade(world, x, y, 1);
                return;
            }

            // ③ 温度致死：过冷 / 过热只死这一格（相邻格各有自己的温度，级联随时间涌现）。
            //    脚下是腐殖土家族时用更宽的窗口（55 / 0，腐殖土保温保水），否则用普通土的 50 / 3。
            //    腐殖土窗口**救活**这一格（普通窗口下必死、腐殖土窗口下存活）时，消耗脚下连续土层里最深的
            //    一格腐殖土（退化为普通土）——优待是消耗型资源，用完即退化。
            ushort belowId = world.GetCell(x, y - 1).typeId;
            bool onHumusSoil = belowId == ParticleIds.HumusSoil || belowId == ParticleIds.WetHumusSoil;
            float lethalHeat = onHumusSoil ? HumusLethalHeatTemperature : LethalHeatTemperature;
            float lethalCold = onHumusSoil ? HumusLethalColdTemperature : LethalColdTemperature;
            float temperature = world.Heat.GetTemperature(x, y);
            if (temperature > lethalHeat || temperature < lethalCold)
            {
                world.Clear(x, y);
            }
            else if (onHumusSoil
                && (temperature > LethalHeatTemperature || temperature < LethalColdTemperature))
            {
                HumusSoilParticle.ConsumeFarthestBelow(world, x, y);
            }
        }

        /// <summary>
        /// 从 (x,y) 起沿 <paramref name="step"/>（−1 = 向下、+1 = 向上）清掉整段连续的**同一株**（草 / 花），
        /// 遇非同一株即停。每一格都走 <see cref="ParticleWorld.Clear"/>，因而各自按有机衰变规则留下腐殖质。
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
        /// (x,y) 是否扎根：正下方是土 / 湿土 / 腐殖土，或是**同一株的下段**（草 / 花——只有整株最底下那格
        /// 需要土壤，上面的草段靠下段支撑）。
        /// </summary>
        private static bool IsRooted(ParticleWorld world, int x, int y)
        {
            ushort belowId = world.GetCell(x, y - 1).typeId;
            return belowId == ParticleIds.Soil
                || belowId == ParticleIds.WetSoil
                || belowId == ParticleIds.HumusSoil
                || belowId == ParticleIds.WetHumusSoil
                || IsStalkMember(belowId);
        }

        /// <summary>
        /// (x,y) 是否被压住：正上方是**固体或液体**且**不是同一株**（草 / 花）。
        /// 于是沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、草种子（固体）与水（液体）都算压；
        /// 同株的草 / 花不算压，空气与气体（大气）也不算。
        /// </summary>
        private static bool IsPressed(ParticleWorld world, int x, int y)
        {
            ushort aboveId = world.GetCell(x, y + 1).typeId;
            if (aboveId == ParticleRegistry.AirTypeId || IsStalkMember(aboveId))
            {
                return false;
            }

            ParticleTags above = ParticleRules.TagsAt(world, x, y + 1);
            return (above & (ParticleTags.Solid | ParticleTags.Liquid)) != 0;
        }

        /// <summary>(x,y) 上的粒子是否属于**同一株草**——草或它顶端的花。同一株互不算压、互为扎根、一起级联死亡。</summary>
        private static bool IsStalkMember(ushort typeId)
        {
            return typeId == ParticleIds.Grass || typeId == ParticleIds.Flower;
        }
    }
}
