using System;
using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// **全工程唯一的粒子类型清单**。新增一个类型只需要在这里追加一行。
    /// </summary>
    /// <remarks>
    /// 注册顺序即 typeId 顺序，且是存档兼容性的一部分：只能追加，不能插入或重排。
    /// 注册走 <see cref="ParticleRegistry.RegisterBatch"/>，因此「全部成功才回填 ParticleIds」，
    /// 任一类型配置非法都会在启动期抛异常并保持注册表原样，不会静默跑成半成品。
    /// </remarks>
    public static class ParticleRegistration
    {
        private static bool _registered;
        private static Exception _failure;

        /// <summary>
        /// 注册全部类型并回填 <see cref="ParticleIds"/>。幂等：重复调用只有第一次生效。
        /// </summary>
        /// <exception cref="InvalidOperationException">任一类型非法；且后续每次调用都会继续抛出，不会静默降级。</exception>
        public static void RegisterAll()
        {
            if (_registered)
            {
                return;
            }

            if (_failure != null)
            {
                // 上次启动期注册失败：继续抛出。绝不允许「部分注册 + ParticleIds 仍是 0(空气)」的静默状态。
                throw new InvalidOperationException(
                    "[ParticleRegistration] 上次注册失败，粒子类型未完成注册。", _failure);
            }

            try
            {
                // ========================= 类型清单 =========================
                ushort[] ids = ParticleRegistry.RegisterBatch(
                    new StoneParticle(),
                    new SandParticle(),
                    new SoilParticle(),
                    new WaterParticle(),
                    new AtmosphereParticle(),
                    new WetSoilParticle(),
                    new GrassSeedParticle(),
                    new GrassParticle(),
                    new HumusParticle(),
                    new HumusSoilParticle(),
                    new AquaticSeedParticle(),
                    new AquaticPlantParticle(),
                    new FireParticle());
                // ===========================================================

                ParticleIds.Stone = ids[0];
                ParticleIds.Sand = ids[1];
                ParticleIds.Soil = ids[2];
                ParticleIds.Water = ids[3];
                ParticleIds.Atmosphere = ids[4];
                ParticleIds.WetSoil = ids[5];
                ParticleIds.GrassSeed = ids[6];
                ParticleIds.Grass = ids[7];
                ParticleIds.Humus = ids[8];
                ParticleIds.HumusSoil = ids[9];
                ParticleIds.AquaticSeed = ids[10];
                ParticleIds.AquaticPlant = ids[11];
                ParticleIds.Fire = ids[12];
                _registered = true;
            }
            catch (Exception exception)
            {
                _failure = exception;
                throw;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            RegisterAll();
        }
    }
}
