using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEngine;
using UnityEngine.TestTools;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 投放选择的公开契约（<see cref="ParticleSelectionEntry"/> + <see cref="ParticleEditor"/> 的选择 API）：
    /// 列表构成（先粒子后动物、白名单过滤）、按名字 / typeId / 下标选择、越界与陈旧实例的拒绝、
    /// 右键循环、<see cref="ParticleEditor.SelectionChanged"/> 的触发时机、阶段投放名单重建列表的语义，
    /// 以及 ProbeText 的显示口径（<see cref="ParticleEditor.ComposeProbeLine"/> 的三段优先级与
    /// <see cref="ParticleEditor.DescribeHighSpeedRunning"/> 的固定格式）。
    /// </summary>
    /// <remarks>
    /// 只读选择状态、只调选择 API——不碰需要世界或场景的入口（<c>Paint</c> / <c>SpawnCreature</c> /
    /// <c>PushHud</c> 那几条），因此 <c>Awake</c> 里的 <c>FindObjectOfType</c> 兜底捞到当前打开场景里的组件也无妨。
    /// </remarks>
    public sealed class ParticleSelectionTests
    {
        private GameObject _object;
        private ParticleEditor _editor;

        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
            _object = new GameObject("ParticleSelectionTests.Editor");
            _editor = _object.AddComponent<ParticleEditor>();

            // EditMode 不跑 Awake（同 SoundControllerTests 对 ParticleSimulationDriver 的做法）：显式调一次，走真实初始化路径。
            typeof(ParticleEditor).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_editor, null);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_object);
        }

        /// <summary>列表构成：先按注册顺序排可投粒子，末尾接 3 个动物物种；<c>Index</c> 就是列表下标；初始选中石头。</summary>
        [Test]
        public void Selections_ListParticlesThenCreatures()
        {
            IReadOnlyList<ParticleSelectionEntry> selections = _editor.Selections;

            Assert.Greater(selections.Count, 3, "至少 1 个粒子项 + 3 个动物项");

            int particles = selections.Count - 3;
            for (int i = 0; i < particles; i++)
            {
                Assert.IsFalse(selections[i].IsCreature, "粒子段里没有动物项");
            }

            string[] creatureNames = { "兔子", "鱼", "鸟" };
            for (int i = 0; i < 3; i++)
            {
                ParticleSelectionEntry entry = selections[particles + i];
                Assert.IsTrue(entry.IsCreature, "动物段排在所有粒子之后");
                Assert.AreEqual(i, entry.CreatureIndex, "动物段按工厂表顺序");
                Assert.AreEqual(creatureNames[i], entry.DisplayName, "显示名取自物种原型");
            }

            for (int i = 0; i < selections.Count; i++)
            {
                Assert.AreEqual(i, selections[i].Index, "每项的 Index 就是它在 Selections 里的下标");
            }

            Assert.AreSame(selections[0], _editor.CurrentSelection, "初始选中第一项");
            Assert.AreEqual(ParticleIds.Stone, _editor.CurrentSelection.TypeId, "第一项是石头（注册表里第一个可投类型）");
        }

        /// <summary>不可手投与不在 Gas 白名单里的类型不进列表；火是 Gas 白名单里的唯一例外。</summary>
        [Test]
        public void Selections_ExcludeUnplaceableAndNonWhitelistedGases()
        {
            ushort[] unplaceable =
            {
                ParticleIds.Grass,
                ParticleIds.WetSoil,
                ParticleIds.Biomass,
                ParticleIds.AquaticPlant,
                ParticleIds.TreeTrunk,
                ParticleIds.TreeLeaf,
                ParticleIds.Flower,
                ParticleIds.Humus,
                ParticleIds.HumusSoil,
                ParticleIds.WetHumusSoil,
            };

            for (int i = 0; i < unplaceable.Length; i++)
            {
                Assert.IsFalse(HasTypeId(unplaceable[i]), "不可手投的类型不进列表");
            }

            Assert.IsFalse(HasTypeId(ParticleIds.Atmosphere), "大气不在（Gas 未列入白名单）");
            Assert.IsFalse(HasTypeId(ParticleIds.Lightning), "雷电不在（只能由随机刻起爆）");
            Assert.IsTrue(HasTypeId(ParticleIds.Fire), "火在（Gas 白名单唯一例外）");
            Assert.IsTrue(HasTypeId(ParticleIds.GrassSeed), "草种子在（单颗投放）");
        }

        /// <summary>按名字选择：先比显示名，再比类名（动物取物种类名）；匹配不中 / null / 空串返回 false 且不改变选择。</summary>
        [Test]
        public void SelectByName_MatchesDisplayNameAndClassName()
        {
            Assert.IsTrue(_editor.SelectByName("WaterParticle"), "按显示名匹配粒子项");
            Assert.AreEqual(ParticleIds.Water, _editor.CurrentSelection.TypeId);
            Assert.IsFalse(_editor.CurrentSelection.IsCreature);

            Assert.IsTrue(_editor.SelectByName("兔子"), "按物种调试名匹配动物项");
            Assert.IsTrue(_editor.CurrentSelection.IsCreature);
            Assert.AreEqual(0, _editor.CurrentSelection.CreatureIndex);

            Assert.IsTrue(_editor.SelectByName("FishCreature"), "按类名匹配动物项");
            Assert.IsTrue(_editor.CurrentSelection.IsCreature);
            Assert.AreEqual(1, _editor.CurrentSelection.CreatureIndex);

            ParticleSelectionEntry before = _editor.CurrentSelection;
            Assert.IsFalse(_editor.SelectByName("Nope"), "查不到的名字");
            Assert.IsFalse(_editor.SelectByName(null), "null");
            Assert.IsFalse(_editor.SelectByName(""), "空串");
            Assert.AreSame(before, _editor.CurrentSelection, "匹配不中不改变选择");
        }

        /// <summary>按 typeId 选择：只认可投粒子项；不可投类型与未知 id 返回 false 且不改变选择。</summary>
        [Test]
        public void SelectByTypeId_RejectsUnplaceableAndUnknown()
        {
            Assert.IsTrue(_editor.SelectByTypeId(ParticleIds.Sand));
            Assert.AreEqual(ParticleIds.Sand, _editor.CurrentSelection.TypeId);

            ParticleSelectionEntry before = _editor.CurrentSelection;
            Assert.IsFalse(_editor.SelectByTypeId(ParticleIds.Grass), "草不可手投，选不中");
            Assert.AreSame(before, _editor.CurrentSelection, "选不中不改变选择");
            Assert.IsFalse(_editor.SelectByTypeId(ParticleIds.Air), "空气（0）不可选");
            Assert.IsFalse(_editor.SelectByTypeId(9999), "未知 typeId 不可选");
            Assert.AreSame(before, _editor.CurrentSelection);
        }

        /// <summary>按下标选择：越界（含负数）与 null 条目返回 false 且不改变选择；末项是动物。</summary>
        [Test]
        public void Select_OutOfRangeIsIgnored()
        {
            ParticleSelectionEntry before = _editor.CurrentSelection;

            Assert.IsFalse(_editor.Select(-1), "负下标");
            Assert.IsFalse(_editor.Select(_editor.Selections.Count), "刚好越界");
            Assert.IsFalse(_editor.Select((ParticleSelectionEntry)null), "null 条目");
            Assert.AreSame(before, _editor.CurrentSelection, "越界 / null 都不改变选择");

            Assert.IsTrue(_editor.Select(_editor.Selections.Count - 1));
            Assert.IsTrue(_editor.CurrentSelection.IsCreature, "末项是动物物种");
        }

        /// <summary>陈旧实例（重建前拿到的条目）不能用来选择：实例不同即拒绝，选择不变。</summary>
        [Test]
        public void SelectWithStaleEntry_IsRejected()
        {
            ParticleSelectionEntry stale = _editor.Selections[1];
            Assert.IsTrue(_editor.Select(stale), "当前列表里的实例可选");

            _editor.SetPlacementFilter(new[] { "SandParticle", "WaterParticle" });
            ParticleSelectionEntry current = _editor.CurrentSelection;

            Assert.IsFalse(_editor.Select(stale), "列表重建后，旧条目不再被认");
            Assert.AreSame(current, _editor.CurrentSelection, "被拒时不改变选择");
            Assert.AreEqual(ParticleIds.Sand, stale.TypeId, "陈旧条目自己仍带着旧数据");
        }

        /// <summary>右键循环：按列表顺序推进（动物段接在粒子之后），到末尾回到开头。</summary>
        [Test]
        public void CycleType_AdvancesAndWrapsAround()
        {
            Assert.AreEqual(0, _editor.CurrentSelection.Index, "从第一项起");

            for (int i = 1; i < _editor.Selections.Count; i++)
            {
                _editor.CycleType();
                Assert.AreEqual(i, _editor.CurrentSelection.Index, "按序推进（动物段接在粒子之后）");
            }

            _editor.CycleType();
            Assert.AreEqual(0, _editor.CurrentSelection.Index, "到末尾再按一次回到开头");
        }

        /// <summary>选中项实例真的变了才发 SelectionChanged；载荷是新的当前选中项。</summary>
        [Test]
        public void SelectionChanged_FiresOnlyOnActualChange()
        {
            int fired = 0;
            ParticleSelectionEntry payload = null;
            _editor.SelectionChanged += entry =>
            {
                fired++;
                payload = entry;
            };

            Assert.IsTrue(_editor.Select(_editor.CurrentSelection.Index), "重选当前项仍算成功");
            Assert.AreEqual(0, fired, "选中项实例没变 ⇒ 不触发");

            Assert.IsTrue(_editor.SelectByName("SandParticle"));
            Assert.AreEqual(1, fired, "换项触发一次");
            Assert.AreSame(_editor.CurrentSelection, payload, "载荷就是新的选中项");

            Assert.IsTrue(_editor.SelectByName("SandParticle"));
            Assert.AreEqual(1, fired, "再选同一项不再触发");
        }

        /// <summary>单颗投放标记：单颗白名单里的粒子类型与全部动物为 true，普通粒子为 false。</summary>
        [Test]
        public void SinglePlacementFlag_MarksSinglePlacementTypesAndCreatures()
        {
            Assert.IsTrue(_editor.SelectByName("GrassSeedParticle"));
            Assert.IsTrue(_editor.CurrentSelection.SinglePlacement, "草种子在单颗投放白名单里");

            Assert.IsTrue(_editor.SelectByName("TreeSeedParticle"));
            Assert.IsTrue(_editor.CurrentSelection.SinglePlacement, "树木种子在单颗投放白名单里");

            Assert.IsTrue(_editor.SelectByName("SandParticle"));
            Assert.IsFalse(_editor.CurrentSelection.SinglePlacement, "沙按半径投放");

            IReadOnlyList<ParticleSelectionEntry> selections = _editor.Selections;
            for (int i = 0; i < selections.Count; i++)
            {
                if (selections[i].IsCreature)
                {
                    Assert.IsTrue(selections[i].SinglePlacement, "动物恒为单只 + 冷却投放");
                }
            }
        }

        /// <summary>阶段投放名单只重建粒子段（动物段原样留在后面），并自动选中新的第一项；清除限制恢复默认。</summary>
        [Test]
        public void SetPlacementFilter_RebuildsParticlesAndKeepsCreatures()
        {
            int original = _editor.Selections.Count;

            _editor.SetPlacementFilter(new[] { "SandParticle", "WaterParticle" });

            Assert.AreEqual(5, _editor.Selections.Count, "2 个粒子项 + 3 个动物项");
            Assert.AreEqual(ParticleIds.Sand, _editor.Selections[0].TypeId, "粒子段按注册顺序");
            Assert.AreEqual(ParticleIds.Water, _editor.Selections[1].TypeId);

            for (int i = 2; i < _editor.Selections.Count; i++)
            {
                Assert.IsTrue(_editor.Selections[i].IsCreature, "动物段原样接在粒子之后");
            }

            Assert.AreEqual(ParticleIds.Sand, _editor.CurrentSelection.TypeId, "重算后自动选中第一个可投项");

            _editor.SetPlacementFilter(null);

            Assert.AreEqual(original, _editor.Selections.Count, "清除限制恢复默认白名单");
            Assert.AreEqual(ParticleIds.Stone, _editor.CurrentSelection.TypeId, "当前选回石头");
        }

        /// <summary>交集为空的那次调用被整体忽略：条目连实例都不换，当前选择也不动。</summary>
        [Test]
        public void SetPlacementFilter_EmptyIntersectionKeepsCurrentSelections()
        {
            var before = new ParticleSelectionEntry[_editor.Selections.Count];
            for (int i = 0; i < before.Length; i++)
            {
                before[i] = _editor.Selections[i];
            }

            ParticleSelectionEntry current = _editor.CurrentSelection;

            LogAssert.Expect(LogType.Error, new Regex("交集为空"));
            _editor.SetPlacementFilter(new[] { "GrassParticle" });

            Assert.AreEqual(before.Length, _editor.Selections.Count, "被忽略的调用不重建列表");
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreSame(before[i], _editor.Selections[i], "条目保持同一批实例");
            }

            Assert.AreSame(current, _editor.CurrentSelection, "当前选择不变");
        }

        /// <summary>清除模式的开关：翻转 / 显式设置，等值不动；与投放选择互不干涉（换项不改它）。</summary>
        [Test]
        public void EraseMode_Toggles()
        {
            Assert.IsFalse(_editor.EraseMode, "初值关");

            _editor.ToggleEraseMode();
            Assert.IsTrue(_editor.EraseMode, "翻转即开");

            _editor.SetEraseMode(true);
            Assert.IsTrue(_editor.EraseMode, "等值设置保持为真");

            _editor.SetEraseMode(false);
            Assert.IsFalse(_editor.EraseMode, "显式关掉");

            _editor.SetEraseMode(true);
            _editor.SelectByName("SandParticle");
            Assert.IsTrue(_editor.EraseMode, "换投放项不影响清除模式（两条独立的输入）");
        }

        /// <summary>编辑锁判据：对话激活（<see cref="ParticleEditor.SuppressInput"/>）即锁——
        /// 投放面板据此把整排按钮变灰（与光照面板同一条判据）。</summary>
        [Test]
        public void EditingLocked_FollowsSuppressInput()
        {
            _editor.SuppressInput = false;
            Assert.IsFalse(_editor.EditingLocked, "没对话、也没有高速会话时编辑不锁");

            _editor.SuppressInput = true;
            Assert.IsTrue(_editor.EditingLocked, "对话激活即锁编辑");
        }

        /// <summary>
        /// 每个粒子项都带**中文显示名**与**语言无关的类名**：界面上显示前者，白名单 / 阶段 JSON / 场景接线用后者，
        /// 两条路径都能选中同一项（<see cref="ParticleEditor.SelectByName"/> 的规则）。
        /// </summary>
        [Test]
        public void Selections_CarryChineseDisplayNameAndClassName()
        {
            AssertParticle(ParticleIds.Stone, "石头");
            AssertParticle(ParticleIds.Sand, "沙");
            AssertParticle(ParticleIds.Soil, "土");
            AssertParticle(ParticleIds.Water, "水");
            AssertParticle(ParticleIds.Fire, "火");
            AssertParticle(ParticleIds.GrassSeed, "草种子");
            AssertParticle(ParticleIds.TreeSeed, "树木种子");

            // 全部可投粒子项都得有中文名（别的类型也一样，这里只把「有中文名」这条契约铺全）。
            IReadOnlyList<ParticleSelectionEntry> selections = _editor.Selections;
            for (int i = 0; i < selections.Count; i++)
            {
                ParticleSelectionEntry entry = selections[i];
                Assert.IsFalse(string.IsNullOrEmpty(entry.DisplayName), entry.ClassName + " 缺显示名");
                Assert.IsFalse(string.IsNullOrEmpty(entry.ClassName), entry.DisplayName + " 缺类名");
                Assert.IsTrue(ContainsChinese(entry.DisplayName), entry.DisplayName + " 的显示名应当是中文");
                Assert.IsTrue(entry.ClassName.EndsWith("Particle") || entry.IsCreature, entry.ClassName + " 应当是类名");
            }
        }

        /// <summary>光标格读数是中文的：粒子中文名 + 温度 / 热量 / 湿度三个中文标签；空气格显示「空气」。
        /// 读数取自**帧里的探针快照**（<see cref="WorldFrame.Probe"/>），测试因此按生产路径采一帧再读。</summary>
        [Test]
        public void DescribeCell_UsesChineseNameAndLabels()
        {
            var world = new ParticleWorld(8, 8, 20261015u);
            world.Set(4, 4, ParticleIds.Water);

            var frames = new FramePump();
            SimulationFrame frame = frames.PublishProbe(world, 4, 4);

            string water = ParticleEditor.DescribeCell(frame.World.Probe);
            StringAssert.Contains("水", water, "显示该格粒子的中文名");
            StringAssert.Contains("温度=", water);
            StringAssert.Contains("热量=", water);
            StringAssert.Contains("湿度=", water);

            string air = ParticleEditor.DescribeCell(frames.PublishProbe(world, 0, 0).World.Probe);
            StringAssert.Contains("空气", air, "空气格显示「空气」");
        }

        /// <summary>ProbeText 的高速状态行是**用户指定的固定格式**：状态词 + 实测倍速 + 地表温度 + 湿度。</summary>
        [Test]
        public void DescribeHighSpeedRunning_FixedFormat()
        {
            Assert.AreEqual("正在运转 ×7.6 地表 12.3° 湿度 0.456",
                ParticleEditor.DescribeHighSpeedRunning(7.6f, 12.3f, 0.456f),
                "倍速一位小数、温度一位小数带度、湿度三位小数");

            Assert.AreEqual("正在运转 ×1.0 地表 -- 湿度 --",
                ParticleEditor.DescribeHighSpeedRunning(1f, float.NaN, float.NaN),
                "温度和湿度未采样（NaN）时都显示 --，不猜数字");

            Assert.AreEqual("正在运转 ×16.0 地表 20.0° 湿度 0.000",
                ParticleEditor.DescribeHighSpeedRunning(16f, 20f, 0f),
                "小数位固定：整数值也照写一位 / 三位");
        }

        /// <summary>ProbeText 的三段优先级：高速状态行 &gt; 悬停的投放项名 &gt; 光标格读数。</summary>
        [Test]
        public void ComposeProbeLine_PrefersRunningStatusThenHoveredName()
        {
            Assert.AreEqual("状态行",
                ParticleEditor.ComposeProbeLine("状态行", "投放项名", "格读数"),
                "三段都有时高速状态行胜出（会话期间编辑被锁，状态行是此刻唯一有意义的读数）");

            Assert.AreEqual("投放项名",
                ParticleEditor.ComposeProbeLine(null, "投放项名", "格读数"),
                "没有高速状态行时悬停的投放项名胜出（指针停在按钮上时底下那格的读数没有意义）");

            Assert.AreEqual("格读数",
                ParticleEditor.ComposeProbeLine(null, null, "格读数"),
                "只有光标格读数时照旧显示（既有行为不变）");

            Assert.AreEqual(string.Empty,
                ParticleEditor.ComposeProbeLine(null, string.Empty, null),
                "三段皆空（null 与空串一视同仁）时给空串，不是 null");
        }

        /// <summary>该 typeId 的可投粒子项显示名是 <paramref name="displayName"/>，且类名是同一类型（按名字两种写法都能选中）。</summary>
        private void AssertParticle(ushort typeId, string displayName)
        {
            Assert.IsTrue(_editor.SelectByTypeId(typeId), "可投列表里应当有 " + displayName);
            ParticleSelectionEntry entry = _editor.CurrentSelection;

            Assert.AreEqual(displayName, entry.DisplayName, "显示名（中文）");
            Assert.IsFalse(entry.IsCreature);
            Assert.AreEqual(entry.ClassName, ParticleRegistry.Types[typeId].ClassName,
                "类名取自注册表条目（语言无关的类型标识）");

            Assert.IsTrue(_editor.SelectByName(entry.ClassName), "按类名也能选中同一项");
            Assert.AreSame(entry, _editor.CurrentSelection, "类名与显示名指向同一项");
        }

        /// <summary>字符串里是否含汉字（用于断言显示名是中文而不是类名）。</summary>
        private static bool ContainsChinese(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= '\u4e00' && text[i] <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>列表里是否有可投粒子项用的是这个 typeId（动物项不参与）。</summary>
        private bool HasTypeId(ushort typeId)
        {
            IReadOnlyList<ParticleSelectionEntry> selections = _editor.Selections;
            for (int i = 0; i < selections.Count; i++)
            {
                if (!selections[i].IsCreature && selections[i].TypeId == typeId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
