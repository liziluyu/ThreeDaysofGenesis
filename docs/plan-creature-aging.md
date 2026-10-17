# 动物衰老死亡实施计划（固定寿命 · 老死 → 既有死亡链）

> 状态：设计定稿，待实现（2026-10-17）。工作分工遵循 `AGENTS.md`「Agent Workflow」——由 `task` 子 agent 按本文件写代码与测试，主模型审查并收尾同步文档。
> 设计依据：`docs/design-creature-ai.md` §5（运动层死因次序）、§16（死亡物质化）、`docs/modules/Creatures.md`（生态交互）。
> 目标运行时：Unity 2022.3.62f1，EditMode 测试；无 linter / formatter。

## 0. 口径（本次决策）

- 衰老 = **每只动物有一个固定寿命**，存活逻辑帧数达到寿命即**老死**（`CreatureDeathCause.OldAge`）。
- **确定性、零随机数**：不摇「自然死亡骰」，不做个体寿命抖动；同一出生帧的个体必然同一帧老死。
- **不新增任何存储字段 / 枚举项 / 事件 / 声音 / 渲染字段**——`AgeTicks` 与 `OldAge` 都已在代码里，只缺「谁在什么时候读它判死」。
- 老死复用**既有**死亡链：`Kill(OldAge)` → `CreatureWorld.Step` 清扫 → `CreatureDeathMaterializer` 写生物质 → `OnDeath` / `CreatureDied` → 帧里的物种死亡计数（声音 / 统计自动跟上）。
- 不改变繁殖规则：年龄只有**下限**（成年门槛），不设「过了某年龄不能生」的上限。

### 非目标

- 不做「越老越慢 / 越老越难繁殖 / 毛色变白」等年龄表现（渲染 DTO `CreatureRenderData` 不带 `AgeTicks`，要做得另加帧字段，属独立需求）。
- 不做随机寿命、不做物种外的寿命配置资产（数值仍是常量，与 `CreatureReproduction` 同一惯例）。
- 不改 `VictoryEvaluator`、高速模式、存档、UI：老死只表现为「存活数下降」。

## 1. 现状（检查结论，2026-10-17）

| 事实 | 位置 |
|---|---|
| `AgeTicks` 每逻辑帧 +1，唯一递增点 | `CreatureWorld.Step` ②：`c.StepPhysics(world); c.AgeTicks++;`（`CreatureWorld.cs:252`） |
| 新生儿 `AgeTicks = 0` | 出生应用阶段（`CreatureWorld.cs:314/326/338`） |
| `AgeTicks` 的唯一读者是繁殖成年门槛 | `CreatureReproduction.cs:205/358`（`AgeTicks < maturity`） |
| `OldAge` 枚举已存在但**生产代码从不产生** | `CreatureDeathCause.cs:29`；仅测试里人为 `Kill(OldAge)` |
| 现有死因（都在 `Creature.ApplyLocomotion`） | ⑥ `Burned` / ⑥b `Hunted` / ⑦ `Crushed` / ⑧ `Drowned` / ⑨ `Beached` / ⑩ `Starved` |

⇒ 缺口只有三处：寿命数值、判死分支、文档/注释同步。

## 2. 数据结构

### 2.1 `Creature`（基类只提供门，不硬编码物种数值）

```csharp
/// <summary>寿命上限（逻辑帧）：AgeTicks 达到它即老死（OldAge）。**0 = 永不老死（默认）**——
/// 与 DrownsInWater / StarvesWithoutFood 同一 opt-in 风格：只有显式覆盖的物种会有寿命，
/// 测试探针（PassiveBody 等）与将来未接线的物种因此零行为差异。</summary>
public virtual int MaxAgeTicks
{
    get { return 0; }
}
```

- **不加新字段**：`AgeTicks` 已经是「已存活的逻辑帧数」，就是这个门的唯一输入。
- 不叫 `LifespanTicks` 是为了让物种常量能叫这个名字而不与虚属性撞名。

### 2.2 物种常量（数值单一来源，放各物种类）

| 物种类 | 常量 | 值 | 换算 |
|---|---|---:|---|
| `RabbitCreature` | `LifespanTicks` | **3600** | 180 s = 12 × 成年(300) = 9 × 繁殖冷却(400) |
| `FishCreature` | `LifespanTicks` | **4800** | 240 s = 12 × 成年(400) = 8 × 冷却(600) |
| `BirdCreature` | `LifespanTicks` | **12000** | 600 s = 10 × 成年(1200) = 4 × 冷却(3000) |

