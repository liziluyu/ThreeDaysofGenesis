# 模拟内核（TapTap2026.Simulation）

程序集边界即架构边界。`Assets/Scripts/Simulation/`，纯模拟，**不引用任何渲染对象**。粒子类型的作者契约、世界 API、渲染契约的唯一权威文档是 **`docs/ParticleTypeInterface.md`**；改模拟/渲染架构前先读它，改完同步它。本文件只记内核级机制，各粒子类型的行为细则见 `docs/modules/Particles.md`。

## 数据结构

- 扁平 `Cell[]`（4 字节/格）+ `(x,y)` 直接寻址；`Cell` 布局唯一真源是 `Cell.cs`，其它文件不得复制该结构。
- 类型行为经全局静态注册表（`ParticleRegistry`）以 `typeId → 委托` 直调，热路径零分配、零虚分派。
- 全工程唯一的粒子类型清单是 `ParticleRegistration.RegisterAll()`；新增类型 = 一个文件 + 追加一行注册 + `ParticleIds` 加一个字段（**只能追加，顺序是存档兼容性的一部分**）。
- 世界另持两张与网格同形的场：热图 `HeatMap`（每格存热量 Q）与湿度场 `HumidityField`（每格存水汽，只有非固非液的格持有；云 = 湿度高的空气，不是粒子），外加光照控制器 `LightController`。

## 热与湿度

- 温度是导出量 `T = Q / 类型热容`；热由物质携带（`Swap` 搬 Q、`Set`/`Clear` 就地继承 Q）。
- `StepLogic` 每逻辑帧在粒子扫描后做：全格四邻热传导（传导界内守恒）+ 两条散热路（顶行向虚空 `VoidHeatLoss`、只作用于暴露面并按材料辐射率 ε 缩放的辐射 `RadiativeLoss`）+ 湿度输送（扩散 + 温度定向：水平冷→热、竖直越热越向上）与降雨，再由光照向「每列最上面的非气体粒子（光打到水面就停）」施热，且按光路湿度做 Beer–Lambert 衰减——云越厚地表越晒不到。持续受热的暴露格就近收敛到 `T ≈ 输入功率 / (RadiativeLoss · ε)`。
- 蒸发/凝结按 `HumidityField.LatentHeat(T)` 显式结算潜热（蒸发冷却、凝结放热）；气体竖直 Swap 按 `HeatMap.AdiabaticLapse` 做绝热直减（上升冷却/下沉增温）；静止的水比正上方暖 0.5° 就上浮一格（`TryLiquidConvect`，海洋翻转——「夜间海面比陆地暖」与海陆风昼夜换向的机制）。
- 水沸腾时把水汽集中成一小片（可见白汽）；平时慢蒸发只把水汽摊薄进空气（不可见，只在液面）——可见水蒸气只在沸腾时出现；低温饱和处凝结成雨。
- 两个 O(W·H) 浮点 pass（`HeatMap.Step`、`HumidityField.Step`）跑在 Burst（`[BurstCompile(FloatMode = FloatMode.Strict)]` 指针内核，类级 `[BurstCompile]` + 直调（DirectCall）；`#if UNITY_2022_3_OR_NEWER` / `#else` 走同一内核的 managed 分支）。**不要用 `CompileFunctionPointer`**——本机编辑器 entry point 发现不可靠。热传导 job 化已实测否决（~0.2ms/帧，调度开销吃掉全部收益），刻意保持串行直调；湿度因 budget 读改写 + pair 顺序即语义同样串行。
- `HeatMap.Step` 有不动点跳过（热场收敛且输入未变时整趟跳过，逐位等价，默认开；收益只在完全静止且已收敛的场；`Swap` 只在两端 typeId 不同时推进格子写入版本）。
- 热力学系数（`_conductivity`/`_voidHeatLoss`/`_radiativeLoss`/`_adiabaticLapse`/`_airThermalConductivity`/`_airThermalEmissivity`）在 `ParticleSimulationSettings` 资产，驱动每帧写回 `world.Heat`。

## 随机刻（Minecraft 式）

每逻辑帧在粒子扫描之后、热传导之前，`StepLogic` 均匀有放回地随机抽 `world.RandomTicksPerStep` 个格，命中非空气、非静态的格调其类型的 `OnRandomTick`——用于「缓慢、按概率发生」的过程（生长/蔓延/渗流/腐化）。抽取与世界同一 xorshift32 随机流（可复现）、不碰 bit0 处理标记；**默认 0（关闭，保持既有测试契约）**，生产值由 `ParticleSimulationSettings._randomTicksPerStep` 每帧注入（资产现配 48 ≈ Minecraft 的 3 格 / 4096 密度）。契约见 `docs/ParticleTypeInterface.md` §6。各类型用法见 `docs/modules/Particles.md`。

## 跨类型规则

