using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Variable-radius fillets through the tessellator, measured against a closed form rather
/// than a fitted number.
/// </summary>
public class VariableFilletTessellationTests
{
    private const double Width = 60, Depth = 40, Corner = 8, Height = 10;
    private const double StraightHalf = Width / 2 - Corner;   // 22
    private const double SmallHalf = Depth / 2 - Corner;      // 12
    private const double Small = 2, Large = 3.5;

    /// <summary>2 → 3.5 along the long sides, constant over each corner arc.</summary>
    private static double TaperedLaw(Vector3d p) =>
        Small + (Large - Small) * Math.Clamp((p.X + StraightHalf) / (2 * StraightHalf), 0, 1);

    private static BrepSolid RoundedPlate()
    {
        double halfWidth = Width / 2, halfDepth = Depth / 2;
        double x = StraightHalf, y = SmallHalf;
        Vector3d P(double a, double b) => new(a, b, 0);
        Curve3d Arc(double cx, double cy, double from) =>
            new CurveSegment(
                new Circle3d(P(cx, cy), Vector3d.UnitX, Vector3d.UnitY, Corner), from, from + Math.PI / 2);

        return SolidFactory.Extrude(new Profile(
        [
            new Line3d(P(-x, -halfDepth), P(x, -halfDepth)),
            Arc(x, -y, -Math.PI / 2),
            new Line3d(P(halfWidth, -y), P(halfWidth, y)),
            Arc(x, y, 0),
            new Line3d(P(x, halfDepth), P(-x, halfDepth)),
            Arc(-x, y, Math.PI / 2),
            new Line3d(P(-halfWidth, y), P(-halfWidth, -y)),
            Arc(-x, -y, Math.PI),
        ]), Vector3d.UnitZ * Height);
    }

    /// <summary>
    /// The material a fillet of radius r removes per unit length of rim: the square corner
    /// minus the quarter disc.
    /// </summary>
    private static double SectionArea(double r) => r * r * (1 - Math.PI / 4);

    /// <summary>
    /// How far that removed section's centroid sits INWARD of the rim. Both coordinates are
    /// equal by symmetry about the corner's diagonal, and the quarter disc's own centroid is
    /// 4r/3π from its centre — so the first moment is r³(1/2 + 1/3 − π/4).
    /// </summary>
    private static double SectionCentroidInset(double r) =>
        r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);

    /// <summary>Removed along a straight run whose radius runs linearly r0 → r1.</summary>
    private static double AlongStraight(double length, double r0, double r1) =>
        (1 - Math.PI / 4) * length * (r0 * r0 + r0 * r1 + r1 * r1) / 3;

    /// <summary>
    /// Removed around a convex corner arc, by Pappus: the section area times the distance its
    /// centroid travels, which is the sweep times a radius reduced by the centroid's inset.
    /// </summary>
    private static double AroundArc(double arcRadius, double sweep, double r) =>
        SectionArea(r) * sweep * (arcRadius - SectionCentroidInset(r));

    [Fact]
    public void VariableFilletOnARoundedPlate_ConvergesOnItsClosedForm()
    {
        double plateArea = Width * Depth - (4 - Math.PI) * Corner * Corner;
        double exact = plateArea * Height
            // The two long sides, each running the full 2 → 3.5 taper.
            - 2 * AlongStraight(2 * StraightHalf, Small, Large)
            // The two short sides, each at the constant radius its end of the plate carries.
            - AlongStraight(2 * SmallHalf, Large, Large)
            - AlongStraight(2 * SmallHalf, Small, Small)
            // Four quarter-turn corner arcs, two at each radius.
            - 2 * AroundArc(Corner, Math.PI / 2, Large)
            - 2 * AroundArc(Corner, Math.PI / 2, Small);

        double previous = 0;
        foreach (int segments in (ReadOnlySpan<int>)[32, 64, 128])
        {
            var plate = RoundedPlate();
            var top = plate.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
            var filleted = Filleting.FilletRim(plate, top, TaperedLaw);
            var mesh = BRepTessellator.Tessellate(filleted, segments, segments / 4);
            mesh.Validate();
            Assert.True(mesh.IsClosed, "a variable-radius fillet must still close");
            Assert.Equal(2, mesh.EulerCharacteristic);

            // Inscribed everywhere, so the tessellation sits UNDER the analytic volume.
            double deficit = exact - mesh.Volume();
            Assert.True(deficit > 0,
                $"an inscribed tessellation cannot exceed the analytic volume (deficit {deficit:E3})");
            if (previous > 0)
                Assert.True(previous / deficit > 3.5,
                    $"quadratic convergence expected; ratio was {previous / deficit:0.00}");
            previous = deficit;
        }
    }

    [Fact]
    public void AConstantLawTessellatesToTheSameMeshAsThePlainRadius()
    {
        // The generalization must not move the case it generalizes — measured on the MESH,
        // which is what a user sees, not only on the topology.
        var a = RoundedPlate();
        var uniform = BRepTessellator.Tessellate(
            Filleting.FilletRim(a, a.PlanarFacesWithNormal(Vector3d.UnitZ).Single(), 3), 64, 16);
        var b = RoundedPlate();
        var law = BRepTessellator.Tessellate(
            Filleting.FilletRim(b, b.PlanarFacesWithNormal(Vector3d.UnitZ).Single(), _ => 3.0), 64, 16);

        Assert.Equal(uniform.VertexCount, law.VertexCount);
        Assert.Equal(uniform.FaceCount, law.FaceCount);
        Assert.Equal(uniform.Volume(), law.Volume(), 15);
    }
}
