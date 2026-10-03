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
        /// <summary>调试热力叠加的最热色（满量程时的纯红）。</summary>
        private static readonly Color32 HotColor = new Color32(255, 0, 0, 255);

        /// <summary>
        /// 按 typeId 查调色板、按 shade 取色，一次性填满缓冲。
        /// 若 <see cref="HeatMap.DebugHeatOverlay"/> 打开，再按温度叠一层热力色（越热越红）。
        /// </summary>
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
            HeatMap heat = world.Heat;

            // 调试热力叠加：越热越红。关闭（或满量程非法）时只多一次判断，不改变任何像素。
            float inverseScale = heat.DebugFullScaleTemperature > 0f
                ? 1f / heat.DebugFullScaleTemperature
                : 0f;
            bool overlay = heat.DebugHeatOverlay && inverseScale > 0f;

            for (int i = 0; i < cells.Length; i++)
            {
                Color32 color = BaseColor(cells[i], types, background);

                if (overlay)
                {
                    float t = heat.GetTemperatureAt(i) * inverseScale;
                    if (t > 0f)
                    {
                        color = Color32.Lerp(color, HotColor, t >= 1f ? 1f : t);
                    }
                }

                pixels[i] = color;
            }
        }

        /// <summary>单格的静态颜色：空气 / 未注册 id / 空槽位 → 背景色，否则查该类型调色板。</summary>
        private static Color32 BaseColor(Cell cell, ParticleType[] types, Color32 background)
        {
            ushort id = cell.typeId;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return background;
            }

            ParticleType type = types[id];
            if (type == null)
            {
                return background;
            }

            Color32[] palette = type.Info.Palette;
            int shade = cell.shade;
            return shade < palette.Length ? palette[shade] : palette[0];
        }
    }
}
