# 动物衰老死亡实施计划（固定寿命 · 老死 → 既有死亡链）

> 状态：**已实现（2026-10-17）**。机制、测试、文档同步均已完成；寿命数值由实测标定（见 §2.2）。
> 设计依据：`docs/design-creature-ai.md` §5（运动层死因次序）、§16（死亡物质化）、`docs/modules/Creatures.md`（生态交互）。
> 目标运行时：Unity 2022.3.62f1，EditMode 测试；无 linter / formatter。

## 0. 口径

- 衰老 = **每只动物有一个固定寿命**，存活逻辑帧数达到寿命即**老死**（`CreatureDeathCause.OldAge`）。
- **确定性、零随机数**：不摇「自然死亡骰」，不做个体寿命抖动；同一出生帧的个体必然同一帧老死。
- **不新增任何存储字段 / 枚举项 / 事件 / 声音 / 渲染字段**——`AgeTicks` 与 `OldAge` 都已在代码里，本次只加「谁在什么时候读它判死」。
- 老死复用**既有**死亡链：`Kill(OldAge)` → `CreatureWorld.Step` 清扫 → `CreatureDeathMaterializer` 写生物质 → `OnDeath` / `CreatureDied` → 帧里的物种死亡计数（声音 / 统计自动跟上）。
- 不改变繁殖规则：年龄只有**下限**（成年门槛），不设「过了某年龄不能生」的上限。

### 非目标

- 不做「越老越慢 / 越老越难繁殖 / 毛色变白」等年龄表现（渲染 DTO `CreatureRenderData` 不带 `AgeTicks`，要做得另加帧字段，属独立需求）。
- 不做随机寿命、不做物种外的寿命配置资产（数值仍是常量，与 `CreatureReproduction` 同一惯例）。
- 不改 `VictoryEvaluator`、高速模式、存档、UI：老死只表现为「存活数下降」。

## 1. 实现位置（已完成）

| 项 | 内容 |
|---|---|
| 门控 | `Creature.MaxAgeTicks` 虚属性，**默认 0 = 永不老死**（与 `DrownsInWater` / `StarvesWithoutFood` 同一 opt-in 风格；测试探针与未接线物种零行为差异） |
| 寿命数值 | 兔 `RabbitCreature.LifespanTicks = 5200` / 鱼 `FishCreature.LifespanTicks = 6400` / 鸟 `BirdCreature.LifespanTicks = 12000`（逻辑帧 @20 Hz） |
| 判死位置 | `Creature.ApplyLocomotion` 末尾新增 **⑪ 老死**，排在 ⑩ 饿死之后 |
| 判据 | `int maxAge = MaxAgeTicks; if (maxAge > 0 && AgeTicks >= maxAge) Kill(CreatureDeathCause.OldAge);` |
| 代价 | 每帧一次虚属性读 + 整数比较；零随机数、零分配、不读世界 |
| 帧语义 | `CreatureWorld.Step` ② 的 `ApplyLocomotion` 在 `AgeTicks++` **之前** ⇒ 存活满 `MaxAgeTicks` 帧、在下一帧的反射段老死（`AgeTicks = 寿命 − 1` 时再步一帧仍活，随后一帧死） |
| 同帧多因 | ⑩ 饿死 / ⑨ 搁浅 / ⑧ 溺死 / ⑦ 压死 / ⑥ 烧死 都先于 ⑪ ⇒ 报更具体的近因，老死只是收尾 |

## 2. 寿命数值：实测标定（2026-10-17）

### 2.1 标定方法（临时夹具，标定后删除）

- **食物充足**：兔的草地每帧补齐（无限草）、鱼的水草每帧补齐（无限水草）、鸟的 `PreyEaten` 每帧钉到 1（视为「随时有猎物」）；**无天敌**、无火无水灾。
- **测量一：成对出生间隔**（一对相邻成年异性，位置每帧钉回、新生儿立刻标死以保持种群 = 2）。
- **测量二：种群平衡扫描**（从少量成年个体起跑 4 万～6 万帧，夹具按扫描寿命标死，扫多个寿命值 × 多个种子）。
- 全部只调 `creatures.Step`（鱼 / 兔的部分扫描另补格子），地形静止。

### 2.2 实测结果

**成对出生间隔（= 繁殖冷却主导，食物充足）**

| 物种 | 成年 | 繁殖冷却 | 实测出生间隔 | 吃/捕食开销后 | 代际间隔（新生儿 → 自己第一次生） |
|---|---:|---:|---:|---:|---:|
| 兔 | 300 | 400 | **405** | 458 | **610** |
| 鱼 | 400 | 600 | **600**（整） | — | ≈1000（理论） |
| 鸟 | 1200 | 3000 | **3025** | — | ≈4200（理论） |

