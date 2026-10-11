# 粒子系统接口规范

> 状态：v1 草案，待团队评审后冻结
> 适用：Unity 2022.3.62f1 / URP 14.0.12 (2D Renderer) / Mono2x / .NET Standard 2.0
> 位置：`docs/` 位于 `Assets/` 之外，Unity 不会导入，无需 `.meta`。
> 本文只规定「一个粒子类型类应该长什么样」，**不规定游戏里应该有哪些粒子类型**——类型清单由各人按需求新增，走第 11 节的流程。

---

## 1. 不可协商的设计约束

这些是架构前提，任何类型实现都不得绕开：

1. 所有粒子的数据存在**一个连续的扁平数组**里（`Cell[]`），靠 `(x, y)` 直接算出下标寻址；**没有任何 GameObject / MonoBehaviour / ScriptableObject 承载单个粒子**。
2. 单个粒子只存**原始数据**（类型标识 + 状态位 + 颜色抖动索引），不含行为逻辑、不含物理组件、不含引用类型字段。
3. 类型属性（标签、密度、调色板、最大横流格数、是否受重力、是否静态）存在**全局静态注册表**里，按 `typeId` O(1) 数组寻址。
4. 注册表在启动时**一次性**初始化：每类型实例化一个行为单例，把 `OnMotion` / `OnLogic` / `OnRandomTick` / `OnSpawn` / `OnKill` 提取成**委托**（函数指针）存进注册表数组。
5. 运行时只做 `typeId → 委托` 的查表直调。**热路径禁止**：字符串匹配、`Dictionary` 查询、接口虚分派、反射、装箱、闭包、LINQ、任何堆分配。
6. 位置变化一律通过**交换两个下标的 Cell 数据**完成，模拟过程中不产生任何对象创建或销毁。
7. 粒子间交互由当前被处理的粒子**直接读写邻居格**实现，不走事件、不走消息、不走回调链。
8. 生成 / 抹除是模拟系统提供的**原子操作**：语句无条件执行，所有前置判断由调用方在调用前完成。

### 允许的例外（明确划界）

- 类型单例上的**私有实例字段做类型级暂存**是允许的（例如反应扩散的邻域缓冲）。理由：整个模拟单线程、每类型只有一个实例，不存在 per-cell 状态。
  **禁止**把 per-cell 状态放进单例字段——per-cell 状态只能放进 `Cell`。
- 类型级配置常量（密度、调色板）写在类里；**全局**模拟参数（网格尺寸、运动/逻辑帧速率、随机种子）走 `ScriptableObject`。
- `string DebugName` 允许存在，因为它只用于编辑器/日志显示，**永不出现在模拟热路径**。

---

## 2. 坐标、索引与边界

| 约定 | 定义 |
|---|---|
| 坐标 | `x ∈ [0, Width)`，`y ∈ [0, Height)`，整数格坐标 |
| 原点 | **(0, 0) 在左下角**；`y` 向上增大 |
| 重力方向 | **-y**（向下） |
| 下标 | `index = y * Width + x` |
| 扫描 | 行从 `y = Height-1` 到 `0`（从上到下）；同一行内 x 方向逐帧交替 |
| 越界读 | `GetCell` 返回**空气格**（`typeId == 0`），不抛异常 |
| 越界写 | `Set` / `Clear` / `Swap` 是**定义好的空操作**，返回 `false`，不做任何写入 |
| 空气 | `typeId == 0` 保留，永不注册行为，永不进入行为调用 |

`IsInBounds(x, y)` 存在且是首选判断方式。越界写是**静默空操作**：不抛异常、不刷日志（逐格日志会淹没控制台）。类型代码必须自己先判断，写操作内部的越界检查只是安全网。
唯一会报错的非法输入是「未注册的 `typeId`」：`Set` 会拒绝写入并 `Debug.LogError` 一次（每个世界实例至多一条）。

网格尺寸上限：单边 ≤ 4096（`Texture2D` 极限 16384，但 CPU `Color32` 缓冲与全量上传成本随面积线性增长）。

**比例约定**（叙事用，不影响任何模拟参数——CA 规则全是局部的）：**1 格 ≈ 10 m、1 运动帧 = 1/60 s**，幕布 320×180 ≈ 3.2 km × 1.8 km（近海海陆风的合理尺度）；温度是模拟内部相对温标，不与摄氏换算。昼夜是光照相位、按**逻辑帧**计数：600/600 逻辑帧 = 1200 / 20 Hz = **60 s 一昼夜**（逻辑帧降到 20 Hz 后一昼夜相应变长；要维持旧观感把 `_litTicks` / `_darkTicks` 调到 200/200）。

---

## 3. Cell 数据布局

**唯一真源：`Cell.cs`。** 其它任何文件不得复制该结构。

```csharp
// Assets/Scripts/Simulation/Cell.cs
[StructLayout(LayoutKind.Sequential)]
public struct Cell
{
    public ushort typeId;   // 0 = 空气；非 0 = 全局注册表下标
    public byte   flags;    // 见下表；bit0 由模拟系统独占管理
    public byte   shade;    // 调色板索引，生成时按该类型调色板随机抽样
}
```

- 尺寸 **4 字节**，无填充。
- `Array.Sort` 式的大数组访存因此是每格 4 字节；网格 1024×512 = 512K 格 = **2 MB** CPU 缓冲，同尺寸 `RGBA32` 纹理 = 2 MB。
- `typeId` 为 `ushort`：最多 65534 个类型（0 被空气占用；实现上限 `ParticleRegistry.MaxTypeCount = ushort.MaxValue - 1`）。
- `shade` 为 `byte`：调色板最多 256 色。
- **热不进 `Cell`**：每格的热量 Q 存在与网格同形的独立平行数组里（`world.Heat`，见 §6）。`Cell` 仍是 4 字节；热由**物质携带**——`Swap` 会把 Q 一起搬走，`Set`/`Clear` 换上的新物质**继承该格原有热量**（旧物质离场，热就地留给新物质）。温度是导出量：`T = Q / C`，热容 C 是**类型属性**（`ParticleTypeInfo.HeatCapacity`）再加上该格湿度带来的附加热容。例外与补充：`SetPhase` 是同一份物质换形态、**Q 原样保留**；`Swap` 还会同步搬运**湿度**，并对换动的**气体**格按竖直位移结算**绝热直减**（上升冷却、下沉增温）。详见 §6。

### flags 位分配（`CellFlags.cs` 集中定义，禁止散落魔法数字）

| 位 | 名称 | 归属 | 语义 |
|---|---|---|---|
| bit0 | `ProcessedParity` | **模拟系统独占** | 存「该格最后一次被改动的 tick 奇偶」——**历史标记**，类型代码必须原样保留。世界内部的「本帧已处理」判据另有一张逐扫描清零的位图（见 §7「运动休眠」），因为 bit0 只有 1 位奇偶、无法区分「本帧已处理」与「偶数帧前处理过」（休眠会跳过格，这个歧义会真的出现） |
| bit1 | `Burning` | 通用（类型无关） | 燃烧中 |
| bit2 | `Forced` | 通用（类型无关） | 受外部作用力 / 被推动 |
| bit3-7 | 类型私有 | 各类型自行定义 | 必须在**该类型文件头部注释**里登记占用位 |

已登记的私有位：**大气** bit3-4（`CellFlags.DriftDirectionMask`，水平热压漂移的方向记忆）、**草种子 / 水生态种子** bit5-6（`CellFlags.SeedStrikeMask`，「无发芽条件的随机刻命中」计数 0..3；两种种子**共用**同一组位——一格同时只属于一个类型，互不冲突）、**草 / 水生植物** bit3-5（`CellFlags.GrassAgeRemainderMask`，年龄的逻辑帧余数 0..4；年龄高位存在 `Cell.shade` 里——两者的 `OnLogic` 都是「每逻辑帧年龄 +1、每 5 帧把 `shade` 推进一档」（`shade` 即颜色档），只是寿终动作不同：草 `Clear` 走 20% 有机衰变、水草 `SetPhase` 还成水）、**树木三件套（种子 / 树干 / 树叶）** bit3-5（`CellFlags.TreeSpeciesMask`，物种编号 0..4；三种树木类型共用）与 bit6-7（`CellFlags.TreeStageMask`＝树干生长阶段 0..2；`CellFlags.TreeSeedStrikeMask`＝树木种子的空转计数 0..3；`CellFlags.TreeLeafDetachedMask`＝树叶失活标记——同一组位按「一格一类型」互斥）；草与树不同时占一格，bit3-5 的两种用途互不冲突）。**花（`FlowerParticle`）不占用任何私有位**：它的 `Cell.shade` 是花色而非年龄档，也不需要计数器（不衰老、不繁殖，只做三条随机刻死法）。

规则：

- **世界内部用逐扫描清零的位图记录「本帧已处理」**（`ParticleWorld._processedBits`，O(N/64) 一次清零），不再依赖 bit0 的奇偶——休眠会跳过格，bit0 会残留成与当前帧同奇偶的旧值，用它判定会漏求值。bit0 仍由写操作按旧语义维护（`Set` / `SetPhase` / `Swap` 写成当前 parity），类型代码**永远不要**直接整体赋值 `flags`：用 `world.At(x,y).flags |= CellFlags.Burning;`（OR 保留 bit0）这类写法，整体赋值 `flags = ...` 会破坏 bit0，属于违规。
- 类型私有位是安全的：一格在同一时刻只属于一个类型。
- **就地改 flags 后若影响自己的 `OnMotion`，必须调 `world.WakeMotion(x,y)`**：`At()` 写 flags 不走 `Set` / `Swap`，运动休眠收不到「这格变了」的信号，会把它当静止跳过（`TreeLeafParticle` 的失活标记是首个用例）。详见 §6 世界 API。
- 类型在 `OnMotion` / `OnLogic` 里**不需要**检查「本帧是否已处理」——模拟系统在派发前已经跳过本帧已处理的格。

### 关于 `vx` / `vy`（速度场，可选）

**v1 默认不包含**，`Cell` 固定 4 字节。理由：基础元胞自动机规则不需要速度场，而 4 字节布局的访存密度更好。

启用时机与代价：**后期可安全补上**。因为所有跨格移动都走 `Swap` / `Set`（整格拷贝），在 `Cell.cs` 追加

```csharp
    public sbyte vx;        // 变为 6 字节（对齐 2，无填充）
    public sbyte vy;
```

不会波及任何类型代码。推进到速度场模拟时改这一个文件即可（外加 `ParticleWorld` 的写入初始化）。

---

## 4. 全局类型注册表

```csharp
// Assets/Scripts/Simulation/ParticleTags.cs —— 类别标签（跨类型通用规则的唯一判据）
[Flags]
public enum ParticleTags : byte
{
    None   = 0,
    Solid  = 1 << 0,   // 固体：堆叠、有安息角
    Liquid = 1 << 1,   // 液体：自由横流
    Gas    = 1 << 2,   // 气体：可被置换，向上
}
```

```csharp
// Assets/Scripts/Simulation/ParticleTypeInfo.cs —— 作者侧结构（只在注册时读一次）
public readonly struct ParticleTypeInfo
{
    public readonly ParticleTags Tags;           // 类别标签 Solid/Liquid/Gas（至少带一个）+ 属性标签 Organic（不参与 rank）
    public readonly float    Density;            // 密度；类别内再排序：气体换位按它（重的沉穿轻的，见 §6「气体按密度次序」）
    public readonly float    HeatCapacity;       // 热容 C（有限正数）；温度 = 热量 / C，见 §6
    public readonly float    ThermalConductivity; // 相对导热率 σ ∈ [0,1]；界面导热率取两侧平均，见 §6
    public readonly float    ThermalEmissivity;   // 相对辐射率 ε ∈ [0,1]；暴露面按 ε 缩放辐射，见 §6
    public readonly bool     AffectedByGravity;  // 是否受重力
    public readonly bool     IsStatic;           // 静态：扫描直接跳过，不进入 OnMotion / OnLogic
    public readonly byte     MaxFlowSpread;      // 最大横流格数；水的带闸门横流把它用作「落差视野」扫描范围（每 tick 只横移 1 格）
    public readonly MotionReadBox MotionReadReach; // OnMotion 对 _cells 的轴对齐阅读盒 (x±X, y−Down..y+Up)，默认 (1,1,1)。世界把各类型的盒反转求并集作为运动休眠的写入唤醒集，见 §7「运动休眠」
    public readonly float    Friction;           // 摩擦度 0..1，只读于固体运动规则（见下）
    public readonly float    Stickiness;         // 粘性 0..1，只读于固体运动规则（见下）
    public readonly float    LightAbsorption;    // 光照吸收率 ∈ [0,1]（可选，默认 1）：光照注入本格时按它缩放，只读于 LightController
    public readonly Color32[] Palette;           // 调色板，长度 1..256，索引 = Cell.shade
    public readonly string   DebugName;          // 仅调试显示，热路径禁用
    public readonly bool     HasLogic;           // OnLogic 是否含真实行为（构造参数可选，默认 true）；false = 严格空体，逻辑帧扫描整段跳过，见 §7「逻辑帧混合扫描」
}
```

### 运动阅读盒（MotionReadReach）：运动休眠的作者契约

**每个类型必须声明自己的 `MotionReadReach`**——一个轴对齐盒 `(X, Up, Down)`：该类型 `OnMotion`（含它调用的 `ParticleRules` helper）的决策**最远读到** `_cells` 的哪些格，即 `(x ± X, y − Down .. y + Up)`（含边界）。默认 `(1, 1, 1)`（八邻）。

世界把类型表里所有盒**反转**求并集，作为运动休眠的写入唤醒集：写入格 W 时，所有「读得到 W」的格都要重新求值。反转规则是 `(X, Up, Down) → (X, Down, Up)`（读者读 `(x ± X, y − Down .. y + Up)` ⇔ 写在 `(W ± X, W − Up .. W + Down)` 里都要唤醒它），详见 §7「运动休眠」。

- 声明值**必须 ≥ 实际读距（宁大勿小）**：偏小会让读得到被写格的粒子在邻居变化后唤不醒，模拟结果与全扫描路径不一致；偏大只是多求值几格。
- 语义是「最远**读**到」，不是「最远写到」——写操作自己就会唤醒周围。
- **不要把「读温度」算进盒里**：温度读距由世界的温度脏路径（Q / 湿度变化格逐格置位半径 2 的 Chebyshev 唤醒球）单独覆盖，与结构读距是两条独立通道。所以大气 `TryThermalDrift` 的 `x ± 2` **不进盒**（它只读温度），石头的 `TrySlope` 净空链**要进盒**（它读 `_cells` 的结构）。
- 只读 `_cells` 的读者与只读它的写法是同一份盒：世界在每次写入时按并集置位。
- 当前生产类型的声明与依据：

| 类型 | 盒 | 依据 |
|---|---|---|
| 沙 | `(1, 0, 1)` | 0/0 摩擦粘性 → 斜滑净空 1，只读正下与斜下 1 格 |
| 土 / 湿土 / 腐殖土 / 湿润腐殖土 / 腐殖质 / 草种子 / 水生态种子 | `(1, 0, 3)` | 0.5/0.5 → 粘住时净空 `1/(1−0.5) + 1/(1−0.5) − 1 = 3` |
| 水 | `(5, 1, 1)` | 落差视野扫描 `MaxFlowSpread = 5`（同行 `x ± 5`、读 `(scanX, y−1)`）；对流读 `(x, y+1)` |
| 石头 | `(1, 0, 19)` | 粘住时净空 19，斜滑净空链读 `(x ± 1, y−1 … y−19)` |
| 大气 | `(1, 1, 1)` | 浮力读四邻；热压流的 `x ± 2` 只读温度，不进盒 |
| 草 / 水生植物 / 火 | 默认 `(1, 1, 1)` | `OnMotion` 为空，无需放宽 |

### HasLogic：何时可声明 `OnLogic` 为空（作者契约）

`ParticleTypeInfo` 的构造参数末尾有一个可选 `bool hasLogic = true`，对应只读字段 `Info.HasLogic`。它是**性能能力标志**，不是语义开关：

- **只能在该类型的 `OnLogic` 是严格空体时传 `hasLogic: false`**。「严格空体」= 方法体里只有注释，**不读**任何状态、**不写**世界、**不消耗世界随机流**（`NextUInt` / `NextInt` / `NextByte`）。满足时跳过这次调用逐位等价，只是省下每次逻辑帧的空调用与相变后 `typeId` 复查。
- **默认 `true` 是刻意的保守选择**：新类型忘了设标志只会多一次空调用，绝不会静默丢掉逻辑（例如误把有随机重掷的 `OnLogic` 标成 `false` 会改变蒸发 / 熄灭速率）。
- 纯运动型粉末 / 静态材料 / 靠随机刻驱动的类型通常是候选（沙、石头、大气、**腐殖土**、种子、花等）；含相变 / 反应 / 邻接写入 / 每帧状态推进（水、火、土、湿土、湿润腐殖土、腐殖质、**草**、**水生植物**——后两者的 `OnLogic` 每逻辑帧推进年龄）必须保持 `true`。
- 世界按 `HasLogic && !IsStatic && 非空气` 维护一张逐格位图 `ParticleWorld._logicCells`（见 §7「逻辑帧混合扫描」），在 `Set` / `SetPhase` / `Swap` 时同步。因此**声明错了会立刻反映到逻辑帧扫描的跳过上**：`false` 的类型在逻辑帧不会再收到 `OnLogic`（无论休眠开关）。

### 摩擦度（Friction）与粘性（Stickiness）：固体的安息角

两个参数都是固体的材料属性，**只被 `ParticleRules.StepSolid` 读取**（液/气不读，填 0 即可），
都决定固体正下被挡住时肯不肯沿坡斜滑。区别在于**生效条件**：

| 参数 | 生效条件 | 直觉 |
|---|---|---|
| `Friction` | 总是（材料自身的静摩擦） | 孤立一颗也滑不动 |
| `Stickiness` | **斜滑方向的反侧同一行有固体邻居**（下面有固体不算） | 旁边有粒子才粘得住；孤立粒子按纯摩擦度滑落 |

安息角斜率（rise/run）由两者相加：`1 / (1 - Friction) + (粘住时) 1 / (1 - Stickiness) - 1`，
实现上四舍五入到整数格净空（`ParticleRules.SlideClearance`）。`Friction = 0` 时单看粘性：

| Stickiness | 粘住时的净空 | 安息角 |
|---|---|---|
| `0` | 1 格 | 1:1（约 45°，像沙） |
| `0.5` | 2 格 | 2:1（约 63°，沙 + 0.5 ≈ 土） |
| `0.9` | 10 格 | 10:1（约 84°，像石头） |
| `1` | 永不斜滑 | 只要旁边有固体就绝不滚落（形成陡壁 / 悬垂） |

- `Friction >= 1`：下方有固体就完全静止（与粘性无关）。
- 粘性只在**粘住**时计入：反侧同排没有固体时，`Stickiness = 1` 的粒子也照样按摩擦度滑落。
- 因此固体的 `OnMotion` 就是一行 `ParticleRules.StepSolid(world, x, y);`——两个参数都从注册表读，类型不必自己抄常量。

当前生产类型的取值（安息角斜率 = 实测堆体斜率）：

| 类型 | `Friction` | `Stickiness` | 安息角 |
|---|---|---|---|
| 沙 | `0` | `0` | 1:1（约 45°），干散：旁边有沙也照样滚 |
| 土 | `0.5` | `0.5` | 约 3:1（摩擦给 2:1，粘性再抬一档；孤立土粒仍按 2:1 滑落） |
| 湿土 | `0.5` | `0.5` | 与干土同档 ⇒ 湿土堆与干土堆形状一致，只在颜色与热学上不同 |
| 石 | `0.9` | `0.9` | 摩擦给 10:1，粘性再抬 9 档到 **19:1（约 87°）**——靠着石头的外侧几乎不滚，垛成近乎垂直的石垛 |

要试别的粘性，改 `Info` 的 `stickiness:` 即可（粘性档位是量化的，步长约 `0.1` 才看得出堆体变化；
`0.9` 与 `1` 的差别只在「粘住时还剩几格净空」与「完全不滑」之间，观感都接近垂直）。

### 标签（Tag）：跨类型规则的唯一判据

标签是「谁能让开谁」的唯一依据，**规则里永远不出现具体 typeId 的判断**——新增一种固体不需要改任何已有类型。

| 标签 | rank | 含义 |
|---|---|---|
| `Solid` | 3 | 固体：互相堆叠、有安息角，可沉穿液体/气体 |
| `Liquid` | 2 | 液体：可自由横流，可沉穿气体 |
| `Gas` | 1 | 气体：可被固/液置换，通常自行上升 |
| （空气） | 0 | `typeId == 0`，不属于任何类型 |
| `Organic` | — | **属性标签，不参与 rank**：生物来源的物质（草、草种子、花、树干、树叶）。携带它的格被覆盖 / 抹除时会按概率留下腐殖质，见 §4「有机物的衰变」。必须与至少一个类别标签（Solid / Liquid / Gas）并用。**显式霉烂路径**（草种子 / 水生态种子 / 树木种子的 `SetPhase → Humus`）的产物本身已是腐殖质，世界不再叠加那道 20% 衰变骰。**水生植物 / 水生态种子故意不带它**：植物的物质归水（死亡还水），种子则是为避开「发芽 SetPhase 被 20% 腐殖质截胡」（见 §11）。 |

一个字节允许同时带多个标签（`[Flags]`）；`TagRank` 只取其中的**类别**（Solid / Liquid / Gas），`Organic` 这类属性标签被忽略。
`TagRank` 的实现按 Solid → Liquid → Gas 依次判位，因此属性标签不会影响置换次序。

**置换规则**：`rank(自己) > rank(目标)` 才能换过去。于是——

- 固体只会被**同为固体**的粒子挡住：`TryFall` 撞到 Solid 就返回 false。
- 同类之间 `rank` 相等，互不置换 ⇒ 沙能堆、石头能垒。
- 固体落进液体/气体时 rank 更高 ⇒ 下沉穿过。

**地下水闸门**：固体置换**液体**还要求该固体处于**运动中**（上一运动扫描位移过，或运行中刚由 `Set` / `SetPhase` 放置——构造期 tick 0 的初始条件一律视作静止；见 `ParticleWorld.HasMomentum`）。静止的固体无论形状、是否与池塘联通都不能把水挤上去，石头 / 沙里封闭的水因此成为不动的「地下水」（土例外：干土会按既有土壤反应把正上方的水吸成湿土）；下落的沙石照旧沉底（下落本身即运动）。气体目标不受此闸门约束。开关 `ParticleWorld.GroundwaterSealingEnabled` 默认开，关掉退回旧 rank 行为。

`ParticleRules` 提供 `TagRank(ParticleTags)`、`TagsAt(world, x, y)`（一次取回整组标签）、`HasTag(world, x, y, tags)`（任意命中一个标签即为 true；越界/空气/空槽返回 `None`）与 `IsSolid(world, x, y)` 等原语，类型代码直接调用；`CanDisplace` / `CanFlowThrough` 已内置上述规则，并额外拒绝静态格（`IsStatic`）目标。`CanDisplace` 对**越界目标一律返回 false**（否则越界会被当成「空气可进入」，调用方以为能移动、随后的 `Swap` 却静默失败）；同时仍会拒绝静态格目标；`CanDisplace` 另内置上述**地下水闸门**（固体 → 液体要求运动中）。

**吸水与湿土的水迁移（土 ⇄ 湿土）**：这是一对**具体类型之间的反应**，因此按「A 生成 B 走 `ParticleIds`」的约定直接引用 `ParticleIds.Soil` / `ParticleIds.WetSoil`（不是通用标签规则）。模型是 **湿土 = 干土 + 一份水**：水总量 = 水格 + 湿土格 + 湿度场，全程守恒。

- **干土吸水（`SoilParticle.OnLogic`）**：正上方一格带 `Liquid` 标签 ⇒ 本格 `SetPhase` 变湿土，并把那一格水 `Clear` 掉。水的热量**搬进**本格而不是复制：先读出水格 Q、`AddHeat` 并进本格，再把水格 Q **显式清零**后再 `Clear`（`Set`/`Clear` 的契约是「新物质继承该格原有热量」，不清零会让同一份热同时留在湿土与水格两处、总热凭空增加）。一格水润湿一格土，水随即消失。（已知简化 B8：判据用 `Liquid` 标签，**当前所有液体一律视为水**；将来加入岩浆 / 酸等其它液体时必须改成 `id == ParticleIds.Water`，否则任何液体都会被灌成湿土。）
- **湿土把这份水交给邻格（`WetSoilParticle.OnLogic`）**，成功即本格变干（水要么进了邻格的干土，要么进了空气，湿土不会凭空变干）：
  - **受热上行**（`T ≥ DryingTemperature`，代码 60）：水优先往**上**走，分两种落点——
    - 上方是**干土**：有 `1/PercolationDenominator`（= **50%**）的概率把那格润湿成湿土、本格变干（与下渗同分母的概率门，用来打破「热源消失后水在相邻两格来回走」的时间对称）。
    - 上方是**空气**且空气没吸饱：直接像水粒子慢蒸发一样把一份水汽摊进空气（`HumidityField.SpreadUnitIntoAir`，半径 4，份额门控 `InvisibleSpreadMaxShare` → **不可见**），本格变干并**从土壤吸走一份相变潜热**（本格 `AddHeat −LatentHeat(T)`，随温度线性）；接收格不足（份额过大）时不变干、水保留。
  - **常温迁移**（`T < DryingTemperature`）：**有压才渗**——只有正上方是水（Liquid 标签）或湿土（被上方的水柱 / 湿层压着，即有水头压力）时，才以每**逻辑帧** `1/PercolationDenominator`（= **50%**）的概率把水交给邻格的干土——**正下方优先**，正下方被石头/沙/水/湿土挡住时按随机顺序改试**同行左右**的干土邻居（侧向渗流）。**先判压、再判目标、再摇骰**：无压或无目标都不消耗随机数，RNG 消耗不随场上湿土格数漂移。
    **孤立湿土（上方是空气）永久持水**——田间持水量：毛管力拽住水，重力渗不动它。湿润因此不会自己沉底，雨停即持；它退出循环的方式是被太阳晒干（受热上行路径）。连续降雨时链条效果是湿润锋面整体下移、身后的格子保持湿润，而不是湿斑穿柱而过攒到柱底。
  - 三条都走不通（无压、被石头/沙/水围着，或下方已是湿土且左右无干土）⇒ 水留在土里，本格保持湿土。
- **只有干土接水**：石头、沙、水都没有"湿"的形态，接不住；湿土本身也不再吸收。
- **饱和会挡住继续迁移**：迁移只看相邻一格（正下方优先、被挡含同行左右），目标已是湿土就渗不动——完全被湿土/固体围死才卡住（真实土壤的"饱和层"被侧向流部分缓解）。
- 换格走 `SetPhase`，且**水带着它的热量一起走**：搬运时把 `ΔQ = T(源) × 1.2` 从源格挪到目标格（`1.2 = 湿土热容 2.0 − 干土热容 0.8`，即那一份水的热容份额），于是热容随物质一起变、两侧温度不因换格而跳，总热量严格守恒。旧「Q 留在原格不搬」已作废。
- 送水只涉及**相邻一格**（下渗被挡时含同行左右邻）：不隔空传导、不逐格扩散。

> **反应写在哪一帧**：吸水 / 送水都是**反应与邻域写入**，因此都在逻辑钩子 `OnLogic`（20 Hz）里；`OnMotion`（60 Hz）只做 `StepSolid` 的实体运动。两者互不干扰——水在土里迁移的同时，土照样按安息角堆叠、下落。

**草种子生长（草种子 → 草）**：同样是 A 生成 B 的**具体类型反应**，走 `ParticleIds.GrassSeed` / `ParticleIds.Grass`（判据读 `ParticleIds.WetSoil` / `ParticleIds.WetHumusSoil` / `ParticleIds.HumusSoil`）。草种子是受重力的固体粉末（摩擦 / 粘性 0.5，落到地面或湿土上即静止），落地后由**随机刻**（`OnRandomTick`）驱动：命中时先查**八邻**（3×3 去自身，含斜向与正下方）有没有**湿土或腐殖土**（腐殖土保水、视作自带墒情）：**没有发芽条件**时累计一次「空转」命中（计数存在本格 `flags` 的 bit5-6，`CellFlags.SeedStrikeMask`，随格移动），连续 `GrassSeedParticle.MaxStrikesBeforeHumus`（=3）次都无发芽条件就**霉烂**——`SetPhase` 直接变成**腐殖质**（`ParticleIds.Humus`）；未满 3 次只把计数写回、原地等待，**不消耗随机数**（随机流不随场上种子数漂移）。有发芽条件则以 `NextInt` 摇出 1..`MaxGrowthHeight`（=3）的高度，把自己 `SetPhase` 成草（同一份物质换形态，Q 保留），再把上方 1..高度格逐格 `Set` 成草——遇到固 / 液（`ParticleRules.BlocksLight` 判据）或顶到网格上边界立即停下，**草只在空气 / 气体里抽出、不覆盖已有物质**。**湿土**只被读取、不被消耗；**腐殖土**则在它是**决定性**发芽条件（八邻没有真湿土 `WetSoil` / `WetHumusSoil`）时会被消耗——同一随机刻里调 `HumusSoilParticle.ConsumeFarthestBelow(world, x, y)`：从种子脚下 `(x, y−1)` 起竖直向下扫过连续土层，把其中**最深（离种子最远）**的一格腐殖土家族 `SetPhase` 回普通土（`HumusSoil→Soil`、`WetHumusSoil→WetSoil`，保水保热）；八邻已有真湿土时不消耗。发芽成功后还有 **30%** 概率**开花**：再摇一次骰（`NextUInt() % GrassSeedParticle.FlowerChanceDenominator < GrassSeedParticle.FlowerChanceNumerator`，即 3/10），命中就把**株顶那格**草 `SetPhase` 成**花**（`ParticleIds.Flower`／`FlowerParticle`）——株顶 = 最后一次成功写入草的那格（上方全被固 / 液挡住时就是种子自己那格）。

草（`GrassParticle`）是固体，但**不是 `IsStatic`**——静态类型收不到随机刻（`RunRandomTicks` 会跳过它），而草要靠随机刻撒种、把附近的沙变成土；它靠**空的 `OnMotion`**（而不是 `IsStatic`）保持长在原地不落、不流。带 `Solid` 标签，因此光照打到草顶即停。

