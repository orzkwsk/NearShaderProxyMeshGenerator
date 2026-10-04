// NearShaderProxyMeshGenerator
// Prototype version: 0.0.4

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace orz_Shop.NearShaderProxyMeshGenerator
{
    internal sealed class orz_Shop_NearShaderProxyVolumeGeneratorWindow : EditorWindow
    {
        private const string DefaultOutputFolder = "Assets/Generated/NearShaderProxyMesh";
        private const int CurrentUiVersion = 4;

        [SerializeField] private int _uiVersion = 0;
        [SerializeField] private SkinnedMeshRenderer _source;
        [SerializeField] private Material _material;
        [SerializeField] private List<Transform> _cutoffBones = new List<Transform>();
        [SerializeField] private float _boneCutBias = 0f;
        [SerializeField] private float _mergeSize = 0f;
        [SerializeField] private float _surfaceOffset = 0f;
        [SerializeField] private bool _sealOpenBoundaries = true;
        [SerializeField] private float _fadeDistance = 0.05f;
        [SerializeField] private float _fadeStrength = 0.2f;
        [SerializeField] private float _coreStrength = 1f;
        [SerializeField] private string _outputFolder = DefaultOutputFolder;
        [SerializeField] private bool _replaceExisting = true;

        private Vector2 _scroll;
        private orz_Shop_NearShaderProxyVolumeGenerator.GenerationResult _lastResult;

        [MenuItem("orz_Shop/Near Shader Proxy Volume Generator")]
        private static void Open()
        {
            var window = GetWindow<orz_Shop_NearShaderProxyVolumeGeneratorWindow>();
            window.titleContent = new GUIContent("Near Shader Proxy Volume");
            window.minSize = new Vector2(480f, 660f);
            window.Show();
        }

        private void OnEnable()
        {
            if (_uiVersion >= CurrentUiVersion)
                return;

            _boneCutBias = 0f;
            _mergeSize = 0f;
            _surfaceOffset = 0f;
            _uiVersion = CurrentUiVersion;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Near Shader Proxy Volume Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Prototype 0.0.4: selected bones are terminal boundaries. " +
                "The selected bone itself is kept; geometry weighted toward its descendant bones is removed, " +
                "then open boundaries are sealed for the volume shader.",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            DrawSourceSection();
            EditorGUILayout.Space(10f);
            DrawCutoffSection();
            EditorGUILayout.Space(10f);
            DrawGeometrySection();
            EditorGUILayout.Space(10f);
            DrawShaderSection();
            EditorGUILayout.Space(10f);
            DrawOutputSection();
            EditorGUILayout.Space(12f);
            DrawActions();
            EditorGUILayout.Space(10f);
            DrawLastResult();

            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

            _source = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("Body Mesh", "Source body SkinnedMeshRenderer."),
                _source,
                typeof(SkinnedMeshRenderer),
                true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Use Selection", GUILayout.Width(120f)))
                {
                    GameObject selected = Selection.activeGameObject;
                    if (selected != null)
                    {
                        _source = selected.GetComponent<SkinnedMeshRenderer>();
                        if (_source == null)
                            _source = selected.GetComponentInChildren<SkinnedMeshRenderer>();
                    }
                }
            }

            if (_source != null && _source.sharedMesh != null)
            {
                Mesh mesh = _source.sharedMesh;
                EditorGUILayout.LabelField("Mesh", mesh.name);
                EditorGUILayout.LabelField("Source Vertices", mesh.vertexCount.ToString("N0"));
                EditorGUILayout.LabelField("Bones", (_source.bones?.Length ?? 0).ToString("N0"));
            }
        }

        private void DrawCutoffSection()
        {
            EditorGUILayout.LabelField("Bone Boundary", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "A selected bone means \"keep through this bone, remove its descendant-bone side\". " +
                "Examples: Neck removes Head-side weighting; Wrist removes finger-side weighting; " +
                "UpperArm removes LowerArm/Hand-side weighting. This is BoneWeight-based, not an infinite spatial plane.",
                MessageType.None);

            if (_cutoffBones == null)
                _cutoffBones = new List<Transform>();

            for (int i = 0; i < _cutoffBones.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _cutoffBones[i] = (Transform)EditorGUILayout.ObjectField(
                        $"Boundary Bone {i + 1}",
                        _cutoffBones[i],
                        typeof(Transform),
                        true);

                    if (GUILayout.Button("-", GUILayout.Width(28f)))
                    {
                        _cutoffBones.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }

                if (_source != null &&
                    _cutoffBones[i] != null &&
                    Array.IndexOf(_source.bones, _cutoffBones[i]) < 0)
                {
                    EditorGUILayout.HelpBox(
                        $"'{_cutoffBones[i].name}' is not a bone used by the selected renderer.",
                        MessageType.Error);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Boundary Bone"))
                    _cutoffBones.Add(null);

                using (new EditorGUI.DisabledScope(Selection.activeTransform == null))
                {
                    if (GUILayout.Button("Use Selected Bone (Replace)"))
                    {
                        _cutoffBones.Clear();
                        _cutoffBones.Add(Selection.activeTransform);
                    }
                }

                using (new EditorGUI.DisabledScope(Selection.activeTransform == null))
                {
                    if (GUILayout.Button("Add Selected Bone"))
                    {
                        Transform selected = Selection.activeTransform;
                        if (!_cutoffBones.Contains(selected))
                            _cutoffBones.Add(selected);
                    }
                }

                using (new EditorGUI.DisabledScope(_cutoffBones.Count == 0))
                {
                    if (GUILayout.Button("Clear"))
                        _cutoffBones.Clear();
                }
            }

            EditorGUILayout.HelpBox(
                $"Active Boundary Count: {_cutoffBones.Count}. For single-bone tests use 'Use Selected Bone (Replace)' so old boundaries cannot remain active.",
                _cutoffBones.Count > 1 ? MessageType.Warning : MessageType.None);

            _boneCutBias = EditorGUILayout.Slider(
                new GUIContent(
                    "Bone Cut Bias",
                    "Neutral 0 uses 50% descendant-bone weight as the boundary. " +
                    "Positive keeps more distal geometry; negative cuts earlier."),
                _boneCutBias,
                -0.45f,
                0.45f);

            if (GUILayout.Button("Reset Bone Cut Bias", GUILayout.Width(170f)))
                _boneCutBias = 0f;
        }

        private void DrawGeometrySection()
        {
            EditorGUILayout.LabelField("Proxy Geometry", EditorStyles.boldLabel);

            _mergeSize = EditorGUILayout.Slider(
                new GUIContent(
                    "Merge Size",
                    "0 = no intentional low-poly reduction; only epsilon welding is performed. " +
                    "Increase gradually to merge nearby vertices and reduce polygon count."),
                _mergeSize,
                0f,
                0.05f);

            _surfaceOffset = EditorGUILayout.Slider(
                new GUIContent(
                    "Core Surface Offset",
                    "Normal-direction size adjustment for the generated core volume. " +
                    "0 preserves the source surface; negative shrinks; positive expands."),
                _surfaceOffset,
                -0.05f,
                0.05f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset Merge Size"))
                    _mergeSize = 0f;

                if (GUILayout.Button("Reset Surface Offset"))
                    _surfaceOffset = 0f;
            }

            _sealOpenBoundaries = EditorGUILayout.Toggle(
                new GUIContent(
                    "Seal Open Boundaries",
                    "Fill single-use boundary loops. Required for stencil-parity inside/outside detection."),
                _sealOpenBoundaries);

            EditorGUILayout.HelpBox(
                "Defaults are intentionally conservative: Merge Size = 0 and Core Surface Offset = 0. " +
                "First verify correct body coverage, then increase Merge Size only as far as topology remains acceptable.",
                MessageType.None);

            if (_mergeSize > 0.025f)
            {
                EditorGUILayout.HelpBox(
                    "Merge Size above 0.025 can visibly collapse thin limbs or cut boundaries on many avatars.",
                    MessageType.Warning);
            }

            if (!_sealOpenBoundaries)
            {
                EditorGUILayout.HelpBox(
                    "The volume shader expects a closed mesh. Disable sealing only for geometry debugging.",
                    MessageType.Warning);
            }
        }

        private void DrawShaderSection()
        {
            EditorGUILayout.LabelField("Volume Fade Shader", EditorStyles.boldLabel);

            _fadeDistance = EditorGUILayout.Slider(
                new GUIContent(
                    "Fade Distance",
                    "World-space normal expansion used for the outer proximity shells."),
                _fadeDistance,
                0f,
                0.25f);

            _fadeStrength = EditorGUILayout.Slider(
                new GUIContent(
                    "Fade Strength",
                    "Opacity of the outer fade shells before entering the core volume."),
                _fadeStrength,
                0f,
                1f);

            _coreStrength = EditorGUILayout.Slider(
                new GUIContent(
                    "Core Black Strength",
                    "Opacity while the camera is inside the original/core proxy volume."),
                _coreStrength,
                0f,
                1f);

            EditorGUILayout.HelpBox(
                "Outside all shells = no effect; outer/mid shells = light black fade; " +
                "inside the core = full/adjustable blackout.",
                MessageType.None);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            _material = (Material)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Material Override",
                    "Optional. Leave empty to generate a material using orz_Shop/NearShaderProxyVolume."),
                _material,
                typeof(Material),
                false);

            _outputFolder = EditorGUILayout.TextField(
                new GUIContent(
                    "Asset Folder",
                    "Generated Mesh/Material asset folder."),
                _outputFolder);

            _replaceExisting = EditorGUILayout.Toggle(
                new GUIContent(
                    "Replace Existing",
                    "Replace the previously generated proxy volume and mesh asset for this source."),
                _replaceExisting);
        }

        private void DrawActions()
        {
            bool invalidCutoff = HasInvalidCutoffBone();

            bool canGenerate =
                _source != null &&
                _source.sharedMesh != null &&
                _mergeSize >= 0f &&
                _fadeDistance >= 0f &&
                !invalidCutoff;

            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generate Closed Proxy Volume", GUILayout.Height(36f)))
                    Generate();
            }

            using (new EditorGUI.DisabledScope(_source == null))
            {
                if (GUILayout.Button("Delete Generated Proxy Volume"))
                {
                    bool removed =
                        orz_Shop_NearShaderProxyVolumeGenerator.DeleteGeneratedProxy(
                            _source,
                            _outputFolder);

                    if (!removed)
                    {
                        ShowNotification(new GUIContent("No generated proxy volume found."));
                    }
                    else
                    {
                        _lastResult = null;
                        ShowNotification(new GUIContent("Generated proxy volume removed."));
                    }
                }
            }
        }

        private bool HasInvalidCutoffBone()
        {
            if (_source == null || _cutoffBones == null)
                return false;

            Transform[] bones = _source.bones ?? Array.Empty<Transform>();

            foreach (Transform cutoff in _cutoffBones)
            {
                if (cutoff != null && Array.IndexOf(bones, cutoff) < 0)
                    return true;
            }

            return false;
        }

        private void DrawLastResult()
        {
            if (_lastResult == null)
                return;

            EditorGUILayout.LabelField("Last Generation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Vertices",
                $"{_lastResult.SourceVertexCount:N0} -> {_lastResult.ProxyVertexCount:N0}");
            EditorGUILayout.LabelField(
                "Triangles",
                $"{_lastResult.SourceTriangleCount:N0} -> {_lastResult.ProxyTriangleCount:N0}");
            EditorGUILayout.LabelField(
                "Bone Boundaries",
                _lastResult.CutPlaneCount.ToString());

            if (_lastResult.SkippedBoundaryCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{_lastResult.SkippedBoundaryCount} selected boundary bone(s) had no renderer-used descendants and produced no cut.",
                    MessageType.Warning);
            }

            if (_lastResult.BoundaryDiagnostics != null &&
                _lastResult.BoundaryDiagnostics.Count > 0)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Boundary Diagnostics", EditorStyles.boldLabel);

                foreach (var diagnostic in _lastResult.BoundaryDiagnostics)
                {
                    float ratio = diagnostic.SourceVertexCount > 0
                        ? (float)diagnostic.DistalVertexCount / diagnostic.SourceVertexCount
                        : 0f;

                    EditorGUILayout.LabelField(
                        diagnostic.BoneName,
                        $"Index {diagnostic.BoneIndex}, descendants {diagnostic.DescendantBoneCount}, distal vertices {diagnostic.DistalVertexCount:N0} ({ratio:P1})");
                }
            }

            EditorGUILayout.LabelField(
                "Boundary Edges Before Seal",
                _lastResult.BoundaryEdgesBeforeSeal.ToString("N0"));
            EditorGUILayout.LabelField(
                "Boundary Edges After Seal",
                _lastResult.BoundaryEdgesAfterSeal.ToString("N0"));
            EditorGUILayout.LabelField(
                "Non-Manifold Edges After Seal",
                _lastResult.NonManifoldEdgesAfterSeal.ToString("N0"));

            if (_lastResult.BoundaryEdgesAfterSeal == 0 &&
                _lastResult.NonManifoldEdgesAfterSeal == 0)
            {
                EditorGUILayout.HelpBox(
                    "Closed-volume edge check passed.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The generated volume is not cleanly closed/manifold. " +
                    "Keep Merge Size at 0 first, then inspect source topology or boundary selection.",
                    MessageType.Warning);
            }

            if (_lastResult.SourceTriangleCount > 0)
            {
                float ratio =
                    (float)_lastResult.ProxyTriangleCount /
                    _lastResult.SourceTriangleCount;

                EditorGUILayout.LabelField(
                    "Triangle Ratio",
                    $"{ratio:P1}");
            }

            EditorGUILayout.ObjectField(
                "Proxy Object",
                _lastResult.ProxyObject,
                typeof(GameObject),
                true);
            EditorGUILayout.ObjectField(
                "Proxy Mesh",
                _lastResult.ProxyMesh,
                typeof(Mesh),
                false);
            EditorGUILayout.ObjectField(
                "Material",
                _lastResult.AssignedMaterial,
                typeof(Material),
                false);
        }

        private void Generate()
        {
            try
            {
                _lastResult =
                    orz_Shop_NearShaderProxyVolumeGenerator.Generate(
                        _source,
                        _material,
                        _cutoffBones,
                        _boneCutBias,
                        _mergeSize,
                        _surfaceOffset,
                        _sealOpenBoundaries,
                        _fadeDistance,
                        _fadeStrength,
                        _coreStrength,
                        _outputFolder,
                        _replaceExisting);

                Selection.activeGameObject = _lastResult.ProxyObject;
                EditorGUIUtility.PingObject(_lastResult.ProxyObject);
                SceneView.RepaintAll();

                Debug.Log(
                    "[NearShaderProxyMeshGenerator] Closed proxy volume generated. " +
                    $"V {_lastResult.SourceVertexCount} -> {_lastResult.ProxyVertexCount}, " +
                    $"T {_lastResult.SourceTriangleCount} -> {_lastResult.ProxyTriangleCount}, " +
                    $"Boundary {_lastResult.BoundaryEdgesBeforeSeal} -> {_lastResult.BoundaryEdgesAfterSeal}, " +
                    $"NonManifold {_lastResult.NonManifoldEdgesAfterSeal}, " +
                    $"Boundaries {_lastResult.CutPlaneCount}.",
                    _lastResult.ProxyObject);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

                EditorUtility.DisplayDialog(
                    "Near Shader Proxy Volume Generator",
                    ex.Message,
                    "OK");
            }
        }
    }
}
