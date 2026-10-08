using UnityEngine;

namespace TapTap2026.Simulation
{
    // FireParticle —— 火焰（气体；**接触点燃**邻接的草、**接触烘干**邻接的湿土，遇石头 / 沙 / 水快速熄灭；
    //   每逻辑帧放热、按温度上色；贴燃料时定在原地燃烧并向上抛出火舌，失去附着物则上升 + 摇曳并迅速熄灭）
    //
    // flags 私有位：无（bit3-4 大气风向记忆、bit5-6 种子计数占用中；bit7 空闲但本类型不使用）
    //   （也不使用 CellFlags.Burning——该位当前没有任何消费者。）
    // 邻居查询范围：**八邻**（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上），每逻辑帧读一遍做：
    //   附着物判定（固 / 液的一格）、燃料判定（第一格草）、干化目标（第一格湿土）、淬熄判定
    //   （第一格液体、第一格石头 / 沙 / 湿土）。**火焰不算附着物**（火是气体，天然不被 Solid/Liquid
    //   判据命中）——火柱不会互相支撑。**投放**资格的锚点判定不同源：除固 / 液外**火链也算锚点**——
    //   同笔刷 / 已有的火可以互相锚定，圆刷贴地时能连锁铺满整团火；完全悬空时一格都放不下。
    //   物理燃烧的附着物始终不含火。
    // 交互的 ParticleIds：Grass → Fire（接触点燃，Set 成火）；WetSoil → Soil（接触烘干——先确认水汽
    //   摊得出去（走湿度场的不可见播撒，与湿土受热蒸发的口径完全一致），水不凭空消失；摊不出去则保留）。
    //   石头 / 沙 / 水 / 湿土只作为**淬熄触发**（见下），不改动对方；液体按标签判（当前所有液体视为水）。
    //   注意：草带 Organic 标签，世界的 MaybeLeaveHumus 规则会在 Set 之后按 20% 概率把那格改判成腐殖质——
    //   **烧过的草地可能留下腐殖质残迹**，这是世界通用规则、不是火的行为。
    //
    // 为什么是 Gas：要被固体 / 液体沉穿（沙落穿火、水浇进火）；上升 / 摇曳也复用气体原语
    //   （ParticleRules.CanGasEnter）。密度 0.6 比环境大气（0.5）重：大气推不动它。
    //
    // ── v2 角色模型（每逻辑帧重判，无持久状态）──
    //   · 附着火：八邻有固 / 液（火不算）。定在原地燃烧、注入热量、概率点燃邻接的草、向上抛射火舌。
    //   · 游离火：八邻无固 / 液。即「火舌」——不注入热量，靠余热上升（OnLogic 末尾），随传导冷却，
    //     冷却到熄影线（EmberTemperature）或摇中熄灭骰即消失，自然呈现「顶端越升越暗、自行收束」。
    //   附着火的燃料烧光 / 被浇灭后，下一帧自动变游离火、上升一二格后熄灭——「脱离残渣上扬消散」是涌现的。
    //
    // 行为（OnLogic，20 Hz；**顺序即契约**）：
    //   ① 颜色回写：shade = **本格温度的分档**（≥300→0 亮黄，200–300→1，130–200→2，80–130→3，
    //      40–80→4，<40→5 暗红）+ (x,y,Tick) 哈希 ±1 抖动后 clamp [0,5]。shade 是温度的**派生量**
    //      （不再由 Set 抽样 / 独立随机游走）：附着火底部恒亮，游离火越升越暗，视觉与热学同源。
    //      哈希不消耗世界随机流。
    //   ② 八邻单趟扫描（只读，不摇骰）：**一趟**同时解析附着物（固 / 液的一格，火不算——火柱不互相支撑）、
    //      燃料（第一格草）、干化目标（第一格湿土）、淬熄触发（液体一档、石头 / 沙 / 湿土一档）。
    //      附着判定原先是一趟独立的 HasAttachment 扫描，与本扫描同源同序，合并后每邻只查一次标签表
    //      （旧实现每邻查两遍，最坏情形白跑近半格成本）。
    //   ③ 放热：**仅附着火**且本格温度 < PeakTemperature 时注入 FireHeatPerTick。游离火不注入——
    //      离焰即离热源，配合 ① 自然得到「顶尖暗红消散」。热由世界每逻辑帧自动做四邻传导扩散
    //      （火绝不自己做传导）。注热必然发生在 ④⑤⑥⑦ 之前：本格新热当帧即经 HeatMap.MarkDirty
    //      回调进入候选位图（同帧可见性契约，见 LogicScanTests），注入后的温度又是 ⑥ 抛射门的门槛。
    //   ④ 干化（先于熄灭骰）：贴湿土且**本格火温 ≥ WetSoilParticle.DryingTemperature（60）**才允许烤干
    //      （冷火不烤干）；水汽先确认摊得出去（IsAirSaturated / SpreadUnitIntoAir 两道门），摊不出去
    //      则原样保留（守恒优先）。烤干按**湿土→干土的热容差** 1.2 结算潜热，不是水↔大气的 5.5。
    //   ⑤ 熄灭（每帧恰摇一次骰，档位自上而下互斥）：
    //      1. **压顶立灭**：正下方是石头 / 沙 / 液体 → 当帧 Clear、不摇骰（石头 / 沙 / 水落在火上时，
    //         世界 rank 规则先把火置换到实体正上方，火在下一逻辑帧即灭）。
    //      2. 贴**液体** → 1/WaterQuenchDenominator（5）：水是最强灭火剂，贴水期望 5 帧 ≈ 0.25 秒灭。
    //      3. 贴**石头 / 沙 / 湿土** → 1/QuenchDenominator（20，期望约 1 秒）。
    //         （干土与腐殖类有意不参与淬熄：草必须长在土上、腐殖类又是草地被烧后的残渣，若它们也淬熄，
    //         火线会被自己脚下按灭，「烧光草地」的既有验收会破。）
    //      4. **冷却即灭**：游离火本格温度 < EmberTemperature（40）→ 当帧 Clear、不摇骰（火舌顶端收束）。
    //      5. **游离熄灭骰**：其余游离火 → 1/FreeExtinguishDenominator（8，期望 8 逻辑帧 ≈ 0.4 秒）。
    //      6. **烧尽骰**：附着但无燃料 → 1/BurnoutDenominator（120，期望约 6 秒，几何分布）。
    //      有燃料且不贴任何淬熄物时**不摇熄灭骰**——保证邻接燃料必被逐格吃掉（否则火线随机断代，
    //      烧过的格变成空气、火不能穿过空气回头补烧，草地上会留下烧不掉的零星残株）。
    //      档位 2/3 命中且带草时仍摇 ⑦ 的点燃骰（见下 RNG 消耗表）。
    //   ⑥ 火舌抛射（附着火专属，先判后摇）：正上方是气体 / 空、且不是火、本格温度 ≥ 200 →
    //      摇 1/TongueDenominator（10），命中 Set(x, y+1, Fire)。新火格通常无附着 → 下一帧起上升为游离火。
    //      上方是火则跳过（火舌不叠火舌，避免垂直火柱自我无限加高）。
    //   ⑦ 蔓延：有燃料 → 摇 1/IgnitionDenominator（20）点燃第一格草（Set 成火，新火有全新寿命）。
    //   ⑧ 游离火上升（末尾，仅游离火，将死之火死在原地）：(x,y,Tick) 哈希 2 bit 选向
    //      （50% 正上 / 25% 左上 / 25% 右上）→ CanGasEnter 通过则 Swap 并 return；首选被挡试另一斜上；
    //      再被挡原地不动（**不下降**）。哈希不消耗随机流；Swap 后两格都被世界打上本 tick 处理标记。
    //
    // RNG 消耗（每格每逻辑帧，枚举即契约）：
    //   贴淬熄物（实体或液体，液体优先判）→ 1 熄灭骰；若还带草 +1 点燃骰。
    //   游离火 → 1 熄灭骰（冷却即灭则不摇，0）。
    //   附着无燃料 → 1 熄灭骰。
    //   附着有燃料 → 1 点燃骰；上方气体且 T ≥ 200 再 +1 抛射骰。
    //   运动帧：0（OnMotion 留空）。上升选向 / 摇曳全是坐标与 Tick 的哈希，不消耗世界随机流。
    //
    // 显式行为（是设计不是 bug）：**火无法停留在石头 / 沙 / 水面上**——这是压顶立灭的必然推论：
    //   手刷在石面 / 沙面 / 水面的火，下一逻辑帧即灭。要「石头旁有篝火」的观感请用草 / 土做基座。
    //
    // 与世界的边界：世界的通用规则仍会作用于火——固 / 液按 rank 规则置换火（水横流换进火格、
    //   沙沉穿火），火被覆盖即经 OnKill 消失：**水浇灭火是涌现的，火自己不写灭火**；降雨凝结
    //   进火格同理。
    //
    // 渲染：不透明火焰色渐变调色板；shade 是温度派生量（见 ①），渲染只读 shade 查表。
    // 投放白名单：ParticleEditor 的 Gas 白名单（_placeableGasTypes，默认 FireParticle）允许手刷火；
    //   其余 Gas（大气 / 水汽）仍不可手刷。
    public sealed class FireParticle : IParticleType
    {
        /// <summary>每逻辑帧向本格注入的热量 Q（火的热容 1.0 ⇒ 未扩散前每帧 +250 度等效）。仅附着火注入。</summary>
        public const float FireHeatPerTick = 250f;

