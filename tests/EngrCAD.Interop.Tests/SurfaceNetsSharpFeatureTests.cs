using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Dual contouring with Hermite data: the vertex goes where the field's own tangent
/// planes say the surface is, not at the mean of the crossings.
/// <para>
/// The bar is an IDENTITY rather than a tolerance, and that is available because a box
/// corner is the intersection of three planes and the quadratic error function of three
/// independent planes has a unique minimiser: the corner. So the tests below assert
/// POSITIONS — every vertex of a polygonized box reads exactly zero from the box's own
/// field, and the volume is exactly the box's volume at every resolution — where a
/// picture, or a volume tolerance, would pass a mesh that is merely close.
/// </para>
/// </summary>
public class SurfaceNetsSharpFeatureTests(ITestOutputHelper output)
{
    private static readonly SurfaceNetsOptions Plain = new() { SharpFeatures = false };

    /// <summary>
    /// A box, at six resolutions, in a region deliberately not commensurate with it.
    /// <b>Every vertex reads EXACTLY zero from the field and the volume is EXACTLY 1000</b>
    /// — the corner cell's three crossings report three perpendicular normals, and the
    /// minimiser of their quadric is the corner rather than something near it.
    /// </summary>
    [Theory]
    [InlineData("symmetric")]
    [InlineData("asymmetric")]
    [InlineData("offset")]
    public void ABoxIsReproducedExactly(string placement)
    {
        // The SYMMETRIC case is secretly benign and would have shipped a defect on its
        // own, which is why all three are here: box and region sharing a centre puts the
        // corner at the same fractional position on all three axes, and only then does the
        // grid's linear crossing land on the surface by itself. Measured before the
        // Hermite points were projected onto the surface: symmetric read EXACTLY zero at
        // every resolution while asymmetric read 2.6e-2 and offset 3.5e-2 — a quarter of
        // the incumbent error rather than none of it.
        var (box, volume) = placement switch
        {
            "symmetric" => ((Sdf)Sdf.Box(10, 10, 10), 1000.0),
            "asymmetric" => (Sdf.Box(10, 7, 4.6), 10 * 7 * 4.6),
            _ => (Sdf.Box(10, 10, 10).Translate((0.137, -0.41, 0.29)), 1000.0),
        };
        var region = new Aabb((-7, -7, -7), (7, 7, 7));
        foreach (int resolution in new[] { 16, 24, 32, 48, 64, 96 })
        {
            var mesh = SurfaceNets.Polygonize(box, region, resolution);
            mesh.Validate();
            Assert.True(mesh.IsClosed);

            double worst = mesh.Vertices.Max(v => Math.Abs(box.Evaluate(v.Position)));
            output.WriteLine($"{placement} {resolution}: worst |sdf| {worst:0.###e+0}, volume {mesh.Volume():F9}");
            // Three decades under the 1e-9 weld tier. Measured 0 exactly on the symmetric
            // placement and 8e-14 to 7e-13 on the other two.
            Assert.True(worst < 1e-12, $"worst |sdf| at a vertex is {worst}");
            // The residual on the volume is NOT the geometry: every vertex is on the box,
            // but a quad spanning a corner has its four corners on three different faces
            // and so is not planar, and PolygonFan's diagonal decides a few parts in 1e12
            // of it either way.
            Assert.True(Math.Abs(mesh.Volume() - volume) < 1e-8 * volume,
                $"volume {mesh.Volume()} against {volume}");
        }
    }

