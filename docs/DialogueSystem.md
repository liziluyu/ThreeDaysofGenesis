# 对话系统（DialogueSystem）

> 适用：Unity 2022.3.62f1 / TextMeshPro 3.0.7 / uGUI 1.0.0 / legacy Input Manager
> 位置：`Assets/Scripts/Driver/Dialogue*.cs`（程序集 `TapTap2026.Game`）；对话文件 `Assets/Data/Dialogues/`
> 与粒子系统（`docs/ParticleTypeInterface.md`）是**互相独立**的两个领域。
> **2026-10-16：分支（选项 / 段跳转）已彻底移除**——一个文件就是一条**线性**台词序列，`segments` / `choices` / `gotoSegment` 三个字段不存在了。

---

## 1. 组成

| 文件 | 角色 |
|---|---|
| `DialogueData.cs` | **纯 C#**：JSON 数据类 + `TryParse`（`JsonUtility`，免第三方依赖） |
| `DialogueRunner.cs` | **纯 C# 状态机**：进度、`Next` / `OnEnd`。不碰 UI / 输入 / 协程 |
| `DialogueLineView.cs` | 显示层数据：一行（说话人 + 名字 + 头像/立绘 + 整句 + 旁白标记） |
| `IDialogueView.cs` | **显示层契约**（由 UI 实现，控制器持有）：`ShowLine` / `SetRevealedText` / `CloseView` |
| `DialogueController.cs` | **UI 无关核心**（MonoBehaviour）：热键、打字机协程、暂停模拟、说话人解析；显示全部经 `IDialogueView` |
| `DialogueDebugView.cs` | **调试用 UI**（实现 `IDialogueView`；UI 层级是**场景里的可编辑对象**——`Dialogue Canvas` 下的对话面板（立绘 / 头像 / 名字 / 台词），本组件只持引用并填数据）；真实 UI 可禁用它并 `SetView` |
| `Assets/Data/Dialogues/*.json` | 对话文件（`TextAsset`）：`example_dialogue.json` 示例 + `phase_01_intro` / `phase_01_retry` 阶段开场 |
| `Assets/Tests/EditMode/DialogueRunnerTests.cs` | 状态机与解析的 EditMode 测试 |

---

## 2. 对话文件格式（JSON）

一个文件 = **一段对话** = 一串**按顺序播**的台词（`lines`）。

```json
{
  "id": "intro",
  "onEnd": "start_simulation",
  "characters": [
    { "id": "mei",  "displayName": "Mei" },
    { "id": "chen", "displayName": "Chen" }
  ],
  "lines": [
    { "speaker": "mei",  "text": "你终于来了，今晚海边的风很大。" },
    { "speaker": "chen", "text": "那就走吧，别等天黑。" }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `id` | 对话标识（调试 / 日志用） |
| `onEnd` | 对话播完派发的事件名；**空 = 无事件** |
| `characters[]` | 本对话用到的角色表（`speaker` id → 显示名）；立绘 / 头像共用该 id |
| `characters[].id` | 角色 id（与 `lines[].speaker` 对应，**同时是两个资源文件夹里的文件名**） |
| `characters[].displayName` | 名字框；空 → 回退用 id |
| `lines[]` | 台词，**从 `lines[0]` 起逐句播** |
| `lines[].speaker` | 角色 id（查角色表）；**空 = 旁白** |
| `lines[].text` | 台词 |

**边界**：`lines` 缺省 / 空 → 解析失败（`TryParse` 返回 false + 错误文案，控制器 `LogError`，不弹 UI、不暂停模拟）；播到 `lines` 尾巴 → 有 `onEnd` 则派发一次事件后结束，否则直接结束。
**没有分支 / 跳转 / 条件 / 变量**。旧格式的 `segments` / `choices` / `gotoSegment` 已删除，带这些字段的文件会因缺 `lines` 而**解析失败**（响亮的失败，不是静默忽略）。

---

## 3. 公开 API（`DialogueController`）

```csharp
public TextAsset DialogueFile;                 // public 变量：默认对话文件
public event Action<string> OnDialogueEnd;     // 对话播完（参数 = onEnd 事件名）
public bool IsActive { get; }                  // 是否有对话在播

