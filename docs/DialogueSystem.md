# 对话系统（DialogueSystem）

> 适用：Unity 2022.3.62f1 / TextMeshPro 3.0.7 / uGUI 1.0.0 / legacy Input Manager
> 位置：`Assets/Scripts/Driver/Dialogue*.cs`（程序集 `TapTap2026.Game`）；示例文件 `Assets/Data/Dialogues/`
> 与粒子系统（`docs/ParticleTypeInterface.md`）是**互相独立**的两个领域。

---

## 1. 组成

| 文件 | 角色 |
|---|---|
| `DialogueData.cs` | **纯 C#**：JSON 数据类 + `TryParse`（`JsonUtility`，免第三方依赖） |
| `DialogueRunner.cs` | **纯 C# 状态机**：进度、历史、`Next` / `Choose` / `OnEnd`。不碰 UI / 输入 / 协程 |
| `DialogueLineView.cs` | 显示层数据：一行（说话人 + 名字 + 头像/立绘 + 整句 + 旁白标记） |
| `IDialogueView.cs` | **显示层契约**（由 UI 实现，控制器持有）：`ShowLine` / `SetRevealedText` / `ShowChoices` / `HideChoices` / `ShowReview` / `HideReview` / `CloseView` |
| `DialogueController.cs` | **UI 无关核心**（MonoBehaviour）：热键、打字机协程、暂停模拟、说话人解析；显示全部经 `IDialogueView` |
| `DialogueDebugView.cs` | **调试用 UI**（实现 `IDialogueView`；UI 层级是**场景里的可编辑对象**——`Dialogue Canvas` 下的对话面板 / 回顾面板 / 选项按钮模板，本组件只持引用并填数据）；真实 UI 可禁用它并 `SetView` |
| `Assets/Data/Dialogues/example_dialogue.json` | 示例对话（`TextAsset`） |
| `Assets/Tests/EditMode/DialogueRunnerTests.cs` | 状态机与解析的 EditMode 测试 |

---

## 2. 对话文件格式（JSON）

一个文件 = **一段对话**，内含若干**命名段**（`segments`）；分支用「段之间跳转」表达（JSON 里无法引用 Unity 资源）。

```json
{
  "id": "intro",
  "onEnd": "start_simulation",
  "characters": [
    { "id": "mei",  "displayName": "Mei" },
    { "id": "chen", "displayName": "Chen" }
  ],
  "segments": [
    { "id": "start", "lines": [
      { "speaker": "mei", "text": "你终于来了，今晚海边的风很大。" },
      { "speaker": "chen", "text": "走哪边？",
        "choices": [
          { "text": "左边", "gotoSegment": "left" },
          { "text": "右边", "gotoSegment": "right" } ] } ] },
    { "id": "left",  "lines": [ { "speaker": "chen", "text": "往左。" } ] },
    { "id": "right", "lines": [ { "speaker": "chen", "text": "往右。" } ] }
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
| `segments[]` | 命名段落；**默认从 `segments[0]` 开始** |
| `segments[].id` | 段名（`gotoSegment` 的跳转目标） |
| `segments[].lines[].speaker` | 角色 id（查角色表）；**空 = 旁白** |
| `segments[].lines[].text` | 台词 |
| `segments[].lines[].choices` | 选项（可选）；有此字段时本句**停下等玩家选** |
| `choices[].text` / `choices[].gotoSegment` | 选项文字 / 选中后跳转的段 id |

**边界**：`choices` 空/缺省 = 普通句；段播完 → 有 `onEnd` 则派发一次事件后结束，否则直接结束；`gotoSegment` 指向不存在的段 → 警告日志 + 视为结束（不崩溃）。

---

## 3. 公开 API（`DialogueController`）

```csharp
public TextAsset DialogueFile;                 // public 变量：默认对话文件
public event Action<string> OnDialogueEnd;     // 对话播完（参数 = onEnd 事件名）
public bool IsActive { get; }                  // 是否有对话在播

public bool StartDialogue(TextAsset asset);    // 直接给文件
public bool StartDialogue(string dialogueId);  // 按注册表 id
public void Advance();                         // 打字中→补全本行；否则→下一句
public bool Choose(int index);                 // 选项按钮点第 i 个时回调
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

