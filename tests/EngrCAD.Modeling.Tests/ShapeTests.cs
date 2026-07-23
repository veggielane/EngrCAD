using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class ShapeTests
{
    private static double PolygonizedVolume(Sdf sdf, int resolution = 96) =>
        SurfaceNets.Polygonize(sdf, resolution).Volume();

    [Fact]
    public void DrilledBlock_AllThreeRepresentationsAgree()
    {
        var shape = Shape.Box(2, 1.5, 1) - Shape.Cylinder(0.3, 3);
        double exact = 2 * 1.5 * 1 - Math.PI * 0.3 * 0.3 * 1;

        var brep = shape.ToBrep();
        brep.Validate();
        var brepMesh = BRepTessellator.Tessellate(brep, 256, 24);
        // Tessellated bore is an inscribed prism: slightly more material left behind.
        Assert.True(Math.Abs(brepMesh.Volume() - exact) / exact < 0.001,
            $"brep volume {brepMesh.Volume()} vs {exact}");

        var mesh = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 256 });
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.001,
            $"mesh volume {mesh.Volume()} vs {exact}");

        double sdfVolume = PolygonizedVolume(shape.ToImplicit());
        Assert.True(Math.Abs(sdfVolume - exact) / exact < 0.05,
            $"sdf volume {sdfVolume} vs {exact}");
    }

    [Fact]
    public void RotatedTranslatedBoolean_SameVolumeAsUntransformed()
    {
        var shape = Shape.Box(2, 1.5, 1) - Shape.Cylinder(0.3, 3);
        var moved = shape.RotateZ(0.7).RotateX(0.3).Translate(5, -2, 1);

        var mesh = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        var movedMesh = moved.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.True(movedMesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - movedMesh.Volume()) < 1e-9,
            $"{movedMesh.Volume()} should equal {mesh.Volume()}");
    }

    [Fact]
    public void ShearedBox_VolumeIsPreserved()
    {
        // Shear keeps volume (det = 1); the box lowers to an extrusion.
        var shear = new Matrix4d(
            1, 0, 0.5, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);
        var solid = Shape.Box(2, 1.5, 1).Transform(shear).ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 32, 24);
        Assert.True(Math.Abs(mesh.Volume() - 3.0) < 1e-9, $"volume {mesh.Volume()}");
    }

    [Fact]
    public void UniformScale_ScalesVolumesCubically()
    {
        var mesh = Shape.Sphere(1).Scale(2).ToMesh(new MeshQuality { SegmentsPerCircle = 128, CurveSamples = 64 });
        double exact = 4.0 / 3.0 * Math.PI * 8;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.005, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void SphereToBrep_ValidGenusZeroWithExactSurface()
    {
        var solid = Shape.Sphere(1).Translate(3, 0, 0).ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 64, 32);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        double exact = 4.0 / 3.0 * Math.PI;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.005);
    }

    [Fact]
    public void TorusToBrep_ValidGenusOnePappusVolume()
    {
        var solid = Shape.Torus(2, 0.5).RotateX(Math.PI / 4).Translate(0, 1, 0).ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 64, 32);
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1
        double exact = 2 * Math.PI * Math.PI * 2 * 0.5 * 0.5;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.005, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void SmoothBlend_ToBrepThrows_ToImplicitIsNative()
    {
        var blended = Shape.Sphere(1).SmoothUnion(Shape.Sphere(1).Translate(1, 0, 0), 0.3);

        var exception = Assert.Throws<ShapeConversionException>(() => blended.ToBrep());
        Assert.Contains(exception.Report.Entries, e => e.Support == NodeSupport.Impossible);

        var implicitReport = blended.Explain(TargetRep.Implicit);
        Assert.True(implicitReport.IsConvertible);
        Assert.All(implicitReport.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        // Mesh is always reachable: the impossible node becomes a bridge.
        var meshReport = blended.Explain(TargetRep.Mesh);
        Assert.True(meshReport.IsConvertible);
        Assert.Contains(meshReport.Entries, e => e.Support == NodeSupport.Bridged);

        var mesh = blended.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 4.0 / 3.0 * Math.PI); // more than one sphere
    }

    [Fact]
    public void Extrude_ToImplicitBridgesThroughMeshSdf()
    {
        var bracket = Shape.Extrude(
            Profile.FromPoints([(0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0)]),
            (0, 0, 0.5));

        var report = bracket.Explain(TargetRep.Implicit);
        Assert.True(report.IsConvertible);
        Assert.Contains(report.Entries, e => e.Support == NodeSupport.Bridged);

        var sdf = bracket.ToImplicit();
        Assert.True(Math.Abs(sdf.Evaluate((1, 0.5, 0.25))) > 0.2);    // inside, ~0.25 deep
        Assert.True(sdf.Evaluate((1, 0.5, 0.25)) < 0);
        Assert.True(sdf.Evaluate((3, 0.5, 0.25)) > 0.9);              // outside, ~1 away
    }

    [Fact]
    public void Revolve_RigidTransformBakesIn()
    {
        // Quarter washer revolved fully, then moved: volume must match Pappus exactly
        // under the rigid transform.
        var profile = Profile.FromPoints([(1, 0, 0), (1.4, 0, 0), (1.4, 0, 0.3), (1, 0, 0.3)]);
        var shape = Shape.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ)
            .RotateY(0.5).Translate(-1, 2, 0.5);

        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 512, 24);
        double exact = Math.PI * (1.4 * 1.4 - 1.0) * 0.3;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.001,
            $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void FromMesh_RoundTripsAndBooleansViaMeshEngine()
    {
        var box = MeshPrimitives.Box(new Aabb((-1, -1, -1), (1, 1, 1)));
        var shape = Shape.From(box) - Shape.Cylinder(0.5, 4);

        Assert.False(shape.CanConvertTo(TargetRep.Brep));
        var mesh = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.True(mesh.IsClosed);
        double exact = 8 - Math.PI * 0.25 * 2;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.005,
            $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void FromSdf_TransformsCompose()
    {
        var moved = Shape.From(Sdf.Sphere(1)).Translate(2, 0, 0).ToImplicit();
        Assert.True(moved.Evaluate((2, 0, 0)) < -0.99);
        Assert.True(Math.Abs(moved.Evaluate((0, 0, 0)) - 1) < 1e-9);
    }

    [Fact]
    public void Shell_ProducesHollowBody()
    {
        var hollow = Shape.Sphere(1).Shell(0.1).ToImplicit();
        Assert.True(hollow.Evaluate(Vector3d.Zero) > 0);          // center is now outside
        Assert.True(hollow.Evaluate((1, 0, 0)) < 0);              // wall is inside
    }

    [Fact]
    public void SceneAdd_UsesBrepPathAndKeepsShapeAsGeometry()
    {
        var scene = new Scene();
        var shape = Shape.Box(1, 1, 1) - Shape.Cylinder(0.3, 2);
        var part = scene.Add(new Part("drilled", shape));
        scene.PreMesh();

        Assert.Same(shape, part.Geometry);
        Assert.True(part.GetMesh().IsClosed);
        // The B-Rep path leaves crisp features: volume matches the exact difference
        // far better than any SDF polygonization would.
        double exact = 1 - Math.PI * 0.09;
        Assert.True(Math.Abs(part.GetMesh().Volume() - exact) / exact < 0.01);
    }
}
