# Blends, offset, shell, lattice

These operations come from the implicit engine (signed distance fields) and have no
exact B-Rep form — `ToBrep()` rejects them with a clear report, while `ToMesh()`
polygonizes the field with Surface Nets. Everything here still composes freely with
the rest of the `Shape` vocabulary.

## Smooth booleans

`SmoothUnion` / `SmoothIntersect` / `SmoothSubtract` take a blend distance that
rounds the junction — the organic fillet the hard [booleans](booleans.md) can't give:

```csharp render:smooth-blend
var stem = Shape.Cylinder(5, 30);
var bulb = Shape.Sphere(11).Translate(0, 0, 18);

var scene = new Scene();
scene.Add(new Part("hard union", stem | bulb, Palette.Steel,
    Matrix4d.CreateTranslation((-30, 0, 15))));
scene.Add(new Part("smooth union", stem.SmoothUnion(bulb, blend: 6), Palette.Teal,
    Matrix4d.CreateTranslation((30, 0, 15))));
```

![A hard union next to a smooth union with a blended neck](images/smooth-blend.png)

## Offset

`Offset(distance)` grows (or shrinks, when negative) the solid by a uniform distance —
offsetting a box rounds its edges and corners exactly:

```csharp render:offset
var scene = new Scene();
scene.Add(new Part("box", Shape.Box(24, 24, 24), Palette.Steel,
    Matrix4d.CreateTranslation((-30, 0, 17))));
scene.Add(new Part("offset +5", Shape.Box(24, 24, 24).Offset(5), Palette.Coral,
    Matrix4d.CreateTranslation((30, 0, 17))));
```

![A box next to its outward offset with rounded edges](images/offset.png)

### Minkowski sums (what OpenSCAD's `minkowski()` is for)

OpenSCAD models rounding as `minkowski() { part(); sphere(r); }` — the Minkowski sum
with a ball. **That exact operation is `Offset(r)`**: for any solid, the sum with a
ball of radius r is the set of points within r of it, which is precisely what the
signed distance field's offset computes — same geometry, evaluated in microseconds
instead of OpenSCAD's notoriously expensive convolution. The common recipes map
directly:

| OpenSCAD | EngrCAD | Notes |
|---|---|---|
| `minkowski { part; sphere(r) }` | `part.Offset(r)` | exact as a field; polygonized on the way to a mesh |
| erode then dilate (opening) | `part.Offset(-r).Offset(r)` | rounds convex edges, preserves size |
| dilate then erode (closing) | `part.Offset(r).Offset(-r)` | rounds concave corners |
| rounding a convex polyhedron | `part.RoundEdges(r)` | **exact B-Rep** — the morphological opening as real planes, cylindrical bands and spherical corner patches, no field involved |
| rounding one rim | `part.Fillet(r, faces)` | exact B-Rep rim surgery |
| `minkowski` with a cube | — | axis-aligned box dilation ≈ `Offset` under the Chebyshev metric; not provided — see below |

For rounding, prefer the B-Rep routes (`RoundEdges`, `Fillet`, `Chamfer`) when they
apply: they produce exact analytic surfaces that export to STEP and measure exactly,
where the offset field polygonizes. Use `Offset` when the shape lives in field land
anyway (blends, lattices, imported meshes) or when the B-Rep route refuses.

**General `minkowski()` — an arbitrary solid swept by an arbitrary solid — is
deliberately not planned.** The ball case covers rounding, which is nearly every real
use; the general polyhedron⊕polyhedron sum is a different algorithm entirely (convex
decomposition of both operands, pairwise convex sums, then a union of the pieces —
combinatorially explosive for non-convex parts, which is exactly why OpenSCAD's is
slow), and its engineering uses (tolerance zones, cam envelopes, clearance sweeps)
are better served by `Offset` on the exact field of the real geometry. If you truly
need a convex⊕convex sum, `Shape.Hull` of translated copies gives it:
`Hull(a.Translate(v₀), a.Translate(v₁), …)` over b's vertices `vᵢ` is exact for
convex polyhedral `a` and `b`.

## Shell

`Shell(thickness)` hollows a solid into a constant-thickness skin. A real **quarter
cut** (two section planes on the render, the viewer's
[section mode](viewer.md)) exposes the interior wall — and because a shelled shape is
SDF-native, the cut carries its [isolines](viewer.md#sdf-isolines-on-the-cut): the
two gold contours are the exact inner and outer surfaces, and the constant gap
between them IS the wall thickness, readable at a glance:

```csharp render:shell section:y,0;z,16
var hollow = Shape.Sphere(16).Shell(2.5);

var scene = new Scene();
scene.Add(new Part("shelled sphere", hollow, Palette.Brass,
    Matrix4d.CreateTranslation((0, 0, 16))));
```

![A shelled sphere quarter-cut by two section planes, isolines showing the constant wall thickness](images/shell.png)

## Lattice

`Lattice(pattern)` intersects a solid with a periodic SDF such as
`Sdf.Gyroid(cellSize, thickness)` — the additive-manufacturing infill workhorse:

```csharp render:lattice
var scene = new Scene(new MeshQuality { SdfResolution = 110 });
scene.Add(new Part("gyroid lattice",
    Shape.Sphere(16).Lattice(Sdf.Gyroid(cellSize: 12, thickness: 1.2)),
    Palette.Slate, Matrix4d.CreateTranslation((0, 0, 16))));
```

![A sphere filled with a gyroid lattice](images/lattice.png)

Any hand-written `Sdf` works as the pattern, and `Shape.From(sdf)` wraps arbitrary
fields back into the modeling vocabulary — see
[dropping down to the engines](representations.md#dropping-down-to-the-engine-apis).
