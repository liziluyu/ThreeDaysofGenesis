using UnityEngine;

namespace TapTap2026.Simulation
{
    // HumusSoilParticle —— 腐殖土（固体标签；土壤吸了腐殖质后的形态）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正上方一格（判腐殖质）+ 半径 SpreadSearchRadius 格内找邻近土壤
    //               （只读结构；写用 SetPhase / Clear，自身运动仍是 StepSolid）
    // 交互的 ParticleIds：Humus —— 上方是腐殖质时被吸收（Clear）；
    //                     Soil → HumusSoil、WetSoil → WetHumusSoil（把邻近土壤改良成腐殖土家族）
    //
    // 行为：与干土同款粉末运动（ParticleRules.StepSolid，摩擦 / 粘性 0.5 ⇒ 约 3:1 安息角）。
    //       **逻辑帧无变化**（OnLogic 仍严格空体，hasLogic: false）；**随机刻**里被点到且**上方一格是腐殖质**时，
    //       从近到远找邻近第一个普通 / 湿润土壤：普通土壤 → 腐殖土、湿润土壤 → 湿润腐殖土（同一份物质换形态，
    //       水原样留在湿润形态里），并把上方那格腐殖质吸收掉（Clear）——腐殖质被腐殖土吞噬、腐殖土因此向外扩张。
    //       附近没有土壤时什么都不做，腐殖质留在原地等下一次随机刻。**无概率门、不摇骰**（唯一的随机流消耗是
    //       换形态时世界对 shade 的抽样，与任何 SetPhase 相同）。它最显眼的是**颜色**：比干土、湿土都更黑
    //       （富有机质），是「土壤被腐殖质改良」的可见结果。
    //       另有共享 helper ConsumeFarthestBelow：草的 / 花的温度优待与草 / 树木种子的发芽优待**消耗型**地
    //       用掉脚下连续土层里**最深**的一格腐殖土家族（HumusSoil → Soil、WetHumusSoil → WetSoil，走 SetPhase
    //       保水保热）——优待不是永久光环，用完即退化。
    //
    //       不可在编辑器中画出（只能由腐殖质转化土壤产生）。
    public sealed class HumusSoilParticle : IParticleType
    {
        /// <summary>找邻近土壤的搜索半径（切比雪夫盒；以**欧氏距离**从近到远选第一个命中的格）。</summary>
        public const int SpreadSearchRadius = 2;

        private static readonly Color32[] Palette =
        {
            new Color32(30, 24, 18, 255),
            new Color32(40, 32, 24, 255),
            new Color32(24, 19, 14, 255),
            new Color32(48, 38, 28, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.9f,   // 比干土（1.8）略重
            heatCapacity: 1.0f,
            thermalConductivity: 0.60f,
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: "腐殖土",
            // 运动阅读盒：与干土同款 0.5/0.5 StepSolid ⇒ 斜滑净空链 3 格，即 (1, 0, 3)。
            motionReadReach: new MotionReadBox(x: 1, up: 0, down: 3),
            hasLogic: false); // OnLogic 严格空体：腐殖土是稳定终态

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 腐殖土是稳定终态：无相变 / 反应。
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 与干土同款粉末运动：摩擦度 0.5 → 约 3:1 安息角。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 条件 1：**正上方一格必须是腐殖质**。用 typeId 判要同时排除空气——隔离类型表里
            // ParticleIds.Humus 是 0（= 空气的 typeId），只比 id 会把空气当成腐殖质。
            ushort aboveId = world.GetCell(x, y + 1).typeId;
            if (aboveId == ParticleRegistry.AirTypeId || aboveId != ParticleIds.Humus)
            {
                return;
            }

            // 条件 2：从近到远找邻近第一个普通 / 湿润土壤。同距取固定扫描序（y 升序、同一行 x 升序）保证确定性。
            // 目标判据同样要排除空气（隔离表里 ParticleIds.Soil 可能是 0）。本行为不消耗随机流。
            bool found = false;
            int bestDx = 0;
            int bestDy = 0;
            int bestDistance = int.MaxValue;
            ushort bestId = ParticleRegistry.AirTypeId;
            for (int dy = -SpreadSearchRadius; dy <= SpreadSearchRadius; dy++)
            {
                for (int dx = -SpreadSearchRadius; dx <= SpreadSearchRadius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    ushort id = world.GetCell(x + dx, y + dy).typeId;
                    if (id == ParticleRegistry.AirTypeId || (id != ParticleIds.Soil && id != ParticleIds.WetSoil))
                    {
                        continue;
                    }

                    int distance = dx * dx + dy * dy;
                    if (distance >= bestDistance)
                    {
                        continue;
                    }

                    found = true;
                    bestDistance = distance;
                    bestDx = dx;
                    bestDy = dy;
                    bestId = id;
                }
            }

            if (!found)
            {
                // 附近没有土壤：什么都不做，腐殖质留在上方等下一次随机刻。
                return;
            }

            // 普通土壤 → 腐殖土；湿润土壤 → 湿润腐殖土（那份水原样带进湿润形态，水账不失）。
            world.SetPhase(
                x + bestDx,
                y + bestDy,
                bestId == ParticleIds.WetSoil ? ParticleIds.WetHumusSoil : ParticleIds.HumusSoil);

            // 腐殖质被吸收：上方那格消失（Clear → 空气）。
            world.Clear(x, y + 1);
        }

        /// <summary>
        /// 消耗脚下**连续土层**里**最深（离植株最远）**的那一格腐殖土家族：从 <c>(x, y-1)</c> 起**竖直向下**扫描，
        /// cell 属于土层四土（土 / 湿土 / 腐殖土 / 湿润腐殖土）则继续，否则停（越界由
        /// <see cref="ParticleWorld.GetCell"/> 返回空气、天然停止），记录扫描中遇到的**最后一格**腐殖土家族坐标；
        /// 扫描结束后若记录到，用 <see cref="ParticleWorld.SetPhase"/> 把它退化为普通土
        /// （<see cref="ParticleIds.HumusSoil"/> → <see cref="ParticleIds.Soil"/>、
        /// <see cref="ParticleIds.WetHumusSoil"/> → <see cref="ParticleIds.WetSoil"/>，同一份物质换形态，
        /// 热量与水账原样保留），返回 <c>true</c>；连续土层里没有腐殖土家族则返回 <c>false</c>。
        /// 只读扫描、零分配、不消耗世界随机流（<c>SetPhase</c> 的 shade 抽样除外，属既有机制）；固定顺序、确定性。
        /// </summary>
        internal static bool ConsumeFarthestBelow(ParticleWorld world, int x, int y)
        {
            bool found = false;
            int farthestY = 0;
            ushort farthestId = ParticleRegistry.AirTypeId;
            for (int scanY = y - 1; ; scanY--)
            {
                ushort id = world.GetCell(x, scanY).typeId;
                if (id == ParticleIds.HumusSoil || id == ParticleIds.WetHumusSoil)
                {
                    found = true;
                    farthestY = scanY;
                    farthestId = id;
                    continue;
                }

                if (id == ParticleIds.Soil || id == ParticleIds.WetSoil)
                {
                    continue;
                }

                break;
            }

            if (!found)
            {
                return false;
            }

            world.SetPhase(
                x,
                farthestY,
                farthestId == ParticleIds.WetHumusSoil ? ParticleIds.WetSoil : ParticleIds.Soil);
            return true;
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set / SetPhase 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 腐殖土被覆盖时无残留效果。
        }
    }
}
