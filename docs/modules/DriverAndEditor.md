# 驱动层与编辑器交互（TapTap2026.Game / Driver）

`Assets/Scripts/Driver/`。引用 Simulation。

## 三级分工与驱动（阶段 1 命令 / 帧边界 + 阶段 3 常驻 worker）

**世界与 tick 循环都在常驻 worker 线程上**（`SimulationWorker`，纯 C# 一条私有 `Thread`）；`ParticleSimulationDriver`（MonoBehaviour）是**主线程薄壳**，只做四件事：①采 Unity 侧配置并变化时入队命令；②用 `SpeedController`（纯 C#，预算式）计划「本帧跑几个运动 tick」；③**非阻塞**排一批请求（请求槽满就放弃本次，绝不等 worker）；④消费最新已发布帧并在有新帧时把像素交给 `ParticleRenderer` 上传。

两级时基在 worker 上才是：运动帧 60 Hz（`world.StepMotion()`）与逻辑帧 20 Hz（`world.StepLogic()`，逻辑帧是运动帧节拍上的子节拍，每 `LogicTicksPerMotionTick = round(_motionRate/_logicRate)` 个运动帧一次）。

驱动是**唯一与 worker 对话的地方**，对外只剩两个口（见 `docs/design-threaded-simulation-rendering.md`）：

- **入口 `Submit(SimulationCommand)`**：值类型、无闭包、无 `UnityEngine.Object` 引用的命令，入队即定序（`Sequence` 单调递增）。驱动自己在 `Advance` 里把 Unity 侧配置采成命令（Inspector 尺寸覆盖 → `Resize`；热力学 / 随机刻 / 调试叠加变化 → `SetSimulationSettings`），所以队列不会被每帧的无意义写入淹没。
- **出口 `CurrentFrame`**：最新已发布的完整 `SimulationFrame`（像素 + 世界面向数据 + 实体 DTO + 音频观察 + 高速状态，见 SimulationCore 文档）。**已发布的帧在两次后续发布之内保持不变**（3 槽轮换），任何消费者都拿不到活世界。
- `Advance(deltaTime)` 是一个驱动帧：采配置 → 空转 worker 故障 / 世界诊断消息 / 回执 → **非阻塞**排一批请求 → 消费新帧上屏。`Update` 用 `Time.deltaTime` 调它；公开（含显式 dt）是为了让测试能脱离帧循环驱动命令与帧边界。**本方法从不等待 worker**。
- 命令边界口径：命令随请求整批交给 worker，在**该批第一个运动 tick 开始前**按 `Sequence` 应用（`PaintDisk`/`EraseDisk`/`SpawnCreature`/`SetPaused`/`Resize`/`ClearAll`/`FillRect`/`SetLightSettings`/`SetSimulationSettings`/`ProbeCell`/`EnterHighSpeed`/`ExitHighSpeed`/`PresentNow`）；高倍速下同一批命令也只在第一个待执行 tick 前应用一次。入队顺序即应用顺序 ⇒ 命令日志就是重放 / 确定性测试的权威输入。`ProbeCell` 在**被应用的那一刻**采一份 `CellProbe`（此后不再重采，见 SimulationCore 文档），故它读到的是同批**之前**那些命令的结果。
- 音频的**清账归采集侧**（worker 的 `PublishFrame` 里）：发布前抽干一次 `LiquidMotion`/`SolidMotion`/`Counts` 并写进 `AudioFrame`，消费者只读快照、不再清账（多个消费者读同一帧各拿各的）。

- **倍速**：每真实帧跑几个运动帧由**主线程**的 `SpeedController` 按「一帧名义时间（`Application.targetFrameRate`，≤ 0 按 60）× `_speedBudgetFraction`（0.5）」的预算给出，上限再按每 tick 耗时 EMA 动态折算（耗时样本来自 worker 回执里的实测毫秒数），倍率上限 `_speedMax`（16）。**普通编辑模式恒 1×**；tick 数按 `1f/motionRate` 的 float 步长重复扣减，预算未截断时与旧固定累加器逐帧逐位一致。超预算直接丢时间、绝不结转（防死亡螺旋），预算再紧保底 1 个运动 tick。`EffectiveSpeed`（EMA(实际 tick) ÷ (EMA(dt) × 速率)，稳态 1× 归一）只作显示，不参与调度。worker 侧**没有第二套累加器**：它只收到「跑几个 tick」这个有界请求。
- 驱动**不暴露**任何活世界：`Pause()`/`Resume()`/`Paused`/`World`/`Creatures`/`HighSpeed`/`Speed` 一概没有——暂停、重建、高速进出、探测全部走命令；当前是否暂停读 `CurrentFrame.World.Paused`。**不含输入判断**（空格暂停热键在 `ParticleEditor`）。
- **生命周期**：`Awake` 建 worker（世界与第一帧在调用线程上同步建好，消费方随后立刻有帧可读）并启线程；`OnEnable` 兜底重启；`OnDisable`/`OnDestroy` 停止并 `Join`——**绝不让线程活过 Unity 对象销毁**（域重载 / 退出播放都走这条）。worker 故障经回执上报，由 `Update` 在主线程 `Debug.LogError` 一次（worker 自己从不碰 Unity API）；世界的诊断消息（未注册类型 / shade 越界）经 `ParticleWorld.MessageSink` → worker 队列 → 主线程记录。
- 配置走 `ParticleSimulationSettings`（ScriptableObject，`Assets/Data/`）：网格尺寸、`_motionRate`/`_logicRate`、`_speedBudgetFraction`/`_speedMax`、`_randomTicksPerStep`、`_highSpeedLogicFrames`（6000）、热力学参数。采样成 `SimulationTickSettings` / `LightSettings` 两个纯值快照后作为命令载荷交给 worker。

