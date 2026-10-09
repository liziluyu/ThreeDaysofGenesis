# 地下水规则实施计划（v2：运动状态判定）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 固体置换液体前必须处于运动中（上一运动扫描位移过或刚放置）——静止岩石无论形状、是否与池塘联通都不再被抽动，成为稳定的「地下水」。

**架构：** `ParticleWorld` 加逐格 `_lastMovedTick`（byte）+ `HasMomentum` 查询，写入在 `Swap` / `Set` / `SetPhase` 里维护；`ParticleRules.CanDisplace` 把 v1 的「左右被夹住」闸门换成「运动中」闸门（删除 `HasLateralEscape`/`IsOpenSide`）。v1 规则实测会漏（画刷形状水囊、联通池塘被逐格抽上去），设计文档 `docs/design-groundwater.md` 已更新为 v2。

**技术栈：** Unity 2022.3.62f1 / C# / NUnit EditMode。

**测试：**
- **离线通道**（委托子代理执行；本机 Unity 安装目录的 Roslyn + MonoBleedingEdge，编译真实源码并反射跑 `[Test]`）：
  ```bash
  bash /tmp/offline-tests/run.sh                       # 全量（约 3 分钟；跳过 CameraPanTests / DialogueRunnerTests：依赖真实 UnityEngine/编辑器）
  bash /tmp/offline-tests/run.sh GroundwaterTests      # 指定 fixture
  bash /tmp/offline-tests/run.sh WaterParticleTests.cs # 指定文件
  ```
  基线（改动前）：total=279 pass=276 fail=3（3 个失败全部是 GroundwaterTests 里被 v1 规则漏洞影响的用例）。
- **真机复跑**（用户执行）：编辑器在线用 MCP `tests-run`；离线用批处理（编辑器实际装在 `D:\uni all\editor\2022.3.62f1`，**不要加 `-quit`**）。

**提交：** 每任务一次本地提交；**不推送远程**。新文件 `.meta` 由 Unity 生成、后续补交。

---

## 文件清单

| 文件 | 动作 | 职责 |
|---|---|---|
| `Assets/Scripts/Simulation/ParticleWorld.cs` | 修改 | `_lastMovedTick` 状态 + `HasMomentum` + `Swap`/`Set`/`SetPhase` 标记 |
| `Assets/Scripts/Simulation/ParticleRules.cs` | 修改 | 闸门改为「运动中」；删除 `HasLateralEscape`/`IsOpenSide` |
| `Assets/Tests/EditMode/GroundwaterTests.cs` | 修改 | 单测换成运动状态语义；行为用例保留并校正 |
| `Assets/Tests/EditMode/WaterParticleTests.cs` | 修改 | 竖井用例改回「照旧沉底」；保留开阔水域两个回归 |
| `Assets/Scripts/Simulation/ParticleIds.cs`、`Particles/WaterParticle.cs` | 修改 | 注释同步 |
| `docs/ParticleTypeInterface.md`、`AGENTS.md` | 修改 | 文档同步（含修正编辑器路径） |

---

### Task 1: 运动状态 + 闸门替换 + 单测

**Files:**
- Modify: `Assets/Scripts/Simulation/ParticleWorld.cs`
- Modify: `Assets/Scripts/Simulation/ParticleRules.cs`
- Modify: `Assets/Tests/EditMode/GroundwaterTests.cs`

- [ ] **Step 1: 改 `ParticleWorld`（状态 + 查询 + 写标记）**

字段（放在 `_groundwaterSealingEnabled` 附近）：

```csharp
        /// <summary>
        /// 逐格「最近一次位移的扫描序号」（byte 存储，与 <see cref="Tick"/> 按模 256 比较）。
        /// 地下水闸门用它判断固体是否处于运动中；写入点：<see cref="Swap"/>（两端格与两格正上方）、
        /// <see cref="Set"/> / <see cref="SetPhase"/>（新格记「上一扫描」，等价刚放置的视作刚运动过）。
        /// </summary>
        private readonly byte[] _lastMovedTick;
```

构造函数里（`_processedBits` 分配处附近）：`_lastMovedTick = new byte[_cells.Length];`

新增方法（放在 `GroundwaterSealingEnabled` 属性附近）：