```csharp
// RabbitCreature / FishCreature / BirdCreature 各一份（物种差异走虚属性，不写 is 特判）
public const int LifespanTicks = 3600;   // 数值见上表

public override int MaxAgeTicks
{
    get { return LifespanTicks; }
}
```

### 2.3 数值为什么这么取

1. **必须显著长于任何阶段的高速时长**：`Assets/Data/Phases/*.json` 的 `simulationTicks` 现值 phase_01..05 = 100 / 500 / 2000 / 2000 / 500（0 才回落到设置资产 `ParticleSimulationSettings.HighSpeedLogicFrames`；phase_05 无胜利条件）。阶段 4 要求高速结束时兔 / 鱼 / 鸟各存活 ≥ 1 ⇒ 寿命必须 > 2000，否则该阶段会在中途失去动物、变成不可胜。3600 留 1.8 倍余量。
2. **必须显著长于现有测试的 `AgeTicks` 固定值 + 世界推进预算**：`CreatureReproductionTests` 的夹具把兔 / 鱼 `AgeTicks` 钉到 **1000**、鸟钉到 **3000**，`RunUntilBirth` 最长推进 **600** 帧（位置每帧钉回，预算内必出生）⇒ 运行时年龄峰值约兔 / 鱼 **1600**、鸟 **3600**；`AssertNeverBreeds` 只反复调 `TryBreed`、**不推进世界**，不改变年龄。其余动物测试从 `AgeTicks = 0` 起跑，最长 1200 帧（`CreatureWorldTests` 的游动体）。因此寿命下限约为 **2000**（阶段时长与夹具两者取大），3600 留有余量——但仍**不要**把寿命调到 2000 以下。
3. **鸟必须长于「成年 + 一次冷却」（1200 + 3000 = 4200）**，否则它可能在第一次繁殖之前老死；12000 保证成年后约 3 次繁殖窗口（与它「捕食者最慢」的定位一致）。
4. **最短的兔寿命 3600 = 3 分钟 @1×**：这是玩家在正常速度下能察觉的尺度。设置资产当前 `_highSpeedLogicFrames = 2000`（脚本字段默认值 6000）；**若**把它调到 6000，则沙盒高速会话里兔 / 鱼会自然老死一批、鸟不会——这是目前唯一能直观看到衰老的窗口，属设计预期（见 §4）。

## 3. 判死位置与帧序

### 3.1 位置：`Creature.ApplyLocomotion` 末尾新增 ⑪

放在 ⑩（饥饿致死）**之后**：

```csharp
// ⑪ 老死（确定性、无随机数）：每帧一次整数比较，不分配、不读世界。
//    排在所有环境 / 计时死因之后 ⇒ 同帧多因时优先报告更具体的近因
//    （烧死 / 压死 / 溺死 / 搁浅 / 饿死），老死只是「没有别的死因」时的收尾。
int maxAge = MaxAgeTicks;
if (maxAge > 0 && AgeTicks >= maxAge)
{
    Kill(CreatureDeathCause.OldAge);
}
```

为什么不放在 `CreatureWorld.Step` 的 `AgeTicks++` 旁边：

- 全部死因（⑥–⑩）都在运动层，老死与它们同层才能让**优先级**在一处读完、由一份注释说明；散到世界层会变成第二个判死点。
- 世界层不该知道物种寿命（`CreatureWorld` 只认识基类）。虚属性虽然也能调，但死因次序会被拆成两截。

### 3.2 精确帧语义（测试必须照此断言）

`Step` ② 的次序是 `OnLogic → ApplyLocomotion → StepPhysics → AgeTicks++`，所以第 n 次推进（1 起）进入 `ApplyLocomotion` 时 `AgeTicks == n − 1`：

- 从 `AgeTicks = 0` 出生的个体，**存活满 `MaxAgeTicks` 帧**（这些帧照常思考、移动、进食），在**下一次推进的反射段**老死。
- 把 `AgeTicks` 直接置为 `MaxAgeTicks − 1` 再 `Step` 一次：仍存活（`AgeTicks` 变为 `MaxAgeTicks`）；再 `Step` 一次：死亡。
- 死亡当帧**照常继续** `StepPhysics` 与 `AgeTicks++`（既有「自身在反射段被杀」语义，⑥–⑩ 相同），不影响结果：清扫按 `!Alive` 结算。
- 被同帧更早的实体杀掉的个体在 ② 循环开头就被 `!Alive` 跳过 ⇒ 不会追加老死判定（死因仍是先发生的那个）。

