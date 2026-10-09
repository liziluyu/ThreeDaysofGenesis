# 性能问题清单（审计 2026-10-06，覆盖 2bd8545..28ec48a 共 12 提交）

方法：代码走查 + 仓库外桩工程实测（`E:/tmp/tap_audit_fields` / `tap_audit_equiv` / `tap_audit_driver`，net9 CoreCLR 直驱；绝对耗时与 Unity Mono 不可比，**只有 on/off 关系与结构结论有效**）。正确性结论另述：本批无正确性 bug（4 场逐位对拍全等，含 900 周期生产火场景与 200 种子×200 周期 fuzz）。

**本清单只列问题与证据；已修项在条目末尾以 `✅ 已修（2026-10-07）` 标注，未动的保留原文。**
（本节修订：2026-10-07，对应提交见文末对照表。）

## P1 热传导不动点跳过：生产场景几乎不可能命中（收益名不副实）

- 位置：`HeatMap.cs:639-650`（跳过判据）+ `ParticleWorld.cs:491/1066`（版本写入）。
- 实测：
  - 精确 float 不动点的收敛时长——16×16 全空气场：封闭参数 1263 逻辑刻、生产参数 16388 逻辑刻；200×100 bench 尺寸 **20524 逻辑刻**（t=10000 时 max|Δ| 仍有 4e-27）。20 Hz 下 ≈ **17 分钟完全静止**才首次可跳过。
  - **任何一次 Swap 都打断跳过**：`ParticleWorld.Swap` 无条件 `_writeVersion++`——实测交换两个相同空气格（typeId/Q/湿度全不变）后下一帧立即退出跳过。
  - 单粒落沙世界：下落期间 0/40 帧跳过，落定后 40/40。即「有运动就不跳」。
  - SampleScene 出厂暗相 600 刻（30 秒），比 20524 刻的收敛需求短 30 倍以上——**文档（docs §「不动点跳过」）与提交 4908449 声称的「昼夜暗相收益」不成立**；LogicBench 的 −35% 取自零热量零写入的合成场。
- 修法方向（两条独立）：
  1. 低风险：`Swap` 只在 `_cells[a].typeId != _cells[b].typeId` 时 `_writeVersion++`（热 pass 的输入——热容/σ/ε/天空暴露——只由 typeId 决定；Q 与湿度各有自己的版本号）。仅此一条就能让「静止但有水/气自流」的场重新可跳过。
  2. 把「精确不动点」放宽为相对阈值（如 max|ΔQ| < 1e-6·max|Q|）后冻结场——会破坏逐位等价契约，需评估。
  3. 无论修不修，文档措辞要改：不要声称暗相有收益。
- ✅ 已修（2026-10-07）：按修法 1——`ParticleWorld.Swap` 只在两端 `typeId` 不同时推进 `_writeVersion`（同类型自流不再打断不动点）；
  并按 P5 把 `HeatMap.Step` 的恒等判定与入参脏记录解耦（`_lastStepWasIdentity = 本趟无任一格变化`，`_anyDirty` 单独 OR）——
  注入后不再白跑一趟才重新进入跳过。修法 2（相对阈值）**未采纳**：会破坏逐位等价契约。
  文档措辞已按修法 3 改写（docs §7「不动点跳过」明说「昼夜暗相吃不到跳过收益」、`AGENTS.md` 同步）。

## P2 逻辑帧混合扫描：热场每帧脏时比全扫更慢（0.61× ~ 0.80×）

- 位置：`ParticleWorld.cs` `FlushTemperatureDirty`/`FlushDirty`（~:1651）+ `ScanLogicSleeping`。
- 实测（256×128，min-of-5）：完整逻辑帧（含场 pass）休眠 on vs off——

  | 场景 | on | off | 比值 |
  |---|---|---|---|
  | 静止石头（热场每帧脏） | 1.371ms | 0.978ms | **0.71×** |
  | 静止石头（热场已不动点） | 0.253ms | 0.650ms | 2.57× |
  | 静水 | 1.217ms | 0.968ms | **0.80×** |
  | 落沙柱 | 1.187ms | 0.728ms | **0.61×** |
  | 草地+火 | 1.258ms | 0.889ms | **0.71×** |

- 根因（已定位）：
  1. `FlushDirty` 把每个脏格周围 Chebyshev 半径 2 的球**按 16×16 chunk 整块置位**——一次真实 StepLogic 后候选位图 65~73% 置位，混合扫描的「整 chunk 全 0 快路径」几乎不命中；
  2. `FlushTemperatureDirty` 自身是 O(W·H) 线性扫两张脏 byte 表（~0.27ms），且只在休眠开时运行——on/off 不在同一基准上。
  3. 只有当热 pass 进入不动点跳过（见 P1）时脏集为空、逻辑帧才真正赢（2.57×，与提交声称的 −82% 同源）。
