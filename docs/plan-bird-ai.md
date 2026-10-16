# 鸟（第三物种）AI 设计与实施计划

> 2026-10-14 用户批准开工。决策记录：**a = 接受「1 源像素 = 1 格」惯例**（鸟视觉上是大型动物，与兔/鱼同风格，不上按物种像素尺度）；**b = 栖息面含树叶 / 树干**（选 2：地形从「挡/穿」二元变三态，见 §3）；**c = v1 不进食**（选 1：无饥饿、不饿死）；**d = 帧序由实现时读图自判**（选 1）。

## 1. 目标与非目标

鸟是第一个**飞行动物**，验证「同一套运动层 + 静态 Brain」架构对空中物种的可扩展性：三态（栖息 / 飞行 / 逃命）、可被火吓飞、落水能自救飞走、死亡走既有物质化管线（`CreatureArtData.Bird` 已由生成器备产：pivot 0 / 行 0..13 / 高 14 / 61 格）。

**v1 非目标**：不进食、不繁殖、不筑巢、不与雨 / 雷互动、不做全局寻路；不做按物种像素尺度（§a 决策）；不做「撞树叶减速」等细节。

## 2. 美术现状（已实测）

`Assets/Images/Animals/bird/1/bird.png` = 64×32，切片 7 片 16×16（`bird_0..6`），pivot (0.5, 0)。下排 3 帧 + 上排 4 帧（下排第 4 格为空位）；帧 0 不透明行 1..9，全表并集行 0..13。帧语义（哪几帧是扑翼循环）**实现时读图自判**（§d 决策），接线时把切片按名序全部接进 `_birdIdleFrames`，渲染循环播放；朝向约定（眼 / 喙在不透明包围盒哪半）同样实现时从 PNG 实测并写进 `CreatureSpriteFacingTests`。

## 3. 地形三态（b2 的核心改动）

现状 `CreatureTerrain.IsSolid` 白名单（沙 / 石 / 土家族）同时服务「竖直支撑」「水平阻挡」「掩埋判定」「尸体可覆盖」四个口径。鸟要「**水平飞穿树冠、但能落在叶顶**」，因此拆出一个新口径：

- 新增 `CreatureTerrain.IsPerchable(x, y)` = `IsSolid ∪ {TreeLeaf, TreeTrunk}`。**只**服务「竖直支撑」（向下扫描的落脚面）。
- `Creature` 新增虚门 `protected virtual bool PerchesOnFoliage => false`（鸟返回 true）。`StepPhysics` 的**向下**支撑扫描在它为真时改用 `IsPerchable`；**向上**顶头扫描、水平扫描、掩埋判定（`IsFullyBuried`）、尸体覆盖（`CanCorpseOccupy`）**全部保持 `IsSolid` 不变**——叶子永不挡上升、永不挡水平、不会把鸟「压死」、尸体依旧直接覆盖树叶。
- 兔 / 鱼的位移逐位不变（它们该门为 false，`IsPerchable` 路径不参与）。回归由既有全套动物测试 + 新增「兔照旧穿过树叶」对照保证。

## 4. 物理与运动层（`Creature` 改动面）

| 改动 | 内容 |
|---|---|
| 新虚门 `FliesInAir`（默认 false，鸟 true） | `ApplyLocomotion` ① 段的竖直意图逼近条件从「仅 `IsSubmerged`」泛化为「`IsSubmerged`（鱼，`SwimAccel`）‖ `FliesInAir && !IsSubmerged`（鸟，`FlyAccel`）」。**含贴地时**——栖息中的鸟把 `IntentVy` 抬正即垂直起飞（不需要 `WantHop` 脉冲） |
| 飞行时重力挂起 | `StepPhysics` 里 `FliesInAir && !IsSubmerged` 的个体不累加重力（等同鱼在水中的中性浮力：净竖直驱动只有意图）；**落地 / 入水即恢复常规则**。鸟因此可以悬停，不会被动坠落——v1 的「降落」一律是主动意图 |
| 离水自救 | 鸟 `Buoyancy = 0.30 > Gravity 0.24`（正浮力 ⇒ 水中自动上浮到中心格出水）+ `SwimAccel = 0.1`（水中竖直意图可用）；出水即进入飞行挂起。`DrownsInWater = true` 作兜底（连续淹没 160 帧溺死，正常几帧内就出水） |

`BirdCreature`：碰撞盒 **1×1**；`Gravity 0.24` / `MaxFallSpeed 2.4`（入水与坠落用）；`WaterDrag 0.5`；`MoveAccel 0.02`；`CanStepUp false`（飞行不需要跨坎）；`DiesWhenBeached false`；`StarvesWithoutFood false`（c1）。

