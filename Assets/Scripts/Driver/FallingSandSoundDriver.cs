using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 落下的固体声：把帧里的 <see cref="AudioFrame.SolidMovingCount"/>（**固体的运动格数** = 本轮发生换位的固体端数）
    /// 折算成一条**连续**循环声床的目标响度——下落的固体越多越响，低于死区就淡出静音。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么响度量取「运动格数」而不是比例</b>：固体存量里绝大多数是常年静止的草 / 树干 / 树叶 /
    /// 花 / 水生植物——一张地图上不动的东西远多于会掉的沙石，比例会被这些静止成员稀释到近 0，
    /// 「一柱沙在落」与「满地沙在滚」几乎分不出来。绝对格数还与玩家直觉一致：**落得越多越吵**，
    /// 多一柱沙就是多一份响（与火那边的取舍相同）；水的流动声反过来用比例，是因为静水塘不该因为大而更吵
    /// （见 <see cref="LiquidFlowSoundDriver"/>），两者口径不同是有意的。</para>
    /// <para><b>为什么不需要滞回 / EMA</b>：固体落地即静止，运动格数没有噪声底——不像水面会永远逐格穿行，
    /// 因此不存在「在死区边界来回开合」的问题，也不需要低通去抹平抖动。响度的平滑已由
    /// <see cref="SoundController"/> 自己的 <c>_loudnessPerSecond</c> 完成（每条声床的音量朝目标每秒推进）。
    /// 这是本驱动比水的流动声简单的原因。</para>
    /// <para><b>口径</b>：帧里的运动格数来自 world 的固体运动观察者——带 <see cref="ParticleTags.Solid"/>
    /// 且**不带** <see cref="ParticleTags.Organic"/> 的类型，即会下落的粉末 / 石：石、沙、土、湿土、腐殖质、
    /// 腐殖土、湿润腐殖土、草种子（草种子不带 Organic——合并水生态种子后它必须能沉水发芽，见
    /// <see cref="GrassSeedParticle"/> 的文件头）。树叶 / 树木种子 / 草 / 花 / 树干带 <see cref="ParticleTags.Organic"/>，
    /// 被排除，故树叶飘落、树木种子下沉**不会**触发落沙声。</para>
    /// <para><b>不重播</b>：本组件只做「读快照 → 算目标响度 → 推给 <see cref="SoundController.SetAmbience"/>」，
    /// 每帧都推、推几次都一样；起播 / 淡入 / 淡出 / 停止全归 <see cref="SoundController"/> 里这条循环声床。</para>
    /// <para><b>不读世界、也不清账</b>：运动记账由 world 的唯一写者在发布帧之前抽干一次
    /// （<see cref="SimulationFrameCollector.CollectAudio"/>），本组件只读 <see cref="AudioFrame"/>——
    /// 因此多个消费者读同一帧各拿各的，谁也不会把账抢掉。陈旧帧（同一帧且序号没变 = 自上次读取以来没有新发布，
    /// 例如暂停中）按「本轮没有运动」处理，与旧版每帧清账后读到 0 同义。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class FallingSandSoundDriver : MonoBehaviour
    {
        [Tooltip("留空则回退到同物体 / 场景里的 ParticleSimulationDriver（只为拿 world）。")]
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("留空则回退到同物体 / 场景里的 SoundController。")]
        [SerializeField] private SoundController _sound;

        [Tooltip("声音文件名。交给 SoundController 查音频表：取最后一段、去扩展名，不区分大小写。")]
        [SerializeField] private string _soundFile = "falling_sand";

        [Tooltip("死区：本轮运动格数 ≤ 它 → 目标响度 0。0 = 恰有一格固体在动就出声。")]
        [SerializeField, Min(0f)] private float _minMovingCount = 0f;

        [Tooltip("满音量运动格数：达到它即满响度。参考量级：一柱沙崩塌约十几格，成片沙流几十格。")]
        [SerializeField, Min(1f)] private float _fullVolumeCount = 40f;

        [Tooltip("总音量缩放：与其它声源配平用。最终目标响度 = 运动格数映射值 × 它。")]
        [SerializeField, Range(0f, 1f)] private float _volume = 1f;

        [Tooltip("关掉即收声（每帧推 0，交给控制器淡出）。仍然每帧清账，重新打开不会补发关闭期间积压的运动。")]
        [SerializeField] private bool _enabled = true;

        private void Awake()
        {
            if (_driver == null)
            {
                _driver = GetComponent<ParticleSimulationDriver>();
            }

            if (_driver == null)
            {
                _driver = FindObjectOfType<ParticleSimulationDriver>();
            }

            if (_sound == null)
            {
                _sound = GetComponent<SoundController>();
            }

            if (_sound == null)
            {
                _sound = FindObjectOfType<SoundController>();
            }
        }

        private void OnDisable()
        {
            // 不推目标就等于把声床留在最后的响度上——禁用时必须显式收声。
            Silence();
        }

        /// <summary>上一次读到的帧（对象身份 + 序号一起判定新鲜度）。</summary>
        private SimulationFrame _lastFrame;

        /// <summary>上一次读到的帧序号（配合 <see cref="_lastFrame"/> 识别陈旧帧）。</summary>
        private long _lastSequence;

        private void Update()
        {
            Pump(_driver != null ? _driver.CurrentFrame : null);
        }

        /// <summary>
        /// 把帧快照里**本帧**（自该帧发布以来）的固体运动格数折算成声床目标响度：
        /// 读快照 → 映射 → 推给 <see cref="SoundController.SetAmbience"/>。
        /// 由 <see cref="Update"/> 每帧调一次；公开是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="frame">已发布的完整帧；null（驱动未就绪）时收声。陈旧帧按 0 运动处理。</param>
        /// <returns>本帧推给声音控制器的目标响度（<c>[0, _volume]</c>）。</returns>
        public float Pump(SimulationFrame frame)
        {
            if (frame == null)
            {
                return Silence();
            }

            // 新鲜 = 换了帧对象，或同一帧里序号又推进了：槽位会回收复用，单看序号会把
            // 「另一个发布者的第 1 帧」误判成陈旧（各发布者序号都从 1 开始），故身份与序号都必须记。
            bool fresh = !ReferenceEquals(frame, _lastFrame) || frame.Sequence != _lastSequence;
            _lastFrame = frame;
            _lastSequence = frame.Sequence;
            long moving = fresh ? frame.Audio.SolidMovingCount : 0L;

            if (!_enabled || _sound == null)
            {
                return Silence();
            }

            float target = SoundController.MapLoudness(moving, _minMovingCount, _fullVolumeCount) * _volume;

            _sound.SetAmbience(_soundFile, target);
            return target;
        }

        /// <summary>收声：把目标推 0（交给 SoundController 淡出）。</summary>
        private float Silence()
        {
            if (_sound != null)
            {
                _sound.StopAmbience(_soundFile);
            }

            return 0f;
        }
    }
}
