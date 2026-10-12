# 动物 AI 与生态交互设计——兔子、鱼和生物质

> 状态：AI 基线与生态扩展（§14–§21）**均已实现**（2026-10-11），实施计划见 `docs/plan-creature-ecology.md`。
> 本文前半部分记录动物 AI 基线；§14 起记录食草、死亡物质化和生物质腐败的落地契约。
> 读者约定：文中的「逻辑帧」= 20 Hz 的 `CreatureWorld.Step` 节拍（计数器 `CreatureWorld.LogicTicks`）。
> **不要**用 `ParticleWorld.Tick`——那是运动帧（60 Hz）计数器。

## 1. 目标与约束

基线行为包括兔子漫步、避火、鱼巡游、浮力和环境致死；生态扩展增加兔子食草、饥饿死亡，以及动物死亡后的生物质粒子循环。

- **性能**：个体几十个量级，每逻辑帧的 AI 总开销必须远小于粒子世界一个 O(W·H) pass（320×180 = 57,600 格）。
- **视觉**：20 Hz 决策下动作不跳变、不鬼畜（转身有弧线、朝向不抖动）；死亡轮廓必须能下落、堆积并可见地腐败。
- **确定性**：同 seed 同操作逐位复现；动物 AI **绝不消耗粒子世界随机流**，只用 `CreatureWorld` 自带 xorshift32；生物质作为粒子，其腐败随机刻使用 `ParticleWorld` 随机流。
- **零分配**：思考 / 运动 / 感知 / 生物质行为路径不允许堆分配、不允许 LINQ / 字符串 / `Time.*` / `UnityEngine.Random`。
- **分层写入**：动物物理与感知仍只读世界；食草和死亡物质化是明确的世界写入阶段，必须通过 `ParticleWorld.Clear` 或新增的原子 `SetWithShade`；不得绕过世界 API。
- **不做全局寻路**：兔子只在半径 6 的局部范围选择食物，目标不可达时放弃，不引入 A* 或势场。

## 2. 架构总览

```
CreatureWorld.Step（每逻辑帧 20 Hz）
  ① 快照 Prev = Cur
  ② 逐只推进（顺序 = 生成顺序）：
       c.OnLogic(creatures, world)     ← 门控后调静态 Brain.Think（2 Hz）
       c.ApplyLocomotion()             ← 每帧：意图 → 速度（加速度限幅）+ 环境致死检查
       c.StepPhysics(world)            ← 重力 / 浮力 / 碰撞
  ③ 倒序清扫死亡：捕获死亡快照 → 死亡物质化器写入生物质 → OnDeath / CreatureDied → 移除实体
ParticleWorld.StepMotion / StepLogic
  ④ 生物质作为普通粒子：独立下落、堆积、随机刻腐败
渲染（每真实帧）
  ⑤ 动物：位置插值与 sprite；生物质：单 quad 粒子纹理
```

| 决策点 | 选择 | 理由 |
|---|---|---|
| 决策组织 | **独立 Brain 类**（`RabbitBrain` / `FishBrain`，**静态无状态**） | 状态全在 Creature 黑板槽位，Brain 只是思考函数容器；零分配、天然无「共享状态」bug |
| 黑板存储 | **基类内联槽位** | 零分配；生态扩展继续复用同一黑板，只追加饥饿与目标字段 |
| 思考频率 | **每 10 逻辑帧（2 Hz）+ SensePhase 错峰** | 决策成本 ÷10；0.5s 反应延迟换取可忽略的扫描开销 |
| 运动接口 | **意图 + 加速度限幅** | 转身是减速→反向加速的弧线，杜绝瞬间镜像鬼畜 |
| 威胁感知 | **决策帧半径扫描**（r = 6 圆盘） | 错峰后每帧摊分 ~11 格读；无需维护全局危险场 |

## 3. 黑板槽位（`Creature` 基类新增字段）

| 字段 | 类型 | 含义 |
|---|---|---|
| `AiState` | `byte` | 物种自定义状态编号（兔子：0=Idle 1=Wander 2=Flee；鱼：0=Swim 1=Stranded）。语义按物种复用 |
| `StateTicksLeft` | `int` | 当前状态剩余逻辑帧数，Brain 置值，递减到 0 触发转移 |
| `SensePhase` | `byte` | 思考相位 ∈ [0, ThinkEvery)，**Spawn 时由 CreatureWorld 钉死**（生成计数器 % ThinkEvery），死亡移除不影响他者相位 |
| `IntentVx` | `float` | 目标水平速度（格/逻辑帧），运动层逼近的目标 |
| `IntentVy` | `float` | 目标竖直速度（鱼水中用；兔子恒 0） |
| `WantHop` | `bool` | 起跳请求，运动层在 `IsGrounded` 时消费成 `VelocityY` 脉冲并清零 |

另有两个**环境致死计时器**：`SubmergedTicks`（连续淹没帧数）、`BeachedTicks`（连续离水帧数，鱼用；兔子复用 `SubmergedTicks` 判溺死）。二者也由运动层每帧维护。

还有一个**帧内信号**（**不是**黑板槽位——Brain 不读写、不进渲染插值、不进 `AgeTicks`，只连接物理与运动层相邻两步）：`Creature` 私有的 `_blockedXDir` / `_blockedXAtWorldEdge` =「上一次 `StepPhysics` 的横向积分被挡住」的方向（+1 右 / −1 左 / 0 未被挡）与成因（撞固体 vs 被世界边界夹取）。每帧 `StepPhysics` 开头清零、横向扫描撞固体或边界夹取时置位；消费方只有运动层第 ④ 段（撞墙翻越 / 掉头）。

## 4. 思考门控与错峰

```csharp
protected bool ShouldThink(CreatureWorld creatures)
    => creatures.LogicTicks % ThinkEvery == SensePhase;
```

- `ThinkEvery = 10`（常量，调这个值即可整体调节思考频率）。
- `CreatureWorld` 持有 `_spawnCounter`，`Spawn` 时 `creature.SensePhase = (byte)(_spawnCounter++ % ThinkEvery)`。
- 同批生成的动物相位相同是**可接受**的（最坏 30 只同帧思考 = ~450 格读/帧，仍远低于一个世界 pass）。
- **RNG 纪律**：`creatures.NextUInt()` / `NextInt()` 只允许出现在 Brain.Think 内（思考帧才消耗），扫描顺序固定（从左到右、从下到上）——两者共同保证确定性。

## 5. 运动层（`Creature.ApplyLocomotion`，每逻辑帧）

思考降频后，**每帧仍要做**的 O(1) 小事全部收在这里（这些不是「思考」，是反射）：

1. **意图逼近**：`VelocityX = MoveTowards(VelocityX, IntentVx, Accel)`；鱼在水中另做 `VelocityY = MoveTowards(VelocityY, IntentVy, SwimAccel)`（离水后 `IntentVy` 不生效，重力接管）。
2. **朝向迟滞**：仅当 `|IntentVx| > 0.001` 时 `Facing = sign(IntentVx)`——渲染无脑读 `Facing`，永不抖动。
3. **悬崖守卫**（兔子 Wander 态、Flee 态**除外**——逃命优先）：`IsGrounded` 且前进方向下一格的**脚下 2 格内无支撑** → `IntentVx` 取反。
4. **撞墙：跨坎 / 掉头**：跨坎在**物理层**完成（见 §5.1）；运动层这一条只处理「跨不过去」的情况——`IsGrounded` 且**上一步物理的横向积分确实被挡住**（`Creature` 的帧内信号 `_blockedXDir`：+1 向右 / −1 向左，且与当前 `IntentVx` 同号）且 `|IntentVx| > 0` → 调 `ProbeWallAhead` 取三态，按态分流：`Blocked`（≥两格高 / 无余量）→ `IntentVx` 取反；`Climbable` 但本物种 `CanStepUp = false`（鱼）→ `IntentVx` 取反；`Climbable` 且能跨、`None` → **什么都不做**。撞**世界左右边界**时（`_blockedXAtWorldEdge`）**单独分流**为 `IntentVx` 取反。

