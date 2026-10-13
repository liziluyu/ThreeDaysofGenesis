# 动物生态扩展实施计划（食草 · 饥饿 · 死亡物质化 · 生物质）

> 设计依据：`docs/design-creature-ai.md` §14–§21（本计划与它一一对应；实现完成后同步 `docs/ParticleTypeInterface.md` 与 `AGENTS.md`）。
> 状态：待实现。工作分工遵循 `AGENTS.md`「Agent Workflow」——由 `task` 子 agent 写代码与测试，主模型审查并收尾。
> 目标运行时：Unity 2022.3.62f1，EditMode 测试；无 linter/formatter。

---

## 全局约束（每个子任务都适用）

1. **确定性**：禁止 `UnityEngine.Random` / `Time.*` / `Input`；粒子行为用 `world.NextUInt()`；动物 AI 用 `creatures.NextUInt()` / `NextInt()`。
2. **零分配**：粒子与动物热路径不得出现 LINQ / 字符串拼接 / 闭包 / 装箱 / `Dictionary`。
3. **异步/输入**：仅 legacy Input（本计划不涉及输入）。
4. **注册顺序**：`ParticleRegistration.RegisterAll()` 与 `ParticleIds` 的字段**只能在末尾追加**（存档兼容性）。`Biomass` 追加为 `ids[19]`。
5. **禁止绕过世界 API**：不得直接写 `_cells`；写格只能走 `Set` / `SetPhase` / `Clear` / 新增的 `SetWithShade` / `Swap`。
6. **flags 纪律**：绝不整体赋值 `flags`；用 `CellFlags` 掩码 + `|` / `&~` 组合（bit0 由世界独占）。
7. **测试写法**：EditMode，NUnit；地形用生产注册路径（`ParticleRegistration.RegisterAll()`）以便按 `ParticleIds` 常量摆放；隔离类型表的测试用四参 `ParticleWorld(w, h, seed, types)`。
8. **子 agent 期间不构建、不跑测试**：所有验证由主模型在任务全部返回后统一跑（Unity 批处理，一次全量 + 必要过滤）。
9. **不做未要求的扩展**：不新增动物物种、不做寻路、不做繁殖、不给生物质加水搬运。

---

## Task 1：生物质粒子 + `SetWithShade` + flags 私有位（粒子侧）

**Files**
- Create: `Assets/Scripts/Simulation/Particles/BiomassParticle.cs`
- Modify: `Assets/Scripts/Simulation/CellFlags.cs`（新增 bit3-7 掩码）
- Modify: `Assets/Scripts/Simulation/ParticleIds.cs`（末尾追加 `Biomass`）
- Modify: `Assets/Scripts/Simulation/ParticleRegistration.cs`（末尾追加注册 + `ids[19]` 回填）
- Modify: `Assets/Scripts/Simulation/ParticleWorld.cs`（新增 `SetWithShade`）
- Modify: `Assets/Scripts/Driver/ParticleEditor.cs`（`_unplaceableTypes` 加入 `ParticleIds.Biomass`；右键循环不可选）
- Test: `Assets/Tests/EditMode/BiomassParticleTests.cs`（新）

**Interfaces — Produces（后续任务与测试依赖这些确切片名）**
```csharp
// ParticleIds
public static ushort Biomass;                     // ids[19]，注册表末尾

// CellFlags
public const byte BiomassSettleMask = 0xF8;       // bit3-7
public const byte BiomassSettleClearMask = 0x07;  // 低 3 位（bit0 处理标记 + bit1/2 通用位）
public const int BiomassSettleShift = 3;

// ParticleWorld（语义 = Set，但 shade 由调用方给，不随机抽样；仍触发 OnKill/OnSpawn、热量继承、休眠唤醒、版本写入、OnCellReplaced 统计）
public bool SetWithShade(int x, int y, ushort typeId, byte shade);

// BiomassParticle
public const int SettleFrames = 31;               // 落稳计数饱和值
public const uint CorruptionDenominator = 200u;   // 1/200 每逻辑帧（落稳后）
public const uint HumusBranchNumerator = 7u;      // <7 → 腐殖质（70%）
public const uint HumusBranchDenominator = 10u;
public static ushort SettleCount(ParticleWorld world, int x, int y); // 读 bit3-7（测试与自检用）
```

