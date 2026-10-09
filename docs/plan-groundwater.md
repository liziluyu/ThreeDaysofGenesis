# 地下水规则实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 固体置换液体前若左右同一行都被固体堵住则不成立——石头 / 沙 / 土里封闭的水不再上浮，成为不动的「地下水」。

**架构：** 纯读取规则，零新增状态。判定挂在 `ParticleRules.CanDisplace`（唯一置换闸门，直落与斜落净空链共用）；开关放在 `ParticleWorld`（默认开，可 A/B 对照）。不改 `Cell` 布局、注册表、渲染、驱动。

**技术栈：** Unity 2022.3.62f1 / C# / NUnit EditMode（286 个既有用例）。设计文档：`docs/design-groundwater.md`。

**运行测试：** 编辑器在线优先用 MCP `tests-run`（EditMode）；离线用批处理（**不要加 `-quit`**）：

```powershell
& 'D:\Unity\2022.3.62f1\Editor\Unity.exe' -batchmode -runTests `
  -projectPath . -testPlatform EditMode -testResults Temp\results.xml -logFile Temp\unity-editmode.log
```

**提交：** 每任务一次本地提交；**不推送远程**（用户要求）。新文件的 `.meta` 由 Unity 生成，提交时一并 `git add`。

---

## 文件清单

| 文件 | 动作 | 职责 |
|---|---|---|
| `Assets/Scripts/Simulation/ParticleRules.cs` | 修改 | `CanDisplace` 加地下水闸门 + `HasLateralEscape` 判据 + 注释契约 |
| `Assets/Scripts/Simulation/ParticleWorld.cs` | 修改 | `GroundwaterSealingEnabled` 开关（默认 `true`） |
| `Assets/Tests/EditMode/GroundwaterTests.cs` | 新建 | 规则单测 + 水囊稳定 + 联通地下水 + 挖开释放 + 休眠 A/B + 气体不受影响 |
| `Assets/Tests/EditMode/WaterParticleTests.cs` | 修改 | 改写竖井用例为新语义 + 新增开阔水域下沉回归 |
| `Assets/Scripts/Simulation/ParticleIds.cs` | 修改 | 水条目的注释同步 |
| `Assets/Scripts/Simulation/Particles/WaterParticle.cs` | 修改 | 头注释同步（「固体沉穿水」加例外） |
| `docs/ParticleTypeInterface.md` | 修改 | §4 置换规则补例外；§6 共享原语说明补一句；§10 文件清单水一行同步 |
| `AGENTS.md` | 修改 | 「跨类型规则只看标签」处补一句 |

---

### Task 1: 规则 + 开关 + 置换闸门单测

**Files:**
- Modify: `Assets/Scripts/Simulation/ParticleRules.cs`（`CanDisplace`，约 104–134 行）
- Modify: `Assets/Scripts/Simulation/ParticleWorld.cs`（`_motionSleepEnabled` 附近，约 114 行与 375 行）
- Create: `Assets/Tests/EditMode/GroundwaterTests.cs`

- [ ] **Step 1: 新建测试文件与闸门单测**

创建 `Assets/Tests/EditMode/GroundwaterTests.cs`：

```csharp
using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 地下水规则（<see cref="ParticleRules.CanDisplace"/> 的「被夹住不放行」闸门）：
    /// 固体置换液体前若左右同一行都被固体堵住（越界按固体算），置换不成立。
    /// 单测走隔离类型表；水囊 / 联通 / 释放 / 休眠 A/B 走生产注册路径（依赖 ParticleIds 回填）。
    /// </summary>
    public sealed class GroundwaterTests
    {
        private const uint SeedValue = 20261009u;

        // 隔离类型表：水 = 1、石头 = 2（TestTables.Build 约定第 n 个粒子 = typeId n + 1）。
        private const ushort WaterId = 1;
        private const ushort StoneId = 2;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static ParticleWorld CreateIsolatedWorld(int width, int height)
        {
            return new ParticleWorld(
                width, height, SeedValue,
                TestTables.Build(new WaterParticle(), new StoneParticle()));
        }

        private static ParticleWorld CreateProductionWorld(int width, int height)
        {
            return new ParticleWorld(width, height, SeedValue);
        }

        private static void FillRect(ParticleWorld world, int x0, int y0, int x1, int y1, ushort typeId)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Assert.IsTrue(world.Set(x, y, typeId), $"Set({x},{y}) 应成功");
                }
            }
        }

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>布局哈希（typeId / shade / 非 bit0 的 flags；FNV-1a，无分配）。</summary>
        private static ulong LayoutHash(ParticleWorld world)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                Cell[] cells = world.Cells;
                for (int i = 0; i < cells.Length; i++)
                {
                    h = (h ^ cells[i].typeId) * 1099511628211UL;
                    h = (h ^ cells[i].shade) * 1099511628211UL;
                    h = (h ^ (ulong)(cells[i].flags & CellFlags.ProcessedParityMask)) * 1099511628211UL;
                }

                return h;
            }
        }

        /// <summary>全状态哈希（布局 + 每格热量 / 湿度位模式）。</summary>
        private static ulong StateHash(ParticleWorld world)
        {
            unchecked
            {
                ulong h = LayoutHash(world);
                for (int y = 0; y < world.Height; y++)
                {
                    for (int x = 0; x < world.Width; x++)
                    {
                        h = (h ^ (ulong)System.BitConverter.SingleToInt32Bits(world.Heat.GetHeat(x, y))) * 1099511628211UL;
                        h = (h ^ (ulong)System.BitConverter.SingleToInt32Bits(world.Humidity.Get(x, y))) * 1099511628211UL;
                    }
                }

                return h;
            }
        }

        /// <summary>跑一个逻辑周期（3 运动帧 + 1 逻辑帧，同生产节拍）。</summary>
        private static void RunCycles(ParticleWorld world, int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                world.StepMotion();
                world.StepMotion();
                world.StepMotion();
                world.StepLogic();
            }
        }

        [Test]
        public void CanDisplace_SolidWedgedBetweenSolids_CannotEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, StoneId);
            world.Set(1, 0, WaterId);

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "左右都被固体堵住 → 置换液体不成立");
        }

        [Test]
        public void CanDisplace_AnyOpenSide_CanEnterLiquid()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(1, 0, WaterId);
            // 右侧 (2,1) 是空气：旁路存在 → 允许置换。
            Assert.IsTrue(ParticleRules.CanDisplace(world, 1, 1, 1, 0));
        }

        [Test]
        public void CanDisplace_WorldEdgeCountsAsBlockedSide()
        {
            ParticleWorld world = CreateIsolatedWorld(1, 2);
            world.Set(0, 1, StoneId);
            world.Set(0, 0, WaterId);

            Assert.IsFalse(
                ParticleRules.CanDisplace(world, 0, 1, 0, 0),
                "两侧越界按固体算 → 置换不成立");
        }

        [Test]
        public void CanDisplace_AirTargetIsUnaffected()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, StoneId);
            // (1,0) 保持空气：目标是气体/空 → 本闸门不适用（气体照旧可被置换）。
            Assert.IsTrue(ParticleRules.CanDisplace(world, 1, 1, 1, 0));
        }

        [Test]
        public void GroundwaterSealingDisabled_RestoresLegacyDisplacement()
        {
            ParticleWorld world = CreateIsolatedWorld(3, 2);
            world.Set(0, 1, StoneId);
            world.Set(1, 1, StoneId);
            world.Set(2, 1, StoneId);
            world.Set(1, 0, WaterId);
            world.GroundwaterSealingEnabled = false;

            Assert.IsTrue(
                ParticleRules.CanDisplace(world, 1, 1, 1, 0),
                "开关关 → 退回旧的 rank 置换行为");
        }
    }
}
```

- [ ] **Step 2: 跑一次确认编译失败（TDD 红灯）**

Run: 批处理 `-runTests`（或 MCP `tests-run`）。
Expected: 编译错误——`GroundwaterSealingEnabled` 尚不存在（C# 下红灯即编译失败）。

- [ ] **Step 3: 实现开关**

`Assets/Scripts/Simulation/ParticleWorld.cs`，在 `private bool _motionSleepEnabled = true;`（约 114 行）下一行加字段：

```csharp
        private bool _groundwaterSealingEnabled = true;
