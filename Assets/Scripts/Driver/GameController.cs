using System;
using System.Collections.Generic;
using System.IO;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 游戏流程控制器：按阶段 JSON 编排「对话 → 沙箱 → 高速模式结算 → 过关 / 重来」。
    /// 本类只做**流程编排**——对话显示与推进在 <see cref="DialogueController"/>、沙箱重建与推进在
    /// <see cref="ParticleSimulationDriver"/>、投放白名单在 <see cref="ParticleEditor"/>、
    /// 高速模式的计时与统计在 <see cref="HighSpeedSession"/>、胜利判定在 <see cref="VictoryEvaluator"/>。
    /// 本类**不修改模拟内核的任何东西**，也不关心对话内容与粒子行为。
    /// </summary>
    /// <remarks>
    /// <para>阶段数据 = <see cref="GamePhaseData"/>（JSON，字段语义见 docs/GameFlow.md）：阶段 id 取 JSON 文件名（去扩展名），
    /// 由 <see cref="_phases"/> 逐个登记。</para>
    /// <para>一局的流程：<see cref="StartPhase"/> 播对话 + 重建空沙箱 + 套用投放白名单，对话结束后玩家在
    /// <see cref="ParticleEditor"/> 里按 H 进高速模式；会话跑满时长自动暂停，本类在 <see cref="Update"/> 里评估一次胜利条件并缓存结论；
    /// 玩家按 Enter / H 确认退出后，本类按缓存的结论进入下一阶段或重来（重来 = 走「重复进入」分支选对话 + 重建空沙箱）。</para>
    /// <para>胜负**不在本类上屏**：结论经只读属性 <see cref="HasVerdict"/> / <see cref="LastResult"/> / <see cref="LastReason"/> 暴露，
    /// 由 <see cref="ParticleEditor"/> 在高速模式结算面板里显示（过关绿 / 未过关红）。
    /// 本类只管开关场景里的「全部阶段完成」横幅（<see cref="_completionBanner"/>）——不画任何 IMGUI。</para>
    /// <para>存档：每次成功启动阶段都把当前阶段 id 与**已解锁阶段集合**写进
    /// <c>Path.Combine(Application.persistentDataPath, _saveFileName)</c>——刻意只存进度与解锁，不存世界状态。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    // 必须先于 ParticleEditor 的 Update 观察 HighSpeedSession.Completed：
    // 同帧先被 ParticleEditor 确认退出（Exit 会清空统计）就永远评不了胜负，流程卡死。
    [DefaultExecutionOrder(-100)]
    public sealed class GameController : MonoBehaviour
    {
        /// <summary>存档文件名缺省值（Inspector 里 <see cref="_saveFileName"/> 留空时回退到它）。</summary>
        public const string DefaultSaveFileName = "game_save.json";

        [Header("引用")]
        [SerializeField] private ParticleSimulationDriver _driver;
        [SerializeField] private ParticleEditor _editor;
        [SerializeField] private DialogueController _dialogue;

        [Tooltip("「全部阶段完成」横幅（场景接线；场景里默认隐藏，走完最后一个阶段时由本类打开）。")]
        [SerializeField] private GameObject _completionBanner;

        [Header("阶段")]
        [Tooltip("阶段 JSON 列表（TextAsset）。阶段 id = 文件名去扩展名；JSON 里的 id 字段被忽略（加载时回填）。")]
        [SerializeField] private TextAsset[] _phases;

        [Tooltip("没有存档时的起始阶段 id（必须在 _phases 里查得到）。")]
        [SerializeField] private string _initialPhaseId;

        [Tooltip("存档文件名（相对 Application.persistentDataPath）。留空回退到 " + DefaultSaveFileName + "。")]
        [SerializeField] private string _saveFileName = DefaultSaveFileName;

        /// <summary>存档 DTO：记进度（当前阶段 id）+ 已解锁阶段集合，不存世界。字段名即 JSON 键。</summary>
        [Serializable]
        private sealed class SaveData
        {
            public string currentPhaseId;

            /// <summary>已解锁阶段 id（写出前按序号排序，保证同一集合产出稳定文件）。</summary>
            public List<string> unlockedPhases;
        }

        /// <summary>已解锁阶段 id 集合（成功 <see cref="StartPhase"/> 过即加入）；<see cref="Start"/> 读档时回填。</summary>
        private readonly HashSet<string> _unlockedPhases = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>阶段 id → 阶段 JSON 的映射；在 <see cref="Awake"/> 里构建，解析失败的条目记错误并跳过。</summary>
        private readonly Dictionary<string, TextAsset> _phaseAssets = new Dictionary<string, TextAsset>();

        /// <summary>当前阶段的解析结果；仅在 <see cref="StartPhase"/> 成功时更新。</summary>
        private GamePhaseData _currentPhase;

        /// <summary>本帧高速会话是否 Active（用于识别「上一帧还在高速、本帧退出了」这一跳变）。</summary>
        private bool _sessionWasActive;

        /// <summary>是否已经见过本会话的结算（<see cref="HighSpeedSession.Completed"/>）。</summary>
        private bool _sessionCompletedSeen;

        /// <summary>缓存的胜利判定结论（仅在结算那一刻求值一次）。</summary>
        private bool _lastResult;

        /// <summary>缓存的判定理由（通过摘要 / 第一条未满足项）。</summary>
        private string _lastReason;

        /// <summary>是否已走完最后一个阶段（打开常驻的「全部阶段完成」横幅，不再启动新阶段）。</summary>
        private bool _allPhasesCompleted;

        /// <summary>当前阶段 id；尚未成功启动任何阶段时为空串。</summary>
        public string CurrentPhaseId { get; private set; } = string.Empty;

        /// <summary>本次结算是否已出结论（高速会话进过 <see cref="HighSpeedSession.Completed"/>、本类已求值一次）。
        /// 结算面板据此决定是否显示胜负行。</summary>
        public bool HasVerdict => _sessionCompletedSeen;

        /// <summary>缓存的胜负结论：true = 过关。仅在 <see cref="HasVerdict"/> 为真时有意义。</summary>
        public bool LastResult => _lastResult;

        /// <summary>缓存的判定理由（通过摘要 / 第一条未满足项）；无结论时为 null。</summary>
        public string LastReason => _lastReason;

        /// <summary>接线兜底（引用未接时 GetComponent → FindObjectOfType）并构建阶段映射表。只在启动阶段做一次。</summary>
        private void Awake()
        {
            // 引用未接线时自动兜底查找（只在 Awake 查一次并缓存）。
            if (_driver == null)
            {
                _driver = GetComponent<ParticleSimulationDriver>();
            }

            if (_driver == null)
            {
                _driver = FindObjectOfType<ParticleSimulationDriver>();
            }

            if (_editor == null)
            {
                _editor = GetComponent<ParticleEditor>();
            }

            if (_editor == null)
            {
                _editor = FindObjectOfType<ParticleEditor>();
            }

            if (_dialogue == null)
            {
                _dialogue = GetComponent<DialogueController>();
            }

            if (_dialogue == null)
            {
                _dialogue = FindObjectOfType<DialogueController>();
            }

            if (string.IsNullOrEmpty(_saveFileName))
            {
                Debug.LogWarning($"[GameController] _saveFileName 为空，回退到默认 \"{DefaultSaveFileName}\"。");
                _saveFileName = DefaultSaveFileName;
            }

            if (_completionBanner == null)
            {
                Debug.LogWarning("[GameController] 未接线 _completionBanner，「全部阶段完成」横幅不会显示。");
            }
            else
            {
                // 场景里本就默认隐藏，这里按状态再钉一次（初值 false = 还没走完）——双保险。
                _completionBanner.SetActive(_allPhasesCompleted);
            }

            BuildPhaseMap();
        }

        /// <summary>
        /// 读存档决定起始阶段：先回填**已解锁阶段集合**（旧档没有 unlockedPhases 时用 currentPhaseId 回填），
        /// 再取存档里的 id——有效就用它，否则回退 <see cref="_initialPhaseId"/>；
        /// 两者都查不到（或未配置）就记错误、不启动流程。
        /// </summary>
        private void Start()
        {
            SaveData data = LoadSave();
            if (data != null)
            {
                if (data.unlockedPhases != null)
                {
                    for (int i = 0; i < data.unlockedPhases.Count; i++)
                    {
                        string unlocked = data.unlockedPhases[i];
                        if (!string.IsNullOrEmpty(unlocked))
                        {
                            _unlockedPhases.Add(unlocked);
                        }
                    }
                }

                // 旧档兼容：unlockedPhases 缺失（或为空）时，把当前阶段本身当作已解锁回填。
                string saved = data.currentPhaseId;
                if (!string.IsNullOrEmpty(saved))
                {
                    _unlockedPhases.Add(saved);
                }
            }

            string id = data != null ? data.currentPhaseId : null;
            if (string.IsNullOrEmpty(id))
            {
                id = _initialPhaseId;
            }

            if (string.IsNullOrEmpty(id))
            {
                Debug.LogError("[GameController] 没有可启动的阶段：存档为空且 _initialPhaseId 未配置。");
                return;
            }

            if (!_phaseAssets.ContainsKey(id))
            {
                Debug.LogError($"[GameController] 阶段 id \"{id}\" 不在 _phases 里，退回 _initialPhaseId。");
                id = _initialPhaseId;
            }

            if (string.IsNullOrEmpty(id) || !_phaseAssets.ContainsKey(id))
            {
                Debug.LogError("[GameController] 起始阶段无效，游戏流程不启动。");
                return;
            }

            StartPhase(id);
        }

        /// <summary>
        /// 高速模式的轮询（每帧一次，纯观察）：会话一进入「Active 且 Completed」就评估一次胜利条件并缓存结论；
        /// 随后观察到会话退出（Active 变 false）且确实结算过，就按缓存结论推进流程（下一阶段 / 重来 / 全部完成）。
        /// 会话被世界重建丢弃（从未结算）时不采取任何行动。
        /// </summary>
        private void Update()
        {
            if (_driver == null)
            {
                return;
            }

            // 懒建属性：世界建好即非 null（不访问它反而不会建会话）。
            HighSpeedSession session = _driver.HighSpeed;
            if (session == null)
            {
                return;
            }

            bool active = session.Active;

            // 结算那一刻：会话仍 Active（等玩家确认退出），评估一次胜利条件并**缓存**结论与理由。
            if (active && session.Completed && !_sessionCompletedSeen)
            {
                _sessionCompletedSeen = true;
                _lastResult = VictoryEvaluator.Evaluate(
                    _currentPhase != null ? _currentPhase.victory : null,
                    BuildSnapshot(session),
                    out string reason);
                _lastReason = reason;
            }

            // 退出那一刻：上一帧还在高速模式、本帧不在了，且确实进过结算 → 按缓存结论推进流程。
            // 会话被世界重建丢弃（Resize / 换阶段）而从未结算的路径不会走到这里（_sessionCompletedSeen 为 false）。
            if (_sessionWasActive && !active && _sessionCompletedSeen)
            {
                _sessionWasActive = false;
                _sessionCompletedSeen = false;
                ApplyResult();
                return;
            }

            _sessionWasActive = active;
        }

        /// <summary>
        /// 启动阶段 <paramref name="id"/>（id = 阶段 JSON 文件名去扩展名）。顺序固定：
        /// ① 解析该阶段的 JSON（失败记错误并返回，不改变当前状态）；
        /// ② 按 <c>isRepeat</c>（阶段是否已在解锁集合中）经 <see cref="GamePhaseData.ResolveOpeningDialogueId"/>
        ///    选开场对话——首次进入用 <c>dialogueId</c>、重复进入用 <c>repeatDialogueId</c>，非空才交给
        ///    <see cref="DialogueController"/> 播（启动失败只警告，流程继续）；
        /// ③ <see cref="ParticleSimulationDriver.Resize"/> 重建沙箱（宽高相同也调——重来要的是空世界）；
        /// ④ <see cref="ParticleEditor.SetPlacementFilter"/> 套用本阶段可投放粒子；
        /// ⑤ 把本阶段加入解锁集合、写存档，最后更新 <see cref="CurrentPhaseId"/> 与当前阶段缓存。
        /// </summary>
        /// <remarks>未接线 <see cref="_driver"/> 记错误并直接返回；<see cref="_dialogue"/> / <see cref="_editor"/> 缺失只降级警告、跳过对应步骤。</remarks>
        public void StartPhase(string id)
        {
            if (_driver == null)
            {
                Debug.LogError("[GameController] StartPhase：未接线 ParticleSimulationDriver，阶段无法启动。");
                return;
            }

            if (string.IsNullOrEmpty(id) || !_phaseAssets.TryGetValue(id, out TextAsset asset))
            {
                Debug.LogError($"[GameController] StartPhase：未找到阶段 id \"{id}\"（检查 _phases 是否登记了该 JSON）。");
                return;
            }

            if (!GamePhaseParser.TryParse(asset.text, out GamePhaseData phase, out string error))
            {
                Debug.LogError($"[GameController] StartPhase：阶段 \"{id}\" 解析失败：{error}");
                return;
            }

            phase.id = id;

            // 重进判定必须在加入解锁集合**之前**取：否则重复进入会被误判成首次。
            bool isRepeat = _unlockedPhases.Contains(id);
            string openingDialogueId = phase.ResolveOpeningDialogueId(isRepeat);

            if (!string.IsNullOrEmpty(openingDialogueId))
            {
                if (_dialogue == null)
                {
                    Debug.LogWarning("[GameController] 阶段配了开场对话但没接线 DialogueController，跳过对话。");
                }
                else if (!_dialogue.StartDialogue(openingDialogueId))
                {
                    Debug.LogWarning($"[GameController] 阶段 \"{id}\" 的对话 \"{openingDialogueId}\" 启动失败，继续流程。");
                }
            }

            _driver.Resize(phase.sandboxWidth, phase.sandboxHeight);

            if (_editor == null)
            {
                Debug.LogWarning("[GameController] 未接线 ParticleEditor，跳过投放白名单设置。");
            }
            else
            {
                _editor.SetPlacementFilter(phase.availableParticles);
            }

            _unlockedPhases.Add(id);
            SaveProgress(id);

            _currentPhase = phase;
            CurrentPhaseId = id;
            _sessionWasActive = false;
            _sessionCompletedSeen = false;
            _lastReason = null;
            _allPhasesCompleted = false;
            SetCompletionBannerVisible(false);
        }

        /// <summary>本阶段是否已在解锁集合里。空 id 返回 false；本方法只查集合，不查阶段表。</summary>
        public bool IsPhaseUnlocked(string id)
        {
            return !string.IsNullOrEmpty(id) && _unlockedPhases.Contains(id);
        }

        /// <summary>
        /// 跳转到阶段 <paramref name="id"/>（供控制台 / 调试或后续关卡选择 UI 的代码入口）。
        /// 门槛：空 id 或不在阶段表 → <c>LogError</c> + <c>false</c>；尚未解锁 → <c>LogWarning</c> + <c>false</c>；
        /// 通过则由 <see cref="StartPhase"/> 实际启动（它自己会再校验一次）并返回 <c>true</c>。
        /// </summary>
        /// <remarks>刻意不做关卡选择 UI（见 docs/GameFlow.md §8）——这里只提供代码入口与解锁门槛。</remarks>
        public bool JumpToPhase(string id)
        {
            if (string.IsNullOrEmpty(id) || !_phaseAssets.ContainsKey(id))
            {
                Debug.LogError($"[GameController] JumpToPhase：阶段 id \"{id}\" 不在 _phases 里，跳转取消。");
                return false;
            }

            if (!_unlockedPhases.Contains(id))
            {
                Debug.LogWarning($"[GameController] JumpToPhase：阶段 \"{id}\" 尚未解锁，跳转取消。");
                return false;
            }

            StartPhase(id);
            return true;
        }

        /// <summary>
        /// 开关「全部阶段完成」横幅：接线为空时是空操作（<see cref="Awake"/> 已就此报过警告）。
        /// 全部阶段完成是**常驻**状态：只有 <see cref="StartPhase"/>（重来 / 下一阶段）会把它重新收起。
        /// </summary>
        private void SetCompletionBannerVisible(bool visible)
        {
            if (_completionBanner != null)
            {
                _completionBanner.SetActive(visible);
            }
        }

        /// <summary>
        /// 按缓存的结算结论行动：过关 → 有 <see cref="GamePhaseData.nextPhaseId"/> 就进下一阶段，
        /// 没有就把 <see cref="_allPhasesCompleted"/> 置位（常驻「全部阶段完成」）；未过关 → 重开当前阶段
        /// （<see cref="StartPhase"/> 会按解锁集合走「重复进入」分支选对话 + 重建空沙箱）。
        /// </summary>
        private void ApplyResult()
        {
            GamePhaseData phase = _currentPhase;
            string phaseId = CurrentPhaseId;

            if (!_lastResult)
            {
                Debug.LogWarning($"[GameController] 阶段 \"{phaseId}\" 未过关（{_lastReason}），重来。");
                StartPhase(phaseId);
                return;
            }

            if (phase == null || string.IsNullOrEmpty(phase.nextPhaseId))
            {
                Debug.Log($"[GameController] 阶段 \"{phaseId}\" 过关（{_lastReason}），已是最终阶段——全部阶段完成。");
                _allPhasesCompleted = true;
                SetCompletionBannerVisible(true);
                return;
            }

            Debug.Log($"[GameController] 阶段 \"{phaseId}\" 过关（{_lastReason}），进入下一阶段 \"{phase.nextPhaseId}\"。");
            StartPhase(phase.nextPhaseId);
        }

        /// <summary>
        /// 从当前高速会话构建判定快照：温度 / 湿度取会话的最新采样（可能为 <see cref="float.NaN"/> = 未采样），
        /// 计数取**全部已注册类型**（id 从 1 起、跳过注册表尾部的 null 槽、不含空气），含计数为 0 的类型；
        /// 存量取 <see cref="HighSpeedSession.GetFinalCount"/>（会话跑满时的一次全网格盘点）。
        /// </summary>
        /// <remarks>
        /// 调用时机固定在会话 <see cref="HighSpeedSession.Completed"/> 那一帧（会话仍 Active、尚未 <see cref="HighSpeedSession.Exit"/>），
        /// 因此 <c>GetFinalCount</c> 拿得到真实存量——它一旦 Exit 就清空、返回 0。
        /// </remarks>
        private static HighSpeedSnapshot BuildSnapshot(HighSpeedSession session)
        {
            ParticleType[] types = ParticleRegistry.Types;
            var counts = new List<ParticleTypeCount>();

            for (int id = 1; id < types.Length; id++)
            {
                if (types[id] == null)
                {
                    continue;
                }

                counts.Add(new ParticleTypeCount
                {
                    typeId = (ushort)id,
                    births = session.GetBirths(id),
                    deaths = session.GetDeaths(id),
                    finalCount = session.GetFinalCount(id),
                });
            }

            return new HighSpeedSnapshot
            {
                surfaceTemperature = session.LatestSurfaceTemperature,
                averageHumidity = session.LatestAverageHumidity,
                counts = counts.ToArray(),
            };
        }

        /// <summary>
        /// 构建阶段 id → JSON 的映射（id = TextAsset 文件名去扩展名）。解析失败的条目记错误并跳过——
        /// 这样 <see cref="Start"/> 的起始阶段查找天然跳过坏条目，不会拿一段读不懂的 JSON 启动流程。
        /// </summary>
        private void BuildPhaseMap()
        {
            _phaseAssets.Clear();

            if (_phases == null || _phases.Length == 0)
            {
                Debug.LogError("[GameController] 未配置任何阶段 JSON（_phases 为空），游戏流程无法启动。");
                return;
            }

            for (int i = 0; i < _phases.Length; i++)
            {
                TextAsset asset = _phases[i];
                if (asset == null)
                {
                    Debug.LogWarning($"[GameController] _phases[{i}] 为 null，已跳过。");
                    continue;
                }

                string id = asset.name;
                if (string.IsNullOrEmpty(id))
                {
                    Debug.LogError($"[GameController] _phases[{i}] 没有文件名，无法作为阶段 id，已跳过。");
                    continue;
                }

                if (_phaseAssets.ContainsKey(id))
                {
                    Debug.LogError($"[GameController] 阶段 id \"{id}\" 重复（_phases[{i}]），该条目已跳过。");
                    continue;
                }

                if (!GamePhaseParser.TryParse(asset.text, out GamePhaseData phase, out string error))
                {
                    Debug.LogError($"[GameController] 阶段 \"{id}\" 解析失败，已跳过：{error}");
                    continue;
                }

                phase.id = id;
                _phaseAssets[id] = asset;
            }
        }

        /// <summary>
        /// 把当前阶段 id + 已解锁阶段集合写进存档；解锁集合复制成 List 后按序号排序（同一集合产出稳定文件）。
        /// IO 失败记错误但**不中断**流程（进度丢了也还能玩）。
        /// </summary>
        private void SaveProgress(string phaseId)
        {
            var unlocked = new List<string>(_unlockedPhases);
            unlocked.Sort(StringComparer.Ordinal);

            string path = SavePath;
            try
            {
                File.WriteAllText(
                    path,
                    JsonUtility.ToJson(new SaveData { currentPhaseId = phaseId, unlockedPhases = unlocked }));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameController] 写存档失败（{path}）：{exception.Message}");
            }
        }

        /// <summary>
        /// 读存档 DTO；文件不存在 / 读取失败 / 反序列化失败（含坏 JSON）都返回 null
        /// （由调用方回退起始阶段，并跳过解锁集合回填）。
        /// </summary>
        private SaveData LoadSave()
        {
            string path = SavePath;
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameController] 读存档失败（{path}）：{exception.Message}");
                return null;
            }
        }

        /// <summary>存档的完整路径：<c>Application.persistentDataPath</c> + <see cref="_saveFileName"/>。</summary>
        private string SavePath
        {
            get { return Path.Combine(Application.persistentDataPath, _saveFileName); }
        }
    }
}
