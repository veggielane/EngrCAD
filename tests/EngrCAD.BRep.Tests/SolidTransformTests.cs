using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="BrepSolid.Transformed"/> — re-placing a whole solid by a proper rigid motion.
///
/// <para>The claim under test is that this is a POSE rather than a re-derivation: the moved
/// solid must be the same solid somewhere else, with every surface answered in its own family
/// and every parameterization carried over verbatim. So the assertions are about IDENTITY of
/// type and of parameter, not merely about points landing near the right place — a wrapper
/// round every curve would satisfy a positions-only test and would quietly cost the
/// tessellator its sampling rules and STEP its analytic entities.</para>
/// </summary>
public class SolidTransformTests
{
    /// <summary>A pose with no special structure: a real rotation about a skew axis plus an
    /// offset, so nothing can pass by accidentally commuting with an axis-aligned map.</summary>
    private static Matrix4d Pose() =>
        Matrix4d.CreateTranslation((13, -7, 4))
        * Matrix4d.CreateFromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.7);

    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8)));

    // ---- it is a pose --------------------------------------------------------

    [Fact]
    public void APosedBoxIsStructurallyTheSameSolid()
    {
        var box = Box();
        var moved = box.Transformed(Pose());
        moved.Validate();
        Assert.True(moved.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(box.Faces.Count(), moved.Faces.Count());
        Assert.Equal(box.Edges.Count(), moved.Edges.Count());
        Assert.Equal(box.Vertices.Count(), moved.Vertices.Count());
    }

    [Fact]
    public void EveryVertexLandsWhereTheTransformPutsIt()
    {
        var box = Box();
        var m = Pose();
        var moved = box.Transformed(m);

        // Compare as SETS: the walk preserves order, but the claim is geometric.
        var expected = box.Vertices.Select(v => m.TransformPoint(v.Position)).ToList();
        foreach (var vertex in moved.Vertices)
        {
            Assert.Contains(expected, p => p.DistanceTo(vertex.Position) < 1e-12);
        }
    }

    [Fact]
    public void ARigidMotionPreservesEveryLength()
    {
        // The property the whole design rests on: an isometry preserves lengths and angles,
        // which is why no parameterization has to be re-derived.
        var box = Box();
        var moved = box.Transformed(Pose());
        var before = box.Edges.Select(e => e.Curve.ArcLength(e.Domain.Start, e.Domain.End)).OrderBy(x => x).ToList();
        var after = moved.Edges.Select(e => e.Curve.ArcLength(e.Domain.Start, e.Domain.End)).OrderBy(x => x).ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
            Assert.Equal(before[i], after[i], 12);
    }

    [Fact]
    public void EdgeTrimDomainsAreCarriedVERBATIM()
    {
        // An isometry does not move a parameter, so a domain is copied rather than recomputed
        // — asserted BITWISE, because "close enough" is exactly what this design avoids.
        var cylinder = SolidFactory.MakeCylinder(5, 12);
        var moved = cylinder.Transformed(Pose());
        var before = cylinder.Edges.Select(e => e.Domain).ToList();
        var after = moved.Edges.Select(e => e.Domain).ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(before[i].Start),
                         BitConverter.DoubleToInt64Bits(after[i].Start));
            Assert.Equal(BitConverter.DoubleToInt64Bits(before[i].End),
                         BitConverter.DoubleToInt64Bits(after[i].End));
        }
    }

    // ---- every type stays in its own family ----------------------------------

    [Fact]
    public void AMovedCylinderIsStillACylinderSurfaceWithItsOwnRadius()
    {
        var moved = SolidFactory.MakeCylinder(5, 12).Transformed(Pose());
        var wall = Assert.IsType<CylinderSurface>(
            moved.Faces.Single(f => f.Surface is not PlaneSurface).Surface);
        Assert.Equal(5, wall.Radius, 12);
        // The frame stays orthonormal, which is what keeps the surface a cylinder rather
        // than something that merely evaluates like one.
        Assert.Equal(1, wall.XDirection.Length, 12);
        Assert.Equal(1, wall.YDirection.Length, 12);
        Assert.Equal(0, wall.XDirection.Dot(wall.YDirection), 12);
    }

    [Fact]
    public void AMovedRimIsStillACircle3dOfTheSameRadius()
    {
        // Rim circles matter beyond tidiness: the tessellator gives Circle3d the
        // segments-per-circle density, and rim surgery reads circles off edges.
        var moved = SolidFactory.MakeCylinder(5, 12).Transformed(Pose());
        foreach (var edge in moved.Edges)
        {
            var circle = Assert.IsType<Circle3d>(edge.Curve.Underlying);
            Assert.Equal(5, circle.Radius, 12);
            Assert.Equal(1, circle.XDirection.Length, 12);
        }
    }

    [Fact]
    public void AMovedRevolveIsStillARevolvedSurfaceWithAUnitAxis()
    {
        var moved = SolidFactory.MakeSphere(7).Transformed(Pose());
        foreach (var face in moved.Faces)
        {
            var revolved = Assert.IsType<RevolvedSurface>(face.Surface);
            Assert.Equal(1, revolved.AxisDirection.Length, 12);
        }
    }

    [Fact]
    public void AMovedPlaneKeepsItsOutwardNormal()
    {
        // A proper motion preserves orientation, so the moved normal must be the moved
        // ORIGINAL normal — not its negation, which is what an improper map would give and
        // which is half the reason reflections are refused.
        var box = Box();
        var m = Pose();
        var moved = box.Transformed(m);
        var originals = box.Faces
            .Select(f => { f.IsPlanar(out var o, out var n); return (Origin: o, Normal: n.Normalized()); })
            .ToList();

        foreach (var face in moved.Faces)
        {
            Assert.True(face.IsPlanar(out _, out var normal));
            var moved3 = normal.Normalized();
            Assert.Contains(originals, s => m.TransformVector(s.Normal).Dot(moved3) > 0.999999);
        }
    }

    // ---- independence and composition ---------------------------------------

    [Fact]
    public void TheOriginalIsUntouchedAndTheResultIsAnIndependentGraph()
    {
        // Transformed is Clone's walk, so it must give the same guarantee: booleans CONSUME
        // their inputs, and a posed copy sharing topology with its source would poison both.
        var box = Box();
        var before = box.Vertices.Select(v => v.Position).ToList();
        var moved = box.Transformed(Pose());

        Assert.All(box.Vertices.Zip(before), pair =>
            Assert.Equal(0, pair.First.Position.DistanceTo(pair.Second), 12));
        Assert.Empty(box.Faces.Intersect(moved.Faces));
        Assert.Empty(box.Edges.Intersect(moved.Edges));
        Assert.Empty(box.Vertices.Intersect(moved.Vertices));
    }

    [Fact]
    public void ASharedCurveStaysShared()
    {
        // A seam curve backs two edges and a carrier backs many. Mapping per EDGE rather
        // than per curve object would silently split one carrier into several — every one
        // numerically equal, and no longer the same object — which is how a solid stops
        // welding without any test noticing.
        var sphere = SolidFactory.MakeSphere(7);
        int distinctBefore = sphere.Edges.Select(e => e.Curve).Distinct().Count();
        var moved = sphere.Transformed(Pose());
        Assert.Equal(distinctBefore, moved.Edges.Select(e => e.Curve).Distinct().Count());
    }

    [Fact]
    public void AClosedEdgeStaysClosed()
    {
        // IsClosedEdge IS reference equality of the two vertices, so the mapping has to
        // preserve object identity rather than merely produce two equal points.
        var cylinder = SolidFactory.MakeCylinder(5, 12);
        Assert.Contains(cylinder.Edges, e => e.IsClosedEdge);
        var moved = cylinder.Transformed(Pose());
        Assert.Equal(cylinder.Edges.Count(e => e.IsClosedEdge), moved.Edges.Count(e => e.IsClosedEdge));
    }

    [Fact]
    public void PoseAndItsInverseReturnTheSolidToItself()
    {
        var box = Box();
        var m = Pose();
        var back = box.Transformed(m).Transformed(m.Inverse());
        foreach (var vertex in back.Vertices)
            Assert.Contains(box.Vertices, v => v.Position.DistanceTo(vertex.Position) < 1e-9);
    }

    [Fact]
    public void ProvenanceRidesThroughAPose()
    {
        // The point of posing an IMPORT: a tagged body stays selectable after placement.
        var box = Box();
        box.PlanarFacesWithNormal(Vector3d.UnitZ).Single().AddProvenance("datum");
        var moved = box.Transformed(Pose());

        var carrier = Assert.Single(moved.Faces, f => f.Provenance.Contains("datum"));
        Assert.True(carrier.IsPlanar(out _, out var normal));
        Assert.True(Pose().TransformVector(Vector3d.UnitZ).Dot(normal.Normalized()) > 0.999999);
    }

    // ---- the wrapper fold ----------------------------------------------------

    [Fact]
    public void AWrappedCurveIsFOLDEDRatherThanNested()
    {
        // A moved copy of a moved curve is ONE placement, so the wrapper must collapse
        // instead of stacking — otherwise a solid posed twice carries a chain of wrappers
        // and its rims stop being recognizable as circles.
        var circle = new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var once = circle.Transformed(Matrix4d.CreateTranslation((1, 0, 0)));
        var twice = GeometryTransform.Apply(once, Matrix4d.CreateTranslation((0, 2, 0)));

        var folded = Assert.IsType<Circle3d>(twice);
        Assert.Equal(5, folded.Radius, 12);
        Assert.Equal(0, folded.Center.DistanceTo((1, 2, 0)), 12);
    }

    [Fact]
    public void AWrapperCarryingAScaleIsNESTEDInsteadOfFolded()
    {
        // The gate is checked on the PRODUCT, not assumed from the outer map. Folding a
        // scaling wrapper into a Circle3d would hand it non-unit axes beside an unscaled
        // radius — which evaluates to the right points and reports the wrong arc length, so
        // nothing downstream would complain. Nesting stays exact.
        var circle = new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var scaled = circle.Transformed(Matrix4d.CreateScale(3));
        var moved = GeometryTransform.Apply(scaled, Matrix4d.CreateTranslation((1, 0, 0)));

        Assert.IsType<TransformedCurve>(moved);
        // Still exact in position: the radius really is 15 about (1, 0, 0).
        for (int i = 0; i <= 8; i++)
        {
            double t = i / 8.0 * 2 * Math.PI;
            Assert.Equal(15, moved.PointAt(t).DistanceTo((1, 0, 0)), 12);
        }
        // And the underlying type still reports through, so sampling rules are unaffected.
        Assert.IsType<Circle3d>(moved.Underlying);
    }

    // ---- the refusals, each with its own reason ------------------------------

    [Fact]
    public void AShearIsRefusedByName()
    {
        var shear = new Matrix4d(
            1, 0.3, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);
        var exception = Assert.Throws<NotSupportedException>(() => Box().Transformed(shear));
        Assert.Contains("rigid", exception.Message);
        Assert.Contains("elliptic cylinder", exception.Message);
    }

    [Fact]
    public void ANonUniformScaleIsRefusedByName()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => Box().Transformed(Matrix4d.CreateScale((2, 1, 1))));
        Assert.Contains("rigid", exception.Message);
    }

    [Fact]
    public void AUniformScaleIsRefusedForTheDOMAINReasonNotAGeometryOne()
    {
        // The refusal worth stating: a uniform scale keeps every surface in its own family,
        // so it LOOKS admissible. What fails is bookkeeping — PolylineCurve3d is
        // parameterized by cumulative chord length, so scaling its points scales its domain,
        // while the edges that use it store their trim domains separately.
        var exception = Assert.Throws<NotSupportedException>(
            () => Box().Transformed(Matrix4d.CreateScale(2)));
        Assert.Contains("scales by 2", exception.Message);
        Assert.Contains("PolylineCurve3d", exception.Message);
    }

    [Fact]
    public void AReflectionIsRefusedAndPointsAtShapeMirror()
    {
        var mirror = Matrix4d.CreateScale((1, 1, -1));
        var exception = Assert.Throws<NotSupportedException>(() => Box().Transformed(mirror));
        Assert.Contains("Shape.Mirror", exception.Message);
    }

    [Fact]
    public void TheRefusalFiresBeforeAnythingIsBuilt()
    {
        // All-or-nothing, the rim-surgery rule: a refusal must not leave a half-built graph
        // or touch the input.
        var box = Box();
        var positions = box.Vertices.Select(v => v.Position).ToList();
        Assert.Throws<NotSupportedException>(() => box.Transformed(Matrix4d.CreateScale(3)));
        Assert.All(box.Vertices.Zip(positions), pair =>
            Assert.Equal(0, pair.First.Position.DistanceTo(pair.Second), 12));
        box.Validate();
    }
}
