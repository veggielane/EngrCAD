using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <c>Shape.Draft</c> — the Shape-graph wiring of <see cref="Draft.Apply"/>. Kernel
/// behaviour (exact plane rotation, composability, rejections) is locked by
/// <c>EngrCAD.BRep.Tests.DraftTests</c>; these tests pin the wiring: selector
/// resolution on the lowered solid, transform baking, and honest Explain reports.
/// All-planar results make every volume assertion here EXACT.
/// </summary>
public class DraftShapeTests
{
    private const double X = 10, Y = 8, H = 6;
    private static readonly Vector3d Neutral = new(0, 0, -H / 2);

    /// <summary>∫₀ᴴ (X − 2z·tanθ)(Y − 2z·tanθ) dz — the frustum a full draft leaves.</summary>
    private static double FullDraftVolume(double angle)
    {
        double t = Math.Tan(angle * Math.PI / 180);
        return X * Y * H - (X + Y) * t * H * H + 4.0 / 3 * t * t * H * H * H;
    }

    [Fact]
    public void Draft_AllSideFaces_HasTheExactFrustumVolume()
    {
        const double angle = 10;
        var drafted = Shape.Box(X, Y, H).Draft(angle, Neutral, Vector3d.UnitZ);

        var brep = drafted.ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        Assert.Equal(FullDraftVolume(angle), mesh.Volume(), 9);
        Assert.Equal(FullDraftVolume(angle), drafted.ToMesh().Volume(), 9);
    }

    [Fact]
    public void Draft_SelectedFacesOnly_LeavesTheOthersInPlace()
    {
        const double angle = 10;
        double t = Math.Tan(angle * Math.PI / 180);
        var drafted = Shape.Box(X, Y, H).Draft(
            angle, Neutral, Vector3d.UnitZ, s => s.PlanarFacesWithNormal(Vector3d.UnitX));

        // Only the +X wall tilts: volume = ∫ (X − z·tanθ)·Y dz.
        double exact = X * Y * H - t * Y * H * H / 2;
        var mesh = BRepTessellator.Tessellate(drafted.ToBrep());
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);
    }

    [Fact]
    public void Draft_Chained_GivesPerFaceAnglesExactly()
    {
        // Per-face angles in one MODEL: chain drafts — the operation is exact and
        // composable, so each call tapers its own faces about the shared neutral plane.
        const double angleX = 3, angleY = 8;
        double tx = Math.Tan(angleX * Math.PI / 180), ty = Math.Tan(angleY * Math.PI / 180);
        var drafted = Shape.Box(X, Y, H)
            .Draft(angleX, Neutral, Vector3d.UnitZ, s => s.PlanarFacesWithNormal(Vector3d.UnitX)
                .Concat(s.PlanarFacesWithNormal(-Vector3d.UnitX)))
            .Draft(angleY, Neutral, Vector3d.UnitZ, s => s.PlanarFacesWithNormal(Vector3d.UnitY)
                .Concat(s.PlanarFacesWithNormal(-Vector3d.UnitY)));

        // ∫ (X − 2z·tx)(Y − 2z·ty) dz, expanded.
        double exact = X * Y * H - (X * ty + Y * tx) * H * H + 4.0 / 3 * tx * ty * H * H * H;
        var mesh = BRepTessellator.Tessellate(drafted.ToBrep());
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);
    }

    [Fact]
    public void Draft_TransformsBakeIntoTheNeutralPlaneAndPull()
    {
        const double angle = 10;
        var drafted = Shape.Box(X, Y, H).Draft(angle, Neutral, Vector3d.UnitZ)
            .RotateX(0.7).Translate(5, -3, 2);

        var mesh = BRepTessellator.Tessellate(drafted.ToBrep());
        Assert.True(mesh.IsClosed);
        Assert.Equal(FullDraftVolume(angle), mesh.Volume(), 9);

        // Uniform scale: lengths ×2, volume ×8, ANGLE unchanged.
        var scaled = Shape.Box(X, Y, H).Draft(angle, Neutral, Vector3d.UnitZ).Scale(2);
        Assert.Equal(8 * FullDraftVolume(angle), BRepTessellator.Tessellate(scaled.ToBrep()).Volume(), 8);
    }

    [Fact]
    public void Draft_ExplainIsHonest()
    {
        var drafted = Shape.Box(X, Y, H).Draft(5, Neutral, Vector3d.UnitZ);

        var brep = drafted.Explain(TargetRep.Brep);
        Assert.True(brep.IsConvertible);
        Assert.Contains(brep.Entries,
            e => e.Node.StartsWith("Draft(", StringComparison.Ordinal) && e.Support == NodeSupport.Native);

        var implicitReport = drafted.Explain(TargetRep.Implicit);
        Assert.True(implicitReport.IsConvertible);
        Assert.Equal(NodeSupport.Bridged, Assert.Single(implicitReport.Entries).Support);

        // Sheared: refused for B-Rep with the angle named, still meshable.
        var sheared = drafted.Transform(Matrix4d.CreateScale(new Vector3d(2, 1, 1)));
        Assert.False(sheared.CanConvertTo(TargetRep.Brep));
        Assert.True(sheared.ToMesh().IsClosed);
    }

    [Fact]
    public void Draft_CurvedSolid_IsRefusedByName()
    {
        var drafted = Shape.Cylinder(4, H).Draft(5, Neutral, Vector3d.UnitZ);
        var ex = Assert.Throws<NotSupportedException>(() => drafted.ToBrep());
        Assert.Contains("planar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Draft_SelectorMatchingNothing_Throws()
    {
        var drafted = Shape.Box(X, Y, H).Draft(
            5, Neutral, Vector3d.UnitZ, s => s.PlanarFacesWithNormal(new Vector3d(1, 1, 1).Normalized()));
        var ex = Assert.Throws<InvalidOperationException>(() => drafted.ToBrep());
        Assert.Contains("matched nothing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Draft_InvalidArguments_FailAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Shape.Box(X, Y, H).Draft(90, Neutral, Vector3d.UnitZ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Shape.Box(X, Y, H).Draft(double.NaN, Neutral, Vector3d.UnitZ));
        Assert.Throws<ArgumentException>(() =>
            Shape.Box(X, Y, H).Draft(5, Neutral, Vector3d.Zero));
    }

    [Fact]
    public void Draft_AppearsInTheConstructionTreeWithItsChild()
    {
        var tree = ConstructionTree.FromShape(Shape.Box(X, Y, H).Draft(5, Neutral, Vector3d.UnitZ));
        Assert.StartsWith("Draft(5", tree.Label, StringComparison.Ordinal);
        var child = Assert.Single(tree.Children);
        Assert.StartsWith("Box(", child.Label, StringComparison.Ordinal);
    }
}
