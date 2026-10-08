using System.Collections.Generic;

namespace TapTap2026.Game
{
    /// <summary>
    /// 回顾面板的一条记录（**已解析好显示名**，实现方不必知道角色表）：普通句 or「玩家选择」。
    /// </summary>
    public readonly struct DialogueHistoryEntryView
    {
        /// <summary>显示名（控制器已按角色表解析；旁白 / 选择记录为空串）。</summary>
        public readonly string DisplayName;

        /// <summary>台词，或「&gt; Chose: XXX」。</summary>
        public readonly string Text;

        /// <summary>true = 这是一条「玩家做了选择」的记录。</summary>
        public readonly bool IsChoiceMark;

        public DialogueHistoryEntryView(string displayName, string text, bool isChoiceMark)
        {
            DisplayName = displayName;
            Text = text;
            IsChoiceMark = isChoiceMark;
        }
    }

    /// <summary>
    /// 对话**显示层**契约：**由 UI 实现**，控制器持有并调用它。
    /// <para>换对话框（例如接入另一个项目的 UI）只需实现本接口并调 <see cref="DialogueController.SetView"/>；
    /// 对话逻辑（进度 / 分支 / 打字机节奏 / 输入 / 暂停模拟）全在控制器里，不随 UI 改变。</para>
    /// <para>控制器自带的运行时 UI 是 <see cref="DialogueDebugView"/>（调试用），它同样实现本接口。</para>
    /// </summary>
    /// <remarks>
    /// **输入归控制器**：热键（下一句 / 回顾）由 <see cref="DialogueController"/> 读取；
    /// 实现方只需在玩家点了选项按钮时回调 <see cref="DialogueController.Choose"/>。
    /// </remarks>
    public interface IDialogueView
    {
        /// <summary>显示新的一行：名字 / 头像 / 立绘 / 整句。随后打字机会通过 <see cref="SetRevealedText"/> 逐字揭示。</summary>
        void ShowLine(DialogueLineView line);

        /// <summary>打字机当前**已显示的前缀**（画在哪、字号多大由实现方决定）。</summary>
        void SetRevealedText(string partial);

        /// <summary>展示选项（文字列表）。实现方在玩家点第 i 个时调 <see cref="DialogueController.Choose"/>。</summary>
        void ShowChoices(IReadOnlyList<string> labels);

        /// <summary>收起选项（进入下一句 / 对话结束）。</summary>
        void HideChoices();

        /// <summary>展示回顾面板；历史由控制器给出，实现方只负责渲染与滚动。</summary>
        void ShowReview(IReadOnlyList<DialogueHistoryEntryView> history);

        /// <summary>收起回顾面板。</summary>
        void HideReview();

        /// <summary>收起整个对话界面（对话结束 / 强制关闭）。</summary>
        void CloseView();
    }
}
