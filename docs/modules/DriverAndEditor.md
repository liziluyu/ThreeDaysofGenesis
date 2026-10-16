# 驱动层与编辑器交互（TapTap2026.Game / Driver）

`Assets/Scripts/Driver/`。引用 Simulation。

## 两级时基与驱动

`ParticleSimulationDriver`（MonoBehaviour）用 `SpeedController`（纯 C#，预算式）推进两级时基：运动帧 60 Hz（`world.StepMotion()`）与逻辑帧 20 Hz（`world.StepLogic()`，逻辑帧是运动帧节拍上的子节拍，每 `LogicTicksPerMotionTick = round(_motionRate/_logicRate)` 个运动帧一次）；上屏每真实帧最多推一次纹理。

- **倍速**：每真实帧跑几个运动帧由 `SpeedController` 按「一帧名义时间（`Application.targetFrameRate`，≤ 0 按 60）× `_speedBudgetFraction`（0.5）」的预算给出，上限再按每 tick 耗时 EMA 动态折算，倍率上限 `_speedMax`（16）。**普通编辑模式恒 1×**（调速热键已移除；`SetSpeed`/`SetAdaptive` 只被高速模式会话调用）；tick 数按 `1f/motionRate` 的 float 步长重复扣减，预算未截断时与旧固定累加器逐帧逐位一致。超预算直接丢时间、绝不结转（防死亡螺旋），预算再紧保底 1 个运动 tick。`EffectiveSpeed`（EMA(实际 tick) ÷ (EMA(dt) × 速率)，稳态 1× 归一）只作显示，不参与调度。自适应档 AIMD 保留但生产不启用。驱动只做 `Stopwatch` 测量与转发。
- 驱动只暴露状态与方法——`Pause()`/`Resume()`/`Paused`，**不含输入判断**（空格暂停热键在 `ParticleEditor`）；`_simulate` 关掉即暂停全部帧。
- 配置走 `ParticleSimulationSettings`（ScriptableObject，`Assets/Data/`）：网格尺寸、`_motionRate`/`_logicRate`、`_speedBudgetFraction`/`_speedMax`、`_randomTicksPerStep`、`_highSpeedLogicFrames`（6000）、热力学参数。

## 高速模式（H）

状态机与统计在内核 `HighSpeedSession`（纯 C# 纯观察者——零 RNG 消耗、零格子写入；O(1) 增量统计每种粒子生灭，每 10 逻辑帧采样地表平均温度与世界平均湿度，跑满时做一次全网格存量扫描 `GetFinalCount`）。输入封锁/左下角进度/结算确认在 `ParticleEditor`、音效整层静音在 `SoundController`（`HighSpeedSilenced` = 驱动的 `HighSpeedActive`：全部声源硬停、两个播放入口丢弃，声音驱动零改动）。驱动只做懒建会话、转发每个逻辑帧给 `OnLogicTick()`、`Completed` 时就地 `Pause()`（世界恰停在 `DurationLogicTicks` 这一刻）。进入钉满档 16×、退出钉回 1×。

## ParticleEditor（输入与投放）

- 左键刷当前粒子；右键循环切换类型（末尾接动物物种兔子/鱼/鸟——选中后左键**放置实体** `SpawnCreature`，脚底中心落格中心，与单颗投放共用 500ms 冷却，不做地形校验——点进石头里会卡住，已知限制）。
- 滚轮调半径 1..10；**Shift+滚轮**切换树木物种（HUD 显示物种名）。
- 选中项由公开描述符 `ParticleSelectionEntry` 表示——`Selections`/`CurrentSelection`/`Select`/`SelectByTypeId`/`SelectByName`/`SelectionChanged` 是投放面板按钮（`ParticleSelectionPanel`）的接线入口；白名单或阶段名单变化时列表整体重建、旧条目作废。
- **清除模式**（`EraseMode`）：`E` 按下置真/抬起置假（边沿事件，按住查询会每帧抹回假）；投放面板「清除」按钮 `ToggleEraseMode()` 翻转；为真时左键改走 `Erase()` 同半径清成空气。
- 空格暂停/恢复；`L` 开关光照面板（输入框拿键盘时让位；高速模式期间锁死——进入即强制隐藏，退出后按原状态放回）。
- 按住 ←/→/A/D 把相机在沙箱内左右平移（视口夹在网格边界内，暂停与高速模式都能用）。
- `H` 进入高速模式。
- 光标处常驻一圈白色像素圆标出笔刷足迹（圈上的格恰好是左键会写到的格，见 Rendering 文档）；光标出网格/高速锁编辑/对话激活时撤下。
- 不画 IMGUI：每帧把字符串推给 `SandboxHud`（`PushHud`）；键盘让位/放置屏蔽查 `SandboxHud.LightFieldFocused`/`IsPointerOverPanel`。
- **输入换算**：相机画进纹理后 `ScreenPointToRay` 吃纹理像素，`TryGetCell` 先用 `CameraScreenFeed.ToCameraPixel` 换算（见 Rendering 文档）。
- 投放白名单见 `docs/modules/Particles.md` 末节。

