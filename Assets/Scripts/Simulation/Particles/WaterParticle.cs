using UnityEngine;

namespace TapTap2026.Simulation
{
    // WaterParticle —— 水（液体标签，摩擦度/粘性不读，填 0；热容 4.2，约为固体的 2~4 倍）
    //
    // flags 私有位：无（bit3-7 全部空闲）
    // 邻居查询范围：正下、左下/右下、同行左右各 MaxFlowSpread 格（横流扫描）
    // 交互的 ParticleIds：Steam —— 本格温度 ≥ BoilingPoint 时蒸发成蒸汽（相变时手动携带热量，见 OnUpdate）
    //
    // 行为：开头先查温度做相变（达到沸点 → 蒸汽），然后才是液体运动。运动为三段固定优先级，成功即 return：
    //         1. TryFall：正下能进（空气 / 气体）就下落一格；同类水与固体都挡住；
    //         2. TryDiagonalFall（clearance 1）：正下被挡就沿坡斜滑——液体无安息角；
    //         3. TryFlowHorizontal：左右随机序逐格扫描，移动到范围内第一个空气格，
    //            单 tick 最多横跳 MaxFlowSpread 格——水因此在平地上摊成水平面。
    //       三处都走不通则本 tick 静止（比如躺在水底）。
    //       跨类型交互全部由标签 rank 产生：固体（rank 3）沉穿水，水被置换上浮；
    //       同类水互不置换但横流时可互相穿过（CanFlowThrough 对流体放行同 rank）。
    public sealed class WaterParticle : IParticleType
    {
        private static readonly Color32[] Palette =
        {
            new Color32(52, 108, 196, 255),
            new Color32(62, 122, 210, 255),
            new Color32(74, 136, 222, 255),
            new Color32(88, 150, 232, 255),
        };

        /// <summary>沸点：本格温度达到它就剧烈蒸发成蒸汽。生产热力参数（传导 0.24）下水面峰值只有 ~56°，故从 60 下移到 40，保持水循环不停摆。</summary>
        public const float BoilingPoint = 40f;

        /// <summary>常温蒸发下限：达到它之后即使不到沸点也缓慢蒸发（现实里水在任何温度都蒸发）。随沸点同比下移到 20。</summary>
        public const float SlowEvaporationTemperature = 20f;

        /// <summary>常温蒸发概率分母（1/N 每 tick）。</summary>
        public const int SlowEvaporationDenominator = 300;

        public ParticleTypeInfo Info => new ParticleTypeInfo(
            tags: ParticleTags.Liquid,
            density: 1.0f,
            heatCapacity: 4.2f,
            thermalConductivity: 0.95f,   // 水：导热最好，水体内部接近等温
            affectedByGravity: true,
            isStatic: false,
            maxFlowSpread: 5,
            friction: 0f,
            stickiness: 0f,
            palette: Palette,
            debugName: nameof(WaterParticle));

        public void OnUpdate(ParticleWorld world, int x, int y)
        {
            // 相变优先于运动。Set 换类型会把热量重置为环境值（契约 §6：热由物质携带，换上新材料重新计），
            // 因此先把旧格的 Q 抄下来、变完再写回——蒸汽带着水的热量出生
            // （热容 4.2 → 2，同一份热温度翻两倍多，浮力把它往上推）。
            float heat = world.Heat.GetHeat(x, y);
            float temperature = world.Heat.GetTemperature(x, y);

            // 剧烈蒸发：达到沸点必定蒸发。
            // 常温蒸发：不到沸点但够暖时以低概率蒸发——现实里水在任何温度都蒸发，
            //           海洋不用被烤沸也在持续向大气供水。
            bool evaporates = temperature >= BoilingPoint
                || (temperature >= SlowEvaporationTemperature
                    && world.NextUInt() % SlowEvaporationDenominator == 0u);
            if (evaporates)
            {
                world.Set(x, y, ParticleIds.Steam);
                world.Heat.SetHeat(x, y, heat);
                return;
            }

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
            // shade 已由 ParticleWorld.Set 按本类型调色板抽样，无需额外初始化。
        }

        public void OnKill(ParticleWorld world, int x, int y, ushort killerTypeId)
        {
            // 水被覆盖时无残留效果。
        }
    }
}
