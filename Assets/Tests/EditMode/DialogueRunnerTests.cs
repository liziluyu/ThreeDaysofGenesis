using NUnit.Framework;
using TapTap2026.Game;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 对话状态机与解析（纯 C#，不碰 UI / 协程 / 输入）。UI、打字机、热键、暂停联动不进 EditMode。
    /// </summary>
    public sealed class DialogueRunnerTests
    {
        private const string LinearJson =
            @"{ ""id"": ""lin"", ""onEnd"": ""done"", ""lines"": [" +
            @"{ ""speaker"": ""mei"", ""text"": ""一"" }," +
            @"{ ""speaker"": ""chen"", ""text"": ""二"" } ] }";

        private const string LinearNoEndJson =
            @"{ ""id"": ""lin"", ""lines"": [ { ""speaker"": ""mei"", ""text"": ""一"" } ] }";

        private const string EmptyLinesJson =
            @"{ ""id"": ""empty"", ""lines"": [] }";

        private const string CharacterJson =
            @"{ ""id"": ""c"", ""characters"": [ { ""id"": ""mei"", ""displayName"": ""Mei"" } ], " +
            @"""lines"": [ { ""speaker"": ""mei"", ""text"": ""hi"" } ] }";

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
            DialogueFile file = Parse(LinearJson);

            Assert.AreEqual("lin", file.id);
            Assert.AreEqual("done", file.onEnd);
            Assert.AreEqual(2, file.lines.Count);
            Assert.AreEqual("mei", file.lines[0].speaker);
            Assert.AreEqual("一", file.lines[0].text);
            Assert.AreEqual("二", file.lines[1].text);
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
        public void Parse_NoLines_ReturnsFalse()
        {
            bool ok = DialogueData.TryParse(@"{ ""id"": ""x"" }", out _, out string error);
            Assert.IsFalse(ok);
            Assert.IsNotEmpty(error);
        }

        // ============================== 状态机 ==============================

        [Test]
        public void Start_BeginsAtFirstLine()
        {
            DialogueRunner runner = Started(LinearJson);

            Assert.IsFalse(runner.Finished);
            Assert.AreEqual(0, runner.Index);
            Assert.AreEqual("一", runner.CurrentLine.text);
        }

        [Test]
        public void Next_AdvancesLinesInOrder()
        {
            DialogueRunner runner = Started(LinearJson);

            Assert.IsTrue(runner.Next());
            Assert.AreEqual(1, runner.Index);
            Assert.AreEqual("二", runner.CurrentLine.text);
            Assert.IsFalse(runner.Next(), "到台词尾巴应返回 false 并结束");
            Assert.IsTrue(runner.Finished);
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
            runner.Next(); // 尾巴 → 结束

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
            DialogueRunner runner = Started(LinearJson);
            runner.Next();

            runner.Start(Parse(LinearJson));

            Assert.IsFalse(runner.Finished);
            Assert.AreEqual(0, runner.Index);
            Assert.AreEqual("一", runner.CurrentLine.text);
        }

        [Test]
        public void EmptyLines_FinishesImmediately()
        {
            DialogueRunner runner = Started(EmptyLinesJson);

            Assert.IsTrue(runner.Finished);
            Assert.IsNull(runner.CurrentLine);
        }
    }
}
