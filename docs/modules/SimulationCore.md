# 模拟内核（TapTap2026.Simulation）

程序集边界即架构边界。`Assets/Scripts/Simulation/`，纯模拟，**不引用任何渲染对象**。粒子类型的作者契约、世界 API、渲染契约的唯一权威文档是 **`docs/ParticleTypeInterface.md`**；改模拟/渲染架构前先读它，改完同步它。本文件只记内核级机制，各粒子类型的行为细则见 `docs/modules/Particles.md`。

## 数据结构

- 扁平 `Cell[]`（4 字节/格）+ `(x,y)` 直接寻址；`Cell` 布局唯一真源是 `Cell.cs`，其它文件不得复制该结构。
- 类型行为经全局静态注册表（`ParticleRegistry`）以 `typeId → 委托` 直调，热路径零分配、零虚分派。
- 全工程唯一的粒子类型清单是 `ParticleRegistration.RegisterAll()`；新增类型 = 一个文件 + 追加一行注册 + `ParticleIds` 加一个字段（**只能追加，顺序是存档兼容性的一部分**）。
- 世界另持两张与网格同形的场：热图 `HeatMap`（每格存热量 Q）与湿度场 `HumidityField`（每格存水汽，只有非固非液的格持有；云 = 湿度高的空气，不是粒子），外加光照控制器 `LightController`。

## 热与湿度

- 温度是导出量 `T = Q / 类型热容`；热由物质携带（`Swap` 搬 Q、`Set`/`Clear` 就地继承 Q）。
- `StepLogic` 每逻辑帧在粒子扫描后做：全格四邻热传导（传导界内守恒）+ 两条散热路（顶行向虚空 `VoidHeatLoss`、只作用于暴露面并按材料辐射率 ε 缩放的辐射 `RadiativeLoss`）+ 湿度输送（扩散 + 温度定向：水平冷→热、竖直越热越向上）与降雨，再由光照向「每列最上面的非气体粒子（光打到水面就停）」施热，且按光路湿度做 Beer–Lambert 衰减——云越厚地表越晒不到。持续受热的暴露格就近收敛到 `T ≈ 输入功率 / (RadiativeLoss · ε)`。
- 光照的每列「挡光顶 + 光路湿度」是**增量缓存**（`LightController`）：首次挂到世界上建一次（O(W·H)），此后 `Set` / `SetPhase`（类型换掉）与 `Swap`（换位）在提交格与湿度之后通知世界侧缓存，湿度场的每次真值改动（含输送与降雨的搬运）经 `HumidityField.ChangedHook` 报「下标 + Δ」按「是否在挡光顶之上」平移该列的和。**施热因此是 O(宽) 的逐列循环、每逻辑帧没有任何竖直扫描**；抬顶 O(1)，只有挡光顶被抹掉 / 有挡光格换位时才对受影响的列重扫（罕见、摊销 O(1)）。稳态逻辑帧零分配（实测 200 帧 0 字节）。
- 蒸发/凝结按 `HumidityField.LatentHeat(T)` 显式结算潜热（蒸发冷却、凝结放热）；气体竖直 Swap 按 `HeatMap.AdiabaticLapse` 做绝热直减（上升冷却/下沉增温）；静止的水比正上方暖 0.5° 就上浮一格（`TryLiquidConvect`，海洋翻转——「夜间海面比陆地暖」与海陆风昼夜换向的机制）。
- 水沸腾时把水汽集中成一小片（可见白汽）；平时慢蒸发只把水汽摊薄进空气（不可见，只在液面）——可见水蒸气只在沸腾时出现；低温饱和处凝结成雨。
- 两个 O(W·H) 浮点 pass（`HeatMap.Step`、`HumidityField.Step`）跑在 Burst（`[BurstCompile(FloatMode = FloatMode.Strict)]` 指针内核，类级 `[BurstCompile]` + 直调（DirectCall）；`#if UNITY_2022_3_OR_NEWER` / `#else` 走同一内核的 managed 分支）。**不要用 `CompileFunctionPointer`**——本机编辑器 entry point 发现不可靠。热传导 job 化已实测否决（~0.2ms/帧，调度开销吃掉全部收益），刻意保持串行直调；湿度因 budget 读改写 + pair 顺序即语义同样串行。
- `HeatMap.Step` 有不动点跳过（热场收敛且输入未变时整趟跳过，逐位等价，默认开；收益只在完全静止且已收敛的场；`Swap` 只在两端 typeId 不同时推进格子写入版本）。
- 热力学系数（`_conductivity`/`_voidHeatLoss`/`_radiativeLoss`/`_adiabaticLapse`/`_airThermalConductivity`/`_airThermalEmissivity`）在 `ParticleSimulationSettings` 资产；驱动把它们（连同随机刻与热力调试叠加）采成 `SimulationTickSettings`，**变化时**才作为命令交给模拟侧应用（`light` 同理走 `LightSettings`）。