**规格（实现契约）**
- `Info`：`tags: ParticleTags.Solid`（**不带 Organic**）、`density: 1.0f`、`heatCapacity: 1.0f`、`thermalConductivity: 0.5f`、`thermalEmissivity: 1.0f`、`affectedByGravity: true`、`isStatic: false`、`friction: 0.5f`、`stickiness: 0.5f`、`motionReadReach: new MotionReadBox(1, 0, 3)`、`hasLogic` 保持默认 `true`、`palette:` 6 档灰阶（见下）、`debugName: nameof(BiomassParticle)`。
- `OnMotion`：`ParticleRules.StepSolid(world, x, y);`
- `OnLogic`（每逻辑帧，先落稳判定、后腐败判定，**先判后摇**）：
  1. 落稳：`ParticleRules.HasTag(world, x, y - 1, ParticleTags.Solid)` 为真 → `count = min(SettleFrames, count + 1)` 写回 flags bit3-7；为假 → 写 0 并 **return**（下落中，不摇骰）。
  2. `count < SettleFrames` → return（不消耗随机数）。
  3. `world.NextUInt() % CorruptionDenominator != 0` → return。
  4. `world.NextUInt() % HumusBranchDenominator < HumusBranchNumerator` → `world.SetPhase(x, y, ParticleIds.Humus)`；否则 `world.Clear(x, y)`。
- `OnRandomTick`：留空（严格空体，方法体只有注释）。
- `OnSpawn`：把 bit3-7 清零（世界写入会把 flags 整体置为当前 parity，这里显式清一次，保证新生成的尸体从 0 开始落稳计数）。
- `OnKill`：留空（不带 Organic，无残留；不写自身格）。
- 文件头注释登记：占用 flags 私有位 bit3-7（`CellFlags.BiomassSettleMask` = 连续落稳帧数 0..31）、邻居查询范围（正下方 1 格判支撑、`StepSolid` 的斜滑净空链）、交互的 ParticleIds（`Humus` = 腐败产物）。
- 调色板（6 档，来源于兔子 / 鱼精灵的灰阶按 0.78 变暗；**顺序即 profile 索引，不得重排**）：
```csharp
private static readonly Color32[] Palette =
{
    new Color32(23, 23, 23, 255),   // 0 最深（眼 / 耳）
    new Color32(45, 45, 45, 255),   // 1
    new Color32(59, 59, 59, 255),   // 2
    new Color32(74, 74, 74, 255),   // 3
    new Color32(101, 101, 101, 255),// 4
    new Color32(129, 129, 129, 255),// 5 最亮
};
```
- `SetWithShade`：复制 `Set` 的实现路径（`IsInBounds` → 未注册 id 拒绝 + `Debug.LogError` 一次 → `previousId` → 写 `cell` 时 `cell.shade = shade` 而不是 `NextUInt() % palette.Length` → `MarkCandidatesThisScan` → `SetLogicCell` → `_monitor.OnCellReplaced` → `EvacuateHumidity` → `type.OnSpawn` → `MaybeLeaveHumus`），`shade` 越界时 `Debug.LogError` 并拒绝写入（返回 false）。**不得**复制 `Set` 的代码体再分叉两处逻辑：把 `Set` 改为转调私有 `SetCore(x, y, typeId, shade, bool explicitShade)`，两条公共入口都走它（保持既有行为逐位不变）。