        /// <summary>火格的软温度上限：本格温度低于它才注入热量。单格火的设计平衡温度低于它，
        /// 因此单格行为不受影响；整片火靠它封顶在 ~400 度，而不是被恒定注入推到数千度。</summary>
        public const float PeakTemperature = 400f;

        /// <summary>蔓延概率的分母：八邻有草时每逻辑帧 <c>NextUInt() % 本值 == 0</c> 就点燃第一格草，
        /// 即 1/20 每帧，期望约 1 秒烧掉一格。</summary>
        public const uint IgnitionDenominator = 20u;

        /// <summary>烧尽概率的分母：**附着**但八邻无草（燃料烧尽）时每逻辑帧 <c>NextUInt() % 本值 == 0</c>
        /// 就自然熄灭，即 1/120 每帧，期望寿命 120 逻辑帧 ≈ 6 秒（几何分布，有少量长尾）。</summary>
        public const uint BurnoutDenominator = 120u;

        /// <summary>非液体淬熄概率的分母：八邻有**石头 / 沙 / 湿土**时走这个快骰
        /// （<c>NextUInt() % 本值 == 0</c> 即熄灭），即 1/20 每帧、期望约 1 秒——被石头压、被沙埋
        /// 都死得快。干土与腐殖类有意不在淬熄集合里（见文件头 ⑤3 的说明）。</summary>
        public const uint QuenchDenominator = 20u;