## 随机刻（Minecraft 式）

每逻辑帧在粒子扫描之后、热传导之前，`StepLogic` 均匀有放回地随机抽 `world.RandomTicksPerStep` 个格，命中非空气、非静态的格调其类型的 `OnRandomTick`——用于「缓慢、按概率发生」的过程（生长/蔓延/渗流/腐化）。抽取与世界同一 xorshift32 随机流（可复现）、不碰 bit0 处理标记；**默认 0（关闭，保持既有测试契约）**，生产值由 `ParticleSimulationSettings._randomTicksPerStep` 每帧注入（资产现配 48 ≈ Minecraft 的 3 格 / 4096 密度）。契约见 `docs/ParticleTypeInterface.md` §6。各类型用法见 `docs/modules/Particles.md`。

## 跨类型规则

只看标签不看 typeId：`ParticleTags` 的 rank 次序 `Solid(3) > Liquid(2) > Gas(1) > 空气(0)` 决定谁置换谁（`ParticleRules`）。固体置换液体另有「地下水」闸门（`ParticleWorld.GroundwaterSealingEnabled` 默认开）：只有**运动中**的固体（上一运动扫描位移过，或运行中刚放置/相变）能排开液体——静止的岩石不会把水挤上去，石头/沙里封闭的水稳定不动（土例外：干土按土壤反应吸正上方的水）；下落的沙石照旧沉底。

`ParticleTags` 另有属性标签 `Organic`（不参与 rank）：草、花、树干、树叶、树木种子带它；世界在它们死亡时按 20%（`OrganicDecayDenominator` = 5）就地生成腐殖质（写在 `Set`/`SetPhase` 之后——`OnKill` 在写新格之前、写自身格会被覆盖，故归世界）。**显式霉烂**（新类型本身就是腐殖质）不再叠加衰变骰——骰只在「有机旧类型被非有机新类型替换」时摇。草种子/水生植物故意不带 Organic。

## 运动休眠与两级时基

- **运动帧 60 Hz**（`world.StepMotion()`）与**逻辑帧 20 Hz**（`world.StepLogic()`，每 `LogicTicksPerMotionTick` 个运动帧一次）。
- 运动休眠默认开启：只求值候选格——写入格按 `MotionReadReach` 盒反转并集唤醒（生产表 151 格）+ 温度变化格按紧凑脏索引逐格置位半径 2 Chebyshev 球（⊇ 实际温度读距）；静止粒子整块跳过；`MotionSleepEnabled=false` 回退全网格旧路径。
- 逻辑帧混合扫描：休眠开启时运动候选格跑完整「先逻辑后运动」、其余带 `Info.HasLogic` 的格只跑 `OnLogic`（`OnLogic` 含随机重掷/相变，**永不休眠**）；扫描进行中的热/湿度写入经 `DirtiedHook` **同帧**并入候选位图（否则晚一帧、逐位发散）。再跑随机刻/热传导/湿度/降水/光照。
- 湿度场的两个回调位**互相独立、各自可再加消费者**：`DirtiedHook`（下标，温度唤醒）与 `ChangedHook`（下标 + Δ，光照的列缓存）。世界绑前者、光照绑后者，都不互相覆盖。