**Tests（`BiomassParticleTests.cs`，至少 8 条）**
1. `SetWithShade_WritesExplicitShade`：指定 shade=4 写入 → `GetCell.shade == 4`；`shade` 超范围（≥ palette 长度）→ 返回 false 且该格不变。
2. `Biomass_FallsAndPilesOnGround`：悬空生物质在若干逻辑帧后落到石面上（Y 方向单元格位置正确），多个格形成堆且逐位可复现（同 seed 两次跑结果一致）。
3. `Biomass_SinksThroughWater_ButNotThroughRock`：沙 / 石头挡住生物质（不覆盖）；水不是支撑（生物质沉到水底石面）。
4. `OnLogic_WhileFalling_DoesNotConsumeRng`：悬空生物质跑 N 个逻辑帧后，与「空世界跑同样帧数」的 `NextUInt()` 序列一致（未落稳不摇骰）。
5. `SettleCounter_SaturatesAt31`：落到石面后 31 帧内 `SettleCount` 单调 +1 并饱和在 31。
6. `Decay_ConvertsToHumus_ByRng`：构造使第一次 `corruption` 判定命中且分支 <7 的种子 → 该格变 `ParticleIds.Humus`。
7. `Decay_Vanishes_ByRng`：构造分支 ≥7 的种子 → 该格变空气。
8. `Decay_DoesNotStackOrganicDecay`：生物质被石头覆盖 / 被 `Clear` 时**不**因 Organic 骰变成腐殖质（生物质不带 Organic）；且 `Humus → 覆盖` 路径不产生额外的意外产物。
9. `Palette_MapsToPixelBuffer`：`ParticlePixelBuffer` 对生物质格的映射颜色与 `Palette[shade]` 一致。

**Acceptance**
- 上述测试全过；`Biomass` 注册为 `ids[19]`，`ParticleRegistry` 类型数 +1；编辑器右键循环里没有生物质。

---

## Task 2：兔子食草与饥饿（动物侧）

**Files**
- Modify: `Assets/Scripts/Simulation/Creatures/Creature.cs`（新增黑板字段 + 饥饿推进）
- Modify: `Assets/Scripts/Simulation/Creatures/AI/CreatureSensors.cs`（新增食物扫描探针）
- Modify: `Assets/Scripts/Simulation/Creatures/AI/RabbitBrain.cs`（`SeekFood` / `Eat` 状态 + 饥饿门控）
- Modify: `Assets/Scripts/Simulation/Creatures/RabbitCreature.cs`（物种常量接线：无新增虚属性，仅确认 `OnLogic` 调 Brain 的路径不变）
- Test: `Assets/Tests/EditMode/CreatureAiTests.cs`（追加用例）

**Interfaces — Produces**
```csharp
// Creature（内联黑板，零分配）
public int HungerTicks;            // 距上次进食的逻辑帧数（每逻辑帧 +1）
public int FeedingCooldownTicks;   // 进食后的禁止再次进食计数
public int FoodTargetX;            // 目标草格；NoFoodTarget = int.MinValue 表示无目标
public int FoodTargetY;
public int FoodTargetFailTicks;    // 连续无法接近目标的帧数
public const int NoFoodTarget = int.MinValue;

// CreatureSensors
// 半径 6 圆盘内找最近的 Grass：固定扫描序（y 升序、同行 x 升序），返回是否找到并输出格坐标
public static bool FindNearestGrass(ParticleWorld world, int cx, int cy, out int grassX, out int grassY);

// RabbitBrain
public const byte StateSeekFood = 3;
public const byte StateEat = 4;
public const int MaxHungerTicks = 1200;
public const int HungryThreshold = 600;
public const int CriticalHungerThreshold = 1000;
public const int FoodRestoreTicks = 420;
public const int FeedingCooldown = 20;
public const int FoodTargetFailLimit = 40;
public const int EatRangeX = 1;      // 与目标格的 |dx| ≤ 1 且 |dy| ≤ 1 即可进食（脚下 / 前方 / 左右）
public const int EatSearchRadius = 1;
```

**规格（实现契约）**
- 饥饿推进放在 `Creature.ApplyLocomotion`（每逻辑帧，与既有计时器同处）：`HungerTicks++`（上限饱和在 `RabbitBrain.MaxHungerTicks`）、`FeedingCooldownTicks` 递减到 0 为止；**仅兔子**（物种差异用虚属性门控，见下一条）执行饥饿致死判定：`HungerTicks >= MaxHungerTicks` → `Kill(CreatureDeathCause.Starved)`。
  - 新增虚属性 `protected virtual bool StarvesWithoutFood { get { return false; } }`，兔子 override 为 `true`。**不要**把饥饿逻辑写成 `is RabbitCreature` 特判。
