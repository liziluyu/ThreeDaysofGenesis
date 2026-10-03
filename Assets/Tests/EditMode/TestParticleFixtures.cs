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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 2f, 1f, 1f, 1f, true, false, 0, 0f, 0f, Palette, "TestGrainParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Liquid, 1f, 1f, 1f, 1f, true, false, 4, 0f, 0f, Palette, "TestLiquidParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 999f, 1f, 1f, 1f, false, true, 0, 0f, 0f, Palette, "TestStaticParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestWalkerParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 5f, 1f, 1f, 1f, false, false, 8, 0f, 0f, Palette, "TestSolidFlowParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestThrowerParticle"); }
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

    /// <summary>测试用的低导热固体（σ = 0.2，静态、热容 1）：验证界面导热率取两侧材料 σ 的平均。</summary>
    internal sealed class TestInsulatingParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(20, 20, 20, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 0.2f, 1f, false, true, 0, 0f, 0f, Palette, "TestInsulatingParticle"); }
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

    /// <summary>测试用的低辐射率固体（ε = 0.2，静态、热容 1、σ = 1）：验证暴露面的辐射按材料 ε 缩放。</summary>
    internal sealed class TestLowEmissivityParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(30, 30, 30, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 0.2f, false, true, 0, 0f, 0f, Palette, "TestLowEmissivityParticle"); }
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

    /// <summary>辐射率越界的类型：注册校验必须拒绝它。</summary>
    internal sealed class TestBadEmissivityParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(40, 40, 40, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1.5f, false, false, 0, 0f, 0f, Palette, "TestBadEmissivityParticle"); }
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

    /// <summary>调色板非法的类型：注册校验必须拒绝它。</summary>
    internal sealed class TestBadPaletteParticle : IParticleType
    {
        internal static readonly Color32[] Palette = new Color32[0];

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestBadPaletteParticle"); }
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
            get { return new ParticleTypeInfo(ParticleTags.None, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestNoTagParticle"); }
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

    /// <summary>摩擦度 1 的固体：走 <see cref="ParticleRules.StepSolid"/>，正下方有固体时永不斜滑（与粘性无关）。</summary>
    internal sealed class TestFrictionOneSolidParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(70, 70, 70, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 4f, 1f, 1f, 1f, true, false, 0, 1f, 0f, Palette, "TestFrictionOneSolidParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>完全靠粘性（摩擦度 0、粘性 1）的固体：旁边有固体邻居就绝不斜滑，孤立时按 1:1 滑落。</summary>
    internal sealed class TestCohesionParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(60, 90, 120, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 3f, 1f, 1f, 1f, true, false, 0, 0f, 1f, Palette, "TestCohesionParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>摩擦度 / 粘性可调的固体（走 <see cref="ParticleRules.StepSolid"/>），用于验证安息角随两个参数变化。</summary>
    internal sealed class TestTunableSolidParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(90, 90, 90, 255) };

        private readonly float _friction;
        private readonly float _stickiness;

        internal TestTunableSolidParticle(float friction, float stickiness)
        {
            _friction = friction;
            _stickiness = stickiness;
        }

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 2f, 1f, 1f, 1f, true, false, 0, _friction, _stickiness, Palette, "TestTunableSolidParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>
    /// 测试专用的热源：每 tick 先读自己所在格的热量（记到 <see cref="LastObservedHeat"/>），再累加 <see cref="Delta"/>；
    /// 不移动、不做其它事。用于验证粒子能在 <c>OnUpdate</c> 里查改世界热图。
    /// </summary>
    internal sealed class TestHeatParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(255, 120, 0, 255) };

        internal const float DefaultDelta = 5f;

        /// <summary>每 tick 的热量增量。测试可改，<see cref="TestTypes.ResetCounters"/> 会复位。</summary>
        internal static float Delta = DefaultDelta;

        /// <summary>最近一次 <c>OnUpdate</c> 读到（写入前）的热量。用于验证「先查后改」。</summary>
        internal static float LastObservedHeat = float.NaN;

        /// <summary>热容刻意取 2（空气是 1），用于验证 <c>T = Q / C</c> 的换算。</summary>
        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 2f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestHeatParticle"); }
        }

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            LastObservedHeat = world.Heat.GetHeat(x, y);
            world.Heat.AddHeat(x, y, Delta);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>粘性非法的类型：注册校验必须拒绝它。</summary>
    internal sealed class TestBadStickinessParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(11, 12, 13, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 1.5f, Palette, "TestBadStickinessParticle"); }
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

    /// <summary>摩擦度非法的类型：注册校验必须拒绝它。</summary>
    internal sealed class TestBadFrictionParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(7, 8, 9, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 1.5f, 0f, Palette, "TestBadFrictionParticle"); }
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
        internal const ushort FrictionOneId = 7;
        internal const ushort CohesionId = 8;
        internal const ushort HeatId = 9;
        internal const ushort InsulatingId = 10;
        internal const ushort LowEmissivityId = 11;

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
                        Bind(FrictionOneId, new TestFrictionOneSolidParticle()),
                        Bind(CohesionId, new TestCohesionParticle()),
                        Bind(HeatId, new TestHeatParticle()),
                        Bind(InsulatingId, new TestInsulatingParticle()),
                        Bind(LowEmissivityId, new TestLowEmissivityParticle()),
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
            TestHeatParticle.Delta = TestHeatParticle.DefaultDelta;
            TestHeatParticle.LastObservedHeat = float.NaN;
        }

        private static ParticleType Bind(ushort id, IParticleType behaviour)
        {
            return new ParticleType(id, behaviour.Info, behaviour.OnUpdate, behaviour.OnSpawn, behaviour.OnKill);
        }
    }

    /// <summary>把若干测试粒子打包成一张隔离类型表（下标 0 = 空气），供单次测试临时构造参数化类型。</summary>
    internal static class TestTables
    {
        /// <summary>第 n 个粒子的 typeId 就是 n + 1。</summary>
        internal static ParticleType[] Build(params IParticleType[] behaviours)
        {
            var table = new ParticleType[behaviours.Length + 1];
            for (int i = 0; i < behaviours.Length; i++)
            {
                IParticleType behaviour = behaviours[i];
                table[i + 1] = new ParticleType(
                    (ushort)(i + 1), behaviour.Info, behaviour.OnUpdate, behaviour.OnSpawn, behaviour.OnKill);
            }

            return table;
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
