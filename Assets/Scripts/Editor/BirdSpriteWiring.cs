using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Editor
{
    /// <summary>
    /// 鸟美术接线工具：把 <c>bird.png</c> 的全部切片（按切片名序 <c>bird_0..bird_6</c>）接进
    /// <c>SampleScene</c> 里动物渲染器的 <c>_birdIdleFrames</c> 序列化数组。
    /// 菜单 <c>Tools/Animals/Wire Bird Sprites</c>（同签名兼作 <c>-executeMethod</c> 入口）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么是「定点 YAML 写入」而不是 <c>EditorSceneManager.SaveScene</c>：</b>整场景保存会把
    /// **所有**序列化字段重写一遍，包括与本次改动无关、且当前提交的场景里恰好缺席的历史遗留字段——
    /// 实测保存后场景会多出一行 <c>SoundController._driver: {fileID: 0}</c>，把 diff 从「只加鸟帧」污染成两处。
    /// 本工具因此只动一处：定位 <c>_fishIdleFrames:</c> 锚点、在它的数组之后插入 <c>_birdIdleFrames:</c> 块
    /// （已存在则先删旧块，天然幂等）。fileID / guid 一律从真实 sprite 资产查得，不手写、
    /// 与 Unity 自身的序列化格式逐字一致。工具是场景的唯一写入者（AGENTS.md 禁止手改 YAML）。</para>
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

        /// <summary>插入锚点字段（鸟帧数组插在它之后）。</summary>
        private const string AnchorField = "_fishIdleFrames:";

        /// <summary>目标字段。</summary>
        private const string TargetField = "_birdIdleFrames:";

        /// <summary>把鸟切片按名序接进场景并保存。可反复运行，输出幂等。</summary>
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

            string fullPath = Path.GetFullPath(ScenePath);
            if (!File.Exists(fullPath))
            {
                Debug.LogError("[BirdSpriteWiring] 场景文件不存在：" + fullPath);
                return;
            }

            var lines = new List<string>(File.ReadAllLines(fullPath));

            // 幂等：先移除可能已存在的旧 _birdIdleFrames 块。
            RemoveFieldBlock(lines, TargetField);

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

            var block = new List<string> { "  " + TargetField };
            foreach (Sprite sprite in frames)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
                block.Add("  - {fileID: " + localId + ", guid: " + guid + ", type: 3}");
            }

            lines.InsertRange(insertAt, block);

            // 行尾统一 LF（场景文件本就是 LF），末尾补一个换行。
            File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
            AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceUpdate);
            Debug.Log(
                "[BirdSpriteWiring] 已把 " + frames.Length + " 片 bird sprite 接进 " + TargetField
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

        /// <summary>返回第一个 trim 后等于 <paramref name="field"/> 的行号；找不到返回 −1。</summary>
        private static int IndexOfTrimmed(List<string> lines, string field)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd().TrimStart() == field)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>移除某个字段行及其紧随其后的全部数组条目（"  - " 前缀）行；字段不存在则什么都不做。</summary>
        private static void RemoveFieldBlock(List<string> lines, string field)
        {
            int index = IndexOfTrimmed(lines, field);
            if (index < 0)
            {
                return;
            }

            int end = index + 1;
            while (end < lines.Count && lines[end].TrimStart().StartsWith("- "))
            {
                end++;
            }

            lines.RemoveRange(index, end - index);
        }
    }
}
