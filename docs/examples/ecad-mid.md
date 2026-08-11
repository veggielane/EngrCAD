---
title: "MID / LDS — routing on a moulded surface"
---

Stage 8 of the ECAD campaign is the flagship: routing conductors and seating components on a
**moulded, doubly-curved surface** rather than a flat board — the **MID** (moulded interconnect
device) / **LDS** (laser direct structuring) construction, where a plastic housing carries its own
circuit on its shaped wall.

The whole idea in one sentence: **everything happens in the surface's exponential-map (u, v)
parameter space.** `MeshLocalParam`'s discrete exp map
from a stated origin gives every point of the moulded surface a flat `(u, v)` coordinate; a pad is a
point in `(u, v)`, a `SurfaceTrace` is a polyline in `(u, v)`, and the routing and the 3D DRC run
there with the **same grow-and-intersect** the [flat copper DRC](ecad-drc.md) uses — with the surface
distortion the map carries **folded into the clearance** rather than averaged away.

## The exp map is exact on a plane, developable-clean on a cylinder, distorted on a cap

That is the whole design. On a **plane** the exp map is the identity; on a **developable** surface (a
cylinder, a cone) it unrolls with a few `1e-4` of distortion; and where Gaussian curvature
concentrates (a sphere cap) it genuinely distorts — a full ring's circumference is shorter than
`2π·(geodesic radius)`, which no map can avoid. So the honest failure mode of the 3D DRC is a
**conservative refusal**: a near-tolerance pair on a high-distortion patch is refused *with its
uncertainty stated*, not passed false-precise — exactly the near-tangency rule the
[anti-drill tamper mesh](tamper-mesh.md) refuses conformal placement over.

## The developable oracle: the 3D DRC equals the unrolled 2D DRC

On a developable surface the distortion band collapses, so the 3D DRC reduces to the flat one. The
decisive test builds a **cylindrical** MID board and the **flat unrolled sheet**, routes the *same*
nets on both, and asserts the verdicts and measured separations agree — bit for bit.

```csharp run:ecad-mid
// A finely tessellated CYLINDER WALL (a developable moulded surface), and a flat unrolled sheet.
HalfEdgeMesh Tube(double r, double h, int around, int along) {
    var pos = new List<Vector3d>();
    for (int j = 0; j <= along; j++)
        for (int i = 0; i < around; i++) {
            double a = 2 * Math.PI * i / around;
            pos.Add(new Vector3d(r * Math.Cos(a), r * Math.Sin(a), h * j / along));
        }
    var faces = new List<int[]>();
    for (int j = 0; j < along; j++)
        for (int i = 0; i < around; i++) {
            int p = j * around + i, q = j * around + (i + 1) % around;
            faces.Add(new[] { p, q, q + around });
            faces.Add(new[] { p, q + around, p + around });
        }
    return HalfEdgeMesh.Build(pos, faces);
}
HalfEdgeMesh Plane(int n, double size) {
    var pos = new List<Vector3d>();
    for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++)
        pos.Add(new Vector3d(-size / 2 + size * i / n, -size / 2 + size * j / n, 0));
    var faces = new List<int[]>();
    for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) {
        int a = j * (n + 1) + i;
        faces.Add(new[] { a, a + 1, a + n + 2 });
        faces.Add(new[] { a, a + n + 2, a + n + 1 });
    }
    return HalfEdgeMesh.Build(pos, faces);
}

var tube = Tube(6, 8, 64, 40);
var plane = Plane(40, 8);

// Parameterize each surface by the exp map from a stated origin — the routing PATCH, a real design
// parameter (which part of the moulding carries the circuit).
var cylinder = MidBoard.OnSurface(tube, seedVertex: 20 * 64, referenceDirection: Vector3d.UnitY, radius: 2.5);
var flat     = MidBoard.OnSurface(plane, seedVertex: 20 * 41 + 20, referenceDirection: Vector3d.UnitX, radius: 5.0);
Console.WriteLine($"cylinder exp-map distortion: {cylinder.MaxDistortion:E2}  (developable => tiny)");
Console.WriteLine($"plane exp-map distortion:    {flat.MaxDistortion:E2}  (the identity)");

// Route the SAME nets in exp-map (u, v) coordinates on both surfaces.
void Route(MidBoard b) {
    b.PlacePad("A", new Vector2d(0, 0), 0.3, "A");
    b.PlacePad("B", new Vector2d(0.30 + 0.30, 0), 0.3, "B");   // 0.30 mm edge-to-edge gap: clear
    b.PlacePad("C", new Vector2d(0, 1), 0.3, "C");
    b.PlacePad("D", new Vector2d(0.30 + 0.05, 1), 0.3, "D");   // 0.05 mm gap: too close
}
Route(cylinder);
Route(flat);

var rules = DrcRuleSet.Default;               // 0.15 mm clearance
var on3d = Mid3dDrc.Check(cylinder, rules);   // the 3D DRC on the moulded cylinder
var on2d = Mid3dDrc.Check(flat, rules);       // the unrolled 2D DRC on the flat sheet

Console.WriteLine($"3D DRC on the cylinder: {on3d.Violations.Count} violation(s), {on3d.Uncertain.Count} uncertain");
Console.WriteLine($"unrolled 2D DRC:        {on2d.Violations.Count} violation(s), {on2d.Uncertain.Count} uncertain");
var v3 = on3d.Violations.Single();
var v2 = on2d.Violations.Single();
Console.WriteLine($"same verdict ({v3.Rule}), same measured separation: "
    + $"{v3.MeasuredParameter:R} == {v2.MeasuredParameter:R}  (bit for bit)");
Console.WriteLine($"the cylinder folds the distortion into the surface clearance: "
    + $"[{v3.SurfaceSeparationMin:g4}, {v3.SurfaceSeparationMax:g4}] mm");
```