## 确定性

模拟只认 `world.Tick` 与世界持有的 xorshift32；禁止 `UnityEngine.Random` / `Time.*` / `Input`。同种子同命令序列逐格可复现。

## 粒子类型作者约束（违者拒收）

- 一个类型一个文件、放 `Simulation/Particles/`、`public sealed`、非 MonoBehaviour。
- **运动写在 `OnMotion`（60 Hz）**（唯一例外：`FireParticle` 游离火上升写在 `OnLogic` 末尾）、**相变/反应写在 `OnLogic`（20 Hz，先于同格运动）**；两者内无分配/LINQ/字符串/`Time.*`；先判断后写、写后立即 return；跨类型判断只走 `ParticleTags`，不特判 typeId/DebugName。
- 必须给有限正数 `HeatCapacity` 与 `[0,1]` 内的 `ThermalConductivity`/`ThermalEmissivity`；读温度用 `world.Heat.GetTemperature`、改热量用 `AddHeat`/`SetHeat`，不要自己重复做热传导。
- 随机刻行为写 `OnRandomTick`（没有就留空），禁分配与 `Time.*`，随机用 `world.NextUInt()`。
- **随机源分路径**：运动路径（`OnMotion` 及 helper）**禁止消耗世界随机流**，方向等随机决策一律用 `HashNoise` 坐标哈希（`(x, y, Tick)` 纯函数——运动休眠的前置）；`OnLogic`/`OnRandomTick` 用 `world.NextUInt()`。
- 每个类型必须声明 `Info.MotionReadReach`（`OnMotion` 对 `_cells` 的轴对齐阅读盒，默认 `(1,1,1)`，宁大勿小；读温度不计入——由温度脏路径覆盖），且 `OnMotion` 不得读湿度/光照等非写入即变的状态、单步位移 ≤ 1 格。
- `OnLogic` 严格空体的类型声明 `Info.HasLogic = false`（构造参数 `hasLogic: false`，默认 `true` 保守）。

## 常驻模拟 worker（阶段 3，2026-10-17 完成）

`SimulationWorker`（`Assets/Scripts/Simulation/SimulationWorker.cs`，纯 C#）是一条**私有 `Thread`**，串行拥有 `ParticleWorld` / `CreatureWorld` / `HighSpeedSession` / 帧环，是这些东西**唯一**的写者与读者：

- **为什么单线程**：`StepMotion` / `StepLogic` 的扫描顺序、世界 RNG 消耗与 `OnLogic` / `OnMotion` 调用次序都是确定性契约，因此**绝不**把同一个 world 的一个 tick 拆给多条线程——结果与在主线程同步推进逐位一致（回归见 `SimulationWorkerTests.ThreadedAndInlineBatches_ProduceIdenticalFrames`：两条路径同一命令日志 ⇒ 像素帧逐位相同）。
- **两个单向无锁通道**：请求环（主线程 → worker，2 槽）与回执环（worker → 主线程，2 槽），各自「待处理 / 空闲」两个 `SemaphoreSlim` 做所有权移交，下标分属各自线程。主线程用 `TryAcquireRequestSlot` **非阻塞**取槽（满载即放弃本次请求，绝不为模拟排队），`RequestBatch` 把待发命令整批搬进该槽并带上有界的运动 tick 数（`MaxBatchTicks = 255`）；worker 每处理完一批写一条 `SimulationBatchResult`（实测耗时 / 是否发布 / 帧序号 / 世代 / 应用命令数 / 是否暂停）。
- **命令边界**：命令在**该批第一个运动 tick 之前**按序全部应用（`DrainCommands` → `Apply`），应用过命令的批次一定发布一帧；`Resize` / `PresentNow` 因此即使在暂停中也有效。`SetHighSpeedLogicFrames` 把每阶段模拟时长（逻辑帧；≤ 0 回到 `SimulationWorkerSetup.HighSpeedLogicFrames`）记在 worker 私有字段上，**只在下次 `EnterHighSpeed` 建会话时生效**，不打断正在跑的会话。
- **至多一帧 / 批**：批次末累计够 `StepsPerPresent` 或应用过命令时发布一帧（五段一次性写完才对外可见）；发布走 `SimulationFramePublisher` 的 release / acquire 语义，主线程要么看到上一帧、要么看到完整新帧。世界重建换新发布器（`Generation + 1`）并立即发布。
- **故障通道**：任一批抛出的异常被捕获 → `Fault`（首个胜出）+ `FaultCount++` + 就地停摆（暂停推进、丢弃其后命令、仍照常回执）；worker **不碰任何 Unity API**，主线程读回执后 `Debug.LogError` 一次。世界的诊断消息（未注册类型 / shade 越界）经 `ParticleWorld.MessageSink` 进队列，同样由主线程记录。
- **生命周期**：构造时在**调用线程**建好世界并发布第一帧（消费方不必等第一次 `Update`）；`Start()` 起线程，`Stop()` 置停止标志 + 唤醒 + `Join` 并复位两环（可再次 `Start()` 沿用同一个世界）。**必须在 Unity 对象销毁前 Join**（驱动在 `OnDisable` / `OnDestroy` 做）。
- **确定性重放口**：`RunBatchInline()` 在调用线程上跑**同一段** `Service` 路径（绕开线程），供测试做「分线程 vs 同步」逐位比对；生产路径永不调它。

