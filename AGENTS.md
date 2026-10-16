# Repository Guidelines

## Project Overview

TapTap2026 是一个 **Unity 2022.3.62f1** 项目（URP 14.0.12 + 2D Renderer，2D 模式）：一个**落沙类粒子模拟游戏**——纯 C# 元胞自动机内核 + 单 quad 渲染 + 两级固定步长驱动 + 动物实体层 + 对话/阶段流程，配 707 个 EditMode 测试。

## 分层（程序集边界即架构边界）

- `TapTap2026.Simulation`（`Assets/Scripts/Simulation/`）：纯模拟，不引用任何渲染对象。
- `TapTap2026.Game`（`Assets/Scripts/Driver/` + `Assets/Scripts/Rendering/`）：引用 Simulation。
- `TapTap2026.Editor`（`Assets/Scripts/Editor/`，仅 Editor 平台）：编辑器工具。

## 模块文档（改哪个模块先读哪个）

| 模块 | 文档 |
|---|---|
| 粒子系统权威契约（作者规范、世界 API、渲染契约） | **`docs/ParticleTypeInterface.md`** |
| 模拟内核机制（Cell、热/湿度场、Burst、随机刻、运动休眠、确定性、作者约束） | `docs/modules/SimulationCore.md` |
| 各粒子类型行为细则 + 投放白名单 | `docs/modules/Particles.md` |
| 驱动、倍速、高速模式、ParticleEditor、声音、游戏流程薄壳 | `docs/modules/DriverAndEditor.md` |
| 渲染、相机→UI、场景界面层 | `docs/modules/Rendering.md` |
| 动物实体层（物理、AI、生态） | `docs/modules/Creatures.md`（权威设计：`docs/design-creature-ai.md`） |
| 对话系统 | **`docs/DialogueSystem.md`** |
| 游戏流程（阶段 JSON、胜利判定） | **`docs/GameFlow.md`** |
| 编辑器工具、场景结构、Unity-MCP | `docs/modules/EditorTooling.md` |
| 测试清单、约定、增量测试纪律 | `docs/modules/Testing.md` |
| 工程配置、版本、代码约定 | `docs/modules/ProjectSetup.md` |

改架构必须同步 `docs/ParticleTypeInterface.md`；改哪个模块同步哪个模块文档。

## 铁律（经常踩的）

- **确定性**：模拟只认 `world.Tick` 与世界 xorshift32；禁止 `UnityEngine.Random`/`Time.*`/`Input`。同种子同命令序列逐格可复现。
- **输入**：仅 legacy Input Manager；**禁止** `using UnityEngine.InputSystem;`。
- **测试**：只跑受影响的测试类（增量）；**全量测试必须用户明确授权**，绝不擅自跑（任何途径，含 `script-execute`）。详见 `docs/modules/Testing.md`。
- **场景**：`MainScene.unity` 是唯一 build 场景，破坏它 = 破坏唯一 build。
- **生成物**：`Library/`、`Temp/`、`obj/`、`Logs/`、`UserSettings/`、`*.csproj`、`*.sln` 永不读、改、提交。
- **Unity 版本**：只用 `D:\Unity\2022.3.62f1` 打开。
- 粒子类型注册只能**追加**（顺序是存档兼容性），清单在 `ParticleRegistration.cs`。
- 全局参数走 `ParticleSimulationSettings` 资产，不硬编码。

## 开发命令

```powershell
# EditMode 测试（批处理；前提：没有别的 Unity 实例开着本工程）。不要加 -quit。
# 退出码 0 = 全过，2 = 有失败，3 = 运行错误。
& 'D:\Unity\2022.3.62f1\Editor\Unity.exe' -batchmode -runTests `
  -projectPath . -testPlatform EditMode -testResults Temp\results.xml -logFile Temp\unity-editmode.log
```

编辑器在线时优先 MCP `tests-run`（可按类过滤），其次 Test Runner 窗口。跑法与过滤细节见 `docs/modules/Testing.md`。

## Agent Workflow（2026-10-09 用户指定）

- **分工**：项目设计与文档创建（`docs/`、方案、评审清单）由主模型（k3）直接完成；具体实现（写代码、改测试、跑测试）开子 agent 由 `task` 模型运行。
- 主模型派发实现前须把设计契约、目标文件、验收标准写全在子任务的 `# Target`/`# Change`/`# Acceptance` 里；子 agent 结果回来后由主模型审查并收尾（同步文档、验证）。
