# NearShaderProxyMeshGenerator

Unity Editor tool for generating an inflated low-poly skinned proxy mesh from an existing `SkinnedMeshRenderer`.

The proxy is intended as backing geometry for near/proximity shaders rather than as visible avatar geometry.

## Branch policy

- `main`: release / stable
- `dev`: integration
- `feature/*`: implementation and experiments

Current prototype: `feature/proxy-mesh-prototype`

## Prototype scope (0.0.1)

- Select a body `SkinnedMeshRenderer`.
- Read mesh geometry in the Editor without requiring `Read/Write Enabled` for vertex/index access.
- Reduce geometry using local-space vertex clustering.
- Inflate clustered vertices along averaged source normals.
- Preserve source `bones`, `rootBone`, bindposes, and up to 4 bone influences per generated vertex.
- Merge all source triangle submeshes into one proxy submesh.
- Assign a user-specified near/proximity material.
- If no material is specified, create a black `Unlit/Color` fallback material.
- Create the proxy as a direct child of the source renderer with identity local TRS so the renderer matrix matches the source.
- Disable shadow casting, receiving shadows, light probes, and reflection probes on the generated renderer.

## Usage

1. Place/clone this repository under the Unity project's `Assets` directory.
2. Open `orz_Shop > Near Shader Proxy Mesh Generator`.
3. Assign the avatar body `SkinnedMeshRenderer`.
4. Set `Cluster Cell Size`.
   - Typical body-sized starting range: `0.02` to `0.05`.
   - Larger values produce fewer vertices/triangles but can collapse narrow regions.
5. Set `Surface Offset`.
   - Typical starting range: `0.01` to `0.05`.
   - Positive values inflate outward in mesh local space.
6. Assign the intended near/proximity shader material, or leave it empty for the fallback black material.
7. Press `Generate Proxy`.

Generated assets default to:

`Assets/Generated/NearShaderProxyMesh/`

## Intended validation

The first prototype is meant to answer these points before improving topology quality:

- Is body-following proxy geometry easier to set up than multiple spheres?
- How coarse can the proxy be before near-shader coverage becomes visibly incorrect?
- How much `Surface Offset` is required around elbows, knees, hips, shoulders, and other bending regions?
- Are self-intersections acceptable for the target near shader?
- Does 4-weight clustered skinning remain sufficient when joints are bent?

## Known limitations

- This is not a visual LOD generator. Silhouette/coverage is prioritized over clean deformation.
- Vertex clustering can create poor topology or holes when `Cluster Cell Size` is too large.
- Self-intersections are not resolved.
- BlendShapes are not transferred.
- Tangents are not generated.
- UV0 is only averaged per cluster; texture fidelity is not a goal.
- Bone weights are reduced to the strongest 4 accumulated influences per generated vertex.
- `Surface Offset` is in local mesh units; non-uniform object scale changes the world-space thickness.
- All source submeshes are merged into one proxy submesh.

## Current files

- `Editor/orz_Shop_NearShaderProxyMeshGenerator.cs` - mesh read, clustering, skinning transfer, inflate, asset/object generation.
- `Editor/orz_Shop_NearShaderProxyMeshGeneratorWindow.cs` - prototype EditorWindow.
