#if ENABLE_INPUT_SYSTEM && TESTMISHA_INPUT_SYSTEM
#define FLYCAM_NEW_INPUT
#endif

using UnityEngine;
#if FLYCAM_NEW_INPUT
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace TestMisha.Tools
{
    /// <summary>
    /// Scene View style free camera for Play Mode.
    /// Hold RMB: mouse look, WASD move, Q/E down/up, Shift boost, wheel changes fly speed.
    /// Wheel: dolly to the pivot. MMB drag: pan. Alt+LMB drag: orbit. Alt+RMB drag: zoom.
    /// In the Editor it is added to the main camera automatically (see <see cref="FlyCameraAutoAttach"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class FlyCamera : MonoBehaviour
    {
        private const float MinSpeed = 0.01f;
        private const float MaxSpeed = 1000f;
        private const float MinPivotDistance = 0.05f;
        private const float MinDollyStep = 0.5f;
        private const float DragZoomPerPixel = 0.005f;
        private const float SpeedHintSeconds = 1f;

        private enum Mode { None, Fly, Orbit, Zoom, Pan }

// Fields are written only by the active input backend; with none of them compiled in they stay default.
#pragma warning disable CS0649
        private struct FrameInput
        {
            public Vector2 mouseDelta;
            public float scroll;
            public bool lmb, rmb, mmb, alt, boost;
            public Vector3 move;
        }
#pragma warning restore CS0649

        [Header("Fly (hold RMB)")]
        [Tooltip("Base fly speed in m/s. Hold RMB and scroll the wheel to change it in Play Mode.")]
        [Min(MinSpeed)]
        public float moveSpeed = 5f;

        [Tooltip("Speed multiplier while Shift is held.")]
        [Min(1f)]
        public float boostMultiplier = 4f;

        [Tooltip("Speed multiplier per wheel notch when changing the fly speed.")]
        [Range(1.01f, 2f)]
        public float speedStep = 1.2f;

        [Tooltip("Ramp the speed up the longer movement keys are held, like the Scene View camera.")]
        public bool acceleration = true;

        [Tooltip("Speed multiplier reached after holding movement keys for Acceleration Time.")]
        [Min(1f)]
        public float maxAcceleration = 4f;

        [Tooltip("Seconds of continuous movement to reach Max Acceleration.")]
        [Min(0.01f)]
        public float accelerationTime = 3f;

        [Header("Look / Orbit")]
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        [Min(0.001f)]
        public float lookSensitivity = 0.15f;

        public bool invertY;

        [Header("Zoom / Pan")]
        [Tooltip("Fraction of the pivot distance travelled per wheel notch.")]
        [Range(0.01f, 0.5f)]
        public float wheelZoomStep = 0.15f;

        [Tooltip("Pivot distance used when no collider is hit in front of the camera.")]
        [Min(0.1f)]
        public float defaultPivotDistance = 10f;

        [Header("Misc")]
        [Tooltip("Keep working while Time.timeScale is 0 (paused or slow-motion effects).")]
        public bool useUnscaledTime = true;

        [Tooltip("Briefly show the fly speed in the Game view after changing it.")]
        public bool showSpeedHint = true;

        private Camera _camera;
        private Mode _mode;
        private float _yaw;
        private float _pitch;
        private float _pivotDistance;
        private float _moveHeldTime;
        private float _speedHintUntil = -1f;
        private int _skipDeltaFrames;
        private bool _cursorCaptured;
        private CursorLockMode _savedLockState;
        private bool _savedCursorVisible;

        private Vector3 Pivot => transform.position + transform.forward * _pivotDistance;

        /// <summary>Moves the camera and re-reads its orientation and orbit pivot.</summary>
        public void SetPose(Vector3 position, Quaternion rotation, float pivotDistance)
        {
            transform.SetPositionAndRotation(position, rotation);
            SyncFromTransform();
            _pivotDistance = Mathf.Max(pivotDistance, MinPivotDistance);
        }

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            SyncFromTransform();
#if !FLYCAM_NEW_INPUT && !ENABLE_LEGACY_INPUT_MANAGER
            Debug.LogWarning("FlyCamera: no input backend. Install com.unity.inputsystem " +
                             "or set Player Settings > Active Input Handling to Both.", this);
#endif
        }

        private void OnDisable()
        {
            _mode = Mode.None;
            SetCursorCaptured(false);
        }

        private void SyncFromTransform()
        {
            Vector3 euler = transform.eulerAngles;
            _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -89.9f, 89.9f);
            _yaw = euler.y;

            float maxDistance = _camera != null ? _camera.farClipPlane : 1000f;
            _pivotDistance = Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, maxDistance)
                ? Mathf.Max(hit.distance, MinPivotDistance)
                : defaultPivotDistance;
        }

        // LateUpdate so the fly camera wins over anything else that moved it this frame.
        private void LateUpdate()
        {
            if (!ReadInput(out FrameInput input))
                return;

            float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

            _mode = NextMode(_mode, input);
            SetCursorCaptured(_mode != Mode.None);

            // Locking the cursor warps it, which shows up as a large delta for a frame or two.
            if (_skipDeltaFrames > 0)
            {
                _skipDeltaFrames--;
                input.mouseDelta = Vector2.zero;
            }

            if (_mode != Mode.Fly)
                _moveHeldTime = 0f;

            switch (_mode)
            {
                case Mode.Fly:
                    Rotate(input.mouseDelta);
                    Fly(input, dt);
                    break;
                case Mode.Orbit:
                    Orbit(input.mouseDelta);
                    break;
                case Mode.Zoom:
                    Dolly(Mathf.Clamp((input.mouseDelta.x + input.mouseDelta.y) * DragZoomPerPixel, -0.9f, 0.9f));
                    break;
                case Mode.Pan:
                    Pan(input.mouseDelta);
                    break;
            }

            if (input.scroll != 0f)
            {
                if (_mode == Mode.Fly)
                {
                    moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(speedStep, input.scroll), MinSpeed, MaxSpeed);
                    _speedHintUntil = Time.unscaledTime + SpeedHintSeconds;
                }
                else
                {
                    Dolly(input.scroll * wheelZoomStep);
                }
            }
        }

        // A drag keeps its mode until its button is released, like the Scene View.
        private static Mode NextMode(Mode current, in FrameInput input)
        {
            switch (current)
            {
                case Mode.Fly:
                case Mode.Zoom:
                    if (input.rmb) return current;
                    break;
                case Mode.Orbit:
                    if (input.lmb) return current;
                    break;
                case Mode.Pan:
                    if (input.mmb) return current;
                    break;
            }

            if (input.rmb) return input.alt ? Mode.Zoom : Mode.Fly;
            if (input.lmb && input.alt) return Mode.Orbit;
            if (input.mmb) return Mode.Pan;
            return Mode.None;
        }

        private void Rotate(Vector2 delta)
        {
            _yaw += delta.x * lookSensitivity;
            _pitch += (invertY ? delta.y : -delta.y) * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch, -89.9f, 89.9f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void Fly(in FrameInput input, float dt)
        {
            if (input.move == Vector3.zero)
            {
                _moveHeldTime = 0f;
                return;
            }

            _moveHeldTime += dt;
            float speed = moveSpeed * (input.boost ? boostMultiplier : 1f);
            if (acceleration)
                speed *= Mathf.Lerp(1f, maxAcceleration, _moveHeldTime / accelerationTime);

            // Camera-local axes, Q/E included, same as the Scene View.
            transform.position += transform.rotation * input.move.normalized * (speed * dt);
        }

        private void Orbit(Vector2 delta)
        {
            Vector3 pivot = Pivot;
            Rotate(delta);
            transform.position = pivot - transform.forward * _pivotDistance;
        }

        private void Pan(Vector2 delta)
        {
            // World units per pixel at the pivot, so the pivot sticks to the cursor.
            float viewHeight = _camera.orthographic
                ? 2f * _camera.orthographicSize
                : 2f * _pivotDistance * Mathf.Tan(0.5f * _camera.fieldOfView * Mathf.Deg2Rad);
            float worldPerPixel = viewHeight / Mathf.Max(_camera.pixelHeight, 1);

            transform.position -= (transform.right * delta.x + transform.up * delta.y) * worldPerPixel;
        }

        // amount > 0 moves towards the pivot, as a fraction of the pivot distance.
        private void Dolly(float amount)
        {
            if (_camera.orthographic)
            {
                _camera.orthographicSize = Mathf.Max(_camera.orthographicSize * (1f - amount), 0.01f);
                return;
            }

            // Keep a minimum step so the camera still moves once it reaches the pivot;
            // the pivot is then pushed forward instead of the camera getting stuck.
            float step = Mathf.Max(_pivotDistance, MinDollyStep) * amount;
            transform.position += transform.forward * step;
            _pivotDistance = Mathf.Max(_pivotDistance - step, MinPivotDistance);
        }

        private void SetCursorCaptured(bool captured)
        {
            if (captured == _cursorCaptured)
                return;

            _cursorCaptured = captured;
            if (captured)
            {
                _savedLockState = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                _skipDeltaFrames = 2;
            }
            else
            {
                Cursor.lockState = _savedLockState;
                Cursor.visible = _savedCursorVisible;
            }
        }

        private void OnGUI()
        {
            if (!showSpeedHint || Time.unscaledTime > _speedHintUntil)
                return;

            GUI.Box(new Rect(10f, 10f, 170f, 24f), $"Fly speed: {moveSpeed:0.##} m/s");
        }

        // One wheel notch per frame at most; raw scroll units differ per platform and backend.
        private static float Notch(float value) => value > 0f ? 1f : value < 0f ? -1f : 0f;

#if FLYCAM_NEW_INPUT
        private static bool ReadInput(out FrameInput input)
        {
            input = default;
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return false;

            input.mouseDelta = mouse.delta.ReadValue();
            input.scroll = Notch(mouse.scroll.ReadValue().y);
            input.lmb = mouse.leftButton.isPressed;
            input.rmb = mouse.rightButton.isPressed;
            input.mmb = mouse.middleButton.isPressed;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                input.alt = keyboard.altKey.isPressed;
                input.boost = keyboard.shiftKey.isPressed;
                input.move = new Vector3(
                    Axis(keyboard.dKey, keyboard.aKey),
                    Axis(keyboard.eKey, keyboard.qKey),
                    Axis(keyboard.wKey, keyboard.sKey));
            }

            return true;
        }

        private static float Axis(KeyControl positive, KeyControl negative) =>
            (positive.isPressed ? 1f : 0f) - (negative.isPressed ? 1f : 0f);
#elif ENABLE_LEGACY_INPUT_MANAGER
        private static bool ReadInput(out FrameInput input)
        {
            input = default;

            // The default "Mouse X/Y" axes are pixel deltas scaled by a sensitivity of 0.1.
            input.mouseDelta = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
            input.scroll = Notch(Input.mouseScrollDelta.y);
            input.lmb = Input.GetMouseButton(0);
            input.rmb = Input.GetMouseButton(1);
            input.mmb = Input.GetMouseButton(2);
            input.alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            input.boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            input.move = new Vector3(
                Axis(KeyCode.D, KeyCode.A),
                Axis(KeyCode.E, KeyCode.Q),
                Axis(KeyCode.W, KeyCode.S));
            return true;
        }

        private static float Axis(KeyCode positive, KeyCode negative) =>
            (Input.GetKey(positive) ? 1f : 0f) - (Input.GetKey(negative) ? 1f : 0f);
#else
        private static bool ReadInput(out FrameInput input)
        {
            input = default;
            return false;
        }
#endif
    }
}
