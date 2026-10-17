using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 火焰的持续声：把帧里的 <see cref="AudioFrame.FireCount"/>（**火格数**存量）折算成一条**连续**循环声床的目标响度
    /// ——火越旺越响，低于死区就淡出静音。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么响度量取「火格数」而不是比例</b>：玩家对火的直觉就是「烧得越大越吵」——一片蔓延的大火
    /// 本来就该比一簇小火响，绝对格数正是想要的量（还天然随火势蔓延 / 熄灭连续变化）。水那边用运动**比例**
    /// 是因为静水塘不该因为大而更吵（尺寸无关），火没有这个顾虑，两者口径不同是有意的。</para>
    /// <para><b>为什么需要 EMA</b>：火格逐帧生灭（火舌抛射、被压顶熄灭、向上蔓延），存量每帧都在小幅跳动，
    /// 直接当响度会让音量持续颤抖。<see cref="_smoothingSeconds"/> 的一阶低通
    /// （<see cref="SoundController.Ema"/>）抹平抖动；dt 经
    /// <see cref="SoundController.ClampDeltaTime"/> 钳过，编辑器大卡顿不会让平滑一步跳完。</para>
    /// <para><b>不重播</b>：本组件只做「读快照 → 算目标响度 → 推给 <see cref="SoundController.SetAmbience"/>」，
    /// 每帧都推、推几次都一样。起播 / 淡入 / 淡出 / 停止全归 <see cref="SoundController"/> 里火那条循环声床
    /// （控制器**每条音频各有一条独立声床**，故水与火能同时响）。</para>
    /// <para><b>不读世界</b>：火格存量由 world 的唯一写者在发布帧之前读进 <see cref="AudioFrame"/>。
    /// 火是**存量**口径（不是本帧新增），因此陈旧帧（同一帧且序号没变，例如暂停中）沿用上一次读到的存量——
    /// 与旧版「暂停时仍每帧读 world.Counts」同义。</para>
    /// <para><b>为什么读存量而不清账</b>：火焰是**持续**现象，「当前烧着几格」才是响度；火格数不取模、
    /// 不消费任何「新增」记账——那是闪电那种一次性事件的口径
    /// （见 <see cref="ParticleCountMonitor.ConsumeCreated"/> 与 <see cref="LightningSoundDriver"/>）。</para>
    /// <para><b>未注册防御</b>：<see cref="ParticleIds.Fire"/> 由 <see cref="ParticleRegistration.RegisterAll"/>
    /// 回填，从未跑过时它等于 <see cref="ParticleRegistry.AirTypeId"/>（0 = 空气）。采集侧对这种情形写 0
    /// （<see cref="SimulationFrameCollector.CollectAudio"/>），本组件因此读到「没有火」而不是「整张空网格的空气都是火」。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class FireSoundDriver : MonoBehaviour
    {
        [Tooltip("留空则回退到同物体 / 场景里的 ParticleSimulationDriver（只为拿 world）。")]
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("留空则回退到同物体 / 场景里的 SoundController。")]
        [SerializeField] private SoundController _sound;

        [Tooltip("声音文件名。交给 SoundController 查音频表：取最后一段、去扩展名，不区分大小写。")]
        [SerializeField] private string _soundFile = "fire";

        [Tooltip("死区：火格数 ≤ 它 → 目标响度 0（没有火就彻底静音）。0 = 恰有一格火就出声。")]
        [SerializeField, Min(0f)] private float _minFireCount = 0f;

        [Tooltip("满音量火格数：达到它即满响度。参考量级：一小簇自然蔓延的火约十几格，一场大火几十格。")]
        [SerializeField, Min(1f)] private float _fullVolumeCount = 40f;

        [Tooltip("响度 EMA 的平滑时长（秒）：抹平火格逐帧生灭造成的音量颤抖。0 = 不平滑。")]
        [SerializeField, Min(0f)] private float _smoothingSeconds = 0.25f;

        [Tooltip("总音量缩放：与其它声源配平用。最终目标响度 = 火格数映射值 × 它。")]
        [SerializeField, Range(0f, 1f)] private float _volume = 1f;

        [Tooltip("关掉即收声（每帧推 0，交给控制器淡出）。")]
        [SerializeField] private bool _enabled = true;

        /// <summary>已经平滑过的目标响度（EMA 的记忆）。</summary>
        private float _smoothed;

        /// <summary>上一次读到的帧（对象身份 + 序号一起判定新鲜度）。</summary>
        private SimulationFrame _lastFrame;

        /// <summary>上一次读到的帧序号（配合 <see cref="_lastFrame"/> 识别陈旧帧）。</summary>
        private long _lastSequence;

        /// <summary>最近一次读到的火格存量（陈旧帧沿用它：火是存量口径，不是本帧新增）。</summary>
        private int _fireCount;

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

        private void Update()
        {
            Pump(_driver != null ? _driver.CurrentFrame : null, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 把帧快照里的**火格存量**折算成声床目标响度：读快照 → 映射 → EMA → 推给
        /// <see cref="SoundController.SetAmbience"/>。由 <see cref="Update"/> 每帧调一次；
        /// 公开（含显式 <paramref name="deltaTime"/>）是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="frame">已发布的完整帧；null（驱动未就绪）时静音。陈旧帧沿用上次读到的存量。</param>
        /// <param name="deltaTime">本帧真实时长（秒），只用于 EMA；经
        /// <see cref="SoundController.ClampDeltaTime"/> 钳制。</param>
        /// <returns>本帧推给声音控制器的目标响度（<c>[0, _volume]</c>）。</returns>
        public float Pump(SimulationFrame frame, float deltaTime)
        {
            if (frame == null || !_enabled || _sound == null)
            {
                return Silence();
            }

            // 新鲜 = 换了帧对象，或同一帧里序号又推进了：槽位会回收复用，单看序号会把
            // 「另一个发布者的第 1 帧」误判成陈旧（各发布者序号都从 1 开始），故身份与序号都必须记。
            if (!ReferenceEquals(frame, _lastFrame) || frame.Sequence != _lastSequence)
            {
                _lastFrame = frame;
                _lastSequence = frame.Sequence;
                _fireCount = frame.Audio.FireCount;
            }

            float target = SoundController.MapLoudness(_fireCount, _minFireCount, _fullVolumeCount) * _volume;
            _smoothed = SoundController.Ema(_smoothed, target, _smoothingSeconds, deltaTime);

            _sound.SetAmbience(_soundFile, _smoothed);
            return _smoothed;
        }

        /// <summary>收声：把目标推 0（交给 SoundController 淡出），并清掉平滑记忆
        /// （重新具备发声条件时从静音重新淡入，而不是从旧响度猛地接上）。</summary>
        private float Silence()
        {
            _smoothed = 0f;

            if (_sound != null)
            {
                _sound.StopAmbience(_soundFile);
            }

            return 0f;
        }

    }
}
