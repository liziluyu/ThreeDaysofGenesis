# Repository Guidelines

## Project Overview

TapTap2026 is a **Unity 2022.3.62f1** project using **URP 14.0.12 with the 2D Renderer**, configured in 2D mode.

当前内容：一个**落沙类粒子模拟系统**（元胞自动机）——纯 C# 模拟内核 + 单 quad 渲染 + 固定步长驱动，配完整的 EditMode 测试。粒子类型的作者契约、世界 API、渲染契约的唯一权威文档是 **`docs/ParticleTypeInterface.md`**；改模拟/渲染架构前先读它，改完同步它。

## Architecture & Data Flow

- **分层**（程序集边界即架构边界）：
  - `TapTap2026.Simulation`（`Assets/Scripts/Simulation/`）：纯模拟，**不引用任何渲染对象**。扁平 `Cell[]`（4 字节/格）+ `(x,y)` 直接寻址；类型行为经全局静态注册表（`ParticleRegistry`）以 `typeId → 委托` 直调，热路径零分配、零虚分派。世界另持两张与网格同形的场：热图 `HeatMap`（每格存热量 Q）与**湿度场** `HumidityField`（每格存水汽，只有非固非液的格持有；云 = 湿度高的空气，不是粒子），外加光照控制器 `LightController`。湿度像热量一样传播，但带温度定向——水平冷→热、竖直越热越向上；水**沸腾**时把水汽集中成一小片（可见白汽），**平时慢蒸发**只把水汽摊薄进空气（不可见，且只在液面）——所以可见的水蒸气只在沸腾时出现；低温饱和处凝结成雨。温度是导出量 `T = Q / 类型热容`；热由物质携带（`Swap` 搬 Q、`Set`/`Clear` 重置 Q）；`StepLogic` 每逻辑帧在粒子扫描后自动做一次全格四邻热传导（传导界内守恒）+ 两条散热路（顶行向虚空 `VoidHeatLoss`、**只作用于暴露面**并按材料辐射率 ε 缩放的辐射 `RadiativeLoss`）+ 湿度输送（扩散 + 温度定向）与降雨，再由光照向「每列最上面的非气体粒子（固体或液体——光打到水面就停）」施热，且**按该列云量（整列湿度之和）做 Beer–Lambert 衰减**——云越厚，地表越晒不到——持续受热的暴露格就近收敛到 `T ≈ 输入功率 / (RadiativeLoss · 该材料 ε)`。粒子经 `world.Heat.GetTemperature/GetHeat/AddHeat` 查改温度。
  - `TapTap2026.Game`（`Assets/Scripts/Driver/` + `Assets/Scripts/Rendering/`）：引用 Simulation。`ParticleSimulationDriver`（MonoBehaviour）用固定步长累加器推进**两级时基**——**运动帧 60 Hz**（`world.StepMotion()`，只跑粒子运动）与**逻辑帧 20 Hz**（`world.StepLogic()`，先逻辑后运动，再跑随机刻/热传导/湿度/降水/光照）；逻辑帧是运动帧节拍上的子节拍（每 `LogicTicksPerMotionTick = round(_motionRate/_logicRate)` 个运动帧一次），并唯一决定上屏时机；`ParticleRenderer` 把 `Cell[]` 经 `ParticlePixelBuffer.Fill` 映射成一张 `Texture2D`，整个网格只有 1 个渲染对象。
  - `TapTap2026.Editor`（`Assets/Scripts/Editor/`，仅 Editor 平台）：编辑器工具，当前只有 `SystemProxyGuard`——系统代理 Mono 解析失败时把 `WebRequest.DefaultWebProxy` 钉为直连（见 MCP 一节）。
