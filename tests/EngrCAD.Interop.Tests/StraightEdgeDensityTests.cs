using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A straight edge is described exactly by its two endpoints — and the FACE it bounds may
/// not be. <c>BRepTessellator.SampleEdge</c> therefore gives a straight edge the ANGULAR
/// density of any face whose parameter is an azimuth that the edge actually crosses.
///
/// <para>The case that forced it: a <c>Shape.Drill</c> tool's flat bottom is a full-turn
/// <see cref="RevolvedSurface"/> whose u is an azimuth about the pole, so a face cutting it
/// obliquely leaves a CHORD — and a chord's two endpoints both sit on the rim, at the same
/// v the arc completing the loop already occupies. Pulled back, the loop is a zero-area
/// sliver out along v = 1 and back, which the trimmed tessellator refuses as a winding
/// structure it cannot read however fine the grid around it becomes.</para>
///
/// <para>The tests below pin BOTH halves, because the rule's whole safety argument is that
/// its gate IS its correctness condition: an edge that sweeps azimuth gets samples, and an
/// ISO-PARAMETER straight edge on the very same surface — every straight edge that existed
/// on an angular face before this rule — sweeps nothing and stays at exactly two.</para>
/// </summary>
public class StraightEdgeDensityTests
{
    private const double Radius = 3;

    /// <summary>A flat disk of <see cref="Radius"/> in the z = 0 plane, as a full-turn
    /// revolve of a radial generator about +Z — the shape a drill tool's flat bottom is.</summary>
    private static RevolvedSurface Disk() => new(
        new Line3d(Vector3d.Zero, new Vector3d(Radius, 0, 0)), Vector3d.Zero, Vector3d.UnitZ);

