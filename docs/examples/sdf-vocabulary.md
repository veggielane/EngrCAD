---
title: "The SDF vocabulary"
---

Beyond the shapes the `Shape` API builds exactly, the implicit engine carries its own
primitive set and a family of **domain operations** that reshape space itself. Reach
for these through `Shape.From(sdf)` when you are already in field land — and see
[dropping down to the engines](representations.md#dropping-down-to-the-engine-apis)
for how they come back into the modelling vocabulary.

## Field primitives

Each states whether it is an **exact distance** or a **bound**:

| primitive | fidelity |
|---|---|
| `Sdf.RoundedBox(x, y, z, r)` | exact |
| `Sdf.RoundCone(r0, r1, h)` | exact — the hull of two spheres |
| `Sdf.Link(major, minor, halfLength)` | exact |
| `Sdf.Prism(sides, circumradius, height)` | exact — regular n-gon, so `3` and `6` are the triangular and hexagonal prisms |
| `Sdf.Wedge(x, y, z, topX, topOffsetX)` | exact — the field twin of `Shape.Wedge` |
| `Sdf.Pyramid(baseSize, height)` | exact |
| `Sdf.Ellipsoid(a, b, c)` | **a bound** — see below |
| `Sdf.ConvexPolyhedron(halfSpaces)` | exact — `ConvexDistance.HalfSpaceBound` asks for the cheap lower bound instead |

```csharp render:sdf-primitives
var scene = new Scene();
scene.Add(new Part("rounded box", Shape.From(Sdf.RoundedBox(20, 20, 20, 5)), Palette.Steel,
    Matrix4d.CreateTranslation((-40, 0, 12))));
scene.Add(new Part("hex prism", Shape.From(Sdf.Prism(6, 11, 24)), Palette.Brass,
    Matrix4d.CreateTranslation((0, 0, 12))));
scene.Add(new Part("round cone", Shape.From(Sdf.RoundCone(9, 3, 18)), Palette.Teal,
    Matrix4d.CreateTranslation((40, 0, 0))));
scene.Add(new Part("link", Shape.From(Sdf.Link(9, 3, 6)), Palette.Coral,
    Matrix4d.CreateTranslation((0, 40, 12))));
```

![A rounded box, a hexagonal prism, a round cone and a chain link](images/sdf-primitives.png)

### The ellipsoid is the one with no closed form

A point's distance to an ellipsoid is the root of a degree-6 polynomial, so every
practical ellipsoid field is an approximation. `Sdf.Ellipsoid` uses the standard
scaled-implicit form, and rather than repeat the folklore that "the error grows with
eccentricity", here is the measurement — reported distance over true distance, against an
exact Lagrange-multiplier oracle:

| aspect ratio | 1 | 1.25 | 1.67 | 3 | 4 | 10 |
|---|---|---|---|---|---|---|
| **outside** | 1.000 | 0.983 | 0.916 | 0.675 | 0.544 | 0.238 |
| **inside** | 1.000 | 1.081 | 1.369 | 2.076 | 2.673 | 6.585 |

Two things follow. Outside the solid it is a genuine **lower bound** — never nearer than
the truth, which is what meshing, culling and offsetting need — and equal semi-axes
reduce it to the sphere's exact distance. Inside, it over-reports depth, so do not read a
wall thickness off an eccentric ellipsoid's field. The **sign is exact everywhere**.

One more thing is worth knowing before you probe near the middle of a *very* eccentric
one: the field is genuinely **discontinuous at the centre**. Approaching the origin down
the long axis the value tends to `−rmax` and down the short axis to `−rmin`, so a
10 × 1 × 1 ellipsoid reads −10 and −1 a nanometre either side of its own centre. That is
maximally far from the surface, so nothing that meshes or culls it is affected, and
`Sdf.LipschitzBound` states the regime it covers rather than pretending otherwise.

### The convex polyhedron

`Sdf.ConvexPolyhedron(halfSpaces)` is the escape hatch for a plane-bounded solid with no
factory — and unlike an `Sdf.Intersection` over the same half-spaces it reports **finite
bounds**, by enumerating the vertices where three planes meet and every other is
satisfied, so a polygonizer can size its own region.

It is **exact**. Inside, the maximum over the face half-spaces already *is* the distance
(for a convex body the nearest boundary point lies on the nearest face plane); outside,
that maximum understates wherever the nearest feature is an edge or a corner, so the
field takes the minimum over the solid's own boundary triangles instead — built from the
vertices it had already enumerated. `ConvexDistance.HalfSpaceBound` asks for the cheap
form, which is a correct-sign lower bound at one dot product per plane rather than a
Voronoi-region test per triangle; reach for it when the polyhedron has many faces and the
outside magnitude does not matter.

## Domain operations

These reshape *space itself* rather than combining two solids, which is what makes them
cheap: the field is evaluated at a moved query point, so a lattice of ten thousand
instances costs one primitive.

```csharp render:sdf-domain-ops
var bar = Sdf.Box(11, 11, 40);

// The default view looks down −X, so the parts read left to right in decreasing x.
var scene = new Scene(new MeshQuality { SdfResolution = 140 });
scene.Add(new Part("twist", Shape.From(bar.Twist(radiansPerUnit: 0.055)), Palette.Teal,
    Matrix4d.CreateTranslation((54, 0, 22))));
scene.Add(new Part("taper", Shape.From(bar.Taper(bottomScale: 1.0, topScale: 0.3)), Palette.Brass,
    Matrix4d.CreateTranslation((18, 0, 22))));
scene.Add(new Part("bend", Shape.From(Sdf.Box(40, 11, 7).Bend(curvature: 0.028)), Palette.Coral,
    Matrix4d.CreateTranslation((-18, 0, 22))));
scene.Add(new Part("elongate", Shape.From(Sdf.Sphere(7).Elongate((0, 0, 13))), Palette.Slate,
    Matrix4d.CreateTranslation((-54, 0, 22))));
```

![A twisted bar, a tapered bar, a bent bar and an elongated sphere, side by side](images/sdf-domain-ops.png)

`Displace(amplitude, frequency)` adds a sinusoidal ripple — knurling, texture, a
grip surface:

```csharp render:sdf-displace
var knurled = Sdf.Cylinder(14, 30).Displace(amplitude: 0.9, frequency: (1.4, 1.4, 1.4));

var scene = new Scene(new MeshQuality { SdfResolution = 190 });
scene.Add(new Part("knurled boss", Shape.From(knurled), Palette.Brass,
    Matrix4d.CreateTranslation((0, 0, 16))));
```

![A cylinder with a sinusoidal knurl over its surface](images/sdf-displace.png)

The ripple is a *product* of three sines, so a zero frequency component makes it
identically zero rather than two-dimensional. And it is the one operation here that adds
a **value** rather than moving a point, which is why its bounds carry a precondition:
material appears wherever the child reads below the amplitude, so the reported bounds are
right as long as the child never reports less than the escape from its own bounds. Every
primitive and every boolean satisfies that — including a difference near its subtracted
tool, which is the case you would expect to break it — but a `Twist`, `Bend` or `Taper`
underneath does not, so state the region yourself there:

```csharp
var bumpy = tapered.Displace(0.8, (4, 4, 4), bounds: tapered.Bounds.Expanded(2.4));
```

### Repetition

`Repeat(spacing)` tiles a solid over an infinite lattice, and `Repeat(spacing, counts)`
gives it a finite count. Both cost one child evaluation per neighbouring cell rather than
one per instance, so the count is free:

```csharp render:sdf-repeat
var pins = Sdf.Cylinder(2.5, 18).Repeat((10, 10, 0), new Vector3i(6, 4, 1));
var plate = Sdf.Box(70, 46, 5).Translate((25, 15, -11));

var scene = new Scene(new MeshQuality { SdfResolution = 200 });
scene.Add(new Part("pin field", Shape.From(pins | plate), Palette.Steel,
    Matrix4d.CreateTranslation((-25, -15, 14))));
```

![A rectangular field of pins standing on a plate](images/sdf-repeat.png)

The child must fit inside one cell, and that is **enforced rather than assumed**: outside
that condition a query point can lie inside an instance the evaluation never visits, and
the sign would be wrong. The refusal names the span it measured. (That precondition is
also why `Repeat` cannot build a [strut lattice](lattices.md#why-repeat-cannot-build-one)
— a strut spans its whole cell, so its capsule overhangs by the radius.)

### What these cost you: the distance stops being a distance

A translate, a rotate, a mirror and a repetition are **isometries** — they move points
without changing lengths — so the field stays an exact distance. A **twist, a bend and a
taper are not**: they shear or stretch space, so the value changes faster than the query
point moves. Three consequences, all handled for you but worth knowing:

- the **sign stays exact everywhere** (the solid is exactly the pre-image of the child),
  so booleans, meshing and inside/outside classification are unaffected;
- the **magnitude becomes an over-estimate** of the true distance, by at most a factor the
  node computes and reports (`Sdf.LipschitzBound`), so do not read a wall thickness or a
  clearance off a twisted field;
- the polygonizer, the narrow-band grid and the remesher's projection each **widen by
  exactly that factor**, which is why a twisted lattice meshes correctly instead of losing
  slivers of geometry. That factor is 1 for every exact field, so nothing else pays for it.

## Compiling a field

`Sdf.Compile()` flattens a whole AST into one delegate, removing the virtual call per node
per query. It is **bit-for-bit identical** to the un-compiled field, and it composes like
any other node:

```csharp
var probe = body.Compile();       // an Sdf, with the same Bounds and the same field
double d = probe.Evaluate(point);
```

Measured (win-x64, Mpts/s):

| case | scalar walk | compiled | batch (SIMD) |
|---|---|---|---|
| single sphere | 434.0 | 444.5 | **519.2** |
| bracket CSG tree | 10.8 | 13.3 | **45.2** |
| deep union chain (24 nodes) | 4.8 | 12.8 | **28.0** |

So it buys 1.02×–2.67× over per-point evaluation — the win tracks how much of the cost is
dispatch rather than arithmetic — and it **loses to the batch path in every case**. Since
`Evaluate(points, distances)` is what meshing, grid bakes and section contours already use,
reach for `Compile()` only when you are genuinely stuck with one point at a time: a
marching solver, an interactive probe, a scattered query loop.

A compiler emitting *vector* kernels would beat both, and the honest headroom is small
enough to be worth stating: on a union chain the marginal cost of one more node in the
batch path is flat at about **1.36 ns/point** from depth 4 to 48, while a lone sphere —
which carries the whole AoS→SoA transpose by itself — costs 1.85 ns. So the per-node
plumbing a vector compiler would remove has already been amortized to below the
arithmetic.

## Related

- [Smooth blends](blends.md), [Offset](offset.md), [Shell](shell.md) — the field
  operations the `Shape` API exposes directly
- [Lattices](lattices.md) — the periodic fields
- [Polygonization](polygonization.md) — turning any of these into a mesh
