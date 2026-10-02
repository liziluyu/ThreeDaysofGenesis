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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 2f, true, false, 0, Palette, "TestGrainParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Liquid, 1f, true, false, 4, Palette, "TestLiquidParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 999f, false, true, 0, Palette, "TestStaticParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, false, false, 0, Palette, "TestWalkerParticle"); }
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

    /// <summary>测试专用的可流动固体：Solid 标签、非静态、不受重力、OnUpdate 不做任何事。</summary>
    /// <remarks>用于直接调用 <see cref="ParticleRules.TryFlowHorizontal"/>，隔离出「穿行判定」这一条规则。</remarks>
    internal sealed class TestSolidFlowParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(120, 120, 120, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 5f, false, false, 8, Palette, "TestSolidFlowParticle"); }
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

    /// <summary>OnUpdate 按需抛异常的粒子，用于验证 Step 异常后写入标记仍然正确。</summary>
    internal sealed class TestThrowerParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(200, 0, 200, 255) };

        internal static bool Armed;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, false, false, 0, Palette, "TestThrowerParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            if (Armed)
            {
                throw new System.InvalidOperationException("TestThrowerParticle 故意抛出。");
            }
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>调色板非法的类型：注册校验必须拒绝它。</summary>
    internal sealed class TestBadPaletteParticle : IParticleType
    {
        internal static readonly Color32[] Palette = new Color32[0];

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, false, false, 0, Palette, "TestBadPaletteParticle"); }
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

    /// <summary>没有任何类别标签的类型：注册校验必须拒绝它（rank 0 等同于空气）。</summary>
    internal sealed class TestNoTagParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(1, 2, 3, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.None, 1f, false, false, 0, Palette, "TestNoTagParticle"); }
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

    /// <summary>
    /// 测试用的隔离类型表。**完全不触碰全局注册表**，因此测试之间、以及与生产注册顺序之间互不污染。
    /// </summary>
    internal static class TestTypes
    {
        internal const ushort GrainId = 1;
        internal const ushort LiquidId = 2;
        internal const ushort StaticId = 3;
        internal const ushort WalkerId = 4;
        internal const ushort SolidFlowId = 5;
        internal const ushort ThrowerId = 6;

        internal static readonly TestGrainParticle Grain = new TestGrainParticle();
        internal static readonly TestLiquidParticle Liquid = new TestLiquidParticle();
        internal static readonly TestStaticParticle Static = new TestStaticParticle();
        internal static readonly TestWalkerParticle Walker = new TestWalkerParticle();

        private static ParticleType[] _table;

        /// <summary>下标 0 是空气（null），其余按上面的 id 常量排列。</summary>
        internal static ParticleType[] Table
        {
            get
            {
                if (_table == null)
                {
                    _table = new[]
                    {
                        null,
                        Bind(GrainId, Grain),
                        Bind(LiquidId, Liquid),
                        Bind(StaticId, Static),
                        Bind(WalkerId, Walker),
                        Bind(SolidFlowId, new TestSolidFlowParticle()),
                        Bind(ThrowerId, new TestThrowerParticle()),
                    };
                }

                return _table;
            }
        }

        internal static void ResetCounters()
        {
            Grain.SpawnCount = 0;
            Grain.KillCount = 0;
            Grain.LastKillerId = ushort.MaxValue;
            Grain.VictimIdSeenByKill = ushort.MaxValue;
            Static.UpdateCount = 0;
            Walker.UpdateCount = 0;
            TestThrowerParticle.Armed = false;
        }

        private static ParticleType Bind(ushort id, IParticleType behaviour)
        {
            return new ParticleType(id, behaviour.Info, behaviour.OnUpdate, behaviour.OnSpawn, behaviour.OnKill);
        }
    }

    /// <summary>生产粒子（Stone / Sand / Soil）的隔离类型表，供行为测试使用；同样不触碰全局注册表。</summary>
    internal static class ProductionTypes
    {
        internal const ushort StoneId = 1;
        internal const ushort SandId = 2;
        internal const ushort SoilId = 3;

        private static ParticleType[] _table;

        internal static ParticleType[] Table
        {
            get
            {
                if (_table == null)
                {
                    _table = new[]
                    {
                        null,
                        Bind(StoneId, new StoneParticle()),
                        Bind(SandId, new SandParticle()),
                        Bind(SoilId, new SoilParticle()),
                    };
                }

                return _table;
            }
        }

        private static ParticleType Bind(ushort id, IParticleType behaviour)
        {
            return new ParticleType(id, behaviour.Info, behaviour.OnUpdate, behaviour.OnSpawn, behaviour.OnKill);
        }
    }
}
