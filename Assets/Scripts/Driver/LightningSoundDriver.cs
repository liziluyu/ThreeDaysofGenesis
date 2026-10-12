using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 闪电的一次性音效：读 <see cref="ParticleWorld.Counts"/> 里「自上次读取以来新写入的雷电格数」，
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
    /// <para><b>为什么先清账再判开关</b>：关闭期间也必须把监视器的「新格」读掉——否则重新打开的那一帧会把
    /// 关闭期间积压的起爆当作「此刻在劈」补发出去（照抄水流声的既有口径）。</para>
    /// <para><b>未注册防御</b>：<see cref="ParticleIds.Lightning"/> 由
    /// <see cref="ParticleRegistration.RegisterAll"/> 回填，从未跑过时等于
    /// <see cref="ParticleRegistry.AirTypeId"/>（0 = 空气）。这种情况按「无法解析」处理：不响 + 只警告一次。</para>
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

        /// <summary>是否已经为「类型 id 未注册」报过警告（每帧都会走到这里，不能刷屏）。</summary>
        private bool _warnedUnregistered;

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
            Pump(_driver != null ? _driver.World : null);
        }

        /// <summary>
        /// 读掉本轮「新写入的雷电格数」并据此决定要不要响：格数 &gt; 0 且启用、音频可用时
        /// <see cref="SoundController.PlayOneShot"/> 响一声并返回 true。由 <see cref="Update"/> 每帧调一次；
        /// 公开是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="world">要观察的世界；null（驱动未就绪）时无账可读，直接不响。</param>
        /// <returns>本帧是否响了一次。</returns>
        public bool Pump(ParticleWorld world)
        {
            if (world == null)
            {
                return false;
            }

            // **先清账再判开关**：关闭期间也必须把监视器读掉，否则重新打开的那一帧会把关闭期间积压的
            // 起爆当作「此刻在劈」补发出去（响一下）。
            int created = world.Counts.ConsumeCreated(ParticleIds.Lightning);

            if (ParticleIds.Lightning == ParticleRegistry.AirTypeId)
            {
                // id 0 是「空」：那是未注册的占位值，不是「雷电劈了 0 格」。
                WarnUnregistered();
                return false;
            }

            if (created <= 0 || !_enabled || _sound == null)
            {
                return false;
            }

            _sound.PlayOneShot(_soundFile, _volume);
            return true;
        }

        /// <summary>「类型 id 未注册」只报一次（<see cref="Pump"/> 每帧都会走到这条分支）。</summary>
        private void WarnUnregistered()
        {
            if (_warnedUnregistered)
            {
                return;
            }

            _warnedUnregistered = true;
            Debug.LogWarning("[LightningSoundDriver] 雷电类型 id 尚未注册（ParticleIds.Lightning == 空气），本驱动静音。");
        }
    }
}