**花（`FlowerParticle`）**：草种子发芽时以 **30%**（3/10）概率把**株顶那格**草换成花（见上），因此花就是「一株草最上面那一格换了颜色」。它**继承草的全部性质**：`Solid | Organic` 标签（挡光、死亡按世界 20% 有机衰变留腐殖质）、非 `IsStatic` + **空的 `OnMotion`**（长在原地不落、不流）、热容 0.8 / 导热率 0.6 / 辐射率 1.0 / 光照吸收率 0.8 与草同值（正午稳态温度仍 &lt; 50° 致死线），随机刻三条死法也与草**同判据同顺序**：① 正上方是固 / 液且**不是同一株**（草 / 花）→ 沿 −y 清掉整段同株；② 正下方不是土 / 湿土 / 腐殖土、也不是同一株 → 沿 +y 清掉整段同株；③ 本格温度 &gt; 50 或 &lt; 3（脚下是腐殖土家族时放宽为 &gt; 55 或 &lt; 0）→ 只死这一格；**若正被腐殖土窗口救活**（普通窗口必死、腐殖土窗口仍存活）则同时消耗脚下土层最深一格腐殖土家族（同草，见下）。差别只有两处：

- **颜色**：花的 `Cell.shade` 不是年龄档而是**花色**——世界在 `Set` / `SetPhase` 时按 12 色调色板（绯红 / 粉红 / 浅粉 / 橙 / 杏黄 / 明黄 / 紫 / 品红 / 玫红 / 蓝 / 天蓝 / 白，每色最亮通道 ≥ 200）**均匀随机**抽一个下标，`OnSpawn` **刻意不覆写**（草在这里把 `shade` 钉成 0），因此每朵花的花色随机、终生不变、更鲜艳多样。渲染只读 `shade` 查表，零渲染改动。
- **不衰老、不繁殖**：花的 `OnLogic` 是**严格空体**（`hasLogic: false`，逻辑帧扫描整段跳过）——没有年龄存储、没有 `LifespanTicks`，生死只由随机刻三条死法决定；也不做草的撒种 / 固沙（那是草株的行为）。**花不占用任何 flags 私有位**。
- **「同一株」判据**：草与花互不算压（否则花会当帧压死自己脚下那格草、整株塌掉）、互为扎根、级联死亡时一起清（`GrassParticle` / `FlowerParticle` 各自的 `IsStalkMember`）；草撒种也不会落到草尖 / 花头上（`IsSeedSupport` 排除同一株）。**火把花当燃料**（与草同档：邻接花时不摇熄灭骰、1/20 点燃），而不是当淬熄物。

**草的随机刻（撒种 + 固沙）**：草被随机刻命中时按固定顺序做两件事：

1. **撒种（10% 概率）**：先摇一次骰（`NextUInt() % GrassParticle.SeedSpawnChanceDenominator`，分母 10 ⇒ 10%），命中才在半径 `SeedSpawnRadius`（=2）格的**圆盘**内找**第一个「下方 1 格是可托住种子的固体的空地」**（固定扫描序：y 升序、同一行 x 升序，只取 `dx² + dy² ≤ 半径²`），在那里 `Set` 出一颗新草种子。「空地」= 空气或气体（`ParticleRules.IsGasOrEmpty`——世界被大气填满，只认 typeId 0 会永远找不到落点）；「可托住」= `ParticleRules.IsSolid` 且**不是草**（`GrassParticle.IsSeedSupport`）——石头 / 沙 / 土 / 湿土 / 腐殖土都算，**草不算**，因此草**不会把种子撒到别的草头上**（否则种子会悬在草尖上）。摇骰**每次都发生**（消耗 1 个随机数，保证确定性）；找不到落点就什么都不做。
2. **固沙（无概率门）**：在半径 `SandConversionRadius`（=2）格的**圆盘**内找**第一个沙**，把它 `SetPhase` 成土（同一份物质换形态，Q 留在该格）。沙 → 土是 A 生成 B 的具体类型反应，走 `ParticleIds.Sand` / `ParticleIds.Soil`。

两步都不做即这一帧无事发生。撒种与固沙互不干扰（撒种写空地、固沙写沙格），同一帧都可能发生。这是又一条对草的正反馈：草撒下种子、把沙变成土，土吸水成湿土，湿土又让更多草种子发芽。

> **草的三条死法与撒种 / 固沙都写在随机刻（`GrassParticle.OnRandomTick`）里，按 ①→⑤ 顺序执行**（死亡一旦触发就清掉整段草并返回）。草的 `OnMotion` 为**空**（草不移动）；`OnLogic` 只做**衰老**（见下）。随机刻本就默认关闭，生产值由 `ParticleSimulationSettings._randomTicksPerStep` 注入。

**草的寿命（`GrassParticle.OnLogic`，20 Hz）**：草每**逻辑帧**衰老一格，年龄到 `GrassParticle.LifespanTicks`（**1200** 逻辑帧 ≈ 60 秒）即**寿终**——走 `Clear`，因此同样按有机衰变规则（20%）就地留下腐殖质。衰老的存储刻意复用既有 per-cell 字段，`Cell` 仍 4 字节、不加字段：

- **年龄 = `Cell.shade` × `TicksPerShadeStep`(5) + `flags` bit3-5 的余数**。`shade` 存**高档**、余数存**低档**（`CellFlags.GrassAgeRemainderMask` / `GrassAgeRemainderShift`，3 位 ⇒ 余数 0..4）——两段拼起来才够数到 1200（单靠 `shade` 只有 255 档）。写入用清掩码（`GrassAgeRemainderClearMask`）+ `|` 组合，绝不整体赋值 `flags`（保留 bit0）。
- **`shade` 同时就是颜色档**：调色板长度 = `ShadeCount` = `LifespanTicks / TicksPerShadeStep`（= 240 档），由 `Color32.Lerp(YoungColor, OldColor, i / (ShadeCount−1))` 生成的**渐变**——下标 0 = 最深绿 `(58,124,50)`、末档 = 黄绿 `(170,178,66)`（红 / 绿升高、蓝压低）。渲染只读 `shade` 查表，因此「越老越接近黄绿色」直接在画面上成立，无需改渲染管线。
- **`OnSpawn` 把 `shade` 钉成 0**（最年轻档）：`ParticleWorld.Set` / `SetPhase` 的随机抽样被覆盖——与火「shade = 温度分档」同一模式，草的 `shade` 是**派生量**而非随机量。年龄余数位由写入原语清零（`flags = 当前标记`）。
- 衰老**不消耗世界随机流**、不读邻居、不移动、不写 Q，因此不影响确定性契约与运动休眠（`OnLogic` 本就永不休眠）。

> 注意与随机刻三条死法的分工：**压死 / 失去扎根 / 温度致死**仍只在随机刻（默认关闭）里判，而**寿终**是每逻辑帧的确定性过程——草地即使随机刻全关，也会在 1200 逻辑帧后自然凋亡、部分化为腐殖质。

**草被压死（`GrassParticle.OnRandomTick` ①）**：草的**正上方是「固体或液体、且不是草」**时，草被压死——沙 / 土 / 石 / 湿土 / 腐殖土 / 腐殖质、**草种子**（固体）与**水**（液体）都算压；**同株草摞在草上不算压**（把「草」本身排除），空气与气体也不算。死亡**沿竖直方向向下传播**：从被压的这一格起，沿 −y 把下面**整段连续的草**逐格 `Clear`（同一株被压垮）。判据用标签 `Solid | Liquid`（只把草这一具体类型排除）。清掉的每一格都走 `Clear`，于是各自按有机衰变规则（20%）就地留下腐殖质——**草被埋 → 部分化为腐殖质 → 腐殖质改良附近土壤**，连成一条闭环。压力的落点很自然：草与沙 / 土同为 `Solid`、互不置换，所以落下的沙 / 土会**停在草顶**，下一逻辑帧就把它压死；水（Liquid）会与草换位、不与草同格，落在草的相邻格时也按上方法判压。

**草失去扎根而死（`GrassParticle.OnRandomTick` ②，压死判定之后）**：草的**正下方不是土 / 湿土 / 腐殖土**（也不是同株草的下段）时，这一格草失去支撑而死，且**死亡沿竖直方向向上传播**——从这一格起沿 +y 把上面**整段连续的草**逐格 `Clear`（根在下面，土一旦没了整株一起死）。只有整株最底下那格需要土壤；上面的草段靠下段支撑，因此「下段」在判据里也算扎根。判据用具体 `ParticleIds`（`Soil` / `WetSoil` / `HumusSoil` / `Grass`——这是「草扎根于土」这一具体反应，不是通用标签规则）。被水冲刷走的土（见下）会让草失去扎根而死。

**草的温度致死（`GrassParticle.OnRandomTick` ③）**：同一次随机刻里（压死、失去扎根判定之后）还检查本格温度 `world.Heat.GetTemperature`——**高于 `GrassParticle.LethalHeatTemperature`（50）或低于 `LethalColdTemperature`（3）**时这一格草死亡（`Clear`）——但**脚下那格（`world.GetCell(x, y - 1).typeId`）是腐殖土家族（`HumusSoil` / `WetHumusSoil`）时，致死窗口放宽为高于 `GrassParticle.HumusLethalHeatTemperature`（55）或低于 `HumusLethalColdTemperature`（0）**（脚下是同株下段时仍按 50 / 3）。**普通窗口必死（`T > 50` 或 `T < 3`）、腐殖土窗口却存活**（即分别 `T ≤ 55` / `T ≥ 0`）时，这一格草**存活并消耗**脚下土层最深一格腐殖土家族——调 `HumusSoilParticle.ConsumeFarthestBelow(world, x, y)`：从脚下 `(x, y−1)` 起竖直向下扫过连续土层，把**最深（离植株最远）**一格的 `HumusSoil→Soil`、`WetHumusSoil→WetSoil`（`SetPhase` 保水保热）；普通窗口内存活（`3 ≤ T ≤ 50`）不消耗。只判这一格：相邻草格各有自己的温度、各有自己的 Q，会随热传导各自判定，级联是随时间涌现的，不需要显式传播。三种死法（压死 / 失去扎根 / 温度）都走 `Clear`，因此都按有机衰变规则（20%）留下腐殖质。阈值是拍出来的（`T = Q / 热容`，草热容 0.8 ⇒ 高/低温阈值对应 Q = 40 / 2.4），要调直接改这几个常量。**注意热平衡标定**：持续受热的暴露面稳态约 `T ≈ 输入功率 / (RadiativeLoss · ε)`（见 §6 散热 B），若光照强度 / 散热系数使草地稳态落在 50 以上或 3 以下，草会持续被晒死 / 冻死——阈值要跟着调。

**水的冲刷（水 → 沙，随机刻）**：水被**随机刻**命中时，以自身为中心、半径 `WaterParticle.SandifyingRadius`（=2）格的**圆盘**内找**第一个土壤 / 湿土 / 腐殖土 / 湿润腐殖土**（固定扫描序：y 升序、同一行 x 升序，只取 `dx² + dy² ≤ 半径²` 的格），找到目标后再摇一次骰（`NextUInt() % WaterParticle.ErosionChanceDenominator`，分母 5 ⇒ **20%**），掷中才把它 `SetPhase` 成沙（同一份物质换形态，Q 留在该格）——**先判目标再摇骰**，无土可冲时不消耗随机流。这是「水把土冲成沙」的慢过程，走随机刻而不是每逻辑帧（每帧冲刷会瞬间抹平地貌）。水本身不动这一格——它只是把附近的土改判成沙。

**水生植物与种子（`AquaticPlantParticle` / `AquaticSeedParticle`，沉水、扎根水底）**：一对具体类型反应，走 `ParticleIds.AquaticSeed` / `ParticleIds.AquaticPlant`。**两者都不带 `Organic`**：植物的物质归水（死亡还水而不是留腐殖质）；种子不带 Organic 是为了避开一个陷阱——发芽走 `SetPhase(种子 → 植物)`，若种子带 Organic 而植物不带，世界的 20% 腐殖质衰变会把 20% 的发芽截胡成腐殖质。种子的腐殖质只来自「连续 3 次无发芽条件」的**显式** `SetPhase → Humus`。

**核心不变量：每格植物 ⇔ 一格水**（严格 1:1 对称，因此不需要任何持水标记 / 计数器）：

```
发芽：种子 → 植物，同时吸收头顶那格水（SetPhase 成大气）
生长：植物 → 长高一格，同时把头顶那格水 SetPhase 成植物
死亡：植物 → 还原成一格水（SetPhase 保留本格热量）
```

植物带 `Solid`、`gravity=false`、`isStatic=false`（靠**空的 `OnMotion`** 定植——静态类型收不到随机刻），`HasLogic` 取默认 **true**；**逻辑帧只做衰老**（`OnLogic`，见下），其余全部行为写在**随机刻**里，按固定顺序：

- ① **被压死**：正上方是**固体且非同类**时被压死——**沿竖直向下**把下面整段连续植物**还成水**。**液体（水）上方是「沉水」常态、不算压**（与草相反：草把液体也算压）。
- ② **失去扎根**：正下方不是**软底** `{Sand, Soil, WetSoil, HumusSoil}`（也不是同株下段）→ **沿竖直向上**把上面整段**还成水**。
- ③ **脱水致死**：本格**八邻内没有任何液体**（冒出水面 / 水被排空）→ 只把这一格**还成水**；死亡从顶端一格格涌现，水下部分（八邻仍有液体）存活。**已知代价**：1 格宽的水道里左右是井壁而非水，会被误判成脱水、整株死。
- ④ **温度致死**：`T > LethalHeatTemperature`（50）或 `T < LethalColdTemperature`（**0**，非草的 3）→ 只把这一格**还成水**。取 0 是因为模拟里环境热量为 0、水下无光——未被加热的水温就是 `T = 0`，用 3 会让水草一挨到未受热的水就冻死。
- ⑤ **生长**：头顶是水 **且头顶上方还有水**（始终给植株留一格保命水，防止吸掉后自我脱水）**且**株高 `ColumnHeightBelow < MaxPlantHeight`（=3）→ 把头顶那格水 `SetPhase` 成植物（消耗一格水、长高一格）。只有最顶上的茎格满足「头顶是水」，所以只有顶格会生长。株高上限沿用旧版一次性抽茎的 1~3 格观感。
- ⑥ **撒种（10%）**：骰子每次都摇（消耗 1 个随机数，保证确定性）；命中才在半径 `SeedSpawnRadius`（=2）的**圆盘**内找第一个「**软底** `{Sand, Soil, WetSoil, HumusSoil}` + **正上方是水** + **水柱顶端之上是界内气体/空格**」的列：把顶端之上那格 `Set` 成水（**水位上升一格**，被顶开的水有处可去），再把基质正上方那格水 `SetPhase` 成**水生态种子**。**基质格不被消耗**（修掉了旧版「每撒一次种海底少一格」的侵蚀），水只是被顶上去，严格守恒。

**衰老（`OnLogic`，每逻辑帧，与草同一模式）**：年龄 = `Cell.shade` × `TicksPerShadeStep`（=5）+ `flags` bit3-5 的余数（`CellFlags.GrassAgeRemainderMask`，**与草共用**同一组位——一格同一时刻只属于一个类型，`Set` / `SetPhase` 换类型时整字节覆写 `flags`）。每逻辑帧年龄 +1：未满一档只推进余数，攒满一档把 `shade` +1（`Palette` 由深绿 `(30,84,58)` 渐变到黄绿 `(140,152,84)` ⇒ **越老越黄**），`shade` 到 `ShadeCount − 1` 即年龄到 `LifespanTicks`（**1200 ≈ 60 秒 @20 Hz**）。**寿终走 `SetPhase` 还成水**（每格植物 ⇔ 一格水；植物不带 `Organic`，所以**不**是 `Clear`、不留腐殖质，热 Q 留在该格）。`OnSpawn` 把 `shade` 钉成 0（最年轻档）——`Set` 的随机抽样被覆盖，**生长新长出的每一格也各有自己的年龄**。衰老**不消耗世界随机流**、不读邻居、不移动、不写 Q，故不影响确定性契约（与随机刻的三/四条死法分工：压死 / 失去扎根 / 脱水 / 温度致死仍只在随机刻里判，寿终是每逻辑帧的确定性过程）。

**被覆盖 / 被抹除（`OnKill`）**：`killerTypeId == Water` 是我们自己「死亡还水」的 `SetPhase` 路径触发的回调（水不可能合法覆盖固体植物，rank 2 < 3），直接返回、不重复结算；其余情况（被固体埋、`Clear`）本格随后会被写成新类型，水不能写回本格——把这份水注入**八邻第一个气体格的湿度场**（水汽仍计入水账，水不丢）；八邻全是固/液（极罕见）时留在本格湿度里，由世界的 `EvacuateHumidity` 兜底（守恒优先于不变量）。

**水生态种子**是受重力的固体粉末（摩擦 / 粘性 0.5，走 `ParticleRules.StepSolid`），沉到水底基质上后由**随机刻**驱动：命中时先判「**正上方是水**（被淹没）**且正下方是软底**」——满足才**吸收头顶那格水**（`SetPhase` 成大气；上方的水下一个运动帧就会落下来补位）并自身发芽成 **1 格**植物；长高完全由植物自己的随机刻逐格驱动，不再是旧版的一次性位移式抽茎。否则累计一次「空转」命中（计数存本格 `flags` 的 bit5-6，**与草种子共用同一组位**），连续 `MaxStrikesBeforeHumus`（=3）次都无发芽条件就 `SetPhase` 成腐殖质，且**不消耗随机数**。副作用：发芽会在水里留一个大气泡（随后被上方落下的水填掉）。

**与草的对比**：草的「被压死」把**液体也算压**、扎根集合是 `{Soil, WetSoil, HumusSoil}`、死亡走 `Clear` + 世界 20% 腐殖质衰变；水生把液体从压力中排除、扎根集合**含沙**（软底）、死亡**还水**（不带 Organic，类型代码自己结算守恒）。植物**不可在编辑器选择**（只能由模拟产生），水生态种子**可单颗投放**（`ParticleEditor._singlePlacementTypes`，500ms 冷却）。

**有机物的衰变（有机物 → 腐殖质 → 腐殖土）**：这是一条**标签驱动的通用过程**，实现在 `ParticleWorld` 而非某个类型的钩子里——因为「死掉的有机体变成腐殖质」要写**自己脚下那一格**，而 `OnKill` 在写入新格**之前**调用、它对自身格的写入会被随后写入的新格覆盖（§6「写操作的钩子与副作用」）。因此在 `Set` / `SetPhase` **写完新格之后**，世界再检查：受害者带 `ParticleTags.Organic`、且新类型不带该标签时，按 `1 / ParticleWorld.OrganicDecayDenominator`（分母 5 ⇒ **20%**）的概率把这格 `SetPhase` 成**腐殖质**（`ParticleIds.Humus`）。判据只看标签、不看 typeId；产物是固定的腐殖质（与 `StepRain` 直接引用 `ParticleIds.Water` 同理，是世界的具体过程）。`Clear` 走 `Set(..., Air)` 同样覆盖。**每次有机物死亡、且新格不是腐殖质时摇一次骰**（消耗一个随机数，确定性不变）；有机物之间互变（草种子 → 草）不算死亡、不摇骰。**例外：显式霉烂不再叠加这道骰**——草种子 / 水生态种子 / 树木种子的霉烂本身就是 `SetPhase → Humus`（新格已是腐殖质），`MaybeLeaveHumus` 开头直接早退、不摇骰；否则即便摇中也会被「同类型 `SetPhase` 不做事」的 early-return 吞掉，只会白耗一个随机数。该豁免让这些霉烂路径之后的世界随机流比旧行为前移一格，确定性不受影响（同种子同命令序列仍逐位可复现）。

- **腐殖质（`HumusParticle`）**：固体，短暂中间态。每**逻辑帧**（`OnLogic`）在**八邻**内找**第一个土壤**（`ParticleIds.Soil`，固定扫描序：y 升序、同一行 x 升序），找到就把那格 `SetPhase` 成腐殖土，然后自己消失（`Clear` → 空气）——腐殖质被土壤吸收，只在世上停留一帧；八邻没有土壤时保持不动、下一逻辑帧再试（悬空时按固体下落）。运动是 `StepSolid`（摩擦 / 粘性 0.5）。
- **腐殖土（`HumusSoilParticle`）**：土壤吸了腐殖质后的**黑色**形态，与干土同款粉末运动（摩擦 / 粘性 0.5）。`OnLogic` 仍是严格空体（`hasLogic: false`）——它靠**随机刻**扩张：被随机刻命中、且**正上方一格是腐殖质**时，以自身为中心在半径 `SpreadSearchRadius`（=2）内**从近到远**（欧氏距离；同距取固定扫描序 y 升序、同行 x 升序）找**第一个普通 / 湿润土壤**——普通土壤 `SetPhase` 成腐殖土、湿润土壤 `SetPhase` 成**湿润腐殖土**（水跟着物质走）——并把上方那格腐殖质 `Clear` 吸收掉。附近没有土壤则什么都不做、腐殖质留在原地等下一次随机刻。**无概率门、不摇骰**（唯一的随机流消耗是换形态时世界对 `shade` 的抽样，与任何 `SetPhase` 相同）。作为**腐殖土家族**（腐殖土 / 湿润腐殖土）的一员，它还叠加一层**植物优待**：草种子八邻有它即视同湿土发芽、喜湿树木（杨 / 橡 / 柳）认它为合格基质，且草 / 花脚下（正下方一格）是它时温度致死窗口放宽为 `> 55° / < 0°`——但优待是**消耗型**的：植物真正靠它存活 / 发芽（腐殖土是**决定性**条件）时，会把脚下连续土层里**最深（离植株最远）**的一格腐殖土家族退化成普通土（`HumusSoil→Soil`、`WetHumusSoil→WetSoil`，`SetPhase` 保水保热），腐殖土因此被缓慢耗回普通土——**植物受益即耗肥**，优待与消耗构成闭环。
- **湿润腐殖土（`WetHumusSoilParticle`）**：腐殖土 + 一份水的黑色形态，**逻辑与 `WetSoilParticle` 逐条同构**（作者契约「和普通的土壤没有区别」），只是把干 / 湿两端从 `Soil` / `WetSoil` 换成 `HumusSoil` / `WetHumusSoil`：受热上行（`T ≥ DryingTemperature` = 60，上方是干腐殖土则润湿、上方是空气则摊一份水汽进湿度场并吸走潜热 `−1.2T`）、常温**有压才渗**（上方是液体或湿润腐殖土才 50% 概率把水交给邻格干腐殖土，正下方优先、被挡随机序试左右）、孤立永久持水（田间持水量）。水迁移时带着 `T(源) × 1.2` 的热量一起走（`1.2 = WetHumusSoil 热容 2.2 − HumusSoil 热容 1.0`），两侧温度不跳。热学：热容 2.2 / 导热率 0.75 / 密度 2.1（都比干腐殖土高）。与腐殖土同属**腐殖土家族**，因此同样叠加那层**植物优待**——草种子八邻有它即视同湿土发芽、喜湿树木（杨 / 橡 / 柳）认它为合格基质、草 / 花脚下是它时温度致死窗口放宽为 `> 55° / < 0°`；这层优待同样是**消耗型**的（植物受益即耗肥，消耗规则与腐殖土一致，见上）。

三种粒子都**不可在编辑器中画出**（只能由模拟产生）——`ParticleEditor._unplaceableTypes` 默认已列入 `HumusParticle` / `HumusSoilParticle` / `WetHumusSoilParticle`。

**火（`FireParticle`）**：气体（密度 0.6，比大气重——大气推不动它），**第一个粒子级热源**。每逻辑帧按八邻环境在两种角色间**重判**（同一 typeId、同一调色板、无持久状态）：**附着火**（八邻内有固 / 液；**火焰不算附着物**）定在原地燃烧、注热、抛射火舌；**游离火**（八邻无固 / 液，即「火舌」）不注热、靠余热上升并随传导冷却，熄灭骰或熄影线即消失。全部行为在逻辑帧 `OnLogic`（20 Hz，**顺序即契约**）；`OnMotion` 留空（v2 删除 v1 的下沉路径，位移全在逻辑帧内完成）；随机刻留空（随机刻密度随世界大小变稀，节奏会漂移）：

- **shade = 温度分档（派生量）**：本格 `shade` 每逻辑帧由**本格温度**重算 + `(x,y,Tick)` 哈希 ±1 抖动、clamp 到 `[0,5]`——≥300→0（亮黄）、200–300→1、130–200→2、80–130→3、40–80→4、<40→5（暗红）。shade 不再是独立随机游走，而是**温度的派生量**（`Set` 的初始抽样被下一逻辑帧覆盖）：附着火底部恒亮、游离火越升越暗，火焰轮廓的颜色梯度 = 温度场；哈希不消耗世界随机流。
- **八邻扫描（只读，不摇骰，单趟）**：每逻辑帧按固定序（左 → 右 → 下 → 上 → 左下 → 右下 → 左上 → 右上）**一趟**读完八邻，取**第一格草 / 花**（燃料）、**第一格湿土**（干化目标）、**第一格液体**与**第一格非可燃固体**（淬熄触发——任何 `Solid`，草 / 花除外，它们是燃料）、**八邻是否有空气 / 气体**（通风口），并判定附着物（固 / 液的一格）——附着判定已折进这同一趟，不再单独扫第二遍。顺序与数组不变。
- **放热（仅附着火）**：附着火且本格温度低于 `FireParticle.PeakTemperature`（400）时向本格 `AddHeat(FireHeatPerTick = 250)`——热由世界每逻辑帧自动四邻传导（火不做传导）。**游离火不注入**：离焰即离热源，配合温度分档自然得到「顶尖暗红消散」。紧贴的沙 / 石会被烤热（热容小、导热快，几帧就到上百度）、紧邻的草被「烤」到致死线（50）附近、紧邻的水趋近沸点（80）；上限只封整片火的内部（四邻等温、传导≈0），防恒定注入把它推到数千度。
- **燃料判定与蔓延**：有第一格燃料（草 / 花）则以 `1 / IgnitionDenominator`（**1/20**）的概率点燃它（`Set` 成火——新火有**全新寿命**，不继承年龄）。无温度门，接触即点燃。
- **遇到湿土 / 湿润腐殖土：先烤干（带温度门）、再结算熄灭**：八邻里的湿土先被 `SetPhase` 烤成干土、湿润腐殖土烤成干腐殖土（`WetSoil → Soil` / `WetHumusSoil → HumusSoil`）——**本格火温（本帧注入前）≥ `WetSoilParticle.DryingTemperature`（60）才允许**（冷火不烤干，FB3；门读注入前温度，否则附着火恒 ≥250°、门永不生效）；潜热按**湿→干的热容差 1.2** 结算（本格 `AddHeat(−1.2 × T)`，湿土 2.0−0.8 = 湿润腐殖土 2.2−1.0 = 1.2），不是水↔大气的 `LatentHeat(T)`（5.5T），烤干前后总热严格守恒、干土温度恒为正；**水汽走湿度场的不可见播撒**（先确认摊得出去才变干，摊不出去则原样保留，水不凭空消失）。干化在熄灭骰**之前**结算。每帧最多烤干一格（八邻固定序里的第一格湿土）。
- **熄灭（档位自上而下互斥；压顶 / 窒息当帧立灭、不摇骰，其余档每帧恰摇一次）**：① **压顶立灭**：**正下方**是**石头 / 沙 / 液体**（实体顶到火头上）→ **当帧直接熄灭、不摇骰**——石头 / 沙 / 水落在火上时，rank 规则先把火置换到实体正上方（`Swap`），火在下一逻辑帧立即熄灭（位移本身来自 rank 规则、火代码拦不住）。② **窒息立灭（被包围）**：八邻**全是固 / 液**（一格空气 / 气体通风口都没有）且**没有燃料** → **当帧直接熄灭、不摇骰**——被物质埋死、被封进密闭腔的火当帧闷熄（旧实现没有这一档，此类火靠「烧尽骰」拖着烧 6 秒，还会被后续落下的物质不断顶到表面「上浮」）。③ **冷却即灭**：游离火本格温度 < `EmberTemperature`（**40**，与最暗色带同阈值）→ **当帧熄灭、不摇骰**——火舌顶端收束，余热散尽必灭、不留漂浮长尾。④ **游离熄灭骰**：其余游离火 → `1 / FreeExtinguishDenominator`（**1/8**，期望约 0.4 秒 ≈ 上升 8 格）。⑤ **有燃料 → 不摇熄灭骰**：八邻有草 / 花就是在烧燃料——**贴住的燃料必被逐格吃掉**（否则火线随机断代、草地上会留零星残株）。⑥ 八邻贴**液体**（当前所有液体视为水）→ `1 / WaterQuenchDenominator`（**1/2**，期望约 0.1 秒）——水是最强灭火剂；水边草地因此天然成防火线。⑦ 八邻贴**任何非可燃固体**（无液体时，按 `Solid` 标签统一命中——石头 / 沙 / 湿土 / 干土 / 腐殖质 / 腐殖土 / 树干 / 树叶 / 各类种子 / 水生植物；**草 / 花除外**，它们是燃料）→ `1 / QuenchDenominator`（**1/5**，期望约 0.25 秒）——**火不在不可燃物上久留**（旧实现只认石头 / 沙 / 湿土、其余靠 1/120 的烧尽骰赖 6 秒）。**收拢后「附着无燃料 → 1/120 烧尽骰」档不可达、已随本次改动删除**（附着 ⇒ 八邻必有固 / 液 ⇒ 有燃料走 ⑤、无燃料必落 ⑥ / ⑦）。每帧随机消耗：压顶 / 窒息 / 冷却即灭 0 个骰；游离 1 个；贴液体 / 非可燃固体 1 个；**有燃料时 0 个熄灭骰**（只摇 ⑦ 蔓延 / ⑥ 抛射的骰）。**注意（已拍板为涌现特性，2026-10-05）**：**火不保证烧光整片草**——点燃走 `Set`（草 → 火）会触发世界「有机死亡 20% 留腐殖质」规则，即 20% 的点燃是哑弹、直接变腐殖质而非火；腐殖质不是燃料、八邻无土时永久滞留为空洞，v2 的游离火上升后不再像 v1 下沉火那样无意回填空洞，火线会在空洞处断代，被围死的零星草格成为残株（桩实测：19×6 草块烧剩 1 格；v1 同场景能烧光，差异即来自下沉回填机制被上升取代）。
- **火舌抛射（附着火专属，先判后摇）**：附着火且**正上方是气体 / 空、且不是火**、本格温度 ≥ 200° 时，以 `1 / TongueDenominator`（**1/10**，平均每 0.5 秒吐一格）摇一次骰，命中则 `world.Set(x, y+1, Fire)`。新火格通常无附着 → 下一逻辑帧起按「游离火上升」上窜；上方是火则跳过（火舌不叠火舌，避免垂直火柱自我无限加高）。一轮草火因此呈现「燃烧层 + 断续上窜的 2–6 格火舌」，而非静态色块。
- **游离火上升（`OnLogic` 末尾）**：仅游离火执行，是逻辑帧的**最后一个动作**（熄灭骰全部结算、确认本帧存活之后才尝试移动）——将死之火死在原地、不拖着尸体上移。`(x, y, Tick)` 哈希取 2 bit 选向（**50% 正上 / 25% 左上 / 25% 右上**）→ `ParticleRules.CanGasEnter` 通过（火 0.6 ≥ 目标气体密度：能换进大气 / 空气 / 火，被固 / 液拒绝）则 `Swap` 过去并立即 return；首选被挡试另一斜上；再被挡本帧原地不动（**不下降**）。每逻辑帧至多一格 → 上限 20 格/秒；摇曳使火舌轨迹呈随机折线，多格并排即「跳动的火墙」；哈希不消耗世界随机流，`Swap` 后两格都被世界打上本 tick 处理标记。
- **附着物与投放资格（两条不同的判定）**：**物理燃烧**的附着物 = 八邻的固 / 液一格（**火焰不算附着物**——火柱不互相支撑）；失去附着物的火下一帧自动变游离火、上升一二格后熄灭（v2 不再下沉）。**投放**资格（`FireParticle.CanPlaceAt`，`ParticleEditor` 手刷走它）：① 目标必须是空气 / 气体格——**不覆盖、不替换任何实体方块 / 粒子**（固、液都不行；大气是可替换的环境气）；② 八邻内必须有**锚点**（固 / 液的一格，**或另一格火**——投放层允许火链：圆刷按「低行先落」逐格判定，贴地时底圈先锚到地面 / 实体、上面的格子再连锁锚到已落下的火，一次刷出整团火；完全悬空时一格都放不下）——**不许浮空投放**。这是**投放**规则、不是生成规则：点燃草与火的自然蔓延不受约束；圆刷时不合格的格被跳过（部分投放）。
- **FD1（显式行为，是设计不是 bug）**：**火无法停留在石头 / 沙 / 水面上**——这是压顶立灭的必然推论：手刷在石面 / 沙面 / 水面的火，下一逻辑帧即灭；落在**任何不可燃固体**（干土 / 腐殖类 / 木头 / 树叶……）上的火也活不过几帧（1/5 淬熄骰）。**只有燃料（草 / 花）能持续供火**——「篝火」要有燃料：火贴着燃料就定在原处把它逐格吃掉，燃料烧光即灭。
- **烧过的草地按 20% 概率留下腐殖质残迹**：点火走 `Set`（草 → 火），草带 Organic 标签，自然触发上文「有机物的衰变」世界规则——零额外代码（甚至刚点着的火可能当场被腐殖质顶替）。
- **水浇灭火是涌现的**：火不写灭火代码；固体 / 液体按 rank 规则置换火（水横流换进火格、沙沉穿火），火被覆盖即经 `OnKill` 消失；贴液体 / 不可燃固体的加速熄灭写在火自己的熄灭骰里（见上）。火不主动横移，只按「游离火上升」向上升腾。
- 火是**唯一可手刷的 Gas**：`ParticleEditor._placeableGasTypes` 白名单默认放行火（且受上述投放资格约束），方便点燃试验；其余 Gas 仍不可手刷。


