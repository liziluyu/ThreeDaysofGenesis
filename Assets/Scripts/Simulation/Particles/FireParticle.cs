using UnityEngine;

namespace TapTap2026.Simulation
{
    // FireParticle —— 火焰（气体；**接触点燃**邻接的草 / 花、**接触烘干**邻接的湿土；落在不可燃物上迅速熄灭、
    //   被物质包死则当帧窒息；每逻辑帧放热、按温度上色；贴燃料时定在原地燃烧并向上抛出火舌，
    //   失去附着物则上升 + 摇曳并迅速熄灭）
    //
    // flags 私有位：无（bit3-4 大气风向记忆、bit5-6 种子计数占用中；bit7 空闲但本类型不使用）
    //   （也不使用 CellFlags.Burning——该位当前没有任何消费者。）
    // 邻居查询范围：**八邻**（固定顺序：左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上），每逻辑帧读一遍做：
    //   附着物判定（固 / 液的一格）、燃料判定（第一格草 / 花）、干化目标（第一格湿土 / 湿润腐殖土）、**通风判定**
    //   （八邻里是否有空气 / 气体）、淬熄判定（第一格液体、第一格非可燃固体）。**火焰不算附着物**
    //   （火是气体，天然不被 Solid/Liquid 判据命中）——火柱不会互相支撑；火柱内部互相算通风口。
    //   **投放**资格（CanPlaceAt）只要求目标不是实体（空气 / 气体格）——**没有附着 / 悬空限制**：
    //   火可刷在任何空位；浮空的火是游离火，会上升 + 摇曳并很快熄灭。物理燃烧的附着物始终不含火。
    // 交互的 ParticleIds：Grass / Flower → Fire（接触点燃，Set 成火——花继承草的可燃性）；
    //   WetSoil → Soil / WetHumusSoil → HumusSoil（接触烘干——先确认水汽
    //   摊得出去（走湿度场的不可见播撒，与湿土受热蒸发的口径完全一致），水不凭空消失；摊不出去则保留）。
    //   石头 / 沙 / 水 / 湿土 / 湿润腐殖土以及**任何非可燃固体**（干土 / 腐殖土 / 腐殖质 / 树干 / 树叶 /
    //   各类种子 / 水生植物……由 Solid 标签统一命中）只作为**淬熄触发**（见下），不改动对方；液体按标签判
    //   （当前所有液体视为水）。
    //   注意：草带 Organic 标签，世界的 MaybeLeaveHumus 规则会在 Set 之后按 20% 概率把那格改判成腐殖质——
    //   **烧过的草地可能留下腐殖质残迹**，这是世界通用规则、不是火的行为。
    //
    // 为什么是 Gas：要被固体 / 液体沉穿（沙落穿火、水浇进火）；上升 / 摇曳也复用气体原语
    //   （ParticleRules.CanGasEnter）。密度 0.6 比环境大气（0.5）重：大气推不动它。
    //
    // ── v2 角色模型（每逻辑帧重判，无持久状态）──
    //   · 附着火：八邻有固 / 液（火不算）。定在原地燃烧、注入热量、概率点燃邻接的草 / 花、向上抛射火舌。
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
    //      燃料（第一格草 / 花）、干化目标（第一格湿土）、通风口（是否有空气 / 气体）、淬熄触发
    //      （液体一档、非可燃固体一档）。
    //      附着判定原先是一趟独立的 HasAttachment 扫描，与本扫描同源同序，合并后每邻只查一次标签表
    //      （旧实现每邻查两遍，最坏情形白跑近半格成本）。
    //   ③ 放热：**仅附着火**且本格温度 < PeakTemperature 时注入 FireHeatPerTick。游离火不注入——
    //      离焰即离热源，配合 ① 自然得到「顶尖暗红消散」。热由世界每逻辑帧自动做四邻传导扩散
    //      （火绝不自己做传导）。注热必然发生在 ④⑤⑥⑦ 之前：本格新热当帧即经 HeatMap.MarkDirty
    //      回调进入候选位图（同帧可见性契约，见 LogicScanTests），注入后的温度又是 ⑥ 抛射门的门槛。
    //   ④ 干化（先于熄灭骰）：贴湿土且**本格火温 ≥ WetSoilParticle.DryingTemperature（60）**才允许烤干
    //      （冷火不烤干）；水汽先确认摊得出去（IsAirSaturated / SpreadUnitIntoAir 两道门），摊不出去
    //      则原样保留（守恒优先）。烤干按**湿土→干土的热容差** 1.2 结算潜热，不是水↔大气的 5.5。
    //   ⑤ 熄灭（档位自上而下互斥；压顶 / 窒息当帧立灭、不摇骰，其余档每帧恰摇一次）：
    //      1. **压顶立灭**：正下方是石头 / 沙 / 液体 → 当帧 Clear、不摇骰（石头 / 沙 / 水落在火上时，
    //         世界 rank 规则先把火置换到实体正上方，火在下一逻辑帧即灭）。
    //      2. **窒息立灭（被包围）**：八邻全是固 / 液——一格空气 / 气体通风口都没有——且**没有燃料** →
    //         当帧 Clear、不摇骰：被埋死、被封进密闭腔的火直接闷熄，不再拖着烧几秒。
    //      3. **冷却即灭**：游离火本格温度 < EmberTemperature（40）→ 当帧 Clear、不摇骰（火舌顶端收束）。
    //      4. **游离熄灭骰**：其余游离火 → 1/FreeExtinguishDenominator（8，期望 8 逻辑帧 ≈ 0.4 秒）。
    //      5. **有燃料 → 不摇熄灭骰**（八邻有草 / 花就算正在烧燃料）。
    //      6. 贴**液体** → 1/WaterQuenchDenominator（2）：水是最强灭火剂，贴水期望 2 帧 ≈ 0.1 秒灭。
    //      7. 贴**任何非可燃固体** → 1/QuenchDenominator（5，期望 5 逻辑帧 ≈ 0.25 秒）：石头 / 沙 / 湿土 /
    //         干土 / 腐殖类 / 树干 / 树叶 / 种子 / 水生植物全是淬熄物——**火不在不可燃物上久留**。
    //      5 是 6/7 的豁免：**有燃料（八邻有草 / 花）时 2/6/7 全不生效**——保证邻接燃料必被逐格吃掉
    //      （否则火线随机断代，烧过的格变成空气、火不能穿过空气回头补烧，草地上会留下烧不掉的零星残株；
    //      草长在土上、腐殖类又是烧过的残渣，若被脚下按灭则「烧光草地」的既有验收会破）。
    //      档位 6/7 与 5 互斥——带燃料根本不摇熄灭骰，旧实现里「贴淬熄物 + 带燃料 → 熄灭骰命中后仍摇点燃骰」
    //      的组合已不存在（见下 RNG 消耗表）。
    //      （旧的「附着无燃料 → 1/120 烧尽骰」已删除：附着火八邻必有固 / 液，有燃料走 5、无燃料必落 6/7，
    //       该档位从此不可达。）
    //   ⑥ 火舌抛射（附着火专属，先判后摇）：正上方是气体 / 空、且不是火、本格温度 ≥ 200 →
    //      摇 1/TongueDenominator（10），命中 Set(x, y+1, Fire)。新火格通常无附着 → 下一帧起上升为游离火。
    //      上方是火则跳过（火舌不叠火舌，避免垂直火柱自我无限加高）。
    //   ⑦ 蔓延：有燃料 → 摇 1/IgnitionDenominator（20）点燃第一格草 / 花（Set 成火，新火有全新寿命）。
    //   ⑧ 游离火上升（末尾，仅游离火，将死之火死在原地）：(x,y,Tick) 哈希 2 bit 选向
    //      （50% 正上 / 25% 左上 / 25% 右上）→ CanGasEnter 通过则 Swap 并 return；首选被挡试另一斜上；
    //      再被挡原地不动（**不下降**）。哈希不消耗随机流；Swap 后两格都被世界打上本 tick 处理标记。
    //
    // RNG 消耗（每格每逻辑帧，枚举即契约）：
    //   附着无燃料 → 贴液体 1 熄灭骰（1/2），否则 1 固体熄灭骰（1/5）；被包围（无通风口）则 0。
    //   附着有燃料 → 位置 5：0 熄灭骰，只摇 ⑦ 点燃骰；上方气体且 T ≥ 200 再 +1 抛射骰。
    //   游离火 → 1 熄灭骰（冷却即灭则不摇，0）。
    //   运动帧：0（OnMotion 留空）。上升选向 / 摇曳全是坐标与 Tick 的哈希，不消耗世界随机流。
    //
    // 显式行为（是设计不是 bug）：**火无法停留在石头 / 沙 / 水面上**——这是压顶立灭的必然推论：
    //   手刷在石面 / 沙面 / 水面的火，下一逻辑帧即灭；落在**任何不可燃固体**（干土 / 腐殖类 / 木头 /
    //   树叶……）上的火也活不过几帧（1/5 淬熄骰），**只有靠燃料（草 / 花）才能持续燃烧**——
    //   「篝火」要有燃料：火贴着燃料就定在原处把它逐格吃掉，燃料烧光即灭。
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

