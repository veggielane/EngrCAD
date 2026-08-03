using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The three manufacturability legs, each against a closed form.
///
/// <para>One fixture rule runs through the file: <b>a fresh <see cref="Part"/> per
/// quality</b>. `Part.GetMesh` caches at the FIRST caller's quality, so a convergence
/// loop reusing one part measures the same mesh three times and reports a perfectly
/// flat "convergence" — which is what the first run of these tests did.</para>
/// </summary>
public class ManufacturabilityTests
{
    private static Shape DraftedBlock(double degrees) =>
        Shape.Box(40, 30, 20).Draft(degrees, (0, 0, 0), Vector3d.UnitZ);

    /// <summary>A 45-degree wall facing down and out: the triangle (0,0)-(20,0)-(0,20)
    /// extruded 30 along +Z and laid on its side, so the hypotenuse face's normal is
    /// exactly (1, 0, -1)/sqrt(2).</summary>
    private static Shape FortyFiveDegreeWall() =>
        Shape.Extrude(Sketch.Start(0, 0).LineTo(20, 0).LineTo(0, 20).Close(), 30)
            .Transform(Matrix4d.CreateFromAxisAngle(Vector3d.UnitX, -Math.PI / 2));

    private static Shape Tray(double scale, double wall) =>
        Shape.Box(50 * scale, 40 * scale, 30 * scale)
            .Shell(wall, s => s.Faces.Where(f => f.IsPlanar(out _, out var n) && n.Dot(Vector3d.UnitZ) > 0.9));

    // ------------------------------------------------------------------- draft

    [Fact]
    public void DraftedBlockWallsReadTheDraftedAngleExactly()
    {
        var report = Manufacturability.CheckDraft(
            new Part("block", DraftedBlock(3)), Vector3d.UnitZ, minimumAngleDegrees: 2);

        // Draft.Apply rotates each wall's plane by exactly the angle, so this is an
        // identity up to the asin round-trip -- measured 4.4e-16 of a degree.
        var walls = report.Faces.Where(f => Math.Abs(f.WorstReleaseDegrees) < 45).ToList();
        Assert.Equal(4, walls.Count);
        foreach (var wall in walls)
        {
            Assert.Equal(3.0, wall.WorstReleaseDegrees, 1e-13);
            Assert.Equal(1, wall.Samples);           // planar: one exact normal, nothing sampled
            Assert.False(wall.Sampled);
        }

        // The two caps are square to the pull, one per mould half.
        var caps = report.Faces.Where(f => Math.Abs(f.WorstReleaseDegrees) > 45).ToList();
        Assert.Equal(2, caps.Count);
        Assert.Contains(caps, c => c.WorstReleaseDegrees == 90);
        Assert.Contains(caps, c => c.WorstReleaseDegrees == -90);
    }

    [Fact]
    public void ADraftedBlockPassesItsOwnAngleAndFailsALargerOne()
    {
        var block = DraftedBlock(3);

        var ok = Manufacturability.CheckDraft(new Part("a", block), Vector3d.UnitZ, 2);
        Assert.True(ok.Passes);
        Assert.Empty(ok.Failing);
        Assert.Equal(0, ok.FailingArea);
        Assert.Equal(3.0, ok.WorstReleaseDegrees, 1e-13);

        var strict = Manufacturability.CheckDraft(new Part("b", block), Vector3d.UnitZ, 5);
        Assert.False(strict.Passes);
        Assert.Equal(4, strict.Failing.Count);                  // the four walls, not the caps
        Assert.True(strict.FailingArea > 2800 && strict.FailingArea < 2810);
        Assert.Contains("under 5.00 deg", strict.ToText());
    }

    [Fact]
    public void AnUndraftedBlockReportsEveryWallAtExactlyZero()
    {
        var report = Manufacturability.CheckDraft(
            new Part("plain", Shape.Box(40, 30, 20)), Vector3d.UnitZ, 1);

        Assert.False(report.Passes);
        Assert.Equal(4, report.Failing.Count);
        // Exactly zero, not nearly: a box's walls are axis-aligned planes.
        Assert.All(report.Failing, f => Assert.Equal(0.0, f.WorstReleaseDegrees));
        Assert.Equal(0.0, report.WorstReleaseDegrees);
    }

