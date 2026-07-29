using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A threaded rod with 45-degree lead-in chamfers, through the exact B-Rep route.
/// <para>The chamfer is ONE ordinary difference against a coaxial tool, and it works
/// because every pair it makes is analytic: the cone cuts each helical band in an exact
/// conical <see cref="SpiralArc3d"/>, the tool's flats are coaxial annuli (the plane cut,
/// clipped radially), and the tool's overshoot keeps every one of them transversal. The
/// intersection used to fall to the marching tracer, whose polyline ended strictly inside
/// the band and which face splitting refuses by name.</para>
/// </summary>
public class ChamferedThreadTests
{
    private const double Pitch = 1.25;
    private static readonly double H = Math.Sqrt(3) / 2 * Pitch;
    private const double MajorRadius = 4.0;
    private static readonly double MinorRadius = MajorRadius - 0.625 * H;
    private const double Length = 6.0;

    private static BrepSolid Rod() => SolidFactory.MakeThreadedRod(
    [
        new Vector2d(MajorRadius, -Pitch / 16),
        new Vector2d(MajorRadius, Pitch / 16),
        new Vector2d(MinorRadius, 3 * Pitch / 8),
        new Vector2d(MinorRadius, 5 * Pitch / 8),
    ], Pitch, Length);

    /// <summary>The same ISO-shaped rod at any size — the sub-depth chamfer sweep needs
    /// several, and a fixture that only ever built one could not see an alignment
    /// phenomenon.</summary>
    private static BrepSolid Rod(double pitch, double majorRadius, double length)
    {
        double minor = majorRadius - 0.625 * (Math.Sqrt(3) / 2 * pitch);
        return SolidFactory.MakeThreadedRod(
        [
            new Vector2d(majorRadius, -pitch / 16),
            new Vector2d(majorRadius, pitch / 16),
            new Vector2d(minor, 3 * pitch / 8),
            new Vector2d(minor, 5 * pitch / 8),
        ], pitch, length);
    }

    private static BrepSolid Chamfered(double chamfer, bool bothEnds = true)
    {
        var body = BrepBoolean.Difference(
            Rod(), SolidFactory.MakeThreadEndChamferTool(MajorRadius, chamfer, Length, true));
        return bothEnds
            ? BrepBoolean.Difference(
                body, SolidFactory.MakeThreadEndChamferTool(MajorRadius, chamfer, 0, false))
            : body;
    }

    [Fact]
    public void EveryCutTheChamferToolMakesIsAnalytic()
    {
        var rod = Rod();
        var tool = SolidFactory.MakeThreadEndChamferTool(MajorRadius, 0.5, Length, true);
        var region = new Aabb((-20, -20, -20), (20, 20, 20));
        int cuts = 0;
        foreach (var rodFace in rod.Faces)
        {
            foreach (var toolFace in tool.Faces)
            {
                foreach (var curve in SurfaceIntersection.Intersect(rodFace.Surface, toolFace.Surface, region))
                {
                    cuts++;
                    Assert.IsNotType<PolylineCurve3d>(curve);
                }
            }
        }
        Assert.True(cuts >= 4, $"expected a cut on every band, found {cuts}");
    }

    [Fact]
    public void AChamferedRodIsAValidTwoManifoldSolid()
    {
        var solid = Chamfered(0.5);
        solid.Validate();
        // Four bands, two caps, two chamfer cones.
        Assert.Equal(8, solid.Faces.Count());
        var mesh = BRepTessellator.Tessellate(solid, 64, 64);
        Assert.True(mesh.IsClosed, "a chamfered rod must weld closed");
    }

    /// <summary>
    /// The chamfered volume converges, and the DIFFERENCE from the unchamfered rod
    /// converges with it onto the material the chamfers remove — which is the assertion
    /// that means something, because both series approach their own limits from below and
    /// the deficit at a single density says nothing on its own.
    /// <para>Measured (win-x64) at 32/64/128/256 segments per circle: the plain rod
    /// 246.5616 / 247.7583 / 248.0578 / 248.1329, the top-chamfered one 245.8516 /
    /// 246.9694 / 247.2383 / 247.3058, so the single chamfer measures 0.7100 / 0.7889 /
    /// 0.8195 / 0.8271 — settling on ~0.83, and doubling for two ends.</para>
    /// </summary>
    [Fact]
    public void TheChamferedVolumeConvergesAndItsDeficitConvergesWithIt()
    {
        var volumes = new List<double>();
        var deficits = new List<double>();
        foreach (int segments in (int[])[32, 64, 128, 256])
        {
            double plain = BRepTessellator.Tessellate(Rod(), segments, segments).Volume();
            double chamfered = BRepTessellator.Tessellate(
                Chamfered(0.5, bothEnds: false), segments, segments).Volume();
            volumes.Add(chamfered);
            deficits.Add(plain - chamfered);
        }

        for (int i = 1; i < volumes.Count; i++)
        {
            Assert.True(volumes[i] > volumes[i - 1],
                $"an inscribed tessellation must rise with density: {volumes[i - 1]:F4} then {volumes[i]:F4}");
        }
        // The deficit IS the chamfer, so it must settle rather than drift: each step
        // moves it less than the last, and by a shrinking factor.
        for (int i = 2; i < deficits.Count; i++)
        {
            double step = deficits[i] - deficits[i - 1], previous = deficits[i - 1] - deficits[i - 2];
            Assert.True(step > 0 && step < previous / 2,
                $"the chamfer's measured volume must settle: steps {previous:F4} then {step:F4}");
        }
        Assert.Equal(0.83, deficits[^1], 1);
    }

