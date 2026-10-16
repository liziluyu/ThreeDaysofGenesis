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
    /// 玩家按 Enter / H 确认退出后，本类按缓存的结论进入下一阶段或重来（重来 = 走「重复进入」分支选对话 + 重建空沙箱）；
    /// 阶段配了结算对话（<see cref="GamePhaseData.ResolveResultDialogueId"/>）时，先播完它再执行这次切换。</para>
    /// <para>胜负**不在本类上屏**：结论经只读属性 <see cref="HasVerdict"/> / <see cref="LastResult"/> / <see cref="LastReason"/> 暴露，
    /// 由 <see cref="ParticleEditor"/> 在高速模式结算面板里显示（过关绿 / 未过关红）。
    /// 本类只管开关场景里的「全部阶段完成」横幅（<see cref="_completionBanner"/>）——不画任何 IMGUI。</para>
    /// <para>存档：每次成功启动阶段都把当前阶段 id 与**已解锁阶段集合**写进 <see cref="SavePathFor(string)"/> 给出的文件
    /// （<c>persistentDataPath</c> + 存档文件名）——刻意只存进度与解锁，不存世界状态。</para>
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
        public sealed class SaveData
        {
            public string currentPhaseId;

            /// <summary>已解锁阶段 id（写出前按序号排序，保证同一集合产出稳定文件）。</summary>
            public List<string> unlockedPhases;
        }

        /// <summary>已解锁阶段 id 集合（成功 <see cref="StartPhase"/> 过即加入）；<see cref="Start"/> 读档时回填。</summary>
        private readonly HashSet<string> _unlockedPhases = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>阶段 id → 阶段 JSON 的映射；在 <see cref="Awake"/> 里经 <see cref="CollectPhaseAssets"/> 构建，解析失败的条目记错误并跳过。</summary>
        private Dictionary<string, TextAsset> _phaseAssets;

        /// <summary>当前阶段的解析结果；仅在 <see cref="StartPhase"/> 成功时更新。</summary>
        private GamePhaseData _currentPhase;

        /// <summary>本帧高速会话是否 Active（用于识别「上一帧还在高速、本帧退出了」这一跳变）。</summary>
        private bool _sessionWasActive;

        /// <summary>是否已经见过本会话的结算（<see cref="HighSpeedSession.Completed"/>）。</summary>
        private bool _sessionCompletedSeen;

        /// <summary>本次结算是否真的求过胜利条件（沙盒阶段没有 victory，跳过判定时为 false）。</summary>
        private bool _sessionHadVerdict;

        /// <summary>缓存的胜利判定结论（仅在结算那一刻求值一次）。</summary>
        private bool _lastResult;

        /// <summary>缓存的判定理由（通过摘要 / 第一条未满足项）。</summary>
        private string _lastReason;

        /// <summary>结算对话正在播，阶段切换已挂起；它播完（或起播失败）再执行。</summary>
        private bool _awaitingResultDialogue;

        /// <summary>是否已走完最后一个阶段（打开常驻的「全部阶段完成」横幅，不再启动新阶段）。</summary>
        private bool _allPhasesCompleted;

        /// <summary>当前阶段 id；尚未成功启动任何阶段时为空串。</summary>
        public string CurrentPhaseId { get; private set; } = string.Empty;

        /// <summary>本次结算是否已出结论（高速会话进过 <see cref="HighSpeedSession.Completed"/>、本类已求值一次）；
        /// 沙盒阶段（无 victory）恒为 false。结算面板据此决定是否显示胜负行。</summary>
        public bool HasVerdict => _sessionCompletedSeen && _sessionHadVerdict;

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
            SaveData data = LoadSave(_saveFileName);

            // 回填规则见 CollectUnlockedPhaseIds：unlockedPhases 里每个非空 id + currentPhaseId（旧档兼容）。
            _unlockedPhases.UnionWith(CollectUnlockedPhaseIds(data));

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
        /// 高速模式的轮询（每帧一次，纯观察）：会话一进入「Active 且 Completed」就评估一次胜利条件并缓存结论
        /// （阶段没有 <c>victory</c> = 沙盒，跳过判定、不置 <see cref="HasVerdict"/>）；
        /// 随后观察到会话退出（Active 变 false）且确实求过胜负，才按缓存结论推进流程（下一阶段 / 重来 / 全部完成）——
        /// 若本阶段配了结算对话，则先播它、播完（或起播失败）才真正执行切换，见 <see cref="_awaitingResultDialogue"/>。
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

            // 挂起的阶段切换：结算对话还在播就等它，播完（或对话被强制关闭）才执行。
            if (_awaitingResultDialogue)
            {
                if (_dialogue != null && _dialogue.IsActive)
                {
                    return;
                }

                _awaitingResultDialogue = false;
                FinishResult();
                return;
            }

            bool active = session.Active;

            // 结算那一刻：会话仍 Active（等玩家确认退出），评估一次胜利条件并**缓存**结论与理由。
            if (active && session.Completed && !_sessionCompletedSeen)
            {
                _sessionCompletedSeen = true;

                if (_currentPhase != null && _currentPhase.victory != null)
                {
                    _sessionHadVerdict = true;
                    _lastResult = VictoryEvaluator.Evaluate(
                        _currentPhase.victory,
                        BuildSnapshot(session),
                        out string reason);
                    _lastReason = reason;
                }
                else
                {
                    // 沙盒阶段（没有 victory）：不判定、不推进，结算面板也不显示胜负行。
                    _sessionHadVerdict = false;
                    _lastResult = false;
                    _lastReason = null;
                    Debug.Log($"[GameController] 阶段 \"{CurrentPhaseId}\" 本阶段没有胜利条件（沙盒），高速模式结束后不做判定与推进。");
                }
            }

            // 退出那一刻：上一帧还在高速模式、本帧不在了，且确实进过结算 → 按缓存结论推进流程。
            // 会话被世界重建丢弃（Resize / 换阶段）而从未结算的路径不会走到这里（_sessionCompletedSeen 为 false）。
            if (_sessionWasActive && !active && _sessionCompletedSeen)
            {
                _sessionWasActive = false;
                _sessionCompletedSeen = false;

                // 只有真求过胜负才推进流程（沙盒阶段到此为止，不判过关也不重来）。
                if (_sessionHadVerdict)
                {
                    ApplyResult();
                }

                _sessionHadVerdict = false;
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
            WriteSave(_saveFileName, id, new List<string>(_unlockedPhases));

            // 阶段一启动就作废任何挂起的结算收尾（本方法也覆盖外部 JumpToPhase 的跳转）：
            // 否则上一轮的结算对话播完后，会把玩家从新阶段拽回去重来 / 前进。
            _currentPhase = phase;
            CurrentPhaseId = id;
            _sessionWasActive = false;
            _sessionCompletedSeen = false;
            _sessionHadVerdict = false;
            _awaitingResultDialogue = false;
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
        /// 跳转到阶段 <paramref name="id"/>（供关卡选择界面与控制台 / 调试调用的代码入口）。
        /// 门槛：空 id 或不在阶段表 → <c>LogError</c> + <c>false</c>；尚未解锁 → <c>LogWarning</c> + <c>false</c>；
        /// 通过则由 <see cref="StartPhase"/> 实际启动（它自己会再校验一次）并返回 <c>true</c>。
        /// </summary>
        /// <remarks>供 <see cref="LevelSelectController"/>（关卡选择界面）与控制台 / 调试调用——解锁门槛与阶段表校验都在这层。</remarks>
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
        /// 结算入口：本阶段配了结算对话就先播、并挂起阶段切换（<see cref="_awaitingResultDialogue"/>，
        /// 由 <see cref="Update"/> 等它播完后再走 <see cref="FinishResult"/>）；没配 / 未接线 / 起播失败 ⇒ 立刻 <see cref="FinishResult"/>。
        /// </summary>
        private void ApplyResult()
        {
            GamePhaseData phase = _currentPhase;

            // 结算对话（若本阶段配了）：先播完它、再切阶段——切换会把世界清空（Resize）并播新阶段开场，
            // 提前做会让玩家在空沙箱上读结算。没配 / 未接线 / 起播失败 ⇒ 与既有行为一致（立刻切换）。
            if (StartResultDialogue(phase != null ? phase.ResolveResultDialogueId(_lastResult) : null))
            {
                _awaitingResultDialogue = true;
                return;
            }

            FinishResult();
        }

        /// <summary>起播结算对话。空 id / 未接线 DialogueController / 注册表里查不到 → 返回 false（调用方按"没有结算对话"处理）。</summary>
        private bool StartResultDialogue(string dialogueId)
        {
            if (string.IsNullOrEmpty(dialogueId))
            {
                return false;
            }

            if (_dialogue == null)
            {
                Debug.LogWarning($"[GameController] 阶段 \"{CurrentPhaseId}\" 配了结算对话 \"{dialogueId}\" 但没接线 DialogueController，跳过。");
                return false;
            }

            if (!_dialogue.StartDialogue(dialogueId))
            {
                Debug.LogWarning($"[GameController] 阶段 \"{CurrentPhaseId}\" 的结算对话 \"{dialogueId}\" 启动失败，按无结算对话处理。");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 结算收尾：按缓存的结论重来 / 进下一阶段 / 开完成横幅。过关 → 有 <see cref="GamePhaseData.nextPhaseId"/> 就进下一阶段，
        /// 没有就把 <see cref="_allPhasesCompleted"/> 置位（常驻「全部阶段完成」）；未过关 → 重开当前阶段
        /// （<see cref="StartPhase"/> 会按解锁集合走「重复进入」分支选对话 + 重建空沙箱）。
        /// 阶段切换逻辑**只此一处**——<see cref="ApplyResult"/> 只负责「先播结算对话、再回来执行这里」。
        /// </summary>
        private void FinishResult()
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
        /// 构建阶段 id → JSON 的映射。建表规则见 <see cref="CollectPhaseAssets"/>——
        /// 这样 <see cref="Start"/> 的起始阶段查找天然跳过坏条目，不会拿一段读不懂的 JSON 启动流程。
        /// </summary>
        private void BuildPhaseMap()
        {
            _phaseAssets = CollectPhaseAssets(_phases);
        }

        /// <summary>
        /// 按 <see cref="TextAsset.name"/>（= 阶段 id，去扩展名）建 id → 资产表。
        /// null 条目 / 无文件名 / 重复 id / JSON 解析失败 一律记错误并跳过该条目（顺序 = 入参数组顺序）。
        /// 供本类建内部阶段表与关卡选择界面读阶段元数据（如 description）共用，
        /// 「id = 文件名」这条约定只此一处。
        /// </summary>
        public static Dictionary<string, TextAsset> CollectPhaseAssets(TextAsset[] phases)
        {
            var assets = new Dictionary<string, TextAsset>();

            if (phases == null || phases.Length == 0)
            {
                Debug.LogError("[GameController] 未配置任何阶段 JSON（_phases 为空），游戏流程无法启动。");
                return assets;
            }

            for (int i = 0; i < phases.Length; i++)
            {
                TextAsset asset = phases[i];
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

                if (assets.ContainsKey(id))
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
                assets[id] = asset;
            }

            return assets;
        }

        /// <summary>
        /// 存档的完整路径：<c>Application.persistentDataPath</c> + 文件名
        /// （<paramref name="saveFileName"/> 为空时回退 <see cref="DefaultSaveFileName"/>）。
        /// </summary>
        public static string SavePathFor(string saveFileName)
        {
            return Path.Combine(
                Application.persistentDataPath,
                string.IsNullOrEmpty(saveFileName) ? DefaultSaveFileName : saveFileName);
        }

        /// <summary>
        /// 读存档 DTO；文件不存在 / 读取失败 / 反序列化失败（含坏 JSON）都返回 null
        /// （由调用方回退起始阶段，并跳过解锁集合回填）。
        /// </summary>
        public static SaveData LoadSave(string saveFileName)
        {
            string path = SavePathFor(saveFileName);
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

        /// <summary>
        /// 从存档里收「已解锁」的阶段 id：<see cref="SaveData.unlockedPhases"/> 里每个非空 id，
        /// **外加** <see cref="SaveData.currentPhaseId"/>——旧档没有 unlockedPhases 时，当前阶段本身即已解锁。
        /// <paramref name="data"/> 为 null 时返回空列表；结果已去重（顺序 = unlockedPhases 顺序，当前阶段补在末尾）。
        /// </summary>
        public static List<string> CollectUnlockedPhaseIds(SaveData data)
        {
            var unlocked = new List<string>();
            if (data == null)
            {
                return unlocked;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (data.unlockedPhases != null)
            {
                for (int i = 0; i < data.unlockedPhases.Count; i++)
                {
                    string id = data.unlockedPhases[i];
                    if (!string.IsNullOrEmpty(id) && seen.Add(id))
                    {
                        unlocked.Add(id);
                    }
                }
            }

            if (!string.IsNullOrEmpty(data.currentPhaseId) && seen.Add(data.currentPhaseId))
            {
                unlocked.Add(data.currentPhaseId);
            }

            return unlocked;
        }

        /// <summary>
        /// 写存档：<paramref name="unlockedPhaseIds"/> 复制成 List 后按序号排序（同一集合产出稳定文件），
        /// 与 <paramref name="currentPhaseId"/> 一起 <see cref="JsonUtility.ToJson(object)"/> 写出。
        /// IO 失败记错误但**不抛**（进度丢了也还能玩）。
        /// </summary>
        public static void WriteSave(string saveFileName, string currentPhaseId, IList<string> unlockedPhaseIds)
        {
            var unlocked = unlockedPhaseIds != null
                ? new List<string>(unlockedPhaseIds)
                : new List<string>();
            unlocked.Sort(StringComparer.Ordinal);

            string path = SavePathFor(saveFileName);
            try
            {
                File.WriteAllText(
                    path,
                    JsonUtility.ToJson(new SaveData { currentPhaseId = currentPhaseId, unlockedPhases = unlocked }));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameController] 写存档失败（{path}）：{exception.Message}");
            }
        }
    }
}
