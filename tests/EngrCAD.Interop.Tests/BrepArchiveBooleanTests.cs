using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The native <c>.ecb</c> archive over BOOLEAN output — the geometry that actually stresses
/// it. A factory solid's curves are all analytic; a boolean's are not: fragments carry
/// <see cref="CurveSegment"/> mappings (which may run past a closed base's domain end),
/// tracer edges are <see cref="PolylineCurve3d"/>s, faces come back with
/// <c>IsReversed</c> set, and edge domains are TRIMMED sub-ranges rather than the carrier's
/// own. This is also where the volume oracle lives: the BRep-side tests fingerprint sampled
/// geometry, and a tessellated volume is the independent second opinion.
/// </summary>
public class BrepArchiveBooleanTests
{
    private static readonly BrepMassPropertyOptions Fine = new() { SegmentsPerCircle = 96, CurveSamples = 48 };

    private static readonly string[] Names =
    [
        "drilled-plate", "cross-drilled-block", "sphere-cavity", "notched-block",
        "counterbored-plate",
    ];

    public static TheoryData<string> Corpus => [.. Names];

    private static BrepSolid Build(string name) => name switch
    {
        "drilled-plate" => (Shape.Box(40, 30, 8) - Shape.Cylinder(5, 40)).ToBrep(),
        "cross-drilled-block" =>
            (Shape.Box(30, 30, 30)
             - Shape.Cylinder(6, 60)
             - Shape.Cylinder(4, 60).Rotate(Vector3d.UnitX, 90)).ToBrep(),
        "sphere-cavity" => (Shape.Box(20, 20, 20) - Shape.Sphere(7)).ToBrep(),
        "notched-block" =>
            (Shape.Box(30, 20, 10) - Shape.Box(10, 30, 6).Translate(12, 0, 4)).ToBrep(),
        "counterbored-plate" => Shape.Box(40, 40, 12)
            .Drill(StandardHoles.Counterbored(6), [(0, 0)], 12)
            .ToBrep(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown corpus member"),
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void BooleanOutputRoundTripsWithItsVolumeAndTopology(string name)
    {
        var original = Build(name);
        var archive = BrepArchive.Write(original, name);
        var restored = BrepArchive.Read(archive).Single();

        Assert.Equal(original.Faces.Count(), restored.Faces.Count());
        Assert.Equal(original.Loops.Count(), restored.Loops.Count());
        Assert.Equal(original.Coedges.Count(), restored.Coedges.Count());
        Assert.Equal(original.Edges.Count(), restored.Edges.Count());
        Assert.Equal(original.Vertices.Count(), restored.Vertices.Count());
        restored.Validate();

        // Volume to 1e-12 RELATIVE. The tessellation is deterministic, so an exactly
        // reconstructed solid gives an exactly equal volume; anything looser here would
        // hide a curve that came back as an approximation of itself.
        double before = BrepMassProperties.Compute(original, options: Fine).Volume;
        double after = BrepMassProperties.Compute(restored, options: Fine).Volume;
        Assert.True(before > 0, $"'{name}' has non-positive volume before the round trip");
        Assert.True(
            Math.Abs(after - before) <= 1e-12 * Math.Abs(before),
            $"'{name}' volume {before} -> {after} (relative change "
            + $"{Math.Abs(after - before) / Math.Abs(before):E3})");

        // Sharing: every edge still used by exactly two coedges, and reversed faces still
        // reversed (Difference sets IsReversed, and a flag lost in transit produces a
        // solid that validates and tessellates inside out).
        Assert.All(restored.Edges, e => Assert.Equal(2, e.Uses.Count));
        Assert.Equal(
            original.Faces.Count(f => f.IsReversed),
            restored.Faces.Count(f => f.IsReversed));

        // Byte-identical fixed point, the strong form of "exact".
        Assert.Equal(archive, BrepArchive.Write(restored, name));
    }

    [Fact]
    public void TrimmedDomainsAndCurveSegmentMappingsSurviveVerbatim()
    {
        // An archive that wrote a CARRIER's domain instead of the edge's, or that
        // normalized a CurveSegment's base range, would still validate, still share
        // topology, and describe a different solid. Both are checked across the whole
        // corpus, with existence guards so the assertions cannot pass on nothing.
        bool sawTrimmedDomain = false;
        bool sawNonTrivialSegment = false;

        foreach (var name in Names)
        {
            var original = Build(name);
            var restored = BrepArchive.Read(BrepArchive.Write(original)).Single();

            var domainsBefore = original.Edges.Select(e => (e.Domain.Start, e.Domain.End)).ToList();
            var domainsAfter = restored.Edges.Select(e => (e.Domain.Start, e.Domain.End)).ToList();
            Assert.Equal(domainsBefore, domainsAfter);

            var segmentsBefore = Segments(original);
            var segmentsAfter = Segments(restored);
            Assert.Equal(segmentsBefore, segmentsAfter);

            sawTrimmedDomain |= original.Edges.Any(e =>
                e.Domain.Start != e.Curve.Domain.Start || e.Domain.End != e.Curve.Domain.End);
            // "Non-trivial" = the segment does not simply cover its base's whole domain,
            // which is exactly the case a normalizing writer would silently lose. Note
            // BaseEnd may exceed the base's domain end (a seam-crossing arc) or fall below
            // BaseStart (a reversed run) — neither is validated on the way back in.
            sawNonTrivialSegment |= segmentsBefore.Any(s =>
                s.Start != s.BaseDomainStart || s.End != s.BaseDomainEnd);
        }

        Assert.True(sawNonTrivialSegment,
            "no corpus member produced a non-trivial CurveSegment mapping, so the equality "
            + "assertion above proves nothing.");
        Assert.True(sawTrimmedDomain,
            "no corpus member produced an edge whose domain differs from its carrier's, so "
            + "the domain assertion above proves nothing.");
    }

    private static List<(double Start, double End, double BaseDomainStart, double BaseDomainEnd)>
        Segments(BrepSolid solid) =>
        [.. solid.Edges
            .Select(e => e.Curve)
            .OfType<CurveSegment>()
            .Select(s => (s.BaseStart, s.BaseEnd, s.Base.Domain.Start, s.Base.Domain.End))];

    [Fact]
    public void ARestoredSolidStillTessellatesClosed()
    {
        // The end-to-end consumer check: a solid that came back subtly wrong usually still
        // validates and only fails when something tries to weld it.
        var restored = BrepArchive.Read(BrepArchive.Write(Build("cross-drilled-block"))).Single();
        var mesh = BRepTessellator.Tessellate(restored, segmentsPerCircle: 48, curveSamples: 24);
        Assert.True(mesh.IsClosed, "the restored solid tessellated to an open mesh");
    }

    [Fact]
    public void ARestoredSolidCanBeBooleanedAgain()
    {
        // Round-tripping must produce a solid the kernel can keep working on, not just one
        // it can look at. BrepBoolean consumes its inputs, so this also proves the restored
        // topology is a complete, independently-owned graph.
        var restored = BrepArchive.Read(BrepArchive.Write(Build("drilled-plate"))).Single();
        var result = BrepBoolean.Difference(
            restored, Shape.Cylinder(3, 40).Translate(14, 0, 0).ToBrep());
        result.Validate();
        Assert.True(BrepMassProperties.Compute(result, options: Fine).Volume > 0);
    }

    [Fact]
    public void PolylineCarriersRoundTripAtTheirVertices()
    {
        // A tracer edge is on its carrier surface only at its VERTICES, so those are the
        // points that must survive verbatim — the sampling rule the whole kernel obeys.
        var original = Build("cross-drilled-block");
        var restored = BrepArchive.Read(BrepArchive.Write(original)).Single();

        var before = Polylines(original);
        var after = Polylines(restored);
        Assert.NotEmpty(before);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
            Assert.Equal(before[i], after[i]);
    }

    private static List<Vector3d[]> Polylines(BrepSolid solid) =>
        [.. solid.Edges
            .Select(e => e.Curve.Underlying)
            .OfType<PolylineCurve3d>()
            .Select(p => p.Points.ToArray())];
}
