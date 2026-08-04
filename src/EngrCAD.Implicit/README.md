# EngrCAD.Implicit

The implicit (signed distance field) geometry engine. A model is an AST of `Sdf` nodes:
negative inside, zero on the surface, positive outside. Depends only on `EngrCAD.Core`.

## Contents

- **`Sdf`** (abstract base) — `Evaluate(point)`, batch `Evaluate(span, span)`,
  finite-difference `Normal` (and batched `Normals`, optionally reporting |grad|), and
  conservative `Bounds` propagated through every node
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
  up front (thread-safe, deterministic first-publish-wins) — see
  [Sparse lazy grids](#sparse-lazy-grids) for how large a domain that reaches.
- **Narrow-band grids** (`NarrowBand(cellSize[, region][, bandWidth])`, `NarrowBandSdf.cs`,
  g3's `MeshSignedDistanceGrid`): evaluate the source **only near its surface** and fill
  the rest by a distance transform — see [Narrow-band grids](#narrow-band-grids).
- **2D-profile solids** (`PlanarRegions.cs`): `Sdf.ExtrudedRegion(region, height)` and
  `Sdf.RevolvedRegion(region)` build exact solids from any `IPlanarRegion` — a 2D signed
  distance supplied by a higher layer (`SketchRegion` in EngrCAD.Modeling). `IPlanarRegion`
  carries a **batch seam** alongside the scalar one, `SignedDistance(x, y, distances)`,
  defaulted to a scalar loop so implementers need not provide it; an override must return
  the same double per point, bit for bit. Both nodes drive it from `EvaluateBatch`, and the
  extruded node asks the region **once per distinct (x, y)** rather than once per sample —
  a prism's field does not vary along z and bulk consumers sample z fastest, so a batch is
  normally a few long constant-xy runs. That is an exact memoization (same input, same
  double), not an approximation; the run test is an identity comparison, so a coordinate an
  ulp away simply misses the cache. Measured 10.5× on polygonizing a 108-segment engraved
  profile.
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

## Batched gradients (`Sdf.Normals`)

A gradient costs six evaluations, so a Hermite consumer — dual contouring's vertex
placement above all — wants them by the thousand rather than one at a time.
`Normals(points, normals, epsilon?)` drives one batch of six times the length through the
same `EvaluateBatch` seam, and is **bit-for-bit identical to the scalar `Normal`** at the
same epsilon for the same reason the batch distance entry is: the probe coordinates are the
same expressions, the seam is contractually bit-identical to the scalar evaluator, and the
difference and normalization are the same two operations in the same order.

The overload taking a `Span<double> gradientMagnitudes` also reports **|grad|**, which the
unit normal throws away and which a caller needs to turn a field value into a distance. It
is 1 for every exact distance field and less than 1 wherever the field is the lower bound
the smooth operators document — measured, an exact field reads 1 to 1e-6 at over 95% of
sample points and never above it, the exceptions being the medial axis and creases where
the gradient does not exist and a central difference straddles two branches (worst 0.99959).

`epsilon` is ABSOLUTE and the default is inherited from `Normal`; a bulk caller working at
a known scale should pass one relative to it, since a central difference's round-off floor
is ~eps·|d|/h. `SurfaceNets` passes 1e-4 of its grid cell, which keeps that floor under
1e-9 at every resolution while staying far too small to straddle any feature the grid
resolves.

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

The 2D side of the planar-region nodes *is* vectorized, one layer up: `SketchRegion`
(EngrCAD.Modeling) implements the `IPlanarRegion` batch seam with lane-wise kernels for
lines, full circles, partial arcs, cubic béziers and elliptical arcs, to the same
bit-for-bit contract. Three of them could not be transcriptions — a partial arc's in-sweep
test is `Math.Atan2`, a bézier's Newton stage has a data-dependent `break`, and an
elliptical arc's distance is a scan-and-Newton over `Math.Cos`/`Math.Sin` — so they carry
their own exactness arguments (a certainty band that hands ambiguous lanes back to
`Atan2`, a masked write rather than a masked iteration, and a baked scan with a
deliberately scalar refinement). See that project's README.

**The ellipse kernel put a number on the standing "no vector transcendental" rule.**
.NET 10 does ship `Vector.Cos`/`Vector.Sin` for `Vector<double>`, so the question is
answered by measurement rather than by absence: against `Math.Cos`/`Math.Sin` over 200 000
doubles on this machine (win-x64, `Vector<double>.Count` = 4), **11 858 differ for `Cos`
and 19 172 for `Sin`, each by one ulp**. One ulp is more than any field here can spend —
its *sign* drives boolean classification kernel-wide — so the same verdict that keeps the
gyroid and the exponential falloff scalar covers the ellipse's Newton refinement, and what
gets vectorized is only the part that is pure arithmetic.

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
`Vector3d[]` corner array. (That corner array is gone: `Polygonize` now generates
coordinates from the grid indices straight into the deinterleaved
`Evaluate(x, y, z, distances)` entry and streams the grid in a window of x-slabs — see the
Interop README.)

## Sparse lazy grids

`Sampled(region, cellSize, lazy: true)` is the sparse variant: 16³-sample blocks are filled
on first touch and never otherwise, so a query pattern that only visits part of the domain
only pays for that part. Two things make it work on domains a dense bake cannot reach:

- **A dense bake is capped by `int` addressing** — its values are one contiguous `double[]`,
  so about 1290³ samples is the wall, and `RequireDenseAddressable` says so and names the
  lazy overload as the way out. A lazy grid has no such cap.
- **The block table is flat while that is free and grouped once it is not.** A flat array of
  block pointers costs 8 bytes per block *whether or not the block is ever touched*. Up to a
  1024³ grid that is 2 MB, cheaper than the indirection it would save, so the flat table
  stays and existing models keep exactly the lookup they had (measured 6.2–6.9 Mpts/s before
  and after). A 4096³ grid is 256³ blocks — 134 MB of pointers up front to index a surface
  that may occupy well under 1% of them. Above the threshold, blocks group into 16³-block
  super-blocks whose slot tables are allocated on first touch: those 134 MB become a 32 KB
  top array plus 32 KB per super-block visited. Both levels publish lock-free by
  `Interlocked.CompareExchange`; block values are deterministic, so a racing fill just loses
  and is dropped.

Measured (`SparseGridBenchmark`; `MeshSdf` over 47 724 triangles, 40 000 probes in a shell
around the surface): at a 0.01 cell (20.3 M samples) a dense bake is 18 816 ms and 156.5 MB
against 8 976 ms and 28.9 MB sparse; at a 0.0012 cell (11.7 G samples, 87 GB of doubles) the
dense bake **throws** and the sparse grid answers 2 000 probes in 80.6 MB. Honest caveat: at
a coarse cell the shell reaches most of the grid anyway, and then the sparse grid buys memory
and nothing else — it is a *localized-query* acceleration, not a faster bake.

geometry3Sharp has both halves of this (`DSparseGrid3` block-hashed, `BiGrid3` two-level) and
neither was worth porting: `BiGrid3` is an unfinished stub with no value API and no in-repo
consumer, and `DSparseGrid3` hashes `Vector3i` keys into a plain `Dictionary` with no
thread-safety story, allocate-on-read defaults and bounds that never shrink after a free.
`HBitArray`, the third class the backlog named, ships with a bug its own author flagged in a
comment (clearing one bit clears its parent summary bits unconditionally, so sparse iteration
can silently skip live siblings) — and we have no use for it: the block table *is* the
occupancy index, and nothing here iterates allocated blocks.

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
