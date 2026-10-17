using NUnit.Framework;
using TapTap2026.Rendering;
using TapTap2026.Simulation;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Tests
{
    /// <summary>
    /// 相机平移（<see cref="ParticleRenderer.PanCamera"/>）的测试：按给定格数平移、视口夹在沙箱左右边界内
    /// （视口比沙箱宽时恒居中）、只动 x 不动竖直框取与相机距离、无输入调用时的自愈夹取，
    /// 以及非正交相机不参与平移。
    /// </summary>
    /// <remarks>
    /// 相机与渲染器都在测试里现建现销，并把相机直接塞进渲染器的 <c>_camera</c> 字段（<see cref="SerializedObject"/>；
    /// 它优先于 <c>Camera.main</c>）：测试因此不依赖当前打开的场景，也不会去动场景里的 Main Camera。
    /// </remarks>
    public sealed class CameraPanTests
    {
        private const int WorldWidth = 200;
        private const int WorldHeight = 100;

        /// <summary>相机距离。只做平移的参照物：它不该被平移改动。</summary>
        private const float CameraZ = -10f;

        /// <summary>正交尺寸取沙箱高的一半——与渲染器的取景约定一致（竖直方向刚好框住整张网格）。</summary>
        private const float OrthographicSize = WorldHeight * 0.5f;

        /// <summary>aspect = 1 时的半视宽：正交尺寸 × aspect = 50，即视口宽 100 格。相机中心的可动范围 = [50, 150]。</summary>
        private const float HalfViewAtAspectOne = 50f;

        private GameObject _cameraObject;
        private GameObject _rendererObject;
        private Camera _camera;
        private ParticleRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _cameraObject = new GameObject("CameraPanTests.Camera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.orthographicSize = OrthographicSize;
            _camera.aspect = 1f;
            _cameraObject.transform.position = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, CameraZ);

            _rendererObject = new GameObject("CameraPanTests.Renderer");
            _renderer = _rendererObject.AddComponent<ParticleRenderer>();

            var serializedRenderer = new SerializedObject(_renderer);
            serializedRenderer.FindProperty("_camera").objectReferenceValue = _camera;
            serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_rendererObject);
            Object.DestroyImmediate(_cameraObject);
        }

        /// <summary>平移按给定格数移动相机（渲染器只认格数，按键 → 格数由编辑器决定），且只动 x。</summary>
        [Test]
        public void CameraPan_MovesByTheGivenCellsInBothDirections()
        {
            _renderer.PanCamera(WorldWidth, 25f);
            Vector3 afterRight = _camera.transform.position;
            Assert.AreEqual(WorldWidth * 0.5f + 25f, afterRight.x, 1e-4f, "向右平移 25 格");
            Assert.AreEqual(WorldHeight * 0.5f, afterRight.y, 1e-4f, "平移不该改竖直框取");
            Assert.AreEqual(CameraZ, afterRight.z, 1e-4f, "平移不该改相机距离");

            _renderer.PanCamera(WorldWidth, -10f);
            Assert.AreEqual(WorldWidth * 0.5f + 25f - 10f, _camera.transform.position.x, 1e-4f, "向左平移 10 格");
        }

        /// <summary>平移不会把视口推出沙箱：两端正好夹在 [半视宽, Width − 半视宽]，越界的部分被丢掉而不是欠着。</summary>
        [Test]
        public void CameraPan_ClampsToTheGridEdges()
        {
            _renderer.PanCamera(WorldWidth, 1000f);
            Assert.AreEqual(WorldWidth - HalfViewAtAspectOne, _camera.transform.position.x, 1e-4f, "右端夹在 Width − 半视宽");

            _renderer.PanCamera(WorldWidth, -1000f);
            Assert.AreEqual(HalfViewAtAspectOne, _camera.transform.position.x, 1e-4f, "左端夹在半视宽");
        }

        /// <summary>视口比沙箱还宽时左右都无处可去：相机恒在沙箱中心，平移无效（而不是把视口推出网格）。</summary>
        [Test]
        public void CameraPan_KeepsTheCameraCenteredWhenTheViewIsWiderThanTheSandbox()
        {
            _camera.aspect = 4f; // 半视宽 200 = 沙箱宽，可动范围为空

            _renderer.PanCamera(WorldWidth, 500f);
            Assert.AreEqual(WorldWidth * 0.5f, _camera.transform.position.x, 1e-4f);

            _renderer.PanCamera(WorldWidth, -500f);
            Assert.AreEqual(WorldWidth * 0.5f, _camera.transform.position.x, 1e-4f);
        }

        /// <summary>无输入时也照常调（delta = 0）：宽高比变化把原本界内的相机挤到界外后，这一次调用就把它夹回沙箱内。</summary>
        [Test]
        public void CameraPan_ReclampsToTheGridWithoutInput()
        {
            _renderer.PanCamera(WorldWidth, 1000f); // 顶到右端：x = 150
            _camera.aspect = 1.5f;              // 半视宽 75 → 可动范围 [75, 125]，150 已在界外

            _renderer.PanCamera(WorldWidth, 0f);
            Assert.AreEqual(WorldWidth - 75f, _camera.transform.position.x, 1e-4f);
        }

        /// <summary>非正交相机不参与平移（透视相机的水平可见范围随深度变化，本项目取景约定是正交），位置原样保留。</summary>
        [Test]
        public void CameraPan_LeavesAPerspectiveCameraAlone()
        {
            _camera.orthographic = false;
            Vector3 before = _camera.transform.position;

            _renderer.PanCamera(WorldWidth, 500f);

            Assert.AreEqual(before, _camera.transform.position, "非正交相机不该被平移碰");
        }
    }
}
