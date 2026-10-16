using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TapTap2026.Game;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 关卡选择控制器（<see cref="LevelSelectController"/>）的公开契约：解锁集合 → 逐条显隐（含空集合 /
    /// 无交集旧档的兜底）、选关校验与写档（可见即可选）、悬停显示关卡简要描述，以及 <c>Awake</c> / <c>OnDestroy</c>
    /// 对按钮点击 / 悬停监听的挂与摘。
    /// </summary>
    /// <remarks>
    /// <para>现建现销，**不依赖场景**：按钮、悬停文本与控制器都在测试里搭出来，序列化字段经 <see cref="SerializedObject"/>
    /// 写（与 Inspector 同名同义）。EditMode 不跑 <c>Awake</c> / <c>OnDestroy</c>，因此显式反射调用，走真实初始化路径
    /// （同 <c>ParticleSelectionPanelTests</c> 的做法）。</para>
    /// <para>悬停描述用到的阶段 JSON 读真资产（<c>Assets/Data/Phases/</c>，与场景 / <c>GameController</c> 同一批），
    /// 期望值由测试自己解析同一份 JSON 得到——**不钉死描述文案**（文案属设计内容，会随设计变）。</para>
    /// <para>存档一律走专用探针文件名（<see cref="ProbeSaveFileName"/>）：<c>[SetUp]</c> 先删、<c>[TearDown]</c> 再删，
    /// 绝不读写真存档 <c>game_save.json</c>。</para>
    /// <para>EditMode 下 Unity 拒收延迟销毁（编辑模式里 <c>Object.Destroy</c> 只记一条平台错误、不真的销毁），
    /// 而控制器 <c>OnDestroy</c> 销毁自有悬停组件正走这条路——这段噪声与被测契约无关，只在对应的销毁窗口内局部屏蔽
    /// （见 <see cref="InvokeControllerTeardown"/> 与 <c>[TearDown]</c>），断言本身照常生效。</para>
    /// </remarks>
    public sealed class LevelSelectControllerTests
    {
        /// <summary>探针存档文件名（相对 <c>persistentDataPath</c>）——只在测试里出现，不碰真存档。</summary>
        private const string ProbeSaveFileName = "editmode_select_probe.json";

        /// <summary>界面登记的 5 条：索引即界面顺序（phase_01 = 入职日 … phase_05 = 沙盒）。</summary>
        private static readonly string[] PhaseIds =
        {
            "phase_01", "phase_02", "phase_03", "phase_04", "phase_05",
        };

        /// <summary>本次测试现建的全部对象（按钮 / 控制器），TearDown 里统一销毁。</summary>
        private readonly List<GameObject> _spawned = new List<GameObject>();

        /// <summary>界面上的 5 颗按钮，顺序与 <see cref="PhaseIds"/> 一致。</summary>
        private readonly List<Button> _buttons = new List<Button>();

        private LevelSelectController _controller;

        /// <summary>悬停描述的显示处：<see cref="MakeController"/> 现建并接给控制器的 <c>_probeText</c>。</summary>
        private TextMeshProUGUI _probeText;

        [SetUp]
        public void SetUp()
        {
            DeleteProbeSave();

            _controller = MakeController();
        }

        [TearDown]
        public void TearDown()
        {
            // 跑过 Awake 的控制器会在按钮上留下自己加的悬停组件：DestroyImmediate 触发它的 OnDestroy 时，
            // EditMode（非播放）下 Unity 会拒收那次延迟销毁并记一条平台错误。这条噪声与断言无关（Play 模式下
            // 不存在），只在销毁这几行的窗口内屏蔽。
            LogAssert.ignoreFailingMessages = true;
            try
            {
                for (int i = 0; i < _spawned.Count; i++)
                {
                    Object.DestroyImmediate(_spawned[i]);
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            _spawned.Clear();
            _buttons.Clear();
            DeleteProbeSave();
        }

        /// <summary>解锁集合里的关显示、其余隐藏。</summary>
        [Test]
        public void ApplyVisibility_ShowsOnlyUnlocked()
        {
            _controller.ApplyVisibility(new List<string> { "phase_01", "phase_03" });

            Assert.IsTrue(Visible(0), "解锁的 phase_01 应当可见");
            Assert.IsFalse(Visible(1), "未解锁的 phase_02 应当隐藏");
            Assert.IsTrue(Visible(2), "解锁的 phase_03 应当可见");
            Assert.IsFalse(Visible(3), "未解锁的 phase_04 应当隐藏");
            Assert.IsFalse(Visible(4), "未解锁的沙盒 phase_05 应当隐藏（沙盒与其它关卡同一条路径）");
        }

        /// <summary>解锁集合为空（无存档 / 空档）时兜底只显示第一关，界面不会空白卡死。</summary>
        [Test]
        public void ApplyVisibility_EmptySet_ShowsOnlyFirst()
        {
            _controller.ApplyVisibility(new List<string>());

            Assert.IsTrue(Visible(0), "空解锁集合兜底只显示第一关");
            for (int i = 1; i < PhaseIds.Length; i++)
            {
                Assert.IsFalse(Visible(i), "空解锁集合下第 " + i + " 条（" + PhaseIds[i] + "）应当隐藏");
            }

            // 同一个兜底也覆盖「没有存档」——CollectUnlockedPhaseIds(null) 返回的正是空表。
            // 先把第一关藏起来，看 null 这条路径是不是真的把它重新显示出来（而不是整段跳过）。
            _buttons[0].gameObject.SetActive(false);
            _controller.ApplyVisibility(null);

            Assert.IsTrue(Visible(0), "null 解锁集合同样兜底只显示第一关");
            Assert.IsFalse(Visible(1), "null 解锁集合下其余关卡保持隐藏");
        }

        /// <summary>非空解锁集合与本界面登记的 id 无一相交（别的版本的旧档）→ 整屏隐藏。</summary>
        [Test]
        public void ApplyVisibility_ForeignSet_HidesAll()
        {
            _controller.ApplyVisibility(new List<string> { "phase_99" });

            for (int i = 0; i < PhaseIds.Length; i++)
            {
                Assert.IsFalse(Visible(i), "无交集解锁集合下第 " + i + " 条（" + PhaseIds[i] + "）应当隐藏");
            }
        }

        /// <summary>已解锁的关可以选：写出的存档记下选中项，且它进了解锁集合。</summary>
        [Test]
        public void CommitSelection_Unlocked_WritesSave()
        {
            GameController.WriteSave(ProbeSaveFileName, "phase_02", new List<string> { "phase_02", "phase_05" });

            Assert.IsTrue(_controller.CommitSelection("phase_05"), "已解锁的 phase_05 应当可选");

            GameController.SaveData save = GameController.LoadSave(ProbeSaveFileName);
            Assert.IsNotNull(save, "选中后应当写出存档");
            Assert.AreEqual("phase_05", save.currentPhaseId, "选中的关应当写进 currentPhaseId");
            CollectionAssert.AreEqual(
                new List<string> { "phase_02", "phase_05" }, save.unlockedPhases,
                "解锁集合应当按序号排序写出，并含选中的关");
        }

        /// <summary>未解锁的关选了也不动存档：拒绝选择，原进度原样保留。</summary>
        [Test]
        public void CommitSelection_Locked_RefusesAndKeepsSave()
        {
            GameController.WriteSave(ProbeSaveFileName, "phase_02", new List<string> { "phase_02" });

            Assert.IsFalse(_controller.CommitSelection("phase_05"), "未解锁的 phase_05 不可选");

            GameController.SaveData save = GameController.LoadSave(ProbeSaveFileName);
            Assert.IsNotNull(save, "拒绝选择不该把存档删掉");
            Assert.AreEqual("phase_02", save.currentPhaseId, "拒绝选择后 currentPhaseId 不该被改写");
            Assert.IsNotNull(save.unlockedPhases, "拒绝选择后解锁集合不该被改写");
            CollectionAssert.DoesNotContain(save.unlockedPhases, "phase_05", "被拒的关不该混进解锁集合");
        }

        /// <summary>空阶段 id（null / 空串）直接拒绝，且不写出任何存档。</summary>
        [Test]
        public void CommitSelection_EmptyId_Refuses()
        {
            // 两种空 id 各报一条错误（本类只此一处置信错误，故用 LogAssert.Expect 逐条对齐）。
            LogAssert.Expect(LogType.Error, new Regex("选择的阶段 id 为空"));
            Assert.IsFalse(_controller.CommitSelection(null), "null 阶段 id 不可选");

            LogAssert.Expect(LogType.Error, new Regex("选择的阶段 id 为空"));
            Assert.IsFalse(_controller.CommitSelection(string.Empty), "空串阶段 id 不可选");

            Assert.IsNull(GameController.LoadSave(ProbeSaveFileName), "无效选择不该写出存档");
        }

        /// <summary>无存档时第一关就是入口：写档成功（可见即可选，界面显示什么就能选什么）。</summary>
        [Test]
        public void CommitSelection_NoSave_AllowsFirstLevel()
        {
            Assert.IsFalse(File.Exists(ProbeSavePath), "前置：本测试开始前没有存档");

            Assert.IsTrue(_controller.CommitSelection("phase_01"), "无存档时 phase_01 是唯一入口，可见即可选");

            GameController.SaveData save = GameController.LoadSave(ProbeSaveFileName);
            Assert.IsNotNull(save, "兜底选中第一关后应当写出存档");
            Assert.AreEqual("phase_01", save.currentPhaseId, "兜底选中的第一关应当写进 currentPhaseId");
            CollectionAssert.AreEqual(
                new List<string> { "phase_01" }, save.unlockedPhases,
                "解锁集合应当补上兜底选中的第一关");
        }

        /// <summary>Awake 给每颗按钮挂上且只挂一个运行期点击监听（场景里不留持久监听）；OnDestroy 把它们摘干净。
        /// 本测试不接 <c>_phases</c>，故先对齐 Awake 里那条「未接线」的应当发生的错误。</summary>
        [Test]
        public void Awake_BindsClicksAndOnDestroy_Unbinds()
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                Assert.AreEqual(0, RuntimeListenerCount(_buttons[i].onClick),
                    "前置：EditMode 不跑 Awake，第 " + i + " 颗按钮此时不该有运行期监听");
            }

            AwakeController(wirePhases: false);

            for (int i = 0; i < _buttons.Count; i++)
            {
                Assert.AreEqual(1, RuntimeListenerCount(_buttons[i].onClick),
                    "Awake 后第 " + i + " 颗按钮应当恰好挂 1 个运行期监听");
                Assert.AreEqual(0, _buttons[i].onClick.GetPersistentEventCount(),
                    "点击由 Awake 运行期绑定，第 " + i + " 颗按钮不该有持久监听");
            }

            InvokeControllerTeardown();

            for (int i = 0; i < _buttons.Count; i++)
            {
                Assert.AreEqual(0, RuntimeListenerCount(_buttons[i].onClick),
                    "OnDestroy 后第 " + i + " 颗按钮不该留下运行期监听");
            }
        }

        /// <summary>悬停到已登记的关卡上：ProbeText 从隐藏变为显示该阶段 JSON 里那份 description。</summary>
        [Test]
        public void ShowDescription_KnownPhase_SetsProbeTextAndShows()
        {
            AwakeController(wirePhases: true);

            Assert.IsFalse(_probeText.gameObject.activeSelf,
                "初始应当隐藏（Awake 末尾 HideDescription，等指针悬停才显示）");
            Assert.AreEqual(string.Empty, _probeText.text, "初始不该有文本");

            string description = PhaseDescription("phase_01");
            Assert.IsFalse(string.IsNullOrEmpty(description),
                "前置：phase_01 的 JSON 里应当写了 description——本测试断言的就是它被显示出来");

            _controller.ShowDescription("phase_01");

            Assert.IsTrue(_probeText.gameObject.activeSelf, "悬停到 phase_01 上应当显示描述");
            Assert.AreEqual(description, _probeText.text, "显示的应当正是 phase_01 自己那份 description");

            // 换一关悬停：显示的应当跟着换（不是只会显示第一关那一份）。
            _controller.ShowDescription("phase_03");
            Assert.AreEqual(PhaseDescription("phase_03"), _probeText.text,
                "悬停换到 phase_03 后应当显示 phase_03 那份 description");
        }

        /// <summary>隐藏描述：同时把文本清空，不留上一关的残字。</summary>
        [Test]
        public void HideDescription_ClearsAndHides()
        {
            AwakeController(wirePhases: true);

            _controller.ShowDescription("phase_01");
            Assert.IsTrue(_probeText.gameObject.activeSelf, "前置：phase_01 的描述已显示");

            _controller.HideDescription();

            Assert.IsFalse(_probeText.gameObject.activeSelf, "隐藏后描述不该还看得见");
            Assert.AreEqual(string.Empty, _probeText.text, "隐藏时应当把文本一并清空");
        }

        /// <summary>本界面没登记 / 空阶段的 id：一律不显示（含把已经显示的那份收回去）。</summary>
        [Test]
        public void ShowDescription_UnknownPhase_Hides()
        {
            AwakeController(wirePhases: true);

            var unknownIds = new string[] { "phase_99", null, string.Empty };
            for (int i = 0; i < unknownIds.Length; i++)
            {
                _controller.ShowDescription("phase_01");
                Assert.IsTrue(_probeText.gameObject.activeSelf, "前置：phase_01 的描述已显示");

                _controller.ShowDescription(unknownIds[i]);

                Assert.IsFalse(_probeText.gameObject.activeSelf,
                    "未知 / 空阶段 id（第 " + i + " 条：" + (unknownIds[i] ?? "null") + "）不该显示描述");
                Assert.AreEqual(string.Empty, _probeText.text, "没有可显示的描述时文本应当为空");
            }
        }

        /// <summary>显隐刷新会撤下悬停描述：按钮被收走时 PointerExit 不会再来，不能留下对不上任何可见按钮的文本。</summary>
        [Test]
        public void ApplyVisibility_HidesAnyShownDescription()
        {
            AwakeController(wirePhases: true);

            _controller.ShowDescription("phase_01");
            Assert.IsTrue(_probeText.gameObject.activeSelf, "前置：phase_01 的描述已显示");

            _controller.ApplyVisibility(new List<string> { "phase_01" });

            Assert.IsFalse(_probeText.gameObject.activeSelf, "显隐刷新后应当撤下悬停描述");
            Assert.AreEqual(string.Empty, _probeText.text, "撤下描述时同时清空文本");
        }

        /// <summary>Awake 给每颗按钮挂上悬停监听：PointerEnter 显示本关描述、PointerExit 隐藏，各恰好 1 条；
        /// 重复 Awake 不叠加。</summary>
        [Test]
        public void Awake_BindsHoverToButtons()
        {
            AwakeController(wirePhases: true);

            AssertHoverEntriesWired();

            Invoke(_controller, "Awake");

            AssertHoverEntriesWired();
            Assert.AreEqual(_buttons.Count, OwnedHoverTriggers().Count,
                "重复 Awake 不该重复记自有悬停组件");
        }

        /// <summary>OnDestroy 只收拾自己加的东西：按钮上原有的 <see cref="EventTrigger"/> 原样留在场景里，
        /// 控制器只是放下这些引用（EditMode 下 Unity 不执行延迟销毁，故不断言组件真的没了）。</summary>
        [Test]
        public void OnDestroy_DestroysOnlyOwnHoverTriggers()
        {
            // 第 0 颗按钮预先带一个「场景自带」的 EventTrigger：控制器只往它上面补悬停条目，绝不该据为己有。
            EventTrigger sceneOwned = _buttons[0].gameObject.AddComponent<EventTrigger>();

            AwakeController(wirePhases: true);

            Assert.AreSame(sceneOwned, _buttons[0].GetComponent<EventTrigger>(),
                "按钮上已有 EventTrigger 时应当复用它，而不是再挂一个");
            Assert.AreEqual(1, HoverEntryCount(sceneOwned, EventTriggerType.PointerEnter),
                "场景自带的 EventTrigger 上也应当补上悬停监听（复用不等于跳过）");

            List<EventTrigger> owned = OwnedHoverTriggers();
            Assert.AreEqual(_buttons.Count - 1, owned.Count,
                "自有名单只该记控制器新建的那些，场景自带的那颗不在此列");
            CollectionAssert.DoesNotContain(owned, sceneOwned, "场景自带的 EventTrigger 不该进自有名单");
            for (int i = 1; i < _buttons.Count; i++)
            {
                CollectionAssert.Contains(owned, _buttons[i].GetComponent<EventTrigger>(),
                    "第 " + i + " 颗按钮上的悬停组件是控制器新建的，应当记进自有名单");
            }

            InvokeControllerTeardown();

            Assert.AreSame(sceneOwned, _buttons[0].GetComponent<EventTrigger>(),
                "OnDestroy 不该碰场景自带的 EventTrigger");
            Assert.AreEqual(1, HoverEntryCount(sceneOwned, EventTriggerType.PointerEnter),
                "场景自带的 EventTrigger 上的悬停条目也不该被拆");
            Assert.AreEqual(1, HoverEntryCount(sceneOwned, EventTriggerType.PointerExit),
                "场景自带的 EventTrigger 上的悬停条目也不该被拆");
            Assert.AreEqual(0, OwnedHoverTriggers().Count, "OnDestroy 后控制器不该再持有任何悬停组件");
            for (int i = 0; i < _buttons.Count; i++)
            {
                Assert.AreEqual(0, RuntimeListenerCount(_buttons[i].onClick),
                    "OnDestroy 后第 " + i + " 颗按钮不该留下运行期监听");
            }
        }

        // ============================== 小工具 ==============================

        /// <summary>探针存档的完整路径（与生产同一处来源）。</summary>
        private static string ProbeSavePath => GameController.SavePathFor(ProbeSaveFileName);

        /// <summary>第 <paramref name="index"/> 条按钮的显隐（<c>activeSelf</c>：只看自己，不看父级）。</summary>
        private bool Visible(int index)
        {
            return _buttons[index].gameObject.activeSelf;
        }

        /// <summary>
        /// 走真实初始化路径：先把 <c>_phases</c> 接好，或先对齐「未接线 <c>_phases</c>」那条**应当发生**的错误，
        /// 再显式调 <c>Awake</c>（EditMode 不跑生命周期回调）。
        /// </summary>
        /// <param name="wirePhases">true = 接真阶段资产（无错误）；false = 留空并对齐那条错误。</param>
        private void AwakeController(bool wirePhases)
        {
            if (wirePhases)
            {
                WirePhases();
            }
            else
            {
                LogAssert.Expect(LogType.Error, new Regex("未接线 _phases"));
            }

            Invoke(_controller, "Awake");
        }

        /// <summary>把 5 个阶段 JSON 按界面顺序写进 <c>_phases</c>（须早于 <c>Awake</c>——描述表在 Awake 里建）。</summary>
        private void WirePhases()
        {
            var serialized = new SerializedObject(_controller);
            SerializedProperty array = serialized.FindProperty("_phases");
            array.arraySize = PhaseIds.Length;
            for (int i = 0; i < PhaseIds.Length; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = PhaseAsset(PhaseIds[i]);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>阶段 JSON 资产（<c>Assets/Data/Phases/&lt;id&gt;.json</c>，与场景 / <c>GameController</c> 同一批）。</summary>
        private static TextAsset PhaseAsset(string phaseId)
        {
            string path = "Assets/Data/Phases/" + phaseId + ".json";
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            Assert.IsNotNull(asset, "阶段资产缺失：" + path);
            return asset;
        }

        /// <summary>
        /// 直接解析阶段 JSON 得到的 description：期望值从数据来，**不钉死文案**（文案属设计内容，会随设计变），
        /// 走的是生产同一个解析器。
        /// </summary>
        private static string PhaseDescription(string phaseId)
        {
            Assert.IsTrue(GamePhaseParser.TryParse(PhaseAsset(phaseId).text, out GamePhaseData phase, out string error),
                "阶段 JSON 应当能解析：" + error);
            return phase.description;
        }

        /// <summary>控制器当前记着的自有悬停组件（私有 <c>_hoverTriggers</c> 的实时列表）。</summary>
        private List<EventTrigger> OwnedHoverTriggers()
        {
            return (List<EventTrigger>)typeof(LevelSelectController)
                .GetField("_hoverTriggers", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_controller);
        }

        /// <summary>按钮的悬停 <see cref="EventTrigger"/> 里指定类型的条目数。</summary>
        private static int HoverEntryCount(EventTrigger trigger, EventTriggerType eventId)
        {
            int count = 0;
            foreach (EventTrigger.Entry entry in trigger.triggers)
            {
                if (entry.eventID == eventId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>断言每颗按钮都挂上了悬停监听：一个 <see cref="EventTrigger"/>，PointerEnter / PointerExit 各恰好 1 条。</summary>
        private void AssertHoverEntriesWired()
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                EventTrigger trigger = _buttons[i].GetComponent<EventTrigger>();
                Assert.IsNotNull(trigger, "Awake 应当给第 " + i + " 颗按钮挂上悬停 EventTrigger");
                Assert.AreEqual(1, HoverEntryCount(trigger, EventTriggerType.PointerEnter),
                    "第 " + i + " 颗按钮应当恰好 1 条 PointerEnter（悬停显示描述），不该重复叠加");
                Assert.AreEqual(1, HoverEntryCount(trigger, EventTriggerType.PointerExit),
                    "第 " + i + " 颗按钮应当恰好 1 条 PointerExit（移开隐藏描述），不该重复叠加");
            }
        }

        /// <summary>
        /// 显式调控制器 <c>OnDestroy</c>（EditMode 不跑生命周期回调）。控制器销毁自己加的悬停组件用的是 Unity 的
        /// 延迟销毁 <c>Object.Destroy</c>，而编辑模式（非播放）下 Unity 会拒收它并记一条平台错误：这条噪声既不是
        /// 被测契约（Play 模式下走的就是这条销毁路径，只是编辑模式不执行），也不随版本稳定，故只在这一次调用的
        /// 窗口内屏蔽。被测行为（引用是否清空、别人的组件有没有被动过）由调用方随后的断言把关。
        /// </summary>
        private void InvokeControllerTeardown()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Invoke(_controller, "OnDestroy");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        /// <summary>现建 5 颗按钮 + 控制器 + 悬停文本，并把 <c>_levels</c> / <c>_saveFileName</c> / <c>_probeText</c>
        /// 写进序列化字段（与 Inspector 同名同义；<c>_gameSceneName</c> 用默认值，本测试不加载场景）。
        /// <c>_phases</c> 留空：需要它的测试自己用 <see cref="WirePhases"/> 在 <c>Awake</c> 之前接。</summary>
        private LevelSelectController MakeController()
        {
            LevelSelectController controller =
                Spawn("LevelSelectTests.Controller").AddComponent<LevelSelectController>();
            _probeText = Spawn("LevelSelectTests.Probe").AddComponent<TextMeshProUGUI>();

            var serialized = new SerializedObject(controller);
            serialized.FindProperty("_saveFileName").stringValue = ProbeSaveFileName;
            serialized.FindProperty("_probeText").objectReferenceValue = _probeText;

            SerializedProperty array = serialized.FindProperty("_levels");
            array.arraySize = PhaseIds.Length;
            for (int i = 0; i < PhaseIds.Length; i++)
            {
                // 名字用 1 起，与 phase_01 对齐。
                Button button = Spawn("Level" + (i + 1)).AddComponent<Button>();
                _buttons.Add(button);

                SerializedProperty element = array.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("button").objectReferenceValue = button;
                element.FindPropertyRelative("phaseId").stringValue = PhaseIds[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            return controller;
        }

        private GameObject Spawn(string name)
        {
            var gameObject = new GameObject(name);
            _spawned.Add(gameObject);
            return gameObject;
        }

        /// <summary>删掉探针存档（不存在也照删）——真存档不在本类的涉猎范围。</summary>
        private static void DeleteProbeSave()
        {
            File.Delete(ProbeSavePath);
        }

        /// <summary>
        /// 按钮上当前挂着的**运行期**监听数：<c>UnityEventBase.m_Calls</c>（<c>InvokableCallList</c>）的
        /// <c>Count</c> 是「持久监听 + 运行期监听」之和，减去公开的持久监听数即运行期监听数。
        /// 运行期监听没有公开的计数入口，只能这样读（本测试的按钮都是现建的，持久数另有断言恒为 0）。
        /// </summary>
        private static int RuntimeListenerCount(UnityEvent unityEvent)
        {
            object calls = typeof(UnityEventBase)
                .GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(unityEvent);
            int total = (int)calls.GetType().GetProperty("Count").GetValue(calls);
            return total - unityEvent.GetPersistentEventCount();
        }

        /// <summary>EditMode 不跑生命周期回调：显式反射调一次，走真实初始化 / 清理路径。</summary>
        private static void Invoke(object target, string methodName)
        {
            target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, null);
        }
    }
}
