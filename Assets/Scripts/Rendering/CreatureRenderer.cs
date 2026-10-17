using System.Collections.Generic;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// 动物实体层渲染：每只实体一个子 GameObject + <see cref="SpriteRenderer"/>。
    /// 已接线的物种（兔子 / 鱼 / 鸟，见 <see cref="ResolveIdleFrames"/>）播真 sprite 帧序列；未接线的退回运行时生成的 1×1 白色占位块（染占位色、拉成碰撞盒）。
    /// </summary>
    /// <remarks>
    /// <para>与 <see cref="ParticleRenderer"/>「整张网格一个 quad」不同：实体数量少、位置是连续浮点，
    /// 只能一实体一物体。本组件只读**已发布的帧快照**（<see cref="SimulationFrame.Creatures"/>），
    /// 不推进任何逻辑帧，也拿不到活的 <see cref="CreatureWorld"/>。</para>
    /// <para>对账方式：每帧轮询最新帧里的一串 <see cref="CreatureRenderData"/>，按稳定 <see cref="CreatureRenderData.Id"/> 与
    /// 视图一一对应（不订阅事件、不用对象引用）。世界重建会换掉世代号
    /// （<see cref="SimulationFrame.Generation"/>），据此整批清视图；已死亡 / 已被清扫的实体在新帧里不存在，
    /// 也在同一次对账里销掉视图。</para>
    /// <para>插值：逻辑帧只有 <see cref="ParticleSimulationSettings.LogicRate"/> Hz（当前 20 Hz），渲染帧远高于它，
    /// 用 <see cref="CreatureRenderData.PrevX"/>/<see cref="CreatureRenderData.PrevY"/> 与当前位置按本逻辑帧已流逝的
    /// 时间比例插值，否则动起来是一格一格跳。逻辑帧停住（暂停）时 alpha 饱和到 1，实体稳定停在落点。</para>
    /// <para>z = −1：世界 quad 在 z = 0、相机在 z = −10，实体画在世界之上（取景约定见 <see cref="ParticleRenderer"/>）。</para>
    /// <para>美术朝向：兔 / 鱼的源 sprite 都是**头朝左**（<see cref="SpriteFacesLeft"/>，序列化可配置），
    /// 因此 <see cref="CreatureRenderData.Facing"/> 为 +1（朝右）时镜像翻转、−1（朝左）时保持原样；
    /// 这条「美术 ↔ 运动」的映射只由 <see cref="ResolveFlipX"/> 一处定义，换美术时改那个字段即可。</para>
    /// <para>动画帧速倍率不在这里算（它要读 AI 状态，那是 world 私有的）：帧里带
    /// <see cref="CreatureRenderData.AnimationSpeedFactor"/>，本组件只做「帧索引 × 倍率」。</para>
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

        /// <summary>鸟 idle 动画帧（同上规格，7 片按切片名序 bird_0..bird_6）。为空或未接线时鸟退回占位块。</summary>
        [SerializeField] private Sprite[] _birdIdleFrames;

        /// <summary>idle 动画帧率（帧/秒）：2 = 每帧 500ms。表现层播放，不进模拟、不影响确定性。</summary>
        [SerializeField] private float _idleFps = 2f;

        /// <summary>真 sprite 的放大倍数：16 = 1 源像素恰好渲染成 1 格（与世界纹理的 texel 网格对齐，Point 过滤下不糊）。</summary>
        [SerializeField] private float _spriteScale = 16f;

        [Tooltip("源 sprite 的美术朝向：true = 头朝左。当前兔 / 鱼美术都是头朝左 ⇒ 向右移动时镜像翻转；" +
                 "换成头朝右的美术时设为 false。映射只由 CreatureRenderer.ResolveFlipX 使用。")]
        [SerializeField] private bool _spriteFacesLeft = true;

        /// <summary>源 sprite 的美术朝向（只读）：true = 头朝左。供测试与后续物种美术核对。</summary>
        public bool SpriteFacesLeft
        {
            get { return _spriteFacesLeft; }
        }

        private ParticleSimulationDriver _driver;

        /// <summary>已对账的帧世代（<see cref="SimulationFrame.Generation"/>）：换了就整批清视图。</summary>
        private int _trackedGeneration = -1;

        private Sprite _placeholderSprite;
        private Texture2D _placeholderTexture;

        /// <summary>实体渲染标识符 → 视图（用稳定 id 而不是对象引用对账，帧里传的就是 id）。</summary>
        private readonly Dictionary<int, View> _views = new Dictionary<int, View>();
        private readonly List<int> _stale = new List<int>();

        private int _frame;
        private int _trackedLogicTicks = -1;
        private float _stepTime;

        private float _animTime;

        private void Awake()
        {
            _driver = GetComponent<ParticleSimulationDriver>();
        }

        private void Update()
        {
            if (_driver == null)
            {
                return;
            }

            SimulationFrame frame = _driver.CurrentFrame;
            if (frame == null)
            {
                return;
            }

            // 世界重建会换世代：旧世代上的实体全部作废，连视图一起清掉。
            if (frame.Generation != _trackedGeneration)
            {
                DestroyAllViews();
                _trackedGeneration = frame.Generation;
                _trackedLogicTicks = -1;
                _animTime = 0f;
            }

            CreatureFrame creatures = frame.Creatures;

            // 逻辑帧计数变化即一个新逻辑帧落定，重置插值计时。暂停时不变化，alpha 因此饱和到 1。
            if (creatures.LogicTicks != _trackedLogicTicks)
            {
                _trackedLogicTicks = creatures.LogicTicks;
                _stepTime = Time.time;
            }

            // 动画时钟只随未暂停的真实时间走：暂停时帧索引冻结，与插值饱和同一语义。
            if (!frame.World.Paused)
            {
                _animTime += Time.deltaTime;
            }

            float alpha = Mathf.Clamp01((Time.time - _stepTime) * creatures.LogicRate);

            // 昼夜调光：与粒子 / 云同一份亮度（帧发布侧按日照度与光照配置算好的整场倍率），
            // 因此实体与像素永远同步变暗，不在这里重算公式。
            float brightness = frame.Render.Brightness;

            _frame++;
            int count = creatures.Count;
            for (int i = 0; i < count; i++)
            {
                CreatureRenderData creature = creatures.Creature(i);
                if (!_views.TryGetValue(creature.Id, out View view))
                {
                    view = CreateView(creature);
                    _views.Add(creature.Id, view);
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
                    // §8：帧播放速度随状态缩放（不换帧图）。倍率由帧发布侧按 AI 状态算好，这里只乘。
                    view.Renderer.sprite = view.Frames[(int)(_animTime * _idleFps * creature.AnimationSpeedFactor) % view.Frames.Length];
                    // 朝向由 ResolveFlipX 统一决定（美术头朝左 ⇒ 朝右才翻），此处不写第二份分支。
                    view.Renderer.flipX = ResolveFlipX(creature.Facing, _spriteFacesLeft);
                }

                Color baseColor = view.BaseColor;
                view.Renderer.color = new Color(baseColor.r * brightness, baseColor.g * brightness, baseColor.b * brightness, baseColor.a);
            }

            // 销视图：本帧没扫到（已被清扫移除）的实体。先收集再删，避免边遍历边改字典。
            _stale.Clear();
            foreach (KeyValuePair<int, View> pair in _views)
            {
                if (pair.Value.SeenFrame != _frame)
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
        /// 该物种 idle 帧已接线时用真 sprite（原生像素尺寸、pivot 底边）；否则退回占位白块（染占位色、拉成碰撞盒）。</summary>
        private View CreateView(in CreatureRenderData creature)
        {
            // 子物体整条渲染链都是运行期生成的、不被任何序列化字段引用：必须 DontSave，
            // 否则保存场景会把这个空壳烘进 .unity 文件（同 ParticleRenderer.CreateQuad）。
            var child = new GameObject("Creature") { hideFlags = HideFlags.DontSave };
            child.transform.SetParent(transform, false);

            var renderer = child.AddComponent<SpriteRenderer>();

            Sprite[] frames = ResolveIdleFrames(creature.Species);
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

            // 帧载荷是项目自有的纯值颜色（worker 可生产），Unity 上屏前在这里换成 Color32 —— 与像素上传同一接缝。
            Color placeholder = RenderColorConversion.ToColor32(creature.PlaceholderColor);
            renderer.color = placeholder;

            return new View { Renderer = renderer, PivotLift = creature.Height * 0.5f, BaseColor = placeholder };
        }

        /// <summary>
        /// 「美术朝向 ↔ 运动朝向」的唯一映射点：给定运动朝向 <paramref name="facing"/>（+1 = 朝右、−1 = 朝左，
        /// 见 <see cref="CreatureRenderData.Facing"/>）与美术约定 <paramref name="spriteFacesLeft"/>（true = 源 sprite 头朝左），
        /// 返回该实体该用的 <see cref="SpriteRenderer.flipX"/>。
        /// </summary>
        /// <remarks>
        /// 源 sprite 头朝左时，只有朝右移动才需要镜像（<c>facing &gt; 0</c>）；头朝右的美术则相反，朝左移动时镜像（<c>facing &lt; 0</c>）。
        /// <paramref name="facing"/> 为 0（静止 / 朝向未定）时不翻，保持美术原样。
        /// 纯函数、无状态：所有翻转判断都必须走这里，渲染处不得再写第二份分支。
        /// </remarks>
        public static bool ResolveFlipX(int facing, bool spriteFacesLeft)
        {
            return spriteFacesLeft ? facing > 0 : facing < 0;
        }

        /// <summary>
        /// 物种 → idle 帧序列：按 <see cref="CreatureSpecies"/> 取序列化字段，**未接线的物种返回 null**（调用方退回占位块）。
        /// </summary>
        /// <remarks>
        /// 用物种枚举而不是类型判断：物种全集是闭集（兔 / 鸟 / 鱼，见 <see cref="CreatureSpawns"/>），
        /// 新增物种时编译器会逼着这里补一行，而不是在运行期静默匹配失败。
        /// 帧序列为空或首帧缺失也视为未接线（手改 YAML 留下的半接线状态不该让渲染炸掉）。
        /// </remarks>
        private Sprite[] ResolveIdleFrames(CreatureSpecies species)
        {
            Sprite[] frames;
            switch (species)
            {
                case CreatureSpecies.Rabbit:
                    frames = _rabbitIdleFrames;
                    break;

                case CreatureSpecies.Fish:
                    frames = _fishIdleFrames;
                    break;

                case CreatureSpecies.Bird:
                    frames = _birdIdleFrames;
                    break;

                default:
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

        private void DestroyView(int creatureId)
        {
            if (!_views.TryGetValue(creatureId, out View view))
            {
                return;
            }

            _views.Remove(creatureId);
            if (view.Renderer != null)
            {
                Destroy(view.Renderer.gameObject);
            }
        }

        /// <summary>销毁全部视图。子物体可能已被销毁（销毁顺序不确定），逐个判空。</summary>
        private void DestroyAllViews()
        {
            foreach (KeyValuePair<int, View> pair in _views)
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
