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
        /// 受热蒸发成水汽（进入湿度场），是湿度场唯一的水汽来源。随机刻命中时把半径 2 格内的第一个
        /// 土壤/湿土/腐殖土冲刷成沙。</summary>
        public static ushort Water;

        /// <summary>大气。气体：空气粒子化，浮力 + 热压流的载体（海陆风），也是云的载体（含凝结物的空气）。字段不叫 Air——Air 已被 typeId 0（空）占用。</summary>
        public static ushort Atmosphere;

        /// <summary>湿土。土吸水后的形态（固体标签）：堆形与干土一致，颜色更深，比干土更重、更扛热、导热更好。
        /// 由 <c>SoilParticle</c> 在正上方是水（吸干那格水）时生成，自身负责把这份水交给邻格的干土或蒸发进空气。
        /// **只能追加到清单末尾**。</summary>
        public static ushort WetSoil;

        /// <summary>草种子。受重力的固体粉末（摩擦/粘性 0.5，堆形与土一致）：随机刻命中且八邻有湿土时，
        /// 把自己与上方 1-3 格（随机高度）替换成草；连续 3 次命中都无发芽条件则霉烂成腐殖质
        /// （计数存在本格 flags 的 bit5-6）。不可在编辑器中按半径刷，只能单颗投放（见 ParticleEditor 白名单）。</summary>
        public static ushort GrassSeed;

        /// <summary>草。固体（靠空的 OnMotion 保持不动，而非 IsStatic——静态类型收不到随机刻）：
        /// 由草种子生长生成。每逻辑帧三种死法：被上方固/液（非草）压死（死亡向下传播）、失去扎根
        /// （下方非土/湿土/腐殖土）而死（死亡向上传播）、温度 &gt; 50 或 &lt; 3 时死亡。随机刻命中时 10% 概率
        /// 在半径 2 格内撒下一颗新草种子，并把半径 2 格内的第一个沙变成土。不可在编辑器中选择。</summary>
        public static ushort Grass;

        /// <summary>腐殖质。有机物（带 Organic 标签）死亡时以 20% 概率就地生成；固体、短暂中间态——
        /// 每逻辑帧尝试把八邻的第一个土壤变成腐殖土，然后自己消失。不可在编辑器中选择。</summary>
        public static ushort Humus;

        /// <summary>腐殖土。土壤被腐殖质改良后的黑色形态；固体，与干土同款粉末运动，稳定不再变化。
        /// 不可在编辑器中选择。</summary>
        public static ushort HumusSoil;
    }
}
