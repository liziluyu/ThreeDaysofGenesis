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

        /// <summary>石头。受重力的固体，摩擦度 0.9（10:1 的陡尖堆）。</summary>
        public static ushort Stone;

        /// <summary>沙。受重力的粉末，摩擦度 0（1:1 安息角，45°）。</summary>
        public static ushort Sand;

        /// <summary>土壤。受重力的粉末，摩擦度 0.5（2:1 安息角，安息角比沙陡一倍）。</summary>
        public static ushort Soil;
    }
}