- `RabbitBrain.Think` 的优先级（严格按此顺序，先判后摇）：
  1. `FindNearestFire`（半径：未逃命 = `ThreatRadius`、已在逃命 = `ThreatRadius + FleeExitMargin`）有威胁 → 进 `Flee`（**饥饿不打断逃命**）。
  2. 否则若 `HungerTicks >= HungryThreshold && FeedingCooldownTicks == 0`：
     - 当前 `AiState == StateEat` 或 `StateSeekFood` → 继续走觅食分支；
     - 否则（Idle / Wander）→ `EnterSeekFood`（调 `FindNearestGrass`，找到则记录目标、`AiState = StateSeekFood`；找不到则 `FoodTargetFailTicks` 计数并在超过 `FoodTargetFailLimit` 后进入 `Wander`）。
  3. 否则按 Idle / Wander 转移逻辑走（Flee 的**退出**已并入逃命分支：滞回半径 + 最短逃命时长）。
- `SeekFood` 每思考帧：目标格已不是 `Grass` → 重新 `FindNearestGrass`（找不到 → `EnterWander`）；`|targetX - floor(X)| <= EatRangeX && |targetY - floor(Y)| <= EatRangeX` → `EnterEat`；否则 `IntentVx = sign(targetX - X) * WalkSpeed`；目标连续 `FoodTargetFailLimit` 帧未接近（`|Δx|` 不下降）→ 放弃目标、随机换向、回 `Wander`。
- `Eat` 每思考帧：`IntentVx = 0`；按固定优先级在半径 `EatSearchRadius` 内找第一格 `Grass`（顺序：正下方、正前方（朝向侧）、正上方、左、右——**按固定枚举顺序写死，不用哈希**）；找到 → `world.Clear(gx, gy)`、`HungerTicks = max(0, HungerTicks - FoodRestoreTicks)`、`FeedingCooldownTicks = FeedingCooldown`、清目标、回 `Wander`；找不到 → 回 `SeekFood`（重新扫描）。
- 只吃 `ParticleIds.Grass`：花 / 草种子 / 腐殖质 / 土壤都不吃。
- 扫描固定顺序 + 先判后摇：无候选 / 冷却未到 / 未达饥饿阈值时**不消耗随机数**。
- `CreatureSensors.FindNearestGrass`：半径 6 圆盘（`dx² + dy² ≤ 36`），固定扫描序（y 升序、同行 x 升序），比较距离平方取最小，平手保留先扫描者；越界跳过。

**Tests（追加到 `CreatureAiTests.cs`，至少 7 条）**
1. `Hunger_IncreasesAndTriggersSeekFood`：饥饿从 0 递增，过阈值且冷度为 0 时 `AiState == StateSeekFood`。
2. `FindNearestGrass_PicksNearest_ThenFixedOrder`：两格草候选 → 取更近者；等距两个 → 取固定扫描序先到者；半径 6 外不计。
3. `Eat_ClearsOneGrassTile_AndRestoresHunger`：兔子在草旁进入 `Eat` → 恰好一格草变空气、`HungerTicks` 下降 `FoodRestoreTicks`、冷却 > 0。
4. `Eat_DoesNotEatFlowerOrSeed`：周围只有花 / 草种子 → 不进食、不消耗随机数。
5. `SeekFood_GivesUpAfterFailLimit`：目标被石头完全封住 → 超过 `FoodTargetFailLimit` 后回 `Wander`（`AiState == StateWander`）且不会永远停在 `SeekFood`。
6. `FireThreat_BeatsHunger`：饥饿超阈值 + 半径 6 内有火 → `AiState == StateFlee`（不停下进食）。
7. `Starvation_KillsWithStarvedCause`：饥饿饱和到 `MaxHungerTicks` → `Kill(Starved)`，`CreatureDied` 收到 `Starved` 且只收到一次。
8. `Hunger_DoesNotConsumeWorldRng`：有 / 无动物两种跑法，`world.NextUInt()` 抽样序列逐位一致（食草写格会消耗世界 RNG 的路径不在此测试内——本测试用纯草地空场景）。

