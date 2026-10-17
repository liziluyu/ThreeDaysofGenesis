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
    /// <para><b>本组件不写世界</b>：它把「清场 + 铺地板 + 砌墙 + 堆沙」表达成一串值类型命令
    /// （<see cref="SimulationCommandKind.ClearAll"/> / <see cref="SimulationCommandKind.FillRect"/>），
    /// 由驱动在下一个 tick 边界按序应用，最后补一条 <see cref="SimulationCommandKind.PresentNow"/> 立即上屏。
    /// 尺寸取自最新已发布帧（<see cref="ParticleSimulationDriver.CurrentFrame"/>）。</para>
    /// <para><b>预设已弃用</b>：石头改为「斜落需 10 格净空」之后，整列挡墙内缘高处的石块会沿斜面
    /// 滑入容器（正下方有支撑挡不住斜落），墙体不再稳定。本组件保留仅为兼容旧场景，
    /// 新演示内容请直接由 <c>ParticleEditor</c> 手绘。</para>
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
        /// 清空世界、重新搭建演示内容，并提交一条立即上屏命令。
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

            SimulationFrame frame = driver.CurrentFrame;
            if (frame == null)
            {
                Debug.LogError("[ParticleSandboxSeeder] 世界还未创建（driver 的 Awake 尚未执行）。");
                return;
            }

            if (ParticleIds.Stone == ParticleRegistry.AirTypeId)
            {
                ParticleRegistration.RegisterAll();
            }

            int width = frame.World.Width;
            int height = frame.World.Height;

            driver.Submit(SimulationCommand.ClearAll());
            int walls = BuildContainer(driver, width, height);
            SandPlaced = FillSand(driver, width, height);

            // 命令同批入队、按序应用；补一条立即上屏，搭建结果当帧可见。
            driver.Submit(SimulationCommand.PresentNow());

            Debug.Log(
                $"[ParticleSandboxSeeder] 沙箱就绪：{width}x{height}，"
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

        /// <summary>铺地板 + 砌两侧整列挡墙，返回石头总数（每段用一个填矩形命令表达，不做逐格命令）。</summary>
        private int BuildContainer(ParticleSimulationDriver driver, int width, int height)
        {
            int floor = Mathf.Clamp(_floorThickness, 0, height);
            int wall = Mathf.Clamp(_wallThickness, 0, width / 2);
            int count = 0;

            if (floor > 0)
            {
                driver.Submit(SimulationCommand.FillRect(0, 0, width, floor, ParticleIds.Stone));
                count += width * floor;
            }

            if (wall > 0 && height > floor)
            {
                int wallHeight = height - floor;
                driver.Submit(SimulationCommand.FillRect(0, floor, wall, wallHeight, ParticleIds.Stone));
                driver.Submit(SimulationCommand.FillRect(width - wall, floor, wall, wallHeight, ParticleIds.Stone));
                count += 2 * wall * wallHeight;
            }

            return count;
        }

        /// <summary>在内部区域中央投放一块沙，返回沙粒数。它会自行塌成安息角堆。</summary>
        private int FillSand(ParticleSimulationDriver driver, int width, int height)
        {
            int floor = Mathf.Clamp(_floorThickness, 0, height);
            int wall = Mathf.Clamp(_wallThickness, 0, width / 2);

            int interiorWidth = width - wall * 2;
            int interiorHeight = height - floor;
            if (interiorWidth <= 0 || interiorHeight <= 0)
            {
                return 0;
            }

            int sandWidth = Mathf.Clamp(Mathf.RoundToInt(interiorWidth * _sandWidthRatio), 1, interiorWidth);
            int sandHeight = Mathf.Clamp(Mathf.RoundToInt(interiorHeight * _sandHeightRatio), 1, interiorHeight);
            int startX = wall + (interiorWidth - sandWidth) / 2;

            driver.Submit(SimulationCommand.FillRect(startX, floor, sandWidth, sandHeight, ParticleIds.Sand));
            return sandWidth * sandHeight;
        }
    }
}
