using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <see cref="FaceSplitter.SplitByCurves"/>' simultaneous arrangement: the case where the
/// one-curve-at-a-time cascade structurally cannot work, because each curve TERMINATES inside
/// the face and only the others give it somewhere to end. That is the shape a face-pair
/// intersection curve takes once it is clipped to the other face's trim.
///
/// <para>Verified through whole solids, like <see cref="OpenSplitTests"/>: splitting a face
/// patches its neighbours through their shared edges, so the rebuilt solid must stay manifold,
/// satisfy Euler–Poincaré and keep its exact volume.</para>
/// </summary>
public class SimultaneousSplitTests
{
    private static BrepSolid Rebuild(BrepSolid solid, BrepFace remove, IEnumerable<BrepFace> add) =>
        new([new BrepShell([.. solid.Faces.Where(f => !ReferenceEquals(f, remove)), .. add])]);

    private static BrepFace TopFace(BrepSolid solid) =>
        solid.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));

    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));

    [Fact]
    public void TwoClippedLinesMeetingInside_CutTheCornerOff()
    {
        var box = Box();
        var top = TopFace(box);

        // An L: each line starts or ends at (1, 1, 2), strictly inside the face. Neither can
        // split the face on its own — the cascade's first call has nothing to terminate on.
        var alongY = new Line3d((0, 1, 2), (1, 1, 2));
        var alongX = new Line3d((1, 1, 2), (1, 0, 2));
        Assert.Throws<NotSupportedException>(() => FaceSplitter.SplitByCurve(top, alongY));

        var parts = FaceSplitter.SplitByCurves(top, [alongY, alongX]);
        Assert.Equal(2, parts.Count);

        // The bitten corner and the rest classify oppositely.
        Assert.Single(parts, f => FaceGeometry.Contains(f, (0.5, 0.5, 2)));
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.5, 1.5, 2)));

        var solid = Rebuild(box, top, parts);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(7, solid.Faces.Count());

        var mesh = BRepTessellator.Tessellate(solid);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(8.0, mesh.Volume(), 9);
    }

    [Fact]
    public void FourClippedLinesFormingAClosedCycleInside_MakeAHoleAndADisk()
    {
        var box = Box();
        var top = TopFace(box);

        // The footprint an offset tool's four side walls cut on a host face once each line is
        // clipped to its own wall: a closed rectangle whose corners are all interior.
        Vector3d P(double x, double y) => new(x, y, 2);
        var cycle = new SplitCurve[]
        {
            new Line3d(P(0.5, 0.5), P(1.5, 0.5)),
            new Line3d(P(1.5, 0.5), P(1.5, 1.5)),
            new Line3d(P(1.5, 1.5), P(0.5, 1.5)),
            new Line3d(P(0.5, 1.5), P(0.5, 0.5)),
        };

        var parts = FaceSplitter.SplitByCurves(top, cycle);
        Assert.Equal(2, parts.Count);
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.0, 1.0, 2)));   // the disk
        Assert.Single(parts, f => FaceGeometry.Contains(f, (0.2, 0.2, 2)));   // the ring
        // The ring carries the cycle as a hole loop.
        Assert.Single(parts, f => f.Loops.Count == 2);

        var solid = Rebuild(box, top, parts);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        var mesh = BRepTessellator.Tessellate(solid);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(8.0, mesh.Volume(), 9);
    }

    [Fact]
    public void ClippedLineMeetingACrossingLine_SplitsBoth()
    {
        var box = Box();
        var top = TopFace(box);

        // A full chord plus a clipped spur that dies on it: a T-junction, which the crossing
        // detection has to find because there is no coincident endpoint to match.
        var chord = new Line3d((-1, 1, 2), (3, 1, 2));
        var spur = new Line3d((1.4, 1, 2), (1.4, 3, 2));

        var parts = FaceSplitter.SplitByCurves(top, [chord, spur]);
        Assert.Equal(3, parts.Count);
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.0, 0.5, 2)));   // below the chord
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.0, 1.5, 2)));   // above, left of the spur
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.7, 1.5, 2)));   // above, right of it

        var solid = Rebuild(box, top, parts);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        var mesh = BRepTessellator.Tessellate(solid);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(8.0, mesh.Volume(), 9);
    }

    [Fact]
    public void DanglingCurveWithNoPartner_TakesTheCascadeAndIsRefusedThere()
    {
        var top = TopFace(Box());
        var dangling = new Line3d((-1, 1, 2), (1, 1, 2));   // ends mid-face
        var elsewhere = new Line3d((-1, 1.8, 2), (3, 1.8, 2)); // a full chord, nowhere near it

        // Nothing terminates where the dangling end stops, so the arrangement could only
        // trade one refusal for another — this is the tracer-truncation shape, and the
        // incumbent path names it.
        var error = Assert.Throws<NotSupportedException>(
            () => FaceSplitter.SplitByCurves(top, [dangling, elsewhere]));
        Assert.Contains("must start and end outside the face", error.Message);
    }

    [Fact]
    public void DanglingEndTouchingAPartner_TakesTheArrangement()
    {
        var top = TopFace(Box());
        var dangling = new Line3d((-1, 1, 2), (1, 1, 2));      // ends mid-face...
        var partner = new Line3d((1, 1, 2), (1, 3, 2));        // ...exactly where this starts

        var parts = FaceSplitter.SplitByCurves(top, [dangling, partner]);
        Assert.Equal(2, parts.Count);
        Assert.Single(parts, f => FaceGeometry.Contains(f, (0.5, 0.5, 2)));
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.5, 1.5, 2)));
    }

    [Fact]
    public void OneCurveThatStandsAlone_TakesTheCascadeAndIsUnchanged()
    {
        // The routing decision must leave the incumbent path alone: a curve crossing the
        // boundary at both ends gives the same two faces through either entry point.
        var viaCurve = FaceSplitter.SplitByCurve(TopFace(Box()), new Line3d((-1, 1, 2), (3, 1, 2)));
        var viaCurves = FaceSplitter.SplitByCurves(TopFace(Box()), [new Line3d((-1, 1, 2), (3, 1, 2))]);

        Assert.Equal(2, viaCurve.Count);
        Assert.Equal(viaCurve.Count, viaCurves.Count);
        for (int i = 0; i < viaCurve.Count; i++)
        {
            Assert.Equal(viaCurve[i].Loops.Count, viaCurves[i].Loops.Count);
            Assert.Equal(
                viaCurve[i].OuterLoop.Coedges.Count,
                viaCurves[i].OuterLoop.Coedges.Count);
        }
    }
}
