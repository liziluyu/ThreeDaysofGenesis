# 测试与 QA

## 程序集与规模

EditMode 程序集：`Assets/Tests/EditMode/TapTap2026.Tests.EditMode.asmdef`（引用 Simulation + Game + TestRunner + NUnit，`includePlatforms: Editor`；Game 引用只为渲染/驱动层测试）。Test Runner 报告的程序集总数 **665 个测试**（下列分文件清单是粗略参考，以 Test Runner 为准）：`ParticleWorldTests` 89 + `SandStoneParticleTests` 9 + `ThermalDriftTests` 7 + `WaterParticleTests` 26 + `WeatherParticleTests` 24 + `WetSoilParticleTests` 17 + `AquaticParticleTests` 27 + `GrassParticleTests` 13 + `FlowerParticleTests` 13 + `FireParticleTests` 34 + `MotionSleepTests` 5 + `LogicScanTests` 4 + `SpeedControllerTests` 12 + `HeatSkipTests` 5 + `HighSpeedSessionTests` 13 + `CameraPanTests` 5 + `DialogueRunnerTests` 11 + `GameFlowTests` 35 + `GameSaveProgressTests` 6 + `LevelSelectControllerTests` 14 + `LevelSelectSceneWiringTests` 6 + `TreeParticleTests` 49 + `GroundwaterTests` 14 + `HumusSoilParticleTests` 13 + `CreatureWorldTests` 13 + `CreatureAiTests` 42 + `BiomassParticleTests` 10 + `CreatureBiomassTests` 10 + `LightningParticleTests` 11 + `CreatureSpriteFacingTests` 7 + `BrushOutlineTests` 6 + `SceneWiringTests` 10 + `SoundControllerTests` 13 + `LiquidFlowSoundDriverTests` 4 + `ParticleCountMonitorTests` 8 + `MotionMonitorTests` 8 + `FireSoundDriverTests` 7 + `LightningSoundDriverTests` 5 + `FallingSandSoundDriverTests` 8 + `CreatureArtDataTests` 1 + `FishShallowWaterTests` 8 + `BirdCreatureTests` 16 + `ParticleSelectionTests` 13 + `ParticleSelectionPanelTests` 7 + `LightPanelStepTests` 5。

PlayMode：`testables` 未配置、`playModeTestRunnerEnabled: 0`，暂不支持。覆盖率未配置。

## 测试约定

- **隔离类型表**：`new ParticleWorld(w, h, seed, types)` 四参构造 + 测试专用 `ParticleType[]`，不污染全局注册表。
- **生产注册路径**（`RegisterAll` 回填）：跨类型相变与 id 引用依赖 `ParticleIds` 回填的测试必须走生产表——集中在 `SandStoneParticleTests`/`WeatherParticleTests`/`WetSoilParticleTests`/`AquaticParticleTests`/`FireParticleTests`/`GrassParticleTests`/`FlowerParticleTests`/`TreeParticleTests`/`LightningParticleTests`/三个声音驱动测试/`ParticleSelectionTests`（`ParticleCountMonitorTests`/`MotionMonitorTests` 用隔离表）。
- EditMode 里组件不会自己跑 `Awake`（没有 `ExecuteAlways`），需要初始化状态的测试用反射显式调一次 `Awake`。
- 场景接线（`SceneWiringTests` 读 `MainScene`、`LevelSelectSceneWiringTests` 读 `LevelSelect`）用 `SerializedObject` 读真场景。
- **期望的 `Debug.LogError` 用 `LogAssert.Expect(LogType.Error, new Regex(...))` 对齐**，不要靠 `LogAssert.ignoreFailingMessages` 整类吞掉（后者会连真错误一起吞）。
- **字体是只读区**：测试（含场景接线测试）**不得**断言或改写任何字体设置——不断言 `m_fontAsset` / 字号 / 字体资产内容，不得 `AssetDatabase.CreateAsset` 字体资产。场景接线测试只钉**接线与显隐**，不碰字体（见 `docs/modules/ProjectSetup.md`「字体」）。
- 音频测试只断言纯逻辑、目标响度与计数，**不断言主观听感**。
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
