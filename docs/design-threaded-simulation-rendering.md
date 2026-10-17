# 模拟与渲染分线程计划

## 目标

把纯粒子模拟从 Unity 主线程移到**单一常驻模拟 worker**。主线程只采样 Unity 输入、提交有序命令、消费已完成帧并调用 Unity 渲染 / UI / 音频 API。

目标不是并行化 `ParticleWorld` 内部扫描；其扫描顺序、世界 RNG 与 `OnLogic` / `OnMotion` 均是确定性契约，仍由一个 worker 串行推进。目标是让模拟计算不阻塞主线程，并消除渲染读取正在被写入的 `ParticleWorld` 的数据竞争。

## 非目标

- 不在 worker 调用任何 `UnityEngine.Object`、`MonoBehaviour`、`Texture2D`、`Material`、`Mesh`、`AudioSource`、`Time` 或 `Input` API。
- 不改变同 seed + 同命令序列的逐 tick 模拟结果。
- 不把热传导、湿度输送或单个世界 tick 拆成并行 job；它们当前的扫描顺序就是语义。
- 不把 `Texture2D.Apply` 移到 worker；Unity 纹理上传留在主线程。
- 不让主线程直接读 / 写 worker 持有的 `ParticleWorld`。

## 现状与边界

当前 `ParticleSimulationDriver.Update` 在主线程内推进 `ParticleWorld.StepMotion` / `StepLogic`，随后同线程 `ParticleRenderer.Present(world)` 读取 `Cell[]`、热场、湿度场并上传纹理。

当前跨边界的直接世界访问必须全部切断（**阶段 1 已完成**，实现细节见「分阶段实施」）：

| 使用者 | 当前行为 | 新边界 |
|---|---|---|
| `ParticleEditor` | 探针、`Set` / `Clear`、笔刷、暂停时 `PresentNow` | 主线程生成 `SimulationCommand`；只读 `WorldFrame` 供探针/HUD；提交后等待下一完成帧 |
| `ParticleLightDriver` | 每帧直接写 `world.Light` | 检测配置变更后提交 `SetLightSettings` 控制命令 |
| `ParticleSimulationDriver` | 推进世界、参数写回、动物推进、HighSpeed | worker 的唯一写者；主线程只调度 worker / 消费帧 |
| `ParticleRenderer` | 直接读 `ParticleWorld` 并填/上传纹理 | 消费不可变 `RenderFrame` 的像素缓冲；主线程只 `SetPixels32` / `Apply` |
| `CreatureRenderer` | 读 `CreatureWorld` 与 `world.Light.Daylight` | 消费 `CreatureFrame` 和帧内已计算的 daylight |
| 声音驱动 | 直接读/清 `MotionMonitor`、`Counts` | 消费每帧附带的不可变 `AudioFrame`，不再清 worker 计数 |
| `GameController` / 高速结算 | 读可变 `HighSpeedSession` | 消费 `WorldFrame.HighSpeed` 快照；进入/退出为控制命令 |

## 数据所有权与帧协议

### 单写者

`SimulationWorker` 唯一拥有：

- `ParticleWorld`、`CreatureWorld`、`HighSpeedSession`；
- 每 tick 的 `ParticleSimulationSettings` 副本；
- 所有 world 写、RNG、统计消费、动物死亡物质化。

主线程**永远不持有可变 world 引用**。公开的 `ParticleSimulationDriver.World` / `Creatures` 必须移除或改成只读帧访问器，禁止保留一个看似可读、实际并发不安全的兼容别名。

### 命令输入

> **实现状态（阶段 1）**：下表的命令种类已全部落地为 `SimulationCommandKind`（`ResetPhase` 未实现——阶段重建走 `Resize`，
> 演示内容清场走 `ClearAll`）；队列 `SimulationCommandQueue` 入队即定序（`Sequence`），应用时严格按入队顺序。

建立预分配、单生产者/单消费者的 `SimulationCommandQueue`：

```text
Input / UI (main) -> ordered SimulationCommand batches -> worker
worker -> completed WorldFrame ring -> main
```

每条命令带单调递增 `sequence`，worker 在**下一个 motion tick 开始前**按 sequence 全部应用。命令是值类型、无闭包、无 `UnityEngine.Object` 引用。首批命令：