### 5.1 跨坎（`CanStepUp`，写在物理层的横向积分里）

横向扫描撞到固体时，若该列是**脚底行的一格坎**（挡路固体就在 `rowMin`、抬升一格后 `rowMin+1 … rowMax+1` 全空，即探针答 `Climbable`）且物种 `CanStepUp`：

```text
Y += 1; rowMin++; rowMax++; 重测同一列（每帧至多抬一格）
```

于是**本帧的水平位移照常走完**，位移连续、不需要速度或弹道参与；随后由本帧的竖直收尾重新判定支撑与边界（不会穿墙）。兔子 `CanStepUp = true`（否则会被沙堆 / 台阶的每一格坎永久卡住），鱼为 `false`。

- **为什么跨坎必须在物理层、且必须继续走完位移**：只在运动层抬格的话，兔子在**贴墙前**就抬了格，坎仍在前方一列 ⇒ 重力立刻把它拉回原地，「抬格 → 落下 → 被挡 → 再抬格」无限循环，永远踏不上去（实测：抬升发生但 120 帧内 X 过不了坎所在列）。
- **为什么不是跳过去**：起跳能提供的横移 = 行走速度 × 「脚高于坎顶」的帧数；`gravity 0.24` + `HopVelocity 0.75` 约 1.4 帧 ≈ 0.08 格，而越过一格坎需要约 2.5 格（半宽 0.75 + 坎宽 1 + 对侧 0.75）——差一个量级，弹道抬到真能过坎需 ≈2.5 格高的跳跃（观感 / 数值都不可接受）。`WantHop` 机制保留给鱼搁浅扑腾。
   - **为什么入口不能用 `|VelocityX| < 0.005`**：贴墙时 ① 的意图逼近每帧仍会给出 `VelocityX = Accel = 0.02`，与阈值同量级 ⇒ 速度**永远进不了那个窗口**，④ 段在正常行走中根本不可达（只剩「顶上 → 被物理归零 → 下帧加速 0.02 → 再顶上」，既不跨坎也不掉头）；反过来，主动转向穿过零点（`MoveTowards` 步长 0.02：0.06→0.04→0.02→**0.00**）那一帧恰好落进窗口，撞墙判定于是被搬到了开阔地上——这正是「兔子朝火走时不断转身、不往对面跑」的根因（实测 200 帧翻转 40 次、净位移朝火 +11.6 格）。触发条件因此换成物理层的事实：`StepPhysics` 在横向扫描撞到固体 / 被世界边界夹取时置位 `_blockedXDir`（每帧开头清零，不残留），速度不再参与判定。
   - **为什么必须三态**：`Climbable` / `Blocked` 对应**两种不同的动作**（跨坎 vs 掉头），布尔探针没有这一区分；`None` 留给「横向扫描覆盖整个身体、而探针只查身体底行」的分歧帧（身体跨行的过渡帧）——此时保守空操作，不反向意图，让下一次扫描在自洽位置上重新回答。
   - **为什么边界要单独区分**：撞世界左右边界时 `X` 被夹取、前方一格越出世界，探针把越界一律当非固体 ⇒ **必答 `None`**。若与「前方是开阔地」共用 `None` 分支（空操作），动物会永远顶着边界原地蹭；边界在此等价于不可翻越的实心体，直接 `IntentVx` 取反。信号的成因因此分「撞固体 / 被边界夹取」两种。
5. **起跳消费**：`WantHop && IsGrounded` → `VelocityY = HopVelocity`，`WantHop = false`。
6. **接触火即死**：身体中心格（`floor(X)`, `floor(Y + Height/2)`）是 `ParticleIds.Fire` → `Kill(Burned)`。
7. **掩埋致死**（`Crushed`，2026-10-12 新增）：用与物理同一套覆盖口径算身体矩形（`colMin/colMax`/`rowMin/rowMax` 都带 `Epsilon`），**每一格**都是**静止**的固体（`CreatureTerrain.IsSolid` 且 `!world.HasMomentum`）⇒ `BuriedTicks++`，否则清零；`> BuriedTicksLimit`（10 帧 = 0.5 秒）→ `Kill(Crushed)`。门控 `DiesWhenBuried` **默认 true**（埋压是对任何实体都成立的物理挤压，与该组其余 opt-in 虚属性不同）。两个防误杀细节：**覆盖物必须静止**（正在下落的沙柱穿过身体不算被埋）+ **0.5 秒宽限**（落沙擦过不瞬间判死）。
8. **溺死**（兔子）：`IsSubmerged` → `SubmergedTicks++`，否则清零；`> DrownTicksLimit`（虚属性，默认 160 帧 = 8 秒）→ `Kill(Drowned)`。
9. **搁浅**（鱼）：非 `IsSubmerged` → `BeachedTicks++`，否则清零；`> 200`（10 秒）→ `Kill(Beached)`。

物种常量（虚属性加到 `Creature`，或 Brain 私有常量——**选定：放各 Brain 里**，基类只留槽位与通用机制）：

| 常量 | 兔子 | 鱼 |
|---|---|---|
| `Accel`（格/帧²） | 0.02 | 0.01 |
| `SwimAccel`（格/帧²） | — | 0.1 |
| 竖直意图速度 `\|IntentVy\|`（格/帧） | — | 0.1 |
| 下潜判定带 `SurfaceClearanceCells`（格） | — | 8（= `ArtHeightCells` 7 + 1） |
| `HopVelocity`（格/帧） | 0.75 | 0.5 |
| 走/游速 `\|IntentVx\|` | 0.06 | 0.05 |
| Flee 速度 | 0.15 | — |
| `FleeExitMargin`（脱离滞回，格） | 3 | — |
| `FleeMinTicks`（最短逃命，帧） | 40 | — |

通用项（两个物种一致）放进 `Creature`：`ThinkEvery = 10`、`ThreatRadius`（**进入逃命**的扫描半径，**设计值 16**——2026-10-12 定稿，不再计划调回曾经的 6；半径 16 圆盘 ≈ 733 格读，仅思考帧执行）、`DrownTicksLimit`（**虚属性**，默认 160 帧 ≈ 8 秒——物种可自带阈值）、`BeachedTicksLimit = 200`（const）、`BuriedTicksLimit = 10`（const，压死宽限 = 0.5 秒）。逃命专属的两个常数放 `RabbitBrain`（只有兔子会逃命）：`FleeExitMargin = 3`（脱离逃命的半径滞回余量，格——脱离半径 = `ThreatRadius + FleeExitMargin`）、`FleeMinTicks = 40`（最短逃命时长，逻辑帧 ≈ 2 秒）。**注意**：溺死 / 搁浅 / 接触火是通用机制（鱼也怕被烧），由运动层统一跑；物种差异只在「哪个计时器会杀人」——兔子查溺死、鱼查搁浅。实现上：基类每帧维护两个计时器，各自 `Kill` 判定由虚属性 `DrownsInWater` / `DiesWhenBeached`（兔子 true/false、鱼 false/true）门控。**掩埋（`Crushed`）同样由运动层统一跑**，门控 `DiesWhenBuried` **默认 true** ⇒ 两个物种都会被静止固体全覆盖 0.5 秒压死。