```

在 `MotionSleepEnabled` 属性（约 375 行）之后加属性：

```csharp
        /// <summary>
        /// 地下水规则开关（默认 <c>true</c>）：固体置换液体前，若它左右同一行都被固体堵住
        /// （越界按固体算），置换不成立——石头 / 沙 / 土里封闭的水不再被上方固体挤上去。
        /// 为 false 时退回旧的 rank 置换行为（A/B 对照与回归验证）。
        /// </summary>
        /// <remarks>
        /// 纯读取规则：不开状态、不消耗随机流、读距 (x±1, y) 在类型表运动阅读盒并集之内，
        /// 运动休眠透明性不受影响（回归见 <c>GroundwaterTests.MotionSleepTransparency_WithGroundwaterRule</c>）。
        /// </remarks>
        public bool GroundwaterSealingEnabled
        {
            get { return _groundwaterSealingEnabled; }
            set { _groundwaterSealingEnabled = value; }
        }
```

- [ ] **Step 4: 实现判定**

`Assets/Scripts/Simulation/ParticleRules.cs`：把 `CanDisplace` 末尾的 `return TagRank(self.Info.Tags) > TagRank(other.Info.Tags);` 替换为：

```csharp
            int selfRank = TagRank(self.Info.Tags);
            int otherRank = TagRank(other.Info.Tags);

            // 地下水：固体置换液体前，若它左右同一行都被固体堵住（越界按固体算），
            // 液体无旁路可让（活塞效应）→ 这次置换不成立。只约束「固体 → 液体」；
            // 斜落净空链同样经过本方法，水囊拐角因此也进不去。
            if (selfRank > LiquidRank
                && (other.Info.Tags & ParticleTags.Liquid) != 0
                && world.GroundwaterSealingEnabled
                && !HasLateralEscape(world, fromX, fromY))
            {
                return false;
            }

            return selfRank > otherRank;