```csharp
// Assets/Scripts/Simulation/ParticleType.cs —— 注册表条目（不可变）
public sealed class ParticleType
{
    public readonly ushort Id;
    public readonly ParticleTypeInfo Info;
    public readonly ParticleMotionDelegate OnMotion;   // 运动帧（60 Hz）；提取自单例实例
    public readonly ParticleLogicDelegate  OnLogic;    // 逻辑帧（20 Hz）；提取自单例实例
    public readonly ParticleRandomTickDelegate OnRandomTick;
    public readonly ParticleSpawnDelegate  OnSpawn;
    public readonly ParticleKillDelegate   OnKill;
}

public delegate void ParticleMotionDelegate(ParticleWorld world, int x, int y);
public delegate void ParticleLogicDelegate (ParticleWorld world, int x, int y);
public delegate void ParticleRandomTickDelegate(ParticleWorld world, int x, int y);
public delegate void ParticleSpawnDelegate (ParticleWorld world, int x, int y);
public delegate void ParticleKillDelegate  (ParticleWorld world, int x, int y, ushort killerTypeId);
```

```csharp
// Assets/Scripts/Simulation/ParticleRegistry.cs
public static class ParticleRegistry
{
    public const ushort AirTypeId = 0;
    public static int Count { get; }
    public static ParticleType[] Types { get; }    // Types[typeId] 直取，无字典
    public static ushort Register(IParticleType behaviour);             // 单个：校验失败不留任何状态
    public static ushort[] RegisterBatch(params IParticleType[] items); // 批量：先全部校验再统一写入，原子
    public static void ValidateTypeInfo(ParticleTypeInfo info, string description);   // 自定义类型表复用同一套校验
}
```

要点：

- **`typeId` = 注册顺序**，从 1 开始，0 保留给空气。`ParticleRegistration.RegisterAll()` 是**全工程唯一的类型清单**。
- 用 `ParticleType[]`（引用类型数组）而不是结构体数组：`Types[id].Info.Density` 只做指针寻址，不会每帧拷贝几十字节的结构体。
- 注册时校验（`ValidateTypeInfo`）：调色板非空且 ≤256、`Density` 有限、**`HeatCapacity` 为有限正数**、**`ThermalConductivity` 与 `ThermalEmissivity` 都在 `[0, 1]` 内**、**`Friction` 与 `Stickiness` 都在 `[0, 1]` 内**、**至少带一个类别标签（`Tags != None`）**、类型数未溢出。违反 = **抛 `InvalidOperationException`**，且校验发生在写入任何状态之前。
- `ParticleRegistration` 用 `RegisterBatch` 一次性注册：**先全部校验、再统一写入**，任一类型非法则整体不生效，`ParticleIds` 只会在全部成功后才回填——这样就不会出现「前几个类型已注册、后面的 `ParticleIds.Xxx` 仍是 0（空气）」的静默退化。失败后 `RegisterAll()` 的后续调用会继续抛出，不会当作成功。
- **自定义类型表也要过同一套校验**：`new ParticleWorld(w, h, seed, types)` 会对每个非 null 条目调用 `ValidateTypeInfo`，非法条目直接抛 `ArgumentException`，而不是等到运行期 `Set` 才 DivideByZero。
- `Types[0]` 恒为 `null`（空气）；数组容量可能大于 `Count + 1`，超出部分是 `null`。`ParticleWorld` 与渲染层对超出范围的 id 一律当作「不存在的类型」跳过。
- 注册完成后再不修改。单线程读，无需锁。
- 初始化入口固定在启动点，且域重载安全：

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
static void Bootstrap() => RegisterAll();
```

- **禁止**在热路径写裸数字 `typeId`。跨类型引用（A 生成 B）统一用 `ParticleIds` 静态字段，由注册流程回填：

```csharp
// Assets/Scripts/Simulation/ParticleIds.cs
public static class ParticleIds
{
    public const ushort Air = ParticleRegistry.AirTypeId;
    public static ushort Stone;     // 由 ParticleRegistration.RegisterAll() 依序回填
    public static ushort Sand;
    // 每新增一个类型，这里加一个字段
}
```

- 注册顺序 = 存档兼容性的一部分。**一旦有存档/回放，禁止重排已有顺序**，只能追加。

---

## 5. 粒子类契约

### 目录与命名

- 一个类型 = **一个文件**，放在 `Assets/Scripts/Simulation/Particles/`。
- 文件名 = 类名，`PascalCase`，后缀 `Particle`：`XxxParticle.cs`。
- 类型需要附带资源（多个源文件、调试工具）时，可以为它建子目录 `Particles/Xxx/`，但**入口行为类仍然每类型一个文件**。
- 类必须是 `public sealed class`，**继承任何基类都不允许**、**不是 MonoBehaviour**、**不是 ScriptableObject**。

### 编译期契约

```csharp
public interface IParticleType
{
    ParticleTypeInfo Info { get; }
    void OnMotion(ParticleWorld world, int x, int y);
    void OnLogic (ParticleWorld world, int x, int y);
    void OnRandomTick(ParticleWorld world, int x, int y);
    void OnSpawn (ParticleWorld world, int x, int y);
    void OnKill  (ParticleWorld world, int x, int y, ushort killerTypeId);
}
```

**接口只用于作者期约束和注册期取委托。** 运行期持有的是注册表里的 `ParticleType`（委托 + 数据），**从不持有 `IParticleType` 引用、从不通过接口调方法**——这保证热路径没有接口虚分派。

### 五个钩子的精确职责

世界有两个节拍：**运动帧 60 Hz**（基本节拍）与**逻辑帧 20 Hz**（运动帧节拍上的子节拍，每 3 个运动帧一次）。一次逻辑帧 = 先逻辑、再运动、再跑随机刻 / 热传导 / 湿度 / 降水 / 光照；其余运动帧只跑运动。

| 钩子 | 何时被调用 | 应该做什么 | 不应该做什么 |
|---|---|---|---|
| `OnMotion` | **每运动帧**（60 Hz）扫描到该类型且该格本帧未被处理时，**每帧至多一次**（休眠开启时只对候选格调用，见 §7「运动休眠」） | 只做移动：查邻居 → 判断 → 调用 `Swap` 实现下落、斜落、横流、浮力位移；并在 `Info.MotionReadReach` 里声明决策的轴对齐阅读盒（§4） | 相变 / 反应（那是 `OnLogic`）；不做渲染、不读 `Time.*`、不分配；**禁止消耗世界随机流**（`NextUInt`/`NextInt`/`NextByte`）——方向等随机决策用 `HashNoise` 坐标哈希（见下「随机与坐标哈希」）；**不得读湿度 / 光照等「非写入即变」的状态**（免得被休眠跳过时决策过期） |
| `OnLogic` | **每逻辑帧**（20 Hz）扫描到该类型且该格本帧未被处理时，**在同格 `OnMotion` 之前**（`Info.HasLogic = false` 时整段跳过，见 §7「逻辑帧混合扫描」） | 相变 / 反应 / 邻域写入：`SetPhase` / `Set` / `Clear`、把水汽或热写给邻居 | **移动**（默认不要 `Swap`，那是 `OnMotion` 的职责；**已定稿的唯一例外是 `FireParticle` 的游离火上升**——写在 `OnLogic` 末尾、`OnMotion` 留空，见 §4「火」）；做渲染、读 `Time.*`、分配 |
| `OnRandomTick` | 每逻辑帧世界随机抽 `RandomTicksPerStep` 个格时被命中，**同一次逻辑帧内同一格可能被命中多次**（也可能一次都不中） | 「缓慢、按概率发生」的局部过程：生长、蔓延、渗流、腐化——一次只处理自己这一格 | 塞进本该每帧发生的运动（那是 `OnMotion`）；指望它每帧都跑 |
| `OnSpawn` | **每次**通过 `Set` 创建该类型格之后（`Swap` 不会触发） | 额外初始化：`vx/vy`、类型私有 flag 位、需要特定 `shade` 时覆盖默认抽样 | 不把新格再挪走（它已标记为本 tick 已处理） |
| `OnKill` | 该类型格被**其它类型或空气覆盖**时，在写入新格**之前**调用 | 残留效果（往**邻居**格写烟/火/灰）、清理由自己申请的全局资源 | **写自己脚下那一格**（会被随后写入的新格覆盖，见下） |

`OnKill` 的 `killerTypeId == 0` 表示被抹除（`Clear`），非 0 表示被该类型覆盖。

`OnMotion` / `OnLogic` / `OnSpawn` 的 `(x, y)` 是调用时刻该粒子的坐标，**就是 `world.Index` 反算的坐标**，不需要类型自己做除法。

### 随机与坐标哈希（运动路径 vs 逻辑路径）

| 路径 | 随机源 |
|---|---|
| `OnMotion`（运动帧 60 Hz，含它调用的 helper） | **只能用 `HashNoise`**（`Hash32` / `Bit`，`(x, y, world.Tick)` 坐标哈希）；**禁止** `world.NextUInt` / `NextInt` / `NextByte` |
| `OnLogic`（逻辑帧 20 Hz） | 世界随机流 `world.NextUInt` 等 |
| `OnRandomTick` | 世界随机流 `world.NextUInt` 等 |

**为什么**：世界随机流是一条**有状态的序列**，「谁消耗了一个数」会改变其后所有格子的决策。运动阶段将来会做**运动休眠**（跳过静止粒子、不重新求值），跳过哪些粒子绝不能改变其余粒子的行为；一旦 `OnMotion` 消耗世界流，流的位置就依赖「本帧哪些粒子被求值」，同种子同命令逐格可复现的契约随之破裂。坐标哈希是 `(x, y, Tick)` 的纯函数，与求值顺序、求值与否都无关，因此运动路径上的一切方向选择（斜落左右先后、横流左右先后、气体斜升 / 斜沉左右先后）一律改用它。逻辑帧与随机刻每逻辑帧都会覆盖全部应处理的格，不参与休眠，继续共享世界流。

`HashNoise.Hash32(x, y, tick, salt)` 就是把火焰 v2 既有的 `(x,y,Tick)` 哈希（shade 抖动 / 游离火摇曳）原样提取出来；`HashNoise.Bit(x, y, tick, salt)` 在其上跑一轮 Murmur3 雪崩再取一位（不能直接取 `Hash32` 的 bit0——乘法不进位到低位，bit0 只含坐标奇偶信息，会造成方向的对角偏置）。**每个调用点必须传互不相同的 `salt` 常量**，否则不同用途会共享同一条位序列。`world.Tick` 每步（运动或逻辑帧）自增，同一格逐帧换方向。

### 单例实例上的字段

允许且推荐：类型级只读配置、调色板、邻域暂存缓冲。

```csharp
public sealed class XxxParticle : IParticleType
{
    private static readonly Color32[] Palette = { /* 1..256 个颜色 */ };
    private readonly Color32[] _neighbourScratch = new Color32[9];   // 允许：类型级暂存

    public ParticleTypeInfo Info => new ParticleTypeInfo(
        tags: ParticleTags.Solid,
        density: 1.0f,
        heatCapacity: 1.0f,
        affectedByGravity: true,
        isStatic: false,
        maxFlowSpread: 0,
        friction: 0f,
        stickiness: 0f,
        palette: Palette,
        debugName: nameof(XxxParticle));

    // 固体：运动帧只做 ParticleRules.StepSolid(world, x, y);（按上面的 friction / stickiness 决定安息角）
    public void OnMotion(ParticleWorld world, int x, int y) { /* ... */ }
    public void OnLogic (ParticleWorld world, int x, int y) { /* 无相变 / 反应就留空 */ }
    public void OnRandomTick(ParticleWorld world, int x, int y) { /* 无慢过程就留空 */ }
    public void OnSpawn (ParticleWorld world, int x, int y) { }
    public void OnKill  (ParticleWorld world, int x, int y, ushort killerTypeId) { }
}
```

`Info` 只在注册时被读一次；每次访问新建一个结构体是刻意的（不走热路径）。

---

## 6. 世界 API（类型作者能用的全部原语）

```csharp
// Assets/Scripts/Simulation/ParticleWorld.cs —— 纯 C# 类，不是 MonoBehaviour
public sealed class ParticleWorld
{
    public ParticleWorld(int width, int height, uint seed);                       // 使用全局注册表 Types
    public ParticleWorld(int width, int height, uint seed, ParticleType[] types); // 隔离测试用自定义类型表

    public int  Width  { get; }
    public int  Height { get; }
    public int  CellCount { get; }              // == Width * Height
    public int  Tick   { get; }                 // 已完成的 tick 计数
    public bool Parity => (Tick & 1) != 0;      // 本次 Step 的扫描方向奇偶
    public int  RandomTicksPerStep { get; set; } // 每逻辑帧随机抽并触发 OnRandomTick 的格数（0 = 关闭）；夹在 [0, CellCount]
    public Cell[] Cells { get; }                // 底层数组，只读契约：外部只允许读
    public ParticleType[] Types { get; }        // Types[0] == null（空气）
    public HeatMap Heat { get; }                // 与网格同形的热量场 Q（float/格），见下
    public LightController Light { get; }       // 光照控制器（按时序向每列最上面的非气体粒子施热——光打到水面就停），见下

    // —— 读 ——
    public int  Index(int x, int y);            // y * Width + x
    public bool IsInBounds(int x, int y);
    public bool IsAir(int x, int y);
    public Cell GetCell(int x, int y);          // 值拷贝（4 字节）；越界返回空气
    public ref Cell At(int x, int y);           // 本格就地读写（flags/shade/vx/vy）；越界返回只读暂存格

    // —— 写（原子、无条件执行、返回是否落到网格内）——
    public bool Swap (int x0, int y0, int x1, int y1);
    public bool Set  (int x, int y, ushort typeId);
    public bool Clear(int x, int y);            // 等价于 Set(x, y, 0)
    public bool SetPhase(int x, int y, ushort typeId); // 相变：Q 与湿度原样带走（见下「写操作的钩子与副作用」）
    public void WakeMotion(int x, int y);       // 就地改 flags 后的运动唤醒：唤醒下一帧的候选位图（休眠关闭时空操作），见下

    // —— 确定性随机（世界持有种子）——
    public uint NextUInt();
    public int  NextInt (int  maxExclusive);    // maxExclusive <= 0 → 0
    public byte NextByte(byte maxExclusive);    // 用于 shade 抽样

    public void StepMotion();                   // 运动帧：扫描一次、只调 OnMotion（由 driver 调用）
    public void StepLogic();                    // 逻辑帧：先 OnLogic 再 OnMotion，随后随机刻 / 热 / 湿度 / 降水 / 光照
}
```

> 种子约定：`seed == 0` 会被静默替换为内部默认种子（xorshift32 需要非零状态）。回放/存档请避免使用 0。


### 热力学（`HeatMap` + `LightController`）：简化的 Q ↔ T 模型

每个世界持有一张与网格同形的热图，每格存**热量 Q**（`float`）。温度不单独存储，而是按该格物质的**热容 C** 换算：

```
T = Q / C        C = 该格粒子类型的 Info.HeatCapacity（空气用 HeatMap.AirHeatCapacity）
                     + 该格湿度 × HeatMap.HumidityHeatCapacity（= 1）——气体格的附加热惯性，见下
```

C 里的湿度项只对**持有湿度的气体格**生效（固/液恒为 0 湿度），它让含水汽的空气热惯性变大——云所在的气柱更"扛热"。

这套模型刻意简化、不完美：无**显式**对流（气体竖直位移的绝热降温 / 增温由 `AdiabaticLapse` 近似）、**蒸发 / 凝结已建模潜热**（`HumidityField.LatentHeat(T)` = `LatentHeatPerDegree × T`，随温度线性、相变点温度连续，每单位水严格守恒）、Q 不钳制（负值表示冷）；辐射只作用于暴露面（顶行或正上方是气体/空）、按材料辐射率 ε 缩放、且是线性近似（真实是 T⁴）。它保证的是三条最基础的热力学直觉：**界内热量守恒**、**温度随物质变化**、**热会散逸到天空（因此光照下会收敛到稳态而不是无限升温）**。

#### 热量由物质携带

| 操作 | 对 Q 与湿度的影响 |
|---|---|
| `Swap(A, B)` | **Q 与湿度随物质一起搬走**——热沙下沉后仍然是热的，落到同一格的空气被换到高处；若换动的是**气体**（含 typeId 0 的空气），还按竖直位移结算绝热直减（见下）。 |
| `Set(x,y,T)` / `Clear` | 换上**新物质**，**继承该格原有热量**——旧物质离场，热就地留给新物质（新生的有机物不会被拍成 0°）；本格残余水汽（> 0 时）**无论目标是气体还是固 / 液**都先由 `EvacuateHumidity` 推给四邻气体格，推不掉才原地保留（守恒优先于不变量）。旧写法对气体目标直接 `ResetAt`，会凭空湮灭水汽（FB2：火蔓延 / 火自灭；世界侧统一后火代码无需任何特判）。 |
| `SetPhase(x,y,T)` | **同一份物质换形态**：Q **原样保留**；湿度只对**气体形态**原样携带——换成固/液时该格水汽同样先经 `EvacuateHumidity` 推给四邻（推不掉才留下）。 |
| `Set(x,y,T)` 目标已是 T | 空操作，物质没换，Q 与湿度都保留。 |
| 粒子 `OnLogic` / `OnMotion` 内 `AddHeat` | 直接改该格 Q，可正可负。 |

因此「热属于物质」而非「属于位置」；想要热量留在原地（例如一炉恒热），由类型自己把 Q 抄给新位置。

#### 绝热直减：气体竖直位移的冷暖

`ParticleWorld.Swap` 在交换两格之后，对**带 `Gas` 标签**的格子（含 typeId 0 的空气）按各自的竖直位移结算热量：

```
ΔQ = −AdiabaticLapse × Δy × C        Δy = 新 y − 旧 y（上升 > 0、下沉 < 0）；C = 该格热容
```

上升（`Δy > 0`）→ 冷却，下沉（`Δy < 0`）→ 增温；两格各自独立结算，因此上升侧与下沉侧的 ΔQ 符号相反。`AdiabaticLapse` 默认 `0.1`（°/格），由 `ParticleSimulationSettings._adiabaticLapse`（资产 `0.1`）每帧写回；`0` = 关闭（气体移动不带温度变化）。这是真实干绝热直减率的格尺度等效，给大气补上一层随高度变化的竖直温度梯度。

#### 热传导：每逻辑帧一次全格四邻扩散

`ParticleWorld.StepLogic` 在粒子扫描之后调用 `HeatMap.Step()`，做一次显式差分扩散。每对相邻格交换的热量正比于**温度差**，界面导热率取两侧材料相对导热率 σ 的**算术平均**：

```
ΔQ(A←B) = Conductivity * (σ_A + σ_B) / 2 * (T_B - T_A)
```

σ 是每个类型的 `ParticleTypeInfo.ThermalConductivity`（`[0, 1]`，1 = 最强、0 = 绝热）；空气（typeId 0）没有类型条目，用 `HeatMap.AirThermalConductivity`。因为界面导热率 ≤ `Conductivity`，σ 全为 1 时就是旧的「单一全局导热系数」行为。这样高导热材料（水）把热摊开、低导热材料（沙）把热憋在表层——**同样注热，隔热材料的表面更烫**（实测 1×H 柱底部持续注热：水底 56°、石 83°、土 94°、沙 101°）。

一对相邻格之间的交换**大小相等、方向相反**，因此**界内热量守恒**——热只是在格与格之间搬家。扩散读的是本 tick 开始时的温度场（先算全部差值、再整体写回，双缓冲），所以结果与扫描顺序无关、确定可复现。`Conductivity = 0` 关闭传导。核心循环跑在 **Burst**（`FloatMode.Strict`）里，见 §7「世界层场 pass 的实现」。

**不动点跳过**：热场一旦收敛（某一趟 `Step` 无任一格 Q 变化）且此后输入全未变（无外部热写入、格子 / 湿度 / 参数都未变），`HeatMap.Step` 就**整趟跳过**——重算与跳过的结果逐位相同，故不改变模拟。持续照亮时每帧注入热、跳过不生效；收益只在「完全静止且热场已收敛」的场景——精确不动点收敛极慢（生产尺寸需上万逻辑刻），昼夜暗相（数百刻）远不足以收敛，**不要指望暗相收益**。详见 §7「热传导不动点跳过」。

#### 散热 A：顶行向虚空

同一次 `HeatMap.Step()` 里，**网格最上面一行**（`y = Height - 1`）还会向界外上方的「虚空」流失热量：

```
ΔQ(顶行) = -VoidHeatLoss * T
```

虚空恒为 `AmbientHeat`（0），相当于一个无限大的冷库，热量只出不进。在辐射关闭时它是热量离开世界的唯一出口：没有它，光照注入的热只增不减（实测 `Intensity = 5` 时温度每逻辑帧涨 ~5.6，永不收敛）；有它之后，持续受热的世界会收敛到稳态：

```
T_稳态 ≈ 输入功率 / VoidHeatLoss        （光照 + 无其它热源时）
```

`VoidHeatLoss = 0` 即封闭系统（保留旧的只增不减行为）。它与 `Conductivity` **独立**——不被导热率缩放，因此可以「导热很强但几乎不散热」，反之亦然。

#### 散热 B：辐射（只作用于暴露面，按材料辐射率缩放）

同一次 `HeatMap.Step()` 里，**暴露于天空的格**（顶行，或正上方是气体/空）还按自身温度向外辐射（斯特藩定律的线性近似），并按**本格材料的相对辐射率 ε** 缩放：

```
ΔQ(暴露格) = -RadiativeLoss * ε * T          ε = ParticleTypeInfo.ThermalEmissivity（空气用 HeatMap.AirThermalEmissivity）
```

**只有暴露面辐射**，埋在地下的格不辐射——它四周都是物质、净辐射 ≈ 0。这一条是「热能不能传进地下」的关键：

- 若**每个**格都辐射（旧行为），埋着的格会逐格漏热，λ 与地下界面导热率同量级时，热往下走 1~2 格就被抽干——**热只停在地表**（实测厚石层顶部注热、λ=0.05：d0=83、d1=49、d5=6、d10=0，衰减长度仅 ≈1.9 格）。
- 只让暴露面辐射后，地表以下的热只能靠传导往**上**散，因而必须先把热铺满地下——**热传得进地下**（稳态下整柱趋于同温，升温过程呈梯度）。

**ε 是「热在竖直方向能铺多远」的旋钮**，也是唯一能把「空气铺得高」与「地表温度」解耦的参数。ε 与导热率 σ 是两个独立的旋钮：σ 决定热在格与格之间传得多快，ε 决定热从暴露面漏掉多少。空气柱每一格都是暴露面、都在散热，所以 ε 控制整柱的漏热总量：

| 大气 ε | 地表 | 空气（距地表 +2 / +10 / +20 / +40 / +60） |
|---|---|---|
| 1.0（旧行为） | 29.2° | 9.6 / 2.4 / 0.3 / 0.0 / 0.0 |
| 0.3 | 35.9° | 18.4 / 9.2 / 3.1 / 0.1 / 0.0 |
| **0.1（现配）** | 41.2° | 26.3 / 19.9 / 13.1 / 3.9 / 0.8 |
| 0.05 | 43.5° | 29.4 / 25.5 / 21.0 / 13.2 / 6.0 |

（实测：100×100、30 格石地、光照 10 亮 600 / 暗 600、λ=0.05，跑 40000 tick + 1200 tick 时均；ε 越大越像「热只在低空」，越小越均匀。ε=1 时空气的热 20 格内耗尽——「断崖」= 对流边界层顶 + 传导只剩 ≈2 格；ε=0.1 时**从地表到网格顶是单调、平滑的衰减**，没有断崖；再降到 0.05 还能更平，但地表跟着升到 43.5°。代价是空气整体变暖（+10 处 2.4° → 19.9°）、地表升温（空气变暖让地表更难散热），且**昼夜幅度变大**（ε=0.1 时地表 5.9..78.4°）。固/液/云都取 ε=1（近黑体，与旧行为一致），所以它们的表面温度不受影响——这正是 B 相对「全局降 λ」的好处：调空气不动固/液，也不必再动光照强度。（水汽不是材料，没有 ε；云的垂直分布改由湿度场的 `RiseStopHumidity` 决定。））

代码默认 0（关闭，保持旧行为与既有热测试契约），生产值由 Settings 资产注入（当前 0.05）。`AirThermalEmissivity` 的**代码默认是 1**（不改既有单测契约），生产值由 Settings 资产注入（当前 0.1）。

各系数都可以在 **`ParticleSimulationSettings` 资产**（`Assets/Data/`）里调，`ParticleSimulationDriver` 每帧写回世界，运行时改即时生效、重建世界后自动重新应用：

| Settings 字段 | 范围 | 默认 | 作用 |
|---|---|---|---|
| `_conductivity` | `[0, 0.24]` | 代码默认 `0.15`，资产现配 `0.24`（最高：热量摊得最平，光照不再产生小区域热点） | 界面导热率的全局尺度；越大越等温 |
| `_voidHeatLoss` | `[0, 0.24]` | 代码默认 `0.1`，资产现配 `0.12`（高空更冷、冷凝带更宽，加快水循环） | 顶行向虚空散热的系数；越大稳态温度越低 |
| `_radiativeLoss` | `[0, 0.24]` | 代码默认 `0`（关闭），资产现配 `0.05` | 辐射散热的全局尺度，**只作用于暴露面**且按 ε 缩放；越大所有暴露面越冷 |
| `_adiabaticLapse` | `>= 0` | 代码默认 `0.1`，资产现配 `0.1` | 气体竖直换位的绝热直减率（°/格）：上升冷却、下沉增温（`ΔQ = −lapse × Δy × C`）；0 = 关闭 |
| `_airThermalConductivity` | `[0, 1]` | 代码默认 `1`（空气不额外削弱传导） | 空气（typeId 0）的相对导热率；决定热从地表/水面漏进上方气柱的难易 |
| `_airThermalEmissivity` | `[0, 1]` | 代码默认 `1`（旧行为），资产现配 `0.1` | 空气（typeId 0）的相对辐射率 ε；**决定热在竖直方向能铺多远**——气柱每格都是暴露面，ε 越大热越早被抽干、只停在地表附近 |

#### 「越高空越冷」：能，但是涌现出来的

竖直温差不是一个写死的「高度 → 温度」法则，而是**热源在下、冷库（虚空）在上**加传导的自然结果。稳态下每列是 1D 热流，串起来可得两条可调公式：

```
每格温差（lapse） = 注入功率 / Conductivity      ← 想让「每升一格冷多少」变陡，就把 Conductivity 调小
顶层温度         = 注入功率 / VoidHeatLoss      ← 想让整条曲线整体冷下来，就把 VoidHeatLoss 调大
```

实测（8×48，石地板 y=0..2，光照强度 5 常亮，跑到稳态）：

| 场景 | 底部（地板顶） | → 顶部 | 结论 |
|---|---|---|---|
| `k = 0.15`（默认） | 1174 | 32 | 单调递减 ✅ |
| `k = 0.03`（更隔热的空气） | 4236 | 17 | 更陡，符合 `斜率 = 功率 / k` ✅ |
| 关掉光照 | 0 | 0 | 没有热源就没有温差 ❌ |

**能成立的前提**：热源在下方（光照打的是「每列最上面的非气体粒子」，空列里光源就是地板；液体挡光后水面也是热源）、网格顶行在热源上方。**反例会失效**：如果固体一直填到网格顶行（光源与散热口重合），实测剖面几乎是平的（y=0 → 47.9，y=47 → 49.9，略微反向），因为「冷库」只在网格最上面那一行，而那一行同时也是被加热的那一行。

**稳定性**：显式格式要求 `(Conductivity * 邻居数 + VoidHeatLoss + RadiativeLoss) / 热容 < 2`。各系数上限都是 `MaxConductivity = 0.24`，配合热容 ≳ 0.5 即可稳定；把热容设得极小又拉满导热会看到温度来回振荡。

> 已简化的地方：虚空散热只发生在网格顶行，而不是**任意暴露表面**（堆顶、侧壁的虚空散热）；**暴露面判定只查正上方一格**——洞穴 / 顶棚内的空气也按暴露面处理（已知简化，要准就复用云遮光的光路扫描）；辐射是线性近似（真实是 T⁴），且 **T < 0 时 `−λ·ε·T` 的符号反转变成加热**（等价于向 0 环境松弛，无害，但「只散热」仅对 T > 0 成立）。辐射率 ε 已按材料区分（空气 0.1、其余 1.0，见 §6）。

#### 光照：`LightController`

世界持有一个光照控制器（`world.Light`），每逻辑帧在传导之后由 `StepLogic` 驱动。它是个**占空比循环光源**，照亮时向**每一列最上面的非气体粒子**（Solid 或 Liquid——光穿过气体，打到水面/地面即停）注入 `Intensity` 的热量 Q（不是温度——升温多少取决于该物质的热容）。海洋因此被表面加热、表面蒸发，而不是被光穿透过热海底。

**云会遮光**：光照时 `ApplyHeat` **自顶向下**逐格扫描，边走边累加**光路上的湿度**，遇到第一个 `BlocksLight` 格（固/液——水面 / 地面）即停；到达该格的能量按 Beer–Lambert 衰减：

```
地表受光 = Intensity × exp(-CloudExtinction × 光路湿度)
```

只累加**光程**上的湿度，意味着**洞穴 / 顶棚下方的地表不会再被上方云层连带遮黑**（旧实现每逻辑帧对整列湿度求和 `HumidityField.ColumnSums`，把光路下方的湿度也算进去——审计 B4；`ColumnSums` 方法保留给测试 / 工具，不再每逻辑帧重算）。
云厚的那一列地表凉、云薄的那一列地表热——这给水循环加了一条负反馈（云多 → 地表冷 → 蒸发少 → 云少）。
`CloudExtinction` = 0 即关掉这条（旧行为）。实测（100×100 水盆、30000 tick 预热）各取值的稳态：

| `CloudExtinction` | 海水留存 | 水面温度 | 备注 |
|---|---|---|---|
| 0 | 284 / 900 | 32.7° | 旧行为（不遮光） |
| 0.05（默认） | 228 / 900 | 27.1° | 厚云列（光路湿度 ≈ 7）透过率 ≈ 0.70 |
| 0.1 | 191 / 900 | 22.3° | 遮光明显，地表更凉 |
| 0.2 | 288 / 900 | 16.3° | 地表很凉；水循环被强烈改写 |

**读数**：`Light.GetCloudThickness(x)`（该列**光路**湿度总量）与 `Light.GetCloudTransmittance(x)`（该列透光率 ∈ (0,1]）是公开导出量，渲染 / 调试可直接读；两者都**按需对光路扫描**（遇第一个 `BlocksLight` 格即停），不再每逻辑帧维护列和。

```csharp
// Assets/Scripts/Simulation/LightController.cs —— 纯 C#，无 Unity 依赖
public sealed class LightController
{
    public bool  Enabled   { get; set; }    // 是否光照（总开关）；关闭时不注入、相位也不推进
    public int   LitTicks  { get; set; }    // 亮相位持续刻数（>= 0）；0 = 从不照亮
    public int   DarkTicks { get; set; }    // 暗相位持续刻数（>= 0）；0 = 一直照亮
    public float Intensity { get; set; }    // 亮度：亮相位每刻向每列最上面的非气体粒子注入的热量 Q
    public float CloudExtinction { get; set; } // 云层消光系数（每单位光路湿度）；0 = 云不遮光