物种**线性物理常数**（`Creature` 虚属性；同一条物理公式配不同常数，没有物种特判）：`Gravity` 两物种同为 0.24 格/逻辑帧²、`MaxFallSpeed` 兔 2.4 / 鱼 1.2；`Buoyancy` **兔子 0.15 < 0.24 ⇒ 入水缓沉（≈0.18 格/帧）**、**鱼 0.24 = 重力 ⇒ 中性浮力（水中不浮不沉，深度由 `FishBrain` 的 `IntentVy` 维持）**；`WaterDrag` 兔 0.5 / 鱼 0.6。水中稳态竖直速度 = `(Buoyancy − Gravity) / WaterDrag`（兔子 −0.18 格/帧；鱼 0 ⇒ 只受意图驱动）。

## 6. 感知（`CreatureSensors`，静态，只读世界）

| 探针 | 签名语义 | 成本 |
|---|---|---|
| `FindNearestFire(world, cx, cy, radius)` | 以 `(cx,cy)` 为中心扫**半径由调用方传入**的圆盘（进入逃命 = `Creature.ThreatRadius`；已在逃命 = `ThreatRadius + RabbitBrain.FleeExitMargin`）找 `ParticleIds.Fire`，返回**距离最近**（距离平方最小，平手保留先见者）的那格坐标（`out int fireX, out int fireY`）。扫描顺序：y 升序、x 升序（固定顺序 ⇒ 平手结果也确定）。**取最近而不是取先见者**是逃命方向的正确性要求：固定扫描序总先命中视野里**最低一行**的火，散开的火场因此会把动物推向更远的那团火 | 随半径平方增长，仅思考帧 |
| `ProbeGroundAhead(world, x, y, facing)` | 前方格 `(floor(x)+facing, floor(y))` 起向下 2 格内是否有 `CreatureTerrain.IsSolid` 支撑 | 2~3 格读，每帧（悬崖守卫） |
| `ProbeWallAhead(world, x, y, height, facing)` → `CreatureSensors.WallAhead` | 前方格 `(floor(x)+facing, floor(y))` **非固体** → `None`（开阔地，调用方不动）；**是固体**且越过身体高度后其上两格都非固体 → `Climbable`；**是固体但不可翻越**（≥两格高 / 实心体）→ `Blocked`。越界格一律按非固体（因此只会得到 `None`；撞世界边界时运动层靠 `_blockedXAtWorldEdge` 分流，不依赖本探针） | ≤3 格读，仅**上一步物理横向被挡**（`_blockedXDir` 与意图同号）且有前向意图时 |

地形判定一律走现有 `CreatureTerrain.IsSolid`（白名单：沙/石/土家族）与 `IsWater`，不新造判定。

## 7. 物种行为

### 7.1 兔子（`AiState`：0=Idle，1=Wander，2=Flee）

- **Idle**：站立（`IntentVx = 0`）。持续 `20 + NextInt(61)` 帧（1~4 秒）。到期 → Wander。小概率（思考帧 1/4）不进 Wander 而是续一个 Idle（原地发呆更久）。
- **Wander**：`NextInt(2)==0 → -1 else +1` 选向，`IntentVx = 向 × 0.06`。持续 `20 + NextInt(41)` 帧（1~3 秒）。到期 → Idle。运动层的悬崖守卫 / 撞墙翻越自动生效。
- **Flee**：思考帧用 `FindNearestFire` 扫火——**进入**用半径 `ThreatRadius`、**已在 Flee** 用 `ThreatRadius + FleeExitMargin`（= 19，滞回）；找到即 `IntentVx = ∓FleeSpeed`（背离**最近**那团火），**不做悬崖守卫**，并只在**刚进入**时写 `StateTicksLeft = FleeMinTicks`（40 帧 ≈ 2 秒）作为最短逃命时长。**火恰与扫描中心同列（`fireX == cx`）时只在刚进入逃命（或当前 `IntentVx == 0`）时摇一次骰选一侧**，此后保持既有逃跑方向——否则每个思考帧都重摇会让兔子在火下方每 0.5 秒翻一次身；`IntentVx = 0` 则会让它站在火正下方不动。火出（放大后的）半径后：最短逃命时长未走完 → 按当前方向继续跑（不改意图）；走完 → 回 Wander（重新摇时长与方向）。**进入半径不因滞回放大**：未处于 Flee 时圈外一格的 `ThreatRadius + 1` 处的火不触发逃命。
- **死法**：接触火 → Burned；连续淹没 > 160 帧 → Drowned。

### 7.2 鱼（`AiState`：0=Swim，1=Stranded）

- **Swim**（水中）：每个思考帧 1/3 概率换向（`NextInt(2)` 选 ±1），`IntentVx = 向 × 0.05`。竖直：**中心格上方水量不足 `SurfaceClearanceCells`（= `ArtHeightCells` 7 格 + 1 = 8 格，向上逐格数水、越界视为非水）** → `IntentVy = −0.1`（下潜）；距底 ≤ 2 格 → `IntentVy = +0.1`（上浮）；否则 `IntentVy = 0`——**中性浮力**（`FishCreature.Buoyancy` = `Gravity` = 0.24）下水阻把竖直速度衰减掉（`V ← 0.4V`）⇒ **保持当前深度**，鱼因此停在水体中段而不是贴着水皮或压着底（旧值的 `Buoyancy 0.25 > Gravity 0.24` 让「中段自由漂」实际恒为缓慢上浮，鱼必然一路浮到水面卡住——2026-10-12 修正）。

**为什么下潜带是 8 格而不是 2 格（2026-10-12 用户要求）**：1 源像素 = 1 格 ⇒ 鱼图案 **7 格高**且**图形底边 = 碰撞盒底边**（切片 pivot 已对齐，见 §8），而碰撞盒只有 0.5 格高。原先的「距水面 ≤ 2 格才下潜」让鱼停在碰撞盒中心距水面 2 格内 ⇒ 7 格高的图案大半露在水面之上（从空中投放后尤其明显）。改成按**图案高度**判定后，鱼稳定停在「中心格 ≤ 水面下 7 格」⇒ **整条可见的鱼没入水下**（这是不改碰撞箱、不改美术尺度时的补偿手段；正解仍是 §22 第 1 条的按物种美术尺度）。**代价**：① 水深 < 8 格的池子里它必然贴底（那时图案无论如何都会出水，物理上无解）；② 它再也不会主动靠到水面（贴水面那一带被判定带排除）。图案高度与常量的一致性由 `CreatureSpriteFacingTests.FishArtHeight_MatchesBrainClearance` 从 PNG 实测守卫（改美术尺度/换图必须同步 `ArtHeightCells`）。

