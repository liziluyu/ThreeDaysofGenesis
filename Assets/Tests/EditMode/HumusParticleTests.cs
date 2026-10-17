using NUnit.Framework;
using TapTap2026.Simulation;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 腐殖质（<see cref="HumusParticle"/>）的随机刻命中计数：累计 <see cref="HumusParticle.DecayHits"/>
    /// （10）次命中即分解成空气。计数存在本格 <see cref="Cell.flags"/> 的私有位里，随格子移动。
    /// </summary>
    /// <remarks>
    /// <para>走 <see cref="ParticleRegistration.RegisterAll"/> 的全局表：<see cref="ParticleIds.Humus"/>
    /// 需要被回填，类型表里的注册顺序也以全局表为准。</para>
    /// <para>随机刻用 <see cref="ParticleType.OnRandomTick"/> 直接调用（仓库既有惯例），
    /// 保证确定、不依赖世界随机抽格。</para>
    /// </remarks>
    public sealed class HumusParticleTests
    {
        [SetUp]
        public void SetUp()
        {
            ParticleRegistration.RegisterAll();
        }

        private static void RandomTick(ParticleWorld world, int x, int y)
        {
            ParticleRegistry.Types[ParticleIds.Humus].OnRandomTick(world, x, y);
        }

        private static int CountType(ParticleWorld world, ushort typeId)
        {
            int count = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.GetCell(x, y).typeId == typeId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void StepMotion(ParticleWorld world, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                world.StepMotion();
            }
        }

        [Test]
        public void RandomTick_NineHits_KeepsHumus()
        {
            var world = new ParticleWorld(9, 9, 20261017u);
            world.Set(4, 4, ParticleIds.Humus);

            for (int hit = 0; hit < HumusParticle.DecayHits - 1; hit++)
            {
                RandomTick(world, 4, 4);
            }

            Assert.AreEqual(
                ParticleIds.Humus, world.GetCell(4, 4).typeId,
                "第 1..9 次随机刻命中都只是累计计数，不得提前分解");
        }

        [Test]
        public void RandomTick_TenthHit_ClearsHumus()
        {
            var world = new ParticleWorld(9, 9, 20261017u);
            world.Set(4, 4, ParticleIds.Humus);

            for (int hit = 0; hit < HumusParticle.DecayHits; hit++)
            {
                RandomTick(world, 4, 4);
            }

            Assert.AreEqual(
                ParticleIds.Air, world.GetCell(4, 4).typeId,
                "第 10 次随机刻命中应把腐殖质分解成空气");
            Assert.AreEqual(
                0, CountType(world, ParticleIds.Humus),
                "腐殖质不带 Organic 标签，Clear 不得再产生新的腐殖质");
        }

        [Test]
        public void RandomTick_CountTravelsWithCell()
        {
            var world = new ParticleWorld(9, 9, 20261017u);
            world.Set(4, 4, ParticleIds.Humus);

            for (int hit = 0; hit < HumusParticle.DecayHits - 1; hit++)
            {
                RandomTick(world, 4, 4);
            }

            // 计数存在 Cell.flags 里，Swap 搬整格 ⇒ 计数跟着腐殖质走。
            Assert.IsTrue(world.Swap(4, 4, 5, 4), "交换坐标都在界内");
            Assert.AreEqual(ParticleIds.Air, world.GetCell(4, 4).typeId, "原格应让给空气");
            Assert.AreEqual(ParticleIds.Humus, world.GetCell(5, 4).typeId, "腐殖质搬到邻格");

            // 新位置上再命中一次就是第 10 次 ⇒ 分解。
            RandomTick(world, 5, 4);

            Assert.AreEqual(
                ParticleIds.Air, world.GetCell(5, 4).typeId,
                "计数随格子移动：新位置的这一击是第 10 次");
        }

        /// <summary>
        /// 腐殖质比水重：**静止**（无动量、地下水闸门会拦住固体置换液体）的腐殖质，一旦正下方从石头变成水
        /// （如冰融化、或本来就生成在水里），下一步就要无条件下沉——一路 Swap 穿过水柱，水只被排开、不湮灭。
        /// </summary>
        [Test]
        public void OnMotion_SinksThroughWater_WithoutMomentum()
        {
            var world = new ParticleWorld(1, 6, 20261017u);
            world.RandomTicksPerStep = 0;
            world.Set(0, 0, ParticleIds.Stone);
            world.Set(0, 1, ParticleIds.Water);
            world.Set(0, 2, ParticleIds.Water);
            world.Set(0, 3, ParticleIds.Stone);
            world.Set(0, 4, ParticleIds.Humus);

            // 让腐殖质在石面上停住、动量过期。
            StepMotion(world, 6);
            Assert.AreEqual(ParticleIds.Humus, world.GetCell(0, 4).typeId, "前置：腐殖质应停在石面上");
            Assert.IsFalse(world.HasMomentum(0, 4), "前置：腐殖质已静止（动量过期）");

            // 支撑（石头）被水替代，如同冰融化。
            world.Set(0, 3, ParticleIds.Water);
            StepMotion(world, 20);

            Assert.AreEqual(ParticleIds.Humus, world.GetCell(0, 1).typeId, "腐殖质应沉到水底石面上");
            Assert.AreEqual(ParticleIds.Stone, world.GetCell(0, 0).typeId, "水底石面不得被覆盖");
            Assert.AreEqual(3, CountType(world, ParticleIds.Water), "水只被排开、不得被湮灭");
        }
    }
}
