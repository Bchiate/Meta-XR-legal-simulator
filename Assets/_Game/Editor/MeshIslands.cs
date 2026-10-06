using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Splits a mesh into connected-vertex islands (parts that share vertices/positions). Editor utility.</summary>
public static class MeshIslands
{
    public class Island { public Mesh mesh; public Bounds bounds; public int triangles; public int subMesh; }

    public static List<Island> Split(Mesh src, float weldDistance = 1e-4f)
    {
        var verts = src.vertices; var normals = src.normals; var uvs = src.uv; var tangents = src.tangents;
        int n = verts.Length;
        // Weld by position so seams between duplicated verts (UV/normal splits) count as connected.
        var key = new Dictionary<Vector3Int, int>(); var canon = new int[n];
        float inv = 1f / weldDistance;
        for (int i = 0; i < n; i++)
        {
            var k = new Vector3Int(Mathf.RoundToInt(verts[i].x * inv), Mathf.RoundToInt(verts[i].y * inv), Mathf.RoundToInt(verts[i].z * inv));
            if (!key.TryGetValue(k, out var c)) { key[k] = i; c = i; }
            canon[i] = c;
        }
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        for (int i = 0; i < n; i++) Union(i, canon[i]);

        var triBySub = new List<int[]>();
        for (int s = 0; s < src.subMeshCount; s++) { var t = src.GetTriangles(s); triBySub.Add(t); for (int i = 0; i < t.Length; i += 3) { Union(t[i], t[i + 1]); Union(t[i], t[i + 2]); } }

        var islands = new List<Island>();
        for (int s = 0; s < triBySub.Count; s++)
        {
            var groups = new Dictionary<int, List<int>>();
            var t = triBySub[s];
            for (int i = 0; i < t.Length; i += 3)
            {
                int root = Find(t[i]);
                if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
                list.Add(t[i]); list.Add(t[i + 1]); list.Add(t[i + 2]);
            }
            foreach (var g in groups.Values)
            {
                var remap = new Dictionary<int, int>(); var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nu = new List<Vector2>(); var nt = new List<Vector4>(); var ntri = new List<int>();
                foreach (var vi in g)
                {
                    if (!remap.TryGetValue(vi, out var ni))
                    {
                        ni = nv.Count; remap[vi] = ni; nv.Add(verts[vi]);
                        if (normals.Length == n) nn.Add(normals[vi]); if (uvs.Length == n) nu.Add(uvs[vi]); if (tangents.Length == n) nt.Add(tangents[vi]);
                    }
                    ntri.Add(ni);
                }
                var m = new Mesh { name = $"{src.name}_island{islands.Count}" };
                m.indexFormat = nv.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                m.SetVertices(nv); if (nn.Count == nv.Count) m.SetNormals(nn); if (nu.Count == nv.Count) m.SetUVs(0, nu); if (nt.Count == nv.Count) m.SetTangents(nt);
                m.SetTriangles(ntri, 0); m.RecalculateBounds();
                islands.Add(new Island { mesh = m, bounds = m.bounds, triangles = ntri.Count / 3, subMesh = s });
            }
        }
        return islands;
    }
}
