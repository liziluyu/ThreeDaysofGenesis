# 工程配置与代码约定

## 运行环境

- **Unity Editor 2022.3.62f1**，装在 `D:\Unity\2022.3.62f1`（GUI 优先）；不要用别的版本打开（`ProjectSettings/ProjectVersion.txt` 钉死 `2022.3.62f1 (4af31df58517)`，版本不一致会重写资产）。
- URP 14.0.12 + 2D Renderer，2D 模式（配置在 `Assets/Settings/`）。
- Scripting backend: Mono2x；API compatibility: .NET Standard 2.0。
- 包管理 UPM：官方 registry + OpenUPM scoped registry（MCP）。`Packages/manifest.json` 与 `packages-lock.json` 一起提交；直接依赖 `com.unity.burst` 1.8.30。
- 序列化：Force Text + Visible Meta Files——`.meta` 与资产同提交、不单独删除；YAML 冲突用 UnityYAMLMerge。
- Build target 未钉死，默认编辑器宿主（Standalone）；做平台相关工作前显式设置。Build 场景列表只有 `MainScene.unity`。
- **输入**：仅 legacy Input Manager（`activeInputHandler: 0`，Input System 未安装）。**禁止** `using UnityEngine.InputSystem;`。
- **异步**：无 UniTask，2022.3 无 `Awaitable`；用协程。`Task`/`async` 仅限主线程延续、禁止阻塞。

## 代码约定

- 类型/方法/属性 `PascalCase`；局部与参数 `camelCase`；私有序列化字段 `[SerializeField] private Type _camelCase;`。
- MonoBehaviour：引用在 `Awake`/`OnEnable` 获取并缓存；`Update` 内禁止 `GetComponent`/`Find*`。
- 错误处理：不静默吞异常；`Debug.LogError`/`LogException` 报运行期失败，`LogWarning` 报可恢复降级。
- 全局模拟参数走 `ParticleSimulationSettings` 资产（`Assets/Data/`），不在 MonoBehaviour 里硬编码。
- 无 linter/formatter 配置；4 空格缩进，文本文件 LF（`.gitattributes` 强制）。
- `*.sln`/`*.csproj` 是生成物且 gitignored，永不手改。

## 生成物

`Library/`、`Temp/`、`obj/`、`Logs/`、`Build(s)/`、`UserSettings/` 为 Unity 生成，gitignored，**永不读、改、提交**。`.gitignore` 是标准 Unity ignores，`git add` 前先看。`.gitattributes`：Unity YAML `merge=unityyamlmerge`、LF 规则、二进制走 LFS（含 `*.dll`）。

## 目录速览

|Path|Purpose|
|---|---|
|`Assets/Scenes/MainScene.unity`|唯一场景；唯一启用的 build 场景|
|`Assets/Scripts/Simulation/`|模拟内核 + 粒子类型（`Particles/`）+ 动物实体层（`Creatures/`）|
|`Assets/Scripts/Driver/`|驱动、编辑器交互、声音、游戏流程薄壳|
|`Assets/Scripts/Rendering/`|世界/动物渲染、相机→UI、笔刷叠层|
|`Assets/Scripts/Editor/`|仅编辑器工具（脚手架、美术生成器、代理自愈）|
|`Assets/Data/`|配置资产（`ParticleSimulationSettings.asset`、`Phases/`、字体、预览 mesh）|
|`Assets/Tests/EditMode/`|EditMode 测试（631 个）|
|`docs/`|设计文档（Unity 不导入，无需 `.meta`）|
|`Assets/Plugins/NuGet/`|MCP NuGet 依赖 DLL（LFS，勿手改）|
|`ProjectSettings/`、`Packages/`|Unity 工程配置、依赖清单|