- `PaintDisk` / `EraseDisk`（中心、半径、typeId）；
- `SpawnCreature`；
- `SetPaused`、`Resize`、`ResetPhase`；
- `SetLightSettings`；
- `SetSimulationSettings`（热、湿度、随机刻、显示调试以可分离字段传递）；
- `EnterHighSpeed` / `ExitHighSpeed`。

输入延迟定义为：命令在下一 motion tick 可见；高倍速时同一批命令只在第一个待执行 tick 前应用一次。命令进入日志的顺序成为重放 / 确定性测试的权威输入序列。

### 输出帧

> **实现状态（阶段 1）**：以下 DTO 与 3 槽发布器（`SimulationFramePublisher`）/ 采集器（`SimulationFrameCollector`）已落地。
> 本阶段的「release publication / acquire」退化为方法调用顺序（读写都在主线程），协议本身与线程无关，阶段 3 只需补发布 / 获取语义。

使用三个预分配槽位（worker 写、main 显示、空闲）：

- `RenderFrame`：`RenderColor[] pixels`（项目自有纯值 RGBA8，见「阶段 2」）、width、height、tick、daylight；worker 在世界 tick 批次结束后把世界映射为像素；
- `WorldFrame`：probe 所需的只读格值、尺寸、tick、daylight、暂停/高速状态、HUD 计数；不暴露 `Cell[]`；
- `CreatureFrame`：连续的 creature render DTO 数组；
- `AudioFrame`：液体/固体运动计数、火存量、雷电新建数、各物种出生/死亡计数（动物声音驱动因此不再订阅活世界事件）；
- `HighSpeedFrame`：状态、进度、样本、完成快照 / 结论输入。

worker 只写 `Writing` 槽；写完用 release publication 标记为 `Ready`。主线程用 acquire 取得最新完整帧；允许丢弃旧 ready 帧，绝不阻塞渲染等待模拟。主线程显示中的槽位到下一次替换后才归还。resize 创建新 frame-ring，在主线程确认旧 ring 不再显示后释放；不得跨尺寸复用像素数组。

`RenderFrame` 是渲染真源，故 worker 不需要复制 `Cell[]` / 热 / 湿度到主线程；每次发布只写同一归属槽的像素阵列。`ParticlePixelBuffer` 已拆成不依赖 `ParticleWorld` 之外任何 Unity 类型的 worker-safe rasterizer（见「阶段 2」），输入为 worker 持有的 arrays / 派生表与纯值渲染设置。

## 分阶段实施

### 阶段 1：先建立无并发帧边界 —— **已完成（2026-10-16）**

1. ✅ `SimulationCommand`（值类型 + `SimulationCommandKind` + 有序队列 `SimulationCommandQueue`，入队即定序）+ `SimulationFrame` 一族纯 C# DTO：
   `RenderFrame` / `WorldFrame`（含 `CellProbe` 值拷贝，不暴露 `Cell[]`）/ `CreatureFrame`（`CreatureRenderData`）/ `AudioFrame` / `HighSpeedFrame`；
   载荷配置也是值类型：`LightSettings`、`SimulationTickSettings`。
2. ✅ 仍走主线程 tick：`ParticleSimulationDriver.Advance(dt)` = 采 Unity 侧配置 → **tick 边界按序抽干并应用命令** → 推进 tick → 需要时发布完整帧。
   帧存储/发布在 `SimulationFramePublisher`（3 槽轮换：只写 `BeginWrite()` 的槽、`Publish()` 才可见；已发布帧在两次后续发布之内保持不变；
   像素数组按尺寸分配、绝不跨尺寸复用；世界重建 = 新发布器 + `Generation` +1）。`SimulationFrameCollector` 是唯一的采集侧。
   对外只剩 `Submit(SimulationCommand)` 与 `CurrentFrame`：`ParticleSimulationDriver` 的
   **`World` / `Creatures` / `HighSpeed` / `Speed` / `Pause()` / `Resume()` / `PresentNow()` / `Resize()` 全部移除**，不保留任何可变兼容别名。
