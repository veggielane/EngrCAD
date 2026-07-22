using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class FilletTests
{
    private static BrepEdge TopRim(BrepSolid cylinder) =>
        cylinder.Edges.First(e => e.Uses.Any(u =>
            u.Loop.Face.Surface is PlaneSurface p && p.Normal.Dot(Vector3d.UnitZ) > 0.9));

    [Fact]
    public void FilletCylinderTopRim_ExactTopologyAndAnalyticVolume()
    {
        double bigRadius = 1.5, height = 2.0, radius = 0.4;
        var puck = SolidFactory.MakeCylinder(bigRadius, height);
        var filleted = Filleting.FilletEdge(puck, TopRim(puck), radius);

        filleted.Validate();
        Assert.True(filleted.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(4, filleted.Faces.Count()); // bottom cap, band, torus, top cap
        Assert.Equal(3, filleted.Edges.Count());
        Assert.Single(filleted.Faces, f => f.Surface is RevolvedSurface);

        var mesh = BRepTessellator.Tessellate(filleted, segmentsPerCircle: 64, curveSamples: 32);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // Removed rim material by Pappus: corner square minus quarter disc, revolved.
        double area = radius * radius * (1 - Math.PI / 4);
        double centroidRadial = (bigRadius - radius) + radius * (1.0 / 6.0) / (1 - Math.PI / 4);
        double removed = 2 * Math.PI * centroidRadial * area;
        double expected = Math.PI * bigRadius * bigRadius * height - removed;
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.01,
            $"filleted volume {mesh.Volume()} vs analytic {expected}");
    }

    [Fact]
    public void Fillet_Validations()
    {
        var puck = SolidFactory.MakeCylinder(1.0, 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Filleting.FilletEdge(puck, TopRim(puck), 1.2)); // > rim radius
        var puck2 = SolidFactory.MakeCylinder(1.0, 0.3);
        Assert.Throws<ArgumentOutOfRangeException>(() => Filleting.FilletEdge(puck2, TopRim(puck2), 0.5)); // > band length

        // A box edge (open line) is not supported yet.
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1)));
        Assert.Throws<NotSupportedException>(() => Filleting.FilletEdge(box, box.Edges.First(), 0.1));
    }
}