## 4. 边界与取舍

| 议题 | 决策 | 理由 |
|---|---|---|
| 同批投放 / 同批出生会**成批老死**（同一帧多具尸体） | 接受 | 确定性优先、实现最简。死亡事件按物种折叠成一声、生物质同帧写入——与「一场火烧死一群」同一既有路径。若将来要错峰，可从 `_spawnCounter` 派生 ±10% 的个体抖动（**不摇骰**），本次不做 |
| 老死与饿死 / 压死 / 烧死同帧 | 报**更具体的近因**（⑩ 之前的分支先执行） | 与⑦ 注释里「活埋现场往往同时离水，先判压死者胜」同一口径 |
| 老死的动物还能繁殖吗 | 能（繁殖只看成年下限） | 不引入「更年期」这类新规则；寿命本身已限制总繁殖次数 |
| 沙盒把 `_highSpeedLogicFrames` 调到 6000 时的老死 | 预期行为 | 见 §2.3 第 4 条；当前所有阶段 ≤ 2000 帧，胜负判定不受影响。注意兔在**没有草**时第 1200 帧就饿死（`MaxHungerTicks`），所以「看到兔老死」的前提是有草可吃 |
| 未来阶段把 `simulationTicks` 调到 > 3600 且要求兔存活 | 阶段设计必须按寿命预算 | 已有守卫测试钉住「当前阶段时长 < 对应物种寿命」，改阶段时测试会红（见 §5 第 5 条） |
| `int` 溢出 | 不需要处理 | `MaxAgeTicks > 0` 的门只在有寿命的物种上成立；`AgeTicks` 要溢出需连续模拟约 3.4 年，且溢出前早已老死 |

## 5. 验收测试（新建 `Assets/Tests/EditMode/CreatureAgingTests.cs`）

测试基建沿用 `CreatureWorldTests`（小世界 + 手摆地形 + `StepFrame`）；需要跨物种时用 `CreatureSpawns.Create`。**避免全量跑，只跑本文件与受影响的类**（`docs/modules/Testing.md`）。

1. `Aging_DiesExactlyAtLifespan_WithOldAgeCause`
   - 兔：`AgeTicks = LifespanTicks − 1` → `Step` 一次仍 `Alive`；再 `Step` 一次 `!Alive`、`CreatureDied` 恰好一次且 `cause == OldAge`、`Count == 0`。
   - 精确帧号钉死（不许写成范围），换寿命常量时用例应红。
2. `Aging_EverySpeciesUsesItsOwnLifespan`
   - 兔 / 鱼 / 鸟各一例：`AgeTicks = LifespanTicks − 1` 存活、`AgeTicks = LifespanTicks` 老死；三物种常量互不相等（防止复制粘贴漏改）。
   - 同时断言 `MaxAgeTicks > 0` 对三个物种都成立（opt-in 门没漏接）。
3. `Aging_DoesNotConsumeRng`
   - 同一 seed 两个世界（都是石头地板、**没有草 / 火**，`RandomTicksPerStep = 0`）：实验组放一只兔，**夹具固定行为**——`AiState = RabbitBrain.StateIdle`、`StateTicksLeft = 100000`（照 `CreatureReproductionTests` 的写法，Brain 因此不摇漫步骰）、`HungerTicks = 0`、`AgeTicks = LifespanTicks − 1`；对照组空跑。
   - **动物随机流**：两边各推进 `creatures.Step` + `world.StepLogic` 共 **400** 帧（兔第 2 帧老死、之后不再推进），比较 `creatures.NextUInt()` 32 次逐位一致。
   - **粒子随机流**：只比到**死亡物质化那一帧**——推进 **2** 帧后立刻比较 `world.NextUInt()` 32 次逐位一致。不要把窗口拉长到生物质落稳之后：那时腐败骰（`1/200`）**本就**消耗世界随机流（既有契约），会把这条用例变成假红。
   - 两个断言覆盖「老死判定 + `Kill` + 物质化」全都不抽随机数。
4. `Aging_StarvationWinsOnSameFrame`
   - 兔同时满足 `AgeTicks >= MaxAgeTicks` 与 `HungerTicks >= MaxHungerTicks` → 死因 `Starved`（⑩ 在 ⑪ 之前），不是 `OldAge`。
