using UnityEngine;

namespace TestMisha.Slime
{
    /// <summary>
    /// Lets refraction made with the Shader Graph Scene Color node show transparent objects.
    /// URP copies the screen for Scene Color (_CameraOpaqueTexture) once, before the transparent pass, so a
    /// refractive object never sees a transparent object behind it, not even another refractive one.
    /// Put this component on each refractive object (Role: Refractive). Every Refractive object then gets its
    /// own copy of the screen, taken just before it (the Built-in pipeline's GrabPass): the opaque scene plus
    /// every transparent object behind it, other Refractive objects included. The copy is bound through a
    /// MaterialPropertyBlock under the name _CameraOpaqueTexture, so Scene Color picks it up without graph
    /// changes.
    /// All other transparent renderers are found automatically, no component needed: Mesh, Skinned Mesh,
    /// Particle System, Trail, Line and Sprite renderers whose materials use the transparent queue. VFX Graph
    /// is not supported. The other roles override the automatic choice:
    /// - Visible In Refraction: always drawn into the copies, whatever the renderer type or render queue.
    /// - Hidden From Refraction: never drawn into the copies.
    /// Objects are placed by distance to their bounds centre, like URP sorts transparents. An object whose centre
    /// lies inside a Refractive object (a flame inside the slime) counts as behind it.
    /// The URP transparent pass still draws every object as before; the chain only fills the copies (Frame
    /// Debugger: "Slime Refraction" passes before "Draw Transparent Objects").
    /// Requirements: Refractive materials write depth (ZWrite On), so a transparent object behind one that is
    /// drawn after it in the transparent pass stays hidden instead of showing a second, unrefracted time.
    /// Limitations: objects are drawn into the copies with CommandBuffer.DrawRenderer, which sets up no
    /// per-object light probe data, so the ambient light of a lit object seen through the refraction can differ
    /// a little (lights, shadows and reflection probes are global in Forward+ and match). A Refractive object
    /// owns the renderer's MaterialPropertyBlock: disabling the component or switching the role clears the block.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public sealed class SlimeRefractionObject : MonoBehaviour
    {
        public enum Role
        {
            Refractive,
            VisibleInRefraction,
            HiddenFromRefraction
        }

        public enum GrabResolution
        {
            MatchPipeline,
            Full,
            Half,
            Quarter
        }

        [Tooltip("Refractive: the material reads Scene Color; the object gets its own screen copy with the transparent objects behind it. Visible In Refraction: always drawn into the copies, even where the automatic search skips it. Hidden From Refraction: never drawn into the copies. Transparent objects without this component are found automatically.")]
        [InspectorName("Role | Affects Performance: 3/10")]
        public Role role = Role.Refractive;

        [Tooltip("Resolution of this object's screen copy (Refractive only). Match Pipeline follows Opaque Downsampling of the URP asset, so the refraction looks the same as with the plain Scene Color node.")]
        [InspectorName("Grab Resolution | Affects Performance: 3/10")]
        public GrabResolution grabResolution = GrabResolution.MatchPipeline;

        [Header("Auto-Search (Refractive only)")]
        [Tooltip("GameObjects with this tag are skipped by the automatic search, whatever their material. Leave as Untagged (default) to search everywhere. When several Refractive objects share a camera, every one of their exclude tags applies.")]
        [InspectorName("Exclude Tag | Affects Performance: 2/10")]
        [TagSelector]
        public string excludeTag = "Untagged";

        [Range(1, 8)]
        [Tooltip("Frames between rescans for transparent objects without this component. 1 searches every frame (default, always current). Higher is cheaper; a spawned or moved object can lag entering the refraction by up to this many frames. When several Refractive objects share a camera, the smallest value wins.")]
        [InspectorName("Search Interval | Affects Performance: 7/10")]
        public int searchInterval = 1;

        [Range(0f, 0.2f)]
        [Tooltip("Auto-found objects smaller than this fraction of the screen are skipped. 0 disables the culling (default). When several Refractive objects share a camera, the smallest value wins.")]
        [InspectorName("Min Screen Size | Affects Performance: 4/10")]
        public float minScreenSize;

        internal Renderer TargetRenderer { get; private set; }
        internal SlimeRefractionSystem.ChainRenderer Chain { get; private set; }

        // The renderer's property block points at a screen copy of this object.
        internal bool HasBoundGrab { get; set; }

        // The opaque-queue warning is logged only once.
        internal bool WarnedAboutQueue { get; set; }

        // Name for the Frame Debugger and the Render Graph Viewer, built once instead of every frame.
        internal string GrabPassName { get; private set; }

        void OnEnable()
        {
            TargetRenderer = GetComponent<Renderer>();
            Chain = new SlimeRefractionSystem.ChainRenderer(TargetRenderer);
            GrabPassName = "Slime Refraction Grab: " + name;
            SlimeRefractionSystem.Register(this);
        }

        void OnDisable()
        {
            SlimeRefractionSystem.Unregister(this);
        }
    }
}