- `Start(file)`：定位 `segments[0]`，第 1 句记入 `History`；空段立即 `Finished`。
- `Next()`：前进下一句；**选项点阻塞**（返回 `false`，必须 `Choose`）；到段尾 → 触发 `OnEnd`（一次）+ `Finished`。
- `Choose(i)`：把选择记入 `History`（`isChoiceMark=true`）+ 跳转到 `gotoSegment`。
- `History`：已呈现的所有句（含当前句与"选择"记录），回顾 UI 直接读它。

---

## 6. 运行时表现（UI / 输入 / 暂停）

- **显示层走接口**：控制器**不碰任何 UI 类型**，全部显示通过 `IDialogueView`（`ShowLine` / `SetRevealedText` / `ShowChoices` / `ShowReview` / `CloseView` …）。换对话框（例如接入另一个项目的 UI）只需实现该接口并 `SetView`。
- **自带调试 UI**：`DialogueDebugView`。它的 Canvas + 对话面板 + 回顾面板**摆在场景里**（`Dialogue Canvas`，overlay、sortingOrder 1000；由编辑器菜单 `Tools/TapTap2026/Scene UI Setup` 幂等补齐与接线），两个面板与选项按钮模板在场景里默认隐藏、可直接在编辑器里可视化编辑；选项按钮运行时从场景里的 `ChoiceButtonTemplate` 实例化。**整张 `Dialogue Canvas` 的显隐 = 是否在对话**：`Awake` 关掉整根画布、`ShowLine`（首句）打开、`CloseView`（对话结束）关掉；面板自己的开关只在画布内部切「哪一屏」。运行初态由 `Awake` 强制保证。同一物体上存在它即作为默认视图；真实 UI 里禁用它并 `SetView(自己的视图)` 即可。
- **打字机**：协程按 `_charsPerSecond` 逐字输出；打字中按"下一句"→ 立即补全；纯文本（不解析 TMP 标签）。
- **热键**（`KeyCode` 字段，可改）：`Space`/左键 = 下一句；`Tab` = 回顾；`Esc`/`Tab` = 关回顾；`T` = 调试播放示例。
- **回顾**：独立面板，多行 `TMP_Text` + `ScrollRect`（滚轮滚动），打开时按当前 `History` 重建；回顾打开时**屏蔽下一句**。
- **暂停模拟**：开始对话 `driver.Pause()`、结束 `driver.Resume()`；**只恢复"本次对话才暂停的"**（不误恢复玩家原本的手动暂停）。
- **输入屏蔽**：对话激活时 `ParticleEditor.SuppressInput = true`——**热键与画笔一起挡**（否则左键"下一句"会同时画粒子、`Space` 会同时切暂停）。

---

## 7. 场景接线（一次性）

1. 场景里已有 `Dialogue Controller` 对象（挂 `DialogueController`），已接线 `_driver` / `_editor`。
2. **首次使用 TMP**：`Window > TextMeshPro > Import TMP Essential Resources`。
3. **中文字体**：把中文 TMP 字体资产拖到 **`DialogueDebugView`** 的 `Font` 字段（不接则中文显示成豆腐块 □；真实 UI 有自己的字体配置）。
4. **角色图**：立绘放 `Assets/Resources/Characters/Portraits/`、头像放 `Assets/Resources/Characters/Avatars/`，**两个文件夹里用同一个文件名 = 角色 id**（= JSON 里 `characters[].id`，也就是 `lines[].speaker`）（导入类型 **Sprite (Single)**）。缺图会自动用占位色块，不会报错。
5. 可选：把 `example_dialogue.json` 拖到 `Debug Dialogue`（或 `Dialogue File`），按 `T` 即可播示例。
6. 其它脚本触发对话：`GetComponent<DialogueController>().StartDialogue("intro")`（或 `StartDialogue(textAsset)`）。

---

## 8. 测试

`DialogueRunnerTests`（15 项，EditMode）：解析（合法 / 坏 JSON / 无 segments / characters 段 / characters 缺省为空）、起点、逐句推进、选项点阻塞、分支跳转、坏跳转警告、`OnEnd` 只触发一次 / 无 onEnd 不触发、历史顺序与选择标记、重启重置、空段立即结束。

UI、打字机、热键、`SuppressInput`、暂停联动**不进 EditMode**（属 MonoBehaviour / UI / 输入，需 Play 模式观察）。

---

## 9. 已知取舍

- 无中文字体时文本豆腐块 □（等接 `DialogueDebugView.Font`）。
- 触发只给 `StartDialogue` API + 调试热键；真正的 NPC 交互等有玩家/NPC 再接。
- 打字机纯文本，不解析 TMP 富文本标签。
- 不做条件跳转（按 flag 分支）。