```csharp
        /// <summary>
        /// (x,y) 上的内容是否处于运动中：上一运动扫描位移过（或本帧新放置，见 <see cref="_lastMovedTick"/>）。
        /// 越界返回 false。地下水闸门（<see cref="ParticleRules.CanDisplace"/>）读它。
        /// </summary>
        public bool HasMomentum(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            return (byte)(_tick - _lastMovedTick[y * _width + x]) == 1;
        }

        /// <summary>把 (x,y) 的内容标记为「本次扫描位移过」。</summary>
        private void MarkMoved(int x, int y)
        {
            _lastMovedTick[y * _width + x] = (byte)_tick;
        }

        /// <summary>把 (x,y) 正上方那格标记为「位移过」——它是下一步会跟随落下的同伴。</summary>
        private void MarkMovedAbove(int x, int y)
        {
            int above = y + 1;
            if ((uint)above < (uint)_height)
            {
                _lastMovedTick[above * _width + x] = (byte)_tick;
            }
        }
```

在 `Swap` 里、写完两格之后（与 `MarkProcessed(a)` / `MarkProcessed(b)` 相邻处）加：

```csharp
            // 地下水闸门：两端格与两格正上方都记「本次扫描位移过」。
            MarkMoved(x0, y0);
            MarkMoved(x1, y1);
            MarkMovedAbove(x0, y0);
            MarkMovedAbove(x1, y1);
```

在 `Set` 与 `SetPhase` 里、写完 `cell.typeId` / `cell.flags` 之后加：

```csharp
            // 地下水闸门：刚放置 / 相变出来的物质视作「上一扫描位移过」——画笔直接放进水里的沙石照旧下沉。
            _lastMovedTick[index] = (byte)(_tick - 1);
```

- [ ] **Step 2: 改 `ParticleRules`（闸门替换 + 删除 v1 判据）**

删除 `HasLateralEscape` 与 `IsOpenSide` 两个方法；`CanDisplace` 末尾改为：

```csharp
            int selfRank = TagRank(self.Info.Tags);
            int otherRank = TagRank(other.Info.Tags);

            // 地下水：只有「运动中」的固体才能排开液体（上一运动扫描位移过，或刚放置）。
            // 静止的岩石无论如何都不会把水挤上去——水囊形状、是否与池塘联通都不影响。
            if (selfRank > LiquidRank
                && (other.Info.Tags & ParticleTags.Liquid) != 0
                && world.GroundwaterSealingEnabled
                && !world.HasMomentum(fromX, fromY))
            {
                return false;
            }

            return selfRank > otherRank;
```

`CanDisplace` 的 XML 注释同步改为「高 rank 可以置换低 rank，但**固体置换液体**还要求该固体处于运动中（见 `ParticleWorld.HasMomentum`）」。

- [ ] **Step 3: 改单测（`GroundwaterTests.cs`）**

删除 v1 的 5 个闸门单测（`CanDisplace_SolidWedgedBetweenSolids_CannotEnterLiquid`、`CanDisplace_AnyOpenSide_CanEnterLiquid`、`CanDisplace_LiquidSideIsAnEscape`、`CanDisplace_GasSideIsAnEscape`、`CanDisplace_WorldEdgeCountsAsBlockedSide`），替换为：

```csharp
        [Test]
        public void CanDisplace_StaticSolid_CannotEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(1, 1, StoneId);
            world.Set(1, 0, StoneId);   // 先垫底，让上面的石头静止下来
            world.StepMotion();          // 宽限期过期（石头下方是石头，不动）
            world.Set(1, 0, WaterId);    // 垫底换成水
            world.StepMotion();          // 石头已静止 → 推不动水（也不应移动）

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "静止石头不能置换液体");
        }

        [Test]
        public void CanDisplace_SolidThatJustFell_CanEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 3);
            world.Set(0, 2, StoneId);
            world.Set(0, 0, WaterId);
            world.StepMotion();          // 石头落进 (0,1)（空气目标不受闸门约束）→ 获得运动状态

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "刚下落的石头可以置换液体");
        }

        [Test]
        public void CanDisplace_FreshlyPlacedSolid_CanEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 2);
            world.Set(0, 1, StoneId);
            world.Set(0, 0, WaterId);

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "刚放置的石头视作运动中（画笔直接放进水里照旧下沉）");
        }
```

`CanDisplace_AirTargetIsUnaffected` 与 `GroundwaterSealingDisabled_RestoresLegacyDisplacement` 保留原样（后者仍有效 ✔）。

- [ ] **Step 4: 离线跑，验证规则替换**

Run: `bash /tmp/offline-tests/run.sh GroundwaterTests`
Expected: **全部通过**（3 个运动状态单测 + 空气目标 + 开关关 + 水囊稳定 2 个 + 联通 / 挖开 2 个——注意最后两个的期望是按 v1 写的，见 Task 3；若它们失败，先确认失败原因与设计一致再交给控制器判定，不要自行改断言）。

- [ ] **Step 5: 本地提交**