## 高速模式（H）

状态机与统计在内核 `HighSpeedSession`（纯 C# 纯观察者——零 RNG 消耗、零格子写入；O(1) 增量统计每种粒子生灭，每 10 逻辑帧采样地表平均温度与世界平均湿度，跑满时做一次全网格存量扫描 `GetFinalCount`）。**会话是 world 的私有一员，且与 world 同线程**（都归 worker）：状态、进度、采样与各类型产生 / 死亡 / 终态存量都在 `HighSpeedFrame` 上（`SimulationFrame.HighSpeed`）。进入 / 退出是命令（`EnterHighSpeed` / `ExitHighSpeed`），输入封锁与左下角进度 / 结算确认在 `ParticleEditor`、音效整层静音在 `SoundController`（`HighSpeedSilenced` = 帧里的 `HighSpeedFrame.Active`：全部声源硬停、两个播放入口丢弃）。worker 侧只做懒建会话、每逻辑帧 `OnLogicTick()`、`Completed` 时就地暂停（世界恰停在 `DurationLogicTicks` 这一刻）；**倍速钉档在主线程**——驱动按帧里的 `Active` 变化把 `SpeedController` 钉满档 16× / 退出回 1×（`SpeedController` 不跨线程）。会话期间 `ProbeText`（左上角）改显示**高速状态行** `正在运转 ×7.6 地表 12.3° 湿度 0.456`（`DescribeHighSpeedRunning`，格式固定：状态词 + 实测倍速 + 地表温度 + 湿度；未采样显示 `--`），左下角进度行照旧。

## ParticleEditor（输入与投放）

