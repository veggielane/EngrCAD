using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <c>Shape.Shell(thickness, openings)</c> — the exact B-Rep shelling
/// (<see cref="Shelling.Shell"/>) beside the SDF-only <c>Shape.Shell(thickness)</c>.
/// Kernel behaviour is locked by <c>EngrCAD.BRep.Tests.ShellingTests</c>; here the
/// wiring is pinned, and — the design point — the DIFFERENCE between the two shells is
/// asserted explicitly rather than left representation-dependent: the B-Rep shell
/// hollows inward keeping the outer surface, the SDF shell is a symmetric skin.
/// </summary>
public class BrepShellShapeTests
{
    private const double X = 20, Y = 12, H = 8, T = 1.5;

    private static Shape Tray() =>
        Shape.Box(X, Y, H).Shell(T, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));

    [Fact]
    public void Shell_OpenTray_HasTheExactWallVolume()
    {
        // Cavity: inset by T on every wall, open through the top (the top plane does
        // not move), so it is (X−2T)(Y−2T)(H−T).
        double exact = X * Y * H - (X - 2 * T) * (Y - 2 * T) * (H - T);

        var brep = Tray().ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);
        Assert.Equal(exact, Tray().ToMesh().Volume(), 9);
    }

    [Fact]
    public void Shell_Sealed_IsATwoShellSolidWithTheExactVolume()
    {
        double exact = X * Y * H - (X - 2 * T) * (Y - 2 * T) * (H - 2 * T);
        var sealedShell = Shape.Box(X, Y, H).Shell(T, openings: null);

        var brep = sealedShell.ToBrep();
        brep.Validate();
        Assert.Equal(2, brep.Shells.Count);
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);
    }

    [Fact]
    public void Shell_KeepsTheOuterSurfaceExactly_UnlikeTheSdfSkin()
    {
        // The design decision under test: the B-Rep shell thickens INWARD (outer
        // bounds unchanged), while Shell(t) is the SDF onion |d| − t/2, whose skin
        // straddles the surface and reaches t/2 OUTSIDE it. Two calls, two geometries —
        // never one call with representation-dependent walls.
        var brepBounds = Tray().ToMesh().ComputeBounds();
        Assert.Equal(X / 2, brepBounds.Max.X, 9);
        Assert.Equal(H / 2, brepBounds.Max.Z, 9);

        var sdf = Shape.Box(X, Y, H).Shell(T).ToImplicit();
        Assert.True(sdf.Evaluate(new Vector3d(X / 2 + T / 4, 0, 0)) < 0,
            "the SDF skin reaches outside the original surface");
        Assert.True(sdf.Evaluate(new Vector3d(0, 0, 0)) > 0, "the SDF skin is hollow at the centre");
    }

    [Fact]
    public void Shell_ExplainIsHonest_AndTheSdfShellStillExplainsItsOwnStory()
    {
        var exact = Tray();
        var report = exact.Explain(TargetRep.Brep);
        Assert.True(report.IsConvertible);
        Assert.Contains(report.Entries,
            e => e.Node.StartsWith("Shell(", StringComparison.Ordinal) && e.Support == NodeSupport.Native);
        Assert.Equal(NodeSupport.Bridged, exact.Explain(TargetRep.Implicit).Entries[^1].Support);

        // The incumbent SDF shell is UNCHANGED: implicit-Native, B-Rep-Impossible with
        // a message that names this overload as the exact route.
        var sdfShell = Shape.Box(X, Y, H).Shell(T);
        Assert.Equal(NodeSupport.Native, sdfShell.Explain(TargetRep.Implicit).Entries[^1].Support);
        var brepReport = sdfShell.Explain(TargetRep.Brep);
        Assert.False(brepReport.IsConvertible);
        Assert.Contains("Shell(thickness, openings)",
            brepReport.Entries.Single(e => e.Support == NodeSupport.Impossible).Detail);
    }

    [Fact]
    public void Shell_ThicknessScalesWithUniformScale()
    {
        // Shell(T) then Scale(2) must equal Box(2X).Shell(2T): the wall is a length.
        double exact = 8 * (X * Y * H - (X - 2 * T) * (Y - 2 * T) * (H - T));
        var scaled = Tray().Scale(2);
        Assert.Equal(exact, BRepTessellator.Tessellate(scaled.ToBrep()).Volume(), 8);
    }

    [Fact]
    public void Shell_CurvedSolid_IsRefusedByName()
    {
        var shelled = Shape.Cylinder(5, H).Shell(T, openings: null);
        var ex = Assert.Throws<NotSupportedException>(() => shelled.ToBrep());
        Assert.Contains("planar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shell_OpeningSelectorMatchingNothing_Throws()
    {
        var shelled = Shape.Box(X, Y, H).Shell(
            T, s => s.PlanarFacesWithNormal(new Vector3d(1, 1, 1).Normalized()));
        var ex = Assert.Throws<InvalidOperationException>(() => shelled.ToBrep());
        Assert.Contains("matched nothing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_InvalidThickness_FailsAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(X, Y, H).Shell(0, openings: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(X, Y, H).Shell(-1, openings: null));
    }

    [Fact]
    public void Shell_AppearsInTheConstructionTreeWithItsChild()
    {
        var tree = ConstructionTree.FromShape(Tray());
        Assert.StartsWith("Shell(", tree.Label, StringComparison.Ordinal);
        Assert.Contains("openings", tree.Label, StringComparison.Ordinal);
        var child = Assert.Single(tree.Children);
        Assert.StartsWith("Box(", child.Label, StringComparison.Ordinal);
    }
}
