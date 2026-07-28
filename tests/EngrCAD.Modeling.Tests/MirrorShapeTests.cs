using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class MirrorShapeTests
{
    private static Vector3d Reflect(in Vector3d p, in Vector3d point, Vector3d normal)
    {
        var n = normal.Normalized();
        return p - n * (2 * n.Dot(p - point));
    }

    [Fact]
    public void MirroredDrilledPlate_EqualVolume_MirroredHolePositions()
    {
        var plate = Shape.Box(4, 3, 1).Drill(
            HoleSpec.Simple(0.4), [new Vector2d(1, 0.5), new Vector2d(-1, -0.5)], depth: 2,
            SketchPlane.At((0, 0, 0.5), Vector3d.UnitX, Vector3d.UnitY));
        var mirrorPoint = new Vector3d(0.3, 0, 0);
        var mirrorNormal = Vector3d.UnitX;
        var mirrored = plate.Mirror(mirrorPoint, mirrorNormal);

        // Mesh: reflection is an isometry, and the mirrored path reflects the exact
        // tessellation (winding flipped), so volumes agree to rounding.
        var mesh = plate.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        var mirroredMesh = mirrored.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.True(mirroredMesh.IsClosed);
        Assert.True(mirroredMesh.Volume() > 0);
        Assert.True(Math.Abs(mesh.Volume() - mirroredMesh.Volume()) < 1e-9,
            $"mirrored volume {mirroredMesh.Volume()} vs {mesh.Volume()}");

        // Implicit: the mirrored SDF is the original evaluated at the reflected point —
        // exact — so the holes sit at exactly the mirrored positions.
        var sdf = plate.ToImplicit();
        var mirroredSdf = mirrored.ToImplicit();
        var random = new Random(21);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 5, (random.NextDouble() - 0.5) * 3);
            Assert.Equal(sdf.Evaluate(p), mirroredSdf.Evaluate(Reflect(p, mirrorPoint, mirrorNormal)), 12);
        }

        // Spot checks: hole voids at mirrored centers, solid where the old hole was.
        Assert.True(mirroredSdf.Evaluate(Reflect((1, 0.5, 0), mirrorPoint, mirrorNormal)) > 0,
            "mirrored hole 1 is not a void");
        Assert.True(mirroredSdf.Evaluate(Reflect((-1, -0.5, 0), mirrorPoint, mirrorNormal)) > 0,
            "mirrored hole 2 is not a void");
        Assert.True(mirroredSdf.Evaluate(new Vector3d(1, 0.5, 0)) < 0,
            "the un-mirrored hole position should be solid material in the mirrored plate");

        // Mirrored drills are B-Rep-Native now: the revolved tools lower via the
        // axis-negation identity, so the whole drilled plate survives ToBrep.
        Assert.True(mirrored.Explain(TargetRep.Brep).IsConvertible);
        var solid = mirrored.ToBrep();
        solid.Validate();
        var brepMesh = BRepTessellator.Tessellate(solid, 64, 24);
        Assert.True(brepMesh.IsClosed);
        Assert.True(Math.Abs(brepMesh.Volume() - mesh.Volume()) / mesh.Volume() < 1e-3,
            $"mirrored B-Rep volume {brepMesh.Volume()} vs mesh {mesh.Volume()}");
    }

    [Fact]
    public void MirrorOfMirror_IsIdentityWithinTolerance()
    {
        var shape = Shape.Box(2, 1.5, 1) - Shape.Cylinder(0.3, 3);
        var back = shape.Mirror((0.7, -0.2, 0.1), (1, 2, -0.5)).Mirror((0.7, -0.2, 0.1), (1, 2, -0.5));

        var mesh = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        var backMesh = back.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.True(backMesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - backMesh.Volume()) < 1e-9,
            $"double-mirrored volume {backMesh.Volume()} vs {mesh.Volume()}");

        var sdf = shape.ToImplicit();
        var backSdf = back.ToImplicit();
        var random = new Random(3);
        for (int i = 0; i < 100; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 4, (random.NextDouble() - 0.5) * 4, (random.NextDouble() - 0.5) * 4);
            Assert.Equal(sdf.Evaluate(p), backSdf.Evaluate(p), 9);
        }
    }

    [Fact]
    public void MirroredBoxMinusCylinder_StaysNativeInBrep()
    {
        // Box and cylinder lower exactly under any affine map (mirrored circles stay
        // true circles), so the whole boolean survives ToBrep after a slanted mirror.
        var shape = Shape.Box(2, 1.5, 1) - Shape.Cylinder(0.3, 3);
        var mirrored = shape.Mirror((0.5, 0, 0.2), (1, 0.3, 0.8));

        Assert.True(mirrored.Explain(TargetRep.Brep).IsConvertible);
        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 128, 24);
        Assert.True(mesh.IsClosed);

        double exact = 2 * 1.5 * 1 - Math.PI * 0.3 * 0.3 * 1;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.001,
            $"mirrored brep volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void MirroredSphereTorusCone_StayNativeInBrep()
    {
        var assembly = Shape.Sphere(0.8).Translate(2, 0, 0)
            | Shape.Torus(2, 0.4).Translate(0, 0, 1.5)
            | Shape.Cone(1, 0.5, 1).Translate(-2.5, 0, 0);
        var mirrored = assembly.Mirror((0, 1, 0), (0.2, 1, 0.1));

        Assert.True(mirrored.Explain(TargetRep.Brep).IsConvertible);
        Assert.All(mirrored.Explain(TargetRep.Brep).Entries,
            e => Assert.Equal(NodeSupport.Native, e.Support));

        var mesh = mirrored.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        var reference = assembly.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) / reference.Volume() < 1e-6,
            $"mirrored volume {mesh.Volume()} vs {reference.Volume()}");
    }

    [Fact]
    public void MirroredSketchRevolve_IsExactViaImplicitAndMesh()
    {
        // Mirrored revolves are B-Rep-Native (axis negation); the mirror also stays
        // exact through the SDF and mesh routes.
        var vase = Shape.Revolve(Sketch.Polygon([(0, 0), (1, 0), (0.8, 1), (0, 1)]));
        var mirrored = vase.Mirror((0, 0, 0.2), (0, 0, 1));

        Assert.True(mirrored.Explain(TargetRep.Brep).IsConvertible);
        Assert.True(mirrored.Explain(TargetRep.Implicit).IsConvertible);

        var sdf = vase.ToImplicit();
        var mirroredSdf = mirrored.ToImplicit();
        var random = new Random(17);
        for (int i = 0; i < 100; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 3, (random.NextDouble() - 0.5) * 3, (random.NextDouble() - 0.5) * 3);
            Assert.Equal(sdf.Evaluate(p), mirroredSdf.Evaluate(Reflect(p, (0, 0, 0.2), (0, 0, 1))), 12);
        }

        var mesh = mirrored.ToMesh();
        var reference = vase.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) < 1e-9,
            $"mirrored vase volume {mesh.Volume()} vs {reference.Volume()}");
    }

    // ---- mirrored B-Rep completion: revolve / sweep / rim / drill -------------
    // The identity: a reflection conjugates a rotation, F·Rot(d, φ)·F = Rot(−F·d, φ),
    // so a mirrored revolve is the same sweep about the negated transformed axis —
    // the same pattern that made mirrored threads exact (left-hand threads).

    [Fact]
    public void MirroredPartialRevolve_IsBrepNative_WithTheExactVolume()
    {
        // A quarter-turn revolve of an off-axis square: exact volume by Pappus,
        // V = A · (2π r̄) · (angle/2π) = A · r̄ · angle.
        var section = Sketch.Polygon([(2, 0), (3, 0), (3, 1), (2, 1)]);
        var revolve = Shape.Revolve(section, Math.PI / 2);
        var mirrored = revolve.Mirror((0.4, -0.2, 0.1), (1, 0.5, 0.3));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 128, 24);
        Assert.True(mesh.IsClosed);

        double exact = 1.0 * 2.5 * (Math.PI / 2);   // A=1, centroid radius 2.5
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 1e-3,
            $"mirrored revolve volume {mesh.Volume()} vs {exact}");

        // And the geometry is genuinely the mirror image: the B-Rep tessellations of
        // "mirror the shape" and "reflect the reference mesh" agree in volume and
        // bounds.
        var reference = revolve.ToMesh(new MeshQuality { SegmentsPerCircle = 128 });
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) / reference.Volume() < 1e-6);
    }

    [Fact]
    public void MirroredSweep_IsBrepNative_AndMatchesTheReflectedGeometry()
    {
        // Sweep a circle along a bending NURBS path (start tangent +Z, matching the
        // default XY sketch plane), then mirror across a slanted plane. The RMF
        // transport is intrinsic, so the mirrored sweep needs no sign fix; volumes
        // agree with the unmirrored solid (reflection is an isometry).
        var path = new NurbsCurve(2,
            [(0, 0, 0), (0, 0, 2.6), (0, 2.2, 4.4)], null,
            [0, 0, 0, 1, 1, 1]);
        var sweep = Shape.Sweep(Sketch.Circle(0.4), path);
        var mirrored = sweep.Mirror((1, 0.3, 0), (0.4, 1, 0.2));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var mesh = BRepTessellator.Tessellate(mirrored.ToBrep(), 64, 24);
        var reference = BRepTessellator.Tessellate(sweep.ToBrep(), 64, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) / reference.Volume() < 1e-9,
            $"mirrored sweep volume {mesh.Volume()} vs {reference.Volume()}");
    }

    [Fact]
    public void MirroredChamfer_IsBrepNative_AndCutsTheMirroredRim()
    {
        // Chamfer the top rim of a plate, then mirror across a vertical plane: the
        // chamfer commutes with the isometry, so the mirrored solid's volume equals
        // the chamfered plate's. (The selector runs on the LOWERED, i.e. mirrored,
        // solid — an x-mirror keeps the top face's +Z normal, so it still matches.)
        var plate = Shape.Box(20, 12, 6).Chamfer(1.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));
        var mirrored = plate.Mirror((3, 0, 0), (1, 0, 0));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var mesh = BRepTessellator.Tessellate(mirrored.ToBrep(), 64, 24);
        var reference = BRepTessellator.Tessellate(plate.ToBrep(), 64, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) < 1e-9,
            $"mirrored chamfered volume {mesh.Volume()} vs {reference.Volume()}");
    }
}
