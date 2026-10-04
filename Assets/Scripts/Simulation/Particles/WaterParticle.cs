using UnityEngine;

namespace TapTap2026.Simulation
{
    // WaterParticle —— 水（液体标签，摩擦度/粘性不读，填 0；热容 6.5，约为固体的 7~10 倍）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下、同行左右各 MaxFlowSpread 格（横流扫描）
    // 交互的 ParticleIds：无 —— 蒸发把水汽交给湿度场（HumidityField），水汽不以粒子形式存在
    //
    // 行为：开头先查温度做相变，然后才是液体运动。相变分两条路：
    //         · 沸腾（T ≥ 沸点）：把 1 份水汽**集中**在原地一小片 → 每格浓度高 → 渲染成可见的白色汽团；
    //         · 慢蒸发（T ≥ 常温阈值、按概率、且**正上方是气体**）：把同样 1 份水汽**摊到 7×7 一大片** →
    //           每格低于可见阈值 → 看不见，只让湿度稳步升高。
    //       这就是"水蒸气只在沸腾时出现"：可见的水汽只由沸腾产生，慢蒸发是隐形的。
    //       运动为三段固定优先级，成功即 return：
    //         1. TryFall：正下能进（空气 / 气体）就下落一格；同类水与固体都挡住；
    //         2. TryDiagonalFall（clearance 1）：正下被挡就沿坡斜滑——液体无安息角；
    //         3. TryFlowHorizontal：左右随机序逐格扫描，移动到范围内第一个空气格，
    //            单 tick 最多横跳 MaxFlowSpread 格——水因此在平地上摊成水平面。
    //       三处都走不通则本 tick 静止（比如躺在水底）。
    //       跨类型交互全部由标签 rank 产生：固体（rank 3）沉穿水，水被置换上浮；
    //       同类水互不置换但横流时可互相穿过（CanFlowThrough 对流体放行同 rank）。
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
        /// </summary>
        /// <param name="radius">播撒半径：1 = 3×3（每格浓度高，<b>看得见</b>）；越大摊得越薄（每格低于可见阈值，<b>看不见</b>）。</param>
        private static void Evaporate(ParticleWorld world, int x, int y, int radius)
        {
            // 先把本格变成大气，再播撒湿度：本格此时已是气体，可以一起参与承载，
            // 于是接收格数至少为 1（不至于因为"周围没空气"把这份水汽弄丢）。
            world.SetPhase(x, y, ParticleIds.Atmosphere);
            world.Humidity.SpreadUnitIntoAir(world, x, y, radius);
        }

        /// <summary>
        /// 慢蒸发能不能发生：正上方必须是气体/空（**只在液面蒸发**，水下的水不会凭空变成气泡），
        /// 且撒播范围内的空气还没吸饱（见 <see cref="HumidityField.IsAirSaturated"/>）。
        /// 后者是"慢蒸发不可见"的保证：加完这一次的份额，最湿的一格仍低于可见阈值。
        /// </summary>
        private static bool CanEvaporateSlowly(ParticleWorld world, int x, int y)
        {
            if (!ParticleRules.IsGasOrEmpty(world, x, y + 1))
            {
                return false;
            }

            return !world.Humidity.IsAirSaturated(
                world, x, y, HumidityField.InvisibleSpreadRadius, HumidityField.InvisibleSpreadCap);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 相变优先于运动。蒸发会把这份热量原样带进新格（SetPhase 携带 Q）——
            // 水热容 6.5、大气 1，同一份热让湿空气更烫，浮力把它往上推。
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

            if (boiling || slow)
            {
                // 沸腾：水汽集中原地 → 可见汽团；慢蒸发：摊成一大片 → 看不见，只让湿度稳步升高。
                // 换成大气后本帧的运动会被 ProcessCell 跳过（新格已标记本帧已处理）。
                Evaporate(world, x, y, boiling ? BoilingSpreadRadius : HumidityField.InvisibleSpreadRadius);
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

            ParticleRules.TryFlowHorizontal(world, x, y, Info.MaxFlowSpread);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 水暂无随机刻行为：蒸发是每个逻辑帧的概率过程，已在 OnLogic 内按温度与概率处理。
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
