# NearShaderProxyMeshGenerator

Unity Editor prototype for generating a closed, skinned proxy volume from an avatar/body `SkinnedMeshRenderer`.

The proxy is backing geometry for a camera near/proximity shader. It is not a visual LOD mesh.

## Branch policy

- `main`: release / stable
- `dev`: integration
- `feature/*`: implementation / experiment

Current prototype: `feature/proxy-mesh-prototype`

## 0.0.10 continuous surface-distance fade

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
- Fade To Core: `1.0`
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

### 0.0.10 continuous surface-distance fade

The stepped shell approximation has been removed.

The shader now uses three passes total:

1. **CORE_PARITY** — stencil parity determines whether each camera ray starts inside the closed proxy;
2. **SURFACE_FADE** — outside only, one surface pass computes continuous camera-to-fragment distance;
3. **CORE_BLACK** — inside only, applies `Core Black Strength` and clears the stencil bit.

Outside the proxy, the fade value is evaluated per fragment:

```text
surfaceDistance = distance(camera, proxy surface fragment)
proximity       = 1 - surfaceDistance / FadeDistance
opacity         = smoothstep(0, 1, proximity)
                  * CoreBlackStrength
                  * FadeToCore
```

There are no expanded fade shells and therefore no spatial band boundaries.

`Fade To Core = 100%` is the continuity setting: immediately outside the proxy surface, the fade approaches the same opacity used by the core. Lower values intentionally leave a darker jump when the camera crosses into the core.

The fade pass runs only where the parity stencil says the camera is outside the proxy. The core pass runs only where parity says it is inside.

This changes the rendering cost from the 0.0.9 ten-pass shell implementation to three passes.

Important limitation: this is camera-to-rendered-surface-fragment distance, not an exact nearest-point signed distance field. It is intended as a practical continuous proximity fade for the body-following proxy.

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
- Self-intersection is not resolved.
- The volume shader uses three passes and stencil bit 128.
- Continuous surface fade is based on camera-to-fragment distance, not a true animated SDF / exact nearest-point distance.
- Runtime/VR validation is still required before merging to `dev`.
- Editor-time simplification cost increases with source polygon count and aggressive quality targets.

## Legacy baseline

The original menu remains temporarily for comparison:

`orz_Shop > Near Shader Proxy Mesh Generator`

It is the 0.0.1 full-body baseline and does not implement bone-region extraction.
