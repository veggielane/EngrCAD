# EngrCAD.Implicit

The implicit (signed distance field) geometry engine. A model is an AST of `Sdf` nodes:
negative inside, zero on the surface, positive outside. Depends only on `EngrCAD.Core`.

## Contents

- **`Sdf`** (abstract base) — `Evaluate(point)`, batch `Evaluate(span, span)`,
  finite-difference `Normal`, and conservative `Bounds` propagated through every node
  (infinite for unbounded fields).
- **Primitives** (exact distances, Quilez forms): sphere, box, cylinder, torus, capsule,
  half-space, and a gyroid lattice (approximate distance, unbounded — intersect with a
  finite solid).
- **Operators**: union / intersection / difference (also as `a | b`, `a & b`, `a - b`),
  polynomial smooth blends (`SmoothUnion` etc. — lower-bound distances near the blend),
  `Offset`, `Shell`, `Translate`, `Rotate`, uniform `Scale`.
- **Sampled grids** (`Sampled(cellSize)` / `Sampled(region, cellSize)`, g3's
  `DenseGridTrilinearImplicit` + `ImplicitFieldSampler3d`): bake any `Sdf` onto a dense
  uniform grid (rows fed through the batch `Evaluate` seam) and evaluate by trilinear
  interpolation — the standard acceleration for expensive ASTs like `MeshSdf`. Pass
  `lazy: true` (g3's `CachingGridImplicit3d`) to materialize 16³-sample blocks on first
  touch instead of up front (thread-safe, deterministic first-publish-wins).
- **N-ary operators** (`NaryOperators.cs`, g3 `ImplicitNaryUnion3d`/`ImplicitBlend3d`
  spirit): static `Sdf.Union(...)` / `Sdf.Intersection(...)` evaluate min/max over any
  number of children in one flat loop (each child evaluated once per query — no nested
  binary trees); static `Sdf.SmoothUnion(children, blend)` folds the polynomial smooth
  min pairwise (coincides exactly with chained binary `SmoothUnion`, reduces exactly to
  hard min outside the band; bounds expand by max(k, (n−1)k/4)); and
  `Sdf.Blend(a, b, blendDistance, Falloff)` adds fillet material where *both* surfaces
  are within `blendDistance`, weighted by a `Falloff` kernel — `Wyvill` (1−t²)³ with
  compact support (exactly the union outside the band) or `Exponential` Blinn Gaussian
  (C^∞, converges to the union). All keep the sign-exactness contract below.

Meshes can join the AST via `EngrCAD.Interop`'s `MeshSdf`, and any finite `Sdf` converts
to a mesh via `SurfaceNets.Polygonize`.

## Notes

- Distances from smooth/blend operators are correct in sign everywhere but exact in
  magnitude only away from blend regions — fine for Surface Nets meshing.
- Smooth blends with `blend <= 0` degrade to the exact hard operator — field *and*
  bounds (the expansion clamps at 0; a negative blend never shrinks conservative
  bounds). Same policy binary, n-ary, and `Sdf.Blend`.
- Sampled-grid values are approximate: exact at grid nodes, trilinear between (error
  O(h²) where the field is smooth, O(h) across creases), so the sign is reliable only
  where the cell size resolves the features — thin walls/gaps under a cell can vanish
  or fuse. Outside the baked region the node returns boundary value + distance to the
  region: continuous, and correct in sign whenever the solid lies inside the baked
  region (the bounds-based `Sampled(cellSize)` overload guarantees this).
- Future: SIMD batch evaluation; C# expression-tree → SDF compilation for the query
  layer.