- 每周期总账（3 运动 + 1 逻辑）仍是正收益：1.08×（草地火）~ 6.63×（不动点静止场）；运动帧单项 3.7×~194×。**混合扫描的正确性无问题**（逐位对拍通过），只是收益依赖 P1。
- 修法方向：脏格按写入唤醒同一条通道（生产表 87 格并集）置位、而不是整 chunk；或给逻辑帧单独维护温度候选位图；`FlushDirty` 改脏索引紧凑列表（int[] + count）替代全表扫描。
- ✅ 已修（分两步，2026-10-07 / 2026-10-09）：
  ① 两张场的脏记录改**紧凑脏索引列表**（`int[]` + count，`MarkDirty` 与 Burst 内核在 `dirty[]` 0→1 时登记），`FlushTemperatureDirty` 与 `ClearDirty` 从 O(W·H) 降为 O(脏格数)——这半一次到位；
  ② 逐格唤醒球走了弯路：`6c14f1b` 的第一版用逐格 `MarkOffsets` 刷球，复审实测**负收益**（热场每帧脏时 FlushDirty 0.098→0.40ms、混合扫描 on/off 从 0.74× 恶化到 0.45×、armed chunk 数不变），`5ccc63c` 返工为**密度自适应三档**——稀疏（≤格数/32）逐格刷球（掩码出循环、每行一次 word 级 OR）、密集（≤格数/2）回退 chunk 整块置位、饱和一趟全表填充。
  复审实测（net9 桩，256×128）：静止石头场 FlushDirty 0.417→0.0146ms（28.6×）、完整逻辑帧 on/off 0.51×→0.98×、稀疏脏集武装面 512bit→100bit；on/off 逐位等价（320 周期混合场景 + 中途切换 + 200 种子 fuzz + 900 周期生产火 + 6 场景跨版本摘要）。
  候选集仍是真实依赖集的超集（球 ⊇ `TryThermalDrift` 的 `x ± 2` 读距）。**注意**：上述数字仍是桩口径，Unity Mono/Burst 下的绝对值未复核。

## P3 SpeedController：预算模型的三个失配（非崩溃，语义/配置风险）

- **P3a 1× 与旧累加器不再逐帧等价（文档失实）**：`ParticleSimulationDriver.cs:245-275` + `SpeedController.cs:283-310`。桩实测 6200 帧：1× 下 0.5s 卡顿帧——每 tick 0.1ms 时新代码追 29 tick（旧固定累加器上限 4），每 tick 2ms 时只跑 4 tick 且丢 25；重载（dt=20ms、每 tick 15ms）下新代码交付 0.833× 时间流速、旧代码 1.000×。`docs:979` 与 `AGENTS.md:13` 的「默认手动 1× 与旧 MaxStepsPerFrame 累加器逐帧逐位一致」缺「预算未截断时」限定，应补限定或改回有限追帧语义。
  ✅ 已修（2026-10-07，仅措辞）：docs §7/§10、`AGENTS.md` 与 `SpeedControllerTests.DefaultManualOneTimesMatchesLegacyAccumulator` 均已补「**预算未截断时**」，`PlanMotionTicks` 的 remarks 写明超预算走丢账。**未改回有限追帧**（丢账是刻意设计）。
- **P3b AIMD 反馈在超预算时饱和，自适应档失效**：`SpeedController.cs:283-352`。桩实测（60fps、预算 8.33ms、每 tick 2.0ms）：3000 帧后目标倍率仍锁 16、实际只交付 3.94 tick/帧、累计丢 31464 tick——`_perTickMs` EMA 只反映已交付吞吐、不含被 cap 丢弃的需求，降档水位线永远够不着。修法：把需求折进反馈（cycleCost = perTick × min(需求, cap)，或用「目标÷实测」作超预算指标）。
  ✅ 已修（2026-10-07）：`UpdateAdaptive` 按**目标倍率**折算周期成本——`perTick × 周期长度 × min(目标倍率, 预算可买 tick 数)`，
  需求超预算时估计值饱和到「预算 × 周期」这一档、必跨降档水位线；`SpeedControllerTests.AdaptiveDropsWhenDemandExceedsBudget` 锁死该回归。
