namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物死亡的**确定性轮廓**：16×16 精灵像素空间里每格是否属于尸体，以及它继承的颜色档。
    /// 与 <c>CreatureDeathMaterializer</c> 配套——后者按固定映射把轮廓逐格写进世界的生物质。
    /// </summary>
    /// <remarks>
    /// <para><b>数据布局：</b><see cref="Cells"/> 长 256（<c>Size × Size</c>），下标 <c>dy * 16 + dx</c>：
    /// 行序 = <c>dy</c> 升序（<c>dy 0</c> = 精灵**底边**所在行），行内 <c>dx</c> 升序（<c>dx 0</c> = 精灵左列）。
    /// 每格 <c>-1</c> = 空；否则 = <see cref="BiomassParticle"/> 调色板的颜色索引（0..5），写作 <c>Cell.shade</c>。</para>
    /// <para><b>数据来源 = 生成器产物：</b>轮廓不是手写常量，而是 <see cref="CreatureArtData"/>（由
    /// 编辑器菜单 <c>Tools/Animals/Regenerate Creature Art Data</c> 读 PNG 与切片 pivot 生成的纯数据 .cs）。
    /// 换美术后重跑该菜单并提交生成物；<c>CreatureArtDataTests</c> 现场从 PNG 复算对拍，忘记重跑即测试红。
    /// 运行期模拟仍**只读这份纯数据、不读纹理**——渲染资源依赖、平台差异与确定性风险都不进模拟层。
    /// 逐格颜色也只取索引（<c>Cell</c> 只有 4 字节，存不下任意 RGB），渲染再从 <see cref="BiomassParticle"/> 调色板查 <c>Color32</c>。</para>
    /// <para><b>世界定位</b>（由 <c>CreatureDeathMaterializer</c> 执行，与 <c>CreatureRenderer</c> 的摆放公式一致：
    /// sprite pivot 点落在 <c>(X, Y)</c>、水平居中原点 = <c>X</c>、1 源像素 = 1 格）：
    /// <c>世界 x = floor(creature.X) - 8 + dx</c>、
    /// <c>世界 y = floor(creature.Y) - <see cref="PivotRowCells"/> + dy</c>。
    /// 减去 <see cref="PivotRowCells"/>（切片 pivot.y，自底像素）让尸体与渲染身体的 pivot 摆放**逐格对齐**
    /// （鱼 pivot = 底边上 5px ⇒ 尸体不再比可见身体高 5 格）。</para>
    /// </remarks>
    public sealed class BiomassProfile
    {
        /// <summary>轮廓边长（格）；也是精灵的像素宽高（PPU = 16 ⇒ 1 像素 = 1 格）。</summary>
        public const int Size = 16;

        /// <summary>轮廓格数（<c>Size × Size</c>）。</summary>
        public const int Area = Size * Size;

        /// <summary>空格的标记值。</summary>
        public const sbyte Empty = -1;

        /// <summary>长度 <see cref="Area"/> 的轮廓数据（<c>dy * 16 + dx</c>；-1 = 空，否则 = 颜色索引）。</summary>
        public readonly sbyte[] Cells;

        /// <summary>
        /// 切片 pivot 距精灵**底边**的像素行数（自底，= 生成物 <c>PivotRowPixels</c>）。
        /// 物质化把 <c>dy 0</c> 对齐到 <c>floor(Y) − PivotRowCells</c>，与渲染按 pivot 摆放一致。
        /// </summary>
        public readonly int PivotRowCells;

        private BiomassProfile(sbyte[] cells, int pivotRowCells)
        {
            Cells = cells;
            PivotRowCells = pivotRowCells;
        }

        /// <summary>按物种选轮廓：兔子用 <see cref="Rabbit"/>，其余（含 null）用 <see cref="Fish"/>。
        /// 死因不参与选择——四种死因轮廓相同。</summary>
        /// <param name="creature">死亡的动物实体。</param>
        /// <returns>该物种的轮廓。</returns>
        public static BiomassProfile For(Creature creature)
        {
            return creature is RabbitCreature ? Rabbit : Fish;
        }

        // 轮廓数据来自生成物 CreatureArtData（菜单 Tools/Animals/Regenerate Creature Art Data 产出）。
        // 物种实例必须**声明在数据之后**：静态字段初始化按文本顺序执行，写反了会拿到 null 表。
        /// <summary>兔子轮廓（陆生物种）。</summary>
        public static readonly BiomassProfile Rabbit = new BiomassProfile(
            CreatureArtData.Rabbit.BiomassCells,
            CreatureArtData.Rabbit.PivotRowPixels);

        /// <summary>鱼轮廓（水生物种）。</summary>
        public static readonly BiomassProfile Fish = new BiomassProfile(
            CreatureArtData.Fish.BiomassCells,
            CreatureArtData.Fish.PivotRowPixels);
    }
}
