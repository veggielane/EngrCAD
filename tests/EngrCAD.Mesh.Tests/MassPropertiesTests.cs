using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// Mass properties against closed-form ground truth. Polyhedral bodies (boxes, prisms,
/// tetrahedra) must agree to round-off — the divergence-theorem sum is exact for them —
/// while tessellated curved bodies (sphere, cylinder) are checked for the analytic limit
/// AND for the O(h²) convergence rate that justifies the tolerance.
/// </summary>
public class MassPropertiesTests
{
    private static void AssertClose(double expected, double actual, double relative, string what)
    {
        double scale = Math.Max(Math.Abs(expected), 1e-300);
        Assert.True(Math.Abs(actual - expected) <= relative * scale,
            $"{what}: expected {expected:G17}, got {actual:G17} (relative error {Math.Abs(actual - expected) / scale:G3} > {relative:G3}).");
    }

    private static void AssertClose(in Vector3d expected, in Vector3d actual, double absolute, string what) =>
        Assert.True(expected.DistanceTo(actual) <= absolute,
            $"{what}: expected {expected}, got {actual} (distance {expected.DistanceTo(actual):G3} > {absolute:G3}).");

    private static void AssertTensorClose(in SymmetricTensor3 expected, in SymmetricTensor3 actual, double relative, string what)
    {
        double scale = Math.Max(Math.Max(Math.Abs(expected.Xx), Math.Abs(expected.Yy)), Math.Abs(expected.Zz));
        scale = Math.Max(scale, 1e-300);
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                Assert.True(Math.Abs(actual[r, c] - expected[r, c]) <= relative * scale,
                    $"{what}[{r},{c}]: expected {expected[r, c]:G17}, got {actual[r, c]:G17} " +
                    $"(error {Math.Abs(actual[r, c] - expected[r, c]):G3}, scale {scale:G3}).");
            }
        }
    }

    // ---- boxes: everything exact ----

    [Fact]
    public void Box_VolumeAreaCentroidAndInertia_MatchClosedForm()
    {
        const double a = 3, b = 5, c = 7, density = 2.5;
        var mesh = MeshPrimitives.Box(a, b, c);
        var mp = mesh.MassProperties(density);

        double volume = a * b * c;
        double mass = density * volume;
        AssertClose(volume, mp.Volume, 1e-15, "volume");
        AssertClose(2 * (a * b + b * c + c * a), mp.SurfaceArea, 1e-15, "area");
        AssertClose(mass, mp.Mass, 1e-15, "mass");
        AssertClose(Vector3d.Zero, mp.Centroid, 1e-14, "centroid");

        var expected = new SymmetricTensor3(
            mass * (b * b + c * c) / 12,
            mass * (a * a + c * c) / 12,
            mass * (a * a + b * b) / 12,
            0, 0, 0);
        AssertTensorClose(expected, mp.Inertia, 1e-14, "inertia");
    }

    [Fact]
    public void Cube_Inertia_IsIsotropic()
    {
        var mp = MeshPrimitives.Box(4, 4, 4).MassProperties();
        double expected = mp.Mass * (16 + 16) / 12.0;
        AssertTensorClose(new SymmetricTensor3(expected, expected, expected, 0, 0, 0), mp.Inertia, 1e-14, "cube inertia");
    }

    [Fact]
    public void Box_OffOrigin_CentroidFollowsAndInertiaAboutCentroidDoesNot()
    {
        const double a = 3, b = 5, c = 7;
        var offset = new Vector3d(11, -13, 17);
        var centered = MeshPrimitives.Box(a, b, c);
        var moved = centered.Transformed(Matrix4d.CreateTranslation(offset));

        var mpCentered = centered.MassProperties();
        var mpMoved = moved.MassProperties();

        AssertClose(offset, mpMoved.Centroid, 1e-13, "moved centroid");
        AssertTensorClose(mpCentered.Inertia, mpMoved.Inertia, 1e-13, "centroidal inertia is translation invariant");
    }

    [Fact]
    public void ParallelAxisTheorem_MatchesDirectRecomputation()
    {
        const double a = 3, b = 5, c = 7;
        var point = new Vector3d(9, -4, 6);
        var mesh = MeshPrimitives.Box(a, b, c);
        var mp = mesh.MassProperties(2.5);

        // Direct: move the body so the query point sits at the origin, then take the
        // inertia about the origin the long way (via the same parallel-axis step from a
        // freshly computed centroid, which is now offset).
        var moved = mesh.Transformed(Matrix4d.CreateTranslation(-point));
        var direct = moved.MassProperties(2.5).InertiaAbout(Vector3d.Zero);

        AssertTensorClose(direct, mp.InertiaAbout(point), 1e-13, "parallel-axis inertia");

        // And against the closed form for a box about a corner-offset point.
        double mass = 2.5 * a * b * c;
        var d = point;   // centroid is the origin
        var expected =
            new SymmetricTensor3(mass * (b * b + c * c) / 12, mass * (a * a + c * c) / 12, mass * (a * a + b * b) / 12, 0, 0, 0)
            + mass * (d.LengthSquared * SymmetricTensor3.Identity - SymmetricTensor3.OuterProduct(d));
        AssertTensorClose(expected, mp.InertiaAbout(point), 1e-13, "closed-form shifted inertia");
    }

    [Fact]
    public void Tetrahedron_MatchesSimplexClosedForm()
    {
        const double a = 2, b = 3, c = 5;
        // Corner tetrahedron (0,0,0), (a,0,0), (0,b,0), (0,0,c) — outward winding.
        var mesh = HalfEdgeMesh.Build(
            [(0, 0, 0), (a, 0, 0), (0, b, 0), (0, 0, c)],
            [new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 0, 3, 2 }]);

        var mp = mesh.MassProperties();
        double volume = a * b * c / 6;
        AssertClose(volume, mp.Volume, 1e-15, "tetrahedron volume");
        AssertClose(new Vector3d(a / 4, b / 4, c / 4), mp.Centroid, 1e-14, "tetrahedron centroid");

        // Second moments about the ORIGIN: ∫x² dV = V·a²/10, ∫xy dV = V·ab/20.
        var aboutOrigin = mp.SecondMoment + mp.Volume * SymmetricTensor3.OuterProduct(mp.Centroid);
        var expected = new SymmetricTensor3(
            volume * a * a / 10, volume * b * b / 10, volume * c * c / 10,
            volume * a * b / 20, volume * a * c / 20, volume * b * c / 20);
        AssertTensorClose(expected, aboutOrigin, 1e-14, "tetrahedron second moment about origin");
    }

    /// <summary>An L-shaped prism: concave top/bottom faces, so Newell area is right and a
    /// sum of fan-triangle areas would not be.</summary>
    [Fact]
    public void ConcavePrism_VolumeAndAreaMatchClosedForm()
    {
        const double h = 4;
        // L: (0,0) (6,0) (6,2) (2,2) (2,5) (0,5) — area 6·2 + 2·3 = 18, perimeter 22.
        Vector3d[] profile = [(0, 0, 0), (6, 0, 0), (6, 2, 0), (2, 2, 0), (2, 5, 0), (0, 5, 0)];
        var positions = new List<Vector3d>();
        positions.AddRange(profile);
        foreach (var p in profile)
            positions.Add(new Vector3d(p.X, p.Y, h));

        var faces = new List<int[]>
        {
            new[] { 5, 4, 3, 2, 1, 0 },
            new[] { 6, 7, 8, 9, 10, 11 },
        };
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            faces.Add([i, j, j + 6, i + 6]);
        }
        var mesh = HalfEdgeMesh.Build(positions, faces);
        mesh.Validate();
        Assert.True(mesh.IsClosed);

        var mp = mesh.MassProperties();
        const double profileArea = 18, perimeter = 22;
        AssertClose(profileArea * h, mp.Volume, 1e-14, "L-prism volume");
        AssertClose(2 * profileArea + perimeter * h, mp.SurfaceArea, 1e-14, "L-prism area");

        // Centroid: the L is two rectangles (6×2 at (3,1)) and (2×3 at (1,3.5)).
        double cx = (12 * 3 + 6 * 1) / 18.0;
        double cy = (12 * 1 + 6 * 3.5) / 18.0;
        AssertClose(new Vector3d(cx, cy, h / 2), mp.Centroid, 1e-13, "L-prism centroid");
    }

    // ---- the far-from-origin cancellation lesson ----

    [Fact]
    public void BodyFarFromOrigin_KeepsFullPrecision_AndTheNaiveOriginSumDoesNot()
    {
        const double s = 10;
        var far = new Vector3d(1e6, 2e6, 3e6);
        // Rotated first, so the coordinates are generic doubles: an axis-aligned box at a
        // round offset has integer coordinates whose products happen to cancel exactly,
        // which hides the very effect this test exists to pin.
        var mesh = MeshPrimitives.Box(s, s, s)
            .Transformed(Matrix4d.CreateFromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.6))
            .Transformed(Matrix4d.CreateTranslation(far));

        var mp = mesh.MassProperties();
        // 1e-9 rather than 1e-14: the vertex coordinates themselves are only good to
        // ulp(3e6) ≈ 5e-10 after the pose, which is ~5e-11 of a 10-unit edge.
        AssertClose(s * s * s, mp.Volume, 1e-9, "far-away box volume");
        AssertClose(far, mp.Centroid, 1e-6, "far-away box centroid");

        // The same sum taken about the world origin loses everything: the terms are ~1e18
        // and cancel down to 6000.
        var naive = new MassPropertyIntegrator(Vector3d.Zero);
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            var loop = mesh.GetFace(f).Vertices().Select(v => v.Position).ToArray();
            for (int i = 1; i + 1 < loop.Length; i++)
                naive.AddTriangle(loop[0], loop[i], loop[i + 1]);
        }
        double exact = s * s * s;
        double naiveError = Math.Abs(naive.SignedVolume - exact) / exact;
        double recentredError = Math.Abs(mp.Volume - exact) / exact;
        // Measured on this box: ~6.5e-7 relative from the origin against ~1e-11 recentred,
        // i.e. five orders of magnitude, and it worsens with (distance / size).
        Assert.True(naiveError > 1e4 * Math.Max(recentredError, 1e-15),
            $"Expected the origin-referenced sum to lose precision on a body at 1e6: naive relative error " +
            $"{naiveError:G3}, recentred {recentredError:G3}. If this ever passes, the recentring lesson has " +
            "been invalidated (or doubles got wider).");
    }

    // ---- curved bodies: analytic limit plus the convergence rate ----

    [Fact]
    public void Sphere_ApproachesClosedFormAndConvergesQuadratically()
    {
        const double r = 3, density = 1.7;
        double exactVolume = 4.0 / 3.0 * Math.PI * r * r * r;
        double exactArea = 4 * Math.PI * r * r;

        var coarse = MeshPrimitives.UvSphere(r, 64, 32).MassProperties(density);
        var fine = MeshPrimitives.UvSphere(r, 128, 64).MassProperties(density);
        var finest = MeshPrimitives.UvSphere(r, 256, 128).MassProperties(density);

        double coarseError = Math.Abs(coarse.Volume - exactVolume) / exactVolume;
        double fineError = Math.Abs(fine.Volume - exactVolume) / exactVolume;
        // Inscribed polyhedra under-estimate; doubling the resolution must quarter the error.
        Assert.True(coarse.Volume < exactVolume && fine.Volume < exactVolume, "Inscribed spheres must under-estimate.");
        double ratio = coarseError / fineError;
        Assert.True(ratio > 3.5 && ratio < 4.5, $"Volume error ratio {ratio:G3} is not the expected ~4 (O(h²)).");
        Assert.True(Math.Abs(finest.Volume - exactVolume) / exactVolume < 3e-4,
            $"256x128 sphere volume error {Math.Abs(finest.Volume - exactVolume) / exactVolume:G3} exceeds 3e-4.");

        AssertClose(exactArea, finest.SurfaceArea, 3e-4, "sphere area");
        AssertClose(Vector3d.Zero, finest.Centroid, 1e-12, "sphere centroid");

        // Solid sphere: I = 2/5 m r², isotropic.
        double expected = 0.4 * finest.Mass * r * r;
        AssertTensorClose(new SymmetricTensor3(expected, expected, expected, 0, 0, 0), finest.Inertia, 1e-3, "sphere inertia");
        // Isotropy itself is far tighter than the discretization error.
        AssertClose(finest.Inertia.Xx, finest.Inertia.Zz, 1e-3, "sphere Ixx vs Izz");
    }

    [Theory]
    [InlineData(0)]   // cylinder axis along Z (as built)
    [InlineData(1)]   // rotated onto X
    [InlineData(2)]   // rotated onto Y
    public void Cylinder_MatchesClosedFormAboutEachAxis(int orientation)
    {
        const double r = 2, h = 9, density = 0.8;
        // MeshPrimitives.Cylinder runs from z = 0 to z = h; centre it so the closed-form
        // centroidal inertia applies directly, then swing the axis onto X or Y.
        var mesh = MeshPrimitives.Cylinder(r, h, 512)
            .Transformed(Matrix4d.CreateTranslation((0, 0, -h / 2)));
        var transform = orientation switch
        {
            1 => Matrix4d.CreateRotationY(Math.PI / 2),
            2 => Matrix4d.CreateRotationX(Math.PI / 2),
            _ => Matrix4d.Identity,
        };
        var mp = mesh.Transformed(transform).MassProperties(density);

        double exactVolume = Math.PI * r * r * h;
        AssertClose(exactVolume, mp.Volume, 5e-5, "cylinder volume");

        double mass = density * exactVolume;
        double axial = 0.5 * mass * r * r;
        double transverse = mass * (3 * r * r + h * h) / 12;
        AssertClose(Vector3d.Zero, mp.Centroid, 1e-12, "cylinder centroid");

        var expected = orientation switch
        {
            1 => new SymmetricTensor3(axial, transverse, transverse, 0, 0, 0),
            2 => new SymmetricTensor3(transverse, axial, transverse, 0, 0, 0),
            _ => new SymmetricTensor3(transverse, transverse, axial, 0, 0, 0),
        };
        AssertTensorClose(expected, mp.Inertia, 2e-4, "cylinder inertia");
    }

    // ---- transforms, principal axes, combination ----

    [Fact]
    public void Transformed_MatchesRecomputationOnTheTransformedMesh()
    {
        var mesh = MeshPrimitives.Box(3, 5, 7);
        var transform =
            Matrix4d.CreateTranslation((4, -2, 9)) *
            Matrix4d.CreateFromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.7) *
            Matrix4d.CreateScale(2.5);

        var posed = mesh.Transformed(transform).MassProperties(1.3);
        var predicted = mesh.MassProperties(1.3).Transformed(transform);

        AssertClose(posed.Volume, predicted.Volume, 1e-13, "posed volume");
        AssertClose(posed.SurfaceArea, predicted.SurfaceArea, 1e-13, "posed area");
        AssertClose(posed.Centroid, predicted.Centroid, 1e-11, "posed centroid");
        AssertTensorClose(posed.Inertia, predicted.Inertia, 1e-12, "posed inertia");
    }

    [Fact]
    public void Transformed_RejectsNonUniformScale()
    {
        var mp = MeshPrimitives.Box(1, 1, 1).MassProperties();
        var ex = Assert.Throws<ArgumentException>(() => mp.Transformed(Matrix4d.CreateScale((1, 2, 3))));
        Assert.Contains("similarity", ex.Message);
    }

    [Fact]
    public void Transformed_AcceptsMirroring()
    {
        var mesh = MeshPrimitives.Box(3, 5, 7).Transformed(Matrix4d.CreateTranslation((2, 0, 0)));
        var mirror = Matrix4d.CreateScale((-1, 1, 1));
        var mirrored = mesh.Transformed(mirror).MassProperties();
        var predicted = mesh.MassProperties().Transformed(mirror);

        AssertClose(mirrored.Volume, predicted.Volume, 1e-14, "mirrored volume");
        AssertClose(mirrored.Centroid, predicted.Centroid, 1e-13, "mirrored centroid");
        AssertTensorClose(mirrored.Inertia, predicted.Inertia, 1e-13, "mirrored inertia");
    }

    [Fact]
    public void PrincipalAxes_RecoverTheBoxAxesUnderRotation()
    {
        const double a = 2, b = 6, c = 10;
        var axis = new Vector3d(1, -2, 4).Normalized();
        var rotation = Matrix4d.CreateFromAxisAngle(axis, 1.1);
        var mesh = MeshPrimitives.Box(a, b, c).Transformed(rotation);
        var mp = mesh.MassProperties(3.0);
        var principal = mp.Principal();

        double mass = 3.0 * a * b * c;
        // Ascending: the least moment is about the LONGEST edge direction (local Z here).
        double ix = mass * (b * b + c * c) / 12;
        double iy = mass * (a * a + c * c) / 12;
        double iz = mass * (a * a + b * b) / 12;
        var sorted = new[] { ix, iy, iz };
        Array.Sort(sorted);
        AssertClose(sorted[0], principal.Moments.X, 1e-12, "least principal moment");
        AssertClose(sorted[1], principal.Moments.Y, 1e-12, "middle principal moment");
        AssertClose(sorted[2], principal.Moments.Z, 1e-12, "greatest principal moment");

        // The least-inertia axis is the rotated local Z (the c = 10 direction), up to sign.
        var expectedLeast = rotation.TransformVector(Vector3d.UnitZ);
        Assert.True(Math.Abs(principal.Axes.X.Dot(expectedLeast)) > 1 - 1e-9,
            $"Least-inertia axis {principal.Axes.X} is not the rotated long axis {expectedLeast}.");
        AssertClose(mp.Centroid, principal.Axes.Origin, 1e-12, "principal frame origin");
    }

    [Fact]
    public void PrincipalAxes_DiagonalizeTheInertiaTensor()
    {
        var mesh = MeshPrimitives.Box(2, 6, 10)
            .Transformed(Matrix4d.CreateFromAxisAngle(new Vector3d(3, 1, 2).Normalized(), 0.9));
        var mp = mesh.MassProperties();
        var principal = mp.Principal();
        var frame = principal.Axes;

        // Rᵀ I R must be diagonal with the principal moments on it.
        Span<Vector3d> axes = [frame.X, frame.Y, frame.Z];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double value = axes[i].Dot(mp.Inertia.Multiply(axes[j]));
                double expected = i == j ? principal.Moments[i] : 0;
                Assert.True(Math.Abs(value - expected) <= 1e-9 * Math.Max(principal.Moments.Z, 1),
                    $"Rᵀ I R [{i},{j}] = {value:G6}, expected {expected:G6}.");
            }
        }
    }

    [Fact]
    public void Combine_MatchesOneMeshHoldingBothBodies()
    {
        var boxA = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2)));
        var boxB = MeshPrimitives.Box(new Aabb((10, 3, 1), (14, 4, 6)));

        // One mesh with both components (disjoint, so it is a legal manifold mesh).
        var positions = new List<Vector3d>();
        var faces = new List<int[]>();
        foreach (var part in new[] { boxA, boxB })
        {
            int offset = positions.Count;
            for (int v = 0; v < part.VertexCount; v++)
                positions.Add(part.GetPosition(v));
            for (int f = 0; f < part.FaceCount; f++)
                faces.Add([.. part.GetFace(f).Vertices().Select(v => v.Index + offset)]);
        }
        var both = HalfEdgeMesh.Build(positions, faces);

        var combined = MassProperties.Combine([boxA.MassProperties(4.0), boxB.MassProperties(4.0)]);
        var direct = both.MassProperties(4.0);

        AssertClose(direct.Volume, combined.Volume, 1e-14, "combined volume");
        AssertClose(direct.SurfaceArea, combined.SurfaceArea, 1e-14, "combined area");
        AssertClose(direct.Centroid, combined.Centroid, 1e-12, "combined centroid");
        AssertTensorClose(direct.Inertia, combined.Inertia, 1e-13, "combined inertia");
    }

    [Fact]
    public void Combine_WithMixedDensities_ReportsBulkDensityAndCorrectInertia()
    {
        var a = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2))).MassProperties(1.0);
        var b = MeshPrimitives.Box(new Aabb((10, 0, 0), (12, 2, 2))).MassProperties(9.0);
        var combined = MassProperties.Combine([a, b]);

        AssertClose(16, combined.Volume, 1e-14, "volume");
        AssertClose(8 * 1.0 + 8 * 9.0, combined.Mass, 1e-14, "mass");
        AssertClose(80.0 / 16.0, combined.Density, 1e-14, "bulk density");
        // Centre of mass sits nine tenths of the way toward the heavy block.
        AssertClose(new Vector3d((8 * 1.0 * 1 + 8 * 9.0 * 11) / 80.0, 1, 1), combined.Centroid, 1e-12, "centre of mass");

        // Inertia must equal the explicit parallel-axis sum in mass units.
        var expected =
            a.InertiaAbout(combined.Centroid) + b.InertiaAbout(combined.Centroid);
        AssertTensorClose(expected, combined.Inertia, 1e-13, "mixed-density inertia");
    }

    [Fact]
    public void Density_ScalesMassAndInertiaLinearly_ButNotGeometry()
    {
        var mesh = MeshPrimitives.Box(3, 5, 7);
        var one = mesh.MassProperties(1.0);
        var heavy = mesh.MassProperties(7.5);

        AssertClose(one.Volume, heavy.Volume, 1e-15, "volume is density-free");
        AssertClose(one.Mass * 7.5, heavy.Mass, 1e-14, "mass");
        AssertTensorClose(one.Inertia * 7.5, heavy.Inertia, 1e-14, "inertia");
        AssertTensorClose(heavy.Inertia, one.WithDensity(7.5).Inertia, 1e-15, "WithDensity");
    }

    [Fact]
    public void MatchesTheExistingVolumeAndAreaMethodsExactly()
    {
        foreach (var mesh in new[]
        {
            MeshPrimitives.Box(3, 5, 7),
            MeshPrimitives.UvSphere(2, 24, 12),
            MeshPrimitives.Cylinder(1.5, 4, 17),
            MeshPrimitives.Cone(2, 0.5, 3, 13),
        })
        {
            var mp = mesh.MassProperties();
            AssertClose(mesh.Volume(), mp.Volume, 1e-12, "volume agrees with HalfEdgeMesh.Volume");
            AssertClose(mesh.SurfaceArea(), mp.SurfaceArea, 1e-14, "area agrees with HalfEdgeMesh.SurfaceArea");
        }
    }

    [Fact]
    public void OpenMesh_IsRejectedUnlessTheCallerOptsOut()
    {
        var open = HalfEdgeMesh.Build([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [new[] { 0, 1, 2 }]);
        Assert.Throws<InvalidOperationException>(() => open.MassProperties());
        // Opting out still refuses, because a single triangle encloses nothing.
        Assert.Throws<InvalidOperationException>(() => open.MassProperties(1.0, requireClosed: false));
    }

    [Fact]
    public void InwardWoundMesh_IsRejectedByName()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        var flipped = HalfEdgeMesh.Build(
            [.. Enumerable.Range(0, box.VertexCount).Select(box.GetPosition)],
            Enumerable.Range(0, box.FaceCount)
                .Select(f => box.GetFace(f).Vertices().Select(v => v.Index).Reverse().ToArray()));
        var ex = Assert.Throws<InvalidOperationException>(() => flipped.MassProperties());
        Assert.Contains("inward", ex.Message);
    }
}