3. ✅ `ParticleRenderer.Present(RenderFrame)`（像素生成移到采集侧；`PanCamera` 只吃宽度；昼夜调光与天空色不再由渲染器算）、
   `CreatureRenderer` 按 `CreatureFrame` 与稳定 `Id` 对账、`ParticleEditor`（`SubmitPlacement` / `SubmitPaused` / 探针命令 + 帧读数）、
   `ParticleLightDriver`（**只在配置变化时**提交 `SetLightSettings`）、`GameController`（`Resize` 命令 + 帧里的高速状态与结算快照）、
   `ParticleSandboxSeeder`（`ClearAll` / `FillRect` / `PresentNow`）、`DialogueController`（`SetPaused`）、四个声音驱动 +
   `SoundController`（`HighSpeedSilenced` 读帧）全部改走命令 / 帧。运行时代码里已无 `ParticleSimulationDriver.World` / `.Creatures` 的读法。
4. ✅ `MotionMonitor.Reset` / `ParticleCountMonitor.ConsumeCreated` 由采集侧（发布前）抽干一次并写进 `AudioFrame`；
   消费方只读快照、不再清账 ⇒ 多个消费者读同一帧各拿各的（回归见 `SimulationFrameTests`）。
5. ✅ 命令 / 帧行为的聚焦测试在 `Assets/Tests/EditMode/SimulationFrameTests.cs`（11 个用例）：命令有序应用（同格先涂后擦 vs 先擦后涂）、
   入队不发布 / 边界才发布、笔刷半径是命令载荷、Resize 换世代、生成实体的 DTO、已发布帧在后续模拟突变后保持稳定
   （3 槽松弛度 = 两次发布）、像素与已知世界映射逐位一致、音频观察对多个消费者无副作用（含两个真实落沙声驱动读同一帧）、
   以及**相同 seed + 相同命令日志 ⇒ 同一 tick 边界上像素 / tick / 探针逐位一致**（确定性回归；`FramePump` 在 `TestParticleFixtures.cs` 里按生产路径采帧）。

**阶段 1 的两条有意保留的限制**（阶段 2 / 3 才解决）：

- ~~`ParticlePixelBuffer.Fill` 仍读 Unity `Color32` 调色板与 `HeatMap`/`HumidityField`，**尚不可宣称 worker 安全**~~ —— **已在阶段 2 解决**（见下）。
- `SpeedController` 的耗时回喂、`Application.targetFrameRate`、`Time.deltaTime` 仍在主线程采样（阶段 3 搬进 worker 批次）。

验收：主线程仍同步推进时，旧路径与帧边界路径在固定 seed / 命令日志下逐 tick world hash、像素、音频事件和高速结算完全一致。

### 阶段 2：提取 worker-safe simulation core

#### 2a. worker-safe 光栅化（像素路径）—— **已完成（2026-10-16）**

像素生成整条链已抽掉 Unity：模拟侧只出现项目自有的纯值类型与纯数学，Unity 类型只剩「注册时镜像」与「上屏时转换」两个边界。

| 组件 | 内容 |
|---|---|
| `RenderColor`（新） | 项目自有 RGBA8 值类型（`r/g/b/a`，与 `Color32` 同布局同字节），实现 `IEquatable`，无任何 Unity 引用 |
| `RenderColorMath`（新） | 光栅化纯数学：`Clamp01` / `Lerp(float,…)` / `Lerp(in RenderColor,…)` / `ScaleRgb`——**逐位复刻** `Mathf.Clamp01` / `Mathf.Lerp` / `Color32.Lerp` 与旧的 `(byte)(c × k)` 算式 |
| `RenderColorConversion`（新，**唯一含 Unity 的文件**） | 两个边界：类型注册时 `Info.Palette`（Unity 授权面）→ `ParticleType.RenderPalette`（纯值镜像）；主线程上屏时 `RenderColor[]` → 复用的 `Color32[]` 上传缓冲 |
| `ParticlePixelBuffer` | 不再 `using UnityEngine`：`Fill(ParticleWorld, RenderColor[], RenderColor background[, float brightness])`，取色走 `ParticleType.RenderPalette`；标志表缓存纯值调色板；描边 / 液面 / 云 / 热力叠加 / 昼夜变暗全部用 `RenderColorMath` |
| `RenderFrame.Pixels` | `Color32[]` → `RenderColor[]`（帧槽持有、跨帧复用，**payload 不再含 Unity 类型**） |
| `LightSettings` | `DayBackground` / `NightBackground` 由 `Color32` 改为 `RenderColor`——命令载荷也不含 Unity 类型；`ParticleLightDriver` 在**提交那一刻**从 Inspector 的 `Color32` 字段转换 |
| `SimulationFrameCollector.CollectRender` | 只用 `RenderColorMath` 算天空色与亮度（`Mathf` / `Color32` 已从这里消失），`ResolveAnimationSpeedFactor` 改用 `System.Math.Abs` |
| `ParticleRenderer.Present` | 新增复用的 `Color32[] _uploadPixels`（与纹理同生共死）：`CopyToColor32` 整块转换 → `SetPixels32` / `Apply`，稳态逐帧**零分配** |

