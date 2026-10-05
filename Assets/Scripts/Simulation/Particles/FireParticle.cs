using UnityEngine;

namespace TapTap2026.Simulation
{
    // FireParticle —— 火焰（气体；**接触点燃**邻接的草、**接触烘干**邻接的湿土，遇石头 / 沙 / 水 / 湿土
    //   快速熄灭；失去附着物会下沉并在空中快速熄灭；每逻辑帧放热、跳动着燃烧）
    //
    // flags 私有位：无（bit3-7 全部空闲；也不使用 CellFlags.Burning——该位当前没有任何消费者）
    // 邻居查询范围：**八邻**（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上），每帧读一遍做：
    //   燃料判定（第一格草）、干化目标（第一格湿土）、淬熄判定（石头 / 沙 / 水 / 湿土的邻接）、
    //   附着物判定（固 / 液的一格——**火焰不算附着物**，火柱不会互相支撑；火是气体，天然不被
    //   Solid/Liquid 判据命中）。**投放**资格的锚点判定不同源：除固 / 液外**火链也算锚点**——
    //   同笔刷 / 已有的火可以互相锚定，圆刷贴地时能连锁铺满整团火；完全悬空时一格都放不下。
    //   物理燃烧的附着物始终不含火：刷出的无支撑火仍会下沉并按 20%/帧熄灭（火团会「塌落」）。
    // 交互的 ParticleIds：Grass → Fire（接触点燃，Set 成火）；WetSoil → Soil（接触烘干——先确认水汽
    //   摊得出去（走湿度场的不可见播撒，与湿土受热蒸发的口径完全一致），水不凭空消失；摊不出去则保留）。
    //   石头 / 沙 / 水 / 湿土只作为**淬熄触发**（见下），不改动对方；液体按标签判（当前所有液体视为水）。
    //   注意：草带 Organic 标签，世界的 MaybeLeaveHumus 规则会在 Set 之后按 20% 概率把那格改判成腐殖质——
    //   **烧过的草地可能留下腐殖质残迹**，这是世界通用规则、不是火的行为。
    //
    // 为什么是 Gas：要被固体 / 液体沉穿（沙落穿火、水浇进火），且失去附着物时要能下沉——
    //   Gas 标签让火无需特判即可复用气体原语（TrySink / CanGasEnter）。密度 0.6 比环境大气（0.5）重，
    //   大气推不动它、火能沉穿大气。
    //
    // 行为（① 在逻辑钩子开头、②-⑥ 在 OnLogic 20 Hz；下沉在 OnMotion 60 Hz）：
    //   ① 跳动：本格 shade 沿调色板随机游走一步（±1，撞界弹回）——纯视觉、确定性哈希、**不消耗世界
    //      随机流**。多格混排即「跳动的火色」，像现实火焰的闪烁。
    //   ② 放热：本格温度低于 PeakTemperature 时向本格注入 FireHeatPerTick。热由世界每逻辑帧自动做
    //      四邻传导扩散（火绝不自己做传导）。紧贴的沙 / 石会被烤热（热容小、导热快，几帧就到上百度，
    //      随后火被 ⑤a 淬熄）、近处的草会被「烤」、紧邻的水会趋近沸腾点（80）。
    //   ③ 八邻扫描（只读，不摇骰）：燃料 / 干化目标 / 淬熄触发 / 附着物。
    //   ④ 干化（先于熄灭骰）：遇到湿土就先把它烤成干土（TryDryWetSoil）。
    //   ⑤ 熄灭：「被压顶」优先——**正下方是石头 / 沙 / 水**（实体顶到火头上）→ **当帧直接熄灭、不摇骰**：
    //      石头 / 沙落在火上时，世界的 rank 规则会先把火置换到实体正上方（Swap），火在下一逻辑帧
    //      立即熄灭——修复「火被顶到石头上方继续烧」（位移本身是 rank 规则，火代码无法拦截，
    //      但能做到「顶上去即灭」）。其余情况按三优先档，每帧恰摇一次骰：
    //      a. 贴着**石头 / 沙 / 水 / 湿土**（非草木的实体，侧 / 斜 / 上邻）→ 快熄骰 1/QuenchDenominator
    //         （20，期望约 1 秒）：被石头压、被沙埋、被水浇都会快速熄灭。注意**干土与腐殖质 / 腐殖土
    //         有意不参与淬熄**：草必须长在土上、腐殖类又是草地被烧后留下的残渣——若它们也淬熄，
    //         火线会被自己脚下的土 / 刚留下的残渣按灭，「烧光草地」的既有验收会破（离线推演过：
    //         分支率 0.8×点火节奏 < 1）。
    //      b. 没有附着物（飘在空中、正在下沉）→ 坠落熄灭骰 1/UnsupportedExtinguishDenominator（5 = 20% 每
    //         逻辑帧）：期望约 5 帧内熄灭，下沉几格后熄灭。
    //      c. 有附着物但燃料烧尽 → 普通熄灭骰 1/BurnoutDenominator（120，期望约 6 秒，几何分布）。
    //   ⑥ 蔓延：有燃料（必然带附着物——草是固体）时以 1/IgnitionDenominator 的概率点燃第一格草
    //      （Set 成火，新火有全新寿命）。**有燃料且不贴淬熄物时不摇熄灭骰**——保证邻接燃料必被逐格
    //      吃掉（否则火线会随机断代，烧过的格变成空气、火不能穿过空气回头补烧，草地上就留下烧不掉的
    //      零星残株——离线模拟复现过）。
    //   每帧的随机消耗：贴淬熄物 1 个熄灭骰（若还带草再加 1 个点燃骰）；无附着物 1 个；只烧草 1 个点燃骰；
    //   托着烧但无火燃料 1 个熄灭骰。
    //
    // 下沉（OnMotion，60 Hz）：有附着物就定在原地；失去附着物则 TrySink（气体版下落——下方是空气 / 气体
    //   才换位，被固 / 液挡住自然停下，落地即恢复附着）。「火焰不算附着物」意味着火柱不互相支撑，会整体下沉。
    //
    // 投放（ParticleEditor 手刷走 CanPlaceAt）：**不覆盖 / 不替换任何实体方块或粒子**（固、液都不行；
    //   空气与大气等气体格放行——大气是可替换的环境气）；**不许浮空**——目标格的八邻内必须有锚点
    //   （固 / 液的一格，**或另一格火**：圆刷贴地时靠火链连锁铺满整团火；完全悬空则一格都放不下）。
    //   注意这是**投放**规则，不是生成规则：点燃草与火的自然蔓延不受它约束；且物理燃烧的附着物
    //   不含火——刷出的无支撑火仍会下沉 / 快熄。
    //
    // 与世界的边界：世界的通用规则仍会作用于火——固 / 液按 rank 规则置换火（水横流换进火格、
    //   沙沉穿火），火被覆盖即经 OnKill 消失：**水浇灭火是涌现的，火自己不写灭火**；降雨凝结
    //   进火格同理。
    //
    // 渲染：不透明火焰色渐变调色板（shade 由 ParticleWorld.Set 抽样后被 ① 逐帧扰动，见上）。
    // 投放白名单：ParticleEditor 的 Gas 白名单（_placeableGasTypes，默认 FireParticle）允许手刷火；
    //   其余 Gas（大气 / 水汽）仍不可手刷。
    public sealed class FireParticle : IParticleType
    {
        /// <summary>每逻辑帧向本格注入的热量 Q（火的热容 1.0 ⇒ 未扩散前每帧 +250 度等效）。</summary>
        public const float FireHeatPerTick = 250f;

