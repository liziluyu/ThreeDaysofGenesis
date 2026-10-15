using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Editor
{
    /// <summary>
    /// 开发期生成器：读动物美术 PNG 与切片 pivot，产出纯数据
    /// <c>Assets/Scripts/Simulation/Creatures/CreatureArtData.g.cs</c>（供
    /// <see cref="BiomassProfile"/> 消费）。菜单 <c>Tools/Animals/Regenerate Creature Art Data</c>
    /// （同签名兼作 <c>-executeMethod</c> 入口）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么要生成而不是手写：</b>把「美术 → 尸体轮廓/几何」的同步从手工硬编码改成
    /// 生成 + 提交生成物 + 测试对拍。运行期模拟仍只读生成的纯数据、**不读纹理**（确定性不破），
    /// 而 <c>CreatureArtDataTests</c> 用同一套 <see cref="BiomassProfileBuilder"/> 从 PNG 现场复算对拍，
    /// 美术变更后忘重跑即测试红。</para>
    /// <para><b>解码不依赖导入旗标：</b>用 <see cref="File.ReadAllBytes"/> + <see cref="Texture2D.LoadImage"/>
    /// 读原始 PNG 字节，因此不受资源 isReadable 影响。</para>
    /// </remarks>
    public static class CreatureArtGenerator
    {
        /// <summary>物种根目录（其下每个子目录 = 一个物种，如 <c>rabbit/fish/bird</c>）。</summary>
        private const string AnimalsRoot = "Assets/Images/Animals";

        /// <summary>生成物落盘路径。</summary>
        private const string OutputPath = "Assets/Scripts/Simulation/Creatures/CreatureArtData.g.cs";

        /// <summary>单帧边长（源像素）。</summary>
        private const int FrameSize = 16;

        /// <summary>每行输出的轮廓值个数。</summary>
        private const int ValuesPerLine = FrameSize;

        /// <summary>pivot.x 期望值（水平居中，源像素）。</summary>
        private const float ExpectedPivotX = FrameSize / 2f;

        /// <summary>pixel 值的浮点比较容差。</summary>
        private const float PivotEpsilon = 0.01f;

        /// <summary>读美术 → 生成 <see cref="CreatureArtData"/>。可反复运行，输出幂等。</summary>
        [MenuItem("Tools/Animals/Regenerate Creature Art Data")]
        public static void RegenerateAll()
        {
            string[] folders = AssetDatabase.GetSubFolders(AnimalsRoot);
            Array.Sort(folders, StringComparer.Ordinal);

            var species = new List<SpeciesData>();
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                SpeciesData data = BuildSpecies(folder, name);
                if (data != null)
                {
                    species.Add(data);
                }
            }

            var sb = new StringBuilder();
            Emit(sb, species);
            File.WriteAllText(OutputPath, sb.ToString());
            AssetDatabase.ImportAsset(OutputPath);

            foreach (SpeciesData s in species)
            {
                Debug.Log(
                    $"[CreatureArtGenerator] {s.ClassName}: PivotRow={s.PivotRow} " +
                    $"Min={s.MinRow} Max={s.MaxRow} Height={s.Height} Cells={s.CellCount} => {OutputPath}");
            }
        }

        /// <summary>读一个物种的 PNG / pivot，算出轮廓与几何；失败（缺文件 / pivot 不合规）返回 null 并记日志。</summary>
        private static SpeciesData BuildSpecies(string folder, string name)
        {
            string pngPath = $"{folder}/1/{name}.png";
            if (!File.Exists(pngPath))
            {
                Debug.LogWarning($"[CreatureArtGenerator] 缺少美术文件，跳过物种 '{name}'：{pngPath}");
                return null;
            }

            var texture = new Texture2D(2, 2);
            Color32[] pixels;
            int width;
            int height;
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(pngPath)))
                {
                    Debug.LogError($"[CreatureArtGenerator] PNG 解码失败，跳过物种 '{name}'：{pngPath}");
                    return null;
                }

                pixels = texture.GetPixels32();
                width = texture.width;
                height = texture.height;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            if (width % FrameSize != 0 || height % FrameSize != 0)
            {
                Debug.LogError(
                    $"[CreatureArtGenerator] sheet 宽高不是 {FrameSize} 的倍数，跳过物种 '{name}'：" +
                    $"{width}×{height} {pngPath}");
                return null;
            }

            // 帧 0 = 左下 16×16 块（x 0..15、y 0..15，自底原点）用于轮廓。
            var frame0 = new Color32[FrameSize * FrameSize];
            for (int dy = 0; dy < FrameSize; dy++)
            {
                int rowStart = dy * width;
                for (int dx = 0; dx < FrameSize; dx++)
                {
                    frame0[(dy * FrameSize) + dx] = pixels[rowStart + dx];
                }
            }

            sbyte[] cells = BiomassProfileBuilder.BuildCells(frame0, BiomassParticle.Palette);
            BiomassProfileBuilder.MeasureOpaqueRows(pixels, width, height, out int minRow, out int maxRow);
            int height_cells = maxRow < 0 ? 0 : maxRow - minRow + 1;

            Sprite sprite = LoadPrimarySprite(pngPath, name + "_0");
            if (sprite == null)
            {
                Debug.LogError($"[CreatureArtGenerator] 找不到 Sprite，跳过物种 '{name}'：{pngPath}");
                return null;
            }

            Vector2 pivot = sprite.pivot;
            if (Mathf.Abs(pivot.x - ExpectedPivotX) > PivotEpsilon
                || Mathf.Abs(pivot.y - Mathf.Round(pivot.y)) > PivotEpsilon)
            {
                Debug.LogError(
                    $"[CreatureArtGenerator] pivot 不合规（要求 x={ExpectedPivotX:0} 且 y 为整数），跳过物种 '{name}'：" +
                    $"pivot=({pivot.x},{pivot.y}) {pngPath}");
                return null;
            }

            int cellCount = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] >= 0)
                {
                    cellCount++;
                }
            }

            return new SpeciesData(
                className: char.ToUpperInvariant(name[0]) + name.Substring(1),
                pivotRow: Mathf.RoundToInt(pivot.y),
                minRow: minRow,
                maxRow: maxRow,
                height: height_cells,
                cells: cells,
                cellCount: cellCount);
        }

        /// <summary>
        /// 取该 PNG 的主切片：优先名字等于 <paramref name="preferredName"/>（如 <c>rabbit_0</c>），
        /// 取不到用资产数组里第一个 <see cref="Sprite"/>。
        /// </summary>
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

        /// <summary>拼生成物文本（显式 <c>\n</c> 换行，无时间戳 ⇒ 内容确定）。</summary>
        private static void Emit(StringBuilder sb, List<SpeciesData> species)
        {
            sb.Append("// <auto-generated> Tools/Animals/Regenerate Creature Art Data 生成,勿手改;美术变更后重跑该菜单\n");
            sb.Append("namespace TapTap2026.Simulation\n");
            sb.Append("{\n");
            sb.Append("    /// <summary>\n");
            sb.Append("    /// 动物美术的**生成产物**：由 Unity 菜单 <c>Tools/Animals/Regenerate Creature Art Data</c> 读\n");
            sb.Append("    /// <c>Assets/Images/Animals/&lt;species&gt;/1/&lt;species&gt;.png</c> 与切片 pivot 生成，供\n");
            sb.Append("    /// <see cref=\"BiomassProfile\"/> 消费。运行期模拟只读这份纯数据，**不碰纹理**（确定性不破）。\n");
            sb.Append("    /// </summary>\n");
            sb.Append("    /// <remarks>\n");
            sb.Append("    /// 美术变更后重跑菜单并提交本文件；<c>CreatureArtDataTests</c> 会现场从 PNG 复算并对拍，\n");
            sb.Append("    /// 忘记重跑即测试红并提示菜单路径。物种按文件夹名序数排序，每个物种一个 public static 嵌套类。\n");
            sb.Append("    /// </remarks>\n");
            sb.Append("    public static class CreatureArtData\n");
            sb.Append("    {\n");

            for (int i = 0; i < species.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }

                EmitSpecies(sb, species[i]);
            }

            sb.Append("    }\n");
            sb.Append("}\n");
        }

        /// <summary>拼一个物种的嵌套类。</summary>
        private static void EmitSpecies(StringBuilder sb, SpeciesData s)
        {
            sb.Append("        /// <summary>").Append(s.ClassName.ToLowerInvariant()).Append("。</summary>\n");
            sb.Append("        public static class ").Append(s.ClassName).Append('\n');
            sb.Append("        {\n");
            sb.Append("            /// <summary>切片 pivot.y（源像素，自底）；渲染按它贴脚底。</summary>\n");
            sb.Append("            public const int PivotRowPixels = ").Append(s.PivotRow.ToString()).Append(";\n\n");
            sb.Append("            /// <summary>整张 sheet 不透明帧局部行并集的最小行（自底）。</summary>\n");
            sb.Append("            public const int MinOpaqueRow = ").Append(s.MinRow.ToString()).Append(";\n\n");
            sb.Append("            /// <summary>整张 sheet 不透明帧局部行并集的最大行（自底）。</summary>\n");
            sb.Append("            public const int MaxOpaqueRow = ").Append(s.MaxRow.ToString()).Append(";\n\n");
            sb.Append("            /// <summary>不透明行数（Max − Min + 1）。</summary>\n");
            sb.Append("            public const int HeightCells = ").Append(s.Height.ToString()).Append(";\n\n");
            sb.Append("            /// <summary>帧 0（左下）轮廓：256 值，<c>dy * 16 + dx</c>，-1 = 空，否则 = 调色板索引。</summary>\n");
            sb.Append("            public static readonly sbyte[] BiomassCells =\n");
            sb.Append("            {\n");

            for (int i = 0; i < s.Cells.Length; i++)
            {
                if (i % ValuesPerLine == 0)
                {
                    sb.Append("                ");
                }

                sb.Append(s.Cells[i].ToString());

                if (i % ValuesPerLine == ValuesPerLine - 1 || i == s.Cells.Length - 1)
                {
                    sb.Append(",\n");
                }
                else
                {
                    sb.Append(", ");
                }
            }

            sb.Append("            };\n");
            sb.Append("        }\n");
        }

        /// <summary>一个物种的生成结果。</summary>
        private sealed class SpeciesData
        {
            public SpeciesData(string className, int pivotRow, int minRow, int maxRow, int height, sbyte[] cells, int cellCount)
            {
                ClassName = className;
                PivotRow = pivotRow;
                MinRow = minRow;
                MaxRow = maxRow;
                Height = height;
                Cells = cells;
                CellCount = cellCount;
            }

            public string ClassName { get; }

            public int PivotRow { get; }

            public int MinRow { get; }

            public int MaxRow { get; }

            public int Height { get; }

            public sbyte[] Cells { get; }

            public int CellCount { get; }
        }
    }
}
