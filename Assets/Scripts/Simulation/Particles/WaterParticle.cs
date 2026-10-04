using UnityEngine;

namespace TapTap2026.Simulation
{
    // WaterParticle —— 水（液体标签，摩擦度/粘性不读，填 0；热容 6.5，约为固体的 7~10 倍）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下、同行左右邻格、左右各 MaxFlowSpread 格（落差视野扫描）
    // 交互的 ParticleIds：无 —— 蒸发把水汽交给湿度场（HumidityField），水汽不以粒子形式存在
    //
    // 行为：开头先查温度做相变，然后才是液体运动。相变分两条路：
    //         · 沸腾（T ≥ 沸点）：把 1 份水汽**集中**在原地一小片 → 每格浓度高 → 渲染成可见的白色汽团；
    //         · 慢蒸发（T ≥ 常温阈值、按概率、且**正上方是界内气体**）：把同样 1 份水汽**摊到 9×9 一大片**
    //           （半径 4）→ 每格低于可见阈值 → 看不见，只让湿度稳步升高。摊不够薄（接收格太少，
    //           单格份额会超过 InvisibleSpreadMaxShare）时**不蒸发、水保留**——否则半封闭地形会凭空
    //           冒出可见水汽（审计 B2）。
    //       这就是"水蒸气只在沸腾时出现"：可见的水汽只由沸腾产生，慢蒸发是隐形的。
    //       潜热：蒸发（沸腾与慢蒸发）从水里**带走** HumidityField.LatentHeatPerUnit（110）的热量，
    //       海面因此被蒸发冷却；这份潜热由湿度单位携带、随降雨归还（见 HumidityField.LatentHeatPerUnit）。
    //       运动为四段固定优先级，成功即 return：
    //         1. TryFall：正下能进（空气 / 气体）就下落一格；同类水与固体都挡住；
    //         2. TryDiagonalFall（clearance 1）：正下被挡就沿坡斜滑——液体无安息角；
    //         3. TryFlow：每 tick 只横移 1 格（60 Hz 下足够快，不像多格瞬移那样刺眼），
    //            左右随机序，且必须过两道闸门之一——(a) 身后有推：反侧同行邻格是液体，
    //            连片水体把多余的水送往两侧、让低于水面的洞被逐格填平（摊平全靠它）；
    //            (b) 落差视野：本侧 MaxFlowSpread 格内存在「正下方还能继续落」的格，
    //            往低处 / 洞口铺（越界不算落差，底行上的水滴也能停住）。
    //            两闸都不满足 = 往哪边流都不会更低（孤立水滴躺在平地 / 满水面上）→ 静止。
    //            旧的「见空格就跳」让平地水滴以 60 Hz 原地随机瞬移、永不静止（观感回归）；
    //         4. TryLiquidConvect：前三段都动不了时，试一次液体热对流——比正上方暖 ε
    //            （LiquidConvectionEpsilon）就上浮一格（海洋翻转：白天顶加热是稳定分层、不翻转，
    //            热靠传导存进水柱；夜晚表面冷、深处暖，暖水持续上浮把存的热带回表面，
    //            是海面夜间比陆地暖、陆风得以涌现的机制）。
    //       四段都走不通则本 tick 静止（比如躺在水底）。
    //       跨类型交互全部由标签 rank 产生：固体（rank 3）沉穿水，水被置换上浮；
    //       同类水互不置换但横流时可互相穿过（CanFlowThrough 对流体放行同 rank）。
    //
    //       随机刻（OnRandomTick）：冲刷——把半径 SandifyingRadius（2）格圆盘内的**第一个**土壤 / 湿土 /
    //       腐殖土 SetPhase 成沙（固定扫描序 y 升序、同行 x 升序）。随机刻默认关闭，生产值由 Settings 注入。
    public sealed class WaterParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(52, 108, 196, 255),
            new Color32(62, 122, 210, 255),
            new Color32(74, 136, 222, 255),
            new Color32(88, 150, 232, 255),
        };

        /// <summary>
        /// 沸点：本格温度达到它就剧烈蒸发成水汽（并把水汽集中成可见的白色汽团）。
        /// 现为 80：空气辐射率降到 0.1 之后水面整体变暖（水盆可达 ~99°、浅水洼 ~70°），
        /// 40 会让浅水在阳光下当场爆沸晒干。80 之下**浅水洼晒不到沸点**（只慢蒸发），
        /// 只有深水最热的局部能在正午擦到沸点——剧烈蒸发留给真正的热源与极浅的薄水。
        /// 注意水温还取决于热容：热容越大，同样的光照 Q 下升温越慢（见 <c>WaterParticle</c> 的 <c>heatCapacity</c>）。
        /// </summary>
        public const float BoilingPoint = 80f;

        /// <summary>常温蒸发下限：达到它之后即使不到沸点也缓慢蒸发（现实里水在任何温度都蒸发）。
        /// 取 20（远低于沸点 80）：水面常温就在 20~30° 附近，因此低层大气始终有稳定的水汽来源，
        /// 这也是水循环的主要供料路径——剧烈蒸发只在真正过热时补充。</summary>
        public const float SlowEvaporationTemperature = 20f;

        /// <summary>常温蒸发概率分母（1/N 每逻辑帧；逻辑帧频率低于运动帧，等效速率按逻辑帧算）。</summary>
        public const int SlowEvaporationDenominator = 300;

        /// <summary>剧烈蒸发（沸腾）的播撒半径：1 = 3×3 一小片，每格浓度高 → 渲染成**可见**的白色汽团。</summary>
        private const int BoilingSpreadRadius = 1;

        /// <summary>
        /// 液体热对流的温度死区：本格比正上方的水暖超过它，本格才上浮（对流搅动）。
        /// 取 0.5：水的导热极好（σ = 0.95）、水体内部本就接近等温，格间残留温差往往只有零点几度；
        /// 阈值偏大（如 2）会让柱内积起逆温层、翻转迟滞，深处的热迟迟回不到表面；
        /// 偏小（如 0.1）又会让等温面上的浮点噪声触发无意义的换位。0.5 让「上冷下暖」一出现就翻转。
        /// </summary>
        public const float LiquidConvectionEpsilon = 0.5f;

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Liquid,
            density: 1.0f,
            heatCapacity: 6.5f,   // 水：热容再调大——升温最慢、最"扛热"，海陆温差（海风）与水面昼夜惰性更明显
            thermalConductivity: 0.95f,   // 水：导热最好，水体内部接近等温
            thermalEmissivity: 1.0f,      // 水是近黑体（红外吸收极强）：水面照常向外散热
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 5,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(WaterParticle));

        /// <summary>
        /// 水蒸发：**提升附近的湿度**（以本格为中心、半径 <paramref name="radius"/> 的气体/空格，总量与
        /// <see cref="HumidityField.Unit"/> 相等），本格变成大气。湿度场随后按「扩散 + 水平冷→热 + 竖直越热越向上」把水汽输送走。
        /// 水汽没有粒子形态（现实里水汽本来就不可见）——云是湿度场里的高值区，见 <see cref="HumidityField"/>。
        /// <para>顺序是「**播撒必成功才相变**」：本格相变后就是气体，也算一个接收格，故先按
        /// <c>接收格数 + 1</c> 预判份额；超出 <paramref name="maxShare"/> 时**不播撒、不蒸发、返回 false**（水保留）。
        /// 预判通过后再 <see cref="ParticleWorld.SetPhase"/>（本格变大气，多出的那个接收者正是本格），
        /// 随后播撒——此时 <see cref="HumidityField.SpreadUnitIntoAir"/> 数到的接收格数与本格预判一致，必定成功。
        /// 本格分到的那一份湿度因 <see cref="ParticleWorld.SetPhase"/> 保留湿度而留在原地，
        /// 沸腾的汽团因此仍以原地为中心保持可见。</para>
        /// <para>相变完成后带走蒸馏潜热 <see cref="HumidityField.LatentHeatPerUnit"/>：蒸发让本格变冷。</para>
        /// </summary>
        /// <param name="radius">播撒半径：1 = 3×3（每格浓度高，<b>看得见</b>）；越大摊得越薄（每格低于可见阈值，<b>看不见</b>）。</param>
        /// <param name="maxShare">单格最大份额；&gt; 0 且 <c>Unit/接收格数</c> 超过它时放弃本次蒸发。0 = 不限。</param>
        /// <returns>是否真的发生了蒸发（false = 接收格不足，水原样保留）。</returns>
        private static bool Evaporate(ParticleWorld world, int x, int y, int radius, float maxShare)
        {
            // 本格（相变后）也是接收者，故 +1：沸腾的汽团仍以原地为中心。
            int receivers = CountGasReceivers(world, x, y, radius) + 1;
            if (maxShare > 0f && HumidityField.Unit / receivers > maxShare)
            {
                // 接收格太少、摊不薄——这份水汽若播下去会立刻可见。不做，把水留给调用方（守恒优先）。
                return false;
            }

            // SetPhase（而非 Set）保留 Q 与湿度：本格变大气后仍是水汽的合法载体。
            world.SetPhase(x, y, ParticleIds.Atmosphere);
            world.Humidity.SpreadUnitIntoAir(world, x, y, radius, maxShare);
            world.Heat.AddHeat(x, y, -HumidityField.LatentHeatPerUnit);
            return true;
        }

        /// <summary>
        /// (x,y) 周围半径 <paramref name="radius"/> 内的气体 / 空格数，**不含本格**
        /// （本格相变后才算接收者，由调用方另行 +1）。越界格不计。
        /// </summary>
        private static int CountGasReceivers(ParticleWorld world, int centerX, int centerY, int radius)
        {
            if (radius < 0)
            {
                radius = 0;
            }

            int receivers = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int nx = centerX + dx;
                    int ny = centerY + dy;
                    if (world.IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(world, nx, ny))
                    {
                        receivers++;
                    }
                }
            }

            return receivers;
        }

        /// <summary>
        /// 慢蒸发能不能发生：正上方必须是**界内**的气体/空（**只在液面蒸发**，水下的水不会凭空变成气泡；
        /// 顶行的水不蒸发——越界会被 <see cref="ParticleRules.IsGasOrEmpty"/> 当成"空气"，那是审计 B3 的 bug），
        /// 且撒播范围内的空气还没吸饱（见 <see cref="HumidityField.IsAirSaturated"/>）。
        /// 后者是"慢蒸发不可见"的保证：加完这一次的份额，最湿的一格仍低于可见阈值。
        /// </summary>
        private static bool CanEvaporateSlowly(ParticleWorld world, int x, int y)
        {
            if (!world.IsInBounds(x, y + 1) || !ParticleRules.IsGasOrEmpty(world, x, y + 1))
            {
                return false;
            }

            return !world.Humidity.IsAirSaturated(
                world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 相变优先于运动。蒸发会带走一份潜热（见 HumidityField.LatentHeatPerUnit），
            // 于是水面被蒸发冷却——这正是海风形成所需的冷源。
            float temperature = world.Heat.GetTemperature(x, y);

            // 剧烈蒸发：达到沸点必定蒸发（沸腾是唯一会产生**可见**水汽的路径）。
            bool boiling = temperature >= BoilingPoint;

            // 常温蒸发：不到沸点但够暖时以低概率蒸发——现实里水在任何温度都蒸发，海洋不用被烤沸也在供水。
            bool slow = !boiling
                && temperature >= SlowEvaporationTemperature
                && world.NextUInt() % SlowEvaporationDenominator == 0u;

            // 慢蒸发只在**水面**、且周围空气还够干时发生（见 CanEvaporateSlowly）——
            // 这是"慢蒸发不可见"的保证。
            if (slow && !CanEvaporateSlowly(world, x, y))
            {
                slow = false;
            }

            // 沸腾也要看空气吸饱没有（饱和水汽压）：吸饱了就等输送把水汽带走再继续。
            // 看的是**水面之上的空气**——液体格按不变量永远持有 0 湿度，查自己等于没查。
            if (boiling && world.Humidity.Get(x, y + 1) >= HumidityField.EvaporationCeiling)
            {
                boiling = false;
            }

            if (boiling)
            {
                // 沸腾：水汽集中原地 → 可见汽团。设计上可见，故不限份额（maxShare = 0）。
                // 换气失败（周围全是固体、连本格都算不进接收者——正常情况下不会发生）就不相变，水保留。
                Evaporate(world, x, y, BoilingSpreadRadius, 0f);
            }
            else if (slow)
            {
                // 慢蒸发：摊成一大片 → 看不见。接收格不足（Unit/接收格数 > InvisibleSpreadMaxShare）
                // 时播撒返回 0、本格不相变：半封闭地形（石墙口袋）因此不会凭空冒出可见水汽（审计 B2）。
                Evaporate(world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadMaxShare);
            }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            if (ParticleRules.TryFall(world, x, y))
            {
                return;
            }

            if (ParticleRules.TryDiagonalFall(world, x, y))
            {
                return;
            }

            // 竖直都走不动时先试横流，再试热对流：横流返回 bool，用 || 短路——
            // 流动成功就不再对流，优先级严格保持在下落 / 斜落 / 横流之后。
            if (TryFlow(world, x, y)
                || ParticleRules.TryLiquidConvect(world, x, y, LiquidConvectionEpsilon))
            {
                return;
            }
        }

        /// <summary>
        /// 横流：每 tick 只横移 1 格，左右顺序随机以消除方向偏置。
        /// 两道闸门（见类注释 stage 3）都不满足时静止——孤立水滴躺在平地 / 满水面上时
        /// 往哪边流都不会更低，流动只会带来原地抖动。
        /// </summary>
        private bool TryFlow(ParticleWorld world, int x, int y)
        {
            bool leftFirst = (world.NextUInt() & 1u) == 0u;
            return leftFirst
                ? TryFlowSide(world, x, y, -1) || TryFlowSide(world, x, y, 1)
                : TryFlowSide(world, x, y, 1) || TryFlowSide(world, x, y, -1);
        }

        private bool TryFlowSide(ParticleWorld world, int x, int y, int direction)
        {
            int targetX = x + direction;
            if (!ParticleRules.CanDisplace(world, x, y, targetX, y))
            {
                return false;
            }

            // 闸门一「身后有推」：反侧同行邻格是液体 → 被推着走。
            // 跨类型判断只走标签 rank，不特判 typeId。
            if (ParticleRules.TagRank(ParticleRules.TagsAt(world, x - direction, y)) == ParticleRules.LiquidRank)
            {
                world.Swap(x, y, targetX, y);
                return true;
            }

            // 闸门二「落差视野」：本侧同行 MaxFlowSpread 格内存在「正下方还能继续落
            // （气体 / 空）」的格 → 往低处铺。视野途中被穿不过的格（固体）阻断即停。
            // 正下方越界不算落差（IsGasOrEmpty 把越界当空气，见 CanEvaporateSlowly 的审计 B3），
            // 否则躺在世界底行上的水滴会永远往两侧流。
            for (int step = 1; step <= Info.MaxFlowSpread; step++)
            {
                int scanX = x + direction * step;
                if (!world.IsInBounds(scanX, y) || !ParticleRules.CanFlowThrough(world, x, y, scanX, y))
                {
                    return false;
                }

                if (world.IsInBounds(scanX, y - 1) && ParticleRules.IsGasOrEmpty(world, scanX, y - 1))
                {
                    world.Swap(x, y, targetX, y);
                    return true;
                }
            }

            return false;
        }

        /// <summary>把附近土变成沙的搜索半径（格，欧氏距离）：只处理 dx²+dy² ≤ 半径² 的格。</summary>
        public const int SandifyingRadius = 2;

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 冲刷：把半径 SandifyingRadius 格圆盘内的**第一个**土壤 / 湿土 / 腐殖土冲刷成沙。
            // 固定扫描序（y 升序、同一行 x 升序）保证确定性（同种子逐格可复现）。
            for (int dy = -SandifyingRadius; dy <= SandifyingRadius; dy++)
            {
                for (int dx = -SandifyingRadius; dx <= SandifyingRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (dx * dx + dy * dy > SandifyingRadius * SandifyingRadius)
                    {
                        continue;
                    }

                    ushort targetId = world.GetCell(x + dx, y + dy).typeId;
                    if (targetId != ParticleIds.Soil
                        && targetId != ParticleIds.WetSoil
                        && targetId != ParticleIds.HumusSoil)
                    {
                        continue;
                    }

                    // 同一份物质换形态：土壤 / 湿土 / 腐殖土 → 沙，热量 Q 留在该格。
                    world.SetPhase(x + dx, y + dy, ParticleIds.Sand);
                    return;
                }
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 水被覆盖时无残留效果。
        }
    }
}
