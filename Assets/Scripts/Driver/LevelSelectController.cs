using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TapTap2026.Game
{
    /// <summary>关卡选择条目：一颗按钮 ↔ 一个阶段 id（顺序 = 界面顺序）。</summary>
    [Serializable]
    public sealed class LevelEntry
    {
        public Button button;
        public string phaseId;
    }

    /// <summary>
    /// 关卡选择界面控制器：按存档里的**解锁集合**控制每颗按钮显隐，玩家点选后写档并加载游戏场景；
    /// 鼠标悬停在关卡按钮上时，把该关的简要描述显示到场景里的 <c>Canvas/ProbeText</c>。
    /// </summary>
    /// <remarks>
    /// <para>Inspector 接线：<c>_levels</c>（按钮 ↔ 阶段 id，数组顺序即界面顺序）、<c>_gameSceneName</c>、<c>_saveFileName</c>、
    /// <c>_probeText</c>（关卡描述显示处，场景 <c>Canvas/ProbeText</c>）、<c>_phases</c>（与 <see cref="GameController"/> 同一批阶段 JSON，只读其中的 description）。</para>
    /// <para>显隐与可选性出自同一个集合（<see cref="SelectableSet"/>），保证**可见即可选**：未解锁 = 整颗隐藏；
    /// 解锁集合为空 / 无存档时退化为「只显示第一关」当入口，避免界面空白卡死；非空但与本界面登记的 id 无一相交（别的版本的旧档）则整屏隐藏。</para>
    /// <para>沙盒就是第五关（<c>phase_05</c>），**不需要任何特殊分支**——解锁了照常显示、没解锁照常隐藏，与其余关卡走同一条路径。
    /// 持久化复用 <see cref="GameController"/> 的公开存档 API（<see cref="GameController.LoadSave(string)"/> /
    /// <see cref="GameController.CollectUnlockedPhaseIds(GameController.SaveData)"/> / <see cref="GameController.WriteSave(string, string, IList{string})"/>）。</para>
    /// <para>悬停提示：鼠标停在某颗关卡按钮上 → 把该关阶段 JSON 的 <see cref="GamePhaseData.description"/> 写到
    /// <see cref="_probeText"/>，指针移开（或按钮被显隐刷新收走）→ 隐藏。描述与点击监听一样**在 <see cref="Awake"/> 里运行期绑定**
    /// （场景里不留持久监听、也不摆 <c>EventTrigger</c>）；文本只从 <c>_phases</c> 读出缓存，不改动任何阶段数据。
    /// 悬停绑定走共用的 <see cref="UiHoverWiring"/>（与投放面板同一条路径：复用按钮上已有的悬停组件、只销毁自己加的）。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LevelSelectController : MonoBehaviour
    {
        [SerializeField] private LevelEntry[] _levels;
        [Tooltip("关卡简要描述显示处（场景 Canvas/ProbeText）。无悬停 / 该关无描述时隐藏。")]
        [SerializeField] private TMP_Text _probeText;
        [Tooltip("与 GameController._phases 同一批阶段 JSON；只取 description 作悬停提示文本。")]
        [SerializeField] private TextAsset[] _phases;
        [SerializeField] private string _gameSceneName = "MainScene";
        [Tooltip("存档文件名（相对 Application.persistentDataPath）。留空回退到 " + GameController.DefaultSaveFileName + "。")]
        [SerializeField] private string _saveFileName = GameController.DefaultSaveFileName;

        /// <summary>Awake 挂上的「按钮 → 监听器」配对：留着同一实例才能在 OnDestroy 精确移除，不动场景里别人的监听。</summary>
        private readonly List<(Button button, UnityAction listener)> _wired = new List<(Button, UnityAction)>();

        /// <summary>阶段 id → 关卡简要描述（只收非空 description）；只按 id 查，故用序数比较。</summary>
        private readonly Dictionary<string, string> _descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Awake 里**自己加**的悬停 <see cref="EventTrigger"/>：只有记在这里的才在 OnDestroy 销毁，场景里原有的不动。</summary>
        private readonly List<EventTrigger> _hoverTriggers = new List<EventTrigger>();

        /// <summary>校验存档文件名并给每条有效条目挂上点击 / 悬停监听；未接线的条目跳过并报错。</summary>
        private void Awake()
        {
            if (string.IsNullOrEmpty(_saveFileName))
            {
                Debug.LogWarning($"[LevelSelectController] _saveFileName 为空，回退到默认 \"{GameController.DefaultSaveFileName}\"。");
                _saveFileName = GameController.DefaultSaveFileName;
            }

            if (_levels == null || _levels.Length == 0)
            {
                Debug.LogError("[LevelSelectController] _levels 未接线或为空，关卡选择界面无法显示任何关卡。");
                return;
            }

            for (int i = 0; i < _levels.Length; i++)
            {
                LevelEntry entry = _levels[i];
                if (entry == null || entry.button == null || string.IsNullOrEmpty(entry.phaseId))
                {
                    Debug.LogError($"[LevelSelectController] _levels[{i}] 未接线（条目 / button / phaseId 任一为空），已跳过。");
                    continue;
                }

                // 每轮拷成局部变量：闭包只捕获本轮副本，不捕获循环变量 i。
                string phaseId = entry.phaseId;
                UnityAction listener = () => SelectLevel(phaseId);
                entry.button.onClick.AddListener(listener);
                _wired.Add((entry.button, listener));

                UiHoverWiring.Attach(
                    entry.button.gameObject, _ => ShowDescription(phaseId), _ => HideDescription(), _hoverTriggers);
            }

            // 悬停提示：ProbeText 未接线只降级（警告 + 保持隐藏），描述表与悬停监听照常走。
            if (_probeText == null)
            {
                Debug.LogWarning("[LevelSelectController] _probeText 未接线，悬停不显示关卡描述（其余交互照常）。");
            }

            HideDescription();
            BuildDescriptions();
        }

        /// <summary>逐个移除 Awake 挂上的监听器并销毁自己加的悬停组件，不给按钮留悬空引用、也不留运行期组件。</summary>
        private void OnDestroy()
        {
            foreach ((Button button, UnityAction listener) in _wired)
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(listener);
                }
            }

            _wired.Clear();

            // 只销毁本组件加的那几个 EventTrigger（记在 _hoverTriggers 里的才是）；场景里原本就有的不动。
            UiHoverWiring.Detach(_hoverTriggers);
        }

        /// <summary>进入界面时读一次存档刷新显隐。</summary>
        private void Start()
        {
            RefreshVisibility();
        }

        /// <summary>读存档 → 取解锁集合 → 应用显隐。</summary>
        public void RefreshVisibility()
        {
            GameController.SaveData save = GameController.LoadSave(_saveFileName);
            ApplyVisibility(GameController.CollectUnlockedPhaseIds(save));
        }

        /// <summary>按解锁集合逐条显隐（纯显隐，不读文件）。解锁集合为空（无存档 / 空档）时兜底只显示第一关。</summary>
        public void ApplyVisibility(ICollection<string> unlockedPhaseIds)
        {
            if (_levels == null || _levels.Length == 0)
            {
                Debug.LogError("[LevelSelectController] _levels 未接线或为空，无法应用显隐。");
                return;
            }

            if (unlockedPhaseIds == null || unlockedPhaseIds.Count == 0)
            {
                Debug.LogWarning("[LevelSelectController] 无可用解锁记录，只显示第一关。");
            }

            HashSet<string> selectable = SelectableSet(unlockedPhaseIds, FirstPhaseId());
            for (int i = 0; i < _levels.Length; i++)
            {
                LevelEntry entry = _levels[i];
                if (entry == null || entry.button == null)
                {
                    continue;
                }

                entry.button.gameObject.SetActive(!string.IsNullOrEmpty(entry.phaseId) && selectable.Contains(entry.phaseId));
            }

            // 显隐刷新一律撤下上一帧的悬停描述：按钮被隐藏后指针不再进/出它，PointerExit 不会再来，
            // 不在这里收掉就会留下对不上任何可见按钮的陈旧文本。
            HideDescription();
        }

        /// <summary>显示 <paramref name="phaseId"/> 的关卡简要描述；无该阶段 / 描述为空 / 未接线 ProbeText → 隐藏。</summary>
        public void ShowDescription(string phaseId)
        {
            if (_probeText == null || string.IsNullOrEmpty(phaseId)
                || !_descriptions.TryGetValue(phaseId, out string description) || string.IsNullOrEmpty(description))
            {
                HideDescription();
                return;
            }

            SetText(_probeText, description);
            SetActive(_probeText.gameObject, true);
        }

        /// <summary>隐藏关卡简要描述（同时清空文本）。</summary>
        public void HideDescription()
        {
            // 隐藏时顺手清空：TMP 下次赋值必重解析重排，留着旧串也不会省下一次赋值。
            SetText(_probeText, null);
            SetActive(_probeText != null ? _probeText.gameObject : null, false);
        }

        /// <summary>校验并写档（选中阶段写入存档，**不加载场景**）。写档成功返回 true。</summary>
        public bool CommitSelection(string phaseId)
        {
            if (string.IsNullOrEmpty(phaseId))
            {
                Debug.LogError("[LevelSelectController] 选择的阶段 id 为空，选择取消。");
                return false;
            }

            GameController.SaveData save = GameController.LoadSave(_saveFileName);
            List<string> unlocked = GameController.CollectUnlockedPhaseIds(save);
            if (!SelectableSet(unlocked, FirstPhaseId()).Contains(phaseId))
            {
                Debug.LogWarning($"[LevelSelectController] 阶段 \"{phaseId}\" 未解锁，选择取消。");
                return false;
            }

            if (!unlocked.Contains(phaseId))
            {
                // 旧档没有它（或走的是第一关兜底）时补进解锁集合；已在集合里则不重复写。
                unlocked.Add(phaseId);
            }

            GameController.WriteSave(_saveFileName, phaseId, unlocked);
            Debug.Log($"[LevelSelectController] 选中阶段 \"{phaseId}\"，已写入存档。");
            return true;
        }

        /// <summary>按钮入口：写档成功后加载游戏场景。</summary>
        public void SelectLevel(string phaseId)
        {
            if (!CommitSelection(phaseId))
            {
                return;
            }

            if (string.IsNullOrEmpty(_gameSceneName))
            {
                Debug.LogError("[LevelSelectController] _gameSceneName 为空，无法加载游戏场景。");
                return;
            }

            SceneManager.LoadScene(_gameSceneName);
        }

        /// <summary>
        /// 建阶段 id → 简要描述表：登记规则沿用 <see cref="GameController.CollectPhaseAssets"/>（id = JSON 文件名去扩展名），
        /// 只收非空 <see cref="GamePhaseData.description"/>；解析失败的条目记错误并跳过——悬停提示缺一条不影响其余交互。
        /// </summary>
        private void BuildDescriptions()
        {
            _descriptions.Clear();
            if (_phases == null || _phases.Length == 0)
            {
                Debug.LogError("[LevelSelectController] 未接线 _phases，悬停不显示关卡描述。");
                return;
            }

            foreach (KeyValuePair<string, TextAsset> pair in GameController.CollectPhaseAssets(_phases))
            {
                if (!GamePhaseParser.TryParse(pair.Value.text, out GamePhaseData phase, out string error))
                {
                    Debug.LogError($"[LevelSelectController] 阶段 \"{pair.Key}\" 解析失败，悬停描述跳过：{error}");
                    continue;
                }

                if (!string.IsNullOrEmpty(phase.description))
                {
                    _descriptions[pair.Key] = phase.description;
                }
            }
        }

        /// <summary>可选择的阶段集合 = 解锁集合；解锁集合为空（null / 空档）时退化为「只有第一条」，与
        /// <see cref="ApplyVisibility"/> 的兜底显示一致——保证可见即可选。</summary>
        private static HashSet<string> SelectableSet(ICollection<string> unlockedPhaseIds, string firstPhaseId)
        {
            HashSet<string> selectable = unlockedPhaseIds == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(unlockedPhaseIds, StringComparer.Ordinal);
            if (selectable.Count == 0 && !string.IsNullOrEmpty(firstPhaseId))
            {
                selectable.Add(firstPhaseId);
            }

            return selectable;
        }

        /// <summary>界面第一条登记了非空 phaseId 的条目 id；没有 → null。</summary>
        private string FirstPhaseId()
        {
            if (_levels == null)
            {
                return null;
            }

            foreach (LevelEntry entry in _levels)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.phaseId))
                {
                    return entry.phaseId;
                }
            }

            return null;
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

        /// <summary>按需切对象激活状态（等价时不碰，避免无谓的 SetActive）。</summary>
        private static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }
    }
}