## 命令与输出帧（阶段 1 边界，见 `docs/design-threaded-simulation-rendering.md`）

`SimulationCommand`（值类型 + 有序队列 `SimulationCommandQueue`：`Sequence` 单调递增、入队顺序即应用顺序）+ `SimulationFrame` 一族**纯数据 DTO**（全部可脱离 Unity 对象存在）：

- `RenderFrame`：`RenderColor[] Pixels`（**项目自有纯值 RGBA8**，帧槽持有、跨帧复用；不含任何 Unity 类型，见下「worker 安全光栅化」）+ 尺寸 + `Tick`/`Daylight`/`Brightness`，渲染真源；
- `WorldFrame`：尺寸、`Tick`、`Daylight`、`Paused`、`CellProbe Probe`（坐标 / typeId / 温度 / 热量 / 湿度的值拷贝，不含 `Cell` 引用）。探针**在 `ProbeCell` 命令被应用的那一刻采一份**（命令边界；先于同批之后的命令与下一个 tick 的网格写入），此后不再重采——因此读数看到的是该命令**之前**那些命令的结果；世界重建时清空（`Valid = false`）；
- `CreatureFrame`：`CreatureRenderData` 数组（`Id`/`Species`/插值位置/碰撞盒/朝向/`AiState`/动画倍率/占位色）+ 逻辑帧读数。`AiState` 原样搬运 Brain 写的字节（鸟据此选帧组），基类不解释它。`Id` 由 `CreatureWorld.Spawn` 赋值、同一世界内从 **1** 起单调递增：**0 是「未赋值」哨兵**（DTO 默认值），渲染层据此区分空槽；
- `AudioFrame`：液体运动比例、固体运动格数、火格存量、自上次发布以来新写入的雷电格数，以及**各物种的出生 / 死亡计数**（`CreatureWorld.ConsumeEvents` 在采集时抽干，按 `CreatureSpecies` 槽位；消费方只读这份计数，不订阅活世界事件——`Step` 跑在 worker 线程上）；
- `HighSpeedFrame`：高速会话状态 / 进度 / 采样 + 每类型产生 / 死亡 / 终态存量。

`SimulationFramePublisher` 是 3 槽轮换（写 / 显示 / 空闲）：只写 `BeginWrite()` 的槽，`Publish()` 才对外可见；**已发布帧在两次后续发布之内保持不变**，写指针前进一格 ⇒ 正在写的槽永远不是当前显示的槽。像素数组按尺寸分配、绝不跨尺寸复用；世界重建 = 换一个发布器 + `Generation` +1。