深度修正的力度：每逻辑帧的竖直更新是 `V ← (1 − WaterDrag) · MoveTowards(V, IntentVy, SwimAccel)`（`ApplyLocomotion` 的意图逼近**先于** `StepPhysics` 的阻尼）⇒ **稳态竖直位移/帧 = `(1 − WaterDrag) · min(VerticalSpeed, SwimAccel / WaterDrag)`** = 0.4 × min(0.1, 0.1/0.6) = **0.04 格/帧（≈ 0.8 格/秒 @20 逻辑帧/秒）**，与水平游速（0.05 格/帧 ≈ 1 格/秒）同量级。**两个常数谁小谁封顶**：`SwimAccel` 太小时（< `VerticalSpeed × WaterDrag` = 0.06）水阻每帧吃掉 60% 的逼近量，稳态被压到 0.0133 格/帧（0.27 格/秒 ⇒ 下潜 8 格要 30 秒）——2026-10-12 实测踩过这个坑，因此把 `SwimAccel` 提到 0.1 让它不再封顶。下潜 8 格约需 10 秒。
- **Stranded**（离水，`!IsSubmerged`）：`IntentVx = 0`；思考帧摇 `NextInt(15, 36)`（15~35）作为下次蹦跳间隔，间隔到 → `WantHop = true`（扑腾）。`BeachedTicks > 200` → `Kill(Beached)`。重新入水自动回 Swim。
- **死法**：搁浅超时 → Beached；接触火 → Burned（通用机制）。

## 8. 渲染（`CreatureRenderer` 小改）

- 帧播放速度按状态缩放：`|Vx| > 0.01` → `_idleFps × 2`；兔子 Flee → `× 3`；否则 `× 1`。**不换帧图**（v1 只有 4 帧 idle，差异化靠播放速度 + 程序动作）。
- 跳弧（兔子翻越、鱼扑腾）由 `VelocityY` 脉冲 + 现有位置插值自然呈现，渲染零特判。
- 朝向：渲染读 `Facing`（迟滞在运动层做过了），并按**美术朝向约定**决定是否镜像——`CreatureRenderer.ResolveFlipX(Facing, _spriteFacesLeft)`：源 sprite **头朝左**（当前兔 / 鱼美术实测都是：眼睛在不透明包围盒左半），因此 **`Facing = +1`（朝右移动）时 `flipX = true`**、朝左时不翻。约定写成序列化字段 `_spriteFacesLeft`（默认 `true`）而不是硬编码分支；换美术方向只需改这一个值，`CreatureSpriteFacingTests` 会直接从 PNG 像素核对「美术确实头朝左」与「默认值一致」。
- 摆放锚点：渲染器把真 sprite 的变换放在（插值后的）碰撞盒底边中点，**不额外抬升**（`PivotLift = 0`；只有占位块抬 `Height/2`）。因此**图形相对碰撞盒的位置完全由切片 pivot 决定**：兔子 `pivot.y = 0`（= 画布底边 = 图形底边）、鱼 `pivot.y = 0.3125`（= 画布底上方 5 px = 图形底边）⇒ 两物种的**图形底边都落在碰撞盒底**（脚底），锚点正确。放大倍率是全局 `_spriteScale = 16`（PPU 16）⇒ **1 源像素 = 1 格** ⇒ 图案 11×7（鱼）/ 9×9（兔）格 vs 碰撞盒 1×0.5 / 1.5×1 格——这条尺度不匹配是本文件 §22 记录的第 1 条已知不一致；**AI 侧已按图案高度补偿**（`FishBrain.SurfaceClearanceCells` = 图案 7 格 + 1，见 §7.2：鱼在水量足够的池子里不再贴近水面），但它解决不了「浅水 / 贴底时图案仍出水」与「图案宽度 ≫ 碰撞盒宽度」。

## 9. 性能预算

最坏情形 30 只动物同相位（按**设计值** `ThreatRadius = 16`）：每逻辑帧 3 只思考 ×（733 格威胁扫 + O(1) 探针）+ 全体 30 只 ×（运动层 ~8 格读）≈ **2,440 格读/逻辑帧**，占一个世界 pass（57,600 格）的 **4%**。零分配。不降频不做错峰时是 ≈ 22,000 格读/帧（占 38%）——这就是降频的意义。

## 10. 确定性

- 动物 RNG 独立（`CreatureWorld` 自带 xorshift32，seed = `settings.Seed ^ 0x9E3779B9`，现有）。
- RNG 只在思考帧消耗；感知扫描顺序固定；`SensePhase` 由生成序号决定。
- 回归测试必须证明：**有动物 vs 无动物**，`ParticleWorld` 的 `NextUInt` 流逐位一致。

## 11. 测试清单（`CreatureAiTests.cs`，EditMode）