## 5. 状态机（`BirdBrain`，静态无状态）

`AiState`：**0 = Perched（栖息）、1 = Fly（巡航）、2 = Flee（逃命）**。思考 2 Hz，RNG 只在思考帧消耗（`creatures.NextInt`）。

- **Perched**：`IntentVx = IntentVy = 0`。思考帧：威胁检查 → 否则 `StateTicksLeft` 归零时起飞（置 Fly、巡航计时 `200 + NextInt(400)` ≈ 10~30 秒、随机水平方向、`IntentVy = +FlyVSpeed`）。栖息计时在落地时摇 `100 + NextInt(200)` ≈ 5~15 秒。
- **Fly**：
  - 水平：沿用鱼的**墙感知换向**（前方 3 格体高行内畅通 ⇒ 保持方向不摇骰；受阻才 1/3 摇骰换向）。
  - 竖直高度带：探「正下方第一个固体或水格」（`CreatureSensors` 新增 `ProbeSurfaceBelow`，固定序、只读、越界视为到底）——距该面 < `MinAltitudeCells`（6）⇒ 上抬意图；`Y > Height − CeilingMarginCells`（12）⇒ 下压意图；带内 ⇒ `IntentVy = 0` 悬停。
  - 降落：巡航计时归零且正下方 `LandProbeCells`（12）格内有 `IsPerchable` 面（其上方一格非地形固体）⇒ `IntentVy = −FlyVSpeed` 直到 `IsGrounded` → Perched。计时归零但下方没有合格面 ⇒ 重置一个短计时（40 帧）继续巡航找落点。
- **Flee**：`CreatureSensors.FindNearestFire`（半径沿用 `Creature.ThreatRadius` = 16）命中 → 进入：水平取**远离火**的方向（火与鸟同列时随机选一侧一次）、`IntentVy = +FleeClimbSpeed`；退出用滞回（`ThreatRadius + FleeExitMargin`）+ 最短逃命时长 `FleeMinTicks`——模式照抄兔子，常数鸟自有一份。
- **落水**：`IsSubmerged` ⇒ `IntentVy = +SwimAccel` 上限幅的强起飞意图直到离水（不进 Stranded，鸟没有扑腾态）。

**参数初值**（集中在 `BirdBrain`，实测后调）：`FlySpeed`（水平）0.06、`FlyVSpeed` 0.08、`FlyAccel` 0.05、`FleeSpeed` 0.12、`FleeClimbSpeed` 0.10、`MinAltitudeCells` 6、`CeilingMarginCells` 12、`LandProbeCells` 12、`AheadProbeCells` 3。

## 6. 渲染与编辑器接线（2026-10-15 定稿：按状态分帧组）

**帧语义实测结论**（2026-10-14 初判、**2026-10-15 用户纠正定稿**）：`bird_4..6`（下排 3 片）= 飞行扑翼循环（翅横展 → 竖直扬起 → 下收）；`bird_0..3`（上排 4 片）= 栖息（站立收腿、落到地面 / 叶子上播）；`bird_5`（下排②，翅竖直顶点）兼任起飞过渡的展翅帧。朝向：眼（最暗像素）恒在不透明包围盒左半 ⇒ 头朝左，与兔 / 鱼一致，沿用 `_spriteFacesLeft` 默认值。

**按状态分帧组**（2026-10-15 用户定稿，取代「单一 `_birdIdleFrames` 顺序播」）：

| 组 | 帧 | 用途 |
|---|---|---|
| `_birdFlyFrames` | bird_4..6（下排 3 片循环） | Fly / Flee（移动 ×2、Flee ×3 加速） |
| `_birdPerchFrames` | bird_0..3（上排 4 片） | Perched（站立栖息，慢速交替） |
| `_birdTakeoffFrame` + `_birdTakeoffSeconds`（默认 0.3s） | bird_5 | **Perched → Fly / Flee 的沿**：先播展翅帧、计时耗尽再进飞行组；落回栖息立即切收翅（无过渡） |

实现要点：帧选择核心抽成**纯静态函数**（可 EditMode 测试）；过渡状态是渲染器 per-view 的表现层私有字段（同 `_animTime`，不进模拟、不影响确定性）；场景接线仍只走 `Tools/Animals/Wire Bird Sprites`（幂等定点写入三帧组、删旧 `_birdIdleFrames` 块）。
- `ParticleEditor`：`CreatureFactories` 末尾加 `() => new BirdCreature()`（右键循环 兔 → 鱼 → 鸟）；不做地形校验（与兔 / 鱼同一已知限制）。
- 朝向：已实测定稿（眼恒在包围盒左半 ⇒ 头朝左，`_spriteFacesLeft` 沿用默认 true），`CreatureSpriteFacingTests` 已加鸟朝向用例。