- **随机刻（Minecraft 式）**：每逻辑帧在粒子扫描之后、热传导之前，`StepLogic` 均匀有放回地随机抽 `world.RandomTicksPerStep` 个格，命中非空气、非静态的格就调其类型的 `OnRandomTick`——用于「缓慢、按概率发生」的过程（生长/蔓延/渗流/腐化），不必每帧全网格扫描。抽取与世界同一 xorshift32 随机流（可复现）、不碰 bit0 处理标记；**默认 0（关闭，保持既有测试契约）**，生产值由 `ParticleSimulationSettings._randomTicksPerStep` 每帧注入 `world.RandomTicksPerStep`（资产现配 48 ≈ Minecraft 的 3 格 / 4096 密度）。契约与语义见 `docs/ParticleTypeInterface.md` §6「随机刻」。
- **跨类型规则只看标签不看 typeId**：`ParticleTags` 的 rank 次序 `Solid(3) > Liquid(2) > Gas(1) > 空气(0)` 决定谁置换谁（`ParticleRules`）。新增粒子类型 = 一个文件 + `ParticleRegistration.RegisterAll()` 追加一行 + `ParticleIds` 加一个字段（只能追加，顺序是存档兼容性的一部分）。当前类型：石头 / 沙 / 土 / **湿土** / 水 / 大气（蒸汽与云已并入**湿度场**，不再是粒子）。**土与湿土**是唯一一对"具体类型之间的反应"（双向走 `ParticleIds`）：干土在**正上方是水**时喝掉那格水、自己变湿土；湿土 = 干土 + 一份水，由它自己搬运这份水——**常温下每逻辑帧 50% 概率**把水交给正下方的干土（自己变干，水一格格往下渗），**受热（≥ 60°）时改往上送**（上方是干土则润湿之；上方是空气且没饱和就像水粒子慢蒸发一样摊一份水汽进空气、不可见），都送不出去就保持湿。水总量（水格 + 湿土格 + 湿度场）全程守恒；只有干土接得住水（石头/沙/水没有"湿"形态）。流程与评审清单见文档 §11。
- **确定性**：模拟只认 `world.Tick` 与世界持有的 xorshift32；禁止 `UnityEngine.Random` / `Time.*` / `Input`。同种子同命令序列逐格可复现。
- **场景**：`Assets/Scenes/SampleScene.unity` 是唯一 build 场景（Main Camera + Global Light 2D）；`Particle Sandbox` 物体上挂 `ParticleSimulationDriver` + `ParticleRenderer`（按 `world.Light.Daylight` 缩放整场亮度：白天上限、夜晚下限，过渡由光照周期给出）+ `ParticleSandboxSeeder`（默认不跑，右键菜单 `Reseed()` 才搭容器）+ `ParticleEditor`（左键刷、右键切换、鼠标旁显示当前粒子）+ `ParticleLightDriver`（光照开关/亮暗刻数/强度/昼夜过渡刻数），接线细节见文档 §10。

## Key Directories

|Path|Purpose|
|---|---|
|`Assets/Scenes/SampleScene.unity`|唯一的场景；唯一启用的 build 场景。|
|`Assets/Scripts/Simulation/`|模拟内核 + 粒子类型（`Particles/`）。`TapTap2026.Simulation.asmdef`。|
|`Assets/Scripts/Driver/`|两级固定步长驱动（运动帧 60 Hz / 逻辑帧 20 Hz）+ `ParticleSimulationSettings`（ScriptableObject）+ 沙箱演示/调试组件（`ParticleSandboxSeeder` 显式 `Reseed()` 搭演示容器、`ParticleEditor` 左键刷当前粒子、滚轮调半径 1..10、右键切换类型、鼠标旁显示当前类型与半径、屏幕左上角显示光标格的粒子名/温度/热量/湿度、`ParticleLightDriver` 把光照的开关/亮暗刻数/强度接到 `world.Light`）。|
|`Assets/Scripts/Rendering/`|`ParticleRenderer`（Color32 缓冲 + Texture2D + quad）。|
|`Assets/Scripts/Editor/`|仅编辑器程序集 `TapTap2026.Editor`：`SystemProxyGuard`（系统代理自愈，保 Unity-MCP 连通）。|
|`Assets/Data/`|实例化的配置资产：`ParticleSimulationSettings.asset`（场景驱动引用；含网格尺寸、`_motionRate` 运动帧速率 / `_logicRate` 逻辑帧速率、`_randomTicksPerStep` 随机刻格数、`_conductivity`/`_voidHeatLoss`/`_radiativeLoss`/`_airThermalConductivity`/`_airThermalEmissivity` 热力学参数）。|
|`Assets/Tests/EditMode/`|EditMode 测试（117 个）：`ParticleWorldTests`（隔离类型表，含随机刻）+ `SandStoneParticleTests`（生产注册路径）+ `WaterParticleTests` / `WeatherParticleTests`（水、湿度场与气象）+ `WetSoilParticleTests`（土的吸水反应）。|
|`docs/`|设计文档（`ParticleTypeInterface.md`）。在 `Assets/` 之外，Unity 不导入，无需 `.meta`。|
|`Assets/Settings/`|URP 配置：`UniversalRP.asset`、`Renderer2D.asset` 等。|
|`Assets/Plugins/NuGet/`|MCP 插件的 NuGet 依赖 DLL（LFS 跟踪），由插件安装器生成，**不要手改**。|
|`ProjectSettings/`|Unity 工程配置（YAML 文本）。|
|`Packages/`|`manifest.json` + `packages-lock.json`。|
|`Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/`|Unity 生成。Gitignored。**永不读、改、提交。**|