        /// <summary>液体淬熄概率的分母：八邻有**液体**（按标签判）时走这个独立快骰
        /// （<c>NextUInt() % 本值 == 0</c> 即熄灭），即 1/5 每帧、期望 5 逻辑帧 ≈ 0.25 秒。
        /// 把液体从非液体淬熄档拆出：贴水的火远快于贴石头熄——水是最强灭火剂，也把
        /// 「贴水必然煮沸邻水」降为偶发。液体档优先于非液体档（同一格两档都命中时走本档）。</summary>
        public const uint WaterQuenchDenominator = 5u;

        /// <summary>游离火熄灭概率的分母：八邻无固 / 液（火舌）时每逻辑帧 <c>NextUInt() % 本值 == 0</c>
        /// 就立即熄灭，即 1/8 每帧、期望 8 逻辑帧 ≈ 0.4 秒（上升 8 格，火舌长度适中）。
        /// 温度先于本骰判定：本格温度 &lt; <see cref="EmberTemperature"/> 时当帧直接熄灭、不摇本骰。</summary>
        public const uint FreeExtinguishDenominator = 8u;

        /// <summary>游离火的熄影线（冷却即灭）：本格温度低于它时当帧直接熄灭、不摇骰。
        /// 取 40，与 shade 最暗档（< 40 → 5 暗红）同阈值——火舌顶端余热散尽必然收束，不会飘浮长尾。</summary>
        public const float EmberTemperature = 40f;