**种群平衡扫描（兔，48×24 草地，上限 12，2 只成年起，3 个种子）**

| 寿命 | 尾段平均种群（3 种子） | 结论 |
|---:|---|---|
| 2400 | 0 / 0 / 0 | 全部灭绝 |
| 3000 | 0 / 0 / 5.6（min 2） | 基本灭绝 |
| 3600 | 0 / 10.9 / 10.8 | **种子相关**：一个种子灭绝 |
| 4800 | 10.8 / 11.3 / 10.7 | 三种子稳定在软上限附近 |
| 6000 | 10.9 / 11.3 / 10.8 | 同上 |
| 8000 / 12000 | 11.4~11.6 | 同上（趋近上限 12） |

**96×48 大草地（4 只成年起）**：4800 与 6000 各在部分种子上灭绝、12000 三个种子都稳定——但该现象来自「平坦大地形上兔子基本直线走、几乎不相遇」这一**配对偶发性**，与寿命本身关系弱；实际关卡有地形起伏，兔子更常转向相遇。

**鱼（48×32 宽水体，6 只成年，水草无限）**：任何寿命下出生都只有 1~4 次，全部个体最终老死、种群归零。原因是繁殖门槛要求**双方相邻（≤2 格）**，而自由游动的鱼几乎不相遇——这是既有繁殖设计的既有限制（与寿命无关），记录在 §4。加**水草**前鱼全部**饿死**（`Starved`），说明鱼的食物供应必须成立才谈得上繁殖。

**鸟（48×40，2 只成年，猎物充足）**：出生 2~5 次，`OldAge` 是唯一死因，任何寿命都不足以维持种群——同上，受「必须相邻」限制；鸟作为顶级捕食者本来靠玩家投放维持。

### 2.3 最终取值与理由

| 物种 | 寿命 | 换算 | 一只成年个体一生可完成的繁殖尝试 |
|---|---:|---|---|
| 兔 | **5200** | 260 s | `(5200 − 300) / 405 ≈ 12` 次 |
| 鱼 | **6400** | 320 s | `(6400 − 400) / 600 = 10` 次 |
| 鸟 | **12000** | 600 s | `(12000 − 1200) / 3025 ≈ 3.6` 次 |

1. **兔取 5200**：实测稳定平台是 4800~12000，取平台中段；3600 及以下有真实的**灭绝**风险（种子相关）。
2. **鱼比兔长（6400）**：鱼的出生间隔更长（600 vs 405），且多一道「完全没入 ≥7 格深的水 + 相邻」的门槛；实测自由游动时几乎不相遇，寿命不宜更短。
3. **鸟 12000**：繁殖尝试次数三个物种最少（≈3.6），对应「顶级捕食者最慢、上限只有 4」的定位；同时长于「成年 1200 + 一次冷却 3000 = 4200」，保证第一次繁殖必然在寿命内。
4. **三者都远长于关卡高速时长（`Assets/Data/Phases/*.json` 现值最长 2000 帧）**，守卫测试 `Aging_StageHighSpeedRunsFitInsideLifespans` 钉住这条关系。
5. **都远长于既有测试夹具的年龄预算**（兔 / 鱼 1000 + 600 帧、鸟 3000 + 600 帧），既有用例不会被老死打断。

## 3. 边界与取舍

| 议题 | 决策 | 理由 |
|---|---|---|
| 同批投放 / 同批出生会**成批老死** | 接受 | 确定性优先。死亡事件按物种折叠成一声、生物质同帧写入——与「一场火烧死一群」同一既有路径 |
| 同年龄个体一起进入老化期 | 接受（并已实测） | 同批 8 只成年起跑时，种群会在软上限卡住「无空位 → 不生」，等这一批同龄个体同时老死又同时消失；有年龄结构（玩家分批投放 / 逐步繁殖）时不会这样 |
| 老死的动物还能繁殖吗 | 能（繁殖只看成年下限） | 不引入「更年期」；寿命本身已限制总繁殖次数 |
| 鱼 / 鸟在有界世界里可能因「配对偶发」灭绝 | 记录为已知限制 | 繁殖要求相邻 ≤2 格，自由游动 / 飞行时罕见；与衰老无关，属繁殖设计。玩家可再投放，或将来给鱼 / 鸟加「寻找配偶」行为 |
| `int` 溢出 | 不需要处理 | `AgeTicks` 要溢出需连续模拟约 3.4 年，且溢出前早已老死 |

