# 动物繁殖实施计划（两性配对 · 种群软上限）

**口径（用户 2026-10-17 指定）**

- **两性配对**：需要**相邻的异性同类**；不是每个个体自己分裂。
- 出生**不消耗**世界物质（只看成年 / 冷却 / 条件 / 上限）。
- 物种软上限：**兔 12 / 鱼 12 / 鸟 4**；低于下限时概率翻倍：**兔 2 / 鱼 2 / 鸟 1**。
- **鸟（攻击者）繁殖最慢**：最长成年 + 最长冷却 + 需要双方各自完成过捕食。

## 1. 数据结构

### 1.1 `Creature`（黑板槽位，基类只存不解释）

| 字段 | 类型 | 语义 |
|---|---|---|
| `Sex` | `byte`（0 = 雌，1 = 雄） | `CreatureWorld.Spawn` 按**生成序号奇偶**钉死（`(_spawnCounter & 1)`）。**不消耗随机流** ⇒ 确定性、初始种群天然 50/50 |
| `BreedCooldownTicks` | `int` | 个体繁殖冷却（逻辑帧，运动层每帧减到 0 为止）；出生成功后**双方**各自写入 |
| `PreyEaten` | `int` | 仅鸟用：运动层 ⑥b 判定击杀成功时 `++`（捕食成功的"进食"计数） |

> `SensePhase` 已占用 `_spawnCounter % ThinkEvery`；`Sex` 用同一个计数器即可，**必须在自增前取值**（写清顺序，保证两者相位对齐）。

### 1.2 `CreatureWorld`

- `private readonly List<(Creature mother, Creature father)> _pendingBirths` —— **出生队列**：`Step` 的②遍历用 `_creatures.Count` 动态上界，若在遍历中 `Spawn` 会让新生儿**同帧被推进**，因此出生一律入队、在③死亡清扫**之后**统一应用。
- `public void RequestBirth(Creature mother, Creature father)` —— 入队（Brain 在思考帧调用；**不做任何校验**，校验留到应用时）。
- `public int Count<T>() where T : Creature` —— 物种计数（`for` + `is T`，零分配）。上限/下限与概率因子都用它。

## 2. 决策（只在**思考帧**、只有**雌性**摇骰）

放在新静态类 `CreatureReproduction`（数值单一来源）+ 各族 Brain 一行调用：

```
if (!Alive || Sex != Female || AgeTicks < MaturityTicks || BreedCooldownTicks > 0) return;
mate = 生成序扫描里**最近**的异性同类（中心欧氏距离 ≤ MateRadius = 2，存活、成年、冷却好、满足该物种"父方条件"；
       严格更近才替换、平手保留先生成者——与 TryDetectPrey 同口径）
if (mate == null) return;
count = creatures.Count<本物种>();
if (count >= MaxPopulation) return;
// 概率 = BaseChance × 容量因子 ×（低于下限时 ×2）
if (creatures.NextInt(Denominator) >= 命中阈值) return;
BreedCooldownTicks = CooldownTicks; mate.BreedCooldownTicks = CooldownTicks;   // 先上冷却，防同帧重复
creatures.RequestBirth(this, mate);
```

## 3. 出生应用（`Step` 末尾，死亡清扫之后）

按入队顺序逐条：
- 跳过 `!mother.Alive`、或该物种已到上限（应用时**复核**）；
- `Spawn(new 本物种(), world, mother.X, mother.Y)`（位置夹取由 `Spawn` 负责；实体互不碰撞，重叠无害）；
- 新生儿：`AgeTicks = 0`、`BreedCooldownTicks = InitialCooldownTicks`（防出生即生）、`PreyEaten = 0`；
- 队列清空。

## 4. 数值（初版，全部常量集中）

| 物种 | 成年 | 冷却 | 基础概率/思考帧 | 上限 | 下限 | 附加条件 |
|---|---|---|---|---|---|---|
| 兔 | 300 帧(15s) | 400 帧(20s) | 1/20 | 12 | 2 | **雌性刚吃过**（`HungerTicks < 300`） |
| 鱼 | 400 帧(20s) | 600 帧(30s) | 1/20 | 12 | 2 | **双方完全没入水下且该列水深 ≥ 7** |
| 鸟 | **1200 帧(60s)** | **3000 帧(150s)** | 1/10 | **4** | 1 | **双方各 ≥ 1 次击杀** |

`MateRadius = 2`（格，中心距离）；`InitialCooldownTicks` = 各物种冷却。

## 5. 确定性 / RNG

- 摇骰只在思考帧（雌性、每对一次）；`Spawn` 的性别取序号奇偶，**不摇骰**；粒子世界随机流**零消耗**。
- 配对搜索固定按生成序；出生队列按入队顺序 ⇒ 同 seed 逐位可复现。

## 6. 风险与已知取舍

- **物质不守恒**（用户口径）：死亡写生物质、出生不消耗 ⇒ 长期总物质增长。记录为已知取舍。
- **两性 + 相邻**：种群稀疏或性别失衡时繁殖会停滞（下限翻倍可缓解）；灭绝后不会自动回生（需玩家投放）。
- 出生与死亡同帧：母体先死则放弃该次出生（应用时复核）。
- **不改变**既有的思考相位错峰与事件时序：新生儿走正常 `Spawn` ⇒ `CreatureSpawned` 恰好一次、渲染/音效自动跟上。

## 7. 验收（新增 `CreatureReproductionTests`）

1. `Sex` 由生成序奇偶：连续生成三只 ⇒ 第 1、3 只同性，第 2 只异性。
2. 相邻异性 + 成年 + 冷却好 + 固定 seed ⇒ 预算内出生 1 只（`Count` +1、`CreatureSpawned` 恰好一次）。
3. **同性相邻 ⇒ 不生**（2000 帧）。
4. **距离 > `MateRadius` ⇒ 不生**。
5. **未成年 / 冷却中 ⇒ 不生**。
6. **上限**：`count == MaxPopulation` ⇒ 双倍预算内不生（应用时复核也覆盖）。
7. **下限翻倍**：用公开静态概率函数断言 `count < Min` 时因子 = 2（不靠统计）。
8. **物种条件**：饿兔（`HungerTicks ≥ 300`）不生；浅水鱼（水深 < 7）不生；鸟 `PreyEaten == 0` 不生，双方各 +1 后会生。
9. **新生儿本帧不推进**：出生帧 `AgeTicks == 0`、`BreedCooldownTicks == InitialCooldownTicks`，且下一帧才 `AgeTicks == 1`。
10. **确定性**：同 seed 两次跑的出生帧号序列一致；`ParticleWorld.NextUInt` / `CreatureWorld.NextUInt` 与未动过的世界逐位一致（隔离）。
11. 回归：既有 `BirdCreatureTests` / `CreatureAiTests` / `CreatureWorldTests` / `CreatureBiomassTests` 全绿。
