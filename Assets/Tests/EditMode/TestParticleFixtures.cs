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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.TryFall(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
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
            // 运动阅读盒：TryFlowHorizontal 沿同一行扫到 x ± MaxFlowSpread(= 4)，另经 TryFall / 斜落读 (x, y−1)
            // 与 (x ± 1, y−1) ⇒ (4, 0, 1)。
            get { return new ParticleTypeInfo(ParticleTags.Liquid, 1f, 1f, 1f, 1f, true, false, 4, 0f, 0f, Palette, "TestLiquidParticle", motionReadReach: new MotionReadBox(x: 4, up: 0, down: 1)); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
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

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>测试专用的静态类型：OnMotion / OnLogic 永不应被调用。</summary>
    internal sealed class TestStaticParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(255, 255, 255, 255) };

        internal int UpdateCount;

        /// <summary>静态类型永不参与随机刻，本计数必须始终为 0。</summary>
        internal int RandomTickCount;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 999f, 1f, 1f, 1f, false, true, 0, 0f, 0f, Palette, "TestStaticParticle"); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            UpdateCount++;
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            RandomTickCount++;
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>测试专用的「每帧向右走一格」粒子，用来验证每帧至多被处理一次。</summary>
    internal sealed class TestWalkerParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(255, 0, 0, 255) };

        internal int UpdateCount;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestWalkerParticle"); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            UpdateCount++;
            if (world.IsAir(x + 1, y))
            {
                world.Swap(x, y, x + 1, y);
            }
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>测试专用的可流动固体：Solid 标签、非静态、不受重力、OnMotion 不做任何事。</summary>
    /// <remarks>用于直接调用 <see cref="ParticleRules.TryFlowHorizontal"/>，隔离出「穿行判定」这一条规则。</remarks>
    internal sealed class TestSolidFlowParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(120, 120, 120, 255) };

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 5f, 1f, 1f, 1f, false, false, 8, 0f, 0f, Palette, "TestSolidFlowParticle"); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>OnMotion 按需抛异常的粒子，用于验证 Step 异常后写入标记仍然正确。</summary>
    internal sealed class TestThrowerParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(200, 0, 200, 255) };

        internal static bool Armed;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestThrowerParticle"); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            if (Armed)
            {
                throw new System.InvalidOperationException("TestThrowerParticle 故意抛出。");
            }
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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
    /// 测试专用的热源：每 tick 先读自己所在格的热量（记到 <see cref="LastObservedHeat"/>），再累加 <see cref="Delta"/>；
    /// 不移动、不做其它事。用于验证粒子能在 <c>OnMotion</c> 里查改世界热图。
    /// </summary>
    internal sealed class TestHeatParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(255, 120, 0, 255) };

        internal const float DefaultDelta = 5f;

        /// <summary>每 tick 的热量增量。测试可改，<see cref="TestTypes.ResetCounters"/> 会复位。</summary>
        internal static float Delta = DefaultDelta;

        /// <summary>最近一次 <c>OnMotion</c> 读到（写入前）的热量。用于验证「先查后改」。</summary>
        internal static float LastObservedHeat = float.NaN;

        /// <summary>热容刻意取 2（空气是 1），用于验证 <c>T = Q / C</c> 的换算。</summary>
        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 2f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestHeatParticle"); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            LastObservedHeat = world.Heat.GetHeat(x, y);
            world.Heat.AddHeat(x, y, Delta);
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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

        public void OnMotion(ParticleWorld world, int x, int y)
        {
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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
    /// 测试专用：记录被随机刻命中的次数、坐标，并把命中坐标折进滚动哈希 <see cref="PickHash"/>，
    /// 用于验证「同一种子 → 同一批随机刻」。不做任何移动，因此不干扰网格状态。
    /// </summary>
    internal sealed class TestRandomTickParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(0, 200, 0, 255) };

        /// <summary>被随机刻命中的总次数。</summary>
        internal int RandomTickCount;

        /// <summary>最近一次命中的格坐标（尚未命中过时为 -1 / -1）。</summary>
        internal int LastX = -1;
        internal int LastY = -1;

        /// <summary>命中坐标序列的滚动哈希（<c>(y * Width + x)</c> 逐次折入）。</summary>
        internal long PickHash;

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestRandomTickParticle"); }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 随机刻测试只关心 OnRandomTick，扫描行为保持空。
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            RandomTickCount++;
            LastX = x;
            LastY = y;
            PickHash = PickHash * 1000003L + (y * world.Width + x);
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
        }
    }

    /// <summary>
    /// 测试专用的两段钩子探针：分别记录 <c>OnLogic</c> / <c>OnMotion</c> 被调用的次数，
    /// 并记录「本格运动时逻辑是否已经跑过」——用于验证运动帧只跑运动、逻辑帧先逻辑后运动。
    /// </summary>
    internal sealed class TestPhaseProbeParticle : IParticleType
    {
        internal static readonly Color32[] Palette = { new Color32(9, 99, 9, 255) };

        /// <summary>逻辑钩子被调用的次数。</summary>
        internal int LogicCount;

        /// <summary>运动钩子被调用的次数。</summary>
        internal int MotionCount;

        /// <summary>每格运动时「这一格的逻辑钩子是否刚跑过」。逻辑帧上应为 true、运动帧上应为 false。</summary>
        internal bool LogicRanBeforeMotion;

        /// <summary>置为 true 时 <c>OnLogic</c> 把本格相变成 <see cref="TestTypes.SolidFlowId"/>（验证相变后本帧跳过运动）。</summary>
        internal bool MorphOnLogic;

        /// <summary>运动钩子里记录当格的逻辑是否跑过，并在每格消费后复位（单格世界下无歧义）。</summary>
        private bool _logicRanForThisCell;

        /// <summary>
        /// 复位探针的全部状态。相变测试里 <c>OnMotion</c> 会被跳过（本格已被换掉），
        /// 因此 <see cref="_logicRanForThisCell"/> 不会被消费——不在这里一并清掉，它就会漏进下一个测试，
        /// 让那个测试的 <see cref="LogicRanBeforeMotion"/> 假阳性。
        /// </summary>
        internal void ResetProbe()
        {
            LogicCount = 0;
            MotionCount = 0;
            LogicRanBeforeMotion = false;
            MorphOnLogic = false;
            _logicRanForThisCell = false;
        }

        public ParticleTypeInfo Info
        {
            get { return new ParticleTypeInfo(ParticleTags.Solid, 1f, 1f, 1f, 1f, false, false, 0, 0f, 0f, Palette, "TestPhaseProbeParticle"); }
        }

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            LogicCount++;
            _logicRanForThisCell = true;

            if (MorphOnLogic)
            {
                world.SetPhase(x, y, TestTypes.SolidFlowId);
            }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            MotionCount++;
            LogicRanBeforeMotion = _logicRanForThisCell;
            _logicRanForThisCell = false;
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
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
        internal const ushort RandomTickId = 12;
        internal const ushort PhaseProbeId = 13;

        internal static readonly TestGrainParticle Grain = new TestGrainParticle();
        internal static readonly TestLiquidParticle Liquid = new TestLiquidParticle();
        internal static readonly TestStaticParticle Static = new TestStaticParticle();
        internal static readonly TestWalkerParticle Walker = new TestWalkerParticle();
        internal static readonly TestRandomTickParticle RandomTick = new TestRandomTickParticle();
        internal static readonly TestPhaseProbeParticle PhaseProbe = new TestPhaseProbeParticle();

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
                        Bind(RandomTickId, RandomTick),
                        Bind(PhaseProbeId, PhaseProbe),
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
            Static.RandomTickCount = 0;
            Walker.UpdateCount = 0;
            RandomTick.RandomTickCount = 0;
            RandomTick.LastX = -1;
            RandomTick.LastY = -1;
            RandomTick.PickHash = 0;
            PhaseProbe.ResetProbe();
            TestThrowerParticle.Armed = false;
            TestHeatParticle.Delta = TestHeatParticle.DefaultDelta;
            TestHeatParticle.LastObservedHeat = float.NaN;
        }

        private static ParticleType Bind(ushort id, IParticleType behaviour)
        {
            return new ParticleType(id, behaviour.Info, behaviour.GetType().Name, behaviour.OnMotion, behaviour.OnLogic, behaviour.OnRandomTick, behaviour.OnSpawn, behaviour.OnKill);
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
                    (ushort)(i + 1), behaviour.Info, behaviour.GetType().Name,
                    behaviour.OnMotion, behaviour.OnLogic, behaviour.OnRandomTick, behaviour.OnSpawn, behaviour.OnKill);
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
            return new ParticleType(id, behaviour.Info, behaviour.GetType().Name, behaviour.OnMotion, behaviour.OnLogic, behaviour.OnRandomTick, behaviour.OnSpawn, behaviour.OnKill);
        }
    }
}