`SimulationFrameCollector` 是**生产侧采集**（只有 world 的唯一写者可以调）：世界 → 像素（`ParticlePixelBuffer.Fill`，**worker 安全**：纯值类型 + 纯数学，不引用 Unity，见下）、世界面向数据 / 探针、实体 DTO、音频观察（**抽干** `MotionMonitor` / `Counts` 的记账）、高速统计。消费侧（渲染 / UI / 音频 / 流程）只读帧。

### worker 安全光栅化（阶段 2a，2026-10-16 完成）

- **项目自有纯值像素**：`RenderColor`（`r/g/b/a` 字节，与 `Color32` 同布局同字节，实现 `IEquatable`）替代像素路径上的 Unity `Color32`；`RenderFrame.Pixels`、`ParticleType.RenderPalette`、`LightSettings.Day/NightBackground` 都是它。
- **纯数学**：`RenderColorMath` 提供 `Clamp01` / `Lerp(float,…)` / `Lerp(in RenderColor,…)` / `ScaleRgb`——**逐位复刻** `Mathf.Clamp01` / `Mathf.Lerp` / `Color32.Lerp`（浮点乘后**截断**）与旧算式 `(byte)(通道 × 系数)`。光栅化输出因此与 Unity 路径逐位相同。
- **Unity 只在两个边界出现**（`RenderColorConversion`，本切片唯一 `using UnityEngine` 的文件）：
  1. **注册 / 建表时**把 Unity 授权的 `Info.Palette`（`Color32[]`，粒子授权面不变）镜像成 `ParticleType.RenderPalette`（`RenderColor[]`）；
  2. **主线程上屏时**把一帧像素整块拷进 `ParticleRenderer` 复用的 `Color32[]` 上传缓冲（`CopyToColor32`）再 `SetPixels32` / `Apply`。
- **`ParticlePixelBuffer` 已无 `using UnityEngine`**：`Fill(ParticleWorld, RenderColor[], RenderColor background[, float brightness])` 只读网格 / 热图 / 湿度场与纯值派生表（类型标志表按类型表引用缓存，只镜像 `RenderPalette`），描边 / 液面高光 / 云 / 热力叠加 / 昼夜明暗全部走 `RenderColorMath`。邻居规则与判定顺序逐字未动。
- **零分配稳态**：像素缓冲归帧槽、上传缓冲与纹理同生共死，逐帧只做循环拷贝。
- **仍未做完**：阶段 4 的性能 / 压力实测（Thread Profiler 上量化主线程与 worker 的耗时分摊、真实窗口 resize / 高速结算 / 反复进出 Play 的压力矩阵）尚未执行——阶段 3 的 worker 只做过功能与确定性验证（`SimulationWorkerTests` + 分线程/同步逐位比对）。
- **帧载荷已全纯值**：`CreatureRenderData.PlaceholderColor` 也已是 `RenderColor`（物种授权的 `Color32` 在采集边界转换），因此 `SimulationFrame` 里没有任何 Unity 类型。

## 世界常驻观察者（零 RNG / 零写入，读数不改模拟）

- `MotionMonitor`（按标签掩码的通用运动量观察者，世界持两实例）：`world.LiquidMotion`（带 `Liquid`）导出 `MovingRatio` 供水流动声；`world.SolidMotion`（`Solid` 且非 `Organic`）供落沙声读**绝对** `MovingCount`。**两者的 `Reset()` 由帧采集侧每发布一次调一次**（不再由消费方清账），读数随 `AudioFrame` 发布给所有消费者。O(1) 增量维护 `TrackedCount`（Set/SetPhase 差分）与 `MovingCount`（Swap 按换位匹配端数），掩码构造时对 `world.Types` 摊表。
- `ParticleCountMonitor`（`world.Counts`）：按类型 O(1) 维护存量 `Count(typeId)` 与新增格数 `ConsumeCreated(typeId)`（读后清零）。`Clear` 走 `Set`、`Swap` 不参与、空气与越界 id 恒 0 不抛。
