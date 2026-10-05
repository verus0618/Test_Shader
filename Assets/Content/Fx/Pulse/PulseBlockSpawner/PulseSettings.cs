using UnityEngine;

namespace TestMisha.Fx.Pulse
{
    /// <summary>Where on an object the impact of a pulse is placed.</summary>
    public enum ImpactCenterMode
    {
        /// <summary>Center of the combined renderer bounds.</summary>
        BoundsCenter,

        /// <summary>Middle of the bottom of the combined renderer bounds, like a hit on the ground.</summary>
        BoundsBottomCenter,
    }

    /// <summary>Timing, origin and shader references of one pulse. Shared by <see cref="PulseAnimator"/> and the block spawner.</summary>
    [System.Serializable]
    public sealed class PulseSettings
    {
        /// <summary>Shortest allowed animation, so the progress never divides by zero.</summary>
        public const float MinDuration = 0.01f;

        [Tooltip("Seconds it takes the time property to go from 0 to 1. The damped wave in the shader plays for exactly this long.")]
        [Min(MinDuration)]
        public float duration = 1f;

        [Tooltip("Shape of the 0 to 1 animation over time. A straight line plays the shader at its normal speed.")]
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Tooltip("Where on the object the impact is placed.")]
        public ImpactCenterMode impactCenter = ImpactCenterMode.BoundsCenter;

        [Header("Global shader parameters")]
        [Tooltip("Reference of the global Vector3 property that holds the impact point, exactly as it is written in the Graph Inspector under Reference. Retype it here after renaming the property in the graph.")]
        public string impactCenterReference = "_ImpactCente";

        [Tooltip("Reference of the global float property that holds the time since the impact, exactly as it is written in the Graph Inspector under Reference. The pulse runs it from 0 to 1. Retype it here after renaming the property in the graph.")]
        public string timeReference = "_PulseTime";
    }
}
