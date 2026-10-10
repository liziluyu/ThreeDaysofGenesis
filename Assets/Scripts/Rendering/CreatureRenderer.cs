using System.Collections.Generic;
using TapTap2026.Game;
using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Rendering
{
    /// <summary>
    /// 动物实体层渲染：每只 <see cref="Creature"/> 一个子 GameObject + <see cref="SpriteRenderer"/>，
    /// sprite 是运行时生成的 1×1 白色纹理（拉成碰撞盒大小）——真美术接入前先把实体看得见。
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
        }

        private ParticleSimulationDriver _driver;
        private CreatureWorld _trackedWorld;

        private Sprite _placeholderSprite;
        private Texture2D _placeholderTexture;

        private readonly Dictionary<Creature, View> _views = new Dictionary<Creature, View>();
        private readonly List<Creature> _stale = new List<Creature>();

        private int _frame;
        private int _trackedLogicTicks = -1;
        private float _stepTime;

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
            }

            // 逻辑帧计数变化即一个新逻辑帧落定，重置插值计时。暂停时不变化，alpha 因此饱和到 1。
            int logicTicks = world.LogicTicks;
            if (logicTicks != _trackedLogicTicks)
            {
                _trackedLogicTicks = logicTicks;
                _stepTime = Time.time;
            }

            float alpha = Mathf.Clamp01((Time.time - _stepTime) * _driver.LogicRate);

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

                // 位置 = 碰撞盒底边中点（X, Y）先插值，再上抬半高换算到 sprite 中心。
                view.Renderer.transform.position = new Vector3(
                    Mathf.Lerp(creature.PrevX, creature.X, alpha),
                    Mathf.Lerp(creature.PrevY, creature.Y, alpha) + creature.Height * 0.5f,
                    CreatureZ);
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

        /// <summary>新建一只实体的视图：DontSave 的子物体 + SpriteRenderer，尺寸取碰撞盒，颜色取占位色。</summary>
        private View CreateView(Creature creature)
        {
            // 子物体整条渲染链都是运行期生成的、不被任何序列化字段引用：必须 DontSave，
            // 否则保存场景会把这个空壳烘进 .unity 文件（同 ParticleRenderer.CreateQuad）。
            var child = new GameObject("Creature") { hideFlags = HideFlags.DontSave };
            child.transform.SetParent(transform, false);
            child.transform.localScale = new Vector3(creature.Width, creature.Height, 1f);

            var renderer = child.AddComponent<SpriteRenderer>();
            renderer.sprite = EnsurePlaceholderSprite();
            renderer.color = creature.PlaceholderColor;

            return new View { Renderer = renderer };
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
