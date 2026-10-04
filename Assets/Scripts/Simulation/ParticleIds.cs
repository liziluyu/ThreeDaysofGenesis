namespace TapTap2026.Simulation
{
    /// <summary>
    /// 类型 id 的跨类型引用入口。禁止在热路径写裸数字 typeId，一律用这里的字段。
    /// 字段由 <see cref="ParticleRegistration.RegisterAll"/> 在注册时回填。
    /// </summary>
    /// <remarks>
    /// 注册顺序 = id = 存档兼容性的一部分。一旦有存档或回放，**禁止重排已有顺序**，只能追加。
    /// </remarks>
    public static class ParticleIds
    {
        /// <summary>空气。恒为 0。</summary>
        public const ushort Air = ParticleRegistry.AirTypeId;

        // ================= 新增类型在此追加字段（只能追加，不能插队） =================

        /// <summary>石头。受重力的固体，摩擦度 0.9 + 高粘性 0.9（靠着石头时外侧几乎不滚，垛得很高）。</summary>
        public static ushort Stone;

        /// <summary>沙。受重力的粉末，摩擦度 0（1:1 安息角，45°）。</summary>
        public static ushort Sand;

        /// <summary>土壤。受重力的粉末，摩擦度 0.5 + 粘性 0.5（旁边有固体时约 3:1 的安息角）。</summary>
        public static ushort Soil;

        /// <summary>水。受重力的液体（Liquid 标签）：下落 → 斜滑 → 横流摊平，无安息角；被固体沉穿。
        /// 受热蒸发成水汽（进入湿度场），是湿度场唯一的水汽来源。</summary>
        public static ushort Water;

        /// <summary>大气。气体：空气粒子化，浮力 + 热压流的载体（海陆风），也是云的载体（含凝结物的空气）。字段不叫 Air——Air 已被 typeId 0（空）占用。</summary>
        public static ushort Atmosphere;

        /// <summary>湿土。土吸水后的形态（固体标签）：堆形与干土一致，颜色更深，比干土更重、更扛热、导热更好。
        /// 由 <c>SoilParticle</c> 在正上方是水（吸干那格水）时生成，自身负责把这份水交给邻格的干土或蒸发进空气。
        /// **只能追加到清单末尾**。</summary>
        public static ushort WetSoil;
    }
}