- 左键刷当前粒子；右键循环切换类型（末尾接动物物种兔子/鱼/鸟，物种表在 `CreatureSpawns`——选中后左键提交 `SpawnCreature` 命令，脚底中心落格中心，与单颗投放共用 500ms 冷却，不做地形校验——点进石头里会卡住，已知限制）。
- 滚轮调半径 1..10；**Shift+滚轮**切换树木物种（HUD 显示物种名）。
- 选中项由公开描述符 `ParticleSelectionEntry` 表示——`Selections`/`CurrentSelection`/`Select`/`SelectByTypeId`/`SelectByName`/`SelectionChanged` 是投放面板按钮（`ParticleSelectionPanel`）的接线入口；白名单或阶段名单变化时列表整体重建、旧条目作废。面板按当前列表**逐颗显隐**十颗投放按钮：本关未解锁的类型（阶段名单剔掉的）整颗隐藏，解锁后按场景里的原位置重新出现；「清除」按钮不受影响。
- **`ProbeText`（左上角）有三段来源，按优先级取一**（`ComposeProbeLine`）：**高速会话状态行** `正在运转 ×7.6 地表 12.3° 湿度 0.456`（`DescribeHighSpeedRunning`，格式固定 = 状态词 + 实测倍速 + 地表温度 + 湿度）> **悬停的投放项名**（`ParticleSelectionPanel` 给每颗投放按钮挂 `EventTrigger`（`UiHoverWiring.Attach`，运行期绑定、幂等、只销毁自己加的），指针进入时把该槽**已解析的**条目经 `SetHoveredSelection` 报上来，移开 / 面板收起即撤下；绑不上可投列表的隐藏槽与「清除」槽不报）> **光标格读数**。高速会话锁编辑、悬停时那格读数也没有意义，因此按这个顺序压过去；`_drawHud` 关时整行清空。
- **清除模式**（`EraseMode`）：`E` 按下置真/抬起置假（边沿事件，按住查询会每帧抹回假）；投放面板「清除」按钮 `ToggleEraseMode()` 翻转；为真时左键提交 `EraseDisk` 同半径清成空气。
- **本类不写世界**：投放 / 擦除 / 生成实体 / 暂停 / 高速进出 / 上屏一律 `driver.Submit(...)`（`SubmitPlacement` 是放置的公开入口）；光标格读数走 `ProbeCell` 命令 + 帧里的 `WorldFrame.Probe`（`DescribeCell(CellProbe)`）——驱动在命令被应用那一刻采快照、之后不再重采，因此**光标格变了、世界又推进了一 tick、或帧里没有有效读数（刚重建世界）时这里就重发一次请求**（没有 tick 的渲染帧与暂停中未移动光标都不发命令），读数才跟着世界走；高速读数 / 结算走 `HighSpeedFrame`。单颗投放与生成实体的 500ms 冷却留在输入侧（纯节流）；暂停中画完会补一条 `PresentNow`，当帧就能看到。
- 空格暂停/恢复（提交 `SetPaused`）；`L` 开关光照面板（面板已无文本输入，键盘热键不必让位；高速模式期间锁死——进入即强制隐藏，退出后按原状态放回）。
- **光照面板**（`HUD Canvas/LightPanel`）：出厂默认**白天 15 秒 / 夜晚 15 秒 / 强度 15**（场景里 `_litTicks = _darkTicks = 300`、`_intensity = 15`）。**强度 = `Slider`（0..30 整数档）**，`onValueChanged` 写 `ParticleLightDriver.Intensity`；**白天 / 夜晚 = 秒读数**（普通 `TMP_Text`，如 `15秒`，按 1 秒 = 逻辑帧速率（`ParticleSimulationSettings._logicRate`，默认 20）刻实时换算）；两行各 4 颗 `-5秒`/`-1秒`/`+1秒`/`+5秒` **秒步进按钮**（`onClick` = 场景里的整型持久监听 → `SandboxHud.AddLitSeconds`/`AddDarkSeconds`，结果钳在 `[0, int.MaxValue]`）。`SandboxHud` 每帧把滑块与读数回填成驱动器真值，写的是同一份字段（详见 `ParticleTypeInterface.md` §10「光照面板」）。
- 按住 ←/→/A/D 把相机在沙箱内左右平移（视口夹在网格边界内，暂停与高速模式都能用）。
- `H` 进入高速模式。
- 光标处常驻一圈白色像素圆标出笔刷足迹（圈上的格恰好是左键会写到的格，见 Rendering 文档）；光标出网格/高速锁编辑/对话激活时撤下。
- 不画 IMGUI：每帧把字符串推给 `SandboxHud`（`PushHud`）；面板使用期间的放置屏蔽查 `SandboxHud.IsPointerOverPanel`。光照面板已无文本输入，键盘热键不必让位。
- **输入换算**：相机画进纹理后 `ScreenPointToRay` 吃纹理像素，`TryGetCell` 先用 `CameraScreenFeed.ToCameraPixel` 换算（见 Rendering 文档）。
- 投放白名单见 `docs/modules/Particles.md` 末节。

## 声音

`SoundController`：每条音频一条独立循环声床（与 `_clips` 下标一一对应、惰性建源、永久复用）+ 一个一次性音效源。`SetAmbience(文件名, 目标响度)`/`StopAmbience(文件名)` 只作用点名声床（水火同响）；`PlayOneShot` 走独立 `loop=false` 声源、累计 `OneShotPlayCount`。查询 `TargetLoudness`/`CurrentLoudness`/`IsPlaying`/`FindClip`（最后一段、去扩展名、大小写不敏感，未知名只警告一次）。`ClampDeltaTime`（上限 0.25s）防异常 dt 跳变；纯数学 `NextVolume`/`MapLoudness`/`Ema` 公开静态供各驱动复用。高速模式整层静音（见上）：目标响度照记、退出后原样淡入。`OnDisable` 归零目标并 `StopAllSources` 硬停、`OnDestroy` 销毁自建子物体；声源一律 2D + `HideFlags.DontSave`。

四个声音驱动（每帧读**帧快照的 `AudioFrame`** → `SetAmbience`/`PlayOneShot`，`Pump(frame)` 公开供测试脱离帧循环驱动）：