    [Fact]
    public void ReversingThePullReversesEverySign()
    {
        var block = DraftedBlock(3);
        var up = Manufacturability.CheckDraft(new Part("u", block), Vector3d.UnitZ, 2);
        var down = Manufacturability.CheckDraft(new Part("d", block), -Vector3d.UnitZ, 2);

        Assert.Equal(up.Faces.Count, down.Faces.Count);
        for (int i = 0; i < up.Faces.Count; i++)
            Assert.Equal(-up.Faces[i].WorstReleaseDegrees, down.Faces[i].WorstReleaseDegrees, 1e-13);
        // The verdict is a magnitude, so it does not move.
        Assert.Equal(up.WorstReleaseDegrees, down.WorstReleaseDegrees, 1e-13);
        Assert.True(down.Passes);
    }

    [Fact]
    public void ACurvedFaceIsSampledAndTheReportSaysSo()
    {
        // A cone whose side leans 3 degrees off the pull axis.
        double lean = Math.Tan(3 * Math.PI / 180);
        var report = Manufacturability.CheckDraft(
            new Part("cone", Shape.Cone(20, 20 - 20 * lean, 20)), Vector3d.UnitZ, 2);

        var side = Assert.Single(report.Faces, f => f.Sampled);
        Assert.True(side.Samples > 100, $"a curved face should be sampled, got {side.Samples}");
        // A RevolvedSurface has no exact NormalAt override, so this reading carries the
        // base class's central differences -- measured 1.5e-7 of a degree, which is why
        // the row is flagged Sampled rather than presented as exact.
        Assert.Equal(3.0, side.WorstReleaseDegrees, 1e-5);
        Assert.Equal(3.0, side.MinAngleDegrees, 1e-5);
        Assert.Equal(3.0, side.MaxAngleDegrees, 1e-5);
        Assert.True(report.Passes);
    }

    [Fact]
    public void APlainCylinderHasNoDraftAtAll()
    {
        var report = Manufacturability.CheckDraft(
            new Part("cyl", Shape.Cylinder(10, 20)), Vector3d.UnitZ, 1);

        var wall = Assert.Single(report.Faces, f => f.Kind == SurfaceKind.Cylindrical);
        Assert.Equal(0.0, wall.WorstReleaseDegrees);   // exactly: every normal is perpendicular to the axis
        Assert.False(wall.Passes);
        Assert.False(report.Passes);
    }

    [Fact]
    public void APartWithNoBRepFallsBackToItsFacetsAndSaysSo()
    {
        var part = new Part("raw", MeshPrimitives.Box(new Aabb((0, 0, 0), (20, 20, 10))));
        var report = Manufacturability.CheckDraft(part, Vector3d.UnitZ, 1);

        Assert.Empty(report.Faces);
        Assert.NotNull(report.Note);
        Assert.Contains("no B-Rep", report.Note);
        // The verdict still exists and is still right: a box's walls have no draft.
        Assert.False(report.Passes);
        Assert.Equal(0.0, report.WorstReleaseDegrees);
        Assert.Equal(800, report.FailingArea, 1e-9);   // four 20x10 walls
    }

