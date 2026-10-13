using System.Collections.Generic;
using UnityEngine;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// 笔刷足迹的**像素圆几何**（格子空间、纯整型、无三角函数）：实心圆 <c>dx² + dy² ≤ radius²</c>
    /// ——编辑器 <c>Paint</c> / <c>Erase</c> 用的就是这条判据——以及它的**轮廓格**：落在圆内、
    /// 但四邻里至少有一个在圆外的格。轮廓即「像素画圆」的那一圈，也是鼠标处白色笔刷圈覆盖的格
    /// （见 <see cref="ParticleRenderer.SetBrushOutline"/>）。
    /// </summary>
    /// <remarks>
    /// 轮廓格全部取自笔刷自己的判据，因此白圈与左键真正会写到的足迹**严丝合缝**。刻意不用 Bresenham 中点圆：
    /// 中点圆取的是**最接近理想圆**的格，小半径下会落到笔刷之外（r = 2 的 (2,1)：<c>4 + 1 &gt; 4</c>，
    /// 左键并不会写它），白圈就会指着一批画不出来的格。
    /// <para>只用「同行取最左 / 最右」同样不行：圆顶附近相邻两行的跨度会一次缩两格
    /// （r = 3 的 dy = ±2 跨 ±2 格、dy = ±3 只剩 1 格），顶上的 (0, ±3) 会与 (±2, ±2) 断开成孤点——
    /// 所以竖直方向的轮廓也要按四邻判据一并取上，环才真的闭合。</para>
    /// </remarks>
    public static class BrushOutline
    {
        /// <summary>轮廓像素（白）。</summary>
        public static readonly Color32 OutlineColor = new Color32(255, 255, 255, 255);

        /// <summary>轮廓之外的像素（全透明——叠层只在这圈格上压白，别处不许挡住粒子）。</summary>
        public static readonly Color32 EmptyColor = new Color32(0, 0, 0, 0);

        /// <summary>相对圆心的格 (dx, dy) 是否在笔刷实心圆内——即编辑器真正会写到的格。</summary>
        public static bool Contains(int dx, int dy, int radius)
        {
            return dx * dx + dy * dy <= radius * radius;
        }

        /// <summary>相对圆心的格 (dx, dy) 是否在轮廓上：圆内、且四邻里至少有一个在圆外。</summary>
        public static bool IsOutline(int dx, int dy, int radius)
        {
            if (!Contains(dx, dy, radius))
            {
                return false;
            }

            return !Contains(dx - 1, dy, radius)
                   || !Contains(dx + 1, dy, radius)
                   || !Contains(dx, dy - 1, radius)
                   || !Contains(dx, dy + 1, radius);
        }

        /// <summary>
        /// 枚举半径 <paramref name="radius"/> 的轮廓格（自下而上、每行自左向右；同一格只出现一次），
        /// 圆心在 (centerX, centerY)。<paramref name="destination"/> 由调用方负责清空；
        /// <paramref name="radius"/> &lt; 1 时什么都不追加。
        /// </summary>
        public static void Collect(int centerX, int centerY, int radius, List<Vector2Int> destination)
        {
            if (destination == null || radius < 1)
            {
                return;
            }

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (IsOutline(dx, dy, radius))
                    {
                        destination.Add(new Vector2Int(centerX + dx, centerY + dy));
                    }
                }
            }
        }

        /// <summary>
        /// 把半径 <paramref name="radius"/> 的轮廓画进边长 <c>2 * capacity + 1</c> 的方形像素缓冲
        /// （行主序、原点左下，与 <see cref="Texture2D.SetPixels32"/> 的约定一致）：
        /// 轮廓格写 <see cref="OutlineColor"/>，其余一律写 <see cref="EmptyColor"/>。
        /// 环心落在缓冲中心的 texel <c>(capacity, capacity)</c>。
        /// </summary>
        /// <remarks>
        /// 整块缓冲**逐像素覆写**（而不是只改环上那几格）：同一块缓冲换半径复用，
        /// 不清干净就会留下上一圈轮廓的残影。缓冲边长固定 2 * capacity + 1，
        /// 因此 <paramref name="radius"/> ≤ <paramref name="capacity"/> 时环会居中留出透明边距。
        /// </remarks>
        public static void Fill(Color32[] pixels, int capacity, int radius)
        {
            if (pixels == null || capacity < 0)
            {
                return;
            }

            int size = capacity * 2 + 1;
            if (pixels.Length < size * size)
            {
                return;
            }

            for (int ty = 0; ty < size; ty++)
            {
                int dy = ty - capacity;
                int row = ty * size;
                for (int tx = 0; tx < size; tx++)
                {
                    pixels[row + tx] = IsOutline(tx - capacity, dy, radius) ? OutlineColor : EmptyColor;
                }
            }
        }
    }
}
