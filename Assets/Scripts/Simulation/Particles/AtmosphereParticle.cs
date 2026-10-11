using UnityEngine;

namespace TapTap2026.Simulation
{
    // AtmosphereParticle —— 大气（气体标签，空气本身粒子化：风与对流、以及**湿度**的载体）
    //
    // flags 私有位：bit3-4 水平热压漂移的方向记忆（CellFlags.DriftDirectionMask /
    //        DriftDirectionShift / DriftDirectionNone/Left/Right，已在 CellFlags.cs 登记为「大气私有」）。
    //        bit5-7 空闲。读写记忆位用 CellFlags 的掩码常量：读取 DriftDirectionMask、清除
    //        DriftDirectionClearMask、写入 |，绝不整体赋值 flags（会破坏 bit0 处理标记）。
    // 邻居查询范围：四邻（浮力比温）、上下/斜向各 1 格、同行左右各 1 格；热压流的「连片」判据读同行左右各 2 格
    // 交互的 ParticleIds：**Lightning**（随机刻起爆：见 OnRandomTick 与 LightningParticle）——
    //   除此之外只比温度、不特判类型
    //
    // 行为：浮力（比四邻热 → 升，冷 → 沉）→ 水平热压流（流向暖侧）。
    //       热由物质携带：空气 Swap 时热量跟着走，这就是对流；低空冷空气流向暖的陆地 = 海风。
    //       世界初始化时由 ParticleSimulationDriver 把所有空格填成大气，因此 typeId 0
    //       在运行中的世界里只作为「被抹除后留下的真空」短暂出现，行为规则同样允许气体换入。
    //
    //       这里**故意不做随机漂移**：那是数值噪声、不是风，而它搬运的是整格（含热量与湿度），
    //       会给云层注入逐 tick 的可见抖动（实测可见度翻转 178 → 57 每 tick）。气体的混合改由
    //       HumidityField 的扩散与温度定向平流负责——真实的混合是场的行为，不是随机换位。
    //
    //       水平热压流带**风向记忆**（见 ParticleRules.TryThermalDrift）：热压流把热量随粒子一起搬走，
    //       换位后梯度对刚移动的格子反向，于是无记忆时同一对格子会确定性逐帧往返（审计 B1：
    //       标记位置 5,4,5,4…）。这里在 flags 的 bit3-4 记下上次漂移方向：反向即不走（走廊里最多
    //       走一格即停），顺向或无记忆才前进，本帧无任何可漂方向才清记忆（换向时梯度必穿过死区）。
    //       于是低空海风能沿稳定梯度持续净输运，而不是原地抖动。浮力换行即退出该水平时间序列，
    //       因此浮力成功时会清掉记忆（见 OnMotion）。
    //
    // 湿度：云不存在于此类型里，也不存在于任何粒子类型里——云是**湿度场**（HumidityField）里的高值区。
    //       空气 Swap 时湿度跟着走（见 ParticleWorld.Swap），于是云随风平移、被湍流混合、自然连片；
    //       水蒸发往湿度场里加水汽（WaterParticle.Evaporate），低温饱和处由 ParticleWorld.StepRain 凝结成雨。
    //       因此本类型只管"空气怎么动"，不管云——这正是"云是空气的一种状态，不是一种粒子"。
    //
    // 命名说明：ParticleIds.Air 已被 typeId 0（空）占用，因此 id 字段叫 Atmosphere。
    public sealed class AtmosphereParticle : IParticleType
    {
        /// <summary>浮力/热压流的温度死区：与邻居温差小于它就不动，避免在均匀温度场里抖动。</summary>
        public const float BuoyancyEpsilon = 1f;

        // 调色板全透明：ParticlePixelBuffer 把 alpha 0 映射为背景色，大气不可见，它就是「天空」。
        // 云不靠这张调色板显示——ParticlePixelBuffer 按湿度值把格子染白（见 HumidityField）。
        // 热力叠加（HeatMap.DebugHeatOverlay）仍会把热格染红——隐形大气因此成了风的可视化层。
        private static readonly Color32[] Palette =
        {
            new Color32(158, 196, 228, 0),
            new Color32(164, 200, 230, 0),
            new Color32(170, 204, 232, 0),
            new Color32(176, 208, 234, 0),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Gas,
            density: 0.5f,
            heatCapacity: 1.0f,
            thermalConductivity: 1.0f,   // 大气：与 HeatMap.DefaultAirThermalConductivity 一致（气体传导最强）
            thermalEmissivity: 0.1f,     // 大气（N₂/O₂）对红外透明：气柱不逐格漏热，地表的热才能铺到网格顶
            affectedByGravity: false,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(AtmosphereParticle),
            // 运动阅读盒：TryBuoyantStep 只读四邻 ⇒ (1, 1, 1)。TryThermalDrift 的「连片」判据读
            // (x ± 2, y) 的**温度**——温度读距不进盒（走世界的热脏路径，半径 2），故这里只声明结构读距。
            motionReadReach: new MotionReadBox(x: 1, up: 1, down: 1),
            hasLogic: false); // OnLogic 严格空体：大气只有浮力 / 热压流运动

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 浮力换了行即退出这段水平漂移的时间序列，所以成功前先清掉风向记忆；失败（原位未动）再恢复，
            // 否则会把记忆误清、让水平热压流重新开始逐帧往返。
            byte memory = ReadDriftMemory(world, x, y);
            if (memory != CellFlags.DriftDirectionNone)
            {
                WriteDriftMemory(world, x, y, CellFlags.DriftDirectionNone);
            }

            if (ParticleRules.TryBuoyantStep(world, x, y, BuoyancyEpsilon))
            {
                return;
            }

            if (memory != CellFlags.DriftDirectionNone)
            {
                WriteDriftMemory(world, x, y, memory);
            }

            ParticleRules.TryThermalDrift(world, x, y, BuoyancyEpsilon);
        }

        /// <summary>读本格的风向记忆（<see cref="CellFlags.DriftDirectionMask"/>）。</summary>
        private static byte ReadDriftMemory(ParticleWorld world, int x, int y)
        {
            return (byte)((world.At(x, y).flags & CellFlags.DriftDirectionMask) >> CellFlags.DriftDirectionShift);
        }

        /// <summary>写本格的风向记忆，<c>|</c> / <c>&amp; ~</c> 组合保留含 bit0 处理标记在内的其它位。</summary>
        private static void WriteDriftMemory(ParticleWorld world, int x, int y, byte direction)
        {
            byte flags = (byte)((world.At(x, y).flags & CellFlags.DriftDirectionClearMask)
                | (byte)(direction << CellFlags.DriftDirectionShift));
            world.At(x, y).flags = flags;
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 大气只有运动（浮力 / 热压流），没有相变 / 反应：逻辑帧留空。
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 雷电起爆点：放电发生在空气里，而随机刻只会命中非空气、非静态的格——被粒子化的「空气」
            // 正是大气，因此雷电的起爆门挂在这里（实现见 LightningParticle.TryStrike：2% 门 +
            // 半径 3 圆盘总湿度资格 + 铺雷电主干与落点一团火）。
            // 除雷电之外，大气**没有**随机刻行为：混合由湿度场扩散与温度定向平流负责，
            // 不要往这里塞随机换位（那会注入云层抖动）。
            LightningParticle.TryStrike(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 大气被覆盖时无残留效果（固/液的湿气由 ParticleWorld.EvacuateHumidity 推给邻居）。
        }
    }
}
