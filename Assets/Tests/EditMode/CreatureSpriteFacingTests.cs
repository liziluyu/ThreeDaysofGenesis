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
        /// <summary>单帧边长（源像素）：两物种的 PNG 都是横向排列的 16×16 帧。</summary>
        private const int FrameSize = 16;

        /// <summary>两物种的帧数：64 / 16。</summary>
        private const int FrameCount = 4;

        /// <summary>兔子美术相对 <see cref="Application.dataPath"/> 的路径。</summary>
        private const string RabbitSpriteFile = "/Images/Animals/rabbit/1/rabbit.png";

        /// <summary>鱼美术相对 <see cref="Application.dataPath"/> 的路径。</summary>
        private const string FishSpriteFile = "/Images/Animals/fish/1/fish.png";

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
            AssertEveryFrameFacesLeft(RabbitSpriteFile);
        }

        /// <summary>美术约定回归：鱼每一帧的眼睛都落在不透明包围盒的左半（头朝左）。</summary>
        [Test]
        public void FishArt_FacesLeftInEveryFrame()
        {
            AssertEveryFrameFacesLeft(FishSpriteFile);
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

        /// <summary>读盘解码某物种的 PNG，逐帧断言「美术头朝左」；缺文件 / 解码失败即失败并报出路径。</summary>
        private static void AssertEveryFrameFacesLeft(string fileName)
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

                Assert.AreEqual(FrameSize, texture.height, "纹理高度不是单帧边长：" + path);
                Assert.AreEqual(0, texture.width % FrameSize, "纹理宽度不是帧宽的整数倍：" + path);
                Assert.AreEqual(FrameCount, texture.width / FrameSize, "帧数与预期不符：" + path);

                // GetPixels32 以左下为原点，但判据只看 x，竖直翻转不影响。
                Color32[] pixels = texture.GetPixels32();
                for (int frame = 0; frame < FrameCount; frame++)
                {
                    Assert.IsTrue(
                        FrameFacesLeft(pixels, texture.width, texture.height, frame),
                        "第 " + frame + " 帧的眼睛落在不透明包围盒的右半，美术并非头朝左：" + path +
                            "（若换了美术，请同步改 CreatureRenderer._spriteFacesLeft）。");
                }
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// 判断某一帧的美术是否头朝左：取该帧不透明像素里最暗的一个（眼睛），
        /// 若它离不透明包围盒左边界比离右边界近，即视为头朝左（当前兔 / 鱼的判据，已实测两物种 4 帧全一致）。
        /// </summary>
        private static bool FrameFacesLeft(Color32[] pixels, int width, int height, int frame)
        {
            int frameLeft = frame * FrameSize;
            int frameRight = frameLeft + FrameSize;

            int minX = int.MaxValue;
            int maxX = int.MinValue;
            int eyeX = -1;
            int eyeLuma = int.MaxValue;

            for (int y = 0; y < height; y++)
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

            Assert.IsTrue(eyeX >= 0, "第 " + frame + " 帧没有不透明像素，无法判断美术朝向。");
            return eyeX - minX < maxX - eyeX;
        }
    }
}
