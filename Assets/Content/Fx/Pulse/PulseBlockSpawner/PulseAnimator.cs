using UnityEngine;

namespace TestMisha.Fx.Pulse
{
    /// <summary>
    /// Plays a pulse from this object: its position becomes the global impact point and the global time runs from 0 to 1.
    /// The shader property names are set in the Global shader parameters of the settings.
    /// Every object that uses the Pulse shader reacts, whatever its material, and the shader's Radius decides which
    /// vertices move. The block spawner drives the same two global values, so use one of them at a time.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("TestMisha/Pulse Animator")]
    public sealed class PulseAnimator : MonoBehaviour
    {
        [SerializeField] private PulseSettings settings = new PulseSettings();

        [Tooltip("Start playing as soon as the scene starts.")]
        [SerializeField] private bool playOnStart = true;

        [Tooltip("Start over from 0 every time the animation reaches 1.")]
        [SerializeField] private bool loop;

        private readonly PulsePlayer _player = new PulsePlayer();
        private bool _hasPlayed;

        /// <summary>Restarts the pulse from 0 with the current settings.</summary>
        public void Play()
        {
            _hasPlayed = true;
            _player.Play(settings, GetImpactPoint());
            enabled = true;
        }

        private void Start()
        {
            // Skip when code already started the pulse.
            if (_hasPlayed)
                return;

            if (playOnStart)
                Play();
            else
                enabled = false;
        }

        private void Update()
        {
            _player.Tick(Time.deltaTime, loop);

            // Stop updating once the pulse is over. The global value stays at rest.
            if (!_player.IsPlaying)
                enabled = false;
        }

        // An object without meshes, such as an empty locator, pulses from its own position.
        private Vector3 GetImpactPoint()
        {
            return PulsePlayer.TryCalculateBounds(GetComponentsInChildren<MeshRenderer>(), out Bounds bounds)
                ? PulsePlayer.GetImpactCenter(bounds, settings.impactCenter)
                : transform.position;
        }
    }
}
