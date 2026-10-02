using System;
using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 网格 → 像素缓冲的 CPU 侧映射。纯数据转换，不认识 Unity 渲染对象，因此可以脱离引擎单独测试。
    /// 唯一真源始终是 <see cref="ParticleWorld.Cells"/>，本缓冲只是只读派生物。
    /// </summary>
    public static class ParticlePixelBuffer
    {
        /// <summary>按 typeId 查调色板、按 shade 取色，一次性填满缓冲。</summary>
        /// <param name="world">源网格。</param>
        /// <param name="pixels">目标缓冲，长度必须等于 <see cref="ParticleWorld.CellCount"/>。</param>
        /// <param name="background">空气格的显示颜色。</param>
        public static void Fill(ParticleWorld world, Color32[] pixels, Color32 background)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (pixels == null)
            {
                throw new ArgumentNullException(nameof(pixels));
            }

            Cell[] cells = world.Cells;
            if (pixels.Length != cells.Length)
            {
                throw new ArgumentException(
                    $"像素缓冲长度 {pixels.Length} 与网格格数 {cells.Length} 不一致。", nameof(pixels));
            }

            ParticleType[] types = world.Types;
            for (int i = 0; i < cells.Length; i++)
            {
                ushort id = cells[i].typeId;
                if (id == ParticleRegistry.AirTypeId || id >= types.Length)
                {
                    pixels[i] = background;
                    continue;
                }

                ParticleType type = types[id];
                if (type == null)
                {
                    pixels[i] = background;
                    continue;
                }

                Color32[] palette = type.Info.Palette;
                int shade = cells[i].shade;
                pixels[i] = shade < palette.Length ? palette[shade] : palette[0];
            }
        }
    }
}