    public int  Elapsed { get; }            // 已推进的刻数（只读）
    public bool IsLit   { get; }            // 当前是否处于亮相位
    public const int DaylightFadeDivisor = 10; // 破晓 / 黄昏的过渡长度 = 下一时段长度 / 10（自动，无字段）
    public float Daylight { get; }          // 归一化日照度 0..1：1 = 白天上限、0 = 夜晚下限（只读，给渲染层做明暗与天空配色）
    public float GetCloudThickness(int x);     // 第 x 列云层厚度 = 光路上的湿度总量（按需扫描，遇 BlocksLight 即停）
    public float GetCloudTransmittance(int x); // 第 x 列透光率 = exp(-CloudExtinction × 厚度)
    public void Reset();                    // 相位拨回起点
    // Step(world) 由 ParticleWorld.StepLogic 调用，正常玩法不要手动调用
}
```

- 通断语义：先亮相位 `LitTicks` 刻 → 暗相位 `DarkTicks` 刻 → 循环往复（白天 / 黑夜、呼吸式频闪）。
  `LitTicks = 0` 从不亮；`DarkTicks = 0` 一直亮；`Enabled = false` 全关。
- 「每一列最上面的非气体」：对每个 `x` 从 `y = Height-1` 向下扫，遇到第一个带 `Solid` 或 `Liquid` 标签的格（`ParticleRules.BlocksLight`）就往那格加 `Intensity × 该列透光率 × 该材料 LightAbsorption`，然后换下一列。
- **材料光照吸收率 `LightAbsorption` ∈ [0,1]**（可选，默认 1）：到达格子的能量再乘上该材料的吸收率——1 = 完全吸收（升温最快）、0 = 全反射（光照不升温）。表达「浅色 / 反光表面在阳光下升温更慢」；**草取 0.8**（`GrassParticle.LightAbsorptionFactor`，略减弱 20%）。只影响光照注入，不影响热传导（σ）与辐射（ε）。
- 简化：光是**垂直、一次性、全网格**的，没有方向、没有距离/角度衰减——但不再穿过液体：水面即光程终点。需要阴影 / 衰减再另行扩展。
- **视觉表现：昼夜（天空配色 + 整场明暗）**。光照系统本身只注入热量，画面上由 `ParticleRenderer` 把光照周期映射成两层表现：
  1. **天空色**在夜晚色（默认 `#2A223A`）/ 白天色（默认 `#CCE5FF`）之间按 `Daylight` 取色；
  2. **粒子与云**的亮度按 `Mathf.Lerp(夜晚亮度, 1, Daylight)` 缩放（默认 0.5 = 夜晚半亮；1 = 不随昼夜变暗）。
  三个参数**都在 `ParticleLightDriver` 上配置**（`_dayBackground` / `_nightBackground` / `_nightBrightness`），由它每帧推给 `ParticleRenderer`（同 `ParticleSimulationDriver` 把热力学参数写回世界）；渲染器只保留同名代码默认值，好在没有光源驱动器时也能上屏。
  两者都交给 `ParticlePixelBuffer.Fill(world, pixels, background, brightness)`；**背景色不参与亮度缩放**（夜晚色本身就是深色，再乘一遍就不是作者指定的颜色了），只有真正画了粒子 / 云的像素才变暗。
- **过渡长度是自动的**：`Daylight` 在破晓 / 黄昏各用「**下一时段**长度的 1/`DaylightFadeDivisor`」刻平滑过渡，其余时间恒定在 1 / 0。例：亮 600 / 暗 600 ⇒ 破晓 60 刻（下一段是白天 600）、黄昏 60 刻（下一段是黑夜 600）；亮 600 / 暗 60 ⇒ 破晓 60、黄昏 6。黄昏的坡道会夹到 `LitTicks / 2` 以内，避免「夜远长于昼」时坡道吞掉整个白天。天空色、亮度、以及光照施热各自独立，都由同一份 `Daylight` / `IsLit` 读数驱动。

#### 世界 API

```csharp
// Assets/Scripts/Simulation/HeatMap.cs —— 纯 C# 类，随 ParticleWorld 一起创建
public sealed class HeatMap
{
    public const float AmbientHeat = 0f;            // 环境热量：初值、越界读返回值、新物质初值
    public const float AirHeatCapacity = 1f;        // 空气热容（空气没有类型条目）
    public const float MaxConductivity = 0.24f;     // 显式扩散的稳定上限（Conductivity 与 VoidHeatLoss 共用）
    public const float MaxThermalConductivity = 1f; // 每材料相对导热率 σ 的上限
    public const float MaxThermalEmissivity = 1f;   // 每材料相对辐射率 ε 的上限
    public const float DefaultConductivity = 0.15f;
    public const float DefaultVoidHeatLoss = 0.1f;  // 顶行向虚空散热：默认值
    public const float DefaultAirThermalConductivity = 1f; // 空气 σ 默认值
    public const float DefaultAirThermalEmissivity = 1f;   // 空气 ε 默认值（1 = 旧行为）

    public HeatMap(int width, int height, Cell[] cells, ParticleType[] types); // 单边 1..ParticleWorld.MaxSide

    public int   Width  { get; }
    public int   Height { get; }
    public int   CellCount { get; }                 // == Width * Height，float 缓冲 4 字节/格
    public float[] Values { get; }                  // 底层 **Q** 缓冲，只读契约
    public float Conductivity { get; set; }         // [0, MaxConductivity]，超出夹紧（界面导热率的全局尺度）
    public float VoidHeatLoss { get; set; }         // [0, MaxConductivity]；顶行向虚空散热的系数，0 = 封闭系统
    public float AirThermalConductivity { get; set; } // [0, 1]；空气（typeId 0）的相对导热率 σ
    public float AirThermalEmissivity { get; set; }   // [0, 1]；空气（typeId 0）的相对辐射率 ε

    public bool  DebugHeatOverlay { get; set; }     // 调试开关：叠层显示热力图（越热越红），只影响显示
    public float DebugFullScaleTemperature { get; set; } // 叠加满量程温度：达到它即纯红；<= 0 关闭叠加

    public int   Index(int x, int y);               // y * Width + x，与 Cell 一致
    public bool  IsInBounds(int x, int y);
    public float GetHeat(int x, int y);             // 热量 Q；越界返回 AmbientHeat
    public float GetCapacity(int x, int y);         // 该格物质热容 + 湿度附加热容；越界 / 空气返回 AirHeatCapacity
    public float GetConductivity(int x, int y);     // 该格物质相对导热率 σ；越界 / 空气返回 AirThermalConductivity
    public float GetEmissivity(int x, int y);       // 该格物质相对辐射率 ε；越界 / 空气返回 AirThermalEmissivity
    public float GetTemperature(int x, int y);      // T = Q / C；越界返回 AmbientHeat
    public float GetTemperatureAt(int index);       // 同上，按扁平下标（渲染层逐格映射用）
    public bool  SetHeat(int x, int y, float heat); // 越界无操作，返回 false
    public bool  AddHeat(int x, int y, float delta);// 累加；越界无操作，返回 false
    public void  Step();                            // 由 ParticleWorld.StepLogic 调用，粒子不要手动调
    public bool  LastStepSkipped { get; }           // 最近一次 Step 是否整趟跳过（不动点跳过），只读
    public bool  FixpointSkipEnabled { get; set; }  // 默认 true；false 时每帧重跑，用于 A/B 逐位对照
}
```

**调试热力叠加**：`DebugHeatOverlay = true` 时，`ParticlePixelBuffer.Fill` 在正常配色之上再叠一层按温度染色的红（`T / DebugFullScaleTemperature` 达到 1 即纯红）——**空气格也会显示**，因为热是整场的。只影响上屏像素，不改任何模拟状态。场景里由 `ParticleSimulationDriver` 的「调试」字段（`_heatDebugOverlay` / `_heatDebugFullScale`）每帧写回，所以 rebuild 世界后依然生效。

粒子侧就是把热当成「另一份可读写的场」——**读温度用来判断、加/减热量用来改变**：

```csharp
public void OnLogic(ParticleWorld world, int x, int y)
{
    // 用温度做相变判断（同一份 Q 下，热容大的物质温度低）
    if (world.Heat.GetTemperature(x, y) >= IgnitionTemperature && world.IsAir(x, y + 1))
    {
        world.Set(x, y + 1, ParticleIds.Smoke);
    }

    world.Heat.AddHeat(x, y, -CoolingRate); // 自身散热（直接改 Q）
}
```

语义（不可协商）：

1. **Q 由物质携带**（见上表）：`Swap` 搬 Q，`Set` / `Clear` 就地继承 Q。
2. **传导与散热都是世界的职责**：每逻辑帧自动四邻扩散一次、并让顶行向虚空散一次热，粒子**不要**自己再做，否则重复计算。
3. **只增不减已被堵住**：热量唯一的出口是顶行向虚空散热（`VoidHeatLoss`）；除此之外没有环境基线、不钳制，Q 可正可负，量纲由玩法约定。
4. **越界语义与 `Cell` 一致**：越界读返回 `AmbientHeat`、越界写是无操作返回 `false`；调用前仍应先 `IsInBounds`。
5. **归世界所有**：`ParticleWorld` 构造时按同一宽高建热图；尺寸变化重建世界会**重置为全 0**。

#### 当前生产类型的热容

| 类型 | `HeatCapacity` | 直觉 |
|---|---|---|
| 沙（`SandParticle`） | `0.7` | 干沙：固体里热容最小，晒热最快、凉得也最快 |
| 土（`SoilParticle`） | `0.8` | 干土 |
| 湿土（`WetSoilParticle`） | `2.0` | 吸进去的水让湿土比干土扛热得多（水给 6.5，按含水量折算到 2.0） |
| 石（`StoneParticle`） | `0.8` | 石头：调小到 0.8，升温更快 |
| 水（`WaterParticle`） | `6.5` | 水：热容最大，升温最慢——海陆温差（海风）与水面昼夜惰性的根源 |
| 大气（`AtmosphereParticle`） | `1.0` | 必须等于 `AirHeatCapacity`：空气格与大气粒子的温度口径一致 |
| 火（`FireParticle`） | `1.0` | 与大气同档：火焰的热惯性按空气算（热源强度是 `FireHeatPerTick` 常量，不靠热容） |
| 空气 | `AirHeatCapacity = 1` | typeId 0（空）没有类型条目，固定常量 |

#### 当前生产类型的导热率 σ

| 类型 | `ThermalConductivity` | 直觉 |
|---|---|---|
| 沙（`SandParticle`） | `0.5` | 干沙：仍是固体里导热最差，但已调快（原 0.4） |
| 土（`SoilParticle`） | `0.7` | 干土 |
| 湿土（`WetSoilParticle`） | `0.85` | 水（0.95）灌进孔隙，湿土比干土导热好 |
| 石（`StoneParticle`） | `0.8` | 石头：固体里导热最快（原 0.7，接近水） |
| 水（`WaterParticle`） | `0.95` | 水：导热最好，水体内部接近等温 |
| 大气（`AtmosphereParticle`） | `1.0` | 气体传导最强；须等于 `HeatMap.AirThermalConductivity` |
| 火（`FireParticle`） | `1.0` | 气体传导最强一档（与大气一致）：火的热迅速摊给四邻 |
| 空气 | `AirThermalConductivity = 1` | typeId 0（空）没有类型条目，可配常量 |

#### 当前生产类型的辐射率 ε

ε 只影响**暴露于天空**的格（见 §6 散热 B），因此地下/水下的材料取多少都不改变它们的温度。

| 类型 | `ThermalEmissivity` | 直觉 |
|---|---|---|
| 沙 / 土 / 湿土 / 石（固） | `1.0` | 沙/岩/土/湿土都是近黑体，与旧行为一致 |
| 水（`WaterParticle`） | `1.0` | 水是近黑体（红外吸收极强），水面照常散热 |
| 大气（`AtmosphereParticle`） | `0.1` | **N₂/O₂ 对红外透明**：气柱不逐格漏热，地表的热才能铺到网格顶 |
| 火（`FireParticle`） | `1.0` | 近黑体：暴露的火焰面照常辐射散热（火的温度靠注入上限封顶，不靠 ε） |
| 空气 | `AirThermalEmissivity = 1`（代码默认）/ 资产 `0.1` | typeId 0（空）没有类型条目，可配常量 |

> **ε 的取法只看一件事：这份物质发出去的红外，有多少真的逸出到太空。**
> 空气（N₂/O₂）几乎不吸收红外 ⇒ 辐射对它是主要的散热方式，但**没有直接热源**，所以取 0.1 只影响它传播被传导进来的热；
> 岩石 / 沙 / 土 / 水都是光学厚的黑体 ⇒ 取 1.0。
> 水汽不在这里：它是**湿度场**里的量、不是材料，因此没有 ε（云与降水由场自身表达，见下）。

#### 相变与气体运动（水循环 / 海陆风）

气体不再是「永远向上」：`ParticleRules.TryBuoyantStep` 把本格温度与四邻均温比较——**热升、冷沉、死区（±1°）内不动**；`TryThermalDrift` 让气体水平流向更暖的一侧（低空海风的微观规则）——它带**风向记忆抗振荡**：唯一的更热侧若与上次漂移方向**相同**（或**无记忆**）才漂移并记下该方向，方向**相反**则无条件不走（记忆保留），本帧没有可漂方向才清记忆（记忆存在空气 flags 的 bit3-4，见 `CellFlags.DriftDirectionMask`）；浮力换行也清记忆。决策完全确定、不消耗随机数。没有它，`Swap` 互换温度后梯度对刚移动的格子同幅反向，任何温差阈值都挡不住乒乓（审计 B1 的位置 5,4,5,4…）。`TryRise`/`TryDiagonalRise`/`TrySink`/`TryDiagonalSink` 是它们的换位原语（同 rank 气体互换 = 混合，不走 rank 置换规则）。

**气体按密度次序换位（`ParticleRules.CanGasEnter`）**：rank 只决定**类别**能否换位（`CanDisplace`），同类内部再由 `Density` 排序——气体换位的落点判据是 `自己.Density >= 目标.Density`，因此**重的气体沉穿轻的、轻的气体挤不动重的**，同密度（同类之间）照常混合。当前有大气（密度 `0.5`）与火（密度 `0.6`）两种气体：**大气推不动火**（`0.5 < 0.6`），游离火上升（`CanGasEnter`）时可换进大气格。`IsGasOrEmpty` 仍是**纯目标判据**（看格不看发起者），保留给视线 / 通用查询。

水循环不再由"粒子类型互变"表达，而是**水 ⇌ 湿度场的湿度**：

```
水   --(T ≥ WaterParticle.BoilingPoint = 80 或 T ≥ 20 且 1/300 每逻辑帧，
        且水面之上的空气湿度 < HumidityField.EvaporationCeiling)--> 水汽（湿度 +1，播撒给本格与气体邻居；
        本格 AddHeat −LatentHeat(T) → 蒸发冷却，随温度放大）
水汽 --(湿度 ≥ HumidityField.SaturationHumidity(T) = RainThreshold × 2^((T − DewPoint)/10))
    --> 雨（碰并凑够一个整水滴 → Water 粒子；凝结本格 AddHeat +LatentHeat(T) → 云层放热）
```

**云不是粒子，是"很湿的空气"**：湿度场（`HumidityField`）每格一个水汽质量，只有非固非液的格持有它。
`ParticlePixelBuffer` 按湿度把格子染白（`VisibleThreshold` 起淡入），因此云就是湿度场里的一片高值区。
这对应真实世界：云是空气的一种状态，不是一种有自己运动规则的物体。

**湿度怎么走**（`HumidityField.Step`，每逻辑帧一次，界内严格守恒）：
- **扩散**：高湿度 → 低湿度（与热传导同形）。
- **水平：冷 → 热**（`HorizontalBias`）。同一高度上水汽向更热的格子迁移。
- **竖直：越热越向上**（`RiseBias`，按 `温度 / RiseTemperatureScale` 归一）。这是一次**上风平流**——
  本格按自身温度把湿度往上送，与上面那格热不热无关；"下面冷"不会把水汽吸下来。它就是"云飘在天上"的动力：
  暖水面蒸出的水汽被持续抬升，升到高空变冷，在高处凝结成云、下雨。
  **但湿度达到 `RiseStopHumidity` 之后不再抬升**（凝结后的气块失去浮力的等效表达）：
  没有这一条，水汽会一路顶到天花板、堆成贴着顶行的薄盖子，而顶行又是向虚空散热的最冷处——
  表现出来就是"云只有薄薄一层、雨像从虚空里落下来"。有门控后云在**第一次浓到看得清的高度**铺开。
  门控**只作用在竖直平流项上**（对称扩散项照常结算），且按**真实湿度**判定——否则会把整对竖直交换整个冻死（半封闭地形里的一格宽水井曾因此永冻不散，审计 F1）。湿度场 `Step` 的 x 扫描方向还按 `world.Parity` 逐逻辑帧交替，消除固定方向的偏置。

**水蒸气只在沸腾时可见**：**不可见蒸发**把 1 份水汽摊到半径 4 的 9×9 = **81 格**一大片（`HumidityField.InvisibleSpreadRadius`，每格 `Unit/接收格数`），
且**只在液面（正上方是界内气体——顶行的水不蒸发）、且撒播范围内所有气体格都低于 `HumidityField.InvisibleSpreadCap`（0.02）时才发生**；
再加一道**份额门控** `HumidityField.InvisibleSpreadMaxShare`（0.04 = 1/25）：接收格 < 25 时摊不薄，`SpreadUnitIntoAir` 返回 0，调用方**不蒸发、水保留**——
所以**接收格足够多**时这条路攒不出可见的白汽（0.02 + 份额 < `VisibleThreshold` 0.06），只是稳稳地往空气里加水汽；半封闭地形（石墙口袋只剩 2 格气体）则干脆不蒸，不会凭空冒出可见水汽（审计 B2）。
**水的常温慢蒸发与湿土的受热干燥都走这条路**（同一入口 `HumidityField.SpreadUnitIntoAir`，传 `InvisibleSpreadMaxShare`；**沸腾传 0 表示不限份额**，因为沸腾按设计就要可见）。
两条蒸发路都**从水里带走一份相变潜热** `HumidityField.LatentHeat(T)`（= `LatentHeatPerDegree × T`，本格 `AddHeat −LatentHeat(T)`）——水面因此被蒸发冷却且冷却随水温放大，海风所需的冷源正来自这里。
沸腾则相反：把同样 1 份水汽**集中**在 3×3 原地，每格浓度高 → 渲染成一片可见的白色汽团。
用户看到的"水蒸气"因此只在真正沸腾时出现。（没有这道闸时，水盆上方会一直冒白汽，
实测近地面最大湿度 0.72；有闸后降到 0.14，且紧贴水面的两层完全不显示。）
慢蒸发只在液面发生也顺手除掉了"水里冒泡"：水下的水不会凭空变成气体格（实测气泡 40 → ~13）。

**渲染**：湿度从 `VisibleThreshold`（0.06）淡入，到 `CloudFullHumidity`（0.16）为纯白。
满白量程**不能取 1.0**：天空里实际能达到的湿度只有 ~0.1~0.2，若按 1.0 归一，云永远渲成近乎透明——
"高空的云看不见"正是这么来的。

**气体不做随机漂移**：大气只按浮力与水平热压流移动，`ParticleRules.TryRandomDrift` 已删除。
那是数值噪声、不是风，而它搬运整格（含热量与湿度），会给云层注入逐 tick 的可见抖动
（实测可见度翻转 178 → 57 每帧，即用户看到的"水汽左右抖动"）；去掉后水循环反而更稳
（海水留存 214 → 284/900）。气体的混合由本场的扩散与温度定向平流负责——混合是场的行为。

**降水前先确认收得下**：空气在封闭气泡里饱和时（四周全是液/固），残余湿度无处安放；
此时不降雨，以免"降完一滴、残余困在水里"（固/液持有湿度 = 不变量违背，也是"水汽困在水里"的来源）。

**降雨半径是 3×3**：云层里每格湿度只有 ~0.1，四邻凑不满一个整水滴，只有湿度最高的顶行才下得了雨
（雨就显得从虚空里落下）；改用八邻后整层云都能下雨。

**守恒**：蒸发把 1 个水格变成 1 份湿度，降雨把 1 份湿度变成 1 个水格，输送只在格间搬运。
因此「水格数 + 湿度总量」在整个循环里不变（由 `WaterCycle_ConservesTotalWater` 与
`HumidityTransport_ConservesTotal` 守住）。相变走 `ParticleWorld.SetPhase`（携带热量 Q），
降雨走 `ParticleWorld.StepRain`。**潜热每单位水严格守恒且随温度线性**：蒸发（水 / 湿土干燥 / 沸腾）本格 `−LatentHeat(T)`，
`StepRain` 凝结成水格后本格 `+LatentHeat(T)`（凝结放热，加热云层）——相变点温度连续，不再蒸出虚假超热汽。

**为什么降雨要"碰并"**：雨滴由本格 + 气体邻居的水汽凑成。若要求单格先攒满一个整水滴，
天空里几乎达不到那个值（实测最大湿度只有 0.2~0.9），海洋会一路蒸干而永不下雨。
世界初始化时 `ParticleSimulationDriver.CreateWorld` 把所有空格填成大气粒子（`ParticleIds.Atmosphere`——不叫 `Air`，`Air` 已被 typeId 0 占用），风与对流因此有物质载体；typeId 0 只作为「抹除后留下的真空」短暂存在，气体规则同样允许换入。

> **液体横流的落点判据是 `CanDisplace`（能置换进去），不是 `IsAir`（typeId 0）**。
> 世界被大气粒子填满后不存在 typeId 0 的格子，用 `IsAir` 会让液体永远找不到落点而不再横流。
> 液体换进大气格 = 两者互换位置，大气被挤开后靠浮力上浮。（曾有回归，由
> `Water_LevelsOut_InAtmosphereFilledWorld` 锁定。）
>
> **水的横流不是无条件「见空格就跳」**：`WaterParticle.TryFlow`（类型内联扫描，
> 不用共享的 `TryFlowHorizontal`）每 tick 只横移 1 格，且必须过两道闸门之一——
> (a) **身后有推**：反侧同行邻格是液体（连片水体把多余的水送往两侧、让低于水面的洞被逐格填平，
> 摊平全靠它）；(b) **落差视野**：本侧 `MaxFlowSpread` 格内存在「正下方还能继续落
> （气体 / 空）」的格（往低处 / 洞口铺；正下方**越界不算**落差，否则底行水滴永不静止）。
> 两闸都不满足 = 往哪边流都不会更低（孤立水滴躺在平地 / 满水面上）→ **静止**。
> 旧实现让平地水滴以 60 Hz 在原地随机瞬移抖动，由 `Water_RestsAlone_OnFlatFloor` /
> `Water_RestsAlone_OnWorldBottomRow` 锁定回归。
>
> **已知几何限制**：水面「差一格」的态可能永久静止——该洞距任何一格水都超过 `MaxFlowSpread`、
> 且洞两侧的水都没触发「身后有推」时，远处的孤水看不到更低处。它与「孤立水滴静止」同源
> （都是视野不足时的静止），不是 bug；因此摊平测试的容器宽度与初始堆形是刻意选定的稳定场景
> （见 `WaterParticleTests.Water_LevelsOut_OnFlatFloor` 的 remarks）。

### 写操作的钩子与副作用（精确定义）

| 调用 | 目标格原为 | `OnKill` | 新格 `shade` | `OnSpawn` |
|---|---|---|---|---|
| `Set(x,y,T)` | 空气 | — | 按 T 的调色板**重新抽样** | T |
| `Set(x,y,T)` | 类型 U（U ≠ T） | `U.OnKill(x,y,T)` | 重新抽样 | T |
| `Set(x,y,T)` | 类型 T | 不触发，整格保持不变 | 不变 | 不触发 |
| `Clear(x,y)` | 类型 U（U ≠ 0） | `U.OnKill(x,y,0)` | — | — |
| `Clear(x,y)` | 空气 | — | — | — |
| `Swap(A,B)` | 任意 | **不触发** | **随 Cell 一起移动，不重新抽样** | 不触发 |

> `Swap` 除了交换两格 `Cell`，还会交换两格的**热量 Q 与湿度**（都由物质携带），并对换动的气体格结算绝热直减；`Set` / `Clear` 换上新材料时，该格 Q 重置为 `AmbientHeat`，残余湿度则**一律先经 `EvacuateHumidity` 推给四邻气体格**（无论目标是气体还是固 / 液，推不掉才留下）；`SetPhase` 保留 Q，湿度只对气体形态原样携带（换成固 / 液时同样先疏散）。详见「热力学」一节的表。

`Set` 的执行顺序（实现必须严格照此）：

1. 读受害者 `typeId`；
2. 若受害者非空气且 ≠ 新类型 → 调 `victim.OnKill(world, x, y, newTypeId)`。此刻网格上该格**仍是受害者本体**；
3. 写入新 `Cell`（`typeId = T`、`flags = 当前 parity`、`shade = 抽样`）；
4. 调 `T.OnSpawn(world, x, y)`。

因此 **`OnKill` 内对本格的写会被第 3 步覆盖**——残留物必须写到邻居格。

### 三条硬规则

1. **世界不做合法性判断。** `Swap` 不会阻止你把石头换走，`Set` 不会阻止你覆盖静态格。是否允许替换（比标签、查 `IsStatic`）由类型逻辑自己决定，这正是 `ParticleRegistry.Types[...]` 暴露在外面的原因。注意：共享原语 `ParticleRules.CanDisplace` / `CanFlowThrough` 为方便起见**内置**了静态格拒绝——它们服务常见情况；确实需要置换静态格的类型可以用裸 `Swap` 自行实现。
2. **前置判断在调用前完成。** 先用 `IsInBounds` / `IsAir` / `GetCell` 判断，再无条件调用写操作。写操作内部的越界检查只是安全网。
3. **移动之后立刻返回。** 任何会改变自身坐标的写操作（`Swap` / 覆盖自身的 `Set`）之后，`(x, y)` 与 `world.At(x,y)` 立刻失效。同一次 `OnMotion` 内禁止继续基于旧坐标操作。一个 `OnMotion` 只做一次决策 + 至多一次移动。

`Swap` 会把两个格的 bit0 都写成当前 parity，所以：

- 被换到已扫描位置的粒子本帧不再被处理（符合「每格每帧至多一次」）；
- 被换到尚未扫描位置的粒子也不会被二次处理（扫描循环先查 bit0）。

这条 bookkeeping 由世界负责，类型代码不能、也不需要手动维护。

### 步进之外写入网格

`Set` / `Clear` / `Swap` 在两次帧之间被调用时（例如鼠标绘制、关卡加载），bit0 会被写成**「下一帧的奇偶取反」**，也就是「未处理」。因此外部新建的粒子必定会在下一帧被处理，不会因为标记而白等一帧。同样由世界负责，类型代码无需关心。

