using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TapTap2026.Game
{
    /// <summary>
    /// 沙盒编辑器的 **uGUI HUD 唯一写入点**：把 <see cref="ParticleEditor"/> 每帧算好的字符串
    /// （光标格读数、鼠标旁的类型 / 半径、高速模式结算、光照面板）写进场景里已经摆好的 uGUI 层级。
    /// 字符串的生产留在编辑器、显示落在这里——两者靠本类的这套推送 API 解耦。
    /// </summary>
    /// <remarks>
    /// <para>层级与布局都是**场景里的可编辑对象**（<c>HUD Canvas</c> 下，编辑模式可见可选中）：本组件只持引用、
    /// 不建对象，因此全部引用都能在 Inspector 里拖。未接线的引用对应的推送整段跳过（可选依赖，不报错）。</para>
    /// <para>光照面板直接写 <c>ParticleLightDriver</c> 的强度 / 亮相位刻数 / 暗相位刻数：驱动器是这三个值的
    /// 唯一配置面（它每帧把字段写回 <c>world.Light</c>，直接写世界会被下一帧覆盖）。面板 = 一个 0..30 的
    /// **强度滑块** + 两行**秒读数**（白天 / 夜晚，把相位刻数按当前逻辑帧速率换算成秒）
    /// + 两行按秒步进按钮（<see cref="AddLitSeconds"/> / <see cref="AddDarkSeconds"/>，把秒换算成
    /// **当前逻辑帧速率**刻数再加）。滑块的回调只往驱动器写值（不解析文本）；
    /// 滑块的显示值与两行读数每帧回填**驱动真值**——运行期在 Inspector 改值 / 世界重建后读数跟着走。</para>
    /// <para>本组件**不读任何热键**：暂停 / 高速 / 平移 / 画笔仍归 <see cref="ParticleEditor"/>；
    /// 面板里已经没有文本输入，<see cref="ParticleEditor"/> 的键盘热键因此不必再为它让位。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SandboxHud : MonoBehaviour
    {
        /// <summary>结算面板胜负行的绿色（过关）。</summary>
        private static readonly Color PassColor = new Color(0.25f, 0.9f, 0.35f);

        /// <summary>结算面板胜负行的红色（未过关）。</summary>
        private static readonly Color FailColor = new Color(1f, 0.35f, 0.35f);

        /// <summary>结算行数组为空时的占位（只为免掉一次每帧分配）。</summary>
        private static readonly string[] EmptyReportLines = new string[0];

        /// <summary>找不到模拟驱动器时的逻辑帧速率兜底（= <see cref="ParticleSimulationSettings"/> 的默认
        /// <c>_logicRate</c>）。真实速率一律从驱动器读，20 只是没有驱动器时的近似。</summary>
        private const int DefaultLogicRate = 20;

        [Tooltip("光照面板的写入口（强度滑块 + 两行相位秒读数）。留空则 Awake 里 GetComponent → FindObjectOfType 兜底；" +
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

        [Tooltip("高速模式结算面板（默认隐藏）。")]
        [SerializeField] private GameObject _reportPanel;

        [Tooltip("结算面板正文（当前结算只显示胜负行，正文留空）。")]
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

        [Tooltip("光照强度滑块（0..30 整数档）；拖动即写驱动器的强度，负数制冷只能在 Inspector 上设。")]
        [SerializeField] private Slider _lightIntensity;

        [Tooltip("强度滑块右侧的数值读数（显示驱动真值，滑块只夹在自身范围里）。")]
        [SerializeField] private TMP_Text _lightIntensityValue;

        [Tooltip("亮相位秒读数（刻数按当前逻辑帧速率换算成秒）。")]
        [SerializeField] private TMP_Text _lightLitTicks;

        [Tooltip("暗相位秒读数（刻数按当前逻辑帧速率换算成秒）。留空则 Awake 里按对象名 \"" + DarkTicksObjectName + "\" 在场景里搜（含隐藏面板下的）。")]
        [SerializeField] private TMP_Text _lightDarkTicks;

        /// <summary>暗相位秒读数在场景里的**固定对象名**：未接线时 <see cref="Awake"/> 按它搜索文本框。</summary>
        private const string DarkTicksObjectName = "DarkTicks";

        /// <summary>光照面板的 CanvasGroup（变灰 / 屏蔽点击的载体）；场景里没挂时 <see cref="Awake"/> 补一个。</summary>
        private CanvasGroup _lightPanelGroup;

        /// <summary>逻辑帧速率的来源（懒解析、非序列化）：秒读数与按秒步进都按它换算成逻辑帧刻数。
        /// 从光照驱动器所在的对象上取，取不到就退回 <see cref="DefaultLogicRate"/>。</summary>
        private ParticleSimulationDriver _simDriver;

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

            // 暗相位秒读数：未接线时按**对象名**在场景里搜（面板收起时对象未激活，因此搜索必须含未激活）。
            if (_lightDarkTicks == null)
            {
                _lightDarkTicks = FindSceneText(DarkTicksObjectName);
                if (_lightDarkTicks == null)
                {
                    Debug.LogWarning($"[SandboxHud] 场景里找不到名为 \"{DarkTicksObjectName}\" 的文本框，暗相位秒读数不显示。");
                }
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
            }
        }

        /// <summary>
        /// 统一开关 HUD：光标读数 / 鼠标跟随组一起显隐，隐藏时暂停界面、光照面板与结算面板也一并收起
        /// （对齐旧的「<c>_drawHud</c> 关了整块不画」）。暂停界面 / 光照面板的再次打开走各自的
        /// <see cref="SetPauseMenuVisible"/> / <see cref="SetLightPanelVisible"/>；光照面板只在接线了光源驱动器时才现身。
        /// </summary>
        public void SetHudVisible(bool visible)
        {
            _hudVisible = visible;

            SetActive(_probeText != null ? _probeText.gameObject : null, visible);
            SetActive(_cursorHint != null ? _cursorHint.gameObject : null, visible);

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

        /// <summary>亮相位刻数按**秒**步进：参数是秒（可负），1 秒 = 当前逻辑帧速率刻，
        /// 结果钳制到 ≥ 0 后写回驱动器的亮相位刻数（写驱动器而不是 <c>world.Light</c>——
        /// 驱动器每帧把字段写回世界，直接写世界会被下一帧覆盖）。</summary>
        public void AddLitSeconds(int seconds)
        {
            AddSeconds(seconds, true);
        }

        /// <summary>暗相位刻数按**秒**步进：语义与 <see cref="AddLitSeconds"/> 相同，只是落在暗相位刻数上。</summary>
        public void AddDarkSeconds(int seconds)
        {
            AddSeconds(seconds, false);
        }

        /// <summary>
        /// 指针是否落在**会吃掉点击的 HUD 面板**上（光照面板或按钮面板）。编辑器据此屏蔽左键投放 / 擦除——
        /// 点按钮、拖滑块时指针同时也在网格上，不挡就会既改 UI 又在底下画一笔。
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
        /// 把**驱动器真值**同步给面板——运行期在 Inspector 改值 / 世界重建后显示跟着走。
        /// 强度滑块夹到自身范围后回填（不等才写，免得拖动时与滑块自己打架），三个读数文本等值不动（零分配）。
        /// </summary>
        private void Update()
        {
            SyncLightValues();
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

        /// <summary>
        /// 面板回填驱动器真值：强度滑块夹到 <c>[minValue, maxValue]</c> 后写（近似相等就不写——
        /// 拖动中滑块的当前值本来就等于驱动器值，重复写只会打架）；三个读数文本走 <see cref="SetText"/>。
        /// 强度读数显示**驱动器真值**：Inspector 里超出 0..30 时读数照显真值，滑块只是被夹在端点。
        /// </summary>
        private void SyncLightValues()
        {
            if (_lightDriver == null)
            {
                return;
            }

            if (_lightIntensity != null)
            {
                float target = Mathf.Clamp(_lightDriver.Intensity, _lightIntensity.minValue, _lightIntensity.maxValue);
                if (!Mathf.Approximately(_lightIntensity.value, target))
                {
                    _lightIntensity.SetValueWithoutNotify(target);
                }
            }

            SetText(_lightIntensityValue, FormatIntensity(_lightDriver.Intensity));
            SetText(_lightLitTicks, FormatSeconds(_lightDriver.LitTicks));
            SetText(_lightDarkTicks, FormatSeconds(_lightDriver.DarkTicks));
        }

        /// <summary>
        /// 按秒步进相位刻数（<paramref name="lit"/> 选亮 / 暗行），钳制到 <c>[0, int.MaxValue]</c>。
        /// 全程用 <see cref="long"/> 算，避免「秒 × 速率」或「刻数 + 增量」在 int 上溢出。
        /// </summary>
        private void AddSeconds(int seconds, bool lit)
        {
            if (_lightDriver == null || seconds == 0)
            {
                return;
            }

            long delta = (long)seconds * TicksPerSecond;
            long value = (lit ? _lightDriver.LitTicks : _lightDriver.DarkTicks) + delta;
            if (value < 0)
            {
                value = 0;
            }
            else if (value > int.MaxValue)
            {
                value = int.MaxValue;
            }

            if (lit)
            {
                _lightDriver.LitTicks = (int)value;
            }
            else
            {
                _lightDriver.DarkTicks = (int)value;
            }
        }

        /// <summary>当前逻辑帧速率（每秒多少刻）：从模拟驱动器读，取不到就用 <see cref="DefaultLogicRate"/>；
        /// 至少 1——否则按秒步进会变成空操作、秒读数会除零。</summary>
        private int TicksPerSecond
        {
            get
            {
                if (_simDriver == null && _lightDriver != null)
                {
                    _simDriver = _lightDriver.GetComponent<ParticleSimulationDriver>();
                }

                int rate = _simDriver != null ? Mathf.RoundToInt(_simDriver.LogicRate) : DefaultLogicRate;
                return rate < 1 ? 1 : rate;
            }
        }

        /// <summary>强度滑块的值变化：原样写进驱动器（写驱动器而不是 <c>world.Light</c>——
        /// 驱动器每帧把字段写回世界，直接写世界会被下一帧覆盖）。未接线时无事发生。</summary>
        private void OnIntensityChanged(float value)
        {
            if (_lightDriver != null)
            {
                _lightDriver.Intensity = value;
            }
        }

        /// <summary>强度的显示格式：最多三位小数、去掉多余的零（整数看起来就是整数）。</summary>
        private static string FormatIntensity(float intensity)
        {
            return intensity.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>相位刻数的**秒**读数：1 秒 = 当前逻辑帧速率刻，就近取整（<see cref="TicksPerSecond"/> 变了读数跟着变）。</summary>
        private string FormatSeconds(int ticks)
        {
            return Mathf.RoundToInt(ticks / (float)TicksPerSecond) + "秒";
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

        /// <summary>
        /// 在**当前活动场景**里按对象名找一个 <see cref="TMP_Text"/>（找的是物体名，不是组件名）。
        /// 用 <c>GetComponentsInChildren(..., true)</c> 逐根对象铺开，因此**未激活**的对象（收起的光照面板）
        /// 一样能命中——<c>GameObject.Find</c> 只认已激活对象，用不了。找不到返回 null。
        /// </summary>
        private static TMP_Text FindSceneText(string objectName)
        {
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                TMP_Text[] texts = roots[i].GetComponentsInChildren<TMP_Text>(true);
                for (int j = 0; j < texts.Length; j++)
                {
                    if (texts[j].gameObject.name == objectName)
                    {
                        return texts[j];
                    }
                }
            }

            return null;
        }
    }
}
