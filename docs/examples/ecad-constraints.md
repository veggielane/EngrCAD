---
title: "Placement constraints"
---

Stage 3 of the ECAD campaign places components by **constraint** rather than by hand-typed
coordinates. A rough drawn layout — the [board and its parts](ecad-pcb.md) — becomes the *seed*,
and a set of relations (a datum, a stated spacing, a row, a clearance, an edge to sit flush to)
is solved for the poses that satisfy them. The one-declaration rule carries over: the solve
produces a **new** layout, and its copper, drills, nets and 3D bodies all *derive* from the moved
placements, so nothing drifts.

## It is the mate solver, one layer up

The variables are each free component's rigid 2D pose — `(x, y, θ)` on the board. The engine is
the **MateSolver doctrine** rebuilt at 2D: an analytic Jacobian; every residual a *length* (angular
residuals scaled by the board diagonal, and the rotation variable divided by it, so one linear
tolerance is meaningful and every column is O(1)); a rank-revealing DOF report; the drawn layout as
**seed and branch selector**; and the honesty rules that make it trustworthy — an under-constrained
layout is normal and reports its remaining degrees of freedom, a contradiction and a stationary
start are *named*, and a failed solve leaves the source layout bit-identically unchanged (the solve
returns a new layout only on success).

## The vocabulary

| Constraint | What it fixes |
| --- | --- |
| `Lock` / `Fix` | A placement is a datum (its pose is an input, not an unknown). |
| `Group` / `Cluster` | Several placements move as ONE rigid body — a functional block. |
| `Orient` / `FixRotation` | A placement's rotation, to an angle or to where it was drawn. |
| `Distance` / `Spacing` | A stated gap between two points (component origins, pads). |
| `AlignX` / `AlignY` | Two points share a coordinate — a column or a row of parts. |
| `Parallel` / `Perpendicular` | Two directions (a component axis, a board edge). |
| `PointOnLine` | A point on a line's carrier at a *signed* offset (0 = on the line). |
| `AlignEdge` | A component side flush (or at a gap) to a board edge or another side. |
| `InsideRegion` / `InsideBoard` | A footprint stays inside a zone (its bounding circle contained). |
| `ClearOf` / `ClearOfRegion` / `ClearOfKeepOut` | A footprint stays a distance clear of another, or of a keep-out. |

Clearance and containment are **one-sided** (active-set) residuals — they push only when violated,
and report feasibility honestly — rather than a fake equality. A footprint's extent is modelled by
the smallest circle about its origin enclosing its pads, so it is rotation-invariant and
conservative: keeping the circle clear keeps the copper clear.

## An aligned, spaced, clear row

Three parts placed roughly and tilted, then constrained: a header as the datum, a resistor a stated
distance from it, and two resistors sharing a row at a fixed gap and kept clear of each other. The
report shows what the constraints pinned and what they left free.

```csharp run:ecad-constraints
// A schematic — three parts (bodies + footprints named once, instanced as components).
var resistor = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
    }));
var header = new PartDefinition("HDR_1x2", "J",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("HDR254", new[] {
        Pad.ThroughHole("1", new Vector2d(-1.27, 0), pad: 1.6, drill: 0.9),
        Pad.ThroughHole("2", new Vector2d(1.27, 0), pad: 1.6, drill: 0.9),
    }));

var sch = new Schematic("aligner");
var j = sch.Add("J1", header);
var r1 = sch.Add("R1", resistor, "330");
var r2 = sch.Add("R2", resistor, "1k");
sch.Connect("VCC", j.Pin("1"), r1.Pin("1"));
sch.Connect("SIG", r1.Pin("2"), r2.Pin("1"));

// A board, and a ROUGH drawn placement — the seed the solve starts from.
var board = new PcbBoard(new[] {
    new Vector2d(-25, -15), new Vector2d(25, -15),
    new Vector2d(25, 15), new Vector2d(-25, 15),
}, thickness: 1.6);

var layout = new PcbLayout(sch, board);
layout.Place("J1", -20, 0, rotationDegrees: 90);
layout.Place("R1", 2, 4, rotationDegrees: 12);    // placed roughly, tilted
layout.Place("R2", 9, -3, rotationDegrees: -8);

// Constrain: J1 is the datum; R1 sits 12 mm from it; R1 and R2 share a row (y) at a 6 mm gap
// and stay at least 0.5 mm clear of each other.
var result = layout.Constrain()
    .Lock("J1")
    .Distance(PlacementPoint.Origin("J1"), PlacementPoint.Origin("R1"), 12)
    .AlignY(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"))
    .Spacing(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 6)
    .ClearOf("R1", "R2", 0.5)
    .Solve();

Console.WriteLine(result);   // the solve report — what it pinned, and what it left free

// The moved poses satisfy the constraints exactly.
var solved = result.Solved!;
var pr1 = solved.Placements.First(p => p.Reference == "R1");
var pr2 = solved.Placements.First(p => p.Reference == "R2");
var pj1 = solved.Placements.First(p => p.Reference == "J1");
double Dist(PcbPlacement a, PcbPlacement b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
Console.WriteLine($"J1↔R1 distance: {Dist(pj1, pr1):g6} mm (asked 12)");
Console.WriteLine($"R1↔R2 spacing:  {Dist(pr1, pr2):g6} mm (asked 6)");
Console.WriteLine($"R1, R2 share a row: y = {pr1.Y:g4} and {pr2.Y:g4}");

// The one-declaration identity survives the move: the copper derives from the solved poses.
Console.WriteLine($"identity holds after the move: {solved.Check().IdentityHolds}");
```

## Honesty in the report

- **Under-constrained is normal, and reported.** A `Distance` and an `AlignY` and a `Spacing`
  leave the row free to translate and each part free to spin — the report says how many degrees of
  freedom remain rather than pretending the layout is pinned. Set
  `PcbConstraintSolverSettings.RequireFullyConstrained` to make remaining freedom an error.
- **A contradiction is named.** Two `Distance`s that cannot both hold do not silently split the
  difference — the solve fails and names the constraints carrying the residual, and the layout is
  left unchanged.
- **A stationary start is named.** A `Perpendicular` on two directions that begin exactly parallel
  has no first-order motion to improve it; the solver says so rather than nudging at random and
  sometimes converging.

## Persistence

Constraints are part of the design intent, so they persist. `ConstrainedLayout.Save`/`Load` extends
the [stage-2 layout format](ecad-pcb.md) with a `constraints` array, write-only-when-stated: a
layout with no constraints saves byte-identically to a stage-2 file, and a constrained one is a
`save → load → save` byte-identical fixed point. Every value a constraint captured (a signed
offset, an `AlignEdge` side read off the drawing) rides as data, so a reload reproduces the exact
constraint rather than re-guessing a branch.