**验收（已过）**：

- worker 侧光栅化器与 `RenderFrame` 像素载荷里**没有任何** Unity 类型 / API（`ParticlePixelBuffer.cs` 已无 `using UnityEngine`）；
- **像素逐位不变**：聚焦测试 `RenderRasterizerTests`（7 例）以 Unity 自己的 `Color32.Lerp` / `Mathf.Lerp` / 截断算式为基准逐字节比对，覆盖实心描边、液面高光、湿度云、热力叠加、夜晚亮度五个命名场景 + 纯数学扫描等价 + 稳态零分配；
- 既有渲染断言（`ParticleWorldTests` / `SandStoneParticleTests` / `WaterParticleTests` / `WeatherParticleTests` / `LightningParticleTests` / `BiomassParticleTests` / `AquaticParticleTests` / `GrassParticleTests` / `SimulationFrameTests`）全部就地更新为 POD 缓冲，期望值仍由源调色板与 Unity 算式亲笔算出。

**当时仍未完成、现已补齐（阶段 2b / 3，2026-10-17）**：

- ~~`CreatureRenderData.PlaceholderColor` 仍是 Unity `Color32`~~ ⇒ 已改为 `RenderColor`（采集边界转换，见 2b）；
- ~~采集 / 光栅化仍由主线程同步调用、worker 线程尚未引入~~ ⇒ `SimulationWorker` 已在阶段 3 落地，采集与光栅化都跑在 worker 线程上；
- `SpeedController` 耗时回喂、`Application.targetFrameRate`、`Time.deltaTime` **有意留在主线程**（倍速计划是主线程职责，worker 只收「跑几个 tick」；见阶段 3 第 2 条）。

#### 2b. 其余 worker-safe 数据 / 配置 —— **已完成（2026-10-17）**

1. ✅ 渲染 DTO 的 `Color32` 已隔离：`CreatureRenderData.PlaceholderColor` 改为项目自有的 `RenderColor`，物种授权的 `Color32` 在**采集边界**转换（`RenderColorConversion.FromColor32`）。`SimulationFrame` 一族（含 `LightSettings` 命令载荷）现在不含任何 Unity 类型。
2. ✅ 热参数 / 随机刻 / 光照配置走命令：驱动采样成 `SimulationTickSettings` / `LightSettings` 两个**纯值**快照，变化时才 `Submit`；worker 在建世界与收到命令时把它们写回 `HeatMap` / `LightController`。
3. ✅ `CreatureWorld.Step` 与 `ParticleWorld.StepLogic` 同在 worker 上、顺序不变（逻辑帧之后、高速转发之前）；动物死亡物质化也在 worker（`CreatureDeathMaterializer` 里的 `Mathf.Floor` 已换成等价的本地位运算）。
4. ✅ `HighSpeedSession` 与其 world 同线程；主线程只消费 `HighSpeedFrame`（倍速钉档留在主线程的 `SpeedController` 上）。

验收：worker 路径上（`SimulationWorker` / `SimulationFrameCollector` / `ParticlePixelBuffer` / `CreatureWorld` / `CreatureDeathMaterializer`）**没有** Unity 对象或 API 调用；世界诊断经 `ParticleWorld.MessageSink` → worker 队列 → 主线程日志。

### 阶段 3：启用常驻 worker —— **已完成（2026-10-17）**

