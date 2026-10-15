# 编辑器工具与 MCP（TapTap2026.Editor）

`Assets/Scripts/Editor/`，仅 Editor 平台程序集（引用 Game + Simulation + `Unity.TextMeshPro`）。

## 编辑器工具

- `SystemProxyGuard`：系统代理 Mono 解析失败时把 `WebRequest.DefaultWebProxy` 钉为直连（保 Unity-MCP 连通，见下）。
- `SceneUiScaffolder`：**场景 UI 脚手架**——菜单 `Tools/TapTap2026/Scene UI Setup（补缺失+接线）` / 批处理 `-executeMethod TapTap2026.EditorTools.SceneUiScaffolder.Run`；**幂等**补齐 `HUD Canvas`（含暂停/光照/高速三按钮）/`Dialogue Canvas`/`EventSystem`/`ParticleGridQuad`/`ParticleBrushOverlay`/`ScreenBackdrop` 并接好全部引用（三按钮 `onClick` 接成指向 `ParticleEditor` 的持久监听）、刷新沙箱预览 mesh 与相机编辑态取景、建中文动态 TMP 字体并套用；已存在的对象不动布局。
- `CreatureArtGenerator`：菜单 `Tools/Animals/Regenerate Creature Art Data`——从 `Assets/Images/Animals/*/1/*.png` + 切片 pivot 生成 `CreatureArtData.g.cs`（尸体轮廓/pivot/不透明行区间），美术变更后重跑（`CreatureArtDataTests` 对拍守卫）。
- `BirdSpriteWiring`：菜单 `Tools/Animals/Wire Bird Sprites`——把鸟切片定点写进场景的 `_birdIdleFrames`，幂等、场景 YAML 不手改。

## MCP（AI Game Developer / Unity-MCP）

- 包：`com.ivanmurzak.unity.mcp@0.87.0`，经 OpenUPM scoped registry（`package.openupm.com`，scopes: `com.ivanmurzak`, `extensions.unity`）在 `Packages/manifest.json`；运行时 DLL 在 `Assets/Plugins/NuGet/`（LFS 跟踪，插件安装器生成，**不要手改**）。`.meta` 平台保持 **Any Platform + Editor**：裁成 Editor-only 会让 player 编译期丢引用、整包 2100+ 条 `CS0246`（`b23663d` 已回退）。MCP 仍会被 AOT 进 player（审计 P4，未解决）。
- **前提：Unity 编辑器必须开着本工程**，MCP 工具由编辑器内插件提供。
- 常用工具：`tests-run`（跑 EditMode/PlayMode 测试；要求所有打开的场景无未保存修改）、`assets-*`、`scene-*`、`gameobject-*`、`script-execute`（Roslyn 动态执行 C#）、`console-get-logs`。
- **连不上先查系统代理**：`HKCU\...\Internet Settings\ProxyServer` 若是 `https=http://127.0.0.1:31181` 这类 Mono 无法解析的形式（dev-sidecar/Clash 会周期性写回），Mono 抛 `UriFormatException`、插件 SignalR 全断、`tools/list` 返回 0 个工具。`SystemProxyGuard` 会在域重载时自动钉直连，**不要去手改注册表**（会被代理工具覆盖）。

## 场景

`Assets/Scenes/MainScene.unity` 是唯一 build 场景（Main Camera + Global Light 2D + `ScreenBackdrop`）。`Particle Sandbox` 物体上挂：`ParticleSimulationDriver` + `ParticleRenderer` + `ParticleSandboxSeeder` + `ParticleEditor` + `ParticleLightDriver`（光照开关/亮暗刻数/强度/云层消光/昼夜天空色/夜晚亮度）+ `SoundController` + `LiquidFlowSoundDriver` + `FireSoundDriver` + `LightningSoundDriver` + `FallingSandSoundDriver`。UI 结构详见 `docs/modules/Rendering.md`。**破坏 `MainScene.unity` 等于破坏唯一 build 场景。**
