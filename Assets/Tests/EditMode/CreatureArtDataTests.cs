using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// <see cref="CreatureArtData"/> 生成物的**漂移守卫**：对每个存在的物种文件夹，用
    /// <see cref="BiomassProfileBuilder"/> 现场从 PNG 复算轮廓与几何（读 PNG + 读切片 pivot），
    /// 与提交的生成物逐项对拍（四个常量 + 256 格逐格）。美术变更后忘重跑菜单即红。
    /// </summary>
    /// <remarks>
    /// <para>解码方式仿 <c>CreatureSpriteFacingTests</c>：<see cref="File.ReadAllBytes"/> +
    /// <see cref="Texture2D.LoadImage"/>，不依赖资源 isReadable 导入旗标。</para>
    /// <para>这是「美术 ↔ 常量同步」意图的唯一守卫（原 <c>CreatureSpriteFacingTests.FishArtHeight_MatchesBrainClearance</c>
    /// 已删除，其职责由本测试接替）。</para>
    /// </remarks>
    public sealed class CreatureArtDataTests
    {
        /// <summary>物种根目录（每个子目录 = 一个物种）。</summary>
        private const string AnimalsRoot = "Assets/Images/Animals";

        /// <summary>单帧边长（源像素）。</summary>
        private const int FrameSize = 16;

        /// <summary>漂移失败时给操作者的提示。</summary>
        private const string RegenerateHint =
            "美术已变更：请运行 Tools/Animals/Regenerate Creature Art Data 并提交生成物。";

        /// <summary>逐物种独立复算并与提交的 <see cref="CreatureArtData"/> 对拍。</summary>
        [Test]
        public void CommittedCreatureArtData_MatchesArtOnDisk()
        {
            string[] folders = AssetDatabase.GetSubFolders(AnimalsRoot);
            Array.Sort(folders, StringComparer.Ordinal);
            Assert.IsNotEmpty(folders, "找不到任何物种文件夹：" + AnimalsRoot);

            foreach (string folder in folders)
            {
                AssertSpecies(folder, Path.GetFileName(folder));
            }
        }

        /// <summary>对拍单个物种：四个常量 + 256 格轮廓。</summary>
        private static void AssertSpecies(string folder, string name)
        {
            string className = char.ToUpperInvariant(name[0]) + name.Substring(1);
            Type nested = typeof(CreatureArtData).GetNestedType(className, BindingFlags.Public);
            Assert.IsNotNull(nested, $"CreatureArtData 缺少物种类 {className}（文件夹 {name}）。{RegenerateHint}");

            string pngPath = folder + "/1/" + name + ".png";
            Assert.IsTrue(File.Exists(pngPath), "美术资源缺失，无法对拍：" + pngPath);

            var texture = new Texture2D(2, 2);
            Color32[] pixels;
            int width;
            int height;
            try
            {
                Assert.IsTrue(texture.LoadImage(File.ReadAllBytes(pngPath)), "PNG 解码失败：" + pngPath);
                pixels = texture.GetPixels32();
                width = texture.width;
                height = texture.height;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            Assert.AreEqual(0, width % FrameSize, "sheet 宽不是 16 的倍数：" + pngPath);
            Assert.AreEqual(0, height % FrameSize, "sheet 高不是 16 的倍数：" + pngPath);

            // 帧 0 = 左下 16×16 块（x 0..15、y 0..15，自底原点）。
            var frame0 = new Color32[FrameSize * FrameSize];
            for (int dy = 0; dy < FrameSize; dy++)
            {
                int rowStart = dy * width;
                for (int dx = 0; dx < FrameSize; dx++)
                {
                    frame0[(dy * FrameSize) + dx] = pixels[rowStart + dx];
                }
            }

            sbyte[] expectedCells = BiomassProfileBuilder.BuildCells(frame0, BiomassParticle.Palette);
            BiomassProfileBuilder.MeasureOpaqueRows(pixels, width, height, out int minRow, out int maxRow);
            int expectedHeight = maxRow < 0 ? 0 : maxRow - minRow + 1;

            Sprite sprite = LoadPrimarySprite(pngPath, name + "_0");
            Assert.IsNotNull(sprite, "找不到 Sprite，无法读 pivot：" + pngPath);
            int expectedPivotRow = Mathf.RoundToInt(sprite.pivot.y);

            AssertConst(nested, "PivotRowPixels", expectedPivotRow, className);
            AssertConst(nested, "MinOpaqueRow", minRow, className);
            AssertConst(nested, "MaxOpaqueRow", maxRow, className);
            AssertConst(nested, "HeightCells", expectedHeight, className);

            FieldInfo cellsField = nested.GetField("BiomassCells", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(cellsField, $"CreatureArtData.{className}.BiomassCells 缺失。{RegenerateHint}");
            var committedCells = (sbyte[])cellsField.GetValue(null);
            Assert.IsNotNull(committedCells, $"CreatureArtData.{className}.BiomassCells 为 null。{RegenerateHint}");
            Assert.AreEqual(
                expectedCells.Length,
                committedCells.Length,
                $"CreatureArtData.{className}.BiomassCells 长度不符。{RegenerateHint}");

            for (int i = 0; i < expectedCells.Length; i++)
            {
                Assert.AreEqual(
                    expectedCells[i],
                    committedCells[i],
                    $"CreatureArtData.{className}.BiomassCells[{i}]（dy={i / FrameSize},dx={i % FrameSize}）与美术不符。{RegenerateHint}");
            }
        }

        /// <summary>断言某个 public const int 与现场推导值相等。</summary>
        private static void AssertConst(Type nested, string fieldName, int expected, string className)
        {
            FieldInfo field = nested.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, $"CreatureArtData.{className}.{fieldName} 缺失。{RegenerateHint}");
            Assert.AreEqual(
                expected,
                (int)field.GetRawConstantValue(),
                $"CreatureArtData.{className}.{fieldName} 与美术不符。{RegenerateHint}");
        }

        /// <summary>取主切片：优先名字等于 <paramref name="preferredName"/>，否则数组里第一个 Sprite。</summary>
        private static Sprite LoadPrimarySprite(string path, string preferredName)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            Sprite first = null;
            foreach (UnityEngine.Object asset in assets)
            {
                if (asset is Sprite s)
                {
                    if (first == null)
                    {
                        first = s;
                    }

                    if (s.name == preferredName)
                    {
                        return s;
                    }
                }
            }

            return first;
        }
    }
}