1. ✅ `SimulationWorker`：一条私有 `Thread`（不用 thread-pool），有界请求环（2 槽，满载即丢请求、绝不让主线程等待）+ 回执环（2 槽，回喂实测耗时）、命令按批在第一个运动 tick 前抽干、至多一帧 / 批、故障通道（首个异常胜出 + 就地停摆 + 主线程报错）、`Stop()` 请求 + `Join` 生命周期、`RunBatchInline()` 供确定性重放。
2. ✅ 主线程 driver 改为：采样 `Time.deltaTime` / `Application.targetFrameRate` / Inspector 配置 → `Submit` 命令 → `SpeedController` 计划 tick 数 → 非阻塞 `RequestBatch` → 消费新帧 → `ParticleRenderer` 在主线程上传纹理。驱动不再持有任何活世界引用。
3. ✅ 一批至多发布一帧（高速模式多 tick 也只发最后稳定状态）。
4. ✅ 生命周期：`Awake` 建 worker（世界与第一帧同步建好）并启线程；`OnEnable` 兜底重启；`OnDisable`/`OnDestroy` 停止并 `Join`；`Resize` / 阶段切换经命令完成并换世代 + 立即发布。
5. ✅ Unity 资源创建、纹理上传、相机、uGUI、音频与日志全部留在主线程。

**已过验收（功能与确定性）**：

- `SimulationWorkerTests`（11 例）：构造即发布完整首帧、命令在同批内按入队顺序应用、探针在命令边界采样（此后 tick 挪走物体即读不到）、一批至多一帧、暂停不推进但仍抽干命令与上屏、`Resize` 换代 + 立即发布、请求槽满载非阻塞失败、世界诊断进队列、worker 跑在**另一条线程**上、`Stop`/`Start` 可重启、**分线程与同步（`RunBatchInline`）同一命令日志 ⇒ 像素帧逐位一致**。
- `SimulationFrameTests`（11 例）：`Submit` / `Advance` / `CurrentFrame` 边界的既有契约在异步 worker 下重写并全绿；新增发布器松弛度（3 槽：已发布帧挺过两次后续发布才被复用）与「同一帧连读两次逐位相同、`Current` 不回退」。
- 受影响的既有类全绿：渲染光栅化、粒子类型、声音驱动 / 控制器、相机平移、投放选择、光照面板步进、动物世界 / AI 等。

**仍未做（阶段 4）**：Thread Profiler 上的主线程 / worker 耗时分摊实测，以及真实窗口 resize、高速结算、反复进出 Play 的压力矩阵。架构本身的正确性已验证，性能收益需在 Unity Profiler 里量。

### 阶段 4：性能与压力验证

- 固定 seed + 录制命令日志：同步基线与 worker 模式逐 motion tick hash 一致。
- 240×135 宽水体、320×180 sandbox、暂停画笔、phase resize、高速完成、禁用/销毁对象、反复进入 Play Mode。
- Unity Profiler：主线程、worker、render upload 分别量化；记录 input-to-visible tick/frame 延迟。
- 仅在阶段 1 确认正确后再评估 `stepsPerPresent` / worker raster cadence；不要把架构改动与显示降频混为一个变量。

## 风险与约束

1. `ParticleWorld` 当前从 UI、renderer、audio、creature renderer 被直接读取或写入；若保留任何一个可变引用，线程化必然竞态。
2. 双缓冲 world 本身不合适：复制 `Cell[]`、heat、humidity 每 tick 的成本与内存带宽会抵消收益。以 render/audio/HUD DTO 发布，world 保持 worker 私有。
3. ~~`ParticlePixelBuffer.Fill` 当前读取邻格、湿度、热量与 Unity `Color32`；必须先改成 worker-safe、只读输入，不能主线程和 worker 同时读活世界。~~ **已解决（阶段 2a）**：光栅化已是不含 Unity 依赖的纯值实现，输入是 worker 私有的网格 / 场与派生纯值表；剩下的「同一时刻只能有一个读者」仍是阶段 3 的线程模型约定。
4. ~~当前 `ParticleLightDriver.Update` 每帧直接写 `world.Light`~~ **已解决（阶段 1/3）**：它只在纯值快照变化时提交 `SetLightSettings` 命令。
5. ~~音频监视器「读后清账」~~ **已解决（阶段 1/3）**：抽干改在 worker 的发布侧（`CollectAudio`），消费端只读 `AudioFrame`、无副作用。
6. Unity 编辑器 domain reload / 场景关闭必须 join worker；不允许依赖 finalizer 或后台线程自然退出。**已落实**：驱动在 `OnDisable` / `OnDestroy` 停止并 `Join`。
7. 单 worker 不改变结果；多 worker 处理一个 world 会破坏扫描顺序、RNG 消耗、同帧热/湿度可见性与确定性。**已验证**：`SimulationWorkerTests` 的「分线程 vs `RunBatchInline` 逐位一致」。
