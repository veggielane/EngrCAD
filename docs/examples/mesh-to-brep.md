---
title: "Mesh to B-Rep (STL to STEP)"
---

The conversion triangle has three edges that throw information away in a controlled way —
implicit→mesh, B-Rep→mesh, mesh→implicit are all discretisations. This is the fourth
direction, and it is a different kind of problem because it puts information **back**: a
triangle mesh re-recognised as a parametric `BrepSolid` of analytic faces. A drilled plate
comes back as about **seven faces** — six planes and one cylindrical bore — not five
thousand planar facets wearing a `.step` extension.

`MeshToBrep.Reconstruct` is that fourth edge. It segments the triangles into regions across
sharp creases, fits a plane / cylinder / sphere to each region **with the residual
reported**, recovers each region boundary as the **exact intersection** of the two fitted
surfaces (so the faces actually close), and welds the trimmed faces into a `BrepSolid` that
must pass `Validate()`.

```csharp render:mesh-to-brep
// A drilled plate, tessellated to a triangle mesh — as if it had arrived as an STL with no
// surface information at all, just facets.
var plate = SolidFactory.MakeBox(new Aabb((0, 0, 0), (40, 30, 10)));
var bore = SolidFactory.MakeCylinder(4, 20).Transformed(Matrix4d.CreateTranslation((20, 15, -5)));
var mesh = BRepTessellator.Tessellate(BrepBoolean.Difference(plate, bore), segmentsPerCircle: 64);

// Re-recognise the analytic surfaces. The report names each region's fitted type and how
// well it fit; RegionCount is the headline metric — seven faces, not the mesh's facets.
var result = MeshToBrep.Reconstruct(mesh);

var scene = new Scene();
scene.Add(new Part("reconstructed", Shape.From(result.Solid!)));
```

The report is the honest half. `result.Report.RegionCount` is **7**; every region is a
`Plane` except the one `Cylinder`, and each `ReconstructedRegion.Residual` is the worst
distance from a mesh vertex to its fitted surface — machine-epsilon-small here, because a
tessellation of exact geometry has its vertices lying **on** the surface.

## The test that separates a real fit from its impostor

A cylinder tessellated at 32, 64, 128 or 256 segments is an inscribed n-gon, and its
vertices lie exactly on the cylinder. A least-squares fit through points on a circle
recovers the **true** radius at every density — not the inscribed radius `r·cos(π/n)`, which
a chord-length fit would report and which is off by 0.024 at 32 segments. That distinction —
exact radius versus inscribed radius — is what tells a reconstruction apart from a plausible
impostor, so it is the first thing the tests pin.

## Scope, stated

v1 is the **tessellated-CAD** case: the input is a tessellation of exact geometry, so a
fit's residual is the chord error and nothing else. Reverse-engineering a **3D scan** (noise,
outliers, missing regions) is a different product and is not attempted — a region whose best
plane / cylinder / sphere fit exceeds the tolerance is reported `Unfitted` by name rather
than forced onto a surface it is not.

Refused by name, each for a stated reason:

- an **open or non-manifold** mesh (run `MeshRepair.AutoRepair` first — closing holes invents
  surface, so it is not done silently);
- **cone, torus and freeform** regions (a NURBS surface fitter is the genuinely new numerical
  work and is future work);
- a **seamless closed surface** with no boundary edge — a whole sphere is one face with no
  edge, and a seamed single-face solid is out of v1 scope (the fit is still reported).
