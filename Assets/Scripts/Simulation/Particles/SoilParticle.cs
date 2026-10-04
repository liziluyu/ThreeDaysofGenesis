using UnityEngine;

namespace TapTap2026.Simulation
{
    // SoilParticle —— 土壤（固体标签的粉末，摩擦度 0.5 + 粘性 0.5）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正上方一格（吸水判据），以及 StepSolid 的「正下、左下/右下、斜滑反侧同一行」
    // 交互的 ParticleIds：WetSoil —— 吸水后本格变成湿土（A 生成 B，走 ParticleIds）
    // 写入的邻居：正上方那一格水（吸水时 Clear，物理上是被土吸走）
    //
    // 行为：逻辑帧（20 Hz）先做吸水反应，运动帧（60 Hz）做「下落 + 斜落」的粉末运动——两者互不干扰：
    //       吸水：**正上方一格是水（Liquid 标签）**时，本格吸饱水变成湿土（SetPhase，热量随物质留在原格），
    //             并且**那一格水被吸干**（Clear）：一格水润湿一格土，随后水就消失了。
    //             吸水只看正上方一格，不横向、不反向——水怎么继续往土里走由 WetSoilParticle 负责。
    //       运动：带 Solid 标签、受重力、只做「下落 + 斜落」，因此同样是粉末，在平地与同类上安分不动。
    //       与沙的区别是两条规则叠加：
    //         - 摩擦度 0.5：土粒要连续 2 格净空才肯斜滑（落点正下方被顶着就停），单独作用时是 2:1 的堆；
    //         - 粘性 0.5：旁边有固体邻居（斜滑反侧同一行）时，安息角再抬一档到 3 格净空，堆体约 3:1；
    //                     孤立、旁边没粒子的土粒不受粘性影响，仍按 2 格净空滑落。
    //       于是土堆明显比 1:1 的沙堆陡，又远不及 10:1 的石头堆——「成块的土」而不是「滑溜的沙」。
    public sealed class SoilParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(86, 62, 40, 255),
            new Color32(105, 78, 50, 255),
            new Color32(124, 96, 64, 255),
            new Color32(143, 114, 80, 255),
        };

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Solid,
            density: 1.8f,
            heatCapacity: 0.80f,   // 干土：与石头并列，固体里偏小
            thermalConductivity: 0.70f,   // 干土：固体里导热居中
            thermalEmissivity: 1.0f,     // 近黑体
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 0,
            friction: 0.5f,
            stickiness: 0.5f,
            palette: Palette,
            debugName: nameof(SoilParticle));

        public void OnLogic(ParticleWorld world, int x, int y)
        {
            // 吸水：**只看正上方一格**，是水就把那一格水喝掉——本格变成湿土（SetPhase 保留本格 Q），
            // 水格被 Clear（它的热量先并进本格，物质带热，避免「水一接触土、热凭空消失」）。
            // 上面是水的这一格，扫过它时本逻辑帧已经处理完了（扫描自上而下），因此在这里清掉它不会漏掉它的更新。
            // 湿土怎么把这份水再传下去由 WetSoilParticle 负责（水现在存在湿土这一格里了）。
            if ((ParticleRules.TagsAt(world, x, y + 1) & ParticleTags.Liquid) != 0)
            {
                world.SetPhase(x, y, ParticleIds.WetSoil);
                world.Heat.AddHeat(x, y, world.Heat.GetHeat(x, y + 1));
                world.Clear(x, y + 1);
            }
        }

        public void OnMotion(ParticleWorld world, int x, int y)
        {
            // 摩擦度 0.5 → 2 格净空；旁边有固体邻居时粘性 0.5 再抬一档到 3 格：
            // 正下被挡住时若落点下方还被顶着就视为稳定，于是坡面比沙的 45° 陡得多，约 3:1。
            ParticleRules.StepSolid(world, x, y);
        }

        public void OnRandomTick(ParticleWorld world, int x, int y)
        {
            // 土暂无随机刻行为：润湿是即时的局部反应（正上方是水就吸水、是湿土就跟着湿），走逻辑钩子而非随机刻。
        }

        public void OnSpawn(ParticleWorld world, int x, int y)
        {
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 土被覆盖时无残留效果。
        }
    }
}