```bash
git add Assets/Scripts/Simulation/ParticleWorld.cs Assets/Scripts/Simulation/ParticleRules.cs Assets/Tests/EditMode/GroundwaterTests.cs
git commit -m "feat(sim): 地下水改运动状态判定——静止固体不能置换液体"
```

---

### Task 2: 竖井用例改回「照旧沉底」

**Files:**
- Modify: `Assets/Tests/EditMode/WaterParticleTests.cs`

- [ ] **Step 1: 恢复原始竖井用例**

把 `Sand_RestsOnWater_InOneWideStoneShaft`（含其 XML 注释与「活塞」措辞）整体替换回原用例（仍在 1 格宽石竖井里，但沙照旧沉底）：

```csharp
        /// <summary>
        /// rank 规则的集成验证：沙（Solid，rank 3）从水柱上方落下，沉穿水到底；
        /// 等量的水被置换上浮。沙与水各自守恒。（运动状态闸门：下落的沙本身在运动中，照旧可排开液体。）
        /// </summary>
        [Test]
        public void Sand_SinksThrough_Water()
        {
            ParticleWorld world = CreateSandWorld(8, 12);
            FillRow(world, 0, StoneId);

            // 两侧石墙把水关在一格宽的竖井里——没有侧壁水会横流摊开（那是正确行为，
            // 由 Water_LevelsOut_OnFlatFloor 验证），本测试专看竖直方向上的置换。
            for (int y = 1; y <= 6; y++)
            {
                world.Set(3, y, StoneId);
                world.Set(5, y, StoneId);
            }
            // 三格水柱立在井底，井口上方一颗沙。
            for (int y = 1; y <= 3; y++)
            {
                world.Set(4, y, WaterId);
            }

            world.Set(4, 6, SandId);

            for (int i = 0; i < 60; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(SandId, world.GetCell(4, 1).typeId, "沙应沉到水柱底部");
            Assert.AreEqual(WaterId, world.GetCell(4, 2).typeId, "水被置换上浮");
            Assert.AreEqual(WaterId, world.GetCell(4, 3).typeId);
            Assert.AreEqual(WaterId, world.GetCell(4, 4).typeId);
            Assert.AreEqual(3, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(1, CountType(world, SandId), "沙量守恒");
        }
```

- [ ] **Step 2: 类头注释改回**

类头里的「被固体沉穿（左右未被夹住时；被夹住则按地下水规则不让路）」改回「被固体沉穿（下落的固体在运动中，照旧可排开液体；静止固体则不能——见地下水规则）」。

- [ ] **Step 3: 离线跑**

Run: `bash /tmp/offline-tests/run.sh WaterParticleTests.cs`
Expected: 全部通过（含竖井沉底与两个开阔水域回归）。

- [ ] **Step 4: 本地提交**

```bash
git add Assets/Tests/EditMode/WaterParticleTests.cs
git commit -m "test(sim): 竖井用例改回照旧沉底（运动状态判定下活塞代价消失）"
```

---

### Task 3: 联通地下水 + 挖开释放（按 v2 期望加入）

**Files:**
- Test: `Assets/Tests/EditMode/GroundwaterTests.cs`

- [ ] **Step 1: 追加 `CountTypeInColumn` 辅助（放在 `CountType` 之后）**

```csharp
        /// <summary>某一列 x 上某类型的格数。</summary>
        private static int CountTypeInColumn(ParticleWorld world, int x, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                if (world.GetCell(x, y).typeId == typeId)
                {
                    count++;
                }
            }

            return count;
        }
```

- [ ] **Step 2: 追加两个用例**

