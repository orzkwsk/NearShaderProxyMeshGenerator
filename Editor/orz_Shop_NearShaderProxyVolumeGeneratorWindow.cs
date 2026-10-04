// NearShaderProxyMeshGenerator
// Prototype version: 0.0.5

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace orz_Shop.NearShaderProxyMeshGenerator
{
    internal sealed class orz_Shop_NearShaderProxyVolumeGeneratorWindow : EditorWindow
    {
        private const string DefaultOutputFolder = "Assets/Generated/NearShaderProxyMesh";
        private const int CurrentUiVersion = 5;

        [SerializeField] private int _uiVersion;
        [SerializeField] private SkinnedMeshRenderer _source;
        [SerializeField] private Material _material;

        [SerializeField] private List<Transform> _selectedBones = new List<Transform>();
        [SerializeField] private string _boneFilter = string.Empty;
        [SerializeField] private float _selectionThreshold = 0.25f;

        [SerializeField] private float _mergeSize = 0f;
        [SerializeField] private float _surfaceOffset = 0f;
        [SerializeField] private bool _sealOpenBoundaries = true;

        [SerializeField] private float _fadeDistance = 0.05f;
        [SerializeField] private float _fadeStrength = 0.2f;
        [SerializeField] private float _coreStrength = 1f;

        [SerializeField] private string _outputFolder = DefaultOutputFolder;
        [SerializeField] private bool _replaceExisting = true;

        private Vector2 _scroll;
        private Vector2 _boneScroll;

        private orz_Shop_NearShaderProxyVolumeGenerator.GenerationResult _lastResult;

        [MenuItem("orz_Shop/Near Shader Proxy Volume Generator")]
        private static void Open()
        {
            var window =
                GetWindow<orz_Shop_NearShaderProxyVolumeGeneratorWindow>();

            window.titleContent =
                new GUIContent("Near Shader Proxy Volume");

            window.minSize =
                new Vector2(500f, 720f);

            window.Show();
        }

        private void OnEnable()
        {
            if (_uiVersion >= CurrentUiVersion)
            {
                SyncSelectedBones();
                return;
            }

            _selectionThreshold = 0.25f;
            _mergeSize = 0f;
            _surfaceOffset = 0f;

            // Boundary-bone semantics from 0.0.2-0.0.4 are intentionally discarded.
            // 0.0.5 uses explicit included-bone selection.
            SelectAllSourceBones();

            _uiVersion = CurrentUiVersion;
        }

        private void OnGUI()
        {
            _scroll =
                EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField(
                "Near Shader Proxy Volume Generator",
                EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Prototype 0.0.5: checked bones are the INCLUDED proxy region. " +
                "A vertex is selected by the summed skin weight of checked bones. " +
                "When all renderer bones are checked, selection filtering is bypassed and the full source body is used.",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            DrawSourceSection();

            EditorGUILayout.Space(10f);
            DrawBoneSelectionSection();

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
            EditorGUILayout.LabelField(
                "Source",
                EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            SkinnedMeshRenderer newSource =
                (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "Body Mesh",
                        "Source body SkinnedMeshRenderer."),
                    _source,
                    typeof(SkinnedMeshRenderer),
                    true);

            if (EditorGUI.EndChangeCheck())
            {
                _source = newSource;
                _lastResult = null;
                SelectAllSourceBones();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(
                    "Use Selection",
                    GUILayout.Width(120f)))
                {
                    GameObject selected =
                        Selection.activeGameObject;

                    if (selected != null)
                    {
                        SkinnedMeshRenderer found =
                            selected.GetComponent<SkinnedMeshRenderer>();

                        if (found == null)
                        {
                            found =
                                selected.GetComponentInChildren<SkinnedMeshRenderer>();
                        }

                        if (found != _source)
                        {
                            _source = found;
                            _lastResult = null;
                            SelectAllSourceBones();
                        }
                    }
                }
            }

            if (_source != null &&
                _source.sharedMesh != null)
            {
                Mesh mesh = _source.sharedMesh;

                EditorGUILayout.LabelField(
                    "Mesh",
                    mesh.name);

                EditorGUILayout.LabelField(
                    "Source Vertices",
                    mesh.vertexCount.ToString("N0"));

                EditorGUILayout.LabelField(
                    "Renderer Bones",
                    (_source.bones?.Length ?? 0).ToString("N0"));
            }
        }

        private void DrawBoneSelectionSection()
        {
            EditorGUILayout.LabelField(
                "Proxy Bone Region",
                EditorStyles.boldLabel);

            if (_source == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a Body Mesh first.",
                    MessageType.None);

                return;
            }

            Transform[] bones =
                _source.bones ?? Array.Empty<Transform>();

            SyncSelectedBones();

            EditorGUILayout.HelpBox(
                "Only checked bones contribute to the generated region. " +
                "For example, checking a leg-chain group generates the skin region weighted to that group. " +
                "Use Select All to reproduce the complete source body.",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All"))
                    SelectAllSourceBones();

                if (GUILayout.Button("Clear"))
                    _selectedBones.Clear();

                if (GUILayout.Button("Invert"))
                    InvertBoneSelection();
            }

            int selectedCount =
                CountSelectedRendererBones();

            EditorGUILayout.LabelField(
                "Selected",
                $"{selectedCount} / {CountSelectableRendererBones()}");

            _boneFilter =
                EditorGUILayout.TextField(
                    new GUIContent(
                        "Filter",
                        "Filters the displayed bone list only."),
                    _boneFilter ?? string.Empty);

            _boneScroll =
                EditorGUILayout.BeginScrollView(
                    _boneScroll,
                    GUI.skin.box,
                    GUILayout.MinHeight(160f),
                    GUILayout.MaxHeight(280f));

            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];

                if (bone == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(_boneFilter) &&
                    bone.name.IndexOf(
                        _boneFilter,
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool selected =
                    _selectedBones.Contains(bone);

                bool next =
                    EditorGUILayout.ToggleLeft(
                        $"{i:000}  {bone.name}",
                        selected);

                if (next != selected)
                    SetBoneSelected(bone, next);
            }

            EditorGUILayout.EndScrollView();

            bool allSelected =
                AreAllSourceBonesSelected();

            using (new EditorGUI.DisabledScope(allSelected))
            {
                _selectionThreshold =
                    EditorGUILayout.Slider(
                        new GUIContent(
                            "Bone Weight Threshold",
                            "Keep the region where the total skin weight assigned to checked bones reaches this value. " +
                            "Ignored when all renderer bones are selected."),
                        _selectionThreshold,
                        0.01f,
                        0.99f);
            }

            if (allSelected)
            {
                EditorGUILayout.HelpBox(
                    "All renderer bones are selected: bone-region filtering will be bypassed. " +
                    "The generator must start from the full source mesh.",
                    MessageType.Info);
            }
            else if (selectedCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "No bones are selected.",
                    MessageType.Error);
            }
        }

        private void DrawGeometrySection()
        {
            EditorGUILayout.LabelField(
                "Proxy Geometry",
                EditorStyles.boldLabel);

            _mergeSize =
                EditorGUILayout.Slider(
                    new GUIContent(
                        "Merge Size",
                        "0 = no intentional low-poly reduction. " +
                        "Increase gradually after region extraction is confirmed."),
                    _mergeSize,
                    0f,
                    0.05f);

            _surfaceOffset =
                EditorGUILayout.Slider(
                    new GUIContent(
                        "Core Surface Offset",
                        "Normal-direction adjustment of the generated core volume. " +
                        "0 preserves the source surface."),
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

            _sealOpenBoundaries =
                EditorGUILayout.Toggle(
                    new GUIContent(
                        "Seal Open Boundaries",
                        "Fill open loops after extraction/welding. " +
                        "Required for stencil-parity inside/outside detection."),
                    _sealOpenBoundaries);

            EditorGUILayout.HelpBox(
                "Validation order: first use Merge Size = 0 and Surface Offset = 0. " +
                "Confirm the selected region, then simplify.",
                MessageType.None);

            if (_mergeSize > 0.025f)
            {
                EditorGUILayout.HelpBox(
                    "Large Merge Size can collapse thin limbs or join nearby surfaces.",
                    MessageType.Warning);
            }
        }

        private void DrawShaderSection()
        {
            EditorGUILayout.LabelField(
                "Volume Fade Shader",
                EditorStyles.boldLabel);

            _fadeDistance =
                EditorGUILayout.Slider(
                    new GUIContent(
                        "Fade Distance",
                        "World-space normal expansion used by the outer shader shells."),
                    _fadeDistance,
                    0f,
                    0.25f);

            _fadeStrength =
                EditorGUILayout.Slider(
                    new GUIContent(
                        "Fade Strength",
                        "Opacity of the outer/mid shells."),
                    _fadeStrength,
                    0f,
                    1f);

            _coreStrength =
                EditorGUILayout.Slider(
                    new GUIContent(
                        "Core Black Strength",
                        "Opacity while the camera is inside the core volume."),
                    _coreStrength,
                    0f,
                    1f);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField(
                "Output",
                EditorStyles.boldLabel);

            _material =
                (Material)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "Material Override",
                        "Leave empty to create a NearShaderProxyVolume material."),
                    _material,
                    typeof(Material),
                    false);

            _outputFolder =
                EditorGUILayout.TextField(
                    new GUIContent(
                        "Asset Folder",
                        "Generated Mesh/Material asset folder."),
                    _outputFolder);

            _replaceExisting =
                EditorGUILayout.Toggle(
                    new GUIContent(
                        "Replace Existing",
                        "Replace the previously generated proxy for this source."),
                    _replaceExisting);
        }

        private void DrawActions()
        {
            int selectedCount =
                CountSelectedRendererBones();

            bool canGenerate =
                _source != null &&
                _source.sharedMesh != null &&
                selectedCount > 0 &&
                _mergeSize >= 0f &&
                _fadeDistance >= 0f;

            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button(
                    "Generate Closed Proxy Volume",
                    GUILayout.Height(36f)))
                {
                    Generate();
                }
            }

            using (new EditorGUI.DisabledScope(_source == null))
            {
                if (GUILayout.Button(
                    "Delete Generated Proxy Volume"))
                {
                    bool removed =
                        orz_Shop_NearShaderProxyVolumeGenerator.DeleteGeneratedProxy(
                            _source,
                            _outputFolder);

                    if (!removed)
                    {
                        ShowNotification(
                            new GUIContent(
                                "No generated proxy volume found."));
                    }
                    else
                    {
                        _lastResult = null;

                        ShowNotification(
                            new GUIContent(
                                "Generated proxy volume removed."));
                    }
                }
            }
        }

        private void DrawLastResult()
        {
            if (_lastResult == null)
                return;

            EditorGUILayout.LabelField(
                "Last Generation",
                EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                "Source Vertices",
                _lastResult.SourceVertexCount.ToString("N0"));

            EditorGUILayout.LabelField(
                "Source Triangles",
                _lastResult.SourceTriangleCount.ToString("N0"));

            EditorGUILayout.LabelField(
                "Selected Bones",
                _lastResult.SelectedBoneCount.ToString("N0"));

            EditorGUILayout.LabelField(
                "Selected Source Vertices",
                _lastResult.SelectionVertexCount.ToString("N0"));

            EditorGUILayout.LabelField(
                "Triangles After Region Clip",
                _lastResult.SelectionTriangleCount.ToString("N0"));

            EditorGUILayout.LabelField(
                "Proxy Vertices",
                _lastResult.ProxyVertexCount.ToString("N0"));

            EditorGUILayout.LabelField(
                "Proxy Triangles",
                _lastResult.ProxyTriangleCount.ToString("N0"));

            if (_lastResult.SelectionBypassed)
            {
                EditorGUILayout.HelpBox(
                    "Bone selection bypassed because all renderer bones were selected. " +
                    "If the proxy is still missing the upper body in this state, the defect is outside bone-region selection.",
                    MessageType.Info);
            }

            if (_lastResult.SelectedBoneDiagnostics != null &&
                _lastResult.SelectedBoneDiagnostics.Count > 0 &&
                _lastResult.SelectedBoneDiagnostics.Count <= 16)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField(
                    "Selected Bone Diagnostics",
                    EditorStyles.boldLabel);

                foreach (
                    orz_Shop_NearShaderProxyVolumeGenerator.SelectedBoneDiagnostic diagnostic
                    in _lastResult.SelectedBoneDiagnostics)
                {
                    float ratio =
                        diagnostic.SourceVertexCount > 0
                            ? (float)diagnostic.InfluencedVertexCount /
                              diagnostic.SourceVertexCount
                            : 0f;

                    EditorGUILayout.LabelField(
                        $"{diagnostic.BoneIndex:000} {diagnostic.BoneName}",
                        $"{diagnostic.InfluencedVertexCount:N0} vertices ({ratio:P1})");
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
                    "The generated proxy is not a clean closed 2-manifold. " +
                    "Keep Merge Size at 0 while debugging selection.",
                    MessageType.Warning);
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
                SyncSelectedBones();

                _lastResult =
                    orz_Shop_NearShaderProxyVolumeGenerator.Generate(
                        _source,
                        _material,
                        _selectedBones,
                        _selectionThreshold,
                        _mergeSize,
                        _surfaceOffset,
                        _sealOpenBoundaries,
                        _fadeDistance,
                        _fadeStrength,
                        _coreStrength,
                        _outputFolder,
                        _replaceExisting);

                Selection.activeGameObject =
                    _lastResult.ProxyObject;

                EditorGUIUtility.PingObject(
                    _lastResult.ProxyObject);

                SceneView.RepaintAll();

                Debug.Log(
                    "[NearShaderProxyMeshGenerator] Proxy volume generated. " +
                    $"Selected bones {_lastResult.SelectedBoneCount}, " +
                    $"Selection bypass {_lastResult.SelectionBypassed}, " +
                    $"Source V/T {_lastResult.SourceVertexCount}/{_lastResult.SourceTriangleCount}, " +
                    $"Selected V/T {_lastResult.SelectionVertexCount}/{_lastResult.SelectionTriangleCount}, " +
                    $"Proxy V/T {_lastResult.ProxyVertexCount}/{_lastResult.ProxyTriangleCount}, " +
                    $"Boundary {_lastResult.BoundaryEdgesBeforeSeal}->{_lastResult.BoundaryEdgesAfterSeal}, " +
                    $"NonManifold {_lastResult.NonManifoldEdgesAfterSeal}.",
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

        private void SyncSelectedBones()
        {
            if (_selectedBones == null)
                _selectedBones = new List<Transform>();

            if (_source == null)
            {
                _selectedBones.Clear();
                return;
            }

            Transform[] bones =
                _source.bones ?? Array.Empty<Transform>();

            var valid =
                new HashSet<Transform>(bones);

            for (int i = _selectedBones.Count - 1;
                 i >= 0;
                 i--)
            {
                Transform bone =
                    _selectedBones[i];

                if (bone == null ||
                    !valid.Contains(bone) ||
                    _selectedBones.IndexOf(bone) != i)
                {
                    _selectedBones.RemoveAt(i);
                }
            }
        }

        private void SelectAllSourceBones()
        {
            if (_selectedBones == null)
                _selectedBones = new List<Transform>();
            else
                _selectedBones.Clear();

            if (_source == null)
                return;

            Transform[] bones =
                _source.bones ?? Array.Empty<Transform>();

            foreach (Transform bone in bones)
            {
                if (bone != null &&
                    !_selectedBones.Contains(bone))
                {
                    _selectedBones.Add(bone);
                }
            }
        }

        private void InvertBoneSelection()
        {
            if (_source == null)
                return;

            Transform[] bones =
                _source.bones ?? Array.Empty<Transform>();

            var next =
                new List<Transform>();

            foreach (Transform bone in bones)
            {
                if (bone != null &&
                    !_selectedBones.Contains(bone))
                {
                    next.Add(bone);
                }
            }

            _selectedBones = next;
        }

        private void SetBoneSelected(
            Transform bone,
            bool selected)
        {
            if (bone == null)
                return;

            if (selected)
            {
                if (!_selectedBones.Contains(bone))
                    _selectedBones.Add(bone);
            }
            else
            {
                _selectedBones.Remove(bone);
            }
        }

        private int CountSelectedRendererBones()
        {
            if (_source == null)
                return 0;

            SyncSelectedBones();
            return _selectedBones.Count;
        }

        private int CountSelectableRendererBones()
        {
            if (_source == null)
                return 0;

            int count = 0;

            foreach (
                Transform bone
                in _source.bones ?? Array.Empty<Transform>())
            {
                if (bone != null)
                    count++;
            }

            return count;
        }

        private bool AreAllSourceBonesSelected()
        {
            int selectable =
                CountSelectableRendererBones();

            return selectable > 0 &&
                   CountSelectedRendererBones() == selectable;
        }
    }
}
