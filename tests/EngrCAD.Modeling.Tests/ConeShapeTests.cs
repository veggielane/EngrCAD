using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class ConeShapeTests
{
    private const double R1 = 2, R2 = 1, H = 3;
    private static double ExactVolume => Math.PI * H * (R1 * R1 + R1 * R2 + R2 * R2) / 3;

    [Fact]
    public void Cone_NativeInAllThreeRepresentations()
    {
        var cone = Shape.Cone(R1, R2, H).RotateX(0.4).Translate(1, 2, 3);
        foreach (var target in new[] { TargetRep.Brep, TargetRep.Implicit, TargetRep.Mesh })
        {
            var report = cone.Explain(target);
            Assert.True(report.IsConvertible);
            Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        }
    }

    [Fact]
    public void Cone_AllThreeRepresentationsAgreeOnVolume()
    {
        var cone = Shape.Cone(R1, R2, H).RotateX(0.4).Translate(1, 2, 3);

        var brep = cone.ToBrep();
        brep.Validate();
        Assert.True(brep.SatisfiesEulerFormula(genus: 0));
        var brepMesh = BRepTessellator.Tessellate(brep, 256, 24);
        Assert.True(brepMesh.IsClosed);
        Assert.True(Math.Abs(brepMesh.Volume() - ExactVolume) / ExactVolume < 0.001,
            $"brep volume {brepMesh.Volume()} vs {ExactVolume}");

        var mesh = cone.ToMesh(new MeshQuality { SegmentsPerCircle = 256 });
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - ExactVolume) / ExactVolume < 0.001,
            $"mesh volume {mesh.Volume()} vs {ExactVolume}");

        double sdfVolume = SurfaceNets.Polygonize(cone.ToImplicit(), 96).Volume();
        Assert.True(Math.Abs(sdfVolume - ExactVolume) / ExactVolume < 0.05,
            $"sdf volume {sdfVolume} vs {ExactVolume}");
    }

    [Fact]
    public void ApexCone_WorksEndToEnd()
    {
        var cone = Shape.Cone(1.5, 0, 2);
        double exact = Math.PI * 1.5 * 1.5 * 2 / 3;

        var brep = cone.ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep, 256, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.001,
            $"volume {mesh.Volume()} vs {exact}");

        Assert.Equal(-2.0 / 3, cone.ToImplicit().Evaluate(new Vector3d(0, 0, -1 + 2.0 / 3)), 6);
    }

    [Fact]
    public void ShearedCone_IsImpossibleInBrepButMeshable()
    {
        var shear = new Matrix4d(
            1, 0, 0.5, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);
        var sheared = Shape.Cone(R1, R2, H).Transform(shear);
        Assert.False(sheared.Explain(TargetRep.Brep).IsConvertible);
        Assert.True(sheared.Explain(TargetRep.Mesh).IsConvertible);
        Assert.True(sheared.ToMesh().IsClosed);
    }

    [Fact]
    public void DrillIntoCone_Smoke()
    {
        // Frustum centered at the origin (z ∈ [−0.5, 0.5]); a through-hole from the top
        // cap removes an exact π·r²·h cylinder.
        var cone = Shape.Cone(2, 1.5, 1);
        var drilled = cone.Drill(
            HoleSpec.Simple(0.6), [new Vector2d(0, 0)], depth: 2,
            SketchPlane.At((0, 0, 0.5), Vector3d.UnitX, Vector3d.UnitY));

        double frustum = Math.PI * 1 * (4 + 3 + 2.25) / 3;
        double exact = frustum - Math.PI * 0.3 * 0.3 * 1;

        var brep = drilled.ToBrep();
        brep.Validate();
        var brepMesh = BRepTessellator.Tessellate(brep, 256, 24);
        Assert.True(brepMesh.IsClosed);
        Assert.True(Math.Abs(brepMesh.Volume() - exact) / exact < 0.001,
            $"drilled brep volume {brepMesh.Volume()} vs {exact}");

        var mesh = drilled.ToMesh(new MeshQuality { SegmentsPerCircle = 128 });
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.002,
            $"drilled mesh volume {mesh.Volume()} vs {exact}");

        double sdfVolume = SurfaceNets.Polygonize(drilled.ToImplicit(), 96).Volume();
        Assert.True(Math.Abs(sdfVolume - exact) / exact < 0.05,
            $"drilled sdf volume {sdfVolume} vs {exact}");
    }
}