## 7. 尸体

`BiomassProfile.For` 加 `BirdCreature` 案 → `CreatureArtData.Bird`（已生成，pivot 0 ⇒ `baseY = floor(Y)` 直接对齐）。四种死因复用；空中死亡就地物质化，生物质粒子随后自然下落（无需尸体坠落特例）。

## 8. 测试清单（EditMode，全部新写于 `BirdCreatureTests.cs`；朝向用例进 `CreatureSpriteFacingTests.cs`）

物理：① 空中悬停 300 帧 `|ΔY| ≤ 0.5`；② 出生在天花板上方的鸟被压回高度带内；③ 落水 → 自动上浮出水 → 起飞（`SubmergedTicks` 会清零、不死）；④ 巡航计时到点 + 下方有树冠 ⇒ 停在**叶顶**（`IsGrounded`、不再下掉）；⑤ 水平飞穿树冠不受阻；⑥ 贴地起飞（Perched → Fly 竖直离地）；⑦ 同 seed 重放逐位一致；⑧ 世界随机流隔离（有鸟 / 无鸟逐位一致）。
AI：⑨ 2 Hz 门控与相位错峰；⑩ 栖息计时归零起飞；⑪ 火进 Flee（方向远离火）、滞回退出、最短逃命时长；⑫ 思考帧外零 RNG 消耗。
尸体：⑬ 鸟死亡物质化格数 == `CreatureArtData.Bird` 非空格数（61）且竖直锚在 `floor(Y)` 起。
回归：⑭ 兔 / 鱼全套既有测试保持绿（尤其「兔子穿过草 / 树叶」对照）。

## 9. 改动面汇总

| 文件 | 动作 |
|---|---|
| `Simulation/Creatures/BirdCreature.cs` | 新建（物种常量 + 虚门） |
| `Simulation/Creatures/AI/BirdBrain.cs` | 新建（三态状态机） |
| `Simulation/Creatures/Creature.cs` | 加 `FliesInAir` / `PerchesOnFoliage` 虚门 + ① 段竖直条件泛化 + `StepPhysics` 向下支撑口径 |
| `Simulation/Creatures/CreatureTerrain.cs` | 加 `IsPerchable` |
| `Simulation/Creatures/CreatureSensors.cs` | 加 `ProbeSurfaceBelow`（含合格栖息面判定） |
| `Simulation/Creatures/BiomassProfile.cs` | `For` 加鸟案 |
| `Rendering/CreatureRenderer.cs` | 三帧组字段（`_birdFlyFrames` / `_birdPerchFrames` / `_birdTakeoffFrame`）+ 状态选组 + 起飞过渡 |
| `Driver/ParticleEditor.cs` | `CreatureFactories` 加鸟 |
| `Editor/BirdSpriteWiring.cs` | 新建（菜单 + `-executeMethod` 布线工具） |
| `Assets/Scenes/SampleScene.unity` | 由布线工具写入三个鸟帧组字段（不手改 YAML） |
| `Tests/EditMode/BirdCreatureTests.cs` | 新建（§8） |
| `Tests/EditMode/CreatureSpriteFacingTests.cs` | 加鸟朝向用例 |

## 10. 风险与开放项

- **视觉穿帮**：b2 让鸟能站树冠，但「图案 7 格宽 / 碰撞盒 1 格宽」意味着栖在叶顶时图案会盖住两侧树冠——与 §22 第 3 条同一类已知风格，接受。
- **帧语义**：已定稿（见 §6）——下排 = 飞行扑翼、上排 = 栖息站立、bird_5 = 起飞展翅；按状态分组播放。
- **Play 模式观感**（飞行速度 / 高度带 / 栖歇节奏）只能人眼验收，测试只钉确定性与不变量。
- 高速模式 / 存档 / 对话系统与鸟零交互，无需改动。

## 11. v2 捕食（Hunt，2026-10-15 用户批准）

鸟会**随机袭击**兔子和鱼。设计要点：