```csharp
        [Test]
        public void GroundwaterConnectedToPond_StaysStill_AndSandDrillsTheShaft()
        {
            ParticleWorld world = CreateProductionWorld(12, 12);

            // 石层 y = 0..4（整层石头）。
            FillRect(world, 0, 0, 11, 4, ParticleIds.Stone);

            // 水囊（2×2）+ 一格宽的竖井（y = 2..4），竖井把水囊与上方池塘联通。
            FillRect(world, 3, 2, 4, 3, ParticleIds.Water);
            world.Set(5, 2, ParticleIds.Water);
            world.Set(5, 3, ParticleIds.Water);
            world.Set(5, 4, ParticleIds.Water);

            // 两侧挡墙 + 池塘（水面 y = 5..6）。
            for (int y = 5; y <= 8; y++)
            {
                world.Set(0, y, ParticleIds.Stone);
                world.Set(11, y, ParticleIds.Stone);
            }
            FillRect(world, 1, 5, 10, 6, ParticleIds.Water);

            // 池塘上方丢一颗沙，正对竖井。
            world.Set(5, 8, ParticleIds.Sand);

            RunCycles(world, 200);

            // 下落的沙在运动中：沿竖井沉到井底，把井里的水逐格挤上去（1:1）；石头静止不塌，水囊不受扰动。
            Assert.AreEqual(ParticleIds.Sand, world.GetCell(5, 2).typeId, "沙沿竖井沉到井底");
            Assert.AreEqual(1, CountType(world, ParticleIds.Sand), "沙量守恒");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(5, 3).typeId, "井里的水被挤上去一格");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(5, 4).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(3, 2).typeId, "水囊纹丝不动");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 2).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(3, 3).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 3).typeId);
            Assert.AreEqual(4 + 3 + 20, CountType(world, ParticleIds.Water), "水总量守恒");
        }

        [Test]
        public void DiggingBesidePocket_ReleasesGroundwater()
        {
            ParticleWorld world = CreateProductionWorld(8, 8);
            FillRect(world, 0, 0, 7, 7, ParticleIds.Stone);
            world.Set(3, 3, ParticleIds.Water);
            world.Set(4, 3, ParticleIds.Water);

            // 在水囊左侧挖一口贯通到顶的竖井：水由自身流动排出（石头静止不塌）。
            for (int y = 1; y <= 7; y++)
            {
                world.Clear(2, y);
            }

            RunCycles(world, 200);

            Assert.AreEqual(2, CountType(world, ParticleIds.Water), "水量守恒");
            Assert.AreNotEqual(ParticleIds.Water, world.GetCell(3, 3).typeId, "水囊已排空");
            Assert.AreNotEqual(ParticleIds.Water, world.GetCell(4, 3).typeId);
            Assert.AreEqual(2, CountTypeInColumn(world, 2, ParticleIds.Water), "两格水都流进竖井");
        }
```

- [ ] **Step 3: 离线跑验证**

Run: `bash /tmp/offline-tests/run.sh GroundwaterTests`
Expected: 全绿。判据（不满足就停下报告，**不要**为了变绿而弱化断言）：
1. **岩石静止**——水囊 (3,2)(4,2)(3,3)(4,3) 全程是水；若被扰动，是实现 bug。
2. **守恒**——沙 1、水 27（联通用例）/ 水 2（挖开用例）。
3. 沙与水的具体坐标若与断言不符（例如沙没沉到 (5,2)），把实测的确定不动点坐标写进断言并在报告里说明依据。

- [ ] **Step 4: 本地提交**

```bash
git add Assets/Tests/EditMode/GroundwaterTests.cs
git commit -m "test(sim): 联通地下水与挖开释放（运动状态判定下的期望）"
```

---

### Task 4: 休眠 A/B + 气体不受影响

**Files:**
- Test: `Assets/Tests/EditMode/GroundwaterTests.cs`

- [ ] **Step 1: 追加两个用例**

```csharp
        [Test]
        public void MotionSleepTransparency_WithGroundwaterRule()
        {
            ParticleWorld sleeping = BuildSleepScene();
            ParticleWorld full = BuildSleepScene();
            full.MotionSleepEnabled = false;

            const int BeforeDig = 20;
            const int AfterDig = 120;
            for (int cycle = 0; cycle < BeforeDig + AfterDig; cycle++)
            {
                if (cycle == BeforeDig)
                {
                    // 挖掉井壁：井里的沙应因支撑消失而重新下落——休眠路径必须与全网格路径逐位一致。
                    sleeping.Clear(5, 4);
                    full.Clear(5, 4);
                }

                sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepMotion(); sleeping.StepLogic();
                full.StepMotion(); full.StepMotion(); full.StepMotion(); full.StepLogic();

                Assert.AreEqual(
                    StateHash(full), StateHash(sleeping), $"第 {cycle} 周期休眠与全网格应逐位一致");
            }
        }

        [Test]
        public void GasPocketInStone_StillRises()
        {
            ParticleWorld world = CreateProductionWorld(3, 8);
            FillRect(world, 0, 0, 2, 7, ParticleIds.Stone);
            world.Set(1, 2, ParticleIds.Atmosphere);

            RunCycles(world, 60);

            Assert.AreEqual(ParticleIds.Atmosphere, world.GetCell(1, 7).typeId, "气穴照旧冒泡升到最顶");
            Assert.AreEqual(1, CountType(world, ParticleIds.Atmosphere), "气量守恒");
        }

        /// <summary>一格宽石井（井壁 x=3、x=5）+ 井底三格水 + 井口上方一颗沙。</summary>
        private static ParticleWorld BuildSleepScene()
        {
            ParticleWorld world = CreateProductionWorld(9, 8);
            FillRect(world, 0, 0, 8, 0, ParticleIds.Stone);
            for (int y = 1; y <= 5; y++)
            {
                world.Set(3, y, ParticleIds.Stone);
                world.Set(5, y, ParticleIds.Stone);
            }

            world.Set(4, 1, ParticleIds.Water);
            world.Set(4, 2, ParticleIds.Water);
            world.Set(4, 3, ParticleIds.Water);
            world.Set(4, 5, ParticleIds.Sand);
            return world;
        }
```