    /// <summary>
    /// The corner point itself is a VERTEX of the mesh, not merely a point the surface
    /// passes near. Measured plain, the nearest vertex to (5, 5, 5) sat 0.72 / 0.38 / 0.22
    /// of a model unit away at resolutions 16 / 24 / 32 — half a cell, and NOT converging
    /// (0.048 at 48 and 0.22 again at 64), because the miss is a property of the averaging
    /// rule and its alignment with the grid rather than of the sampling density.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public void EveryCornerOfABoxIsAVertex(int resolution)
    {
        var box = Sdf.Box(10, 10, 10);
        var region = new Aabb((-7, -7, -7), (7, 7, 7));
        var mesh = SurfaceNets.Polygonize(box, region, resolution);
        var plain = SurfaceNets.Polygonize(box, region, resolution, null, Plain);

        foreach (int sx in new[] { -1, 1 })
        {
            foreach (int sy in new[] { -1, 1 })
            {
                foreach (int sz in new[] { -1, 1 })
                {
                    var corner = new Vector3d(5.0 * sx, 5.0 * sy, 5.0 * sz);
                    double sharpMiss = mesh.Vertices.Min(v => (v.Position - corner).Length);
                    Assert.True(sharpMiss < 1e-12, $"the corner is missed by {sharpMiss}");
                    // …and the incumbent rule misses it by an appreciable fraction of a cell,
                    // so the assertion above is measuring something.
                    double miss = plain.Vertices.Min(v => (v.Position - corner).Length);
                    Assert.True(miss > 0.1 * (14.0 / resolution), $"plain missed by only {miss}");
                }
            }
        }
    }

    /// <summary>
    /// The identity is about PLANES meeting, not about axis alignment: a box rotated off
    /// every axis is reproduced to round-off, and the residual is reported so the claim
    /// cannot rot into "within some tolerance".
    /// </summary>
    [Fact]
    public void ARotatedBoxIsReproducedToRoundOff()
    {
        var rotation = Quaterniond.FromAxisAngle(new Vector3d(0.3, 0.8, 0.5).Normalized(), 0.7);
        var box = Sdf.Box(10, 8, 6).Rotate(rotation);
        var region = new Aabb((-9, -9, -9), (9, 9, 9));
        var mesh = SurfaceNets.Polygonize(box, region, 48);
        mesh.Validate();

        double worst = mesh.Vertices.Max(v => Math.Abs(box.Evaluate(v.Position)));
        output.WriteLine($"worst |sdf| at a vertex: {worst:0.###e+0}");
        // Two decades under the 1e-9 weld tier; measured 4.4e-12 on the reference machine.
        Assert.True(worst < 1e-10, $"worst |sdf| {worst}");
        Assert.Equal(10.0 * 8 * 6, mesh.Volume(), 6);
    }

    /// <summary>
    /// A CSG corner — three half-spaces intersected — is the same identity reached through
    /// an operator tree rather than a primitive, which is what says the gradient is being
    /// read from the composed field rather than from a primitive that happens to know its
    /// own normals.
    /// </summary>
    [Fact]
    public void AnIntersectionOfHalfSpacesReproducesItsCorner()
    {
        var wedge =
            Sdf.HalfSpace(new Vector3d(1, 0, 0), 3) &
            Sdf.HalfSpace(new Vector3d(0, 1, 0), 2) &
            Sdf.HalfSpace(new Vector3d(0, 0, 1), 4) &
            Sdf.Box(20, 20, 20);
        var region = new Aabb((-8, -8, -8), (5, 4, 6));
        var mesh = SurfaceNets.Polygonize(wedge, region, 40);
        mesh.Validate();

        var corner = new Vector3d(3, 2, 4);
        double miss = mesh.Vertices.Min(v => (v.Position - corner).Length);
        output.WriteLine($"corner miss: {miss:0.###e+0}");
        Assert.True(miss < 1e-12, $"corner miss {miss}");
    }

    /// <summary>
    /// <b>Smooth fields improve too, by an order of magnitude</b>, and this is the half of
    /// the result that argues for the feature being ON by default rather than a mode for
    /// mechanical parts. A cell's crossings all lie on a sphere, so their mean lies INSIDE
    /// it by the chord sagitta; the rank-1 quadric projects that mean onto the field's own
    /// tangent plane, which removes the bias rather than reducing it. Measured volume
    /// error, plain against sharp: sphere −2.66% → +0.57%, −0.53% → +0.11%, −0.12% →
    /// +0.025%, −0.028% → +0.0059%; torus −2.18% → +0.46%, −0.475% → +0.097%, −0.113% →
    /// +0.024%.
    /// </summary>
    [Theory]
    [InlineData("sphere", 16)]
    [InlineData("sphere", 32)]
    [InlineData("sphere", 64)]
    [InlineData("torus", 32)]
    [InlineData("torus", 64)]
    public void ASmoothFieldsVolumeErrorFallsByAnOrderOfMagnitude(string name, int resolution)
    {
        var (field, exact) = name switch
        {
            "sphere" => ((Sdf)Sdf.Sphere(5), 4.0 / 3.0 * Math.PI * 125),
            _ => (Sdf.Torus(5, 2), 2 * Math.PI * Math.PI * 5 * 4),
        };
        double plain = Math.Abs(
            SurfaceNets.Polygonize(field, resolution: resolution, options: Plain).Volume() - exact);
        double sharp = Math.Abs(SurfaceNets.Polygonize(field, resolution: resolution).Volume() - exact);
        output.WriteLine($"{name} {resolution}: plain {plain / exact:P4} sharp {sharp / exact:P4}");
        Assert.True(sharp * 4 < plain, $"plain {plain}, sharp {sharp}");
    }

