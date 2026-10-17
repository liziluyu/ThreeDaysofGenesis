using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 项目自有 <see cref="RenderColor"/> 与 Unity <c>Color32</c> 之间的**唯一接缝**。
    /// 模拟侧（含 worker 光栅化）只认 <see cref="RenderColor"/>；只有两处需要转换，且都不在每格热路径上：
    /// <list type="number">
    /// <item><b>类型注册 / 建表时</b>：把 Unity 授权的 <see cref="ParticleTypeInfo.Palette"/> 镜像成纯值调色板
    /// （<see cref="FromPalette"/>，见 <see cref="ParticleType.RenderPalette"/>）；</item>
    /// <item><b>主线程上屏时</b>：把一帧纯值像素拷进复用的 <c>Color32[]</c> 上传缓冲
    /// （<see cref="CopyToColor32"/>，见 <c>ParticleRenderer.Present</c>）。</item>
    /// </list>
    /// 两个方向都是**逐字节恒等映射**（<c>r/g/b/a</c> 直接搬），不做任何色彩空间换算，
    /// 因此像素与阶段 1 的 Unity 调色板路径逐位一致。
    /// </remarks>
    public static class RenderColorConversion
    {
        /// <summary>Unity 颜色 → 纯值颜色（逐字节）。</summary>
        public static RenderColor FromColor32(Color32 color)
        {
            return new RenderColor(color.r, color.g, color.b, color.a);
        }

        /// <summary>纯值颜色 → Unity 颜色（逐字节）。上屏与编辑器预览用。</summary>
        public static Color32 ToColor32(RenderColor color)
        {
            return new Color32(color.r, color.g, color.b, color.a);
        }

        /// <summary>
        /// 镜像一条 Unity 调色板：长度与每个字节都原样复制，因此 <c>RenderPalette[shade]</c> 与
        /// <c>Info.Palette[shade]</c> 逐位相同。<paramref name="palette"/> 为 null 时返回 null。
        /// </summary>
        public static RenderColor[] FromPalette(Color32[] palette)
        {
            if (palette == null)
            {
                return null;
            }

            var mirrored = new RenderColor[palette.Length];
            for (int i = 0; i < palette.Length; i++)
            {
                mirrored[i] = FromColor32(palette[i]);
            }

            return mirrored;
        }

        /// <summary>
        /// 把一帧纯值像素整块拷进 <paramref name="destination"/>（主线程复用的上传缓冲）。
        /// 两个数组长度必须一致；本方法不分配（调用方保证缓冲预先按帧尺寸备好）。
        /// </summary>
        /// <exception cref="System.ArgumentException">长度不一致（上传缓冲没跟着帧尺寸重建）。</exception>
        public static void CopyToColor32(RenderColor[] source, Color32[] destination)
        {
            if (source.Length != destination.Length)
            {
                throw new System.ArgumentException(
                    $"上传缓冲长度 {destination.Length} 与像素帧长度 {source.Length} 不一致。", nameof(destination));
            }

            for (int i = 0; i < source.Length; i++)
            {
                RenderColor color = source[i];
                destination[i] = new Color32(color.r, color.g, color.b, color.a);
            }
        }
    }
}
