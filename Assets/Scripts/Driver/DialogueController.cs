using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>对话注册表条目：<c>id</c> → 对话文件（供 <see cref="DialogueController.StartDialogue(string)"/>）。</summary>
    [Serializable]
    public sealed class DialogueRef
    {
        public string id;
        public TextAsset asset;
    }

    /// <summary>
    /// 对话控制器（**UI 无关核心**）：状态机推进、打字机（逐字）、输入热键、暂停模拟、说话人解析（名字 / 头像 / 立绘）。
    /// <para>显示全部通过 <see cref="IDialogueView"/> 抽象出去——换对话框（例如接入另一个项目的 UI）只实现该接口，
    /// 再 <see cref="SetView"/> 交给控制器即可，**对话逻辑一行不用改**。</para>
    /// <para>自带的调试 UI 是 <see cref="DialogueDebugView"/>（同物体的组件，自动作为默认视图；禁用它并 SetView 即可替换）。</para>
    /// </summary>
    /// <remarks>
    /// 依赖：TextMeshPro（仅调试视图需要；核心不需要）。Inspector 接线：<c>_driver</c>（暂停/恢复模拟）、
    /// <c>_editor</c>（对话时屏蔽其输入）、<c>_portraitFolder</c> / <c>_avatarFolder</c>（Resources 里的立绘 / 头像文件夹）、<c>_dialogues</c>（id→文件）、<c>_debugDialogue</c>。
    /// </remarks>
    public sealed class DialogueController : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private ParticleSimulationDriver _driver;
        [SerializeField] private ParticleEditor _editor;

        [Header("对话文件")]
        [Tooltip("默认/公开的对话文件（public 变量）。")]
        public TextAsset DialogueFile;
        [SerializeField] private DialogueRef[] _dialogues;
        [SerializeField] private TextAsset _debugDialogue;

        [Header("角色资源（相对 Assets/Resources 的路径，不含扩展名）")]
        [Tooltip("立绘文件夹。")]
        [SerializeField] private string _portraitFolder = "Characters/Portraits";
        [Tooltip("头像文件夹。")]
        [SerializeField] private string _avatarFolder = "Characters/Avatars";

        [Header("打字机 / 输入")]
        [SerializeField] private float _charsPerSecond = 40f;
        [SerializeField] private KeyCode _nextKey = KeyCode.Space;
        [SerializeField] private bool _nextOnLeftClick = true;
        [SerializeField] private KeyCode _debugStartKey = KeyCode.T;

        /// <summary>对话播完派发（参数 = 文件的 <c>onEnd</c> 事件名）。外部脚本可订阅。</summary>
        public event Action<string> OnDialogueEnd;

        /// <summary>是否有对话正在播放。</summary>
        public bool IsActive { get; private set; }

        private readonly DialogueRunner _runner = new DialogueRunner();
        private readonly Dictionary<string, DialogueCharacter> _characterMap = new Dictionary<string, DialogueCharacter>();
        private readonly Dictionary<string, TextAsset> _dialogueMap = new Dictionary<string, TextAsset>();
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, Sprite> _placeholderByKey = new Dictionary<string, Sprite>();
        private readonly HashSet<string> _warnedSpeakers = new HashSet<string>();

        private IDialogueView _view;       // 外部注入（SetView）
        private IDialogueView _sceneView;  // 场景里的 DialogueDebugView

        private Coroutine _typing;
        private string _fullLine = string.Empty;
        private bool _isTyping;
        private bool _pausedByDialogue;

        // 结束对话的那一帧里，Space / 点击会被 ParticleEditor.Update 同帧消费（两者无执行顺序保证）；
        // 因此输入屏蔽的恢复延后到下一帧帧首（见 Update / EndDialogue）。0 = 无待恢复。
        private int _restoreInputFrame;

        private IDialogueView View { get { return _view != null ? _view : _sceneView; } }

        private void Awake()
        {
            // 引用未接线时自动兜底查找（只在 Awake 查一次并缓存）。
            if (_driver == null)
            {
                _driver = FindObjectOfType<ParticleSimulationDriver>();
            }

            if (_editor == null)
            {
                _editor = FindObjectOfType<ParticleEditor>();
            }

            _sceneView = GetComponent<DialogueDebugView>();
            BuildMaps();
            _runner.OnEnd += HandleRunnerEnd;

            Debug.Log($"[DialogueController] 就绪：view={((View != null) ? View.GetType().Name : "null")}，" +
                $"driver={(_driver != null)}，editor={(_editor != null)}，debugDialogue={(_debugDialogue != null)}。");
        }

        private void OnDisable()
        {
            // 禁用会停 Update 但**不停协程**：若不收尾，打字机会跑完，却再也等不到输入推进（对话卡住），
            // 同时 SuppressInput / 模拟暂停会永久残留。OnDestroy 前必经 OnDisable，故在此兜底。
            // 禁用后 Update 不会运行，无法走「延后一帧」的恢复路径，必须同步恢复。
            if (IsActive)
            {
                EndDialogue(false);
            }
            else if (_restoreInputFrame != 0)
            {
                // 对话刚结束、延后恢复还没来得及执行就被禁用：这里补上，避免 SuppressInput 永久残留。
                _restoreInputFrame = 0;
                SetEditorSuppressed(false);
            }
        }

        private void OnDestroy()
        {
            _runner.OnEnd -= HandleRunnerEnd;
        }

        // ============================== 公开 API ==============================

        /// <summary>注入/替换显示层（外部 UI 在 Awake/Start 里调一次即可）。传 null 表示退回调试视图。</summary>
        public void SetView(IDialogueView view)
        {
            _view = view;
        }

        /// <summary>开始一段对话（直接给文件）。成功返回 true。</summary>
        public bool StartDialogue(TextAsset asset)
        {
            if (asset == null)
            {
                Debug.LogError("[DialogueController] StartDialogue：对话文件为 null。");
                return false;
            }

            if (!DialogueData.TryParse(asset.text, out DialogueFile file, out string error))
            {
                Debug.LogError($"[DialogueController] 解析失败（{asset.name}）：{error}");
                return false;
            }

            BuildCharacterMap(file);
            Begin(file);
            Debug.Log($"[DialogueController] 开始对话：{asset.name}（{file.lines.Count} 句、{_characterMap.Count} 角色）。");
            return true;
        }

        /// <summary>按注册表 id 开始一段对话。查不到返回 false。</summary>
        public bool StartDialogue(string dialogueId)
        {
            if (string.IsNullOrEmpty(dialogueId) || !_dialogueMap.TryGetValue(dialogueId, out TextAsset asset))
            {
                Debug.LogError($"[DialogueController] 未注册的对话 id：{dialogueId}");
                return false;
            }

            return StartDialogue(asset);
        }

        /// <summary>前进：打字中 → 补全本行；否则 → 下一句；已到尾巴 → 收尾对话。</summary>
        public void Advance()
        {
            if (!IsActive)
            {
                return;
            }

            if (_isTyping)
            {
                CompleteTyping();
                return;
            }

            if (!_runner.Next())
            {
                EndDialogue();
                return;
            }

            ShowLine();
        }

        /// <summary>强制关闭当前对话（跳过 onEnd 事件）。</summary>
        public void CloseDialogue()
        {
            if (IsActive)
            {
                EndDialogue();
            }
        }

        // ============================== 输入 ==============================

        private void Update()
        {
            // 帧首消费延后的输入屏蔽恢复（见 EndDialogue）：在任何按键读取之前恢复，且本帧直接 return，
            // 不消费输入——避免结束对话的那次 Space/点击同帧泄漏给 ParticleEditor。
            if (_restoreInputFrame != 0 && Time.frameCount >= _restoreInputFrame)
            {
                _restoreInputFrame = 0;
                SetEditorSuppressed(false);
                return;
            }

            if (!IsActive)
            {
                if (_debugStartKey != KeyCode.None && Input.GetKeyDown(_debugStartKey))
                {
                    TextAsset debugAsset = _debugDialogue != null ? _debugDialogue : DialogueFile;
                    if (debugAsset != null)
                    {
                        StartDialogue(debugAsset);
                    }
                    else
                    {
                        Debug.LogWarning(
                            "[DialogueController] 调试热键被按下，但 Debug Dialogue / Dialogue File 都没赋值——" +
                            "请在 Inspector 里把对话 JSON（如 Assets/Data/Dialogues/example_dialogue.json）拖到其中一个字段。");
                    }
                }

                return;
            }

            bool nextPressed = (_nextKey != KeyCode.None && Input.GetKeyDown(_nextKey))
                || (_nextOnLeftClick && Input.GetMouseButtonDown(0));
            if (nextPressed)
            {
                Advance();
            }
        }

        // ============================== 流程 ==============================

        private void Begin(DialogueFile file)
        {
            // 播放中再次开始对话：先幂等收尾旧对话（恢复它造成的暂停 / 输入屏蔽记账），
            // 否则第二次 Begin 会把 _pausedByDialogue 覆盖成 false，导致新对话结束后永久不 Resume。
            if (IsActive)
            {
                EndDialogue(false);
            }

            IsActive = true;
            _restoreInputFrame = 0; // 作废可能遗留的延后恢复——本帧即将重新屏蔽输入
            _runner.Start(file);

            // 先建立"对话中"的外部状态（屏蔽编辑器输入 + 暂停模拟），再显示首句——
            // 若首句就让对话结束（空对话），ShowLine 里的 EndDialogue 会正确反向恢复。
            SetEditorSuppressed(true);
            PauseSimulation();

            ShowLine();
        }

        /// <summary>收尾当前对话。默认把输入屏蔽的恢复延后一帧（见 <see cref="Update"/> 帧首）。</summary>
        private void EndDialogue()
        {
            EndDialogue(true);
        }

        /// <param name="deferInputRestore">
        /// true = 输入屏蔽延后到下一帧帧首恢复：结束对话的那次按键与 ParticleEditor.Update 无执行顺序保证，
        /// 同帧立即恢复会让该次 Space/点击被编辑器吃掉（暂停模拟 / 画一笔）。代价是结束帧仍处于屏蔽，
        /// 玩家可能在这一帧画不出预期的一笔——按「屏蔽对话期间输入」的文档语义，这一帧过渡可接受。
        /// false = 立即恢复（OnDisable / 重新 Begin 用：禁用后 Update 不再运行，无法走延后路径）。
        /// </param>
        private void EndDialogue(bool deferInputRestore)
        {
            if (!IsActive)
            {
                return;
            }

            IsActive = false;
            _isTyping = false;
            if (_typing != null)
            {
                StopCoroutine(_typing);
                _typing = null;
            }

            IDialogueView view = View;
            if (view != null)
            {
                view.CloseView();
            }

            if (deferInputRestore)
            {
                _restoreInputFrame = Time.frameCount + 1;
            }
            else
            {
                _restoreInputFrame = 0;
                SetEditorSuppressed(false);
            }

            ResumeSimulation();
        }

        private void HandleRunnerEnd(string eventName)
        {
            Action<string> handler = OnDialogueEnd;
            if (handler != null)
            {
                handler(eventName);
            }
        }

        private void ShowLine()
        {
            DialogueLine line = _runner.CurrentLine;
            if (line == null)
            {
                EndDialogue();
                return;
            }

            IDialogueView view = View;
            if (view != null)
            {
                view.ShowLine(BuildLineView(line));
            }

            _fullLine = line.text ?? string.Empty;
            BeginTyping();
        }

        /// <summary>把状态机的一行 + 角色表解析成 UI 要的纯数据。</summary>
        private DialogueLineView BuildLineView(DialogueLine line)
        {
            string speakerId = line.speaker ?? string.Empty;
            bool isNarrator = string.IsNullOrEmpty(speakerId);
            if (isNarrator)
            {
                return new DialogueLineView(string.Empty, string.Empty, null, null, line.text ?? string.Empty, true);
            }

            _characterMap.TryGetValue(speakerId, out DialogueCharacter def);
            if (def == null && _warnedSpeakers.Add(speakerId))
            {
                Debug.LogWarning(
                    $"[DialogueController] 对话文件的 characters 段里没有 speaker：{speakerId}（用 id 当名字与资源名）。");
            }

            string name = def != null && !string.IsNullOrEmpty(def.displayName) ? def.displayName : speakerId;

            // 立绘与头像共用一个 id（= speaker），两个文件夹各查一次。
            Sprite portrait = LoadSprite(_portraitFolder, speakerId);
            if (portrait == null)
            {
                portrait = Placeholder(speakerId, "portrait", new Vector2Int(160, 240));
            }

            Sprite avatar = LoadSprite(_avatarFolder, speakerId);
            if (avatar == null)
            {
                avatar = Placeholder(speakerId, "avatar", new Vector2Int(64, 64));
            }

            return new DialogueLineView(speakerId, name, avatar, portrait, line.text ?? string.Empty, false);
        }

        // ============================== 打字机 ==============================

        private void BeginTyping()
        {
            if (_typing != null)
            {
                StopCoroutine(_typing);
                _typing = null;
            }

            IDialogueView view = View;
            if (view != null)
            {
                view.SetRevealedText(string.Empty);
            }

            if (string.IsNullOrEmpty(_fullLine))
            {
                CompleteTyping();
                return;
            }

            _isTyping = true;
            _typing = StartCoroutine(TypeRoutine());
        }

        private IEnumerator TypeRoutine()
        {
            float interval = _charsPerSecond > 0f ? 1f / _charsPerSecond : 0f;
            for (int i = 0; i < _fullLine.Length; i++)
            {
                IDialogueView view = View;
                if (view != null)
                {
                    view.SetRevealedText(_fullLine.Substring(0, i + 1));
                }

                if (interval > 0f)
                {
                    yield return new WaitForSeconds(interval);
                }
                else
                {
                    yield return null;
                }
            }

            _typing = null;
            _isTyping = false;
        }

        private void CompleteTyping()
        {
            if (_typing != null)
            {
                StopCoroutine(_typing);
                _typing = null;
            }

            _isTyping = false;
            IDialogueView view = View;
            if (view != null)
            {
                view.SetRevealedText(_fullLine);
            }
        }

        // ============================== 暂停 / 输入屏蔽 ==============================

        private void PauseSimulation()
        {
            if (_driver == null)
            {
                Debug.LogWarning("[DialogueController] 未接线 ParticleSimulationDriver，对话不会暂停模拟。");
                return;
            }

            _pausedByDialogue = !_driver.Paused;
            if (_pausedByDialogue)
            {
                _driver.Pause();
            }
        }

        private void ResumeSimulation()
        {
            if (_driver != null && _pausedByDialogue)
            {
                _driver.Resume();
            }

            _pausedByDialogue = false;
        }

        private void SetEditorSuppressed(bool suppressed)
        {
            if (_editor != null)
            {
                _editor.SuppressInput = suppressed;
            }
        }

        // ============================== 映射 / 占位图 ==============================

        /// <summary>用**当前对话文件**里的 characters 段重建角色表（完全由 JSON 驱动）。</summary>
        private void BuildCharacterMap(DialogueFile file)
        {
            _characterMap.Clear();
            if (file == null || file.characters == null)
            {
                return;
            }

            foreach (DialogueCharacter character in file.characters)
            {
                if (character != null && !string.IsNullOrEmpty(character.id))
                {
                    _characterMap[character.id] = character;
                }
            }
        }

        private void BuildMaps()
        {
            _dialogueMap.Clear();
            if (_dialogues != null)
            {
                foreach (DialogueRef reference in _dialogues)
                {
                    if (reference != null && !string.IsNullOrEmpty(reference.id) && reference.asset != null)
                    {
                        _dialogueMap[reference.id] = reference.asset;
                    }
                }
            }
        }

        /// <summary>
        /// 按 id 去指定文件夹（相对 <c>Assets/Resources</c>，不含扩展名）取 Sprite。
        /// 结果（含 null）缓存——同一个角色只查一次；取不到由调用方退回占位色块。
        /// </summary>
        private Sprite LoadSprite(string folder, string id)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(folder))
            {
                return null;
            }

            string key = folder + "/" + id;
            if (_spriteCache.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            Sprite sprite = Resources.Load<Sprite>(key);
            _spriteCache[key] = sprite;
            return sprite;
        }

        private Sprite Placeholder(string speakerId, string kind, Vector2Int size)
        {
            string key = speakerId + "/" + kind;
            if (_placeholderByKey.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            // 按 id 哈希取固定色相 → 同一个角色永远同一个占位色（确定性）。
            int hash = 0;
            for (int i = 0; i < speakerId.Length; i++)
            {
                hash = unchecked(hash * 31 + speakerId[i]);
            }

            float hue = Mathf.Abs(hash % 360) / 360f;
            Color color = Color.HSVToRGB(hue, 0.55f, 0.72f);

            var texture = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            var pixels = new Color32[size.x * size.y];
            Color32 fill = color;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill;
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            texture.hideFlags = HideFlags.DontSave;

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size.x, size.y), new Vector2(0.5f, 0.5f));
            sprite.hideFlags = HideFlags.DontSave;

            _placeholderByKey[key] = sprite;
            return sprite;
        }
    }
}
