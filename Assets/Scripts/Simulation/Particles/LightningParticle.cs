using UnityEngine;

namespace TapTap2026.Simulation
{
    // LightningParticle —— 雷电（气体；**随机刻**在空气粒子上起爆：2% 门 + 半径 3 圆盘总湿度达标，
    //   自本格向下铺设一整条主干（逐行 ±1 抖动）直到撞上固 / 液——那里就是**落点**——并在落点上方
    //   那排创建一团火焰）
    //
    // flags 私有位：**无**。生命周期不使用 flags——`Cell.shade` 同时就是「剩余寿命兼颜色档」
    //   （与火「shade = 温度分档」、草「shade = 年龄档」同一模式：Set 的随机抽样被 OnSpawn 钉成 0，
    //   之后由 OnLogic 每逻辑帧推进一档）。因此本类型不占用任何 bit3-7，也不与其它类型的私有位冲突。
    // 邻居查询范围：无（OnMotion 是空体）；起爆时按需向下逐格读 `_cells`，并按半径 3 圆盘读**湿度场**
    //   （湿度读距与「读温度」同属场读取，不进 MotionReadReach；本类型 OnMotion 为空，盒取默认 (1,1,1)）。
    // 交互的 ParticleIds：Atmosphere → Lightning（随机刻起爆，`Set` 成雷电）；
    //   落点上方那排的 Lightning / Atmosphere → Fire（`Set` 成火，一团火焰）。
    //   固 / 液只作为「落点」被读取，**从不被覆盖**——起爆只写气体格。
    //
    // 为什么是 Gas：视觉上是「空气里的一道放电」——要被下落的沙 / 水按 rank 规则置换（落下的物质把
    //   雷电切断），又不该被环境大气推动。密度因此取 1.0（> 大气 0.5）：`CanGasEnter` 的
    //   「自己.Density >= 目标.Density」判据让 大气 → 雷电 恒为 false，所以浮力 / 热压流都推不动它。
    //   热学参数与大气同值（热容 1 / 导热率 1 / 辐射率 0.1）：雷电**不注热、不挡光、不改变原有温度场**，
    //   只是「被它替换掉的那几格空气」的等价物——放电的全部热效应由落点那团火负责（火自带注热）。
    //
    // 生命周期（OnLogic，20 Hz）：`Cell.shade` = 已闪过的档数（0 = 刚起爆、最亮），每逻辑帧 +1，
    //   走到末档即 `Clear`。调色板长度 == LifetimeTicks == 4 ⇒ 一次闪光约 4 逻辑帧 ≈ 0.2 秒。
    //   整条主干在同一随机刻里起爆，因此各格同档推进、整条闪电一起由白热变暗琥珀再消失。
    //   不消耗随机流、不读邻居、不写世界（除本格 shade）：`OnLogic` 只有衰老这一件事。
    //
    // 行为（OnRandomTick，20 Hz；**顺序即契约**）：
    //   ① 起爆门：`NextUInt() % StrikeChanceDenominator == 0`（2%）。**先摇骰**——每次被随机刻点到
    //      的空气格都恰好消耗 1 个随机数，与云量 / 地形无关（随机流不随场上内容漂移）。
    //   ② 湿度资格：以本格为中心、半径 StrikeRadius(3) 的**圆盘**（29 格）累计湿度 ≥
    //      StrikeHumidityThreshold(3.0) 才继续。不达标即结束——② 只读湿度场，不消耗随机流。
    //   ③ 起爆 Strike()：自本格向下逐行铺雷电，每行的 x 按 (x, y, Tick) 坐标哈希左右抖动 −1/0/+1
    //      （50% 直落、25% 左、25% 右，两个独立哈希位 ⇒ **不消耗世界随机流**），遇到非气体格即止；
    //      随后在落点上方那一排（impactX−1 .. impactX+1）把气体格 `Set` 成火（一团火焰）。
    //      雷电的随机流消耗 = 每格一次 shade 抽样（`Set` 内部），shade 随即被 OnSpawn 钉回 0。
    //
    // 与世界的边界：世界的通用规则照常作用于雷电——下落的沙 / 水按 rank 置换它（把闪电切断）、
    //   降水凝结进它格也一样；雷电自己既不清除也不点燃别的类型（点燃是落点那团火的事）。
    //
    // 渲染：不透明**黄色**调色板（白热核心 → 亮黄 → 黄 → 暗琥珀），shade 即闪光进度，渲染只读 shade 查表。
    // 投放白名单：不在 ParticleEditor 的 Gas 白名单（`_placeableGasTypes`，默认只有火）里 ⇒ **不可手刷**，
    //   只能由模拟（随机刻起爆）产生——放电不该由画笔凭空制造。
    public sealed class LightningParticle : IParticleType
    {
        /// <summary>
        /// 起爆概率的分母：随机刻命中空气粒子时 <c>NextUInt() % 本值 == 0</c> 才继续做湿度资格判定，
        /// 即 **2%**（需求：随机刻点到空气粒子时有 2% 的几率检查周围半径 3 的区域）。
        /// </summary>
        public const uint StrikeChanceDenominator = 50u;