- **P3c `MotionTicksPerCycle=3` 硬编码**：`SpeedController.cs:49/339`。改 `_motionRate`（如 120/20）后实际一周期 6 tick 而 AIMD 按 3 折算，预算门槛失准 2×。应由驱动把 `LogicTicksPerMotionTick` 注入。
  ✅ 已修（2026-10-07）：`MotionTicksPerCycle` 常量删除，周期长度由 `PlanMotionTicks(..., logicTicksPerMotionTick)` 每帧注入
  （`ParticleSimulationDriver` 传 `settings.LogicTicksPerMotionTick`）；`SpeedControllerTests.AdaptiveThresholdUsesInjectedCycleLength` 用同一成本信号注入 1 vs 3 验证阈值跟着变。
- **P3d 预算基准是固定 60fps，不是真实帧时间**：vSync 关、`targetFrameRate` 未设置 ⇒ 恒走 60 兜底 = 8.33ms/帧绝对上限；144Hz 下是帧时间的 120%。行为本身可接受（绝对上限语义），但 tooltip 与 docs §10 的「帧时间比例」措辞与实现不符。
  ✅ 已修（2026-10-07，仅措辞）：`_speedBudgetFraction` 的 tooltip 与 `SpeedBudgetFraction` 注释、docs §10 均改为「**名义帧时间（1000 / 目标帧率，不限帧按 60 兜底）的绝对上限**」；行为未动（按绝对上限语义保留）。

## P4 打包体积/构建：IL2CPP 把编辑器 MCP 桥 AOT 进 player（既有问题被放大）

- 位置：`ProjectSettings.asset:592-593`（358ad7c）+ `Assets/Plugins/NuGet/*.dll.meta`。
- 实测（构建产物 `Builds/Dev261006_2144`）：player 内含 McpPlugin / Microsoft.CodeAnalysis（Roslyn）/ SignalR 等 136 个程序集，`RuntimeInitializeOnLoads.json` 注册了 MCP 的 `MainThreadInstaller.Init`（BeforeSceneLoad）——**打包版在首个场景前执行编辑器桥代码**；GameAssembly.dll 44.7MB、global-metadata.dat 9.76MB。Mono 构建同样包含（既有问题），IL2CPP 把它 AOT 化，放大构建时间/体积并新增缺 AOT 代码的 ExecutionEngineException 面。player 实测能正常启动、无外连，未观察到运行期故障。
- ~~修法：`Assets/Plugins/NuGet/*.dll` 的 .meta platformData 改仅 Editor（或移入 Editor 专用目录），同步砍体积与构建时间。~~
- ❌ **已回退（2026-10-08）：meta 改 Editor-only 这条路走不通，打包直接失败。**
  `b23663d` 把 40 个 `*.dll.meta` 改成 Editor-only 后，下一次 Standalone（IL2CPP）打包在脚本编译阶段中止：
  `Error building Player because scripts had compiler errors`，日志 2118 条 `CS0246`/`CS0234`，**全部落在
  `com.IvanMurzak.Unity.MCP.Runtime`**（`Runtime/Converter` 1287、`Runtime/Data` 258、`Runtime/Extensions` 168 …），
  报的是 `Reflector` / `SerializedMember` / `Logs` / `ILogger` / `JsonElement` / `R3.ReactiveProperty` 等找不到类型。
  原因：该 asmdef 不限定平台（只被 `UNITY_MCP_READY` 的 `defineConstraints` 把关），**player 仍要编译它**，
  而它 `overrideReferences` 里显式引用的 `McpPlugin.dll` / `ReflectorNet.dll` / `R3.dll` / `System.Text.Json.dll`
  被改成 Editor-only 后从 player 的引用集里消失 ⇒ 整组类型解析失败。即：**只要引用方 asmdef 还在 player 目标里，
  被引用 DLL 的平台裁剪就不可能单独生效**，只会把报错从「静默打包进去」变成「打包失败」。
  另外这条路本身也不稳定：包内 `NuGetPluginConfigurator.ConfigureAll()` 会按 `NuGetConfig.Packages` 的
  `includeInBuild` 在每次 restore 时把 PluginImporter 写回「include in build」，手改的 meta 会被插件覆写。
- 待定的真修法（都需要动程序集纳入，不是 meta 能解决）：把 MCP 包整体排除在 player 之外——例如发布打包用不含该包的
  工程副本，或把 `com.ivanmurzak.unity.mcp` 嵌到 `Packages/` 下本地化后把它的 Runtime/Editor asmdef 限定 Editor 平台；
  否则就是接受现状（player 内含 MCP，~1.13GB Development 构建、`GameAssembly.dll` 45.8MB）。

## P5 微项（可顺手清理，不阻塞）

