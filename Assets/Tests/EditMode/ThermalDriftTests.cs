using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 大气水平热压流（<see cref="ParticleRules.TryThermalDrift"/>）的风向记忆：审计 B1 的
    /// 「同一对格子逐帧往返」回归测试，以及「大温差净输运保留」的对照。
    /// </summary>
    /// <remarks>
    /// 全部走隔离类型表（<see cref="TestTables.Build"/> + 生产 <see cref="AtmosphereParticle"/>），
    /// 不触碰全局注册表；直接调用规则 / <c>OnMotion</c>，不经过世界扫描，因此不受浮力阈值与热传导干扰。
    /// <para>记忆位是 flags 的 bit3-4（<see cref="CellFlags.DriftDirectionMask"/>）；「记忆是否破坏 bit0
    /// 处理标记」也作为对外契约断言，其余只断言可观察的格子位置 / 类型。</para>
    /// </remarks>
    public sealed class ThermalDriftTests
    {
        private const ushort ColdId = 1;
        private const ushort WarmId = 2;

        private static readonly float Epsilon = AtmosphereParticle.BuoyancyEpsilon;

        /// <summary>两个大气格：左冷右暖，只在同一行 y=4 上，横向走廊。</summary>
        private static ParticleWorld NewCorridor(float cold, float warm)
        {
            ParticleWorld world = new ParticleWorld(
                9, 5, 7u, TestTables.Build(new AtmosphereParticle(), new AtmosphereParticle()));
            world.Set(4, 4, ColdId);
            world.Set(5, 4, WarmId);
            world.Heat.SetHeat(4, 4, cold); // 大气热容 1 → T = Q
            world.Heat.SetHeat(5, 4, warm);
            return world;
        }

        private static int FindX(ParticleWorld world, ushort typeId)
        {
            for (int x = 0; x < world.Width; x++)
            {
                if (world.GetCell(x, 4).typeId == typeId)
                {
                    return x;
                }
            }

            return -1;
        }

        private static byte DirectionOf(ParticleWorld world, int x, int y)
        {
            return (byte)((world.GetCell(x, y).flags & CellFlags.DriftDirectionMask) >> CellFlags.DriftDirectionShift);
        }

        /// <summary>
        /// 审计 B1 走廊用例：两格温差场景，连续 8 帧不再 5,4,5,4… 乒乓——冷空气漂到暖侧后即停，
        /// 同一对格子不连续互换。
        /// </summary>
        [Test]
        public void ThermalDrift_TwoCellCorridor_DoesNotPingPong()
        {
            ParticleWorld world = NewCorridor(20f, 30f);

            int[] positions = new int[8];
            for (int frame = 0; frame < positions.Length; frame++)
            {
                int x = FindX(world, ColdId);
                ParticleRules.TryThermalDrift(world, x, 4, Epsilon);
                positions[frame] = FindX(world, ColdId);
            }

            Assert.AreEqual(5, positions[0], "冷空气应沿唯一更热侧漂到 x=5（规则确实在动，断言非空转）");
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.AreEqual(5, positions[i], $"第 {i} 帧不应离开暖侧（无往复）");
            }

            for (int i = 0; i + 2 < positions.Length; i++)
            {
                bool pingPong = positions[i] != positions[i + 1]
                    && positions[i + 1] != positions[i + 2]
                    && positions[i] == positions[i + 2];
                Assert.IsFalse(pingPong, $"第 {i}..{i + 2} 帧出现同一对格子来回互换：{positions[i]},{positions[i + 1]},{positions[i + 2]}");
            }
        }

        /// <summary>
        /// 稳定大温差下冷空气仍向暖侧净迁移：暖块挡住回头路，冷空气只进不退（记忆不得把净输运也挡掉）。
        /// </summary>
        [Test]
        public void ThermalDrift_LargeStableDelta_NetMigrationTowardWarm()
        {
            ParticleWorld world = new ParticleWorld(
                9, 5, 7u, TestTables.Build(new AtmosphereParticle(), new AtmosphereParticle()));
            world.Set(4, 4, ColdId);   // 冷空气 T=20
            world.Set(5, 4, WarmId);   // 暖块 T=30
            world.Set(6, 4, WarmId);   // 暖块延伸
            world.Heat.SetHeat(4, 4, 20f);
            world.Heat.SetHeat(5, 4, 30f);
            world.Heat.SetHeat(6, 4, 30f);

            for (int frame = 0; frame < 8; frame++)
            {
                int x = FindX(world, ColdId);
                ParticleRules.TryThermalDrift(world, x, 4, Epsilon);
                Assert.LessOrEqual(4, FindX(world, ColdId), "冷空气不得反向迁回左侧");
            }

            Assert.AreEqual(5, FindX(world, ColdId), "冷空气应净迁移到暖块里的 x=5 并停住");
        }

        /// <summary>
        /// 记忆复位：梯度消失（左右都不更热）时记忆被清，之后出现新的暖侧即可朝新方向漂——
        /// 记忆不是永久锁死的方向。
        /// </summary>
        [Test]
        public void ThermalDrift_WhenGradientDies_MemoryResetsAllowingNewDirection()
        {
            ParticleWorld world = new ParticleWorld(
                9, 5, 7u, TestTables.Build(new AtmosphereParticle()));
            world.Set(5, 4, ColdId);
            world.Heat.SetHeat(5, 4, 22f);
            world.Heat.SetHeat(4, 4, 20f); // 左 20、右 22：两侧都不比 22 热 → 无触发
            world.Heat.SetHeat(6, 4, 22f);
            // 预置「向右」记忆，模拟这团气体之前顺梯度右漂过。
            world.At(5, 4).flags = (byte)(world.At(5, 4).flags | (CellFlags.DriftDirectionRight << CellFlags.DriftDirectionShift));

            Assert.IsFalse(ParticleRules.TryThermalDrift(world, 5, 4, Epsilon), "无更热侧不应漂移");
            Assert.AreEqual(CellFlags.DriftDirectionNone, DirectionOf(world, 5, 4), "无触发应清掉记忆");

            world.Heat.SetHeat(4, 4, 50f); // 左侧骤热

            Assert.IsTrue(ParticleRules.TryThermalDrift(world, 5, 4, Epsilon), "复位后应能朝新的暖侧漂");
            Assert.AreEqual(4, FindX(world, ColdId), "气体漂向新的暖侧");
        }

        /// <summary>方向记忆只写 bit3-4：bit0 处理标记与其它位（如 Burning）必须原样保留。</summary>
        [Test]
        public void ThermalDrift_WritesDirectionWithoutBreakingProcessedParity()
        {
            ParticleWorld world = NewCorridor(20f, 30f);
            world.At(4, 4).flags = (byte)(CellFlags.ProcessedParity | CellFlags.Burning);

            Assert.IsTrue(ParticleRules.TryThermalDrift(world, 4, 4, Epsilon));

            Cell moved = world.GetCell(5, 4);
            Assert.AreEqual(ColdId, moved.typeId);
            Assert.AreNotEqual(0, moved.flags & CellFlags.ProcessedParity, "bit0 处理标记不得被记忆写入破坏");
            Assert.AreNotEqual(0, moved.flags & CellFlags.Burning, "其它 flags 位必须保留");
            Assert.AreEqual(CellFlags.DriftDirectionRight, DirectionOf(world, 5, 4), "漂移方向写进记忆");
        }

        /// <summary>浮力换行成功即清掉风向记忆：新的行不该继承上一段水平漂移的记忆。</summary>
        [Test]
        public void OnMotion_BuoyantMove_ClearsDriftMemory()
        {
            var atmosphere = new AtmosphereParticle();
            ParticleWorld world = new ParticleWorld(8, 8, 7u, TestTables.Build(atmosphere));
            world.Set(4, 4, ColdId);
            world.Heat.SetHeat(4, 4, 200f); // 比四邻（空气 T=0）热得多 → 浮力上升
            world.At(4, 4).flags = (byte)(world.At(4, 4).flags | (CellFlags.DriftDirectionRight << CellFlags.DriftDirectionShift));

            atmosphere.OnMotion(world, 4, 4);

            Assert.AreEqual(ColdId, world.GetCell(4, 5).typeId, "热气应上升到上一行");
            Assert.AreEqual(CellFlags.DriftDirectionNone, DirectionOf(world, 4, 5), "浮力换行后风向记忆清零");
        }

        /// <summary>
        /// 规则级别的抗振荡核心：与记忆相反的方向即使有足够的温差也不走，且记忆保留——
        /// 正是这一条挡住了走廊里的逐帧往返。
        /// </summary>
        [Test]
        public void ThermalDrift_ReverseOfMemory_IsBlockedAndMemoryKept()
        {
            ParticleWorld world = NewCorridor(20f, 30f); // 冷在 4、暖在 5：唯一更热侧是右侧
            // 预置「向左」记忆，模拟这团气体上一帧刚向左漂过。
            world.At(4, 4).flags = (byte)(world.At(4, 4).flags | (CellFlags.DriftDirectionLeft << CellFlags.DriftDirectionShift));

            Assert.IsFalse(ParticleRules.TryThermalDrift(world, 4, 4, Epsilon), "反向热压不应掉头");
            Assert.AreEqual(4, FindX(world, ColdId), "原地未动");
            Assert.AreEqual(CellFlags.DriftDirectionLeft, DirectionOf(world, 4, 4), "记忆应保留");
        }

        /// <summary>
        /// 审计 N4 回归：反向不再一刀切。记忆已建立后，wanted 侧只有单格热点（回声余温）仍被挡；
        /// wanted 侧的热**连片**（再前一格也不更冷）时放行并改写记忆——离散梯度反转（潜热 / 大范围加热）
        /// 不再被永久锁死。
        /// </summary>
        [Test]
        public void ThermalDrift_ReversalRequiresExtendedWarmth()
        {
            ParticleWorld world = NewCorridor(20f, 30f);

            // 建立记忆：冷粒子顺梯度向右漂一格（x4 → x5），记忆 = Right。
            Assert.IsTrue(ParticleRules.TryThermalDrift(world, 4, 4, Epsilon));
            Assert.AreEqual(CellFlags.DriftDirectionRight, DirectionOf(world, 5, 4));

            // 回声 / 单格热点：wanted 侧只有交换伙伴那一格热，extent 不足 → 仍被挡。
            Assert.IsFalse(ParticleRules.TryThermalDrift(world, 5, 4, Epsilon), "单格余温不得引发反向");
            Assert.AreEqual(5, FindX(world, ColdId), "粒子原地未动");

            // 梯度真的反转：wanted 侧连片加热（x3 也热），extent 满足 → 放行并改写记忆。
            world.Heat.SetHeat(3, 4, 80f);
            world.Heat.SetHeat(4, 4, 80f);
            Assert.IsTrue(ParticleRules.TryThermalDrift(world, 5, 4, Epsilon), "连片热反转应放行");
            Assert.AreEqual(4, FindX(world, ColdId), "冷粒子回迁 x4");
            Assert.AreEqual(CellFlags.DriftDirectionLeft, DirectionOf(world, 4, 4), "记忆应改写为新方向");
        }
    }
}