        /// <summary>火舌抛射概率的分母：**附着火**正上方是气体 / 空（且不是火）、本格温度 ≥ 200 时，
        /// 每逻辑帧 <c>NextUInt() % 本值 == 0</c> 就在正上方 Set 一格火，即 1/10（平均每 0.5 秒吐一格火舌）。
        /// 先判「上方可抛」再摇骰（无目标不消耗 RNG）。</summary>
        public const uint TongueDenominator = 10u;

        /// <summary>湿土热容（与 <see cref="WetSoilParticle.Info"/> 的 heatCapacity 同源）。
        /// 烤干潜热按 湿土 − 干土 的热容差结算，见 <see cref="TryDryWetSoil"/>。</summary>
        private const float WetSoilCapacity = 2.0f;

        /// <summary>干土热容（与 <see cref="SoilParticle.Info"/> 的 heatCapacity 同源）。</summary>
        private const float SoilCapacity = 0.8f;

        /// <summary>火焰 (x,y,Tick) 哈希（<see cref="HashNoise"/>）的 salt。取 0 = 恒等值，
        /// 使抽取成共享 helper 后 shade 抖动与游离火摇曳的序列**逐位不变**。</summary>
        private const uint NoiseSalt = 0u;

        // 八邻偏移（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上）。
        // 静态共享数组：所有八邻扫描（附着物 / 燃料 / 湿土 / 淬熄）同序同源，热路径零分配。
        private static readonly int[] NeighbourX = { -1, 1, 0, 0, -1, 1, -1, 1 };
        private static readonly int[] NeighbourY = { 0, 0, -1, 1, -1, -1, 1, 1 };

        // 火焰色渐变（亮黄 → 暗红，全部不透明）。shade 是温度派生量：每逻辑帧由 ① 按本格温度分档
        // 重算（+ 哈希 ±1 抖动），Set 时的初始抽样会被下一逻辑帧覆盖。
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
            density: 0.6f,               // 比大气（0.5）重：大气推不动它
            heatCapacity: 1.0f,
            thermalConductivity: 1.0f,   // 气体传导最强一档（与大气一致）
            thermalEmissivity: 1.0f,     // 火焰近黑体：暴露面辐射散热强
            affectedByGravity: false,    // 位移全部在逻辑帧手动完成（上升 / 摇曳），不靠重力
            isStatic: false,             // 非静态：才能被扫描（静态类型的钩子永不触发）
            maxFlowSpread: 0,
            friction: 0f,                // 非固体，不参与安息角规则
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(FireParticle));

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 留空（已拍板：不动世界、上升走 OnLogic）：火的位移全部在逻辑帧完成——附着火定在原地；
            // 游离火的上升 + 摇曳是 OnLogic 的最后一步（⑧），每逻辑帧至多一格 → 上限 20 格/秒。
            // 运动帧对火零操作、不消耗任何随机流；上升与颜色回写在同一逻辑帧发生，天然合拍，
            // 不会出现「移动了但颜色没跟上」的错帧。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // ① 颜色回写：shade = 本格温度分档（≥300→0 … <40→5）+ (x,y,Tick) 哈希 ±1 抖动后 clamp [0,5]。
            //    shade 是温度的**派生量**（不再是独立随机游走）；哈希走 HashNoise（坐标哈希，不消耗世界随机流）。
            float temperature = world.Heat.GetTemperature(x, y);
            int band = temperature >= 300f ? 0
                : temperature >= 200f ? 1
                : temperature >= 130f ? 2
                : temperature >= 80f ? 3
                : temperature >= 40f ? 4
                : 5;
            uint noise = HashNoise.Hash32(x, y, world.Tick, NoiseSalt);
            int shade = band + (int)(noise % 3u) - 1;
            if (shade < 0)
            {
                shade = 0;
            }
            else if (shade >= Palette.Length)
            {
                shade = Palette.Length - 1;
            }

            world.At(x, y).shade = (byte)shade;

