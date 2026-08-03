# Space-filling curves & 2D infill

A **space-filling curve** reaches every part of a region along one continuous path. That is
exactly what a printed infill, a pocket-clearing pass, an engraved fill or a serpentine heater
track wants: full coverage with as few travel moves as possible.

`SpaceFillingCurve` (in `EngrCAD.Core.Geometry2`) generates the curve; `SpaceFillingInfill`
clips it to a sketch and reports what it did.

## The name overpromises, so start there

A *true* space-filling curve is the limit of an infinite sequence and has infinite length. What
is built here is one finite member of that sequence, and the **order** is the parameter. You do
not state an order — you state a **spacing**, the distance you want between neighbouring
passes, and the generator picks the order whose cell size is at or under it and **reports the
spacing it achieved**:

```csharp run:infill-spacing-report
var plate = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(8));
var fill = SpaceFillingInfill.Fill(plate, spacing: 5.0);

// 60 wide, so the curve's footprint is a 60 mm square and 60 / 2^4 = 3.75 is the first cell
// size at or under 5. The order is an integer, so the surplus has to land somewhere.
if (fill.RequestedSpacing != 5.0) throw new Exception("the request is kept");
if (fill.Order != 4) throw new Exception($"expected order 4, got {fill.Order}");
if (Math.Abs(fill.Spacing - 3.75) > 1e-12) throw new Exception($"achieved {fill.Spacing}");
if (fill.Spacing > fill.RequestedSpacing) throw new Exception("never coarser than asked");
```

The achieved spacing is never coarser than the request and can be up to a factor of the
family's radix finer. Read `Spacing`, never `RequestedSpacing`, when you size a bead.

> **Which quantity quantises is a decision.** The order comes from one inequality,
> `side ≤ spacing × radix^n`, and the surplus has to go somewhere: hold the *footprint* to the
> region and the spacing comes out finer than asked; hold the *spacing* and the footprint comes
> out bigger than the region. Both give the same order, so neither is cheaper. EngrCAD holds
> the footprint — a curve is laid *over* a region, and a footprint overhanging by an arbitrary
> amount would put the pattern's phase somewhere you never stated, which for a layered infill
> is the one thing that has to be reproducible.

## A filled plate

```csharp render:infill-hilbert
var plate = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(8));
var fill = SpaceFillingInfill.Fill(plate, spacing: 5.0);

Shape Extruded(Region2d region, double height)
{
    var (outer, holes) = Profile.FromRegion(region);
    return Shape.Extrude(outer, Vector3d.UnitZ * height, holes);
}

var scene = new Scene();
scene.Add(new Part("plate", Extruded(plate.ToRegions()[0], 2), Palette.Steel));

// The bead is deliberately narrower than the spacing, so the passes stay visible; at
// width == Spacing they just touch and the fill reads as a solid skin.
int index = 0;
foreach (var piece in fill.Footprint(width: fill.Spacing * 0.45))
{
    scene.Add(new Part($"pass {++index}", Extruded(piece, 3.5), Palette.Coral,
        Matrix4d.CreateTranslation((0, 0, 1))));
}
```

![A rectangular plate with a central bore, filled by a Hilbert-curve toolpath](images/infill-hilbert.png)

Everything below the placement into model coordinates is integer arithmetic: consecutive cells
of the lattice differ by exactly one step, the cells are counted in closed form and visited
exactly once, and the path length is the segment count times the spacing exactly. Only the
coverage is a measurement.

## Choosing a family

Pick by what the consumer needs, not by fame. The differences are small, real, and measured
rather than asserted:

| Family | Radix | Closed? | Longest straight run | Use it for |
| --- | --- | --- | --- | --- |
| `Hilbert` | 2 | no | 3 cells | the isotropy default — no preferred direction |
| `Moore` | 2 | **yes** | 3 cells | a path that must return to its start |
| `Peano` | 3 | no | 5 cells | fewer direction changes, so quicker to machine |
| `Gosper` | 7 | no | 2 cells | a hexagonal lattice the square families cannot tile |
| `ZOrder` | 2 | no | — | indexing only; **not** a path (see below) |

The longest straight run **saturates**: it is 3 cells for Hilbert at order 3 and at order 6
alike, and 5 for Peano at every order. That is the isotropy claim as a number.

```csharp run:infill-families
var plate = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(8));

foreach (var family in new[]
{
    SpaceFillingFamily.Hilbert, SpaceFillingFamily.Moore,
    SpaceFillingFamily.Peano, SpaceFillingFamily.Gosper,
})
{
    var fill = SpaceFillingInfill.Fill(plate, spacing: 4.0, family);
    Console.WriteLine(
        $"{family,-8} order {fill.Order}  spacing {fill.Spacing:F3}  "
        + $"{fill.PointCount} points in {fill.Runs.Count} runs, {fill.Length:F0} mm drawn");
    if (fill.Length <= 0) throw new Exception("every family draws something");
}

// A Moore fill of its own square, with nothing clipped away, comes back as ONE closed run.
var moore = SpaceFillingInfill.Fill(
    Sketch.Rectangle(40, 40), 3.0, SpaceFillingFamily.Moore, clearance: 0);
if (moore.Runs.Count != 1) throw new Exception("one run");
double gap = moore.Runs[0][^1].DistanceTo(moore.Runs[0][0]);
if (Math.Abs(gap - moore.Spacing) > 1e-9) throw new Exception($"not closed: {gap}");
```

**Z-order is refused for a fill, by name.** It is a bijective spatial *ordering* — the same
Morton code `PlanarSection`'s silhouette fold sorts by — and its consecutive cells are up to a
whole grid width apart, so a "toolpath" made of it would be mostly rapid moves. The generator
still offers it; the fill does not.

