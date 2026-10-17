# 动物实体层（Creatures）

`Assets/Scripts/Simulation/Creatures/`，与粒子世界并列、同线程、逻辑帧 20 Hz。架构与数值的权威文档是 **`docs/design-creature-ai.md`**；生态扩展计划见 `docs/plan-creature-ecology.md`、鸟方案见 `docs/plan-bird-ai.md`。

## 骨架

- `CreatureWorld`：实体表 + **独立 xorshift32**——动物绝不消耗世界随机流；事件 `CreatureSpawned`/`CreatureDied` 为渲染/音源预留。
- `Creature` 抽象基类（相当于粒子体系的 `IParticleType`）：物种差异走虚属性——`Gravity`/`MaxFallSpeed`/`Buoyancy`/`WaterDrag`/`FliesInAir`/`PerchesOnFoliage`/`FlyAccel`/`PlaceholderColor`；钩子 `OnSpawn`/`OnLogic`（物理之前，AI 决策入口）/`OnDeath`。
- `CreatureTerrain` 碰撞白名单：**只有沙/石头/泥土家族（土、湿土、腐殖土、湿润腐殖土）与冰挡动物**，水是淹没/浮力介质，草花叶树种子一律穿过；鸟另有栖息面口径 `IsPerchable` = IsSolid ∪ 树叶/树干（只服务向下支撑）。冰在死亡物质化那侧是**显式例外**（尸体不得覆盖冰，见下）。
- 实体 = 连续浮点位置（`X, Y` = 碰撞盒底边中点，单位格）+ 轴分离 AABB 物理（重力 → 竖直逐行扫 → 水平 → 边界夹取 → 刷新 `IsGrounded`/`IsSubmerged`）。物理与感知对粒子世界只读；两处写入例外：**兔子进食**（`world.Clear` 一格草）与**死亡物质化**，都走世界 API，不得直接改 `_cells`。
- 驱动在每个 `StepLogic()` 之后调 `Creatures.Step(world)`（高速会话转发之前）。

## 物种

