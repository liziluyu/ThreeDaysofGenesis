using System;
using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 全局静态类型注册表。启动时一次性填充，之后只读，单线程访问。
    /// 运行期寻址方式只有一个：<c>ParticleRegistry.Types[typeId]</c>——数组下标，无字典、无字符串。
    /// </summary>
    public static class ParticleRegistry
    {
        /// <summary>空气的 typeId，保留值，永不注册行为。</summary>
        public const ushort AirTypeId = 0;

        /// <summary>最多可注册的类型数（id 1..65534）。</summary>
        public const int MaxTypeCount = ushort.MaxValue - 1;

        /// <summary>按 typeId 直接寻址。<c>Types[0]</c> 恒为 <c>null</c>（空气）。</summary>
        private static ParticleType[] _types = new ParticleType[1];

        private static int _count;

        /// <summary>已注册的类型数量（不含空气）。</summary>
        public static int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// 类型表。<c>Types[0] == null</c>；<c>Types[id]</c>（1 &lt;= id &lt;= Count）为注册条目。
        /// 数组容量可能大于 Count + 1，超出部分是 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// <see cref="ParticleWorld"/> 在构造时抓取这个引用。因此注册必须在创建任何世界之前完成，
        /// 由 <see cref="ParticleRegistration"/> 的启动钩子保证。
        /// </remarks>
        public static ParticleType[] Types
        {
            get { return _types; }
        }

        /// <summary>
        /// 注册一个粒子类型。**只能在启动阶段调用**，返回分配到的 typeId（= 注册顺序，从 1 开始）。
        /// 校验失败会抛异常而不写入任何状态，因此不会留下半注册的注册表。
        /// </summary>
        public static ushort Register(IParticleType behaviour)
        {
            if (behaviour == null)
            {
                throw new ArgumentNullException(nameof(behaviour));
            }

            ParticleTypeInfo info = behaviour.Info;
            ValidateInfo(behaviour, info);

            if (_count >= MaxTypeCount)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] 类型数量已达上限 {MaxTypeCount}，无法注册 {Describe(behaviour, info)}。");
            }

            ushort id = (ushort)(_count + 1);
            var type = new ParticleType(id, info, behaviour.OnUpdate, behaviour.OnSpawn, behaviour.OnKill);

            EnsureCapacity(id);
            _types[id] = type;
            _count = id;
            return id;
        }

        private static void ValidateInfo(IParticleType behaviour, ParticleTypeInfo info)
        {
            if (info.Palette == null || info.Palette.Length == 0 || info.Palette.Length > 256)
            {
                string length = info.Palette == null ? "null" : info.Palette.Length.ToString();
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {Describe(behaviour, info)} 的调色板长度必须在 1..256，实际为 {length}。");
            }

            if (float.IsNaN(info.Density) || float.IsInfinity(info.Density))
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {Describe(behaviour, info)} 的 Density 必须是有限值，实际为 {info.Density}。");
            }
        }

        private static string Describe(IParticleType behaviour, ParticleTypeInfo info)
        {
            return string.IsNullOrEmpty(info.DebugName) ? behaviour.GetType().Name : info.DebugName;
        }

        /// <summary>保证 <paramref name="requiredIndex"/> 是合法下标。</summary>
        private static void EnsureCapacity(int requiredIndex)
        {
            if (_types.Length > requiredIndex)
            {
                return;
            }

            int capacity = _types.Length;
            while (capacity <= requiredIndex)
            {
                capacity = capacity <= 1 ? 16 : capacity * 2;
            }

            Array.Resize(ref _types, capacity);
        }
    }
}
