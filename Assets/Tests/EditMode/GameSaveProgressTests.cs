using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using TapTap2026.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="GameController"/> 的公开**静态存档 API**（路径拼装、读写往返、缺档/坏档回退、
    /// 解锁集合收集）的纯逻辑测试——全是静态方法，不建 MonoBehaviour。
    /// </summary>
    /// <remarks>
    /// <para>EditMode 里组件不会自己跑 <c>Awake</c>，而存档这部分也不依赖它，所以整类只调静态方法。</para>
    /// <para>全程只碰**探头文件**（<see cref="ProbeSaveFileName"/>），绝不读写真实存档：<see cref="SetUp"/>
    /// 先删探头文件保证每个测试可独立重复跑，<see cref="TearDown"/> 再删一次不把文件留在
    /// <see cref="Application.persistentDataPath"/> 里。</para>
    /// </remarks>
    public sealed class GameSaveProgressTests
    {
        /// <summary>探头存档文件名：绝不碰真存档 game_save.json。</summary>
        private const string ProbeSaveFileName = "editmode_save_probe.json";

        /// <summary>探头存档的完整路径。</summary>
        private static string ProbeSavePath
        {
            get { return GameController.SavePathFor(ProbeSaveFileName); }
        }

        [SetUp]
        public void SetUp()
        {
            DeleteProbeFile();
        }

        [TearDown]
        public void TearDown()
        {
            DeleteProbeFile();
            // 坏档 / 写失败路径会记错误日志（见 LoadSave/WriteSave 的 catch）——那是被测行为，
            // 不是未处理异常，别让测试框架当成测试失败。
            LogAssert.ignoreFailingMessages = false;
        }

        private static void DeleteProbeFile()
        {
            if (File.Exists(ProbeSavePath))
            {
                File.Delete(ProbeSavePath);
            }
        }

        /// <summary>路径 = persistentDataPath 目录 + 原样文件名；文件名传 null / 空串回退默认名。</summary>
        [Test]
        public void SavePathFor_RoundTripsFileName()
        {
            string path = GameController.SavePathFor(ProbeSaveFileName);

            Assert.AreEqual(
                Path.GetFullPath(Application.persistentDataPath),
                Path.GetDirectoryName(Path.GetFullPath(path)),
                "探头存档应落在 persistentDataPath 目录下");
            Assert.AreEqual(ProbeSaveFileName, Path.GetFileName(path), "文件名应原样保留探头名");

            Assert.AreEqual(
                GameController.DefaultSaveFileName,
                Path.GetFileName(GameController.SavePathFor(null)),
                "文件名传 null 应回退到默认存档名");
            Assert.AreEqual(
                GameController.DefaultSaveFileName,
                Path.GetFileName(GameController.SavePathFor("")),
                "文件名传空串应回退到默认存档名");
        }

        /// <summary>文件不存在时读档返回 null（调用方据此回退起始阶段）。</summary>
        [Test]
        public void LoadSave_MissingFile_ReturnsNull()
        {
            Assert.IsFalse(File.Exists(ProbeSavePath), "SetUp 应已把探头文件删掉");
            Assert.IsNull(GameController.LoadSave(ProbeSaveFileName), "没有存档文件时应返回 null");
        }

        /// <summary>坏 JSON 读档返回 null（反序列化异常被吞成 null，不抛给调用方）。</summary>
        [Test]
        public void LoadSave_MalformedFile_ReturnsNull()
        {
            LogAssert.ignoreFailingMessages = true;
            File.WriteAllText(ProbeSavePath, "{ not json");

            Assert.IsNull(GameController.LoadSave(ProbeSaveFileName), "坏 JSON 应返回 null");
        }

        /// <summary>写出后读回逐字段一致，且解锁集合按序号排序（同一集合产出稳定文件）。</summary>
        [Test]
        public void WriteSaveThenLoadSave_RoundTrips()
        {
            GameController.WriteSave(ProbeSaveFileName, "phase_02", new List<string> { "phase_03", "phase_01" });

            GameController.SaveData loaded = GameController.LoadSave(ProbeSaveFileName);
            Assert.IsNotNull(loaded, "写出后应能读回");
            Assert.AreEqual("phase_02", loaded.currentPhaseId, "当前阶段 id 应往返一致");
            Assert.IsNotNull(loaded.unlockedPhases, "解锁集合应往返一致");
            CollectionAssert.AreEqual(
                new List<string> { "phase_01", "phase_03" },
                loaded.unlockedPhases,
                "解锁集合应按序号排序写出（同一集合产出稳定文件）");
        }

        /// <summary>收集解锁集合：null → 空表；旧档只有 currentPhaseId → 补入当前阶段；空项剔除且不重复。</summary>
        [Test]
        public void CollectUnlockedPhaseIds_AddsCurrentPhaseAndSkipsBlanks()
        {
            List<string> fromNull = GameController.CollectUnlockedPhaseIds(null);
            Assert.AreEqual(0, fromNull.Count, "null 存档应收集到空集合");

            List<string> legacy = GameController.CollectUnlockedPhaseIds(
                new GameController.SaveData { currentPhaseId = "phase_02" });
            CollectionAssert.AreEquivalent(
                new[] { "phase_02" },
                legacy,
                "旧档没有 unlockedPhases 时，当前阶段本身即已解锁");

            List<string> collected = GameController.CollectUnlockedPhaseIds(
                new GameController.SaveData
                {
                    currentPhaseId = "phase_02",
                    unlockedPhases = new List<string> { "phase_01", null, "", "phase_02" },
                });
            CollectionAssert.AreEquivalent(
                new[] { "phase_01", "phase_02" },
                collected,
                "空项应剔除、当前阶段应补入且不重复");
        }

        /// <summary>解锁集合传 null 不抛，字段写成空表（读回是空表而不是缺字段）。</summary>
        [Test]
        public void WriteSave_NullUnlockedList_WritesEmptyArray()
        {
            Assert.DoesNotThrow(
                () => GameController.WriteSave(ProbeSaveFileName, "phase_01", null),
                "解锁集合传 null 不应抛");

            GameController.SaveData loaded = GameController.LoadSave(ProbeSaveFileName);
            Assert.IsNotNull(loaded, "传入 null 解锁集合也应写出可读回的存档");
            Assert.AreEqual("phase_01", loaded.currentPhaseId, "当前阶段 id 应写出");
            Assert.IsNotNull(loaded.unlockedPhases, "解锁集合应写为空表而不是缺字段");
            Assert.AreEqual(0, loaded.unlockedPhases.Count, "解锁集合应为空表");
        }
    }
}