        /// <summary>火格的软温度上限：本格温度低于它才注入热量。单格火的设计平衡温度低于它，
        /// 因此单格行为不受影响；整片火靠它封顶在 ~400 度，而不是被恒定注入推到数千度。</summary>
        public const float PeakTemperature = 400f;

        /// <summary>蔓延概率的分母：八邻有草时每逻辑帧 <c>NextUInt() % 本值 == 0</c> 就点燃第一格草，
        /// 即 1/20 每帧，期望约 1 秒烧掉一格。</summary>
        public const uint IgnitionDenominator = 20u;

        /// <summary>熄灭概率的分母：八邻无草（燃料烧尽）时每逻辑帧 <c>NextUInt() % 本值 == 0</c>
        /// 就自然熄灭，即 1/120 每帧，期望寿命 120 逻辑帧 ≈ 6 秒（几何分布，有少量长尾）。</summary>
        public const uint BurnoutDenominator = 120u;

        /// <summary>淬熄概率的分母：八邻有**石头 / 沙 / 水（液体）/ 湿土**时走这个快骰
        /// （<c>NextUInt() % 本值 == 0</c> 即熄灭），即 1/20 每帧、期望约 1 秒——被石头压、被沙埋、
        /// 被水浇都死得快。干土与腐殖类有意不在淬熄集合里（见文件头 ⑤a 的说明）。</summary>
        public const uint QuenchDenominator = 20u;

        /// <summary>坠落熄灭概率的分母：失去附着物（飘在空中 / 正在下沉）时每逻辑帧
        /// <c>NextUInt() % 本值 == 0</c> 就立即熄灭，即 1/5 = 20% 每帧（期望约 5 帧 ≈ 0.25 秒）。</summary>
        public const uint UnsupportedExtinguishDenominator = 5u;