### 就地改 `flags` 后的运动唤醒：`WakeMotion`

写入原语（`Set` / `SetPhase` / `Swap`）改格时会顺带把「周围读得到这格的格」并入运动休眠的候选位图；但类型用 `world.At(x,y).flags |= ...` 就地改 flags（不改 `typeId`）时**不会**触发这个唤醒。若这次改动会影响自己后面 `OnMotion` 的决策（例如树叶的失活标记决定它是否下落），必须再调一次 `world.WakeMotion(x, y)`：它把该格（及其唤醒盒内的邻居）并入**下一运动帧**的候选位图，语义 = 写入原语自带的唤醒那一半。

- **只需在「改动影响自己 `OnMotion`」时调**：只改与被覆盖后才读的状态（如树的生长阶段）不必调。
- **`WakeMotion` 不改格子内容**：不写 `typeId`、不重抽 `shade`、不触发 `OnSpawn` / `OnKill`、不动动量与处理标记，只置候选位。
- **休眠关闭（`MotionSleepEnabled = false`）时是空操作**——全网格旧路径本来每帧都会求值该格。
- 首选用例：`TreeLeafParticle` 在随机刻里标记失活后调 `WakeMotion`，否则运动休眠会把这张安静的叶子当静止跳过、它永远不落。

### 随机刻（Minecraft 式）

`world.RandomTicksPerStep` > 0 时，每个**逻辑帧**在**粒子扫描之后、热传导之前**（`StepLogic` 的 ①′ 相位）均匀**有放回**地抽这么多格，命中非空气、非静态的格就调用其类型的 `OnRandomTick`。它模拟 Minecraft 的 random tick：不做全网格扫描，而是每逻辑帧只让极少数格发生「慢过程」——生长、蔓延、渗流、腐化。

- **默认 0（关闭）**：代码默认关闭以保持既有测试契约（与 `HeatMap` 的 `RadiativeLoss` 同一取舍）；生产值由 `ParticleSimulationSettings._randomTicksPerStep` 注入（驱动每帧写回 `world.RandomTicksPerStep`），当前资产配 `48` ≈ Minecraft 的 3 格 / 4096 密度（320×180 ≈ 等效 42）。数量按**逻辑帧**计——逻辑帧 20 Hz 时等效密度是每秒 960 次抽取。
- **当前生产使用者**（`OnRandomTick` 非空）：草种子（八邻有湿土或腐殖土 → 发芽〔腐殖土为**决定性**条件时消耗脚下土层最深一格腐殖土家族〕；连续 3 次无条件空转 → 霉烂）、草（撒种 / 固沙 + 三条死法）、水（半径 2 冲刷成沙）、腐殖土（正上方是腐殖质时向邻近土壤扩张）、水生态种子 / 水生植物（吸水发芽 / 逐格生长）、**树干**（**繁殖**：成熟顶格每次命中按 **1/4** 概率在半径 **5** 的圆盘内撒一颗同物种树木种子——落点＝界内空地 + 正下方可托住种子的固体且非树木三件套；先判后摇，非顶格 / 未成熟零随机流消耗）。
- **与 `OnMotion` / `OnLogic` 相互独立**：随机刻**不检查、也不写** bit0 处理标记，所以同一格可以在同一次逻辑帧里被抽中多次；反过来，已被扫描处理过的格照样可能被随机刻命中。选择与格内容无关（先抽格、再看格），空气与 `IsStatic` 的格被抽中则跳过。
- **确定性**：抽取用世界同一条 xorshift32 随机流（`NextUInt`），因此同种子逐格可复现；随机刻的抽取会推进该流。
- **异常语义与扫描一致**：相位在 `_tick++` 之前、在同一个 `try` 内，`OnRandomTick` 抛异常时 `_tick` 不自增，②③④ 一并跳过（运动帧 `StepMotion` 只做扫描，异常同样使 `_tick` 不自增）。
- 赋值夹在 `[0, CellCount]`（超过格数无意义）。

```csharp
// 缓慢蔓延示例——不必每帧扫描，交给随机刻（ParticleIds.Moss 是假想类型，本项目尚未注册）
public void OnRandomTick(ParticleWorld world, int x, int y)
{
    if (world.NextUInt() % 50u == 0u && ParticleRules.HasTag(world, x, y + 1, ParticleTags.Liquid))
    {
        world.SetPhase(x, y, ParticleIds.Moss);
    }
}
```

> 反例：**不要**把「上方有水就吸水」写成随机刻。润湿是即时的局部反应，写在 `OnLogic` 里（见 §4「吸水与湿土的水迁移」）——否则整柱土要等许多逻辑帧才慢慢湿透，观感变成「渗水」而不是「水一泡就变色」。

---

## 7. 主循环语义

**两级固定时基**：`ParticleSimulationDriver`（MonoBehaviour）用同一个累加器推进**运动帧**（60 Hz 基本节拍），并把每第 `LogicTicksPerMotionTick` 个运动帧升级为**逻辑帧**。速度改 `ParticleSimulationSettings` 的 `_motionRate` / `_logicRate` 即可。

```mermaid
flowchart LR
    A[driver: 固定步长累加器<br/>stepSeconds = 1/MotionRate] --> Q{本帧是否为逻辑帧?<br/>每 round(MotionRate/LogicRate) 个运动帧一次}
    Q -- 否 --> M1[world.StepMotion]
    Q -- 是 --> L1[world.StepLogic]
    M1 --> M2[一次扫描: 只调 OnMotion]
    L1 --> B[扫描: 每格先 OnLogic<br/>未被相变换走的再 OnMotion]
    B --> B1[y 从高到低逐行 / x 方向按 Tick 奇偶交替]
    B1 --> B3{typeId==0 或 IsStatic?}
    B3 -- 是 --> B4[跳过]
    B3 -- 否 --> B5{本帧已处理?}
    B5 -- 是 --> B4
    B5 -- 否 --> B6[标记本帧已处理<br/>OnLogic -> 若未换类型再 OnMotion]
    B6 --> R[①′ 随机刻: 随机抽 RandomTicksPerStep 个格<br/>命中非空气非静态 -> 调 OnRandomTick]
    R --> C[② HeatMap.Step 全格四邻热传导 + 顶行向虚空散热]
    C --> C2[③ HumidityField.Step 湿度输送]
    C2 --> C3[③′ StepRain 降水]
    C3 --> D[④ LightController.Step 维护每列云量 + 向每列最上面的非气体粒子施热（按云量衰减）]
```

- **运动帧 `StepMotion`**：一次扫描，只对每个非空气、非静态且本帧未处理的格调用 `OnMotion`（移动）。**不跑**逻辑、随机刻、热、湿度、降水、光照。休眠开启（`MotionSleepEnabled`，默认）时只扫候选格，见上方「运动休眠」。
- **逻辑帧 `StepLogic`**：同一次扫描里每格**先 `OnLogic`**（相变 / 反应 / 邻域写入）——若它把本格换成了别的类型（`Set` / `SetPhase` 已把新格标成已处理），本帧**跳过该格的运动**；否则继续 `OnMotion`。休眠开启（默认）时扫描改走**混合扫描**：运动候选格跑完整 `OnLogic → OnMotion`，其余带 `Info.HasLogic` 的格只跑 `OnLogic`（`OnLogic` **永不休眠**，见下「逻辑帧混合扫描」）；休眠关闭时退回上述全网格路径。扫描后再依次跑 **①′ 随机刻**（抽 `RandomTicksPerStep` 个格调 `OnRandomTick`，见 §6「随机刻」，按**逻辑帧**计）→ **② 热传导 + 顶行散热**（热场已到不动点且输入未变时整趟跳过，见 §7）→ **③ 湿度输送** → **③′ 降水** → **④ 光照**。因此逻辑帧注入的热 / 湿度会在下一个逻辑帧被粒子读到、并参与下一轮扩散。
- 两个入口都推进 `world.Tick` 并交替扫描方向（`Parity`）与 bit0 标记，因此**运动帧才是世界的时基**；`Tick` 数 = 运动帧数。
- **固定时间步长**，与渲染帧率解耦：每真实帧跑几个运动帧由 `SpeedController` 按预算决定（默认手动 1×，**预算未截断时**与旧 `MaxStepsPerFrame` 固定累加器逐帧一致；见 §10「倍速」）。
- **暂停/恢复**：驱动只暴露状态与方法——`Pause()` / `Resume()`（幂等）与 `Simulate` / `Paused` 属性，**不含任何输入判断**（何时暂停由调用方决定）。暂停时整个推进分支跳过：不累加 `Time.deltaTime`、不跑 `StepMotion` / `StepLogic`、不上屏，因此**所有帧一起冻结**，恢复后也不会补跑暂停期间堆积的时间。热键（默认空格，`ParticleEditor._pauseKey`，设 `None` 关闭）写在 `ParticleEditor.Update`：按下即按当前状态调 `Pause()` 或 `Resume()`。暂停中仍可 `PresentNow()` 手动上屏（`ParticleEditor` 画完自动补一下）。
- 扫描顺序：**行永远从上到下**；**水平方向逐帧交替**（偶帧 x 升序、奇帧 x 降序）以消除重力/流动的方向偏置。交替由扫描循环内部完成，类型代码不感知。
- 空气格与 `IsStatic` 类型**直接跳过**，不进行为调用（随机刻抽取同样跳过它们；静态类型仍会收到 `OnSpawn` / `OnKill`）。

### 运动休眠（阶段 B）：StepMotion 只求值候选格

**目标**：纯 `StepMotion` 不再逐格求值静止粒子。逻辑帧仅在 `MotionSleepEnabled` 关闭时保持全网格；开启时走**逻辑帧混合扫描**（下节），对 `OnMotion` 半同样只求值候选格、对 `OnLogic` 半按 `HasLogic` 位图跳过空体——两条路径都与全网格逐位等价。

**机制**：候选位图 = 每 chunk（16×16）一块 `ulong[4]`（256 bit），扁平存一维数组。

- **唤醒源一（写入）**：`Set` / `SetPhase` / `Clear` / `Swap`（两个格都算）按类型表所有 `MotionReadReach` 盒**反转后的并集**置候选位（见 §4「运动阅读盒」）。并集按行表示为「行 dy 上关于写入格对称的连续 x 区间」，因此实现上是逐行按 64 位字 OR，而不是逐格置位：生产类型表共 **87 格**（`dy ∈ [−1,1]` 半宽 5、`dy ∈ [2,19]` 半宽 1），比「全体最大值的 Chebyshev 球」（41×41 = 1681 格）便宜两个数量级。
- **唤醒源二（温度）**：`HeatMap` 记录本逻辑阶段内 **Q 精确变化（Δ ≠ 0，无阈值）** 的格（传导 / 光照 / 潜热 / 降水；`Swap` 搬热走写入路径），`HumidityField` 记录**湿度精确变化**的格——<c>T = Q / C</c> 而 C 含湿度附加项，所以湿度变了温度就变了，这条通道不能漏。两张场各持一张**紧凑脏索引列表**（`int[]` + count，替代全表扫描），`StepLogic` 末尾据此把每个脏格**逐格置位半径 2 的 Chebyshev 唤醒球**（25 格；球 ⊇ 运动决策的实际温度读距，仍是安全超集），随后清空记录。数学依据：两次逻辑帧之间热场不变，被唤醒求值一次后若没有位移，后续运动帧跳过它与全扫描精确等价。
- **同帧级联**：写入时还会把被写格周围 R 内**本帧尚未扫到**的格并入本帧的候选位图——全扫描里扫描顺序在被写格之后的邻居会在同一帧用新状态决策，只写下一帧的位图会让它们晚一帧才看到改动（实测在落雨 + 水面场景约 37 个逻辑周期后出现水位差）。被写格自身不并入本帧（等价于「本帧已处理」跳过），下一帧照常重新求值。**扫描进行中的热 / 湿度变化走同一机制**（`HeatMap` / `HumidityField` 的 `DirtiedHook` → `MarkCandidatesThisScan`），否则被同帧注热的格会晚一帧决策，见下节「逻辑帧混合扫描」。
- **位图生命周期**：写入 / 热标记随时置位（休眠关闭时不置位，A/B 对照走的就是旧快路径）；`StepMotion` 开始时把「待消费」位图与「本帧扫描」位图互换、只读后者、扫描末尾清空后者。`StepLogic` 开启休眠时同样消费候选位图（见下节「逻辑帧混合扫描」）；关闭休眠时保持全网格、不动位图，其间的写入继续累积给下一个 `StepMotion`。
- **初态**：构造后整张位图置位（第一帧必须全扫描）；`MotionSleepEnabled` 从 false 切回 true 时同样整张置位。
- **扫描顺序不变**：仍然是 y 降序、x 随 `Tick` 奇偶交替，只对非候选格快速跳过（按 chunk 行 16 位判空整段跳过）。
- **「本帧已处理」的判据**：世界内部用一张逐扫描清零的位图（`ParticleWorld._processedBits`），而不是 `Cell.flags` 的 bit0——bit0 只有 1 位奇偶，休眠跳过若干帧的格会残留成与当前帧同奇偶的旧值，用它判定会漏求值（全扫描路径靠「每 tick 处理所有格」掩盖了这一点）。bit0 仍由写操作按旧语义维护（类型作者契约与既有测试依赖它）。
- **同帧级联必须在段内可见**：休眠扫描按 16 格一段读 64 位字做快路径，但**段内每格都要重新读那个字**——扫描途中的写入会往同一段补候选位（本帧级联，与全扫描里「扫描顺序在后的邻居立刻看到改动」等价），把字缓存在局部变量里会让新位永远看不到、级联晚一帧。
- **不变量**（任何类型的 `OnMotion` 都必须满足，否则休眠会与全扫描不等价）：① 单步位移 ≤ 1 格；② 除温度外不读任何「非写入即变」的状态（湿度、光照、`RandomTicksPerStep` 等）——温度由世界的温度唤醒覆盖；③ **不消耗世界随机流**（用 `HashNoise` 坐标哈希，见 §5「随机与坐标哈希」）。

```csharp
// 休眠开关（默认 true；false = 忽略位图、回退全网格旧路径，用于 A/B 对照）与观测计数
public bool MotionSleepEnabled { get; set; }
public int  MotionCellsEvaluatedLastStep { get; }   // 最近一次扫描实际调用 OnMotion 的格数（逻辑帧也消费运动候选、同样更新；休眠关闭时逻辑帧全扫不计数）
```

> **实测**（阶段 B 落地时记录，200×100、.NET 9 独立编译）：静止水池求值数直接降到 0；LogicBench 同款场景（热 / 光照开、1800 运动帧）sleep on/off ≈ **0.96–1.05×**（不再慢于全扫描）；预热后的倾泻场景 ≈ **0.71–0.85×**。最坏情况是「整块沙 + 水同时崩落」的冷启动瞬态（每帧约一半格子都在动、每动一格都要置 87 个候选位），≈ 1.45×——这是瞬态而非稳态。
>
> 记录：实现初期用「全体最大值的 Chebyshev 球」（41×41 = 1681 格/写）时，动态场景实测慢 12–33×；换成逐类型的反向阅读盒并集（87 格）并按行做 64 位字 OR 后才拿回收益。

- 扫描循环在**访问那一刻**从扁平数组读 `typeId` 再取委托，不做跨帧缓存。
- 单线程，主线程。不存在并行扫描。
- `StepMotion` / `StepLogic` 都用 `try/finally` 恢复写入标记：某个钩子抛异常时 `_tick` 不自增、`_stamp` 按「旧 tick」恢复，因此异常后新建的粒子仍会在下一帧被处理，不会白等一帧。

### 逻辑帧混合扫描（休眠开启时的 `StepLogic`）

**为什么需要**：逻辑帧同样要跑一次运动扫描，但它还带 `OnLogic`。生产表里大多数格（大气 / 沙 / 石头 ≈ 91%）`OnLogic` 是空体，全网格调用纯属浪费；而运动那一半自阶段 B 起已能休眠。把两者合起来，逻辑帧扫描同时做「运动候选休眠」与「`HasLogic` 位图跳过」。

**`OnLogic` 永不休眠**：与 `OnMotion` 不同，`OnLogic` 含**随机重掷型行为**（水蒸发摇骰、火熄灭档位骰等）——「输入没变就跳过」对它们不等价（满扫每帧重掷、休眠不重掷会改变速率）。因此逻辑帧对**每个** `HasLogic` 格仍每帧同序调用一次 `OnLogic`；休眠只作用于 `OnMotion` 那一半（其等价性由阶段 B 的证明保证：候选集 ⊇ 输入可能变化的格）。

**`_logicCells` 位图**：与候选位图**同布局**（每 chunk 16×16 一块 `ulong[4]`）的持久位图，每格 1 bit = 该格类型「非空气、非静态、`Info.HasLogic`」。维护点只有 `Set` / `SetPhase` / `Swap`（两端格随格子内容整体交换）；世界初建为空网格 ⇒ 全 0。与候选位图不同，它**不随扫描换出 / 清空**。

**扫描分派**（`Scan(bool logic)`）：

- 休眠关闭 ⇒ 全网格旧路径（逻辑帧逐格 `OnLogic → OnMotion`，逐位不变）。
- 休眠开启、运动帧 ⇒ `ScanSleeping`（只求值候选格）。
- 休眠开启、逻辑帧 ⇒ `ScanLogicSleeping`：扫描顺序与全网格**逐格一致**（y 降序、x 随 `Tick` 奇偶交替），按格分三种：
  1. **运动候选位置位** → 完整 `ProcessCell`（候选位可能被本帧更早的求值补上，故每格重读候选字）——计入 `MotionCellsEvaluatedLastStep`；
  2. **仅 `_logicCells` 置位** → `TakeProcessed` + flags 戳 + **只调 `OnLogic`**（不调 `OnMotion`、不计运动求值）；
  3. **都不置位** → 跳过。逐 chunk 行的快路径是 `候选字 | _logicCells字 == 0` 整段跳过。
- 位图生命周期与 `ScanSleeping` 相同（换出 `_candidates` ↔ `_scanCandidates`、`_motionScanActive = true` 维持同帧级联、末尾清空 `_scanCandidates`）。

**扫描中的热 / 湿度写入必须同帧可见**（`DirtiedHook`）：全网格旧路径里，先扫到的格 `OnLogic` 改了温度后，扫描序在其后的格**同帧**就在新温度下做 `OnMotion`（火 `OnLogic` 向本格注热 250Q，越过大气浮力的温度死区）。混合扫描若不即时唤醒这些格，就会晚一帧决策——只剩帧末 `FlushTemperatureDirty` 的逐格唤醒（下一帧才生效）。因此：

- `HeatMap` / `HumidityField` 各有一个 `internal Action<int> DirtiedHook`（默认 `null`），在 `MarkDirty(index)` 里回调；世界构造时绑到 `ParticleWorld.OnThermalCellDirtied`，由它算 `(x, y)` 后调 `MarkCandidatesThisScan(x, y, x, y)`——即**扫描进行中**（`_motionScanActive`）把该格并入**本帧**候选位图；非扫描期是空操作。唤醒盒即类型表反转阅读盒并集（生产表 87 格），⊇ 运动决策的温度读距（半径 2），是安全超集。
- 湿度改变热容 `C`、因而改变 `T = Q / C`，与热变化是同一条「温度输入变了」的通道，同样走这个 hook。
- **后置路径不动**：热 / 湿度 pass、光照、降水（都在扫描之后）里的标脏仍由帧末 `FlushTemperatureDirty` 的逐格半径 2 唤醒统一处理；**Burst 传导内核不经过 `MarkDirty`**（直接写 `dirty[]`、把 0→1 新脏格登记进紧凑索引列表），因此那里不会回调托管委托。
- `Swap` 的绝热 Q 调整已由 `Swap` 自身的写入唤醒（`MarkCandidatesThisScan`）覆盖，无需额外处理。

**等价性依据**：`OnLogic` 的调用集合与顺序与全网格完全一致（同为「所有 `HasLogic` 格、非本帧已处理、按扫描序」），所以随机流消耗序列不变；`OnMotion` 只对候选格调用，候选集是「输入可能变化」的超集（写入唤醒 + 扫描中热/湿度唤醒 + 帧末温度脏唤醒），由阶段 B 已证明等价。因此混合扫描与全网格**逐位一致**。

**RNG 路径无改动**：本特性不新增 / 不改变任何随机数消耗点——它只是「跳过空 `OnLogic`」与「对静止格跳过 `OnMotion`」两个逐位中性的省略。

**验收**：`LogicScanTests`（活跃场景 300 周期 sleep on/off 逐位透明、`HasLogic` 契约、休眠开时蒸发仍发生、**火注热的同帧可见性回归**）。另有一个 320×180 生产形态的逐 tick 状态哈希对照（点火 tick 起 360 周期，与 HEAD 旧全扫代码 900 tick 逐行全等）——`HeatMap` 的记脏回调就是为这个发散点补的。

### 世界层场 pass 的实现（阶段 C 起 Burst；阶段 D 实测否决并行，保持串行直调）

**目标**：把两个 O(W·H) 的浮点遍历——`HeatMap.Step`（四邻传导 + 顶行虚空散热 + 暴露面辐射）与 `HumidityField.Step`（扩散 + 水平冷→热 + 竖直越热越向上）——交给 Burst 编译执行；**降水 / 光照 / 随机刻 / 粒子类型代码保持 managed（类型代码零改动）**。

**实现**：

- `Packages/manifest.json` 直接依赖 `com.unity.burst`（**1.8.30**，Unity 2022.3 兼容的 1.8.x 线）；`TapTap2026.Simulation.asmdef` 引用 `Unity.Burst` 并开启 `allowUnsafeCode`（内核签名要指针）。
- **按 typeId 预生成的扁平参数表**（世界构造时一次）：热容 `_capacityByType`、导热率 `_conductivityByType`、辐射率 `_emissivityByType`、天空暴露 `_skyExposedByType`、湿度载体 `_moistAirByType`。热路径不再逐格回调 `ParticleType.Info`（既更快，也是 Burst 只认 blittable 数据的前提）。空气（typeId 0）与空槽项在每次 `Step` 前用**可变属性**（`AirThermalConductivity` / `AirThermalEmissivity`）刷新，`id >= types.Length` 的越界 id 仍用标量空气值，与原 `CapacityOf` / `ConductivityOf` / `EmissivityOf` 语义逐格一致。
- **热传导的循环体已抽成两个 per-cell 静态方法**：`HeatStepParamsCell`（阶段 ①：逐格填热容倒数 `1/C`、σ、ε）与 `HeatStepConductCell`（阶段 ②：四邻传导 + 暴露面辐射 + 顶行虚空散热 + 脏标记）。`HeatStepKernel` 顺序调它们，net9 与 Unity 共用同一份单格体（阶段 D 抽取，位哈希证明逐位中性）。
- **阶段 D 测量结论：热传导刻意保持串行直调，不做 `IJobParallelFor`**。隔离诊断（Burst on，200×100）实测热 pass 全量仅 **≈0.19~0.26 ms/帧**（占逻辑帧的 1.5~3%），job 化后 workers=31 与 workers=0 **无可分辨差异**——313 个调度批次的调度 / 同步开销吃掉了全部并行收益；网格规模远未到并行甜蜜点。结论：保持 `Step` → 直调 `HeatStepKernel` 的单线程 Burst 路径（若将来网格放大到热 pass 成为瓶颈量级，再重估）。
- **湿度为何保持串行（阶段 D 未动 `HumidityField`）**：`Transfer` 对给出方的 `budget[a]` 做**读改写**（额度随本步内已发生的转移逐笔扣减），接收方 `next[b]` 又由多来源累加——典型 scatter + 就地松弛（Gauss-Seidel 式）。pair 的处理顺序（y 升序 × x 随 `Parity` 交替、每格先右对后上对）**就是语义本身**：`budget` 的演化依赖前序 pair 的结果，浮点累加顺序同样影响位级轨迹。任何并行重排（行带 / 棋盘 / 相位着色）都会改变逐位结果，所以 `HumidityField.Step` 仍在主线程直调 `HumidityStepKernel`（同样 `FloatMode.Strict`），不并行化。
- 核心循环是 `[BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)] static unsafe` 内核（`HeatMap.HeatStepKernel` / `HumidityField.HumidityStepKernel`），签名只含 `float*` / `byte*` / `Cell*` / 标量。**宿主直接调用内核方法**——Burst 的 ILPP 把直调重写成 DirectCall 分发（编辑器 JIT / 构建 AOT 自动选路，Burst 关闭时回落到同一方法体的 managed 版本），宿主把数组 `fixed` 住再传指针，零拷贝、零分配。
- **类级 `[BurstCompile]` 是 ILPP 的发现门**：Burst 的 IL 后处理器只进入「类型本身带 `[BurstCompile]`」的类，所以 `HeatMap` / `HumidityField` 两个类都标了类级特性；真正被编译的仍只有带方法级特性的内核方法。方法级 `[BurstCompile]` 的 `#if UNITY_2022_3_OR_NEWER` 包裹同理（net9 独立编译时属性不存在）。
- `obj/LogicBench`（net9 独立编译，**注意别放 `Temp/`——Unity 启动会清空 Temp**）里 `UNITY_2022_3_OR_NEWER` 未定义、属性被剥掉，同一个内核方法直接以 managed 执行——两条路径共用一份源码（含抽出的 `HeatStepParamsCell` / `HeatStepConductCell` 单格体），不存在第二份实现，因此「编辑器 Burst / 独立编译 managed」可直接逐位对照。

**FloatMode.Strict 与确定性条款**：

- 内核显式 `FloatMode.Strict`——禁用 FMA 收缩与浮点重排。内核里的算式、四邻顺序、双缓冲、暴露面判据**逐句照搬**原 managed 实现，所以 Burst 与 managed 分支逐位一致。Burst 1.8 的 `FloatMode.Default` 恰好也是 `Strict`，这里**显式写死**，避免将来默认值变化悄悄破坏确定性契约。
- `CompileSynchronously = true`：首次调用即编译完成，杜绝「前几帧 managed、之后 Burst」的混合执行——同一段模拟不能中途换浮点实现。
- **确定性契约不变**：同种子同命令序列逐格可复现；Burst（编辑器 / player）与 managed 分支的结果逐位一致，因此确定性重放测试在任何分支下都成立。
- **历史问题与最终方案**：最初用 `BurstCompiler.CompileFunctionPointer` 拿函数指针调用，但本机环境里 Burst 的「编辑器运行时 entry point 名单」始终为空（连一个朴素的 `[BurstCompile] static float Add(float,float)` 都报 `not a known Burst entry point`），`CompileFunctionPointer` 静默回落 managed。根因是该路径依赖编辑器侧的运行时发现，而 DirectCall（ILPP 在编译期改写直调）把入口注册烘焙进 IL、不依赖运行时发现。改为「类级 `[BurstCompile]` + 直调内核」后验证通过：ILPP 产物里出现 `$BurstManaged`/`$BurstDirectCall` 标记、entry point 报错归零、`Library/BurstCache/JIT` 产出新库、200/200 测试在 Burst 编译的内核下全绿（零断言失败）。**教训：内核调用一律直调（DirectCall），不要用 `CompileFunctionPointer`。** 阶段 D 曾把热传导改成 `IJobParallelFor`（已实测否决并回退，原因见上文测量结论），最终确认「类级发现门 + 直调内核」仍是本项目的正确路径。

**脏标记语义（与阶段 B 逐位一致）**：

- Q / 湿度的脏缓冲是 `byte[]`（`1` = 脏、`0` = 干净）——Burst 内核要求扁平 blittable 缓冲，公开属性 `HeatMap.Dirty` / `HumidityField.Dirty` 随之从 `bool[]` 改为 `byte[]`（语义不变：非 0 即脏，保留为只读成员关系视图）；两张场另各持一张 `int[]` **紧凑脏索引列表**（+ count）作为消费路径。
- 内核按**精确 Δ ≠ 0**（无阈值）就地置位、把每个 0→1 新脏格登记进索引列表，只返回「本趟是否有变化」；宿主自行把它 OR 进 `_anyDirty`（记账与旧 `dirtyAny` 逐位相同），不动点判定看本趟是否恒等。
- `HumidityField` 的 `_touched` 短路（本帧无任何流动就不扫脏格）内联成内核局部变量，全零湿度场不产生任何脏格扫描。
- 消费方：`ParticleWorld.StepLogic` 末尾的 `FlushTemperatureDirty()` 遍历两张紧凑索引列表、把每个脏格的半径 2 Chebyshev 球逐格并入候选位图（见下），随后 `ClearDirty()`（只清登记过的索引，O(脏格数)，不再整表扫描）。

**热传导不动点跳过（fixpoint skip）**：

- `HeatMap.Step` 入口先判「整趟跳过」：若**上一趟是恒等**（该趟无任一格 Q 变化），且此后所有输入都没变——外部未改过 Q（`_qVersion`）、世界的格子写入版本（`CellsVersion`）与湿度场版本（`HumidityField.Version`）未变、可变热学参数版本未变——则直接返回，**不交换双缓冲、不动 dirty**。`Swap` 只在两端 `typeId` 不同时才推进格子写入版本：同类型交换是 typeId 数组上的恒等置换，而 Q（`SwapHeat`）与湿度各有独立版本号、仍按「值真的变了才自增」兜底——因此「静止但内部有同类型自流」的场不再被打断（审计 P1.1）。
- 数学依据：`Step` 是 `(Q, 湿度, cells, 参数)` 的确定性函数。上一趟输入与输出相同（恒等），且此后输入全未变，故再跑一遍得到同一个输出——重算与跳过**逐位等价**（`HeatSkipTests` 用「跳过开 / 关」两条路径跑到收敛后逐位对照验证）。
- **为什么必须用「上一趟恒等」兜底、而不能只看 `_anyDirty`**：`_anyDirty` 由世界在每逻辑帧末尾消费并清空，它只说明「本帧内有过变化」，无法回答「自上次热 pass 以来 Q 变没变」。光照 / 降水 / 潜热都在热 pass **之后**才注热，所以另设只增不减的 `_qVersion`（`SetHeat` / `AddHeat` / `SwapHeat` 每次真正改 Q 时自增）来回答后一个问题；否则会把帧末刚注入的热误判为「无变化」而跳过、任其逐帧累积。
- **跳过只发生在无注入且已收敛的静止期**：持续照亮时每帧都在 `AddHeat → MarkDirty`，注入本身即破跳过（与热场是否平衡无关）——这是**语义而非缺陷**。注意精确不动点收敛极慢（16×16 全空气场上千逻辑刻、生产尺寸上万逻辑刻），而 `LightController` 昼夜暗相只有数百刻，**暗相吃不到跳过收益**；收益场景是「完全静止且早已收敛」的场（审计 P1 实测）。
- 构造后**首帧必真跑**（不给 `_lastStepIsIdentity` 预置初值）；`FixpointSkipEnabled`（默认 true）可关掉跳过做 A/B 逐位对照；`LastStepSkipped` 暴露「本趟是否跳过」供测试 / 诊断。

