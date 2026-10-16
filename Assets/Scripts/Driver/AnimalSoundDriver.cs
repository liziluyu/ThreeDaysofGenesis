using System;
using System.Collections.Generic;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 动物声音：检测到场上**存在**某类动物就按随机间隔间歇放一声该物种的声音，另接出生 / 死亡各一声。
    /// 只复用 <see cref="SoundController.PlayOneShot"/>（一次性播放路径）——**不新增循环声床、不改控制器**。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么间歇而不循环</b>：水 / 火 / 落沙是连续声床（逐帧推目标响度、由控制器平滑），
    /// 动物声却必须是**偶发**的一下——循环播放的动物叫声会变成一条永不间断的声底，且取模锁相位会让每声
    /// 听上去像同一段被剪断的循环。因此走既有的 <see cref="SoundController.PlayOneShot"/>：短音、不循环、
    /// 不锁相位、重叠由原生混音叠放（同闪电）。一声响完 <c>[idleIntervalMin, idleIntervalMax]</c> 秒后再摇下一次，
    /// 且**每次重摇** ⇒ 间歇而非节拍器。</para>
    /// <para><b>为什么不按只数</b>：口径是「场上存在该类动物就间歇放一声」，不是「每只各叫一声」。
    /// 十只兔子按只数铺声会糊成噪音，也与「一次事件最多一声」的降噪取舍一致（见下）。因此每物种只取
    /// **存在性**（<c>is</c> 判定）、不数只数、不逐只发声——每帧只读一遍实体表，零分配、零 LINQ。</para>
    /// <para><b>为什么自带随机流</b>：本驱动是**纯观察者**，绝不能消耗粒子世界或动物世界各自那条 xorshift32——
    /// 否则「有没有挂这个声音驱动」会扰动模拟的每一次摇骰，确定性铁律失效。于是自带一条同算法的流，
    /// 只用来摇闲聊间隔（与事件声的音量抖动），同 seed 的出声时刻可复现、可测。</para>
    /// <para><b>为什么事件回调只置标志位</b>：出生 / 死亡事件在 <see cref="CreatureWorld.Step"/> 的清扫阶段
    /// 触发，一帧里可能连着发几十次（一场火灾烧死一群）。**直接在回调里出声**会一帧叠放几十声、糊成噪音，
    /// 也绕开了「高速静音」的统一闸门。回调只把该物种该路的标志置真；下一次 <see cref="Pump"/> 再统一结算，
    /// 每物种每路**最多一声**，多出来的折叠掉。</para>
    /// <para><b>高速模式</b>：<see cref="SoundController.HighSpeedSilenced"/> 期间整段跳过——不倒计时、不摇骰、
    /// 不结算事件（标志照旧保留，退出后按当时的标志补一声）。<see cref="SoundController"/> 本身也会丢弃，
    /// 这里连骰子都不摇是为了不给随机流留下高速模式下的消耗痕迹。</para>
    /// <para><b>哑音容错</b>：素材未就位时不知道音频名——<see cref="SoundController.PlayOneShot"/> 自己只
    /// <c>LogWarning</c> 一次、不抛、不动其它声床；本驱动不缓存索引、不抛异常。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AnimalSoundDriver : MonoBehaviour
    {
        /// <summary>某个物种的声音配置：闲聊声 + 出生 / 死亡声（后两者留空则回落用闲聊声）。</summary>
        [Serializable]
        public struct CreatureVoice
        {
            [Tooltip("闲聊声文件名（音频表查询键）；留空 = 该物种不发声")] public string idleClip;
            [Tooltip("闲聊间隔下限（秒）")] public float idleIntervalMin;
            [Tooltip("闲聊间隔上限（秒）")] public float idleIntervalMax;
            [Tooltip("闲聊音量")] [Range(0f, 1f)] public float idleVolume;
            [Tooltip("死亡声文件名；留空 = 回落用 idleClip")] public string deathClip;
            [Tooltip("出生声文件名；留空 = 回落用 idleClip")] public string spawnClip;
        }

        /// <summary>seed == 0 时的替换值（黄金比例常数，避免全零种子退化成恒零序列；与 <see cref="CreatureWorld"/> 同一兜底）。</summary>
        private const uint FallbackSeed = 0x9E3779B9u;

        [Tooltip("留空则回退到同物体 / 场景里的 ParticleSimulationDriver（只为拿动物世界）。")]
        [SerializeField] private ParticleSimulationDriver _driver;

        [Tooltip("留空则回退到同物体 / 场景里的 SoundController。")]
        [SerializeField] private SoundController _sound;

        [SerializeField] private CreatureVoice _rabbit = new CreatureVoice { idleClip = "rabbit", idleIntervalMin = 6f, idleIntervalMax = 14f, idleVolume = 0.5f };
        [SerializeField] private CreatureVoice _bird = new CreatureVoice { idleClip = "bird", idleIntervalMin = 5f, idleIntervalMax = 12f, idleVolume = 0.5f };
        [SerializeField] private CreatureVoice _fish = new CreatureVoice { idleClip = "fish", idleIntervalMin = 8f, idleIntervalMax = 18f, idleVolume = 0.45f };

        [Tooltip("本驱动自己的随机流种子（xorshift32）：不碰粒子/动物那两条流；同 seed 出声时刻可复现")]
        [SerializeField] private uint _seed = 0x5EED2026u;

        /// <summary>本驱动自己的 xorshift32 状态（算法与 <see cref="CreatureWorld.NextUInt"/> 逐字一致）。</summary>
        private uint _state;

        /// <summary>是否已用 <see cref="_seed"/> 初始化 <see cref="_state"/>（懒初始化：EditMode 测试不跑 <see cref="Awake"/>）。</summary>
        private bool _seeded;

        /// <summary>当前已订阅事件的世界实例；换实例时重新对账订阅（世界重建天然免疫）。</summary>
        private CreatureWorld _subscribed;

        /// <summary>各物种的闲聊倒计时：未武装（物种不存在 / 刚重现）时不倒计时。</summary>
        private bool _rabbitArmed;
        private bool _birdArmed;
        private bool _fishArmed;
        private float _rabbitTimer;
        private float _birdTimer;
        private float _fishTimer;

        /// <summary>各物种待结算的出生 / 死亡事件标志（回调只置位，<see cref="Pump"/> 统一结算）。</summary>
        private bool _rabbitSpawned;
        private bool _birdSpawned;
        private bool _fishSpawned;
        private bool _rabbitDied;
        private bool _birdDied;
        private bool _fishDied;

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

            EnsureSeeded();
        }

        private void OnDisable()
        {
            // 退订：禁用 / 销毁后不再收事件（标志随对象一起消失）。
            Unsubscribe();
        }

        private void Update()
        {
            Pump(_driver != null ? _driver.Creatures : null, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 推进一帧：对账订阅 → 统计各物种是否存在 → 结算出生 / 死亡事件声 → 走闲聊倒计时。
        /// 由 <see cref="Update"/> 每帧调一次；公开 + 显式 dt 是为了让测试能脱离帧循环确定性地驱动它。
        /// </summary>
        /// <param name="creatures">要观察的动物世界；null（驱动未就绪）时不响。</param>
        /// <param name="deltaTime">本帧真实时长（秒）；经 <see cref="SoundController.ClampDeltaTime"/> 钳制。</param>
        /// <returns>本次出声数（含事件声与闲聊声；测试可观测量）。</returns>
        public int Pump(CreatureWorld creatures, float deltaTime)
        {
            if (_sound == null || creatures == null)
            {
                return 0;
            }

            // 高速模式整层静音：连骰子都不摇、事件标志留到退出后补（见类注释）。
            if (_sound.HighSpeedSilenced)
            {
                return 0;
            }

            EnsureSeeded();

            // 编辑器卡顿 / 断点造成的秒级 dt 不该让倒计时一步跨过好几个间隔（同控制器的唯一闸门）。
            deltaTime = SoundController.ClampDeltaTime(deltaTime);

            // 每帧对账：世界重建换了实例就重新订阅（照渲染层轮询的做法）。
            ReconcileSubscription(creatures);

            bool rabbit = false;
            bool bird = false;
            bool fish = false;
            IReadOnlyList<Creature> list = creatures.Creatures;
            for (int i = 0; i < list.Count; i++)
            {
                Creature c = list[i];
                if (c is RabbitCreature)
                {
                    rabbit = true;
                }
                else if (c is BirdCreature)
                {
                    bird = true;
                }
                else if (c is FishCreature)
                {
                    fish = true;
                }
            }

            int played = 0;

            // 事件声：每物种每路最多一声（同帧死 5 只也折叠成一声）。
            played += PlayEvent(ref _rabbitSpawned, _rabbit.spawnClip, _rabbit.idleClip, _rabbit.idleVolume);
            played += PlayEvent(ref _rabbitDied, _rabbit.deathClip, _rabbit.idleClip, _rabbit.idleVolume);
            played += PlayEvent(ref _birdSpawned, _bird.spawnClip, _bird.idleClip, _bird.idleVolume);
            played += PlayEvent(ref _birdDied, _bird.deathClip, _bird.idleClip, _bird.idleVolume);
            played += PlayEvent(ref _fishSpawned, _fish.spawnClip, _fish.idleClip, _fish.idleVolume);
            played += PlayEvent(ref _fishDied, _fish.deathClip, _fish.idleClip, _fish.idleVolume);

            // 闲聊倒计时：存在才排程 / 倒计时，到点出声并重摇（间歇而非节拍器）。
            played += TickIdle(ref _rabbitArmed, ref _rabbitTimer, rabbit, _rabbit, deltaTime);
            played += TickIdle(ref _birdArmed, ref _birdTimer, bird, _bird, deltaTime);
            played += TickIdle(ref _fishArmed, ref _fishTimer, fish, _fish, deltaTime);

            return played;
        }

        /// <summary>
        /// 结算一路事件声（出生或死亡）：标志置真则清账并放一声，文件名留空则回落用 <paramref name="fallback"/>。
        /// 音量乘一个确定性的 <c>[0.9, 1]</c> 抖动（同种事件连发时不至于完全一样）。
        /// </summary>
        /// <returns>本路是否出了声（0 或 1）。</returns>
        private int PlayEvent(ref bool flag, string clip, string fallback, float volume)
        {
            if (!flag)
            {
                return 0;
            }

            // 先清账：即使该物种没配任何音频，事件也不该积压到下一帧。
            flag = false;

            string file = !string.IsNullOrEmpty(clip) ? clip : fallback;
            if (string.IsNullOrEmpty(file))
            {
                return 0;
            }

            _sound.PlayOneShot(file, volume * NextFloat(0.9f, 1f));
            return 1;
        }

        /// <summary>
        /// 推进某物种的闲聊倒计时：
        /// 不存在 ⇒ 解除武装（不排程、不倒计时，重新出现时按新间隔起算，不会一出现就响）；
        /// 存在且未武装 ⇒ 武装并摇一个 <c>[min, max]</c> 间隔（本帧不倒计时）；
        /// 存在且已武装 ⇒ 倒计时；到点出声并**重摇**下一次间隔。
        /// </summary>
        /// <returns>本物种本帧是否出声（0 或 1）。</returns>
        private int TickIdle(ref bool armed, ref float timer, bool present, CreatureVoice voice, float deltaTime)
        {
            if (string.IsNullOrEmpty(voice.idleClip))
            {
                // 留空 = 该物种不发声：不排程、不摇骰（随机流不因未配置的物种漂移）。
                armed = false;
                return 0;
            }

            if (!present)
            {
                armed = false;
                return 0;
            }

            if (!armed)
            {
                armed = true;
                timer = NextInterval(voice);
                return 0;
            }

            timer -= deltaTime;
            if (timer > 0f)
            {
                return 0;
            }

            _sound.PlayOneShot(voice.idleClip, voice.idleVolume);
            timer = NextInterval(voice);
            return 1;
        }

        /// <summary>摇一个 <c>[idleIntervalMin, idleIntervalMax]</c> 的闲聊间隔（上界小于下界时退化为下界）。</summary>
        private float NextInterval(CreatureVoice voice)
        {
            float min = voice.idleIntervalMin;
            float max = voice.idleIntervalMax >= min ? voice.idleIntervalMax : min;
            return NextFloat(min, max);
        }

        /// <summary>把订阅对到当前世界实例：换实例（世界重建）时先退旧再订新，否则只做一次相等比较。</summary>
        private void ReconcileSubscription(CreatureWorld creatures)
        {
            if (ReferenceEquals(_subscribed, creatures))
            {
                return;
            }

            Unsubscribe();
            _subscribed = creatures;
            _subscribed.CreatureSpawned += OnCreatureSpawned;
            _subscribed.CreatureDied += OnCreatureDied;
        }

        /// <summary>退订当前世界（禁用 / 世界换实例时）。</summary>
        private void Unsubscribe()
        {
            if (_subscribed == null)
            {
                return;
            }

            _subscribed.CreatureSpawned -= OnCreatureSpawned;
            _subscribed.CreatureDied -= OnCreatureDied;
            _subscribed = null;
        }

        /// <summary>出生事件回调：只置标志位（不直接出声，见类注释）。</summary>
        private void OnCreatureSpawned(Creature creature)
        {
            if (creature is RabbitCreature)
            {
                _rabbitSpawned = true;
            }
            else if (creature is BirdCreature)
            {
                _birdSpawned = true;
            }
            else if (creature is FishCreature)
            {
                _fishSpawned = true;
            }
        }

        /// <summary>死亡事件回调：只置标志位（不直接出声，见类注释）。</summary>
        private void OnCreatureDied(Creature creature, CreatureDeathCause cause)
        {
            if (creature is RabbitCreature)
            {
                _rabbitDied = true;
            }
            else if (creature is BirdCreature)
            {
                _birdDied = true;
            }
            else if (creature is FishCreature)
            {
                _fishDied = true;
            }
        }

        /// <summary>懒初始化随机流（<see cref="Awake"/> 与首次 <see cref="Pump"/> 都会调，只生效一次）。</summary>
        private void EnsureSeeded()
        {
            if (_seeded)
            {
                return;
            }

            _seeded = true;
            _state = _seed == 0u ? FallbackSeed : _seed;
        }

        /// <summary>确定性随机数（xorshift32），与 <see cref="CreatureWorld.NextUInt"/> 逐字一致。</summary>
        private uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>由 <see cref="NextUInt"/> 折算确定性浮点：取高 24 位映射到 <c>[0, 1)</c>，再线性映到 <c>[min, max)</c>。
        /// 不引 <c>UnityEngine.Random</c>、不引 <c>System.Random</c>（同确定性铁律）。</summary>
        private float NextFloat(float min, float max)
        {
            float unit = (NextUInt() >> 8) * (1f / 16777216f);
            return min + (max - min) * unit;
        }
    }
}
