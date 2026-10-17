using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 闪电的一次性音效：读帧里的 <see cref="AudioFrame.LightningCreated"/>（「自上次发布以来新写入的雷电格数」），
    /// 只要 &gt; 0 就让 <see cref="SoundController.PlayOneShot"/> 响一声——**一帧最多一声**，
    /// 绝不循环、绝不取模、绝不锁相位。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么读到的格子数是「格数」而不是「次数」</b>：一次起爆
    /// （<see cref="LightningParticle.TryStrike"/>）会把**整条**雷电主干自起爆格逐格写进网格
    /// （一条完整闪电就是几十格），再在落点上方写一团火。因此判据只能是 <c>created &gt; 0</c>：
    /// 把格数当次数会在一帧里叠放几十声，把一条闪电听成一串爆豆。于是**一帧最多响一次**——
    /// 这正是「只在劈下的那一刻响一声」的实现。</para>
    /// <para><b>为什么不走循环声床</b>：闪电音频很短、只在事件当帧响一下。循环声床会锁相位、要取模
    /// （每次劈听起来都像同一段被剪断的循环），也不该为一次性事件常驻。<see cref="SoundController.PlayOneShot"/>
    /// 走独立 <c>loop = false</c> 声源，连续劈下由原生混音自然叠放。</para>
    /// <para><b>为什么先清账再判开关</b>：抽干监视器的「新格」归帧发布侧
    /// （<see cref="SimulationFrameCollector.CollectAudio"/>），因此关闭期间的新格同样每帧被读掉——
    /// 重新打开那一帧不会把关闭期间积压的起爆当作「此刻在劈」补发出去。
    /// 本组件按帧**身份 + 序号**共同去重：同一帧读第二次不重复响（一次性事件绝不能因为多读一次而多响一声）。</para>
    /// <para><b>未注册防御</b>：<see cref="ParticleIds.Lightning"/> 由
    /// <see cref="ParticleRegistration.RegisterAll"/> 回填，从未跑过时等于
    /// <see cref="ParticleRegistry.AirTypeId"/>（0 = 空气）。空气槽永不计数，采集侧因此写 0，本组件自动不响。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LightningSoundDriver : MonoBehaviour
    {
        [Tooltip("留空则回退到同物体 / 场景里的 ParticleSimulationDriver（只为拿 world）。")]
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("留空则回退到同物体 / 场景里的 SoundController。")]
        [SerializeField] private SoundController _sound;

        [Tooltip("声音文件名。交给 SoundController 查音频表：取最后一段、去扩展名，不区分大小写。")]
        [SerializeField] private string _soundFile = "lightning";

        [Tooltip("一次性音效的音量（不参与任何平滑 / 声床配平）。")]
        [SerializeField, Range(0f, 1f)] private float _volume = 1f;

        [Tooltip("关掉即不发声，但仍然每帧清账：重新打开不会补发关闭期间积压的起爆。")]
        [SerializeField] private bool _enabled = true;

        /// <summary>上一次读过的帧（对象身份 + 序号一起判定：一次性事件绝不重复响）。</summary>
        private SimulationFrame _lastFrame;

        /// <summary>上一次读过的帧序号（配合 <see cref="_lastFrame"/> 识别读过的帧）。</summary>
        private long _lastSequence;

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

        private void Update()
        {
            Pump(_driver != null ? _driver.CurrentFrame : null);
        }

        /// <summary>
        /// 读本帧「新写入的雷电格数」并据此决定要不要响：格数 &gt; 0 且启用、音频可用时
        /// <see cref="SoundController.PlayOneShot"/> 响一声并返回 true。由 <see cref="Update"/> 每帧调一次；
        /// 公开是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="frame">已发布的完整帧；null（驱动未就绪）时无账可读，直接不响。</param>
        /// <returns>本帧是否响了一次。</returns>
        public bool Pump(SimulationFrame frame)
        {
            if (frame == null || (ReferenceEquals(frame, _lastFrame) && frame.Sequence == _lastSequence))
            {
                // 没有新帧（暂停中反复读同一帧）＝没有新的起爆：一次性事件绝不重复响。
                return false;
            }

            // 槽位会回收复用：单看序号会把「另一个发布者的第 1 帧」误判成读过的帧，故身份与序号都记。
            _lastFrame = frame;
            _lastSequence = frame.Sequence;

            if (!_enabled || _sound == null || frame.Audio.LightningCreated <= 0)
            {
                return false;
            }

            _sound.PlayOneShot(_soundFile, _volume);
            return true;
        }
    }
}
