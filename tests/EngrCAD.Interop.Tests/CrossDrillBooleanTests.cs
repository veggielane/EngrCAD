using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Booleans where two perpendicular cylinders pierce each other: a box with a Z-bore
/// cross-drilled by an X-cylinder through the bore. The cylinder∩cylinder intersection
/// curves are closed NON-PLANAR tracer polylines that wrap the tool band — the case
/// that used to leave rim edges single-use ("Edge is used by 1 coedges") because
/// uniform-parameter sampling of the tracer polylines put every pullback sample a
/// chord-sagitta off the surface and both bands silently failed to split. These tests
/// also carry the cross-drilled-bore-reaches-tessellation regression: band faces with
/// wavy hole loops and sub-bands bounded by wavy wrap curves must tessellate closed.
/// </summary>
public class CrossDrillBooleanTests
{
    // Inputs are consumed by the boolean (faces split in place), so build fresh ones.
    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));

    /// <summary>Bore along Z, radius 0.4, overshooting the box on both sides.</summary>
    private static BrepSolid BoreTool() => SolidFactory.Extrude(
        Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, 0.4), (0, 0, 3));

    /// <summary>Cross tool along X, radius 0.25, through the bore at mid-height.</summary>
    private static BrepSolid CrossTool() => SolidFactory.Extrude(
        Profile.Circle((-2, 0, 0.5), Vector3d.UnitY, Vector3d.UnitZ, 0.25), (4, 0, 0));

    /// <summary>Inscribed n-gon area — what a tessellated circle of radius r encloses.</summary>
    private static double NgonArea(int n, double r) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    /// <summary>
    /// Bicylinder (generalized Steinmetz) volume: two perpendicular cylinders with
    /// intersecting axes, radii a ≤ b. With y along the common perpendicular of the two
    /// axes, the cross-section at fixed y is a rectangle 2√(a²−y²) × 2√(b²−y²), so
    ///   V = ∫₋ₐᵃ 4·√(a²−y²)·√(b²−y²) dy = 4a²·∫ cos²t·√(b²−a²·sin²t) dt  (y = a·sin t)
    /// For a = b this is the classic Steinmetz 16a³/3; for a &lt; b it is elliptic, so we
    /// evaluate the smooth substituted integrand by composite Simpson.
    /// </summary>
    private static double BicylinderVolume(double a, double b)
    {
        const int n = 2000;
        double h = Math.PI / n;
        double sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = -Math.PI / 2 + i * h;
            double f = Math.Cos(t) * Math.Cos(t) * Math.Sqrt(b * b - a * a * Math.Sin(t) * Math.Sin(t));
            sum += (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2) * f;
        }
        return 4 * a * a * sum * h / 3;
    }

    /// <summary>
    /// Discretization-derived volume tolerance: the mesh inscribes every curved surface,
    /// and each vertex lies exactly on it (grid samples, polyline vertices, refined
    /// crossings), so the volume deficit is bounded by total area × the largest chordal
    /// sagitta. The n-gon circle sagitta r·(1 − cos(π/n)) dominates the tracer's step
    /// sagitta (step²/8r ≈ 4e-4 here).
    /// </summary>
    private static double VolumeTolerance(EngrCAD.Mesh.HalfEdgeMesh mesh, int n, double maxRadius) =>
        mesh.SurfaceArea() * maxRadius * (1 - Math.Cos(Math.PI / n));

    private static EngrCAD.Mesh.HalfEdgeMesh AssertSealedAndMeshed(BrepSolid solid, int genus, int segments)
    {
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus), $"expected Euler–Poincaré to hold for genus {genus}");
        var mesh = BRepTessellator.Tessellate(solid, segments);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "boolean result must tessellate closed");
        Assert.Equal(2 - 2 * genus, mesh.EulerCharacteristic);
        return mesh;
    }

    [Fact]
    public void Difference_CrossDrillThroughBore_ValidGenus3SolidWithBicylinderVolume()
    {
        // Genus: the removed void is two perpendicular through-tunnels meeting in a
        // (contractible) bicylinder lens — topologically a single X-shaped tunnel with
        // 4 openings on the boundary. Drilling a tree-shaped tunnel with k openings out
        // of a ball yields a genus k−1 handlebody (one independent loop per opening
        // beyond the first): k = 4 ⇒ genus 3. (Two DISJOINT through-holes would be two
        // k = 2 trees ⇒ genus 1 + 1 = 2; the shared junction adds the third handle.)
        const int n = 32;
        var drilled = BrepBoolean.Difference(Box(), BoreTool());
        var result = BrepBoolean.Difference(drilled, CrossTool());
        var mesh = AssertSealedAndMeshed(result, genus: 3, segments: n);

        // Volume: box − boreZ − cylX + lens. The prism parts tessellate as exact
        // inscribed n-gons; only the lens seam region is smooth-vs-chordal, covered by
        // the derived tolerance (measured error ≈ 2.6e-3 against a bound of 3.6e-2).
        double expected = 4.0
            - NgonArea(n, 0.4) * 1.0        // Z-bore through the unit-height box
            - NgonArea(n, 0.25) * 2.0       // X-tool across the box width
            + BicylinderVolume(0.25, 0.4);  // their overlap, subtracted twice
        Assert.InRange(mesh.Volume(), expected - VolumeTolerance(mesh, n, 0.4), expected + VolumeTolerance(mesh, n, 0.4));

        // 4 box sides (2 pierced) + 2 annular caps + bore wall with 2 wavy holes +
        // 2 kept tool sub-bands; the bore wall and the tool bands are reversed.
        Assert.Equal(9, result.Faces.Count());
        Assert.Equal(3, result.Faces.Count(f => f.IsReversed));
    }

    [Fact]
    public void Union_CrossToolThroughBore_ValidGenus2Solid()
    {
        // Genus: the remaining void is the bore with the tool rod crossing its middle —
        // a tube (2 boundary openings) whose core has one independent loop (around the
        // rod). Complement genus = g_void + openings − 1 = 1 + 2 − 1 = 2.
        const int n = 32;
        var drilled = BrepBoolean.Difference(Box(), BoreTool());
        var result = BrepBoolean.Union(drilled, CrossTool());
        var mesh = AssertSealedAndMeshed(result, genus: 2, segments: n);

        // vol(A ∪ C) = vol(A) + vol(C) − vol(A ∩ C) with A = box − bore and C the tool
        // prism of length 4: A ∩ C = (tool ∩ box) − lens = toolArea·2 − lens, so
        // vol = 4 − boreArea + toolArea·2 + lens.
        double expected = 4.0
            - NgonArea(n, 0.4) * 1.0
            + NgonArea(n, 0.25) * 2.0
            + BicylinderVolume(0.25, 0.4);
        Assert.InRange(mesh.Volume(), expected - VolumeTolerance(mesh, n, 0.4), expected + VolumeTolerance(mesh, n, 0.4));

        // The tool's middle sub-band (the rod wall crossing the bore void) must survive:
        // 4 sides + 2 annular caps + bore wall + 3 tool sub-bands + 2 tool end caps.
        Assert.Equal(12, result.Faces.Count());
        Assert.Equal(1, result.Faces.Count(f => f.IsReversed)); // the bore wall from A
    }

    [Fact]
    public void Intersection_PerpendicularCylinders_IsBicylinderLens()
    {
        const int n = 32;
        var cylZ = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, 0.4), (0, 0, 2));
        var cylX = SolidFactory.Extrude(
            Profile.Circle((-1, 0, 0), Vector3d.UnitY, Vector3d.UnitZ, 0.25), (2, 0, 0));
        var result = BrepBoolean.Intersection(cylZ, cylX);
        var mesh = AssertSealedAndMeshed(result, genus: 0, segments: n);

        // The lens: the tool band's middle sub-band (between the two wavy pierce
        // curves) capped by the two bore-wall patches — three faces, no caps involved.
        Assert.Equal(3, result.Faces.Count());
        double expected = BicylinderVolume(0.25, 0.4);
        Assert.InRange(mesh.Volume(), expected - VolumeTolerance(mesh, n, 0.4), expected + VolumeTolerance(mesh, n, 0.4));
    }
}