1. **思考相位错峰**：连生 3 只兔子，`SensePhase` 分别为 0/1/2；`ShouldThink` 在对应 `LogicTicks % 10` 才为真。
2. **确定性重放**：同 seed 两个 `CreatureWorld` + 同种子 `ParticleWorld`，各放一兔一鱼跑 300 逻辑帧 → 每只的位置 / 速度 / `AiState` 逐位一致。
3. **不消耗世界 RNG**：有 / 无动物两种跑法，`world.NextUInt()` 抽样序列逐位一致。
4. **意图限幅**：从静止给 `IntentVx = 0.06`，`VelocityX` 每帧增量 ≤ `Accel`，且最终恰好等于 0.06（不超调）。
5. **朝向迟滞**：`IntentVx` 在 ±0.0005 间抖动时 `Facing` 不变；`IntentVx = -0.06` 持续后 `Facing = -1`。
6. **悬崖折返**：平台边缘的兔子 Wander 若干帧后 `IntentVx` 已反向、且从未掉下去。
7. **威胁方向**：火在右侧 → `FindNearestFire` 返回右侧那格（`fireX > cx`）；兔子下一思考帧 `IntentVx < 0`（逃离）。**最近优先**：远处那团火放在更低的行（固定扫描序先命中它）+ 近处那团与兔子同排 → 兔子背离**近处**那团火逃（方向由最近火格的 `dx` 推出，不是先见者）。
8. **接触火即死**：中心格放火 → 下一逻辑帧 `Alive == false`，死因 Burned。
9. **兔子溺死**：没顶 161 帧后死于 Drowned；159 帧时捞出（排水）则不死。
10. **鱼搁浅**：无水环境 201 帧后死于 Beached；中途回水则计时清零不死。
11. **鱼巡航不出水**：鱼在水中跑 200 帧，中心格始终不越出水面之上 1 格。
12. **Flee 滞回 + 最短时长**：`ThreatRadius − 2` 有火 → 进 Flee（Think 写入 `StateTicksLeft = FleeMinTicks`）；最短逃命时长走完后，火挪到 `ThreatRadius + 1`（进入半径外、脱离半径内）→ 仍 Flee 且逃跑方向不变；再挪到 `ThreatRadius + FleeExitMargin + 1` → 回 Wander。火被清掉后，`FleeMinTicks` 内同样不退出、不改方向，走完后的第一个思考帧才回 Wander。
13. **同列逃命**：火与兔子正上方同列（`fireX == cx`）→ 仍进 Flee 且 `|IntentVx| == FleeSpeed`（进入时随机选一侧跑，不原地不动）；**同列条件在连续 ≥6 个思考帧上持续成立时逃跑方向不重摇**（`IntentVx` 符号不变、`Facing` 至多翻一次）。
14. **进入半径不因滞回放大**：兔子未处于 Flee 时，`ThreatRadius + 1` 处的火不触发逃命、也不写逃命速度。
15. **鱼保持深度（中性浮力）**：水池 0..15、鱼放 (16, 5)（新判定带下真正的中段：`cy = 5`、`cy+8 = 13` 仍是水）跑 300 逻辑帧 ⇒ `|Y − 5| ≤ 1` 且 `Y ≤ 6`（不再浮向水面；旧常数 `Buoyancy 0.25 > Gravity` 会升到 ≈15.75，故本测试对 2026-10-12 的中性浮力改动有判别性）+ **`Y + ArtHeightCells ≤ 16`（整条图案在水下）**。实测 `Y = 5.00` 恒定。
16. **鱼近水面深潜**：鱼放 (16, 15.5) ⇒ 400 帧内降到 `Y ≤ 9`（深潜进下潜带）、`Y ≥ 7`（停在带内而不是一路沉底）+ **`Y + ArtHeightCells ≤ 16`**；再跑 200 帧 `|ΔY| ≤ 0.5`。实测 `Y@400 = 7.50`（带 [7, 7.75) 内）、随后 200 帧 `ΔY = 0`；下潜 8 格约需 200 帧（0.04 格/帧）。
17. **鱼近底上浮**：鱼放 (16, 0.2) ⇒ 400 帧内 `Y ≥ 1.7`（抬离底部）且 `Y ≤ 3`（不冲出去）。实测 `Y = 1.80`。**停机带**：`nearBottom` 在 `cy = floor(Y + 0.25) ≥ 2` 时消失（水 0..15）⇒ 稳态 `Y ∈ [1.75, 2.75)`，故阈值取 1.7（而不是 2.0）。
19. **空中投放后整条没入水下**（用户原始场景）：水 0..15、鱼放 **(16, 20)**（空中）跑 600 帧 ⇒ 存活、**`Y + ArtHeightCells ≤ 16`**、`Y ≤ 10`。实测 `Y = 7.41`（图案顶 14.41 ≤ 16 ⇒ 整条鱼在水下）。
20. **图案高度与 AI 常量一致**（`CreatureSpriteFacingTests`）：直接解码 `Assets/Images/Animals/fish/1/fish.png` 的 4 个 16×16 切片，取不透明行数最大值，断言 `== FishBrain.ArtHeightCells`（实测 7）——改美术尺度 / 换图时若忘同步这个常量，本条立刻红。
21. **压死 `Crushed`**（`CreatureAiTests`，2026-10-12 新增）：兔子被**静止**石料全覆盖 ⇒ 第 **11** 帧死、死因 `Crushed`（并断言第 8 帧仍活、`BuriedTicks == 8` ⇒ 0.5 秒宽限真的生效）；鱼被 1~2 格静止沙全覆盖 ⇒ 第 **11** 帧死、死因 `Crushed`（**不是** `Beached`——两者差 200 帧，可判别）；只盖住身体覆盖矩形的**部分**格 ⇒ 300 帧存活；全格都是**水**（不是固体）⇒ 300 帧存活且 `BuriedTicks == 0`。（「覆盖物必须静止」这条过滤器构造不出确定性用例，故不写专门测试；它由前两条的时间窗间接覆盖。）
22. **尸体覆盖三档**（`CreatureBiomassTests`，2026-10-12）：石头与水都该被覆盖 ⇒ 生物质 **48** 格、**水格总数不变**且那口水按固定候选顺序挪到 **(12,22)**（前两个候选正落在轮廓非空格上、被正确排除——这条约束真的被触发过）；同一条里「贴左边界越界」半段仍是 **29** 格 / `columnZero == 9`；**密闭水囊**（水格四周被石头围死、半径 2 内无空气）⇒ **47** 格写入、那格仍是水、水格总数不变；**被沙埋死的兔子** ⇒ 死因 `Crushed`、**48** 格生物质出现在原地（沙被覆盖）；火 / 树干 / 已有生物质仍跳过（**46** = 新写 45 + 原有 1）。
18. **中性浮力的物理口径**（`CreatureWorldTests`）：被动水生体 `Buoyancy = 0.24`（= 重力）⇒ 水中 300 帧 `|ΔY| ≤ 0.5`（保持深度）；原 `Buoyancy_FishRisesThroughWaterAndStopsAtSurface` 更名为 `Buoyancy_SyntheticSwimmerRisesThroughWaterAndStopsAtSurface`，仍用合成被动体钉「浮力 > 重力 ⇒ 上浮 / 离水照落」这条**公式不变量**（其常数不再声称与鱼一致）。

测试基建：参照 `CreatureWorldTests` 现有写法（构造小世界 + 手工摆地形）。威胁测试需要火的 typeId → 走生产注册表（同 `FireParticleTests` 的 `RegisterAll` 路径）。

## 12. 非目标

以下内容不属于本次食草与生物质扩展：

- 全局寻路（A* / 势场）；兔子只做半径 6 的局部目标选择。
- 繁殖、捕食关系、群体协作、动物之间碰撞。
- 鸟物种及其专用行为。
- 动物专用走 / 逃 / 游帧序列；当前仍用已有 idle 帧和播放速度缩放。
- 生物质被水搬运；第一版生物质只受重力下落和固体支撑。

## 13. 基线改动面汇总

| 文件 | 改动 |
|---|---|
| `Simulation/Creatures/Creature.cs` | +黑板槽位（§3）、`ShouldThink`、`ApplyLocomotion`（§5）、致死虚属性门控 |
| `Simulation/Creatures/CreatureWorld.cs` | +`_spawnCounter`，`Spawn` 钉 `SensePhase` |
| `Simulation/Creatures/CreatureDeathCause.cs` | 追加 `Beached`（不重排） |
| `Simulation/Creatures/AI/CreatureSensors.cs` | 新：三个静态探针（§6） |
| `Simulation/Creatures/AI/RabbitBrain.cs` | 新：§7.1 状态机 |
| `Simulation/Creatures/AI/FishBrain.cs` | 新：§7.2 状态机 |
| `Simulation/Creatures/RabbitCreature.cs` / `FishCreature.cs` | `OnLogic` = 门控 + 调 Brain；物种常量 |
| `Tests/EditMode/CreatureAiTests.cs` | 已实现：§11 的 13 条；扩展实现时追加 §20 的食草 / 饥饿测试 |
| `AGENTS.md` / 本文件 | 实现完成后由主模型同步 |

## 14. 生态扩展总览（待实现）

本扩展采用**分层有限状态机 + 局部目标选择**，不引入行为树、A* 或全局势场。

```text
兔子：Idle / Wander / SeekFood / Eat / Flee
鱼：  Swim / Stranded
死亡：CreatureWorld 捕获快照 → 生成 Biomass 格 → 动物实体移除
生物质：下落堆积 → 保护期 → 随机刻腐败 → 消失或 Humus
```

### 14.1 方案取舍

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| 行为树 | 复杂行为组合直观 | 节点生命周期、状态共享和随机流更复杂；当前物种规模不值得 | 不采用 |
| 纯有限状态机 | 零分配、易测试、与现有黑板直接匹配 | 多需求优先级容易堆成 `if/else` | 可行但不够灵活 |
| **分层 FSM + 局部评分** | 保留 FSM 的确定性和低成本；食物、火焰、悬崖有明确优先级 | 只保证局部最优；不可达目标需要失败冷却 | **采用** |