## Development Commands

Unity 编辑器路径：`D:\Unity\2022.3.62f1\Editor\Unity.exe`（GUI 优先）。从项目根目录：

```powershell
# 跑 EditMode 测试（批处理；前提：没有别的 Unity 实例开着本工程，否则 'another Unity instance is running' 直接中止）
& 'D:\Unity\2022.3.62f1\Editor\Unity.exe' -batchmode -quit -runTests `
  -projectPath . -testPlatform EditMode -testResults Temp\results.xml -logFile Temp\unity-editmode.log
```

- 编辑器开着本工程时，改用 Test Runner 窗口或 MCP 的 `tests-run`（见下）。
- 无 linter/formatter 配置；4 空格缩进，文本文件 LF（`.gitattributes` 强制）。
- `*.sln` / `*.csproj` 是生成物且 gitignored，永不手改。

## MCP（AI Game Developer / Unity-MCP）

- 包：`com.ivanmurzak.unity.mcp@0.87.0`，经 OpenUPM scoped registry（`package.openupm.com`，scopes: `com.ivanmurzak`, `extensions.unity`）安装在 `Packages/manifest.json`；运行时 DLL 在 `Assets/Plugins/NuGet/`。
- **前提：Unity 编辑器必须开着本工程**，MCP 工具由编辑器内插件提供。
- 常用工具：`tests-run`（跑 EditMode/PlayMode 测试；注意它会要求所有打开的场景无未保存修改）、`assets-*`（查/改资产、材质、prefab）、`scene-*`（场景 CRUD）、`gameobject-*`、`script-execute`（Roslyn 动态执行 C#）、`console-get-logs`。
- 验证工作流首选 `tests-run`（编辑器在线时）；编辑器不在线时用上面的批处理命令。
- **连不上先查系统代理**：`HKCU\...\Internet Settings\ProxyServer` 若是 `https=http://127.0.0.1:31181` 这类 Mono 无法解析的形式（dev-sidecar/Clash 会周期性写回），Mono 抛 `UriFormatException: Invalid URI: The hostname could not be parsed.`，插件 SignalR 全断、`tools/list` 返回 0 个工具。`SystemProxyGuard` 会在域重载时自动改钉 `WebRequest.DefaultWebProxy` 为直连，**不要去手改注册表**（会被代理工具覆盖）。

## Code Conventions & Common Patterns

- **C# / Unity 命名**：类型/方法/属性 `PascalCase`；局部与参数 `camelCase`；私有序列化字段 `[SerializeField] private Type _camelCase;`。
- **MonoBehaviour 生命周期**：引用在 `Awake`/`OnEnable` 获取并缓存；`Update` 内禁止 `GetComponent`/`Find*`。
- **输入**：仅 legacy Input Manager（`activeInputHandler: 0`，Input System 未安装）。**禁止** `using UnityEngine.InputSystem;`。
- **异步**：无 UniTask，2022.3 无 `Awaitable`；用协程。`Task`/`async` 仅限主线程延续、禁止阻塞。
- **粒子类型作者约束**（详见 `docs/ParticleTypeInterface.md`，违者拒收）：一个类型一个文件、放 `Simulation/Particles/`、`public sealed`、非 MonoBehaviour；**运动写在 `OnMotion`（运动帧 60 Hz）**、**相变/反应写在 `OnLogic`（逻辑帧 20 Hz，先于同格运动）**，两者内无分配/LINQ/字符串/`Time.*`；先判断后写、写后立即 return；跨类型判断只走 `ParticleTags`，不特判 typeId/DebugName；必须给有限正数的 `HeatCapacity`（热容）与 `[0,1]` 内的 `ThermalConductivity`（导热率）/`ThermalEmissivity`（辐射率，只影响暴露面），读温度用 `world.Heat.GetTemperature`、改热量用 `AddHeat`/`SetHeat`，不要自己重复做热传导；随机刻行为写在 `OnRandomTick`（没有就留空），同样禁止分配与 `Time.*`、随机用 `world.NextUInt()`。
- **错误处理**：不静默吞异常；`Debug.LogError`/`LogException` 报运行期失败，`LogWarning` 报可恢复降级。
- **配置**：全局模拟参数走 `ParticleSimulationSettings` 资产（`Assets/Data/` 下创建），不在 MonoBehaviour 里硬编码。帧速率 `_motionRate`（运动帧，60）/ `_logicRate`（逻辑帧，20）也在这里。热力学系数（`_conductivity` 传导、`_voidHeatLoss` 顶行虚空散热、`_radiativeLoss` 暴露面辐射、`_airThermalConductivity` 空气导热率、`_airThermalEmissivity` 空气辐射率）与随机刻格数 `_randomTicksPerStep` 也在这里；`ParticleSimulationDriver` 每帧写回 `world.Heat` 与 `world.RandomTicksPerStep`。

