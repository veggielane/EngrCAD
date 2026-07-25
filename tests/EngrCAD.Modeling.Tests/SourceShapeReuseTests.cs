using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Shape.From(BrepSolid)"/> wraps a solid the CALLER owns, and B-Rep booleans
/// CONSUME their inputs — they mutate topology in place: <c>TopologyEditor.SplitEdge</c>
/// patches every loop using a split edge, and <c>SealSeams</c> re-parents coedges and
/// unifies vertices. Handing the raw wrapped solid over therefore poisons it for every
/// later lowering, which happens on a second target representation, a re-render after a
/// cached lowering is dropped, or simply two designs derived from one imported body. It
/// was a hazard sequentially long before parallel meshing existed.
/// </summary>
/// <remarks>
/// The damage is CONDITIONAL, which is why it hid for so long: a tool whose intersection
/// curves are interior to the body's faces (a plain through bore) only adds hole loops to
/// freshly built cap faces and leaves the body's own edges alone, so that case survives
/// two lowerings even unfixed. The tools below deliberately cross the body's edges — a
/// notch and a corner bite — which is where <c>SplitEdge</c> rewrites the caller's
/// topology and the second lowering throws.
/// </remarks>
public class SourceShapeReuseTests
{
    private static BrepSolid Plate() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 20, 4)));

    /// <summary>A notch across the plate: its intersection curves cross the plate's own edges.</summary>
    private static Shape Notch() => Shape.Box(new Aabb((-1, 8, 1), (21, 12, 5)));

    /// <summary>A bite out of one corner: likewise splits the plate's vertical edges.</summary>
    private static Shape CornerBite() => Shape.Box(new Aabb((15, 15, 1), (25, 25, 5)));

    /// <summary>A plain through bore, whose curves stay interior to the cap faces.</summary>
    private static Shape Bore() =>
        Shape.Cylinder(3, 12).Transform(Matrix4d.CreateTranslation((10, 10, 2)));

    [Fact]
    public void WrappedSolid_SurvivesBeingLoweredTwiceAsABooleanOperand()
    {
        var design = Shape.From(Plate()) - Notch();

        var first = design.ToBrep();
        first.Validate();

        // The SAME shape graph lowered again: a second render, a second export, a second
        // representation. Nothing about the first lowering may have damaged the source.
        var second = design.ToBrep();
        second.Validate();

        Assert.Equal(first.Faces.Count(), second.Faces.Count());
        Assert.Equal(first.Edges.Count(), second.Edges.Count());
        Assert.Equal(first.Vertices.Count(), second.Vertices.Count());
        Assert.Equal(BRepTessellator.Tessellate(first).Volume(),
                     BRepTessellator.Tessellate(second).Volume(), 9);
    }

    [Fact]
    public void WrappedSolid_CanFeedTwoDifferentBooleans()
    {
        // One wrapped solid, two independent designs derived from it. Consuming the
        // source in the first would leave the second with a mangled body.
        var body = Shape.From(Plate());
        var notched = body - Notch();
        var bitten = body - CornerBite();

        // 20 x 20 x 4 minus a 20 x 4 x 3 through notch cut from the top.
        Assert.Equal(1600 - 20 * 4 * 3, notched.ToMesh().Volume(), 6);
        // 20 x 20 x 4 minus a 5 x 5 x 3 corner bite.
        Assert.Equal(1600 - 5 * 5 * 3, bitten.ToMesh().Volume(), 6);
    }

    [Fact]
    public void WrappedSolid_ReusedAcrossRepresentations()
    {
        // Lowering to a mesh already ran the B-Rep boolean once; the STEP/B-Rep route
        // must still work afterwards.
        var design = Shape.From(Plate()) - CornerBite();
        Assert.Equal(1600 - 5 * 5 * 3, design.ToMesh().Volume(), 6);

        var solid = design.ToBrep();
        solid.Validate();
        Assert.Equal(1600 - 5 * 5 * 3, BRepTessellator.Tessellate(solid).Volume(), 6);
    }

    [Fact]
    public void WrappedSolid_IsNotMutatedByLowering()
    {
        // The strongest statement of the contract: the caller's own object is untouched,
        // right down to the vertex objects its edges point at.
        var solid = Plate();
        int faces = solid.Faces.Count();
        var edgeVertices = solid.Edges.ToDictionary(e => e, e => (e.StartVertex, e.EndVertex));

        _ = (Shape.From(solid) - Notch()).ToBrep();

        solid.Validate();
        Assert.Equal(faces, solid.Faces.Count());
        Assert.Equal(edgeVertices.Count, solid.Edges.Count());
        foreach (var edge in solid.Edges)
        {
            Assert.Equal(2, edge.Uses.Count);
            var (start, end) = edgeVertices[edge];
            Assert.Same(start, edge.StartVertex);
            Assert.Same(end, edge.EndVertex);
        }
        foreach (var loop in solid.Loops)
        {
            foreach (var coedge in loop.Coedges)
                Assert.Same(loop, coedge.Loop);
        }
    }

    [Fact]
    public void InteriorOnlyTool_AlsoRoundTripsTwice()
    {
        // The case that hid the bug: a bore whose circles never touch the plate's edges.
        // Kept as a test so a future change cannot make the easy case regress either.
        var design = Shape.From(Plate()) - Bore();
        double first = design.ToMesh().Volume();
        double second = design.ToMesh().Volume();
        Assert.Equal(first, second, 9);
        Assert.InRange(first, 1600 - Math.PI * 9 * 4 - 2, 1600 - Math.PI * 9 * 4 + 2);
    }
}
