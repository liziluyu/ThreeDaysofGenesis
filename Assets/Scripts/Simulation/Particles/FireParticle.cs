using UnityEngine;

namespace TapTap2026.Simulation
{
    // FireParticle —— 火焰（气体；**接触点燃**邻接的草、**接触烘干**邻接的湿土，遇水 / 湿土加速熄灭，
    //   每逻辑帧向本格注入热量；燃料烧尽后自然熄灭）
    //
    // flags 私有位：无（bit3-7 全部空闲；也不使用 CellFlags.Burning——该位当前没有任何消费者）
    // 邻居查询范围：**八邻**（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上），每帧读一遍做三件事：
    //   燃料判定（第一格草）、干化目标（第一格湿土）、淬熄判定（任一液体邻居）；投放资格 CanPlaceAt 的
    //   附着物判定同序（附着物 = 固 / 液 / 另一格火，见下）。
    // 交互的 ParticleIds：Grass → Fire（接触点燃，Set 成火）；WetSoil → Soil（接触烘干——**先确认水汽
    //   摊得出去**（走湿度场的不可见播撒，与湿土受热蒸发的口径完全一致），水不凭空消失；摊不出去则保留）。
    //   液体邻居只影响自身寿命（加速熄灭），不改动对方。
    //   注意：草带 Organic 标签，世界的 MaybeLeaveHumus 规则会在 Set 之后按 20% 概率把那格改判成腐殖质——
    //   **烧过的草地可能留下腐殖质残迹**，这是世界通用规则、不是火的行为。
    //
    // 为什么是 Gas：火将来要自行上腾、且要被固体 / 液体沉穿（沙落穿火、水浇进火），Gas 标签让火
    //   无需特判即可复用气体规则；密度 0.6 比环境大气（0.5）重——大气推不动它。火当前不移动
    //   （OnMotion 空，浮力上腾留给将来的迭代）。
    //
    // 行为（全部在逻辑帧 OnLogic，20 Hz；随机刻留空——随机刻密度随世界大小变稀，蔓延节奏会漂移）：
    //   ① 放热：本格温度低于 PeakTemperature 时向本格注入 FireHeatPerTick。热由世界每逻辑帧自动做
    //      四邻传导扩散（火绝不自己做传导）。单格火平衡温度明显高于环境、四邻被显著抬温：紧贴的
    //      沙 / 石会被烤热（热容小、导热快，几帧内就到上百度）、近处的草会被「烤」（草的温度致死走
    //      草自己的随机刻，慢于接触点燃，表现为「近燃远烤」）、紧邻的水会趋近沸腾点（80）。
    //      上限防止一片火的内部（四邻等温、传导≈0）被恒定注入推到数千度。
    //   ② 燃料 / 湿土 / 液体判定：八邻扫描一次，收集第一格草、第一格湿土、以及是否有液体邻居。
    //   ③ 干化（先于熄灭骰）：遇到湿土就先把它烤成干土（TryDryWetSoil）。
    //   ④ 熄灭：贴水 / 贴湿土 → 快速熄灭骰（1/QuenchDenominator，期望约 1 秒）；仅燃料烧尽 →
    //      普通熄灭骰（1/BurnoutDenominator，期望约 6 秒）；有燃料且不贴水火 → **不摇熄灭骰**——
    //      保证邻接燃料必被逐格吃掉（否则火线会随机断代，烧过的格变成空气、火不能穿过空气回头补烧，
    //      草地上就留下烧不掉的零星残株）。
    //   ⑤ 蔓延：有燃料时以 1/IgnitionDenominator 的概率点燃第一格草（Set 成火，新火有全新寿命）。
    //   每帧的随机消耗：贴水火 1 个熄灭骰（若还带草再加 1 个点燃骰）；只烧草 1 个点燃骰；裸火 1 个熄灭骰。
    //
    // 投放（ParticleEditor 手刷走 CanPlaceAt）：**不覆盖 / 不替换任何实体方块或粒子**（固、液都不行；
    //   空气与大气等气体格放行——大气是可替换的环境气）；**不许浮空**——目标格的八邻内必须有附着物
    //   （固 / 液的一格，或另一格火）。注意这是**投放**规则，不是生成规则：点燃草、火链蔓延不受它约束。
    //
    // 与世界的边界：世界的通用规则仍会作用于火——固 / 液按 rank 规则置换火（水横流换进火格、
    //   沙沉穿火），火被覆盖即经 OnKill 消失：**水浇灭火是涌现的，火自己不写灭火**；降雨凝结
    //   进火格同理。
    //
    // 渲染：不透明火焰色渐变调色板（shade 由 ParticleWorld.Set 按调色板长度均匀随机抽样，
    //   多档颜色随机分布 = 火色的自然抖动；Set 会重置本格湿度，火格初始无湿度）。
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

