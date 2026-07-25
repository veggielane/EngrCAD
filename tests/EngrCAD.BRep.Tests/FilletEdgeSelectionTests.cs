using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Addressing rim features by EDGE instead of by face: the selection is resolved to the
/// complete planar face rims it covers, and anything else is refused before any surgery
/// touches the solid.
/// </summary>
public class FilletEdgeSelectionTests
{
    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (4, 3, 2)));

    private static BrepFace TopFace(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    [Fact]
    public void CompleteRim_ResolvesToItsFace()
    {
        var box = Box();
        var top = TopFace(box);
        var resolved = Filleting.RimFacesFor(box, top.RimEdges());
        Assert.Same(top, Assert.Single(resolved));
    }

    [Fact]
    public void TwoDisjointRims_ResolveToBothFaces()
    {
        var box = Box();
        var top = TopFace(box);
        var bottom = box.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();
        var resolved = Filleting.RimFacesFor(box, [.. top.RimEdges(), .. bottom.RimEdges()]);
        Assert.Equal(2, resolved.Count);
        Assert.Contains(top, resolved);
        Assert.Contains(bottom, resolved);
    }

    [Fact]
    public void FilletEdges_MatchesTheFaceOverload()
    {
        var faceBox = Box();
        var byFace = Filleting.FilletRim(faceBox, TopFace(faceBox), 0.4);
        var edgeBox = Box();
        var byEdges = Filleting.FilletEdges(edgeBox, TopFace(edgeBox).RimEdges(), 0.4);

        byEdges.Validate();
        Assert.Equal(byFace.Faces.Count(), byEdges.Faces.Count());
        Assert.Equal(byFace.Edges.Count(), byEdges.Edges.Count());
        Assert.Equal(4, byEdges.Edges.Count(e => e.Curve is Ellipse3d));
    }

    [Fact]
    public void ChamferEdges_TopAndBottomRims_StaysValid()
    {
        var box = Box();
        var rims = box.PlanarFacesWithNormal(Vector3d.UnitZ)
            .Concat(box.PlanarFacesWithNormal(-Vector3d.UnitZ))
            .SelectMany(f => f.RimEdges())
            .ToList();
        var chamfered = Filleting.ChamferEdges(box, rims, 0.3);
        chamfered.Validate();
        Assert.True(chamfered.SatisfiesEulerFormula(genus: 0));
        // 4 side faces (shortened at both ends) + 2 shrunk caps + 8 chamfer strips.
        Assert.Equal(14, chamfered.Faces.Count());
    }

    [Fact]
    public void ClosedCircularRim_ResolvesToItsCap()
    {
        var puck = SolidFactory.MakeCylinder(1.5, 2.0);
        var top = puck.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        var filleted = Filleting.FilletEdges(puck, top.RimEdges(), 0.4);
        filleted.Validate();
        Assert.Single(filleted.Faces, f => f.Surface is RevolvedSurface); // the quarter torus
    }

    [Fact]
    public void PartialRun_IsRefusedWithGuidance()
    {
        var box = Box();
        var twoOfFour = TopFace(box).RimEdges().Take(2).ToList();
        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletEdges(box, twoOfFour, 0.2));
        Assert.Contains("complete rims", exception.Message);
    }

    [Fact]
    public void AllEdgesOfABox_IsRefused_AndLeavesTheSolidUntouched()
    {
        // Every vertex would need a spherical corner patch where three blended edges meet.
        var box = Box();
        int facesBefore = box.Faces.Count();
        Assert.Throws<NotSupportedException>(() => Filleting.FilletEdges(box, box.Edges, 0.2));
        // Resolution happens before any surgery, so the input is still a valid box.
        box.Validate();
        Assert.Equal(facesBefore, box.Faces.Count());
    }

    [Fact]
    public void ConvexEdges_FindsEveryBoxEdge()
    {
        var box = Box();
        Assert.Equal(12, box.ConvexEdges().Count());
    }

    [Fact]
    public void EmptySelection_Throws() =>
        Assert.Throws<ArgumentException>(() => Filleting.RimFacesFor(Box(), []));

    [Fact]
    public void ChamferAtAngle_SetsTheSetbacksFromTheAngle()
    {
        // 30° from the top face: the neighbours drop setback·tan(30°).
        double setback = 0.5, drop = 0.5 * Math.Tan(Math.PI / 6);
        var box = Box();
        var byAngle = Filleting.ChamferRimAtAngle(box, TopFace(box), setback, 30);
        byAngle.Validate();

        var lowered = byAngle.Vertices.Select(v => v.Position).Where(p => p.Z < 2 - 1e-9 && p.Z > 1e-9).ToList();
        Assert.Equal(4, lowered.Count);
        Assert.All(lowered, p => Assert.Equal(2 - drop, p.Z, 12));
    }

    [Fact]
    public void ChamferAtAngle_RejectsDegenerateAngles()
    {
        var box = Box();
        Assert.Throws<ArgumentOutOfRangeException>(() => Filleting.ChamferRimAtAngle(box, TopFace(box), 0.2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Filleting.ChamferRimAtAngle(box, TopFace(box), 0.2, 90));
    }
}
