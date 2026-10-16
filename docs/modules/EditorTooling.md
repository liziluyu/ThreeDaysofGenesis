# 编辑器工具与 MCP（TapTap2026.Editor）

`Assets/Scripts/Editor/`，仅 Editor 平台程序集（引用 Game + Simulation + `Unity.TextMeshPro`）。

## 编辑器工具

- `SystemProxyGuard`：系统代理 Mono 解析失败时把 `WebRequest.DefaultWebProxy` 钉为直连（保 Unity-MCP 连通，见下）。
- `SceneUiScaffolder`：**场景 UI 脚手架**——菜单 `Tools/TapTap2026/Scene UI Setup（补缺失+接线）` / 批处理 `-executeMethod TapTap2026.EditorTools.SceneUiScaffolder.Run`；**幂等**补齐 `HUD Canvas`（含暂停/光照/高速三按钮、光照面板的强度滑块（0..30）、白天 / 夜晚秒读数与「白天 / 夜晚」±1秒/±5秒 步进按钮——那 8 颗按钮的 `onClick` 接成指向 `SandboxHud` 的**整型**持久监听，秒数写进监听参数）/`Dialogue Canvas`/`EventSystem`/`ParticleGridQuad`/`ParticleBrushOverlay`/`ScreenBackdrop` 并接好全部引用（三按钮 `onClick` 接成指向 `ParticleEditor` 的持久监听）、刷新沙箱预览 mesh 与相机编辑态取景、~~建中文动态 TMP 字体并套用~~（**该步骤已被禁用，见下**）；已存在的对象不动布局。

  > 🚫 **禁止运行这个工具**（菜单与批处理都算）：它的**字体步骤**会违反 `AGENTS.md` 铁律「**任何情况下都不许动用户的字体设置**」（2026-10-16 用户指定），而且是本工程里唯一会**批量改写字体**的代码路径：
  > - `EnsureCjkFont`（常量 `FontAssetPath = Assets/Data/CjkDynamicFont.asset`、`FontFolderPath = Assets/Data/Fonts`）在该资产缺失时会从系统字体（硬编码候选 `simhei` / `NotoSansSC-VF`）**重新生成** `CjkDynamicFont.asset` 及其材质 / 图集（`AssetDatabase.CreateAsset`）。
  > - `ApplyFont(GameObject canvasRoot, TMP_FontAsset font)` 会把两个画布下**每一个** `TMP_Text`（含隐藏面板里的读数）的 `fontAsset` 覆盖成上面那个字体——用户手工设的像素字体（`Assets/Data/Fonts/{8,10,12}px.asset`）会被整片抹掉。
  >
  > 需要它做的其它事情（补 UI 对象、接引用）请手工做，或改代码绕开这两个方法。字体资产与 `TMP Settings` 属**只读区**，清单见 `docs/modules/ProjectSetup.md`「字体」一节。
- `CreatureArtGenerator`：菜单 `Tools/Animals/Regenerate Creature Art Data`——从 `Assets/Images/Animals/*/1/*.png` + 切片 pivot 生成 `CreatureArtData.g.cs`（尸体轮廓/pivot/不透明行区间），美术变更后重跑（`CreatureArtDataTests` 对拍守卫）。
- `BirdSpriteWiring`：菜单 `Tools/Animals/Wire Bird Sprites`——把鸟切片定点写进场景的 `_birdIdleFrames`，幂等、场景 YAML 不手改。

## MCP（AI Game Developer / Unity-MCP）

- 包：`com.ivanmurzak.unity.mcp@0.87.0`，经 OpenUPM scoped registry（`package.openupm.com`，scopes: `com.ivanmurzak`, `extensions.unity`）在 `Packages/manifest.json`；运行时 DLL 在 `Assets/Plugins/NuGet/`（LFS 跟踪，插件安装器生成，**不要手改**）。`.meta` 平台保持 **Any Platform + Editor**：裁成 Editor-only 会让 player 编译期丢引用、整包 2100+ 条 `CS0246`（`b23663d` 已回退）。MCP 仍会被 AOT 进 player（审计 P4，未解决）。
- **前提：Unity 编辑器必须开着本工程**，MCP 工具由编辑器内插件提供。
- 常用工具：`tests-run`（跑 EditMode/PlayMode 测试；要求所有打开的场景无未保存修改）、`assets-*`、`scene-*`、`gameobject-*`、`script-execute`（Roslyn 动态执行 C#）、`console-get-logs`。
- **连不上先查系统代理**：`HKCU\...\Internet Settings\ProxyServer` 若是 `https=http://127.0.0.1:31181` 这类 Mono 无法解析的形式（dev-sidecar/Clash 会周期性写回），Mono 抛 `UriFormatException`、插件 SignalR 全断、`tools/list` 返回 0 个工具。`SystemProxyGuard` 会在域重载时自动钉直连，**不要去手改注册表**（会被代理工具覆盖）。

## 场景

- `Assets/Scenes/MainScene.unity`：**游戏场景**（build 下标 0）。Main Camera + Global Light 2D + `ScreenBackdrop`。`Particle Sandbox` 物体上挂：`ParticleSimulationDriver` + `ParticleRenderer` + `ParticleSandboxSeeder` + `ParticleEditor` + `ParticleLightDriver`（光照开关/亮暗刻数/强度/云层消光/昼夜天空色/夜晚亮度）+ `SoundController` + `LiquidFlowSoundDriver` + `FireSoundDriver` + `LightningSoundDriver` + `FallingSandSoundDriver`；场景根另有 `Game Controller`（`GameController`）。UI 结构详见 `docs/modules/Rendering.md`。
- `Assets/Scenes/LevelSelect.unity`：**关卡选择场景**（build 下标 1）。根对象 `Canvas`（五颗关卡按钮 `Day0`..`Day3` + `Sandbox`，外加悬停描述用的 `ProbeText`，初始隐藏）与 `Level Select Controller`（`LevelSelectController`，逐条接好 `_levels` → 上述按钮 / `phase_01..05`，`_probeText` → `Canvas/ProbeText`，`_phases` → 五个阶段 JSON）。选关流程见 `docs/GameFlow.md` §9。

**破坏这两个场景中任何一个都会破坏 build。**