```

并更新 `CanDisplace` 的 XML 注释（把「高 rank 可以置换低 rank」改成「高 rank 可以置换低 rank，但固体置换液体还要求左右至少一侧有旁路，见 `HasLateralEscape`」）。

在同一类里加两个方法（放在 `IsSolid` 之后）：

```csharp
        /// <summary>
        /// 地下水规则的「旁路」判据：固体在 (x,y) 下落置换液体前，左右同一行是否至少有一侧不是固体
        /// （越界按固体算）。两侧都被堵 = 被夹在固体之间（活塞），液体无处可让。
        /// </summary>
        public static bool HasLateralEscape(ParticleWorld world, int x, int y)
        {
            return IsOpenSide(world, x - 1, y) || IsOpenSide(world, x + 1, y);
        }

        /// <summary>(x,y) 是否是可让液体通过的「旁路」：界内且不带 <see cref="ParticleTags.Solid"/> 标签。</summary>
        private static bool IsOpenSide(ParticleWorld world, int x, int y)
        {
            return world.IsInBounds(x, y) && !IsSolid(world, x, y);
        }
```

- [ ] **Step 5: 跑测试确认绿灯**

Run: 批处理 `-runTests`（可加 `-testFilter GroundwaterTests` 缩范围）。
Expected: 5 个新用例全过；输出 `Test run completed. Exiting with code 0`。

- [ ] **Step 6: 本地提交**

```bash
git add Assets/Scripts/Simulation/ParticleRules.cs Assets/Scripts/Simulation/ParticleWorld.cs Assets/Tests/EditMode/GroundwaterTests.cs Assets/Tests/EditMode/GroundwaterTests.cs.meta
git commit -m "feat(sim): 地下水规则——左右被固体夹住的固体不能置换液体"
```

---

### Task 2: 水囊稳定（含画刷形状）

**Files:**
- Test: `Assets/Tests/EditMode/GroundwaterTests.cs`（追加）

- [ ] **Step 1: 追加两个用例**

追加到 `GroundwaterTests`（`LayoutHash` 之前的位置随意）：

```csharp
        [Test]
        public void WaterPocketInStone_StaysBitIdentical()
        {
            ParticleWorld world = CreateProductionWorld(12, 12);
            FillRect(world, 0, 0, 11, 11, ParticleIds.Stone);
            FillRect(world, 4, 4, 6, 6, ParticleIds.Water);

            ulong before = LayoutHash(world);
            int waterBefore = CountType(world, ParticleIds.Water);

            RunCycles(world, 120);

            Assert.AreEqual(before, LayoutHash(world), "石头里的水囊与整块石头都不许动");
            Assert.AreEqual(waterBefore, CountType(world, ParticleIds.Water), "水量守恒");
        }

        [Test]
        public void BrushShapedWaterPocketInStone_StaysBitIdentical()
        {
            ParticleWorld world = CreateProductionWorld(20, 16);
            FillRect(world, 0, 0, 19, 15, ParticleIds.Stone);
            // 画刷形状：4×3 主体 + 斜边（模拟手绘的水囊边界）。
            FillRect(world, 6, 6, 9, 8, ParticleIds.Water);
            world.Set(10, 8, ParticleIds.Water);
            world.Set(10, 9, ParticleIds.Water);
            world.Set(11, 9, ParticleIds.Water);

            ulong before = LayoutHash(world);

            RunCycles(world, 120);

            Assert.AreEqual(before, LayoutHash(world), "画刷画出的水囊不许动");
        }
