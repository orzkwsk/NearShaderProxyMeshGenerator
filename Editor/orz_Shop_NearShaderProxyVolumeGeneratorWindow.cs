// NearShaderProxyMeshGenerator
// Prototype version: 0.0.2

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace orz_Shop.NearShaderProxyMeshGenerator
{
    internal sealed class orz_Shop_NearShaderProxyVolumeGeneratorWindow : EditorWindow
    {
        private const string DefaultOutputFolder = "Assets/Generated/NearShaderProxyMesh";

        [SerializeField] private SkinnedMeshRenderer _source;
        [SerializeField] private Material _material;
        [SerializeField] private List<Transform> _cutoffBones = new List<Transform>();
        [SerializeField] private float _cutoffOffset = 0f;
        [SerializeField] private float _clusterCellSize = 0.03f;
        [SerializeField] private float _surfaceOffset = 0.005f;
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
            window.minSize = new Vector2(460f, 620f);
            window.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Near Shader Proxy Volume Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Prototype 0.0.2: builds a closed low-poly skinned volume from a body mesh. " +
                "Optional cutoff bones remove the child-side geometry at each selected joint and the resulting openings are sealed for volume-based proximity rendering.",
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
            EditorGUILayout.LabelField("Bone Cutoff", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each cutoff plane passes through the selected bone in bind pose. " +
                "The parent side is kept and the child/distal side is removed. Multiple bones can be used, e.g. both wrists, ankles, neck, etc.",
                MessageType.None);

            if (_cutoffBones == null)
                _cutoffBones = new List<Transform>();

            for (int i = 0; i < _cutoffBones.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _cutoffBones[i] = (Transform)EditorGUILayout.ObjectField(
                        $"Cutoff Bone {i + 1}",
                        _cutoffBones[i],
                        typeof(Transform),
                        true);

                    if (GUILayout.Button("-", GUILayout.Width(28f)))
                    {
                        _cutoffBones.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }

                if (_source != null && _cutoffBones[i] != null && Array.IndexOf(_source.bones, _cutoffBones[i]) < 0)
                    EditorGUILayout.HelpBox($"'{_cutoffBones[i].name}' is not a bone used by the selected renderer.", MessageType.Error);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Cutoff Bone"))
                    _cutoffBones.Add(null);

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

            _cutoffOffset = EditorGUILayout.FloatField(
                new GUIContent(
                    "Cutoff Offset",
                    "Moves every cut plane along parent->bone direction in source mesh local units. Positive keeps slightly more distal geometry."),
                _cutoffOffset);
        }

        private void DrawGeometrySection()
        {
            EditorGUILayout.LabelField("Proxy Geometry", EditorStyles.boldLabel);

            _clusterCellSize = EditorGUILayout.FloatField(
                new GUIContent(
                    "Cluster Cell Size",
                    "Local-space vertex clustering cell size. Larger values reduce polygons more aggressively but can damage volume closure."),
                _clusterCellSize);

            _surfaceOffset = EditorGUILayout.FloatField(
                new GUIContent(
                    "Core Surface Offset",
                    "Small normal-direction offset applied to the generated core volume. Keep near zero; outer fade is handled by the shader."),
                _surfaceOffset);

            _sealOpenBoundaries = EditorGUILayout.Toggle(
                new GUIContent(
                    "Seal Open Boundaries",
                    "Find boundary loops after simplification and fill them with cap triangles. Required for stencil-parity inside/outside detection."),
                _sealOpenBoundaries);

            if (_clusterCellSize <= 0f)
                EditorGUILayout.HelpBox("Cluster Cell Size must be greater than zero.", MessageType.Error);
            else if (_clusterCellSize > 0.06f)
                EditorGUILayout.HelpBox("Large clustering cells can collapse thin limbs and create non-manifold geometry. Check Boundary Edges after generation.", MessageType.Warning);

            if (!_sealOpenBoundaries)
                EditorGUILayout.HelpBox("The proximity volume shader requires a closed mesh. Disabling sealing is intended only for geometry debugging.", MessageType.Warning);
        }

        private void DrawShaderSection()
        {
            EditorGUILayout.LabelField("Volume Fade Shader", EditorStyles.boldLabel);

            _fadeDistance = EditorGUILayout.Slider(
                new GUIContent("Fade Distance", "World-space normal expansion used for the outer proximity shells."),
                _fadeDistance,
                0f,
                0.25f);

            _fadeStrength = EditorGUILayout.Slider(
                new GUIContent("Fade Strength", "Opacity of the outer fade shells before entering the core volume."),
                _fadeStrength,
                0f,
                1f);

            _coreStrength = EditorGUILayout.Slider(
                new GUIContent("Core Black Strength", "Opacity while the camera is inside the original/core proxy volume."),
                _coreStrength,
                0f,
                1f);

            EditorGUILayout.HelpBox(
                "Default behavior: outside all shells = no effect; inside outer shell = very light black; " +
                "inside mid shell = stronger fade; inside the core = black. The shader does not use object-origin distance or vertex *= 3.",
                MessageType.None);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            _material = (Material)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Material Override",
                    "Optional. Leave empty to generate a material using orz_Shop/NearShaderProxyVolume with the Fade/Core values above."),
                _material,
                typeof(Material),
                false);

            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Asset Folder", "Generated Mesh/Material asset folder."),
                _outputFolder);

            _replaceExisting = EditorGUILayout.Toggle(
                new GUIContent("Replace Existing", "Replace the previously generated proxy volume and mesh asset for this source."),
                _replaceExisting);
        }

        private void DrawActions()
        {
            bool invalidCutoff = HasInvalidCutoffBone();
            bool canGenerate = _source != null &&
                               _source.sharedMesh != null &&
                               _clusterCellSize > 0f &&
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
                    bool removed = orz_Shop_NearShaderProxyVolumeGenerator.DeleteGeneratedProxy(_source, _outputFolder);
                    if (!removed)
                        ShowNotification(new GUIContent("No generated proxy volume found."));
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
            EditorGUILayout.LabelField("Vertices", $"{_lastResult.SourceVertexCount:N0} -> {_lastResult.ProxyVertexCount:N0}");
            EditorGUILayout.LabelField("Triangles", $"{_lastResult.SourceTriangleCount:N0} -> {_lastResult.ProxyTriangleCount:N0}");
            EditorGUILayout.LabelField("Cut Planes", _lastResult.CutPlaneCount.ToString());
            EditorGUILayout.LabelField("Boundary Edges Before Seal", _lastResult.BoundaryEdgesBeforeSeal.ToString("N0"));
            EditorGUILayout.LabelField("Boundary Edges After Seal", _lastResult.BoundaryEdgesAfterSeal.ToString("N0"));

            if (_lastResult.BoundaryEdgesAfterSeal == 0)
            {
                EditorGUILayout.HelpBox("Closed-volume edge check passed: no single-use boundary edges remain.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Open boundary edges remain. Stencil parity can leak or fail. Reduce Cluster Cell Size or inspect non-manifold/source topology.",
                    MessageType.Warning);
            }

            if (_lastResult.SourceTriangleCount > 0)
            {
                float ratio = (float)_lastResult.ProxyTriangleCount / _lastResult.SourceTriangleCount;
                EditorGUILayout.LabelField("Triangle Ratio", $"{ratio:P1}");
            }

            EditorGUILayout.ObjectField("Proxy Object", _lastResult.ProxyObject, typeof(GameObject), true);
            EditorGUILayout.ObjectField("Proxy Mesh", _lastResult.ProxyMesh, typeof(Mesh), false);
            EditorGUILayout.ObjectField("Material", _lastResult.AssignedMaterial, typeof(Material), false);
        }

        private void Generate()
        {
            try
            {
                _lastResult = orz_Shop_NearShaderProxyVolumeGenerator.Generate(
                    _source,
                    _material,
                    _cutoffBones,
                    _cutoffOffset,
                    _clusterCellSize,
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
                    $"[NearShaderProxyMeshGenerator] Closed proxy volume generated. " +
                    $"V {_lastResult.SourceVertexCount} -> {_lastResult.ProxyVertexCount}, " +
                    $"T {_lastResult.SourceTriangleCount} -> {_lastResult.ProxyTriangleCount}, " +
                    $"Boundary {_lastResult.BoundaryEdgesBeforeSeal} -> {_lastResult.BoundaryEdgesAfterSeal}.",
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