- **随机触发的口径**：随机刻（`OnRandomTick`）是粒子机制（每帧抽格子），动物不是格子用不了；动物层的等价物是**思考帧摇骰**——Fly 态思考帧（0.5 秒）上，若视野 `HuntDetectCells`（12）格内存在猎物（兔 / 鱼，扫 `CreatureWorld.Creatures`，生成序固定 ⇒ 确定性）则摇 `1 / HuntDenominator`（20）⇒ 命中进 `StateHunt`（= 3）。**视野内无猎物时不摇骰**（随机流不漂移）。视野内期望约 10 秒发起一次。
- **追击**：攻击动作与降落同款——每思考帧重瞄活动目标：水平意图朝目标 X（`HuntSpeed` 0.10）、竖直下压（目标更低）或缓升（目标更高）；渲染即飞行帧组下扑。
- **击杀**：与「接触火即死」同层——`ApplyLocomotion` 新增通用条目：`HuntTarget` 非 null 且两碰撞盒**重叠**（AABB）⇒ `HuntTarget.Kill(CreatureDeathCause.Hunted)`（新死因，枚举只追加）并把 `HuntTarget` 置 null。尸体走既有物质化管线（兔 22 格 / 鱼 22 格生物质），`CreatureDied` 照常一次。
- **撤离（攻击完就飞走）**：Brain 发现 `HuntTarget` 刚变 null 且本态是 Hunt ⇒ 回 Fly + 新巡航计时 + `IntentVy = +FlyVSpeed` 起飞意图（不停留）。
- **放弃**：目标死亡 / 被移除、距离 > `HuntAbortCells`（16）、超时 `HuntTimeoutTicks`（100 帧 ≈ 5 秒，复用 `StateTicksLeft`）⇒ 回 Fly。
- **鱼目标（俯冲入水）**：`Hunt` 态且目标是鱼时**暂时豁免落水自救反射**（鸟可以扎进水里叼鱼）；`DrownsInWater`（160 帧）兜底不变，抓到 / 放弃后反射恢复。
- **优先级**：落水自救 > 火焰逃命（Flee 可打断 Hunt）> Hunt > Fly / Perched；触发只从 Fly 进（栖息中的鸟不伏击）。
- **猎物无感知（v1）**：兔 / 鱼不会逃鸟；「天敌逃逸」是后续选项。
- **黑板**：`Creature` 新增 `public Creature HuntTarget`（引用槽位，null = 无；基类只存不解释，语义同 `AiState`）。
- **渲染**：零改动（攻击就是飞行帧组）。

**测试清单**（进 `BirdCreatureTests.cs`）：① 视野无猎物 ⇒ 永不进 Hunt 且随机流逐位不变；② 有猎物 ⇒ 跑够帧数必进 Hunt（骰子命中）；③ 接触 ⇒ 猎物死一次、死因 Hunted、事件恰好一次、尸体物质化；④ 击杀后回 Fly 且竖直意图向上、不停留；⑤ 超时与追丢放弃；⑥ 俯冲入水杀浅层鱼且鸟不死；⑦ 同 seed 重放逐位一致；⑧ 世界随机流隔离；⑨ 兔子对鸟无感知（v1 契约）。

## 12. v3 高度带随机上下巡航(2026-10-16 用户指定)

**问题(现状 v1/v2 的高度带):** 只有三条竖直规则--"离下方面 < 6 格 ⇒ 上抬"、"`Y > 世界高 - 12` ⇒ 下压"、其余 ⇒ `IntentVy = 0` 悬停。后果:

1. **带内高度粘死**:`Y` 一旦落在"下方面上方 6 格 ~ 世界顶下 12 格"之间就永远悬停 ⇒ 放置高度 = 永久巡航高度,既不上也不下。
2. **单向棘轮**:越过丘陵 / 树冠后下方面变低(相对高度变大),但没有任何"相对高度过大 ⇒ 下压"的分支 ⇒ 净高度只升不降,永不回落。
3. 恒定高度 ⇒ 掠过树冠 / 掠近地面的机会少 ⇒ 落树与捕食机会都变少。

**方案:** 把高度带从"被动悬停"改成**相对正下方第一个可栖面 / 水的目标高度**主动巡航。

### 12.1 新黑板槽位与常量

- `Creature.TargetAltitudeCells`(int,格):**Brain 写 / Brain 读,基类只存不解释**(语义同 `HuntTarget` / `VerticalDiveLatched`)。
  语义 = 相对"正下方第一个可栖面 / 水"的**目标高度**。
- 常量:`MaxAltitudeCells = 12`(新增);下限沿用 `MinAltitudeCells = 6`。目标恒落在 `[6, 12]`(含端点,7 档)。

### 12.2 Fly 态每思考帧的竖直决策(替换原高度带块)

