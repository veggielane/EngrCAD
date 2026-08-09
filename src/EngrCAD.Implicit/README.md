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
- **`Sdf.LipschitzBound(region)`** — how fast this field's value can change per unit of
  movement, anywhere in a stated region. 1 by default, which is the whole engine's standing
  contract; the domain operators below break it on purpose and report by how much. See
  [The Lipschitz bound](#the-lipschitz-bound-and-why-it-exists).
- **Primitives**: sphere, box, cylinder, cone (`Sdf.Cone(r1, r2, height)` capped frustum;
  a zero radius gives a pointed apex), torus, capsule, half-space, **rounded box**,
  **round cone** (the hull of two spheres), **link**, **regular n-gon prism**
  (`Sdf.Prism(sides, r, h)` — `3` and `6` are the triangular and hexagonal prisms),
  **wedge** (the field twin of `Shape.Wedge`) and **pyramid**, all exact distances; plus
  **`Sdf.Ellipsoid`** (a bound — see [The ellipsoid](#the-ellipsoid-the-one-primitive-with-no-closed-form)),
  **`Sdf.ConvexPolyhedron(halfSpaces)`** (**exact**, by taking the minimum over its own
  boundary triangles outside — the vertices were already being enumerated for the FINITE
  bounds it reports where `Sdf.Intersection` over the same half-spaces reports infinity;
  `ConvexDistance.HalfSpaceBound` asks for the cheap correct-sign lower bound instead).
- **Lattices** (`Tpms.cs`, `StrutLattice.cs`, `LatticeGrading.cs`): eight triply periodic
  minimal surfaces as sheets or networks (`Sdf.TpmsSheet` / `Sdf.TpmsSolid`, `Sdf.Gyroid`
  still naming the one everybody reaches for) and six strut lattices (`Sdf.StrutLattice`),
  each also available **graded** — a thickness, diameter, level or volume fraction that
  varies over space. Both unbounded — intersect with a finite solid. **Two families, two
  distance contracts, and the difference is the point**:
  see [Lattices](#lattices-two-families-two-contracts).
- **Domain operations** (`DomainOperators.cs`): `Repeat(spacing)` / `Repeat(spacing, counts)`,
  `Twist`, `Bend`, `Taper`, `Elongate`, `Displace` — see
  [Domain operations](#domain-operations).
- **`Sdf.Compile()`** — the AST flattened to one delegate via expression trees, bit-for-bit
  identical to the scalar path. Measured, and mostly a decline: see
  [Compiling an AST](#compiling-an-ast).
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

## Domain operations

A domain operation moves the QUERY POINT rather than combining two values, which is what
makes it cheap: a lattice of ten thousand instances costs one primitive evaluation per
neighbouring cell. The file divides cleanly in two, and the division is the design.

**Isometries — the distance stays a distance.** `Translate`, `Rotate`, `Mirror` and
`Repeat` move points without changing lengths, so composing a field with them changes
nothing about what the value means. `Elongate` joins them: its map is 1-Lipschitz per
component, exact outside the stretched body (an elongated sphere is bit-identical to
`RoundedBox`) and a strict lower bound inside the clamped core.

**Non-isometries — the value stops being a distance.** `Twist`, `Bend` and `Taper` shear
or stretch space, so the composed value changes faster than the query point moves. What
survives is the **sign**, exactly (the solid is exactly the pre-image of the child); what
does not is the magnitude, which becomes an over-estimate by at most the factor
`LipschitzBound` reports. `Displace` is a fourth case and the odd one: it adds a value
rather than moving a point, so it is not a distance at all — the solid is exactly
`{d + ripple < 0}` by definition, and the Lipschitz constant rises by `amplitude · |frequency|`.

**`Displace`'s BOUNDS rest on a property of its child that is narrower than it looks.**
Material appears wherever the child reads below the amplitude, so what
`Bounds.Expanded(amplitude)` needs is `{child < t} ⊆ child.Bounds.Expanded(t)` — *the child
never reports less than the per-axis escape from its own bounds* — which is weaker than "the
field is a true distance", and which the whole CSG family satisfies by induction: an exact
primitive gives at least its own escape, a union's minimum is at least the escape from the
union of the boxes, and an intersection's maximum is the per-axis maximum of its operands'
escapes, which IS the escape from the intersected box. So the standing example of an
under-reporting field — a difference near its tool's fictitious faces — is in fact covered,
measured at 0.0 over fifteen nodes including a tangent-sphere intersection.

What is *not* covered is a child whose own bounds were widened relative to the field it
reports, which is the non-isometries: `Sphere(1).Taper(1, 3)` reads **0.667** at a point
**1.0** outside its box, so a ripple of amplitude 0.8 raises material the reported bounds do
not contain. `Displace(amplitude, frequency, bounds)` is the stated-region overload for
that, and both halves are pinned by test.

**Repetition visits two cells per repeated axis, and that is not a refinement — it is the
correctness condition.** The single nearest-cell form every shader implementation uses is
*discontinuous* at a cell boundary for a child that is not symmetric about its cell centre,
because the map jumps by a whole spacing there; a discontinuous field is Lipschitz at no
constant, so the polygonizer's cull could not be widened to cover it and it would report
surface where there is none. Visiting both neighbours makes the field continuous AND makes
the sign exact, given one enforced precondition: the child's bounds must fit inside one
cell. Outside that, a query point can lie inside an instance the evaluation never looks at
— which is a wrong SIGN, the one thing this engine cannot absorb — so it is refused by name
with the span it measured, rather than left as a caveat. The identity that verifies it:
a lattice must equal an explicit `Sdf.Union` of translated copies **bit for bit**, since
both spell `child(p − spacing·n)`.

**One derivation covers all three non-isometries.** Each of their Jacobians reduces, after
an orthogonal change of basis (free — singular values are invariant under one), to a 2×2 of
the form `[[g, w], [0, 1]]` beside an untouched unit direction. `DomainMath.ShearedScaleNorm`
is that matrix's spectral norm in closed form, and the three supply their own `(g, w)`:

| operator | g | w | note |
|---|---|---|---|
| twist | 1 | `rate · r` | reduces to the tidy `(k + √(k²+4))/2` |
| bend | `1 − k·y` | `k·x` | the Jacobian is this matrix transposed; a matrix and its transpose share singular values |
| taper | `1/f` | `r·f′/f²` | beside a second `1/f` direction |

The function increases in both arguments, so substituting each one's largest magnitude over
a region bounds the norm over that whole region.

## The Lipschitz bound, and why it exists

`Sdf.LipschitzBound(region)` reports an upper bound on how fast the value can change per
unit of movement. It is **1 for everything that existed before the domain operators** —
every primitive here is an exact distance, and every operator either combines values (min,
max and the polynomial smooth min, whose gradient is a *convex combination* `h·∇a + (1−h)·∇b`
of its operands') or moves points by an isometry.

It exists because three consumers all reason from |d| in the same way — *a value of |d|
proves there is no surface within |d| of here* — and that is false by exactly this factor
for a twisted, bent or tapered field:

- `SurfaceNets`' block cull skips a block when `|d(centre)| > L·R`;
- the narrow-band octree skips a node when `|d(centre)| − L·R > bandWidth`;
- `SdfProjectionTarget` steps by `|d| / L`, which is what keeps its one-sidedness guarantee
  (the surface is at least `|d|/L` away in every direction, so a step of that length cannot
  overshoot however wrong the gradient direction is).

Unwidened, each of them would drop geometry **silently**, which is why the bound is asked
for rather than assumed. It takes a REGION rather than being a plain number because a
twist's local factor grows with distance from the axis, so no finite constant is valid over
all of space; a consumer knows the region it is about to sample and asks once. An infinite
bound (an unbounded field under such an operator) means "cull nothing", which is always
correct.

**Overriding it is mandatory for any node that wraps a child**, even one that is itself
1-Lipschitz — a wrapper that inherits the default silently claims 1 for a twisted subtree.
`LipschitzBoundTests` measures secants over the whole catalogue with a twist deliberately
buried inside every wrapper, and includes a wrapper that forgets to propagate, asserted to
be caught: a guard that has not been shown to fire is not a guard.

**One pre-existing gap this surfaced.** A **sampled grid is measurably steeper than what
went into it, by up to √3**: each first difference of the trilinear interpolant spans one
cell so each partial inherits the source's bound, but all three can reach it at once. That
is attained rather than merely permitted — baking `max(x, y, z)`, a 1-Lipschitz field, onto
the unit cell gives the interpolant `1 − (1−x)(1−y)(1−z)`, whose gradient at the corner is
exactly (1, 1, 1). So `Sdf.Sampled` had been breaking the assumption the cull rests on.
Nothing in the repository reached the combination (no production path and no rendered
example bakes a grid and then polygonizes it), which is why it had never surfaced;
`SampledGridSdf` now reports `√3 ×` its source's bound.

## Lattices: two families, two contracts

|  | what it is | distance | what the parameter means |
|---|---|---|---|
| `Sdf.TpmsSheet` / `Sdf.TpmsSolid` | a level set of a trigonometric polynomial | a **lower bound** (1-Lipschitz, exact sign) | the thickness is a guaranteed **minimum** wall |
| `Sdf.StrutLattice` | a periodic union of capsules | **exact** | the diameter is the diameter |

**The TPMS family is not a distance field and the constant it divides by is what makes it
one.** Each surface is `F(p) = 0` for a trigonometric polynomial — the standard nodal
approximation — whose gradient magnitude varies over space, so `|F|` says nothing about
how far the surface is until it is divided by `max |grad F|`. Dividing by the *global*
maximum makes the field 1-Lipschitz, and a 1-Lipschitz function vanishing on the surface
is a lower bound on the distance (`|g(p)| = |g(p) − g(nearest)| ≤ |p − nearest|`) — the
engine's standing contract. Dividing by anything smaller breaks it, and breaks it in the
direction that drops geometry silently.

So every constant is DERIVED, and measured in every case:

| surface | max \|grad F\| | how |
|---|---|---|
| Schwarz P | √3 | `grad F = −(sin x, sin y, sin z)`; attained at (π/2, π/2, π/2), **on the surface** |
| Schwarz D | √3 | after the product-to-sum collapse `F = sin x·cos(y−z) + cos x·sin(y+z)`, the Gram quadratic maxes at 3 |
| gyroid | √3 | at `x = t, y = z = −t` two components are identically 1 and the third is cos 2t |
| Neovius | 7 | `dF/dx = −sin x (3 + 4 cos y cos z)`; the other partials vanish at (π/2, 0, 0) |
| I-WP | 3√3 | `dF/dz = 4 sin z (1 − cos z)` at x = y = 0, maximized at cos z = −½ |
| Lidinoid | 3√3 / 2 | the diagonal lemma at A = 3/2, E = 0 — **exactly**, where the file used to record only that a dense scan landed on it |
| Split P | 2√3·√(1−c\*²)(3c\*/2 + 2/5), c\* = (√454 − 2)/30 | the diagonal lemma at A = 3/2, E = 2/5 — a quadratic surd = 3.620073899187 |
| Fischer–Koch S | √G(v\*/√2) | v\* the root in (0,1) of `3v⁴ + 7v³ − 11v² − 7v + 4` = 2.4439726372930344 |

**The last three share ONE derivation, and it is worth more than three formulas.** Every
polynomial here is CYCLIC in (x, y, z), so the diagonal `x = y = z = t` is invariant under
the cycle and the three partials are equal on it — which makes `|grad F|` there simply
`|F_diag'(t)| / √3`, a *one-variable* problem. For the shape Lidinoid and Split P share,
`a·Σ sin2x sin z cos y + b·Σ cos2x cos2y + e·Σ cos2x`, the restriction is
`F_diag = (3a/2)sin²2t + 3b cos²2t + 3e cos2t`, so

```
F_diag' = 6 sin2t [ (a − 2b) cos2t − e ]   and   |grad F| = 2√3 |sin 2t| |A cos 2t + E|
```

with `A = a − 2b`, `E = −e`, maximized where `2A c² + E c − A = 0`.

**Fischer–Koch S is the one member whose maximum is not on the diagonal** (there it is only
√3, a local maximum), so it has its own invariant family, `(t + 3π/2, t, π/4)`. On it `F`
vanishes identically — the maximum sits *on* the surface, as Schwarz P's does — and
`|grad F|²` collapses to `G(sin t) = 4u⁶ + 8√2u⁵ − 4u⁴ − 12√2u³ − 3u² + 4√2u + 5`. The
substitution `v = √2 u` clears the radicals from its derivative,
`G'(u) = √2 (v+1)(3v⁴ + 7v³ − 11v² − 7v + 4)`, so the maximizer is the root of an INTEGER
QUARTIC — solvable in radicals, which makes the constant an explicit algebraic number of
degree at most 4 over ℚ(√2) rather than the "no closed form found" the file used to record.

**The stored constants are unchanged by any of that, deliberately**: they already round the
supremum UP at the sixth significant figure, which is the safe direction and costs about
three parts per million of wall, while re-storing them would move every rendered lattice for
nothing.

Two test files carry it. `TpmsTests.GradientBound_IsSoundAndTight` re-measures each field's
own slope over a dense grid on one cell plus a hill climb and asserts it is **at most 1 and
at least 0.99** — sound *and* tight, because a bound that is merely large costs wall
thickness in direct proportion; note it takes the gradient by central differences rather
than the largest secant over a fixed set of chord directions, since 26 directions leave up
to 20° to the true gradient and such a probe caps out near 0.94. `TpmsDerivationTests` then
checks each closed form against a global scan AND checks the load-bearing STEP of each
derivation — the diagonal identity on all eight kinds, the family reduction, the quintic's
factorization — because a value can agree by coincidence where a structural claim cannot.

**What the constant costs is wall thickness, and the excess is INHERENT rather than a defect
waiting to be tuned away.** The sheet `|F| ≤ bound·ω·t/2` has local half-thickness
`(bound / |grad F|)·t/2`, so the requested thickness is a **minimum** and the excess is
exactly how far the local gradient falls short of the global maximum. Measured on the level
set, area-weighted (median / worst excess): gyroid 1.15 / 1.24, I-WP 1.19 / 1.68, Schwarz D
1.20 / 1.26, Schwarz P 1.32 / 1.77, Fischer–Koch S 1.41 / 2.24, Split P 1.54 / 4.95,
Lidinoid 1.65 / 27.4, Neovius 2.32 / 37.7 — the last two being the surfaces whose level set
passes a near-critical point of `F`.

**No choice of DIVISOR fixes that**, which is what makes it structural. A sheet is a band of
the level set, and a band's width is `2L/|grad F|`, so it varies wherever the gradient does;
dividing by a different constant (the surface maximum rather than the global one, say) only
rescales the whole distribution, and dividing by the LOCAL gradient would make the wall
first-order uniform at the cost of the 1-Lipschitz contract the field exists to keep — at
Lidinoid's near-critical point the local form is twenty-odd times steeper, so the cull would
have to widen by that and would buy nothing.

So the wall is **reported**: `Tpms.WallThickness(kind, cell, t)` gives the `SheetWall`
(minimum, median, maximum) and `Tpms.SheetForWallThickness(kind, cell, wall)` solves the
nominal thickness for a stated median — a 1.0 mm wall asks for 0.43 on Neovius and 0.87 on
the gyroid. The relation is first order and verified point by point against a direct march
of the sheet's own field: under 3% inside the regime it claims (the band locally parallel,
i.e. an excess factor under two), with the points past it COUNTED rather than quietly
dropped. `SheetWall.Maximum` is an upper bound and over-states exactly there, which is said
on the type rather than left to be discovered.

**Volume fraction is still the parameter offered as the engineering one** —
`Tpms.SheetForVolumeFraction` / `SolidForVolumeFraction` / `StrutLattices.ForVolumeFraction`
solve for it and report what they *achieved* (the `BiArcFit.MaxDeviation` convention).

### Graded lattices

A thickness, a strut diameter, a level or a volume fraction that VARIES over space —
stiffness where the stress is, porosity where the flow is. `LatticeGrading` is the value;
`Sdf.TpmsSheet(kind, cell, grading)`, `Sdf.TpmsSolid`, `Sdf.StrutLattice`,
`Tpms.GradedSheetForVolumeFraction` / `GradedSolidForVolumeFraction` and
`StrutLattices.GradedForVolumeFraction` consume it.

**What is graded is the PARAMETER, never the cell, and that scoping is the whole design.**
Grading the thickness leaves the structure underneath exactly as it was — a TPMS's
polynomial is still periodic, and a strut lattice's fold and three-wide candidate
neighbourhood are arguments about the strut AXES, which do not move — so the exact sign, the
completeness of the visited neighbourhood and the periodicity are all inherited rather than
restated. Grading the CELL SIZE would be a different and much larger feature: the fold stops
being a fold and there is no sound evaluation to fall back on, so it is refused by omission
rather than approximated.

**The Lipschitz constant is STATED, never measured.** Every graded field is
(something 1-Lipschitz) minus (the grading), so its bound is `1 + L` (or `1 + L/2` for a
half-thickness, `1 + L/(bound·ω)` for a level) — and a constant that is too small is the one
failure this engine cannot absorb. So `LatticeGrading.Along` and `.Radial` derive theirs
exactly (a coordinate along a unit direction and a distance from a point are both
1-Lipschitz, and the clamp cannot steepen either), `.Constant` reports 0, and
`.FromFunction` makes the caller say it. A volume-fraction grading is pushed through the
same measured cell distribution the uniform solves use, as a piecewise-linear ladder — so
the composed constant is exact (the map IS the ladder) and a query is a lookup rather than a
bisection.

**A constant grading reproduces the uniform field BIT FOR BIT**, which is the identity that
says the graded path is the same geometry rather than a second opinion about it. What a
graded field gives up is the volume-fraction ESTIMATOR's premise — "sample one cell" needs a
periodic field — so no achieved fraction is reported for one; what the grading states is the
fraction the LOCAL cell would carry, measured at 0.128 and 0.384 for a request of 0.12 and
0.40.

A **conformal** lattice, one following a curved body, is already expressible: `Twist`,
`Bend` and `Taper` compose with any of these fields and each reports its own factor, so the
cull widens correctly through them. A general free-form warp is a different feature and is
not offered.

**Level 0 splits space evenly for five of the eight and measurably not for the rest** —
Schwarz P, Schwarz D, the gyroid, Neovius and Fischer–Koch S have an antisymmetry, while
Split P measures 0.510, I-WP 0.469 and Lidinoid 0.385. Verified two ways: by counting
samples, and by polygonizing the network in a block of whole cells and integrating its
mesh volume, which lands on 0.5000.

**A sheet and a network are different solids**, and the structural statement is about the
VOID: a sheet is the wall between the two labyrinths so its complement falls into two
disconnected pieces, while a network *is* one labyrinth so its complement is a single
piece. Counted by a six-connected flood fill over a sampled block.

### Strut lattices are exact, and `Repeat` cannot build one

A strut is a capsule, whose distance is exact, and the exact distance to a union is the
minimum over its members — so a strut lattice is an exact distance field, `LipschitzBound`
stays 1, and nothing comes out thicker than asked.

`Sdf.Repeat` looks like the way to tile a unit cell and **refuses, correctly**. A
lattice's struts span the whole cell — that is what joins them into a lattice — so a
capsule's bounds overhang by the strut radius on every side, and `Repeat`'s
two-cells-per-axis window is sound only while the child fits inside one cell. Shortening
the axes so the solids fit would make consecutive copies meet at a single tangent point
instead of joining: a pinched lattice rather than a lattice.

So the node folds the query point itself (an isometry, since the set is lattice-invariant)
and visits a three-wide neighbourhood — sound because a copy at index 2 or beyond is at
least a whole cell away, while the nearest strut the window *does* visit is nearer than
that (measured per kind, with the sampling grid's own resolution added back so the bound
is certified rather than sampled). The end-to-end check is stronger: the field is compared
against a brute-force minimum over an explicit 5×5×5 block of capsules, to round-off — not
bit for bit, because the fold is an isometry mathematically and not arithmetically
(`(p − shift) − a` and `p − (a + shift)` are the same real number and different doubles) —
with a companion asserting that comparison can see a missing neighbour.

**The query cost is decided at construction, and three per-query strategies were measured
before that was believed.** All three in one harness, ns per point (win-x64, best of five
after a wall-clock warm-up budget):

| kind | struts/cell | linear over the block | + per-strut box prune | BVH over the block |
|---|---|---|---|---|
| simple cubic | 3 | 440 | 542 | 901 |
| BCC | 4 | 664 | 864 | 1025 |
| FCC | 6 | 975 | 1167 | 1125 |
| octet | 18 | 3808 | 3432 | 2313 |
| diamond | 16 | 2865 | 3009 | 971 |
| Kelvin | 24 | 4669 | 4147 | 1100 |

**None of them is uniform.** The box prune is *worse* than no prune on four of the six,
because it pays a box test per strut to save a segment distance that is only three times
its cost; and a cell-level prune is nearly vacuous for a structural reason — every cell's
box IS the whole cell, since its struts span it by construction. The BVH wins where the
block is large and loses by 2× where it is small.

What is uniform is to prune **once**: the cell is divided into 4³ sub-cells, each keeping
the struts that can be nearest to a point inside it, so a query is a fold, an index and a
short scan. The selection is exact rather than heuristic — the distance to a segment is
convex, so its maximum over a box is at a corner, and the smallest such maximum over the
struts bounds how far a point in that sub-cell can be from the whole block. Measured
through the production batch entry, against the BVH through the same entry:

| kind | BVH | sub-cell lists |
|---|---|---|
| simple cubic | 933 | **132** ns/pt (7.1×) |
| BCC | 1056 | **230** (4.6×) |
| FCC | 1150 | **189** (6.1×) |
| octet | 2385 | **429** (5.6×) |
| diamond | 989 | **335** (3.0×) |
| Kelvin | 1103 | **380** (2.9×) |

For scale, a gyroid sheet is 75 ns/pt and Split P 146, so the two families now cost the
same order. Two representation notes ride along: a face diagonal is two
corner-to-face-centre struts end to end, so representing it as ONE segment is the same set
of material for half the segment distances (which is why the octet reports two strut
lengths); and the FCC cell carries only the three LOW faces' diagonals, since every face
is the low face of exactly one cell.

**The half-open fold is load-bearing for the generated Kelvin cell.** A symmetric round
leaves a midpoint at +cell/2 where it is and one at −cell/2 where it is, so two spellings
of the same lattice point survive deduplication and the cell came back with 36 struts
where the bitruncated cubic honeycomb has 24. Flooring the shifted coordinate collapses
the pair.

**The batch path vectorizes by grouping the POINTS**, and neither shape the obvious reading
suggests is how. The obstacle is that the candidate list is chosen per point, so four lanes
can want four different lists — and padding every sub-cell's list to the longest one throws
away exactly the pruning that made a query affordable (up to 648 segments against a
handful), while gathering per lane needs a width-agnostic gather `Vector<double>` does not
have and would spend six scalar loads a lane a candidate to save a few flops. What is left
is to make the lanes AGREE: partition the batch by sub-cell with a counting sort, then walk
each bucket's points a register at a time with the strut broadcast as a scalar. The struts
become constants and the points become the vector, which is the layout the arithmetic wanted
— no gather and no branch in the inner loop. Measured (win-x64, interleaved in one process,
minimum over passes, 110 592 z-fastest grid points):

| kind | scalar | grouped batch | |
|---|---|---|---|
| simple cubic | 13.1 | **26.3** Mpts/s | 2.01× |
| BCC | 5.06 | **13.2** | 2.62× |
| FCC | 6.33 | **16.2** | 2.57× |
| octet | 3.23 | **9.02** | 2.79× |
| diamond | 4.87 | **12.5** | 2.58× |
| Kelvin | 4.49 | **12.0** | 2.67× |

Bit-identity is by construction rather than by tuning: the fold and the bucket index run
through the scalar code verbatim (one `BucketOf`, asked by both), the kernel mirrors
`SegmentDistanceSquared` term for term with the segment's own `LengthSquared` broadcast as
a scalar so the division is the identical double, and the running minimum is over the SAME
list in the same ascending order. The one documented vector/scalar divergence,
`Vector.Min`'s ±0 tie-break, cannot reach the result because every quantity it touches is a
sum of squares and the difference is squared again. A GRADED lattice takes the scalar loop —
the radius is a delegate call per point, which does not vectorize.

One benchmark lesson rides along and is recorded in the harness itself: warming per kind
inside the measurement loop is not enough, and the FIRST row is where it shows. Simple cubic
read **0.95×** cold and **2.01×** properly warmed — a measurement artefact sitting exactly
where it is easiest to mistake for a property of the geometry (three struts a cell, short
candidate lists, "the partition must cost more than it saves"). Warm every case before
measuring any.

## The ellipsoid: the one primitive with no closed form

A point's distance to an ellipsoid is the root of a degree-6 polynomial, so every practical
ellipsoid field is an approximation. `Sdf.Ellipsoid` uses the standard scaled-implicit form
`k0(k0−1)/k1`. Rather than repeat the folklore that "the error grows with eccentricity",
here is the measurement against an exact oracle — the Lagrange-multiplier condition
`Σ p_i²r_i²/(r_i²+λ)² = 1`, one scalar equation, strictly decreasing, bisected to machine
precision — as reported distance over true distance:

| aspect ratio | 1 | 1.11 | 1.25 | 1.67 | 3 | 4 | 10 |
|---|---|---|---|---|---|---|---|
| **outside** | 1.000 | 0.996 | 0.983 | 0.916 | 0.675 | 0.544 | 0.238 |
| **inside** | 1.000 | 1.017 | 1.081 | 1.369 | 2.076 | 2.673 | 6.585 |

So outside the solid it is a genuine **lower bound** — never nearer than the truth, the
engine's standing contract — and equal semi-axes reduce it algebraically to the sphere's
exact distance. Inside it **over-reports depth**, which is harmless for meshing and for the
cull (both argue from the Lipschitz bound rather than from one-sidedness) and is why the
projection target divides its step. The sign is exact everywhere, and there is one genuine
singularity: at the exact centre the limit is direction-dependent over the semi-axes, and
the value returned there is `−min(semi-axis)`, the true distance.

**Its Lipschitz bound is derived per AXIS**, which is what makes it exactly **1** for a
sphere where the earlier `2 + (rmax/rmin)²` reported 3. Writing `w_i = p_i/r_i²`, every
component of the gradient carries the same factor `w_j`:

```
∂_j V = w_j·[ (2k0−1)/(k0 k1) − k0(k0−1)/(r_j² k1³) ]
```

and since `Σ w_j² = k1²` that gives `|∇V| ≤ max_j |(2 − μ) + u(μ − 1)|` with `u = 1/k0` and
`μ = ρ²/r_j²`, `ρ = k0/k1 ∈ [rmin, rmax]`. The expression is BILINEAR in (u, μ), so its
maximum over a rectangle of ranges is at a corner and four evaluations settle it over a
region's own `k0` range. For a sphere `μ ≡ 1` exactly (the same double divided by itself),
the u-term vanishes identically, and the bound is 1 — which is right, since the field there
really is `|p| − r`. Measured against the true supremum the reported bound now runs
1.00 / 0.67 / 0.45 / 0.33 / 0.28 / 0.24 over semi-axes (5,5,5) to (10,2,1), where the old
form ran 0.33 / 0.29 / 0.28 / 0.27 / 0.26 / 0.24. The remaining slack is the ρ/axis pairing:
a large `μ_j` requires that axis to carry LITTLE of the gradient, and taking the two ranges
independently ignores the link.

**The regime `k0 ≥ ½` is a real restriction, and the reason is now measured**: the field is
genuinely DISCONTINUOUS at the centre of a non-spherical ellipsoid. Along direction `d` the
value tends to `−|Ad|/|A²d|`, which is `−rmax` down the longest axis and `−rmin` down the
shortest — 10 and 1 at a nanometre from the centre of a 10×1×1 ellipsoid — so no finite
Lipschitz constant covers a region containing it, and `u` is capped at the derivation's own
regime rather than reporting the arithmetic's infinity. What the consumers rest on there is
the weaker property the field does keep, its magnitude never exceeding a bounded multiple of
the true distance (the 1.0–6.6 column above), so the cull's conclusion holds where its
stated premise does not. A sphere has no such gap and is asserted not to.

**The measurement itself carried a lesson.** The first oracle was a dense scan over the
ellipsoid's own parameterization, and its resolution error swamps the quantity being
measured near the surface — it reported an 86% "error" for a **sphere**, where the formula
is exact. An oracle whose error is comparable to the effect is not an oracle.

**The pyramid's published closed form did not survive the same treatment.** Quilez's
`sdPyramid` computes the distance to the pyramid's LATERAL surface and uses the base plane
only for the sign, so wherever the base FACE is nearest it reports the lateral distance:
measured on a 10-wide, 12-tall pyramid, **5.831 against a true 3.0** directly below the base
centre. Both errors are OVER-estimates, the one direction this engine cannot absorb. So
`Sdf.Pyramid` takes the distance over its own six boundary triangles through Core's
`Distance3d.ClosestPointOnTriangle` — six Voronoi-region tests where a closed form would
cost one, and the price of a field that means what the rest of the engine assumes.

## Compiling an AST

`Sdf.Compile()` flattens the whole tree into one delegate (LINQ expression tree → IL → JIT),
removing a virtual dispatch per node per query. It is **bit-for-bit identical to the scalar
path by construction rather than by testing**: each node emits its OWN expression, term for
term, calling the same `Math` methods in the same association order. That the emitter is a
virtual on the node rather than a type switch inside the compiler is the load-bearing
decision — a switch would be a second copy of every formula, free to drift from the one it
claims to mirror. A node with no expression form (a sampled grid, a thread, a planar region,
a `MeshSdf` from another assembly) emits a call back into its own `Evaluate`, so compilation
always succeeds and is always exact; it simply stops paying for that subtree.

Measured (win-x64 i9-9900K, Release, best of five passes after a wall-clock warm-up budget;
`SdfCompilerTests.Measure_ScalarVersusCompiledVersusBatch` is the harness):

| case | scalar walk | compiled | batch (SIMD) | compiled/scalar | batch/compiled |
|---|---|---|---|---|---|
| single sphere | 434.0 | 444.5 | **519.2** Mpts/s | 1.02× | 1.17× |
| bracket CSG tree | 10.8 | 13.3 | **45.2** | 1.23× | 3.40× |
| deep union chain (24) | 4.8 | 12.8 | **28.0** | 2.67× | 2.20× |

**Two conclusions, and the second decides how to use it.** Compilation pays in proportion to
how much of the cost is *dispatch* rather than arithmetic — 1.02× on a lone sphere (one call
to remove, five flops behind it), 2.67× on a chain of 24 unions — so the win is on tree
DEPTH. But it **loses to the SIMD batch path in every case**, and the batch path is what
every bulk consumer here already uses. So this is for callers genuinely stuck with per-point
queries (a marching solver, an interactive probe, a scattered query loop) and is not a faster
way to sample a grid.

**A VECTOR-kernel compiler is a separate project, and the measurement says how much it could
possibly buy.** It would remove exactly two things from the batch path — the virtual call per
node per chunk, and the pooled scratch each operator writes and its parent reads — and it
cannot remove the AoS→SoA transpose (the public signature hands over interleaved points) or
the arithmetic. `VectorCompilerHeadroomBenchmark` reads that ceiling off a union chain of
known depth (win-x64, 200 000 points):

| depth | 1 | 4 | 12 | 24 | 48 |
|---|---|---|---|---|---|
| ns/point | 1.754 | 5.857 | 16.613 | 33.293 | 66.036 |
| marginal ns/node | — | 1.368 | 1.345 | 1.390 | 1.364 |

The marginal cost of one more node is **flat at ~1.36 ns from depth 4 to 48**, and a lone
sphere — which carries the whole transpose by itself — costs **1.85 ns**. So the per-node
plumbing has already been amortized to below the arithmetic, and what a vector compiler
could remove is a fraction of that 1.36 ns, against the 1.2–3.4× the *scalar* compiler was
already losing by. That is a large build (every node's expression rewritten against
`Vector<double>`, plus a per-node masking fallback for the deliberately scalar ones, to
which the recorded "block granularity destroys per-lane savings" rule then applies) for a
ceiling in the low tens of per cent. Filed as a separate project rather than as a residual.

Note the asymmetry with vectorization: every TPMS **does** compile, because an expression
tree calls `Math.Sin` itself and so is bit-identical, where a SIMD kernel would have to
substitute a vector sine and could not be. Compilation and vectorization are not the same
trade. For the TPMS family that identity is by construction rather than by testing in a
second sense too: one term table per surface is the single source of the scalar evaluator
AND the emitted expression, so there are eight formulas rather than sixteen copies free to
drift — which is also what let the gyroid move out of `Primitives.cs` with its field
bit-for-bit unchanged (asserted against a transcription of the closed form it used to be).

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

**What is vectorized**: every primitive except the lattices (sphere, box, cylinder, cone,
torus, capsule, half-space), every set operator and smooth blend, offset/shell, and the
translate/rotate/mirror/scale transforms, plus the n-ary union/intersection/smooth-union
and the Wyvill falloff blend. **Deliberately not vectorized**: every TPMS (including the
gyroid) and the exponential falloff (they need `Math.Sin`/`Math.Exp`, and no vector
transcendental reproduces those bit for bit — a silently divergent fast path is worse than
none), the **strut lattices** (their fold and per-sub-cell candidate list are
data-dependent branches, so the vector form is its own piece of work — filed), and
`ThreadSdf` / the sampled grids / planar-region extrusions (branchy or gather-bound).
They still batch through the default `EvaluateBatch` loop and still benefit from
vectorized operands around them, and they are bit-identical to the scalar path by
construction, since not overriding the seam IS the scalar path.

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
