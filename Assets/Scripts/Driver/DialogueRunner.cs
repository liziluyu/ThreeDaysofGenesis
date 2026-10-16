using System;

namespace TapTap2026.Game
{
    /// <summary>
    /// 对话状态机：**纯 C#、不碰 UI / 输入 / 协程**，维护进度，便于 EditMode 测试。
    /// 由 <see cref="DialogueController"/>（MonoBehaviour 薄壳）驱动；它把打字机 / UI / 热键 / 暂停接进来。
    /// </summary>
    public sealed class DialogueRunner
    {
        /// <summary>对话播完派发：参数 = 文件的 <c>onEnd</c> 事件名。整个对话只触发一次。</summary>
        public event Action<string> OnEnd;

        private DialogueFile _file;
        private int _index;
        private bool _finished;
        private bool _onEndFired;

        public DialogueFile File { get { return _file; } }
        public int Index { get { return _index; } }
        public bool Finished { get { return _finished; } }

        /// <summary>当前应显示的句（结束 / 越界时返回 null）。</summary>
        public DialogueLine CurrentLine
        {
            get
            {
                if (_file == null || _index < 0 || _index >= _file.lines.Count)
                {
                    return null;
                }

                return _file.lines[_index];
            }
        }

        /// <summary>从头逐句播；没有台词立即结束。</summary>
        public void Start(DialogueFile file)
        {
            _file = file;
            _finished = false;
            _onEndFired = false;
            _index = 0;

            if (_file == null || _file.lines == null || _file.lines.Count == 0)
            {
                Finish();
            }
        }

        /// <summary>前进下一句。返回是否还能继续（false = 已到尾巴 / 已结束）。</summary>
        public bool Next()
        {
            if (_finished)
            {
                return false;
            }

            _index++;
            if (_index >= _file.lines.Count)
            {
                Finish();
                return false;
            }

            return true;
        }

        /// <summary>结束：标记 Finished，并按 <c>onEnd</c>（若非空）派发一次 <see cref="OnEnd"/>。</summary>
        private void Finish()
        {
            _finished = true;
            if (!_onEndFired)
            {
                _onEndFired = true;
                string eventName = _file != null ? _file.onEnd : null;
                if (!string.IsNullOrEmpty(eventName))
                {
                    Action<string> handler = OnEnd;
                    if (handler != null)
                    {
                        handler(eventName);
                    }
                }
            }
        }
    }
}