1. `ProbeSurfaceBelow(world, cx, footRow - 1, world.Height, out surfaceRow, out _)`;探不到(该列扫到世界底仍无支撑 / 水面) ⇒ `IntentVy = 0` 悬停,**与现状一致**。
2. `altitude = (footRow - 1) - surfaceRow`。
3. **天花板是硬边界**(优先于高度带):`Y > 世界高 - CeilingMarginCells` ⇒ `TargetAltitudeCells = MinAltitudeCells`(不摇骰)+ `IntentVy = -FlyVSpeed`,返回。
   理由:天花板是物理世界边界,必须无条件下压;同时把目标收敛到下限,避免"目标在高处但被顶住 ⇒ 永远达成不了 ⇒ 无谓来回顶"。
4. **目标无效或已达成** ⇒ 重摇:`target = MinAltitudeCells + NextInt(MaxAltitudeCells - MinAltitudeCells + 1)`。
   无效判据 = `target < MinAltitudeCells || target > MaxAltitudeCells`(兼顾新生成实体的默认 0);
   达成判据 = `altitude == target`。
5. 意图:`altitude < target ⇒ IntentVy = +FlyVSpeed`;`altitude > target ⇒ IntentVy = -FlyVSpeed`;
   `altitude == target`(重摇恰好同值,概率 1/7) ⇒ `IntentVy = 0`(悬停一个思考帧,下帧再摇)。

**自动获得的三个性质:**

| 情形 | 行为 |
|---|---|
| 放置低于 6 格 | 爬升到目标高度(∈6..12),与现状的"爬升"一致,但不再停死在 6 |
| 放置 ≥ 6 格(含高于天花板) | 朝目标升降 ⇒ **棘轮消失**;地形抬高后下方面变低、相对高度变大 ⇒ 主动回落 |
| 带内 | 在 6~12 格之间**上下往返**:每到达目标即重摇新目标(行程约 1~4 秒) |

### 12.3 不动的部分(明确边界)

- 水平:墙感知换向原样(`AheadProbeCells` / 1/3 摇骰)。
- 降落探测:`StateTicksLeft <= 0` 且正下方 `LandProbeCells = 12` 内有**合格**栖息面 ⇒ 停水平 + 下压,`return`--**仍排在高度带之前**,下压途中不受"低于下限即上抬"干扰。
- 天花板常量、落水自救(最高优先级)、`Flee` 的竖直上冲、`Hunt` 的"目标更低即俯冲 / 否则缓升"与 `ExitHunt` 的撤离上冲,全部原样。
- `EnterFly`(起飞)仍给 `IntentVy = +FlyVSpeed`;`ExitHunt(retreat)` 仍给向上冲。二者的高度目标交给下一个 Fly 思考帧按 12.2 第 4 步自然处理(旧目标仍有效时先回到旧目标,达成即重摇)。

### 12.4 RNG 纪律

- 摇骰**只**发生在 Fly 态思考帧内;正下方无基准 / 天花板夹取 / 探不到时**零摇骰**(天花板分支显式"不摇骰")。
- 摇骰频率 ≈ 每段行程 1 次(约 1~4 秒),远低于思考帧频率 ⇒ 对既有随机流序列的扰动最小。
- 世界随机流(粒子世界 xorshift32)依旧零消耗:只用 `CreatureWorld.NextInt`。

### 12.5 验收(测试)

1. 低放(相对高度 < 6) ⇒ 上抬;到达目标后相对高度仍落在 `[6, 12]`。
2. 高放(相对高度 > 12) ⇒ 下压进带(**棘轮修复回归**)。
3. 带内长跑(如 600 逻辑帧):相对高度始终留在 `[6, 12]`(±1 格物理容差),且**上下两个方向都出现过**(不粘死、不悬停到底)。
4. 地形抽走(移走脚下支柱) ⇒ 高度主动回落(**棘轮回归**)。
5. `Y > 世界高 - 12` ⇒ 下压且 `TargetAltitudeCells == MinAltitudeCells`。
6. 正下方无基准(空列) ⇒ `IntentVy == 0`(现状不变)。
7. 目标无效(0 / 99) ⇒ 重摇后落在 `[6, 12]`。
8. 既有用例保持绿:`SpawnedAboveCeiling_*`、`Determinism_SameSeed_ReplayBitExact`、`RngIsolation_*`、
   `Rng_IsConsumedOnlyOnThinkFrames`、`ThinkPhase_*`、Hunt 全组。`AssertCreatureEqual` 增补
   `TargetAltitudeCells` 的逐位比较。若有用例钉死"带内必须悬停" ⇒ 按新契约改写(那是被本次故意替换的旧契约)。
9. 同 seed 逐位重放;世界随机流零消耗。