优先级固定为：

```text
死亡 > 火焰逃生 > 水环境处理 > 严重饥饿 > 进食 > 寻找食物 > 漫步 / Idle
```

“局部评分”只用于半径 6 内的食物候选排序：距离更近优先；距离相同按固定扫描序优先；火焰风险、深坑和两格高墙直接淘汰。不做跨障碍路径搜索。

## 15. 兔子食草与饥饿

### 15.1 状态与黑板

兔子状态扩展为：

| 状态 | 编号 | 行为 |
|---|---:|---|
| `Idle` | 0 | 原地停留 |
| `Wander` | 1 | 随机方向漫步 |
| `Flee` | 2 | 背离火焰逃跑 |
| `SeekFood` | 3 | 朝局部食物目标移动 |
| `Eat` | 4 | 停下并清除一格草 |

`Creature` 增加的扩展黑板字段：

| 字段 | 类型 | 语义 |
|---|---|---|
| `HungerTicks` | `int` | 距离上次进食的逻辑帧数 |
| `FeedingCooldownTicks` | `int` | 禁止立即再次进食的剩余帧数 |
| `FoodTargetX/Y` | `int` | 当前目标草格；无目标用固定哨兵值 |
| `FoodTargetFailTicks` | `int` | 接近目标失败的连续帧数 |

字段仍为内联值类型，不创建 per-creature 黑板对象。

### 15.2 参数初值

| 参数 | 初值 | 说明 |
|---|---:|---|
| `MaxHungerTicks` | 1200 | 60 秒未进食后饿死 |
| `HungryThreshold` | 600 | 开始寻找食物 |
| `CriticalHungerThreshold` | 1000 | 食物优先级高于漫步 |
| `FoodRestoreTicks` | 420 | 吃一格草后减少的饥饿计数 |
| `FeedingCooldownTicks` | 20 | 两次进食最小间隔 |
| `FoodTargetFailLimit` | 40 | 目标长期不可达则放弃 |

数值应作为 `RabbitBrain` 常量集中管理；如果后续需要关卡调参，再迁移到动物配置资产，不在 `ParticleSimulationDriver` 内散落硬编码。

### 15.3 感知与目标选择

只有 `HungerTicks >= HungryThreshold` 且冷却为零时才搜索。

1. 思考帧先执行现有火焰扫描；发现火时始终进入 `Flee`。
2. 否则扫描半径 6 圆盘内的 `Grass`。
3. 扫描顺序固定为 `y` 升序、同行 `x` 升序。
4. 选择距离平方最小者；距离相同保留先扫描者。
5. 候选必须通过局部安全检查：高度差不超过 1 格、目标不在火中、前方不是两格高墙、不会直接走入两格深坑。
6. 无候选时回 `Wander`，不消耗额外随机数。

目标选择只使用 `CreatureWorld.NextUInt` 进行已有漫步 / 转向决策；扫描本身不消耗随机数。

### 15.4 寻找与进食

`SeekFood` 将 `IntentVx` 指向目标格，复用现有加速度限幅、墙体翻越和悬崖守卫。满足以下条件时进入 `Eat`（**整数格距离**，与 `EatSearchRadius` 一致）：

```text
abs(targetX - floor(X)) <= 1 且 abs(targetY - floor(Y)) <= 1
```

即目标在脚下 / 斜下 / 前后左右相邻一格内即可进食。

如果 `FoodTargetFailTicks >= FoodTargetFailLimit`，清除目标、随机改变方向并回到 `Wander`，避免对墙后草无限抖动。

`Eat` 的执行顺序：

1. 停止水平意图；
2. 按固定优先级检查脚下、前方、左侧、右侧一格；
3. 再次确认目标格仍为 `ParticleIds.Grass`；
4. 调用 `world.Clear(foodX, foodY)` 清除一格草——注意这是**世界写入**：草带 `Organic` 标签，因此走世界通用有机衰变规则，有 **20%** 概率该格就地留腐殖质（与「烧过的草」同一条既有规则，不是缺陷；测试断言「不再是草且场上草数 −1」而非强断言空气）；
5. `HungerTicks = max(0, HungerTicks - FoodRestoreTicks)`；
6. 设置进食冷却；
7. 冷却后重新寻找或恢复漫步。

兔子第一版只吃 `Grass`，不吃花、草种子、腐殖质或土壤。一次只吃一格，不连带清除整株，保留草逐格再生和啃食路径。

### 15.5 饥饿死亡

每逻辑帧增加 `HungerTicks`。达到 `MaxHungerTicks` 后调用：

```csharp
Kill(CreatureDeathCause.Starved);
```

死亡仍由 `CreatureWorld` 统一清扫；兔子 Brain 不直接创建粒子、不直接从实体表移除自己。

## 16. 死亡物质化与生物质粒子

### 16.1 死亡处理顺序

动物死亡清扫时必须先捕获不可变的 `CreatureDeathSnapshot`，再生成生物质，最后执行原有死亡回调和移除：

```text
Kill(cause)
  → CreatureWorld 清扫
  → 保存位置、尺寸、物种、轮廓、颜色和死因
  → CreatureDeathMaterializer 写入 Biomass
  → OnDeath / CreatureDied
  → 从 CreatureWorld 移除
```

死亡快照避免 `OnDeath` 或列表移除后再读取实体状态造成轮廓漂移。`CreatureDied` 事件仍必须发送一次，参数使用原死因。

### 16.2 生物质类型

新增 `BiomassParticle`，注册顺序只能追加：

```text
Tags = Solid
受重力 = true
IsStatic = false
OnMotion = ParticleRules.StepSolid
OnLogic = 严格空体
OnRandomTick = 腐败
```

生物质**不携带 `ParticleTags.Organic`**。它有自己的 70/30 腐败规则；若再带通用 Organic 标签，被覆盖 / 抹除时会额外触发世界的 20% 腐殖质骰，违反“腐败一次、一次结算”的契约。生物质仍然是动物来源的有机材料，但在粒子规则中把它视为“自带腐败流程”的特殊终端材料。

生物质**独占 `Cell.flags` bit3-7** 作为「连续落稳帧数」（`CellFlags.BiomassSettleMask`，见 §17.1）；颜色走 `Cell.shade`，两者不冲突。

生物质第一版不可被水搬运，不主动横向流动；它像有机固体一样下落、被固体支撑并自然堆积。它不覆盖石头、沙、土家族或水；死亡轮廓中无法写入的格直接跳过。
生成必须使用新的原子世界 API `SetWithShade(x, y, ParticleIds.Biomass, paletteIndex)`，语义等同 `Set`，但允许调用方提供继承轮廓颜色的 `shade`；它仍执行 `OnKill` / `OnSpawn`、热量继承、休眠唤醒和版本写入，禁止直接改 `_cells`。
 

`Cell` 只有 `typeId / flags / shade` 四字节，不能为每格保存任意 RGB。采用确定性的物种轮廓 profile：

```text
RabbitBiomassProfile
FishBiomassProfile
```

每个 profile 定义：宽、高，以及每个轮廓格的“存在”标记和 `byte paletteIndex`。生成时按相对 `y` 从下到上、同行 `x` 从左到右写入。`paletteIndex` 存入 `Cell.shade`，渲染从 `BiomassParticle` 调色板查 `Color32`。

