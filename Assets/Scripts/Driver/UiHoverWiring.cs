using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace TapTap2026.Game
{
    /// <summary>
    /// 按钮悬停监听的运行期绑定：给一颗按钮补上 <see cref="EventTrigger"/> 的指针进入 / 离开条目，
    /// 供 <see cref="LevelSelectController"/>（关卡描述提示）与 <see cref="ParticleSelectionPanel"/>
    /// （投放项悬停上报）共用同一条路径。
    /// </summary>
    /// <remarks>
    /// <para>运行期绑定、场景里不留持久监听（<c>SceneUiScaffolder</c> 会清掉按钮上的旧持久监听，免得重复触发），
    /// 因此场景资产里不摆 <see cref="EventTrigger"/>、也不摆监听条目。</para>
    /// <para><b>只记自有组件</b>：调用方把「本次新建」的 <see cref="EventTrigger"/> 记进自己的列表，
    /// <see cref="Detach"/> 只销毁这些——按钮上原本就有的悬停组件一律不碰（复用不等于跳过：
    /// 复用的那个也要补上缺的条目）。</para>
    /// <para>已有同 eventID 的条目一律跳过，因此同一颗按钮重复走一遍绑定路径不会叠加重复监听。</para>
    /// </remarks>
    internal static class UiHoverWiring
    {
        /// <summary>
        /// 给 <paramref name="target"/> 挂上悬停监听：指针进入调 <paramref name="onEnter"/>、离开调
        /// <paramref name="onExit"/>（各补一条空条目）。<paramref name="target"/> 上没有
        /// <see cref="EventTrigger"/> 才加一个，并把**新建的**那个记进 <paramref name="owned"/>——
        /// 调用方随后（OnDestroy 里）只销毁这个名单里的组件。<paramref name="target"/> 为 null 直接返回。
        /// </summary>
        /// <param name="target">挂监听的按钮对象（面板 / 关卡按钮自己）。</param>
        /// <param name="onEnter">PointerEnter 回调。</param>
        /// <param name="onExit">PointerExit 回调。</param>
        /// <param name="owned">调用方记自有悬停组件的列表（已有组件时不追加）。</param>
        public static void Attach(GameObject target, UnityAction<BaseEventData> onEnter, UnityAction<BaseEventData> onExit,
            List<EventTrigger> owned)
        {
            if (target == null)
            {
                return;
            }

            EventTrigger trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = target.AddComponent<EventTrigger>();
                owned.Add(trigger);
            }

            AddEntry(trigger, EventTriggerType.PointerEnter, onEnter);
            AddEntry(trigger, EventTriggerType.PointerExit, onExit);
        }

        /// <summary>销毁 <paramref name="owned"/> 里记着的悬停组件（只销毁调用方自己加的那些），然后清空名单。</summary>
        /// <remarks>写全 <see cref="UnityEngine.Object.Destroy"/>：本类同时用到 <c>object</c> / <c>Destroy</c> 的语境下，
        /// 短名容易让人以为在说别的销毁入口。</remarks>
        public static void Detach(List<EventTrigger> owned)
        {
            for (int i = 0; i < owned.Count; i++)
            {
                if (owned[i] != null)
                {
                    UnityEngine.Object.Destroy(owned[i]);
                }
            }

            owned.Clear();
        }

        /// <summary>给 <paramref name="trigger"/> 补一条指定类型的空条目并挂上回调；已有同 eventID 的条目则跳过。</summary>
        private static void AddEntry(EventTrigger trigger, EventTriggerType eventId, UnityAction<BaseEventData> callback)
        {
            List<EventTrigger.Entry> entries = trigger.triggers;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].eventID == eventId)
                {
                    return;
                }
            }

            EventTrigger.Entry entry = new EventTrigger.Entry
            {
                eventID = eventId,
                callback = new EventTrigger.TriggerEvent(),
            };
            entry.callback.AddListener(callback);
            entries.Add(entry);
        }
    }
}
