using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>测试专用的重力颗粒。不属于游戏内容。</summary>
    internal sealed class TestGrainParticle : IParticleType
    {
        internal static readonly Color32[] Palette =
        {
            new Color32(10, 20, 30, 255),
            new Color32(40, 50, 60, 255),
            new Color32(70, 80, 90, 255),
        };

        internal int SpawnCount;
        internal int KillCount;
        internal ushort LastKillerId = ushort.MaxValue;
        internal ushort VictimIdSeenByKill = ushort.MaxValue;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(2f, true, false, 0, Palette, "TestGrainParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            ParticleRules.TryFall(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            SpawnCount++;
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            KillCount++;
            LastKillerId = killerTypeId;
            // OnKill 必须发生在写入新格之前：此刻网格上还应该是受害者本体。
            VictimIdSeenByKill = world.GetCell(x, y).typeId;
        }
    }

    /// <summary>测试专用的液体：下落 → 斜落 → 横流。</summary>
    internal sealed class TestLiquidParticle : IParticleType
    {
        internal static readonly Color32[] Palette =
        {
            new Color32(0, 0, 200, 255),
            new Color32(0, 0, 255, 255),
        };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(1f, true, false, 4, Palette, "TestLiquidParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            if (ParticleRules.TryFall(world, x, y))
            {
                return;
            }

            if (ParticleRules.TryDiagonalFall(world, x, y))
            {
                return;
            }

            ParticleRules.TryFlowHorizontal(world, x, y, Info.MaxFlowSpread);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>测试专用的静态类型：OnUpdate 永不应被调用。</summary>
    internal sealed class TestStaticParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(255, 255, 255, 255) };

        internal int UpdateCount;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(999f, false, true, 0, Palette, "TestStaticParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            UpdateCount++;
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>测试专用的「每 tick 向右走一格」粒子，用来验证每 tick 至多被处理一次。</summary>
    internal sealed class TestWalkerParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(255, 0, 0, 255) };

        internal int UpdateCount;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(1f, false, false, 0, Palette, "TestWalkerParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            UpdateCount++;
            if (world.IsAir(x + 1, y))
            {
                world.Swap(x, y, x + 1, y);
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>调色板非法的类型，用于验证注册校验不会留下半注册状态。</summary>
    internal sealed class TestBadPaletteParticle : IParticleType
    {
        internal static readonly Color32[] Palette = new Color32[0];

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(1f, false, false, 0, Palette, "TestBadPaletteParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>一次性把测试类型注册进全局注册表，幂等。</summary>
    internal static class TestTypes
    {
        internal static TestGrainParticle Grain;
        internal static TestLiquidParticle Liquid;
        internal static TestStaticParticle Static;
        internal static TestWalkerParticle Walker;

        internal static ushort GrainId;
        internal static ushort LiquidId;
        internal static ushort StaticId;
        internal static ushort WalkerId;

        private static bool _ready;

        internal static void EnsureRegistered()
        {
            if (_ready)
            {
                return;
            }

            ParticleRegistration.RegisterAll();

            Grain = new TestGrainParticle();
            Liquid = new TestLiquidParticle();
            Static = new TestStaticParticle();
            Walker = new TestWalkerParticle();

            GrainId = ParticleRegistry.Register(Grain);
            LiquidId = ParticleRegistry.Register(Liquid);
            StaticId = ParticleRegistry.Register(Static);
            WalkerId = ParticleRegistry.Register(Walker);

            _ready = true;
        }

        internal static void ResetCounters()
        {
            Grain.SpawnCount = 0;
            Grain.KillCount = 0;
            Grain.LastKillerId = ushort.MaxValue;
            Grain.VictimIdSeenByKill = ushort.MaxValue;
            Static.UpdateCount = 0;
            Walker.UpdateCount = 0;
        }
    }
}
