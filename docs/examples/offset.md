---
title: "Offset & Minkowski sums"
---

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

## Minkowski sums (what OpenSCAD's `minkowski()` is for)

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

## Related

- [Smooth blends](blends.md) — rounding a junction rather than a whole solid
- [Shell](shell.md) — hollow it into a skin
- [Chamfers & fillets](chamfer-fillet.md) — the exact B-Rep routes
