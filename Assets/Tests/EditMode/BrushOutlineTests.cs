using System.Collections.Generic;
using NUnit.Framework;
using TapTap2026.Rendering;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 笔刷轮廓（<see cref="BrushOutline"/>）的几何与像素落点：环必须闭合、八向对称、圆心不在环上，
    /// 小半径下的形状与「一圈像素画圆」的视觉一致，且 <see cref="BrushOutline.Fill"/> 写出的像素
    /// 恰好是环上的格（换半径时不留上一圈的残影）。
    /// </summary>
    /// <remarks>
    /// 这些就是编辑器白色笔刷圈的全部内容：环上断一格会看成虚线、多一格会指到左键画不到的格；
    /// 纹理的行主序 / 环心 texel 换算错一位，圈就会整体偏半格（与像素场景错开），因此用像素落点钉住它。
    /// 测试全部按格子空间（相对圆心的 dx / dy）断言，不依赖相机、场景或任何渲染对象。
    /// </remarks>
    public sealed class BrushOutlineTests
    {
        /// <summary>半径上限（与编辑器笔刷半径上限一致）。</summary>
        private const int MaxRadius = 10;

        /// <summary>「像素画圆」的环是闭合的：环上任取一格，八邻内必定另有环上的格（不断成孤点 / 虚线）。</summary>
        [Test]
        public void Outline_FormsAClosedRing()
        {
            for (int radius = 1; radius <= MaxRadius; radius++)
            {
                List<Vector2Int> cells = Collect(radius);
                var ring = new HashSet<Vector2Int>(cells);

                Assert.AreEqual(cells.Count, ring.Count, $"半径 {radius} 的轮廓出现了重复格");

                for (int i = 0; i < cells.Count; i++)
                {
                    Vector2Int cell = cells[i];
                    bool connected = false;
                    for (int dy = -1; dy <= 1 && !connected; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if ((dx != 0 || dy != 0) && ring.Contains(new Vector2Int(cell.x + dx, cell.y + dy)))
                            {
                                connected = true;
                                break;
                            }
                        }
                    }

                    Assert.IsTrue(connected, $"半径 {radius} 的轮廓在 {cell} 处断开");
                }
            }
        }

        /// <summary>环八向对称（含对角交换）：圆的四个象限与两条对角线看到的必须是同一圈。</summary>
        [Test]
        public void Outline_IsEightFoldSymmetric()
        {
            for (int radius = 1; radius <= MaxRadius; radius++)
            {
                List<Vector2Int> cells = Collect(radius);
                var ring = new HashSet<Vector2Int>(cells);

                for (int i = 0; i < cells.Count; i++)
                {
                    Vector2Int cell = cells[i];
                    Assert.IsTrue(ring.Contains(new Vector2Int(-cell.x, cell.y)), $"半径 {radius}：{cell} 的左右镜像不在环上");
                    Assert.IsTrue(ring.Contains(new Vector2Int(cell.x, -cell.y)), $"半径 {radius}：{cell} 的上下镜像不在环上");
                    Assert.IsTrue(ring.Contains(new Vector2Int(-cell.x, -cell.y)), $"半径 {radius}：{cell} 的中心对称格不在环上");
                    Assert.IsTrue(ring.Contains(new Vector2Int(cell.y, cell.x)), $"半径 {radius}：{cell} 的对角交换格不在环上");
                }
            }
        }

        /// <summary>环是**环**不是盘：圆心的那一格不属于轮廓（白色实心盘会把圈里的粒子全糊住）。</summary>
        [Test]
        public void Outline_ExcludesTheCenterCell()
        {
            for (int radius = 1; radius <= MaxRadius; radius++)
            {
                Assert.IsFalse(BrushOutline.IsOutline(0, 0, radius), $"半径 {radius} 的轮廓不该包含圆心");
            }
        }

        /// <summary>最小两档笔刷的形状：r = 1 是十字环（4 格）、r = 2 是菱形环（8 格）——玩家最常看到的两圈。</summary>
        [Test]
        public void Outline_MatchesTheSmallestBrushShapes()
        {
            AssertRing(1, new[]
            {
                new Vector2Int(0, 1),
                new Vector2Int(1, 0),
                new Vector2Int(0, -1),
                new Vector2Int(-1, 0),
            });

            AssertRing(2, new[]
            {
                new Vector2Int(0, 2),
                new Vector2Int(1, 1),
                new Vector2Int(2, 0),
                new Vector2Int(1, -1),
                new Vector2Int(0, -2),
                new Vector2Int(-1, -1),
                new Vector2Int(-2, 0),
                new Vector2Int(-1, 1),
            });
        }

        /// <summary>
        /// 纹理落点：缓冲边长 <c>2 * capacity + 1</c> 时，环心 texel 在 <c>(capacity, capacity)</c>——
        /// 环上的格写白、其余（含留出的透明边距）写全透明。
        /// </summary>
        [Test]
        public void Fill_PaintsExactlyTheOutlineCells()
        {
            const int capacity = 10;
            const int radius = 4;
            int size = capacity * 2 + 1;

            var pixels = new Color32[size * size];
            BrushOutline.Fill(pixels, capacity, radius);

            AssertPixelsMatchRing(pixels, capacity, radius);
        }

        /// <summary>同一块缓冲换半径复用：上一圈轮廓的像素必须被清掉，不能留下两个圆叠在一起。</summary>
        [Test]
        public void Fill_ClearsThePixelsLeftFromAPreviousRadius()
        {
            const int capacity = 6;
            int size = capacity * 2 + 1;

            var pixels = new Color32[size * size];
            BrushOutline.Fill(pixels, capacity, capacity);
            BrushOutline.Fill(pixels, capacity, 2);

            AssertPixelsMatchRing(pixels, capacity, 2);
        }

        /// <summary>半径为 <paramref name="radius"/> 的轮廓格（圆心在原点，自下而上、每行自左向右）。</summary>
        private static List<Vector2Int> Collect(int radius)
        {
            var cells = new List<Vector2Int>();
            BrushOutline.Collect(0, 0, radius, cells);
            return cells;
        }

        /// <summary>断言 <paramref name="radius"/> 的轮廓格集合与 <paramref name="expected"/> 完全一致。</summary>
        private static void AssertRing(int radius, Vector2Int[] expected)
        {
            List<Vector2Int> cells = Collect(radius);
            var ring = new HashSet<Vector2Int>(cells);
            var want = new HashSet<Vector2Int>(expected);

            Assert.AreEqual(want.Count, ring.Count, $"半径 {radius} 的轮廓格数不对");
            foreach (Vector2Int cell in want)
            {
                Assert.IsTrue(ring.Contains(cell), $"半径 {radius} 的轮廓缺少 {cell}");
            }
        }

        /// <summary>断言缓冲里只有轮廓格是白的，其余一律全透明。</summary>
        private static void AssertPixelsMatchRing(Color32[] pixels, int capacity, int radius)
        {
            int size = capacity * 2 + 1;
            var ring = new HashSet<Vector2Int>(Collect(radius));

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var cell = new Vector2Int(x - capacity, y - capacity);
                    Color32 pixel = pixels[y * size + x];

                    if (ring.Contains(cell))
                    {
                        Assert.AreEqual(BrushOutline.OutlineColor, pixel, $"半径 {radius}：{cell} 应在轮廓上");
                    }
                    else
                    {
                        Assert.AreEqual(BrushOutline.EmptyColor, pixel, $"半径 {radius}：{cell} 不该被画出来");
                    }
                }
            }
        }
    }
}
