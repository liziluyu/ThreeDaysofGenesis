using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TapTap2026.Game
{
    /// <summary>
    /// 沙盒编辑器的 **uGUI HUD 唯一写入点**：把 <see cref="ParticleEditor"/> 每帧算好的字符串
    /// （光标格读数、鼠标旁的类型 / 半径、高速模式进度与结算、光照面板）写进场景里已经摆好的 uGUI 层级。
    /// 字符串的生产留在编辑器、显示落在这里——两者靠本类的这套推送 API 解耦。
    /// </summary>
    /// <remarks>
    /// <para>层级与布局都是**场景里的可编辑对象**（<c>HUD Canvas</c> 下，编辑模式可见可选中）：本组件只持引用、
    /// 不建对象，因此全部引用都能在 Inspector 里拖。未接线的引用对应的推送整段跳过（可选依赖，不报错）。</para>
    /// <para>光照面板的三个输入框直接写 <c>ParticleLightDriver</c> 的强度 / 亮相位刻数 / 暗相位刻数：
    /// 驱动器是这三个值的唯一配置面（它每帧把字段写回 <c>world.Light</c>，直接写世界会被下一帧覆盖）。
    /// 解析一律用不变文化；非法输入（相位不是非负整数 / 强度不是有限浮点）标红且**不提交**，
    /// 失焦后回填驱动真值——与旧 IMGUI 面板逐条同义。</para>
    /// <para>本组件**不读任何热键**：暂停 / 高速 / 平移 / 画笔仍归 <see cref="ParticleEditor"/>；
    /// 编辑器通过 <see cref="LightFieldFocused"/> 判断「输入框是不是正拿着键盘」，据此把键盘热键让给输入框。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SandboxHud : MonoBehaviour
    {
        /// <summary>结算面板胜负行的绿色（过关）。</summary>
        private static readonly Color PassColor = new Color(0.25f, 0.9f, 0.35f);

        /// <summary>结算面板胜负行的红色（未过关）。</summary>
        private static readonly Color FailColor = new Color(1f, 0.35f, 0.35f);

        /// <summary>光照输入框解析不出合法值时的文本色（值不会被提交）。</summary>
        private static readonly Color InvalidLightColor = new Color(1f, 0.45f, 0.45f);

        /// <summary>光照输入框的正常文本色。</summary>
        private static readonly Color ValidLightColor = Color.white;

        /// <summary>结算行数组为空时的占位（只为免掉一次每帧分配）。</summary>
        private static readonly string[] EmptyReportLines = new string[0];

        [Tooltip("光照面板三个输入框的写入口。留空则 Awake 里 GetComponent → FindObjectOfType 兜底；" +
                 "仍找不到就隐藏光照面板——没有写入口的面板画出来只会误导。")]
        [SerializeField] private ParticleLightDriver _lightDriver;

        [Tooltip("左上角光标格读数。")]
        [SerializeField] private TMP_Text _probeText;

        [Tooltip("鼠标跟随组（色块 + 文字）的根，pivot 取 (0, 0.5)。")]
        [SerializeField] private RectTransform _cursorHint;

        [Tooltip("鼠标跟随组的色块（当前投放项的代表色）。")]
        [SerializeField] private Image _cursorSwatch;

        [Tooltip("鼠标跟随组的文字（当前投放项 + 半径 / 物种）。")]
        [SerializeField] private TMP_Text _cursorLabel;

        [Tooltip("左下角高速模式进度读数。")]
        [SerializeField] private TMP_Text _highSpeedStatus;

        [Tooltip("高速模式结算面板（默认隐藏）。")]
        [SerializeField] private GameObject _reportPanel;

        [Tooltip("结算面板的统计正文（每行一个类型）。")]
        [SerializeField] private TMP_Text _reportBody;

        [Tooltip("结算面板的胜负行（无胜负结论时隐藏）。")]
        [SerializeField] private TMP_Text _reportVerdict;

        [Tooltip("暂停界面（暂停菜单）的根，默认隐藏，由 ParticleEditor 的暂停按钮开关。")]
        [SerializeField] private GameObject _pauseMenu;

        [Tooltip("右上角光照面板的根；挂 CanvasGroup 用于整体变灰 / 屏蔽点击。")]
        [SerializeField] private RectTransform _lightPanel;

        [Tooltip("暂停 / 光照 / 高速三颗按钮的面板根。指针落在它上面时编辑器屏蔽左键投放——" +
                 "点按钮不会顺带在底下画一笔（与光照面板同一条判据）。")]
        [SerializeField] private RectTransform _buttonPanel;

        [Tooltip("光照强度输入框（可写，任意有限浮点；负值 = 制冷）。")]
        [SerializeField] private TMP_InputField _lightIntensity;

        [Tooltip("亮相位刻数输入框（可写，非负整数）。")]
        [SerializeField] private TMP_InputField _lightLitTicks;

        [Tooltip("暗相位刻数输入框（可写，非负整数）。")]
        [SerializeField] private TMP_InputField _lightDarkTicks;

        /// <summary>光照面板的 CanvasGroup（变灰 / 屏蔽点击的载体）；场景里没挂时 <see cref="Awake"/> 补一个。</summary>
        private CanvasGroup _lightPanelGroup;

        /// <summary>HUD 的开关状态（<see cref="SetHudVisible"/>）。鼠标跟随组据此决定要不要因
        /// <see cref="SetCursorHint"/> 而现身——否则它会绕过整体开关自己冒出来。</summary>
        private bool _hudVisible = true;

        /// <summary>光照面板当前的可用状态（<see cref="SetLightPanelInteractable"/> 的去重缓存）。</summary>
        private bool _lightPanelInteractable = true;

        /// <summary>结算内容的缓存键：编辑器把结算行数组建一次后反复传（进终态后内容不再变），
        /// 按引用比较即可零分配去重；<see cref="_reportCached"/> 区分「还没收到过结算」。</summary>
        private string[] _reportLines;
        private bool _reportHasVerdict;
        private bool _reportVerdictPass;
        private bool _reportCached;

        /// <summary>三个光照输入框里是否有任一正持有键盘焦点（<see cref="ParticleEditor"/> 据此让出键盘热键）。</summary>
        public bool LightFieldFocused
        {
            get
            {
                return (_lightIntensity != null && _lightIntensity.isFocused)
                    || (_lightLitTicks != null && _lightLitTicks.isFocused)
                    || (_lightDarkTicks != null && _lightDarkTicks.isFocused);
            }
        }

        private void Awake()
        {
            // 光照值的写入口；找不到就没有可写的面板（可选依赖，不报错）。
            if (_lightDriver == null)
            {
                _lightDriver = GetComponent<ParticleLightDriver>();
            }

            if (_lightDriver == null)
            {
                _lightDriver = FindObjectOfType<ParticleLightDriver>();
            }

            if (_lightPanel != null)
            {
                _lightPanelGroup = _lightPanel.GetComponent<CanvasGroup>();
                if (_lightPanelGroup == null)
                {
                    // 场景里漏挂 CanvasGroup 时补一个：不然 SetLightPanelInteractable 的变灰 / 屏蔽点击全成空操作。
                    _lightPanelGroup = _lightPanel.gameObject.AddComponent<CanvasGroup>();
                }

                if (_lightDriver == null)
                {
                    _lightPanel.gameObject.SetActive(false);
                }
            }

            if (_lightIntensity != null)
            {
                _lightIntensity.onValueChanged.AddListener(OnIntensityChanged);
                _lightIntensity.onEndEdit.AddListener(OnIntensityEndEdit);
            }

            if (_lightLitTicks != null)
            {
                _lightLitTicks.onValueChanged.AddListener(OnLitTicksChanged);
                _lightLitTicks.onEndEdit.AddListener(OnLitTicksEndEdit);
            }

            if (_lightDarkTicks != null)
            {
                _lightDarkTicks.onValueChanged.AddListener(OnDarkTicksChanged);
                _lightDarkTicks.onEndEdit.AddListener(OnDarkTicksEndEdit);
            }
        }

        /// <summary>
        /// 统一开关 HUD：光标读数 / 鼠标跟随组 / 高速读数一起显隐，隐藏时暂停界面、光照面板与结算面板也一并收起
        /// （对齐旧的「<c>_drawHud</c> 关了整块不画」）。暂停界面 / 光照面板的再次打开走各自的
        /// <see cref="SetPauseMenuVisible"/> / <see cref="SetLightPanelVisible"/>；光照面板只在接线了光源驱动器时才现身。
        /// </summary>
        public void SetHudVisible(bool visible)
        {
            _hudVisible = visible;

            SetActive(_probeText != null ? _probeText.gameObject : null, visible);
            SetActive(_cursorHint != null ? _cursorHint.gameObject : null, visible);
            SetActive(_highSpeedStatus != null ? _highSpeedStatus.gameObject : null, visible);

            if (!visible)
            {
                // 整块 HUD 关闭时暂停界面与光照面板一并收起；重新打开由各自的方法恢复——它们另有独立开关。
                SetActive(_pauseMenu, false);
                SetActive(_lightPanel != null ? _lightPanel.gameObject : null, false);
                HideHighSpeedReport();
            }
        }

        /// <summary>写入左上角光标格读数；null / 空串 = 清空文本（**不隐藏**对象，可见性归 <see cref="SetHudVisible"/> 管）。</summary>
        public void SetProbeText(string text)
        {
            SetText(_probeText, text);
        }

        /// <summary>
        /// 写入鼠标跟随组（色块 + 文字）。<paramref name="text"/> 为 null = 撤下整组
        /// （旧 IMGUI 的「没有文本就整块不画」）；否则显示并写色块色与文本。
        /// 文本 / 颜色等值时不动它——省掉 TMP 的重排与画布重建。
        /// </summary>
        public void SetCursorHint(string text, Color color)
        {
            SetActive(_cursorHint != null ? _cursorHint.gameObject : null, _hudVisible && text != null);

            if (text == null)
            {
                return;
            }

            SetText(_cursorLabel, text);

            if (_cursorSwatch != null && _cursorSwatch.color != color)
            {
                _cursorSwatch.color = color;
            }
        }

        /// <summary>写入左下角高速模式进度读数；null / 空串 = 清空文本（不隐藏对象）。</summary>
        public void SetHighSpeedStatus(string text)
        {
            SetText(_highSpeedStatus, text);
        }

        /// <summary>
        /// 显示高速模式结算面板：<paramref name="lines"/> 里末尾一行是胜负结论时
        /// （<paramref name="hasVerdict"/>）单独拆进 <see cref="_reportVerdict"/> 并按
        /// <paramref name="verdictPass"/> 上绿 / 红，其余行用换行拼进正文；没有结论就藏起那一行。
        /// </summary>
        public void ShowHighSpeedReport(string[] lines, bool hasVerdict, bool verdictPass)
        {
            lines = lines ?? EmptyReportLines;

            // 结算行数组由编辑器建一次后反复传（会话进终态后内容不再变），按引用比较即可零分配去重。
            if (!_reportCached
                || !ReferenceEquals(_reportLines, lines)
                || _reportHasVerdict != hasVerdict
                || _reportVerdictPass != verdictPass)
            {
                _reportLines = lines;
                _reportHasVerdict = hasVerdict;
                _reportVerdictPass = verdictPass;
                _reportCached = true;

                RebuildReport(lines, hasVerdict, verdictPass);
            }

            SetActive(_reportPanel, true);
        }

        /// <summary>收起结算面板（内容缓存留着——同一场会话再显示时不必重建）。</summary>
        public void HideHighSpeedReport()
        {
            SetActive(_reportPanel, false);
        }

        /// <summary>开关暂停界面（暂停菜单）的显示；<paramref name="visible"/> 为 false 时整块收起。未接线时无事发生。</summary>
        public void SetPauseMenuVisible(bool visible)
        {
            SetActive(_pauseMenu, visible);
        }

        /// <summary>
        /// 开关光照面板（光照选择界面）的显示；<paramref name="visible"/> 为 true 但没接光源驱动器时仍保持隐藏
        /// （没有写入口的面板画出来只会误导，与 <see cref="Awake"/> 的初态判断一致）。
        /// </summary>
        public void SetLightPanelVisible(bool visible)
        {
            SetActive(_lightPanel != null ? _lightPanel.gameObject : null, visible && _lightDriver != null);
        }

        /// <summary>
        /// 光照面板整体可用性：<c>interactable</c> / <c>blocksRaycasts</c> 一起切，并按 1 / 0.6 的透明度
        /// 表达「变灰」（对齐旧 IMGUI 的 <c>GUI.enabled = false</c>——看得见、点不动、也敲不进去）。
        /// </summary>
        public void SetLightPanelInteractable(bool interactable)
        {
            if (_lightPanelGroup == null || _lightPanelInteractable == interactable)
            {
                return;
            }

            _lightPanelInteractable = interactable;
            _lightPanelGroup.interactable = interactable;
            _lightPanelGroup.blocksRaycasts = interactable;
            _lightPanelGroup.alpha = interactable ? 1f : 0.6f;
        }

        /// <summary>
        /// 指针是否落在**会吃掉点击的 HUD 面板**上（光照面板或按钮面板）。编辑器据此屏蔽左键投放 / 擦除——
        /// 点按钮、点输入框时指针同时也在网格上，不挡就会既改 UI 又在底下画一笔。
        /// 隐藏的面板不挡（没画出来的东西不该挡画笔）；未接线的面板恒不挡。
        /// </summary>
        public bool IsPointerOverPanel(Vector2 screenPosition)
        {
            return Contains(_lightPanel, screenPosition) || Contains(_buttonPanel, screenPosition);
        }

        /// <summary>overlay 画布与矩形求交（camera 传 null）；不可见 / 未接线恒 false。</summary>
        private static bool Contains(RectTransform panel, Vector2 screenPosition)
        {
            return panel != null
                && panel.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(panel, screenPosition);
        }

        /// <summary>
        /// 把**失焦**的输入框文本同步回驱动器的真值——运行期在 Inspector 改值 / 世界重建后仍显示真值。
        /// 文本已等值时不动它（稳态不重新格式化、零分配）；**聚焦中的那个框不碰**，免得吃掉正在敲的草稿。
        /// </summary>
        private void Update()
        {
            SyncLightTexts();
        }

        /// <summary>
        /// 鼠标跟随组贴着指针走：pivot 取 (0, 0.5)，位置 = 鼠标 + (18, 0)——色块落在指针右侧 18px、竖直居中。
        /// overlay 画布的世界坐标就是屏幕像素（画布居屏幕中心、世界尺寸 = 屏幕尺寸），所以直接用鼠标坐标；
        /// 放在 <see cref="LateUpdate"/> 是为了在渲染前摆好，跟手不掉帧。组没显示时什么都不做。
        /// </summary>
        private void LateUpdate()
        {
            if (_cursorHint == null || !_cursorHint.gameObject.activeSelf)
            {
                return;
            }

            Vector3 mouse = Input.mousePosition;
            _cursorHint.position = new Vector3(mouse.x + 18f, mouse.y, 0f);
        }

        private void SyncLightTexts()
        {
            if (_lightDriver == null)
            {
                return;
            }

            if (_lightIntensity != null && !_lightIntensity.isFocused
                && !(TryParseLightScalar(_lightIntensity.text, out float intensity)
                     && intensity == _lightDriver.Intensity))
            {
                SetLightText(_lightIntensity, FormatIntensity(_lightDriver.Intensity));
            }

            if (_lightLitTicks != null && !_lightLitTicks.isFocused
                && !(TryParseLightTicks(_lightLitTicks.text, out int litTicks)
                     && litTicks == _lightDriver.LitTicks))
            {
                SetLightText(_lightLitTicks, FormatTicks(_lightDriver.LitTicks));
            }

            if (_lightDarkTicks != null && !_lightDarkTicks.isFocused
                && !(TryParseLightTicks(_lightDarkTicks.text, out int darkTicks)
                     && darkTicks == _lightDriver.DarkTicks))
            {
                SetLightText(_lightDarkTicks, FormatTicks(_lightDriver.DarkTicks));
            }
        }

        /// <summary>强度输入框内容变化：解析成有限浮点就提交（写驱动器），否则文本标红不提交。</summary>
        private void OnIntensityChanged(string text)
        {
            if (!TryParseLightScalar(text, out float value))
            {
                SetLightTextColor(_lightIntensity, InvalidLightColor);
                return;
            }

            if (_lightDriver != null)
            {
                _lightDriver.Intensity = value;
            }

            SetLightTextColor(_lightIntensity, ValidLightColor);
        }

        /// <summary>亮相位刻数输入框内容变化：解析成非负整数就提交，否则标红不提交。</summary>
        private void OnLitTicksChanged(string text)
        {
            if (!TryParseLightTicks(text, out int value))
            {
                SetLightTextColor(_lightLitTicks, InvalidLightColor);
                return;
            }

            if (_lightDriver != null)
            {
                _lightDriver.LitTicks = value;
            }

            SetLightTextColor(_lightLitTicks, ValidLightColor);
        }

        /// <summary>暗相位刻数输入框内容变化：解析成非负整数就提交，否则标红不提交。</summary>
        private void OnDarkTicksChanged(string text)
        {
            if (!TryParseLightTicks(text, out int value))
            {
                SetLightTextColor(_lightDarkTicks, InvalidLightColor);
                return;
            }

            if (_lightDriver != null)
            {
                _lightDriver.DarkTicks = value;
            }

            SetLightTextColor(_lightDarkTicks, ValidLightColor);
        }

        /// <summary>强度框失焦：文本回填驱动真值（旧「失焦弹回真值」语义——非法草稿不留在框里）。</summary>
        private void OnIntensityEndEdit(string text)
        {
            if (_lightDriver == null || _lightIntensity == null)
            {
                return;
            }

            if (!TryParseLightScalar(text, out float value) || value != _lightDriver.Intensity)
            {
                SetLightText(_lightIntensity, FormatIntensity(_lightDriver.Intensity));
            }
        }

        /// <summary>亮相位数框失焦：文本回填驱动真值。</summary>
        private void OnLitTicksEndEdit(string text)
        {
            if (_lightDriver == null || _lightLitTicks == null)
            {
                return;
            }

            if (!TryParseLightTicks(text, out int value) || value != _lightDriver.LitTicks)
            {
                SetLightText(_lightLitTicks, FormatTicks(_lightDriver.LitTicks));
            }
        }

        /// <summary>暗相位框失焦：文本回填驱动真值。</summary>
        private void OnDarkTicksEndEdit(string text)
        {
            if (_lightDriver == null || _lightDarkTicks == null)
            {
                return;
            }

            if (!TryParseLightTicks(text, out int value) || value != _lightDriver.DarkTicks)
            {
                SetLightText(_lightDarkTicks, FormatTicks(_lightDriver.DarkTicks));
            }
        }

        /// <summary>光照强度文本 → 有限浮点（NaN / 无穷非法，与 <c>LightController.Intensity</c> 的钳制一致）。</summary>
        private static bool TryParseLightScalar(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>相位刻数文本 → 非负整数（与 <c>LightController.LitTicks</c> 的钳制语义一致）。</summary>
        private static bool TryParseLightTicks(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        /// <summary>强度的显示格式：最多三位小数、去掉多余的零（整数看起来就是整数）。</summary>
        private static string FormatIntensity(float intensity)
        {
            return intensity.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>相位刻数的显示格式（不变文化，免得其他区域设置插进千位分隔符）。</summary>
        private static string FormatTicks(int ticks)
        {
            return ticks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>写输入框文本（等值不动）并把文本色恢复成正常色——回填的都是**驱动真值**，必然合法。</summary>
        private static void SetLightText(TMP_InputField field, string text)
        {
            if (field == null)
            {
                return;
            }

            if (field.text != text)
            {
                field.text = text;
            }

            SetLightTextColor(field, ValidLightColor);
        }

        /// <summary>给输入框的文本组件上色（等值不动，省掉 TMP 的网格重建）。</summary>
        private static void SetLightTextColor(TMP_InputField field, Color color)
        {
            if (field == null || field.textComponent == null || field.textComponent.color == color)
            {
                return;
            }

            field.textComponent.color = color;
        }

        /// <summary>写 TMP 文本；null / 空串当空文本，等值不重复赋值（TMP 每次赋值都会重解析并重排）。</summary>
        private static void SetText(TMP_Text label, string text)
        {
            if (label == null)
            {
                return;
            }

            string value = text ?? string.Empty;
            if (label.text != value)
            {
                label.text = value;
            }
        }

        /// <summary>按需切对象激活状态（等价时不碰，避免每帧的无谓 SetActive）。</summary>
        private static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }

        /// <summary>重建结算面板内容：正文 = 除胜负行以外的全部行（换行连接），胜负行单独一行上色。</summary>
        private void RebuildReport(string[] lines, bool hasVerdict, bool verdictPass)
        {
            bool showVerdict = hasVerdict && lines.Length > 0;
            int bodyCount = showVerdict ? lines.Length - 1 : lines.Length;

            if (_reportBody != null)
            {
                string body = bodyCount > 0 ? string.Join("\n", lines, 0, bodyCount) : string.Empty;
                if (_reportBody.text != body)
                {
                    _reportBody.text = body;
                }
            }

            if (_reportVerdict != null)
            {
                if (showVerdict)
                {
                    string verdict = lines[lines.Length - 1];
                    if (_reportVerdict.text != verdict)
                    {
                        _reportVerdict.text = verdict;
                    }

                    Color color = verdictPass ? PassColor : FailColor;
                    if (_reportVerdict.color != color)
                    {
                        _reportVerdict.color = color;
                    }
                }

                SetActive(_reportVerdict.gameObject, showVerdict);
            }
        }
    }
}
