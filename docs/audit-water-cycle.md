# 审计问题清单：水循环重构（d9e77e6..f89412f，6 个提交）

> 审计日期：2026-10-02 ｜ 范围：双时基重构（OnLogic/OnMotion + 随机刻）、湿度场取代蒸汽/云粒子、湿土、气体密度换位、每材料导热率、暴露面辐射、云遮光、昼夜亮度、光标读数
> 方法：两路并行静态走查 + 仓库外编译桩实测复现（harness：`E:/tmp/tap_audit_harness`，`dotnet run -c Release`）
> 总体结论：核心契约（界内热守恒、辐射暴露面、湿土 1:1 搬运、密度次序、Beer–Lambert、昼夜 Daylight）经实测无误；无递归、无 O(N²)、热路径零分配。**发现 4 个可复现 bug、8 项风险、若干风格/文档残留。**

## Bug（实测复现，建议修）

### B1. 水平热压流确定性 60Hz 往复振荡（拖湿度往复）

- 位置：`ParticleRules.TryThermalDrift`；调用点 `AtmosphereParticle` / `CloudParticle`（已删）/ 湿区云滴
- 证据：harness 用例 1——9×5 静态墙走廊两层大气，冷粒子 T=20、右邻 T=30：标记位置 8 帧 = 5,4,5,4,5,4,5,4；两格 Q 每帧 20↔30 对调。机制：冷格换向唯一暖侧后热量随粒子搬走，下一帧「唯一更热侧」依旧成立，于是换回。无随机项、完全确定。这正是 1671b02 想消灭的云层抖动。
- 修法（首选）：停用粒子级 `TryThermalDrift`——湿度场已有「水平冷→热」的场输运（`HumidityField`），物理效果相同且守恒不振荡。若保留粒子级风，必须加打破时间对称的机制（概率门只降频率不根治），并补「稳定格对 N 帧内不来回换位」回归测试。
- ✅ 已修（2026-10-04）：保留粒子级风，改加**风向记忆**（`CellFlags.DriftDirectionMask` bit3-4）——顺向 / 无记忆才漂、反向无条件挡、无触发清记忆、浮力成功清记忆，完全确定不耗 RNG；新增 `ThermalDriftTests`（6 个，含「两格走廊不逐帧往返」回归）。

### B2. 「慢蒸发不可见」契约在半封闭地形失效

- 位置：`HumidityField.SpreadUnitIntoAir`（`share = Unit / receivers`）；入口 `WaterParticle` 慢蒸发、`WetSoilParticle` 受热干燥
- 证据：harness 用例 2/2b——9×9 石墙口袋剩 2 个气体格 → 每格 0.5；单接收格 → 1.0。均远超可见阈值 0.06，且 ≥ 0.4（RainThreshold）下一逻辑帧可直接降雨。注释算术也错：半径 4 = 9×9 = **81** 格（不是 49）；要不可见需 share ≤ 0.04 ⇔ 接收格 ≥ 25。
- 修法：加 `maxShare` 门控（接收格 ≳19 才蒸发，不足则返回 0 保留水）；沸腾路径按设计可见，不加。同步修正 docs 与注释中「永远不可见」表述。
- ✅ 已修（2026-10-04）：`SpreadUnitIntoAir` 改 5 参并加份额门控，`InvisibleSpreadMaxShare = 0.04`（接收格 < 25 → 返回 0、调用方保留水、不相变），沸腾传 0 不限；注释改正「半径 4 = 9×9 = 81 格」与「接收格足够才不可见」，docs 同步。

### B3. 水慢蒸发查正上方缺 `IsInBounds`

- 位置：`WaterParticle.cs:89`（`CanEvaporateSlowly`）
- 证据：harness 用例 3——世界顶行的水把越界当空气（`IsGasOrEmpty` 越界 = true），蒸发成功，整份水汽（1.0）落进自己那格。对比 `WetSoilParticle.cs:80` 有显式 `IsInBounds`。
- 修法：补 `world.IsInBounds(x, y + 1)`；若设计上允许顶行蒸发，必须配合 B2 的份额控制。
- ✅ 已修（2026-10-04）：`WaterParticle.CanEvaporateSlowly` 首查 `world.IsInBounds(x, y + 1)`——顶行的水不再蒸发；配合 B2 的份额门控，慢蒸发只在液面发生。

### F1. RiseStop 门控关掉整对竖直交换，可把湿度永久冻死

