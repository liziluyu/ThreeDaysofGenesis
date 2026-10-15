using System.IO;
using NUnit.Framework;
using TapTap2026.Rendering;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 实体朝向渲染（<see cref="CreatureRenderer.ResolveFlipX"/> / <see cref="CreatureRenderer.SpriteFacesLeft"/>）的测试：
    /// 映射表的四个组合 + <c>facing == 0</c>，以及**直接读盘核对美术约定**——兔 / 鱼的源 sprite 都是头朝左，
    /// 所以默认配置必须是「头朝左」，否则朝右移动时翻转方向会反。
    /// </summary>
    /// <remarks>
    /// 美术回归测试不依赖 Unity 的导入设置：用 <see cref="File.ReadAllBytes"/> 读原始 PNG 字节，再
    /// <see cref="Texture2D.LoadImage"/> 解码（这条路径不需要资源开 Read/Write，也就不受 Inspector 导入选项影响）。
    /// 若换了美术，本测试会失败并提示同步改 <c>CreatureRenderer._spriteFacesLeft</c>。
    /// </remarks>
    public sealed class CreatureSpriteFacingTests
    {
        /// <summary>单帧边长（源像素）：三物种的 PNG 都是 16×16 切片。</summary>
        private const int FrameSize = 16;

        /// <summary>兔子美术相对 <see cref="Application.dataPath"/> 的路径。</summary>
        private const string RabbitSpriteFile = "/Images/Animals/rabbit/1/rabbit.png";

        /// <summary>鱼美术相对 <see cref="Application.dataPath"/> 的路径。</summary>
        private const string FishSpriteFile = "/Images/Animals/fish/1/fish.png";

        /// <summary>鸟美术相对 <see cref="Application.dataPath"/> 的路径（64×32 = 4 列 × 2 行，7 片有名切片 + 下排第 4 格空位）。</summary>
        private const string BirdSpriteFile = "/Images/Animals/bird/1/bird.png";

        /// <summary>默认值测试现建现销的宿主，避免把临时物体留在场景里。</summary>
        private GameObject _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                Object.DestroyImmediate(_host);
                _host = null;
            }
        }

        /// <summary>美术头朝左时：朝右移动（+1）才镜像，朝左（−1）保持美术原样。</summary>
        [Test]
        public void ResolveFlipX_MirrorsRightwardMovementWhenTheArtFacesLeft()
        {
            Assert.IsTrue(CreatureRenderer.ResolveFlipX(1, true));
            Assert.IsFalse(CreatureRenderer.ResolveFlipX(-1, true));
        }

        /// <summary>美术头朝右时正好相反：朝左移动（−1）才镜像，朝右（+1）保持原样。</summary>
        [Test]
        public void ResolveFlipX_MirrorsLeftwardMovementWhenTheArtFacesRight()
        {
            Assert.IsFalse(CreatureRenderer.ResolveFlipX(1, false));
            Assert.IsTrue(CreatureRenderer.ResolveFlipX(-1, false));
        }

        /// <summary>朝向未定（0，静止）时不翻转，两种情况都保持美术原样。</summary>
        [Test]
        public void ResolveFlipX_KeepsTheArtAsIsWhenFacingIsZero()
        {
            Assert.IsFalse(CreatureRenderer.ResolveFlipX(0, true));
            Assert.IsFalse(CreatureRenderer.ResolveFlipX(0, false));
        }

        /// <summary>美术约定回归：兔子每一帧的眼睛都落在不透明包围盒的左半（头朝左）。</summary>
        [Test]
        public void RabbitArt_FacesLeftInEveryFrame()
        {
            AssertEveryFrameFacesLeft(RabbitSpriteFile, columns: 4, rows: 1, expectedFrames: 4);
        }

        /// <summary>美术约定回归：鱼每一帧的眼睛都落在不透明包围盒的左半（头朝左）。</summary>
        [Test]
        public void FishArt_FacesLeftInEveryFrame()
        {
            AssertEveryFrameFacesLeft(FishSpriteFile, columns: 4, rows: 1, expectedFrames: 4);
        }

        /// <summary>
        /// 美术约定回归：鸟每一帧的眼睛都落在不透明包围盒的左半（头朝左）。
        /// 鸟 sheet 是 64×32（4 列 × 2 行，共 8 格），其中下排第 4 格（无切片名的空位）为空像素；
        /// 7 片有名切片 bird_0..bird_6 全部有内容——<paramref name="expectedFrames"/> 传 7 顺带把「有内容格数」钉住。
        /// 结论：鸟与兔 / 鱼同向（头朝左），因此 <c>_spriteFacesLeft</c> 不需要按物种区分。
        /// </summary>
        [Test]
        public void BirdArt_FacesLeftInEveryFrame()
        {
            AssertEveryFrameFacesLeft(BirdSpriteFile, columns: 4, rows: 2, expectedFrames: 7);
        }

        // 原 FishArtHeight_MatchesBrainClearance（美术↔常量同步）已删除：其漂移守卫职责由
        // CreatureArtDataTests 接替（现场从 PNG 复算 CreatureArtData 并对拍）。

        /// <summary>默认配置与美术约定一致：头朝左 ⇒ <see cref="CreatureRenderer.SpriteFacesLeft"/> 默认 true。</summary>
        [Test]
        public void SpriteFacesLeft_DefaultsToTrue()
        {
            _host = new GameObject("CreatureSpriteFacingTests");
            CreatureRenderer renderer = _host.AddComponent<CreatureRenderer>();

            Assert.IsTrue(
                renderer.SpriteFacesLeft,
                "兔 / 鱼源 sprite 都头朝左，缺省配置必须也是头朝左（true）；否则朝右移动会被画成头朝左。");
        }

        /// <summary>读盘解码某物种的 PNG，逐格断言「美术头朝左」；缺文件 / 解码失败即失败并报出路径。
        /// 空白格（无任何不透明像素）跳过，并断言有内容的格数恰为 <paramref name="expectedFrames"/>。</summary>
        private static void AssertEveryFrameFacesLeft(string fileName, int columns, int rows, int expectedFrames)
        {
            string path = Application.dataPath + fileName;
            if (!File.Exists(path))
            {
                Assert.Fail("美术资源缺失，无法核对美术朝向：" + path);
            }

            var texture = new Texture2D(2, 2);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    Assert.Fail("PNG 解码失败（LoadImage 返回 false）：" + path);
                }

                Assert.AreEqual(rows * FrameSize, texture.height, "纹理高度不是行数的整数倍：" + path);
                Assert.AreEqual(0, texture.width % FrameSize, "纹理宽度不是帧宽的整数倍：" + path);
                Assert.AreEqual(columns, texture.width / FrameSize, "列数与预期不符：" + path);

                // GetPixels32 以左下为原点；判据只看 x，竖直顺序不影响。
                Color32[] pixels = texture.GetPixels32();
                int nonEmpty = 0;
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        // GetPixels32 行优先、自底向上：格 (column, row) 的原点 = (column·16, row·16)。
                        if (!TryFrameFacesLeft(pixels, texture.width, column * FrameSize, row * FrameSize, out bool facesLeft))
                        {
                            continue; // 空切片（如鸟下排第 4 格）：没有不透明像素，无从判断朝向。
                        }

                        nonEmpty++;
                        Assert.IsTrue(
                            facesLeft,
                            "格（列 " + column + " 行 " + row + "）的眼睛落在不透明包围盒的右半，"
                            + "美术并非头朝左：" + path
                            + "（若换了美术，请同步改 CreatureRenderer._spriteFacesLeft）。");
                    }
                }

                Assert.AreEqual(expectedFrames, nonEmpty, "有内容的切片数与预期不符：" + path);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// 判断某一格的美术是否头朝左：取该格（原点 <paramref name="originX"/> / <paramref name="originY"/>，
        /// <see cref="GetPixels32"/> 的自底向上像素空间）不透明像素里最暗的一个（眼睛），
        /// 若它离不透明包围盒左边界比离右边界近，即视为头朝左（当前兔 / 鱼 / 鸟的判据，已实测三物种全部有向帧一致）。
        /// 该格没有任何不透明像素时返回 false（空切片，调用方跳过）。
        /// </summary>
        private static bool TryFrameFacesLeft(
            Color32[] pixels, int width, int originX, int originY, out bool facesLeft)
        {
            facesLeft = false;
            int frameLeft = originX;
            int frameRight = originX + FrameSize;
            int frameBottom = originY;
            int frameTop = originY + FrameSize;

            int minX = int.MaxValue;
            int maxX = int.MinValue;
            int eyeX = -1;
            int eyeLuma = int.MaxValue;

            for (int y = frameBottom; y < frameTop; y++)
            {
                int row = y * width;
                for (int x = frameLeft; x < frameRight; x++)
                {
                    Color32 pixel = pixels[row + x];
                    if (pixel.a == 0)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    int luma = pixel.r + pixel.g + pixel.b;
                    if (luma < eyeLuma)
                    {
                        eyeLuma = luma;
                        eyeX = x;
                    }
                }
            }

            if (eyeX < 0)
            {
                return false;
            }

            facesLeft = eyeX - minX < maxX - eyeX;
            return true;
        }
    }
}
