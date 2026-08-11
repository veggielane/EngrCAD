using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The INTRINSIC MID/LDS routing — the generalisation from a single global exp-map chart to an ARBITRARY
/// mesh surface. The headline is a CLOSED surface (a torus) that a single global chart would wrap onto
/// itself, now routed and verified with no chart. The measurements are the certified ones: a sphere
/// great-circle geodesic against its closed form, a developable cylinder agreeing with the flat DRC to
/// the discretisation grade, and a high-curvature pair conservatively REFUSED.
/// </summary>
public class Mid3dIntrinsicTests
{
    private readonly ITestOutputHelper _out;
    public Mid3dIntrinsicTests(ITestOutputHelper output) => _out = output;

    // ---- meshes --------------------------------------------------------------

    private static Vector3d TorusPoint(double major, double minor, double u, double v) =>
        new((major + minor * Math.Cos(v)) * Math.Cos(u),
            (major + minor * Math.Cos(v)) * Math.Sin(u),
            minor * Math.Sin(v));

    private static HalfEdgeMesh Torus(double major = 12, double minor = 4, int around = 96, int tube = 48)
    {
        var positions = new List<Vector3d>();
        for (int i = 0; i < around; i++)
        for (int j = 0; j < tube; j++)
        {
            double u = 2 * Math.PI * i / around;
            double v = 2 * Math.PI * j / tube;
            positions.Add(TorusPoint(major, minor, u, v));
        }
        var faces = new List<int[]>();
        for (int i = 0; i < around; i++)
        for (int j = 0; j < tube; j++)
        {
            int a = i * tube + j;
            int b = ((i + 1) % around) * tube + j;
            int c = ((i + 1) % around) * tube + (j + 1) % tube;
            int d = i * tube + (j + 1) % tube;
            faces.Add([a, b, c]);
            faces.Add([a, c, d]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>A bumpy organic blob — a sphere with a radial ripple, so its Gaussian curvature is
    /// genuinely doubly-varying (no chart is an isometry anywhere).</summary>
    private static HalfEdgeMesh Blob(double radius = 10, int around = 96, int down = 64)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= down; j++)
        for (int i = 0; i < around; i++)
        {
            double u = 2 * Math.PI * i / around;
            double v = Math.PI * j / down;
            double r = radius * (1 + 0.12 * Math.Sin(3 * u) * Math.Sin(2 * v));
            positions.Add(new Vector3d(
                r * Math.Sin(v) * Math.Cos(u),
                r * Math.Sin(v) * Math.Sin(u),
                r * Math.Cos(v)));
        }
        var faces = new List<int[]>();
        for (int j = 0; j < down; j++)
        for (int i = 0; i < around; i++)
        {
            int a = j * around + i;
            int b = j * around + (i + 1) % around;
            faces.Add([a, b, b + around]);
            faces.Add([a, b + around, a + around]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static HalfEdgeMesh Tube(double radius = 2, double height = 6, int around = 96, int along = 48)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= along; j++)
        for (int i = 0; i < around; i++)
        {
            double a = 2 * Math.PI * i / around;
            positions.Add(new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), height * j / along));
        }
        var faces = new List<int[]>();
        for (int j = 0; j < along; j++)
        for (int i = 0; i < around; i++)
        {
            int a = j * around + i;
            int b = j * around + (i + 1) % around;
            faces.Add([a, b, b + around]);
            faces.Add([a, b + around, a + around]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static double PathLength(IReadOnlyList<SurfacePoint> path)
    {
        double len = 0;
        for (int i = 1; i < path.Count; i++)
            len += path[i].Position.DistanceTo(path[i - 1].Position);
        return len;
    }

    // ---- the headline: a closed surface no longer wraps ----------------------

    [Fact]
    public void Torus_RoutesAndVerifies_WhereAGlobalChartWouldWrap()
    {
        var torus = Torus();
        // A single global exp-map chart over the whole closed torus wraps onto itself and its distortion
        // blows up — the recorded failure the intrinsic model exists to remove.
        int seed = 0;
        var wrapped = MidBoard.OnSurface(torus, seed, Vector3d.UnitZ, radius: 100);
        Assert.True(wrapped.MaxDistortion > 1.0,
            $"a global chart over a whole torus should wrap (huge distortion), got {wrapped.MaxDistortion:F3}");

        // The intrinsic board routes and verifies the same closed surface with no chart at all.
        double major = 12, minor = 4, land = 0.6, width = 0.35;
        var board = MidBoard.OnMesh(torus);
        // Three pads on the outer equator, spread a quarter of the way around — a net routed most of the
        // way round the torus, which a single seed+radius chart could never cover.
        var p0 = board.PlacePad("SIG", TorusPoint(major, minor, 0.0, 0), land, "S0");
        var p1 = board.PlacePad("SIG", TorusPoint(major, minor, 0.9, 0), land, "S1");
        var p2 = board.PlacePad("SIG", TorusPoint(major, minor, 1.8, 0), land, "S2");
        MidRouting.Connect(board, p0, p1, width);
        MidRouting.Connect(board, p1, p2, width);

        var report = MidRouting.Verify(board);
        // The whole net connects along the surface — no ratsnest, and the DRC is clean.
        Assert.DoesNotContain("SIG", report.Ratsnest);
        Assert.True(report.Ok, "the routed torus board should verify clean: " + report);
        // The per-region distortion is reported and finite (a torus is genuinely curved, not a plane).
        _out.WriteLine($"torus MaxDistortion (intrinsic) = {board.MaxDistortion:F4}");
        Assert.True(board.MaxDistortion is > 0 and < 0.2, $"torus local distortion {board.MaxDistortion:F4}");
    }

    [Fact]
    public void Torus_DrcFindsNearAndClearsFar()
    {
        const double clearance = 0.2;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0 };
        double major = 12, minor = 4, land = 0.4;
        var board = MidBoard.OnMesh(Torus(major, minor));

        // FAR pair: opposite sides of the torus — a global chart could not even see both.
        board.PlacePad("F1", TorusPoint(major, minor, 0.0, 0), land, "F1");
        board.PlacePad("F2", TorusPoint(major, minor, Math.PI, 0), land, "F2");
        // NEAR pair on the outer equator: half a clearance apart edge to edge — a definite violation.
        double du = (land + 0.5 * clearance) / (major + minor);   // arc/radius on the outer equator
        board.PlacePad("N1", TorusPoint(major, minor, 2.0, 0), land, "N1");
        board.PlacePad("N2", TorusPoint(major, minor, 2.0 + du, 0), land, "N2");

        var report = MidRouting.Verify(board, rules);
        _out.WriteLine(report.ToString());

        // Far pair: never flagged (the certified 3D broad phase proves the surface clearance).
        Assert.DoesNotContain(report.Violations, v => v.Message.Contains("F1") || v.Message.Contains("F2"));
        Assert.DoesNotContain(report.Uncertain, v => v.Message.Contains("F1") || v.Message.Contains("F2"));
        // Near pair: a definite clearance violation, measured through the local geodesic chart.
        Assert.Contains(report.Violations, v => v.Rule == MidDrcRule.Clearance
            && (v.Message.Contains("N1") || v.Message.Contains("N2")));
    }

    [Fact]
    public void HighCurvature_NearLimitPair_IsRefusedButAFlatOneIsCertified()
    {
        // A SMALL sphere where the clearance is a large fraction of the curvature radius: the exp map
        // OVERESTIMATES tangential surface distances (a ring's circumference is shorter than 2π·geodesic),
        // so a pair whose PARAMETER separation clears the rule can have a true surface distance that dips
        // below it — the band straddles, and the DRC refuses rather than passing false-precise. The same
        // pair on a plane (no shrink) is certified CLEAR.
        const double clearance = 1.0;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0 };
        double radius = 1.0, land = 0.3;
        var sphere = MeshPrimitives.UvSphere(radius, 128, 64);
        var board = MidBoard.OnMesh(sphere);

        // Edge-to-edge arc just ABOVE the clearance, so a flat sheet clears it but the sphere's shrink
        // pulls the worst-case surface distance below.
        double gapArc = land + 1.05 * clearance;                   // centre-to-centre arc
        double dphi = gapArc / radius;
        var a = new Vector3d(radius, 0, 0);
        var b = new Vector3d(radius * Math.Cos(dphi), radius * Math.Sin(dphi), 0);
        board.PlacePad("A", a, land, "A");
        board.PlacePad("B", b, land, "B");

        var onSphere = MidRouting.Verify(board, rules);
        _out.WriteLine("sphere: " + onSphere);
        // The surface shrink leaves the verdict un-certifiable → refused, not passed false-precise.
        Assert.Contains(onSphere.Uncertain, u => u.Rule == MidDrcRule.Clearance);
        Assert.False(onSphere.Ok);

        // The SAME configuration on a flat sheet is certified CLEAR — no shrink, no ambiguity.
        var plane = FlatGrid(20, 60);
        var flat = MidBoard.OnMesh(plane);
        flat.PlacePad("A", new Vector3d(0, 0, 0), land, "A");
        flat.PlacePad("B", new Vector3d(gapArc, 0, 0), land, "B");
        var onFlat = MidRouting.Verify(flat, rules);
        _out.WriteLine("plane: " + onFlat);
        Assert.Empty(onFlat.Uncertain);
        Assert.True(onFlat.Ok, "the flat pair is above the limit → clear: " + onFlat);
    }

    private static HalfEdgeMesh FlatGrid(double size, int n)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
            positions.Add(new Vector3d(-size / 2 + size * i / n, -size / 2 + size * j / n, 0));
        var faces = new List<int[]>();
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i;
            faces.Add([a, a + 1, a + n + 2]);
            faces.Add([a, a + n + 2, a + n + 1]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    // ---- sphere great-circle geodesic (closed form) --------------------------

    [Fact]
    public void SphereGeodesic_MatchesTheGreatCircleClosedForm()
    {
        const double radius = 10;
        var sphere = MeshPrimitives.UvSphere(radius, 128, 64);
        var board = MidBoard.OnMesh(sphere);

        // Two points a known central angle apart: pole to the 60° latitude on the prime meridian.
        double lat = Math.PI / 3;   // central angle from the pole
        var a = new Vector3d(0, 0, radius);
        var b = new Vector3d(radius * Math.Sin(lat), 0, radius * Math.Cos(lat));
        double greatCircle = radius * lat;   // R·θ, the exact geodesic

        var path = MidRouting.Geodesic(board, board.Locate(a), board.Locate(b));
        double measured = PathLength(path);
        _out.WriteLine($"great circle {greatCircle:F4}, edge-graph geodesic {measured:F4}, ratio {measured / greatCircle:F4}");
        // The edge-graph geodesic overestimates the true great circle by up to ~8% (paths ride mesh
        // edges) — the discretisation grade — and never underestimates it.
        // A discrete edge path inscribes the smooth great circle, so it may fall a chord's-worth BELOW
        // it, and staircases up to ~8% above where it cannot follow a mesh line.
        Assert.InRange(measured, greatCircle * 0.98, greatCircle * 1.10);
    }

    // ---- the developable oracle under the intrinsic formulation --------------

    [Fact]
    public void Cylinder_IntrinsicGeodesicClearance_AgreesWithTheArcToTheDiscretisationGrade()
    {
        const double clearance = 0.15;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0 };
        double radius = 2, land = 0.3;
        var board = MidBoard.OnMesh(Tube(radius));

        // Two different-net pads on the tube, an arc gap of exactly `1.05·clearance` edge to edge — a
        // near miss the developable exp map must resolve to a CLEAR (its distortion band collapses).
        double gap = 1.05 * clearance;
        double dphi = (land + gap) / radius;   // centre-to-centre arc → edge-to-edge gap = `gap`
        var a = new Vector3d(radius, 0, 3);
        var b = new Vector3d(radius * Math.Cos(dphi), radius * Math.Sin(dphi), 3);
        board.PlacePad("A", a, land, "A");
        board.PlacePad("B", b, land, "B");
        // A definite violation pair: half a clearance.
        double dphi2 = (land + 0.5 * clearance) / radius;
        board.PlacePad("C", new Vector3d(radius, 0, 1), land, "C");
        board.PlacePad("D", new Vector3d(radius * Math.Cos(dphi2), radius * Math.Sin(dphi2), 1), land, "D");

        var report = MidRouting.Verify(board, rules);
        _out.WriteLine(report.ToString());

        // A developable surface never straddles: nothing is Uncertain, and the near miss is CLEAR.
        Assert.Empty(report.Uncertain);
        Assert.DoesNotContain(report.Violations, v => v.Message.Contains("'A'") || v.Message.Contains("'B'"));
        // The violation pair is caught.
        var cd = report.Violations.Single(v => v.Rule == MidDrcRule.Clearance);
        // The measured surface separation matches the arc gap (a cylinder's geodesic IS its unrolled arc)
        // to the mesh's discretisation grade — the developable oracle under the intrinsic formulation.
        double arcGap = 0.5 * clearance;
        _out.WriteLine($"measured {cd.MeasuredParameter:F5}, arc gap {arcGap:F5}");
        Assert.InRange(cd.MeasuredParameter, arcGap * 0.94, arcGap * 1.06);   // the discretisation grade
        // And the developable band is ~1 (no distortion), so surface == parameter.
        Assert.True(Math.Abs(cd.MinScale - 1) < 0.02 && Math.Abs(cd.MaxScale - 1) < 0.02,
            $"a cylinder is developable: band [{cd.MinScale:F4}, {cd.MaxScale:F4}] should be ≈1");
    }

    // ---- a bumpy blob routes and verifies ------------------------------------

    [Fact]
    public void Blob_RoutesAndVerifiesAndFoldsDistortion()
    {
        const double clearance = 0.2;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0.15 };
        var blob = Blob();
        var board = MidBoard.OnMesh(blob);

        // A component-and-trace layout on the blob's shoulder (mid latitude, where the ripple curves).
        SurfacePoint OnBlob(double u, double v)
        {
            double r = 10 * (1 + 0.12 * Math.Sin(3 * u) * Math.Sin(2 * v));
            return board.Locate(new Vector3d(
                r * Math.Sin(v) * Math.Cos(u), r * Math.Sin(v) * Math.Sin(u), r * Math.Cos(v)));
        }
        var a = board.PlacePad("SIG", OnBlob(0.5, 1.0).Position, 0.5, "A");
        var b = board.PlacePad("SIG", OnBlob(1.1, 1.2).Position, 0.5, "B");
        MidRouting.Connect(board, a, b, 0.35);
        board.PlacePad("GND", OnBlob(0.2, 1.4).Position, 0.5, "G");

        var report = MidRouting.Verify(board, rules);
        _out.WriteLine($"blob distortion {board.MaxDistortion:F4}; {report}");
        // The net is routed (connected) and the board verifies (the layout is spaced clear).
        Assert.DoesNotContain("SIG", report.Ratsnest);
        Assert.True(report.Ok, "blob board should verify: " + report);
        // A doubly-curved blob carries real distortion, reported per region.
        Assert.True(board.MaxDistortion > 0.005, $"a rippled blob should distort, got {board.MaxDistortion:F4}");
    }