            // ② 八邻单趟扫描（只读，不摇骰）：一趟同时解析附着物、燃料、干化目标、淬熄触发——附着判定
            //    与 ③ 同源同序，旧实现各查一遍标签表，合并后每邻只查一次（最坏情形省近半格成本）。
            //    扫描只读类型 / 标签，注热只改本格 Q，两者互不观测，故本趟先于 ③ 注热、逐位等价。
            bool attached = false;
            bool hasFuel = false;
            int fuelX = 0;
            int fuelY = 0;
            bool hasWetSoil = false;
            int wetX = 0;
            int wetY = 0;
            bool hasLiquidQuench = false;
            bool hasSolidQuench = false;
            for (int i = 0; i < NeighbourX.Length; i++)
            {
                int nx = x + NeighbourX[i];
                int ny = y + NeighbourY[i];
                ParticleTags tags = ParticleRules.TagsAt(world, nx, ny);
                ushort id = world.GetCell(nx, ny).typeId;

                // 附着物：固 / 液的一格（**火不算**——火是气体，天然不被 Solid/Liquid 判据命中，
                // 所以火柱不会互相支撑）。
                if (!attached && (tags & (ParticleTags.Solid | ParticleTags.Liquid)) != 0)
                {
                    attached = true;
                }

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

                // 淬熄触发拆两档：液体一档（优先），石头 / 沙 / 湿土一档。
                if (!hasLiquidQuench && (tags & ParticleTags.Liquid) != 0)
                {
                    hasLiquidQuench = true;
                }
                else if (!hasSolidQuench
                    && (id == ParticleIds.Stone || id == ParticleIds.Sand || id == ParticleIds.WetSoil))
                {
                    hasSolidQuench = true;
                }
            }

            // ③ 放热：仅附着火且本格温度 < 软上限时注入 FireHeatPerTick（游离火不注入，离焰即离热源）。
            //    注入后的温度记入 postHeatTemperature 供 ⑥ 的门槛使用；④ 的干化门故意用**注入前**的
            //    temperature——否则附着火恒 ≥250，温度门永不阻断（FB3 死代码）。
            float postHeatTemperature = temperature;
            if (attached && temperature < PeakTemperature)
            {
                world.Heat.AddHeat(x, y, FireHeatPerTick);
                postHeatTemperature = world.Heat.GetTemperature(x, y);
            }

            // ④ 干化（先于熄灭骰）：贴湿土且本格火温（**注入前**）≥ DryingTemperature 才烤干——
            //    新生火第一逻辑帧 T≈0 不烤干，注入的热量下一帧才生效（FB3；冷火不再是万能烘干机）。
            if (hasWetSoil && temperature >= WetSoilParticle.DryingTemperature)
            {
                TryDryWetSoil(world, wetX, wetY);
            }

            // ⑤ 熄灭：每帧恰摇一次骰，档位自上而下互斥。
            //    1. 压顶立灭：正下方是石头 / 沙 / 液体 → 当帧 Clear、不摇骰。
            ushort belowId = world.GetCell(x, y - 1).typeId;
            bool crushed = belowId == ParticleIds.Stone || belowId == ParticleIds.Sand
                || (ParticleRules.TagsAt(world, x, y - 1) & ParticleTags.Liquid) != 0;
            if (crushed)
            {
                world.Clear(x, y);
                return;
            }