## Important Files

|File|Why it matters|
|---|---|
|`docs/ParticleTypeInterface.md`|粒子系统的接口规范与架构契约。改架构必须同步它。|
|`ProjectSettings/ProjectVersion.txt`|钉死编辑器 `2022.3.62f1 (4af31df58517)`，版本不一致会重写资产。|
|`Packages/manifest.json`|依赖清单（含 OpenUPM scoped registry）；与 `packages-lock.json` 一起提交。|
|`ProjectSettings/EditorBuildSettings.asset`|Build 场景列表，当前只有 `SampleScene.unity`。|
|`Assets/Scripts/Simulation/ParticleRegistration.cs`|全工程唯一的粒子类型清单；注册顺序只能追加。|
|`Assets/Scripts/Simulation/Cell.cs`|`Cell` 布局唯一真源，其它文件不得复制该结构。|
|`.gitattributes`|Unity YAML `merge=unityyamlmerge`、LF 规则、二进制走 LFS（含 `*.dll`）。|
|`.gitignore`|标准 Unity ignores，`git add` 前先看。|

## Runtime/Tooling Preferences

- **Runtime: Unity Editor 2022.3.62f1**，装在 `D:\Unity\2022.3.62f1`。不要用别的版本打开。
- **Scripting backend:** Mono2x；**API compatibility: .NET Standard 2.0**。
- **包管理：UPM**，官方 registry + OpenUPM scoped registry（MCP）。
- **序列化：Force Text + Visible Meta Files**——`.meta` 与资产同提交、不单独删除。YAML 冲突用 UnityYAMLMerge。
- **生成物不提交**：`Library/`、`Temp/`、`obj/`、`Logs/`、`Build(s)/`、`UserSettings/`、`*.csproj`、`*.sln`。
- **Build target**：未钉死，默认编辑器宿主（Standalone）；做平台相关工作前显式设置。

## Testing & QA

- EditMode 程序集：`Assets/Tests/EditMode/TapTap2026.Tests.EditMode.asmdef`（引用 Simulation + TestRunner + NUnit，`includePlatforms: Editor`）。现有 **117 个测试**（`ParticleWorldTests` 81 + `SandStoneParticleTests` 2 + `WaterParticleTests` 6 + `WeatherParticleTests` 18 + `WetSoilParticleTests` 10）。
- 约定：测试用**隔离类型表**（`new ParticleWorld(w, h, seed, types)` 四参构造 + 测试专用 `ParticleType[]`），不污染全局注册表；生产注册路径的测试（`RegisterAll` 回填）集中在 `SandStoneParticleTests` 与 `WeatherParticleTests`（跨类型相变依赖 `ParticleIds` 回填，必须走生产表）。
- **运行方式**（按可用性排序）：MCP `tests-run`（编辑器在线）→ Test Runner 窗口 → 批处理命令（编辑器离线）。
- PlayMode：`testables` 未配置、`playModeTestRunnerEnabled: 0`，暂不支持。
- 覆盖率：未安装未配置。
- **QA 期望**：逻辑改动必须过 EditMode 测试；视觉/玩法改动进 Play 模式实际观察；破坏 `SampleScene.unity` 等于破坏唯一 build 场景。