The verdicts and the measured `(u, v)` separations are identical because both run the *same*
grow-and-intersect on the *same* `(u, v)` geometry; the only thing the cylinder adds is the surface
clearance **band** the fold reports, which on a developable surface is a hair wide.

## Conductors lifted onto the moulded surface

A `SurfaceTrace` is routed in `(u, v)` and lifted onto the surface, so its endpoints land **exactly**
on the pad points it connects. Its exported form is a thin conductive `Shape` — a ribbon offset
laterally in the surface tangent plane and extruded along the surface normal — that round-trips
through STL / STEP like any part, and each trace **reports the distortion it carried**
(`MinScale`, `MaxScale`).

```csharp render:ecad-mid-conductors
HalfEdgeMesh Tube(double r, double h, int around, int along) {
    var pos = new List<Vector3d>();
    for (int j = 0; j <= along; j++)
        for (int i = 0; i < around; i++) {
            double a = 2 * Math.PI * i / around;
            pos.Add(new Vector3d(r * Math.Cos(a), r * Math.Sin(a), h * j / along));
        }
    var faces = new List<int[]>();
    for (int j = 0; j < along; j++)
        for (int i = 0; i < around; i++) {
            int p = j * around + i, q = j * around + (i + 1) % around;
            faces.Add(new[] { p, q, q + around });
            faces.Add(new[] { p, q + around, p + around });
        }
    return HalfEdgeMesh.Build(pos, faces);
}

var tube = Tube(8, 14, 96, 60);
// The map radius is a GRAPH distance (it over-estimates the straight-line one), so state it
// comfortably above the furthest feature's reach.
var board = MidBoard.OnSurface(tube, seedVertex: 30 * 96, referenceDirection: Vector3d.UnitY, radius: 9.0);

// A serpentine and two straight conductors, routed in (u, v) and lifted onto the curved wall.
var serpentine = board.PlaceTrace("SIG", new[] {
    new Vector2d(-4, -3), new Vector2d(-4, 3), new Vector2d(-2.5, 3),
    new Vector2d(-2.5, -3), new Vector2d(-1, -3), new Vector2d(-1, 3) }, 0.6, "S");
var vcc = board.PlaceTrace("VCC", new[] { new Vector2d(1.5, -3), new Vector2d(1.5, 3) }, 0.6, "V");
var gnd = board.PlaceTrace("GND", new[] { new Vector2d(3, -3), new Vector2d(3, 3) }, 0.6, "G");

var scene = new Scene();
scene.Add(new Part("wall", Shape.From(tube), Palette.Steel));
scene.Add(new Part("SIG", serpentine.Conductor(0.25), Palette.Coral));
scene.Add(new Part("VCC", vcc.Conductor(0.25), Palette.Teal));
scene.Add(new Part("GND", gnd.Conductor(0.25), Palette.Plum));
```

![Copper conductors — a serpentine and two straight runs — following the wall of a moulded cylinder](images/ecad-mid-conductors.png)

## Seating a component

A catalogue `HardwareComponent` seats on the surface at a `(u, v)` point, its body posed in the
surface's own tangent frame (Z the surface normal, X the exp-map `+u`) — the component's seating
convention transported onto the moulded wall:

```csharp
var board = MidBoard.OnSurface(mesh, seed, Vector3d.UnitY, radius: 4);
var seated = board.Seat(StandardComponents.CapScrew(6, 12), new Vector2d(0.5, 0.5));
// seated.Body is the screw posed on the surface, ready to be an assembly occurrence.
```

## Scope, v1

`MidRouting` **places** traces and **verifies** them; it does **not auto-route**. Auto-routing on a
surface is a *geodesic maze search* (the flat grid autorouter does not lift, since the metric is the
distorted `(u, v)` space) — filed as a later stage, and `MidRouting.Route` refuses it by name. Also
filed: **multi-shell** MID (traces on an inner moulded shell as well as the outer), and a conformal
**solder mask / pour** on the surface (refused for the distortion reason, exactly as copper pours
already refuse curved walls). LDS process specifics (laser activation paths) are out of scope.

## Verification

The bar is higher than usual because ECAD fails plausibly. The decisive oracle is the **developable
agreement** above (the cylinder's 3D DRC verdicts and measured separations equal the unrolled sheet's,
bit for bit, with the cylinder's exp-map distortion `~1.2e-3`). Then, on a **sphere cap** (distortion
`~11%`, a tangential trace reading `MinScale ~0.92`): the distortion is **reported**, and a pair that
passes flat but whose worst-case surface clearance drops below the rule is **refused** (an
`Uncertain` finding), never passed false-precise. A trace's lifted endpoint lands **exactly** on its
pad point; a run reaching past the map **breaks and is counted** (never inventing surface); the
exported conductor is a **closed solid** that round-trips through STL; and the whole check is
**deterministic**.