        /// <summary>
        /// 湿度资格判定的半径（格）：以随机刻命中的空气格为中心，取半径 3 的**圆盘**累计湿度。
        /// 圆盘点数 = 29（<c>dx² + dy² ≤ 9</c>），不是 49（外接正方形）——「区域」按圆盘口径。
        /// </summary>
        public const int StrikeRadius = 3;

        /// <summary>
        /// 圆盘总湿度门槛：达到它才起爆。取 3.0 ≈ **圆盘内每格 0.103**——即整片区域都吃满了水汽
        /// （蒸发上限 <see cref="HumidityField.EvaporationCeiling"/> = 0.1/格，只有凝结回灌的雨云才压得到
        /// 这个均值），对应「厚雨云才打雷」：普通天空的圆盘总和 &lt; 1，一根云柱边缘也只有 2~2.5。
        /// </summary>
        /// <remarks>
        /// 标定依据（临时探针）：生产形态场景（320×180、全海洋 + 稳定日照）里圆盘总和在雨云柱中饱和于
        /// **3.2** 左右、全图均值远低于 1；把门槛放在 3.0 使起爆只发生在最厚的云柱上。
        /// 起爆频率 ≈ (达标格数 / 气体格数) × RandomTicksPerStep × 逻辑帧率 × <see cref="StrikeChanceDenominator"/> 的倒数——
        /// 实测（随机刻 48、24000 逻辑帧）：全海洋正午 ≈ **0.24 道/秒（每 ~4 秒一道）**、混合地形 ≈ 0.13 道/秒
        /// （每 ~8 秒一道），干燥 / 小池塘场景为 **0**。注意它不是与门的概率线性缩放：达标格数本身随云层
        /// 增厚而涨（云越厚打雷越密，正是「厚雨云才打雷」的体现）。手感偏多 / 偏少改本常量或分母即可。
        /// </remarks>
        public const float StrikeHumidityThreshold = 3f;

        /// <summary>
        /// 闪光持续的逻辑帧数 = 调色板档数（4 逻辑帧 ≈ 0.2 秒）。必须等于 <see cref="Palette"/> 的长度：
        /// shade 从 0 走到 <c>LifetimeTicks − 1</c> 即 Clear。
        /// </summary>
        public const int LifetimeTicks = 4;

        /// <summary>
        /// 抖动哈希的两个 salt：位选「要不要横移」、另一个位选「往哪边」。分开取 salt 是为了让两个决策
        /// 互不相关（同一个 <see cref="HashNoise.Hash32"/> 的高低 bit 有相关性）。
        /// </summary>
        private const uint JitterShiftSalt = 0x9E3779B9u;
        private const uint JitterSideSalt = 0x85EBCA6Bu;

        /// <summary>
        /// 闪光色（下标 = <see cref="Cell.shade"/> = 已闪过的档数）：0 最亮（刚起爆的白热核心），
        /// 末档最暗（将熄的暗琥珀）。全是黄色系且**不透明**——雷电要在天空 / 云上看得见。
        /// </summary>
        private static readonly Color32[] Palette =
        {
            new Color32(255, 250, 205, 255),
            new Color32(255, 238, 120, 255),
            new Color32(250, 206, 56, 255),
            new Color32(214, 160, 36, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Gas,
            density: 1.0f,               // 最重的气体：环境大气（0.5）推不动它（CanGasEnter 的密度次序）
            heatCapacity: 1.0f,          // 与大气同值：替换掉的那几格空气热量口径不变
            thermalConductivity: 1.0f,   // 气体传导最强一档（与大气一致）
            thermalEmissivity: 0.1f,     // 与大气同值：雷电不额外辐射漏热，温度场照旧
            affectedByGravity: false,
            isStatic: false,             // 非静态：要收到 OnLogic（闪光衰老）与随机刻（起爆在空气上）
            maxFlowSpread: 0,
            friction: 0f,                // 非固体，不参与安息角规则
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(LightningParticle));
        // HasLogic 取默认 true：OnLogic 做闪光衰老（每帧推进 shade，末档 Clear）。
        // MotionReadReach 取默认 (1, 1, 1)：OnMotion 是空体，没有任何结构读距。

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 留空：雷电定在原地闪光。它是气体但没有位移行为——上升 / 摇曳属于火（火舌），
            // 不是闪电；被下落的沙 / 水置换是世界 rank 规则的涌现，不需要本方法参与。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 闪光衰老：shade 即已闪过的档数（0 = 刚起爆、最亮）。每逻辑帧推进一档，末档即消失。
            // 不消耗随机流、不读邻居、不写世界（除本格 shade），因此对运动休眠 / 确定性零影响。
            ref Cell cell = ref world.At(x, y);
            int shade = cell.shade + 1;
            if (shade >= LifetimeTicks)
            {
                world.Clear(x, y);
                return;
            }

            cell.shade = (byte)shade;
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 随机刻**只**属于空气粒子（本类型自己不会收到随机刻——那是大气的事）。见
            // AtmosphereParticle.OnRandomTick：它把随机刻直接转给 TryStrike。
        }