    [Fact]
    public void TheDraftFieldIsIndexedByDisplayMeshVerticesAndCarriesNoNaN()
    {
        var part = new Part("block", DraftedBlock(3));
        var report = Manufacturability.CheckDraft(part, Vector3d.UnitZ, 2);

        Assert.Equal(part.GetMesh().VertexCount, report.Field.Count);
        Assert.Equal(Manufacturability.FieldNames.DraftAngle, report.Field.Name);
        for (int v = 0; v < report.Field.Count; v++)
            Assert.False(double.IsNaN(report.Field.ValueAt(v)));

        // Every vertex of a drafted block touches a wall, and a vertex carries the WORST
        // reading among its incident facets -- so the whole field is the wall's 3 degrees
        // and the caps' 90 never appears. That is the per-vertex contract, stated.
        Assert.Equal(3.0, report.Field.Range.Min, 1e-9);
        Assert.Equal(3.0, report.Field.Range.Max, 1e-9);

        part.AddResult(report.Field);
        part.FieldDisplay = report.Display;
        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? error), error);
        Assert.Equal(FieldColorMap.Diverging, resolved.ColorMap);
        Assert.Equal(new FieldRange(-4, 4), resolved.Range);   // +/- twice the 2-degree minimum
    }

    // --------------------------------------------------------------- overhangs

    [Fact]
    public void AFortyFiveDegreeWallUnderAFortyFiveDegreeThresholdIsSelfSupporting()
    {
        var wall = FortyFiveDegreeWall();

        var tie = Manufacturability.CheckOverhangs(new Part("a", wall), Vector3d.UnitZ, 45);
        Assert.True(tie.Passes);
        Assert.Equal(0, tie.OverhangArea);
        Assert.Equal(0, tie.OverhangFacetCount);

        // THE REASON THAT WORKS. The wall's own reported angle is 45.000000000000007 --
        // an ulp OVER the threshold -- so a check comparing DEGREES would report a wall
        // drawn at exactly the stated angle as an overhang. The comparison is made on
        // the dot product instead (-n.b against sin(threshold)), which carries one fewer
        // rounding, and there the wall is self-supporting. This assertion pins both
        // halves so the rule cannot quietly be rewritten in degrees.
        Assert.True(tie.SteepestDegrees > 45, "the fixture must sit an ulp over the threshold");
        Assert.Equal(45.0, tie.SteepestDegrees, 1e-12);

        // A shade under the threshold and the whole wall is reported, exactly.
        var below = Manufacturability.CheckOverhangs(new Part("b", wall), Vector3d.UnitZ, 44.9);
        Assert.False(below.Passes);
        Assert.Equal(20 * Math.Sqrt(2) * 30, below.OverhangArea, 1e-9);

        var above = Manufacturability.CheckOverhangs(new Part("c", wall), Vector3d.UnitZ, 45.1);
        Assert.Equal(0, above.OverhangArea);
    }

    [Fact]
    public void AHorizontalCeilingIsTheOtherExactTie()
    {
        // A box's bottom face has normal exactly (0, 0, -1) and the threshold's sine at
        // 90 degrees is exactly 1.0, so 1.0 > 1.0 is the whole test -- no ulp anywhere.
        var box = Shape.Box(20, 20, 10);

        var at90 = Manufacturability.CheckOverhangs(new Part("a", box), Vector3d.UnitZ, 90);
        Assert.True(at90.Passes);
        Assert.Equal(0, at90.OverhangArea);
        Assert.Equal(90.0, at90.SteepestDegrees, 1e-12);

        var below = Manufacturability.CheckOverhangs(new Part("b", box), Vector3d.UnitZ, 89.999);
        Assert.Equal(400, below.OverhangArea, 1e-9);
        Assert.Equal(400, below.ProjectedArea, 1e-9);   // a ceiling projects to its own area
    }

    [Fact]
    public void ASelfSupportingConeReportsNoOverhangAtAll()
    {
        // The apex-down 45-degree cone is the canonical self-supporting print. A report
        // that cries wolf here is worse than no report.
        var report = Manufacturability.CheckOverhangs(
            new Part("cone", Shape.Cone(0, 10, 10)), Vector3d.UnitZ, 45);

        Assert.True(report.Passes);
        Assert.Equal(0, report.OverhangArea);
        Assert.Equal(0, report.ProjectedArea);
        Assert.Equal(0, report.OverhangFraction);
        Assert.Contains("none need support", report.ToText());
    }

    [Fact]
    public void TheTessellationDecidesWhatAngleACurvedSurfaceReports()
    {
        // An inscribed n-gon pyramid's lateral faces are STEEPER than the cone they
        // approximate, by exactly atan(cos(pi/n)) -- so a 45-degree cone reads 44.86 at
        // 32 segments and passes a 45-degree threshold for a reason that is about the
        // mesh rather than about the rule. That is why the tie above is pinned on a
        // planar fixture and this one only records the bias.
        foreach (int segments in new[] { 32, 64, 128 })
        {
            var quality = new MeshQuality { SegmentsPerCircle = segments };
            var report = Manufacturability.CheckOverhangs(
                new Part($"cone{segments}", Shape.Cone(0, 10, 10)), Vector3d.UnitZ, 45, quality);
            double predicted = Math.Atan(Math.Cos(Math.PI / segments)) * 180 / Math.PI;
            Assert.Equal(predicted, report.SteepestDegrees, 1e-12);
            Assert.True(predicted < 45);
        }
    }

    [Fact]
    public void AConeLateralAreaConvergesOnItsClosedForm()
    {
        // A 45-degree cone of base radius r and height r: lateral area = pi.r.slant
        // = sqrt(2).pi.r^2. Below the threshold the WHOLE lateral surface is overhang,
        // so the region's boundary is a model edge and nothing is quantized -- which is
        // what makes this converge quadratically where the sphere cap below does not.
        double r = 10;
        double exact = Math.Sqrt(2) * Math.PI * r * r;
        var errors = new List<double>();
        foreach (int segments in new[] { 32, 64, 128, 256 })
        {
            var quality = new MeshQuality { SegmentsPerCircle = segments };
            var report = Manufacturability.CheckOverhangs(
                new Part($"cone{segments}", Shape.Cone(0, r, r)), Vector3d.UnitZ, 44, quality);
            errors.Add(Math.Abs(report.OverhangArea / exact - 1));
        }

        // Measured 4.0e-3 / 1.0e-3 / 2.5e-4 / 6.3e-5 -- ratios 4.00, i.e. second order,
        // and always from BELOW (an inscribed polyhedron has less area).
        Assert.True(errors[0] < 5e-3, $"coarse error {errors[0]}");
        Assert.True(errors[^1] < 1e-4, $"fine error {errors[^1]}");
        for (int i = 1; i < errors.Count; i++)
        {
            double ratio = errors[i - 1] / errors[i];
            Assert.InRange(ratio, 3.5, 4.5);
        }
    }

    [Fact]
    public void TheLowerHemisphereIsExactlyHalfTheSphere()
    {
        // At threshold zero every downward-facing facet counts, so the answer is the
        // lower half of a mirror-symmetric polyhedron: an exact FRACTION, independent of
        // how coarse the mesh is. The projected area then converges on pi.r^2, the
        // silhouette -- pi.R^2.cos^2(threshold) at threshold 0.
        double R = 10;
        var projectedErrors = new List<double>();
        foreach (int segments in new[] { 32, 64, 128 })
        {
            var quality = new MeshQuality
            {
                SegmentsPerCircle = segments,
                CurveSamples = segments * 3 / 4,
            };
            var report = Manufacturability.CheckOverhangs(
                new Part($"s{segments}", Shape.Sphere(R)), Vector3d.UnitZ, 0, quality);

            Assert.Equal(0.5, report.OverhangFraction, 1e-13);
            projectedErrors.Add(Math.Abs(report.ProjectedArea / (Math.PI * R * R) - 1));
        }

        // Measured 6.4e-3 / 1.6e-3 / 4.0e-4: ratios 3.99, second order.
        for (int i = 1; i < projectedErrors.Count; i++)
            Assert.InRange(projectedErrors[i - 1] / projectedErrors[i], 3.5, 4.5);
    }

    [Fact]
    public void ACapWhoseBoundaryFallsInsideAFaceIsQuantizedByTheTessellation()
    {
        // The complement of the test above, recorded rather than hidden. A cap cut at
        // 45 degrees has its boundary in the MIDDLE of the sphere's facet bands, and a
        // facet is all-or-nothing, so the reported area snaps to a band boundary: the
        // error is first order in the band height and its SIGN depends on where the
        // cutoff happens to fall. Measured -0.57% at 32 segments and +2.1% for a
        // 30-degree cutoff, so the closed form here is a sanity band, not a tolerance.
        double R = 10;
        double exact = 2 * Math.PI * R * R * (1 - Math.Sin(45 * Math.PI / 180));
        var report = Manufacturability.CheckOverhangs(
            new Part("sphere", Shape.Sphere(R)), Vector3d.UnitZ, 45);

        Assert.Equal(exact, report.OverhangArea, exact * 0.03);
        Assert.True(report.OverhangArea < report.TotalArea);
    }

    [Fact]
    public void TheOverhangFieldAndDisplayAreReadyToDraw()
    {
        var part = new Part("sphere", Shape.Sphere(10));
        var report = Manufacturability.CheckOverhangs(part, Vector3d.UnitZ, 45);

        Assert.Equal(part.GetMesh().VertexCount, report.Field.Count);
        Assert.Equal(Manufacturability.FieldNames.OverhangAngle, report.Field.Name);
        // A sphere very nearly reaches both extremes -- but not exactly, and the gap is
        // the tessellation again: the pole vertex's incident facets sit half a latitude
        // band off the pole, so the field tops out at 88.28 rather than 90 at the
        // default density. The check's own SteepestDegrees reads the same facet.
        Assert.InRange(report.Field.Range.Max, 85, 90);
        Assert.InRange(report.Field.Range.Min, -90, -85);
        Assert.Equal(report.Field.Range.Max, report.SteepestDegrees, 1e-9);

        part.AddResult(report.Field);
        part.FieldDisplay = report.Display;
        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? error), error);
        Assert.Equal(new FieldRange(45, 90), resolved.Range);
        Assert.Equal(FieldColorMap.Viridis, resolved.ColorMap);

        // At a 90-degree threshold the range would be zero-span (everything at 0.5), so
        // the display declines to state one and the field's own range is used.
        var degenerate = Manufacturability.CheckOverhangs(new Part("b", Shape.Sphere(10)), Vector3d.UnitZ, 90);
        Assert.Null(degenerate.Display.Range);
    }

    // ---------------------------------------------------------- wall thickness

    [Fact]
    public void AShelledBoxReadsItsWallThicknessExactly()
    {
        var report = Manufacturability.CheckThickness(new Part("tray", Tray(1, 2.5)), 2.0);

        // Both walls are planes, so the correction cosine is exactly 1 and the reading is
        // the offset itself -- measured 2.4999999999999996.
        Assert.Equal(2.5, report.Minimum, 1e-12);
        Assert.True(report.Passes);
        Assert.Equal(0, report.BelowCount);
        Assert.Equal(0, report.UnmeasuredCount);

        var strict = Manufacturability.CheckThickness(new Part("tray2", Tray(1, 2.5)), 3.0);
        Assert.False(strict.Passes);
        Assert.Equal(strict.VertexCount, strict.BelowCount);
        Assert.Contains("under 3", strict.ToText());
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void TheThicknessReadingIsScaleFree(double scale)
    {
        // The self-hit floor and the degeneracy guard are both RELATIVE, so the same
        // model at three scales reads the same relative answer. An absolute epsilon on
        // an area (the recorded MeshDecimator trap) would lose the small one entirely.
        var report = Manufacturability.CheckThickness(new Part("t", Tray(scale, 2.5 * scale)), 2.0 * scale);
        Assert.Equal(2.5 * scale, report.Minimum, 1e-12 * scale);
        Assert.True(report.Passes);
    }

    [Fact]
    public void ATaperedWallReadsThePerpendicularDistanceNotTheRayLength()
    {
        // A right-triangular prism with 20 mm legs: the perpendicular distance from the
        // right-angle corner to the hypotenuse is a.b / hypot(a, b) = 14.14213562373095.
        // The vertex normal there is the area-weighted average of THREE faces, so the ray
        // does not run perpendicular to the wall it hits -- its raw length is 14.5297.
        // Multiplying by |n . n_hit| recovers the exact perpendicular distance, which is
        // the whole reason the correction exists.
        var part = new Part("wedge",
            Shape.Extrude(Sketch.Start(0, 0).LineTo(20, 0).LineTo(0, 20).Close(), 30));
        var report = Manufacturability.CheckThickness(part, 1);

        double exact = 20.0 * 20 / Math.Sqrt(800);
        Assert.Equal(exact, report.Minimum, 1e-12);

        // The instrument must be able to SEE the correction: recompute the raw ray length
        // at the same corner and assert it is a different number.
        var mesh = part.GetMesh();
        var normals = mesh.ComputeVertexNormals();
        int corner = Enumerable.Range(0, mesh.VertexCount)
            .First(v => mesh.GetPosition(v).LengthSquared < 1e-18);
        var direction = -normals[corner];
        double raw = 20.0 / (direction.X + direction.Y);   // the plane x + y = 20
        Assert.True(raw - exact > 0.3, $"the fixture must exercise the correction: raw {raw}, exact {exact}");
        Assert.Equal(exact, report.Field.ValueAt(corner), 1e-12);
    }

    [Fact]
    public void APointWithNoOpposingSurfaceCarriesTheDiagonalAndNeverNaN()
    {
        // The two acute corners of the wedge fire their rays out along the prism and
        // never leave the material through an opposing wall. NaN would be the obvious
        // spelling and is the wrong one: FieldRange skips NaN when ranging, but a NaN
        // still paints as the colour map's BOTTOM stop -- which on a thickness plot is
        // the colour of the thinnest wall in the part. So an unmeasurable point takes
        // the conservative end of the scale and is COUNTED in the report instead.
        var part = new Part("wedge",
            Shape.Extrude(Sketch.Start(0, 0).LineTo(20, 0).LineTo(0, 20).Close(), 30));
        var report = Manufacturability.CheckThickness(part, 1);
        var mesh = part.GetMesh();
        double diagonal = mesh.ComputeBounds().Size.Length;

        Assert.Equal(2, report.UnmeasuredCount);
        Assert.Contains("no opposing surface", report.ToText());
        for (int v = 0; v < report.Field.Count; v++)
            Assert.False(double.IsNaN(report.Field.ValueAt(v)));
        Assert.Equal(diagonal, report.Field.Range.Max, 1e-9);
        // ... and the unmeasured value is the LARGEST in the field, never the smallest.
        Assert.True(report.Minimum < report.Field.Range.Max);
    }

    [Fact]
    public void APlateReadsItsOwnThickness()
    {
        var report = Manufacturability.CheckThickness(new Part("plate", Shape.Box(60, 40, 6)), 5);
        Assert.Equal(6.0, report.Minimum, 1e-12);
        Assert.True(report.Passes);
        Assert.Equal(0, report.UnmeasuredCount);
    }

    [Fact]
    public void TheThicknessDisplayPutsTheRequirementAtHalfSaturation()
    {
        var part = new Part("tray", Tray(1, 2.5));
        var report = Manufacturability.CheckThickness(part, 2.0);
        part.AddResult(report.Field);
        part.FieldDisplay = report.Display;

        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? error), error);
        Assert.Equal(new FieldRange(0, 4), resolved.Range);
        Assert.Equal(Manufacturability.FieldNames.WallThickness, resolved.Field.Name);
    }

    // -------------------------------------------------------------- refusals

    [Fact]
    public void EveryCheckRefusesInputItCannotUse()
    {
        var part = new Part("box", Shape.Box(10, 10, 10));

        Assert.Throws<ArgumentException>(() =>
            Manufacturability.CheckDraft(part, Vector3d.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Manufacturability.CheckDraft(part, Vector3d.UnitZ, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Manufacturability.CheckDraft(part, Vector3d.UnitZ, 91));
        Assert.Throws<ArgumentException>(() =>
            Manufacturability.CheckOverhangs(part, Vector3d.Zero, 45));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Manufacturability.CheckOverhangs(part, Vector3d.UnitZ, 91));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Manufacturability.CheckThickness(part, 0));
    }

    [Fact]
    public void ReRunningACheckReplacesItsResultRatherThanAccumulatingTwins()
    {
        var part = new Part("block", DraftedBlock(3));
        part.AddResult(Manufacturability.CheckDraft(part, Vector3d.UnitZ, 2).Field);
        part.AddResult(Manufacturability.CheckDraft(part, -Vector3d.UnitZ, 2).Field);
        Assert.Single(part.Results);

        part.AddResult(Manufacturability.CheckOverhangs(part, Vector3d.UnitZ, 45).Field);
        part.AddResult(Manufacturability.CheckThickness(part, 2).Field);
        Assert.Equal(3, part.Results.Count);
    }
}
