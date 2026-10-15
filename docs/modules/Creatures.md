# 动物实体层（Creatures）

`Assets/Scripts/Simulation/Creatures/`，与粒子世界并列、同线程、逻辑帧 20 Hz。架构与数值的权威文档是 **`docs/design-creature-ai.md`**；生态扩展计划见 `docs/plan-creature-ecology.md`、鸟方案见 `docs/plan-bird-ai.md`。

## 骨架

- `CreatureWorld`：实体表 + **独立 xorshift32**——动物绝不消耗世界随机流；事件 `CreatureSpawned`/`CreatureDied` 为渲染/音源预留。
- `Creature` 抽象基类（相当于粒子体系的 `IParticleType`）：物种差异走虚属性——`Gravity`/`MaxFallSpeed`/`Buoyancy`/`WaterDrag`/`FliesInAir`/`PerchesOnFoliage`/`FlyAccel`/`PlaceholderColor`；钩子 `OnSpawn`/`OnLogic`（物理之前，AI 决策入口）/`OnDeath`。
- `CreatureTerrain` 碰撞白名单：**只有沙/石头/泥土家族（土、湿土、腐殖土、湿润腐殖土）挡动物**，水是淹没/浮力介质，草花叶树种子一律穿过；鸟另有栖息面口径 `IsPerchable` = IsSolid ∪ 树叶/树干（只服务向下支撑）。
- 实体 = 连续浮点位置（`X, Y` = 碰撞盒底边中点，单位格）+ 轴分离 AABB 物理（重力 → 竖直逐行扫 → 水平 → 边界夹取 → 刷新 `IsGrounded`/`IsSubmerged`）。物理与感知对粒子世界只读；两处写入例外：**兔子进食**（`world.Clear` 一格草）与**死亡物质化**，都走世界 API，不得直接改 `_cells`。
- 驱动在每个 `StepLogic()` 之后调 `Creatures.Step(world)`（高速会话转发之前）。

## 物种

- **兔**（陆生验证体）。
- **鱼**（水生，**中性浮力** `Buoyancy = Gravity`）：体顶之上那格不是水（体顶 = floor(Y) + `BodyTopOffsetCells`，尺寸来自 `CreatureArtData.Fish`）就锁潜下潜、体顶之上连续 3 格全水才解锁、离底 2 格内上浮 ⇒ 水深 ≥ 7 格整条没入水下停在休息带，d ≤ 6 永久锁潜贴底（停水平意图、不撞坎掉头）。水平为墙感知换向——前方 3 格体高行内全水保持方向不摇骰、受阻才 1/3 摇骰。
- **鸟**（飞行，`FliesInAir`：空中挂起重力、可悬停；`FlyAccel` 驱动空中竖直意图，落地/入水恢复常规）：三态 `BirdBrain` = Perched/Fly/Flee——栖息计时起飞、高度带巡航（离地/水面 ≥ 6 格、世界顶下 12 格封顶）、计时降落可栖面、火威胁逃命（同兔滞回 + 最短时长）、落水正浮力上浮自救、墙感知换向同鱼。

## AI（v1）

静态无状态 Brain（`RabbitBrain` = Idle/Wander/Flee/SeekFood/Eat、`FishBrain` = Swim/Stranded、`BirdBrain` = Perched/Fly/Flee）+ 基类内联黑板槽位（`AiState`/`StateTicksLeft`/`SensePhase`/`IntentVx`/`IntentVy`/`WantHop`/淹没与离水计时）+ `ShouldThink` 门控（**每 10 逻辑帧 = 2 Hz**，相位 `SensePhase = 生成序号 % 10` 由 `Spawn` 钉死 ⇒ 错峰、死亡移除不改他者相位）+ 运动层 `ApplyLocomotion`（每逻辑帧夹在 `OnLogic` 与 `StepPhysics` 之间：意图→速度加速度限幅、朝向迟滞、悬崖守卫〔Flee 豁免〕、撞墙跨坎（`CanStepUp`：兔子 true/鱼 false；不可翻越掉头；顶世界边界也掉头）、接触火即死 `Burned`、身体被静止固体全覆盖 10 帧压死 `Crushed`〔门控 `DiesWhenBuried`〕、连续淹没 `DrownTicksLimit` 溺死 `Drowned`〔兔〕/连续离水 200 帧搁浅死 `Beached`〔鱼〕）+ `CreatureSensors`（固定扫描序只读探针：半径 `Creature.ThreatRadius` = **16** 圆盘威胁扫描仅思考帧——逃命取最近的火定方向，有滞回（进入 16、脱离 +`FleeExitMargin`）与最短逃命时长 `FleeMinTicks`；火与兔同列时只在刚进入逃命那一刻摇一次方向；地面/墙探针每帧 O(1)）。

**RNG 纪律**：决策只吃 `creatures.NextInt` 且只在思考帧消耗 ⇒ 加动物不扰动粒子世界随机流；**例外**：死亡物质化与兔子进食写格消耗世界随机流（它们是粒子世界内容）——「有动物 vs 无动物逐位一致」只在无死亡、无进食场景成立。

## 生态交互

- **饥饿与食草**（兔）：`HungerTicks` 每逻辑帧 +1、饱和 `MaxHungerTicks` = 1200（≈60 秒）；只有 `StarvesWithoutFood` 为真的物种〔兔〕饿死 → `Starved`。超过 `HungryThreshold`（600）且进食冷却为 0 进入觅食——`SeekFood` 用 `FindNearestGrass`（半径 6 圆盘、固定扫描序、取最近）选目标，进到相邻一格转 `Eat`，按固定顺序（正下→正前→正上→左→右）找第一格草 `Clear`（一次只吃一格、恢复 `FoodRestoreTicks` 并进 20 帧冷却、只吃草不吃花/种子/腐殖质；目标连续 40 思考帧不可达放弃）。火焰威胁优先于饥饿。
- **死亡物质化**：死亡清扫先由 `CreatureDeathMaterializer` 按 `BiomassProfile`（轮廓由编辑器菜单 `Tools/Animals/Regenerate Creature Art Data` 从 PNG + 切片 pivot 生成 `CreatureArtData.g.cs`——当前兔 22 格/鱼 22 格/鸟 61 格；`CreatureArtDataTests` 对拍守卫，美术变更后忘重跑即红；映射 `x = floor(X) − 8 + dx`、`y = floor(Y) − PivotRowPixels + dy` ⇒ 尸体与渲染身体逐格对齐）用 `SetWithShade` 逐格写生物质（空气/气体/草花叶 + 地形固体可覆盖；**水先 `Swap` 挪到合格空位再写、挪不动跳过该格**（绝不删水 ⇒ 水守恒）；火/树干/已有生物质/越界跳过），**再**发 `OnDeath`/`CreatureDied`（顺序即契约）。生物质随后作为普通粒子下落堆积、腐败（见 Particles 文档）。

## 时间尺度

空中运动：重力 0.24 格/帧² + 终端速度 2.4/1.2 格/帧（比初版 0.08/0.8/0.4 快 3 倍、同比缩放 ⇒ 运动形状不变；只提重力无效——终端速度才是落体上限）。水中速率经 `Buoyancy` 重算后与初版逐位一致（兔 0.18 格/帧沉；鱼中性浮力 ⇒ 深度由意图维持）。