        /// <summary>
        /// 雷电起爆的确定性入口（由 <see cref="AtmosphereParticle.OnRandomTick"/> 在每个被随机刻命中的
        /// 空气粒子上调用）：① 2% 起爆门 → ② 半径 3 圆盘总湿度资格 → ③ 铺雷电主干 + 落点一团火。
        /// </summary>
        /// <returns>本次调用真的起爆了返回 true（随机流消耗：门命中时恰 1 个随机数，起爆再按格数各加 1）。</returns>
        public static bool TryStrike(ParticleWorld world, int x, int y)
        {
            // ① 起爆门（2%）：**先摇骰**——每次被点到的空气格都恰好消耗 1 个随机数，与后面的湿度资格无关，
            //    因此世界的随机流不随云量 / 地形漂移（同草「撒种先摇骰，找不到落点也照摇」的口径）。
            if (world.NextUInt() % StrikeChanceDenominator != 0u)
            {
                return false;
            }

            // ② 湿度资格：圆盘累计湿度达标才起爆。只读湿度场（越界读 0），不消耗随机流。
            if (DiskHumidity(world, x, y) < StrikeHumidityThreshold)
            {
                return false;
            }

            // ③ 起爆。
            Strike(world, x, y);
            return true;
        }

        /// <summary>
        /// 以 (cx,cy) 为中心、半径 <see cref="StrikeRadius"/> 的**圆盘**（29 格）内的湿度总和。
        /// 越界格由 <see cref="HumidityField.Get"/> 读成 0。
        /// </summary>
        private static float DiskHumidity(ParticleWorld world, int cx, int cy)
        {
            HumidityField humidity = world.Humidity;
            float total = 0f;
            for (int dy = -StrikeRadius; dy <= StrikeRadius; dy++)
            {
                for (int dx = -StrikeRadius; dx <= StrikeRadius; dx++)
                {
                    if (dx * dx + dy * dy > StrikeRadius * StrikeRadius)
                    {
                        continue;
                    }

                    total += humidity.Get(cx + dx, cy + dy);
                }
            }

            return total;
        }

        /// <summary>
        /// 起爆：自 (x,y) 向下铺设雷电主干（每行先写雷电、再定下一行的 x），撞上非气体格即止——那里就是
        /// **落点**；随后在落点上方那一排创建一团火（<c>impactX−1 .. impactX+1</c> 中的气体格）。
        /// </summary>
        /// <remarks>
        /// **只写气体格**（<see cref="ParticleRules.IsGasOrEmpty"/>）：主干绝不覆盖固 / 液，落点那格本身
        /// 也原样保留（雷电打在土 / 石 / 水面上，不是把它打穿）。整列都是气体（网格底行也没有落点）时
        /// 不生成火——没有接地就没有落点。
        /// </remarks>
        private static void Strike(ParticleWorld world, int x, int y)
        {
            int columnX = x;
            int impactX = x;
            int impactY = -1;   // 落点（首个非气体格）的 y；−1 = 一路到底都没有落点

            for (int ty = y; ty >= 0; ty--)
            {
                if (!ParticleRules.IsGasOrEmpty(world, columnX, ty))
                {
                    impactX = columnX;
                    impactY = ty;
                    break;
                }

                world.Set(columnX, ty, ParticleIds.Lightning);

                // 下一行的横向抖动：50% 直落、25% 左、25% 右。两个独立哈希位（不消耗世界随机流），
                // 抖动后越界就改回直落——主干永远留在网格内。
                int nextX = columnX;
                if (HashNoise.Bit(columnX, ty, world.Tick, JitterShiftSalt))
                {
                    nextX += HashNoise.Bit(columnX, ty, world.Tick, JitterSideSalt) ? -1 : 1;
                }

                if (!world.IsInBounds(nextX, ty))
                {
                    nextX = columnX;
                }

                columnX = nextX;
            }

            if (impactY < 0)
            {
                return;
            }

            // 落点处的一大团火焰：落点上方那一排的三格（只写界内的气体格——固 / 液不被覆盖）。
            // 火自己会按既有规则找到附着物（下方就是落点那格实体）并给地面加热 / 点燃草地上的燃料。
            int fireY = impactY + 1;
            for (int dx = -1; dx <= 1; dx++)
            {
                int fx = impactX + dx;
                if (world.IsInBounds(fx, fireY) && ParticleRules.IsGasOrEmpty(world, fx, fireY))
                {
                    world.Set(fx, fireY, ParticleIds.Fire);
                }
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // 新生雷电从**最亮**的一档开始：ParticleWorld.Set 的随机 shade 抽样被这里覆盖（与草同一模式）。
            world.At(x, y).shade = 0;
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 雷电消失时无残留效果（不留下余烬 / 焦痕——落点那团火才是热源）。
        }
    }
}