**逐格半径 2 的温度脏唤醒（紧凑脏索引列表）**：

- `FlushTemperatureDirty` 遍历两张场的紧凑脏索引列表（`int[]` + count，内核在 `dirty[]` 0→1 时登记），对每个脏格把半径 2 的 Chebyshev 球（25 格）**逐格**置入候选位图。球 ⊇ 运动决策的实际温度读距（`TryThermalDrift` 的 `x ± 2`、`TryLiquidConvect` / `TryBuoyantStep` 的四邻），候选集仍是「输入可能变化的格」的超集，多求值一些输入未变的格不改变任何模拟结果（与运动休眠「超集即安全」同一依据）。相比旧的「按 16×16 chunk 整块置位」，密集脏区不再把候选位图刷到大半置位（「整 chunk 全 0」快路径几乎不命中、混合扫描反而比全扫慢，审计 P2 实测 0.61×~0.80×）；紧凑列表同时消掉了两张脏字节表的 O(W·H) 全表扫描。

---

## 8. 写 `OnMotion` / `OnLogic` 的推荐形态

**运动帧只写 `OnMotion`**（移动，一次决策 + 至多一次 `Swap` 后立即返回）：

```csharp
public void OnMotion(ParticleWorld world, int x, int y)
{
    // 1) 读自己的数据（需要时缓存 ParticleType 到局部变量）
    ParticleType self = ParticleRegistry.Types[world.At(x, y).typeId];

    // 2) 先查后动：所有判断都在这里完成
    int ny = y - 1;
    if (!world.IsInBounds(x, ny)) return;            // 越界 = 空气，是否允许流出网格由类型自己定
    if (world.IsAir(x, ny)) { world.Swap(x, y, x, ny); return; }   // 3) 无条件执行 + 立即返回

    // 4) 与邻居的类型交互：通用规则只看标签，不看具体类型
    ushort otherId = world.GetCell(x, ny).typeId;
    if (otherId == ParticleRegistry.AirTypeId) return;
    ParticleType other = ParticleRegistry.Types[otherId];
    if (other.Info.IsStatic) return;
    if (ParticleRules.TagRank(other.Info.Tags) >= ParticleRules.TagRank(self.Info.Tags)) return;

    world.Swap(x, y, x, ny);
}
```

**逻辑帧写 `OnLogic`**（相变 / 反应 / 邻域写入，**不移动**）：把上面样例里的换格 / 写邻居挪进 `OnLogic` 即可；跑完 `OnLogic` 后世界会自动对未被相变换走的格再跑 `OnMotion`，两者共享同一份 bit0 标记。**已定稿的唯一例外**：`FireParticle` 的游离火在 `OnLogic` 末尾 `Swap` 上升、`OnMotion` 留空（见 §4「火」）。

```csharp
public void OnLogic(ParticleWorld world, int x, int y)
{
    // 只做相变 / 反应 / 把物质写给邻居；不要 Swap 自己
    // （FireParticle 的游离火上升是唯一的已定稿例外，见 §4「火」）。
    if (world.Heat.GetTemperature(x, y) >= BoilingPoint)
    {
        world.SetPhase(x, y, ParticleIds.Atmosphere); // 换成别的类型：本帧运动被跳过
    }
}
```

约定：

- 随机一律用世界随机流 `world.NextUInt/NextInt/NextByte`（**禁止 `UnityEngine.Random`**，会破坏确定性与可复现回放）；**例外**：`OnMotion`（含它调用的 helper）**禁止消耗世界流**，方向等随机决策一律用 `HashNoise` 坐标哈希——见 §7「随机与坐标哈希」。
- 禁止 `Time.deltaTime` / `Time.time`：时间语义只有 `world.Tick`。
- 禁止在 `OnMotion` / `OnLogic` 里 `new`、装箱、捕获局部变量的 lambda、LINQ、字符串拼接。
- 重复的移动规则（下落、斜落、横流）应抽成**静态无状态帮助方法**（例如 `ParticleRules.*`），而不是继承基类；帮助方法同样只在参数里传 `world`/坐标，不持有每格状态。
- 标签、最大横流格数等属性从 `ParticleRegistry.Types[id].Info` 读；**这些值在运行期不可变**，任何类型都不得修改注册表条目。
- **跨类型判断一律走标签**（`ParticleRules.TagRank` / `TagsAt` / `IsSolid`），不要写 `if (other.Info.DebugName == "Stone")` 这类特判——那会毁掉「新增类型零改动」的性质。

---

## 9. 渲染契约

### 与模拟帧解耦（重点）

**上屏时机是一条明确的接缝，不写在模拟里。** 分层如下：

| 层 | 职责 | 不知道什么 |
|---|---|---|
| `ParticleWorld.StepMotion()` / `StepLogic()` | 只推进模拟、只改 `Cell[]` | **完全不知道渲染存在**（`Simulation` 程序集不引用任何渲染对象） |
| `ParticlePixelBuffer.Fill()` | `Cell[]` → `Color32[]` 的纯数据映射 | 不知道纹理、材质、相机 |
| `ParticleRenderer.Present()` | 把已有缓冲 `SetPixels32` + `Apply` 上传一次 | 不知道跑了几步、谁是 tick |
| `ParticleSimulationDriver` | 两级固定步长推进（运动 60 Hz / 逻辑 20 Hz）+ **唯一决定上屏时机的地方** | —— |

驱动侧的循环（暂停时整段跳过——不累加时间、不推进、不上屏；暂停键由 `ParticleEditor` 处理）：

```csharp
if (_simulate)   // Pause()/Resume() 翻转它
{
    _motionTicks++;
    if (_motionTicks >= logicEvery) { _motionTicks = 0; _world.StepLogic(); }
    else                            { _world.StepMotion(); }
    _stepsSincePresent++;
    if (_stepsSincePresent >= settings.StepsPerPresent) Present();   // 默认 StepsPerPresent = 1
}
```

- **默认 `StepsPerPresent = 1`：每个运动帧后调用一次渲染。**
- 调大即可变成「多个运动帧渲染一次」（例如 4 = 4 个运动帧只上传一次），**不需要改动模拟或渲染任何代码**。
- 反过来拖慢运动帧速率、或某一帧累加器没攒够一步（`steps == 0`）时，本帧不做任何上传——画面停留在上一次的结果。
- `ParticleRenderer` 是可选引用；未指定时驱动照常跑模拟，只是不上屏。

> 注意这是**上屏时机**，不是**模拟正确性**：两个 `Step*` 之间没有隐式上屏，多步之间也不会有中间态泄漏到屏幕。

### 渲染实现

- **不创建任何 per-particle 的 GameObject / SpriteRenderer / Transform。** 整个网格只有 1 个渲染对象。
- 像素缓冲 `Color32[] _pixels`，长度 = `world.CellCount`，填充逻辑集中在 `ParticlePixelBuffer.Fill(world, pixels, background)`：
  - `pixels[i] = world.Types[cells[i].typeId].Info.Palette[cells[i].shade]`
  - 空气、未注册 id、`null` 槽位一律写背景色；`shade` 越界时回退 `palette[0]`。
  - 这个方法是纯函数、无引擎依赖，因此**可以在 EditMode 测试里直接断言**，不需要 GPU。
- **昼夜就在这一步表现**：`ParticleRenderer.Present` 按 `world.Light.Daylight` 算出天空色与亮度——
  `background = Color32.Lerp(NightBackground, DayBackground, daylight)`、
  `brightness = Mathf.Lerp(NightBrightness, 1, daylight)`，再调 4 参重载
  `ParticlePixelBuffer.Fill(world, _pixels, background, brightness)`。**亮度只作用于粒子与云**，
  天空走配置的颜色（默认 `#2A223A` / `#CCE5FF`）。纯映射、可脱离 GPU 断言；
  `NightBrightness = 1` 且昼夜色相同即等价于「不做昼夜表现」。
- **描边（可选表现层标志）**：`ParticleTypeInfo.HasOutline`（构造参数 `hasOutline`，默认 `false`）为 `true` 的类型，
  在 `Fill` 里做四邻判定——任一邻居「显示为背景」（空气 / 未注册 / `null` 槽位 / 调色板 alpha==0 的透明类型；
  **越界视同空气**，网格边缘的暴露面也描边）**或带 Liquid 标签**（固液交界也描边）时，
  本格基底色乘以 `ParticlePixelBuffer.OutlineFactor`（0.55）调暗。
  只作用于基底色：云 / 热力叠加 / 昼夜变暗照常在其上进行（描边格仍算 `painted`）。纯 CPU 映射、零 GPU 增量，
  不参与模拟、不影响确定性。当前启用：**石头 / 沙 / 土**。
- **液面高光（可选表现层标志）**：`ParticleTypeInfo.HasSurfaceHighlight`（构造参数 `hasSurfaceHighlight`，默认 `false`）
  为 `true` 的类型，`Fill` 里本格**正上方无实物**（非 Solid 非 Liquid——空气 / 气体算液面，被固体或液体压住不算；
  越界顶行算）时，基底色向 `ParticlePixelBuffer.SurfaceHighlightColor`（浅蓝白 `#C8E4F5`）按
  `SurfaceHighlightFactor`（0.6）插值。与描边同一轮判定、同样只作用于基底色。当前启用：**水**。
- 目标对象：**1 个 quad**。运行时用代码建一个 4 顶点 mesh（不是 `GameObject.CreatePrimitive`，避免附带 Collider），挂在子物体上，顶点范围就是 `(0,0)`–`(Width, Height)` 世界单位，1 cell = 1 unit。
- 材质：优先 `Universal Render Pipeline/2D/Sprite-Unlit-Default`，回退 `Sprites/Default`；都找不到时报错并**停止上屏**（不再每帧刷日志）。可在 Inspector 覆盖。**发布包要注意 shader 剥离**，届时把该 shader 加入 Always Included Shaders 或直接指定材质资产。
  - Inspector 指定的 `_material` 是共享资产：运行期一律 `new Material(_material)` 取副本，**绝不改写资产本身**；销毁/重建纹理前先把 `mainTexture` 置 null，避免材质持有 Missing 引用。
  - 「渲染目标就绪」= 纹理尺寸匹配 **且** mesh 与材质都已挂上。shader 缺失导致首次失败后不会每帧重试；一旦补上 `_material`，下一次 `Present` 会自动重建并恢复上屏。
- 纹理：`new Texture2D(Width, Height, TextureFormat.RGBA32, mipChain: false, linear: false)`，`filterMode = FilterMode.Point`（必须），`wrapMode = TextureWrapMode.Clamp`，`hideFlags = DontSave`。
- 上传：一次 `SetPixels32` + `Apply(false, false)`。世界尺寸变化时才重建纹理与 mesh。
- 相机：`orthographicSize = Height / 2`，位置 `(Width / 2, Height / 2, -10)`（可关）。相机移动**不触碰 `Cell[]`、不重建纹理**。
- 相机平移：`ParticleRenderer.PanCamera(world, deltaCells)` 把相机沿网格水平方向推 `deltaCells` 格（正 = +x），并**把视口夹回沙箱内**——相机中心 x 恒在 `[半视宽, Width − 半视宽]`（半视宽 = `orthographicSize × aspect`）；视口比沙箱宽时**恒居中**、平移无效。只改 x：竖直框取（`orthographicSize` 与位置 y / z）不动。`deltaCells = 0` 也被调用（编辑器每帧都调一次），因此 Game 视图宽高比变化把原本界内的相机挤出界外时，下一次调用即把它夹回沙箱内；非正交相机不参与（半视宽不是常数），此时什么都不做。按键 → 格数在 `ParticleEditor`（见 §10），位置与夹取都在渲染器——收在同一处，相机只有一个写入者。
- 后续优化（不改数据结构）：只对相机可见矩形调用带 `Rect` 的 `SetPixels32` 重载 + `Apply`，CPU 侧仍是同一块全尺寸缓冲。

---

## 10. 文件清单（已落地）

