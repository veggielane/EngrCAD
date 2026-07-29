using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Document-model mass properties: the right numbers, from the caches the part already
/// holds, posed by the part's or the occurrence's transform.
/// </summary>
public class PartMassPropertiesTests
{
    private static void AssertClose(double expected, double actual, double relative, string what)
    {
        double scale = Math.Max(Math.Abs(expected), 1e-300);
        Assert.True(Math.Abs(actual - expected) <= relative * scale,
            $"{what}: expected {expected:G17}, got {actual:G17} (relative error {Math.Abs(actual - expected) / scale:G3}).");
    }

    [Fact]
    public void ShapePart_MeasuresTheExactSolid()
    {
        const double density = 7.85e-9;   // steel in tonne/mm3, the ModelUnits convention
        var part = new Part("block", Shape.Box(20, 30, 10));
        var mp = part.MassProperties(density);

        AssertClose(6000, mp.Volume, 1e-12, "volume");
        AssertClose(density * 6000, mp.Mass, 1e-12, "mass");
        AssertClose(2 * (20 * 30 + 30 * 10 + 10 * 20), mp.SurfaceArea, 1e-12, "area");
        Assert.True(mp.Centroid.DistanceTo(Vector3d.Zero) < 1e-12, "centroid");

        double mass = density * 6000;
        AssertClose(mass * (30 * 30 + 10 * 10) / 12, mp.Inertia.Xx, 1e-11, "Ixx");
    }

    [Fact]
    public void PartTransform_IsApplied()
    {
        var placed = new Part("block", Shape.Box(2, 4, 6), transform: Matrix4d.CreateTranslation((10, 0, 0)));
        var mp = placed.MassProperties();

        AssertClose(48, mp.Volume, 1e-12, "volume");
        Assert.True(mp.Centroid.DistanceTo(new Vector3d(10, 0, 0)) < 1e-11, $"centroid was {mp.Centroid}");
    }

    [Fact]
    public void MeasuringDoesNotLowerTheGeometryASecondTime()
    {
        var part = new Part("block", Shape.Box(2, 2, 2));
        var first = part.TryGetSolid();
        part.MassProperties();
        // TryGetSolid caches by design; measuring must ride that cache, not bypass it.
        Assert.Same(first, part.TryGetSolid());
    }

    [Fact]
    public void SdfPart_FallsBackToTheDisplayMesh()
    {
        var part = new Part("ball", Sdf.Sphere(1));
        var mp = part.MassProperties();

        // Surface Nets on the default resolution: a coarse but honest polyhedron, so this
        // is a sanity band, not a closed-form claim.
        AssertClose(4.0 / 3.0 * Math.PI, mp.Volume, 0.05, "polygonized sphere volume");
        Assert.True(mp.Centroid.DistanceTo(Vector3d.Zero) < 1e-2, "centroid");
        Assert.Same(part.GetMesh(), part.GetMesh());
    }

    [Fact]
    public void MeshPart_MeasuresExactly()
    {
        var part = new Part("box", MeshPrimitives.Box(3, 5, 7));
        AssertClose(105, part.MassProperties().Volume, 1e-14, "mesh part volume");
    }

    [Fact]
    public void Assembly_AddsUpItsOccurrences()
    {
        var block = new Part("block", Shape.Box(2, 2, 2));
        var assembly = new Assembly("stack");
        assembly.Add(block, Frame3d.WorldXY);
        assembly.Add(block, Frame3d.FromXY((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY));

        var instances = assembly.Flatten();
        Assert.Equal(2, instances.Count);

        var total = instances.MassProperties();
        AssertClose(16, total.Volume, 1e-12, "two blocks");
        Assert.True(total.Centroid.DistanceTo(new Vector3d(0, 0, 5)) < 1e-10, $"centroid was {total.Centroid}");

        // One occurrence on its own is posed by its own world matrix.
        var upper = instances[1].MassProperties();
        Assert.True(upper.Centroid.DistanceTo(new Vector3d(0, 0, 10)) < 1e-10, $"centroid was {upper.Centroid}");
    }

    [Fact]
    public void Assembly_WithPerPartDensities_ReportsBulkDensity()
    {
        var steel = new Part("steel", Shape.Box(2, 2, 2));
        var foam = new Part("foam", Shape.Box(2, 2, 2), transform: Matrix4d.CreateTranslation((10, 0, 0)));
        var assembly = new Assembly("mixed");
        assembly.Add(steel, Frame3d.WorldXY);
        assembly.Add(foam, Frame3d.WorldXY);

        var total = assembly.Flatten().MassProperties(p => p.Name == "steel" ? 7.85e-9 : 1e-10);

        AssertClose(16, total.Volume, 1e-12, "volume");
        AssertClose(8 * 7.85e-9 + 8 * 1e-10, total.Mass, 1e-12, "mass");
        AssertClose(total.Mass / total.Volume, total.Density, 1e-14, "bulk density");
        // The centre of mass sits next to the steel block, not halfway.
        Assert.True(total.Centroid.X < 0.2, $"centre of mass at {total.Centroid} should hug the steel block.");
    }
}