模拟层禁止运行时读取 Unity `Sprite` 纹理：这会引入渲染资源依赖、平台差异和确定性风险。Sprite 与 profile 必须由同一份离线轮廓数据维护；更换 sprite 时同步更新 profile。

### 16.4 轮廓落点规则

死亡轮廓以动物死亡时的脚底中心和 profile 尺寸定位：

| 原目标格 | 处理 |
|---|---|
| 空气 / 大气 | 写入对应生物质格 |
| 草 / 花 / 树叶 | 允许覆盖，有机衰变由世界写入规则处理 |
| **地形固体**（沙 / 石 / 土 / 湿土 / 腐殖土 / 湿润腐殖土） | **写入**（2026-10-12 决策：被压死的尸体必须可见）。判据直接复用 `CreatureTerrain.IsSolid` —— 「地形固体」只有这一份白名单，避免两处漂移。代价：局部挤掉一格地形（沙里出现一块生物质）；**没有任何全局守恒律被破坏** |
| **水** | **先把水挪开再写**（2026-10-12 决策）：用 `ParticleWorld.Swap` 把该格的水换到一个合格空位，再写生物质；**挪不动就跳过该格**（密闭水囊里尸体不完整，但**绝不删水**——水守恒不受影响）。见下 |
| 火 / 树干 / 已有生物质 | 跳过，不覆盖 |
| 越界 | 跳过 |

**挪水的两条硬约束**（2026-10-12）：① 候选目标必须**在界内**、`typeId ∈ {Air, Atmosphere}`，且**不在本轮廓的非空格集合内** —— 否则那格稍后会被生物质覆盖、那口水就凭空消失（写入顺序是 `dy` 升序，落在后面的轮廓格尤其危险）；② 候选顺序固定且无随机数：`(0,+1) (0,+2) (-1,0) (+1,0) (-2,0) (+2,0) (0,-1) (0,-2) (-1,+1) (+1,+1) (-1,-1) (+1,-1)`（先上方——尸体下沉把水往上挤——再同高两侧、再下方）。实现上分两趟：**先把轮廓范围内的水全部挪走，再逐格写生物质**（两趟都保持 `dy` / `dx` 升序）。

生物质生成使用新的原子世界 API `SetWithShade(x, y, ParticleIds.Biomass, paletteIndex)`，语义等同 `Set`，但允许调用方提供继承轮廓颜色的 `shade`；它仍执行 `OnKill` / `OnSpawn`、热量继承、休眠唤醒和版本写入，禁止直接改 `_cells`。每个生物质格独立进入下一次 `StepMotion`，不把尸体作为一个整体实体模拟。

**已知口径不一致（§22 第 2 条）**：本节的映射以**画布底边**为 `floor(Y)`，而渲染以**切片 pivot**（= 图形底边）为锚点。兔子 `pivot.y = 0`（画布底边）两者一致；鱼 `pivot.y = 0.3125`（画布底上方 5 px）⇒ **鱼的生物质出现在鱼图案上方 5 格**。修正方向：物质化器按物种 `PivotRows` 平移（兔 0 / 鱼 5）并补一条「尸体格世界坐标 == 精灵图案世界坐标」的测试（当前测试只钉画布口径，所以漏了）。

### 16.5 生物质颜色和粒子随机性

生物质的颜色继承死亡动物 profile 的颜色索引，不重新随机抽样。生物质运动不消耗世界随机流；腐败只在**落稳计数饱和之后**的每逻辑帧摇骰（见 §17.2），未落稳时一个随机数都不消耗。

## 17. 生物质腐败

### 17.1 新鲜度与「落稳」计数

生物质的 `Cell.shade` 保存颜色索引，因此颜色不能与年龄共用。第一版**不新增世界同形数组**（避免在 `Set` / `SetPhase` / `Clear` / `Swap` 四处同步一份与 `Cell` 平行的状态、把改动扩散进粒子内核），而是让生物质类型独占 `Cell.flags` 的 **bit3-7**（`CellFlags.BiomassSettleMask`，0..31）作为「连续落稳帧数」：

- `OnLogic`（每逻辑帧）判定**脚下是否有支撑**（正下方带 `Solid` 标签，含生物质本身）；有支撑则计数 +1 并**饱和在 31**，无支撑则清零（此时它在下落）。
- 计数达到 `SettleFrames`（= 31，约 1.5 秒）即视为「落稳已久」，进入可腐败状态。
- `shade` 保持轮廓颜色索引不动；`flags` 只改 bit3-7，按 `CellFlags` 约定用掩码读 / 清 / 或写，绝不整体赋值 `flags`（会破坏 bit0）。

计数只在 `OnLogic` 推进，**不依赖 `RandomTicksPerStep`**——随机刻为 0（测试与部分关卡配置）时尸体仍会正常腐败。因此生物质 `Info.HasLogic` 必须为 `true`（`OnLogic` 有真实行为，不是空体）。

### 17.2 腐败规则

腐败是**每逻辑帧**的判定（`OnLogic`，在落稳计数推进之后），只有满足前置条件才摇骰——保证「先判后摇」：

1. 未落稳（bit3-7 < 31）→ 直接返回，**不消耗随机数**；
2. 已落稳 → 摇 `NextUInt() % CorruptionDenominator`（= 200，20 Hz 下期望寿命约 10 秒）；
3. 命中 → 再摇 `NextUInt() % 10`：`< 7` 转腐殖质（`SetPhase(x, y, ParticleIds.Humus)`），否则消失（`Clear(x, y)`），即 **70% 腐殖质 / 30% 散失**。

转为 `Humus` 是显式完成态：腐殖质非 Organic，不再叠加通用衰变；生物质本身也不带 Organic，因此被覆盖 / 抹除时不会额外摇那一次 20% 腐殖质骰（一次腐败只结算一次）。腐败允许质量以「散失」分支离开系统，不参与水量守恒；热量仍遵循 `SetPhase` / `Clear` 的既有世界语义。

## 18. 跨层接口与帧序

建议新增纯 C# `CreatureDeathMaterializer`，职责只有：接收 `CreatureDeathSnapshot`，按 profile 和固定顺序向 `ParticleWorld` 写 Biomass。它不参与 AI 决策，也不进入动物物理热路径。

驱动帧序保持：

```text
ParticleWorld.StepLogic()
  → CreatureWorld.Step()
      → 动物 AI / 物理
      → 死亡快照与生物质写入
  → HighSpeedSession.OnLogicTick()
```

兔子吃草发生在动物层，因此粒子世界在下一次逻辑帧观察到草被清除；死亡生成的生物质同样通过世界写入唤醒粒子运动候选格。高速统计自然统计 Biomass 的产生 / 死亡，不增加动物专用分支。

## 19. 性能、确定性与风险

- 兔子食物扫描与火焰扫描共享半径 6、固定扫描序，只在 2 Hz 思考帧执行。
- 目标失败有 40 帧上限，禁止在不可达草格前永久抖动。
- 每个死亡 profile 格最多一次 `Set`，写入成本为轮廓面积，不扫描全世界。
- 动物 RNG 与生物质粒子 RNG 分离；同种子回放必须同时固定两条流。
- 生物质颜色放在 `shade`、落稳计数放在 `flags` bit3-7：两者不共用，且不需要新增与 `Cell` 平行的世界数组（避免把同步点扩散进 `Set` / `SetPhase` / `Clear` / `Swap`）。
- 生物质腐败走 `OnLogic` 而非随机刻：随机刻为 0 时尸体仍会腐败，测试无需打开随机刻。
- 死亡轮廓遇固体、水和越界跳过，因此生物质数量不是守恒量；这是有意设计。
- 生物质不被水搬运，避免引入水流堵塞、漂尸体和额外水热守恒耦合。
- `ParticleTags.Organic` 的通用 20% 衰变不能与 Biomass 的 70/30 腐败重复触发；显式 `Biomass → Humus` 必须有豁免。

