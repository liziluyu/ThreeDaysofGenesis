using System;
using System.Collections.Generic;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>对话文件（JSON 顶层）：一段对话，从头到尾把台词按顺序播一遍。</summary>
    [Serializable]
    public sealed class DialogueFile
    {
        /// <summary>对话标识（调试 / 日志用）。</summary>
        public string id;

        /// <summary>对话播完派发的事件名；空 = 无事件。</summary>
        public string onEnd;

        /// <summary>本对话用到的角色表（speaker id → 显示名 + 立绘/头像资源名）。</summary>
        public List<DialogueCharacter> characters;

        /// <summary>整段对话按顺序播的台词；播到尾巴就派发 <see cref="onEnd"/>。</summary>
        public List<DialogueLine> lines;
    }

    /// <summary>
    /// 对话文件里的一个角色：<c>lines[].speaker</c> 对应的显示名。
    /// **立绘与头像共用一个 id**（就是 <see cref="id"/>）——运行时去「立绘文件夹」和「头像文件夹」用**同一个名字**各找一次。
    /// </summary>
    [Serializable]
    public sealed class DialogueCharacter
    {
        /// <summary>角色 id（与 <c>lines[].speaker</c> 对应，同时作为两个资源文件夹里的文件名）。</summary>
        public string id;

        /// <summary>显示在名字框里的名字；空则回退用 id。</summary>
        public string displayName;
    }

    /// <summary>一句台词。</summary>
    [Serializable]
    public sealed class DialogueLine
    {
        /// <summary>角色 id（查角色表得名字 / 头像 / 立绘）；空 = 旁白。</summary>
        public string speaker;

        /// <summary>台词内容。</summary>
        public string text;
    }

    /// <summary>
    /// 对话文件的解析入口（<c>UnityEngine.JsonUtility</c>，免第三方依赖；不支持字典 / 多态，但此处用不到）。
    /// 失败不抛异常、不打日志，由调用方（控制器）决定如何报错——便于 EditMode 测试。
    /// </summary>
    public static class DialogueData
    {
        /// <summary>把 JSON 解析成 <see cref="DialogueFile"/>。失败返回 false 并给 <paramref name="error"/>。</summary>
        public static bool TryParse(string json, out DialogueFile file, out string error)
        {
            file = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "对话 JSON 为空。";
                return false;
            }

            DialogueFile parsed;
            try
            {
                parsed = JsonUtility.FromJson<DialogueFile>(json);
            }
            catch (Exception exception)
            {
                error = "对话 JSON 解析失败：" + exception.Message;
                return false;
            }

            if (parsed == null)
            {
                error = "对话 JSON 解析结果为空。";
                return false;
            }

            if (parsed.lines == null || parsed.lines.Count == 0)
            {
                error = "对话缺少 lines（至少一句）。id = " + (parsed.id ?? "<null>");
                return false;
            }

            // 归一化：缺省字段补成空集合，Runner 里不必反复判空。
            if (parsed.characters == null)
            {
                parsed.characters = new List<DialogueCharacter>();
            }

            file = parsed;
            return true;
        }
    }
}
