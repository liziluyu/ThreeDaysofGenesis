# 测试与 QA

## 程序集与规模

EditMode 程序集：`Assets/Tests/EditMode/TapTap2026.Tests.EditMode.asmdef`（引用 Simulation + Game + TestRunner + NUnit，`includePlatforms: Editor`；Game 引用只为渲染/驱动层测试）。Test Runner 报告的程序集总数 **668 个测试**（下列分文件清单是粗略参考，以 Test Runner 为准；阶段 1 新增 `SimulationFrameTests` 10 个用例，尚未全量刷新计数）：`ParticleWorldTests` 89 + `SandStoneParticleTests` 9 + `ThermalDriftTests` 7 + `WaterParticleTests` 26 + `WeatherParticleTests` 24 + `WetSoilParticleTests` 17 + `AquaticParticleTests` 27 + `GrassParticleTests` 13 + `FlowerParticleTests` 13 + `FireParticleTests` 34 + `MotionSleepTests` 5 + `LogicScanTests` 4 + `SpeedControllerTests` 12 + `HeatSkipTests` 5 + `HighSpeedSessionTests` 13 + `CameraPanTests` 5 + `DialogueRunnerTests` 11 + `GameFlowTests` 35 + `GameSaveProgressTests` 6 + `LevelSelectControllerTests` 14 + `LevelSelectSceneWiringTests` 6 + `TreeParticleTests` 49 + `GroundwaterTests` 14 + `HumusSoilParticleTests` 13 + `CreatureWorldTests` 13 + `CreatureAiTests` 42 + `BiomassParticleTests` 10 + `CreatureBiomassTests` 10 + `LightningParticleTests` 11 + `CreatureSpriteFacingTests` 7 + `BrushOutlineTests` 6 + `SceneWiringTests` 10 + `SoundControllerTests` 13 + `LiquidFlowSoundDriverTests` 4 + `ParticleCountMonitorTests` 8 + `MotionMonitorTests` 8 + `FireSoundDriverTests` 7 + `LightningSoundDriverTests` 5 + `FallingSandSoundDriverTests` 8 + `CreatureArtDataTests` 1 + `FishShallowWaterTests` 8 + `BirdCreatureTests` 16 + `ParticleSelectionTests` 15 + `ParticleSelectionPanelTests` 8 + `LightPanelStepTests` 5 + `SimulationFrameTests` 11 + `RenderRasterizerTests` 7。

PlayMode：`testables` 未配置、`playModeTestRunnerEnabled: 0`，暂不支持。覆盖率未配置。

## 测试约定