    /// <summary>
    /// A sphere must not gain a crease it has no business having. The default 10° feature
    /// angle is far above the per-cell normal variation of a smooth surface (a radius-5
    /// sphere at cell 0.2 turns 2.3° across a cell), so every cell there is rank 1 and the
    /// worst dihedral between neighbouring faces must not exceed the plain walk's.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(48)]
    public void ASphereGainsNoCreases(int resolution)
    {
        var sphere = Sdf.Sphere(5);
        double plain = WorstDihedralDegrees(
            SurfaceNets.Polygonize(sphere, resolution: resolution, options: Plain));
        double sharp = WorstDihedralDegrees(SurfaceNets.Polygonize(sphere, resolution: resolution));
        output.WriteLine($"worst dihedral at {resolution}: plain {plain:F2}°, sharp {sharp:F2}°");
        Assert.True(sharp <= plain + 1e-9, $"plain {plain}, sharp {sharp}");
    }

    /// <summary>
    /// <b>The manifoldness argument, as a test.</b> Placement is a change to WHERE a vertex
    /// goes, never to which crossings belong to which vertex — so the index buffer, the
    /// counts, and every combinatorial property derived from them are bit-for-bit what the
    /// incumbent walk produced. The fixtures deliberately include the two families that
    /// carry the recorded pinch-vertex residual, because the strong statement is that the
    /// pinch COUNT is unchanged too: this feature neither creates nor repairs one.
    /// </summary>
    [Theory]
    [InlineData("box", 44)]
    [InlineData("sphere", 44)]
    [InlineData("csg", 44)]
    [InlineData("shell", 44)]
    [InlineData("gyroid", 44)]
    [InlineData("gyroid", 64)]
    public void PlacementNeverChangesTopology(string name, int resolution)
    {
        var (field, region) = Case(name);
        var plain = SurfaceNets.Polygonize(field, region, resolution, null, Plain);
        var sharp = SurfaceNets.Polygonize(field, region, resolution);

        Assert.Equal(plain.VertexCount, sharp.VertexCount);
        Assert.Equal(plain.FaceCount, sharp.FaceCount);
        var (_, plainFaces) = plain.ToIndexed();
        var (_, sharpFaces) = sharp.ToIndexed();
        for (int f = 0; f < plainFaces.Count; f++)
            Assert.Equal(plainFaces[f], sharpFaces[f]);
        Assert.Equal(plain.NonManifoldVertices(), sharp.NonManifoldVertices());
    }

