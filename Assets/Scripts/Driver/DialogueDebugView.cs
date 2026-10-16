using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TapTap2026.Game
{
    /// <summary>
    /// **调试用**对话 UI：把 <see cref="IDialogueView"/> 的数据填进场景里已经摆好的 uGUI 层级
    /// （对话面板 + 选项按钮模板）。
    /// <para>它是"另一个项目真正的对话框"的替身——只要那个 UI 实现同一个 <see cref="IDialogueView"/> 接口，
    /// 用 <see cref="DialogueController.SetView"/> 交给控制器即可，本组件可以禁用/删掉。</para>
    /// <para><b>UI 层级是场景里的可编辑对象</b>（<c>Dialogue Canvas</c> 下）：本组件只做**接线与数据填充**，
    /// 不在运行期建任何 UI 对象——因此整个对话框都能在编辑模式里看见、摆放、调参数。
    /// 本组件需与 <see cref="DialogueController"/> 挂在同一个物体上（<see cref="_controller"/> 未接线时按此兜底）。</para>
    /// <para><b>整张画布 = 「是否在对话」的开关</b>：<see cref="Awake"/> 关掉、<see cref="CloseView"/> 关掉、
    /// <see cref="ShowLine"/> 打开——面板自己的开关只在画布内部切「哪一屏」。也因此本组件不能挂到画布根上
    /// （<see cref="Awake"/> 会把组件自己一起关掉），场景里它挂在 <c>Dialogue Controller</c> 上。</para>
    /// </summary>
    public sealed class DialogueDebugView : MonoBehaviour, IDialogueView
    {
        [Tooltip("留空则自动取同物体上的 DialogueController（用于选项按钮回调）。")]
        [SerializeField] private DialogueController _controller;

        [Tooltip("历史字段（不再被本组件使用）：旧实现用它把整张对话画布的字体统一覆盖掉。" +
                 "字体归用户手工维护（只读区），场景里每个 TMP_Text 各自设字体——本组件不再读写字体，此字段仅为保住场景里的原有接线。")]
        [SerializeField] private TMP_FontAsset _font;

        [Tooltip("对话 UI 的根画布（overlay）。它的显隐 = 是否在对话：不在对话时整根关闭。")]
        [SerializeField] private Canvas _canvas;

        [Tooltip("对话面板根（运行初态隐藏；编辑时可在场景里临时激活做可视化编辑）。")]
        [SerializeField] private GameObject _dialoguePanel;

        [Tooltip("左侧立绘。")]
        [SerializeField] private Image _portraitImage;

        [Tooltip("小头像。")]
        [SerializeField] private Image _avatarImage;

        [Tooltip("说话人名字。")]
        [SerializeField] private TMP_Text _nameText;

        [Tooltip("台词正文（打字机逐字填充）。")]
        [SerializeField] private TMP_Text _lineText;

        [Tooltip("选项按钮的容器（右侧竖排布局）。")]
        [SerializeField] private RectTransform _choiceRoot;

        [Tooltip("选项按钮模板：每次显示选项时按它 Instantiate 一份（模板本身默认隐藏）。")]
        [SerializeField] private Button _choiceButtonTemplate;

        private readonly List<Button> _choiceButtons = new List<Button>();

        private void Awake()
        {
            if (_controller == null)
            {
                _controller = GetComponent<DialogueController>();
            }

            LogMissingWiring();

            // 场景必须自带 EventSystem：没有它按钮与滚动都收不到事件。这里只报错、**不自建**——
            // 运行期悄悄塞一个进场景，正是这次「UI 全部改为场景可接线」要消灭的东西。
            if (EventSystem.current == null)
            {
                Debug.LogError(
                    "[DialogueDebugView] 场景里没有 EventSystem，对话 UI 的按钮与输入收不到事件。" +
                    "请在场景里放置 EventSystem + StandaloneInputModule。");
            }

            // 刻意**不**在这里套字体：字体属用户的只读区（AGENTS.md 铁律「任何情况下都不许动字体设置」），
            // 场景里每个 TMP_Text 各自设好的字体就是最终结果，运行期不再覆盖。
            // （旧实现会把整张对话画布的字体统一改成 _font，会抹平逐组件的字体设置。）

            // 运行初态：**整张画布先关掉**（还没在对话），面板与选项模板也各自收起——
            // 场景里因此可以临时把它们激活来做可视化编辑，运行时切回由这里保证。
            SetCanvasVisible(false);

            if (_dialoguePanel != null)
            {
                _dialoguePanel.SetActive(false);
            }

            HideChoices();

            if (_choiceButtonTemplate != null)
            {
                _choiceButtonTemplate.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// <c>Dialogue Canvas</c> 整根的显隐：**「是否在对话」就是它的开关**——<see cref="Awake"/> 关掉、
        /// <see cref="ShowLine"/> 打开、<see cref="CloseView"/> 关掉；选项在进行中的对话里，
        /// 不额外碰画布。面板自己的 <c>SetActive</c> 只管画布内部切哪一屏。
        /// </summary>
        private void SetCanvasVisible(bool visible)
        {
            if (_canvas != null && _canvas.gameObject.activeSelf != visible)
            {
                _canvas.gameObject.SetActive(visible);
            }
        }

        /// <summary>一次性把所有未接线的引用列出来报错——缺哪个一眼看到，不用逐个字段翻 Inspector。</summary>
        private void LogMissingWiring()
        {
            var missing = new List<string>(8);

            if (_canvas == null)
            {
                missing.Add(nameof(_canvas));
            }

            if (_dialoguePanel == null)
            {
                missing.Add(nameof(_dialoguePanel));
            }

            if (_portraitImage == null)
            {
                missing.Add(nameof(_portraitImage));
            }

            if (_avatarImage == null)
            {
                missing.Add(nameof(_avatarImage));
            }

            if (_nameText == null)
            {
                missing.Add(nameof(_nameText));
            }

            if (_lineText == null)
            {
                missing.Add(nameof(_lineText));
            }

            if (_choiceRoot == null)
            {
                missing.Add(nameof(_choiceRoot));
            }

            if (_choiceButtonTemplate == null)
            {
                missing.Add(nameof(_choiceButtonTemplate));
            }

            if (missing.Count > 0)
            {
                Debug.LogError(
                    "[DialogueDebugView] 以下场景接线为空，对话 UI 无法完整显示：" + string.Join("、", missing));
            }
        }

        // ============================== IDialogueView ==============================

        public void ShowLine(DialogueLineView line)
        {
            if (_dialoguePanel == null)
            {
                return;
            }

            SetCanvasVisible(true);
            _dialoguePanel.SetActive(true);
            HideChoices();

            if (line.IsNarrator)
            {
                _nameText.gameObject.SetActive(false);
                _avatarImage.gameObject.SetActive(false);
                _portraitImage.gameObject.SetActive(false);
            }
            else
            {
                _nameText.gameObject.SetActive(true);
                _avatarImage.gameObject.SetActive(true);
                _portraitImage.gameObject.SetActive(true);
                _nameText.text = line.DisplayName ?? string.Empty;
                _avatarImage.sprite = line.Avatar;
                _avatarImage.enabled = line.Avatar != null;
                _portraitImage.sprite = line.Portrait;
                _portraitImage.enabled = line.Portrait != null;
            }

            _lineText.text = string.Empty;
        }

        public void SetRevealedText(string partial)
        {
            if (_lineText != null)
            {
                _lineText.text = partial ?? string.Empty;
            }
        }

        public void ShowChoices(IReadOnlyList<string> labels)
        {
            HideChoices();
            if (_choiceRoot == null || _choiceButtonTemplate == null || labels == null)
            {
                return;
            }

            for (int i = 0; i < labels.Count; i++)
            {
                int captured = i;

                Button button = Instantiate(_choiceButtonTemplate, _choiceRoot);
                button.gameObject.SetActive(true);

                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    label.text = labels[i];
                }

                button.onClick.AddListener(() =>
                {
                    if (_controller != null)
                    {
                        _controller.Choose(captured);
                    }
                });
                _choiceButtons.Add(button);
            }

            _choiceRoot.gameObject.SetActive(true);
        }

        public void HideChoices()
        {
            for (int i = 0; i < _choiceButtons.Count; i++)
            {
                if (_choiceButtons[i] != null)
                {
                    Destroy(_choiceButtons[i].gameObject);
                }
            }

            _choiceButtons.Clear();
            if (_choiceRoot != null)
            {
                _choiceRoot.gameObject.SetActive(false);
            }
        }

        public void CloseView()
        {
            HideChoices();
            if (_dialoguePanel != null)
            {
                _dialoguePanel.SetActive(false);
            }

            // 最后收画布：不在对话了，整根关掉（面板的开关上面已经复位，下次开启从干净状态起）。
            SetCanvasVisible(false);
        }
    }
}
