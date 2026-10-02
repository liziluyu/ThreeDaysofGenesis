using TapTap2026.Simulation;
using UnityEngine;

namespace TapTap2026.Game
{
    /// <summary>
    /// 可选的演示内容：石头地板 + 两侧石头挡墙 + 中央一堆沙。
    /// **默认什么都不做**——沙箱进播放时是空世界；只有显式调用 <see cref="Reseed"/>
    /// （Inspector 右键菜单或代码）才会搭建。纯演示/调试用，不属于模拟内核，也不参与固定步长驱动。
    /// </summary>
    /// <remarks>
    /// <para>挡墙做成**从地板一直砌到顶的整列**：<c>StoneParticle</c> 是受重力的固体（非静态），
    /// 悬空的石块会掉下来；整列每块都由下方支撑，因此稳定不塌。</para>
    /// <para>沙只横向铺展靠斜落，因而是自然收敛的安息角堆。</para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ParticleSandboxSeeder : MonoBehaviour
    {
        [SerializeField] private ParticleSimulationDriver _driver;

        [Header("容器（石头）")]
        [SerializeField, Min(0)] private int _floorThickness = 3;
        [SerializeField, Min(0)] private int _wallThickness = 3;

        [Header("沙堆（占内部尺寸的比例）")]
        [SerializeField, Range(0.05f, 1f)] private float _sandWidthRatio = 0.5f;
        [SerializeField, Range(0.05f, 1f)] private float _sandHeightRatio = 0.6f;

        /// <summary>搭建时实际投放的沙粒数，供 HUD / 测试读取。</summary>
        public int SandPlaced { get; private set; }

        /// <summary>
        /// 清空世界、重新搭建演示内容，并立即上屏一次。
        /// **只会在显式调用时执行**（Inspector 右键菜单或代码），沙箱进播放时不会自动搭任何东西。
        /// </summary>
        [ContextMenu("铺设演示内容（石头容器 + 沙堆）")]
        public void Reseed()
        {
            ParticleSimulationDriver driver = ResolveDriver();
            if (driver == null)
            {
                Debug.LogError("[ParticleSandboxSeeder] 找不到 ParticleSimulationDriver，无法搭建沙箱。");
                return;
            }

            ParticleWorld world = driver.World;
            if (world == null)
            {
                Debug.LogError("[ParticleSandboxSeeder] 世界还未创建（driver 的 Awake 尚未执行）。");
                return;
            }

            if (ParticleIds.Stone == ParticleRegistry.AirTypeId)
            {
                ParticleRegistration.RegisterAll();
            }

            Clear(world);
            int walls = BuildContainer(world);
            SandPlaced = FillSand(world);

            driver.PresentNow();

            Debug.Log(
                $"[ParticleSandboxSeeder] 沙箱就绪：{world.Width}x{world.Height}，"
                + $"石头 {walls} 块，沙 {SandPlaced} 粒。");
        }

        private ParticleSimulationDriver ResolveDriver()
        {
            if (_driver != null)
            {
                return _driver;
            }

            _driver = GetComponent<ParticleSimulationDriver>();
            if (_driver == null)
            {
                _driver = FindObjectOfType<ParticleSimulationDriver>();
            }

            return _driver;
        }

        private static void Clear(ParticleWorld world)
        {
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    world.Clear(x, y);
                }
            }
        }

        /// <summary>铺地板 + 砌两侧整列挡墙，返回石头总数。</summary>
        private int BuildContainer(ParticleWorld world)
        {
            int width = world.Width;
            int height = world.Height;
            int floor = Mathf.Clamp(_floorThickness, 0, height);
            int wall = Mathf.Clamp(_wallThickness, 0, width / 2);
            int count = 0;

            for (int y = 0; y < floor; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    world.Set(x, y, ParticleIds.Stone);
                    count++;
                }
            }

            for (int y = floor; y < height; y++)
            {
                for (int i = 0; i < wall; i++)
                {
                    world.Set(i, y, ParticleIds.Stone);
                    world.Set(width - 1 - i, y, ParticleIds.Stone);
                    count += 2;
                }
            }

            return count;
        }

        /// <summary>在内部区域中央投放一块沙，返回沙粒数。它会自行塌成安息角堆。</summary>
        private int FillSand(ParticleWorld world)
        {
            int floor = Mathf.Clamp(_floorThickness, 0, world.Height);
            int wall = Mathf.Clamp(_wallThickness, 0, world.Width / 2);

            int interiorWidth = world.Width - wall * 2;
            int interiorHeight = world.Height - floor;
            if (interiorWidth <= 0 || interiorHeight <= 0)
            {
                return 0;
            }

            int sandWidth = Mathf.Clamp(Mathf.RoundToInt(interiorWidth * _sandWidthRatio), 1, interiorWidth);
            int sandHeight = Mathf.Clamp(Mathf.RoundToInt(interiorHeight * _sandHeightRatio), 1, interiorHeight);
            int startX = wall + (interiorWidth - sandWidth) / 2;

            int placed = 0;
            for (int y = floor; y < floor + sandHeight; y++)
            {
                for (int x = startX; x < startX + sandWidth; x++)
                {
                    world.Set(x, y, ParticleIds.Sand);
                    placed++;
                }
            }

            return placed;
        }
    }
}