**Acceptance**
- 上述测试全过；既有 13 条 AI 测试与 12 条物理测试仍全过（新增字段不得改变既有行为：默认不吃草、不饿）。

---

## Task 3：死亡物质化（轮廓 → 生物质），依赖 Task 1

**Files**
- Create: `Assets/Scripts/Simulation/Creatures/BiomassProfile.cs`
- Create: `Assets/Scripts/Simulation/Creatures/CreatureDeathMaterializer.cs`
- Modify: `Assets/Scripts/Simulation/Creatures/CreatureWorld.cs`（清扫阶段接入物质化器）
- Test: `Assets/Tests/EditMode/CreatureBiomassTests.cs`（新）

**Interfaces — Consumes（来自 Task 1）**：`ParticleIds.Biomass`、`ParticleWorld.SetWithShade(x, y, typeId, shade)`、`BiomassParticle` 调色板长度 6。
**Interfaces — Produces**
```csharp
// BiomassProfile：等距投影的确定性轮廓（sprite 像素空间 16×16，-1 = 空）
public sealed class BiomassProfile
{
    public const int Size = 16;
    public readonly sbyte[] Cells;   // 长度 256，行序 = dy 升序（dy 0 = 精灵底边所在行），行内 dx 升序（dx 0 → x-8）
    public static readonly BiomassProfile Rabbit;
    public static readonly BiomassProfile Fish;
    public static BiomassProfile For(Creature creature); // RabbitCreature → Rabbit；其余 → Fish
}

// CreatureDeathMaterializer：静态、纯函数式（只写 world）
public static class CreatureDeathMaterializer
{
    // 世界坐标定位：x = floor(creature.X) + dx，y = floor(creature.Y) + dy
    // dx = 0..15 → 世界列 floor(X) - 8 + dx；dy 0 = 精灵底边 → 世界行 floor(Y)
    public static int Materialize(ParticleWorld world, Creature creature); // 返回实际写入的格数
}
```

**定位映射（必须精确照此，来自 `CreatureRenderer` 的既有摆放公式：sprite 底边 = 碰撞盒底边 = `Y`，sprite 水平中心 = `X`，`_spriteScale = 16` 且 PPU 16 ⇒ 1 源像素 = 1 格）**
- 精灵像素 `(pxX, pxY)`（PNG 坐标，左上原点）→ 世界格 `x = floor(X) - 8 + pxX`、`y = floor(Y) + 15 - pxY`。
- profile 里 `Cells[dy * 16 + dx]` 对应 `pxX = dx`、`pxY = 15 - dy`。

**轮廓数据（已生成，逐字使用，不得"顺手优化"）**

`Rabbit`（48 格；`dy` 从 0 起）：
```
dy 0 : -1,-1,-1,-1,-1,-1,-1, 1, 1, 1, 1, 1, 1,-1,-1,-1
dy 1 : -1,-1,-1,-1,-1,-1, 2, 3, 3, 4, 3, 3, 1,-1,-1,-1
dy 2 : -1,-1,-1,-1,-1, 2, 1, 1, 3, 3, 4, 4, 1,-1,-1,-1
dy 3 : -1,-1,-1,-1, 2, 4, 3, 3, 1, 3, 3, 1,-1,-1,-1,-1
dy 4 : -1,-1,-1,-1, 2, 4, 0, 3, 1, 1, 1,-1,-1,-1,-1,-1
dy 5 : -1,-1,-1,-1,-1, 2, 2, 1, 1,-1,-1,-1,-1,-1,-1,-1
dy 6 : -1,-1,-1,-1,-1,-1,-1, 2, 5, 1,-1,-1,-1,-1,-1,-1
dy 7 : -1,-1,-1,-1,-1,-1,-1, 2, 5, 1,-1,-1,-1,-1,-1,-1
dy 8 : -1,-1,-1,-1,-1,-1,-1,-1, 2, 2,-1,-1,-1,-1,-1,-1
dy 9..15 : 全 -1
```

