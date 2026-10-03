// NearShaderProxyMeshGenerator
// Prototype version: 0.0.1

using System;
using UnityEditor;
using UnityEngine;

namespace orz_Shop.NearShaderProxyMeshGenerator
{
    internal sealed class orz_Shop_NearShaderProxyMeshGeneratorWindow : EditorWindow
    {
        private const string DefaultOutputFolder = "Assets/Generated/NearShaderProxyMesh";

        [SerializeField] private SkinnedMeshRenderer _source;
        [SerializeField] private Material _material;
        [SerializeField] private float _clusterCellSize = 0.03f;
        [SerializeField] private float _surfaceOffset = 0.03f;
        [SerializeField] private string _outputFolder = DefaultOutputFolder;
        [SerializeField] private bool _replaceExisting = true;

        private Vector2 _scroll;
        private orz_Shop_NearShaderProxyMeshGenerator.GenerationResult _lastResult;

        [MenuItem("orz_Shop/Near Shader Proxy Mesh Generator")]
        private static void Open()
        {
            var window = GetWindow<orz_Shop_NearShaderProxyMeshGeneratorWindow>();
            window.titleContent = new GUIContent("Near Shader Proxy");
            window.minSize = new Vector2(420f, 420f);
            window.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Near Shader Proxy Mesh Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Prototype: generates a low-poly inflated SkinnedMesh proxy for near/proximity shader backing geometry. " +
                "Joint deformation quality is intentionally secondary to coverage and low triangle count.",
                MessageType.Info);

            EditorGUILayout.Space(4f);
            DrawSourceSection();
            EditorGUILayout.Space(8f);
            DrawGeometrySection();
            EditorGUILayout.Space(8f);
            DrawOutputSection();
            EditorGUILayout.Space(12f);
            DrawActions();
            EditorGUILayout.Space(8f);
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

        private void DrawGeometrySection()
        {
            EditorGUILayout.LabelField("Geometry", EditorStyles.boldLabel);

            _clusterCellSize = EditorGUILayout.FloatField(
                new GUIContent(
                    "Cluster Cell Size",
                    "Local-space grid size used for vertex clustering. Larger values reduce polygon count more aggressively."),
                _clusterCellSize);

            _surfaceOffset = EditorGUILayout.FloatField(
                new GUIContent(
                    "Surface Offset",
                    "Local-space distance added along the averaged source normal. Positive values inflate the proxy."),
                _surfaceOffset);

            if (_clusterCellSize <= 0f)
                EditorGUILayout.HelpBox("Cluster Cell Size must be greater than zero.", MessageType.Error);
            else if (_clusterCellSize > 0.1f)
                EditorGUILayout.HelpBox("A cell size above 0.1 may collapse narrow body parts or produce holes.", MessageType.Warning);

            if (_surfaceOffset < 0f)
                EditorGUILayout.HelpBox("Negative Surface Offset shrinks the proxy inward.", MessageType.Warning);

            EditorGUILayout.HelpBox(
                "Initial body-sized values: Cell Size 0.02-0.05, Surface Offset 0.01-0.05. " +
                "Both values are in the source mesh local coordinate system.",
                MessageType.None);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            _material = (Material)EditorGUILayout.ObjectField(
                new GUIContent("Material", "Near/proximity shader material. If empty, a black Unlit fallback material is created."),
                _material,
                typeof(Material),
                false);

            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Asset Folder", "Generated Mesh/Material asset folder."),
                _outputFolder);

            _replaceExisting = EditorGUILayout.Toggle(
                new GUIContent("Replace Existing", "Replace the previously generated proxy child and mesh asset for this source."),
                _replaceExisting);

            EditorGUILayout.HelpBox(
                "The generated object is placed directly under the source renderer with identity local transform, " +
                "so its renderer matrix matches the source and the original bindposes remain valid.",
                MessageType.None);
        }

        private void DrawActions()
        {
            bool canGenerate = _source != null && _source.sharedMesh != null && _clusterCellSize > 0f;

            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generate Proxy", GUILayout.Height(34f)))
                    Generate();
            }

            using (new EditorGUI.DisabledScope(_source == null))
            {
                if (GUILayout.Button("Delete Generated Proxy"))
                {
                    bool removed = orz_Shop_NearShaderProxyMeshGenerator.DeleteGeneratedProxy(_source, _outputFolder);
                    if (!removed)
                        ShowNotification(new GUIContent("No generated proxy found."));
                    else
                    {
                        _lastResult = null;
                        ShowNotification(new GUIContent("Generated proxy removed."));
                    }
                }
            }
        }

        private void DrawLastResult()
        {
            if (_lastResult == null)
                return;

            EditorGUILayout.LabelField("Last Generation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Vertices", $"{_lastResult.SourceVertexCount:N0} -> {_lastResult.ProxyVertexCount:N0}");
            EditorGUILayout.LabelField("Triangles", $"{_lastResult.SourceTriangleCount:N0} -> {_lastResult.ProxyTriangleCount:N0}");

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
                _lastResult = orz_Shop_NearShaderProxyMeshGenerator.Generate(
                    _source,
                    _material,
                    _clusterCellSize,
                    _surfaceOffset,
                    _outputFolder,
                    _replaceExisting);

                Selection.activeGameObject = _lastResult.ProxyObject;
                EditorGUIUtility.PingObject(_lastResult.ProxyObject);
                SceneView.RepaintAll();

                Debug.Log(
                    $"[NearShaderProxyMeshGenerator] Generated proxy: " +
                    $"V {_lastResult.SourceVertexCount} -> {_lastResult.ProxyVertexCount}, " +
                    $"T {_lastResult.SourceTriangleCount} -> {_lastResult.ProxyTriangleCount}.",
                    _lastResult.ProxyObject);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "Near Shader Proxy Mesh Generator",
                    ex.Message,
                    "OK");
            }
        }
    }
}
