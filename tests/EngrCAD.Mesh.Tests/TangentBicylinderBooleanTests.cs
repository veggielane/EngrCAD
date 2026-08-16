using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The tangent bicylinder — equal-radius perpendicular cylinders through one axis
/// point, the degenerate Steinmetz configuration, where the exact surfaces GRAZE at two
/// points. Whether the exact mesh boolean survives it is a property of the
/// tessellations' ALIGNMENT, not of the configuration (the recorded
/// alignment-not-tolerance family): THIS fixture — one primitive cylinder and its own
/// exact quarter-turn copy — mis-joins nothing and lands on the Steinmetz volume, while
/// the Shape-route tessellation of the same geometry mis-joins its imprint seams at the
/// graze and returns a quarter of it (pinned in
/// <c>EngrCAD.Modeling.Tests.TangentBicylinderDefectTests</c>, since the defective
/// alignment comes from the B-Rep tessellator this project cannot reference).
/// </summary>
public class TangentBicylinderBooleanTests
{
    [Fact]
    public void TheFavourableAlignment_LandsOnTheSteinmetzVolume_WithConsistentIdentities()
    {
        var a = MeshPrimitives.Cylinder(2, 8).Transformed(Matrix4d.CreateTranslation((0, 0, -4)));
        var b = a.Transformed(Matrix4d.CreateRotationY(Math.PI / 2));
        double va = a.Volume(), vb = b.Volume();
        var intersection = MeshBoolean.Intersection(a, b);
        var union = MeshBoolean.Union(a, b);
        var difference = MeshBoolean.Difference(a, b);

        Assert.True(intersection.IsClosed);
        Assert.True(union.IsClosed);
        Assert.True(difference.IsClosed);
        double vi = intersection.Volume(), vu = union.Volume(), vd = difference.Volume();
        // Inclusion–exclusion and the A = (A∩B) ∪ (A−B) partition, to round-off.
        Assert.InRange(Math.Abs(va + vb - (vu + vi)) / (va + vb), 0, 1e-12);
        Assert.InRange(Math.Abs(vi + vd - va) / va, 0, 1e-12);

        // The degenerate Steinmetz solid is 16r³/3 = 42.67; the tessellated answer
        // sits within its own chord grade under it.
        Assert.InRange(vi, 16.0 * 8 / 3 * 0.97, 16.0 * 8 / 3);
    }
}
