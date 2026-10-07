# 性能问题清单（审计 2026-10-06，覆盖 2bd8545..28ec48a 共 12 提交）

方法：代码走查 + 仓库外桩工程实测（`E:/tmp/tap_audit_fields` / `tap_audit_equiv` / `tap_audit_driver`，net9 CoreCLR 直驱；绝对耗时与 Unity Mono 不可比，**只有 on/off 关系与结构结论有效**）。正确性结论另述：本批无正确性 bug（4 场逐位对拍全等，含 900 周期生产火场景与 200 种子×200 周期 fuzz）。

**本清单只列问题与证据，不含修复。**

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

## P3 SpeedController：预算模型的三个失配（非崩溃，语义/配置风险）

- **P3a 1× 与旧累加器不再逐帧等价（文档失实）**：`ParticleSimulationDriver.cs:245-275` + `SpeedController.cs:283-310`。桩实测 6200 帧：1× 下 0.5s 卡顿帧——每 tick 0.1ms 时新代码追 29 tick（旧固定累加器上限 4），每 tick 2ms 时只跑 4 tick 且丢 25；重载（dt=20ms、每 tick 15ms）下新代码交付 0.833× 时间流速、旧代码 1.000×。`docs:979` 与 `AGENTS.md:13` 的「默认手动 1× 与旧 MaxStepsPerFrame 累加器逐帧逐位一致」缺「预算未截断时」限定，应补限定或改回有限追帧语义。
- **P3b AIMD 反馈在超预算时饱和，自适应档失效**：`SpeedController.cs:283-352`。桩实测（60fps、预算 8.33ms、每 tick 2.0ms）：3000 帧后目标倍率仍锁 16、实际只交付 3.94 tick/帧、累计丢 31464 tick——`_perTickMs` EMA 只反映已交付吞吐、不含被 cap 丢弃的需求，降档水位线永远够不着。修法：把需求折进反馈（cycleCost = perTick × min(需求, cap)，或用「目标÷实测」作超预算指标）。
- **P3c `MotionTicksPerCycle=3` 硬编码**：`SpeedController.cs:49/339`。改 `_motionRate`（如 120/20）后实际一周期 6 tick 而 AIMD 按 3 折算，预算门槛失准 2×。应由驱动把 `LogicTicksPerMotionTick` 注入。
- **P3d 预算基准是固定 60fps，不是真实帧时间**：vSync 关、`targetFrameRate` 未设置 ⇒ 恒走 60 兜底 = 8.33ms/帧绝对上限；144Hz 下是帧时间的 120%。行为本身可接受（绝对上限语义），但 tooltip 与 docs §10 的「帧时间比例」措辞与实现不符。

## P4 打包体积/构建：IL2CPP 把编辑器 MCP 桥 AOT 进 player（既有问题被放大）

- 位置：`ProjectSettings.asset:592-593`（358ad7c）+ `Assets/Plugins/NuGet/*.dll.meta`。
- 实测（构建产物 `Builds/Dev261006_2144`）：player 内含 McpPlugin / Microsoft.CodeAnalysis（Roslyn）/ SignalR 等 136 个程序集，`RuntimeInitializeOnLoads.json` 注册了 MCP 的 `MainThreadInstaller.Init`（BeforeSceneLoad）——**打包版在首个场景前执行编辑器桥代码**；GameAssembly.dll 44.7MB、global-metadata.dat 9.76MB。Mono 构建同样包含（既有问题），IL2CPP 把它 AOT 化，放大构建时间/体积并新增缺 AOT 代码的 ExecutionEngineException 面。player 实测能正常启动、无外连，未观察到运行期故障。
- 修法：`Assets/Plugins/NuGet/*.dll` 的 .meta platformData 改仅 Editor（或移入 Editor 专用目录），同步砍体积与构建时间。

## P5 微项（可顺手清理，不阻塞）

- `FireParticle.OnLogic`：`HasAttachment` 与 ③ 八邻扫描各跑一遍同一邻居表，最坏情形占该格成本约一半（实测 27ns/68ns）。把 attached 折进 ③ 的单趟循环即可；注意 ② 注热相对扫描的位置不能动（同帧可见性契约）。
- `HeatMap` 不动点判定把入参 anyDirty 一起 OR 进返回值：每次注入后多跑一趟才重新进入跳过（每次仅多 1 趟，无害）。
- URP 重序列化（28ec48a）落盘的 prefiltering 值是计算剥离集而非默认值：构建期 ShaderPreprocessor 会重算自愈，但编辑器内变体收集吃这份陈旧集——启用 Forward+/附加光阴影/SSAO/软阴影后需重新生成，否则渲染异常易被误判为新 bug。
- `ParticleRenderer.cs:263` 运行期 quad 用 `HideFlags.DontSaveInEditor`，同链 mesh/材质/纹理用 `DontSave`——修复目标已达成（构建产物无 ParticleGridQuad），仅旗标不统一。

## 已验证无碍（不再复查）

- 运动休眠正确性：on/off 逐位等价 4 场对拍（含 900 周期生产火、4 万周期 fuzz）、切换与异常路径全过；每周期 0 字节分配。
- Burst 场内核与旧 managed 算法 60+60 随机用例 bitwise 全等（managed 分支）；IL2CPP player 实测 Burst DirectCall + FloatMode.Strict 已注册生效。
- SpeedController 边界：NaN/∞/负 dt、不雪球（cap 截断即清账）、暂停冻结、EMA 配对、16× 量化无漂移，全部通过。
- 混合扫描隔离测速 10×~109× 与提交声称方向一致（受限条件见 P2）。