**已知取舍:** 目标重摇恰好抽到当前高度时悬停一个思考帧(0.5 秒),随后立即再摇--概率 1/7,视觉上是"短暂停顿",接受。

## 13. v4 观感调参:高度带放大到 6~24 格(2026-10-16 用户批准 A 档)

**动机(尺度依据):** 生产世界 = `MainScene.unity` 的 `_widthOverride: 200` / `_heightOverride: 100`,相机 `orthographicSize = 50` ⇒ 整屏就是全高 100 行 ⇒ **1 格 ≈ 屏幕高的 1%**。鸟图实际占地 **11 行高 × ~10 列宽**(`CreatureArtData.Bird.HeightCells = 11`,16×16 sheet 的不透明并集)。§12 的 6~12 带振幅只有 6 格 ⇒ 鸟只上下挪动**半个身子**,观感是"轻微浮动"而非上下巡航。初始沙盒地面在第 ~61 行(floor 3 + 沙高 ≈ 0.6×97),天花板余量 12 ⇒ 地表上方净空 **27 格** ⇒ 带顶取 24 仍有 3 格余量。

**改动(全部是常量,逻辑不动):**

| 常量 | 旧 | 新 | 理由 |
|---|---|---|---|
| `MaxAltitudeCells` | 12 | **24** | 振幅 18 格 = 屏高 18% ≈ 1.6× 鸟图高,且带顶仍在净空内 |
| `HuntDetectCells` | 12 | **24** | 捕食判据是**盒中心球**距离、`dy ≈ 高度` ⇒ 带顶 24 格时视野半径若仍为 12,则水平只能看到 ~3.5 格,带的上半段完全看不见地面猎物 ⇒ 必须在同一量级 |
| `HuntAbortCells` | 16 | **32** | 必须 > 视野半径(契约:追击中短暂甩出视野不立刻放弃);24 ⇒ 32 保持 +8 的余量 |
| `HuntDenominator` | 20 | **40** | 视野球体积放大 ⇒ 触发骰相应变稀,维持"视野内有猎物时约 20 秒一次"的观感 |
| `HuntTimeoutTicks` | 100 | **200** | 追击超时从 5 秒放宽到 10 秒,给高处发起的追击留足下潜时间 |
| `HuntDiveSpeed`(新增) | — | **0.2f** | 俯冲速度与巡航竖直速度(`FlyVSpeed = 0.08`)解耦:0.08 从 24 格下潜要 15 秒 ≫ 超时 ⇒ 高处追击必然放弃;0.2 只要 6 秒。`ThinkHunt` 的下压分支改用它,上抬仍 `FlyVSpeed × 0.5` |
| `LandProbeCells` | 12 | **24** | 落点探测必须覆盖整个带,否则高于 12 格时无法起降(树上/地面都落不下来) |

**已知取舍:** 落点探测 24 格 ⇒ 巡航计时一到就总能找到落点(带内任何高度都可起降),落树/落地频率比 v3 略高,符合"要有机会落到树上"的目标。带顶 24 格在高地形(地表 > 世界高 − 12 − 24)上会被天花板夹取 ⇒ 目标收敛到下限 6(§12.2 第 3 步),行为自洽。

**验收:** 全部作用于既有判据,无新逻辑;新增一条契约用例钉住"从带顶发起追击能在 `HuntTimeoutTicks` 内够到地面猎物";测试里凡涉及这些数字的摆放/帧数一律改常量派生(`BirdBrain.*`),以后调参不再大面积红。

## 14. v5 捕食死亡骰:受击摇概率决定死活(2026-10-16 用户指定)

**动机:** 现状一次接触 = 必死(运动层 ⑥b 的即时反射),兔子/鱼在鸟面前毫无生还机会。用户要求:**在兔子和鱼上写一个概率,受到鸟攻击时摇骰决定死亡**。

### 14.1 数值与语义

| 项 | 值 | 说明 |
|---|---|---|
| `Creature.HuntDeathDenominator`(public int 字段,黑板槽位风格) | 默认 **1** | 接触当帧摇 `creatures.NextInt(本值) == 0` 才判死;**≤ 1 ⇒ 必死且不摇骰**(保持既有物种零随机流消耗) |
| `RabbitCreature.HuntDeathRoll`(public const) | **2** | 50% 死亡--开阔地被扑中,有一半机会挣脱 |
| `FishCreature.HuntDeathRoll`(public const) | **3** | ≈33% 死亡--鱼在水里更难抓 |

两者在各自**构造函数**里写进字段(物种常量是唯一数值来源,一行可调)。

