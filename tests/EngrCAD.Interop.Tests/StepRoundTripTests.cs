using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// StepWriter → StepReader round trips: everything the writer emits for these solids
/// must come back as a valid solid with identical topology counts and a tessellated
/// volume matching the original within 1e-6 relative (in practice ~1e-12 — geometry is
/// reconstructed exactly, not approximately).
/// </summary>
public class StepRoundTripTests
{
    private static BrepSolid RoundTrip(BrepSolid original, int genus, int segmentsPerCircle = 64, int curveSamples = 24)
    {
        string step = StepWriter.Write(original);
        var result = StepReader.Read(step);
        Assert.Empty(result.Diagnostics);
        var read = Assert.Single(result.Solids);

        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus), $"Euler–Poincaré failed for genus {genus}.");
        Assert.Equal(original.Faces.Count(), read.Faces.Count());
        Assert.Equal(original.Loops.Count(), read.Loops.Count());
        Assert.Equal(original.Edges.Count(), read.Edges.Count());
        Assert.Equal(original.Vertices.Count(), read.Vertices.Count());

        var originalMesh = BRepTessellator.Tessellate(original, segmentsPerCircle, curveSamples);
        var readMesh = BRepTessellator.Tessellate(read, segmentsPerCircle, curveSamples);
        Assert.True(originalMesh.IsClosed, "original tessellation is not closed");
        Assert.True(readMesh.IsClosed, "round-tripped tessellation is not closed");
        double expected = originalMesh.Volume();
        double actual = readMesh.Volume();
        Assert.True(Math.Abs(actual - expected) <= 1e-6 * Math.Abs(expected),
            $"volume {actual} vs {expected} (relative {Math.Abs(actual - expected) / Math.Abs(expected):G3})");
        return read;
    }

    [Fact]
    public void Box_RoundTrips()
    {
        var read = RoundTrip(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), genus: 0);
        Assert.Equal(24.0, BRepTessellator.Tessellate(read).Volume(), 9);
    }

    [Fact]
    public void Cylinder_RoundTrips()
    {
        RoundTrip(SolidFactory.MakeCylinder(1.5, 4), genus: 0);
    }

    [Fact]
    public void DrilledPlate_ShapeDrillOutput_RoundTrips()
    {
        // Shape.Drill subtracts axis-touching revolved tools: the bore walls come back
        // from the boolean as full-turn RevolvedSurface bands with CurveSegment
        // generators, which the writer flattens to the base curve — the reader re-trims
        // them from the rim circles.
        var top = SketchPlane.At((0, 0, 0.5), Vector3d.UnitX, Vector3d.UnitY);
        var drilled = Shape.Box(4, 3, 1)
            .Drill(HoleSpec.Simple(0.8), [new Vector2d(1, 0.5), new Vector2d(-1, -0.5)], depth: 2, top)
            .ToBrep();
        RoundTrip(drilled, genus: 2);
    }

    [Fact]
    public void RevolvedSolid_FullTurn_RoundTrips()
    {
        var pulley = SolidFactory.Revolve(
            Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]),
            Vector3d.Zero, Vector3d.UnitZ);
        RoundTrip(pulley, genus: 1);
    }

    [Fact]
    public void RevolvedSolid_PartialTurn_RoundTrips()
    {
        // SURFACE_OF_REVOLUTION stores no swept angle; the reader recovers it from the
        // rail arcs, and the tessellated wedge volume proves it.
        var wedge = SolidFactory.Revolve(
            Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]),
            Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);
        RoundTrip(wedge, genus: 0);
    }

    [Fact]
    public void Sphere_PoleBands_RoundTrip()
    {
        // Rational quarter-circle generators through the complex-instance form, plus
        // single-rim pole-bounded bands (the equator is the only boundary loop).
        RoundTrip(SolidFactory.MakeSphere(1.5), genus: 0);
    }

    [Fact]
    public void ExtrudedBracket_RoundTrips()
    {
        var bracket = SolidFactory.Extrude(
            Profile.FromPoints([(0, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0), (1, 2, 0), (0, 2, 0)]),
            (0, 0, 1));
        RoundTrip(bracket, genus: 0);
    }

    [Fact]
    public void BooleanDifference_ReversedBoreWall_RoundTrips()
    {
        // Extruded-circle tool: the bore wall face is reversed (same-sense .F. in the
        // file) — orientation must survive the trip.
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var tool = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, 0.4), (0, 0, 3));
        var drilled = BrepBoolean.Difference(box, tool);
        int reversedCount = drilled.Faces.Count(f => f.IsReversed);
        var read = RoundTrip(drilled, genus: 1);
        Assert.True(reversedCount > 0, "expected the boolean to produce reversed bore faces");
        Assert.Equal(reversedCount, read.Faces.Count(f => f.IsReversed));
    }

    [Fact]
    public void MultiShellUnion_AdoptsTheWriterOrphanShells()
    {
        // The writer emits every shell but references only the first from the
        // MANIFOLD_SOLID_BREP; the reader adopts the rest (with a note).
        var union = BrepBoolean.Union(
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))),
            SolidFactory.MakeBox(new Aabb((3, 0, 0), (4, 1, 1))));
        Assert.Equal(2, union.Shells.Count);

        var result = StepReader.Read(StepWriter.Write(union));
        var read = Assert.Single(result.Solids);
        Assert.Equal(2, read.Shells.Count);
        Assert.Contains(result.Diagnostics, d => d.Contains("Adopted"));
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(
            BRepTessellator.Tessellate(union).Volume(),
            BRepTessellator.Tessellate(read).Volume(), 9);
    }

    [Fact]
    public void FilletedPuck_RoundTrips()
    {
        var puck = SolidFactory.MakeCylinder(1.5, 2);
        var filleted = Filleting.FilletEdge(puck, puck.Edges.First(e => e.Uses.Any(u =>
            u.Loop.Face.Surface is PlaneSurface p && p.Normal.Dot(Vector3d.UnitZ) > 0.9)), 0.4);
        RoundTrip(filleted, genus: 0);
    }

    // ---- large geometry with off-axis rim noise (rim re-trim tolerance) ----

    private const double BoreRadius = 400;

    private static BrepSolid ScaledDrilledPlate()
    {
        // 1000× the DrilledPlate test: bore-wall generators come back as untrimmed
        // base curves that the reader must re-trim from the rim circles.
        var top = SketchPlane.At((0, 0, 500), Vector3d.UnitX, Vector3d.UnitY);
        return Shape.Box(4000, 3000, 1000)
            .Drill(
                HoleSpec.Simple(2 * BoreRadius),
                [new Vector2d(1234.5678, 456.789), new Vector2d(-987.654, -321.987)],
                depth: 2000, top)
            .ToBrep();
    }

    /// <summary>
    /// Shifts the center points of all circles with the given radius by
    /// <paramref name="dx"/> in X, simulating a foreign file whose rim circles carry
    /// placement noise decorrelated from the surface axis (our own writer emits both
    /// from the same exact values, so uniform rounding can never move a rim off-axis).
    /// </summary>
    private static string OffsetRimCircleCenters(string step, double radius, double dx)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var placementOf = System.Text.RegularExpressions.Regex
            .Matches(step, @"#(\d+)=AXIS2_PLACEMENT_3D\('',#(\d+),")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        var targetPoints = new HashSet<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex
            .Matches(step, @"#\d+=CIRCLE\('',#(\d+),([0-9.Ee+-]+)\)"))
        {
            if (Math.Abs(double.Parse(m.Groups[2].Value, culture) - radius) < 1e-6 * radius)
                targetPoints.Add(placementOf[m.Groups[1].Value]);
        }
        Assert.NotEmpty(targetPoints);

        static string Real(double v)
        {
            string s = v.ToString("G16", System.Globalization.CultureInfo.InvariantCulture);
            return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".";
        }
        return System.Text.RegularExpressions.Regex.Replace(
            step,
            @"#(\d+)=CARTESIAN_POINT\('',\(([^)]*)\)\)",
            m =>
            {
                if (!targetPoints.Contains(m.Groups[1].Value))
                    return m.Value;
                var c = m.Groups[2].Value.Split(',').Select(s => double.Parse(s, culture)).ToArray();
                return $"#{m.Groups[1].Value}=CARTESIAN_POINT('',({Real(c[0] + dx)},{Real(c[1])},{Real(c[2])}))";
            });
    }

    [Fact]
    public void ScaledDrilledPlate_SlightlyOffAxisRims_StillRecoverGeneratorTrims()
    {
        // 2e-4 off-axis on ~1e3-magnitude coordinates is realistic foreign-file noise.
        // The old absolute 1e-6 rejection floor silently skipped such rims, leaving the
        // bore-wall generators untrimmed at the tool's full overshoot; the floor now
        // scales with the coordinate magnitude.
        var original = ScaledDrilledPlate();
        string perturbed = OffsetRimCircleCenters(StepWriter.Write(original), BoreRadius, dx: 2e-4);

        var result = StepReader.Read(perturbed);
        var read = Assert.Single(result.Solids);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("off-axis"));
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 2));

        // Every revolved band's surface must be trimmed to its own boundary's axial
        // extent — an untrimmed generator would span the tool's overshoot too.
        var bands = read.Faces.Where(f => f.Surface is RevolvedSurface).ToList();
        Assert.NotEmpty(bands);
        foreach (var face in bands)
        {
            var surface = (RevolvedSurface)face.Surface;
            double s0 = surface.PointAt(surface.DomainU.Start, surface.DomainV.Start).Z;
            double s1 = surface.PointAt(surface.DomainU.Start, surface.DomainV.End).Z;
            var boundaryZ = face.Loops
                .SelectMany(l => l.Coedges)
                .SelectMany(c => new[]
                {
                    c.Edge.Curve.PointAt(c.Edge.Domain.Start).Z,
                    c.Edge.Curve.PointAt(c.Edge.Domain.ParameterAt(0.5)).Z,
                    c.Edge.Curve.PointAt(c.Edge.Domain.End).Z,
                })
                .ToList();
            double surfaceExtent = Math.Abs(s1 - s0);
            double boundaryExtent = boundaryZ.Max() - boundaryZ.Min();
            Assert.True(Math.Abs(surfaceExtent - boundaryExtent) < 1.0,
                $"revolved band left untrimmed: surface spans {surfaceExtent}, boundary spans {boundaryExtent}");
        }
    }

    [Fact]
    public void ScaledDrilledPlate_ClearlyOffAxisRims_ReportNearMissRejection()
    {
        // 0.01 off-axis exceeds the scaled tolerance — rejection is correct, but it
        // must never be silent.
        var original = ScaledDrilledPlate();
        string perturbed = OffsetRimCircleCenters(StepWriter.Write(original), BoreRadius, dx: 0.01);
        var result = StepReader.Read(perturbed);
        Assert.Contains(result.Diagnostics, d => d.Contains("off-axis"));
    }
}