        /// <summary>蔓延概率的分母：八邻有草 / 花（燃料）时每逻辑帧 <c>NextUInt() % 本值 == 0</c> 就点燃第一格，
        /// 即 1/20 每帧，期望约 1 秒烧掉一格。</summary>
        public const uint IgnitionDenominator = 20u;

        /// <summary>非可燃固体淬熄概率的分母：八邻有**任何非可燃固体**（石头 / 沙 / 湿土 / 干土 / 腐殖类 /
        /// 树干 / 树叶 / 种子 / 水生植物……按 <see cref="ParticleTags.Solid"/> 标签判，草 / 花除外——它们是燃料）
        /// 时走这个快骰（<c>NextUInt() % 本值 == 0</c> 即熄灭），即 1/5 每帧、期望 5 逻辑帧 ≈ 0.25 秒——
        /// **火不允许在不可燃物上久留**：被石头压、被沙埋、落在土 / 木头上都当场熄掉。
        /// 未收拢进本档之前是 1/120 的烧尽骰，附着在土 / 腐殖类上的火要赖 6 秒才灭。</summary>
        public const uint QuenchDenominator = 5u;

        /// <summary>液体淬熄概率的分母：八邻有**液体**（按标签判）时走这个独立快骰
        /// （<c>NextUInt() % 本值 == 0</c> 即熄灭），即 1/2 每帧、期望 2 逻辑帧 ≈ 0.1 秒。
        /// 把液体从固体淬熄档拆出：贴水的火远快于贴石头熄——水是最强灭火剂，也把
        /// 「贴水必然煮沸邻水」降为偶发。液体档优先于固体档（同一格两档都命中时走本档）。</summary>
        public const uint WaterQuenchDenominator = 2u;

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
            bool hasVent = false;
            bool hasFuel = false;
            int fuelX = 0;
            int fuelY = 0;
            bool hasWetSoil = false;
            int wetX = 0;
            int wetY = 0;
            bool hasLiquidQuench = false;
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

