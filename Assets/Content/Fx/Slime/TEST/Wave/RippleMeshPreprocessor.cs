using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RippleSystem.Editor
{
    /// <summary>
    /// Offline mesh preprocessor for the ripple system.
    ///
    /// Does two things:
    /// 1) Builds an adjacency graph of "welded" vertices — vertices whose
    ///    positions match within an epsilon are treated as one logical
    ///    surface point, even if the mesh stores them as separate vertices
    ///    (Unity splits vertices at UV seams and hard-normal edges).
    /// 2) Bakes each welded vertex's ID into the mesh's UV3 (TEXCOORD3)
    ///    channel, so the shader can index the simulation buffer by it.
    ///
    /// Output: a baked copy of the mesh (with UV3) and a RippleAdjacencyData
    /// asset next to it. The original imported mesh is never modified.
    /// </summary>
    public class RippleMeshPreprocessorWindow : EditorWindow
    {
        private Mesh sourceMesh;
        private float weldEpsilon = 0.0001f;
        private string outputFolder = "Assets/RippleSystem/Baked";

        [MenuItem("Tools/Ripple System/Bake Mesh For Ripples")]
        private static void Open()
        {
            GetWindow<RippleMeshPreprocessorWindow>("Ripple Mesh Bake");
        }

        private void OnEnable()
        {
            // Default to the mesh of the currently selected GameObject, if any.
            var go = Selection.activeGameObject;
            if (go != null)
            {
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null) sourceMesh = meshFilter.sharedMesh;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Builds a welded-vertex adjacency graph and bakes the welded ID into UV3.\n" +
                "Run once per wall/blob mesh (and again whenever the mesh changes).",
                MessageType.Info);

            sourceMesh = (Mesh)EditorGUILayout.ObjectField("Source Mesh", sourceMesh, typeof(Mesh), false);
            weldEpsilon = EditorGUILayout.FloatField(
                new GUIContent("Weld Epsilon", "Position-based welding threshold, in the mesh's local space units."),
                weldEpsilon);
            outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);

            GUI.enabled = sourceMesh != null;
            if (GUILayout.Button("Bake"))
            {
                Bake(sourceMesh, weldEpsilon, outputFolder);
            }
            GUI.enabled = true;
        }

        public static void Bake(Mesh mesh, float epsilon, string outputFolder)
        {
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
                AssetDatabase.Refresh();
            }

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int vertexCount = vertices.Length;

            // ---- Step 1: weld vertices by position using a spatial hash ----
            // Cell key is the position rounded to the epsilon grid. We check
            // the cell itself plus its neighbors in case a point falls right
            // on a cell boundary.
            var buckets = new Dictionary<Vector3Int, List<int>>();
            var vertexToWelded = new int[vertexCount];
            var weldedPositions = new List<Vector3>();
            var weldedNormalSum = new List<Vector3>();
            var weldedContribCount = new List<int>();

            float invEpsilon = 1f / epsilon;

            Vector3Int CellOf(Vector3 p) => new Vector3Int(
                Mathf.RoundToInt(p.x * invEpsilon),
                Mathf.RoundToInt(p.y * invEpsilon),
                Mathf.RoundToInt(p.z * invEpsilon));

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 position = vertices[i];
                Vector3Int cell = CellOf(position);
                int foundWeldedId = -1;

                // Scan the 3x3x3 cell neighborhood so points near a cell
                // border are not missed.
                for (int dx = -1; dx <= 1 && foundWeldedId < 0; dx++)
                for (int dy = -1; dy <= 1 && foundWeldedId < 0; dy++)
                for (int dz = -1; dz <= 1 && foundWeldedId < 0; dz++)
                {
                    var neighborCell = new Vector3Int(cell.x + dx, cell.y + dy, cell.z + dz);
                    if (!buckets.TryGetValue(neighborCell, out var candidates)) continue;

                    foreach (var weldedId in candidates)
                    {
                        if ((weldedPositions[weldedId] - position).sqrMagnitude <= epsilon * epsilon)
                        {
                            foundWeldedId = weldedId;
                            break;
                        }
                    }
                }

                if (foundWeldedId < 0)
                {
                    foundWeldedId = weldedPositions.Count;
                    weldedPositions.Add(position);
                    weldedNormalSum.Add(Vector3.zero);
                    weldedContribCount.Add(0);

                    if (!buckets.TryGetValue(cell, out var list))
                    {
                        list = new List<int>();
                        buckets[cell] = list;
                    }
                    list.Add(foundWeldedId);
                }

                vertexToWelded[i] = foundWeldedId;

                if (normals != null && normals.Length == vertexCount)
                {
                    weldedNormalSum[foundWeldedId] += normals[i];
                    weldedContribCount[foundWeldedId]++;
                }
            }

            int weldedCount = weldedPositions.Count;
            var weldedNormals = new Vector3[weldedCount];
            for (int i = 0; i < weldedCount; i++)
            {
                weldedNormals[i] = weldedContribCount[i] > 0
                    ? (weldedNormalSum[i] / weldedContribCount[i]).normalized
                    : Vector3.up;
            }

            // ---- Step 2: build the adjacency graph from triangle edges ----
            var neighborSets = new HashSet<int>[weldedCount];
            for (int i = 0; i < weldedCount; i++) neighborSets[i] = new HashSet<int>();

            int[] triangles = mesh.triangles;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = vertexToWelded[triangles[t]];
                int b = vertexToWelded[triangles[t + 1]];
                int c = vertexToWelded[triangles[t + 2]];

                AddEdge(neighborSets, a, b);
                AddEdge(neighborSets, b, c);
                AddEdge(neighborSets, c, a);
            }

            // ---- Step 3: pack into CSR (offsets + flat indices + edge lengths) ----
            var neighborOffsets = new int[weldedCount + 1];
            var neighborIndicesList = new List<int>();
            var neighborDistancesList = new List<float>();
            for (int i = 0; i < weldedCount; i++)
            {
                neighborOffsets[i] = neighborIndicesList.Count;
                foreach (var neighborId in neighborSets[i])
                {
                    neighborIndicesList.Add(neighborId);
                    neighborDistancesList.Add(Vector3.Distance(weldedPositions[i], weldedPositions[neighborId]));
                }
            }
            neighborOffsets[weldedCount] = neighborIndicesList.Count;

            // ---- Step 4: bake welded IDs into UV3 of a mesh copy ----
            Mesh bakedMesh = Object.Instantiate(mesh);
            bakedMesh.name = mesh.name + "_RippleBaked";

            var uv3 = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                // Welded ID goes in the x component. y is reserved for future
                // use (e.g. vertex degree, debug data).
                uv3[i] = new Vector2(vertexToWelded[i], 0f);
            }
            bakedMesh.SetUVs(3, new List<Vector2>(uv3));

            string meshPath = Path.Combine(outputFolder, bakedMesh.name + ".asset");
            AssetDatabase.CreateAsset(bakedMesh, AssetDatabase.GenerateUniqueAssetPath(meshPath));

            // ---- Step 5: save the adjacency asset ----
            var data = ScriptableObject.CreateInstance<RippleAdjacencyData>();
            data.bakedMesh = bakedMesh;
            data.weldedVertexCount = weldedCount;
            data.weldedPositions = weldedPositions.ToArray();
            data.weldedNormals = weldedNormals;
            data.neighborOffsets = neighborOffsets;
            data.neighborIndices = neighborIndicesList.ToArray();
            data.neighborDistances = neighborDistancesList.ToArray();

            string dataPath = Path.Combine(outputFolder, mesh.name + "_RippleAdjacency.asset");
            AssetDatabase.CreateAsset(data, AssetDatabase.GenerateUniqueAssetPath(dataPath));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RippleSystem] Baked '{mesh.name}': {vertexCount} source vertices -> " +
                      $"{weldedCount} welded, {neighborIndicesList.Count} directed edges. " +
                      $"Mesh: {meshPath}, Adjacency: {dataPath}");

            Selection.activeObject = data;
        }

        private static void AddEdge(HashSet<int>[] neighborSets, int a, int b)
        {
            if (a == b) return; // degenerate triangle, skip
            neighborSets[a].Add(b);
            neighborSets[b].Add(a);
        }
    }
}
