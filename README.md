# NearShaderProxyMeshGenerator

Unity Editor prototype for generating a **closed skinned proxy volume** from an avatar/body `SkinnedMeshRenderer` for camera near/proximity blackout effects.

The proxy is not intended as visible avatar geometry or a general-purpose LOD.

## Branch policy

- `main`: release / stable
- `dev`: integration
- `feature/*`: implementation and experiments

Current prototype: `feature/proxy-mesh-prototype`

## Prototype 0.0.3

Menu:

`orz_Shop > Near Shader Proxy Volume Generator`

### Bone boundary semantics

0.0.2 used an infinite spatial cut plane through the selected bone. This could retain the wrong body half-space depending on bone axis/avatar layout.

0.0.3 changes the cutoff to **BoneWeight-based terminal boundaries**:

- selected bone itself is kept;
- source bones that are descendants of the selected bone are treated as the distal side;
- each vertex gets a distal-weight value from those descendant bone influences;
- the default boundary is 50% distal weight;
- triangles crossing that weight boundary are clipped and new vertex Normal/UV/BoneWeight values are interpolated;
- multiple selected bones are applied as multiple terminal boundaries.

Examples:

- `Neck`: keep through Neck, remove Head-side weighting;
- `Wrist`: keep through Wrist, remove finger-side weighting;
- `UpperArm`: keep through UpperArm, remove LowerArm/Hand-side weighting.

`Bone Cut Bias` is centered at `0`:

- `0`: 50% descendant weight boundary;
- positive: keep more distal geometry;
- negative: cut earlier.

A selected terminal bone with no renderer-used descendants cannot define a BoneWeight boundary and is reported as skipped.

### Geometry controls

Defaults are intentionally neutral:

- `Merge Size = 0`
- `Core Surface Offset = 0`
- `Bone Cut Bias = 0`

The EditorWindow migrates old serialized prototype values to these neutral defaults on first load of 0.0.3.

`Merge Size = 0` means no intentional low-poly reduction. A very small epsilon weld is still performed because triangle clipping creates duplicate per-triangle vertices and the volume requires shared edges.

Increase `Merge Size` gradually only after verifying the generated coverage. Large values can collapse thin limbs or cut boundaries.

`Core Surface Offset` is a signed normal-direction adjustment:

- `0`: preserve source surface;
- negative: shrink;
- positive: expand.

### Closed-volume diagnostics

After mesh generation the tool reports:

- `Boundary Edges Before Seal`
- `Boundary Edges After Seal`
- `Non-Manifold Edges After Seal`

For the stencil-parity shader, the desired state is:

- `Boundary Edges After Seal = 0`
- `Non-Manifold Edges After Seal = 0`

`Seal Open Boundaries` fills traced boundary loops using simple cap fans. Caps are backing geometry and are not intended for visible rendering.

## Shader concept

`Shaders/orz_Shop_NearShaderProxyVolume.shader` uses the generated mesh itself as the proximity volume. It does not use object-origin distance and does not use the original shader's fixed `vertex *= 3` expansion.

Three nested shells are evaluated:

1. outer shell = core expanded by `Fade Distance`;
2. mid shell = core expanded by half `Fade Distance`;
3. core = generated proxy surface.

Each shell uses stencil parity to distinguish camera-inside from camera-outside state.

The color passes use `Cull Off` in 0.0.3 so generated cap winding does not suppress the inside effect.

Intended result:

- outside outer shell: no effect;
- inside outer shell: light black fade;
- inside mid shell: stronger fade;
- inside core: black according to `Core Black Strength`.

The shader currently uses stencil bit `128` and six passes total.

## Recommended validation order

1. Keep `Merge Size = 0` and `Core Surface Offset = 0`.
2. Select one boundary bone and verify that the intended distal weighted region disappears.
3. Verify `Boundary Edges After Seal = 0` and `Non-Manifold Edges After Seal = 0`.
4. Add the remaining boundary bones.
5. Test proximity fade/blackout.
6. Only then increase `Merge Size` to reduce polygon count.

## Known limitations

- BoneWeight boundary behavior depends on the avatar's actual skinning quality.
- Legacy `BoneWeight` handling is reduced to the strongest four influences per generated vertex.
- BlendShapes are not transferred.
- Tangents are not generated.
- UV fidelity is not a goal.
- Boundary sealing uses simple fan caps.
- Complex/non-manifold source topology can still defeat sealing/parity.
- Self-intersections are not resolved.
- The volume shader reserves stencil bit 128 while evaluating each shell.

Generated assets default to:

`Assets/Generated/NearShaderProxyMesh/`

## Current files

- `Editor/orz_Shop_NearShaderProxyMeshGenerator.cs` - 0.0.1 baseline generator.
- `Editor/orz_Shop_NearShaderProxyMeshGeneratorWindow.cs` - 0.0.1 baseline window.
- `Editor/orz_Shop_NearShaderProxyVolumeGenerator.cs` - 0.0.3 BoneWeight boundary / weld / sealing / skinning / asset generation.
- `Editor/orz_Shop_NearShaderProxyVolumeGeneratorWindow.cs` - 0.0.3 controls and diagnostics.
- `Shaders/orz_Shop_NearShaderProxyVolume.shader` - closed-volume stencil-parity blackout/fade shader.
