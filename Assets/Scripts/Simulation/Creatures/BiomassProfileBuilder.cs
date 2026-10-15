using System;
using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 开发期算法：把动物美术的**原始像素**转成 <see cref="BiomassProfile"/> 需要的纯数据
    /// （死亡轮廓的每格颜色索引 + 不透明行范围）。编辑器生成器（<c>CreatureArtGenerator</c>）与
    /// EditMode 漂移守卫测试（<c>CreatureArtDataTests</c>）共用这一份实现，两边因此不可能算出不同的表。
    /// </summary>
    /// <remarks>
    /// <para><b>不引用 UnityEditor：</b>本类只依赖 <see cref="Color32"/>，编译进纯 C# 的
    /// <c>TapTap2026.Simulation</c> 程序集，美术路径的位图解码仍留在编辑器侧。</para>
    /// <para><b>可分配：</b>本类只属于开发期 / 测试期，不参与 <c>CreatureWorld.Step</c> 的确定性约束，
    /// 允许 <c>new</c> 数组、允许循环里的普通索引。</para>
    /// <para><b>行序约定：</b>输入像素一律是 <c>Texture2D.GetPixels32()</c> 的行序——**自底向上**，
    /// <c>pixels[row * width + col]</c>，row 0 = 画布底边。输出轮廓沿用同一行序
    /// （<c>dy * 16 + dx</c>，dy 0 = 精灵底边），与 <see cref="BiomassProfile"/> 的布局逐格对齐。</para>
    /// </remarks>
    public static class BiomassProfileBuilder
    {
        /// <summary>判定「不透明 = 尸体格」的 alpha 阈值；低于它记为空格。</summary>
        private const int OpaqueAlphaThreshold = 128;

        /// <summary>
        /// 把一帧 16×16 像素转成 256 长的轮廓表（与输入等长）。
        /// <c>alpha ≥ 128</c> 的像素 = 尸体格，其值 = <paramref name="palette"/> 中**平方 RGB 距离最近**的
        /// 颜色索引（平局取较小索引，保证确定性）；<c>alpha &lt; 128</c> = <see cref="BiomassProfile.Empty"/>（-1）。
        /// </summary>
        /// <param name="framePixels">单帧像素，<c>GetPixels32</c> 行序（自底向上）。</param>
        /// <param name="palette">颜色索引对应的调色板（顺序即索引，不得重排）。</param>
        /// <returns>长度等于 <paramref name="framePixels"/> 的轮廓表。</returns>
        public static sbyte[] BuildCells(Color32[] framePixels, Color32[] palette)
        {
            if (framePixels == null)
            {
                throw new ArgumentNullException(nameof(framePixels));
            }

            if (palette == null || palette.Length == 0)
            {
                throw new ArgumentException("调色板不能为空。", nameof(palette));
            }

            var cells = new sbyte[framePixels.Length];
            for (int i = 0; i < framePixels.Length; i++)
            {
                Color32 pixel = framePixels[i];
                cells[i] = pixel.a < OpaqueAlphaThreshold
                    ? BiomassProfile.Empty
                    : (sbyte)NearestPaletteIndex(pixel, palette);
            }

            return cells;
        }

        /// <summary>
        /// 度量整张 sheet 上**帧局部行**的不透明并集：sheet 是 16×16 帧的网格，局部行 = <c>y % 16</c>；
        /// 任一帧该行有 <c>alpha ≥ 128</c> 的像素即计入。全空表（无任何不透明像素）时
        /// <paramref name="minRow"/> = 0、<paramref name="maxRow"/> = -1。
        /// </summary>
        /// <param name="sheetPixels">整张 sheet 的像素，<c>GetPixels32</c> 行序（自底向上）。</param>
        /// <param name="sheetWidth">sheet 宽（必须为 16 的倍数）。</param>
        /// <param name="sheetHeight">sheet 高（必须为 16 的倍数）。</param>
        /// <param name="minRow">输出：并集中最小的帧局部行。</param>
        /// <param name="maxRow">输出：并集中最大的帧局部行。</param>
        public static void MeasureOpaqueRows(
            Color32[] sheetPixels,
            int sheetWidth,
            int sheetHeight,
            out int minRow,
            out int maxRow)
        {
            if (sheetPixels == null)
            {
                throw new ArgumentNullException(nameof(sheetPixels));
            }

            if (sheetWidth <= 0 || sheetHeight <= 0)
            {
                throw new ArgumentException("sheet 宽高必须为正。");
            }

            if (sheetWidth % 16 != 0 || sheetHeight % 16 != 0)
            {
                throw new ArgumentException("sheet 宽高必须是 16 的倍数（帧网格）。");
            }

            if (sheetPixels.Length != sheetWidth * sheetHeight)
            {
                throw new ArgumentException("sheet 像素数与宽高不符。", nameof(sheetPixels));
            }

            int min = 16;
            int max = -1;
            for (int y = 0; y < sheetHeight; y++)
            {
                int localRow = y % 16;
                int rowStart = y * sheetWidth;
                for (int x = 0; x < sheetWidth; x++)
                {
                    if (sheetPixels[rowStart + x].a < OpaqueAlphaThreshold)
                    {
                        continue;
                    }

                    if (localRow < min)
                    {
                        min = localRow;
                    }

                    if (localRow > max)
                    {
                        max = localRow;
                    }

                    break;
                }
            }

            if (max < 0)
            {
                // 全空表：契约规定 min = 0、max = -1。
                minRow = 0;
                maxRow = -1;
                return;
            }

            minRow = min;
            maxRow = max;
        }

        /// <summary>在调色板里找平方 RGB 距离最近的颜色索引；平局取较小索引（严格 &lt; 保留先遇到的）。</summary>
        private static int NearestPaletteIndex(Color32 color, Color32[] palette)
        {
            int bestIndex = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int dr = color.r - palette[i].r;
                int dg = color.g - palette[i].g;
                int db = color.b - palette[i].b;
                int distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }
    }
}