        // 八邻偏移（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上）。
        // 静态共享数组：所有八邻扫描（燃料 / 湿土 / 淬熄 / 附着物）同序同源，热路径零分配。
        private static readonly int[] NeighbourX = { -1, 1, 0, 0, -1, 1, -1, 1 };
        private static readonly int[] NeighbourY = { 0, 0, -1, 1, -1, -1, 1, 1 };

        // 火焰色渐变（亮黄 → 暗红，全部不透明）。shade 初始由 ParticleWorld.Set 抽样，
        // 之后由 ① 的随机游走逐帧扰动——多档混排且逐帧变化即「跳动的火色」。
        private static readonly Color32[] Palette =
        {
            new Color32(255, 238, 140, 255),
            new Color32(255, 205, 70, 255),
            new Color32(255, 160, 38, 255),
            new Color32(244, 110, 26, 255),
            new Color32(210, 64, 22, 255),
            new Color32(163, 38, 18, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Gas,
            density: 0.6f,               // 比大气（0.5）重：大气推不动它，火能沉穿大气
            heatCapacity: 1.0f,
            thermalConductivity: 1.0f,   // 气体传导最强一档（与大气一致）
            thermalEmissivity: 1.0f,     // 火焰近黑体：暴露面辐射散热强
            affectedByGravity: false,    // 下沉由 OnMotion 手动发起（只在失去附着物时）
            isStatic: false,             // 非静态：才能被扫描（静态类型的钩子永不触发）
            maxFlowSpread: 0,
            friction: 0f,                // 非固体，不参与安息角规则
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(FireParticle));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 有附着物（八邻有固 / 液；**火不算附着物**）就定在原地燃烧；
            // 失去附着物则尝试下沉——气体版下落：下方是空气 / 气体才换位，被固 / 液挡住自然停下
            // （落地即恢复附着，随后按 OnLogic 的熄灭骰结算）。
            if (HasAttachment(world, x, y))
            {
                return;
            }

            ParticleRules.TrySink(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // ① 跳动：本格 shade 沿调色板随机游走一步（±1，撞界弹回，避免停在边缘不动）——纯视觉、
            //    确定性哈希、不消耗世界随机流（At() 是就地读写 flags / shade 的官方入口）。
            ref Cell cell = ref world.At(x, y);
            uint noise = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)world.Tick * 83492791u;
            int stepped = cell.shade + (int)(noise % 3u) - 1;
            if (stepped < 0)
            {
                stepped = 1;
            }
            else if (stepped >= Palette.Length)
            {
                stepped = Palette.Length - 2;
            }

            cell.shade = (byte)stepped;

            // ② 放热：低于上限才注入。热由世界每逻辑帧做四邻传导扩散，火绝不自己做传导。
            if (world.Heat.GetTemperature(x, y) < PeakTemperature)
            {
                world.Heat.AddHeat(x, y, FireHeatPerTick);
            }

            // ③ 八邻扫描（只读，不摇骰）：燃料 / 干化目标 / 淬熄触发 / 附着物。
            bool hasFuel = false;
            int fuelX = 0;
            int fuelY = 0;
            bool hasWetSoil = false;
            int wetX = 0;
            int wetY = 0;
            bool quench = false;
            bool attached = false;
            for (int i = 0; i < NeighbourX.Length; i++)
            {
                int nx = x + NeighbourX[i];
                int ny = y + NeighbourY[i];
                ushort id = world.GetCell(nx, ny).typeId;
                ParticleTags tags = ParticleRules.TagsAt(world, nx, ny);

                if (!hasFuel && id == ParticleIds.Grass)
                {
                    hasFuel = true;
                    fuelX = nx;
                    fuelY = ny;
                }
                else if (!hasWetSoil && id == ParticleIds.WetSoil)
                {
                    hasWetSoil = true;
                    wetX = nx;
                    wetY = ny;
                }

                if (!quench
                    && (id == ParticleIds.Stone || id == ParticleIds.Sand || id == ParticleIds.WetSoil
                        || (tags & ParticleTags.Liquid) != 0))
                {
                    quench = true;
                }

                if (!attached && (tags & (ParticleTags.Solid | ParticleTags.Liquid)) != 0)
                {
                    attached = true;
                }
            }

            // ④ 干化（先于熄灭骰）：遇到湿土就先把它烤成干土——干化的代价是 ⑤a 的快速熄灭骰。
            if (hasWetSoil)
            {
                TryDryWetSoil(world, wetX, wetY);
            }

            // ⑤ 熄灭：「被压顶」优先——正下方是石头 / 沙 / 水（实体顶到火头上）→ 当帧直接熄灭、不摇骰
            //    （石头 / 沙落在火上时，rank 规则先把火置换到实体正上方，火在下一逻辑帧即灭）。
            //    其余情况按三优先档，每帧恰摇一次骰：
            //    a. 贴着石头 / 沙 / 水 / 湿土 → 快熄骰；
            //    b. 没有附着物（飘在空中）→ 坠落熄灭骰（20% 每逻辑帧）；
            //    c. 有附着物但燃料烧尽 → 普通熄灭骰。
            ushort belowId = world.GetCell(x, y - 1).typeId;
            bool crushed = belowId == ParticleIds.Stone || belowId == ParticleIds.Sand
                || (ParticleRules.TagsAt(world, x, y - 1) & ParticleTags.Liquid) != 0;
            if (crushed)
            {
                world.Clear(x, y);
                return;
            }

            if (quench)
            {
                if (world.NextUInt() % QuenchDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else if (!attached)
            {
                if (world.NextUInt() % UnsupportedExtinguishDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else if (!hasFuel && world.NextUInt() % BurnoutDenominator == 0u)
            {
                world.Clear(x, y);
                return;
            }

            // ⑥ 蔓延：有燃料时概率点燃第一格草（Set 成火，新火有全新寿命）。
            if (hasFuel && world.NextUInt() % IgnitionDenominator == 0u)
            {
                world.Set(fuelX, fuelY, ParticleIds.Fire);
            }
        }

        /// <summary>
        /// 火能否被**投放**到 (x,y)（ParticleEditor 手刷路径）：
        /// ① 目标必须是空气 / 气体格——**不覆盖或替换任何实体方块 / 粒子**（固、液都不行；
        ///    空气与大气等气体格放行：大气是可替换的环境气，否则被大气填满的沙箱里火将无处可放）；
        /// ② 八邻内必须有**锚点**：固 / 液的一格，**或另一格火**。投放层允许火链——圆刷按「低行先落」
        ///    的顺序逐格判定，贴地时底圈先锚到地面 / 实体、上面的格子再连锁锚到已落下的火，
        ///    因此能一次刷出整团火；完全悬空时底圈一格都锚不上、后续无火可锚，一格都放不下。
        ///    （注意：**物理燃烧的附着物不含火**——见 <see cref="HasAttachment"/>，刷出的无支撑火
        ///    仍会下沉并按 20%/帧熄灭。）越界返回 false。
        /// 注意这是**投放**规则、不是生成规则：点燃草与火的自然蔓延不受它约束。
        /// </summary>
        public static bool CanPlaceAt(ParticleWorld world, int x, int y)
        {
            if (!world.IsInBounds(x, y) || !ParticleRules.IsGasOrEmpty(world, x, y))
            {
                return false;
            }

            for (int i = 0; i < NeighbourX.Length; i++)
            {
                int nx = x + NeighbourX[i];
                int ny = y + NeighbourY[i];
                if (world.GetCell(nx, ny).typeId == ParticleIds.Fire)
                {
                    return true;
                }

                if (ParticleRules.HasTag(world, nx, ny, ParticleTags.Solid | ParticleTags.Liquid))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>(x,y) 八邻内是否有**附着物**：带 <see cref="ParticleTags.Solid"/> 或
        /// <see cref="ParticleTags.Liquid"/> 标签的一格。**火焰不算附着物**（火是气体，
        /// 天然不被 Solid/Liquid 判据命中）——所以火柱不会互相支撑，失去支撑会整体下沉。</summary>
        private static bool HasAttachment(ParticleWorld world, int x, int y)
        {
            for (int i = 0; i < NeighbourX.Length; i++)
            {
                if (ParticleRules.HasTag(
                        world, x + NeighbourX[i], y + NeighbourY[i], ParticleTags.Solid | ParticleTags.Liquid))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把 (wx,wy) 的湿土烤成干土：复用湿土自身「受热蒸发」的既有配方——**先确认水汽摊得出去**
        /// （空气没饱和、且不可见播撒返回 &gt; 0），摊不出去就原样保留（水不能凭空消失，守恒优先）；
        /// 成功则 SetPhase 成干土，并按同一口径从土壤吸走一份相变潜热。
        /// </summary>
        private static void TryDryWetSoil(ParticleWorld world, int wx, int wy)
        {
            if (world.Humidity.IsAirSaturated(
                    world, wx, wy, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap)
                || world.Humidity.SpreadUnitIntoAir(
                    world, wx, wy, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadMaxShare) <= 0)
            {
                return;
            }

            float temperature = world.Heat.GetTemperature(wx, wy);
            world.SetPhase(wx, wy, ParticleIds.Soil);
            world.Heat.AddHeat(wx, wy, -HumidityField.LatentHeat(temperature));
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 留空：蔓延 / 熄灭 / 干化都是每逻辑帧的确定性或概率行为，写在 OnLogic（随机刻密度随世界
            // 大小变稀，用随机刻会让节奏漂移）。将来若有「缓慢过程」（如余烬）再写这里。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化（之后由 ① 逐帧扰动）。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 火被覆盖 / 抹除时无残留效果（将来要留余烬的话在这里往邻居格发射）。
        }
    }
}
