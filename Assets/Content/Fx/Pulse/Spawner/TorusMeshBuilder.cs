using UnityEngine;

namespace TestMisha.Fx.Pulse
{
    /// <summary>Builds a torus mesh, because Unity has no built-in one. The hole axis is Y.</summary>
    internal static class TorusMeshBuilder
    {
        /// <param name="majorRadius">Distance from the torus center to the middle of the tube.</param>
        /// <param name="minorRadius">Radius of the tube.</param>
        /// <param name="majorSegments">Subdivisions around the big ring.</param>
        /// <param name="minorSegments">Subdivisions around the tube.</param>
        public static Mesh Build(float majorRadius, float minorRadius, int majorSegments, int minorSegments)
        {
            // The first and last ring share a position but not UVs, so each ring has one extra vertex.
            int ringVertexCount = minorSegments + 1;
            int vertexCount = (majorSegments + 1) * ringVertexCount;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];

            for (int major = 0; major <= majorSegments; major++)
            {
                float majorT = major / (float)majorSegments;
                float majorAngle = majorT * Mathf.PI * 2f;
                var ringDirection = new Vector3(Mathf.Cos(majorAngle), 0f, Mathf.Sin(majorAngle));

                for (int minor = 0; minor <= minorSegments; minor++)
                {
                    float minorT = minor / (float)minorSegments;
                    float minorAngle = minorT * Mathf.PI * 2f;
                    Vector3 normal = ringDirection * Mathf.Cos(minorAngle) + Vector3.up * Mathf.Sin(minorAngle);

                    int index = major * ringVertexCount + minor;
                    vertices[index] = ringDirection * majorRadius + normal * minorRadius;
                    normals[index] = normal;
                    uvs[index] = new Vector2(majorT, minorT);
                }
            }

            var triangles = new int[majorSegments * minorSegments * 6];
            int cursor = 0;
            for (int major = 0; major < majorSegments; major++)
            {
                for (int minor = 0; minor < minorSegments; minor++)
                {
                    int current = major * ringVertexCount + minor;
                    int nextMajor = current + ringVertexCount;

                    // Unity treats clockwise triangles as front faces, so the outside faces outward.
                    triangles[cursor++] = current;
                    triangles[cursor++] = current + 1;
                    triangles[cursor++] = nextMajor;
                    triangles[cursor++] = nextMajor;
                    triangles[cursor++] = current + 1;
                    triangles[cursor++] = nextMajor + 1;
                }
            }

            var mesh = new Mesh
            {
                name = "Torus (generated)",
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles,
            };
            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
