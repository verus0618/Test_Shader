using UnityEngine;

/// <summary>
/// Authoring data for the wall pass-through mask.
/// Create via Assets > Create > VFX > Wall Pass Settings.
/// </summary>
[CreateAssetMenu(fileName = "SO_WallPassSettings", menuName = "VFX/Wall Pass Settings")]
public class WallPassSettings : ScriptableObject
{
    [Header("Imprint Shape")]
    [Tooltip("Multiplier applied to probe radii. 1 matches the authored probe sizes.")]
    [Min(0.01f)] public float radiusScale = 1f;

    [Tooltip("Gradient length outward from the silhouette, in meters. Keep it below the gap " +
             "between limbs and torso, otherwise separate body parts merge into one blob.")]
    [Min(0.001f)] public float falloff = 0.14f;

    [Tooltip("How much distance along the surface normal counts. 1 is true 3D distance: only the " +
             "slice of the body currently at the wall plane shows up. Lower values project the " +
             "silhouette through the wall; 0.15-0.3 reads as a full body silhouette. " +
             "Avoid 0, which makes the imprint an infinite tube.")]
    [Range(0f, 1f)] public float depthWeight = 0.2f;

    [Header("Timing")]
    [Tooltip("Delay between the contact event and the start of the mask, in seconds.")]
    [Min(0f)] public float delay = 0.05f;

    [Tooltip("Time for the mask to reach full strength, in seconds.")]
    [Min(0.001f)] public float attack = 0.06f;

    [Tooltip("Total lifetime of a single imprint, in seconds.")]
    [Min(0.01f)] public float lifetime = 0.8f;

    [Header("Output")]
    [Tooltip("Global multiplier applied before the final clamp. Above 1 the silhouette core widens " +
             "rather than brightening, since the mask is clamped to 0..1.")]
    public float intensity = 1f;

    [Header("Event Buffer")]
    [Tooltip("Maximum concurrent imprints. Hard ceiling is 64 (WP_MAX_EVENTS in WallPassMask.hlsl). " +
             "Each probe emits one event per step, so budget roughly probeCount x poses to retain.")]
    [Range(1, 64)] public int maxEvents = 48;

    [Tooltip("Minimum probe displacement required to record a new event, in meters.")]
    [Min(0f)] public float minStep = 0.02f;

    [Tooltip("Maximum gap between events while a probe is stationary, in seconds. " +
             "Keeps the mask alive when an object stops inside the wall.")]
    [Min(0.01f)] public float maxInterval = 0.05f;

    /// <summary>Delay plus lifetime: how long an event stays in the buffer.</summary>
    public float TotalLife => delay + lifetime;
}
