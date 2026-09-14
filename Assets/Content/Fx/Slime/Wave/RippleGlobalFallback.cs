using UnityEngine;

namespace RippleSystem
{
    /// <summary>
    /// Registers a global fallback for the "_RippleHBuffer" StructuredBuffer
    /// at application startup. Without this, any material/preview that uses
    /// a shader referencing this buffer but isn't driven by a
    /// RippleSurfaceController (e.g. before a controller is assigned) logs
    /// "buffer required but none provided. Skipping draw calls" and simply
    /// doesn't render.
    ///
    /// Material.SetBuffer on a specific material takes priority over this
    /// global value, so multiple walls with their own RippleSurfaceController
    /// keep working independently — this is only a safety net for everything
    /// else.
    /// </summary>
    public static class RippleGlobalFallback
    {
        private static ComputeBuffer _dummyBuffer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            if (_dummyBuffer != null) return;

            _dummyBuffer = new ComputeBuffer(1, sizeof(float));
            _dummyBuffer.SetData(new float[] { 0f });
            Shader.SetGlobalBuffer("_RippleHBuffer", _dummyBuffer);
            Shader.SetGlobalInt("_RippleHBufferLength", 1);

            Application.quitting += Release;
        }

        private static void Release()
        {
            _dummyBuffer?.Release();
            _dummyBuffer = null;
        }
    }
}