```
Assets/Scripts/
  TapTap2026.Game.asmdef                      ← 渲染层 + 驱动层
  Simulation/
    TapTap2026.Simulation.asmdef              ← 纯模拟，无渲染引用
    Cell.cs                                   Cell 结构体（唯一真源）
    CellFlags.cs                              flag 位常量 + 读写帮助
    IParticleType.cs                          IParticleType + 五个委托类型（运动/逻辑/随机刻/生成/死亡）
    ParticleTags.cs                           类别标签 Solid / Liquid / Gas
    ParticleTypeInfo.cs                       类型属性结构体（含 HasLogic 能力标志：OnLogic 是否为空体，逻辑帧混合扫描据此跳过）
    ParticleType.cs                           注册表条目（Id + Info + 五委托）
    ParticleRegistry.cs                       全局静态注册表 + Register 校验
    ParticleIds.cs                            类型 id 静态字段（注册时回填）
    ParticleRegistration.cs                   唯一类型清单（新增类型只改这里；顺序只能追加）
    ParticleWorld.cs                          扁平数组、两级入口（StepMotion / StepLogic）、扫描（休眠开启时运动帧 ScanSleeping、逻辑帧 ScanLogicSleeping 混合扫描 + 持久 _logicCells 位图）、读写原语、RNG、随机刻抽取
    HeatMap.cs                                与网格同形的热量场 Q（float/格）+ 每逻辑帧四邻热传导；T = Q / C；Swap 对气体竖直位移结算绝热直减 AdiabaticLapse；Step 热传导由直调内核 HeatStepKernel 串行执行（FloatMode.Strict；循环体已抽成 HeatStepParamsCell / HeatStepConductCell 单格体，job 化已实测否决——热 pass 仅 ~0.2ms/帧，调度开销吃掉收益）；**不动点跳过**（上一趟恒等且 Q/格子/湿度/参数版本全未变则整趟跳过，逐位等价；FixpointSkipEnabled 可关掉做 A/B，LastStepSkipped 诊断）；按 typeId 预生成热容/导热率/辐射率/天空暴露参数表；`DirtiedHook`（`internal Action<int>`）在 `MarkDirty` 时回调，让世界把扫描中的热变化同帧并入候选位图（Burst 内核直接写 `dirty[]`，不经此路径）
    HumidityField.cs                          与网格同形的湿度场（float/格）：扩散 + 水平冷→热 + 竖直越热越向上（RiseStopHumidity 只门控平流项、x 扫描按 Parity 交替）；云 = 湿度高的空气；蒸发的唯一入口 SpreadUnitIntoAir（5 参含 maxShare 份额门控）与饱和判据 IsAirSaturated / SaturationHumidity（降雨用指数饱和曲线）；潜热 LatentHeat(T) = LatentHeatPerDegree × T（5.5/度，20° 锚点 = 110）；Step 核心循环在 Burst（HumidityStepKernel，FloatMode.Strict，`_touched` 短路内联；**保持串行**——budget 读改写 + pair 顺序即语义，阶段 D 未并行化）；`DirtiedHook`（`internal Action<int>`）在 `MarkDirty` 时回调，让世界把扫描中的湿度变化同帧并入候选位图（湿度改热容 ⇒ 改温度）
    LightController.cs                        光照控制器：按时序通断，向每列最上面的非气体粒子（固体或液体）注入热量（ApplyHeat 自顶向下边走边累加光路湿度、遇 BlocksLight 即停，按光路湿度做 Beer–Lambert 衰减；GetCloudThickness 按需扫描）；另导出 Daylight 供渲染层做昼夜明暗
    ParticleRules.cs                          无状态共享移动规则（含标签原语 TagRank / TagsAt / HasTag / IsSolid / BlocksLight）——移动规则被 OnMotion 调用，BlocksLight 另被 LightController 的光路扫描调用
    HashNoise.cs                              (x, y, tick) 坐标哈希（Hash32 / Bit）：OnMotion 路径的随机决策源，不消耗世界随机流（Hash32 即火焰 v2 哈希原样提取）
    MotionReadBox.cs                          类型的运动阅读盒 (X, Up, Down)：OnMotion 对 _cells 的轴对齐读距，世界求反转并集作为运动休眠的写入唤醒集
    TreeSpecies.cs                           树木物种静态表（五物种 杨/橡/松/柏/柳 = flags bit3-5 编号，只能追加）：H 树干高 / N 生长分母 / D 发芽分母 / 基质档（Drought 任意三土；Moist 三土且八邻有湿土或腐殖土；Wet 正下方湿土或腐殖土）/ 树冠有序偏移表（数组顺序＝生长顺序；**表自支撑**——除首项外每项与树干列或更早项 Chebyshev 相邻，生长守卫据此防浮空叶）/ 支撑半径 / 色片色数；含物种位读写 helper `Read` / `Stamp`（Stamp 在 Set/SetPhase 之后调用：重写 bit3-5 并把世界已抽样的 shade 折进本物种色片，零额外随机流；编辑器与粒子共用）
    ParticlePixelBuffer.cs                    Cell[] → Color32[] 的纯映射（调色板颜色 alpha == 0 → 显示背景色，透明粒子契约；热力叠加在其上）
    SpeedController.cs                        倍速控制器（纯 C#，不引用 UnityEngine）：手动档 ×2/÷2 调档（[1,16]，也可由 `_speedMax` 封顶）+ 自适应档 AIMD（平滑「每 tick 耗时」EMA × 驱动注入的周期长度（`LogicTicksPerMotionTick`）× min(需求, cap) 折算周期成本——需求超预算时成本饱和、必触发降档；0.75/0.95 双水位线死区 + 每 10 帧一次评估的门控 + 滞回），每真实帧给出「本帧跑几个运动 tick」；超预算丢账不追账、预算上限保底 1 tick；另导出实测倍率 `EffectiveSpeed`（分子分母分别 EMA 再相除，稳态 1× 归一）供 HUD 显示
    Particles/
      StoneParticle.cs                        石头（摩擦度 0.9 / 高粘性 0.9 ⇒ 靠着石头时 19:1，近乎垂直的石垛）
      SandParticle.cs                         沙（固体标签，摩擦度 0 / 粘性 0 ⇒ 1:1 安息角三角堆）
      SoilParticle.cs                         土壤（固体标签，摩擦度 0.5 / 粘性 0.5 ⇒ 约 3:1 安息角；**正上方是水就喝掉那格水并变成湿土**）
      WetSoilParticle.cs                      湿土（固体标签，摩擦度 / 粘性沿用干土 ⇒ 同款堆形；密度 2.0、热容 2.0、导热 0.85 都比干土高，颜色更深。湿土 = 干土 + 一份水：受热上行（50% 概率门 + 潜热 −LatentHeat(T)）/ 常温（T < 60）**有压才渗**——上方是水或湿土才 50% 下渗（正下方优先、被挡随机序试左右干土），孤立湿土永久持水（田间持水量，湿斑不沉底）/ 上方是空气且受热就蒸发；水带热 T(源)×1.2 一起走；给出水后自己变回干土）
      WaterParticle.cs                        水（液体：下落 → 斜滑 → 带闸门的横流（每 tick 1 格：身后有液体推 或 落差视野 MaxFlowSpread=5 格内有更低处才流，否则静止——平地/水面上的零散水滴不抖动）→ 都走不通时液体热对流 TryLiquidConvect（比正上方的水暖 LiquidConvectionEpsilon=0.5 就上浮一格——白天顶加热是稳定分层不翻转，夜晚表冷底暖时深处暖水上浮把白天存的热带回表面 = 海洋翻转，海面夜间比陆地暖、陆风涌现的机制），无安息角；T ≥ 沸点 80 剧烈蒸发（水汽集中 3×3 → 可见），T ≥ 20 常温慢蒸发（水汽摊到半径 4 的 9×9 = 81 格，受 InvisibleSpreadCap 与 InvisibleSpreadMaxShare 双重节流 → 接收格 ≥ 25 才不可见；只在液面，顶行不蒸发）；两条蒸发路本格 −110 潜热；随机刻：冲刷——找到半径 2 格圆盘内的第一个土壤/湿土/腐殖土后以 20% 概率 SetPhase 成沙）
      AtmosphereParticle.cs                   大气（气体：空气粒子化，浮力 + 热压流 = 海陆风的载体；flags bit3-4 = 水平热压漂移的方向记忆 CellFlags.DriftDirectionMask；世界初始化时由 driver 填满空格；调色板全透明 = 隐形天空，热力叠加时可当风的可视化层）
      GrassSeedParticle.cs                    草种子（固体粉末，摩擦 / 粘性 0.5；flags bit5-6 = 无发芽条件的随机刻命中计数。随机刻命中且八邻有湿土或腐殖土时，把自己与上方 1-3 格随机高度替换为草，遇固 / 液即停（腐殖土为**决定性**条件时，发芽成功额外消耗脚下土层最深一格腐殖土家族 → 普通土）；发芽后 **30%**（3/10）概率把**株顶那格**草换成花；连续 3 次命中都无发芽条件则直接变成腐殖质。不可在编辑器中选择，只能单颗投放）
      GrassParticle.cs                        草（固体 + Organic，非 IsStatic：由草种子生长生成；空的 OnMotion 使其长在原地不落、不流。**逻辑帧 = 衰老**：年龄 = Cell.shade × TicksPerShadeStep(5) + flags bit3-5 的余数，每逻辑帧 +1、每 5 帧把 shade 推进一档（shade 即颜色档，调色板由 YoungColor→OldColor 渐变 ⇒ 越老越接近黄绿；Set 的随机抽样被 OnSpawn 钉成 shade = 0），年龄到 LifespanTicks（1200 ≈ 60 秒）即寿终走 Clear（20% 留腐殖质）；不耗随机流、不读邻居、不移动、不写 Q。**随机刻**里按顺序：① 正上方是「固体或液体、且不是**同一株**（草 / 花）」时被压死、死亡沿竖直方向向下传播清掉下面整段连续的草（含花）；② 正下方不是土/湿土/腐殖土（或同一株的下段）时失去扎根而死、死亡沿竖直方向向上传播清掉上面整段连续的草（含花）；③ 本格温度 > 50 或 < 3（脚下是腐殖土家族时放宽为 > 55 / < 0）时这一格草死亡；④ 10% 概率在半径 2 格圆盘内找第一个「下方是可托住种子的固体（固体且非同一株）的空地」并 Set 出一颗新草种子（不会撒到别的草尖 / 花头上）；⑤ 把半径 2 格圆盘内的第一个沙 SetPhase 成土。LightAbsorption=0.8（受阳光升温略慢）。不可在编辑器中选择）
      FlowerParticle.cs                       花（固体 + Organic，**草株顶端的显色格**：草种子发芽时 30%（3/10）概率把株顶那格草 SetPhase 成它。继承草的全部性质——非 IsStatic + 空 OnMotion 定植、热容 0.8 / 导热率 0.6 / 辐射率 1.0 / 光照吸收率 0.8 与草同值、随机刻三条死法同判据同顺序（被压 / 失去扎根 / 温度 > 50 或 < 3〔脚下是腐殖土家族时放宽为 > 55 / < 0〕，死亡各按 20% 留腐殖质；被腐殖土窗口救活时消耗脚下土层最深一格腐殖土家族）、带 Organic；差别只有颜色与两项去掉的行为：**不衰老**（OnLogic 严格空体 ⇒ `hasLogic: false`，shade 是花色而非年龄档、无 LifespanTicks）、**不撒种 / 不固沙**。花色 = 12 色调色板（绯红/粉红/浅粉/橙/杏黄/明黄/紫/品红/玫红/蓝/天蓝/白）由世界在 Set / SetPhase 时均匀随机抽样，OnSpawn 刻意不覆写 ⇒ 每朵花色随机、终生不变、更鲜艳；**不占用 flags 私有位**。与草互为「同一株」：互不算压、互为扎根、级联死亡一起清；火把花当燃料。不可在编辑器中选择）
      HumusParticle.cs                        腐殖质（固体；有机物死亡时按 20% 概率就地生成。每逻辑帧在八邻内找第一个土壤 SetPhase 成腐殖土，然后自己 Clear 消失；八邻无土则保持不动。不可在编辑器中选择）
      HumusSoilParticle.cs                    腐殖土（固体；土壤被腐殖质改良后的黑色形态，与干土同款粉末运动。**逻辑帧无变化**（hasLogic: false），靠**随机刻**扩张：被点到且**正上方是腐殖质**时，半径 2 内从近到远找第一个普通 / 湿润土壤，普通土壤 → 腐殖土、湿润土壤 → 湿润腐殖土，并吸收掉上方腐殖质；无土壤则不动、不摇骰。不可在编辑器中选择）
      WetHumusSoilParticle.cs                 湿润腐殖土（固体；腐殖土 + 一份水的黑色形态，**逻辑与 WetSoilParticle 逐条同构**——受热上行 / 常温有压才渗 / 孤立永久持水；水迁移带 T(源)×1.2 的热走，热容 2.2、导热 0.75、密度 2.1。由腐殖土随机刻扩张把邻近的湿润土壤改良成它时产生。不可在编辑器中选择）
      AquaticSeedParticle.cs                  水生态种子（固体粉末，**不带 Organic**；flags bit5-6 与草种子共用 = 无发芽条件计数。随机刻命中且「正上方是水 + 正下方是软底」时**吸收头顶那格水**、自身发芽成 1 格水生植物；连续 3 次无发芽条件则变成腐殖质。不可在右键循环，但可单颗投放）
      AquaticPlantParticle.cs                 水生植物（固体，**不带 Organic**，非 IsStatic：沉水、扎根水底，空的 OnMotion 定植。**每格植物 ⇔ 一格水**（1:1）。**逻辑帧只做衰老**（与草同一模式：年龄 = `Cell.shade` × 5 + `flags` bit3-5 的余数，每 5 帧把 `shade` 推进一档 ⇒ **越老越黄**，1200 帧 ≈ 60 秒寿终；`SetPhase` **还成水**而不是腐殖质；`OnSpawn` 把 `shade` 钉成 0）。**随机刻**里按顺序：① 正上方是固体（非同类）被压死 → 向下整段**还成水**（**液体不算压**）；② 正下方非软底 {土/湿土/腐殖土/沙} 失去扎根 → 向上整段**还成水**；③ 八邻无液体脱水 → 单格还成水；④ 温度 > 50 或 < 0 → 单格还成水；⑤ 生长：头顶是水且上方还有水且株高 < 3 → 消耗头顶那格水长高一格；⑥ 10% 概率撒种：找「软底 + 正上方是水 + 水柱顶上有接收格」的列，水位 +1、水格变成种子（**基质不消耗**）。被固体压埋时那份水注入八邻气体格的湿度场。不可在编辑器中选择）
      FireParticle.cs                         火（气体，第一个粒子级热源；v2 角色模型——每逻辑帧按八邻重判**附着火 / 游离火**。附着火定在原地：接触点燃邻接的草 / 花（八邻固定序，1/20 每逻辑帧；**有燃料就不摇熄灭骰**）、**本格火温 ≥ 60° 才**烤干邻接湿土并按湿土→干土热容差 **−1.2T** 结算潜热（水汽走湿度场守恒）、格温 < 400 注热 250（烤热贴着的沙/石）、正上方是气体且 T ≥ 200 时 1/10 抛射火舌；游离火不注热，在 OnLogic 末尾按 (x,y,Tick) 哈希上升/摇曳（50% 正上、25% 左上、25% 右上，被挡不下降；OnMotion 留空）。熄灭档位互斥：正下方石头/沙/液体压顶立灭 > **被包围（八邻全无空气/气体且无燃料）当帧窒息立灭** > 游离火 T<40 立灭 > 游离火 1/8 > **有燃料不摇骰** > 贴液体 1/2 > 贴任何非可燃固体 1/5（干土/腐殖类/木头/树叶全在内；旧「附着无燃料 1/120」烧尽骰已删）。shade 是温度分档派生量（≥300 亮黄…<40 暗红，+哈希 ±1，不消耗随机流）。投放资格 CanPlaceAt：不覆盖实体、需锚点不浮空（投放锚点含火链——圆刷贴地可铺满整团火；物理附着不含火）；水/沙置换即灭（世界规则涌现）；**唯一可手刷的 Gas**（ParticleEditor._placeableGasTypes 白名单 + CanPlaceAt 资格）。烧草按世界有机衰变规则留 20% 腐殖质。显式行为：火无法停留在石头/沙/水面上；落在任何不可燃物上也活不过几帧，**只有燃料（草/花）能持续供火**）
      TreeSeedParticle.cs                     树木种子（固体粉末，摩擦 / 粘性 0.5，运动 = StepSolid；flags bit3-5 = 物种、bit6-7 = **连续**空转计数。**随机刻**命中且「正上方非固非液 + 正下方按物种基质档（松/柏耐旱＝任意三土；杨/橡喜湿＝三土且八邻有湿土或腐殖土；柳最喜水＝正下方湿土或腐殖土）」时按 **1/D 概率**发芽成树干（D=2..4，比草种子的命中即发慢）：先判后摇、掷骰未中不计打击原地等待；**连续** 3 次无发芽条件霉烂成腐殖质（**条件一旦满足即清零打击计数**，条件性写入、不消耗随机流）、空转不消耗随机数。**腐殖土是决定性基质**（柳：正下方是**非真湿土**的腐殖土；杨 / 橡：八邻无真湿土但有腐殖土）时，发芽成功会消耗脚下土层最深一格腐殖土家族（`HumusSoil→Soil` / `WetHumusSoil→WetSoil`，`SetPhase` 保水保热）；松 / 柏耐旱档永不消耗。可单颗投放，Shift+滚轮切换物种）
      TreeTrunkParticle.cs                    树干（固体 + Organic，非 IsStatic、OnMotion 空；flags bit3-5 = 物种、bit6-7 = 生长阶段 0 爬升 / 1 造冠 / 2 成熟。**每逻辑帧**：① 温度 > 50 死（低温致死取 0，同水生植物——环境温可低到 0）；② 失去扎根（下格非三土 / 沙 / 树干）→ 本格与上方整段连续树干级联 Clear，**清完整段后逐格调 `TreeLeafParticle.NotifyTrunkRemoved` 主动通知邻接树叶失活**（顺序不能反：支撑检查必须读到整段已空；即使 `RandomTicksPerStep = 0`，砍树 / 挖根后整冠也在同一次逻辑帧集体失活、随后靠运动帧落叶）；③ 顶格生长——阶段 0 按物种 H 向上爬升（先查后掷 1/N；**正上方是树叶时可顶开它继续爬升**，石头 / 水 / 沙仍硬阻塞；上方越界即触顶 → 转造冠），阶段 1 按有序偏移表取「第一个未填充且当前可放」项 Set 成树叶（可放＝界内 + 非固液 + 八邻已有树干 / **未失活**树叶防浮空；**界外冠项视为已填充**，被挡项跳过不卡死、腾空后补长；全表填满转成熟）。**随机刻（繁殖）**：成熟顶格每次命中按 **1/4** 概率在半径 **5** 的圆盘内撒一颗同物种树木种子（落点＝界内空地 + 正下方固体且非树干 / 树叶 / 种子；先判后摇——非顶格 / 未成熟零 RNG，合格格每次命中恰耗 1 骰）；成熟后 OnLogic 零随机流消耗、撒种消耗只在 OnRandomTick。不可手刷）
      TreeLeafParticle.cs                     树叶（固体 + Organic，非 IsStatic，OnLogic 空体；flags bit3-5 = 物种、bit6 = 失活标记。**对外静态入口 `NotifyTrunkRemoved(world,x,y)`**：树干级联倒下清完整段后逐格调用，让八邻树叶各跑支撑检查、无支撑者经 `DetachAndSpread` 向整冠扩散失活——整冠瞬时失活不依赖随机刻（关闭也生效）。**随机刻**：已失活 → Clear（**只在再被随机刻点到时才死**）；否则 ① 温度 > 50（低温致死取 0）；② 正上方是固体且非树叶（**液体不压**——树叶可泡水里，与草的有意不同）；③ 本物种支撑半径（Chebyshev 环由内向外扫描、命中即返回）内无树干 → **不失活即死**：记录失活（flags bit6）并**立刻**让八邻一圈的树叶各自检测支撑（无支撑者同样失活、继续扩散 → 砍树后整冠一瞬间集体失活）。**OnMotion**：失活叶竖直**自由下落**一格（**不滚落**，正下方是固体就停；空气 / 气体 / 液体都照落）。死亡走 Clear，按世界 20% 有机衰变留腐殖质。不可手刷）
  Rendering/
    ParticleRenderer.cs                       Color32 缓冲 + Texture2D + quad（按 LightController.Daylight 取昼夜天空色、粒子与云按 1 → 夜晚亮度变暗；昼夜参数由 ParticleLightDriver 推送）
  Driver/
    ParticleSimulationSettings.cs             全局模拟参数（ScriptableObject）：网格尺寸、_motionRate（运动帧 60）/ _logicRate（逻辑帧 20）、倍速预算 `_speedBudgetFraction`（0.5）与倍率上限 `_speedMax`（16）、热力学系数、随机刻格数
    ParticleSimulationDriver.cs               MonoBehaviour：两级固定步长驱动（运动帧 60 Hz / 逻辑帧 20 Hz）+ 上屏调度；每真实帧跑几个运动帧由 `SpeedController` 决定（暴露 `Speed` 属性，只做 Stopwatch 测量与转发）；`_simulate`（关 = 暂停全部帧），暴露 `Pause()` / `Resume()` / `Paused`（无输入判断）
    ParticleSandboxSeeder.cs                  演示用（默认不跑）：右键菜单 Reseed() 才铺石头容器 + 中央一堆沙
    ParticleEditor.cs                         调试用：左键刷当前粒子、右键切换类型（材料类固/液 + Gas 白名单——默认只有火可手刷，其余 Gas 中间态与草/花/湿土/树干/树叶不可手刷）、滚轮调半径 1..10（**Shift+滚轮**循环切换树木物种，HUD 在选中树木种子时显示物种名）、空格暂停/恢复（`_pauseKey`，调驱动的 Pause()/Resume()）、**按住左右方向键 / A D 把相机在沙箱内左右平移**（`_panLeftKeys` / `_panRightKeys` / `_panSpeed` = 30 格/秒；相机位置与「视口不越出网格左右边界」的夹取在 `ParticleRenderer.PanCamera`，见 §9）、**右上角光照面板**（三个输入框 = 光照强度 / 亮相位刻数 / 暗相位刻数，直接写 `ParticleLightDriver` 的三个可写属性；输入非法标红不提交、失焦弹回真值；输入框拿着键盘时暂停 / 高速 / 平移热键让位；高速模式与 `SuppressInput` 时变灰）、鼠标旁显示当前类型、左上角读数末尾追加当前倍速（普通模式恒 `×1`——`=` / `-` / `Tab` 调速热键已移除，速度只由高速模式改变，见 §10「倍速」）；`_unplaceableTypes` / `_singlePlacementTypes` / `_placeableGasTypes` 三个按类型名配置的白名单（不可投放 / 单颗投放 + 500ms 冷却〔草种子/水生态种子/树木种子〕 / Gas 可手刷例外）
    ParticleLightDriver.cs                    把光照的开关 / 亮相位刻数 / 暗相位刻数 / 强度 / 云层消光接到 world.Light，并把昼夜天空色与夜晚亮度推给 ParticleRenderer；其中**强度 / 亮相位刻数 / 暗相位刻数三个属性可写**（运行期由 ParticleEditor 的光照面板驱动，钳制语义与 LightController 同名属性一致）
  Editor/
    TapTap2026.Editor.asmdef                  ← 仅 Editor 平台的工具程序集
    SystemProxyGuard.cs                       系统代理无法被 Mono 解析时把 WebRequest.DefaultWebProxy 钉为直连（保 Unity-MCP 连通）
Assets/Data/
  ParticleSimulationSettings.asset            实例化后的全局参数资产（320x180 / 运动 60 Hz、逻辑 20 Hz / seed 123456789）
Assets/Tests/EditMode/
  TapTap2026.Tests.EditMode.asmdef
  TestParticleFixtures.cs                     测试专用粒子类型（含两段钩子探针 TestPhaseProbeParticle：分别计数 OnLogic / OnMotion）
  ParticleWorldTests.cs                       世界/钩子/确定性/像素映射（含调试热力叠加：越热越红、空气格也叠、满量程 <= 0 失效）/热力学（默认环境热量、逐格读改、越界语义、AddHeat 累加、T = Q / C 换算、按扁平下标读温度、Swap 搬运 Q、Set/Clear 继承 Q、四邻传导与界内热量守恒、界面导热率取两侧 σ 平均、顶行向虚空持续散热 / 内部行不散热 / 关闭即封闭 / 夹紧到稳定上限、辐射只作用于暴露面（顶行 / 上方是气体或空）、空气柱照常全柱辐射、辐射按材料 ε 缩放、空气 ε 可配、辐射功率注入收敛、光照下收敛到稳态温度、热源粒子在 OnMotion 内查改）与光照（每列最上面的非气体——液体挡光、亮暗占空比循环、关闭 / 零强度不施热、关闭不推进相位、昼夜日照度 Daylight 按「下一时段长度的 1/10」自动过渡（含昼 / 夜极限保持、退化周期与黄昏坡道夹紧）、亮度只缩粒子与云不动天空色） + 摩擦度与粘性→净空换算/粘性只在旁边有固体时生效/摩擦度 1 不滚落/斜落净空判据/干散材料安息角随摩擦度（1:1/2:1/10:1）/高粘性石头垛远陡于干石对照/固体互阻/沙堆守恒收敛/土堆比同摩擦度无粘性更陡（隔离类型表）+ 随机刻（默认关闭 / 每逻辑帧命中计数 / 跳过空气与静态 / 夹在 [0, CellCount] / 同种子同批次）
                                               + 分段扫描（运动帧只跑 OnMotion 不跑 OnLogic / 逻辑帧先 OnLogic 再同格 OnMotion / OnLogic 把本格相变后跳过本帧运动；休眠开启时逻辑帧走混合扫描：候选格跑完整两钩子、其余 HasLogic 格只跑 OnLogic，见 §7「逻辑帧混合扫描」）+ 气体竖直 Swap 绝热直减（上升冷却 / 下沉增温 / 固体不结算）/ Set 与 SetPhase 的湿度驱逐 / StepRain 凝结释放潜热
  SandStoneParticleTests.cs                   石头/沙子/土壤的生产注册路径（RegisterAll 回填 + 标签 + 摩擦度/粘性断言）+ 安息角剖面（三角堆）测试
  WaterParticleTests.cs                       水：直落/平底摊平成水平面/沙沉穿水置换上浮（下落的沙在运动中）/同种子确定性/孤立水滴静止不抖动（平地 + 世界底行）+ 开阔水域下沉回归（单颗 + 3×3 沙块沉底、水位抬高、守恒）+ 慢蒸发带走潜热 / 顶行不蒸发 / 接收格不足不蒸发 + 液体热对流（上冷下暖暖水上浮换位 / 死区内不动 / 上方非液体不触发 / 海陆昼夜模拟锁「夜间海面比陆面暖」涌现回归）+ 生产注册路径（隔离表为主）
  GroundwaterTests.cs                         地下水（运动状态闸门，生产注册路径）：闸门单测（静止固体不能置换 / 刚下落的可以 / 运行中放置的宽限一个扫描后过期 / 开关关回退旧行为且不影响状态维护 / 空气目标不受影响）+ 石头水囊与画刷水囊逐位稳定（120 周期哈希不变）+ 联通池塘的竖井地下水（沙沿井沉底、水囊纹丝不动、守恒）+ 挖开释放（1 格宽缺口先放一格水、上方石头塌入自封）+ 沙柱接力钻穿水柱（删掉接力标记即变红）+ 运动休眠透明性 A/B + 石中气穴照旧冒泡
  WetSoilParticleTests.cs                     土 ⇄ 湿土：水被土喝掉（含热量并进湿土）/ **有压才渗**（孤立湿土永久持水不沉底 / 被水压着才下渗且锋面身后保持湿润）/ 被挡侧渗 / 下渗温度门（T < 60）/ 上行 50% 概率门 / 换格水带热 T(源)×1.2 两侧温度不跳 / 受热上行润湿上方干土 / 受热在空气下蒸发（播撒不可见 + 带走潜热）/ 空气吸饱则不蒸发 / 无干土可接则保持湿 / 常温不蒸发 / 湿土照样下落 / 调色板逐档更深 / 生产注册路径
  HumusSoilParticleTests.cs                   腐殖土扩张与湿润腐殖土：随机刻命中且**正上方是腐殖质**时从近到远改良邻近土壤（普通土壤→腐殖土、湿润土壤→湿润腐殖土且水账守恒）并吸收上方腐殖质 / **从近到远**而非扫描序（更近但更晚扫到的土壤被选中）/ 上方无腐殖质或附近无土壤则不动 / **无目标不消耗随机流**（先判后写）；湿润腐殖土与湿土逐条同构（孤立永久持水 / 有压才渗到干腐殖土 / 受热上行润湿上方干腐殖土）；它被当作土壤：草扎根其上有活（对照石头死）/ 水的冲刷把它变成沙；火把它烤成**干腐殖土**并按热容差 1.2T 结算潜热（温度连续、水进湿度场）；生产注册路径（Solid / 受重力 / 非静态 / id 追加）
  WeatherParticleTests.cs                     气象：浮力方向（热升/冷沉/死区，含绝热直减降温）/水平热压流（直调原语，隔离表）+ 湿度场（蒸发提升附近湿度 / 水平冷→热 / 竖直越热越向上 / 随风平流 / 输送守恒 / 凝结释放潜热）+ 降雨（碰并凑整、液体不持有湿度）+ 水循环总量守恒 + 云渲染（湿度染白）+ 云遮光（按列 Beer–Lambert 衰减、逐列独立）（生产注册路径——跨类型相变依赖 ParticleIds 回填；云遮光测试用隔离表 + `new ParticleWorld(w,h,seed,types)` 直构）
  ThermalDriftTests.cs                        大气水平热压流的风向记忆（隔离类型表，直接调 TryThermalDrift / OnMotion）：两格走廊不再逐帧往返（B1 回归）/ 大温差净输运保留 / 梯度消失即清记忆允许新方向 / 写记忆不破坏 bit0 处理标记 / 浮力换行清记忆 / 反向漂移被挡且记忆保留
  AquaticParticleTests.cs                     水生植物 / 水生态种子（生产注册路径，随机刻直接调 OnRandomTick）：生产注册回填（含「不带 Organic」的防截胡断言）/ 吸水发芽（头顶水变大气）且守恒 / 水柱到顶也能发芽 / 未淹没或未扎根不发芽 / 3 次空转霉烂且不消耗随机流 / 逐格生长消耗一格水 / 只剩一格保命水不长 / 株高到顶不长 / 四种死法各自还水 / 被固体覆盖走湿度场且守恒 / 液体上方不算压 / 环境低温（T=0）不冻死 / 不移动 / 撒种顶水位 +1 且基质不消耗 / 全流程（发芽→补水→生长→过热死）总量守恒 / **寿命**（逻辑帧衰老：shade 从 0 起、每 5 帧 +1、第 1200 帧寿终还成水且水账守恒、生长出的新茎格也从 0 起、衰老不消耗随机流、调色板红分量随年龄非递减且渲染更黄绿）
  GrassParticleTests.cs                       草的寿命（生产注册路径，随机刻默认 0 ⇒ 隔离出唯一的衰老变量）：新生草从 shade = 0（最年轻档）开始 / 不满 TicksPerShadeStep 帧颜色档不动、满一档 +1 / 第 1200 逻辑帧寿终（终态只允许空气或腐殖质）/ 衰老与所在逻辑帧的场 pass 不消耗世界随机流（对照世界取下一个 NextUInt 相等）/ 颜色渐变（调色板长度 = ShadeCount、红分量随年龄非递减、最老档比最年轻档更偏黄、渲染像素经 ParticlePixelBuffer 按 shade 查表后同样更偏黄绿）
  FlowerParticleTests.cs                      花（生产注册路径；随机刻里的温度致死与草的低温线（< 3°）会杀掉裸世界的草株，因此直接调 OnRandomTick 并先给参与的格显式注热隔离变量）：注册回填（Solid+Organic / 非静态 / 不落体 / 光照吸收率与草同值）/ 调色板鲜艳（最亮通道 ≥ 200）且互不相同、40 朵花抽到 ≥ 8 种花色（种子发芽 200 次直接调钩子）**株顶是花且之上无草**、比例落在 22%..39% 窗口（≈30%）且一次发芽至多一朵 / **与草互为同一株**（花不压死自己那株草，200 次双向随机刻后两者俱在）/ 被石头压死并沿竖直方向向下级联清掉草段（止于土壤）/ 失去扎根（悬空或站在石头上）死 / 过热（> 50°）与过冷（< 3°）死 / 死亡只留空气或腐殖质且 40 次里两者都出现（20% 有机衰变）/ 可燃（贴花的火不摇熄灭骰、200 帧内点燃花）/ **不衰老**（1300 逻辑帧后仍在原地、花色不变）
  FireParticleTests.cs                       火 v2：生产注册路径（Gas / 非静态 / 不透明调色板 / 追加在水生植物之后）+ 放热（关传导隔离 250/帧；默认传导首帧烤热四邻、火格 > 50°）+ 托土无燃料自然熄灭终态为空气 + 蔓延烧掉大部后全部熄灭（八邻点燃 + 有草不熄；**烧不光整片草是已拍板的涌现特性**——点燃 20% 腐殖质哑弹成空洞断火线，终态只余空气 / 腐殖质 / 零星残草）+ 投放资格（不覆盖实体、不浮空；锚点含火链——贴地圆刷 13 格全落、悬空 0 格；大气可投、越界不可投）+ 烤热沙石与 1/5 淬熄 + **任何非可燃固体（干土/腐殖类/木头/树叶）都在窗口内淬熄** + **被包围当帧窒息（不摇骰，随机流 0 消耗）与「有燃料豁免窒息」** + **长在土上的草皮照样烧掉 ≥ 3/4** + 压顶立灭（落石 / 手刷在石沙水面——FD1）+ 烤干湿土（总热守恒 −1.2T、干土正温、总水守恒）+ 贴水 1/2 快熄 + 游离火上升从不下沉 + 冷却即灭 + 游离火必散尽无残留 + shade 即温度分档 + 火舌抛射（上方气体才摇骰、上方是火跳过、不覆盖实体）+ 确定性重放逐帧一致 + 覆盖大气先疏散水汽（FB2）+ 点火自灭全程总水守恒 + 生产参数下温度有界且草致死线可达 + 400° 停注
  MotionSleepTests.cs                        运动休眠（阶段 B）：休眠透明性（同一受热场景 sleep on/off 跑 200 周期后逐格 typeId / shade / 非 bit0 flags、热场、湿度场一致）/ 静止水池求值数为 0（写入后回升）/ 石头净空链的 19 格远写唤醒（清空最远那格后石头正确滑动）/ 湿度变化（无写入）也唤醒读温度的决策 / MotionSleepEnabled=false 回退全扫描且与 true 逐格一致（含来回切换）
  LogicScanTests.cs                          逻辑帧混合扫描：休眠开时活跃场景（石头底/土层/草皮/一池水/湿土，光照开、随机刻 48）300 周期 ×（3 运动帧 + 1 逻辑帧）sleep on/off 逐位透明（Cells 口径同 MotionSleepTests + 热场 + 湿度场）/ **火注热的同帧可见性回归**（320×180 生产形态：全网格大气 + 陆海 + 光照 + 随机刻 48，预热 540 周期后在 y=28 点 x=40..119 一排火，之后 300 周期**每周期**比状态哈希、末态逐格断开——未把扫描中的热变化同帧并入候选位图时首个点火周期即发散）/ HasLogic 契约（hasLogic:false 的 OnLogic 有副作用也永不被调、默认 true 每逻辑帧照常调；覆盖混合扫描与全网格两条路径）/ 回归：休眠开时沸点之上的水仍蒸发（OnLogic 未被休眠）
  HeatSkipTests.cs                           热传导不动点跳过：静止世界首帧真跑后持续跳过 / 注热后恢复执行再收敛 / 改格类型后恢复执行再收敛 / 灯照脉冲必须被下一帧热 pass 传导、暗相收敛后进入跳过 / 跳过 vs 每帧重算（FixpointSkipEnabled）逐位透明
  SpeedControllerTests.cs                    倍速控制器（纯 C#，不依赖 Unity 时钟）：手动档 tick 数随倍率精确缩放且忽略耗时反馈（含 [1,16] 钳位）/ 自适应档便宜帧加性升到 16（每 K=10 帧一次评估）、昂贵帧乘性 16→1 只花 4 次评估 / 逐帧成本噪声不再驱动换向（回归）/ 超预算丢账（cap、丢账计数、下一帧不雪球）与预算为 0 时的 1 tick 保底 / 滞回把成块便宜·昂贵交替的方向反转次数压到低于无滞回对照 / 默认手动 1× 与迁移前固定累加器在 600 帧抖动序列上逐帧 tick 数一致（预算未截断时）/ 实测倍率：16× 撞紧预算时 `EffectiveSpeed` 收敛到被 cap 的 4×（不是目标 16）、预算充裕时收敛到目标 4×、稳态 1× 精确归一（含 dt 抖动，≈1±0.02）/ 需求超预算时自适应必降档（旧实现的 EMA 只反映已交付吞吐、锁死 16 的回归）/ 周期长度由驱动注入（同一成本信号下注入周期 1 升档、注入周期 3 不升）
  CameraPanTests.cs                          相机平移（`ParticleRenderer.PanCamera`；测试里现建相机与渲染器，用 SerializedObject 把相机塞进 `_camera`，不依赖场景里的 Main Camera）：按给定格数左右平移且只动 x（y / z 不动）/ 两端各夹在 `[半视宽, Width − 半视宽]`（越界部分丢掉，不欠账）/ 视口比沙箱宽时恒居中（平移无效）/ 无输入调用（delta = 0）也把被宽高比变化挤出界外的相机夹回沙箱 / 非正交相机原样不动
  DialogueRunnerTests.cs                      对话系统（纯 C#，不碰模拟；见 `docs/DialogueSystem.md`）：JSON 解析（合法 / 畸形 / 无 segments / 角色表缺失）/ 进度推进 / 选项点阻塞与跳转 / 非法 goto 以警告收尾 / `OnEnd` 只触发一次 / 历史顺序 / Restart / 空段立即结束
```

测试总数 **393**：`ParticleWorldTests` 89 + `SandStoneParticleTests` 2 + `ThermalDriftTests` 7 + `WaterParticleTests` 18 + `WeatherParticleTests` 24 + `WetSoilParticleTests` 17 + `AquaticParticleTests` 25 + `GrassParticleTests` 13 + `FlowerParticleTests` 13 + `FireParticleTests` 28 + `MotionSleepTests` 5 + `LogicScanTests` 4 + `SpeedControllerTests` 12 + `HeatSkipTests` 5 + `HighSpeedSessionTests` 13 + `CameraPanTests` 5 + `DialogueRunnerTests` 17 + `GameFlowTests` 24 + `TreeParticleTests` 45 + `GroundwaterTests` 14 + `HumusSoilParticleTests` 13。

程序集划分：`TapTap2026.Simulation`（无渲染依赖，`Particles/` 归入同一程序集，新增类型不需要动 asmdef）、`TapTap2026.Game`（引用 Simulation，装渲染与驱动）与 `TapTap2026.Editor`（仅 Editor 平台，无运行时引用，编辑器工具）。EditMode 测试程序集引用 `TapTap2026.Simulation` + `TapTap2026.Game`（后者只为 `CameraPanTests` 这类渲染/驱动层测试；测试逻辑仍以纯模拟断言为主）。

**场景接线（已完成，`SampleScene.unity`）**：场景根下 `Particle Sandbox` 一个物体，挂五个组件并已两两接线：

```
Particle Sandbox
├─ ParticleRenderer            (无外部引用，材质运行期自建；_fitCameraOnInitialize=1)
│                              （昼夜天空色 / 夜晚亮度由 ParticleLightDriver 每帧推送，本组件不配置）
├─ ParticleSimulationDriver    _renderer → 上面的 ParticleRenderer
│                              _settings → Assets/Data/ParticleSimulationSettings.asset
│                              _simulate=1（暂停走 Pause()/Resume()，见 §7；空格热键在 ParticleEditor）
│                              _widthOverride / _heightOverride = 200 / 100（0 = 用 Settings 资产；非 0 时覆盖沙箱尺寸）
│                              _heatDebugOverlay / _heatDebugFullScale（调试热力叠加，见 §6）
├─ ParticleSandboxSeeder       _driver   → 上面的 ParticleSimulationDriver
│                              （默认什么都不做；地板厚 3、挡墙厚 3、沙 50%x60% 仅在显式 Reseed 时使用）
├─ ParticleEditor              _driver   → 上面的 ParticleSimulationDriver
│                              _renderer → 上面的 ParticleRenderer（屏幕→格子的换算基准）
│                              _drawHud=1 / _pauseKey=Space（空格暂停/恢复，调驱动 Pause()/Resume()，见 §7）
│                              _panLeftKeys=LeftArrow+A / _panRightKeys=RightArrow+D / _panSpeed=30（相机在沙箱内左右平移，见下）
│                              （与 _highSpeedKey 一样，这三个字段尚未落进场景文件，按脚本初值生效——新增字段缺省即用代码默认值）
└─ ParticleLightDriver         _driver   → 上面的 ParticleSimulationDriver
                                _renderer → 上面的 ParticleRenderer
                                _enabled=1 / _litTicks=600 / _darkTicks=600 / _intensity=10 / _cloudExtinction=0.05
                                _dayBackground=#CCE5FF / _nightBackground=#2A223A / _nightBrightness=0.5
                                （每帧写回 world.Light、并把昼夜参数推给渲染器；过渡长度由周期自动推出）
```

- **沙箱初始全是大气**：`ParticleSimulationDriver.Awake` 建世界后把所有空格填成 `AtmosphereParticle`（全透明，看不见）；固体/液体由 `ParticleEditor` 画进去，`ParticleSandboxSeeder` 不再在 `Start` 自动搭建。
- **暂停/恢复（空格）**：`ParticleEditor._pauseKey`（默认 `Space`；设 `None` 关闭热键）每帧检测按下，按当前状态调驱动的 `Pause()` / `Resume()`——暂停时运动帧与逻辑帧**一起冻结**、时间不累加（恢复不补跑暂停期间堆积的帧），画面停在最后一帧；再按一次继续。驱动侧只提供 `Pause()` / `Resume()` / `Paused`（及 `Simulate`）等状态与方法，**不含输入判断**。暂停中 `ParticleEditor` 画完会自己 `PresentNow()` 上屏，因此暂停状态下依然能涂改并立即看到结果。
- **倍速（只由高速模式改变）**：**没有调速热键**——`=` / `-` / `Tab` 已从 `ParticleEditor` 移除，普通编辑模式**恒 1×**（`SetSpeed` / `SetAdaptive` 只被高速模式会话调用）；左上角读数行末尾随之常驻 `×1`。倍速的实际交付量另由 `SpeedController.EffectiveSpeed` 观察（把「每帧实际 tick 数」与「每帧 dt」分别 EMA 再相除（÷ 运动速率），因此 dt 抖动与 0/1/2 tick 的量化都不会给比值带偏、稳态 1× 精确归一；显示用观测量、不参与调度，高速模式左下角 HUD 用它显示实测吞吐）。策略与状态全在 **`SpeedController`**（`Assets/Scripts/Simulation/SpeedController.cs`；**纯 C#、不引用 `UnityEngine`**，因此能在 net9 测试工程下单独编译测试），驱动只做两件事：`Stopwatch` 量本帧 tick 循环耗时回喂、把控制器给定的 tick 数跑完；输入与展示仍在 `ParticleEditor`（照抄暂停模式的分工：方法在驱动 / 输入在编辑器）。四条语义：① **固定步长不变**——倍速只改「每真实帧跑几个运动 tick」，`StepMotion` / `StepLogic` 本体与每个 tick 内部逐位不变，任何倍率下同一 tick 序列推进出的世界逐位相同；② **丢时间不追账**——单帧需求超过预算上限时，超出部分连同欠账一起丢弃（欠账清零），死亡螺旋构造性不可能；③ **进度保底**——预算上限至少 1，因此极端超预算也照跑 1 个运动 tick（帧率代价用户自负；`dt` 本身不足一个 tick 时该帧照旧 0 个 tick）；④ **默认 1× 且自适应关闭**——与不含倍速的旧固定累加器**逐帧逐位一致（预算未截断时）**（tick 数按 `1f / motionRate` 的 float 步长从欠账里重复扣减，不是 double 取整）。预算 = `1000 / 目标帧率 × _speedBudgetFraction`（默认 0.5；目标帧率取 `Application.targetFrameRate`，≤ 0 时按 60——是**目标帧率名义帧时间的绝对上限**，不是本帧真实 `deltaTime` 的比例），上限再按「每运动 tick 耗时」的 EMA 动态折算，倍率封顶由 `_speedMax`（默认 16）给定。**手动档是生产里唯一的档**（普通模式恒 1×、高速模式钉到控制器满档 = `_speedMax`），自适应档 = AIMD 保留但生产不启用，其调档决策只吃**已平滑的**「每 tick 耗时」EMA、且每 `DecisionIntervalFrames`（10）次 Plan 才评估一次：一周期（周期长度由驱动注入 = `LogicTicksPerMotionTick`，生产 60/20 = 3 个运动 tick）的平滑成本按 `每 tick 耗时 × 周期 × min(需求, cap)` 折算——需求超预算时折算成本饱和、必够到降档水位线（旧实现的 EMA 只反映已交付吞吐，被 cap 丢弃的需求不进反馈，永远降不下来）；低于「预算 × 0.75」加性升 `+0.25`、高于「预算 × 0.95」乘性降 `×0.5`，两者之间是死区，换方向再有 10 帧滞回——单帧成本抖动不再驱动倍率。**注意：倍速下同一段输入按 tick 对齐而非真实时间**——编辑器刷进去的粒子总在下一个运动 tick 被处理，而一个真实帧可能跑多个 tick，所以同一次按键 / 涂抹在 N× 下「经历」的模拟时间约为 1× 的 N 倍；需要严格按时序复现的脚本请固定手动 1×。
- **高速模式（`H`）**：按 `H`（`ParticleEditor._highSpeedKey`，设 `None` 关闭）进入——内核 `HighSpeedSession` 把倍速钉到**控制器满档**（`SpeedController.MaxSpeed`，即 `_speedMax`，默认 16×；手动档、跑不满由预算封顶）、锁一切编辑（刷子/右键切换/滚轮在 `Update` 同一处统一拦截，只剩空格暂停、相机平移与只读读数——锁的是编辑，看视角不锁），跑满 `_highSpeedLogicFrames`（默认 6000）逻辑帧后自动暂停并在屏幕中央显示每种粒子的产生/死亡结算（`Enter` 或 `H` 确认退出、倍速钉回 1×，仍保持暂停由玩家空格继续）。运行中屏幕左下角常驻 `高速 1234/6000 ×7.6 地表 12.3° 湿度 0.456`——倍速一档只显示**实测**吞吐 `EffectiveSpeed`（EMA 平滑的一位小数；目标档恒为满档，没有显示价值），未采样时温度 / 湿度显示 `--`。状态机、O(1) 生灭统计与采样全在内核（见 §14），驱动只做懒建 + 每逻辑帧转发 + 完成自动 `Pause()`。
- **沙箱尺寸可在 Inspector 调整**：驱动上的 `_widthOverride` / `_heightOverride`（0 = 用 `ParticleSimulationSettings` 资产的 `Width` / `Height`，非 0 则覆盖，夹在 1..4096）。改 Inspector 字段、改 Settings 资产宽高、或调用 `ParticleSimulationDriver.Resize(w, h)` 都会在下一帧重建一个**空**世界（已有内容不保留），渲染器随之重建纹理/mesh 并由 `_fitCameraOnInitialize` 重新框住整张网格；`ParticleEditor` 每帧从 `driver.World` 取尺寸，无需额外处理。
- **编辑操作（`ParticleEditor`）**：按住**左键**用当前粒子填满鼠标处半径 r 的实心圆（r 默认 3，即 29 格）；**滚轮**在 1..10 之间调整 r；按**右键**在可投放类型间循环切换（**材料类固/液 + Gas 白名单**——石头/沙/土/水/火，外加草种子与水生态种子；其余 Gas 中间态由相变/清空产生，草/湿土/腐殖质/腐殖土/湿润腐殖土/水生植物只能由模拟产生，都不可手刷；水汽是湿度场的量，根本不是粒子）；鼠标旁常驻色块 + 类型名 + `序号/总数` + `r=半径`（单颗投放的类型显示「单颗」）。写入走 `world.Set`，因此世界会把新格标成「本 tick 未处理」，画的粒子下一 tick 必定被模拟；驱动暂停（`Simulate = false`）时编辑器补一次 `PresentNow()` 以便看到刚落下的东西。鼠标 → 格子：相机射线打到 z=0 平面后换算到渲染器局部坐标取整，网格 quad 的 1 cell = 1 unit 是这条换算成立的前提。
- **投放白名单（`ParticleEditor`）**：三个 Inspector 字符串列表，按类型的 `DebugName`（或类名）解析成 typeId——`_unplaceableTypes`（默认 `GrassParticle` / `FlowerParticle` / `WetSoilParticle` / `HumusParticle` / `HumusSoilParticle` / `WetHumusSoilParticle` / `AquaticPlantParticle` / `TreeTrunkParticle` / `TreeLeafParticle`）把它们从右键循环里剔除；`_singlePlacementTypes`（默认 `GrassSeedParticle` / `AquaticSeedParticle`）让这些类型**无论笔刷半径多大都只在光标中心落一颗**，且两次投放之间共享 `SinglePlacementCooldownSeconds` = **500ms** 冷却（`Time.unscaledTime`，暂停时照常计时），按住左键也不会每帧落一颗；`_placeableGasTypes`（默认 `FireParticle`）让白名单里的 Gas 例外可手刷（Gas 默认一律不可投放——蒸汽/云/大气是水循环中间态、只能由相变产生，火是唯一例外；火还另有 `FireParticle.CanPlaceAt` 投放资格：不覆盖实体、需附着物不浮空，圆刷时不合格的格被跳过）。将来有更多「单颗 + 冷却」「不可投放」或「可手刷气体」的类型时，往对应列表追加类型名即可。
- **光标格读数（`ParticleEditor`，屏幕左上角）**：常驻一行 `(x,y)  粒子名  T=温度  Q=热量  湿度=…`，显示光标指向的那一格；光标落在网格外则显示「视口外」。温度是导出量 `T = Q / C`（C 取该格类型的热容，气体还会加上该格**湿度**带来的附加热容 `HeatMap.HumidityHeatCapacity`），湿度对固/液恒为 0。纯调试读数：只读 `world.Heat` / `world.Humidity`，不参与模拟，也不改动任何状态。
- **预设演示内容已弃用**：`ParticleSandboxSeeder` 的「石头容器 + 沙堆」预设在石头改为高摩擦 + 高粘性（摩擦度 0.9 / 粘性 0.9）后不再稳定——整列挡墙内缘高处的石块会滑入容器。保留仅为兼容旧场景；新内容用 `ParticleEditor` 手绘。（右键菜单 `Reseed()` 仍可用，但别指望挡墙长期不漏。）
- 相机由渲染器在首次建立纹理时自动摆正（`orthographicSize = Height/2`，位置 `(Width/2, Height/2, -10)`），Main Camera 本身无需手改。
- **相机左右平移（左右方向键 / A / D）**：`ParticleEditor` 每帧按 `_panLeftKeys`（默认 `LeftArrow` + `A`）与 `_panRightKeys`（默认 `RightArrow` + `D`）的**按住**状态算出方向（+1 / −1，同时按住即抵消），乘 `_panSpeed`（默认 30 格/秒）与 `Time.deltaTime` 得到格数，交给 `ParticleRenderer.PanCamera` 落地（相机位置写入与夹取都在渲染器，见 §9）。**视口始终夹在沙箱左右边界内**：相机中心 x 恒在 `[半视宽, Width − 半视宽]`；视口比沙箱还宽时恒居中（此时平移无效）——移不出沙箱，也不会露出网格外的空白。**平移只动相机**：不写 `Cell[]`、不碰随机流、不重建纹理，因此不参与确定性，也**不受暂停影响**（`Time.deltaTime` 是真实帧时间，暂停只冻结模拟推进）；高速模式中同样可用（那里锁的是编辑，不是视角）。看不见输入时也照常调一次（格数 = 0），以便 Game 视图宽高比变化后把相机夹回沙箱内。默认两键是 `LeftArrow` + `A` / `RightArrow` + `D`；列表留空或全设 `None` 即关闭对应方向，`_panSpeed <= 0` 关闭平移（夹取仍在）。
- **光照面板（`ParticleEditor` 右上角）**：三个输入框直接改**光照强度 / 亮相位刻数 / 暗相位刻数**——写的是 `ParticleLightDriver` 的三个可写属性（`Intensity` / `LitTicks` / `DarkTicks`）。**必须写驱动器而不是 `world.Light`**：驱动器每帧把字段写回世界，写世界会被下一帧覆盖（面板与 Inspector 因此是同一份配置）。文本解析用 `InvariantCulture`：刻数须为非负整数、强度须为有限浮点（与 `LightController` 同名属性的钳制语义一致），非法时文本标红且**不提交**，失焦后自动弹回驱动器当前值（顺带把 Inspector 运行期改的值同步到面板）。输入框获得键盘焦点时，暂停（空格）/ 高速（`H`）/ 相机平移的按键整段让位给输入框——这些键在输入框里也是能敲进去的合法字符，否则敲个空格就暂停、敲个 `H` 就跑一整场高速。**使用面板期间同时屏蔽左键放置**：判据是「某个光照输入框持有键盘焦点 **或** 指针落在面板矩形内」（`LightPanelRect` 由绘制与命中测试共用，不漂移；面板未接线时恒不挡）——点进输入框时指针也压在网格上，不挡就会既改光照值又在底下画一笔；持有焦点这条让「点进去后把指针拖出面板再按左键」也继续挡。注意只挡**放置**（左键投放 / 擦除），右键切类型与滚轮调半径不在此列。高速模式进行中与对话激活（`SuppressInput`）时整个面板变灰不可编辑：前者会话结果依赖光照输入、与画笔一起封锁，后者是本组件「完全不响应输入」的既有语义。改参数等于改世界输入，因此只在调试时用——不影响代码本身的确定性契约。
- **热力学参数（`ParticleSimulationSettings` 资产）**：`_conductivity`（资产 0.24）、`_voidHeatLoss`（资产 0.12）、`_radiativeLoss`（资产 0.05）、`_adiabaticLapse`（资产 0.1，气体竖直位移的绝热直减率）、`_airThermalConductivity`（资产 1）与 `_airThermalEmissivity`（资产 0.1），驱动每帧写回 `world.Heat`。改这几个值即可调「竖直温差的陡峭度」（`_conductivity` 越大热量摊得越平、越不会有光照热点）、「热在空气里能铺多高」（`_airThermalEmissivity` 越小铺得越高、整柱越暖，见 §6）与「整体冷热」，无需改代码。**`_airThermalEmissivity` 是调「热铺多高」的首选旋钮**：它只动空气、不动固/液，因此不会连带改地表温度（对比：全局降 `_radiativeLoss` 会把地表一起改热，且必须同步调光度）。
- **光照与昼夜（`ParticleLightDriver`）**：六个字段（是否光照 / 亮相位刻数 / 暗相位刻数 / 强度 / 云层消光系数）每帧写回 `world.Light`——其中**强度 / 亮相位刻数 / 暗相位刻数**三个参数另有可写属性，运行期由 `ParticleEditor` 右上角的光照面板驱动（见上一条）；另三个字段 **`_dayBackground` / `_nightBackground` / `_nightBrightness`** 是昼夜表现的**唯一配置面**，每帧推给渲染器（`_renderer`，同名在 `ParticleRenderer` 上只作为无光源驱动器时的代码默认值）。天空色在夜 / 昼两色间按 `Daylight` 取色、粒子与云按 1 → `_nightBrightness` 变暗，过渡长度**自动**取「下一时段长度的 1/10」（亮 600 / 暗 600 ⇒ 各 60 刻），无需接线。想在编辑器里看到热，把 `ParticleSimulationDriver` 的 `_heatDebugOverlay` 勾上（叠加是整场的、越热越红）；`_heatDebugFullScale` 决定满量程温度，量纲由玩法约定，默认 10。两个字段默认关闭，不影响正式观感。