- 位置：`HumidityField.cs:421-428`
- 证据：`budget[a] < RiseStopHumidity` 的门控包住的是该对**唯一的** `Transfer`（含对称扩散项），实现强于注释声称的「不再上升」。可构造锁死：1 格宽水井慢蒸发摊 5 格各 0.2 → 竖直对全关、左右是固体、上方因下格饱和不收 → 湿度永冻；0.2 ≥ `CloudFullHumidity`(0.16) → 渲染为常驻纯白列。次要：门控读的是被水平出流扣过的剩余 budget，且扫描固定 +x 无奇偶交替 → 方向偏置。
- 修法：门控只加在平流项上（保留 `diffusion * (self - budget[b])` 对称项），或改用 `current` 缓冲的真实湿度做门控；补「1 格宽水井」回归测试。
- ✅ 已修（2026-10-04）：`RiseStopHumidity` 只门控 `upward` 平流项、对称扩散项照常结算，且改用真实湿度 `current[a]` 判定；`HumidityField.Step` 的 x 扫描按 `world.Parity` 交替消偏置；补「1 格宽水井」回归测试。

## 风险

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| B4 | `LightController` + `HumidityField.ColumnSums` | 列云厚把**光路下方**（洞穴/顶棚下）的湿度也算进去：实测洞下湿度 10 → 地表受热 5.0→3.03 | `ApplyHeat` 自顶向下扫描时边走边累加、遇第一个 `BlocksLight` 即停（顺带省掉每逻辑帧一遍 ColumnSums）。✅ 已修（2026-10-04）：按此实现——`ApplyHeat` 边走边累加**光路湿度**（洞穴/顶棚下的湿度不再遮下方地表），`GetCloudThickness` 改为按需扫描，`ColumnSums` 保留给测试/工具、不再每逻辑帧重算 ｜ **2026-10-16 更新**：`ApplyHeat` 改为读 `LightController` 的每列增量缓存（挡光顶 + 光路湿度），连「自顶向下边走边累加」的那趟扫描也不再每逻辑帧发生——缓存由 `Set`/`SetPhase`/`Swap` 与 `HumidityField.ChangedHook` 维护，口径仍是「光路上的湿度」 |
| B5 | `HeatMap.IsSkyExposed` | 「暴露面」只查正上方一格：洞穴内空气也算暴露、被辐射抽热 | 接受就写注释；要准就复用 B4 的扫描结果。✅ 已修（2026-10-04）：按「接受」结案——`HeatMap` 注释与 docs 已写明「暴露面只查正上方一格、洞穴内空气按暴露面处理」为已知简化 |
| B9 | `HumidityField` 输运 + 降雨条件 | 湿度输运冷→热、降雨要求 T≤露点（30)：热地表只进不出，可能积累永久白斑（推断，无复现） | 出现观感问题再加温度连续凝结核或高温慢降雨。⚠️ 仍开放（2026-10-04）：C2 改用指数饱和曲线 `SaturationHumidity` 后已缓解（低温空气更易凝结、取消 T>露点硬截断），但高温下饱和湿度指数升高、**极热表面仍难降雨**，未根治——保留为风险注记（见 docs §13.8） |
| B7 | `WetSoilParticle.cs:66-95` | 上行无概率、下行 50% 不对称：热源消失时水在相邻两格往返 2~5 帧（8/8 种子复现）；守恒不受影响 | 接受就补注释；或给下渗加 `T < DryingTemperature` 条件。✅ 已修（2026-10-04）：上行加 50% 概率门（与下渗同分母消时间对称）、下渗加 `T < DryingTemperature` 温度门，热源消失即停渗；补回归用例 |
| F4 | `ParticleWorld.SetPhase` | 无条件搬湿度，但固/液格的湿度永不参与输送、被 `Set`/`Clear` 时静默丢失（`Sum()` 仍计入，守恒断言看不见泄漏） | 目标非气体且残差 > 0 时推给周围 moist 邻居，或在 SetPhase 的 remarks 写明该义务。✅ 已修（2026-10-04）：新增 `ParticleWorld.EvacuateHumidity`——`Set`/`SetPhase` 目标非气体时先把残差推给四邻气体格、推不掉才原地保留（守恒优先），湿度不再静默丢失 |
| F5 | `ParticleWorld.cs:800-807` | 「相变后跳过运动」只比 typeId：同类型 Swap 可绕过（当前所有 OnLogic 均未违反，潜在） | 在 OnLogic 契约里明确「同类型 Swap 同样禁止」；可加恶意探针类型测试锁死。✅ 已修（2026-10-04）：`ParticleWorld` 段扫描注释与 `OnLogic` 契约已明确「同类型 `Swap` 同样禁止绕过本帧的运动跳过」 |
| B6 | `HeatMap.cs:436` | T < 0 时辐射项 `-λ·ε·T` 变加热（等价向 0 环境松弛，无害但「只散热」表述不成立） | 文档补一句。✅ 已修（2026-10-04）：`HeatMap` 注释与 docs §6 已补「T < 0 时辐射项符号反转变成加热（向 0 环境松弛），『只散热』仅对 T > 0 成立」 |
| B8 | `SoilParticle.cs:52-57` | 吸水判据用 Liquid 标签：未来任何液体都会被当水灌成湿土 | 未来加岩浆/酸时改 `id == ParticleIds.Water`；否则写清「所有液体视为水」。✅ 已修（2026-10-04）：`SoilParticle` 判据旁已注释「所有液体视为水；未来岩浆/酸改 `id == ParticleIds.Water`」，docs §4 同步 |