    /// <summary>
    /// Both chamfer cones tessellate without a single inverted facet — the metric that
    /// matters, since a closed mesh with folds is exactly what an earlier version of this
    /// produced (244 folds of 3562 facets on one cone) while passing every count-based
    /// check.
    /// </summary>
    [Fact]
    public void TheChamferConesCarryNoFolds()
    {
        var report = TessellationQuality.Audit(Chamfered(0.5), 64, 64);
        Assert.Empty(report.Refusals);
        Assert.Equal(0, report.Folds);
        Assert.Equal(0, report.Slivers);
        Assert.True(report.Worst.WorstDot > 0.8,
            $"worst facet-vs-surface agreement {report.Worst.WorstDot:F5} on a {report.Worst.Family}");
    }

    /// <summary>
    /// A rod chamfered at BOTH ends, over the sub-depth fractions that used to fold.
    /// <para>The defect was in <c>TrimmedFaceTessellator.TurnsIntoInterior</c>: a chamfer
    /// cone's boundary carries a long run of samples at CONSTANT v (the rim in the end
    /// plane), where the monotone sweep's turn test reduces to the sign of the pullback's
    /// own round-off. When that sign said "turn", the sweep popped and emitted a facet
    /// spanning three consecutive rim samples — flat in the end plane, so its normal
    /// disagreed with the 45-degree cone by exactly <c>-cos(45°) = -0.7071</c>. It is an
    /// ALIGNMENT phenomenon, not a depth threshold: scanning 5% steps of the thread depth
    /// on M6x1 / M8x1.25 / M10x1.5 / M12x1.75 (both ends, length 5P - 0.2, 64 segments)
    /// folded at 0 / 4 / 3 / 3 of 19 steps each — 10 of 76, at unrelated fractions.</para>
    /// <para>Every one of those ten is asserted here, and the sweep is over BOTH the size
    /// and the fraction because that is the only shape of test an arithmetic tie-break
    /// respects. The fixture is also asserted to still CARRY the configuration — a cone
    /// face whose boundary genuinely has a long constant-v rim run — so it cannot quietly
    /// stop exercising the trap if the tool or the chamfer geometry is ever rebuilt.</para>
    /// </summary>
    [Theory]
    [InlineData(8, 1.25, 1)]
    [InlineData(8, 1.25, 3)]
    [InlineData(8, 1.25, 5)]
    [InlineData(8, 1.25, 6)]
    [InlineData(10, 1.5, 1)]
    [InlineData(10, 1.5, 4)]
    [InlineData(10, 1.5, 5)]
    [InlineData(12, 1.75, 1)]
    [InlineData(12, 1.75, 4)]
    [InlineData(12, 1.75, 6)]
    public void SubDepthChamfersCarryNoFoldsAtAnyFraction(double diameter, double pitch, int step)
    {
        double major = diameter / 2;
        double depth = 0.625 * (Math.Sqrt(3) / 2 * pitch);
        double chamfer = depth * step / 20.0;
        double length = 5 * pitch - 0.2;

        var oneEnd = BrepBoolean.Difference(
            Rod(pitch, major, length),
            SolidFactory.MakeThreadEndChamferTool(major, chamfer, length, true));
        var solid = BrepBoolean.Difference(
            oneEnd, SolidFactory.MakeThreadEndChamferTool(major, chamfer, 0, false));

        var report = TessellationQuality.Audit(solid, 64, 64);
        Assert.Empty(report.Refusals);
        Assert.Equal(0, report.Slivers);
        Assert.Equal(0, report.Folds);
        // NOT the corpus floor, deliberately, and the reason is worth stating: a sub-depth
        // chamfer cone is an extreme-aspect band — 0.034 mm tall around a 25 mm
        // circumference at the shallowest step here — so its facets are genuinely coarse
        // whatever the sweep does. Scanning all 76 depths, the ones that never folded
        // already measured 0.562..0.979 and the ten that did now measure 0.513..0.730, one
        // population rather than two, so this bar records the family's real quality and
        // still catches the defect it was written for (which read -0.7071). The coarseness
        // itself is filed as a separate residual.
        Assert.True(report.WorstDot > 0.4,
            $"worst facet-vs-surface agreement {report.WorstDot:F6} on a {report.Worst.Family}");

        // The configuration itself: each chamfer cone must still present a long run of
        // tessellation vertices sitting exactly in an end plane at one radius — the
        // constant-v rim whose turn test is pure round-off. Without this the test could
        // pass by no longer building the shape that used to break.
        int cones = 0;
        foreach (var (face, polygons) in BRepTessellator.TessellateByFace(solid, 64, 64))
        {
            if (face.Surface is not RevolvedSurface)
                continue;
            foreach (double plane in (ReadOnlySpan<double>)[0, length])
            {
                var rim = polygons
                    .SelectMany(p => p)
                    .Where(v => Math.Abs(v.Z - plane) < 1e-9)
                    .Select(v => Math.Round(Math.Sqrt(v.X * v.X + v.Y * v.Y), 9))
                    .ToList();
                if (rim.Count >= 32 && rim.Distinct().Count() == 1)
                    cones++;
            }
        }
        Assert.Equal(2, cones);
    }

