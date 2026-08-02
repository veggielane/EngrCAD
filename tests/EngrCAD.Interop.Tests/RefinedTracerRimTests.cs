using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Tessellation-time refinement of marching-tracer intersection curves. A traced
/// polyline's sample count is fixed at boolean time, so the facets straddling it used to
/// disagree MORE with the exact surface as the density rose — measured 0.9988 → 0.9460 →
/// 0.3229 worst normal agreement at 32/96/192 on a bore crossing a whole-solid fillet's
/// bands. The curve now carries the two exact surfaces it was traced on
/// (<see cref="PolylineCurve3d.Carriers"/>), and <see cref="BRepTessellator.SampleEdge"/>
/// refines each chord back onto the exact intersection
/// (<see cref="SurfaceCorner.TrySolvePoint"/>) until it subtends at most one natural
/// angular step.
/// </summary>
public class RefinedTracerRimTests
{
    private static BrepSolid RoundedBox() =>
        Filleting.FilletAllEdges(SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))), 2);

    private static BrepSolid BandCrossingBore() => BrepBoolean.Difference(
        RoundedBox(), Shape.Cylinder(3, 30).Translate((10, 1.2, 4)).ToBrep());

    [Fact]
    public void BandCrossingBore_NoLongerDegradesWithDensity()
    {
        // The reason this feature exists: the worst facet-vs-surface agreement must not
        // FALL as the density rises. The floors here are the measured post-fix values
        // less a safety margin; the pre-fix values (0.9460 at 96, 0.3229 at 192) sit far
        // below them, so a lost carrier or a disabled refinement fails loudly.
        var solid = BandCrossingBore();
        double at96 = TessellationQuality.Audit(solid, 96, 48).WorstDot;
        double at192 = TessellationQuality.Audit(solid, 192, 96).WorstDot;
        Assert.True(at96 > 0.99, $"96/48 worst agreement {at96}");
        Assert.True(at192 > 0.99, $"192/96 worst agreement {at192}");
        // And rising density must not fold anything.
        Assert.Equal(0, TessellationQuality.Audit(solid, 192, 96).Folds);
    }

    [Fact]
    public void RefinementOnlyInserts_TheBakedVerticesPassThroughVerbatim()
    {
        // The safety argument, asserted structurally: refinement INSERTS between baked
        // vertices and never moves one, so every pre-fix sample is still present
        // bit-for-bit at every density — which is also what keeps a coarse density (where
        // no chord exceeds the angular step) bit-identical to the pre-carrier output.
        var solid = BandCrossingBore();
        foreach (var edge in solid.Edges)
        {
            if (edge.Curve.Underlying is not PolylineCurve3d { Carriers: not null })
                continue;
            var refined = new HashSet<Vector3d>(BRepTessellator.SampleEdge(edge, 192, 96));
            var parameters = FaceGeometry.ExactSampleParameters(
                edge.Curve, edge.Domain.Start, edge.Domain.End, 96);
            foreach (double t in parameters)
                Assert.Contains(edge.Curve.PointAt(t), refined); // exact containment, no tolerance
        }
    }

    [Fact]
    public void RefinedPointsLieOnBothCarriers()
    {
        var solid = BandCrossingBore();
        bool sawRefinement = false;
        foreach (var edge in solid.Edges)
        {
            if (edge.Curve.Underlying is not PolylineCurve3d { Carriers: { } carriers })
                continue;
            var coarse = BRepTessellator.SampleEdge(edge, 32, 16);
            var refined = BRepTessellator.SampleEdge(edge, 192, 96);
            if (refined.Count <= coarse.Count)
                continue;
            sawRefinement = true;
            foreach (var point in refined)
            {
                // Weld tier: refined vertices are shared seam geometry, so they must sit
                // on BOTH exact surfaces to the same standard as the baked vertices.
                Assert.True(
                    ImplicitDistance(carriers.A, point) < 1e-8
                    && ImplicitDistance(carriers.B, point) < 1e-8,
                    $"refined point {point} off its carriers");
            }
        }
        Assert.True(sawRefinement, "no edge refined at 192/96 — the fixture no longer carries the configuration");
    }

    [Fact]
    public void TheWholeSolidStillWeldsClosedAtEveryDensity()
    {
        var solid = BandCrossingBore();
        foreach (var (s, c) in ((int, int)[])[(16, 8), (48, 24), (96, 48), (192, 96)])
        {
            var mesh = BRepTessellator.Tessellate(solid, s, c);
            mesh.Validate();
            Assert.True(mesh.IsClosed, $"open at {s}/{c}");
        }
    }

    [Fact]
    public void CarriersSurviveTheArchiveRoundTrip()
    {
        // The archive holds reload volumes to 1e-12 relative through a DETERMINISTIC
        // tessellation, so a dropped carrier pair would make a reloaded solid tessellate
        // coarser than the one that was saved. Asserted directly on the curves.
        var solid = BandCrossingBore();
        var restored = BrepArchive.Read(BrepArchive.Write(solid)).Single();

        int before = CarrierCount(solid);
        Assert.True(before > 0, "the fixture carries no tracer curves with carriers");
        Assert.Equal(before, CarrierCount(restored));

        // And the tessellations agree exactly, refinement included.
        Assert.Equal(
            BRepTessellator.Tessellate(solid, 96, 48).Volume(),
            BRepTessellator.Tessellate(restored, 96, 48).Volume());
    }

    private static int CarrierCount(BrepSolid solid) =>
        solid.Edges.Count(e => e.Curve.Underlying is PolylineCurve3d { Carriers: not null });

    private static double ImplicitDistance(Surface surface, in Vector3d point)
    {
        // The audit's own ruler: the surface's closed-form implicit distance where it has
        // one (every carrier that refines has one, or TrySolvePoint would have refused).
        Assert.True(SurfaceCorner.TrySolvePoint([surface], point, out var corner, out _));
        return corner.Point.DistanceTo(point);
    }
}
