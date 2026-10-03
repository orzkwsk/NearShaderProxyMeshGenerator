// NearShaderProxyMeshGenerator
// Prototype version: 0.0.3

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
    internal static class orz_Shop_NearShaderProxyVolumeGenerator
    {
        internal const string ProxySuffix = "_NearShaderProxyVolume";
        internal const string ShaderName = "orz_Shop/NearShaderProxyVolume";

        internal sealed class GenerationResult
        {
            public GameObject ProxyObject;
            public Mesh ProxyMesh;
            public Material AssignedMaterial;
            public int SourceVertexCount;
            public int SourceTriangleCount;
            public int ProxyVertexCount;
            public int ProxyTriangleCount;
            public int CutPlaneCount;
            public int SkippedBoundaryCount;
            public int BoundaryEdgesBeforeSeal;
            public int BoundaryEdgesAfterSeal;
            public int NonManifoldEdgesAfterSeal;
        }

        private struct VertexData
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv;
            public BoneWeight BoneWeight;
        }

        private sealed class BoneBoundary
        {
            public Transform Bone;
            public bool[] DistalBoneMask;

            public float DistalWeight(BoneWeight weight)
            {
                float sum = 0f;
                if (IsDistal(weight.boneIndex0)) sum += weight.weight0;
                if (IsDistal(weight.boneIndex1)) sum += weight.weight1;
                if (IsDistal(weight.boneIndex2)) sum += weight.weight2;
                if (IsDistal(weight.boneIndex3)) sum += weight.weight3;
                return sum;
            }

            private bool IsDistal(int boneIndex)
            {
                return boneIndex >= 0 &&
                       boneIndex < DistalBoneMask.Length &&
                       DistalBoneMask[boneIndex];
            }
        }

        private sealed class Cluster
        {
            public Vector3 PositionSum;
            public Vector3 NormalSum;
            public Vector2 UvSum;
            public int Count;
            public readonly Dictionary<int, float> BoneWeights = new Dictionary<int, float>();

            public void Add(VertexData vertex)
            {
                PositionSum += vertex.Position;
                NormalSum += vertex.Normal;
                UvSum += vertex.Uv;
                Count++;
                AccumulateBoneWeight(BoneWeights, vertex.BoneWeight, 1f);
            }
        }

        private readonly struct GridKey : IEquatable<GridKey>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public GridKey(Vector3 position, float cellSize)
            {
                _x = Mathf.RoundToInt(position.x / cellSize);
                _y = Mathf.RoundToInt(position.y / cellSize);
                _z = Mathf.RoundToInt(position.z / cellSize);
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

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly int A;
            public readonly int B;

            public EdgeKey(int a, int b)
            {
                if (a < b)
                {
                    A = a;
                    B = b;
                }
                else
                {
                    A = b;
                    B = a;
                }
            }

            public bool Equals(EdgeKey other) => A == other.A && B == other.B;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => (A * 397) ^ B;
        }

        private sealed class EdgeInfo
        {
            public int Count;
            public int From;
            public int To;
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
            public override int GetHashCode() => ((_a * 397) ^ _b) * 397 ^ _c;
        }

        internal static GenerationResult Generate(
            SkinnedMeshRenderer source,
            Material requestedMaterial,
            IReadOnlyList<Transform> cutoffBones,
            float boneCutBias,
            float mergeSize,
            float surfaceOffset,
            bool sealOpenBoundaries,
            float fadeDistance,
            float fadeStrength,
            float coreStrength,
            string outputFolder,
            bool replaceExisting)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (source.sharedMesh == null)
                throw new InvalidOperationException("Source SkinnedMeshRenderer has no sharedMesh.");
            if (mergeSize < 0f)
                throw new ArgumentOutOfRangeException(nameof(mergeSize), "Merge Size must not be negative.");
            if (fadeDistance < 0f)
                throw new ArgumentOutOfRangeException(nameof(fadeDistance), "Fade Distance must not be negative.");
            if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
                throw new ArgumentException("Output folder must be inside Assets/.", nameof(outputFolder));

            Mesh sourceMesh = source.sharedMesh;
            ReadSourceMesh(
                sourceMesh,
                source.bones,
                out Vector3[] sourceVertices,
                out Vector3[] sourceNormals,
                out Vector2[] sourceUvs,
                out BoneWeight[] sourceBoneWeights,
                out List<int> sourceTriangles);

            List<BoneBoundary> boundaries = BuildBoneBoundaries(source, cutoffBones, out int skippedBoundaryCount);
            float distalThreshold = Mathf.Clamp(0.5f + boneCutBias, 0.01f, 0.99f);

            ClipGeometryByBoneBoundaries(
                sourceVertices,
                sourceNormals,
                sourceUvs,
                sourceBoneWeights,
                sourceTriangles,
                boundaries,
                distalThreshold,
                out List<VertexData> clippedVertices,
                out List<int> clippedTriangles);

            if (clippedTriangles.Count == 0)
                throw new InvalidOperationException(
                    "All triangles were removed by the selected bone boundaries. " +
                    "Move Bone Cut Bias toward +, or remove an incorrect boundary bone.");

            Mesh proxyMesh = BuildProxy(
                sourceMesh,
                clippedVertices,
                clippedTriangles,
                mergeSize,
                surfaceOffset);

            Dictionary<EdgeKey, EdgeInfo> beforeEdges = BuildEdgeTable(proxyMesh.triangles);
            int boundaryBeforeSeal = beforeEdges.Count(pair => pair.Value.Count == 1);

            if (sealOpenBoundaries && boundaryBeforeSeal > 0)
                SealOpenBoundaries(proxyMesh);

            Dictionary<EdgeKey, EdgeInfo> afterEdges = BuildEdgeTable(proxyMesh.triangles);
            int boundaryAfterSeal = afterEdges.Count(pair => pair.Value.Count == 1);
            int nonManifoldAfterSeal = afterEdges.Count(pair => pair.Value.Count > 2);

            EnsureAssetFolder(outputFolder);
            string safeName = MakeSafeFileName(source.gameObject.name + ProxySuffix);
            string meshPath = $"{outputFolder}/{safeName}.asset";
            string materialPath = $"{outputFolder}/{safeName}_Volume.mat";

            if (replaceExisting && AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                AssetDatabase.DeleteAsset(meshPath);

            if (!replaceExisting)
                meshPath = AssetDatabase.GenerateUniqueAssetPath(meshPath);

            AssetDatabase.CreateAsset(proxyMesh, meshPath);

            Material material = requestedMaterial;
            if (material == null)
                material = GetOrCreateVolumeMaterial(
                    materialPath,
                    replaceExisting,
                    fadeDistance,
                    fadeStrength,
                    coreStrength);

            Transform existing = source.transform.Find(source.gameObject.name + ProxySuffix);
            if (existing != null && replaceExisting)
                Undo.DestroyObjectImmediate(existing.gameObject);

            GameObject proxyObject = new GameObject(source.gameObject.name + ProxySuffix);
            Undo.RegisterCreatedObjectUndo(proxyObject, "Generate Near Shader Proxy Volume");
            proxyObject.transform.SetParent(source.transform, false);
            proxyObject.transform.localPosition = Vector3.zero;
            proxyObject.transform.localRotation = Quaternion.identity;
            proxyObject.transform.localScale = Vector3.one;

            SkinnedMeshRenderer proxyRenderer = proxyObject.AddComponent<SkinnedMeshRenderer>();
            proxyRenderer.sharedMesh = proxyMesh;
            proxyRenderer.bones = source.bones;
            proxyRenderer.rootBone = source.rootBone;
            proxyRenderer.quality = source.quality;
            proxyRenderer.updateWhenOffscreen = true;
            proxyRenderer.sharedMaterial = material;
            proxyRenderer.shadowCastingMode = ShadowCastingMode.Off;
            proxyRenderer.receiveShadows = false;
            proxyRenderer.lightProbeUsage = LightProbeUsage.Off;
            proxyRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            proxyRenderer.localBounds = ExpandBounds(
                source.localBounds,
                Mathf.Abs(surfaceOffset) + Mathf.Max(fadeDistance, mergeSize));

            EditorUtility.SetDirty(proxyObject);
            AssetDatabase.SaveAssets();

            return new GenerationResult
            {
                ProxyObject = proxyObject,
                ProxyMesh = proxyMesh,
                AssignedMaterial = material,
                SourceVertexCount = sourceVertices.Length,
                SourceTriangleCount = sourceTriangles.Count / 3,
                ProxyVertexCount = proxyMesh.vertexCount,
                ProxyTriangleCount = proxyMesh.triangles.Length / 3,
                CutPlaneCount = boundaries.Count,
                SkippedBoundaryCount = skippedBoundaryCount,
                BoundaryEdgesBeforeSeal = boundaryBeforeSeal,
                BoundaryEdgesAfterSeal = boundaryAfterSeal,
                NonManifoldEdgesAfterSeal = nonManifoldAfterSeal
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

            if (!string.IsNullOrWhiteSpace(outputFolder) &&
                outputFolder.StartsWith("Assets", StringComparison.Ordinal))
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

        private static List<BoneBoundary> BuildBoneBoundaries(
            SkinnedMeshRenderer source,
            IReadOnlyList<Transform> cutoffBones,
            out int skippedBoundaryCount)
        {
            skippedBoundaryCount = 0;
            var result = new List<BoneBoundary>();

            if (cutoffBones == null || cutoffBones.Count == 0)
                return result;

            Transform[] bones = source.bones ?? Array.Empty<Transform>();
            var seen = new HashSet<Transform>();

            foreach (Transform boundaryBone in cutoffBones)
            {
                if (boundaryBone == null || !seen.Add(boundaryBone))
                    continue;

                if (Array.IndexOf(bones, boundaryBone) < 0)
                    throw new InvalidOperationException(
                        $"Boundary bone '{boundaryBone.name}' is not used by the source SkinnedMeshRenderer.");

                var mask = new bool[bones.Length];
                int descendantCount = 0;

                for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    Transform candidate = bones[boneIndex];
                    if (candidate == null || candidate == boundaryBone)
                        continue;

                    if (candidate.IsChildOf(boundaryBone))
                    {
                        mask[boneIndex] = true;
                        descendantCount++;
                    }
                }

                if (descendantCount == 0)
                {
                    skippedBoundaryCount++;
                    continue;
                }

                result.Add(new BoneBoundary
                {
                    Bone = boundaryBone,
                    DistalBoneMask = mask
                });
            }

            return result;
        }

        private static void ClipGeometryByBoneBoundaries(
            IReadOnlyList<Vector3> sourceVertices,
            IReadOnlyList<Vector3> sourceNormals,
            IReadOnlyList<Vector2> sourceUvs,
            IReadOnlyList<BoneWeight> sourceBoneWeights,
            IReadOnlyList<int> sourceTriangles,
            IReadOnlyList<BoneBoundary> boundaries,
            float distalThreshold,
            out List<VertexData> vertices,
            out List<int> triangles)
        {
            vertices = new List<VertexData>(sourceTriangles.Count);
            triangles = new List<int>(sourceTriangles.Count);

            for (int triangleIndex = 0; triangleIndex + 2 < sourceTriangles.Count; triangleIndex += 3)
            {
                var polygon = new List<VertexData>(3)
                {
                    MakeVertex(sourceTriangles[triangleIndex], sourceVertices, sourceNormals, sourceUvs, sourceBoneWeights),
                    MakeVertex(sourceTriangles[triangleIndex + 1], sourceVertices, sourceNormals, sourceUvs, sourceBoneWeights),
                    MakeVertex(sourceTriangles[triangleIndex + 2], sourceVertices, sourceNormals, sourceUvs, sourceBoneWeights)
                };

                for (int boundaryIndex = 0;
                     boundaryIndex < boundaries.Count && polygon.Count >= 3;
                     boundaryIndex++)
                {
                    polygon = ClipPolygonByBoundary(
                        polygon,
                        boundaries[boundaryIndex],
                        distalThreshold);
                }

                if (polygon.Count < 3)
                    continue;

                int baseIndex = vertices.Count;
                vertices.AddRange(polygon);

                for (int i = 1; i + 1 < polygon.Count; i++)
                {
                    triangles.Add(baseIndex);
                    triangles.Add(baseIndex + i);
                    triangles.Add(baseIndex + i + 1);
                }
            }
        }

        private static VertexData MakeVertex(
            int index,
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<Vector3> normals,
            IReadOnlyList<Vector2> uvs,
            IReadOnlyList<BoneWeight> boneWeights)
        {
            return new VertexData
            {
                Position = positions[index],
                Normal = normals[index],
                Uv = uvs[index],
                BoneWeight = boneWeights[index]
            };
        }

        private static List<VertexData> ClipPolygonByBoundary(
            IReadOnlyList<VertexData> input,
            BoneBoundary boundary,
            float distalThreshold)
        {
            var output = new List<VertexData>(input.Count + 1);
            if (input.Count == 0)
                return output;

            VertexData previous = input[input.Count - 1];
            float previousDistance = boundary.DistalWeight(previous.BoneWeight) - distalThreshold;
            bool previousInside = previousDistance <= 1e-6f;

            for (int i = 0; i < input.Count; i++)
            {
                VertexData current = input[i];
                float currentDistance = boundary.DistalWeight(current.BoneWeight) - distalThreshold;
                bool currentInside = currentDistance <= 1e-6f;

                if (previousInside != currentInside)
                {
                    float denominator = previousDistance - currentDistance;
                    float t = Mathf.Abs(denominator) > 1e-8f
                        ? previousDistance / denominator
                        : 0.5f;

                    output.Add(Interpolate(previous, current, Mathf.Clamp01(t)));
                }

                if (currentInside)
                    output.Add(current);

                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }

            return output;
        }

        private static VertexData Interpolate(VertexData a, VertexData b, float t)
        {
            Vector3 normal = Vector3.Lerp(a.Normal, b.Normal, t);
            normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;

            return new VertexData
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Normal = normal,
                Uv = Vector2.Lerp(a.Uv, b.Uv, t),
                BoneWeight = LerpBoneWeight(a.BoneWeight, b.BoneWeight, t)
            };
        }

        private static Mesh BuildProxy(
            Mesh sourceMesh,
            IReadOnlyList<VertexData> sourceVertices,
            IReadOnlyList<int> sourceTriangles,
            float mergeSize,
            float surfaceOffset)
        {
            float epsilonWeld = Mathf.Max(1e-7f, sourceMesh.bounds.size.magnitude * 1e-7f);
            float cellSize = mergeSize > 1e-7f ? mergeSize : epsilonWeld;

            var lookup = new Dictionary<GridKey, int>();
            var clusters = new List<Cluster>();
            var remap = new int[sourceVertices.Count];

            for (int i = 0; i < sourceVertices.Count; i++)
            {
                GridKey key = new GridKey(sourceVertices[i].Position, cellSize);
                if (!lookup.TryGetValue(key, out int clusterIndex))
                {
                    clusterIndex = clusters.Count;
                    lookup.Add(key, clusterIndex);
                    clusters.Add(new Cluster());
                }

                clusters[clusterIndex].Add(sourceVertices[i]);
                remap[i] = clusterIndex;
            }

            var vertices = new List<Vector3>(clusters.Count);
            var normals = new List<Vector3>(clusters.Count);
            var uvs = new List<Vector2>(clusters.Count);
            var weights = new List<BoneWeight>(clusters.Count);

            foreach (Cluster cluster in clusters)
            {
                float invCount = 1f / Mathf.Max(1, cluster.Count);
                Vector3 normal = cluster.NormalSum.sqrMagnitude > 1e-12f
                    ? cluster.NormalSum.normalized
                    : Vector3.up;

                vertices.Add(cluster.PositionSum * invCount + normal * surfaceOffset);
                normals.Add(normal);
                uvs.Add(cluster.UvSum * invCount);
                weights.Add(BuildBoneWeight(cluster.BoneWeights));
            }

            var triangles = new List<int>(sourceTriangles.Count);
            var seen = new HashSet<TriangleKey>();

            for (int i = 0; i + 2 < sourceTriangles.Count; i += 3)
            {
                int a = remap[sourceTriangles[i]];
                int b = remap[sourceTriangles[i + 1]];
                int c = remap[sourceTriangles[i + 2]];

                if (a == b || b == c || c == a)
                    continue;

                Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                if (cross.sqrMagnitude < 1e-12f)
                    continue;

                if (!seen.Add(new TriangleKey(a, b, c)))
                    continue;

                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }

            if (triangles.Count == 0)
                throw new InvalidOperationException(
                    "Proxy generation produced no triangles. Set Merge Size to 0 or remove an incorrect boundary bone.");

            var mesh = new Mesh
            {
                name = sourceMesh.name + ProxySuffix,
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = sourceMesh.bindposes;
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();

            return mesh;
        }

        private static Dictionary<EdgeKey, EdgeInfo> BuildEdgeTable(IReadOnlyList<int> triangles)
        {
            var edges = new Dictionary<EdgeKey, EdgeInfo>();

            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                AddEdge(edges, triangles[i], triangles[i + 1]);
                AddEdge(edges, triangles[i + 1], triangles[i + 2]);
                AddEdge(edges, triangles[i + 2], triangles[i]);
            }

            return edges;
        }

        private static void AddEdge(
            Dictionary<EdgeKey, EdgeInfo> edges,
            int from,
            int to)
        {
            EdgeKey key = new EdgeKey(from, to);
            if (edges.TryGetValue(key, out EdgeInfo info))
            {
                info.Count++;
            }
            else
            {
                edges.Add(key, new EdgeInfo
                {
                    Count = 1,
                    From = from,
                    To = to
                });
            }
        }

        private static void SealOpenBoundaries(Mesh mesh)
        {
            var vertices = mesh.vertices.ToList();
            var normals = mesh.normals.ToList();
            var uvs = mesh.uv != null && mesh.uv.Length == mesh.vertexCount
                ? mesh.uv.ToList()
                : Enumerable.Repeat(Vector2.zero, mesh.vertexCount).ToList();
            var weights = mesh.boneWeights.ToList();
            var triangles = mesh.triangles.ToList();

            Dictionary<EdgeKey, EdgeInfo> edgeTable = BuildEdgeTable(triangles);
            List<EdgeInfo> boundary = edgeTable.Values
                .Where(edge => edge.Count == 1)
                .ToList();

            if (boundary.Count == 0)
                return;

            var adjacency = new Dictionary<int, List<EdgeInfo>>();
            foreach (EdgeInfo edge in boundary)
            {
                AddAdjacency(adjacency, edge.From, edge);
                AddAdjacency(adjacency, edge.To, edge);
            }

            var used = new HashSet<EdgeKey>();

            foreach (EdgeInfo first in boundary)
            {
                EdgeKey firstKey = new EdgeKey(first.From, first.To);
                if (used.Contains(firstKey))
                    continue;

                var loop = new List<int> { first.From, first.To };
                used.Add(firstKey);

                int start = first.From;
                int previous = first.From;
                int current = first.To;
                bool closed = false;

                for (int guard = 0; guard < boundary.Count + 2; guard++)
                {
                    if (!adjacency.TryGetValue(current, out List<EdgeInfo> connected))
                        break;

                    EdgeInfo nextEdge = null;
                    int nextVertex = -1;

                    foreach (EdgeInfo candidate in connected)
                    {
                        EdgeKey candidateKey = new EdgeKey(candidate.From, candidate.To);
                        if (used.Contains(candidateKey))
                            continue;

                        int candidateNext = candidate.From == current
                            ? candidate.To
                            : candidate.From;

                        if (candidateNext == previous && connected.Count > 1)
                            continue;

                        nextEdge = candidate;
                        nextVertex = candidateNext;
                        break;
                    }

                    if (nextEdge == null)
                        break;

                    used.Add(new EdgeKey(nextEdge.From, nextEdge.To));

                    if (nextVertex == start)
                    {
                        closed = true;
                        break;
                    }

                    loop.Add(nextVertex);
                    previous = current;
                    current = nextVertex;
                }

                if (!closed || loop.Count < 3)
                    continue;

                Vector3 center = Vector3.zero;
                Vector2 uvCenter = Vector2.zero;
                var accumulatedWeights = new Dictionary<int, float>();

                foreach (int index in loop)
                {
                    center += vertices[index];
                    uvCenter += uvs[index];
                    AccumulateBoneWeight(
                        accumulatedWeights,
                        weights[index],
                        1f / loop.Count);
                }

                center /= loop.Count;
                uvCenter /= loop.Count;

                Vector3 loopNormal = Vector3.zero;
                for (int i = 0; i < loop.Count; i++)
                {
                    Vector3 a = vertices[loop[i]] - center;
                    Vector3 b = vertices[loop[(i + 1) % loop.Count]] - center;
                    loopNormal += Vector3.Cross(a, b);
                }

                Vector3 capNormal = loopNormal.sqrMagnitude > 1e-12f
                    ? -loopNormal.normalized
                    : Vector3.up;

                int centerIndex = vertices.Count;
                vertices.Add(center);
                normals.Add(capNormal);
                uvs.Add(uvCenter);
                weights.Add(BuildBoneWeight(accumulatedWeights));

                for (int i = 0; i < loop.Count; i++)
                {
                    int currentIndex = loop[i];
                    int nextIndex = loop[(i + 1) % loop.Count];

                    triangles.Add(nextIndex);
                    triangles.Add(currentIndex);
                    triangles.Add(centerIndex);
                }
            }

            mesh.indexFormat = vertices.Count > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.boneWeights = weights.ToArray();
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
        }

        private static void AddAdjacency(
            Dictionary<int, List<EdgeInfo>> adjacency,
            int vertex,
            EdgeInfo edge)
        {
            if (!adjacency.TryGetValue(vertex, out List<EdgeInfo> list))
            {
                list = new List<EdgeInfo>();
                adjacency.Add(vertex, list);
            }

            list.Add(edge);
        }

        private static BoneWeight LerpBoneWeight(
            BoneWeight a,
            BoneWeight b,
            float t)
        {
            var accumulated = new Dictionary<int, float>();
            AccumulateBoneWeight(accumulated, a, 1f - t);
            AccumulateBoneWeight(accumulated, b, t);
            return BuildBoneWeight(accumulated);
        }

        private static void AccumulateBoneWeight(
            Dictionary<int, float> target,
            BoneWeight weight,
            float multiplier)
        {
            AddInfluence(target, weight.boneIndex0, weight.weight0 * multiplier);
            AddInfluence(target, weight.boneIndex1, weight.weight1 * multiplier);
            AddInfluence(target, weight.boneIndex2, weight.weight2 * multiplier);
            AddInfluence(target, weight.boneIndex3, weight.weight3 * multiplier);
        }

        private static void AddInfluence(
            Dictionary<int, float> target,
            int boneIndex,
            float weight)
        {
            if (weight <= 0f)
                return;

            if (target.TryGetValue(boneIndex, out float current))
                target[boneIndex] = current + weight;
            else
                target.Add(boneIndex, weight);
        }

        private static BoneWeight BuildBoneWeight(
            Dictionary<int, float> accumulated)
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
                throw new InvalidOperationException(
                    "Failed to read source bone weights. Try enabling Read/Write on the source model importer.",
                    ex);
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

        private static Vector3[] CalculateNormals(
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> triangles)
        {
            var result = new Vector3[vertices.Count];

            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];

                Vector3 normal = Vector3.Cross(
                    vertices[b] - vertices[a],
                    vertices[c] - vertices[a]);

                result[a] += normal;
                result[b] += normal;
                result[c] += normal;
            }

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = result[i].sqrMagnitude > 1e-12f
                    ? result[i].normalized
                    : Vector3.up;
            }

            return result;
        }

        private static Material GetOrCreateVolumeMaterial(
            string path,
            bool replaceExisting,
            float fadeDistance,
            float fadeStrength,
            float coreStrength)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null && !replaceExisting)
                return existing;

            if (existing != null)
                AssetDatabase.DeleteAsset(path);

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                throw new InvalidOperationException(
                    "NearShaderProxyVolume shader was not found and no fallback shader is available.");

            var material = new Material(shader)
            {
                name = Path.GetFileNameWithoutExtension(path)
            };

            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.black);
            if (material.HasProperty("_FadeDistance"))
                material.SetFloat("_FadeDistance", fadeDistance);
            if (material.HasProperty("_FadeStrength"))
                material.SetFloat("_FadeStrength", fadeStrength);
            if (material.HasProperty("_CoreStrength"))
                material.SetFloat("_CoreStrength", coreStrength);

            if (shader.name == "Unlit/Color")
                material.color = Color.black;

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static Bounds ExpandBounds(Bounds source, float margin)
        {
            source.Expand(Mathf.Max(0f, margin) * 2f);
            return source;
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
