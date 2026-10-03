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
        /// 注册单个类型。校验失败会抛异常且不写入任何状态，因此单个调用本身是原子的。
        /// 注册多个类型请用 <see cref="RegisterBatch"/>，避免中途失败留下半注册表。
        /// </summary>
        public static ushort Register(IParticleType behaviour)
        {
            ParticleTypeInfo info = RequireValidInfo(behaviour);
            EnsureRoom(1);
            return Commit(behaviour, info);
        }

        /// <summary>
        /// 批量注册：**先全部校验、再统一写入**。任一类型非法则整体不生效，不会留下半注册的注册表。
        /// </summary>
        /// <returns>与入参一一对应的 typeId（按顺序分配）。</returns>
        /// <remarks>
        /// <see cref="ParticleRegistration"/> 依赖这个原子性：只有全部成功才回填 <see cref="ParticleIds"/>，
        /// 否则未回填的 <c>ParticleIds.Xxx</c> 会停留在默认值 0（= 空气），变成静默退化。
        /// </remarks>
        public static ushort[] RegisterBatch(params IParticleType[] behaviours)
        {
            if (behaviours == null)
            {
                throw new ArgumentNullException(nameof(behaviours));
            }

            var infos = new ParticleTypeInfo[behaviours.Length];
            for (int i = 0; i < behaviours.Length; i++)
            {
                infos[i] = RequireValidInfo(behaviours[i]);
            }

            EnsureRoom(behaviours.Length);

            var ids = new ushort[behaviours.Length];
            for (int i = 0; i < behaviours.Length; i++)
            {
                ids[i] = Commit(behaviours[i], infos[i]);
            }

            return ids;
        }

        /// <summary>
        /// 校验一份类型属性。注册流程与自定义类型表（<see cref="ParticleWorld"/> 的四参构造）共用本方法，
        /// 保证任何进入运行期的类型都经过同一套检查。
        /// </summary>
        /// <exception cref="InvalidOperationException">调色板长度不在 1..256、Density 非有限值、HeatCapacity 非有限正数、Friction 或 Stickiness 不在 [0,1]、或未带任何类别标签。</exception>
        public static void ValidateTypeInfo(ParticleTypeInfo info, string description)
        {
            string name = string.IsNullOrEmpty(description) ? "<未命名类型>" : description;

            if (info.Palette == null || info.Palette.Length == 0 || info.Palette.Length > 256)
            {
                string length = info.Palette == null ? "null" : info.Palette.Length.ToString();
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 的调色板长度必须在 1..256，实际为 {length}。");
            }

            if (float.IsNaN(info.Density) || float.IsInfinity(info.Density))
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 的 Density 必须是有限值，实际为 {info.Density}。");
            }

            if (float.IsNaN(info.HeatCapacity) || float.IsInfinity(info.HeatCapacity) || info.HeatCapacity <= 0f)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 的 HeatCapacity 必须是有限正数（温度 = 热量 / 热容），实际为 {info.HeatCapacity}。");
            }

            if (float.IsNaN(info.ThermalConductivity) || float.IsInfinity(info.ThermalConductivity) ||
                info.ThermalConductivity < 0f || info.ThermalConductivity > HeatMap.MaxThermalConductivity)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 的 ThermalConductivity 必须在 [0, {HeatMap.MaxThermalConductivity}] 内，实际为 {info.ThermalConductivity}。");
            }

            if (float.IsNaN(info.Friction) || float.IsInfinity(info.Friction) ||
                info.Friction < 0f || info.Friction > 1f)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 的 Friction 必须在 [0, 1] 内，实际为 {info.Friction}。");
            }

            if (float.IsNaN(info.Stickiness) || float.IsInfinity(info.Stickiness) ||
                info.Stickiness < 0f || info.Stickiness > 1f)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 的 Stickiness 必须在 [0, 1] 内，实际为 {info.Stickiness}。");
            }

            if (info.Tags == ParticleTags.None)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] {name} 必须至少带一个类别标签（Solid / Liquid / Gas）：" +
                    "None 的 rank 为 0，会被任何粒子置换、且置换不了任何东西，等同于空气。");
            }
        }

        private static ParticleTypeInfo RequireValidInfo(IParticleType behaviour)
        {
            if (behaviour == null)
            {
                throw new ArgumentNullException(nameof(behaviour));
            }

            ParticleTypeInfo info = behaviour.Info;
            ValidateTypeInfo(info, Describe(behaviour, info));
            return info;
        }

        private static void EnsureRoom(int additional)
        {
            if (_count + additional > MaxTypeCount)
            {
                throw new InvalidOperationException(
                    $"[ParticleRegistry] 类型数量将超过上限 {MaxTypeCount}（当前 {_count}，本次 +{additional}）。");
            }
        }

        private static ushort Commit(IParticleType behaviour, ParticleTypeInfo info)
        {
            ushort id = (ushort)(_count + 1);
            var type = new ParticleType(id, info, behaviour.OnUpdate, behaviour.OnSpawn, behaviour.OnKill);

            EnsureCapacity(id);
            _types[id] = type;
            _count = id;
            return id;
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
