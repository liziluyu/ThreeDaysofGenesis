using System;
using System.Runtime.CompilerServices;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 项目自有的 **RGBA8 像素值**：纯 BCL 值类型，零 <c>UnityEngine</c> 依赖。
    /// 字段名与 Unity <c>Color32</c> 一致（<c>r</c>/<c>g</c>/<c>b</c>/<c>a</c>，各 0..255），逐字节等价——
    /// 但本类型**不认识**引擎，因此模拟 worker 可以在不触碰任何 Unity API 的前提下把世界光栅化成像素
    /// （见 <see cref="ParticlePixelBuffer"/>），只有主线程上屏那一步才转成 Unity <c>Color32</c>
    /// （唯一接缝见 <see cref="RenderColorConversion"/>）。
    /// </summary>
    /// <remarks>
    /// 与 Unity <c>Color32</c> 的关系是「同布局的镜像」而非包装：模拟侧读写的都是本类型，
    /// 转换只在两个边界发生（类型注册时的调色板镜像、上屏时的上传缓冲拷贝），因此光栅化热路径上
    /// 不出现任何 Unity 类型、也没有装箱 / 虚调用。
    /// </remarks>
    public struct RenderColor : IEquatable<RenderColor>
    {
        /// <summary>红分量。</summary>
        public byte r;

        /// <summary>绿分量。</summary>
        public byte g;

        /// <summary>蓝分量。</summary>
        public byte b;

        /// <summary>alpha 分量（光栅化只原样搬运，不做混合）。</summary>
        public byte a;

        public RenderColor(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        /// <summary>逐字段相等（与 <c>Color32.Equals</c> 同一口径：四个分量全等）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(RenderColor other)
        {
            return r == other.r && g == other.g && b == other.b && a == other.a;
        }

        public override bool Equals(object obj)
        {
            return obj is RenderColor other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        public override string ToString()
        {
            return "RGBA(" + r + ", " + g + ", " + b + ", " + a + ")";
        }

        public static bool operator ==(RenderColor left, RenderColor right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RenderColor left, RenderColor right)
        {
            return !left.Equals(right);
        }
    }
}