## 声音

`SoundController`：每条音频一条独立循环声床（与 `_clips` 下标一一对应、惰性建源、永久复用）+ 一个一次性音效源。`SetAmbience(文件名, 目标响度)`/`StopAmbience(文件名)` 只作用点名声床（水火同响）；`PlayOneShot` 走独立 `loop=false` 声源、累计 `OneShotPlayCount`。查询 `TargetLoudness`/`CurrentLoudness`/`IsPlaying`/`FindClip`（最后一段、去扩展名、大小写不敏感，未知名只警告一次）。`ClampDeltaTime`（上限 0.25s）防异常 dt 跳变；纯数学 `NextVolume`/`MapLoudness`/`Ema` 公开静态供各驱动复用。高速模式整层静音（见上）：目标响度照记、退出后原样淡入。`OnDisable` 归零目标并 `StopAllSources` 硬停、`OnDestroy` 销毁自建子物体；声源一律 2D + `HideFlags.DontSave`。

五个声音驱动（每帧读世界观察者 → `SetAmbience`/`PlayOneShot`）：

- `LiquidFlowSoundDriver`：`world.LiquidMotion.MovingRatio` → Reset → 死区 0.15 + 滞回 0.03 → 比例→响度（满响 0.6）→ EMA 0.25s。
- `FireSoundDriver`：`world.Counts.Count(ParticleIds.Fire)` 火格数（不是比例）→ `MapLoudness(0..40)` → EMA 0.25s。
- `LightningSoundDriver`：每帧**无条件** `ConsumeCreated(ParticleIds.Lightning)` 先清账再判开关，`> 0` 就 `PlayOneShot` 一声（created 是格数——一次起爆几十格，故一帧最多一声）。
- `FallingSandSoundDriver`：`world.SolidMotion.MovingCount` 绝对运动格数 → `MapLoudness(0..40)`。**无 EMA/滞回**——固体落地即静止、无噪声底；口径 = Solid 且非 Organic（落叶/种子不触发）。
- `AnimalSoundDriver`（动物声音，§16）：**按物种存在与否**间歇放一次性音——每帧 `for` 遍历 `CreatureWorld.Creatures` 只判「该物种在不在场」，每物种一个倒计时，到点 `PlayOneShot(idleClip, 音量)` 并**重摇** `[min, max]` 间隔（间歇而非节拍器）；物种消失即解除武装（重现要等完整间隔）。另接 `CreatureSpawned`/`CreatureDied` 各一声（**每物种每路每帧最多一声**，同帧死 5 只折叠成一声；死声/生声文件名留空回落用闲聊声）。`Pump(creatures, dt)` 公开（返回本次出声数）供测试脱离帧循环驱动；**自带一条 xorshift32**（不碰粒子/动物两条流）；`HighSpeedSilenced` 期间整段跳过（不倒计时、不摇骰、事件留到退出后补）。默认 兔 6~14s / 鸟 5~12s / 鱼 8~18s。

## 沙箱演示

`ParticleSandboxSeeder` 默认不跑，右键菜单 `Reseed()` 才搭演示容器。

## 游戏流程层

`GameController`（阶段 JSON 驱动薄壳：读档 → `StartPhase`（按解锁集合选 `dialogueId`/`repeatDialogueId` 播对话 → `Resize` 重建沙箱 → 投放白名单 → 自动存档）→ 高速模式结算 → 过关推进 `nextPhaseId`/失败重来，`JumpToPhase` 按解锁门槛跳关）+ `GamePhaseData`/`GamePhaseParser`（纯 C# 阶段解析，哨兵值表「不约束」）+ `VictoryEvaluator`（纯 C# 胜利判定：吃 `HighSpeedSnapshot`、输出布尔 + 第一条未满足项理由）。示例阶段 `Assets/Data/Phases/`；唯一权威文档 **`docs/GameFlow.md`**。

## 对话系统

`DialogueData`（纯 C# JSON）+ `DialogueRunner`（纯 C# 状态机）+ `DialogueController`（MonoBehaviour：打字机协程/UI 无关核心/热键/暂停模拟；显示走 `IDialogueView`，自带 `DialogueDebugView`）。对话激活时 `driver.Pause()` + `ParticleEditor.SuppressInput = true`。权威文档 **`docs/DialogueSystem.md`**。
