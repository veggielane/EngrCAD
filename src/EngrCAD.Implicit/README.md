# EngrCAD.Implicit

The implicit (signed distance field) geometry engine. A model is an AST of `Sdf` nodes:
negative inside, zero on the surface, positive outside. Depends only on `EngrCAD.Core`.

## Contents

- **`Sdf`** (abstract base) — `Evaluate(point)`, batch `Evaluate(span, span)`,
  finite-difference `Normal`, and conservative `Bounds` propagated through every node
  (infinite for unbounded fields). Batches come in two shapes: interleaved
  `Evaluate(ReadOnlySpan<Vector3d>, Span<double>)` for callers holding point arrays, and
  **deinterleaved `Evaluate(x, y, z, distances)`** for callers that generate coordinates
  procedurally (grid sampling) — the latter skips the transpose entirely and lets a caller
  stream an arbitrarily long run through a fixed-size coordinate buffer instead of
  materializing one `Vector3d` per sample. Both drive the same internal `EvaluateBatch`
  SIMD seam, chunked identically, so they agree bit for bit.
- **SIMD batch evaluation** (`BatchEvaluation.cs`) — the batch entry point is the
  throughput path, and it is vectorized; see [Batch evaluation](#batch-evaluation-simd).
- **Primitives** (exact distances, Quilez forms): sphere, box, cylinder, cone
  (`Sdf.Cone(r1, r2, height)` capped frustum; a zero radius gives a pointed apex),
  torus, capsule, half-space, and a gyroid lattice (approximate distance, unbounded —
  intersect with a finite solid).
- **`Sdf.Thread`** (`ThreadSdf.cs`) — helical thread solid about +Z (straight-flanked
  trapezoidal form; the ISO 60° V-profile is the intended special case): the 2D profile
  repeated along the helical coordinate w = z − pitch·θ/2π. **Sign is exact** (wrapped
  2D membership test), magnitude is the exact 2D profile distance scaled by cos(lead
  angle at the root radius) — a lower-bound-style distance near the threaded surface,
  approximate deep inside. `profileOffset` dilates/erodes the profile normal to its
  boundary (the 3D-printing clearance mechanism, exact as a distance shift); optional
  45° start/end chamfer cones. Finite: z ∈ [0, length], conservative cylinder-box
  bounds.
- **Operators**: union / intersection / difference (also as `a | b`, `a & b`, `a - b`),
  polynomial smooth blends (`SmoothUnion` etc. — lower-bound distances near the blend),
  `Offset`, `Shell`, `Translate`, `Rotate`, uniform `Scale`, and `Mirror(point, normal)`
  (reflects the query point — an isometry, so distances stay exact).
- **Sampled grids** (`Sampled(cellSize)` / `Sampled(region, cellSize)`, g3's
  `DenseGridTrilinearImplicit` + `ImplicitFieldSampler3d`): bake any `Sdf` onto a dense
  uniform grid (rows fed through the batch `Evaluate` seam; dense bakes parallelize
  over k-slabs via `ParallelFor.Blocks` — every sample computed from its own (i, j, k),
  so the bake is bit-for-bit deterministic) and evaluate by trilinear interpolation —
  the standard acceleration for expensive ASTs like `MeshSdf`. Pass `lazy: true` (g3's
  `CachingGridImplicit3d`) to materialize 16³-sample blocks on first touch instead of
  up front (thread-safe, deterministic first-publish-wins).
- **Narrow-band grids** (`NarrowBand(cellSize[, region][, bandWidth])`, `NarrowBandSdf.cs`,
  g3's `MeshSignedDistanceGrid`): evaluate the source **only near its surface** and fill
  the rest by a distance transform — see [Narrow-band grids](#narrow-band-grids).
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

## Batch evaluation (SIMD)

`Evaluate(ReadOnlySpan<Vector3d>, Span<double>)` is the throughput path every bulk
consumer uses (Surface Nets sampling, `Sdf.Sampled` bakes, section contours). It is
vectorized through `System.Numerics.Vector<double>` — width-agnostic, so one kernel
serves 128-bit NEON, 256-bit AVX2 and 512-bit AVX-512 without per-ISA forks.

**Layout: structure-of-arrays, transposed once at the root.** A lane-wise kernel wants
all the x's contiguous, but the public signature hands over interleaved `Vector3d`s
(which is right for callers — they all have AoS point arrays). So the base
`Evaluate` deinterleaves into pooled x/y/z scratch **once, at the root of the AST**, in
1024-point chunks (24 KB of coordinates — cache resident, and bounded no matter how many
points the caller passes), then drives the internal SoA seam:

```csharp
protected internal virtual void EvaluateBatch(
    ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
```

Operators forward those same spans straight to their children, so the transpose is paid
once per batch rather than once per node, and every kernel below the root reads and
writes contiguous doubles. (Measured: the transpose is ~1 ns/point and must be written
against raw references — four span bounds checks per point cost more than a cheap kernel
saves; going through indexers halved whole-batch throughput on `Sdf.Box`.)

**Exactness.** Every vector kernel mirrors its scalar `Evaluate` term for term, in the
same association order, using only correctly rounded IEEE-754 operations, and the ragged
tail past the last full register falls back to the scalar path. Results are therefore
bit-for-bit identical to per-point scalar evaluation for all finite inputs, with one
documented deviation: `Vector.Min`/`Vector.Max` break a tie between +0.0 and −0.0 by
operand position while `Math.Min`/`Math.Max` break it by sign, so a result that is exactly
zero may carry the opposite sign bit (±0 compare equal; no consumer can tell them apart).
`BatchEvaluationTests` asserts this over the whole node catalogue at randomized points,
at structured points that land exactly on surfaces/edges/corners, and at every batch
length around the register and chunk boundaries. The suite also passes with
`DOTNET_EnableHWIntrinsic=0`, which drives every kernel down its scalar tail loop — the
no-SIMD fallback is exercised, not assumed.

**What is vectorized**: every primitive except the gyroid (sphere, box, cylinder, cone,
torus, capsule, half-space), every set operator and smooth blend, offset/shell, and the
translate/rotate/mirror/scale transforms, plus the n-ary union/intersection/smooth-union
and the Wyvill falloff blend. **Deliberately not vectorized**: the gyroid and the
exponential falloff (they need `Math.Sin`/`Math.Exp`, and no vector transcendental
reproduces those bit for bit — a silently divergent fast path is worse than none), and
`ThreadSdf` / the sampled grids / planar-region extrusions (branchy or gather-bound).
They still batch through the default `EvaluateBatch` loop and still benefit from
vectorized operands around them.

Measured on an 8-core win-arm64 box (`Vector<double>.Count == 2`, so the ceiling from
lanes alone is 2×; the rest comes from batching away per-point virtual dispatch through
the AST), best-of-9 after a 1 s warm-up:

| case | before | after | |
| --- | --- | --- | --- |
| primitive union of 5, 2M points | 38.4 Mpts/s | 132.6 Mpts/s | **3.5×** |
| bracket CSG tree, 2M points | 17.2 Mpts/s | 58.1 Mpts/s | **3.4×** |
| capsule / cone, 2M points | 78 / 83 Mpts/s | 502 / 343 Mpts/s | **6.4× / 4.1×** |
| box / cylinder / torus, 2M points | 426 / 444 / 518 Mpts/s | 626 / 594 / 724 Mpts/s | 1.3–1.5× |
| sphere, 2M points | 1090 Mpts/s | 887 Mpts/s | 0.81× |
| `Sampled` bake, primitive union | 12.2 ms | 3.9 ms | **3.1×** |
| `Sampled` bake, bracket CSG | 28.7 ms | 10.0 ms | **2.9×** |
| `SurfaceNets.Polygonize` @ 220 | 195 ms | 156 ms | 1.25× |

Two honest caveats. A bare sphere at the *root* of the AST is slower: its kernel is five
flops, so at two lanes the transpose costs more than SIMD saves (on 4- or 8-lane hardware
this inverts). And `Polygonize` gains far less than its sampling does — the sampling pass
is now a minority of its cost, the rest being the topology passes and the full-size
`Vector3d[]` corner array; feeding Surface Nets deinterleaved coordinates directly is the
obvious follow-up, on the Interop side.

## Narrow-band grids

`sdf.NarrowBand(cellSize, bandWidth)` bakes a grid that pays for source evaluations only
near the surface. It is this project's answer to g3's `MeshSignedDistanceGrid` ("exact
distances in a narrow band, then extend outward by sweeping"), generalized off meshes:

- **Finding the band** — recursive octant subdivision down to 4³-sample leaves. Each node
  is probed once at its centre; if `|d(centre)| − circumradius > bandWidth` the whole node
  is provably clear of the band (the true distance is 1-Lipschitz and the field's magnitude
  is a lower bound on it), so it is stamped with the sign of `d(centre)` and skipped.
  Surviving leaves are evaluated in full through the batch/SIMD seam. g3 instead rasterizes
  triangle bounding boxes and signs the grid by ray-crossing parity; we need neither,
  because an `Sdf` is sign-exact by contract — which also means this accelerates *any*
  expensive field, `MeshSdf` included.
- **Filling the rest** — a chamfer distance transform over the 26-neighbour mask with true
  Euclidean step lengths ⟨1, √2, √3⟩. One causal raster scan plus one anti-causal scan is
  the *complete* chamfer distance, so unlike eikonal fast sweeping there is nothing to
  iterate to convergence.
- **Leaf size is THE knob**: a surviving leaf is evaluated whole, so the exactly-evaluated
  shell is `2·(bandWidth + leaf circumradius)` thick and the circumradius is √3·(n−1)/2
  cells — 6.1 cells at n = 8 but 2.6 at n = 4, which nearly halves the shell. Hence 4.

Fidelity, stated honestly: inside the band the values are the source's own, so the
reconstruction is exactly a dense bake there — and since the zero level set lies inside the
band, meshing and inside/outside classification are unaffected. Outside the band the sign
is still exact at every sample, but the magnitude is a chamfer approximation. It is a
provable **over**-estimate (each far sample is min(exact band value + chamfer path), and
the true distance is 1-Lipschitz) by up to ~13% — Borgefors' anisotropy bound for this
mask; the measured worst case on a sphere is 1.112×. Borgefors-optimized weights would cut
that to ~1.4% but forfeit the over-estimate invariant, which is worth more than the last
decimal of a far-field value. Don't use a narrow-band grid as a sphere-tracing lower bound,
and don't offset it by more than the band width.

**When it pays.** The saving is in source evaluations; the outward fill still touches every
sample twice at ~60 ns each and raster scans don't parallelize. Measured against a dense
`Sampled` bake of the same grid:

| source | cell | dense bake | narrow band | |
| --- | --- | --- | --- | --- |
| `MeshSdf` (sphere, 8k faces) | 0.35 | 785 ms | 93 ms | **8.4×** |
| `MeshSdf` (sphere, 8k faces) | 0.18 | 2997 ms | 269 ms | **11.1×** |
| bracket CSG (analytic) | 0.25 | 100 ms | 315 ms | 0.3× |
| primitive union (analytic) | 0.12 | 23 ms | 215 ms | 0.1× |

So: reach for it for mesh-backed and otherwise expensive fields — and note the ratio
*improves* with resolution, because the shell thickness scales with the cell size while
the region does not. For a cheap analytic tree it is a pessimization; use `Sampled`.

**Benchmark timing lesson**: tiered JIT promotes these inner batch methods only after
tens of invocations plus a background compile. A single warm-up call measured tier-0 code
and reported `Sdf.Box` at anywhere from 147 to 548 Mpts/s across runs — every number above
comes from a fixed wall-clock warm-up budget, not a warm-up count.

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
- Future: C# expression-tree → SDF compilation for the query layer; narrow-band mesh
  SDFs and sparse/multiresolution grids on top of the lazy-grid seam.