- **兔**（陆生验证体）：被鸟扑中时 **1/2** 概率死亡（`HuntDeathRoll = 2`，§14）。
- **鱼**（水生，**中性浮力** `Buoyancy = Gravity`）：体顶之上那格不是水（体顶 = floor(Y) + `BodyTopOffsetCells`，尺寸来自 `CreatureArtData.Fish`）就锁潜下潜、体顶之上连续 3 格全水才解锁、离底 2 格内上浮 ⇒ 水深 ≥ 7 格整条没入水下并进**深度带随机上下游**（v6 §15：目标深度 = 体顶之上连续水格数 ∈ `[3, min(8, 水深 − 4)]`，到达即重摇 ⇒ 5 格振幅内缓漂），`geoMax = 水深 − 4 < 3` 的浅池整段跳过（行为与从前一字不变、零额外摇骰）⇒ d ≤ 6 永久锁潜贴底（停水平意图、不撞坎掉头）。水平为墙感知换向——前方 3 格体高行内全水保持方向不摇骰、受阻才 1/3 摇骰。被鸟扑中时 **1/3** 概率死亡（`HuntDeathRoll = 3`，§14）。
- **鸟**（飞行，`FliesInAir`：空中挂起重力、可悬停；`FlyAccel` 驱动空中竖直意图，落地/入水恢复常规）：四态 `BirdBrain` = Perched/Fly/Flee/**Hunt**——栖息计时起飞、**随机目标高度的上下巡航**（v3/v4/v8：以正下方第一个可栖面/水为基准，目标高度随机、到达即重摇 ⇒ 越过高地形后主动回落、消除旧的单向棘轮；**名义带顶 50 格，逐图按可达净空钳制** `maxAltitude = max(6, min(50, 世界高 − 12 − 1 − 地表行))` ⇒ 脚底 ≤ min(地表行+51, 88)，天花板 `Y > 世界高 − 12` 是硬边界，探不到基准则悬停）、计时降落**树叶/树干**（v7 §17：地面虽可站但**不落**；`LandProbeCells = 24` 且**不随带顶放大** ⇒ 只有漂低时才降）、火威胁逃命（同兔滞回 + 最短时长，可打断 Hunt）、落水正浮力上浮自救、墙感知换向同鱼。**Hunt（捕食，v2；死亡骰 v5，§14）**：Fly 思考帧视野 24 格内（**§18 起不随带顶 50 放大** ⇒ 高度 > 24 时看不见地面猎物，只有漂低才捕食）有兔/鱼才摇 1/40 骰（无猎物不摇骰、随机流不漂移）⇒ 追击（降落同款重瞄活动目标，下压用 `HuntDiveSpeed = 0.2`）⇒ **锁定目标那一刻再摇一次死亡骰**（`1 / HuntDeathDenominator`，兔 1/2、鱼 1/3）并锁存到 `HuntKillLatched`——追击动作不变；碰撞盒接触时由运动层 ⑥b **只读锁存**结算（零随机数依赖）：命中判死（死因 `Hunted`，尸体走既有物质化）、扑空则猎物**无伤**（生物质零增量），两种情况都即刻清空目标 ⇒ 杀完 / 扑空都起飞离开不停留；追丢（>32 格）/超时（200 帧 ≈ 10 秒）放弃；目标是鱼时可短暂俯冲入水（豁免落水自救反射，溺死兜底不变）；猎物 v1 无天敌感知。

## AI（v1）

静态无状态 Brain（`RabbitBrain` = Idle/Wander/Flee/SeekFood/Eat、`FishBrain` = Swim/Stranded、`BirdBrain` = Perched/Fly/Flee/Hunt）+ 基类内联黑板槽位（`AiState`/`StateTicksLeft`/`SensePhase`/`IntentVx`/`IntentVy`/`WantHop`/`VerticalDiveLatched`/`HuntTarget`/`HuntKillLatched`/`TargetAltitudeCells`/`TargetDepthCells`/淹没与离水计时）+ `ShouldThink` 门控（**每 10 逻辑帧 = 2 Hz**，相位 `SensePhase = 生成序号 % 10` 由 `Spawn` 钉死 ⇒ 错峰、死亡移除不改他者相位）+ 运动层 `ApplyLocomotion`（每逻辑帧夹在 `OnLogic` 与 `StepPhysics` 之间：意图→速度加速度限幅、朝向迟滞、悬崖守卫〔Flee 豁免〕、撞墙跨坎（`CanStepUp`：兔子 true/鱼 false；不可翻越掉头；顶世界边界也掉头）、接触火即死 `Burned`、身体被静止固体全覆盖 10 帧压死 `Crushed`〔门控 `DiesWhenBuried`〕、连续淹没 `DrownTicksLimit` 溺死 `Drowned`〔兔〕/连续离水 200 帧搁浅死 `Beached`〔鱼〕）+ `CreatureSensors`（固定扫描序只读探针：半径 `Creature.ThreatRadius` = **16** 圆盘威胁扫描仅思考帧——逃命取最近的火定方向，有滞回（进入 16、脱离 +`FleeExitMargin`）与最短逃命时长 `FleeMinTicks`；火与兔同列时只在刚进入逃命那一刻摇一次方向；地面/墙探针每帧 O(1)）。

**RNG 纪律**：决策只吃 `creatures.NextInt` 且只在思考帧消耗（含**锁定猎物那一刻**的死亡骰 §14、鱼的**到达目标深度时**的重摇 §15；浅池不进带内路径 ⇒ 零额外摇骰）⇒ 加动物不扰动粒子世界随机流；**例外**：死亡物质化与兔子进食写格消耗世界随机流（它们是粒子世界内容）——「有动物 vs 无动物逐位一致」只在无死亡、无进食场景成立。

## 生态交互

- **饥饿与食草**（兔）：`HungerTicks` 每逻辑帧 +1、饱和 `MaxHungerTicks` = 1200（≈60 秒）；只有 `StarvesWithoutFood` 为真的物种〔兔〕饿死 → `Starved`。超过 `HungryThreshold`（600）且进食冷却为 0 进入觅食——`SeekFood` 用 `FindNearestGrass`（半径 6 圆盘、固定扫描序、取最近）选目标，进到相邻一格转 `Eat`，按固定顺序（正下→正前→正上→左→右）找第一格草 `Clear`（一次只吃一格、恢复 `FoodRestoreTicks` 并进 20 帧冷却、只吃草不吃花/种子/腐殖质；目标连续 40 思考帧不可达放弃）。火焰威胁优先于饥饿。
- **死亡物质化**：死亡清扫先由 `CreatureDeathMaterializer` 按 `BiomassProfile`（轮廓由编辑器菜单 `Tools/Animals/Regenerate Creature Art Data` 从 PNG + 切片 pivot 生成 `CreatureArtData.g.cs`——当前兔 22 格/鱼 22 格/鸟 61 格；`CreatureArtDataTests` 对拍守卫，美术变更后忘重跑即红；映射 `x = floor(X) − 8 + dx`、`y = floor(Y) − PivotRowPixels + dy` ⇒ 尸体与渲染身体逐格对齐）用 `SetWithShade` 逐格写生物质（空气/气体/草花叶 + 地形固体可覆盖——**冰除外**：冰算地形但不可被尸体覆盖，否则水账漏一份水；**水先 `Swap` 挪到合格空位再写、挪不动跳过该格**（绝不删水 ⇒ 水守恒）；火/树干/已有生物质/越界跳过），**再**发 `OnDeath`/`CreatureDied`（顺序即契约）。生物质随后作为普通粒子下落堆积、腐败（见 Particles 文档）。

## 时间尺度

空中运动：重力 0.24 格/帧² + 终端速度 2.4/1.2 格/帧（比初版 0.08/0.8/0.4 快 3 倍、同比缩放 ⇒ 运动形状不变；只提重力无效——终端速度才是落体上限）。水中速率经 `Buoyancy` 重算后与初版逐位一致（兔 0.18 格/帧沉；鱼中性浮力 ⇒ 深度由意图维持）。