            if (hasLiquidQuench)
            {
                // 2. 贴液体 → 1/5 快熄（液体优先于非液体档）。
                if (world.NextUInt() % WaterQuenchDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else if (hasSolidQuench)
            {
                // 3. 贴石头 / 沙 / 湿土 → 1/20。
                if (world.NextUInt() % QuenchDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else if (!attached)
            {
                // 4. 冷却即灭：游离火本格温度 < EmberTemperature → 当帧 Clear、不摇骰（火舌顶端收束）。
                if (temperature < EmberTemperature)
                {
                    world.Clear(x, y);
                    return;
                }

                // 5. 游离熄灭骰：1/8。
                if (world.NextUInt() % FreeExtinguishDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else if (!hasFuel && world.NextUInt() % BurnoutDenominator == 0u)
            {
                // 6. 附着但无燃料 → 1/120 烧尽。
                world.Clear(x, y);
                return;
            }

            // ⑥ 火舌抛射（附着火专属）：正上方是气体 / 空、不是火、本格温度 ≥ 200 才摇 1/10
            //    （先判「上方可抛」再摇骰；无目标不消耗 RNG）。
            if (attached && postHeatTemperature >= 200f)
            {
                ushort aboveId = world.GetCell(x, y + 1).typeId;
                if (aboveId != ParticleIds.Fire && ParticleRules.IsGasOrEmpty(world, x, y + 1)
                    && world.NextUInt() % TongueDenominator == 0u)
                {
                    world.Set(x, y + 1, ParticleIds.Fire);
                }
            }

            // ⑦ 蔓延：有燃料 → 1/20 点燃第一格草（Set 成火，新火有全新寿命）。
            if (hasFuel && world.NextUInt() % IgnitionDenominator == 0u)
            {
                world.Set(fuelX, fuelY, ParticleIds.Fire);
            }

            // ⑧ 游离火上升（OnLogic 末尾，仅游离火）：熄灭骰结算完、确认本帧存活之后才尝试移动，
            //    将死之火死在原地、不拖着尸体上移。哈希 2 bit 选向：50% 正上 / 25% 左上 / 25% 右上；
            //    首选被挡试另一斜上（正上被挡时由 hash bit2 选一侧）；再被挡原地不动（**不下降**）。
            if (attached)
            {
                return;
            }

            uint roll = noise & 3u;
            int dx = roll == 2u ? -1 : roll == 3u ? 1 : 0;
            if (ParticleRules.CanGasEnter(world, x, y, x + dx, y + 1))
            {
                world.Swap(x, y, x + dx, y + 1);
                return;
            }

            int fallbackX = dx != 0 ? -dx : ((noise & 4u) == 0u ? -1 : 1);
            if (ParticleRules.CanGasEnter(world, x, y, x + fallbackX, y + 1))
            {
                world.Swap(x, y, x + fallbackX, y + 1);
            }
        }

        /// <summary>
        /// 火能否被**投放**到 (x,y)（ParticleEditor 手刷路径）：
        /// ① 目标必须是空气 / 气体格——**不覆盖或替换任何实体方块 / 粒子**（固、液都不行；
        ///    空气与大气等气体格放行：大气是可替换的环境气，否则被大气填满的沙箱里火将无处可放）；
        /// ② 八邻内必须有**锚点**：固 / 液的一格，**或另一格火**。投放层允许火链——圆刷按「低行先落」
        ///    的顺序逐格判定，贴地时底圈先锚到地面 / 实体、上面的格子再连锁锚到已落下的火，
        ///    因此能一次刷出整团火；完全悬空时底圈一格都锚不上、后续无火可锚，一格都放不下。
        ///    （注意：**物理燃烧的附着物不含火**——见文件头 ② 的附着判定，刷出的无支撑火
        ///    仍会上升、摇曳并按 1/8 熄灭。）越界返回 false。
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

        /// <summary>
        /// 把 (wx,wy) 的湿土烤成干土：复用湿土自身「受热蒸发」的既有配方——**先确认水汽摊得出去**
        /// （空气没饱和、且不可见播撒返回 &gt; 0），摊不出去就原样保留（水不能凭空消失，守恒优先）；
        /// 成功则 SetPhase 成干土，并按相变双方的热容差结算潜热。
        /// </summary>
        /// <remarks>
        /// 潜热系数取**湿土→干土的热容差** <c>WetSoilCapacity − SoilCapacity = 1.2</c>（随温度线性），
        /// **不是** <see cref="HumidityField.LatentHeat"/>（5.5T，水↔大气的口径）：干燥的相变双方是
        /// 湿土与干土，温度连续、只需补热容差这一份。错取 5.5T 会过冷、土温为负。
        /// </remarks>
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
            world.Heat.AddHeat(wx, wy, -(WetSoilCapacity - SoilCapacity) * temperature);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 留空：蔓延 / 熄灭 / 干化 / 上升都是每逻辑帧的确定性或概率行为，写在 OnLogic（随机刻密度随
            // 世界大小变稀，用随机刻会让节奏漂移）。将来若有「缓慢过程」（如余烬）再写这里。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化——下一逻辑帧起由 OnLogic ①
            // 按本格温度分档重算（shade 是温度派生量）。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 火被覆盖 / 抹除时无残留效果（将来若做余烬粒子在这里往邻居格发射）。
        }
    }
}
