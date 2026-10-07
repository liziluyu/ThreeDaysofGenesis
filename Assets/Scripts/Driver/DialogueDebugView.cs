using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TapTap2026.Game
{
    /// <summary>
    /// **调试用**对话 UI：运行时构建 Canvas + 对话面板 + 回顾面板，实现 <see cref="IDialogueView"/>。
    /// <para>它是"另一个项目真正的对话框"的替身——只要那个 UI 实现同一个 <see cref="IDialogueView"/> 接口，
    /// 用 <see cref="DialogueController.SetView"/> 交给控制器即可，本组件可以禁用/删掉。</para>
    /// <para>面板运行时构建（不写场景文件）；本组件需与 <see cref="DialogueController"/> 挂在同一个物体上。</para>
    /// </summary>
    public sealed class DialogueDebugView : MonoBehaviour, IDialogueView
    {
        [Tooltip("留空则自动取同物体上的 DialogueController（用于选项按钮回调）。")]
        [SerializeField] private DialogueController _controller;

        [Tooltip("TMP 字体资产（中文字体）。留空 = 中文显示成豆腐块。")]
        [SerializeField] private TMP_FontAsset _font;

        [Tooltip("回顾面板顶部的提示文字。")]
        [SerializeField] private string _reviewHint = "Review  (Tab/Esc to close, scroll wheel to browse)";

        private Canvas _canvas;
        private GameObject _dialoguePanel;
        private GameObject _reviewPanel;
        private Image _portraitImage;
        private Image _avatarImage;
        private TMP_Text _nameText;
        private TMP_Text _lineText;
        private RectTransform _choiceRoot;
        private TMP_Text _reviewText;
        private ScrollRect _reviewScroll;
        private GameObject _ownedEventSystem;
        private readonly List<Button> _choiceButtons = new List<Button>();

        private void Awake()
        {
            if (_controller == null)
            {
                _controller = GetComponent<DialogueController>();
            }

            BuildUi();
        }

        private void OnDestroy()
        {
            DestroyUi();
        }

        // ============================== IDialogueView ==============================

        public void ShowLine(DialogueLineView line)
        {
            if (_dialoguePanel == null)
            {
                return;
            }

            _dialoguePanel.SetActive(true);
            _reviewPanel.SetActive(false);
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
            if (_choiceRoot == null || labels == null)
            {
                return;
            }

            for (int i = 0; i < labels.Count; i++)
            {
                int captured = i;
                Button button = NewButton("Choice" + i, _choiceRoot, labels[i]);
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

        public void ShowReview(IReadOnlyList<DialogueHistoryEntryView> history)
        {
            if (_reviewPanel == null)
            {
                return;
            }

            _reviewText.text = BuildReviewText(history);
            _reviewPanel.SetActive(true);
            _reviewScroll.verticalNormalizedPosition = 1f;
        }

        public void HideReview()
        {
            if (_reviewPanel != null)
            {
                _reviewPanel.SetActive(false);
            }
        }

        public void CloseView()
        {
            HideChoices();
            if (_dialoguePanel != null)
            {
                _dialoguePanel.SetActive(false);
            }

            if (_reviewPanel != null)
            {
                _reviewPanel.SetActive(false);
            }
        }

        private static string BuildReviewText(IReadOnlyList<DialogueHistoryEntryView> history)
        {
            var builder = new System.Text.StringBuilder();
            if (history == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < history.Count; i++)
            {
                DialogueHistoryEntryView entry = history[i];
                if (entry.IsChoiceMark)
                {
                    builder.Append("<color=#FFD37A>").Append(entry.Text).Append("</color>\n");
                }
                else if (string.IsNullOrEmpty(entry.DisplayName))
                {
                    builder.Append(entry.Text).Append('\n');
                }
                else
                {
                    builder.Append("<color=#9FD0FF>").Append(entry.DisplayName).Append("：</color>")
                        .Append(entry.Text).Append('\n');
                }
            }

            return builder.ToString();
        }

        // ============================== 运行时 UI ==============================

        private void BuildUi()
        {
            EnsureEventSystem();

            var canvasGo = new GameObject("DialogueCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 1000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();

            BuildDialoguePanel(canvasGo.transform);
            BuildReviewPanel(canvasGo.transform);

            _dialoguePanel.SetActive(false);
            _reviewPanel.SetActive(false);
        }

        private void BuildDialoguePanel(Transform parent)
        {
            _dialoguePanel = NewRect("DialoguePanel", parent).gameObject;
            Image bg = _dialoguePanel.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.06f, 0.09f, 0.85f);
            RectTransform rect = (RectTransform)_dialoguePanel.transform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 30f);
            rect.sizeDelta = new Vector2(-80f, 260f);

            // 立绘（左）
            _portraitImage = NewImage("Portrait", _dialoguePanel.transform, Color.white);
            Anchor((RectTransform)_portraitImage.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 16f), new Vector2(150f, 228f));
            _portraitImage.preserveAspect = true;

            // 小头像
            _avatarImage = NewImage("Avatar", _dialoguePanel.transform, Color.white);
            Anchor((RectTransform)_avatarImage.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(182f, 172f), new Vector2(48f, 48f));
            _avatarImage.preserveAspect = true;

            // 名字
            _nameText = NewText("Name", _dialoguePanel.transform, 34f);
            Anchor((RectTransform)_nameText.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(242f, 196f), new Vector2(600f, 44f));
            _nameText.alignment = TextAlignmentOptions.BottomLeft;

            // 台词（拉伸：左让开立绘、右让开选项）
            _lineText = NewText("Line", _dialoguePanel.transform, 30f);
            AnchorStretch((RectTransform)_lineText.transform, Vector2.zero, Vector2.one, new Vector2(242f, 16f), new Vector2(-380f, -60f));
            _lineText.alignment = TextAlignmentOptions.TopLeft;

            // 选项容器（右侧竖排）
            _choiceRoot = NewRect("Choices", _dialoguePanel.transform);
            Anchor(_choiceRoot, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-16f, 16f), new Vector2(360f, 228f));
            var layout = _choiceRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.LowerRight;
        }

        private void BuildReviewPanel(Transform parent)
        {
            _reviewPanel = NewRect("ReviewPanel", parent).gameObject;
            Image bg = _reviewPanel.AddComponent<Image>();
            bg.color = new Color(0.03f, 0.04f, 0.07f, 0.96f);
            RectTransform rect = (RectTransform)_reviewPanel.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(120f, 90f);
            rect.offsetMax = new Vector2(-120f, -90f);

            TMP_Text hint = NewText("Hint", _reviewPanel.transform, 24f);
            AnchorStretch((RectTransform)hint.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -56f), new Vector2(-24f, -16f));
            hint.text = _reviewHint;
            hint.color = new Color(0.75f, 0.8f, 0.9f, 1f);

            var scrollGo = NewRect("Scroll", _reviewPanel.transform).gameObject;
            AnchorStretch((RectTransform)scrollGo.transform, Vector2.zero, Vector2.one, new Vector2(24f, 24f), new Vector2(-24f, -70f));
            _reviewScroll = scrollGo.AddComponent<ScrollRect>();
            _reviewScroll.horizontal = false;
            _reviewScroll.vertical = true;
            _reviewScroll.movementType = ScrollRect.MovementType.Clamped;

            var viewportGo = NewRect("Viewport", scrollGo.transform).gameObject;
            RectTransform viewport = (RectTransform)viewportGo.transform;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            viewportGo.AddComponent<RectMask2D>();

            _reviewText = NewText("Content", viewportGo.transform, 28f);
            RectTransform content = (RectTransform)_reviewText.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);
            _reviewText.alignment = TextAlignmentOptions.TopLeft;
            var fitter = _reviewText.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _reviewScroll.viewport = viewport;
            _reviewScroll.content = content;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            _ownedEventSystem = new GameObject("DialogueEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            _ownedEventSystem.transform.SetParent(transform, false);
        }

        private void DestroyUi()
        {
            if (_canvas != null)
            {
                Destroy(_canvas.gameObject);
                _canvas = null;
            }

            if (_ownedEventSystem != null)
            {
                Destroy(_ownedEventSystem);
                _ownedEventSystem = null;
            }

            _choiceButtons.Clear();
        }

        // ---- UI 小工具 ----

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private Image NewImage(string name, Transform parent, Color color)
        {
            RectTransform rect = NewRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private TMP_Text NewText(string name, Transform parent, float size)
        {
            RectTransform rect = NewRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.color = Color.white;
            text.enableWordWrapping = true;
            text.raycastTarget = false;
            if (_font != null)
            {
                text.font = _font;
            }

            return text;
        }

        private Button NewButton(string name, Transform parent, string label)
        {
            RectTransform rect = NewRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.16f, 0.2f, 0.28f, 0.95f);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            TMP_Text text = NewText("Label", rect, 28f);
            RectTransform textRect = (RectTransform)text.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 4f);
            textRect.offsetMax = new Vector2(-12f, -4f);
            text.alignment = TextAlignmentOptions.Center;
            text.text = label;

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 48f;
            element.preferredHeight = 48f;

            return button;
        }

        private static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }

        /// <summary>拉伸锚点（anchorMin ≠ anchorMax 的轴）必须用 offsetMin/offsetMax 定位。</summary>
        private static void AnchorStretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