只看标签不看 typeId：`ParticleTags` 的 rank 次序 `Solid(3) > Liquid(2) > Gas(1) > 空气(0)` 决定谁置换谁（`ParticleRules`）。固体置换液体另有「地下水」闸门（`ParticleWorld.GroundwaterSealingEnabled` 默认开）：只有**运动中**的固体（上一运动扫描位移过，或运行中刚放置/相变）能排开液体——静止的岩石不会把水挤上去，石头/沙里封闭的水稳定不动（土例外：干土按土壤反应吸正上方的水）；下落的沙石照旧沉底。

`ParticleTags` 另有属性标签 `Organic`（不参与 rank）：草、花、树干、树叶、树木种子带它；世界在它们死亡时按 20%（`OrganicDecayDenominator` = 5）就地生成腐殖质（写在 `Set`/`SetPhase` 之后——`OnKill` 在写新格之前、写自身格会被覆盖，故归世界）。**显式霉烂**（新类型本身就是腐殖质）不再叠加衰变骰——骰只在「有机旧类型被非有机新类型替换」时摇。草种子/水生植物故意不带 Organic。

## 运动休眠与两级时基

- **运动帧 60 Hz**（`world.StepMotion()`）与**逻辑帧 20 Hz**（`world.StepLogic()`，每 `LogicTicksPerMotionTick` 个运动帧一次）。
- 运动休眠默认开启：只求值候选格——写入格按 `MotionReadReach` 盒反转并集唤醒（生产表 151 格）+ 温度变化格按紧凑脏索引逐格置位半径 2 Chebyshev 球（⊇ 实际温度读距）；静止粒子整块跳过；`MotionSleepEnabled=false` 回退全网格旧路径。
- 逻辑帧混合扫描：休眠开启时运动候选格跑完整「先逻辑后运动」、其余带 `Info.HasLogic` 的格只跑 `OnLogic`（`OnLogic` 含随机重掷/相变，**永不休眠**）；扫描进行中的热/湿度写入经 `DirtiedHook` **同帧**并入候选位图（否则晚一帧、逐位发散）。再跑随机刻/热传导/湿度/降水/光照。

## 确定性

模拟只认 `world.Tick` 与世界持有的 xorshift32；禁止 `UnityEngine.Random` / `Time.*` / `Input`。同种子同命令序列逐格可复现。

## 粒子类型作者约束（违者拒收）

- 一个类型一个文件、放 `Simulation/Particles/`、`public sealed`、非 MonoBehaviour。
- **运动写在 `OnMotion`（60 Hz）**（唯一例外：`FireParticle` 游离火上升写在 `OnLogic` 末尾）、**相变/反应写在 `OnLogic`（20 Hz，先于同格运动）**；两者内无分配/LINQ/字符串/`Time.*`；先判断后写、写后立即 return；跨类型判断只走 `ParticleTags`，不特判 typeId/DebugName。
- 必须给有限正数 `HeatCapacity` 与 `[0,1]` 内的 `ThermalConductivity`/`ThermalEmissivity`；读温度用 `world.Heat.GetTemperature`、改热量用 `AddHeat`/`SetHeat`，不要自己重复做热传导。
- 随机刻行为写 `OnRandomTick`（没有就留空），禁分配与 `Time.*`，随机用 `world.NextUInt()`。
- **随机源分路径**：运动路径（`OnMotion` 及 helper）**禁止消耗世界随机流**，方向等随机决策一律用 `HashNoise` 坐标哈希（`(x, y, Tick)` 纯函数——运动休眠的前置）；`OnLogic`/`OnRandomTick` 用 `world.NextUInt()`。
- 每个类型必须声明 `Info.MotionReadReach`（`OnMotion` 对 `_cells` 的轴对齐阅读盒，默认 `(1,1,1)`，宁大勿小；读温度不计入——由温度脏路径覆盖），且 `OnMotion` 不得读湿度/光照等非写入即变的状态、单步位移 ≤ 1 格。
- `OnLogic` 严格空体的类型声明 `Info.HasLogic = false`（构造参数 `hasLogic: false`，默认 `true` 保守）。

## 世界常驻观察者（零 RNG / 零写入，读数不改模拟）

- `MotionMonitor`（按标签掩码的通用运动量观察者，世界持两实例）：`world.LiquidMotion`（带 `Liquid`）导出 `MovingRatio` 供水流动声；`world.SolidMotion`（`Solid` 且非 `Organic`）供落沙声读**绝对** `MovingCount`。O(1) 增量维护 `TrackedCount`（Set/SetPhase 差分）与 `MovingCount`（Swap 按换位匹配端数），掩码构造时对 `world.Types` 摊表。
- `ParticleCountMonitor`（`world.Counts`）：按类型 O(1) 维护存量 `Count(typeId)` 与新增格数 `ConsumeCreated(typeId)`（读后清零）。`Clear` 走 `Set`、`Swap` 不参与、空气与越界 id 恒 0 不抛。