    // ---- pad-endpoint exactness ----------------------------------------------

    [Fact]
    public void GeodesicTraceEndpointsLandExactlyOnTheirPads()
    {
        double major = 12, minor = 4;
        var board = MidBoard.OnMesh(Torus(major, minor));
        var a = board.PlacePad("N", TorusPoint(major, minor, 0.5, 0.3), 0.4, "A");
        var b = board.PlacePad("N", TorusPoint(major, minor, 1.1, 0.5), 0.4, "B");
        var trace = MidRouting.Connect(board, a, b, 0.3);

        var start = trace.Runs[0].Points[0];
        var end = trace.Runs[0].Points[^1];
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.SurfacePoint.X), BitConverter.DoubleToInt64Bits(start.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.SurfacePoint.Z), BitConverter.DoubleToInt64Bits(start.Z));
        Assert.Equal(BitConverter.DoubleToInt64Bits(b.SurfacePoint.X), BitConverter.DoubleToInt64Bits(end.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(b.SurfacePoint.Z), BitConverter.DoubleToInt64Bits(end.Z));
    }

    // ---- determinism ---------------------------------------------------------

    [Fact]
    public void IntrinsicCheck_IsDeterministic()
    {
        const double clearance = 0.2;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance };
        double major = 12, minor = 4;
        var board = MidBoard.OnMesh(Torus(major, minor));
        board.PlacePad("A", TorusPoint(major, minor, 2.0, 0), 0.4, "A");
        board.PlacePad("B", TorusPoint(major, minor, 2.05, 0), 0.4, "B");
        var a = MidRouting.Verify(board, rules);
        var b = MidRouting.Verify(board, rules);
        Assert.Equal(a.Violations.Count, b.Violations.Count);
        Assert.Equal(a.Uncertain.Count, b.Uncertain.Count);
        for (int i = 0; i < a.Violations.Count; i++)
            Assert.Equal(a.Violations[i].Message, b.Violations[i].Message);
        Assert.Equal(a.Ratsnest, b.Ratsnest);
    }

    // ---- seating a component intrinsically -----------------------------------

    [Fact]
    public void Seat_PosesAComponentOnAnArbitrarySurface()
    {
        double major = 12, minor = 4;
        var board = MidBoard.OnMesh(Torus(major, minor));
        var world = TorusPoint(major, minor, 1.0, 0.4);
        var located = board.Locate(world);

        var screw = StandardComponents.CapScrew(6, 12);
        var seated = board.Seat(screw, world);

        // The body seats SeatDepth below the surface along its normal, and the frame's Z is the surface
        // normal — the seating convention transported onto the moulded surface, on any geometry.
        double along = (seated.SeatFrame.Origin - located.Position).Dot(located.Normal);
        Assert.True(Math.Abs(along + screw.SeatDepth) < 1e-6, $"seat should sit SeatDepth below, got {along}");
        Assert.True((located.Normal - seated.SurfaceFrame.Z).Length < 1e-9);

        // And a raw Shape body (an electronic component) seats too.
        var chip = Shape.Box(2, 2, 0.8);
        var chipSeat = board.Seat(chip, world);
        Assert.NotNull(chipSeat.Body);
        Assert.Null(chipSeat.Component);
    }

    // ---- two close TRACES measured through the chart -------------------------

    [Fact]
    public void TwoParallelTraces_TooClose_AreAViolation_Measured_ThroughTheChart()
    {
        // Two different-net conductors running parallel on the tube, a small gap apart — close enough
        // that the certified 3D broad phase cannot clear them, so the DRC must project BOTH traces into
        // a local chart and measure the geodesic gap between the ribbons. (This is the path a pad-pair
        // never exercises, and where a trace whose points lost their barycentric weights would misread.)
        const double clearance = 0.3;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0 };
        double radius = 3, width = 0.4;
        var board = MidBoard.OnMesh(Tube(radius, 6, 128, 48));

        double dphi = (width + 0.5 * clearance) / radius;   // centre arc → edge-to-edge gap = 0.5·c
        var a0 = board.PlacePad("A", new Vector3d(radius, 0, 1.5), 0.4, "A0");
        var a1 = board.PlacePad("A", new Vector3d(radius, 0, 4.5), 0.4, "A1");
        var b0 = board.PlacePad("B", new Vector3d(radius * Math.Cos(dphi), radius * Math.Sin(dphi), 1.5), 0.4, "B0");
        var b1 = board.PlacePad("B", new Vector3d(radius * Math.Cos(dphi), radius * Math.Sin(dphi), 4.5), 0.4, "B1");
        MidRouting.Connect(board, a0, a1, width);
        MidRouting.Connect(board, b0, b1, width);

        var report = MidRouting.Verify(board, rules);
        _out.WriteLine("two traces: " + report);
        // The parallel conductors are half a clearance apart → a definite clearance violation, measured
        // through the local chart (a degenerate trace projection would miss it or misreport it). The
        // trace-vs-trace pair reads the closest ribbon-to-ribbon gap, 0.5·clearance.
        var v = report.Violations.First(x => x.Rule == MidDrcRule.Clearance
            && x.Message.Contains("A0->A1") && x.Message.Contains("B0->B1"));
        Assert.Equal(MidDrcVerdict.Violation, v.Verdict);
        Assert.InRange(v.MeasuredParameter, 0.5 * clearance * 0.80, 0.5 * clearance * 1.20);
        Assert.Empty(report.Uncertain);   // a developable tube never straddles
    }

    // ---- the showcase: a moulded wearable dome -------------------------------

    [Fact]
    public void Showcase_MoudledWearable_SeatsComponents_RoutesConductors_VerifiesClean()
    {
        var scene = MidShowcase.Build(out var report, out int netCount, out int componentCount);
        Assert.NotNull(scene);
        // Every routed net connects its pads along the moulded surface, and the DRC is clean.
        Assert.True(report.Ok, "the showcase should verify clean on the moulded surface: " + report);
        Assert.Empty(report.Ratsnest);
        Assert.True(netCount >= 4, $"the showcase routes {netCount} nets");
        Assert.True(componentCount >= 5, $"the showcase seats {componentCount} components");
    }

    // ---- guards fire ---------------------------------------------------------

    [Fact]
    public void IntrinsicGuards_Fire()
    {
        var board = MidBoard.OnMesh(Torus());
        // An intrinsic board has no (u, v): asking for a global-chart lift is refused by name.
        Assert.Throws<InvalidOperationException>(() => board.TryLift(new Vector2d(0, 0), out _));
        Assert.Throws<InvalidOperationException>(() => board.PlacePad("N", new Vector2d(0, 0), 0.3, "P"));
        // A pad placed intrinsically has no parameter.
        var pad = board.PlacePad("N", TorusPoint(12, 4, 0, 0), 0.3, "P");
        Assert.False(pad.HasParameter);
        Assert.Throws<InvalidOperationException>(() => pad.Parameter);
        // Auto-routing an intrinsic board with nothing to route (one lone pad) leaves it unchanged.
        var routed = MidRouting.Route(board);
        Assert.Empty(routed.RoutedNets);
        Assert.Empty(routed.UnroutedNets);
        Assert.Equal(0, routed.TracesAdded);
    }
}