                // 通风口：八邻里有一格空气 / 气体（火自身是气体 ⇒ 火柱内部互相算通风口）。
                // GetCell 把越界当空气 ⇒ 网格边界也算通风口（宁漏勿错杀）。
                if (!hasVent && (id == ParticleRegistry.AirTypeId || (tags & ParticleTags.Gas) != 0))
                {
                    hasVent = true;
                }

                // 燃料：草与它顶端的花（花带 Organic、继承草的可燃性——见 FlowerParticle）。
                if (!hasFuel && (id == ParticleIds.Grass || id == ParticleIds.Flower))
                {
                    hasFuel = true;
                    fuelX = nx;
                    fuelY = ny;
                }
                else if (!hasWetSoil && (id == ParticleIds.WetSoil || id == ParticleIds.WetHumusSoil))
                {
                    hasWetSoil = true;
                    wetX = nx;
                    wetY = ny;
                }

                // 淬熄触发只留一档（液体，优先于固体档）；固体那一档不需要标志——「附着且无燃料」时
                // 八邻的固 / 液里必有非可燃固体（草 / 花是燃料，见 ⑤5），熄灭阶梯最后一级直接兜住。
                if (!hasLiquidQuench && (tags & ParticleTags.Liquid) != 0)
                {
                    hasLiquidQuench = true;
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

            // ⑤ 熄灭：档位自上而下互斥；压顶 / 窒息当帧立灭、不摇骰，其余档每帧恰摇一次。
            //    1. 压顶立灭：正下方是石头 / 沙 / 液体 → 当帧 Clear、不摇骰。
            ushort belowId = world.GetCell(x, y - 1).typeId;
            bool crushed = belowId == ParticleIds.Stone || belowId == ParticleIds.Sand
                || (ParticleRules.TagsAt(world, x, y - 1) & ParticleTags.Liquid) != 0;
            if (crushed)
            {
                world.Clear(x, y);
                return;
            }

            //    2. 窒息立灭（被包围）：八邻全是固 / 液（一格空气 / 气体通风口都没有）且**没有燃料** →
            //       当帧 Clear、不摇骰——被埋死 / 封在密闭腔里的火直接闷熄，不再靠烧尽骰拖着烧几秒。
            //       燃料是唯一豁免：被草包着的火是在烧燃料（火线必须能穿过草块），不算闷死。
            if (!hasVent && !hasFuel)
            {
                world.Clear(x, y);
                return;
            }

            if (!attached)
            {
                // 3. 冷却即灭：游离火本格温度 < EmberTemperature → 当帧 Clear、不摇骰（火舌顶端收束）。
                if (temperature < EmberTemperature)
                {
                    world.Clear(x, y);
                    return;
                }

                // 4. 游离熄灭骰：1/8。
                if (world.NextUInt() % FreeExtinguishDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else if (hasFuel)
            {
                // 5. 有燃料：不摇熄灭骰——邻接的草 / 花必被逐格吃掉（火线随机断代会让草地留零星残株）。
                //    干土 / 腐殖类虽然也是不可燃固体，但「火贴着草 / 花」说明它正在烧，此时不该被熄灭。
            }
            else if (hasLiquidQuench)
            {
                // 6. 贴液体 → 1/2 快熄（液体优先于固体档）。
                if (world.NextUInt() % WaterQuenchDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
            }
            else
            {
                // 7. 贴非可燃固体 → 1/5。附着且无燃料 ⇒ 八邻的固 / 液里必有一格是非可燃固体，
                //    因此本档必中（旧的「附着无燃料 → 1/120 烧尽骰」已不可达，随本档一并删除）。
                if (world.NextUInt() % QuenchDenominator == 0u)
                {
                    world.Clear(x, y);
                    return;
                }
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

            // ⑦ 蔓延：有燃料 → 1/20 点燃第一格草 / 花（Set 成火，新火有全新寿命）。
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
        /// 火能否被**投放**到 (x,y)（ParticleEditor 手刷路径）：目标必须是空气 / 气体格——
        /// **不覆盖或替换任何实体方块 / 粒子**（固、液都不行；空气与大气等气体格放行：大气是可替换的
        /// 环境气，否则被大气填满的沙箱里火将无处可放）。**没有任何附着 / 悬空限制**——火可刷在任何空位，
        /// 浮空的火是游离火，会上升 + 摇曳并按 1/8 熄灭。越界返回 false。
        /// 注意这是**投放**规则、不是生成规则：点燃草与火的自然蔓延不受它约束。
        /// </summary>
        public static bool CanPlaceAt(ParticleWorld world, int x, int y)
        {
            return world.IsInBounds(x, y) && ParticleRules.IsGasOrEmpty(world, x, y);
        }

        /// <summary>
        /// 把 (wx,wy) 的湿土 / 湿润腐殖土烤成对应的干形态：复用湿土自身「受热蒸发」的既有配方——**先确认水汽摊得出去**
        /// （空气没饱和、且不可见播撒返回 &gt; 0），摊不出去就原样保留（水不能凭空消失，守恒优先）；
        /// 成功则 SetPhase 成干形态，并按相变双方的热容差结算潜热。
        /// </summary>
        /// <remarks>
        /// 潜热系数取**湿 → 干的热容差**（<c>WetSoil − Soil = 2.0 − 0.8 = 1.2</c>；
        /// <c>WetHumusSoil − HumusSoil = 2.2 − 1.0 = 1.2</c>，两者同值，随温度线性），
        /// **不是** <see cref="HumidityField.LatentHeat"/>（5.5T，水↔大气的口径）：干燥的相变双方是
        /// 湿 / 干同族材料，温度连续、只需补热容差这一份。错取 5.5T 会过冷、土温为负。
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

            // 湿润腐殖土 → 干腐殖土；湿土 → 干土。两者的潜热系数都是 1.2（见上）。
            ushort targetId = world.GetCell(wx, wy).typeId == ParticleIds.WetHumusSoil
                ? ParticleIds.HumusSoil
                : ParticleIds.Soil;
            float temperature = world.Heat.GetTemperature(wx, wy);
            world.SetPhase(wx, wy, targetId);
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