---

## 11. 新增一个粒子类型的流程

1. 在 `Assets/Scripts/Simulation/Particles/` 新建 `XxxParticle.cs`，`public sealed class XxxParticle : IParticleType`，实现 `Info` / `OnMotion`（运动，60 Hz）/ `OnLogic`（相变反应，20 Hz）/ `OnRandomTick` / `OnSpawn` / `OnKill`。没有对应行为的钩子留空——它们必存在，但可以什么都不做（`OnLogic` 留空是常态）。
2. 在文件头注释里登记：占用的 flags 私有位、需要的邻居查询范围、与哪些 `ParticleIds` 交互。
2′. 在 `Info` 里声明 `motionReadReach:` ——该类型 `OnMotion` 对 `_cells` 的轴对齐阅读盒（默认 `(1,1,1)`，见 §4「运动阅读盒」）；步进固体按斜滑净空链、流体按视野扫描的实际格数声明，读温度不计入。
3. 在 `ParticleRegistration.RegisterAll()` 里追加一行 `ParticleIds.Xxx = ParticleRegistry.Register(new XxxParticle());`（**只能追加，不能插队重排**）。
4. 在 `ParticleIds` 里加对应 `public static ushort Xxx;` 字段。
5. 自测：EditMode 测试里 `new ParticleWorld(w, h, seed)`，手动 `Set` 若干格，跑 N 次 `StepLogic()`（或 `StepMotion()` 隔离运动），断言最终 `GetCell` 分布（不依赖渲染、不依赖场景）。

评审清单：

- [ ] 一个类型一个文件；`sealed`；无基类；非 MonoBehaviour。
- [ ] `OnMotion` / `OnLogic` 内无分配、无 LINQ、无字符串、无 `UnityEngine.Random`、无 `Time.*`；运动只写在 `OnMotion`、相变 / 反应只写在 `OnLogic`，`OnLogic` 不移动粒子（**唯一例外**：`FireParticle` 的游离火在 `OnLogic` 末尾上升，已定稿）。
- [ ] `OnRandomTick` 只用于「缓慢、按概率发生」的过程（生长 / 蔓延 / 渗流 / 腐化）；没有该行为的类型留空。它同样不许分配、不许读 `Time.*`，随机用 `world.NextUInt()`。
- [ ] 每个写操作之前都有显式判断；写操作之后**立即 return**。
- [ ] 没有直接写 `Cell.typeId`（绕过钩子）；没有整体覆盖 `flags`（破坏 bit0）。
- [ ] 没有把 per-cell 状态放进单例字段。
- [ ] 越界坐标先经 `IsInBounds` 判断，而不是依赖写操作静默失败。
- [ ] 调色板 1..256 项，`shade` 语义一致。
- [ ] `Info.HeatCapacity` 是有限正数（热容，决定同一份热量下的温度）；`Info.ThermalConductivity` 与 `Info.ThermalEmissivity` 都在 `[0, 1]` 内（相对导热率，界面取两侧平均；相对辐射率，只在暴露面生效）；`OnLogic` 内读温度用 `world.Heat.GetTemperature`、改热量用 `AddHeat` / `SetHeat`，不要自己再做一遍四邻热传导（世界每逻辑帧已做）。
- [ ] `Info.MotionReadReach` ≥ `OnMotion` 对 `_cells` 的实际阅读盒（宁大勿小，默认 `(1,1,1)`）：运动休眠的写入唤醒集 = 所有类型盒反转后的并集，声明偏小会让读得到被写格的粒子唤不醒（见 §4「运动阅读盒」）。读温度不进盒。
- [ ] 若 `OnLogic` 是**严格空体**（只有注释，不读 / 不写 / 不消耗世界随机流），构造时传 `hasLogic: false`；否则保持默认 `true`。声明 `false` 会让该类型在逻辑帧不再收到 `OnLogic`（混合扫描整段跳过），误判等于丢失逻辑（见 §4「HasLogic」）。
- [ ] 固体类型：`Info.Friction` 与 `Info.Stickiness` 都在 `[0, 1]` 内，`OnMotion` 走 `ParticleRules.StepSolid(world, x, y)`（自行实现则要在注释里说明为何不走共享规则）。
- [ ] 跨类型判断只走 `ParticleTags`（`TagsAt` / `HasTag` / `IsSolid` / `CanDisplace` / …），不特判 typeId；**例外是"一对具体类型之间的反应"**（如土吸湿土、湿土吐干土），那种情况按约定用 `ParticleIds` 双向引用。
- [ ] 静态类型的 `OnMotion` / `OnLogic` / `OnRandomTick` 都不会被调用——留空并注明原因，不要往里写逻辑。

---

## 12. 明确禁止

| 禁止 | 原因 |
|---|---|
| 用 GameObject / MonoBehaviour / SpriteRenderer 承载单个粒子 | 破坏扁平数组与 O(1) 寻址，GC 与 transform 开销不可接受 |
| 在热路径用 `Dictionary` / 字符串 / 反射 / `GetType()` | 设计硬约束：只允许 `typeId` 数组寻址 + 委托直调 |
| `Cell` 内放引用类型字段 | 破坏连续性、增加 GC 扫描面 |
| 让类型继承基类或通过 `IParticleType` 引用调用方法 | 引入虚分派；接口只用于注册期取委托 |
| 在类型单例里存 per-cell 状态 | 逻辑上就把 per-cell 数据挪出了扁平数组 |
| 运行期修改 `ParticleTypeInfo` / 重排注册顺序 | 破坏确定性与存档兼容性 |
| 在模拟代码里读 `Time.*`、`UnityEngine.Random`、`Input` | 破坏「固定步长 + 可复现」 |
| 直接改 `Color32` 缓冲 / 纹理来表示粒子状态 | 渲染缓冲是只读派生物，唯一真源是 `Cell[]` |

---

## 13. 待定决策（评审时拍板）

1. **`vx` / `vy`**：v1 采用 4 字节 `Cell`（不含速度）；确认何时切到 6 字节。
2. **帧速率默认值**：运动帧 60 Hz（`_motionRate`）、逻辑帧 20 Hz（`_logicRate`，即每 3 个运动帧一次逻辑）；每帧 tick 上限不再固定为 4，改由 `SpeedController` 按 `_speedBudgetFraction`（0.5）与实测每 tick 耗时动态给出（都放在 `ParticleSimulationSettings` 里，可改）。逻辑帧降频后热 / 湿度 / 降水 / 光照与昼夜相位都按 20 Hz 推进——昼夜周期相应变长约 3 倍（见 §2 比例约定），光照冷却速率若观感变化需另调系数。
3. **上屏倍数**：`StepsPerPresent` 现为 1（每个运动帧渲染一次）。等帧速率提高或网格变大后，评估是否调大以省掉同一帧内的重复上传。
4. **`ParticleRules` 共享帮助方法的边界**：已提供 `StepSolid`（固体的标准运动：正下 → 按 `Info.Friction` / `Info.Stickiness` 斜落）/ `TryCohesiveFall`（带粘性的斜落）/ `TryFall` / `TryDiagonalFall`（可选 `clearance`：斜下方落点起需要连续能进入的格数，1 = 1:1、2 = 2:1、10 = 10:1，`<= 0` = 不斜滑）/ `TryFlowHorizontal` / `TryLiquidConvect`（液体热对流：比正上方的液体暖 ε 就上浮一格，海洋翻转——白天稳定分层不翻转、夜晚表冷底暖时把深处的热翻回表面）/ `TryBuoyantStep` / `TryThermalDrift`（气体浮力与带风向记忆的水平热压流）/ `CanDisplace` / `CanGasEnter`（气体换位的密度次序判据：`自己.Density >= 目标.Density` 才可换入）/ `IsGasOrEmpty` / `TagsAt` / `HasTag` / `IsSolid` / `BlocksLight`。新增跨类型原语一律放这里、只走标签判断。
5. **一键搭沙箱**：`SampleScene` 已接线完成；重建演示内容靠 `ParticleSandboxSeeder` 的 Inspector 右键菜单（`Reseed()`）。是否值得再加一个编辑器菜单项（`GameObject/粒子沙箱`）供新场景快速起一个。
6. **存档格式**：`Cell[]` 直接按 4 字节/格序列化 + 版本号，还是 RLE。v1 不做存档，但 id 顺序策略按「不可重排」执行。
7. **随机刻密度**：`RandomTicksPerStep` 生产值取 48（≈ Minecraft 的 3 格 / 4096 密度）。等出现真实随机刻行为（生长 / 蔓延）后按观感重调；是否要改成按格数归一（密度而非绝对计数）也一并拍板。
8. **热力学（`HeatMap` + `LightController`）**：模型为「每格存 Q、T = Q / C、热由物质携带（`Swap` 搬 Q、`Set`/`Clear` 就地继承 Q）+ 每逻辑帧全格四邻扩散 + 两条散热路（顶行向虚空 `VoidHeatLoss`、**只作用于暴露面**的辐射 `RadiativeLoss`）+ 光照打到每列最上面的非气体粒子（液体挡光）」。三系数均在 `ParticleSimulationSettings` 资产（现配 0.24 / 0.12 / 0.05），另有绝热直减率 `_adiabaticLapse`（现配 0.1，气体竖直 Swap 结算）、空气导热率 `_airThermalConductivity`（现配 1）与空气辐射率 `_airThermalEmissivity`（现配 0.1）。辐射率已按材料区分（`ParticleTypeInfo.ThermalEmissivity`：大气 0.1、其余 1.0，见 §6 辐射率表），这是把「空气热铺得高」与「地表温度」解耦的旋钮。待定：虚空散热仍只看**网格顶行**，是否要一并改成「暴露面」（与辐射同一判据）；导热率已按材料区分（`ThermalConductivity`，界面取两侧平均），后续可考虑改几何平均；是否要给固/液/气之外的维度再分辐射率（目前按材料区分，大气 0.1、其余 1.0）；空气热容 `AirHeatCapacity` 是否也要可配；`LightController` 是否要方向性 / 距离衰减（目前垂直、无衰减，但不再穿过液体；云遮光已按**光路湿度**（自顶向下累加、遇 `BlocksLight` 即停）做 Beer–Lambert 衰减，`CloudExtinction` 目前是代码常量 + Inspector 字段）；`float` 精度是否需要改成定点以利存档与回放比对。「Q 由物质携带」「温度是导出量 T = Q / C」「传导与散热由世界每逻辑帧自动做一次」这三条已是契约，不随上述决策改变。相变阈值（沸点 80 / 慢蒸发 20 / 露点 30）与湿度场系数（`Diffusion` / `HorizontalBias` / `RiseBias` / `RiseStopHumidity` / `RainThreshold` / `RainRate` / `EvaporationCeiling` / `VisibleThreshold` / `CloudFullHumidity`）都还是代码常量；**水循环尚未做最终配平**——去掉大气随机漂移后，100×100 水盆在 30000 tick 后稳定在海水 284/900、湿度 616（总量 900 守恒），剖面单调上密下疏（顶带 0.094、低带 0.002），已是可玩状态；若要进一步提高海水留存，动「蒸发节流 / 降水阈值 / 水平辐合」时须重测云层厚度。待定：要不要把「沸点」也做成可配项。**风险注记（审计 B9）**：取消旧「温度高于露点即跳过」的硬截断后，高温下 `SaturationHumidity` 随指数升高，**极热表面仍难降雨**（热地表可能积累湿斑）——C2 的指数饱和已缓解（低温空气反而更易凝结）但未根治；等观感出问题再加「温度连续凝结核 / 高温慢降雨」。
9. **土与湿土（`SoilParticle` / `WetSoilParticle`）的取舍**（当前实现见 §4「吸水与湿土的水迁移」）：
   - **已定：一格水润湿一格土、水随即消失**（土把正上方那格水喝掉），水的那份热量并进湿土。
   - **已定：湿土 = 干土 + 一份水**，这份水由湿土自己搬运——常温（`T < DryingTemperature` = 60）下**有压才渗**（正上方是水或湿土才有 50%/逻辑帧 的下渗，正下方优先、被挡则随机序试左右干土；孤立湿土永久持水 = 田间持水量，湿斑不沉底），受热（≥ 60°）改往上送（上方是干土、带 50% 概率门）或蒸发（上方是空气，带份额门控与潜热），且**每送一次自己就变干**。因此水不会凭空出现或消失（水总量 = 水格 + 湿土格 + 湿度场，全程守恒）。
   - **待定（已大幅缓解）：饱和层会挡住迁移**。迁移只看相邻一格，目标已是湿土就渗不动。现已加**侧向渗流**（被挡时随机序试左右干土）与**有压才渗**（无压即持水，湿斑不再穿柱沉底）；但四周都是湿土 / 固体时仍会卡住。若要「水总能找到路」需要更完整的地下水模型。
   - **待定：干燥阈值 60 与"只查相邻一格"**。60 是拍的（水：慢蒸发 20 / 沸腾 80）；斜上方有水不算、隔着沙也不算。参考现实可改成按"距最近水源"判定。
   - **已定：换格时水带热一起走**。`WetSoilParticle.GiveWater` 搬运时把 `ΔQ = T(源) × 1.2` 从源格挪到目标格（`1.2 = 湿土热容 2.0 − 干土热容 0.8`，即那一份水的热容份额），热容随物质一起变、两侧温度不因换格而跳，总热量严格守恒。旧「Q 留在原格不搬」已作废（详见 §4「吸水与湿土的水迁移」）。
   - **待定：常温不蒸发**。只有受热（≥ 60°）才往空气里蒸发，所以夜里/冷水里的湿土会一直保持湿。要不要给一条"常温极慢蒸发"的路（参考水粒子的 `SlowEvaporationTemperature` / 慢蒸发概率）。
   - 附：湿土的 `density 2.0` / `heatCapacity 2.0` / `thermalConductivity 0.85` 是取水与干土之间的中间值（水 6.5/0.95、干土 0.8/0.7），要不要按「孔隙含水率」正式推导。

---

## 14. 高速模式（High-Speed Mode）

**目标**：一键让世界「无人干预地长程演化」——进入后把倍速钉到**控制器满档**（`SpeedController.MaxSpeed`，默认 16×）、**锁掉一切编辑**，
跑满 `ParticleSimulationSettings._highSpeedLogicFrames`（默认 **6000**）个逻辑帧后**自动暂停**并结算
每种粒子的产生 / 死亡，玩家确认后退出、倍速钉回 1×（仍保持暂停，空格继续）。

### 分层

| 层 | 位置 | 职责 |
|---|---|---|
| 内核（纯 C#） | `Simulation/HighSpeedSession.cs` + `Simulation/HighSpeedMonitor.cs` | 状态机、产生 / 死亡计数、采样、完成判定、终态存量扫描；不引用 UnityEngine |
| 驱动 | `ParticleSimulationDriver`：`HighSpeed`（懒建，用 `settings.HighSpeedLogicFrames`）/ `EnterHighSpeed()` / `HighSpeedActive`；每逻辑帧转发一次 `OnLogicTick()`；见到 `Completed` 自动 `Pause()` | 只做转发与暂停 |
| 输入 / 显示 | `ParticleEditor`：`H` 进入；模式中**除空格（暂停）外的输入全锁**；左下角 HUD（**实测倍速** + 进度 + 实时地表温度 / 湿度）；结算面板用 `Enter` 或 `H` 确认退出 | 只做输入与渲染 |

### 内核 API（契约）

```csharp
namespace TapTap2026.Simulation
{
    public sealed class HighSpeedSession
    {
        public const int SampleInterval = 10;
        public HighSpeedSession(ParticleWorld world, int durationLogicTicks);  // duration < 1 钳到 1

        public bool Active { get; }                 // Enter 后、Exit 前
        public bool Completed { get; }              // 达到 duration（Active 仍为 true，直到 Exit）
        public int ElapsedLogicTicks { get; }
        public int DurationLogicTicks { get; }
        public int SampleCount { get; }
        public float LatestSurfaceTemperature { get; }   // 未采样时 float.NaN
        public float LatestAverageHumidity { get; }      // 未采样时 float.NaN
        public IReadOnlyList<float> SurfaceTemperatureSamples { get; }
        public IReadOnlyList<float> AverageHumiditySamples { get; }

        public void Enter(SpeedController speed);   // 幂等；把倍速钉到 MaxSpeed（手动档）
        public void Exit(SpeedController speed);    // 幂等；倍速钉回 MinSpeed，Active=false，清计数 / 计时 / 样本
        public void OnLogicTick();                  // 每逻辑帧一次（世界跑完这一帧之后）
        public long GetBirths(int typeId);
        public long GetDeaths(int typeId);
        public long GetFinalCount(int typeId);   // 跑满时长那一刻全网格扫一次：typeId 的存量格数（含空气；未结算 / 越界 / Exit 为 0）
    }
}
```

### 统计语义（产生 / 死亡）

`ParticleWorld` 持有一个 `internal HighSpeedMonitor _monitor`（`null` = 关闭；写路径只多一次判空，关闭时开销可忽略）。
`Set` / `SetPhase` / `Clear` 在**写完新格、`OnSpawn` 之前**调 `OnCellReplaced(oldId, newId, x, y)`：
`old ≠ new` 时 `deaths[old]++` 且 `births[new]++`——**空气（`typeId 0`）也是一条计数项**，显示时过滤。
`Swap` 是搬运、**不产生统计**，只在尾部调 `OnSwap(x0, y0, x1, y1)` 更新两端列的地表高度。
`Clear` 就是 `Set(..., Air)`，`Set` / `SetPhase` 的所有路径都覆盖；`Swap` 内部不走 `Set`，因此不会重复计数。
`OnKill` / `OnSpawn` 里再写格子会各走自己那次写操作的钩子——账目按**写操作**累积（一次相变链因此记成多笔）。

**O(1) 论证**：`OnCellReplaced` 只有两次 `long++`（计数数组按 typeId 直接寻址）加一次地表维护，
`OnSwap` 只有两次地表维护，都不扫网格；计数器在 `Enter` 时按类型表长度一次分配，写路径零分配。

### 地表定义与增量维护

**地表 = 该列最上方的非气体格**，判据与光照完全同款：`TagRank(tags) >= LiquidRank`（即带 `Solid` / `Liquid` 标签），
只看标签、不特判 typeId（见 §4「标签」与 §6 光照）。`HighSpeedMonitor._surfaceTop[x]` 保存这个 y
（`-1` = 该列全是气体 / 空）。标签在运行期不可变（§12「明确禁止」），故构造时把 typeId → 是否挡光摊成
`bool[]` 查表，钩子上只剩一次数组读。

- `Enter` 时对每列**自顶向下全扫一次**（一次性 O(W·H)）把 `_surfaceTop` 拉成真值。
- 之后每次单格改动 **O(1) 摊销**：本格是非气体且 `y > top[x]` → `top[x] = y`；
  本格是气体 / 空且 `y == top[x]` → 从 `y-1` 向下重扫该列（**罕见**：只有地表那一格恰好被抹除 / 变空时才发生）。
  `Swap` 对两端列各走一遍同样的维护（同行换位时两次维护都作用在同一列，仍然正确）。

### 采样

`OnLogicTick()` 每逻辑帧递增 `ElapsedLogicTicks`，每 `SampleInterval`（= 10）帧采一次（第 10、20、30… 帧），
`SampleCount` 与两个 `Samples` 列表同步增长、`Latest*` 更新；**未采样时两个 `Latest*` 都是 `float.NaN`**。

- **地表平均温度** = `Σ T(top[x]) / W`：只读每列地表格的温度（`HeatMap.GetTemperature`），一趟 O(W)，
  **不重扫网格**。没有非气体格的列贡献 0，除数恒为网格宽 W。
- **世界平均湿度** = `湿度总量 / (W·H)`：O(1)，直接读 `HumidityField.Total`。

### 终态存量扫描（完成时一次）

跑到 `duration` 那一刻（`OnLogicTick` 置 `Completed` **之前**）对全网格做**一次** `O(W·H)` 存量扫描：
遍历每格的 `typeId` 累加出**每种 typeId 的存量格数**（空气 `typeId 0` 也计），存入按类型表长度分配的
`long[]`，经 `GetFinalCount(typeId)` 读取——**未结算 / 越界 typeId / 已 `Exit`** 一律返回 0。
它是**纯观察者**：只读 `ParticleWorld.GetCell`，**零 RNG 消耗、零格子 / 场写入**，不影响世界推进结果；
此后 `OnLogicTick` 是空操作，存量定格在完成那一刻，不再重扫。这也是本模式唯一一次全网格读。

### 湿度总量：守恒核实结论与 O(1) 增量维护

**核实结论：`HumidityStepKernel` 的 pair 运送净守恒。** 内核每帧把旧场 `Array.Copy` 到新场，
其后的全部改动只经 `Transfer(next, budget, a, b, flow)`：正流量由给出方 `a` 出（超额时被 `budget[a]` 截断），
`next[a] -= flow` 且 `next[b] += flow`；负流量对称地由 `b` 出。**同一笔流量等量地从给出方扣、给接收方加**，
没有第二条写入路径（`budget` 只用于限流，不是场本身），内核里也**没有**任何下界截断式写入
（下界截断只出现在写路径 `Floor` 上，那正是需要逐笔记账的地方）。因此成对反对称、界内总量在实数意义下严格不变，
**不需要**在核心里加 delta 累加的 out 参数。

据此在写路径上增量维护 `internal double Total`：`Set` / `Add` / `AddAt` / `ResetAt` 各自在
「真值确实变了」的分支里按 `新值 − 旧值` 精确累加（`double` 累加避免逐次求和的舍入累积），
`Swap` 与 `Step` 是搬运、不动它。它只是 `Sum()` 的 **O(1) 镜像**：逐格真源仍是 `Sum()`，
两者只差「输送内核每笔 ±1 ulp 的浮点舍入」这类 ulp 级漂移，所以断言用容差而非逐位相等。

### 确定性：纯观察者保证

监视器与终态存量扫描**零 RNG 消耗、零格子写入、零场写入**——只读 `ParticleWorld.Cells` / `GetCell` 与类型标签；
`Enter` / `Exit` 只挂 / 摘 `_monitor`、清 / 置 `_finalCounts` 并改 `SpeedController` 状态，不碰网格。
因此「同种子 + 同命令序列」下，挂监视器（含跑满时长触发一次扫描）的世界与不挂的世界逐格（`typeId` / `flags` / `shade` / Q / 湿度）
以及随机流位置（后续 `NextUInt()` 序列）**完全一致**——`HighSpeedSessionTests` 有回归测试。

### 进入 / 退出语义

- `Enter(speed)`：**幂等**；`SetSpeed(SpeedController.MaxSpeed)`（钉到满档、**手动档**——不是自适应；跑不满
  由预算封顶，实测吞吐看 `EffectiveSpeed`），创建并挂上监视器，清零计时 / 样本 / 两个 `Latest*` 与终态存量。
- `Exit(speed)`：**幂等**；摘监视器、清计数与终态存量，再 `SetSpeed(SpeedController.MinSpeed)`（钉回 1×——普通编辑模式
  恒 1×，不恢复进入前的档位）。**暂停由驱动 / 编辑器负责**，内核不管暂停。
- `OnLogicTick()` 在未进入或已 `Completed` 时是**空操作**——所以完成后 `ElapsedLogicTicks` 精确停在
  `duration`，不会溢出（满程样本数 = `duration / 10`），终态存量也定格在完成那一刻（不再重扫）。
