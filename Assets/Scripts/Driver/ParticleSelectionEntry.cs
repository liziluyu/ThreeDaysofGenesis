using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 编辑器里**一个可投放项**的描述符：一种可手投的粒子类型，或一个动物物种。
    /// </summary>
    /// <remarks>
    /// <para>只读快照：建好后不再变。Inspector 白名单或阶段投放名单（<see cref="ParticleEditor.SetPlacementFilter"/>）
    /// 变化时由 <see cref="ParticleEditor"/> 整体重建，因此 <see cref="Index"/> 只在一次构建内有效。</para>
    /// <para><b>用途</b>：给未来的投放选择按钮（粒子调色板 / 物种按钮）接线——按钮只读本对象决定显示什么
    /// （<see cref="DisplayName"/> / <see cref="Swatch"/> / <see cref="SinglePlacement"/> / <see cref="IsCreature"/>），
    /// 选中动作调 <see cref="ParticleEditor.Select(ParticleSelectionEntry)"/> / <see cref="ParticleEditor.SelectByName"/> /
    /// <see cref="ParticleEditor.SelectByTypeId"/>。</para>
    /// </remarks>
    public sealed class ParticleSelectionEntry
    {
        private ParticleSelectionEntry(int index, ushort typeId, int creatureIndex, string displayName, string className, Color swatch, bool singlePlacement)
        {
            Index = index;
            TypeId = typeId;
            CreatureIndex = creatureIndex;
            DisplayName = displayName;
            ClassName = className;
            Swatch = swatch;
            SinglePlacement = singlePlacement;
        }

        /// <summary>在 <see cref="ParticleEditor.Selections"/> 里的下标（= 右键循环顺序）。</summary>
        public int Index { get; }

        /// <summary>粒子类型 id；动物项恒为 0（空气，无意义）。</summary>
        public ushort TypeId { get; }

        /// <summary>动物物种在 <see cref="CreatureSpawns.Species"/> 里的下标（即生成命令的物种下标）；粒子项恒为 −1。</summary>
        public int CreatureIndex { get; }

        /// <summary>本项是不是动物物种（true ⇒ 左键放置实体、<see cref="TypeId"/> 无意义）。</summary>
        public bool IsCreature
        {
            get { return CreatureIndex >= 0; }
        }

        /// <summary>显示名（中文）：粒子取 <c>DebugName</c>（缺失回退类名），动物取物种调试名。</summary>
        public string DisplayName { get; }

        /// <summary>类名（粒子 = 类型类名、动物 = 物种类名）：与 <see cref="DisplayName"/> 一起构成
        /// <see cref="ParticleEditor.SelectByName"/> 的匹配规则——阶段 JSON 与场景接线用**语言无关的类名**，
        /// 界面上显示 <see cref="DisplayName"/>。</summary>
        public string ClassName { get; }

        /// <summary>代表色：粒子取调色板首色，动物取物种占位色（鼠标旁那个色块用它）。</summary>
        public Color Swatch { get; }

        /// <summary>是否按「单颗 + 冷却」投放：单颗投放白名单里的粒子类型，以及全部动物物种。</summary>
        public bool SinglePlacement { get; }

        /// <summary>粒子类型项（<paramref name="displayName"/> / <paramref name="className"/> / <paramref name="swatch"/>
        /// 由调用方从注册表取）。</summary>
        public static ParticleSelectionEntry ForParticle(int index, ushort typeId, string displayName, string className,
            Color swatch, bool singlePlacement)
        {
            return new ParticleSelectionEntry(index, typeId, -1, displayName, className, swatch, singlePlacement);
        }

        /// <summary>动物物种项（恒为「单颗 + 冷却」投放，见 <see cref="ParticleEditor.SubmitPlacement"/>）。</summary>
        public static ParticleSelectionEntry ForCreature(int index, int creatureIndex, string displayName, string className,
            Color swatch)
        {
            return new ParticleSelectionEntry(index, 0, creatureIndex, displayName, className, swatch, true);
        }
    }
}
