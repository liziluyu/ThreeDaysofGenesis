using System.Runtime.CompilerServices;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 光栅化用的**纯数学**（零 <c>UnityEngine</c>）：三个函数都是本工程此前所用 Unity 实现的**逐位复刻**，
    /// 不是「数学上等价」的近似——
    /// <list type="bullet">
    /// <item><see cref="Clamp01"/> ≡ <c>Mathf.Clamp01</c>；</item>
    /// <item><see cref="Lerp(float, float, float)"/> ≡ <c>Mathf.Lerp</c>（<c>a + (b − a) × Clamp01(t)</c>）；</item>
    /// <item><see cref="Lerp(in RenderColor, in RenderColor, float)"/> ≡ <c>Color32.Lerp</c>
    /// （先夹 <c>t</c>，再逐通道 <c>(byte)(a + (b − a) × t)</c>——**截断**而非四舍五入）。</item>
    /// </list>
    /// 之所以必须一比一复刻：阶段 2 的验收是「像素与阶段 1 的 Unity 路径逐位一致」，
    /// 而浮点表达式换个写法（如 <c>(1 − t) × a + t × b</c>）在边界上就会差 1 个色阶。
    /// 等价性由聚焦测试 <c>RenderRasterizerTests</c> 对 Unity 实现做扫描比对，不靠眼看。
    /// </remarks>
    public static class RenderColorMath
    {
        /// <summary><c>value</c> 夹到 [0, 1]（NaN 原样返回，与 <c>Mathf.Clamp01</c> 同）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        /// <summary>标量线性插值，<paramref name="t"/> 先夹到 [0, 1]（逐位等价于 <c>Mathf.Lerp</c>）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Clamp01(t);
        }

        /// <summary>
        /// 颜色线性插值，<paramref name="t"/> 先夹到 [0, 1]，逐通道**截断**取整（逐位等价于 <c>Color32.Lerp</c>）。
        /// </summary>
        public static RenderColor Lerp(in RenderColor a, in RenderColor b, float t)
        {
            float k = Clamp01(t);
            return new RenderColor(
                (byte)(a.r + (b.r - a.r) * k),
                (byte)(a.g + (b.g - a.g) * k),
                (byte)(a.b + (b.b - a.b) * k),
                (byte)(a.a + (b.a - a.a) * k));
        }

        /// <summary>
        /// RGB 各通道乘以 <paramref name="brightness"/>、alpha 原样——逐位等价于旧路径的
        /// <c>new Color32((byte)(c.r * brightness), …, c.a)</c>（浮点乘后截断）。
        /// </summary>
        public static RenderColor ScaleRgb(in RenderColor color, float brightness)
        {
            return new RenderColor(
                (byte)(color.r * brightness),
                (byte)(color.g * brightness),
                (byte)(color.b * brightness),
                color.a);
        }
    }
}
