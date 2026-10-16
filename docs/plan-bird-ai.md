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
