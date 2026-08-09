using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// EDGE provenance — the UNION of the construction-step tags of the faces an edge borders,
/// the edge form of <see cref="BrepFace.Provenance"/>. The decision the face-provenance note
/// left open is settled as UNION (an edge is "of" a step whenever it touches a face of that
/// step), because the motivating query "the edges of the boss" wants the boss's BASE rim,
/// which borders a boss face and a non-boss one — an intersection would drop exactly it.
/// These are measurements: each test tags a known face and asserts WHERE the tag reaches on
/// the edges, not merely that some edge has it.
/// </summary>
public class EdgeProvenanceTests
{
    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((-10, -10, 0), (10, 10, 10)));

    private static bool AtTop(BrepEdge e) =>
        Math.Abs(e.StartVertex.Position.Z - 10) < 1e-9 && Math.Abs(e.EndVertex.Position.Z - 10) < 1e-9;

    private static BrepFace FaceWithNormal(BrepSolid box, Vector3d direction) =>
        box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Dot(direction) > 1 - 1e-9);

    [Fact]
    public void AnEdgeReportsTheTagOfEitherFace_AndAnUntaggedNeighbourReportsNone()
    {
        var box = Box();
        FaceWithNormal(box, Vector3d.UnitZ).AddProvenance("lid");

        // The four rim edges of the top face each border it (tagged) and a side (untagged),
        // so union reports "lid" on all four.
        var topEdges = box.Edges.Where(AtTop).ToList();
        Assert.Equal(4, topEdges.Count);
        foreach (var e in topEdges)
        {
            Assert.Single(e.Provenance());
            Assert.Equal("lid", e.Provenance()[0]);
            Assert.True(e.DescendsFrom("lid"));
        }

        // Every other edge borders only untagged faces.
        foreach (var e in box.Edges.Where(e => !AtTop(e)))
            Assert.Empty(e.Provenance());

        // The set query returns exactly the four rim edges, and nothing for an unused tag.
        Assert.Equal(4, box.EdgesTagged("lid").Count());
        Assert.All(box.EdgesTagged("lid"), e => Assert.True(AtTop(e)));
        Assert.Empty(box.EdgesTagged("nothing"));
    }

    [Fact]
    public void WhereTwoTaggedFacesMeet_TheSharedEdgeReportsBOTH_WhichIsWhyUnionNotIntersection()
    {
        // The decision made measurable: tag the top "lid" and the +X wall "wall". The edge
        // they share borders both, so UNION reports {"lid","wall"} where an intersection —
        // "belongs to a step only when both faces do" — would report nothing there and drop
        // the very rim a caller most wants to blend.
        var box = Box();
        FaceWithNormal(box, Vector3d.UnitZ).AddProvenance("lid");
        FaceWithNormal(box, Vector3d.UnitX).AddProvenance("wall");

        var shared = box.Edges.Single(e =>
            AtTop(e)
            && Math.Abs(e.StartVertex.Position.X - 10) < 1e-9
            && Math.Abs(e.EndVertex.Position.X - 10) < 1e-9);
        var tags = shared.Provenance();
        Assert.Equal(2, tags.Count);
        Assert.Contains("lid", tags);
        Assert.Contains("wall", tags);

        Assert.Contains(shared, box.EdgesTagged("lid"));
        Assert.Contains(shared, box.EdgesTagged("wall"));
    }
}