```

- [ ] **Step 2: 跑测试确认绿灯**

Run: 批处理（或 MCP `tests-run`）。
Expected: 全过。若「水囊在动」——说明闸门没生效或斜落路径漏网，停下核查 `CanDisplace`（`TrySlide` 逐格调用它）。

- [ ] **Step 3: 本地提交**

```bash
git add Assets/Tests/EditMode/GroundwaterTests.cs
git commit -m "test(sim): 地下水——封闭水囊与画刷水囊逐位稳定的回归"
```

---

### Task 3: 竖井用例改写 + 开阔水域下沉回归

**Files:**
- Modify: `Assets/Tests/EditMode/WaterParticleTests.cs`（类头注释、约 232–269 行的 `Sand_SinksThrough_Water`）

- [ ] **Step 1: 改写竖井用例为新语义**

把 `WaterParticleTests.cs` 中 `Sand_SinksThrough_Water`（约 233–269 行，含 XML 注释）整体替换为：

```csharp
        /// <summary>
        /// 地下水规则（活塞效应）：沙被**两侧石壁夹住**时推不动水——水无处可让，置换不成立。
        /// 沙停在水面上、水柱纹丝不动。（旧语义是「沙沉穿水」，随地下水设计改写；
        /// 开阔水域的下沉由 <see cref="Sand_SinksThrough_Water_InOpenBasin"/> 守住。）
        /// </summary>
        [Test]
        public void Sand_RestsOnWater_InOneWideStoneShaft()
        {
            ParticleWorld world = CreateSandWorld(8, 12);
            FillRow(world, 0, StoneId);

            // 两侧石墙把水关在一格宽的竖井里——沙在其中左右都被夹住。
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

            Assert.AreEqual(SandId, world.GetCell(4, 4).typeId, "被石壁夹住的沙停在水面上（活塞顶住）");
            Assert.AreEqual(WaterId, world.GetCell(4, 1).typeId, "水柱纹丝不动");
            Assert.AreEqual(WaterId, world.GetCell(4, 2).typeId);
            Assert.AreEqual(WaterId, world.GetCell(4, 3).typeId);
            Assert.AreEqual(3, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(1, CountType(world, SandId), "沙量守恒");
        }

        /// <summary>
        /// 开阔水域照旧：两侧石墙离得足够远（沙左右是水，有旁路）时，沙依旧沉穿水柱到底、
        /// 等量的水被置换上浮。这是地下水规则不得破坏的既有玩法。
        /// </summary>
        [Test]
        public void Sand_SinksThrough_Water_InOpenBasin()
        {
            ParticleWorld world = CreateSandWorld(9, 12);
            FillRow(world, 0, StoneId);

            // 两侧石墙离水池足够远：池内每一列的水左右都有水的旁路。
            for (int y = 1; y < 12; y++)
            {
                world.Set(1, y, StoneId);
                world.Set(7, y, StoneId);
            }

            // 5×4 水池，池面正上方一颗沙。
            for (int y = 1; y <= 4; y++)
            {
                for (int x = 2; x <= 6; x++)
                {
                    world.Set(x, y, WaterId);
                }
            }

            world.Set(4, 6, SandId);

            for (int i = 0; i < 120; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(SandId, world.GetCell(4, 1).typeId, "开阔水域：沙照旧沉到底");
            Assert.AreEqual(20, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(1, CountType(world, SandId), "沙量守恒");
            Assert.AreEqual(5, HighestTypeY(world, WaterId), "被挤出的水浮到原水面之上（池底被沙占掉一格）");
        }
```

- [ ] **Step 2: 追加整块沙下沉用例与 `HighestTypeY` 辅助（辅助放在 `CountType` 之后）**

```csharp
        [Test]
        public void SandBlock_SinksThrough_Water_InOpenBasin()
        {
            ParticleWorld world = CreateSandWorld(13, 14);
            FillRow(world, 0, StoneId);

            for (int y = 1; y < 14; y++)
            {
                world.Set(1, y, StoneId);
                world.Set(11, y, StoneId);
            }

            // 9 格宽 × 6 深的水池，池面正上方一整块 3×3 沙。
            for (int y = 1; y <= 6; y++)
            {
                for (int x = 2; x <= 10; x++)
                {
                    world.Set(x, y, WaterId);
                }
            }

            for (int y = 7; y <= 9; y++)
            {
                for (int x = 5; x <= 7; x++)
                {
                    world.Set(x, y, SandId);
                }
            }

            for (int i = 0; i < 300; i++)
            {
                world.StepLogic();
            }

            Assert.AreEqual(9, CountType(world, SandId), "沙量守恒");
            Assert.AreEqual(54, CountType(world, WaterId), "水量守恒");
            Assert.AreEqual(SandId, world.GetCell(6, 1).typeId, "整块沙沉降到底（同行相邻格腾出的水就是旁路）");
            Assert.LessOrEqual(HighestTypeY(world, SandId), 6, "沙已沉到原水面之下");
            Assert.GreaterOrEqual(HighestTypeY(world, WaterId), 7, "被挤出的水升到原水面之上");
        }

        /// <summary>某类型的最高一格的 y；不存在时返回 -1。</summary>
        private static int HighestTypeY(ParticleWorld world, ushort typeId)
        {
            for (int y = world.Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        return y;
                    }
                }
            }

            return -1;
        }
```

- [ ] **Step 3: 同步类头注释**

把类头 XML 注释里的「被固体沉穿」改为「被固体沉穿（左右未被夹住时；被夹住则按地下水规则不让路）」。

- [ ] **Step 4: 跑测试**

Run: 批处理（或 MCP `tests-run`；可 `-testFilter WaterParticleTests`）。
Expected: 两用例全过。若开阔水域用例「沙停在半途」——检查测试几何里水池是否够宽（沙左右必须有一侧是水）。

- [ ] **Step 5: 本地提交**

```bash
git add Assets/Tests/EditMode/WaterParticleTests.cs
git commit -m "test(sim): 竖井用例改写为地下水语义，新增开阔水域下沉回归"
```

---

### Task 4: 联通地下水 + 挖开释放

**Files:**
- Test: `Assets/Tests/EditMode/GroundwaterTests.cs`（追加）

- [ ] **Step 1: 追加两个用例**

```csharp
        [Test]
        public void GroundwaterConnectedToPond_StaysStill_AndSandStopsAtShaftMouth()
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

            Assert.AreEqual(ParticleIds.Sand, world.GetCell(5, 4).typeId, "沙停在竖井口（左右是石壁，被夹住）");
            Assert.AreEqual(1, CountType(world, ParticleIds.Sand), "沙量守恒");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(3, 2).typeId, "水囊纹丝不动");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 2).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(3, 3).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(4, 3).typeId);
            Assert.AreEqual(ParticleIds.Water, world.GetCell(5, 2).typeId, "竖井里的水纹丝不动");
            Assert.AreEqual(ParticleIds.Water, world.GetCell(5, 3).typeId);
            Assert.AreEqual(4 + 3 + 20, CountType(world, ParticleIds.Water), "水总量守恒");
        }

        [Test]
        public void DiggingBesidePocket_ReleasesGroundwater()
        {
            ParticleWorld world = CreateProductionWorld(8, 8);
            FillRect(world, 0, 0, 7, 7, ParticleIds.Stone);
            world.Set(3, 3, ParticleIds.Water);
            world.Set(4, 3, ParticleIds.Water);

            // 在水囊左侧挖一口贯通到顶的竖井：被夹条件不再成立，水恢复普通水行为。
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

- [ ] **Step 2: 加 `CountTypeInColumn` 辅助（放在 `CountType` 之后）**

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

- [ ] **Step 3: 跑测试**

Run: 批处理（或 MCP `tests-run`；可 `-testFilter GroundwaterTests`）。
Expected: 全过。若「联通」用例里沙越过竖井口继续下沉——检查 `HasLateralEscape` 是否真的读到了竖井壁。

- [ ] **Step 4: 本地提交**

```bash
git add Assets/Tests/EditMode/GroundwaterTests.cs
git commit -m "test(sim): 地下水——联通池塘不被动、挖开即释放"
```

---

### Task 5: 运动休眠 A/B + 气体不受影响

**Files:**
- Test: `Assets/Tests/EditMode/GroundwaterTests.cs`（追加）

- [ ] **Step 1: 追加两个用例与场景构建**

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
                    // 挖掉井壁：被夹住的沙应恢复下落——休眠路径必须与全网格路径逐位一致。
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

        /// <summary>一格宽石井（井壁 x=3、x=5）+ 井底三格水 + 井口上方一颗沙（停在 (4,4)）。</summary>
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

- [ ] **Step 2: 跑测试**

Run: 批处理（或 MCP `tests-run`；可 `-testFilter GroundwaterTests`）。
Expected: 全过。休眠 A/B 若发散——说明 `(x±1, y)` 读距没被写入唤醒并集覆盖，检查 `MotionReadBox` 声明与并集构造。

- [ ] **Step 3: 本地提交**

```bash
git add Assets/Tests/EditMode/GroundwaterTests.cs
git commit -m "test(sim): 地下水——休眠透明性 A/B 与气体不受影响回归"
```

---

### Task 6: 文档同步 + 全量回归

**Files:**
- Modify: `Assets/Scripts/Simulation/ParticleIds.cs`（水条目，约 26–29 行的 `<summary>`）
- Modify: `Assets/Scripts/Simulation/Particles/WaterParticle.cs`（头注释第 39 行附近）
- Modify: `docs/ParticleTypeInterface.md`（§4 约 224–228 行；§6 约 932 行；§10 约 1279 行）
- Modify: `AGENTS.md`（「跨类型规则只看标签不看 typeId」处）

- [ ] **Step 1: 同步 `ParticleIds.cs` 与 `WaterParticle.cs` 注释**

`ParticleIds.cs` 水条目的 `<summary>` 改为：

```csharp
        /// <summary>水。受重力的液体（Liquid 标签）：下落 → 斜滑 → 横流摊平，无安息角；被固体沉穿
        /// （**左右未被固体夹住时**——被夹住则按地下水规则不让路，见 ParticleRules.HasLateralEscape）。</summary>
```

`WaterParticle.cs` 头注释「跨类型交互全部由标签 rank 产生：固体（rank 3）沉穿水，水被置换上浮；」改为：

```csharp
    //       跨类型交互全部由标签 rank 产生：固体（rank 3）沉穿水，水被置换上浮——
    //       但固体左右都被固体堵住时置换不成立（地下水规则），水因此能停在石头/沙/土里不动；
```

- [ ] **Step 2: 同步 `docs/ParticleTypeInterface.md`**

在 §4「置换规则」（约 224–228 行，`rank(自己) > rank(目标)` 那段）之后追加：

```markdown
**地下水（例外）**：固体置换**液体**还多一道闸门（`ParticleWorld.GroundwaterSealingEnabled`，默认开）：
若固体在落下前**左右同一行两侧都被固体堵住**（越界按固体算），这次置换不成立——液体无旁路可让，
固体推不动它。石头 / 沙 / 土里封闭的水因此停在原地成为「地下水」；开阔水域（左右有液体 / 气体旁路）
不受影响，沙石照旧沉底。斜落净空链同样经过 `CanDisplace`，水囊拐角也进不去。气体不受此闸门约束。
```

在 §6 的「共享原语内置了静态格拒绝」一段（约 932 行）末尾补一句：

```markdown
`CanDisplace` 还内置「固体置换液体时左右被固体夹住则拒绝」的地下水闸门（默认开，`ParticleWorld.GroundwaterSealingEnabled`）。
```

在 §10 文件清单的水一行（约 1279 行）把「沙沉穿水置换上浮」改为「沙沉穿水置换上浮（开阔水域）/ 被石壁夹住的竖井里沙停在水面（地下水规则）」。

- [ ] **Step 3: 同步 `AGENTS.md`**

「**跨类型规则只看标签不看 typeId**：`ParticleTags` 的 rank 次序 … 决定谁置换谁（`ParticleRules`）。」之后补一句：

```markdown
固体置换液体另有一道「地下水」闸门（`ParticleWorld.GroundwaterSealingEnabled` 默认开）：下落者左右同一行都被固体堵住（越界按固体算）时置换不成立——石头/沙/土里封闭的水不再被挤上浮，成为不动的「地下水」；开阔水域照旧（见 `docs/design-groundwater.md`）。
```

- [ ] **Step 4: 全量回归**

Run: 批处理 `-runTests`（不加过滤，跑全部 EditMode）。
Expected: 全部通过（286 + 新增 ≈ 295 个；退出码 0）。

若出现失败：逐个判断是「新语义的预期改变」（更新断言并在注释里写明原因）还是实现 bug（修实现）。**分不清就停下来问用户**，不要为了让测试变绿而弱化断言。

- [ ] **Step 5: 本地提交**

```bash
git add Assets/Scripts/Simulation/ParticleIds.cs Assets/Scripts/Simulation/Particles/WaterParticle.cs docs/ParticleTypeInterface.md AGENTS.md
git commit -m "docs: 同步地下水规则的接口文档、类型注释与测试清单"
```

---

## 完成标准

- 全部 EditMode 测试通过（含 5 个闸门单测与 6 个行为用例）。
- `Sand_RestsOnWater_InOneWideStoneShaft` / `Sand_SinksThrough_Water_InOpenBasin` 分别锁定「夹住不让路」与「开阔照旧沉」。
- 水囊与画刷水囊逐位稳定；联通池塘的地下水稳定；挖开即释放；休眠 A/B 逐位一致；气穴照旧冒泡。
- 文档四处同步完成。