- **隔离类型表**：`new ParticleWorld(w, h, seed, types)` 四参构造 + 测试专用 `ParticleType[]`，不污染全局注册表。
- **生产注册路径**（`RegisterAll` 回填）：跨类型相变与 id 引用依赖 `ParticleIds` 回填的测试必须走生产表——集中在 `SandStoneParticleTests`/`WeatherParticleTests`/`WetSoilParticleTests`/`AquaticParticleTests`/`FireParticleTests`/`GrassParticleTests`/`FlowerParticleTests`/`TreeParticleTests`/`LightningParticleTests`/三个声音驱动测试/`ParticleSelectionTests`（`ParticleCountMonitorTests`/`MotionMonitorTests` 用隔离表）。
- EditMode 里组件不会自己跑 `Awake`（没有 `ExecuteAlways`），需要初始化状态的测试用反射显式调一次 `Awake`。
- 场景接线（`SceneWiringTests` 读 `MainScene`、`LevelSelectSceneWiringTests` 读 `LevelSelect`）用 `SerializedObject` 读真场景。
- **期望的 `Debug.LogError` 用 `LogAssert.Expect(LogType.Error, new Regex(...))` 对齐**，不要靠 `LogAssert.ignoreFailingMessages` 整类吞掉（后者会连真错误一起吞）。
- **字体是只读区**：测试（含场景接线测试）**不得**断言或改写任何字体设置——不断言 `m_fontAsset` / 字号 / 字体资产内容，不得 `AssetDatabase.CreateAsset` 字体资产。场景接线测试只钉**接线与显隐**，不碰字体（见 `docs/modules/ProjectSetup.md`「字体」）。
- 音频测试只断言纯逻辑、目标响度与计数，**不断言主观听感**。
- **帧边界测试**（`SimulationFrameTests`）只走公开边界：驱动经反射调一次 `Awake`，再用 `Advance(dt)` / `Submit(命令)` 与 `CurrentFrame` 驱动与观察，**不持有任何来自驱动的 `ParticleWorld` / `CreatureWorld` 句柄**。`FramePump`（在 `TestParticleFixtures.cs` 里）是把活世界按生产路径采成一帧已发布快照的测试装置，供渲染 / 音频 / 探针测试复用。
- **worker 是异步的**（阶段 3）：`Advance(dt)` 只负责「采样 + 非阻塞排一批请求 + 消费上一批成果」，因此**任何依赖「本批结果已发布」的断言都必须泵到新帧为止**（`SimulationFrameTests.AdvanceUntilNewFrame` / `SoundControllerTests.PumpUntilHighSpeed` 都是这个模式：循环 `Advance` + 按帧序号或状态判据收敛 + 超时 `Assert.Fail`），不能假设一次 `Advance` 恰好消费到本批结果。
- **worker 契约测试**（`SimulationWorkerTests`，阶段 3）直接用 `SimulationWorker`：命令有序 / 探针在命令边界采样、一批至多一帧、暂停不推进但仍抽干命令、`Resize` 换代、请求槽满载非阻塞失败、世界诊断进队列、`Stop`/`Start` 生命周期、**每阶段高速时长**（`SetHighSpeedLogicFrames` 只影响下一次 `EnterHighSpeed` 建的新会话、已退出的旧会话被丢弃、`≤ 0` 回到 setup 默认值），以及**确定性**——`RunBatchInline()` 在调用线程跑同一段 `Service`，与真线程路径同一命令日志逐位比对像素帧。发布器自身的松弛度（3 槽：已发布帧挺过两次后续发布）在 `SimulationFrameTests` 里确定性验证，async 侧只验证「同一帧连读两次相同、`Current` 不回退」。
- **worker 安全光栅化测试**（`RenderRasterizerTests`，阶段 2a）：像素缓冲是项目自有纯值 `RenderColor[]`，**期望值一律用 Unity 自己的实现亲笔算出**（`Color32.Lerp` / `Mathf.Lerp` / `(byte)(通道 × 系数)`）再与光栅化输出**逐字节**比对——因此「去掉 Unity 依赖」与「像素逐位不变」同时被钉住。覆盖：实心描边、液面高光、湿度云、热力叠加、夜晚亮度（只缩画了东西的像素）、纯数学对 Unity 实现的扫描等价（含边界外的 t）、以及光栅化 + 上传转换的**稳态零分配**（`GC.GetAllocatedBytesForCurrentThread` 前后差 0）。直接调 `ParticlePixelBuffer.Fill` 的测试必须传 `RenderColor` 缓冲；要与源调色板（`Info.Palette`，仍是 `Color32[]`）比对的用 `RenderColorConversion.ToColor32` / `FromColor32`。
- 动物物理断言一律用无 AI 的被动物理体 `PassiveBody`/`PassiveSwimmer`（物种自带 AI 后会自主移动）。

## 运行方式（按可用性排序）

1. MCP `tests-run`（编辑器在线；支持按程序集/类过滤；要求打开的场景无未保存修改）。
2. Test Runner 窗口。
3. 批处理（编辑器离线，前提是没有别的 Unity 实例开着本工程）：

```powershell
# 不要加 -quit——它会让编辑器在测试跑起来之前退出。去掉后 Unity 跑完自行退出：
# 退出码 0 = 全过，2 = 有失败，3 = 运行错误。
& 'D:\Unity\2022.3.62f1\Editor\Unity.exe' -batchmode -runTests `
  -projectPath . -testPlatform EditMode -testResults Temp\results.xml -logFile Temp\unity-editmode.log
```

## 增量测试纪律

- **增量测试是默认且唯一的跑法**：不动模拟内核（`Assets/Scripts/Simulation/`）时只跑受影响的测试类；动了内核也只跑受影响的类。
- **全量测试必须用户明确授权**：绝不擅自跑全量（无论 MCP `tests-run`、`TestRunnerApi`、Test Runner 窗口还是批处理；临时脚本 `script-execute` 同样不许拿来起全量）。全量一轮很贵、长时间占用编辑器。需要全量（发版前/大重构后/用户点名）时先问用户，跑完报结论。

## QA 期望

逻辑改动必须过 EditMode 测试；视觉/玩法改动进 Play 模式实际观察。
