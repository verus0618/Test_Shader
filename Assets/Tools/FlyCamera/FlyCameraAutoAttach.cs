#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TestMisha.Tools
{
    /// <summary>
    /// Adds a <see cref="FlyCamera"/> to the main camera when Play Mode starts, so any scene
    /// can be flown through without being edited. The component only lives for the Play Mode
    /// session. Toggles are under Tools > TestMisha > Fly Camera. Editor-only.
    /// </summary>
    internal static class FlyCameraAutoAttach
    {
        private const string MenuRoot = "Tools/TestMisha/Fly Camera/";
        private const string AutoAttachMenu = MenuRoot + "Auto-Attach In Play Mode";
        private const string StartFromSceneViewMenu = MenuRoot + "Start From Scene View Camera";

        private static string AutoAttachKey =>
            "TestMisha.FlyCamera.AutoAttach." + Application.dataPath;
        private static string StartFromSceneViewKey =>
            "TestMisha.FlyCamera.StartFromSceneView." + Application.dataPath;

        private static bool AutoAttach => EditorPrefs.GetBool(AutoAttachKey, true);
        private static bool StartFromSceneView => EditorPrefs.GetBool(StartFromSceneViewKey, false);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnPlayModeStarted()
        {
            // Unsubscribe first: without a domain reload the static handler would pile up.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Attach(StartFromSceneView);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Attach(false);

        private static void Attach(bool fromSceneView)
        {
            // A FlyCamera added by hand wins, including its inspector settings.
            FlyCamera fly = Object.FindAnyObjectByType<FlyCamera>();
            if (fly == null)
            {
                Camera camera = Camera.main;
                if (!AutoAttach || camera == null)
                    return;

                fly = camera.gameObject.AddComponent<FlyCamera>();
            }

            if (fromSceneView)
                CopySceneViewPose(fly);
        }

        private static void CopySceneViewPose(FlyCamera fly)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null)
                return;

            Transform source = view.camera.transform;
            fly.SetPose(source.position, source.rotation, view.cameraDistance);
        }

        [MenuItem(AutoAttachMenu, false, 2100)]
        private static void ToggleAutoAttach() => EditorPrefs.SetBool(AutoAttachKey, !AutoAttach);

        [MenuItem(AutoAttachMenu, true)]
        private static bool ToggleAutoAttachValidate()
        {
            Menu.SetChecked(AutoAttachMenu, AutoAttach);
            return true;
        }

        [MenuItem(StartFromSceneViewMenu, false, 2101)]
        private static void ToggleStartFromSceneView() =>
            EditorPrefs.SetBool(StartFromSceneViewKey, !StartFromSceneView);

        [MenuItem(StartFromSceneViewMenu, true)]
        private static bool ToggleStartFromSceneViewValidate()
        {
            Menu.SetChecked(StartFromSceneViewMenu, StartFromSceneView);
            return true;
        }
    }
}
#endif