        /// <summary>淬熄概率的分母：八邻有**液体**（水）或**湿土**时，每逻辑帧走这个快骰
        /// （<c>NextUInt() % 本值 == 0</c> 即熄灭），即 1/20 每帧、期望约 1 秒——遇到水火就死得快。
        /// 与点燃同分母：贴着火的水 / 湿土与贴着火的可燃物，对火来说是同样量级的「事件速率」。</summary>
        public const uint QuenchDenominator = 20u;

        // 八邻偏移（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上）。
        // 静态共享数组：所有八邻扫描（燃料 / 湿土 / 液体 / 投放附着物）同序同源，热路径零分配。
        private static readonly int[] NeighbourX = { -1, 1, 0, 0, -1, 1, -1, 1 };
        private static readonly int[] NeighbourY = { 0, 0, -1, 1, -1, -1, 1, 1 };

        // 火焰色渐变（亮黄 → 暗红，全部不透明）。shade 随机抽样到哪一档，
        // 格子就显示哪一档——多档混排即「跳动的火色」。
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
            density: 0.6f,               // 与蒸汽同档：比大气（0.5）重，大气推不动它
            heatCapacity: 1.0f,
            thermalConductivity: 1.0f,   // 气体传导最强一档（与大气一致）
            thermalEmissivity: 1.0f,     // 火焰近黑体：暴露面辐射散热强
            affectedByGravity: false,
            isStatic: false,             // 非静态：才能被扫描（静态类型的钩子永不触发）
            maxFlowSpread: 0,
            friction: 0f,                // 非固体，不参与安息角规则
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(FireParticle));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 火当前不移动（浮力上腾 / 热压流留给将来的迭代）。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // ① 放热：低于上限才注入。热由世界每逻辑帧做四邻传导扩散，火绝不自己做传导。
            if (world.Heat.GetTemperature(x, y) < PeakTemperature)
            {
                world.Heat.AddHeat(x, y, FireHeatPerTick);
            }

            // ② 八邻扫描（只读，不摇骰）：第一格草（蔓延目标）、第一格湿土（干化目标）、是否有液体邻居。
            bool hasFuel = false;
            int fuelX = 0;
            int fuelY = 0;
            bool hasWetSoil = false;
            int wetX = 0;
            int wetY = 0;
            bool nearLiquid = false;
            for (int i = 0; i < NeighbourX.Length; i++)
            {
                int nx = x + NeighbourX[i];
                int ny = y + NeighbourY[i];
                ushort id = world.GetCell(nx, ny).typeId;

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

                if (!nearLiquid && ParticleRules.HasTag(world, nx, ny, ParticleTags.Liquid))
                {
                    nearLiquid = true;
                }
            }

            // ③ 干化（先于熄灭骰）：遇到湿土就先把它烤成干土——干化的代价是 ④ 的快速熄灭骰。
            if (hasWetSoil)
            {
                TryDryWetSoil(world, wetX, wetY);
            }

            // ④ 熄灭：贴水 / 贴湿土 → 快速熄灭骰；仅燃料烧尽 → 普通熄灭骰；有燃料且贴的是可燃物 → 不摇。
            if (nearLiquid || hasWetSoil)
            {
                if (world.NextUInt() % QuenchDenominator == 0u)
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

            // ⑤ 蔓延：有燃料时概率点燃第一格草（Set 成火，新火有全新寿命）。
            if (hasFuel && world.NextUInt() % IgnitionDenominator == 0u)
            {
                world.Set(fuelX, fuelY, ParticleIds.Fire);
            }
        }

        /// <summary>
        /// 火能否被**投放**到 (x,y)（ParticleEditor 手刷路径）：
        /// ① 目标必须是空气 / 气体格——**不覆盖或替换任何实体方块 / 粒子**（固、液都不行；
        ///    空气与大气等气体格放行：大气是可替换的环境气，否则被大气填满的沙箱里火将无处可放）；
        /// ② 八邻内必须有**附着物**——固体 / 液体的一格，或另一格火（火链，链条必然扎根于实体），
        ///    **不允许浮空放置**。越界返回 false。
        /// 注意这是**投放**规则、不是生成规则：点燃草（Set 成火）与火链蔓延不受它约束。
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
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 火被覆盖 / 抹除时无残留效果（将来要留余烬的话在这里往邻居格发射）。
        }
    }
}