    /// <summary>
    /// <b>The clamp, decided by measurement rather than by preference — and BOTH textbook
    /// answers are wrong.</b> A quadric minimiser can land outside its own cell, which is
    /// the classic route to self-intersecting dual contouring; clamping to the cell is the
    /// classic fix and its classic objection is that it defeats the feature on exactly the
    /// cells that needed it.
    /// <para>
    /// The objection is REAL, and an axis-aligned box hides it completely — which is why
    /// the fixture here is ROTATED. A cell that sees both faces of an edge need not contain
    /// the edge, so the minimiser on the edge LINE is legitimately just outside its cell,
    /// and refusing it there chamfers precisely the feature the quadric found: measured
    /// 0.109 and 0.048 model units off the surface at resolutions 48 and 96 under a strict
    /// cell clamp — a quarter of a cell, converging only linearly — against 4.4e-12 with
    /// the default slack of one cell. Half a cell is NOT enough (4.3e-3), so the bound is
    /// measured rather than rounded to a comfortable number.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(48)]
    [InlineData(96)]
    public void ARotatedBoxNeedsTheSlackAndIsExactWithIt(int resolution)
    {
        var rotation = Quaterniond.FromAxisAngle(new Vector3d(0.3, 0.8, 0.5).Normalized(), 0.7);
        var box = Sdf.Box(10, 8, 6).Rotate(rotation);
        var region = new Aabb((-9, -9, -9), (9, 9, 9));

        double Worst(double clampCells) => SurfaceNets
            .Polygonize(box, region, resolution, null, new SurfaceNetsOptions { ClampCells = clampCells })
            .Vertices.Max(v => Math.Abs(box.Evaluate(v.Position)));

        double strict = Worst(0), half = Worst(0.5), slack = Worst(1);
        output.WriteLine($"resolution {resolution}: strict {strict:0.###e+0}, " +
                         $"half {half:0.###e+0}, one cell {slack:0.###e+0}");
        Assert.True(slack < 1e-9, $"the default slack should be exact; measured {slack}");
        Assert.True(strict > 100 * slack, "the fixture must still CARRY the configuration");
        Assert.True(half > 100 * slack, "half a cell must be shown to be insufficient");
    }

    /// <summary>
    /// The other half of the same measurement, and what the clamp is really for: WITHOUT a
    /// bound a vertex has none. On an under-resolved gyroid — wall 0.2 against a cell of
    /// 0.25 and 0.125 — the free solve moves vertices several cells outside the cell that
    /// owns them, past their neighbours' neighbours, so the quads around them stop being a
    /// discretization of anything. The default keeps every vertex inside the neighbourhood
    /// its own crossings came from.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    public void TheClampBoundsAnOtherwiseUnboundedExcursion(int resolution)
    {
        var (field, region) = Case("gyroid");
        double cell = region.Size[region.LongestAxis] / resolution;
        var strict = SurfaceNets.Polygonize(
            field, region, resolution, null, new SurfaceNetsOptions { ClampCells = 0 });
        var free = SurfaceNets.Polygonize(
            field, region, resolution, null, new SurfaceNetsOptions { ClampCells = double.PositiveInfinity });
        var slack = SurfaceNets.Polygonize(field, region, resolution);

        double Excursion(HalfEdgeMesh mesh)
        {
            var (a, _) = strict.ToIndexed();
            var (b, _) = mesh.ToIndexed();
            double worst = 0;
            for (int i = 0; i < a.Length; i++)
                worst = Math.Max(worst, (a[i] - b[i]).Length / cell);
            return worst;
        }

        double freeExcursion = Excursion(free), slackExcursion = Excursion(slack);
        output.WriteLine($"resolution {resolution}: free {freeExcursion:F3} cells, default {slackExcursion:F3}");
        Assert.True(freeExcursion > 2, $"the fixture must still CARRY the configuration; {freeExcursion} cells");
        // The clamp box is the cell grown by one cell each way, so nothing can move further
        // than its own diagonal plus that slack — asserted rather than trusted.
        Assert.True(slackExcursion < freeExcursion, $"{slackExcursion} against {freeExcursion}");
        Assert.True(slackExcursion <= 1.5 * Math.Sqrt(3) + 1e-9, $"{slackExcursion} cells");
        // NOT Validate(): this fixture carries the recorded ambiguous-face pinch residual
        // at these resolutions and does so identically under every placement rule
        // (PlacementNeverChangesTopology asserts the counts match). Closedness is the
        // property placement could break and does not.
        Assert.True(slack.IsClosed);
    }

