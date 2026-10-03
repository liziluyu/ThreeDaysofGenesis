using UnityEngine;

namespace TapTap2026.Simulation
{
    // WaterParticle —— 水（液体标签，摩擦度/粘性不读，填 0；热容 4.2，约为固体的 2~4 倍）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下、同行左右各 MaxFlowSpread 格（横流扫描）
    // 交互的 ParticleIds：无 —— 蒸发把水汽交给湿度场（HumidityField），水汽不以粒子形式存在
    //
    // 行为：开头先查温度做相变（达到阈值 → 蒸发成水汽、提升附近湿度），然后才是液体运动。运动为三段固定优先级，成功即 return：
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
        /// 沸点：本格温度达到它就剧烈蒸发成蒸汽。
        /// 现为 80：空气辐射率降到 0.1 之后水面整体变暖（水盆可达 ~99°、浅水洼 ~70°），
        /// 40 会让浅水在阳光下当场爆沸晒干。80 之下**浅水洼晒不到沸点**（只慢蒸发），
        /// 只有深水最热的局部能在正午擦到沸点——剧烈蒸发留给真正的热源与极浅的薄水。
        /// 上游约束见 <c>WaterCycle_ThresholdsKeepSteamHotterThanTheDewPoint</c>：阈值必须让
        /// 蒸汽出生温度（阈值 × 4.2 / 2.0）仍高于露点，否则水循环短路。
        /// </summary>
        public const float BoilingPoint = 80f;

        /// <summary>常温蒸发下限：达到它之后即使不到沸点也缓慢蒸发（现实里水在任何温度都蒸发）。
        /// 取 20（远低于沸点 80）：水面常温就在 20~30° 附近，因此低层大气始终有稳定的水汽来源，
        /// 这也是水循环的主要供料路径——剧烈蒸发只在真正过热时补充。</summary>
        public const float SlowEvaporationTemperature = 20f;

        /// <summary>常温蒸发概率分母（1/N 每 tick）。</summary>
        public const int SlowEvaporationDenominator = 300;

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Liquid,
            density: 1.0f,
            heatCapacity: 4.2f,
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
        /// 水蒸发：**提升附近的湿度**（本格 + 周围的气体邻居，总量一个水格 = <see cref="HumidityField.Unit"/>），
        /// 本格变成大气。湿度场随后按「扩散 + 水平冷→热 + 竖直越热越向上」把水汽输送走。
        /// 水汽没有粒子形态（现实里水汽本来就不可见）——云是湿度场里的高值区，见 <see cref="HumidityField"/>。
        /// </summary>
        private static void Evaporate(ParticleWorld world, int x, int y)
        {
            // 先把本格变成大气，再播撒湿度：本格此时已是气体，可以一起参与承载。
            world.SetPhase(x, y, ParticleIds.Atmosphere);

            // 只播撒给**界内且是气体/空**的邻居；份额按实际能接收的格数均分，总量恰好一个水格（守恒）。
            int receivers = 1;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NeighbourX[k];
                int ny = y + NeighbourY[k];
                if (world.IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(world, nx, ny))
                {
                    receivers++;
                }
            }

            float share = HumidityField.Unit / receivers;
            world.Humidity.Add(x, y, share);
            for (int k = 0; k < 4; k++)
            {
                int nx = x + NeighbourX[k];
                int ny = y + NeighbourY[k];
                if (world.IsInBounds(nx, ny) && ParticleRules.IsGasOrEmpty(world, nx, ny))
                {
                    world.Humidity.Add(nx, ny, share);
                }
            }
        }

        private static readonly int[] NeighbourX = { -1, 1, 0, 0 };
        private static readonly int[] NeighbourY = { 0, 0, -1, 1 };

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 相变优先于运动。蒸发会把这份热量原样带进新格（SetPhase 携带 Q）——
            // 水热容 4.2、大气 1，同一份热让湿空气更烫，浮力把它往上推。
            float temperature = world.Heat.GetTemperature(x, y);

            // 剧烈蒸发：达到沸点必定蒸发。
            // 常温蒸发：不到沸点但够暖时以低概率蒸发——现实里水在任何温度都蒸发，
            //           海洋不用被烤沸也在持续向大气供水。
            bool evaporates = temperature >= BoilingPoint
                || (temperature >= SlowEvaporationTemperature
                    && world.NextUInt() % SlowEvaporationDenominator == 0u);

            // 空气已经吸饱了就不能再蒸发（饱和水汽压的等效表达）：否则海洋会一路蒸干、
            // 水汽摊成满天薄雾，永远达不到降水阈值。
            // 看的是**水面之上的空气**——液体格按不变量永远持有 0 湿度，查自己等于没查。
            if (evaporates && world.Humidity.Get(x, y + 1) >= HumidityField.EvaporationCeiling)
            {
                evaporates = false;
            }

            if (evaporates)
            {
                Evaporate(world, x, y);
                return;
            }

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