    /// <summary>
    /// The B-Rep chamfer IS the implicit chamfer: every vertex of the tessellation reads
    /// zero against <see cref="Sdf.Thread"/>'s own chamfered field. That is the check that
    /// can see a chamfer placed at the wrong depth or on the wrong end, where a volume
    /// comparison across representations is limited by Surface Nets' own resolution.
    /// </summary>
    [Fact]
    public void TheBrepChamferAgreesWithTheImplicitFieldAtEveryVertex()
    {
        var shape = Shape.ExternalThread(StandardThreads.Metric(8), 6, chamferLength: 0.5);
        var mesh = BRepTessellator.Tessellate(shape.ToBrep(), 96, 96);
        var field = shape.ToImplicit();
        double worst = 0;
        foreach (var vertex in mesh.Vertices)
            worst = Math.Max(worst, Math.Abs(field.Evaluate(vertex.Position)));
        Assert.True(worst < 1e-9, $"worst |sdf| on the chamfered B-Rep surface = {worst:E3}");
    }

    /// <summary>
    /// A chamfer at the thread depth puts the cone's base exactly on the minor diameter,
    /// so it is TANGENT to every root band along the end plane — coincident curved-surface
    /// boolean input, refused by classification rather than attempted.
    /// </summary>
    [Fact]
    public void AFullDepthChamferIsRefusedByName()
    {
        var spec = StandardThreads.Metric(8);
        var report = Shape.ExternalThread(spec, 6).Explain(TargetRep.Brep);
        Assert.False(report.IsConvertible);
        Assert.Contains(report.Entries, e =>
            e.Support == NodeSupport.Impossible && e.Detail!.Contains("minor diameter"));

        var shallow = Shape.ExternalThread(spec, 6, chamferLength: spec.ThreadDepth / 2);
        Assert.True(shallow.Explain(TargetRep.Brep).IsConvertible);
    }

    /// <summary>
    /// A spiral edge that came out of a split is a <see cref="CurveSegment"/>
    /// reparameterized to [0, 1] while <c>Underlying</c> still points at the
    /// <see cref="SpiralArc3d"/>, so its sample count has to be read from the TURNING
    /// ANGLE through that mapping. Read off the edge's own domain instead — which is what
    /// happened — every such edge got the same count whatever it spanned (11 at
    /// segmentsPerCircle 64, and 11 at 256 as well: a density FLOOR), and two cuts of the
    /// SAME angular span came back at 8 and 11 samples, which the sheared helical grid
    /// reports as "boundary polylines disagree in sample count".
    /// </summary>
    [Fact]
    public void ASplitSpiralEdgeIsSampledByItsTurningAngle()
    {
        var solid = Chamfered(0.5);
        var segments = solid.Edges
            .Where(e => e.Curve is CurveSegment && e.Curve.Underlying is SpiralArc3d)
            .ToList();
        Assert.NotEmpty(segments);

        foreach (var edge in segments)
        {
            var wrapper = (CurveSegment)edge.Curve;
            double angle = Math.Abs(wrapper.BaseEnd - wrapper.BaseStart);
            foreach (int density in (int[])[64, 256])
            {
                int expected = Math.Max(1, (int)Math.Ceiling(angle * density / (2 * Math.PI) - 1e-9));
                Assert.Equal(expected + 1, BRepTessellator.SampleEdge(edge, density, density).Count);
            }
        }
    }

    [Fact]
    public void AnUnchamferedRodIsUnchangedByTheChamferPath()
    {
        // Exact-zero gate: asking for no chamfer must skip both booleans entirely, so the
        // rod keeps the topology MakeThreadedRod gives it.
        var solid = Shape.ExternalThread(StandardThreads.Metric(8), 6, chamferEnds: false).ToBrep();
        Assert.Equal(6, solid.Faces.Count());
    }
}
