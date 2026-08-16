using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The exact mesh boolean's measured defect on the tangent bicylinder, PINNED at its
/// measured value so a fix announces itself here rather than passing silently
/// (2026-08-16, found by the interference-volume work; todo.md carries the entry).
/// <para><b>The mechanism, corrected twice by measurement.</b> First guess: "whole
/// lobes dropped by per-patch winding classification" — WRONG, the classification is
/// perfectly consistent (inclusion–exclusion and the partition identity hold to
/// round-off, every result closed). Second finding: the defect is
/// ALIGNMENT-dependent — the SAME geometry through <c>MeshPrimitives.Cylinder</c> and
/// an exact quarter-turn lands on the Steinmetz volume (the healthy twin in
/// <c>EngrCAD.Mesh.Tests.TangentBicylinderBooleanTests</c>), while THIS tessellation
/// (the B-Rep route's) mis-joins its imprint seams where the surfaces GRAZE and
/// returns a self-consistent boolean of a WRONG partition: 10.56 against the analytic
/// 42.67, with every identity a consumer could check still holding.</para>
/// </summary>
public class TangentBicylinderDefectTests
{
    [Fact]
    public void TheBrepRouteTessellation_MisJoinsItsImprintAtTheGraze()
    {
        var housing = new Part("h", Shape.Cylinder(2, 8).Translate(0, 0, -4));
        var shaft = new Part("s", Shape.Cylinder(2, 8).Translate(0, 0, -4).RotateY(Math.PI / 2));
        var a = housing.GetMesh();
        var b = shaft.GetMesh();
        double va = a.Volume(), vb = b.Volume();
        var intersection = MeshBoolean.Intersection(a, b);
        var union = MeshBoolean.Union(a, b);
        var difference = MeshBoolean.Difference(a, b);

        // The HEALTHY half, asserted so a fix cannot trade it away: closed results and
        // consistent identities — which is exactly what makes this defect invisible to
        // every downstream check.
        Assert.True(intersection.IsClosed);
        Assert.True(union.IsClosed);
        Assert.True(difference.IsClosed);
        double vi = intersection.Volume(), vu = union.Volume(), vd = difference.Volume();
        Assert.InRange(Math.Abs(va + vb - (vu + vi)) / (va + vb), 0, 1e-12);
        Assert.InRange(Math.Abs(vi + vd - va) / va, 0, 1e-12);

        // THE PIN: the analytic intersection is 16r³/3 = 42.67 and this alignment
        // delivers a quarter of it. When the graze is handled, replace this with
        // InRange(vi, 42.67 * 0.97, 42.67) and update todo.md's entry.
        Assert.InRange(vi, 10.0, 11.5);
    }
}
