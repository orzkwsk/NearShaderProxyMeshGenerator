# NearShaderProxyMeshGenerator

Unity Editor prototype for generating a closed, skinned proxy volume from an avatar/body `SkinnedMeshRenderer`.

The proxy is backing geometry for a camera near/proximity shader. It is not a visual LOD mesh.

## Branch policy

- `main`: release / stable
- `dev`: integration
- `feature/*`: implementation / experiment

Current prototype: `feature/proxy-mesh-prototype`

## 0.0.12 fullscreen stencil resolve

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
        +-- epsilon weld
        |
        +-- boundary-loop sealing / planar bone caps
        |
        +-- topology-safe edge-collapse simplification
        |
        +-- optional Surface Offset
        |
        +-- closed SkinnedMesh proxy
```

Generated vertices keep interpolated skinning. Output skinning is normalized to the strongest four influences per generated vertex.

## Conservative defaults

- Bone Weight Threshold: `0.25`
- Mesh Quality: `100%`
- Core Surface Offset: `0`
- Seal Open Boundaries: On
- Fade Distance: `0.05`
- Core Inset: `0.01`
- Fade To Core: `1.0`
- Shell Dither: `0.15`
- Core Black Strength: `1.0`

`Mesh Quality = 100%` disables intentional polygon reduction. A very small positional weld is still used to reconnect duplicated triangle vertices / source seams.

### Planar Bone Caps

0.0.7 adds flat cut sections for partial bone-region proxies.

For every nearest renderer-bone parent/child pair where selection changes from checked to unchecked (or vice versa), the generator creates a bind-pose cut plane:

- plane point: child-joint bind-pose position;
- plane normal: parent -> child bone axis;
- selected-side bone: the checked bone on that transition.

After the bone-weight region has been extracted and epsilon-welded, each open boundary loop is matched to the nearest plausible bone cut plane.

Matched loops are:

1. projected onto the plane;
2. rigidly reweighted to the selected-side bone;
3. closed by a flat fan cap;
4. tagged so later edge collapses cannot cross from the planar cap into the body surface.

Edge collapses inside one planar cap are allowed, but their new vertex positions are reprojected onto the same cut plane. This preserves a flat, low-cost end section while the visible/functional outer side of the proxy remains body-following.

Loops that cannot be matched safely to a bone transition use the existing fallback cap instead.

The result panel reports:

- detected bone cut planes;
- planar caps;
- fallback caps;
- number of boundary vertices snapped to cut planes.

### Mesh Quality

The quality slider is a **target triangle ratio**, not a destructive merge radius.

The pipeline is:

```text
bone-region extraction
  -> epsilon weld
  -> seal open boundaries
  -> verify closed 2-manifold
  -> topology-safe edge collapse
  -> surface offset
  -> final topology check
```

The edge-collapse reducer only collapses existing connected interior edges. A collapse is rejected when it violates the manifold link condition, flips/severely folds neighboring triangles, or would make a simplification pass produce boundary/non-manifold edges.

If a requested ratio cannot be reached safely, simplification stops early. The result panel reports the requested target, achieved triangle ratio, safe collapse count, and stop reason. This is intentional: preserving the closed volume required by stencil parity has priority over reaching an exact polygon count.

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
- triangles before simplification;
- target triangles from Mesh Quality;
- final proxy vertex/triangle count;
- achieved triangle ratio;
- accepted safe edge-collapse count;
- simplifier stop reason when the target could not be reached;
- whether selection filtering was bypassed;
- per-selected-bone influenced vertex count (up to 16 selected bones);
- boundary edges before sealing and after final generation;
- non-manifold edges after final generation.

When **Select All** is active, the result must say that bone selection was bypassed. If the upper body is still missing in that state, the defect is outside bone selection and should be investigated in geometry/index/weld/seal/rendering.

## Shader

### 0.0.12 validation shader

The 0.0.10 surface-fragment fade was rejected because it only darkened rendered mesh fragments and did not behave as an enclosing proximity field.

The 0.0.12 renderer separates **volume detection** from **color application**.

The proxy mesh is used only for stencil parity. Each shell/core then resolves its color with one generated fullscreen helper triangle.

This prevents concave or overlapping proxy surfaces from blending the same fade level multiple times into one pixel. A pixel is darkened once per shell membership, independent of how many proxy faces project onto that pixel.

The generated proxy asset therefore contains one additional tagged helper triangle (3 vertices / 1 triangle). It is appended only after sealing, simplification, and final topology diagnostics, so it is excluded from manifold validation and from the reported proxy geometry triangle count.

0.0.11 returns to closed-volume shell detection, but changes the layout substantially.

The shader uses:

- eight nested fade shells;
- one inward-shrunk core volume;
- stencil parity for every shell/core;
- cumulative alpha compensation;
- optional subtle screen-space dithering.

The fade shells span the full interval:

```text
+Fade Distance
      |
      | outer fade
      |
body proxy surface
      |
      | inner fade
      |
-Core Inset
      |
     Core
```

So the body surface is no longer the mandatory point where the fade ends. The transition can continue inside the proxy before the solid-black core starts.

#### Shell placement

Eight shell samples use band midpoints, then pass those through a smoothstep-shaped position mapping. This makes shell positions denser near the outer edge and near the core boundary.

Each shell contributes one eighth of the requested cumulative fade opacity. Keeping per-shell opacity increments equal minimizes the size of each temporal brightness jump. The color resolve is fullscreen and stencil-gated, so mesh overlap no longer increases opacity.

#### Core Inset

`Core Inset` moves the core volume inward along skinned vertex normals.

This is useful for delaying solid black until the camera has penetrated slightly past the body surface.

Large negative-normal offsets can self-intersect or collapse in thin/concave regions, so this is a validation control rather than a guaranteed geometric operation. Values around 0.005-0.015 m are the recommended first test range.

#### Shell Dither

`Shell Dither` adds a small stable screen-space variation to each shell's incremental alpha. It does not make the distance field mathematically continuous; it is intended only to break up a uniform full-screen shell step.

Set it to 0 to compare pure eight-shell behavior.

#### Cost

Eight shells plus the core require:

```text
8 shells x (parity + color) = 16 passes
1 core   x (parity + color) =  2 passes
---------------------------------------
total                         18 passes
```

This is deliberately a quality/performance validation shader. The generated proxy is expected to be heavily reduced before deciding whether this pass count is acceptable.

The shader uses stencil bit `128`.

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
- Planar cap matching is based on bind-pose bone transitions and spatial proximity; unusual rigs may fall back to the generic cap.
- The topology-safe reducer is intentionally conservative and may stop well above very aggressive target ratios.
- It uses shortest-edge style collapse cost with normal and skin-weight penalties rather than a full production QEM implementation.
- Complex branching/non-manifold boundary loops may not be sealable by the simple fan-cap implementation.
- Self-intersection is not resolved. Inward Core Inset can therefore fail on sufficiently thin or concave geometry.
- The 0.0.11 validation shader uses eighteen passes and stencil bit 128.
- Runtime/VR validation is still required before merging to `dev`.
- Editor-time simplification cost increases with source polygon count and aggressive quality targets.

## Legacy baseline

The original menu remains temporarily for comparison:

`orz_Shop > Near Shader Proxy Mesh Generator`

It is the 0.0.1 full-body baseline and does not implement bone-region extraction.
