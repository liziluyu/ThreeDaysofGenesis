using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 落下的固体声：把 <see cref="ParticleWorld.SolidMotion"/>（**固体的运动格数** = 本轮发生换位的固体端数）
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
    /// <para><b>口径</b>：读的是 <see cref="ParticleWorld.SolidMotion"/>——带 <see cref="ParticleTags.Solid"/>
    /// 且**不带** <see cref="ParticleTags.Organic"/> 的类型，即会下落的粉末 / 石：石、沙、土、湿土、腐殖质、
    /// 腐殖土、湿润腐殖土、水生态种子。树叶 / 草种子 / 树木种子带 <see cref="ParticleTags.Organic"/>，
    /// 连同草 / 花 / 树干一起被排除，故树叶飘落、种子下沉**不会**触发落沙声。</para>
    /// <para><b>不重播</b>：本组件只做「读计数 → 算目标响度 → 推给 <see cref="SoundController.SetAmbience"/>」，
    /// 每帧都推、推几次都一样；起播 / 淡入 / 淡出 / 停止全归 <see cref="SoundController"/> 里这条循环声床。</para>
    /// <para><b>先清账再判开关</b>：<c>_enabled == false</c> 期间也**无条件**读掉并重置运动计数，
    /// 否则重新打开的那一帧会把关闭期间积压的运动当作「此刻在落」补发出去（响一下）。</para>
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

        private void Update()
        {
            Pump(_driver != null ? _driver.World : null);
        }

        /// <summary>
        /// 把 <paramref name="world"/> **本轮**（自上次调用以来）累积的固体运动格数折算成声床目标响度：
        /// 读计数 → 清账 → 映射 → 推给 <see cref="SoundController.SetAmbience"/>。
        /// 由 <see cref="Update"/> 每帧调一次；公开是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="world">要观察的世界；null（驱动未就绪）时收声。</param>
        /// <returns>本帧推给声音控制器的目标响度（<c>[0, _volume]</c>）。</returns>
        public float Pump(ParticleWorld world)
        {
            if (world == null)
            {
                return Silence();
            }

            // **无条件先清账**：关闭期间也必须把运动计数读掉，否则重新打开的那一帧会把
            // 关闭期间积压的运动当作「此刻在落」补发出去（响一下）。
            long moving = world.SolidMotion.MovingCount;
            world.SolidMotion.Reset();

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