- [ ] **Step 2: 离线跑 + 本地提交**

Run: `bash /tmp/offline-tests/run.sh GroundwaterTests`
Expected: 全绿（气穴用例的期望「升到最顶」若与实测不符，报告实际值——气体目标不受闸门约束，理论上应照旧冒泡）。

```bash
git add Assets/Tests/EditMode/GroundwaterTests.cs
git commit -m "test(sim): 地下水——休眠透明性 A/B 与气体不受影响回归"
```

---

### Task 5: 文档同步 + 全量离线 + 真机复跑

**Files:**
- Modify: `Assets/Scripts/Simulation/ParticleIds.cs`、`Assets/Scripts/Simulation/Particles/WaterParticle.cs`
- Modify: `docs/ParticleTypeInterface.md`、`AGENTS.md`

- [ ] **Step 1: 类型注释同步**

`ParticleIds.cs` 水条目 `<summary>` 里「被固体沉穿」后补「（**下落的固体在运动中**——静止固体推不动水，见 `ParticleRules.CanDisplace` 的地下水闸门）」。`WaterParticle.cs` 头注释「固体（rank 3）沉穿水，水被置换上浮」补「——但**静止**的固体不能；水因此能在石头 / 沙 / 土里成为不动的「地下水」」。

- [ ] **Step 2: `docs/ParticleTypeInterface.md` 同步**

§4 置换规则段后追加（简述，不写理由）：

```markdown
**地下水闸门**：固体置换**液体**还要求该固体处于**运动中**（上一运动扫描位移过，或刚放置；见 `ParticleWorld.HasMomentum`，
开关 `ParticleWorld.GroundwaterSealingEnabled` 默认开）。静止的固体无论形状、是否与池塘联通都不能把液体挤上去——
石头 / 沙 / 土里封闭的水因此成为不动的「地下水」；下落的沙石照旧沉底（下落本身即运动）。气体目标不受此闸门约束。
```

§6「共享原语」一段补一句指向该闸门；§10 文件清单的水一行改为「沙沉穿水置换上浮（下落的沙在运动中）」+ 水囊稳定 / 联通 / 挖开等新用例说明。

- [ ] **Step 3: `AGENTS.md` 同步 + 修正编辑器路径**

「**跨类型规则只看标签不看 typeId**」段后补一句：

```markdown
固体置换液体另有一道「地下水」闸门（`ParticleWorld.GroundwaterSealingEnabled` 默认开）：只有**运动中**的固体（上一运动扫描位移过，或刚由 `Set`/`SetPhase` 放置）能排开液体——静止岩石不会把水挤上去，石头/沙/土里封闭的水因此稳定不动；下落的沙石照旧沉底（见 `docs/design-groundwater.md`）。
```

并把 `Development Commands` 与 `Runtime/Tooling Preferences` 里的编辑器路径 `D:\Unity\2022.3.62f1` 更正为实际路径 `D:\uni all\editor\2022.3.62f1`（本机已验证）。

- [ ] **Step 4: 全量离线 + 请用户真机复跑**

Run: `bash /tmp/offline-tests/run.sh`
Expected: 除被跳过的 2 个文件（CameraPanTests / DialogueRunnerTests）外全绿；然后请用户跑一次真机 EditMode（MCP `tests-run` 或批处理）。

- [ ] **Step 5: 本地提交**

```bash
git add Assets/Scripts/Simulation/ParticleIds.cs Assets/Scripts/Simulation/Particles/WaterParticle.cs docs/ParticleTypeInterface.md AGENTS.md
git commit -m "docs: 同步地下水运动状态闸门的接口文档、类型注释与编辑器路径"
```

---

## 完成标准

- 离线全量除跳过的 2 个文件外全绿；用户真机复跑通过。
- 水囊（含画刷形状）、联通池塘的地下水稳定；竖井 / 开阔水域照旧下沉；挖开即释放；休眠 A/B 逐位一致；气穴照旧冒泡。
- 文档四处同步完成（含编辑器路径更正）。