### 14.2 摇骰时机:**锁定目标时锁存**,不在运动层(用户指定)

- 鸟在 `BirdBrain.EnterHunt`(**思考帧**)摇一次骰,结果写进新槽位 `Creature.HuntKillLatched`(真 = 咬杀 / 假 = 扑空);
- 之后**追击动作完全不变**;接触那一刻由运动层 ⑥b 只读锁存结果结算;
- 运动层因此**不摇骰、不依赖动物随机流、签名不动**。

**为什么不在运动层摇**(第一版方案,已被用户否决):(a) 运动层原本拿不到动物随机流(`ApplyLocomotion` 只收 `ParticleWorld`),要扩签名;(b) 会把随机流消耗点从「仅思考帧」扩散到任意接触帧。放在行为层这两点都不存在--**RNG 纪律原样不变**。

**为什么不是每次接触摇:** 接触会持续多帧,若每帧摇一次,1/2 在 3 帧内就逼近必死(1 − 2⁻³ = 87.5%),概率形同虚设。锁存把粒度钉死在**一次追击 = 一次摇骰**。

**扑空语义:** 接触照样发生(动作不变),但猎物**不死、世界零写入**;同样清空 `HuntTarget` ⇒ Brain 走既有 `ExitHunt(retreat: true)` ⇒ 鸟拉升飞走(观感 = 「扑空后撤退」)。平均 2 次扑击 1 次击杀(鱼 3 次)。

**不变的契约:** 猎物仍**无天敌感知**(v1);死因只用 `Hunted`(扑空不是死亡);扑空不写生物质;撤离复用既有出口。

### 14.3 代码落点

1. `Creature`:`public bool HuntKillLatched;`(Brain 写 / 运动层读,基类只存不解释,语义同 `VerticalDiveLatched`)。
2. `BirdBrain.EnterHunt` 签名 `(creature, prey)` → **`(creature, prey, creatures)`**(由 `ThinkFly` 既有调用点传入);内部摇骰并锁存(`prey.HuntDeathDenominator <= 1` ⇒ 必杀且不摇骰);`ExitHunt` 补 `HuntKillLatched = false`(复位防残留)。
3. 运动层 ⑥b 改为只读:`if (HuntKillLatched) huntTarget.Kill(Hunted); HuntTarget = null;`——零随机流、零签名改动。
4. 兔子 / 鱼:物种 const + 构造函数写入字段。

### 14.4 RNG 纪律:**不变**

动物随机流的消耗点仍**只有思考帧**(摇骰发生在 `EnterHunt`,本身就在思考帧路径内);粒子世界随机流依旧**零消耗**。既有的 `Rng_IsConsumedOnlyOnThinkFrames` 用例原样通过、注释不动。

### 14.5 验收(测试)

1. **机制**:分母 = 1 ⇒ 接触必死(既有行为不变、零摇骰);分母 = 2 ⇒ 两个 seed 分别钉住「命中即死」与「扑空存活 + 撤离」两条路径(seed 由实现者现场试出并写进用例,与既有 seed 钉法一致)。
2. **扑空路径**:猎物 `Alive`、生物质格数零增量、`HuntTarget == null`、`HuntKillLatched == false`、鸟回 `StateFly` 且 `IntentVy > 0`(撤离上冲)。
3. **摇骰发生在锁定时刻、而非接触帧**:`EnterHunt` 之后的思考帧随机流应已前进;而随后强制两盒持续重叠 30 帧,随机流**不再前进**(沿用「与从未消耗流的空世界比对」的手法)。这一条同时钉住「摇骰在行为层」与「运动层零消耗」。
4. **世界随机流隔离**:发生接触(含扑空)时粒子世界流仍零消耗。
5. **物种配置守卫**:`new RabbitCreature().HuntDeathDenominator > 1` 且 `new FishCreature().HuntDeathDenominator > 1`(否则功能静默失效)。**不钉具体数值**(调参不该红测试)。
6. **既有必死用例收尾**:`Hunt_Contact_KillsPreyOnceAsHunted_AndMaterializes`、`Hunt_DivesIntoWater_KillsShallowFish_AndSurvives`、`Hunt_FromBandTop_ReachesGroundPrey_WithinTimeout` 显式置 `prey.HuntDeathDenominator = 1`(语义不变)。
7. **确定性**:同 seed 逐位重放保持绿(锁存结果由思考帧摇骰决定,同样确定性)。

## 15. v6 鱼的上下游:深度带随机游层(2026-10-16 用户指定,振幅 5 格)

