using UnityEditor;
using UnityEngine;

namespace RippleSystem.Editor
{
    /// <summary>
    /// Editor-time counterpart of RippleGlobalFallback. Shader Graph's own
    /// preview windows (Master Preview, node previews) render outside Play
    /// Mode too, and any wall using RippleSurfaceController also relies on
    /// this while not playing, since RippleSurfaceController is a plain
    /// MonoBehaviour (no [ExecuteAlways]) and never runs outside Play.
    /// Without a valid global buffer, the shader logs "buffer required but
    /// none provided" continuously in the Scene/Game view.
    /// </summary>
    [InitializeOnLoad]
    public static class RippleGlobalFallbackEditor
    {
        private static ComputeBuffer _dummyBuffer;

        static RippleGlobalFallbackEditor()
        {
            Register();
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;

            // RippleGlobalFallback (the runtime counterpart) releases its own
            // dummy buffer on Application.quitting, which also fires on a
            // plain Stop Play in the Editor — that can leave the global
            // "_RippleHBuffer" property pointing at an already-disposed
            // buffer if no domain reload happens in between (e.g. with
            // "Enter Play Mode Options" domain reload disabled). Force our
            // own valid buffer back in immediately after returning to Edit
            // Mode, regardless of what state the runtime side left behind.
            Release();
            Register();
        }

        [MenuItem("Tools/Ripple System/Force Rebind Global Fallback Buffer")]
        private static void ForceRebind()
        {
            Release();
            Register();
            Debug.Log("[RippleSystem] Global fallback buffer for \"_RippleHBuffer\" re-registered manually.");
        }

        private static void Register()
        {
            if (_dummyBuffer != null) return;

            _dummyBuffer = new ComputeBuffer(1, sizeof(float));
            _dummyBuffer.SetData(new float[] { 0f });
            Shader.SetGlobalBuffer("_RippleHBuffer", _dummyBuffer);
            Shader.SetGlobalInt("_RippleHBufferLength", 1);

            Debug.Log("[RippleSystem] Global fallback buffer for \"_RippleHBuffer\" registered.");
        }

        private static void Release()
        {
            _dummyBuffer?.Release();
            _dummyBuffer = null;
        }
    }
}
