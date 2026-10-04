# NearShaderProxyMeshGenerator

Unity Editor prototype for generating a closed, skinned proxy volume from an avatar/body `SkinnedMeshRenderer`.

The proxy is backing geometry for a camera near/proximity shader. It is not a visual LOD mesh.

## Branch policy

- `main`: release / stable
- `dev`: integration
- `feature/*`: implementation / experiment

Current prototype: `feature/proxy-mesh-prototype`

## 0.0.5 selection model

Open:

`orz_Shop > Near Shader Proxy Volume Generator`

0.0.5 replaces the previous terminal-boundary interpretation.

### Bone selection semantics

Checked bones are the **included proxy region**.

For every source vertex:

1. read all non-zero skin influences using `Mesh.GetBonesPerVertex()` and `Mesh.GetAllBoneWeights()`;
2. sum the weights whose bone index is checked;
3. keep/clip geometry at `Bone Weight Threshold`.

If every renderer bone is checked, the bone-region filter is bypassed completely and the full source mesh is used.

This gives a deterministic validation case:

- **Select All** must start from the entire source body;
- selecting one bone should produce the region weighted to that bone;
- selecting a chain/group produces the combined weighted region.

The previous 0.0.2-0.0.4 behavior ("selected bone is a terminal cutoff and descendants are removed") is no longer used.

## Geometry pipeline

```text
Source SkinnedMeshRenderer
        |
        +-- geometry/index read
        |
        +-- all BoneWeight1 influences
        |
        +-- checked-bone weight field
        |
        +-- triangle clipping at threshold
        |
        +-- epsilon weld
        |
        +-- optional Merge Size simplification
        |
        +-- optional Surface Offset
        |
        +-- boundary-loop sealing
        |
        +-- closed SkinnedMesh proxy
```

Generated vertices keep interpolated skinning. Output skinning is normalized to the strongest four influences per generated vertex.

## Conservative defaults

- Bone Weight Threshold: `0.25`
- Merge Size: `0`
- Core Surface Offset: `0`
- Seal Open Boundaries: On
- Fade Distance: `0.05`
- Fade Strength: `0.2`
- Core Black Strength: `1.0`

`Merge Size = 0` performs no intentional low-poly reduction. Only a very small positional weld is used to reconnect duplicated triangle vertices / source seams.

First confirm region extraction with Merge Size and Surface Offset at zero. Simplification comes afterwards.

## Bone UI

The Volume Generator no longer uses `Selection.activeTransform` as the primary bone picker.

The Source renderer's own `bones[]` array is displayed directly as a checklist, including bone indices.

Controls:

- **Select All**
- **Clear**
- **Invert**
- name filter
- per-bone checkbox

This removes ambiguity about which renderer bone is actually selected.

## Diagnostics

The result panel reports:

- source vertex/triangle count;
- selected bone count;
- source vertices passing the weight threshold;
- triangles after region clipping;
- final proxy vertex/triangle count;
- whether selection filtering was bypassed;
- per-selected-bone influenced vertex count (up to 16 selected bones);
- boundary edges before/after sealing;
- non-manifold edges after sealing.

When **Select All** is active, the result must say that bone selection was bypassed. If the upper body is still missing in that state, the defect is outside bone selection and should be investigated in geometry/index/weld/seal/rendering.

## Shader

`Shaders/orz_Shop_NearShaderProxyVolume.shader` uses the generated closed mesh as the proximity volume.

It does not use the original shader's object-origin distance or fixed `vertex *= 3` expansion.

It currently renders three shells:

- core: source/proxy surface;
- mid: half Fade Distance;
- outer: full Fade Distance.

Each shell uses stencil parity to determine whether the camera ray starts inside the closed volume.

The shader currently uses stencil bit `128`.

## Closed-volume requirement

Stencil parity assumes closed geometry.

Desired diagnostics:

```text
Boundary Edges After Seal = 0
Non-Manifold Edges After Seal = 0
```

The simple cap generator is intentionally not suitable for visible rendering; its purpose is only to close the proxy volume.

## Known limitations

- This is still a prototype and has not been validated against every avatar topology.
- BlendShapes are not transferred.
- Tangents are not generated.
- UV fidelity is not a goal.
- Output skinning is reduced to four influences even when source selection analysis used more than four.
- Position-based merging can join nearby surfaces if Merge Size is increased too far.
- Complex branching/non-manifold boundary loops may not be sealed by the simple fan-cap implementation.
- Self-intersection is not resolved.
- The volume shader uses six passes and stencil bit 128.
- Runtime/VR validation is still required before merging to `dev`.

## Legacy baseline

The original menu remains temporarily for comparison:

`orz_Shop > Near Shader Proxy Mesh Generator`

It is the 0.0.1 full-body baseline and does not implement bone-region extraction.