`Fish`（51 格）：
```
dy 0..4 : 全 -1
dy 5 : -1,-1,-1,-1,-1, 1, 1, 1,-1,-1,-1,-1, 2,-1,-1,-1
dy 6 : -1,-1,-1, 1, 1, 2, 2, 2, 1, 1,-1, 1, 1,-1,-1,-1
dy 7 : -1,-1, 1, 2, 3, 2, 3, 1, 1, 2, 1, 2, 2,-1,-1,-1
dy 8 : -1,-1, 2, 3, 0, 2, 3, 3, 2, 2, 1, 1, 1,-1,-1,-1
dy 9 : -1,-1,-1, 2, 2, 2, 3, 2, 1, 1,-1, 2, 2,-1,-1,-1
dy 10: -1,-1,-1,-1,-1, 1, 2, 1,-1,-1,-1,-1, 1,-1,-1,-1
dy 11: -1,-1,-1,-1,-1,-1, 1, 1, 1,-1,-1,-1,-1,-1,-1,-1
dy 12..15 : 全 -1
```

**规格（实现契约）**
- `CreatureWorld.Step` 的**倒序清扫**里，在 `OnDeath` / `CreatureDied` **之前**插入物质化：先 `CreatureDeathMaterializer.Materialize(world, c)`，再 `c.OnDeath(...)`、`CreatureDied?.Invoke(...)`、`RemoveAt(i)`。顺序即契约（见设计 §16.1）。
- `Materialize` 按 `dy` 升序、同行 `dx` 升序遍历 profile；每格：
  - 目标格越界 → 跳过；
  - 目标格 `typeId` 为空气 / 大气 → `SetWithShade(...)`；
  - 目标格是同种生物质 → 跳过（保持已有格，不重抽颜色）；
  - 目标格是草 / 花 / 树叶（`Organic` 且非固体阻挡）→ 允许 `SetWithShade` 覆盖（草被尸体压死，走世界既有 Organic 衰变）；
  - 目标格是其它固体 / 液体 / 火 → 跳过。
- 写入使用 profile 的 palette 索引作为 `shade`。
- 死因不参与轮廓选择（四种死因轮廓相同）；`CreatureDied` 仍带原死因。
- `Clear()`（世界重建）不物质化。
- 不做任何分配：遍历是纯索引循环。

**Tests（`CreatureBiomassTests.cs`，至少 6 条）**
1. `Materialize_WritesProfileAlignedToSprite`：在已知位置杀死一只兔子 → 生物质格集合恰等于 profile 映射到世界的坐标集合（逐一断言），且 `GetCell.shade` 等于 profile 索引。
2. `Materialize_Fish_UsesFishProfile`：鱼 → 使用鱼 profile 的坐标集合。
3. `Materialize_SkipsSolidsWaterAndOutOfBounds`：把轮廓的一部分盖进石头 / 水里 → 这些格不被覆盖，其余格照常写入，返回写入数 == 期望数。
4. `Death_RaisesEventOnceWithCause`：四种死因（Burned / Drowned / Beached / Starved）各跑一次 → `CreatureDied` 恰好一次、参数为原死因、`Creatures.Count == 0`。
5. `Death_IsIndependentOfWorldRebuild`：`Clear()` 后再 `Step` 不产生生物质。
6. `Materialize_ThenDecays`：把物质化后的生物质跑足够逻辑帧（落稳 31 帧 + 腐败期望）→ 场上只剩下 `Humus` 或空气（不残留生物质），且不出现「腐殖质又变成空气」的叠加。

**Acceptance**
- 上述测试全过；`CreatureWorld.Step` 的既有 12 + 13 条测试不受影响（无死亡时 `Materialize` 不被调用路径改变行为）。

---

## 收尾（主模型，非子 agent）
1. 全量 EditMode 测试（批处理，编辑器离线时）或 MCP `tests-run`。
2. Play 模式实测：投放兔子 → 草地里啃食 → 饿死 / 烧死 → 尸体下落成堆 → 变腐殖质或消失。
3. 同步：`docs/ParticleTypeInterface.md`（§4 私有位登记 bit3-7、§4 标签表不提 Organic、§6 写操作表加 `SetWithShade`、§11 类型清单加 Biomass、类型清单行）、`AGENTS.md`（类型清单、测试数、动物层描述）、`docs/design-creature-ai.md` 状态行改为"已实现"。