**动机:** 鱼的竖直行为只有三条硬规则(触面锁潜 / 捷近池底上抬 / 否则中性浮动 `IntentVy = 0`),于是**深池里鱼下潜到「体顶之上还剩 3 格水」就冻住**,之后深度永不改变。用户要求:**给鱼也加上下游,范围比鸟小一档(指定振幅 5 格)**。

### 15.1 参考量与判据

- **参考量 = `depth`(体顶之上的连续水格数)**,即水面到体顶的距离:
  `bodyTopRow = floor(Y) + BodyTopOffsetCells`;`surfaceRow` = 体顶之上第一个非水行(越界算非水);`depth = surfaceRow − 1 − bodyTopRow`。
- **几何上限** `geoMax`(鱼贴底时能有的 depth):`bottomRow` = 脚底之下第一个非水行(越界算非水),
  `geoMax = surfaceRow − bottomRow − 2 − BodyTopOffsetCells`(深 `d` 的池里正好 = `d − 4`)。
  目标带 = `[MinDepthCells, min(MaxDepthCells, geoMax)]`。
- 常量:`MinDepthCells = DiveUnlockMarginCells`(**3**:上层永远留出解锁所需余量 ⇒ 不可能浮出水面)、
  `MaxDepthCells = 8` ⇒ 带 **3~8 格 / 振幅 5 格**(屏高 5%、约 1.25× 鱼图案高 4 行)。对比鸟的 6~24(振幅 18)就是小一档。
- 新黑板槽位 `Creature.TargetDepthCells`(int,Brain 写 / 读,基类只存;默认 0 视为无效并在首个思考帧重摇)。

### 15.2 每思考帧的竖直判定(前三条=现有硬规则,不变)

1. `IsWater(体顶+1)` 为假 ⇒ `VerticalDiveLatched = true`(触面锁潜,原样);
2. `HasWaterAbove`(连续 `DiveUnlockMarginCells` 格全水)⇒ 解锁(原样);
3. `geoMax < MinDepthCells`(池浅到摆不下带)⇒ **整段游层跳过**,行为与今天完全一致(浅池永久伏底、零额外摇骰);
4. 否则:目标无效(`target < MinDepthCells` 或 `target > min(MaxDepthCells, geoMax)`)或**已达成**(`depth == target`)⇒ 重摇 `target = MinDepthCells + NextInt(min(MaxDepthCells, geoMax) − MinDepthCells + 1)`;
5. 朝目标走:`depth > target`(比目标深)⇒ 上浮 `+VerticalSpeed`;`depth < target` ⇒ 下潜 `−VerticalSpeed`;相等 ⇒ `IntentVy = 0` 悬停一帧后重摇。

锁潜(第 1-2 条)仍**优先于**游层:贴合水面的鱼先下潜,解锁后才进入带内上下游。近底上抬(`nearBottom`,按盒中心判)
保留在其后的分支里,只影响贴底附近。

### 15.3 观感与纪律

- 竖直稳态 0.04 格/帧(0.8 格/秒)⇒ 一段 2~5 格约 **3~6 秒**,是缓漂而不是抽撞。
- RNG:只在思考帧摇;浅池(`geoMax < 3`)**零摇骰**;粒子世界流零消耗。
- 「到达即重摇」而非逐帧摇:否则退化成逐帧抖动(FishShallowWaterTests 记录过旧实现 600 帧换号 52 次的坑)。

### 15.4 验收(测试)

1. **深池(如 d = 20)600 帧**:`depth` 恒在 `[MinDepthCells − 1, min(MaxDepthCells, geoMax) + 1]`(±1 物理容差)、
   **上浮与下潜两个方向都出现过**、**每帧整条身体没入水下**(`BodyTopRow ≤ 最后一格水`)、从不贴底。
2. **到达即重摇**:强制 `TargetDepthCells` = 当前 `depth` ⇒ 随后若干帧内 depth 离开该值(不粘死)。
3. **池太浅(d ≤ 6)**:行为与今天完全一致(锁潜恒真 / 竖直意图恒 0 / 贴底),且随机流**零额外消耗**。
4. **目标越界**(置 0 / 置 99,或池变浅)⇒ 重摇后落在合法带内。
5. **既有深池用例收尾**:`OpenDeeperPool_RestsWithoutVerticalIntent` / `OpenPool_Depth10_HoldsDepthAndHeading`
   钉的是旧契约(「深池里竖直意图恒 0、深度不变」),按新契约改写为「深度留在带内 + 双向都出现」;
   浅池三条(`OpenShallowPool_*` / `NarrowShallowTank_*`)与确定性 / 世界流隔离用例保持绿。
6. **确定性**:同 seed 逐位重放。
