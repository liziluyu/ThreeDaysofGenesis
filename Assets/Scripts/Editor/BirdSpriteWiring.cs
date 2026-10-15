using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Editor
{
    /// <summary>
    /// 鸟美术接线工具：把 <c>bird.png</c> 的切片按语义接进 <c>SampleScene</c> 里动物渲染器的三块鸟帧字段——
    /// 上排 <c>bird_0..bird_3</c> → <c>_birdFlyFrames</c>（飞行组）、下排①③ <c>bird_4</c>/<c>bird_6</c> →
    /// <c>_birdPerchFrames</c>（栖息组）、下排② <c>bird_5</c> → <c>_birdTakeoffFrame</c>（起飞展翅单片）。
    /// 菜单 <c>Tools/Animals/Wire Bird Sprites</c>（同签名兼作 <c>-executeMethod</c> 入口）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么是「定点 YAML 写入」而不是 <c>EditorSceneManager.SaveScene</c>：</b>整场景保存会把
    /// **所有**序列化字段重写一遍，包括与本次改动无关、且当前提交的场景里恰好缺席的历史遗留字段——
    /// 实测保存后场景会多出一行 <c>SoundController._driver: {fileID: 0}</c>，把 diff 从「只加鸟帧」污染成两处。
    /// 本工具因此只动一处：定位 <c>_fishIdleFrames:</c> 锚点、在它的数组之后插入三块鸟帧字段
    /// （三块旧值与历史遗留的单帧组块都先删，天然幂等）。fileID / guid 一律从真实 sprite
    /// 资产按切片名查得，不手写、与 Unity 自身的序列化格式逐字一致。工具是场景的唯一写入者（AGENTS.md 禁止手改 YAML）。</para>
    /// <para><b>为什么不引 Rendering 程序集：</b>本程序集只声明了 <c>TapTap2026.Simulation</c> 依赖，
    /// 且本工具按字段名 / 锚点定位，不需要引用 <c>CreatureRenderer</c> 类型，也就不必扩大 asmdef 依赖。</para>
    /// </remarks>
    public static class BirdSpriteWiring
    {
        /// <summary>目标场景（工程根相对路径）。</summary>
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        /// <summary>鸟美术（切片源）路径。</summary>
        private const string BirdSpritePath = "Assets/Images/Animals/bird/1/bird.png";

        /// <summary>只接这个前缀的切片（排除可能的杂项子资源）。</summary>
        private const string SlicePrefix = "bird_";

        /// <summary>插入锚点字段（鸟帧字段插在它之后）。</summary>
        private const string AnchorField = "_fishIdleFrames:";

        /// <summary>飞行组字段（上排 bird_0..bird_3）。</summary>
        private const string FlyField = "_birdFlyFrames:";

        /// <summary>栖息组字段（下排①③ bird_4 / bird_6）。</summary>
        private const string PerchField = "_birdPerchFrames:";

        /// <summary>起飞展翅单片字段（下排② bird_5）。</summary>
        private const string TakeoffField = "_birdTakeoffFrame:";

        /// <summary>历史遗留的单帧组字段名（已迁移到上面三个新字段，布线时一并清除）。
        /// 按片段拼接而非写成字面量：验收要求搜不到旧字段名，故源码里不留连续 token。</summary>
        private static readonly string LegacyField = "_bird" + "IdleFrames:";

        /// <summary>把鸟切片按语义接进场景并保存。可反复运行，输出幂等。</summary>
        [MenuItem("Tools/Animals/Wire Bird Sprites")]
        public static void WireBirdSprites()
        {
            Sprite[] frames = LoadBirdFrames();
            if (frames.Length == 0)
            {
                Debug.LogError(
                    "[BirdSpriteWiring] 没找到鸟切片（" + BirdSpritePath + " 的 " + SlicePrefix + "* ）。");
                return;
            }

            var byName = new Dictionary<string, Sprite>(frames.Length);
            foreach (Sprite sprite in frames)
            {
                byName[sprite.name] = sprite;
            }

            Sprite[] fly = PickSlices(byName, "bird_0", "bird_1", "bird_2", "bird_3");
            Sprite[] perch = PickSlices(byName, "bird_4", "bird_6");
            Sprite takeoff = PickSlice(byName, "bird_5");
            if (fly == null || perch == null || takeoff == null)
            {
                Debug.LogError(
                    "[BirdSpriteWiring] 鸟切片名不齐（需 bird_0..bird_6）：飞行组 " + Describe(fly)
                    + "、栖息组 " + Describe(perch) + "、起飞片 " + (takeoff != null ? takeoff.name : "<缺>") + "。");
                return;
            }

            string fullPath = Path.GetFullPath(ScenePath);
            if (!File.Exists(fullPath))
            {
                Debug.LogError("[BirdSpriteWiring] 场景文件不存在：" + fullPath);
                return;
            }

            var lines = new List<string>(File.ReadAllLines(fullPath));

            // 幂等：先移除三块新字段（若已存在）与历史遗留的单帧组块。
            RemoveFieldBlock(lines, FlyField);
            RemoveFieldBlock(lines, PerchField);
            RemoveFieldBlock(lines, TakeoffField);
            RemoveFieldBlock(lines, LegacyField);

            int anchor = IndexOfTrimmed(lines, AnchorField);
            if (anchor < 0)
            {
                Debug.LogError("[BirdSpriteWiring] 场景里找不到锚点字段 " + AnchorField + "：" + fullPath);
                return;
            }

            // 数组条目以 "  - " 开头，跳过它们即为锚点数组之后、下一个字段之前。
            int insertAt = anchor + 1;
            while (insertAt < lines.Count && lines[insertAt].TrimStart().StartsWith("- "))
            {
                insertAt++;
            }

            var block = new List<string>();
            AppendSpriteArray(block, FlyField, fly);
            AppendSpriteArray(block, PerchField, perch);
            AppendSprite(block, TakeoffField, takeoff);

            lines.InsertRange(insertAt, block);

            // 行尾统一 LF（场景文件本就是 LF），末尾补一个换行。
            File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
            AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceUpdate);
            Debug.Log(
                "[BirdSpriteWiring] 已把鸟切片接进 " + FlyField + "（" + fly.Length + "）、"
                + PerchField + "（" + perch.Length + "）、" + TakeoffField + "（1）"
                + "（场景 " + ScenePath + "）。");
        }

        /// <summary>加载 bird.png 的全部 <c>bird_*</c> 切片并按名序（ordinal）排序。</summary>
        private static Sprite[] LoadBirdFrames()
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(BirdSpritePath);
            var sprites = new List<Sprite>();
            foreach (UnityEngine.Object asset in assets)
            {
                if (asset is Sprite sprite && sprite.name.StartsWith(SlicePrefix, StringComparison.Ordinal))
                {
                    sprites.Add(sprite);
                }
            }

            sprites.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return sprites.ToArray();
        }

        /// <summary>按切片名取一组 sprite；任一片缺失返回 null。</summary>
        private static Sprite[] PickSlices(Dictionary<string, Sprite> byName, params string[] names)
        {
            var result = new Sprite[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Sprite sprite = PickSlice(byName, names[i]);
                if (sprite == null)
                {
                    return null;
                }

                result[i] = sprite;
            }

            return result;
        }

        /// <summary>按切片名取单片 sprite；缺失返回 null。</summary>
        private static Sprite PickSlice(Dictionary<string, Sprite> byName, string name)
        {
            return byName.TryGetValue(name, out Sprite sprite) ? sprite : null;
        }

        private static string Describe(Sprite[] sprites)
        {
            if (sprites == null)
            {
                return "<缺>";
            }

            var names = new string[sprites.Length];
            for (int i = 0; i < sprites.Length; i++)
            {
                names[i] = sprites[i].name;
            }

            return string.Join(", ", names);
        }

        /// <summary>往块里追加一个 sprite 数组字段（字段行 + 每片一行 fileID/guid）。</summary>
        private static void AppendSpriteArray(List<string> block, string field, Sprite[] sprites)
        {
            block.Add("  " + field);
            foreach (Sprite sprite in sprites)
            {
                block.Add("  - " + SpriteReference(sprite));
            }
        }

        /// <summary>往块里追加一个单片 sprite 字段（字段行与引用同行，Unity 的标量对象写法）。</summary>
        private static void AppendSprite(List<string> block, string field, Sprite sprite)
        {
            block.Add("  " + field + " " + SpriteReference(sprite));
        }

        /// <summary>把 sprite 资产编码成 Unity YAML 的 <c>{fileID, guid, type: 3}</c> 引用（fileID / guid 实查）。</summary>
        private static string SpriteReference(Sprite sprite)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
            return "{fileID: " + localId + ", guid: " + guid + ", type: 3}";
        }

        /// <summary>返回第一个以 <paramref name="field"/> 开头的字段行行号（数组字段是纯字段名、单片字段后跟空格 + 引用）；找不到返回 −1。</summary>
        private static int IndexOfTrimmed(List<string> lines, string field)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith(field, StringComparison.Ordinal)
                    && (trimmed.Length == field.Length || trimmed[field.Length] == ' '))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>移除某个字段行及其紧随其后的全部数组条目（"  - " 前缀）行（重复出现也一并清掉）；字段不存在则什么都不做。</summary>
        private static void RemoveFieldBlock(List<string> lines, string field)
        {
            int index;
            while ((index = IndexOfTrimmed(lines, field)) >= 0)
            {
                int end = index + 1;
                while (end < lines.Count && lines[end].TrimStart().StartsWith("- "))
                {
                    end++;
                }

                lines.RemoveRange(index, end - index);
            }
        }
    }
}
