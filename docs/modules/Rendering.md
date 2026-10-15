# 渲染与界面（TapTap2026.Game / Rendering + 场景 UI）

`Assets/Scripts/Rendering/`。整个粒子网格只有 1 个渲染对象。

## 世界渲染

- `ParticleRenderer`：Color32 缓冲 + `ParticlePixelBuffer.Fill` → `Texture2D` → quad；首次建纹理自动框住整张网格；`PanCamera` 做沙箱内左右平移 + 视口夹取（相机唯一写入者）。按 `world.Light.Daylight` 取昼夜天空色（夜 `#2A223A`/昼 `#CCE5FF`），粒子与云亮度按 1 → 夜晚亮度；昼夜色/亮度在 `ParticleLightDriver` 配置并每帧推给渲染器，过渡长度 = 下一时段的 1/10。
- **笔刷轮廓叠层**：与格子逐格对齐的白像素圆 sprite（1 texel = 1 格、pivot 居中、`pixelsPerUnit = 1`，z = −2 且 `sortingOrder = 1` ⇒ 压在世界 quad 与动物之上）；纹理按半径增长式复用、≤ 21×21、不参与昼夜调光；由 `ParticleEditor` 每帧推 `SetBrushOutline`/`HideBrushOutline`。
- `BrushOutline`：笔刷足迹的像素圆几何——纯整型 `Contains`/`IsOutline`/`Collect`/`Fill`，环 = 圆内且四邻有圆外——与编辑器笔刷判据同一条，白圈与实际写入格严丝合缝。

## 相机 → UI（CameraScreenFeed）

建一张与场景显示图等像素的 `RenderTexture`（运行期建、`HideFlags.DontSave`，尺寸随显示图屏幕尺寸重建）设为相机 `targetTexture`、绑到 `RawImage`——相机不再直接画屏，世界在画布里的这张图里。纹理宽高比 = 显示图宽高比 ⇒ 显示图 2:1（= 沙箱 200×100）时相机视野恰好整张网格、格子是方的、平移锁死居中；换固定尺寸纹理资产则非该比例的 Game 视图会拉伸世界。**只在运行期接管**（编辑模式写 `targetTexture` 会标脏场景）；`OnDisable` 清空并释放。**输入必须换算**：`ScreenPointToRay` 吃纹理像素，`ParticleEditor.TryGetCell` 先经 `CameraScreenFeed.ToCameraPixel` 换算（实测 864/864 采样点落在自身格、0 px 越界）。

## 动物渲染（CreatureRenderer）

`[DefaultExecutionOrder(100)]` 排在驱动之后。每帧轮询对账实体表（世界重建换实例天然免疫）、一实体一 SpriteRenderer（运行时 1×1 白纹理拉成碰撞盒、占位色、z = −1 在世界 quad 之上）、按 `Prev → Cur` 逻辑帧间插值（暂停时 alpha 饱和到 1 稳定停住）。已接线兔子/鱼 idle 帧（各 4 帧）与**鸟的三帧组**（`_birdFlyFrames` 下排 3 片 bird_4..6 = 飞行扑翼、`_birdPerchFrames` 上排 4 片 bird_0..3 = 站立栖息、`_birdTakeoffFrame` = bird_5 翅竖直顶点兼任起飞展翅）——`Assets/Images/Animals/{rabbit,fish,bird}/1/*.png` 16×16 切片（PPU 16、Point、不压缩、pivot 贴画底），`_spriteScale` 16 ⇒ 1 源像素 = 1 格、`_idleFps` 2 = 500ms/帧，暂停冻结；未接线物种退回占位白块。鸟按 `AiState` 选帧组（纯函数 `ResolveBirdFrame`/`AdvanceBirdTakeoff`）：栖息播收翅、飞行播上排、**Perched→Fly/Flee 沿**先定格展翅帧 0.3s（`_birdTakeoffSeconds`）再进飞行循环，落回栖息立即切收翅；过渡计时是 per-view 表现层状态，不进模拟。朝向：源 sprite 头朝左 ⇒ `_spriteFacesLeft` 默认 `true`，`Facing = +1` 时 `flipX = true`，映射只在 `ResolveFlipX` 一处定义。

## 界面层（uGUI，全部场景可编辑）

所有屏幕 UI 与沙盒渲染对象都是 `MainScene` 里的真实对象，不再运行时动态创建：

- 场景根：`EventSystem`。
- `HUD Canvas`（overlay 500）：`SandboxHud` 组件 + 光标格读数/鼠标跟随组/高速读数/高速结算面板/光照面板（三个 TMP_InputField 直写 `ParticleLightDriver`）/「全部阶段完成」横幅（`GameController._completionBanner` 只开关激活态）/ **`SandboxView`**（屏幕正中央 2:1 `RawImage`，画布最底层；运行时 `CameraScreenFeed` 绑相机纹理；编辑模式显示自带占位图）/ **`ButtonPanel`**（暂停/光照/高速三颗按钮，`onClick` 持久监听指向 `ParticleEditor` 的 `TogglePauseMenu`/`ToggleLightPanel`/`EnterHighSpeedMode`——暂停按钮一并 `Pause()`/`Resume()`；高速模式与对话激活期间只切显示不动模拟）/ **`ParticlePanel`**（投放选择面板：10 颗按钮由 `ParticleSelectionPanel` 运行期把 `onClick` 绑到 `ParticleEditor.SelectByName`——按 `DisplayName` 绑名，每帧回显选中项（压灰）、名字对不上或锁编辑（`EditingLocked`）时变灰不可点；「清除」槽翻转 `EraseMode`）。
- `Dialogue Canvas`（overlay 1000）：对话/回顾面板 + 选项按钮模板；**整张画布显隐 = 是否在对话**（`Awake` 关、`ShowLine` 开、`CloseView` 关）。
- `Particle Sandbox` 下：`ParticleGridQuad`（编辑态挂预览 mesh/材质 `Assets/Data/SandboxPreviewMesh.asset`/`SandboxPreview.mat`，Game 视图不进 Play 可见取景；运行时首帧换 DontSave 运行时 mesh/材质）与 `ParticleBrushOverlay`（笔刷白圈）。
- 中文显示靠 `Assets/Data/CjkDynamicFont.asset`（动态 TMP 字体，源 `Assets/Data/Fonts/simhei.ttf`，ttf 走 LFS）。
- 场景结构由 `SceneUiScaffolder` 幂等维护，接线由 `SceneWiringTests` 钉住（见 EditorTooling/Testing 文档）。
