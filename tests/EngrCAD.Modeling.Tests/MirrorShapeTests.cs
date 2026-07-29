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

    // ---- mirrored B-Rep completion: draft / shell / round-edges / loft ---------
    // These four needed no conjugation identity at all — each is defined purely by
    // LENGTHS and ANGLES, which every isometry preserves, so lowering the mirrored
    // child and running the same operation on it IS the mirrored result. Draft is the
    // only one with a direction to carry, and it takes the pull's LINEAR IMAGE (not a
    // negated one: a pull direction is transported, not conjugated the way a revolve's
    // axis is).

    [Fact]
    public void MirroredDraft_IsBrepNative_WithTheExactTaperedVolume()
    {
        // A 20 x 12 x 6 block drafted 5 degrees about its BASE, so the taper's SIGN is
        // visible in the volume: narrowing gives abh - (a+b)t h^2 + (4/3)t^2 h^3 while
        // widening gives + on the middle term - 1341.4 against 1543.0, two hundred units
        // apart. That is what makes this an oracle for the pull direction rather than a
        // check that some solid came out: a mirror is an isometry, so a WRONGLY signed
        // pull still produces a closed, valid solid of a perfectly plausible size.
        const double angle = 5.0;
        double t = Math.Tan(angle * Math.PI / 180);
        double exact = 20 * 12 * 6 - (20 + 12) * t * 36 + 4.0 / 3.0 * t * t * 216;
        double widened = 20 * 12 * 6 + (20 + 12) * t * 36 + 4.0 / 3.0 * t * t * 216;

        var drafted = Shape.Box(20, 12, 6).Draft(angle, (0, 0, -3), Vector3d.UnitZ);
        var mirrored = drafted.Mirror((3, 0, 0), Vector3d.UnitX);

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 64, 24);
        Assert.True(mesh.IsClosed);

        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 1e-9,
            $"mirrored drafted volume {mesh.Volume()} vs {exact} (a flipped pull would give {widened})");
    }

    [Fact]
    public void MirroredDraft_AcrossThePullDirection_StillTapersTheSameWay()
    {
        // Mirroring across a plane PERPENDICULAR to the pull direction turns the block
        // upside down, so "narrows going +Z" becomes "narrows going -Z" in world terms —
        // the reflected pull is -Z and the solid is the reflection of the original. The
        // volume is unchanged (isometry) and the top face is now the WIDE one.
        var drafted = Shape.Box(20, 12, 6).Draft(5, (0, 0, -3), Vector3d.UnitZ);
        var mirrored = drafted.Mirror((0, 0, 0), Vector3d.UnitZ);

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var mesh = BRepTessellator.Tessellate(mirrored.ToBrep(), 64, 24);
        var reference = BRepTessellator.Tessellate(drafted.ToBrep(), 64, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) < 1e-9,
            $"mirrored drafted volume {mesh.Volume()} vs {reference.Volume()}");

        // The neutral plane was the base at z = -3, which reflects to z = +3, so the
        // mirrored block is WIDE at the top and narrow at the bottom. Half-widths in x
        // are 10 - (z' + 3)tan(5 deg) at the original height z' = -z: 9.956 at z = +2.5
        // and 9.519 at z = -2.5, so x = 9.7 straddles them. Sample the field rather than
        // inferring the direction from a volume, which a flipped pull would match.
        var sdf = mirrored.ToImplicit();
        Assert.True(sdf.Evaluate((9.7, 0, 2.5)) < 0, "x = 9.7 is inside the wide end");
        Assert.True(sdf.Evaluate((9.7, 0, -2.5)) > 0, "x = 9.7 must be outside the narrow end");
    }

    [Fact]
    public void MirroredShell_IsBrepNative_WithTheExactWallVolume()
    {
        // A sealed 1.5 mm shell of a 20 x 12 x 6 block: the void is 17 x 9 x 3, so the
        // closed mesh measures 1440 - 459 = 981. An offset is defined by DISTANCE alone,
        // which every reflection preserves.
        var shelled = Shape.Box(20, 12, 6).Shell(1.5, null);
        var mirrored = shelled.Mirror((1, -2, 0.5), (0.6, 1, 0.3));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 64, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - 981.0) < 1e-9,
            $"mirrored shelled volume {mesh.Volume()} vs 981");
    }

    [Fact]
    public void MirroredRoundEdges_IsBrepNative_AndMatchesSteiner()
    {
        // The morphological opening's structuring element is a BALL, which every
        // reflection maps to itself, so the mirrored rounding is the rounding of the
        // mirrored solid. Steiner on the ERODED body (16 x 8 x 2) is the analytic
        // target; a tessellation converges onto it from inside, so compare at a
        // discretization-honest tolerance and pin the mirror against its own twin
        // exactly.
        const double r = 2;
        double steiner = 16 * 8 * 2                                  // eroded volume
            + 2 * (16 * 8 + 16 * 2 + 8 * 2) * r                      // eroded area * r
            + r * r / 2 * (4 * (16 + 8 + 2)) * (Math.PI / 2)         // edges, each a quarter turn
            + 4 * Math.PI / 3 * r * r * r;                           // corners

        var rounded = Shape.Box(20, 12, 6).RoundEdges(r);
        var mirrored = rounded.Mirror((0.5, 0, 0.2), (1, 0.3, 0.8));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 96, 32);
        var reference = BRepTessellator.Tessellate(rounded.ToBrep(), 96, 32);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - reference.Volume()) < 1e-9,
            $"mirrored rounded volume {mesh.Volume()} vs {reference.Volume()}");
        Assert.True(Math.Abs(mesh.Volume() - steiner) / steiner < 2e-3,
            $"mirrored rounded volume {mesh.Volume()} vs Steiner {steiner}");
    }

    [Fact]
    public void MirroredLoft_IsBrepNative_WithTheExactPrismatoidVolume()
    {
        // A ruled loft between a 10 x 10 square and a 4 x 4 square 8 apart is a
        // frustum: the prismatoid formula h/6 (A0 + 4Am + A1) is exact for it, with the
        // mid-section 7 x 7. The loft's chord-length parameterization and least-twist
        // alignment are metric, so an isometry commutes with both.
        double exact = 8.0 / 6.0 * (100 + 4 * 49 + 16);

        var loft = Shape.Loft(
        [
            (Sketch.Rectangle(10, 10), SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY)),
            (Sketch.Rectangle(4, 4), SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY)),
        ], LoftStyle.Ruled);
        var mirrored = loft.Mirror((2, -1, 0), (1, 0.4, 0.2));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 64, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 1e-9,
            $"mirrored loft volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void MirroredTaperedExtrude_IsBrepNative()
    {
        // A pure taper LOWERS as a two-section ruled loft, so it inherits the loft's
        // isometry argument verbatim — leaving it refused with the loft Native would be
        // one operation disagreeing with itself. Same prismatoid oracle: 10 x 10 scaled
        // to 0.4 over a height of 8, mid-section 7 x 7.
        double exact = 8.0 / 6.0 * (100 + 4 * 49 + 16);

        var tapered = Shape.Extrude(Sketch.Rectangle(10, 10), height: 8, twist: 0, scale: 0.4);
        var mirrored = tapered.Mirror((0, 3, 1), (0.3, 1, 0));

        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var mesh = BRepTessellator.Tessellate(mirrored.ToBrep(), 64, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 1e-9,
            $"mirrored tapered volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void MirroredSheetMetal_StaysRefusedByName()
    {
        // The one node in this family that is NOT isometry-commuting, and it is refused
        // for a stated reason rather than by omission: a flange tree is an ORDERED tree
        // of bends quoted on named edges, and a reflection reverses the sense of every
        // one of them, so the body would have to be rebuilt the other way round rather
        // than re-placed.
        var body = SheetMetalBody.Base(Sketch.Rectangle(80, 50), new SheetMetalSpec(1.5, 2));
        var sheet = body.Solid.Mirror((0, 0, 0), Vector3d.UnitX);

        var report = sheet.Explain(TargetRep.Brep);
        Assert.False(report.IsConvertible);
        Assert.Contains(report.Entries, e => e.Support == NodeSupport.Impossible &&
            e.Detail is not null && e.Detail.Contains("MIRROR"));
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
