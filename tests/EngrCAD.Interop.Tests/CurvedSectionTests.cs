using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The section of a B-Rep as EXACT 2D curves rather than as chords.
/// <para>The oracle throughout is an AREA against a closed form: a flattened section of a
/// bore is an inscribed n-gon and is short by a fixed amount that no chord tolerance
/// removes — <c>πr²(1 − (n/2π)sin(2π/n))</c> — while an exact one is πr² to round-off. That
/// is what separates "the section improved" from "the section is the geometry".</para>
/// </summary>
public class CurvedSectionTests
{
    private static readonly SketchPlane MidPlate =
        SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);

    private static Shape DrilledPlate() =>
        Shape.Box(60, 40, 10).Drill(HoleSpec.Simple(8), [new(-15, 0), new(15, 0)], 20,
            SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY));

    /// <summary>
    /// A drilled plate's mid-plane section: one rectangle with two round holes, and the
    /// holes are ONE arc edge each rather than a polygon. The area is the closed form to
    /// round-off, where the flattened section is measurably short.
    /// </summary>
    [Fact]
    public void ADrilledPlatesSectionKeepsItsBoresAsExactCircles()
    {
        var plate = DrilledPlate();
        var exact = plate.SectionExact(MidPlate);

        var region = Assert.Single(exact);
        Assert.Equal(2, region.Holes.Count);
        // Four straight sides, and each bore a single closed arc.
        Assert.Equal(4, region.Outer.Count);
        Assert.All(region.Holes, h => Assert.Single(h));
        Assert.All(region.Holes, h => Assert.True(h[0].IsArc));

        double expected = 60.0 * 40 - 2 * Math.PI * 16;
        Assert.Equal(expected, region.Area, 1e-9);

        // And the flattened route is short by exactly the inscribed n-gon's deficit, which
        // is a FLOOR rather than a tolerance: the same section at a ten times finer chord
        // tolerance is still short, just less so.
        double flattened = plate.Section(MidPlate).Sum(r => r.Area);
        double finer = plate.Section(MidPlate, chordTolerance: 1e-4).Sum(r => r.Area);
        Assert.True(flattened > expected, "an inscribed polygon leaves MORE material");
        Assert.True(finer > expected);
        Assert.True(finer < flattened);
    }

    /// <summary>
    /// A cylinder cut across its axis is one circle — the case with no straight edges at
    /// all, so the whole loop is the closed-curve emit.
    /// </summary>
    [Fact]
    public void ACylindersSectionIsOneCircle()
    {
        var section = Shape.Cylinder(7, 20).SectionExact(
            SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        var region = Assert.Single(section);
        Assert.Empty(region.Holes);
        var edge = Assert.Single(region.Outer);
        Assert.True(edge.IsArc);
        Assert.Equal(7, edge.Radius, 1e-9);
        Assert.Equal(Math.PI * 49, region.Area, 1e-9);
    }

    /// <summary>
    /// A section that CROSSES a bore rather than passing through it: the loop mixes lines
    /// and arcs, and the arcs are partial. This is the case the chaining has to get right —
    /// each face contributes a run and they meet at shared edge crossings.
    /// </summary>
    [Fact]
    public void AMixedLoopChainsArcsAndLinesTogether()
    {
        // A plate whose bore breaks out of one side: the section's outline runs along the
        // plate's edges and round part of the bore.
        var plate = Shape.Box(40, 30, 10)
            .Drill(HoleSpec.Simple(10), [new(20, 0)], 20,
                SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY));
        var section = plate.SectionExact(MidPlate);
        var region = Assert.Single(section);
        Assert.Empty(region.Holes);
        Assert.Contains(region.Outer, e => e.IsArc);
        Assert.Contains(region.Outer, e => !e.IsArc);

        // Area: the rectangle less the half disc the bore removes from its edge.
        Assert.Equal(40.0 * 30 - Math.PI * 25 / 2, region.Area, 1e-6);
    }

    /// <summary>
    /// What the tier cannot carry exactly is FLATTENED rather than refused — an oblique
    /// plane through a cylinder cuts an ellipse, which the curved 2D tier deliberately does
    /// not have — and the answer is still a valid region of the right area.
    /// </summary>
    [Fact]
    public void AnObliqueCutThroughACylinderFlattensItsEllipse()
    {
        double tilt = Math.PI / 8;
        var oblique = SketchPlane.At(
            (0, 0, 0),
            Vector3d.UnitX,
            new Vector3d(0, Math.Cos(tilt), Math.Sin(tilt)));
        var section = Shape.Cylinder(6, 40).SectionExact(oblique, chordTolerance: 1e-5);
        var region = Assert.Single(section);

        // No arc survives (an ellipse is not one), but the area is the ellipse's πab to
        // the flattening's OWN accuracy — an inscribed polygon is short by roughly
        // (2/3)·perimeter·sagitta, which is ~2.7e-4 for this ellipse at 1e-5.
        Assert.All(region.Outer, e => Assert.False(e.IsArc));
        double exact = Math.PI * 6 * (6 / Math.Cos(tilt));
        Assert.True(region.Area < exact, "an inscribed polygon is short");
        Assert.Equal(exact, region.Area, 5e-4);
    }

    /// <summary>
    /// The exact and the flattened sections describe the SAME set, which is the check that
    /// the shared piece enumeration really is shared: every vertex of the flattened outline
    /// lies on the exact region's boundary to the chord tolerance.
    /// </summary>
    [Fact]
    public void TheExactAndFlattenedSectionsAgreeOnTheSameOutline()
    {
        var plate = DrilledPlate();
        var flattened = plate.Section(MidPlate, chordTolerance: 1e-4);
        var exact = plate.SectionExact(MidPlate);

        var region = Assert.Single(exact);
        foreach (var polygon in flattened)
        {
            foreach (var loop in polygon.AllLoops())
            {
                foreach (var p in loop)
                {
                    double distance = region.AllLoops()
                        .SelectMany(l => l)
                        .Min(e => DistanceToEdge(e, p));
                    Assert.True(distance < 1e-3, $"{p} is {distance:e3} from the exact section");
                }
            }
        }
    }

    /// <summary>Exact point-to-edge distance. Sampling would not do: 64 samples of a
    /// radius-4 circle are 0.39 apart, so a point exactly ON it measures up to 0.2 away and
    /// the assertion would be about the sampling rather than about the geometry.</summary>
    private static double DistanceToEdge(CurvedEdge2d edge, in Vector2d p)
    {
        if (!edge.IsArc)
        {
            var d = edge.End - edge.Start;
            double lengthSquared = d.LengthSquared;
            double t = lengthSquared > 0
                ? Math.Clamp((p - edge.Start).Dot(d) / lengthSquared, 0, 1)
                : 0;
            return (edge.Start + d * t - p).Length;
        }

        var radial = p - edge.Center;
        double angle = Math.Atan2(radial.Y, radial.X);
        double from = edge.StartAngle, sweep = edge.SweepAngle;
        double delta = angle - from;
        delta -= 2 * Math.PI * Math.Floor(delta / (2 * Math.PI));      // into [0, 2π)
        double along = sweep >= 0 ? delta : delta - 2 * Math.PI;       // onto the sweep's side
        if (Math.Abs(along) <= Math.Abs(sweep))
            return Math.Abs(radial.Length - edge.Radius);
        return Math.Min((edge.Start - p).Length, (edge.End - p).Length);
    }
}
