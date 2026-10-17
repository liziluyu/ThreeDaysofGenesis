using System;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// **生产侧**的帧采集：把活世界 / 动物世界 / 高速会话映射成帧快照。只有 world 的唯一写者调用它
    /// （否则「单一写者」的契约就破了）。
    /// </summary>
    /// <remarks>
    /// <para>这里刻意保留了对 <see cref="ParticleWorld"/> / <see cref="CreatureWorld"/> 的读访问：采集发生在写者线程内、
    /// 在发布之前，读到的必须是**同一个** world 实例的当前状态。消费侧（渲染 / UI / 音频）一律只见
    /// <see cref="SimulationFrame"/>，因此采集是本阶段唯一「主线程仍同步读活世界」的地方。</para>
    /// <para><b>音频清账也在这里</b>：<see cref="MotionMonitor.Reset"/> / <see cref="ParticleCountMonitor.ConsumeCreated"/>
    /// 都是「读后清零」的记账口，采集时抽干一次并把结果写进 <see cref="AudioFrame"/>。消费方只读快照，
    /// 不再清账——两个消费者同时读同一帧也各拿各的，不会抢账。</para>
    /// <para><b>性能</b>：所有缓冲都在帧槽上（<see cref="RenderFrame.Pixels"/> / <see cref="CreatureFrame"/> /
    /// <see cref="HighSpeedFrame"/>），采集本身不分配（实体 DTO 逐只写进槽内数组）。</para>
    /// <para><b>worker 安全（阶段 2 / 3 已完成）</b>：采集本身跑在 worker 线程上（<c>SimulationWorker.PublishFrame</c>），
    /// 因此它读到的必须是 world 写者自己的当前状态。像素采集 <see cref="CollectRender"/> 与它调用的光栅化
    /// <see cref="ParticlePixelBuffer.Fill"/> 只读活世界与**纯值**渲染设置（<see cref="LightSettings"/> 的
    /// <see cref="RenderColor"/>），全部用 <see cref="RenderColorMath"/> 的纯数学。实体 DTO 的唯一 Unity 类型
    /// （<see cref="Creature.PlaceholderColor"/>）在采集边界用 <see cref="RenderColorConversion.FromColor32"/>
    /// 转成纯值，因此整份帧载荷不含 Unity 类型——Unity 只剩注册期镜像与主线程上屏转换两个接缝。</para>
    /// </remarks>
    public static class SimulationFrameCollector
    {
        /// <summary>
        /// 采集上屏像素：按日照度定天空色与整体明暗，再把当前网格填进帧槽的像素缓冲。
        /// 像素缓冲归帧槽所有、跨帧复用。
        /// </summary>
        /// <param name="world">源网格（写者私有）。</param>
        /// <param name="light">当前光照配置（提供昼夜天空色与夜晚亮度）。</param>
        /// <param name="frame">目标像素帧；尺寸必须与网格一致。</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> 或 <paramref name="frame"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="frame"/> 的尺寸与网格不一致（说明帧环没随世界重建）。</exception>
        public static void CollectRender(ParticleWorld world, in LightSettings light, RenderFrame frame)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Width != world.Width || frame.Height != world.Height)
            {
                throw new ArgumentException(
                    $"帧尺寸 {frame.Width}×{frame.Height} 与网格 {world.Width}×{world.Height} 不一致。", nameof(frame));
            }

            float daylight = world.Light.Daylight;
            float brightness = RenderColorMath.Lerp(RenderColorMath.Clamp01(light.NightBrightness), 1f, daylight);
            RenderColor background = RenderColorMath.Lerp(light.NightBackground, light.DayBackground, daylight);

            ParticlePixelBuffer.Fill(world, frame.Pixels, background, brightness);

            frame.Tick = world.Tick;
            frame.Daylight = daylight;
            frame.Brightness = brightness;
        }

        /// <summary>
        /// 采集世界面向数据（尺寸 / tick / 日照 / 暂停）与**调用方在命令边界捕获的**探针快照
        /// （见 <see cref="ReadProbe"/>）。<paramref name="probe"/> 原样写进帧——采集侧不再重读活世界，
        /// 因此「请求探针的那条命令看到的是它之前那些命令的结果」这一时序不会被发布时刻的网格状态顶掉。
        /// </summary>
        public static void CollectWorld(ParticleWorld world, bool paused, in CellProbe probe, WorldFrame frame)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            frame.Width = world.Width;
            frame.Height = world.Height;
            frame.Tick = world.Tick;
            frame.Daylight = world.Light.Daylight;
            frame.Paused = paused;
            frame.Probe = probe;
        }

        /// <summary>采集实体渲染 DTO 数组与插值读数（逐只写进帧槽缓冲，不分配）。</summary>
        /// <param name="creatures">动物世界；null 时帧为空。</param>
        /// <param name="logicRate">逻辑帧速率（Hz），渲染层据此推进插值比例。</param>
        public static void CollectCreatures(CreatureWorld creatures, float logicRate, CreatureFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            frame.LogicRate = logicRate;

            if (creatures == null)
            {
                frame.LogicTicks = 0;
                frame.Begin(0);
                return;
            }

            frame.LogicTicks = creatures.LogicTicks;

            System.Collections.Generic.IReadOnlyList<Creature> list = creatures.Creatures;
            int count = list.Count;
            frame.Begin(count);

            for (int i = 0; i < count; i++)
            {
                Creature creature = list[i];
                frame.Set(i, new CreatureRenderData
                {
                    Id = creature.Id,
                    Species = creature.Species,
                    X = creature.X,
                    Y = creature.Y,
                    PrevX = creature.PrevX,
                    PrevY = creature.PrevY,
                    Width = creature.Width,
                    Height = creature.Height,
                    Facing = creature.Facing,
                    AnimationSpeedFactor = ResolveAnimationSpeedFactor(creature),
                    PlaceholderColor = RenderColorConversion.FromColor32(creature.PlaceholderColor),
                });
            }
        }

        /// <summary>
        /// 抽干世界的音频监视器，把本帧的运动 / 存量 / 新建计数写进音频帧。
        /// **抽干这一动作归采集**（也就是归 world 的唯一写者）：消费方此后只读快照，读几次都不再改账。
        /// </summary>
        public static void CollectAudio(ParticleWorld world, AudioFrame frame)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            MotionMonitor liquid = world.LiquidMotion;
            frame.LiquidMovingRatio = liquid.MovingRatio;
            liquid.Reset();

            MotionMonitor solid = world.SolidMotion;
            frame.SolidMovingCount = solid.MovingCount;
            solid.Reset();

            // 未注册（id 尚未回填 = 空气）时按 0 处理：读 0 号槽只会恒为 0，更糟的是会让人误以为
            // 「整张空网格的空气都是火 / 都是一条闪电」。
            ushort fireId = ParticleIds.Fire;
            frame.FireCount = fireId == ParticleRegistry.AirTypeId ? 0 : world.Counts.Count(fireId);

            frame.LightningCreated = world.Counts.ConsumeCreated(ParticleIds.Lightning);
        }

        /// <summary>
        /// 采集高速模式读数与统计快照（每种类型的产生 / 死亡 / 终态存量）。
        /// 会话为 null 或未激活时全部读数清零（计数也逐类型写 0，免得槽里留着上一轮的旧账）。
        /// </summary>
        /// <param name="session">高速会话（world 私有）；null = 从未进入过。</param>
        /// <param name="effectiveSpeed">实测吞吐倍率（驱动持有的倍速控制器）。</param>
        public static void CollectHighSpeed(HighSpeedSession session, float effectiveSpeed, HighSpeedFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            bool active = session != null && session.Active;
            frame.Active = active;
            frame.Completed = active && session.Completed;
            frame.ElapsedLogicTicks = active ? session.ElapsedLogicTicks : 0;
            frame.DurationLogicTicks = active ? session.DurationLogicTicks : 0;
            frame.EffectiveSpeed = active ? effectiveSpeed : 0f;
            frame.LatestSurfaceTemperature = active ? session.LatestSurfaceTemperature : float.NaN;
            frame.LatestAverageHumidity = active ? session.LatestAverageHumidity : float.NaN;

            ParticleType[] types = ParticleRegistry.Types;
            frame.BeginCounts(types.Length);

            for (int id = 0; id < types.Length; id++)
            {
                frame.SetCount(
                    id,
                    active ? session.GetBirths(id) : 0L,
                    active ? session.GetDeaths(id) : 0L,
                    active ? session.GetFinalCount(id) : 0L);
            }
        }

        /// <summary>
        /// 动画帧速倍率：兔子逃命 ×3（最高优先）、否则在动 ×2（<c>|Vx| &gt; 0.01</c>）、静止 ×1。
        /// 在采集侧算（它要读 AI 状态，那是 world 私有的），渲染层只做「帧索引 × 倍率」。
        /// </summary>
        public static float ResolveAnimationSpeedFactor(Creature creature)
        {
            if (creature is RabbitCreature && creature.AiState == RabbitBrain.StateFlee)
            {
                return 3f;
            }

            return System.Math.Abs(creature.VelocityX) > 0.01f ? 2f : 1f;
        }

        /// <summary>
        /// 读一格的探针读数（越界 → 无效）。**读的是活世界的当前状态**，因此调用方必须在正确的命令边界调用：
        /// 驱动的 <c>ProbeCell</c> 命令一被应用就采一份、之后不再重采，于是同一命令批次里它看到的是
        /// 它**之前**那些命令的结果，而不是下一个 tick 之后的网格。
        /// </summary>
        public static CellProbe ReadProbe(ParticleWorld world, int x, int y)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!world.IsInBounds(x, y))
            {
                return default(CellProbe);
            }

            return new CellProbe
            {
                Valid = true,
                X = x,
                Y = y,
                TypeId = world.GetCell(x, y).typeId,
                Temperature = world.Heat.GetTemperature(x, y),
                Heat = world.Heat.GetHeat(x, y),
                Humidity = world.Humidity.Get(x, y),
            };
        }
    }
}
