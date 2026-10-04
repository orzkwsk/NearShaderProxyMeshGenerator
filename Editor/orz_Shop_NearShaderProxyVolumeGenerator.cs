// NearShaderProxyMeshGenerator
// Prototype version: 0.0.6
//
// Selection model rewrite:
// - Checked bones are the INCLUDED proxy region.
// - Vertex selection = sum of weights assigned to checked bones.
// - All bones selected bypasses filtering and reproduces the full source mesh.
// - Source skinning is read with BoneWeight1 so selection is not limited to four influences.

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

        internal sealed class SelectedBoneDiagnostic
        {
            public string BoneName;
            public int BoneIndex;
            public int InfluencedVertexCount;
            public int SourceVertexCount;
        }

        internal sealed class GenerationResult
        {
            public GameObject ProxyObject;
            public Mesh ProxyMesh;
            public Material AssignedMaterial;

            public int SourceVertexCount;
            public int SourceTriangleCount;
            public int SelectedBoneCount;
            public int SelectionVertexCount;
            public int SelectionTriangleCount;

            public int ProxyVertexCount;
            public int ProxyTriangleCount;

            public int PreSimplifyTriangleCount;
            public int TargetTriangleCount;
            public int SimplificationCollapseCount;
            public bool SimplificationAttempted;
            public bool SimplificationReachedTarget;
            public string SimplificationStopReason;

            public int BoundaryEdgesBeforeSeal;
            public int BoundaryEdgesAfterSeal;
            public int NonManifoldEdgesAfterSeal;

            public bool SelectionBypassed;
            public List<SelectedBoneDiagnostic> SelectedBoneDiagnostics;
        }

        private struct VertexData
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv;
            public BoneWeight BoneWeight;
            public float SelectionWeight;
        }

        private sealed class Cluster
        {
            public Vector3 PositionSum;
            public Vector3 NormalSum;
            public Vector2 UvSum;
            public float SelectionWeightSum;
            public int Count;
            public readonly Dictionary<int, float> BoneWeights = new Dictionary<int, float>();

            public void Add(VertexData vertex)
            {
                PositionSum += vertex.Position;
                NormalSum += vertex.Normal;
                UvSum += vertex.Uv;
                SelectionWeightSum += vertex.SelectionWeight;
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

            public bool Equals(GridKey other)
            {
                return _x == other._x && _y == other._y && _z == other._z;
            }

            public override bool Equals(object obj)
            {
                return obj is GridKey other && Equals(other);
            }

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

            public bool Equals(EdgeKey other)
            {
                return A == other.A && B == other.B;
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (A * 397) ^ B;
            }
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

            public bool Equals(TriangleKey other)
            {
                return _a == other._a && _b == other._b && _c == other._c;
            }

            public override bool Equals(object obj)
            {
                return obj is TriangleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return ((_a * 397) ^ _b) * 397 ^ _c;
            }
        }

        internal static GenerationResult Generate(
            SkinnedMeshRenderer source,
            Material requestedMaterial,
            IReadOnlyList<Transform> selectedBones,
            float selectionThreshold,
            float meshQuality,
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

            if (meshQuality < 0.1f || meshQuality > 1f)
                throw new ArgumentOutOfRangeException(nameof(meshQuality), "Mesh Quality must be between 0.1 and 1.0.");

            if (selectionThreshold < 0f || selectionThreshold > 1f)
                throw new ArgumentOutOfRangeException(nameof(selectionThreshold), "Selection Threshold must be between 0 and 1.");

            if (fadeDistance < 0f)
                throw new ArgumentOutOfRangeException(nameof(fadeDistance), "Fade Distance must not be negative.");

            if (string.IsNullOrWhiteSpace(outputFolder) ||
                !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new ArgumentException("Output folder must be inside Assets/.", nameof(outputFolder));
            }

            Transform[] rendererBones = source.bones ?? Array.Empty<Transform>();
            if (rendererBones.Length == 0)
                throw new InvalidOperationException("Source renderer has no bones.");

            bool[] selectedBoneMask = BuildSelectedBoneMask(
                rendererBones,
                selectedBones,
                out int selectedBoneCount,
                out bool allRendererBonesSelected);

            if (selectedBoneCount == 0)
                throw new InvalidOperationException("No proxy target bones are selected.");

            Mesh sourceMesh = source.sharedMesh;

            ReadSourceGeometry(
                sourceMesh,
                out Vector3[] sourceVertices,
                out Vector3[] sourceNormals,
                out Vector2[] sourceUvs,
                out List<int> sourceTriangles);

            ReadSourceSkinning(
                sourceMesh,
                rendererBones.Length,
                selectedBoneMask,
                out BoneWeight[] sourceBoneWeights,
                out float[] sourceSelectionWeights,
                out int[] influencedVertexCountPerBone);

            int selectionVertexCount = allRendererBonesSelected
                ? sourceVertices.Length
                : CountSelectedVertices(sourceSelectionWeights, selectionThreshold);

            ExtractSelectedGeometry(
                sourceVertices,
                sourceNormals,
                sourceUvs,
                sourceBoneWeights,
                sourceSelectionWeights,
                sourceTriangles,
                selectionThreshold,
                allRendererBonesSelected,
                out List<VertexData> selectedVertices,
                out List<int> selectedTriangles);

            if (selectedTriangles.Count == 0)
            {
                throw new InvalidOperationException(
                    "The selected bones produced no geometry. " +
                    "Select additional bones or lower Selection Threshold.");
            }

            int selectionTriangleCount = selectedTriangles.Count / 3;

            // Build topology first with only an epsilon weld. Simplification is deliberately
            // performed after sealing so the reducer starts from a closed 2-manifold whenever possible.
            Mesh proxyMesh = BuildProxy(
                sourceMesh,
                selectedVertices,
                selectedTriangles);

            Dictionary<EdgeKey, EdgeInfo> beforeEdges = BuildEdgeTable(proxyMesh.triangles);
            int boundaryBeforeSeal = beforeEdges.Count(pair => pair.Value.Count == 1);

            if (sealOpenBoundaries && boundaryBeforeSeal > 0)
                SealOpenBoundaries(proxyMesh);

            int preSimplifyTriangleCount = proxyMesh.triangles.Length / 3;

            SimplificationStats simplification = SimplifyClosedMesh(
                proxyMesh,
                meshQuality);

            // Surface offset does not change topology, so apply it only after topology-safe
            // simplification has finished.
            if (Mathf.Abs(surfaceOffset) > 1e-8f)
                ApplySurfaceOffset(proxyMesh, surfaceOffset);

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
            {
                material = GetOrCreateVolumeMaterial(
                    materialPath,
                    replaceExisting,
                    fadeDistance,
                    fadeStrength,
                    coreStrength);
            }

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
            proxyRenderer.bones = rendererBones;
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
                Mathf.Abs(surfaceOffset) + fadeDistance);

            EditorUtility.SetDirty(proxyObject);
            AssetDatabase.SaveAssets();

            return new GenerationResult
            {
                ProxyObject = proxyObject,
                ProxyMesh = proxyMesh,
                AssignedMaterial = material,

                SourceVertexCount = sourceVertices.Length,
                SourceTriangleCount = sourceTriangles.Count / 3,
                SelectedBoneCount = selectedBoneCount,
                SelectionVertexCount = selectionVertexCount,
                SelectionTriangleCount = selectionTriangleCount,

                ProxyVertexCount = proxyMesh.vertexCount,
                ProxyTriangleCount = proxyMesh.triangles.Length / 3,

                PreSimplifyTriangleCount = preSimplifyTriangleCount,
                TargetTriangleCount = simplification.TargetTriangleCount,
                SimplificationCollapseCount = simplification.CollapseCount,
                SimplificationAttempted = simplification.Attempted,
                SimplificationReachedTarget = simplification.ReachedTarget,
                SimplificationStopReason = simplification.StopReason,

                BoundaryEdgesBeforeSeal = boundaryBeforeSeal,
                BoundaryEdgesAfterSeal = boundaryAfterSeal,
                NonManifoldEdgesAfterSeal = nonManifoldAfterSeal,

                SelectionBypassed = allRendererBonesSelected,
                SelectedBoneDiagnostics = BuildSelectedBoneDiagnostics(
                    rendererBones,
                    selectedBoneMask,
                    influencedVertexCountPerBone,
                    sourceVertices.Length)
            };
        }

        internal static bool DeleteGeneratedProxy(
            SkinnedMeshRenderer source,
            string outputFolder)
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

        private static bool[] BuildSelectedBoneMask(
            IReadOnlyList<Transform> rendererBones,
            IReadOnlyList<Transform> selectedBones,
            out int selectedBoneCount,
            out bool allRendererBonesSelected)
        {
            var mask = new bool[rendererBones.Count];
            selectedBoneCount = 0;

            if (selectedBones != null)
            {
                var selectedSet = new HashSet<Transform>(
                    selectedBones.Where(bone => bone != null));

                for (int i = 0; i < rendererBones.Count; i++)
                {
                    Transform bone = rendererBones[i];
                    if (bone != null && selectedSet.Contains(bone))
                    {
                        mask[i] = true;
                        selectedBoneCount++;
                    }
                }

                foreach (Transform selected in selectedSet)
                {
                    bool found = false;
                    for (int i = 0; i < rendererBones.Count; i++)
                    {
                        if (rendererBones[i] == selected)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        throw new InvalidOperationException(
                            $"Selected bone '{selected.name}' is not used by the source renderer.");
                    }
                }
            }

            int selectableBoneCount = 0;
            allRendererBonesSelected = true;

            for (int i = 0; i < rendererBones.Count; i++)
            {
                if (rendererBones[i] == null)
                    continue;

                selectableBoneCount++;
                if (!mask[i])
                    allRendererBonesSelected = false;
            }

            if (selectableBoneCount == 0)
                allRendererBonesSelected = false;

            return mask;
        }

        private static List<SelectedBoneDiagnostic> BuildSelectedBoneDiagnostics(
            IReadOnlyList<Transform> rendererBones,
            IReadOnlyList<bool> selectedBoneMask,
            IReadOnlyList<int> influencedVertexCountPerBone,
            int sourceVertexCount)
        {
            var result = new List<SelectedBoneDiagnostic>();

            for (int i = 0; i < rendererBones.Count; i++)
            {
                if (!selectedBoneMask[i])
                    continue;

                result.Add(new SelectedBoneDiagnostic
                {
                    BoneName = rendererBones[i] != null
                        ? rendererBones[i].name
                        : "<null>",
                    BoneIndex = i,
                    InfluencedVertexCount =
                        i < influencedVertexCountPerBone.Count
                            ? influencedVertexCountPerBone[i]
                            : 0,
                    SourceVertexCount = sourceVertexCount
                });
            }

            return result;
        }

        private static int CountSelectedVertices(
            IReadOnlyList<float> selectionWeights,
            float selectionThreshold)
        {
            int count = 0;

            for (int i = 0; i < selectionWeights.Count; i++)
            {
                if (selectionWeights[i] >= selectionThreshold)
                    count++;
            }

            return count;
        }

        private static void ReadSourceGeometry(
            Mesh mesh,
            out Vector3[] vertices,
            out Vector3[] normals,
            out Vector2[] uvs,
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
        }

        private static void ReadSourceSkinning(
            Mesh mesh,
            int rendererBoneCount,
            IReadOnlyList<bool> selectedBoneMask,
            out BoneWeight[] fourWeights,
            out float[] selectionWeights,
            out int[] influencedVertexCountPerBone)
        {
            NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
            NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();

            if (bonesPerVertex.Length != mesh.vertexCount)
            {
                throw new InvalidOperationException(
                    "Source mesh bone-weight count does not match vertex count.");
            }

            fourWeights = new BoneWeight[mesh.vertexCount];
            selectionWeights = new float[mesh.vertexCount];
            influencedVertexCountPerBone = new int[rendererBoneCount];

            int cursor = 0;

            for (int vertexIndex = 0; vertexIndex < mesh.vertexCount; vertexIndex++)
            {
                int influenceCount = bonesPerVertex[vertexIndex];
                float selectedWeight = 0f;

                int b0 = 0;
                int b1 = 0;
                int b2 = 0;
                int b3 = 0;
                float w0 = 0f;
                float w1 = 0f;
                float w2 = 0f;
                float w3 = 0f;
                float topFourTotal = 0f;

                for (int influenceIndex = 0; influenceIndex < influenceCount; influenceIndex++)
                {
                    if (cursor >= allWeights.Length)
                    {
                        throw new InvalidOperationException(
                            "Source mesh bone-weight stream ended unexpectedly.");
                    }

                    BoneWeight1 influence = allWeights[cursor++];
                    int boneIndex = influence.boneIndex;
                    float weight = influence.weight;

                    if (boneIndex < 0 || boneIndex >= rendererBoneCount)
                    {
                        throw new InvalidOperationException(
                            $"Source mesh references bone index {boneIndex}, " +
                            $"but renderer has only {rendererBoneCount} bones.");
                    }

                    if (weight > 0f)
                        influencedVertexCountPerBone[boneIndex]++;

                    if (selectedBoneMask[boneIndex])
                        selectedWeight += weight;

                    if (influenceIndex < 4)
                    {
                        switch (influenceIndex)
                        {
                            case 0:
                                b0 = boneIndex;
                                w0 = weight;
                                break;
                            case 1:
                                b1 = boneIndex;
                                w1 = weight;
                                break;
                            case 2:
                                b2 = boneIndex;
                                w2 = weight;
                                break;
                            case 3:
                                b3 = boneIndex;
                                w3 = weight;
                                break;
                        }

                        topFourTotal += weight;
                    }
                }

                if (influenceCount == 0 || topFourTotal <= 1e-8f)
                {
                    throw new InvalidOperationException(
                        $"Source mesh vertex {vertexIndex} has no usable bone weights.");
                }

                float inv = 1f / topFourTotal;

                fourWeights[vertexIndex] = new BoneWeight
                {
                    boneIndex0 = b0,
                    boneIndex1 = b1,
                    boneIndex2 = b2,
                    boneIndex3 = b3,
                    weight0 = w0 * inv,
                    weight1 = w1 * inv,
                    weight2 = w2 * inv,
                    weight3 = w3 * inv
                };

                selectionWeights[vertexIndex] = Mathf.Clamp01(selectedWeight);
            }

            if (cursor != allWeights.Length)
            {
                throw new InvalidOperationException(
                    "Source mesh bone-weight stream contains unused entries. " +
                    "The skinning data is inconsistent.");
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
                    {
                        result.Add(
                            indices[subMesh.indexStart + i] +
                            subMesh.baseVertex);
                    }
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
                    {
                        result.Add(
                            (int)indices[subMesh.indexStart + i] +
                            subMesh.baseVertex);
                    }
                }
            }

            if (result.Count == 0)
                throw new InvalidOperationException("Source mesh has no triangle submeshes.");

            return result;
        }

        private static void ExtractSelectedGeometry(
            IReadOnlyList<Vector3> sourceVertices,
            IReadOnlyList<Vector3> sourceNormals,
            IReadOnlyList<Vector2> sourceUvs,
            IReadOnlyList<BoneWeight> sourceBoneWeights,
            IReadOnlyList<float> sourceSelectionWeights,
            IReadOnlyList<int> sourceTriangles,
            float selectionThreshold,
            bool bypassSelection,
            out List<VertexData> vertices,
            out List<int> triangles)
        {
            vertices = new List<VertexData>(sourceTriangles.Count);
            triangles = new List<int>(sourceTriangles.Count);

            for (int triangleIndex = 0;
                 triangleIndex + 2 < sourceTriangles.Count;
                 triangleIndex += 3)
            {
                var polygon = new List<VertexData>(3)
                {
                    MakeVertex(
                        sourceTriangles[triangleIndex],
                        sourceVertices,
                        sourceNormals,
                        sourceUvs,
                        sourceBoneWeights,
                        sourceSelectionWeights),
                    MakeVertex(
                        sourceTriangles[triangleIndex + 1],
                        sourceVertices,
                        sourceNormals,
                        sourceUvs,
                        sourceBoneWeights,
                        sourceSelectionWeights),
                    MakeVertex(
                        sourceTriangles[triangleIndex + 2],
                        sourceVertices,
                        sourceNormals,
                        sourceUvs,
                        sourceBoneWeights,
                        sourceSelectionWeights)
                };

                if (!bypassSelection)
                    polygon = ClipPolygonBySelectionWeight(polygon, selectionThreshold);

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
            IReadOnlyList<BoneWeight> boneWeights,
            IReadOnlyList<float> selectionWeights)
        {
            return new VertexData
            {
                Position = positions[index],
                Normal = normals[index],
                Uv = uvs[index],
                BoneWeight = boneWeights[index],
                SelectionWeight = selectionWeights[index]
            };
        }

        private static List<VertexData> ClipPolygonBySelectionWeight(
            IReadOnlyList<VertexData> input,
            float threshold)
        {
            var output = new List<VertexData>(input.Count + 1);
            if (input.Count == 0)
                return output;

            VertexData previous = input[input.Count - 1];
            float previousDistance = previous.SelectionWeight - threshold;
            bool previousInside = previousDistance >= -1e-6f;

            for (int i = 0; i < input.Count; i++)
            {
                VertexData current = input[i];
                float currentDistance = current.SelectionWeight - threshold;
                bool currentInside = currentDistance >= -1e-6f;

                if (previousInside != currentInside)
                {
                    float denominator = previousDistance - currentDistance;
                    float t = Mathf.Abs(denominator) > 1e-8f
                        ? previousDistance / denominator
                        : 0.5f;

                    output.Add(
                        Interpolate(
                            previous,
                            current,
                            Mathf.Clamp01(t)));
                }

                if (currentInside)
                    output.Add(current);

                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }

            return output;
        }

        private static VertexData Interpolate(
            VertexData a,
            VertexData b,
            float t)
        {
            Vector3 normal = Vector3.Lerp(a.Normal, b.Normal, t);
            normal = normal.sqrMagnitude > 1e-12f
                ? normal.normalized
                : Vector3.up;

            return new VertexData
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Normal = normal,
                Uv = Vector2.Lerp(a.Uv, b.Uv, t),
                BoneWeight = LerpBoneWeight(a.BoneWeight, b.BoneWeight, t),
                SelectionWeight = Mathf.Lerp(a.SelectionWeight, b.SelectionWeight, t)
            };
        }

        private static Mesh BuildProxy(
            Mesh sourceMesh,
            IReadOnlyList<VertexData> sourceVertices,
            IReadOnlyList<int> sourceTriangles)
        {
            // Only weld practically coincident vertices here. Coarser positional clustering
            // used in 0.0.5 could create holes/non-manifold edges and break stencil parity.
            float cellSize =
                Mathf.Max(
                    1e-7f,
                    sourceMesh.bounds.size.magnitude * 1e-7f);

            var lookup = new Dictionary<GridKey, int>();
            var clusters = new List<Cluster>();
            var remap = new int[sourceVertices.Count];

            for (int i = 0; i < sourceVertices.Count; i++)
            {
                GridKey key = new GridKey(
                    sourceVertices[i].Position,
                    cellSize);

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

                Vector3 normal =
                    cluster.NormalSum.sqrMagnitude > 1e-12f
                        ? cluster.NormalSum.normalized
                        : Vector3.up;

                vertices.Add(
                    cluster.PositionSum * invCount);

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

                Vector3 cross =
                    Vector3.Cross(
                        vertices[b] - vertices[a],
                        vertices[c] - vertices[a]);

                if (cross.sqrMagnitude < 1e-12f)
                    continue;

                if (!seen.Add(new TriangleKey(a, b, c)))
                    continue;

                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }

            if (triangles.Count == 0)
            {
                throw new InvalidOperationException(
                    "Proxy generation produced no triangles. Broaden the bone selection.");
            }

            var mesh = new Mesh
            {
                name = sourceMesh.name + ProxySuffix,
                indexFormat =
                    vertices.Count > 65535
                        ? IndexFormat.UInt32
                        : IndexFormat.UInt16
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


        private sealed class SimplificationStats
        {
            public bool Attempted;
            public bool ReachedTarget;
            public int TargetTriangleCount;
            public int CollapseCount;
            public string StopReason;
        }

        private sealed class CollapseEdge
        {
            public int A;
            public int B;
            public int Count;
            public int Opposite0 = -1;
            public int Opposite1 = -1;
        }

        private sealed class CollapseProposal
        {
            public int Keep;
            public int Remove;
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv;
            public BoneWeight BoneWeight;
        }

        private readonly struct CollapseCandidate
        {
            public readonly int A;
            public readonly int B;
            public readonly float Cost;

            public CollapseCandidate(int a, int b, float cost)
            {
                A = a;
                B = b;
                Cost = cost;
            }
        }

        private static SimplificationStats SimplifyClosedMesh(
            Mesh mesh,
            float meshQuality)
        {
            int startTriangleCount = mesh.triangles.Length / 3;
            int targetTriangleCount = Mathf.Max(
                4,
                Mathf.RoundToInt(startTriangleCount * Mathf.Clamp(meshQuality, 0.1f, 1f)));

            var stats = new SimplificationStats
            {
                Attempted = meshQuality < 0.9999f,
                ReachedTarget = startTriangleCount <= targetTriangleCount,
                TargetTriangleCount = targetTriangleCount,
                CollapseCount = 0,
                StopReason = string.Empty
            };

            if (!stats.Attempted || stats.ReachedTarget)
            {
                stats.ReachedTarget = true;
                return stats;
            }

            int[] initialTriangles = mesh.triangles;
            if (!IsClosedTwoManifold(initialTriangles, out string initialReason))
            {
                stats.StopReason =
                    "Topology-safe simplification skipped: " + initialReason;
                return stats;
            }

            var vertices = mesh.vertices.ToList();
            var normals =
                mesh.normals != null && mesh.normals.Length == mesh.vertexCount
                    ? mesh.normals.ToList()
                    : Enumerable.Repeat(Vector3.up, mesh.vertexCount).ToList();

            var uvs =
                mesh.uv != null && mesh.uv.Length == mesh.vertexCount
                    ? mesh.uv.ToList()
                    : Enumerable.Repeat(Vector2.zero, mesh.vertexCount).ToList();

            var weights =
                mesh.boneWeights != null && mesh.boneWeights.Length == mesh.vertexCount
                    ? mesh.boneWeights.ToList()
                    : Enumerable.Repeat(
                        new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                        mesh.vertexCount).ToList();

            var triangles = initialTriangles.ToList();
            var active = Enumerable.Repeat(true, vertices.Count).ToArray();

            int passGuard = 0;

            while (triangles.Count / 3 > targetTriangleCount && passGuard++ < 512)
            {
                BuildCollapseTopology(
                    triangles,
                    vertices.Count,
                    out Dictionary<EdgeKey, CollapseEdge> edges,
                    out HashSet<int>[] neighbors,
                    out List<int>[] incidentTriangles);

                if (edges.Values.Any(edge => edge.Count != 2))
                {
                    stats.StopReason =
                        "Simplification stopped because the mesh ceased to be a closed 2-manifold.";
                    break;
                }

                var candidates = new List<CollapseCandidate>(edges.Count);

                foreach (CollapseEdge edge in edges.Values)
                {
                    if (!active[edge.A] || !active[edge.B] || edge.Count != 2)
                        continue;

                    float lengthCost =
                        (vertices[edge.A] - vertices[edge.B]).sqrMagnitude;

                    float normalDot =
                        Mathf.Clamp(
                            Vector3.Dot(
                                normals[edge.A].normalized,
                                normals[edge.B].normalized),
                            -1f,
                            1f);

                    float normalPenalty =
                        1f + (1f - normalDot) * 4f;

                    float skinPenalty =
                        1f + BoneWeightDistance(
                            weights[edge.A],
                            weights[edge.B]) * 3f;

                    candidates.Add(
                        new CollapseCandidate(
                            edge.A,
                            edge.B,
                            lengthCost * normalPenalty * skinPenalty));
                }

                candidates.Sort(
                    (left, right) => left.Cost.CompareTo(right.Cost));

                var locked = new bool[vertices.Count];
                var proposals = new List<CollapseProposal>();

                int remainingToRemove =
                    triangles.Count / 3 - targetTriangleCount;

                foreach (CollapseCandidate candidate in candidates)
                {
                    if (remainingToRemove <= 0)
                        break;

                    int a = candidate.A;
                    int b = candidate.B;

                    if (!active[a] || !active[b] || locked[a] || locked[b])
                        continue;

                    EdgeKey key = new EdgeKey(a, b);

                    if (!edges.TryGetValue(key, out CollapseEdge edge) ||
                        edge.Count != 2)
                    {
                        continue;
                    }

                    if (!PassesLinkCondition(
                        a,
                        b,
                        edge,
                        neighbors))
                    {
                        continue;
                    }

                    Vector3 newPosition =
                        (vertices[a] + vertices[b]) * 0.5f;

                    if (!PassesCollapseGeometryCheck(
                        a,
                        b,
                        newPosition,
                        vertices,
                        triangles,
                        incidentTriangles))
                    {
                        continue;
                    }

                    int keep = Mathf.Min(a, b);
                    int remove = Mathf.Max(a, b);

                    Vector3 mergedNormal =
                        normals[a] + normals[b];

                    if (mergedNormal.sqrMagnitude > 1e-12f)
                        mergedNormal.Normalize();
                    else
                        mergedNormal = normals[keep];

                    proposals.Add(
                        new CollapseProposal
                        {
                            Keep = keep,
                            Remove = remove,
                            Position = newPosition,
                            Normal = mergedNormal,
                            Uv = (uvs[a] + uvs[b]) * 0.5f,
                            BoneWeight = LerpBoneWeight(
                                weights[a],
                                weights[b],
                                0.5f)
                        });

                    LockOneRing(locked, a, neighbors);
                    LockOneRing(locked, b, neighbors);

                    // A valid interior edge collapse on a closed triangular 2-manifold
                    // removes the two incident triangles.
                    remainingToRemove -= 2;
                }

                if (proposals.Count == 0)
                {
                    stats.StopReason =
                        "Topology/normal constraints prevent further safe edge collapses.";
                    break;
                }

                List<int> candidateTriangles =
                    ApplyCollapseProposalsToTriangles(
                        triangles,
                        proposals);

                if (candidateTriangles.Count >= triangles.Count)
                {
                    stats.StopReason =
                        "No further triangle reduction was produced by safe collapses.";
                    break;
                }

                if (!IsClosedTwoManifold(
                    candidateTriangles,
                    out string validationReason))
                {
                    // Proposals are one-ring isolated, so failure here means this pass found
                    // a pathological configuration. Roll the whole pass back rather than
                    // accepting a leaky proxy.
                    stats.StopReason =
                        "A simplification pass was rolled back: " +
                        validationReason;
                    break;
                }

                foreach (CollapseProposal proposal in proposals)
                {
                    vertices[proposal.Keep] = proposal.Position;
                    normals[proposal.Keep] = proposal.Normal;
                    uvs[proposal.Keep] = proposal.Uv;
                    weights[proposal.Keep] = proposal.BoneWeight;
                    active[proposal.Remove] = false;
                    stats.CollapseCount++;
                }

                triangles = candidateTriangles;
            }

            CompactSimplifiedMesh(
                mesh,
                vertices,
                normals,
                uvs,
                weights,
                active,
                triangles);

            int finalTriangleCount =
                mesh.triangles.Length / 3;

            stats.ReachedTarget =
                finalTriangleCount <= targetTriangleCount;

            if (!stats.ReachedTarget &&
                string.IsNullOrEmpty(stats.StopReason))
            {
                stats.StopReason =
                    "Topology-safe simplification reached its conservative limit.";
            }

            return stats;
        }

        private static void BuildCollapseTopology(
            IReadOnlyList<int> triangles,
            int vertexCount,
            out Dictionary<EdgeKey, CollapseEdge> edges,
            out HashSet<int>[] neighbors,
            out List<int>[] incidentTriangles)
        {
            edges = new Dictionary<EdgeKey, CollapseEdge>();
            neighbors = new HashSet<int>[vertexCount];
            incidentTriangles = new List<int>[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                neighbors[i] = new HashSet<int>();
                incidentTriangles[i] = new List<int>();
            }

            for (int triangleStart = 0;
                 triangleStart + 2 < triangles.Count;
                 triangleStart += 3)
            {
                int a = triangles[triangleStart];
                int b = triangles[triangleStart + 1];
                int c = triangles[triangleStart + 2];

                incidentTriangles[a].Add(triangleStart);
                incidentTriangles[b].Add(triangleStart);
                incidentTriangles[c].Add(triangleStart);

                neighbors[a].Add(b);
                neighbors[a].Add(c);
                neighbors[b].Add(a);
                neighbors[b].Add(c);
                neighbors[c].Add(a);
                neighbors[c].Add(b);

                AddCollapseEdge(edges, a, b, c);
                AddCollapseEdge(edges, b, c, a);
                AddCollapseEdge(edges, c, a, b);
            }
        }

        private static void AddCollapseEdge(
            Dictionary<EdgeKey, CollapseEdge> edges,
            int a,
            int b,
            int opposite)
        {
            EdgeKey key = new EdgeKey(a, b);

            if (!edges.TryGetValue(key, out CollapseEdge edge))
            {
                edge = new CollapseEdge
                {
                    A = key.A,
                    B = key.B,
                    Count = 0
                };

                edges.Add(key, edge);
            }

            if (edge.Count == 0)
                edge.Opposite0 = opposite;
            else if (edge.Count == 1)
                edge.Opposite1 = opposite;

            edge.Count++;
        }

        private static bool PassesLinkCondition(
            int a,
            int b,
            CollapseEdge edge,
            IReadOnlyList<HashSet<int>> neighbors)
        {
            int commonCount = 0;

            foreach (int candidate in neighbors[a])
            {
                if (!neighbors[b].Contains(candidate))
                    continue;

                if (candidate != edge.Opposite0 &&
                    candidate != edge.Opposite1)
                {
                    return false;
                }

                commonCount++;
            }

            return commonCount == 2 &&
                   edge.Opposite0 >= 0 &&
                   edge.Opposite1 >= 0 &&
                   edge.Opposite0 != edge.Opposite1;
        }

        private static bool PassesCollapseGeometryCheck(
            int a,
            int b,
            Vector3 newPosition,
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> triangles,
            IReadOnlyList<List<int>> incidentTriangles)
        {
            var affected =
                new HashSet<int>(
                    incidentTriangles[a]);

            foreach (int triangleStart in incidentTriangles[b])
                affected.Add(triangleStart);

            foreach (int triangleStart in affected)
            {
                int ia = triangles[triangleStart];
                int ib = triangles[triangleStart + 1];
                int ic = triangles[triangleStart + 2];

                bool hasA =
                    ia == a || ib == a || ic == a;

                bool hasB =
                    ia == b || ib == b || ic == b;

                // The two triangles incident to the collapsing edge disappear.
                if (hasA && hasB)
                    continue;

                Vector3 oldA = vertices[ia];
                Vector3 oldB = vertices[ib];
                Vector3 oldC = vertices[ic];

                Vector3 nextA =
                    (ia == a || ia == b) ? newPosition : oldA;
                Vector3 nextB =
                    (ib == a || ib == b) ? newPosition : oldB;
                Vector3 nextC =
                    (ic == a || ic == b) ? newPosition : oldC;

                Vector3 oldCross =
                    Vector3.Cross(
                        oldB - oldA,
                        oldC - oldA);

                Vector3 newCross =
                    Vector3.Cross(
                        nextB - nextA,
                        nextC - nextA);

                if (oldCross.sqrMagnitude < 1e-16f ||
                    newCross.sqrMagnitude < 1e-16f)
                {
                    return false;
                }

                float orientation =
                    Vector3.Dot(
                        oldCross.normalized,
                        newCross.normalized);

                // Reject flips and very severe local folding.
                if (orientation < 0.15f)
                    return false;
            }

            return true;
        }

        private static void LockOneRing(
            bool[] locked,
            int vertex,
            IReadOnlyList<HashSet<int>> neighbors)
        {
            locked[vertex] = true;

            foreach (int neighbor in neighbors[vertex])
                locked[neighbor] = true;
        }

        private static List<int> ApplyCollapseProposalsToTriangles(
            IReadOnlyList<int> triangles,
            IReadOnlyList<CollapseProposal> proposals)
        {
            var remap =
                new Dictionary<int, int>();

            foreach (CollapseProposal proposal in proposals)
                remap[proposal.Remove] = proposal.Keep;

            var result =
                new List<int>(triangles.Count);

            var seen =
                new HashSet<TriangleKey>();

            for (int i = 0;
                 i + 2 < triangles.Count;
                 i += 3)
            {
                int a = ResolveCollapseIndex(triangles[i], remap);
                int b = ResolveCollapseIndex(triangles[i + 1], remap);
                int c = ResolveCollapseIndex(triangles[i + 2], remap);

                if (a == b || b == c || c == a)
                    continue;

                TriangleKey key =
                    new TriangleKey(a, b, c);

                if (!seen.Add(key))
                    continue;

                result.Add(a);
                result.Add(b);
                result.Add(c);
            }

            return result;
        }

        private static int ResolveCollapseIndex(
            int index,
            IReadOnlyDictionary<int, int> remap)
        {
            int current = index;
            int guard = 0;

            while (remap.TryGetValue(current, out int next) &&
                   next != current &&
                   guard++ < 16)
            {
                current = next;
            }

            return current;
        }

        private static bool IsClosedTwoManifold(
            IReadOnlyList<int> triangles,
            out string reason)
        {
            Dictionary<EdgeKey, EdgeInfo> edges =
                BuildEdgeTable(triangles);

            int boundaryCount =
                edges.Count(pair => pair.Value.Count == 1);

            if (boundaryCount > 0)
            {
                reason =
                    $"{boundaryCount} boundary edge(s) remain.";
                return false;
            }

            int nonManifoldCount =
                edges.Count(pair => pair.Value.Count > 2);

            if (nonManifoldCount > 0)
            {
                reason =
                    $"{nonManifoldCount} non-manifold edge(s) remain.";
                return false;
            }

            if (edges.Count == 0)
            {
                reason =
                    "The mesh contains no usable edges.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static float BoneWeightDistance(
            BoneWeight a,
            BoneWeight b)
        {
            var values =
                new Dictionary<int, float>();

            AddSignedInfluence(values, a.boneIndex0, a.weight0);
            AddSignedInfluence(values, a.boneIndex1, a.weight1);
            AddSignedInfluence(values, a.boneIndex2, a.weight2);
            AddSignedInfluence(values, a.boneIndex3, a.weight3);

            AddSignedInfluence(values, b.boneIndex0, -b.weight0);
            AddSignedInfluence(values, b.boneIndex1, -b.weight1);
            AddSignedInfluence(values, b.boneIndex2, -b.weight2);
            AddSignedInfluence(values, b.boneIndex3, -b.weight3);

            float distance = 0f;

            foreach (float value in values.Values)
                distance += Mathf.Abs(value);

            return Mathf.Clamp01(distance * 0.5f);
        }

        private static void AddSignedInfluence(
            Dictionary<int, float> target,
            int boneIndex,
            float weight)
        {
            if (Mathf.Abs(weight) <= 1e-8f)
                return;

            if (target.TryGetValue(boneIndex, out float current))
                target[boneIndex] = current + weight;
            else
                target.Add(boneIndex, weight);
        }

        private static void CompactSimplifiedMesh(
            Mesh mesh,
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<Vector3> normals,
            IReadOnlyList<Vector2> uvs,
            IReadOnlyList<BoneWeight> weights,
            IReadOnlyList<bool> active,
            IReadOnlyList<int> triangles)
        {
            var used =
                new bool[vertices.Count];

            foreach (int index in triangles)
                used[index] = true;

            var remap =
                Enumerable.Repeat(-1, vertices.Count).ToArray();

            var compactVertices = new List<Vector3>();
            var compactNormals = new List<Vector3>();
            var compactUvs = new List<Vector2>();
            var compactWeights = new List<BoneWeight>();

            for (int i = 0; i < vertices.Count; i++)
            {
                if (!active[i] || !used[i])
                    continue;

                remap[i] = compactVertices.Count;
                compactVertices.Add(vertices[i]);
                compactNormals.Add(normals[i]);
                compactUvs.Add(uvs[i]);
                compactWeights.Add(weights[i]);
            }

            var compactTriangles =
                new List<int>(triangles.Count);

            foreach (int oldIndex in triangles)
            {
                int newIndex = remap[oldIndex];

                if (newIndex < 0)
                {
                    throw new InvalidOperationException(
                        "Simplifier produced an invalid vertex remap.");
                }

                compactTriangles.Add(newIndex);
            }

            mesh.Clear();
            mesh.indexFormat =
                compactVertices.Count > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;

            mesh.SetVertices(compactVertices);
            mesh.SetNormals(compactNormals);
            mesh.SetUVs(0, compactUvs);
            mesh.boneWeights = compactWeights.ToArray();
            mesh.SetTriangles(compactTriangles, 0, true);
            mesh.RecalculateBounds();
        }

        private static void ApplySurfaceOffset(
            Mesh mesh,
            float surfaceOffset)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;

            if (normals == null ||
                normals.Length != vertices.Length)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 normal =
                    normals[i].sqrMagnitude > 1e-12f
                        ? normals[i].normalized
                        : Vector3.up;

                vertices[i] +=
                    normal * surfaceOffset;
            }

            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        private static Dictionary<EdgeKey, EdgeInfo> BuildEdgeTable(
            IReadOnlyList<int> triangles)
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
                edges.Add(
                    key,
                    new EdgeInfo
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

            var uvs =
                mesh.uv != null && mesh.uv.Length == mesh.vertexCount
                    ? mesh.uv.ToList()
                    : Enumerable.Repeat(Vector2.zero, mesh.vertexCount).ToList();

            var weights = mesh.boneWeights.ToList();
            var triangles = mesh.triangles.ToList();

            Dictionary<EdgeKey, EdgeInfo> edgeTable = BuildEdgeTable(triangles);

            List<EdgeInfo> boundary =
                edgeTable.Values
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

                var loop = new List<int>
                {
                    first.From,
                    first.To
                };

                used.Add(firstKey);

                int start = first.From;
                int previous = first.From;
                int current = first.To;
                bool closed = false;

                for (int guard = 0;
                     guard < boundary.Count + 2;
                     guard++)
                {
                    if (!adjacency.TryGetValue(
                        current,
                        out List<EdgeInfo> connected))
                    {
                        break;
                    }

                    EdgeInfo nextEdge = null;
                    int nextVertex = -1;

                    foreach (EdgeInfo candidate in connected)
                    {
                        EdgeKey candidateKey =
                            new EdgeKey(candidate.From, candidate.To);

                        if (used.Contains(candidateKey))
                            continue;

                        int candidateNext =
                            candidate.From == current
                                ? candidate.To
                                : candidate.From;

                        if (candidateNext == previous &&
                            connected.Count > 1)
                        {
                            continue;
                        }

                        nextEdge = candidate;
                        nextVertex = candidateNext;
                        break;
                    }

                    if (nextEdge == null)
                        break;

                    used.Add(
                        new EdgeKey(
                            nextEdge.From,
                            nextEdge.To));

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
                var accumulatedWeights =
                    new Dictionary<int, float>();

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
                    Vector3 a =
                        vertices[loop[i]] -
                        center;

                    Vector3 b =
                        vertices[loop[(i + 1) % loop.Count]] -
                        center;

                    loopNormal += Vector3.Cross(a, b);
                }

                Vector3 capNormal =
                    loopNormal.sqrMagnitude > 1e-12f
                        ? -loopNormal.normalized
                        : Vector3.up;

                int centerIndex = vertices.Count;

                vertices.Add(center);
                normals.Add(capNormal);
                uvs.Add(uvCenter);
                weights.Add(
                    BuildBoneWeight(accumulatedWeights));

                for (int i = 0; i < loop.Count; i++)
                {
                    int currentIndex = loop[i];
                    int nextIndex =
                        loop[(i + 1) % loop.Count];

                    triangles.Add(nextIndex);
                    triangles.Add(currentIndex);
                    triangles.Add(centerIndex);
                }
            }

            mesh.indexFormat =
                vertices.Count > 65535
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
            if (!adjacency.TryGetValue(
                vertex,
                out List<EdgeInfo> list))
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
            var accumulated =
                new Dictionary<int, float>();

            AccumulateBoneWeight(
                accumulated,
                a,
                1f - t);

            AccumulateBoneWeight(
                accumulated,
                b,
                t);

            return BuildBoneWeight(accumulated);
        }

        private static void AccumulateBoneWeight(
            Dictionary<int, float> target,
            BoneWeight weight,
            float multiplier)
        {
            AddInfluence(
                target,
                weight.boneIndex0,
                weight.weight0 * multiplier);

            AddInfluence(
                target,
                weight.boneIndex1,
                weight.weight1 * multiplier);

            AddInfluence(
                target,
                weight.boneIndex2,
                weight.weight2 * multiplier);

            AddInfluence(
                target,
                weight.boneIndex3,
                weight.weight3 * multiplier);
        }

        private static void AddInfluence(
            Dictionary<int, float> target,
            int boneIndex,
            float weight)
        {
            if (weight <= 0f)
                return;

            if (target.TryGetValue(
                boneIndex,
                out float current))
            {
                target[boneIndex] =
                    current + weight;
            }
            else
            {
                target.Add(
                    boneIndex,
                    weight);
            }
        }

        private static BoneWeight BuildBoneWeight(
            Dictionary<int, float> accumulated)
        {
            if (accumulated == null ||
                accumulated.Count == 0)
            {
                return new BoneWeight
                {
                    boneIndex0 = 0,
                    weight0 = 1f
                };
            }

            KeyValuePair<int, float>[] top =
                accumulated
                    .Where(pair => pair.Value > 0f)
                    .OrderByDescending(pair => pair.Value)
                    .Take(4)
                    .ToArray();

            float total =
                top.Sum(pair => pair.Value);

            if (total <= 1e-8f)
            {
                return new BoneWeight
                {
                    boneIndex0 = 0,
                    weight0 = 1f
                };
            }

            BoneWeight result = default;

            for (int i = 0; i < top.Length; i++)
            {
                float normalized =
                    top[i].Value / total;

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

        private static Vector3[] CalculateNormals(
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> triangles)
        {
            var result =
                new Vector3[vertices.Count];

            for (int i = 0;
                 i + 2 < triangles.Count;
                 i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];

                Vector3 normal =
                    Vector3.Cross(
                        vertices[b] - vertices[a],
                        vertices[c] - vertices[a]);

                result[a] += normal;
                result[b] += normal;
                result[c] += normal;
            }

            for (int i = 0;
                 i < result.Length;
                 i++)
            {
                result[i] =
                    result[i].sqrMagnitude > 1e-12f
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
            Material existing =
                AssetDatabase.LoadAssetAtPath<Material>(path);

            if (existing != null &&
                !replaceExisting)
            {
                return existing;
            }

            if (existing != null)
                AssetDatabase.DeleteAsset(path);

            Shader shader =
                Shader.Find(ShaderName);

            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            if (shader == null)
            {
                throw new InvalidOperationException(
                    "NearShaderProxyVolume shader was not found and no fallback shader is available.");
            }

            var material =
                new Material(shader)
                {
                    name =
                        Path.GetFileNameWithoutExtension(path)
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

            AssetDatabase.CreateAsset(
                material,
                path);

            return material;
        }

        private static Bounds ExpandBounds(
            Bounds source,
            float margin)
        {
            source.Expand(
                Mathf.Max(0f, margin) * 2f);

            return source;
        }

        private static void EnsureAssetFolder(
            string folder)
        {
            folder =
                folder
                    .Replace('\\', '/')
                    .TrimEnd('/');

            if (AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts =
                folder.Split('/');

            if (parts.Length == 0 ||
                parts[0] != "Assets")
            {
                throw new ArgumentException(
                    "Output folder must be under Assets/.",
                    nameof(folder));
            }

            string current = "Assets";

            for (int i = 1;
                 i < parts.Length;
                 i++)
            {
                string next =
                    current + "/" + parts[i];

                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(
                        current,
                        parts[i]);
                }

                current = next;
            }
        }

        private static string MakeSafeFileName(
            string name)
        {
            foreach (char invalid in
                     Path.GetInvalidFileNameChars())
            {
                name =
                    name.Replace(
                        invalid,
                        '_');
            }

            return name;
        }
    }
}