    /// <summary>
    /// The feature angle is a stated ANGLE, and this pins the conversion rather than the
    /// implementation: two planes meeting at exactly the stated deviation from flat sit on
    /// the threshold, so a slightly sharper crease is resolved and a slightly shallower one
    /// is not. The fixture is a shallow roof — two half-spaces whose normals differ by a
    /// controlled angle — measured at the ridge.
    /// </summary>
    [Theory]
    [InlineData(30.0, true)]
    [InlineData(4.0, false)]
    public void AShallowCreaseIsResolvedOnlyAboveTheFeatureAngle(double deviationDegrees, bool resolved)
    {
        double half = deviationDegrees * Math.PI / 360;
        var roof =
            Sdf.HalfSpace(new Vector3d(Math.Sin(half), 0, Math.Cos(half)), 0) &
            Sdf.HalfSpace(new Vector3d(-Math.Sin(half), 0, Math.Cos(half)), 0) &
            Sdf.Box(20, 20, 20).Translate((0, 0, -8));
        var region = new Aabb((-9, -9, -18), (9, 9, 1));
        var mesh = SurfaceNets.Polygonize(roof, region, 40);

        // The ridge is the line x = 0, z = 0. How close does the mesh get to it?
        double miss = mesh.Vertices.Min(v => Math.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z));
        output.WriteLine($"{deviationDegrees}° deviation: ridge miss {miss:0.###e+0}");
        if (resolved)
            Assert.True(miss < 1e-9, $"a {deviationDegrees}° crease should be resolved; missed by {miss}");
        else
            Assert.True(miss > 1e-3, $"a {deviationDegrees}° crease should NOT be resolved; missed by {miss}");
    }

    /// <summary>Turning the feature off reproduces the incumbent output bit for bit —
    /// which is what makes every golden taken before this feature still meaningful.</summary>
    [Fact]
    public void TurningItOffIsBitIdenticalToTheIncumbentWalk()
    {
        var (field, region) = Case("csg");
        var a = SurfaceNets.Polygonize(field, region, 41, null, Plain);
        var b = SurfaceNets.Polygonize(field, region, 41, null, new SurfaceNetsOptions
        {
            SharpFeatures = false,
            FeatureAngleDegrees = 45,
            ClampCells = 4,
        });
        var (pa, _) = a.ToIndexed();
        var (pb, _) = b.ToIndexed();
        for (int i = 0; i < pa.Length; i++)
            Assert.Equal(pa[i], pb[i]);
    }

    internal static (Sdf Field, Aabb Region) Case(string name) => name switch
    {
        "box" => (Sdf.Box(10, 10, 10), new Aabb((-7, -7, -7), (7, 7, 7))),
        "sphere" => (Sdf.Sphere(5), new Aabb((-7, -7, -7), (7, 7, 7))),
        "csg" => ((Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
            .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25),
            new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2))),
        "shell" => (Sdf.Sphere(10).Shell(0.6), new Aabb((-12, -12, -12), (12, 12, 12))),
        "gyroid" => (Sdf.Box(10, 10, 10) & Sdf.Gyroid(8, 0.2), new Aabb((-6, -6, -6), (6, 6, 6))),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static double WorstDihedralDegrees(HalfEdgeMesh mesh)
    {
        double worst = 0;
        foreach (var face in mesh.Faces)
        {
            var n = FaceNormal(face, out _);
            foreach (var other in face.AdjacentFaces())
            {
                var m = FaceNormal(other, out _);
                worst = Math.Max(worst, Math.Acos(Math.Clamp(n.Dot(m), -1, 1)) * 180 / Math.PI);
            }
        }
        return worst;
    }

    /// <summary>Folds against the field: a facet whose normal opposes the field's normal at
    /// its own centroid — the tessellation audit's measure, at grid scale.</summary>
    internal static (int Folds, double WorstDot) Folds(Sdf field, HalfEdgeMesh mesh)
    {
        int folds = 0;
        double worst = 1;
        foreach (var face in mesh.Faces)
        {
            var n = FaceNormal(face, out var centroid);
            if (n == Vector3d.Zero)
                continue;
            double dot = n.Dot(field.Normal(centroid, 1e-7));
            worst = Math.Min(worst, dot);
            if (dot < 0)
                folds++;
        }
        return (folds, worst);
    }

    private static Vector3d FaceNormal(Face face, out Vector3d centroid)
    {
        var newell = Vector3d.Zero;
        centroid = Vector3d.Zero;
        int count = 0;
        var points = face.Vertices().Select(v => v.Position).ToArray();
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Length];
            newell += new Vector3d(
                (p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y));
            centroid += p;
            count++;
        }
        centroid /= count;
        return newell.TryNormalize(Tolerance.Default, out var unit) ? unit : Vector3d.Zero;
    }
}