## 性能备忘（当前网格无虞）

- 每逻辑帧 ≈ 6 遍 O(N)（扫描 + 热 2 遍 + 湿度 + 降雨 + 光照；列和已并入光照的每列缓存——光照那一步现在是 O(宽) 的，不再有整列 / 整网格求和），320×180 ≈ 8M 格访问/秒。无递归、无 O(N²)、热路径零分配零 LINQ。
- 湿度输送每对邻居重复 `GetTemperatureAt`（≈4 次/格，各含一次热容查表 + 除法）——`HeatMap.Step` 每逻辑帧已算好 `_inverseCapacity`，可复用。
- `ColumnSums` 在光照关闭时仍每逻辑帧整列求和——并入 `ApplyHeat`（见 B4）后自然消失。✅ 已修（2026-10-04）：`ApplyHeat` 自顶向下边走边累加**光路湿度**，`ColumnSums` 已不再每逻辑帧调用（方法保留给测试 / 工具）。**2026-10-16 更新**：该趟光路扫描本身也已被 `LightController` 的每列增量缓存取代（施热 = O(宽) 逐列循环，抬顶 O(1)、顶被抹掉 / 挡光格换位时重扫受影响的列）。
- 网格上限 4096² 时热+湿场合计约 0.5GB 内存，文档未标注。

## 风格 / 文档残留（低）

- `IParticleType.cs:48-49` 自相矛盾：「运动帧不做任何相变/反应」与「禁止相变**之外**的跨类型反应」并存，与 docs §5 表冲突。
- 残留重构前措辞：「凝结物/悬浮水滴/Steam/云滴」（`ParticleWorld.cs:342, 364-372, 412`）；`SetPhase` 文档说携带「热量 Q 与凝结物」，实际是 Q + 湿度。
- 失效 cref：`ParticleWorld.Step` 已拆分为 `StepLogic`/`StepMotion`，`HumidityField.cs:364`、`HeatMap.cs:354`、`LightController.cs:7/:206` 仍引旧名。
- 「每 tick」应为「每逻辑帧」：`HumidityField.cs:38/42/77/328`、`LightController.cs:85` 等。
- `ParticleRegistry.cs:89` 的 `<exception>` 列表漏写新增的 ThermalConductivity / ThermalEmissivity 两条校验。
- `ParticleWorld.cs:686-688`：StepLogic 接续注释漏写湿度/降水阶段；且「异常时各阶段不生效」的表述对 `_tick++`（已先执行）不成立。
- `HumidityField.Step(world)` 未做 null 检查（类内其它公开方法统一抛 `ArgumentNullException`）。
- 湿土先摇骰后判目标（B11）：RNG 消耗随湿土格数漂移，建议先判后摇（短路）。✅ 已修（2026-10-04）：`WetSoilParticle.OnLogic` 改为**先判目标**（下 / 左 / 右干土）、**再摇骰**，随机流不再随场上湿土格数漂移。

## 已修（`519f72f`）

- `ParticlePixelBuffer` 注释「到 RainThreshold 淡入」→ 实为 `CloudFullHumidity`（RainThreshold 是降雨阈值，与渲染无关）。
- `WaterParticle` 慢蒸发概率注释「每 tick」→「每逻辑帧」。

## 实测确认无误（节选）

- 每材料导热率逐对抵消：混合 4 种材料 64 步 ΣQ 相对漂移 5.7e-7（纯浮点舍入），界内严格守恒。
- 辐射暴露面：埋藏格不掉热、暴露格 ΔQ = −λ·ε·T 精确命中；判定 O(1)/格。
- 湿土：SetPhase 保 Q + AddHeat 水格 Q + Clear 归零，总 Q 精确守恒；1 水格 ↔ 1 湿土；上行/蒸发先判后写、成功即 return。
- 气体密度次序按 `Info.Density` 数值比较（非标签），`>=` 放行同密度互混；重者下沉轻者上浮后即分层，不成环（B1 的振荡来自热压流决策规则，与密度比较无关）。
- Beer–Lambert 逐列 `exp(-k·列湿度)` 独立正确；Daylight 相位数学连续无除零；渲染消费 `Lerp(_nightBrightness, 1, Daylight)`。
- 双时基：逻辑帧是运动帧节拍的子节拍；随机刻在 `_tick++` 之前；异常语义与文档一致。
- 测试 147 个（ParticleWorldTests 89 + SandStone 2 + ThermalDrift 6 + Water 10 + Weather 24 + WetSoil 16），与 AGENTS.md 一致。
