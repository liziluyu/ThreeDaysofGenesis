using System;
using System.Collections.Generic;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>回顾面板里的一条历史记录。</summary>
    public readonly struct HistoryEntry
    {
        /// <summary>角色 id；「玩家选择」记录为空串。</summary>
        public readonly string speakerId;

        /// <summary>台词，或「→ 选择了：XXX」。</summary>
        public readonly string text;

        /// <summary>true = 这是一条「玩家做了选择」的记录。</summary>
        public readonly bool isChoiceMark;

        public HistoryEntry(string speakerId, string text, bool isChoiceMark)
        {
            this.speakerId = speakerId;
            this.text = text;
            this.isChoiceMark = isChoiceMark;
        }
    }

    /// <summary>
    /// 对话状态机：**纯 C#、不碰 UI / 输入 / 协程**，维护进度与历史，便于 EditMode 测试。
    /// 由 <see cref="DialogueController"/>（MonoBehaviour 薄壳）驱动；它把打字机 / UI / 热键 / 暂停接进来。
    /// </summary>
    public sealed class DialogueRunner
    {
        /// <summary>对话播完派发：参数 = 文件的 <c>onEnd</c> 事件名。整个对话只触发一次。</summary>
        public event Action<string> OnEnd;

        private DialogueFile _file;
        private DialogueSegment _segment;
        private int _index;
        private bool _finished;
        private bool _onEndFired;
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();

        public DialogueFile File { get { return _file; } }
        public DialogueSegment CurrentSegment { get { return _segment; } }
        public int Index { get { return _index; } }
        public bool Finished { get { return _finished; } }
        public IReadOnlyList<HistoryEntry> History { get { return _history; } }

        /// <summary>当前应显示的句（结束 / 越界时返回 null）。</summary>
        public DialogueLine CurrentLine
        {
            get
            {
                if (_finished || _segment == null || _index < 0 || _index >= _segment.lines.Count)
                {
                    return null;
                }

                return _segment.lines[_index];
            }
        }

        /// <summary>当前句是否带选项（需要玩家选择）。</summary>
        public bool IsChoicePoint
        {
            get
            {
                DialogueLine line = CurrentLine;
                return line != null && line.choices != null && line.choices.Count > 0;
            }
        }

        /// <summary>从 <c>segments[0]</c> 开始；空段 / 无段立即结束。</summary>
        public void Start(DialogueFile file)
        {
            _file = file;
            _finished = false;
            _onEndFired = false;
            _history.Clear();
            _index = 0;
            _segment = file != null && file.segments != null && file.segments.Count > 0 ? file.segments[0] : null;

            if (_segment == null || _segment.lines.Count == 0)
            {
                Finish();
                return;
            }

            Record(_segment.lines[0]);
        }

        /// <summary>前进下一句。返回是否还能继续（false = 已结束 / 正停在选项点）。</summary>
        public bool Next()
        {
            if (_finished)
            {
                return false;
            }

            if (IsChoicePoint)
            {
                return false; // 选项点阻塞，必须等 Choose()
            }

            _index++;
            if (_index >= _segment.lines.Count)
            {
                Finish();
                return false;
            }

            Record(_segment.lines[_index]);
            return true;
        }

        /// <summary>选分支并跳段。返回是否成功。</summary>
        public bool Choose(int index)
        {
            if (_finished || !IsChoicePoint)
            {
                return false;
            }

            DialogueLine line = CurrentLine;
            if (index < 0 || index >= line.choices.Count)
            {
                return false;
            }

            DialogueChoice choice = line.choices[index];
            _history.Add(new HistoryEntry(string.Empty, "> Chose: " + choice.text, true));

            DialogueSegment target = FindSegment(choice.gotoSegment);
            if (target == null)
            {
                Debug.LogWarning(
                    $"[DialogueRunner] 选项跳转到不存在的段：{choice.gotoSegment}（对话 id = {_file.id}）。");
                Finish();
                return false;
            }

            _segment = target;
            _index = 0;
            if (_segment.lines.Count == 0)
            {
                Finish();
                return false;
            }

            Record(_segment.lines[0]);
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

        private void Record(DialogueLine line)
        {
            _history.Add(new HistoryEntry(line.speaker ?? string.Empty, line.text ?? string.Empty, false));
        }

        private DialogueSegment FindSegment(string id)
        {
            if (string.IsNullOrEmpty(id) || _file == null)
            {
                return null;
            }

            for (int i = 0; i < _file.segments.Count; i++)
            {
                if (_file.segments[i].id == id)
                {
                    return _file.segments[i];
                }
            }

            return null;
        }
    }
}
