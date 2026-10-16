# 工程配置与代码约定

## 运行环境

- **Unity Editor 2022.3.62f1**，装在 `D:\Unity\2022.3.62f1`（GUI 优先）；不要用别的版本打开（`ProjectSettings/ProjectVersion.txt` 钉死 `2022.3.62f1 (4af31df58517)`，版本不一致会重写资产）。
- URP 14.0.12 + 2D Renderer，2D 模式（配置在 `Assets/Settings/`）。
- Scripting backend: Mono2x；API compatibility: .NET Standard 2.0。
- 包管理 UPM：官方 registry + OpenUPM scoped registry（MCP）。`Packages/manifest.json` 与 `packages-lock.json` 一起提交；直接依赖 `com.unity.burst` 1.8.30。
- 序列化：Force Text + Visible Meta Files——`.meta` 与资产同提交、不单独删除；YAML 冲突用 UnityYAMLMerge。
- Build target 未钉死，默认编辑器宿主（Standalone）；做平台相关工作前显式设置。Build 场景列表：`MainScene.unity`（下标 0）+ `LevelSelect.unity`（下标 1）。
- **输入**：仅 legacy Input Manager（`activeInputHandler: 0`，Input System 未安装）。**禁止** `using UnityEngine.InputSystem;`。
- **异步**：无 UniTask，2022.3 无 `Awaitable`；用协程。`Task`/`async` 仅限主线程延续、禁止阻塞。

## 代码约定

- 类型/方法/属性 `PascalCase`；局部与参数 `camelCase`；私有序列化字段 `[SerializeField] private Type _camelCase;`。
- MonoBehaviour：引用在 `Awake`/`OnEnable` 获取并缓存；`Update` 内禁止 `GetComponent`/`Find*`。
- 错误处理：不静默吞异常；`Debug.LogError`/`LogException` 报运行期失败，`LogWarning` 报可恢复降级。
- 全局模拟参数走 `ParticleSimulationSettings` 资产（`Assets/Data/`），不在 MonoBehaviour 里硬编码。
- 无 linter/formatter 配置；4 空格缩进，文本文件 LF（`.gitattributes` 强制）。
- `*.sln`/`*.csproj` 是生成物且 gitignored，永不手改。

## 字体（**只读区，agent 绝不修改**）

**任何情况下都不许动用户的字体设置**（2026-10-16 用户指定）。字体由用户手工维护；agent 只能读、不能写：

- **禁止**创建 / 替换 / 删除 / 重新生成字体资产，**禁止**改字体资产的导入设置（`.meta`），**禁止**改 `TMP Settings`。
- **禁止**改任何 `TMP_Text` 的字体引用 / 字号 / 字体材质——包括两个场景（`MainScene.unity`、`LevelSelect.unity`）里的、以及运行期由代码写入的。
- 需要换字体时**由用户自己做**；agent 的工作若牵涉到字体（例如新建 UI 文本），一律只接引用 / 沿用现状，不指定字体。

| 受保护资产 | 说明 |
|---|---|
| `Assets/Data/Fonts/8px.asset` / `10px.asset` / `12px.asset` | 三个像素字体 TMP 资产（用户当前使用的字体） |
| `Assets/Data/Fonts/fusion-pixel-{8,10,12}px-monospaced-{latin,zh_hans}.ttf` | 像素字体源文件（`*.ttf` 走 LFS） |
| `Assets/Data/Fonts/LICENSES/` | 各字体授权文本 |
| `Assets/Data/Fonts/**/*.meta` | 全部导入设置 |
| `Assets/Data/CjkDynamicFont.asset` | 旧的中文动态字体资产（**历史遗留，同样不许动 / 不许删**） |
| `Assets/TextMesh Pro/Resources/TMP Settings.asset` | TMP 全局设置（默认字体等） |

> ⚠️ **`SceneUiScaffolder` 的字体步骤会违反此约定，禁止运行**——见 `docs/modules/EditorTooling.md`。

**字形写入路径**：2026-10-16 删除了 `DialogueDebugView.Awake` 里「把整张对话画布字体统一改成 `_font`」的那段代码（它会抹平逐组件设的字体）。全工程**只剩一条**会写字体（`TMP_Text.font` / 字号）的代码路径，且**禁止运行**：

| 位置 | 行为 |
|---|---|
| `SceneUiScaffolder.EnsureCjkFont` / `ApplyFont` / `EnsureText` | 生成 `CjkDynamicFont.asset` + 覆盖两个画布下每个 `TMP_Text` 的字体（并写字号 / 颜色）。**禁止运行该工具** |

> 除它外，任何新代码 / 脚本 / 工具都**不得**写入字体字段；本组件的运行期行为不得再自行套字体。

## 生成物

`Library/`、`Temp/`、`obj/`、`Logs/`、`Build(s)/`、`UserSettings/` 为 Unity 生成，gitignored，**永不读、改、提交**。`.gitignore` 是标准 Unity ignores，`git add` 前先看。`.gitattributes`：Unity YAML `merge=unityyamlmerge`、LF 规则、二进制走 LFS（含 `*.dll`）。

## 目录速览

|Path|Purpose|
|---|---|
|`Assets/Scenes/MainScene.unity`|游戏场景；唯一启用的 build 场景之一（下标 0）|
|`Assets/Scripts/Simulation/`|模拟内核 + 粒子类型（`Particles/`）+ 动物实体层（`Creatures/`）|
|`Assets/Scripts/Driver/`|驱动、编辑器交互、声音、游戏流程薄壳|
|`Assets/Scripts/Rendering/`|世界/动物渲染、相机→UI、笔刷叠层|
|`Assets/Scripts/Editor/`|仅编辑器工具（脚手架、美术生成器、代理自愈）|
|`Assets/Data/`|配置资产（`ParticleSimulationSettings.asset`、`Phases/`、**字体（只读，见上）**、预览 mesh）|
|`Assets/Tests/EditMode/`|EditMode 测试（见 `docs/modules/Testing.md` 的当前计数）|
|`docs/`|设计文档（Unity 不导入，无需 `.meta`）|
|`Assets/Plugins/NuGet/`|MCP NuGet 依赖 DLL（LFS，勿手改）|
|`ProjectSettings/`、`Packages/`|Unity 工程配置、依赖清单|
