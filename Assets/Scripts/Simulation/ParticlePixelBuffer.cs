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

        /// <summary>云滴的染色（凝结物浓度 0..1 在背景色与该色之间插值）。</summary>
        private static readonly Color32 CloudColor = new Color32(244, 248, 252, 255);

        /// <summary>
        /// 描边调暗系数：带 <see cref="ParticleTypeInfo.HasOutline"/> 的格，四邻中有「显示为背景」的格
        /// （空气 / 透明类型，越界视同空气）或带 Liquid 标签的格（固液交界）时，基底色乘上它。纯表现层，无逐类型配置。
        /// </summary>
        public const float OutlineFactor = 0.55f;

        /// <summary>
        /// 液面高光色：带 <see cref="ParticleTypeInfo.HasSurfaceHighlight"/> 的格、正上方不是液体时，
        /// 基底色向它插值（<see cref="SurfaceHighlightFactor"/>）。非常浅的淡蓝白。纯表现层，无逐类型配置。
        /// </summary>
        public static readonly Color32 SurfaceHighlightColor = new Color32(200, 228, 245, 255);

        /// <summary>液面高光的插值比例（0 = 原样，1 = 纯高光色）。</summary>
        public const float SurfaceHighlightFactor = 0.6f;

        /// <summary>
        /// 按 typeId 查调色板、按 shade 取色，一次性填满缓冲。
        /// **湿度足够高的空气（云）**再按湿度在底色与 <see cref="CloudColor"/> 之间插值——
        /// 云不是一种粒子，而是"很湿的空气"，因此它的显示由湿度场给出（见 <see cref="HumidityField"/>）。
        /// 若 <see cref="HeatMap.DebugHeatOverlay"/> 打开，再按温度叠一层热力色（越热越红）。
        /// </summary>
        /// <param name="world">源网格。</param>
        /// <param name="pixels">目标缓冲，长度必须等于 <see cref="ParticleWorld.CellCount"/>。</param>
        /// <param name="background">空气格的显示颜色。</param>
        public static void Fill(ParticleWorld world, Color32[] pixels, Color32 background)
        {
            Fill(world, pixels, background, 1f);
        }

        /// <summary>
        /// 同上，并额外把**画在天空之上的内容**（粒子、云）按 <paramref name="brightness"/> 缩放明暗——
        /// 整场亮度调节，白天上限、夜晚下限，由调用方从 <see cref="LightController.Daylight"/> 映射得到。
        /// </summary>
        /// <param name="brightness">亮度倍率，夹在 [0, 1]；1 = 原样。</param>
        /// <remarks>
        /// **不缩 <paramref name="background"/>**：天空色由调用方按昼夜直接给定（夜晚本身就是深色），
        /// 再乘一遍亮度就不是作者指定的那个颜色了。云与粒子画在天空之上，照常参与明暗。
        /// </remarks>
        public static void Fill(ParticleWorld world, Color32[] pixels, Color32 background, float brightness)
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
            HumidityField humidity = world.Humidity;

            // 调试热力叠加：越热越红。关闭（或满量程非法）时只多一次判断，不改变任何像素。
            float inverseScale = heat.DebugFullScaleTemperature > 0f
                ? 1f / heat.DebugFullScaleTemperature
                : 0f;
            bool overlay = heat.DebugHeatOverlay && inverseScale > 0f;

            bool dim = brightness < 1f;
            if (dim && brightness < 0f)
            {
                brightness = 0f;
            }

            // 云：湿度从 VisibleThreshold 到 CloudFullHumidity 之间淡入到纯白（RainThreshold 是降雨阈值，与渲染无关）。
            // 低于阈值完全不显示——否则近海面那种常年偏湿的空气会给整片天空蒙一层灰。
            float visibleSpan = HumidityField.CloudFullHumidity - HumidityField.VisibleThreshold;

            int width = world.Width;
            int height = world.Height;
            int x = 0;
            int y = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                Color32 color = BaseColor(cells[i], types, background, out bool painted);

                // 描边：带 HasOutline 的格，四邻中有「显示为背景」的格（含越界）或是液体（固液交界）时
                // 按 OutlineFactor 调暗。在云/热叠加之前做——描边是基底色的一部分，热力染色照常能盖上去；
                // painted 恒为真（底色已画出实物），描边格因此照常参与昼夜变暗。
                // 液面高光：带 HasSurfaceHighlight 的格，正上方无实物（液面）时向浅蓝白插值（同一轮判定，不另开 pass）。
                if (painted)
                {
                    ushort id = cells[i].typeId;
                    if (id < types.Length)
                    {
                        ParticleType type = types[id];
                        if (type != null)
                        {
                            ParticleTypeInfo info = type.Info;
                            if (info.HasOutline
                                && NeighbourTriggersOutline(cells, types, width, height, x, y))
                            {
                                color = new Color32(
                                    (byte)(color.r * OutlineFactor),
                                    (byte)(color.g * OutlineFactor),
                                    (byte)(color.b * OutlineFactor),
                                    color.a);
                            }
                            else if (info.HasSurfaceHighlight
                                && IsOpenSurface(cells, types, width, height, x, y))
                            {
                                color = Color32.Lerp(color, SurfaceHighlightColor, SurfaceHighlightFactor);
                            }
                        }
                    }
                }

                float cloud = (humidity.GetAt(i) - HumidityField.VisibleThreshold) / visibleSpan;
                if (cloud > 0f)
                {
                    color = Color32.Lerp(color, CloudColor, cloud >= 1f ? 1f : cloud);
                    painted = true;
                }

                if (overlay)
                {
                    float t = heat.GetTemperatureAt(i) * inverseScale;
                    if (t > 0f)
                    {
                        color = Color32.Lerp(color, HotColor, t >= 1f ? 1f : t);
                    }
                }

                // 只有真正画了东西（粒子 / 云）的像素才随昼夜变暗；裸天空走调用方给的颜色。
                if (dim && painted)
                {
                    color = new Color32(
                        (byte)(color.r * brightness),
                        (byte)(color.g * brightness),
                        (byte)(color.b * brightness),
                        color.a);
                }

                pixels[i] = color;

                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
        }

        /// <summary>描边判据：四邻（上下左右）中是否至少有一格触发描边（<see cref="TriggersOutline"/>）。</summary>
        private static bool NeighbourTriggersOutline(Cell[] cells, ParticleType[] types, int width, int height, int x, int y)
        {
            return TriggersOutline(cells, types, width, height, x - 1, y)
                || TriggersOutline(cells, types, width, height, x + 1, y)
                || TriggersOutline(cells, types, width, height, x, y - 1)
                || TriggersOutline(cells, types, width, height, x, y + 1);
        }

        /// <summary>
        /// 该格是否触发邻格的描边：「显示为背景」（与 <see cref="BaseColor"/> 同一套语义——空气 / 未注册 /
        /// 空槽位 / 透明调色板，越界视同空气）或**带 Liquid 标签**（固液交界也描边）。
        /// </summary>
        private static bool TriggersOutline(Cell[] cells, ParticleType[] types, int width, int height, int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                return true;
            }

            Cell cell = cells[y * width + x];
            ushort id = cell.typeId;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return true;
            }

            ParticleType type = types[id];
            if (type == null)
            {
                return true;
            }

            ParticleTypeInfo info = type.Info;
            if ((info.Tags & ParticleTags.Liquid) != 0)
            {
                return true;
            }

            Color32[] palette = info.Palette;
            int shade = cell.shade;
            return (shade < palette.Length ? palette[shade] : palette[0]).a == 0;
        }

        /// <summary>
        /// 液面判据：本格正上方**不是实物**（非 Solid 非 Liquid）= 液面——空气 / 气体（含火）算液面，
        /// 被固体盖住或被另一格液体压住都不算；越界顶行算（水漫到顶也有液面）。
        /// 只被带 <see cref="ParticleTypeInfo.HasSurfaceHighlight"/> 的类型使用。
        /// </summary>
        private static bool IsOpenSurface(Cell[] cells, ParticleType[] types, int width, int height, int x, int y)
        {
            int ny = y + 1;
            if (ny >= height)
            {
                return true;
            }

            Cell above = cells[ny * width + x];
            ushort id = above.typeId;
            if (id == ParticleRegistry.AirTypeId || id >= types.Length)
            {
                return true;
            }

            ParticleType type = types[id];
            return type == null || (type.Info.Tags & (ParticleTags.Solid | ParticleTags.Liquid)) == 0;
        }

        /// <summary>单格的静态颜色：空气 / 未注册 id / 空槽位 → 背景色，否则查该类型调色板。
        /// 调色板颜色 alpha == 0 视为「透明粒子」→ 也显示背景色（运行期材质不做 alpha 混合，
        /// 透明只能在映射层表达；热力叠加仍会在此基础上把热格染红——隐形大气因此能当风的可视化层）。
        /// <paramref name="painted"/> 为 true 表示这一格画了实物（后面要参与昼夜变暗）。</summary>
        private static Color32 BaseColor(Cell cell, ParticleType[] types, Color32 background, out bool painted)
        {
            painted = false;

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
            Color32 color = shade < palette.Length ? palette[shade] : palette[0];
            if (color.a == 0)
            {
                return background;
            }

            painted = true;
            return color;
        }
    }
}