    /// <summary>A cylinder of <see cref="Radius"/> about +Z — an angular face whose only
    /// straight edges are rulings.</summary>
    private static CylinderSurface Cylinder() =>
        new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, Radius);

    /// <summary>An edge over <paramref name="curve"/> used once by a face over
    /// <paramref name="surface"/>. The loop is not closed and does not need to be: the
    /// density rule reads the using face's SURFACE, nothing else.</summary>
    private static BrepEdge EdgeOn(Curve3d curve, Surface surface)
    {
        var edge = new BrepEdge(
            curve, curve.Domain,
            new BrepVertex(curve.PointAt(curve.Domain.Start)),
            new BrepVertex(curve.PointAt(curve.Domain.End)));
        _ = new BrepFace(surface, [new BrepLoop([new BrepCoedge(edge, true)])]);
        return edge;
    }

    /// <summary>The chord of the disk at signed distance <paramref name="d"/> from the
    /// pole, running the full width of the disk.</summary>
    private static Line3d Chord(double d)
    {
        double half = Math.Sqrt(Radius * Radius - d * d);
        return new Line3d((-half, d, 0), (half, d, 0));
    }

    /// <summary>The azimuth such a chord sweeps about the pole: its endpoints sit at
    /// asin(d/R) either side of the far apex, so the span is pi - 2 asin(d/R) — exactly
    /// pi for a DIAMETRAL chord, which passes through the pole where u does not exist.</summary>
    private static double ChordSpan(double d) => Math.PI - 2 * Math.Asin(Math.Abs(d) / Radius);

    [Theory]
    [InlineData(1.5)]
    [InlineData(0.5)]
    [InlineData(2.9)]
    [InlineData(0.0)]
    public void AChordAcrossADiskTakesTheDisksOwnAngularDensity(double d)
    {
        const int segmentsPerCircle = 64;
        var samples = BRepTessellator.SampleEdge(
            EdgeOn(Chord(d), Disk()), segmentsPerCircle, curveSamples: 24);

        // One sample per natural u column the chord crosses — the SAME Ceiling rule the
        // helical rails use, so an edge and the grid it welds to cannot round apart.
        int expected = Math.Max(1, (int)Math.Ceiling(
            ChordSpan(d) * segmentsPerCircle / (2 * Math.PI) - 1e-9)) + 1;
        Assert.Equal(expected, samples.Count);

        // Extra samples on a straight curve carry no fidelity cost: every one is exactly
        // on the curve, which is the whole argument for spending them.
        foreach (var p in samples)
            Assert.Equal(Math.Abs(d), p.Y, 12);
        Assert.Equal(0, samples[0].DistanceTo(Chord(d).PointAt(Chord(d).Domain.Start)), 12);
        Assert.Equal(0, samples[^1].DistanceTo(Chord(d).PointAt(Chord(d).Domain.End)), 12);
    }

    [Fact]
    public void TheDensityFollowsTheStatedSegmentsPerCircle()
    {
        // Not a fixed count: doubling the density the caller asked for doubles the columns
        // the chord crosses, so it must double the samples the chord spends on them. A
        // FLOOR here is exactly the defect the helical spiral edges had.
        int[] counts = [.. new[] { 32, 64, 128, 256 }.Select(n =>
            BRepTessellator.SampleEdge(EdgeOn(Chord(1.5), Disk()), n, 24).Count - 1)];

        // Ceiling is not multiplicative, so doubling the density can land one short of
        // twice the count and never anywhere else: ceil(2x) is 2*ceil(x) or one less.
        for (int i = 1; i < counts.Length; i++)
            Assert.InRange(counts[i], 2 * counts[i - 1] - 1, 2 * counts[i - 1]);
        Assert.Equal(11, counts[0]);
        Assert.Equal(86, counts[^1]);
    }

    [Fact]
    public void ARadialSeamOnTheSameDiskStaysAtTwoSamples()
    {
        // The gate IS the correctness condition. A radial line is ISO-parameter on the
        // disk, so it sweeps no azimuth and needs no samples — decided by measuring the
        // sweep rather than by a separate "is this iso-parameter" test that could drift.
        var radial = new Line3d(Vector3d.Zero, new Vector3d(Radius, 0, 0));
        Assert.Equal(2, BRepTessellator.SampleEdge(EdgeOn(radial, Disk()), 256, 24).Count);
    }

    [Fact]
    public void ARulingOnACylinderStaysAtTwoSamples()
    {
        // The other angular family, and the reason the rule can be stated for every one of
        // them at once: a straight line ON a cylinder is a ruling, so it too sweeps nothing.
        var ruling = new Line3d((Radius, 0, -5), (Radius, 0, 5));
        Assert.Equal(2, BRepTessellator.SampleEdge(EdgeOn(ruling, Cylinder()), 256, 24).Count);
    }

    [Fact]
    public void AStraightEdgeOnPlanarFacesStaysAtTwoSamples()
    {
        // A box's every edge: no using face has an angular parameter at all, so the rule
        // never fires and the incumbent two-point answer is returned by the same expressions
        // it always was.
        var line = new Line3d((0, 0, 0), (10, 4, 0));
        var plane = new PlaneSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        Assert.Equal(2, BRepTessellator.SampleEdge(EdgeOn(line, plane), 256, 24).Count);
    }

    [Fact]
    public void TheCountIsTheMaximumOverEveryUsingFace()
    {
        // SampleEdge fills ONE polyline per edge and both sides read it, so a density that
        // satisfied only one of them would leave the other's loop unreadable. Here a chord
        // is shared between the disk that needs 22 samples and a plane that needs 2.
        var chord = Chord(1.5);
        var edge = new BrepEdge(
            chord, chord.Domain,
            new BrepVertex(chord.PointAt(chord.Domain.Start)),
            new BrepVertex(chord.PointAt(chord.Domain.End)));
        _ = new BrepFace(
            new PlaneSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ),
            [new BrepLoop([new BrepCoedge(edge, true)])]);
        _ = new BrepFace(Disk(), [new BrepLoop([new BrepCoedge(edge, false)])]);

        int expected = (int)Math.Ceiling(ChordSpan(1.5) * 64 / (2 * Math.PI) - 1e-9) + 1;
        Assert.Equal(expected, BRepTessellator.SampleEdge(edge, 64, 24).Count);
    }
}
