using System.Collections.Generic;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// 动物实体层渲染：每只 <see cref="Creature"/> 一个子 GameObject + <see cref="SpriteRenderer"/>。
    /// 已接线的物种（兔子 / 鱼，见 <see cref="ResolveIdleFrames"/>）播真 sprite 帧序列；未接线的退回运行时生成的 1×1 白色占位块（染占位色、拉成碰撞盒）。
    /// </summary>
    /// <remarks>
    /// <para>与 <see cref="ParticleRenderer"/>「整张网格一个 quad」不同：实体数量少、位置是连续浮点，
    /// 只能一实体一物体。本组件只读 <see cref="CreatureWorld"/> 的公开状态，不推进任何逻辑帧。</para>
    /// <para>对账方式：每帧**轮询** <see cref="ParticleSimulationDriver.Creatures"/>（不订阅事件）——
    /// 世界重建会整只换掉 <see cref="CreatureWorld"/> 实例，按引用比对天然免疫，不会留下悬挂视图；
    /// 已死亡 / 已被清扫移除的实体也在同一次对账里销掉视图。</para>
    /// <para>插值：逻辑帧只有 <see cref="ParticleSimulationSettings.LogicRate"/> Hz（当前 20 Hz），渲染帧远高于它，
    /// 用 <see cref="Creature.PrevX"/>/<see cref="Creature.PrevY"/> 与当前位置按本逻辑帧已流逝的时间比例插值，
    /// 否则动起来是一格一格跳。逻辑帧停住（暂停）时 alpha 饱和到 1，实体稳定停在落点，不需要额外分支。</para>
    /// <para>z = −1：世界 quad 在 z = 0、相机在 z = −10，实体画在世界之上（取景约定见 <see cref="ParticleRenderer"/>）。</para>
    /// <para>所有运行期资源（纹理 / sprite / 子物体）都带 <see cref="HideFlags.DontSave"/>，绝不随场景落盘——
    /// 与 <see cref="ParticleRenderer"/> 的网格链同一约定。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class CreatureRenderer : MonoBehaviour
    {
        /// <summary>实体所在的深度：世界 quad 在 z = 0，本层贴在相机与 quad 之间。</summary>
        private const float CreatureZ = -1f;

        /// <summary>一只实体的视图：子物体上的 <see cref="SpriteRenderer"/> + 本帧对账是否扫到它。</summary>
        private sealed class View
        {
            public SpriteRenderer Renderer;
            public int SeenFrame;
            /// <summary>真 sprite 动画帧（null = 占位模式：白块染占位色、拉成碰撞盒）。</summary>
            public Sprite[] Frames;
            /// <summary>脚底（X, Y）到 sprite 锚点的竖直偏移：占位 sprite pivot 居中 = 半高；切片 pivot 在底边 = 0。</summary>
            public float PivotLift;
            /// <summary>未调光的基准色（占位 = 占位色；真 sprite = 白）。每帧乘昼夜亮度后写入 SpriteRenderer。</summary>
            public Color BaseColor;
        }

        /// <summary>兔子 idle 动画帧（Sprite Editor 切片，pivot 底部中心）。为空或未接线时兔子退回占位块。</summary>
        [SerializeField] private Sprite[] _rabbitIdleFrames;

        /// <summary>鱼 idle 动画帧（同上规格）。为空或未接线时鱼退回占位块。</summary>
        [SerializeField] private Sprite[] _fishIdleFrames;

        /// <summary>idle 动画帧率（帧/秒）：2 = 每帧 500ms。表现层播放，不进模拟、不影响确定性。</summary>
        [SerializeField] private float _idleFps = 2f;

        /// <summary>真 sprite 的放大倍数：16 = 1 源像素恰好渲染成 1 格（与世界纹理的 texel 网格对齐，Point 过滤下不糊）。</summary>
        [SerializeField] private float _spriteScale = 16f;

        private ParticleRenderer _particles;

        private ParticleSimulationDriver _driver;
        private CreatureWorld _trackedWorld;

        private Sprite _placeholderSprite;
        private Texture2D _placeholderTexture;

        private readonly Dictionary<Creature, View> _views = new Dictionary<Creature, View>();
        private readonly List<Creature> _stale = new List<Creature>();

        private int _frame;
        private int _trackedLogicTicks = -1;
        private float _stepTime;

        private float _animTime;

        private void Awake()
        {
            _driver = GetComponent<ParticleSimulationDriver>();
            _particles = GetComponent<ParticleRenderer>();
        }

        private void Update()
        {
            if (_driver == null)
            {
                return;
            }

            CreatureWorld world = _driver.Creatures;
            if (world == null)
            {
                return;
            }

            // 世界重建会整个换掉 CreatureWorld 实例：旧实例上的实体全部作废，连视图一起清掉。
            if (!ReferenceEquals(world, _trackedWorld))
            {
                DestroyAllViews();
                _trackedWorld = world;
                _trackedLogicTicks = -1;
                _animTime = 0f;
            }

            // 逻辑帧计数变化即一个新逻辑帧落定，重置插值计时。暂停时不变化，alpha 因此饱和到 1。
            int logicTicks = world.LogicTicks;
            if (logicTicks != _trackedLogicTicks)
            {
                _trackedLogicTicks = logicTicks;
                _stepTime = Time.time;
            }

            // 动画时钟只随未暂停的真实时间走：暂停时帧索引冻结，与插值饱和同一语义。
            if (!_driver.Paused)
            {
                _animTime += Time.deltaTime;
            }

            float alpha = Mathf.Clamp01((Time.time - _stepTime) * _driver.LogicRate);

            // 昼夜调光：与 ParticleRenderer 同一公式同一配置面（NightBrightness 由 ParticleLightDriver 推到 ParticleRenderer，
            // 这里直接读它的当前值），实体与粒子/云一起按 Lerp(夜晚亮度, 1, Daylight) 变暗。粒子世界缺失时按全亮。
            ParticleWorld particleWorld = _driver.World;
            float brightness = particleWorld != null && _particles != null
                ? Mathf.Lerp(_particles.NightBrightness, 1f, particleWorld.Light.Daylight)
                : 1f;

            _frame++;
            IReadOnlyList<Creature> creatures = world.Creatures;
            int count = creatures.Count;
            for (int i = 0; i < count; i++)
            {
                Creature creature = creatures[i];
                if (!_views.TryGetValue(creature, out View view))
                {
                    view = CreateView(creature);
                    _views.Add(creature, view);
                }

                view.SeenFrame = _frame;

                // 位置 = 碰撞盒底边中点（X, Y）先插值，再上抬到 sprite 锚点（占位 pivot 居中抬半高、切片 pivot 在底边不抬）。
                Transform childTransform = view.Renderer.transform;
                childTransform.position = new Vector3(
                    Mathf.Lerp(creature.PrevX, creature.X, alpha),
                    Mathf.Lerp(creature.PrevY, creature.Y, alpha) + view.PivotLift,
                    CreatureZ);

                if (view.Frames != null)
                {
                    view.Renderer.sprite = view.Frames[(int)(_animTime * _idleFps) % view.Frames.Length];
                    view.Renderer.flipX = creature.Facing < 0;
                }

                Color baseColor = view.BaseColor;
                view.Renderer.color = new Color(baseColor.r * brightness, baseColor.g * brightness, baseColor.b * brightness, baseColor.a);
            }

            // 销视图：本帧没扫到（已被 CreatureWorld 清扫移除）或已死亡的实体。先收集再删，避免边遍历边改字典。
            _stale.Clear();
            foreach (KeyValuePair<Creature, View> pair in _views)
            {
                if (pair.Value.SeenFrame != _frame || !pair.Key.Alive)
                {
                    _stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < _stale.Count; i++)
            {
                DestroyView(_stale[i]);
            }
        }

        private void OnDestroy()
        {
            DestroyAllViews();

            // 纹理与 sprite 都由本类在运行期创建，必须自己释放（同 ParticleRenderer 的纹理 / 网格 / 材质）。
            if (_placeholderSprite != null)
            {
                Destroy(_placeholderSprite);
                _placeholderSprite = null;
            }

            if (_placeholderTexture != null)
            {
                Destroy(_placeholderTexture);
                _placeholderTexture = null;
            }
        }

        /// <summary>新建一只实体的视图：DontSave 的子物体 + SpriteRenderer。
        /// 兔子且 idle 帧已接线时用真 sprite（原生像素尺寸、pivot 底边）；否则退回占位白块（染占位色、拉成碰撞盒）。</summary>
        private View CreateView(Creature creature)
        {
            // 子物体整条渲染链都是运行期生成的、不被任何序列化字段引用：必须 DontSave，
            // 否则保存场景会把这个空壳烘进 .unity 文件（同 ParticleRenderer.CreateQuad）。
            var child = new GameObject("Creature") { hideFlags = HideFlags.DontSave };
            child.transform.SetParent(transform, false);

            var renderer = child.AddComponent<SpriteRenderer>();

            Sprite[] frames = ResolveIdleFrames(creature);
            if (frames != null)
            {
                // 真 sprite：切片 pivot 已对齐脚底（见 Assets/Images/Animals/*/1/*.png 的导入设置），
                // 按 _spriteScale 放大（16 倍时 1 源像素 = 1 格，与兔子的像素大小一致），不染色。
                child.transform.localScale = new Vector3(_spriteScale, _spriteScale, 1f);
                renderer.sprite = frames[0];
                return new View { Renderer = renderer, Frames = frames, BaseColor = Color.white };
            }

            child.transform.localScale = new Vector3(creature.Width, creature.Height, 1f);
            renderer.sprite = EnsurePlaceholderSprite();
            renderer.color = creature.PlaceholderColor;

            return new View { Renderer = renderer, PivotLift = creature.Height * 0.5f, BaseColor = creature.PlaceholderColor };
        }

        /// <summary>
        /// 物种 → idle 帧序列：按物种类型取序列化字段，**未接线的物种返回 null**（调用方退回占位块）。
        /// </summary>
        /// <remarks>
        /// 用类型判断而不是字符串匹配：物种全集是闭集（兔 / 鸟 / 鱼，见 AGENTS.md），
        /// 新增物种时编译器会逼着这里补一行，而不是在运行期静默匹配失败。
        /// 帧序列为空或首帧缺失也视为未接线（手改 YAML 留下的半接线状态不该让渲染炸掉）。
        /// </remarks>
        private Sprite[] ResolveIdleFrames(Creature creature)
        {
            Sprite[] frames;
            if (creature is RabbitCreature)
            {
                frames = _rabbitIdleFrames;
            }
            else if (creature is FishCreature)
            {
                frames = _fishIdleFrames;
            }
            else
            {
                return null;
            }

            return frames != null && frames.Length > 0 && frames[0] != null ? frames : null;
        }

        /// <summary>懒建共享的占位 sprite：1×1 白纹理 + pivot 居中 + pixelsPerUnit = 1，因此 localScale 即世界尺寸（格）。</summary>
        private Sprite EnsurePlaceholderSprite()
        {
            if (_placeholderSprite != null)
            {
                return _placeholderSprite;
            }

            _placeholderTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, false)
            {
                name = "CreaturePlaceholder",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            _placeholderTexture.SetPixel(0, 0, Color.white);
            _placeholderTexture.Apply(false, false);

            _placeholderSprite = Sprite.Create(
                _placeholderTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            _placeholderSprite.name = "CreaturePlaceholder";
            _placeholderSprite.hideFlags = HideFlags.DontSave;

            return _placeholderSprite;
        }

        private void DestroyView(Creature creature)
        {
            if (!_views.TryGetValue(creature, out View view))
            {
                return;
            }

            _views.Remove(creature);
            if (view.Renderer != null)
            {
                Destroy(view.Renderer.gameObject);
            }
        }

        /// <summary>销毁全部视图。子物体可能已被销毁（销毁顺序不确定），逐个判空。</summary>
        private void DestroyAllViews()
        {
            foreach (KeyValuePair<Creature, View> pair in _views)
            {
                if (pair.Value.Renderer != null)
                {
                    Destroy(pair.Value.Renderer.gameObject);
                }
            }

            _views.Clear();
        }
    }
}
