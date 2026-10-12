using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 水的流动声：把 <see cref="ParticleWorld.LiquidMotion"/>（液体**运动比例** = 本帧动的液体 ÷ 液体总数）
    /// 折算成一条**连续**循环声床的目标响度——动的比例越大越响，低于死区就淡出静音。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么用运动比例而不是位置 / 绝对数</b>：绝对移动数随水体大小涨（水面越宽、抖动越多），
    /// 用它当响度会让大水塘无端更响；位置 / 方向都不行，因为当前流体算法在平静水面上也会逐格穿行。
    /// 比例与尺寸无关：静止水洼只有一薄层液面在动、比例低；瀑布整片在动、比例高
    /// （见 <see cref="MotionMonitor"/>）。</para>
    /// <para><b>不重播</b>：本组件只做「读比例 → 算目标响度 → 推给 <see cref="SoundController.SetAmbience"/>」，
    /// 每帧都推、推几次都一样。起播 / 淡入 / 淡出 / 停止全归 <see cref="SoundController"/> 里水那条循环声床
    /// （控制器**每条音频各有一条独立声床**，故水与火能同时响），因此根本没有「续声」——旧版按一个持续秒数周期
    /// <c>Play/Stop</c> 重播的那套（续声边界、相位跳变、声部叠加、抢占）已整体删除。</para>
    /// <para><b>防抖</b>：运动比例每帧都在水面抖动下小幅起伏，直接当响度会让音量持续颤抖。两道闸门：
    /// **滞回**（<see cref="_hysteresis"/>：进入流动要 <c>ratio ≥ MinRatio + 滞回</c>，退出要
    /// <c>ratio &lt; MinRatio</c>，避免在死区边界来回开合）与 **EMA**（<see cref="_smoothingSeconds"/> 的一阶低通，
    /// 抹平死区之上的连续抖动）。死区内目标恒为 0——静止水洼静音。映射用
    /// <see cref="SoundController.MapLoudness"/>、平滑用 <see cref="SoundController.Ema"/>，后者用
    /// <see cref="SoundController.ClampDeltaTime"/> 钳过的 dt，编辑器大卡顿不会让平滑一步跳完。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LiquidFlowSoundDriver : MonoBehaviour
    {
        [Tooltip("留空则回退到同物体 / 场景里的 ParticleSimulationDriver（只为拿 world）。")]
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("留空则回退到同物体 / 场景里的 SoundController。")]
        [SerializeField] private SoundController _sound;

        [Tooltip("声音文件名。交给 SoundController 查音频表：取最后一段、去扩展名，不区分大小写。")]
        [SerializeField] private string _soundFile = "water";

        [Tooltip("死区：一帧内**液体运动比例**（动的液体 ÷ 液体总数）低于它就完全不发声。" +
                 "参考实测（生产表）：石盆里的静水 = 0.00、退潮期 ≈ 0.14、摊开的薄水层 0.21~0.32、" +
                 "连续瀑布 0.72。0.15 可让盆/塘里的静水彻底静音。")]
        [SerializeField, Range(0f, 1f)] private float _minRatio = 0.15f;

        [Tooltip("满音量比例：运动比例达到它就按满响度放。瀑布约 0.72，故 0.6 起就接近满响。")]
        [SerializeField, Range(0f, 1f)] private float _fullVolumeRatio = 0.6f;

        [Tooltip("滞回宽度：进入流动要 ratio ≥ 死区 + 它，退出只要 ratio < 死区。" +
                 "0 = 无滞回（比例恰在死区边界抖动时会来回开合）。")]
        [SerializeField, Range(0f, 1f)] private float _hysteresis = 0.03f;

        [Tooltip("响度 EMA 的平滑时长（秒）：抹平水面抖动造成的逐帧音量颤抖。0 = 不平滑。")]
        [SerializeField, Min(0f)] private float _smoothingSeconds = 0.25f;

        [Tooltip("关掉即不发声。仍然每帧清账，重新打开不会补发关闭期间积压的流动。")]
        [SerializeField] private bool _enabled = true;

        /// <summary>已经平滑过的目标响度（EMA 的记忆）。</summary>
        private float _smoothed;

        /// <summary>当前是否判定为「有流动」（滞回门的记忆）。</summary>
        private bool _flowing;

        /// <summary>进入流动的比例阈值 = 死区 + 滞回。</summary>
        private float FlowEnterRatio
        {
            get { return _minRatio + _hysteresis; }
        }

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
            Pump(_driver != null ? _driver.World : null, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 把 <paramref name="world"/> **本轮**（自上次调用以来）累积的液体运动比例折算成声床目标响度：
        /// 读监视器 → 清账 → 目标推给 <see cref="SoundController.SetAmbience"/>。由 <see cref="Update"/> 每帧调一次；
        /// 公开（含显式 <paramref name="deltaTime"/>）是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="world">要观察的世界；null（驱动未就绪）时静音。</param>
        /// <param name="deltaTime">本帧真实时长（秒），只用于 EMA；经
        /// <see cref="SoundController.ClampDeltaTime"/> 钳制。</param>
        /// <returns>本帧推给声音控制器的目标响度（<c>[0, 1]</c>）。</returns>
        public float Pump(ParticleWorld world, float deltaTime)
        {
            if (world == null)
            {
                return Silence();
            }

            // **先清账再判开关**：关闭期间也必须把监视器的本轮计数读掉，否则重新打开的那一帧会把
            // 关闭期间积压的流动当作「此刻在流」补发出去（响一下）。
            MotionMonitor motion = world.LiquidMotion;
            float ratio = motion.MovingRatio;
            motion.Reset();

            if (!_enabled || _sound == null)
            {
                return Silence();
            }

            _flowing = IsFlowing(_flowing, ratio, FlowEnterRatio, _minRatio);
            float target = _flowing ? SoundController.MapLoudness(ratio, _minRatio, _fullVolumeRatio) : 0f;
            _smoothed = SoundController.Ema(_smoothed, target, _smoothingSeconds, deltaTime);

            _sound.SetAmbience(_soundFile, _smoothed);
            return _smoothed;
        }

        /// <summary>死区 + 滞回门：<paramref name="ratio"/> 达到 <paramref name="enterRatio"/> 视为进入流动，
        /// 低于 <paramref name="exitRatio"/> 视为退出，两者之间维持 <paramref name="wasFlowing"/>。
        /// 阈值相等即退化为无滞回的单一死区边界（恰好等于边界算进入）。</summary>
        public static bool IsFlowing(bool wasFlowing, float ratio, float enterRatio, float exitRatio)
        {
            if (ratio >= enterRatio)
            {
                return true;
            }

            if (ratio < exitRatio)
            {
                return false;
            }

            return wasFlowing;
        }

        /// <summary>收声：把目标推 0（交给 SoundController 淡出），并清掉平滑 / 滞回的记忆
        /// （重新具备发声条件时从静音重新淡入，而不是从旧响度猛地接上）。</summary>
        private float Silence()
        {
            _flowing = false;
            _smoothed = 0f;

            if (_sound != null)
            {
                _sound.StopAmbience(_soundFile);
            }

            return 0f;
        }
    }
}