```csharp run:infill-zorder-refused
var curve = SpaceFillingCurve.Over(
    new Aabb((0, 0, 0), (32, 32, 0)), SpaceFillingFamily.ZOrder, 2.0);
if (curve.IsContinuous) throw new Exception("Z-order is not a curve");
if (curve.MaxLatticeStep != 15) throw new Exception($"it jumps the grid: {curve.MaxLatticeStep}");

try
{
    SpaceFillingInfill.Fill(Sketch.Rectangle(40, 40), 3.0, SpaceFillingFamily.ZOrder);
    throw new Exception("should have refused");
}
catch (ArgumentOutOfRangeException e) when (e.Message.Contains("spatial ORDERING")) { }
```

## Clipping is exact; coverage is measured

The clip asks `SketchRegion` — the sketch's own exact 2D signed distance, closed form for lines
and arcs and Newton-refined for béziers — whether a point is at least `clearance` inside the
wall. That is a comparison against an exact number, not a tolerant containment test. The
default clearance is **half the achieved spacing**, which is what stops a bead of that width
running into the perimeter; pass `clearance: 0` to fill right up to the boundary.

Coverage is not inferred from the path length. `Footprint()` strokes the path through
`Region2dOffset.Stroke` — already the toolpath-footprint operation — and `CoveredArea()`
intersects that with the region:

```csharp run:infill-coverage
var plate = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(8));
double previous = 0;

foreach (double spacing in new[] { 8.0, 5.0, 3.0 })
{
    var fill = SpaceFillingInfill.Fill(plate, spacing);
    double covered = fill.CoveredArea();

    // A stroke of length L and width w covers at most L*w plus its caps, and LESS wherever the
    // path turns back over itself — which is why this is measured rather than taken as L*w.
    double caps = Math.PI * fill.Spacing * fill.Spacing / 4 * fill.Runs.Count;
    if (covered > fill.Length * fill.Spacing + caps) throw new Exception("over L*w");
    if (covered <= previous) throw new Exception("coverage must rise with order");
    previous = covered;

    Console.WriteLine($"spacing {fill.Spacing:F3}: {fill.CoveredFraction():P1} covered");
}
```

The fraction converges on 1 rather than reaching it: the clearance band along every wall is
uncovered by design, and so is the outer half-cell ring (the curve visits cell *centres*).

## What is refused, and why

A fill that quietly misses part of the region is the one failure it must not have, so both ways
that can happen are named:

```csharp run:infill-refusals
// (a) Nothing fits: eroding the region by the clearance leaves nothing at all.
try
{
    SpaceFillingInfill.Fill(Sketch.Rectangle(80, 1.5), 3.0);
    throw new Exception("should have refused");
}
catch (ArgumentException e) when (e.Message.Contains("would miss it entirely")) { }

// (b) There IS room, and the lattice's phase stepped over it. The same 80 x 1.5 plate with no
// clearance at all still misses: the achieved spacing is set by the plate's LENGTH, because
// the curve's footprint is the region's bounding square.
try
{
    SpaceFillingInfill.Fill(Sketch.Rectangle(80, 1.5), 3.0, clearance: 0);
    throw new Exception("should have refused");
}
catch (ArgumentException e) when (e.Message.Contains("stepped over it")) { }

// A plate thick enough for the same request fills happily — the refusal is about the thinness.
if (SpaceFillingInfill.Fill(Sketch.Rectangle(80, 12), 3.0).PointCount == 0)
    throw new Exception("a 12 mm plate fills");
```

A self-intersecting outline is refused by `Region2d`'s own simplicity guard, which names the
crossing segments — "inside" is exactly what the clip is asking, and a bow tie has no answer
that does not depend on an arbitrary fill rule. An *open* chain cannot be spelled at all: a
`Sketch` validates closure when it is built.

**The honest limit.** The refusals catch a whole connected piece being missed. A thin *neck*
inside a piece that is otherwise filled is not refused — the piece as a whole does catch
passes — and shows up in `CoveredFraction` instead. Check the fraction when a part has
features near the spacing.

## The curve on its own

`SpaceFillingCurve.Over` is usable without a sketch — for a scan order, a cache-friendly
traversal, or a decoration you clip yourself:

```csharp run:infill-curve-only
var curve = SpaceFillingCurve.Over(
    new Aabb((0, 0, 0), (100, 100, 0)), SpaceFillingFamily.Hilbert, spacing: 3.125);

// 100 / 2^5 = 3.125 lands exactly on an order boundary, so the request is honoured exactly.
if (curve.Order != 5 || curve.Spacing != 3.125) throw new Exception("exact hit");

// The closed form: an order-n Hilbert curve of 4^n cells draws 4^n - 1 segments.
long segments = SpaceFillingCurve.SegmentCount(SpaceFillingFamily.Hilbert, curve.Order);
if (segments != 1023) throw new Exception($"{segments} segments");
if (Math.Abs(curve.Length - segments * curve.Spacing) > 1e-9) throw new Exception("length");

// And the lattice underneath is pure integers: every step is exactly one cell.
var sites = curve.Lattice;
for (int i = 1; i < sites.Count; i++)
{
    if (!SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, sites[i - 1], sites[i]))
        throw new Exception($"step {i} is not a lattice step");
}
```

`Gosper` is the one family placed differently, and it is stated rather than hidden: its cells
are hexagons, so it fills a hexagonal *island* rather than a rectangle. It is scaled by its own
**measured** inradius — the largest disc about the island's centroid that its cells contain,
computed from the walk rather than tabulated — so that the island covers the region's bounding
circle. That makes its achieved spacing markedly finer than a square family's at the same
order, which is the honest price of a lattice that does not tile a rectangle.
