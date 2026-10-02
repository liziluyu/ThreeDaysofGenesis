using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// **全工程唯一的粒子类型清单**。新增一个类型只需要在这里追加一行。
    /// </summary>
    /// <remarks>
    /// 注册顺序即 typeId 顺序，且是存档兼容性的一部分：只能追加，不能插入或重排。
    /// </remarks>
    public static class ParticleRegistration
    {
        private static bool _registered;

        /// <summary>
        /// 注册全部类型并回填 <see cref="ParticleIds"/>。幂等：重复调用只有第一次生效。
        /// </summary>
        public static void RegisterAll()
        {
            if (_registered)
            {
                return;
            }

            // 先占标志，避免注册中途抛异常后被重复调用而产生重复 id。
            _registered = true;

            // ========================= 类型清单 =========================
            // ParticleIds.Xxx = ParticleRegistry.Register(new XxxParticle());
            // ===========================================================
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            RegisterAll();
        }
    }
}