## 20. 扩展验收测试

除 §11 的 13 条基线 AI 测试外，新增以下 EditMode 测试：

1. **兔子饥饿**：饥饿值按逻辑帧增加，达到阈值后进入 `SeekFood`，达到上限后死因为 `Starved`。
2. **局部找食**：半径 6 内最近草格被选中；距离相同按固定扫描序选择；半径外草不影响结果。
3. **食草写入**：兔子到达邻近草格后只清除一格草，饥饿值降低并进入冷却；花和草种子不被吃。
4. **目标失败**：墙后或深坑目标在 `FoodTargetFailLimit` 后放弃，不无限抖动。
5. **火焰优先级**：饥饿中的兔子发现火仍先进入 Flee，不停下进食。
6. **死亡快照**：火焰、溺水、搁浅、饥饿四种死因都生成对应轮廓；事件恰好一次，实体恰好移除一次。
7. **轮廓颜色**：生物质逐格 `shade` 与死亡 profile 一致，轮廓写入顺序固定。
8. **生物质下落**：悬空生物质逐格下落、遇固体停稳并形成堆积；不覆盖石头、水和土家族。
9. **落稳计数**：悬空生物质不累计落稳（不消耗随机数）；落到固体上后计数逐帧 +1 并饱和在 31，随机刻关闭时腐败仍会发生。
10. **腐败分支**：固定 RNG 下分别验证 `Biomass → Humus` 与 `Biomass → Air`；两种结果都不叠加通用 Organic 衰变骰（腐殖质格不会再变成空气）。
11. **随机流隔离**：有无动物时动物 RNG 不改变粒子 RNG；生物质腐败只改变粒子 RNG，且固定扫描 / 先判后摇语义可重放。
12. **完整闭环**：草 → 兔子进食 → 饥饿恢复；兔子死亡 → 生物质下落 → 腐殖质 / 消失，逐阶段状态符合设计。

## 21. 扩展改动面（实现时）

| 文件 / 组件 | 计划职责 |
|---|---|
| `Simulation/Creatures/Creature.cs` | 饥饿、目标、进食冷却等内联黑板槽位；不直接写粒子 |
| `Simulation/Creatures/RabbitBrain.cs` | `SeekFood` / `Eat` 状态、局部目标选择、饥饿优先级 |
| `Simulation/Creatures/CreatureWorld.cs` | 死亡快照、清扫时调用物质化器，保持事件顺序 |
| `Simulation/Creatures/CreatureDeathMaterializer.cs` | 按 profile 将死亡轮廓写入 Biomass |
| `Simulation/Creatures/BiomassProfile.cs` | 兔 / 鱼的确定性轮廓与颜色索引 |
| `Simulation/Particles/BiomassParticle.cs` | Solid（不带 Organic，避免重复通用衰变）+ 下落、落稳计数和腐败 |
| `Simulation/CellFlags.cs` | 新增 `BiomassSettleMask` / 清位掩码（bit3-7 私有位登记） |
| `Simulation/ParticleWorld.cs` | 新增原子 `SetWithShade(x, y, typeId, shade)`（继承轮廓颜色用） |
| `Simulation/ParticleIds.cs` / `ParticleRegistration.cs` | 只能在注册表末尾追加 Biomass |
| `Simulation/ParticlePixelBuffer.cs` | Biomass 调色板映射 |
| `Rendering/CreatureRenderer.cs` 或共享 profile 资源 | 保证动物 sprite 与死亡轮廓契约同步 |
| `Tests/EditMode/CreatureAiTests.cs` | 食草、饥饿、目标失败、优先级 |
| 新增生物质测试文件 | 轮廓、颜色、物理、保护期、腐败和 RNG |
| `docs/ParticleTypeInterface.md` | 补充 Biomass 的注册、颜色、年龄状态和显式腐败豁免契约 |

## 22. 已知不一致与待办（2026-10-12 记录）

以下三条都是**已确认的视觉 / 口径不一致**；用户 2026-10-12 明确**本次不改**，留待单独处理（写在这里以防丢失）。

| # | 现象 | 事实与数值 | 备选修法 |
|---|---|---|---|
| 1 | 鱼贴近水面时**看起来大半出水**（用户原始反馈） | 图案与碰撞盒尺度差 ~14 倍：1 源像素 = 1 格（`_spriteScale = 16`、PPU 16）⇒ 鱼图案 **11×7 格**、兔 9×9 格，而碰撞盒只有 **1×0.5 / 1.5×1 格**。浮力把**碰撞盒**顶到水面（中心格一出水即停），图案从碰撞盒底向上铺 7 格 ⇒ 水面上只留 ~0.25 格。兔子因「站在地面、图形底边 = 脚底」看不出矛盾 | **已在 AI 层缓解（2026-10-12，按用户要求、不改碰撞箱）：**`FishBrain.SurfaceClearanceCells = ArtHeightCells + 1 = 8` ⇒ 鱼稳定停在「上方 8 格全为水」的深度 ⇒ 图案整条没入水下（水深 ≥ 8 格时），见 §7.2。**仍存在的部分**：浅水（< 8 格）必然露头、贴底时图案贴地，且图案**宽度**仍 ≫ 碰撞盒宽度。**正解**仍是按物种配「每源像素 = 多少格」（鱼 0.25 ⇒ 2.75×1.75 格、兔 0.375 ⇒ 3.4×3.4 格），渲染与尸体**同口径**（§16.4） |
| 2 | **鱼尸体比鱼高 5 格** | `CreatureDeathMaterializer` 按「画布底边 = `floor(Y)`」映射（`dy = 15 − pxY`，`FishCells` 内容在 dy 5~11），而渲染按「切片 pivot = 图形底边」摆放（鱼 pivot `y = 0.3125` = 画布底上方 5 px；兔 pivot 0）⇒ 鱼的生物质出现在鱼图案**上方 5 格**（兔子两者一致，故无此问题） | 物质化器按物种 `PivotRows` 平移（兔 0 / 鱼 5）+ 写入去重（小尺度下多个艺术像素会落到同一世界格）；补一条「尸体格世界坐标 == 精灵图案世界坐标」的测试（当前只钉画布口径，所以漏了） |
| 3 | 图案宽度 ≫ 碰撞盒宽度 | 兔图案 9 格宽 / 碰撞盒 1.5 格；鱼 11 格宽 / 碰撞盒 1 格 ⇒ 图案与地形视觉重叠（穿墙感） | 放大碰撞盒会改玩法（跨坎 `CanStepUp`、过缝、悬崖守卫的调参全部要重做）——需单独设计，不建议顺手改 |

另：`Creature.ThreatRadius` 的**设计值即 16**（半径 16 圆盘 ≈ 733 格读、仅思考帧；2026-10-12 用户确认定稿，不再计划调回曾经的 6）。
