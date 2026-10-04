// NearShaderProxyMeshGenerator
// Prototype version: 0.0.1

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace orz_Shop.NearShaderProxyMeshGenerator
{
    internal static class orz_Shop_NearShaderProxyMeshGenerator
    {
        internal const string ProxySuffix = "_NearShaderProxy";

        internal sealed class GenerationResult
        {
            public GameObject ProxyObject;
            public Mesh ProxyMesh;
            public Material AssignedMaterial;
            public int SourceVertexCount;
            public int SourceTriangleCount;
            public int ProxyVertexCount;
            public int ProxyTriangleCount;
        }

        private sealed class Cluster
        {
            public Vector3 PositionSum;
            public Vector3 NormalSum;
            public Vector2 UvSum;
            public int Count;
            public readonly Dictionary<int, float> BoneWeights = new Dictionary<int, float>();

            public void Add(Vector3 position, Vector3 normal, Vector2 uv, BoneWeight boneWeight)
            {
                PositionSum += position;
                NormalSum += normal;
                UvSum += uv;
                Count++;

                AddBoneWeight(boneWeight.boneIndex0, boneWeight.weight0);
                AddBoneWeight(boneWeight.boneIndex1, boneWeight.weight1);
                AddBoneWeight(boneWeight.boneIndex2, boneWeight.weight2);
                AddBoneWeight(boneWeight.boneIndex3, boneWeight.weight3);
            }

            private void AddBoneWeight(int boneIndex, float weight)
            {
                if (weight <= 0f)
                    return;

                if (BoneWeights.TryGetValue(boneIndex, out float current))
                    BoneWeights[boneIndex] = current + weight;
                else
                    BoneWeights.Add(boneIndex, weight);
            }
        }

        private readonly struct GridKey : IEquatable<GridKey>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public GridKey(Vector3 position, Vector3 origin, float cellSize)
            {
                Vector3 relative = position - origin;
                _x = Mathf.FloorToInt(relative.x / cellSize);
                _y = Mathf.FloorToInt(relative.y / cellSize);
                _z = Mathf.FloorToInt(relative.z / cellSize);
            }

            public bool Equals(GridKey other) => _x == other._x && _y == other._y && _z == other._z;
            public override bool Equals(object obj) => obj is GridKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _x;
                    hash = (hash * 397) ^ _y;
                    hash = (hash * 397) ^ _z;
                    return hash;
                }
            }
        }

        private readonly struct TriangleKey : IEquatable<TriangleKey>
        {
            private readonly int _a;
            private readonly int _b;
            private readonly int _c;

            public TriangleKey(int a, int b, int c)
            {
                if (a > b) Swap(ref a, ref b);
                if (b > c) Swap(ref b, ref c);
                if (a > b) Swap(ref a, ref b);

                _a = a;
                _b = b;
                _c = c;
            }

            private static void Swap(ref int a, ref int b)
            {
                int t = a;
                a = b;
                b = t;
            }

            public bool Equals(TriangleKey other) => _a == other._a && _b == other._b && _c == other._c;
            public override bool Equals(object obj) => obj is TriangleKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _a;
                    hash = (hash * 397) ^ _b;
                    hash = (hash * 397) ^ _c;
                    return hash;
                }
            }
        }

        internal static GenerationResult Generate(
            SkinnedMeshRenderer source,
            Material requestedMaterial,
            float clusterCellSize,
            float surfaceOffset,
            string outputFolder,
            bool replaceExisting)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (source.sharedMesh == null)
                throw new InvalidOperationException("Source SkinnedMeshRenderer has no sharedMesh.");
            if (clusterCellSize <= 0f)
                throw new ArgumentOutOfRangeException(nameof(clusterCellSize), "Cluster cell size must be greater than zero.");
            if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
                throw new ArgumentException("Output folder must be inside Assets/.", nameof(outputFolder));

            Mesh sourceMesh = source.sharedMesh;
            ReadSourceMesh(sourceMesh, source.bones, out Vector3[] vertices, out Vector3[] normals, out Vector2[] uvs, out BoneWeight[] boneWeights, out List<int> sourceTriangles);

            Mesh proxyMesh = BuildProxyMesh(sourceMesh, vertices, normals, uvs, boneWeights, sourceTriangles, clusterCellSize, surfaceOffset);

            EnsureAssetFolder(outputFolder);
            string safeName = MakeSafeFileName(source.gameObject.name + ProxySuffix);
            string meshPath = $"{outputFolder}/{safeName}.asset";
            string materialPath = $"{outputFolder}/{safeName}_Black.mat";

            if (replaceExisting && AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                AssetDatabase.DeleteAsset(meshPath);

            if (!replaceExisting)
                meshPath = AssetDatabase.GenerateUniqueAssetPath(meshPath);

            AssetDatabase.CreateAsset(proxyMesh, meshPath);

            Material material = requestedMaterial;
            if (material == null)
                material = GetOrCreateFallbackBlackMaterial(materialPath, replaceExisting);

            Transform existing = source.transform.Find(source.gameObject.name + ProxySuffix);
            if (existing != null && replaceExisting)
                Undo.DestroyObjectImmediate(existing.gameObject);

            GameObject proxyObject = new GameObject(source.gameObject.name + ProxySuffix);
            Undo.RegisterCreatedObjectUndo(proxyObject, "Generate Near Shader Proxy Mesh");
            proxyObject.transform.SetParent(source.transform, false);
            proxyObject.transform.localPosition = Vector3.zero;
            proxyObject.transform.localRotation = Quaternion.identity;
            proxyObject.transform.localScale = Vector3.one;

            SkinnedMeshRenderer proxyRenderer = proxyObject.AddComponent<SkinnedMeshRenderer>();
            proxyRenderer.sharedMesh = proxyMesh;
            proxyRenderer.bones = source.bones;
            proxyRenderer.rootBone = source.rootBone;
            proxyRenderer.quality = source.quality;
            proxyRenderer.updateWhenOffscreen = source.updateWhenOffscreen;
            proxyRenderer.sharedMaterial = material;
            proxyRenderer.shadowCastingMode = ShadowCastingMode.Off;
            proxyRenderer.receiveShadows = false;
            proxyRenderer.lightProbeUsage = LightProbeUsage.Off;
            proxyRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            proxyRenderer.localBounds = ExpandBounds(source.localBounds, Mathf.Abs(surfaceOffset) + clusterCellSize);

            EditorUtility.SetDirty(proxyObject);
            AssetDatabase.SaveAssets();

            return new GenerationResult
            {
                ProxyObject = proxyObject,
                ProxyMesh = proxyMesh,
                AssignedMaterial = material,
                SourceVertexCount = vertices.Length,
                SourceTriangleCount = sourceTriangles.Count / 3,
                ProxyVertexCount = proxyMesh.vertexCount,
                ProxyTriangleCount = proxyMesh.triangles.Length / 3
            };
        }

        internal static bool DeleteGeneratedProxy(SkinnedMeshRenderer source, string outputFolder)
        {
            if (source == null)
                return false;

            bool changed = false;
            Transform child = source.transform.Find(source.gameObject.name + ProxySuffix);
            if (child != null)
            {
                Undo.DestroyObjectImmediate(child.gameObject);
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(outputFolder) && outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                string safeName = MakeSafeFileName(source.gameObject.name + ProxySuffix);
                string meshPath = $"{outputFolder}/{safeName}.asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                {
                    AssetDatabase.DeleteAsset(meshPath);
                    changed = true;
                }
            }

            if (changed)
                AssetDatabase.SaveAssets();

            return changed;
        }

        private static Mesh BuildProxyMesh(
            Mesh sourceMesh,
            IReadOnlyList<Vector3> sourceVertices,
            IReadOnlyList<Vector3> sourceNormals,
            IReadOnlyList<Vector2> sourceUvs,
            IReadOnlyList<BoneWeight> sourceWeights,
            IReadOnlyList<int> sourceTriangles,
            float cellSize,
            float surfaceOffset)
        {
            Vector3 origin = CalculateMin(sourceVertices);
            var clusterLookup = new Dictionary<GridKey, int>(sourceVertices.Count);
            var clusters = new List<Cluster>();
            var vertexToCluster = new int[sourceVertices.Count];

            for (int i = 0; i < sourceVertices.Count; i++)
            {
                GridKey key = new GridKey(sourceVertices[i], origin, cellSize);
                if (!clusterLookup.TryGetValue(key, out int clusterIndex))
                {
                    clusterIndex = clusters.Count;
                    clusterLookup.Add(key, clusterIndex);
                    clusters.Add(new Cluster());
                }

                clusters[clusterIndex].Add(sourceVertices[i], sourceNormals[i], sourceUvs[i], sourceWeights[i]);
                vertexToCluster[i] = clusterIndex;
            }

            var vertices = new List<Vector3>(clusters.Count);
            var normals = new List<Vector3>(clusters.Count);
            var uvs = new List<Vector2>(clusters.Count);
            var boneWeights = new List<BoneWeight>(clusters.Count);

            foreach (Cluster cluster in clusters)
            {
                float invCount = 1f / Mathf.Max(1, cluster.Count);
                Vector3 normal = cluster.NormalSum.sqrMagnitude > 1e-12f ? cluster.NormalSum.normalized : Vector3.up;
                Vector3 position = cluster.PositionSum * invCount + normal * surfaceOffset;

                vertices.Add(position);
                normals.Add(normal);
                uvs.Add(cluster.UvSum * invCount);
                boneWeights.Add(BuildBoneWeight(cluster.BoneWeights));
            }

            var triangles = new List<int>(sourceTriangles.Count);
            var seen = new HashSet<TriangleKey>();

            for (int i = 0; i + 2 < sourceTriangles.Count; i += 3)
            {
                int a = vertexToCluster[sourceTriangles[i]];
                int b = vertexToCluster[sourceTriangles[i + 1]];
                int c = vertexToCluster[sourceTriangles[i + 2]];

                if (a == b || b == c || c == a)
                    continue;

                Vector3 ab = vertices[b] - vertices[a];
                Vector3 ac = vertices[c] - vertices[a];
                if (Vector3.Cross(ab, ac).sqrMagnitude < 1e-12f)
                    continue;

                TriangleKey key = new TriangleKey(a, b, c);
                if (!seen.Add(key))
                    continue;

                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }

            if (triangles.Count == 0)
                throw new InvalidOperationException("Proxy generation produced no triangles. Reduce Cluster Cell Size.");

            var mesh = new Mesh
            {
                name = sourceMesh.name + ProxySuffix,
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.boneWeights = boneWeights.ToArray();
            mesh.bindposes = sourceMesh.bindposes;
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();

            return mesh;
        }

        private static BoneWeight BuildBoneWeight(Dictionary<int, float> accumulated)
        {
            if (accumulated == null || accumulated.Count == 0)
                return new BoneWeight { boneIndex0 = 0, weight0 = 1f };

            KeyValuePair<int, float>[] top = accumulated
                .Where(pair => pair.Value > 0f)
                .OrderByDescending(pair => pair.Value)
                .Take(4)
                .ToArray();

            float total = top.Sum(pair => pair.Value);
            if (total <= 1e-8f)
                return new BoneWeight { boneIndex0 = 0, weight0 = 1f };

            BoneWeight result = default;

            for (int i = 0; i < top.Length; i++)
            {
                float normalized = top[i].Value / total;
                switch (i)
                {
                    case 0:
                        result.boneIndex0 = top[i].Key;
                        result.weight0 = normalized;
                        break;
                    case 1:
                        result.boneIndex1 = top[i].Key;
                        result.weight1 = normalized;
                        break;
                    case 2:
                        result.boneIndex2 = top[i].Key;
                        result.weight2 = normalized;
                        break;
                    case 3:
                        result.boneIndex3 = top[i].Key;
                        result.weight3 = normalized;
                        break;
                }
            }

            return result;
        }

        private static void ReadSourceMesh(
            Mesh mesh,
            Transform[] bones,
            out Vector3[] vertices,
            out Vector3[] normals,
            out Vector2[] uvs,
            out BoneWeight[] boneWeights,
            out List<int> triangles)
        {
            using (Mesh.MeshDataArray dataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData data = dataArray[0];
                int vertexCount = data.vertexCount;

                using (var nativeVertices = new NativeArray<Vector3>(vertexCount, Allocator.Temp))
                {
                    data.GetVertices(nativeVertices);
                    vertices = nativeVertices.ToArray();
                }

                if (data.HasVertexAttribute(VertexAttribute.Normal))
                {
                    using (var nativeNormals = new NativeArray<Vector3>(vertexCount, Allocator.Temp))
                    {
                        data.GetNormals(nativeNormals);
                        normals = nativeNormals.ToArray();
                    }
                }
                else
                {
                    normals = null;
                }

                if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    using (var nativeUvs = new NativeArray<Vector2>(vertexCount, Allocator.Temp))
                    {
                        data.GetUVs(0, nativeUvs);
                        uvs = nativeUvs.ToArray();
                    }
                }
                else
                {
                    uvs = new Vector2[vertexCount];
                }

                triangles = ReadTriangleIndices(data);
            }

            if (normals == null || normals.Length != vertices.Length)
                normals = CalculateNormals(vertices, triangles);

            var weights = new List<BoneWeight>(vertices.Length);
            try
            {
                mesh.GetBoneWeights(weights);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to read source bone weights. Try enabling Read/Write on the source model importer.", ex);
            }

            if (weights.Count == vertices.Length)
            {
                boneWeights = weights.ToArray();
            }
            else
            {
                boneWeights = new BoneWeight[vertices.Length];
                for (int i = 0; i < boneWeights.Length; i++)
                {
                    boneWeights[i] = new BoneWeight
                    {
                        boneIndex0 = 0,
                        weight0 = bones != null && bones.Length > 0 ? 1f : 0f
                    };
                }
            }
        }

        private static List<int> ReadTriangleIndices(Mesh.MeshData data)
        {
            var result = new List<int>();

            if (data.indexFormat == IndexFormat.UInt16)
            {
                NativeArray<ushort> indices = data.GetIndexData<ushort>();
                for (int subMeshIndex = 0; subMeshIndex < data.subMeshCount; subMeshIndex++)
                {
                    SubMeshDescriptor subMesh = data.GetSubMesh(subMeshIndex);
                    if (subMesh.topology != MeshTopology.Triangles)
                        continue;

                    for (int i = 0; i < subMesh.indexCount; i++)
                        result.Add(indices[subMesh.indexStart + i] + subMesh.baseVertex);
                }
            }
            else
            {
                NativeArray<uint> indices = data.GetIndexData<uint>();
                for (int subMeshIndex = 0; subMeshIndex < data.subMeshCount; subMeshIndex++)
                {
                    SubMeshDescriptor subMesh = data.GetSubMesh(subMeshIndex);
                    if (subMesh.topology != MeshTopology.Triangles)
                        continue;

                    for (int i = 0; i < subMesh.indexCount; i++)
                        result.Add((int)indices[subMesh.indexStart + i] + subMesh.baseVertex);
                }
            }

            if (result.Count == 0)
                throw new InvalidOperationException("Source mesh has no triangle submeshes.");

            return result;
        }

        private static Vector3[] CalculateNormals(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles)
        {
            var result = new Vector3[vertices.Count];

            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                result[a] += normal;
                result[b] += normal;
                result[c] += normal;
            }

            for (int i = 0; i < result.Length; i++)
                result[i] = result[i].sqrMagnitude > 1e-12f ? result[i].normalized : Vector3.up;

            return result;
        }

        private static Vector3 CalculateMin(IReadOnlyList<Vector3> vertices)
        {
            if (vertices.Count == 0)
                return Vector3.zero;

            Vector3 min = vertices[0];
            for (int i = 1; i < vertices.Count; i++)
                min = Vector3.Min(min, vertices[i]);
            return min;
        }

        private static Bounds ExpandBounds(Bounds source, float margin)
        {
            source.Expand(Mathf.Max(0f, margin) * 2f);
            return source;
        }

        private static Material GetOrCreateFallbackBlackMaterial(string path, bool replaceExisting)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null && !replaceExisting)
                return existing;

            if (existing != null)
                AssetDatabase.DeleteAsset(path);

            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Standard");
            if (shader == null)
                throw new InvalidOperationException("No fallback shader was found. Assign a material explicitly.");

            var material = new Material(shader)
            {
                name = Path.GetFileNameWithoutExtension(path),
                color = Color.black
            };

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void EnsureAssetFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts = folder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
                throw new ArgumentException("Output folder must be under Assets/.", nameof(folder));

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name;
        }
    }
}
