using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 一行对话要显示的**全部数据**（纯数据，不含任何 UI 类型）。控制器填好后交给 <see cref="IDialogueView"/>，
    /// 因此换 UI 不需要改对话逻辑。
    /// </summary>
    public readonly struct DialogueLineView
    {
        /// <summary>角色 id（对话 JSON 里的 <c>speaker</c>）。</summary>
        public readonly string SpeakerId;

        /// <summary>要显示的名字（角色表 <c>displayName</c>；查不到回退 id；旁白为空）。</summary>
        public readonly string DisplayName;

        /// <summary>小头像 Sprite（可能为程序生成的占位图）；旁白为 null。</summary>
        public readonly Sprite Avatar;

        /// <summary>大立绘 Sprite（可能为程序生成的占位图）；旁白为 null。</summary>
        public readonly Sprite Portrait;

        /// <summary>整句台词（逐字揭示由控制器驱动 <see cref="IDialogueView.SetRevealedText"/>）。</summary>
        public readonly string Text;

        /// <summary>是否旁白（<c>speaker</c> 为空 / 缺失）——实现方通常隐藏名字 / 头像 / 立绘。</summary>
        public readonly bool IsNarrator;

        public DialogueLineView(
            string speakerId,
            string displayName,
            Sprite avatar,
            Sprite portrait,
            string text,
            bool isNarrator)
        {
            SpeakerId = speakerId;
            DisplayName = displayName;
            Avatar = avatar;
            Portrait = portrait;
            Text = text;
            IsNarrator = isNarrator;
        }
    }
}
