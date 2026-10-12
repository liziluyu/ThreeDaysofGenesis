using System;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 声音控制器：按文件名查音频，**每条音频各有一条独立的循环声床**，外加**一个一次性音效声源**。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么每条音频一条声床</b>：水声与火声必须**同时**响——它们是两条各自独立的连续声床，
    /// 响度互不影响。旧版只有一个全局循环声源，同一时刻至多一条音频在响，换音频就得抢声部（停旧的、挂新的），
    /// 水与火永远互相掐断。现在声床与 <see cref="_clips"/> 下标一一对应、惰性建源，
    /// <see cref="SetAmbience"/> 只写**点名那条**的目标响度，别的声床一个采样都不动。</para>
    /// <para><b>为什么一次性音效不走声床</b>：闪电只在劈下的那一刻响一次——短音频、不循环、也绝不允许取模锁相位
    /// （取模会让「每次劈」听起来都像同一段被剪断的循环）。因此另起一个 <see cref="AudioSource"/> 交给
    /// <see cref="AudioSource.PlayOneShot(AudioClip, float)"/>：原生支持重叠（连续劈下会自然叠放），
    /// 不需要声部池、不写 <c>source.time</c>、也完全不需要知道片段时长。它的 <c>volume</c> 恒为 1，音量一律由调用参数给出。</para>
    /// <para><b>响度平滑</b>：<see cref="SetAmbience"/> 只写**目标**响度，实际 <c>volume</c> 由 <see cref="Update"/>
    /// 每秒朝目标推进 <see cref="_loudnessPerSecond"/>（默认 3 = 满量程约 1/3 秒）。时间步长先经
    /// <see cref="ClampDeltaTime"/> 钳制：编辑器卡顿 / 断点调试造成的秒级 <see cref="Time.unscaledDeltaTime"/>
    /// 不会让音量在一个真实帧里跳满量程。设 <see cref="_loudnessPerSecond"/> = 0 则退回立即跳变。
    /// 时间轴用 <see cref="Time.unscaledDeltaTime"/>（不随 <c>Time.timeScale</c> 变）。</para>
    /// <para><b>淡出后停止</b>：目标为 0 时音量平滑降到听不见（<see cref="SilentVolume"/>）才真正 <c>Stop()</c>；
    /// 目标重新为正会重新 <c>Play()</c>（从静音淡入）。声源与 clip 都保留，重新推目标即可再次淡入。</para>
    /// <para><b>高速模式整层静音</b>：<see cref="ParticleSimulationDriver.HighSpeedActive"/> 期间（进入后到玩家确认退出前）
    /// <see cref="HighSpeedSilenced"/> 恒为 true——所有声音都从本类过，因此这里一刀压掉就覆盖全部声源：
    /// <see cref="Update"/> **硬停**全部声床与一次性源（不淡出：淡出那段尾巴也还是在出声）、
    /// <see cref="SetAmbience"/> 不起播、<see cref="PlayOneShot"/> 直接丢弃。理由：高速模式一帧跑上百个逻辑帧
    /// （一场沙崩、一串雷暴全挤在同一个真实帧里），逐帧出声只会变成噪音。
    /// **目标响度照记**——驱动每帧还在推入需求，退出模式后由 <see cref="Update"/> 原样淡入。判定者留空（无驱动）时恒不静音。</para>
    /// <para><b>生命周期</b>：声床与一次性源都是运行时建的子物体（<c>HideFlags.DontSave</c>，绝不随场景落盘），
    /// 声床**惰性**：首次对某条音频 <see cref="SetAmbience"/> 才建，一次建好永久复用。
    /// <see cref="OnDestroy"/> 销毁全部自建子物体；<see cref="OnDisable"/> 把所有声床目标归零并停声（含一次性源）——
    /// 不会留下无人接管的循环，也不会积攒重复声部。</para>
    /// <para><b>查找</b>：<c>fileName</c> 只取**最后一段、去掉扩展名**，与 <see cref="_clips"/> 里每个
    /// <see cref="AudioClip"/> 的资产名做不区分大小写的比较——<c>"water"</c> / <c>"water.mp3"</c> /
    /// <c>"Assets/Sounds/water.mp3"</c> 等价。查不到只 <c>LogWarning</c>（同一个名字只警告一次）、不抛、不改动任何声床。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SoundController : MonoBehaviour
    {
        /// <summary>单个真实帧计入的时间步长上界（秒）：编辑器卡顿 / 断点后 dt 可能是好几秒。</summary>
        public const float MaxDeltaTime = 0.25f;

        /// <summary>「已经听不见」的音量阈值：目标为 0 且音量低到它以下才真正 <c>Stop()</c>（浮点不会恰好到 0）。</summary>
        public const float SilentVolume = 1e-4f;

        [Tooltip("可查的音频表：查询键 =「文件名最后一段去掉扩展名」= AudioClip 的资产名（不区分大小写）。每条音频各有一条独立循环声床。")]
        [SerializeField] private AudioClip[] _clips;

        [Tooltip("声床响度平滑速度：每秒把音量朝目标推进的幅度（1 = 满量程约 1 秒）。0 = 立即跳变（会有台阶声）。")]
        [SerializeField, Min(0f)] private float _loudnessPerSecond = 3f;

        [Tooltip("高速模式的判定者：留空则回退到同物体 / 场景里的 ParticleSimulationDriver（只为读 HighSpeedActive）。" +
                 "留空且找不到驱动 = 永不静音。")]
        [SerializeField] private ParticleSimulationDriver _driver;

        /// <summary>与 <see cref="_clips"/> 下标一一对应的循环声床；没推过的槽为 null（惰性建源）。</summary>
        private AudioSource[] _beds;

        /// <summary>每条声床的**目标**响度（<c>[0, 1]</c>）；实际音量由 <see cref="Update"/> 平滑朝它推进。</summary>
        private float[] _targets;

        /// <summary>一次性音效源（<c>loop = false</c>）：只给 <see cref="PlayOneShot"/> 用，绝不参与声床平滑。</summary>
        private AudioSource _oneShot;

        /// <summary>上一次警告过的文件名：同一个名字只报一次，避免每帧刷日志。</summary>
        private string _warnedFile;

        /// <summary>一次性音效成功起播的累计计数（只加不减，诊断 / 测试可观测量）。</summary>
        private int _oneShotPlayCount;

        /// <summary>累计成功起播的一次性音效次数（<see cref="PlayOneShot"/> 每次命中音频 +1）。</summary>
        public int OneShotPlayCount
        {
            get { return _oneShotPlayCount; }
        }

        /// <summary>
        /// 是否因**高速模式**而整层静音：高速会话 <c>Active</c> 期间（进入后、玩家确认退出前——
        /// 跑满时长自动暂停后仍为 true）恒为 true。没接驱动（留空且场景里也没有）时恒为 false。
        /// 置真时全部声源硬停、两个播放入口一并丢弃（见类注释「高速模式整层静音」）。
        /// </summary>
        public bool HighSpeedSilenced
        {
            get { return _driver != null && _driver.HighSpeedActive; }
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

            EnsureBeds();
            EnsureOneShot();

            if (FindObjectOfType<AudioListener>() == null)
            {
                Debug.LogWarning("[SoundController] 场景里没有 AudioListener，播放不会出声。");
            }
        }

        private void OnDisable()
        {
            // 立即停声：禁用 / 暂停不该留下无人接管的循环声床，重新启用时由调用方重新推目标、从静音淡入。
            if (_beds != null)
            {
                for (int i = 0; i < _beds.Length; i++)
                {
                    _targets[i] = 0f;
                }
            }

            StopAllSources();
        }

        /// <summary>
        /// 立刻停掉全部声源（循环声床 + 一次性源）并把声床音量归零——不留任何正在出声的声部。
        /// 禁用（<see cref="OnDisable"/>）与高速模式静音（<see cref="Update"/>）共用同一口径。
        /// **不动目标响度**：那记的是驱动的需求，重新具备发声条件时由它推入的目标原样淡入。
        /// </summary>
        private void StopAllSources()
        {
            if (_beds != null)
            {
                for (int i = 0; i < _beds.Length; i++)
                {
                    AudioSource bed = _beds[i];
                    if (bed != null)
                    {
                        bed.Stop();
                        bed.volume = 0f;
                    }
                }
            }

            if (_oneShot != null)
            {
                _oneShot.Stop();
            }
        }

        private void OnDestroy()
        {
            if (_beds != null)
            {
                for (int i = 0; i < _beds.Length; i++)
                {
                    if (_beds[i] != null)
                    {
                        // 子物体通常随本物体一起销毁；这里兜住「只删组件、不删物体」的情况。
                        Destroy(_beds[i].gameObject);
                        _beds[i] = null;
                    }
                }
            }

            if (_oneShot != null)
            {
                Destroy(_oneShot.gameObject);
                _oneShot = null;
            }
        }

        /// <summary>
        /// 把名为 <paramref name="fileName"/> 的音频那条循环声床的目标响度设为 <paramref name="loudness"/>
        /// （钳在 <c>[0, 1]</c>）；目标为正且该声床未在播时起播。可每帧调用：改响度不重播、不切相位、不新增声部，
        /// 也**不动别的声床**——水与火各自独立、可以同时响。
        /// 查不到音频时保持现状，只按名字报一次警告。
        /// </summary>
        public void SetAmbience(string fileName, float loudness)
        {
            int index = FindIndex(fileName);
            if (index < 0)
            {
                WarnUnknown(fileName);
                return;
            }

            EnsureBeds();
            _targets[index] = Mathf.Clamp01(loudness);

            AudioSource bed = EnsureBed(index);
            if (_targets[index] > 0f && !HighSpeedSilenced && !bed.isPlaying)
            {
                bed.Play();
            }
        }

        /// <summary>
        /// 把名为 <paramref name="fileName"/> 的那条声床目标响度归零：音量由 <see cref="Update"/> 平滑淡出，
        /// 低到听不见才真正 <c>Stop()</c>。**只影响这一条**，其它声床纹丝不动；同名音频的后续
        /// <see cref="SetAmbience"/> 会把目标推回去重新淡入。查不到音频时不抛、不改动其它声床。
        /// </summary>
        public void StopAmbience(string fileName)
        {
            int index = FindIndex(fileName);
            if (index < 0)
            {
                WarnUnknown(fileName);
                return;
            }

            if (_targets != null && index < _targets.Length)
            {
                _targets[index] = 0f;
            }
        }

        /// <summary>
        /// 一次性播放名为 <paramref name="fileName"/> 的音频（闪电那种只在事件当帧响一下的短音效）。
        /// 走独立的 <c>loop = false</c> 声源与 <see cref="AudioSource.PlayOneShot(AudioClip, float)"/>：
        /// 不循环、不锁相位、不取模、不重播，也不需要片段时长；重叠调用由原生混音叠加。
        /// **不触碰任何循环声床**的目标或音量。查不到音频时只按名字报一次警告、不抛。
        /// </summary>
        public void PlayOneShot(string fileName, float loudness)
        {
            // 高速模式整层静音：连账都不记（没有起播就没有次数）。调用方的清账不受影响——它读自己的监视器。
            if (HighSpeedSilenced)
            {
                return;
            }

            int index = FindIndex(fileName);
            if (index < 0)
            {
                WarnUnknown(fileName);
                return;
            }

            EnsureOneShot();
            _oneShot.PlayOneShot(_clips[index], Mathf.Clamp01(loudness));
            _oneShotPlayCount++;
        }

        /// <summary>名为 <paramref name="fileName"/> 的声床当前**目标**响度；没有该声床（未知音频 / 从未推过）时为 0。</summary>
        public float TargetLoudness(string fileName)
        {
            int index = FindIndex(fileName);
            if (index < 0 || _targets == null || index >= _targets.Length)
            {
                return 0f;
            }

            return _targets[index];
        }

        /// <summary>名为 <paramref name="fileName"/> 的声床当前**实际**音量（平滑的产物）；声床尚未建立时为 0。</summary>
        public float CurrentLoudness(string fileName)
        {
            AudioSource bed = FindBed(fileName);
            return bed != null ? bed.volume : 0f;
        }

        /// <summary>名为 <paramref name="fileName"/> 的声床当前是否在响；没有该声床时为 false。</summary>
        public bool IsPlaying(string fileName)
        {
            AudioSource bed = FindBed(fileName);
            return bed != null && bed.isPlaying;
        }

        /// <summary>
        /// 按 <paramref name="fileName"/> 在 <see cref="_clips"/> 里找音频。
        /// 只比较**最后一段、去掉扩展名**的文件名与资产名，不区分大小写；找不到返回 null。
        /// </summary>
        public AudioClip FindClip(string fileName)
        {
            int index = FindIndex(fileName);
            return index >= 0 ? _clips[index] : null;
        }

        /// <summary>
        /// 把真实帧步长（秒）钳到可用区间 <c>[0, <see cref="MaxDeltaTime"/>]</c>：负数 / NaN / 巨大值
        /// （编辑器卡顿、断点调试、窗口最小化后恢复）一律折算成有限的一步。
        /// 所有按 dt 推进的平滑都必须经过它——这是「异常 deltaTime 不会造成瞬时跳变」的唯一闸门。
        /// </summary>
        public static float ClampDeltaTime(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || deltaTime <= 0f)
            {
                return 0f;
            }

            return deltaTime > MaxDeltaTime ? MaxDeltaTime : deltaTime;
        }

        /// <summary>
        /// 响度平滑的一步：<paramref name="deltaTime"/> 先经 <see cref="ClampDeltaTime"/> 钳制，再把
        /// <paramref name="current"/> 朝 <paramref name="target"/> 推进 <c>ratePerSecond × dt</c>（不超过目标）。
        /// <paramref name="ratePerSecond"/> 非正时直接对齐到目标（等于关掉平滑）。
        /// </summary>
        public static float NextVolume(float current, float target, float ratePerSecond, float deltaTime)
        {
            if (ratePerSecond <= 0f)
            {
                return target;
            }

            return Mathf.MoveTowards(current, target, ratePerSecond * ClampDeltaTime(deltaTime));
        }

        /// <summary>
        /// 把一个「比例 / 阈值」区间线性映射成 <c>[0, 1]</c> 的响度：<paramref name="minValue"/> 处为 0、
        /// <paramref name="fullValue"/> 处为 1、区间外钳制。区间退化（<paramref name="fullValue"/> ≤
        /// <paramref name="minValue"/>）时恒返回 1——越过死区即满响，且永不除零。水流动声的「运动比例 → 响度」用它。
        /// </summary>
        public static float MapLoudness(float value, float minValue, float fullValue)
        {
            if (fullValue <= minValue)
            {
                return 1f;
            }

            return Mathf.Clamp01((value - minValue) / (fullValue - minValue));
        }

        /// <summary>
        /// 一阶低通（EMA）一步：把 <paramref name="current"/> 朝 <paramref name="target"/> 靠拢
        /// <c>dt / (smoothingSeconds + dt)</c> 的比例——时间常数与帧率无关，抹平逐帧抖动。
        /// <paramref name="smoothingSeconds"/> 非正时直接对齐目标（等于关掉平滑）；dt 经
        /// <see cref="ClampDeltaTime"/> 钳制，非正 / NaN 冻结、过大只按上界推进。
        /// 水流动声的目标响度防抖用它。
        /// </summary>
        public static float Ema(float current, float target, float smoothingSeconds, float deltaTime)
        {
            if (smoothingSeconds <= 0f)
            {
                return target;
            }

            float dt = ClampDeltaTime(deltaTime);
            if (dt <= 0f)
            {
                return current;
            }

            return current + (target - current) * (dt / (smoothingSeconds + dt));
        }

        private void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 一帧的声源推进：每个声床的音量朝目标走一步、目标为正且未在播就起播、目标为 0 且已淡到听不见就停源。
        /// 高速模式静音期间（<see cref="HighSpeedSilenced"/>）不淡出、直接硬停全部声源并返回。
        /// 由 <see cref="Update"/> 每帧调一次；公开（含显式 <paramref name="deltaTime"/>）是为了让测试能脱离帧循环
        /// 确定性地驱动它——与四个声音驱动器的 <c>Pump</c> 同一口径。
        /// </summary>
        /// <param name="deltaTime">本帧真实时长（秒）；经 <see cref="ClampDeltaTime"/> 钳制。</param>
        public void Tick(float deltaTime)
        {
            if (_beds == null)
            {
                return;
            }

            // 高速模式：整层硬停（不淡出——淡出那段尾巴也还是在出声）。目标数组不动：
            // 驱动每帧照旧推入需求，退出模式后下面这段循环原样把它们淡入。
            if (HighSpeedSilenced)
            {
                StopAllSources();
                return;
            }

            for (int i = 0; i < _beds.Length; i++)
            {
                AudioSource bed = _beds[i];
                if (bed == null)
                {
                    continue;
                }

                float target = _targets[i];
                bed.volume = NextVolume(bed.volume, target, _loudnessPerSecond, deltaTime);

                if (target > 0f)
                {
                    // 片段循环播放，正常只在起播那一帧需要 Play；这里兜住外部 Stop / 音频设备变动后的静默。
                    if (!bed.isPlaying)
                    {
                        bed.Play();
                    }

                    continue;
                }

                // 目标为 0：淡到听不见就真正停掉，不留一条空转的循环声部（声源与 clip 保留，可再次淡入）。
                if (bed.isPlaying && bed.volume <= SilentVolume)
                {
                    bed.Stop();
                }
            }
        }

        /// <summary>按 <see cref="_clips"/> 的长度对齐声床 / 目标数组；长度不变就原地复用（不重建、不丢声部）。</summary>
        private void EnsureBeds()
        {
            int count = _clips != null ? _clips.Length : 0;
            if (_beds != null && _beds.Length == count)
            {
                return;
            }

            var beds = new AudioSource[count];
            var targets = new float[count];

            // 音频表长度若在运行中变过（编辑器增删片段），下标对得上的声床继续用，不打断正在响的声部。
            if (_beds != null)
            {
                int keep = _beds.Length < count ? _beds.Length : count;
                Array.Copy(_beds, beds, keep);
                Array.Copy(_targets, targets, keep);
            }

            _beds = beds;
            _targets = targets;
        }

        /// <summary>建 / 复用下标 <paramref name="index"/> 的循环声床：2D、循环、不随场景落盘；一次建好永久复用。</summary>
        private AudioSource EnsureBed(int index)
        {
            AudioSource bed = _beds[index];
            if (bed != null)
            {
                return bed;
            }

            var bedObject = new GameObject("SoundBed") { hideFlags = HideFlags.DontSave };
            bedObject.transform.SetParent(transform, false);

            bed = bedObject.AddComponent<AudioSource>();
            bed.playOnAwake = false;
            bed.loop = true;
            bed.spatialBlend = 0f; // 2D：没有位置，也就不需要空间化
            bed.dopplerLevel = 0f;
            bed.clip = _clips[index];
            bed.volume = 0f;       // 起播从静音淡入（响度一律走平滑，不在 SetAmbience 里直接赋值）

            _beds[index] = bed;
            return bed;
        }

        /// <summary>建 / 复用那个一次性音效源：2D、不循环、不随场景落盘；<c>volume</c> 恒为 1（音量由调用参数给）。</summary>
        private AudioSource EnsureOneShot()
        {
            if (_oneShot != null)
            {
                return _oneShot;
            }

            var sourceObject = new GameObject("SoundOneShot") { hideFlags = HideFlags.DontSave };
            sourceObject.transform.SetParent(transform, false);

            _oneShot = sourceObject.AddComponent<AudioSource>();
            _oneShot.playOnAwake = false;
            _oneShot.loop = false;
            _oneShot.spatialBlend = 0f;
            _oneShot.dopplerLevel = 0f;
            _oneShot.volume = 1f;
            return _oneShot;
        }

        /// <summary>按文件名取声床；未知音频 / 尚未建源时为 null。</summary>
        private AudioSource FindBed(string fileName)
        {
            int index = FindIndex(fileName);
            if (index < 0 || _beds == null || index >= _beds.Length)
            {
                return null;
            }

            return _beds[index];
        }

        /// <summary>查音频在 <see cref="_clips"/> 里的下标；名字为空 / 表为空 / 查不到返回 -1。查法见 <see cref="FindClip"/>。</summary>
        private int FindIndex(string fileName)
        {
            if (_clips == null || string.IsNullOrEmpty(fileName))
            {
                return -1;
            }

            int start = 0;
            for (int i = 0; i < fileName.Length; i++)
            {
                if (fileName[i] == '/' || fileName[i] == '\\')
                {
                    start = i + 1;
                }
            }

            int end = fileName.Length;
            int dot = fileName.LastIndexOf('.');
            if (dot > start)
            {
                end = dot;
            }

            int length = end - start;
            if (length <= 0)
            {
                return -1;
            }

            for (int i = 0; i < _clips.Length; i++)
            {
                AudioClip clip = _clips[i];
                if (clip != null
                    && clip.name.Length == length
                    && string.Compare(clip.name, 0, fileName, start, length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>同一个文件名只报一次「找不到音频」的警告（每帧推目标响度时不会刷屏）。</summary>
        private void WarnUnknown(string fileName)
        {
            if (fileName == _warnedFile)
            {
                return;
            }

            _warnedFile = fileName;
            Debug.LogWarning("[SoundController] 找不到音频 \"" + fileName + "\"，忽略这次播放。");
        }
    }
}