public bool StartDialogue(TextAsset asset);    // 直接给文件
public bool StartDialogue(string dialogueId);  // 按注册表 id
public void Advance();                         // 打字中→补全本行；否则→下一句（尾句再按一次即收尾）
public void SetView(IDialogueView view);       // 注入/替换显示层（传 null 退回调试视图）
public void CloseDialogue();                   // 强制关闭（跳过 onEnd）
```

- 失败（文件 null / JSON 坏 / id 查不到）→ `Debug.LogError` + 返回 `false`，不弹 UI、不暂停模拟。
- 播放中再次 `StartDialogue` → **重置重启**。

---

## 4. 角色资源（名字 / 立绘 / 头像）——完全由 JSON + 文件夹驱动

**没有 Inspector 角色表**。每段对话在自己的 JSON 里用 `characters` 段声明角色（`id` + `displayName`）；**立绘与头像共用同一个 id**，按它去两个 `Resources` 文件夹各找一次同名 Sprite：

| 资源 | 位置（相对 `Assets/Resources`，路径可在控制器上改） |
|---|---|
| 立绘 | `Characters/Portraits/<speaker id>` |
| 头像 | `Characters/Avatars/<speaker id>` |

- 加载：`Resources.Load<Sprite>(folder + "/" + id)`，**按 (文件夹, id) 缓存**，同一角色只查一次。
- 图片需导入为 **Sprite (Single)**（这样 `Load` 直接拿到 Sprite）。
- **缺图** → 退回**程序生成的占位色块**（按角色 id 哈希取固定色相，确定性、同 id 同色），按 id 缓存。
- **`characters` 里没有该 `speaker`** → 名字用 `speaker` 本身 + 两个文件夹仍试查 + 一次性警告。
- **旁白（`speaker` 空）** → 隐藏名字 / 立绘 / 头像，只显示文字。
- 立绘与头像**分开存、分开查**（**不同时显示**，视图按需切换），但**共用一个文件名**。

---

## 5. 状态机（`DialogueRunner`）

- `Start(file)`：`_index = 0`；`lines` 空 / 文件为 null → 立即 `Finished`。
- `Next()`：`_index++`；越过尾句 → 触发 `OnEnd`（一次）+ `Finished`，返回 `false`；否则返回 `true`（还能继续）。
- 状态只有 `_file` / `_index` / `_finished` / `_onEndFired` 四个字段，**没有任何跳转**——下标只增不减。

---

## 6. 运行时表现（UI / 输入 / 暂停）

- **显示层走接口**：控制器**不碰任何 UI 类型**，全部显示通过 `IDialogueView`（`ShowLine` / `SetRevealedText` / `CloseView`）。换对话框（例如接入另一个项目的 UI）只需实现该接口并 `SetView`。
- **自带调试 UI**：`DialogueDebugView`。它的 Canvas + 对话面板**摆在场景里**（`Dialogue Canvas`，overlay、sortingOrder 1000；由编辑器菜单 `Tools/TapTap2026/Scene UI Setup` 幂等补齐与接线；**禁止运行该工具**——字体步骤见 `docs/modules/EditorTooling.md`），面板在场景里默认隐藏、可直接在编辑器里可视化编辑。**整张 `Dialogue Canvas` 的显隐 = 是否在对话**：`Awake` 关掉整根画布、`ShowLine`（首句）打开、`CloseView`（对话结束）关掉；面板自己的开关只管画布内部那几屏。运行初态由 `Awake` 强制保证。同一物体上存在它即作为默认视图；真实 UI 里禁用它并 `SetView(自己的视图)` 即可。
- **打字机**：协程按 `_charsPerSecond` 逐字输出；打字中按"下一句"→ 立即补全；纯文本（不解析 TMP 标签）。
- **热键**（`KeyCode` 字段，可改）：`Space`/左键 = 下一句；`T` = 调试播放示例。
- **暂停模拟**：开始对话 `driver.Pause()`、结束 `driver.Resume()`；**只恢复"本次对话才暂停的"**（不误恢复玩家原本的手动暂停）。
- **输入屏蔽**：对话激活时 `ParticleEditor.SuppressInput = true`——**热键与画笔一起挡**（否则左键"下一句"会同时画粒子、`Space` 会同时切暂停）。

---

## 7. 场景接线（一次性）

1. 场景里已有 `Dialogue Controller` 对象（挂 `DialogueController` + `DialogueDebugView`），已接线 `_driver` / `_editor`。
2. **首次使用 TMP**：`Window > TextMeshPro > Import TMP Essential Resources`。
3. **中文字体**：字体在**场景里逐个 `TMP_Text` 设**（`DialogueDebugView` 不套字体，见下）。注意字体是用户的**只读区**（`AGENTS.md` 铁律「任何情况下都不许动字体设置」）：不要改字体资产、导入设置、`TMP Settings`，也不要改任何 `TMP_Text` 的字体引用与字号。
   > 2026-10-16 起 `DialogueDebugView.Awake` 里那段「把整张对话画布下每个 `TMP_Text` 统一改成 `_font`」的代码**已删除**（它会抹平逐组件设的字体）。`_font` 字段保留（保住场景里的原有接线）但**不再被读写**。清单见 `docs/modules/ProjectSetup.md`「字体」。
   > 中文字体缺字时显示豆腐块 □——若出现，是场景里那个 `TMP_Text` 的字体没覆盖到该字，属用户的字体配置范畴。
4. **角色图**：立绘放 `Assets/Resources/Characters/Portraits/`、头像放 `Assets/Resources/Characters/Avatars/`，**两个文件夹里用同一个文件名 = 角色 id**（= JSON 里 `characters[].id`，也就是 `lines[].speaker`）（导入类型 **Sprite (Single)**）。缺图会自动用占位色块，不会报错。
5. 可选：把 `example_dialogue.json` 拖到 `Debug Dialogue`（或 `Dialogue File`），按 `T` 即可播示例。
6. 其它脚本触发对话：`GetComponent<DialogueController>().StartDialogue("intro")`（或 `StartDialogue(textAsset)`）。

**`DialogueDebugView` 的 6 个接线**（`SceneWiringTests` 钉住）：`_canvas` / `_dialoguePanel` / `_portraitImage` / `_avatarImage` / `_nameText` / `_lineText`。`_font` 是历史字段（不再被读写，只为保住场景里的原有接线）；2026-10-16 分支移除时**删掉了 `_controller`**（原先只有选项按钮回调需要它，已随选项一起失效）。

---

## 8. 测试

`DialogueRunnerTests`（11 项，EditMode）：解析（合法 / 坏 JSON / 无 `lines` / `characters` 段 / `characters` 缺省为空）、起点、逐句推进（尾句返回 `false` 并结束）、`OnEnd` 只触发一次 / 无 `onEnd` 不触发、重启重置、空 `lines` 立即结束。

UI、打字机、热键、`SuppressInput`、暂停联动**不进 EditMode**（属 MonoBehaviour / UI / 输入，需 Play 模式观察）。

---

## 9. 已知取舍

- 无中文字体时文本豆腐块 □（字体归用户在场景里逐组件维护，见 §7.3）。
- 触发只给 `StartDialogue` API + 调试热键；真正的 NPC 交互等有玩家/NPC 再接。
- 打字机纯文本，不解析 TMP 富文本标签。
- **对话是线性的**：没有分支 / 条件 / flag / 变量，一个文件就是一条台词序列。要「按状态说不同的话」得在**游戏流程层**做（`GameController` 按阶段 / 是否重进 / 高速模式过关与否选不同的对话 id——`dialogueId` / `repeatDialogueId` / `successDialogueId` / `failureDialogueId`，见 `docs/GameFlow.md`），而不是在对话文件里。
