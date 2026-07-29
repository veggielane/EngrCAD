using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Carrier clipping: an intersection curve is trimmed to the stretches the two faces
/// genuinely SHARE before either of them is split by it.
///
/// <para><see cref="SurfaceIntersection"/> intersects CARRIERS, and a carrier is unbounded (a
/// plane) or bounded only by its own parameter rectangle, so the curve it returns runs past
/// both faces. Each face's splitter already discarded the stretches outside ITSELF; nothing
/// discarded the stretches outside the OTHER face, so a face was split along geometry the pair
/// does not share — a pocket tool's four wall lines cut a host face into NINE fragments where
/// the tool's footprint asks for two.</para>
///
/// <para>What these tests hold is the pair of claims that together make the clip safe: the
/// decomposition gets STRICTLY SIMPLER while the solid stays the same solid, and the cases the
/// clip's own asymmetry and keep-bias exist for still close. The volumes are analytic, because
/// a face count alone would be satisfied by a boolean that lost material.</para>
/// </summary>
public class CarrierClipTests
{
    private static BrepSolid Lowered(Shape shape)
    {
        var solid = shape.ToBrep();
        solid.Validate();
        return solid;
    }

    private static double Volume(BrepSolid solid, int segments = 96)
    {
        var mesh = BRepTessellator.Tessellate(solid, segments, segments / 2);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "result must tessellate closed");
        return mesh.Volume();
    }

    /// <summary>
    /// The headline case. A pocket's four wall planes cross the host's top plane in four full
    /// LINES; unclipped they cut it into a 3x3 grid of fragments, where the tool's footprint
    /// asks for a face-with-a-hole plus the pocket floor. Measured 18 faces before the clip
    /// and 11 after, with the same exact volume.
    /// </summary>
    [Fact]
    public void APocketDoesNotCutItsHostFaceIntoAGrid()
    {
        var solid = Lowered(Shape.Box(4, 4, 2) - Shape.Box(2, 2, 1).Translate(0, 0, 1));

        // 6 box faces, of which the top gains a hole; plus 4 pocket walls and a floor.
        Assert.Equal(11, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // Shape.Box is CENTRED, so the plate is z in [-1, 1] and the tool z in [0.5, 1.5]:
        // a 2x2 pocket 0.5 deep. Planar throughout, so this is exact.
        Assert.Equal(4 * 4 * 2 - 2 * 2 * 0.5, Volume(solid), 9);
    }

    /// <summary>
    /// The same, through a boolean whose curves are a mix of analytic circles (which lie
    /// wholly inside both trims and must therefore survive UNCLIPPED, as the closed curves
    /// they are) and wall lines (which do not).
    /// </summary>
    [Fact]
    public void BoresAndAPocketInOnePlateKeepTheirCirclesAndClipTheirLines()
    {
        var solid = Lowered(
            Shape.Box(30, 20, 6)
            - Shape.Cylinder(2, 10).Translate(-8, 0, 0)
            - Shape.Cylinder(2, 10).Translate(8, 0, 0)
            - Shape.Box(6, 6, 3).Translate(0, 0, 2.5));

        // 6 box faces (the top carrying three holes, the bottom two) + 2 bore walls
        // + 4 pocket walls + 1 pocket floor.
        Assert.Equal(13, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 2), "two through bores");

        // Centred boxes again: the plate is z in [-3, 3] and the pocket tool z in [1, 4],
        // so the pocket is 6x6 by 2 deep and both bores go right through.
        double expected = 30 * 20 * 6 - 2 * Math.PI * 4 * 6 - 6 * 6 * 2;
        // Inscribed n-gon bores, so the tessellated volume sits just ABOVE the smooth value
        // by their chord deficit (0.11 at 96 segments/circle). A band of 1 leaves no room for
        // a lost fragment — the smallest face here is a 72 mm^3 pocket in a 3377 mm^3 plate.
        Assert.InRange(Volume(solid), expected, expected + 1);
    }

    /// <summary>
    /// The asymmetry, stated as the case that forced it. These two boxes' SIDE WALLS meet
    /// along their full height, so the symmetric clip — hand both faces the stretches inside
    /// both trims — cuts every vertical curve exactly ON each wall's own rim, turning a
    /// transversal crossing into a tangential touch, and the arrangement is left with an
    /// endpoint no boundary edge owns ("Arrangement tracing did not close"). Keeping the
    /// stretches that lie outside THIS face costs nothing and restores the crossing.
    /// </summary>
    [Fact]
    public void FacesSharingABoundaryStillCrossItTransversally()
    {
        var solid = Lowered(Shape.Box(20, 20, 10) & Shape.Box(10, 30, 10));
        Assert.Equal(10 * 20 * 10, Volume(solid), 9);
    }

    /// <summary>
    /// The keep-bias, stated as the case that forced it. A sphere piercing a box meets each
    /// face in a closed circle interior to it; the clip's containment test runs on the
    /// SPHERE's face, which is pole-bounded, and the one-sided upward-v ray calls every point
    /// on it outside. Dropped, the seam curve goes with it and the union comes back as two
    /// touching shells at Euler 4 instead of one solid at Euler 2.
    /// </summary>
    [Fact]
    public void APoleBoundedFaceDoesNotLoseItsSeamCurve()
    {
        var solid = Lowered(Shape.Box(20, 20, 20) | Shape.Sphere(8).Translate(0, 0, 10));

        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Single(solid.Shells);
        // Box plus the hemisphere cap standing proud of the top face. The dome is inscribed,
        // so the tessellated volume sits BELOW the smooth value — 1.06 short at 96
        // segments/circle, where losing the dome entirely would be 1072.
        double expected = 20 * 20 * 20 + 2.0 / 3 * Math.PI * 8 * 8 * 8;
        Assert.InRange(Volume(solid), expected - 3, expected);
    }

    /// <summary>
    /// A closed curve that survives the clip whole must come back as ITSELF, not as a segment
    /// covering its own domain — wrap-splitting and hole-splitting both key on
    /// <see cref="Curve3d.IsClosed"/>, so a full-domain segment would silently route a bore
    /// rim down the open-curve path. A plain through bore is the case: its rim circles lie
    /// wholly inside both the cap's trim and the tool wall's.
    /// </summary>
    [Fact]
    public void AWhollySharedClosedCurveStaysClosed()
    {
        var solid = Lowered(Shape.Box(20, 20, 6) - Shape.Cylinder(3, 20));

        Assert.True(solid.SatisfiesEulerFormula(genus: 1));
        // The bore's two rims are CLOSED edges: one vertex, used twice.
        var rims = solid.Edges.Where(e => e.IsClosedEdge).ToList();
        Assert.Equal(2, rims.Count);
        Assert.All(rims, rim => Assert.Equal(2, rim.Uses.Count));
    }
}