- `FireParticle.OnLogic`：`HasAttachment` 与 ③ 八邻扫描各跑一遍同一邻居表，最坏情形占该格成本约一半（实测 27ns/68ns）。把 attached 折进 ③ 的单趟循环即可；注意 ② 注热相对扫描的位置不能动（同帧可见性契约）。
  ✅ 已修（2026-10-07）：附着判定折进同一趟八邻扫描（`HasAttachment` 删除，`ParticleTags` 每邻只查一次，标签判据与旧实现同源同序）；
  注热仍排在该趟扫描**之后**（本趟只读类型 / 标签、只写本格 Q，两者互不观测 ⇒ 逐位等价），`FireParticleTests` 与 `LogicScanTests`（火注热同帧可见性）全过。
- `HeatMap` 不动点判定把入参 anyDirty 一起 OR 进返回值：每次注入后多跑一趟才重新进入跳过（每次仅多 1 趟，无害）。
  ✅ 已修（2026-10-07）：`HeatStepKernel` 只返回「本趟是否有格变化」，宿主把新脏格 OR 进 `_anyDirty`、`_lastStepWasIdentity = changed == 0`。
- URP 重序列化（28ec48a）落盘的 prefiltering 值是计算剥离集而非默认值：构建期 ShaderPreprocessor 会重算自愈，但编辑器内变体收集吃这份陈旧集——启用 Forward+/附加光阴影/SSAO/软阴影后需重新生成，否则渲染异常易被误判为新 bug。
  ⚠️ 仍开放（2026-10-07）：本次未动。
- `ParticleRenderer.cs:263` 运行期 quad 用 `HideFlags.DontSaveInEditor`，同链 mesh/材质/纹理用 `DontSave`——修复目标已达成（构建产物无 ParticleGridQuad），仅旗标不统一。
  ✅ 已修（2026-10-07）：运行期 quad 改 `HideFlags.DontSave`，与同链 mesh / 材质 / 纹理一致（编辑器与 player 都不落盘）。

## 已验证无碍（不再复查）

- 运动休眠正确性：on/off 逐位等价 4 场对拍（含 900 周期生产火、4 万周期 fuzz）、切换与异常路径全过；每周期 0 字节分配。
- Burst 场内核与旧 managed 算法 60+60 随机用例 bitwise 全等（managed 分支）；IL2CPP player 实测 Burst DirectCall + FloatMode.Strict 已注册生效。
- SpeedController 边界：NaN/∞/负 dt、不雪球（cap 截断即清账）、暂停冻结、EMA 配对、16× 量化无漂移，全部通过。
- 混合扫描隔离测速 10×~109× 与提交声称方向一致（受限条件见 P2）。

## 本次修复提交（2026-10-07）

| 提交 | 主题 | 对应条目 |
|---|---|---|
| `b23663d` | chore(plugins): NuGet 依赖 DLL 平台限定为 Editor-only | P4（**2026-10-08 回退**：该改动使打包编译失败） |
| `6c14f1b` | perf(simulation): 脏格改紧凑索引列表 + 温度唤醒球逐格置位 | P1 / P2（**逐格球部分复审实测负收益，由 `5ccc63c` 返工**） |
| `34cf65b` | fix(simulation): SpeedController 周期成本按目标需求折算、周期长度由驱动注入 | P3a / P3b / P3c / P3d |
| `6264fa7` | feat(game): 速度只由高速模式改变——普通编辑恒 1×、高速钉满档 | P3（档位语义：生产不再有玩家可调倍速） |
| `04c6837` | perf(simulation): 火逻辑帧八邻单趟扫描合并附着判定 | P5① |
| `26b2ba0` | chore(rendering): 运行期 quad 旗标统一为 HideFlags.DontSave | P5④ |
| `5ccc63c` | perf(simulation): FlushDirty 改密度自适应三档 | P2（返工，实测收益见 P2 条目） |

未随代码一起复核的项：P2/P5① 的 Unity Mono/Burst 实机耗时（全部为 net9 桩口径）、P5③ 的 URP prefiltering 陈旧集（未动）。

**P4 回退（2026-10-08 还原、2026-10-09 落提交）**：`b23663d` 的 Editor-only meta 改动已全部还原（40 个 `*.dll.meta` 与 `b23663d^` 逐字节一致），
Standalone/IL2CPP 打包复测 `Succeeded errors=0`（Development，1.13GB；player 仍含 MCP 桥、`GameAssembly.dll` 45.8MB）。
P4 的打包体积收益仍未落地——meta 路线已证否，见上文 P4 条目。