5. `Aging_StageHighSpeedRunsFitInsideLifespans`（配置守卫）
   - `AssetDatabase` 读全部 `Assets/Data/Phases/*.json`（读法照 `GameFlowTests.cs:998` 的 `AssetDatabase.LoadAssetAtPath<TextAsset>` + `GamePhaseParser.TryParse`），并读 `Assets/Data/ParticleSimulationSettings.asset` 取 `HighSpeedLogicFrames`；
   - 对每个带 `victory.creatures` 的阶段：`effectiveTicks = phase.simulationTicks > 0 ? phase.simulationTicks : settings.HighSpeedLogicFrames`；逐条把 `condition.species`（类名，如 `"RabbitCreature"`）按 `CreatureSpawns.Species` 循环 + `prototype.GetType().Name` 解析（与 `VictoryEvaluator.TryResolveCreatureSpecies` 同一口径，该方法为 private，测试内镜像 8 行），断言 `effectiveTicks < prototype.MaxAgeTicks`；
   - 失败信息给出阶段 id + 物种 + 两个数值（防止将来把阶段调长或把寿命调短）。

### 既有测试的连带改动

- `CreatureBiomassTests.Death_RaisesEventOnceWithCause`：死因数组**追加 `OldAge`**（老死同样必须物质化、事件恰好一次）——这是对既有用例的一行数据扩展，不是新用例。
- 其余用例（`CreatureAiTests` / `CreatureWorldTests` / `CreatureReproductionTests` / `BirdCreatureTests` / `CreatureBiomassTests`）**不改**：寿命选值保证它们跑不到老死（§2.3 第 2 条）。

## 6. 改动面（实现清单）

| 文件 | 改动 |
|---|---|
| `Assets/Scripts/Simulation/Creatures/Creature.cs` | 新增虚属性 `MaxAgeTicks`（默认 0 = 永不老死）；`ApplyLocomotion` 末尾新增 ⑪ 老死分支；`ApplyLocomotion` 的 `<remarks>` 把「⑥ 火 / ⑦ 掩埋 / ⑧ 溺死 / ⑨ 搁浅 / ⑩ 饿死」补成 ⑥–⑪ 并说明老死排在最后；`AgeTicks` 字段注释补「`MaxAgeTicks` 是它的判死读者」 |
| `Assets/Scripts/Simulation/Creatures/RabbitCreature.cs` | `public const int LifespanTicks = 3600;` + `override MaxAgeTicks` |
| `Assets/Scripts/Simulation/Creatures/FishCreature.cs` | `public const int LifespanTicks = 4800;` + `override MaxAgeTicks` |
| `Assets/Scripts/Simulation/Creatures/BirdCreature.cs` | `public const int LifespanTicks = 12000;` + `override MaxAgeTicks`；类头注释的「致死门控」列表补寿命 |
| `Assets/Scripts/Simulation/Creatures/CreatureDeathCause.cs` | 只改过时注释：删掉「本阶段只有物理，实际只会产生 Unknown；其余取值是预留」——现在每种死因都由生产代码产生，`OldAge` 不再例外 |
| `Assets/Tests/EditMode/CreatureAgingTests.cs` | 新建，§5 的 5 条 |
| `Assets/Tests/EditMode/CreatureBiomassTests.cs` | 死因数组追加 `OldAge` |

**不动**：`CreatureWorld`、`CreatureRenderer`、`SimulationFrame`（渲染 DTO 无 `AgeTicks`，无需改）、`AnimalSoundDriver`、`VictoryEvaluator`、`ParticleWorld`、任何粒子类型、任何场景 / 资产 / 字体。

## 7. 收尾（主模型，实现返回后）

1. 只跑受影响用例：`CreatureAgingTests` + `CreatureBiomassTests` + `CreatureReproductionTests` + `CreatureWorldTests` + `CreatureAiTests` + `BirdCreatureTests`（**全量测试需用户明确授权**）。
2. 同步文档：
   - `docs/modules/Creatures.md`：「生态交互」加一条**衰老死亡**（寿命数值表、`OldAge`、无随机数、`MaxAgeTicks` opt-in 门）；「物种」三行各补寿命。
   - `docs/design-creature-ai.md`：§5 的死因清单补一条**老死**（注明排在饿死之后）；§25 状态由「待实现」改为「已实现」并记实测帧号。
   - `docs/modules/Testing.md`：测试清单加 `CreatureAgingTests.cs` 一行。
   - `docs/plan-creature-aging.md`：状态行改为「已实现（日期）」。
3. `AGENTS.md` 不需要改（无新程序集、无新类型注册、无测试数硬编码）。
