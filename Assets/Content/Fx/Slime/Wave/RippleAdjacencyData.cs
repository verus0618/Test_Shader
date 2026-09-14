using UnityEngine;

namespace RippleSystem
{
    /// <summary>
    /// Baked adjacency graph for a surface mesh: welded vertices (duplicate
    /// positions caused by UV seams / hard normals merged into one logical
    /// point) plus their neighbor list, stored in CSR format.
    ///
    /// Produced offline by RippleMeshPreprocessor and referenced by
    /// RippleSurfaceController at runtime.
    ///
    /// CSR (Compressed Sparse Row) layout:
    /// neighbors of vertex i are neighborIndices[neighborOffsets[i] .. neighborOffsets[i+1] - 1]
    /// </summary>
    [CreateAssetMenu(menuName = "Ripple System/Adjacency Data", fileName = "RippleAdjacency")]
    public class RippleAdjacencyData : ScriptableObject
    {
        [Tooltip("Mesh this graph was baked from (after welded IDs were written to UV3).")]
        public Mesh bakedMesh;

        [Tooltip("Number of welded vertices (logical surface points, seam duplicates merged).")]
        public int weldedVertexCount;

        [Tooltip("Local-space positions of welded vertices, used for the intersection test.")]
        public Vector3[] weldedPositions;

        [Tooltip("Averaged normals of welded vertices, reserved for future normal-based displacement.")]
        public Vector3[] weldedNormals;

        [Tooltip("CSR offsets. offsets[i]..offsets[i+1] is the neighbor range for vertex i in neighborIndices. Length = weldedVertexCount + 1.")]
        public int[] neighborOffsets;

        [Tooltip("CSR flat neighbor index list for all vertices.")]
        public int[] neighborIndices;

        [Tooltip("World/local-space distance to each neighbor, parallel array to neighborIndices. Used to weight the wave propagation so non-uniform mesh density doesn't distort perceived wave speed.")]
        public float[] neighborDistances;

        public int GetDegree(int weldedIndex)
        {
            return neighborOffsets[weldedIndex + 1] - neighborOffsets[weldedIndex];
        }
    }
}