## 4. 测试（`Assets/Tests/EditMode/CreatureAgingTests.cs`，5 条，已全绿）

1. `Aging_DiesExactlyAtLifespan_WithOldAgeCause`：兔 `AgeTicks = 寿命 − 1` 再步一帧仍活且年龄变为寿命；再步一帧老死（`OldAge`、事件恰一次、清扫后 `Count == 0`）。
2. `Aging_EverySpeciesUsesItsOwnLifespan`：兔 / 鱼 / 鸟各跑一次精确帧语义；三常量互不相等且 > 0；`MaxAgeTicks` 分别等于各自常量（鱼在静水深柱、鸟在空中，避开溺死 / 搁浅路径）。
3. `Aging_DoesNotConsumeRng`：动物随机流 400 帧后 32 次 `NextUInt` 逐位一致；粒子随机流只比到物质化当帧（2 帧），32 次逐位一致。
4. `Aging_StarvationWinsOnSameFrame`：同帧同时满足饿死与老死 ⇒ 死因为 `Starved`（⑩ 先于 ⑪）。
5. `Aging_StageHighSpeedRunsFitInsideLifespans`：读全部阶段 JSON + 设置资产，要求带 `victory.creatures` 的阶段高速时长 < 该物种寿命（失败信息带阶段名 / 物种 / 两数值）。

既有用例的连带改动：`CreatureBiomassTests.Death_RaisesEventOnceWithCause` 的死因数组追加 `OldAge`（五种死因各跑一次，事件恰好一次）。

## 5. 改动清单（已完成）

| 文件 | 改动 |
|---|---|
| `Assets/Scripts/Simulation/Creatures/Creature.cs` | `MaxAgeTicks` 虚属性（默认 0）；`ApplyLocomotion` 末尾 ⑪ 老死分支；`AgeTicks` 注释补判死读者；`<remarks>` 死因清单补 ⑥b / ⑪ |
| `Assets/Scripts/Simulation/Creatures/RabbitCreature.cs` | `LifespanTicks = 5200` + `MaxAgeTicks` 覆盖 + 实测依据注释 |
| `Assets/Scripts/Simulation/Creatures/FishCreature.cs` | `LifespanTicks = 6400` + 同上 |
| `Assets/Scripts/Simulation/Creatures/BirdCreature.cs` | `LifespanTicks = 12000` + 同上 |
| `Assets/Scripts/Simulation/Creatures/CreatureDeathCause.cs` | 过时注释修正（每种死因都有生产路径） |
| `Assets/Tests/EditMode/CreatureAgingTests.cs` | 新建，§4 的 5 条 |
| `Assets/Tests/EditMode/CreatureBiomassTests.cs` | 死因数组追加 `OldAge` |

**未动**：`CreatureWorld`、`CreatureRenderer`、`SimulationFrame`、`AnimalSoundDriver`、`VictoryEvaluator`、`ParticleWorld`、粒子类型、场景 / 资产 / 字体。

## 6. 验证记录（2026-10-17，MCP 增量测试）

| 测试类 | 结果 |
|---|---|
| `CreatureAgingTests` | **5/5 通过** |
| `CreatureReproductionTests` | 16/16 通过 |
| `CreatureWorldTests` | 13/13 通过 |
| `BirdCreatureTests` | 56/56 通过 |
| `FishShallowWaterTests` | 15/15 通过 |
| `AnimalSoundDriverTests` | 11/11 通过 |
| `CreatureBiomassTests` | 9/10：`Death_RaisesEventOnceWithCause`（含 `OldAge`）通过；`Materialize_Fish_UsesFishProfile` **失败且与本改动无关**——`CreatureArtData` 与磁盘上的美术不符（同因：`CreatureArtDataTests` 报鸟 `MaxOpaqueRow` 提交值 7、磁盘美术推导 10；本改动未触碰任何美术 / 生成器 / 生成物），需跑 `Tools/Animals/Regenerate Creature Art Data` 并提交生成物 |
| `CreatureAiTests` | 45/48：3 条逃命用例失败（`FleeOnOpenGround_*` / `Threat_FleeHysteresis_*` / `Threat_SameColumn_*`）——**已在未含本改动的基线代码上复现同样的 3 条失败**，与本改动无关 |
| `CreatureArtDataTests` | 1/1 失败：美术生成物漂移（同上；与本改动无关，未触碰美术 / 生成器 / 生成物） |
