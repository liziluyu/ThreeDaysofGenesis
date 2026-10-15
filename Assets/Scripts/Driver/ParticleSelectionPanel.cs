using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TapTap2026.Game
{
    /// <summary>
    /// 投放选择面板（<c>HUD Canvas/ParticlePanel</c>）的接线与状态回显：把面板里那排手摆的按钮接到
    /// <see cref="ParticleEditor"/> 的投放选择上——点粒子 / 动物按钮就选中那一项，点「清除」按钮翻转清除模式。
    /// </summary>
    /// <remarks>
    /// <para><b>按钮是场景里的可编辑内容</b>：本组件只持引用，不建对象、不动 RectTransform 与文本。
    /// 每颗按钮绑一个**投放项显示名**（<see cref="ParticleSelectionEntry.DisplayName"/>，规则同
    /// <see cref="ParticleEditor.SelectByName"/>）——名字对不上当前可投列表时（阶段投放名单把这类型剔掉了）
    /// 那颗按钮变灰不可点，绝不静默选错东西。</para>
    /// <para><b>点击在 <see cref="Awake"/> 里 <c>AddListener</c> 绑定</b>（不是场景里的持久监听）：面板的可用性与
    /// 高亮本来就得每帧跟着编辑器走，点击与回显放一处才不会分叉；<c>SceneUiScaffolder</c> 会把这些按钮上的
    /// 旧持久监听清掉，免得重复触发。</para>
    /// <para><b>回显</b>：每帧把编辑器状态投影到按钮上——某一项被选中 ⇒ 那颗按钮的 <c>Image</c> 压成
    /// <see cref="SelectedTint"/> 灰（透明沿用底色）；清除模式开着时「清除」槽同样压灰；未选中 ⇒ 恢复场景里
    /// 手调的底色（**透明度也沿用**）。对话激活 / 高速模式期间（<see cref="ParticleEditor.EditingLocked"/>）
    /// 整排变灰不可点。</para>
    /// <para>状态一律从编辑器读、不另存一份：<see cref="ParticleEditor.Selections"/> 是数组后备的只读快照，
    /// 换了一批实例（白名单或阶段名单变化）时本组件按引用比对重新解析绑定（因此缓存的条目不会因重建而指着旧对象）。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSelectionPanel : MonoBehaviour
    {
        /// <summary>选中项压成的灰色（透明沿用场景里手调的底色）：面板不靠颜色区分粒子，只标出「当前是这颗」。</summary>
        public static readonly Color SelectedTint = new Color(0.35f, 0.35f, 0.35f);

        /// <summary>一颗投放按钮与它绑定的投放项。</summary>
        [Serializable]
        public struct ButtonBinding
        {
            [Tooltip("面板里的投放按钮（场景对象）。留空 = 这一槽不参与。")]
            public Button Button;

            [Tooltip("该按钮选中的投放项名字：显示名（中文，ParticleSelectionEntry.DisplayName）或类名都行——" +
                     "匹配规则同 ParticleEditor.SelectByName。名字对不上当前可投列表时按钮变灰不可点。")]
            public string SelectionName;
        }

        [Tooltip("投放选择的读写入口。留空则 Awake 里 GetComponent → FindObjectOfType 兜底；" +
                 "仍找不到就整排变灰不可点。")]
        [SerializeField] private ParticleEditor _editor;

        [Tooltip("粒子 / 动物按钮：每颗绑一个投放项。")]
        [SerializeField] private ButtonBinding[] _bindings;

        [Tooltip("「清除」按钮：翻转 ParticleEditor.EraseMode（左键改为擦除）。留空 = 面板没有清除槽。")]
        [SerializeField] private Button _eraseButton;

        /// <summary>上次解析绑定用的可投列表（引用比对：换了一批实例就重解析）。</summary>
        private IReadOnlyList<ParticleSelectionEntry> _selections;

        /// <summary>每颗按钮绑定的投放项；null = 当前不可投（按钮变灰不可点）/ 该槽没接按钮。</summary>
        private ParticleSelectionEntry[] _bindingEntries;

        /// <summary>每颗按钮的原始底色（含手调过的透明度）——未被选中时恢复它。</summary>
        private Color[] _baseColors;

        /// <summary>「清除」按钮的原始底色。</summary>
        private Color _eraseBaseColor = Color.white;

        private void Awake()
        {
            if (_editor == null)
            {
                _editor = GetComponent<ParticleEditor>();
            }

            if (_editor == null)
            {
                _editor = FindObjectOfType<ParticleEditor>();
            }

            int count = _bindings != null ? _bindings.Length : 0;
            _bindingEntries = new ParticleSelectionEntry[count];
            _baseColors = new Color[count];

            for (int i = 0; i < count; i++)
            {
                Button button = _bindings[i].Button;
                if (button == null)
                {
                    continue;
                }

                _baseColors[i] = ButtonColorOf(button);
                int slot = i;
                button.onClick.AddListener(() => OnSelectionButton(slot));
            }

            if (_eraseButton != null)
            {
                _eraseBaseColor = ButtonColorOf(_eraseButton);
                _eraseButton.onClick.AddListener(OnEraseButton);
            }
        }

        private void Update()
        {
            Refresh();
        }

        /// <summary>
        /// 把编辑器当前的投放选择投影到面板：解析绑定、开关可用性、给选中项上色。每帧一次
        /// （选择只由玩家输入改变，轮询比事件省心），也供测试直接调用。
        /// </summary>
        public void Refresh()
        {
            bool locked = _editor == null || _editor.EditingLocked;
            IReadOnlyList<ParticleSelectionEntry> selections = _editor != null ? _editor.Selections : null;
            if (!ReferenceEquals(_selections, selections))
            {
                _selections = selections;
                ResolveBindings();
            }

            ParticleSelectionEntry current = _editor != null ? _editor.CurrentSelection : null;

            for (int i = 0; i < _bindingEntries.Length; i++)
            {
                Button button = _bindings[i].Button;
                if (button == null)
                {
                    continue;
                }

                ParticleSelectionEntry entry = _bindingEntries[i];
                button.interactable = !locked && entry != null;

                // 选中的按钮压成灰（与未选中的原色形成对比），未选中恢复场景里手调的底色（透明度沿用）。
                Color tint = _baseColors[i];
                if (!locked && entry != null && ReferenceEquals(current, entry))
                {
                    tint = WithAlpha(SelectedTint, tint.a);
                }

                Image image = button.image;
                if (image != null && image.color != tint)
                {
                    image.color = tint;
                }
            }

            if (_eraseButton != null)
            {
                _eraseButton.interactable = !locked;

                // 清除模式开着时「清除」槽同样压成灰（与选中项同一套标记：灰 = 当前生效）。
                bool erasing = !locked && _editor != null && _editor.EraseMode;
                Color eraseTint = erasing ? WithAlpha(SelectedTint, _eraseBaseColor.a) : _eraseBaseColor;
                Image eraseImage = _eraseButton.image;
                if (eraseImage != null && eraseImage.color != eraseTint)
                {
                    eraseImage.color = eraseTint;
                }
            }
        }

        /// <summary>按显示名把每颗按钮对上当前列表里的条目；对不上（本阶段不可投）置 null。</summary>
        private void ResolveBindings()
        {
            for (int i = 0; i < _bindingEntries.Length; i++)
            {
                _bindingEntries[i] = Resolve(_bindings[i].SelectionName);
            }
        }

        /// <summary>投放项名字 → 当前列表里的条目：比显示名（中文）或类名（与
        /// <see cref="ParticleEditor.SelectByName"/> 同一规则，因此绑语言无关的类名或中文名都能对上）；
        /// 空名字 / 不可投 / 名字对不上返回 null。</summary>
        private ParticleSelectionEntry Resolve(string selectionName)
        {
            if (string.IsNullOrEmpty(selectionName) || _selections == null)
            {
                return null;
            }

            for (int i = 0; i < _selections.Count; i++)
            {
                if (_selections[i].DisplayName == selectionName || _selections[i].ClassName == selectionName)
                {
                    return _selections[i];
                }
            }

            return null;
        }

        /// <summary>粒子 / 动物按钮：按名字选中（编辑器按同一规则解析；本阶段不可投时什么都不做）。</summary>
        private void OnSelectionButton(int slot)
        {
            if (_editor == null || _bindings == null || slot < 0 || slot >= _bindings.Length)
            {
                return;
            }

            _editor.SelectByName(_bindings[slot].SelectionName);
            Refresh();
        }

        /// <summary>「清除」按钮：翻转清除模式（左键改为擦除）。</summary>
        private void OnEraseButton()
        {
            if (_editor == null)
            {
                return;
            }

            _editor.ToggleEraseMode();
            Refresh();
        }

        private static Color ButtonColorOf(Button button)
        {
            Image image = button.image;
            return image != null ? image.color : Color.white;
        }

        /// <summary>取高亮色但沿用底色的透明度——面板按钮的透明度是场景里手调的，回显不该把它抹平。</summary>
        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