- `LiquidFlowSoundDriver`：`frames.Audio.LiquidMovingRatio` → 死区 0.15 + 滞回 0.03 → 比例→响度（满响 0.6）→ EMA 0.25s。
- `FireSoundDriver`：`frames.Audio.FireCount` 火格数（不是比例）→ `MapLoudness(0..40)` → EMA 0.25s。
- `LightningSoundDriver`：`frames.Audio.LightningCreated > 0` 就 `PlayOneShot` 一声（created 是格数——一次起爆几十格，故一帧最多一声）；按帧**身份 + 序号**去重，同一帧读两次也只响一次。
- `FallingSandSoundDriver`：`frames.Audio.SolidMovingCount` 绝对运动格数 → `MapLoudness(0..40)`。**无 EMA/滞回**——固体落地即静止、无噪声底；口径 = Solid 且非 Organic（落叶/种子不触发）。

**运动类信号在陈旧帧上按 0 处理**（**同一帧对象且序号没变** = 自上次读取以来没有新发布，例如暂停中不发布）：与旧版「每帧读后清账 ⇒ 读到 0」逐帧同义，因此死区 / 滞回 / EMA 的既有行为不变；火焰按**存量**口径沿用上次读到的值（火格数不是本帧增量）。新鲜度判定必须**帧身份 + 序号**一起看：帧槽会回收复用，而每个发布器的 `Sequence` 都从 1 开始，只比序号会把别的发布器（别的世界 / 测试里的第二个世界）的第 1 帧误判成陈旧。

## 沙箱演示

`ParticleSandboxSeeder` 默认不跑，右键菜单 `Reseed()` 才搭演示容器——它把「清场 + 铺地板 + 砌墙 + 堆沙」提交成 `ClearAll` / `FillRect` 命令（尺寸取自帧），最后补一条 `PresentNow`，本身不写世界。

## 游戏流程层

`GameController`（阶段 JSON 驱动薄壳：读档 → `StartPhase`（按解锁集合选 `dialogueId`/`repeatDialogueId` 播对话 → 提交 `SetHighSpeedLogicFrames(simulationTicks)`（每阶段模拟时长；0 = 设置资产默认）+ `Resize` 命令重建沙箱 → 粒子 / 动物投放白名单 → 自动存档）→ 高速模式结算（`successDialogueId` / `failureDialogueId` 配了就先播结算对话、**播完才切阶段**，见 `docs/GameFlow.md` §4.4）→ 过关推进 `nextPhaseId`/失败重来，`JumpToPhase` 按解锁门槛跳关；阶段**没有 `victory`**（沙盒 `phase_05`）时高速模式跑完不判定、不推进、不播结算对话）。高速会话的观察者读**帧**里的 `HighSpeedFrame`（`Active`/`Completed`/采样/产生死亡存量），因此它拿不到 `HighSpeedSession`；结算快照 `BuildSnapshot(HighSpeedFrame, CreatureFrame)`（粒子统计 + 同一帧的逐物种存活数）供纯 C# `VictoryEvaluator`（吃 `HighSpeedSnapshot`、输出布尔 + 第一条未满足项理由）。另有 `GamePhaseData`/`GamePhaseParser`（纯 C# 阶段解析：`simulationTicks`、`availableParticles`、`availableCreatures` 与温度 / 湿度 / 粒子 / 动物约束的哨兵值表「不约束」，全哨兵的 `victory` 归一为 `null`）。存档格式唯一权威也在 `GameController`（`SaveData` + 静态 `SavePathFor`/`LoadSave`/`CollectUnlockedPhaseIds`/`WriteSave`），供选关界面复用。阶段 `Assets/Data/Phases/phase_01..05.json`；唯一权…

`LevelSelectController`（**关卡选择界面**，`Assets/Scenes/LevelSelect.unity` 的根对象 `Level Select Controller`）：按存档解锁集合逐颗 `SetActive` 五颗按钮（Day0..Day3 + 沙盒 = `phase_01..phase_05`，未解锁整颗隐藏；解锁集合为空时只显示第一关当入口），点选经 `CommitSelection` 写档后 `SceneManager.LoadScene("MainScene")`；**悬停**在关卡按钮上时把该关阶段 JSON 的 `description` 写到 `Canvas/ProbeText`（`EventTrigger` 在 `Awake` 运行期绑定——与投放面板共用 `UiHoverWiring.Attach` / `Detach`，非悬停时隐藏）。**LevelSelect 与 MainScene 都在 Build Settings**（`MainScene` 下标 0）。场景接线由 `LevelSelectSceneWiringTests` 钉住。

## 对话系统

`DialogueData`（纯 C# JSON）+ `DialogueRunner`（纯 C# 状态机）+ `DialogueController`（MonoBehaviour：打字机协程/UI 无关核心/热键/暂停模拟；显示走 `IDialogueView`，自带 `DialogueDebugView`）。对话激活时提交 `SetPaused(true)`（是否「本对话造成的暂停」读帧里的 `WorldFrame.Paused`）+ `ParticleEditor.SuppressInput = true`。权威文档 **`docs/DialogueSystem.md`**。
