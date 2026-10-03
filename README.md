# NearShaderProxyMeshGenerator

Unity Editor prototype for generating a low-poly **closed skinned proxy volume** from an existing avatar/body `SkinnedMeshRenderer`.

The proxy is backing geometry for camera near/proximity effects. It is not intended to be visible avatar geometry or a general-purpose LOD generator.

## Branch policy

- `main`: release / stable
- `dev`: integration
- `feature/*`: implementation and experiments

Current prototype: `feature/proxy-mesh-prototype`

## Prototype 0.0.2

The 0.0.2 path is available from:

`orz_Shop > Near Shader Proxy Volume Generator`

It adds a closed-volume workflow intended for a blackout/proximity shader:

- Select a body `SkinnedMeshRenderer`.
- Optionally specify one or more **Cutoff Bones**.
- For each cutoff bone, reconstruct the bone position from the source bindpose.
- Build a plane through the selected bone, normal to the parent -> bone direction.
- Keep the parent/proximal side and remove the child/distal side.
- Clip triangles against each cut plane and interpolate new vertex Normal/UV/BoneWeight values.
- Reduce the clipped mesh using local-space vertex clustering.
- Keep source `bones`, `rootBone`, bindposes, and strongest 4 accumulated bone influences.
- Apply only a small normal-direction `Core Surface Offset` to the core volume.
- Detect single-use boundary edges after simplification.
- Optionally seal boundary loops with cap triangles.
- Report boundary edge counts before and after sealing.
- Create a default material using `orz_Shop/NearShaderProxyVolume` when no override material is supplied.

Generated assets default to:

`Assets/Generated/NearShaderProxyMesh/`

## Shader concept

`Shaders/orz_Shop_NearShaderProxyVolume.shader` does **not** use object-origin distance and does not multiply all vertices by a fixed scale.

Instead it uses the generated mesh as the actual proximity volume:

1. **Outer shell**: core mesh expanded along world-space vertex normals by `Fade Distance`.
2. **Mid shell**: expanded by half of `Fade Distance`.
3. **Core**: the generated proxy surface itself.

Each shell uses stencil parity:

- render all shell faces with `ColorMask 0`, `ZTest Always`, and `Stencil Pass Invert`;
- a ray from outside a closed mesh crosses an even number of faces, leaving the stencil bit clear;
- a ray starting inside a closed mesh crosses an odd number of faces, leaving the stencil bit set;
- a `Cull Front` color pass then draws only where the parity bit is set and clears that bit again.

Resulting intent:

- outside the outer shell: no effect;
- inside the outer shell: light black fade;
- inside the mid shell: stronger fade;
- inside the core proxy: black according to `Core Black Strength`.

The shader currently uses stencil bit `128` internally. Materials/shaders that also write that bit can interfere with the parity test.

## Recommended starting values

- `Cluster Cell Size`: `0.02` - `0.04`
- `Core Surface Offset`: `0.0` - `0.01`
- `Fade Distance`: `0.03` - `0.08`
- `Fade Strength`: `0.1` - `0.3`
- `Core Black Strength`: `1.0`
- `Seal Open Boundaries`: On

Unlike 0.0.1, a large `Surface Offset` is no longer the main method for creating the proximity range. The shader's outer shells provide that range.

## Bone cutoff semantics

A cutoff bone means **"keep geometry up to this bone from the parent side"**.

For a cutoff bone `B` with parent `P`:

- cut origin = bind-pose position of `B`;
- cut normal = normalized `(B - P)`;
- kept half-space = parent/proximal side of that plane.

`Cutoff Offset` moves the plane along the parent -> bone direction. Positive values retain slightly more distal geometry.

Multiple cutoff bones are applied as multiple half-space clips. Typical uses are paired wrists, ankles, a neck cutoff, or limb cutoffs.

## Closed-volume requirement

Stencil parity depends on a closed surface. The generator reports:

- `Boundary Edges Before Seal`
- `Boundary Edges After Seal`

`Boundary Edges After Seal = 0` is the desired result.

A non-zero result means open/non-manifold topology remains and the shader can leak, miss pixels, or produce unstable inside/outside results. Reduce `Cluster Cell Size`, change cutoff positions, or inspect the source/proxy topology.

## 0.0.1 baseline

The original menu remains temporarily available for comparison:

`orz_Shop > Near Shader Proxy Mesh Generator`

0.0.1 performs:

- body mesh read;
- vertex clustering;
- normal-direction inflate;
- bone/root/bindpose reuse;
- material assignment.

It does not provide bone-plane clipping, boundary sealing, or closed-volume shader logic.

## Current validation focus

Before replacing the 0.0.1 path, validate:

- Cutoff planes actually land at the expected joints on multiple avatars.
- Wrist/ankle/neck cuts produce usable caps.
- `Boundary Edges After Seal` reaches zero on normal avatar body meshes.
- Aggressive clustering does not create non-manifold topology that defeats parity.
- The core surface blacks out reliably when the camera enters it.
- Outer and mid shells produce a useful pre-entry fade without visible silhouette artifacts while outside.
- Skinning at elbows/knees/hips remains sufficient for proximity coverage even if visually ugly.
- Stencil bit 128 does not conflict with the target avatar shader stack.

## Known limitations

- This is not a visual LOD generator. Coverage and closed-volume behavior take priority over appearance.
- Boundary sealing uses simple fan caps and is not intended for visible rendering.
- Complex/non-manifold boundary networks may not be sealable by the prototype loop tracer.
- Self-intersections are not resolved. Parity tolerates some self-intersection, but pathological topology can still fail.
- BlendShapes are not transferred.
- Tangents are not generated.
- UV0 fidelity is not a goal.
- Bone weights are reduced to the strongest 4 accumulated influences per generated vertex.
- Cutoff planes are half-space cuts; a badly chosen bone axis can clip unrelated geometry if that geometry lies beyond the same plane.
- The volume shader currently uses six passes: parity + color for outer, mid, and core shells.
- The shader relies on the stencil buffer and reserves bit 128 while each shell is evaluated.

## Current files

- `Editor/orz_Shop_NearShaderProxyMeshGenerator.cs` - 0.0.1 baseline mesh generator.
- `Editor/orz_Shop_NearShaderProxyMeshGeneratorWindow.cs` - 0.0.1 baseline EditorWindow.
- `Editor/orz_Shop_NearShaderProxyVolumeGenerator.cs` - 0.0.2 clipping, clustering, sealing, skinning, and asset generation.
- `Editor/orz_Shop_NearShaderProxyVolumeGeneratorWindow.cs` - 0.0.2 cutoff/volume EditorWindow.
- `Shaders/orz_Shop_NearShaderProxyVolume.shader` - closed-volume stencil parity blackout/fade shader.
