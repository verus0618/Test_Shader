using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TestMisha.Fx.Pulse
{
    /// <summary>Shapes the spawner can create.</summary>
    public enum BlockShape
    {
        Cube,
        Sphere,
        Capsule,
        Cylinder,
        Torus,
        CustomPrefab,
    }

    /// <summary>What the click ray is tested against, besides the blocks that were already spawned.</summary>
    public enum SurfaceMode
    {
        /// <summary>An invisible horizontal plane at Ground Height. Needs no colliders in the scene.</summary>
        GroundPlane,

        /// <summary>Physics colliders on the selected layers.</summary>
        Colliders,
    }

    /// <summary>One entry of the spawner's model list.</summary>
    [System.Serializable]
    public sealed class BlockModel
    {
        [Tooltip("Name given to the spawned object. Falls back to the shape name.")]
        public string name = "Cube";

        [Tooltip("The Unity primitives come with the default material. To get the Pulse material, use Custom Prefab.")]
        public BlockShape shape;

        [Tooltip("Used only when Shape is Custom Prefab. The block keeps the materials of this prefab, so put the Pulse material on it.")]
        public GameObject prefab;

        [Tooltip("Size multiplier. Primitives are 1 m wide; capsule and cylinder are 2 m tall.")]
        public Vector3 scale = Vector3.one;

        /// <summary>Name for the spawned object.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(name) ? shape.ToString() : name;
    }

    /// <summary>
    /// Click-to-spawn test tool for the Pulse shader. A left click in the Game view drops a block on the clicked
    /// surface, or on top of the spawned block that was clicked, so blocks stack. A block keeps the materials of its prefab.
    /// Every spawn is a new impact. The spawner sets the global shader impact point to the new block and the global
    /// time from 0 to 1, under the property names given in the Pulse settings. Every object that uses the Pulse shader
    /// reacts, whatever its material and whether or not it was spawned here, and the shader alone decides, per vertex,
    /// what the Radius of its material reaches.
    /// Keys 1-9 pick the model. Alt+click is left to the Fly Camera. The click ray uses the Main Camera.
    /// Uses the Input System package, which the project is set up to use exclusively.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("TestMisha/Pulse Block Spawner")]
    public sealed class PulseBlockSpawner : MonoBehaviour
    {
        private const int MaxHotkeyModels = 9;
        private const float MaxRayDistance = 1000f;
        private const float FootprintTolerance = 0.001f;
        private const float TorusMajorRadius = 0.35f;
        private const float TorusMinorRadius = 0.15f;
        private const int TorusMajorSegments = 32;
        private const int TorusMinorSegments = 16;
        private const string SpawnRootName = "Spawned Blocks";

        [Header("Models")]
        [Tooltip("What can be spawned. Keys 1-9 pick the model in Play Mode. Add Unity primitives, or your own prefabs with Shape = Custom Prefab.")]
        [SerializeField] private List<BlockModel> models = new List<BlockModel>
        {
            new BlockModel { name = "Cube", shape = BlockShape.Cube },
            new BlockModel { name = "Sphere", shape = BlockShape.Sphere },
            new BlockModel { name = "Capsule", shape = BlockShape.Capsule },
            new BlockModel { name = "Cylinder", shape = BlockShape.Cylinder },
            new BlockModel { name = "Torus", shape = BlockShape.Torus },
        };

        [Header("Placement")]
        [Tooltip("Ground Plane needs no colliders. Colliders hits the physics colliders on Surface Layers.")]
        [SerializeField] private SurfaceMode surface = SurfaceMode.GroundPlane;

        [Tooltip("World height of the invisible ground plane. Used by Ground Plane mode.")]
        [SerializeField] private float groundHeight;

        [Tooltip("Layers the click ray can hit. Used by Colliders mode.")]
        [SerializeField] private LayerMask surfaceLayers = ~0;

        [Tooltip("Blocks snap to multiples of this size on X and Z. 0 turns snapping off.")]
        [Min(0f)]
        [SerializeField] private float gridSize = 1f;

        [Header("Pulse")]
        [Tooltip("Timing and origin of the pulse. The impact point and time are global shader values, so every object with the shader reacts. Which vertices move is decided by Radius and Hardness in each material.")]
        [SerializeField] private PulseSettings pulse = new PulseSettings();

        private readonly List<SpawnedBlock> _blocks = new List<SpawnedBlock>();
        private readonly PulsePlayer _pulsePlayer = new PulsePlayer();
        private int _selectedIndex;
        private Transform _spawnRoot;
        private Mesh _torusMesh;

        /// <summary>Picks the model spawned on click. The index is clamped to the model list.</summary>
        public void SelectModel(int index)
        {
            _selectedIndex = Mathf.Clamp(index, 0, Mathf.Max(models.Count - 1, 0));
        }

        /// <summary>
        /// Drops a block at the X and Z of the point. It rests at the height of the point, or on top of the spawned
        /// blocks under its footprint when they are higher. The new block becomes the impact point of the pulse.
        /// </summary>
        /// <returns>The new block, or null when nothing could be spawned.</returns>
        public GameObject SpawnBlock(Vector3 spawnPoint)
        {
            BlockModel model = PickModel();
            GameObject visual = model != null ? CreateBlock(model) : null;
            if (visual == null)
                return null;

            // The size goes on a wrapper, so a prefab that animates its own scale, like a spawn-in pop, cannot overwrite it.
            Vector3 target = SnapToGrid(spawnPoint);
            var block = new GameObject(model.DisplayName);
            block.transform.SetParent(GetSpawnRoot(), false);
            block.transform.localScale = model.scale;
            block.transform.position = target;
            visual.transform.SetParent(block.transform, false);

            MeshRenderer[] renderers = block.GetComponentsInChildren<MeshRenderer>();
            if (!PulsePlayer.TryCalculateBounds(renderers, out Bounds bounds))
            {
                Debug.LogWarning($"PulseBlockSpawner: '{model.DisplayName}' has no MeshRenderer, nothing to show.", this);
                Destroy(block);
                return null;
            }

            MakeVisualOnly(block);

            PruneDestroyedBlocks();
            bounds = PlaceOnSupport(block, bounds, target, spawnPoint.y);

            _pulsePlayer.Play(pulse, PulsePlayer.GetImpactCenter(bounds, pulse.impactCenter));
            _blocks.Add(new SpawnedBlock { Root = block, WorldBounds = bounds });
            return block;
        }

        /// <summary>Destroys every block spawned so far.</summary>
        [ContextMenu("Clear Spawned Blocks")]
        public void ClearBlocks()
        {
            _blocks.Clear();
            if (_spawnRoot == null)
                return;

            Destroy(_spawnRoot.gameObject);
            _spawnRoot = null;
        }

        private void Update()
        {
            _pulsePlayer.Tick(Time.deltaTime, false);

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
                HandleModelHotkeys(keyboard);

            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            // Alt + LMB orbits the Fly Camera, so it must not spawn anything.
            if (keyboard != null && keyboard.altKey.isPressed)
                return;

            if (TryGetSpawnPoint(mouse.position.ReadValue(), out Vector3 spawnPoint))
                SpawnBlock(spawnPoint);
        }

        private void OnDestroy()
        {
            // The blocks share the generated mesh, so they go away together with it.
            ClearBlocks();
            if (_torusMesh != null)
                Destroy(_torusMesh);
        }

        private void HandleModelHotkeys(Keyboard keyboard)
        {
            int hotkeyCount = Mathf.Min(models.Count, MaxHotkeyModels);
            for (int i = 0; i < hotkeyCount; i++)
            {
                if (keyboard[Key.Digit1 + i].wasPressedThisFrame)
                    SelectModel(i);
            }
        }

        // The point a new block should be dropped at: on the ground, or on top of the spawned block under the cursor.
        private bool TryGetSpawnPoint(Vector2 screenPosition, out Vector3 spawnPoint)
        {
            spawnPoint = default;

            Camera rayCamera = Camera.main;
            if (rayCamera == null)
            {
                Debug.LogWarning("PulseBlockSpawner: no camera found. Tag a camera as MainCamera.", this);
                return false;
            }

            Ray ray = rayCamera.ScreenPointToRay(screenPosition);
            bool hitSurface = TryRaycastSurface(ray, out Vector3 surfacePoint, out float surfaceDistance);
            SpawnedBlock hitBlock = RaycastBlocks(ray, out float blockDistance);

            if (hitBlock != null && (!hitSurface || blockDistance < surfaceDistance))
            {
                spawnPoint = GetStackPoint(hitBlock.WorldBounds, ray.GetPoint(blockDistance));
                return true;
            }

            spawnPoint = surfacePoint;
            return hitSurface;
        }

        private bool TryRaycastSurface(Ray ray, out Vector3 point, out float distance)
        {
            point = default;
            distance = 0f;

            if (surface == SurfaceMode.Colliders)
            {
                bool hitCollider = Physics.Raycast(ray, out RaycastHit hit, MaxRayDistance, surfaceLayers, QueryTriggerInteraction.Ignore);
                point = hit.point;
                distance = hit.distance;
                return hitCollider;
            }

            var ground = new Plane(Vector3.up, new Vector3(0f, groundHeight, 0f));
            if (!ground.Raycast(ray, out distance))
                return false;

            point = ray.GetPoint(distance);
            return true;
        }

        // Spawned blocks have no colliders, so the ray is tested against their bounds instead.
        private SpawnedBlock RaycastBlocks(Ray ray, out float nearestDistance)
        {
            PruneDestroyedBlocks();

            SpawnedBlock nearest = null;
            nearestDistance = float.MaxValue;
            foreach (SpawnedBlock block in _blocks)
            {
                if (block.WorldBounds.IntersectRay(ray, out float distance) && distance < nearestDistance)
                {
                    nearest = block;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        // A click on the top face stacks where the cursor is. A click on a side stacks over the middle of that block.
        private static Vector3 GetStackPoint(Bounds blockBounds, Vector3 hitPoint)
        {
            float topGap = blockBounds.max.y - hitPoint.y;
            float sideGap = Mathf.Min(
                blockBounds.extents.x - Mathf.Abs(hitPoint.x - blockBounds.center.x),
                blockBounds.extents.z - Mathf.Abs(hitPoint.z - blockBounds.center.z));

            Vector3 anchor = topGap <= sideGap ? hitPoint : blockBounds.center;
            return new Vector3(anchor.x, blockBounds.max.y, anchor.z);
        }

        // Centers the block's footprint on the target, then lifts it onto the highest support under that footprint:
        // the surface itself or any spawned block, so repeated clicks build a tower.
        private Bounds PlaceOnSupport(GameObject block, Bounds bounds, Vector3 target, float surfaceHeight)
        {
            var centerShift = new Vector3(target.x - bounds.center.x, 0f, target.z - bounds.center.z);
            bounds.center += centerShift;

            float supportHeight = surfaceHeight;
            foreach (SpawnedBlock other in _blocks)
            {
                if (OverlapInPlan(other.WorldBounds, bounds))
                    supportHeight = Mathf.Max(supportHeight, other.WorldBounds.max.y);
            }

            Vector3 lift = Vector3.up * (supportHeight - bounds.min.y);
            block.transform.position += centerShift + lift;
            bounds.center += lift;
            return bounds;
        }

        private static bool OverlapInPlan(Bounds a, Bounds b)
        {
            return a.min.x < b.max.x - FootprintTolerance && a.max.x > b.min.x + FootprintTolerance
                && a.min.z < b.max.z - FootprintTolerance && a.max.z > b.min.z + FootprintTolerance;
        }

        private void PruneDestroyedBlocks()
        {
            _blocks.RemoveAll(block => block.Root == null);
        }

        private BlockModel PickModel()
        {
            if (models.Count == 0)
            {
                Debug.LogWarning("PulseBlockSpawner: the model list is empty.", this);
                return null;
            }

            return models[Mathf.Clamp(_selectedIndex, 0, models.Count - 1)];
        }

        private GameObject CreateBlock(BlockModel model)
        {
            switch (model.shape)
            {
                case BlockShape.Sphere: return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case BlockShape.Capsule: return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case BlockShape.Cylinder: return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case BlockShape.Torus: return CreateTorus();
                case BlockShape.CustomPrefab: return InstantiateCustomPrefab(model);
                default: return GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
        }

        private GameObject CreateTorus()
        {
            if (_torusMesh == null)
                _torusMesh = TorusMeshBuilder.Build(TorusMajorRadius, TorusMinorRadius, TorusMajorSegments, TorusMinorSegments);

            var torus = new GameObject("Torus", typeof(MeshFilter), typeof(MeshRenderer));
            torus.GetComponent<MeshFilter>().sharedMesh = _torusMesh;
            return torus;
        }

        private GameObject InstantiateCustomPrefab(BlockModel model)
        {
            if (model.prefab != null)
                return Instantiate(model.prefab);

            Debug.LogWarning($"PulseBlockSpawner: '{model.DisplayName}' is a Custom Prefab but no prefab is assigned.", this);
            return null;
        }

        private Transform GetSpawnRoot()
        {
            // A scene root object without any transform, so a scaled or moved spawner cannot distort the blocks.
            if (_spawnRoot == null)
                _spawnRoot = new GameObject(SpawnRootName).transform;

            return _spawnRoot;
        }

        private Vector3 SnapToGrid(Vector3 point)
        {
            if (gridSize <= 0f)
                return point;

            return new Vector3(
                Mathf.Round(point.x / gridSize) * gridSize,
                point.y,
                Mathf.Round(point.z / gridSize) * gridSize);
        }

        // Test blocks are visual only. Their bounds are used for picking and stacking, and colliders would block the Colliders mode ray.
        private static void MakeVisualOnly(GameObject block)
        {
            foreach (Collider blockCollider in block.GetComponentsInChildren<Collider>())
                Destroy(blockCollider);
        }

        /// <summary>A block placed by the spawner, with the bounds it had when it landed.</summary>
        private sealed class SpawnedBlock
        {
            public GameObject Root;
            public Bounds WorldBounds;
        }
    }
}
