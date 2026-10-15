using System.Text.RegularExpressions;
using NUnit.Framework;
using TapTap2026.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 对话状态机与解析（纯 C#，不碰 UI / 协程 / 输入）。UI、打字机、热键、暂停联动不进 EditMode。
    /// </summary>
    public sealed class DialogueRunnerTests
    {
        private const string BranchJson =
            @"{ ""id"": ""intro"", ""onEnd"": ""start_simulation"", ""segments"": [" +
            @"{ ""id"": ""start"", ""lines"": [" +
            @"{ ""speaker"": ""mei"", ""text"": ""你来了。"" }," +
            @"{ ""speaker"": ""chen"", ""text"": ""走哪边？"", ""choices"": [" +
            @"{ ""text"": ""左边"", ""gotoSegment"": ""left"" }," +
            @"{ ""text"": ""右边"", ""gotoSegment"": ""right"" } ] } ] }," +
            @"{ ""id"": ""left"", ""lines"": [ { ""speaker"": ""chen"", ""text"": ""往左。"" } ] }," +
            @"{ ""id"": ""right"", ""lines"": [ { ""speaker"": ""chen"", ""text"": ""往右。"" } ] } ] }";

        private const string LinearJson =
            @"{ ""id"": ""lin"", ""onEnd"": ""done"", ""segments"": [" +
            @"{ ""id"": ""a"", ""lines"": [" +
            @"{ ""speaker"": ""mei"", ""text"": ""一"" }," +
            @"{ ""speaker"": ""chen"", ""text"": ""二"" } ] } ] }";

        private const string LinearNoEndJson =
            @"{ ""id"": ""lin"", ""segments"": [" +
            @"{ ""id"": ""a"", ""lines"": [ { ""speaker"": ""mei"", ""text"": ""一"" } ] } ] }";

        private const string BadGotoJson =
            @"{ ""id"": ""bad"", ""segments"": [" +
            @"{ ""id"": ""a"", ""lines"": [ { ""speaker"": ""mei"", ""text"": ""?"", " +
            @"""choices"": [ { ""text"": ""走"", ""gotoSegment"": ""missing"" } ] } ] } ] }";

        private const string EmptySegmentJson =
            @"{ ""id"": ""empty"", ""segments"": [ { ""id"": ""a"", ""lines"": [] } ] }";

        private const string CharacterJson =
            @"{ ""id"": ""c"", ""characters"": [ { ""id"": ""mei"", ""displayName"": ""Mei"" } ], " +
            @"""segments"": [ { ""id"": ""a"", ""lines"": [ { ""speaker"": ""mei"", ""text"": ""hi"" } ] } ] }";

        private static DialogueFile Parse(string json)
        {
            bool ok = DialogueData.TryParse(json, out DialogueFile file, out string error);
            Assert.IsTrue(ok, "解析应成功：" + error);
            Assert.IsNotNull(file);
            return file;
        }

        private static DialogueRunner Started(string json)
        {
            DialogueRunner runner = new DialogueRunner();
            runner.Start(Parse(json));
            return runner;
        }

        // ============================== 解析 ==============================

        [Test]
        public void Parse_ValidJson_RoundTrips()
        {
            DialogueFile file = Parse(BranchJson);

            Assert.AreEqual("intro", file.id);
            Assert.AreEqual("start_simulation", file.onEnd);
            Assert.AreEqual(3, file.segments.Count);
            Assert.AreEqual("start", file.segments[0].id);
            Assert.AreEqual(2, file.segments[0].lines.Count);
            Assert.AreEqual("mei", file.segments[0].lines[0].speaker);
            Assert.IsNotNull(file.segments[0].lines[1].choices);
            Assert.AreEqual(2, file.segments[0].lines[1].choices.Count);
            Assert.AreEqual("left", file.segments[0].lines[1].choices[0].gotoSegment);
        }

        [Test]
        public void Parse_Characters_RoundTrips()
        {
            DialogueFile file = Parse(CharacterJson);

            Assert.AreEqual(1, file.characters.Count);
            Assert.AreEqual("mei", file.characters[0].id);
            Assert.AreEqual("Mei", file.characters[0].displayName);
        }

        [Test]
        public void Parse_MissingCharacters_YieldsEmptyList()
        {
            DialogueFile file = Parse(LinearJson);

            Assert.IsNotNull(file.characters);
            Assert.AreEqual(0, file.characters.Count);
        }

        [Test]
        public void Parse_MalformedJson_ReturnsFalse()
        {
            bool ok = DialogueData.TryParse("这不是 json {", out _, out string error);
            Assert.IsFalse(ok);
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void Parse_NoSegments_ReturnsFalse()
        {
            bool ok = DialogueData.TryParse(@"{ ""id"": ""x"" }", out _, out string error);
            Assert.IsFalse(ok);
            Assert.IsNotEmpty(error);
        }

        // ============================== 状态机 ==============================

        [Test]
        public void Start_BeginsAtFirstSegment()
        {
            DialogueRunner runner = Started(BranchJson);

            Assert.IsFalse(runner.Finished);
            Assert.AreEqual("start", runner.CurrentSegment.id);
            Assert.AreEqual(0, runner.Index);
            Assert.AreEqual("你来了。", runner.CurrentLine.text);
        }

        [Test]
        public void Next_AdvancesLinesInOrder()
        {
            DialogueRunner runner = Started(LinearJson);

            Assert.IsTrue(runner.Next());
            Assert.AreEqual(1, runner.Index);
            Assert.AreEqual("二", runner.CurrentLine.text);
            Assert.IsFalse(runner.Next(), "到段尾应返回 false 并结束");
            Assert.IsTrue(runner.Finished);
        }

        [Test]
        public void Next_AtChoicePoint_BlocksUntilChoose()
        {
            DialogueRunner runner = Started(BranchJson);

            Assert.IsTrue(runner.Next()); // 进入带选项的句
            Assert.IsTrue(runner.IsChoicePoint);
            Assert.AreEqual(1, runner.Index);

            Assert.IsFalse(runner.Next(), "选项点 Next 应阻塞");
            Assert.AreEqual(1, runner.Index, "阻塞时下标不变");
            Assert.IsFalse(runner.Finished);
        }

        [Test]
        public void Choose_JumpsToSegment()
        {
            DialogueRunner runner = Started(BranchJson);
            runner.Next();

            Assert.IsTrue(runner.Choose(0));
            Assert.AreEqual("left", runner.CurrentSegment.id);
            Assert.AreEqual(0, runner.Index);
            Assert.AreEqual("往左。", runner.CurrentLine.text);
        }

        [Test]
        public void Choose_WithBadGoto_FinishesWithWarning()
        {
            DialogueRunner runner = Started(BadGotoJson);

            LogAssert.Expect(LogType.Warning, new Regex("不存在的段"));
            Assert.IsFalse(runner.Choose(0));
            Assert.IsTrue(runner.Finished, "坏跳转应视为结束");
        }

        [Test]
        public void Choose_WithOutOfRangeIndex_ReturnsFalseAndKeepsState()
        {
            DialogueRunner runner = Started(BranchJson);
            runner.Next(); // 进入带选项的句
            Assert.IsTrue(runner.IsChoicePoint);

            Assert.IsFalse(runner.Choose(-1), "负下标应失败");
            Assert.IsFalse(runner.Choose(2), "越界下标应失败");
            Assert.AreEqual("start", runner.CurrentSegment.id, "越界选择不应改段");
            Assert.AreEqual(1, runner.Index, "越界选择不应改下标");
            Assert.IsTrue(runner.IsChoicePoint, "越界选择后仍停在选项点");
            Assert.IsFalse(runner.Finished, "越界选择不应结束对话");
        }

        [Test]
        public void Choose_AtNonChoicePoint_ReturnsFalseAndKeepsState()
        {
            DialogueRunner runner = Started(LinearJson);

            Assert.IsFalse(runner.Choose(0), "非选项点 Choose 应失败");
            Assert.AreEqual("a", runner.CurrentSegment.id);
            Assert.AreEqual(0, runner.Index);
            Assert.AreEqual("一", runner.CurrentLine.text);
            Assert.IsFalse(runner.Finished, "非选项点 Choose 不应结束对话");
        }

        [Test]
        public void OnEnd_FiresOnceAtEnd()
        {
            DialogueRunner runner = new DialogueRunner();
            int fired = 0;
            string received = null;
            runner.OnEnd += name =>
            {
                fired++;
                received = name;
            };
            runner.Start(Parse(LinearJson));

            runner.Next(); // 到第二句
            runner.Next(); // 段尾 → 结束

            Assert.AreEqual(1, fired);
            Assert.AreEqual("done", received);
            Assert.IsTrue(runner.Finished);

            Assert.IsFalse(runner.Next(), "结束后 Next 仍为 false");
            Assert.AreEqual(1, fired, "OnEnd 只触发一次");
        }

        [Test]
        public void OnEnd_NotFiredWhenAbsent()
        {
            DialogueRunner runner = new DialogueRunner();
            int fired = 0;
            runner.OnEnd += _ => fired++;
            runner.Start(Parse(LinearNoEndJson));

            runner.Next();

            Assert.IsTrue(runner.Finished);
            Assert.AreEqual(0, fired, "未配 onEnd 不应触发事件");
        }

        [Test]
        public void Restart_ResetsState()
        {
            DialogueRunner runner = Started(BranchJson);
            runner.Next();
            runner.Choose(0);

            runner.Start(Parse(BranchJson));

            Assert.IsFalse(runner.Finished);
            Assert.AreEqual(0, runner.Index);
            Assert.AreEqual("你来了。", runner.CurrentLine.text);
        }

        [Test]
        public void EmptySegment_FinishesImmediately()
        {
            DialogueRunner runner = Started(EmptySegmentJson);

            Assert.IsTrue(runner.Finished);
            Assert.IsNull(runner.CurrentLine);
        }
    }
}
