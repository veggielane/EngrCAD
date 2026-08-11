using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The MID / LDS 3D surface-routing stage. The decisive test is the DEVELOPABLE oracle: on a cylinder
/// the exp map is an isometry, so the 3D DRC in exp-map coordinates must AGREE with the flat unrolled
/// 2D DRC — verdicts and measured separations, bit for bit. Then, on a doubly-curved patch (a sphere
/// cap), the distortion is REPORTED and FOLDED so a pair that passes flat but whose worst-scale surface
/// clearance drops below the rule is REFUSED (the conservative band), not passed false-precise.
/// </summary>
public class Mid3dRoutingTests
{
    // ---- meshes --------------------------------------------------------------

    /// <summary>A flat grid in z = 0, centred at the origin, so the exp map is the identity and a pad
    /// at (u, v) lifts to (u, v, 0). Its seed is the centre vertex.</summary>
    private static (HalfEdgeMesh Mesh, int Seed) Plane(int n = 40, double size = 8)
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
        int seed = (n / 2) * (n + 1) + n / 2;   // the centre vertex, at the origin
        return (HalfEdgeMesh.Build(positions, faces), seed);
    }

    /// <summary>A finely tessellated open tube — a DEVELOPABLE surface whose exp map unrolls with a few
    /// 1e-4 of distortion. Seed mid-height at angle 0; +u along the azimuth tangent (+y there), +v up.</summary>
    private static (HalfEdgeMesh Mesh, int Seed) Tube(double radius = 2, double height = 6, int around = 64, int along = 40)
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
        int seed = (along / 2) * around;   // i = 0, mid height
        return (HalfEdgeMesh.Build(positions, faces), seed);
    }

    private static (HalfEdgeMesh Mesh, int Seed) SphereCap(double radius = 5)
    {
        var sphere = MeshPrimitives.UvSphere(radius, 64, 32);
        int pole = sphere.Vertices.OrderByDescending(v => v.Position.Z).First().Index;
        return (sphere, pole);
    }

    /// <summary>Places the SAME pads and traces on two boards (the fixtures the developable oracle
    /// compares). Different-net pads N1/N2, a same-net trace, and a clear pair well apart.</summary>
    private static void Populate(MidBoard board, double clearance)
    {
        double land = 0.3, width = 0.25;
        // A clear pair: two nets 2·clearance apart edge to edge.
        board.PlacePad("N1", new Vector2d(0, 0), land, "P1");
        board.PlacePad("N2", new Vector2d(land + 2 * clearance, 0), land, "P2");
        // A violation pair: two nets half a clearance apart edge to edge.
        board.PlacePad("N3", new Vector2d(0, 1.0), land, "P3");
        board.PlacePad("N4", new Vector2d(land + 0.5 * clearance, 1.0), land, "P4");
        // A short: two nets overlapping.
        board.PlacePad("N5", new Vector2d(0, -1.0), land, "P5");
        board.PlacePad("N6", new Vector2d(land * 0.5, -1.0), land, "P6");
        // A same-net pad and a trace joining it (never flagged, and it makes the net routed).
        var a = board.PlacePad("SIG", new Vector2d(-1.2, 0), land, "S1");
        var b = board.PlacePad("SIG", new Vector2d(-1.2, 0.8), land, "S2");
        MidRouting.Connect(board, a, b, width);
    }

    // ---- the developable oracle (the headline) -------------------------------

    [Fact]
    public void Cylinder_3dDrcAgreesWithTheUnrolledFlatDrc_VerdictsAndClearancesBitForBit()
    {
        const double clearance = 0.15;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance };

        var (tube, tubeSeed) = Tube();
        var (plane, planeSeed) = Plane();
        var cylinder = MidBoard.OnSurface(tube, tubeSeed, Vector3d.UnitY, radius: 2.5);
        var flat = MidBoard.OnSurface(plane, planeSeed, Vector3d.UnitX, radius: 5.0);
        Populate(cylinder, clearance);
        Populate(flat, clearance);

        // The cylinder really is developable-clean: the exp map carries a few 1e-4 of distortion, so
        // the fold cannot flip a verdict away from the (generously spaced) fixture's band.
        Assert.True(cylinder.MaxDistortion < 0.01,
            $"the tube's exp map distortion {cylinder.MaxDistortion:E3} is larger than a developable surface's");

        var onCylinder = Mid3dDrc.Check(cylinder, rules);
        var onFlat = Mid3dDrc.Check(flat, rules);

        // Nothing is uncertain on either — a developable surface never straddles.
        Assert.Empty(onCylinder.Uncertain);
        Assert.Empty(onFlat.Uncertain);

        // The violation lists are identical: same count, and each finding equal in rule, verdict and
        // location, with the measured PARAMETER separation bit-identical (both measure it on the SAME
        // (u, v) geometry with the same band-independent cap). The MESSAGE differs on purpose — the
        // cylinder honestly reports a surface-clearance BAND ([0.075, 0.0751]) where the flat reports a
        // single value, which is the distortion fold showing through — so it is not compared.
        Assert.Equal(onFlat.Violations.Count, onCylinder.Violations.Count);
        static (double, double, int) Key(MidDrcViolation v) => (v.ParameterLocation.X, v.ParameterLocation.Y, (int)v.Rule);
        var flatSorted = onFlat.Violations.OrderBy(Key).ToList();
        var cylSorted = onCylinder.Violations.OrderBy(Key).ToList();
        for (int i = 0; i < flatSorted.Count; i++)
        {
            Assert.Equal(flatSorted[i].Rule, cylSorted[i].Rule);
            Assert.Equal(flatSorted[i].Verdict, cylSorted[i].Verdict);
            Assert.Equal(flatSorted[i].ParameterLocation, cylSorted[i].ParameterLocation);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(flatSorted[i].MeasuredParameter),
                BitConverter.DoubleToInt64Bits(cylSorted[i].MeasuredParameter));
        }

        // And the fixture actually exercises the DRC: a short and a clearance violation, both found.
        Assert.Contains(onCylinder.Violations, v => v.Rule == MidDrcRule.Short);
        Assert.Contains(onCylinder.Violations, v => v.Rule == MidDrcRule.Clearance);
        // The same-net trace routes its net — no ratsnest for SIG.
        Assert.DoesNotContain("SIG", onCylinder.Ratsnest);
    }

    [Fact]
    public void Plane_3dDrcMatchesHandComputedClearance()
    {
        const double clearance = 0.2;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0 };
        var (plane, seed) = Plane();
        var board = MidBoard.OnSurface(plane, seed, Vector3d.UnitX, radius: 5.0);

        double land = 0.4;
        // Centre-to-centre = land + gap, so the edge-to-edge gap is exactly `gap`.
        board.PlacePad("A", new Vector2d(0, 0), land, "A");
        board.PlacePad("B", new Vector2d(land + 0.5 * clearance, 0), land, "B");     // gap 0.5c → violation
        board.PlacePad("C", new Vector2d(0, 3), land, "C");
        board.PlacePad("D", new Vector2d(land + 3 * clearance, 3), land, "D");        // gap 3c → clear

        var report = Mid3dDrc.Check(board, rules);
        var clearances = report.OfRule(MidDrcRule.Clearance).ToList();
        Assert.Single(clearances);                                    // exactly the A–B pair
        Assert.Equal(MidDrcVerdict.Violation, clearances[0].Verdict);
        // The measured surface separation on a plane (scale 1) is the parameter gap = 0.5·clearance.
        Assert.Equal(0.5 * clearance, clearances[0].MeasuredParameter, 6);
        Assert.Equal(0.5 * clearance, clearances[0].SurfaceSeparationMin, 6);
    }

    // ---- the doubly-curved patch: distortion reported and folded -------------

    [Fact]
    public void SphereCap_TraceReportsItsDistortion()
    {
        var (sphere, pole) = SphereCap(5);
        var board = MidBoard.OnSurface(sphere, pole, Vector3d.UnitX, radius: 3.05);

        // A trace running radially out toward the cap edge, where the sphere shrinks a full ring's
        // circumference below 2π·(geodesic) — the distortion the exp map cannot avoid.
        var trace = board.PlaceTrace("N", [new Vector2d(0.5, 0), new Vector2d(2.6, 0)], 0.2, "T");
        Assert.Equal(0, trace.UnmappedPoints);
        // Radial spacing is faithful, so a radial trace's scale is near 1 — but the map's global
        // distortion (a ring's circumference) is several percent, reported by the board.
        Assert.True(board.MaxDistortion > 0.02,
            $"a 35° sphere cap should distort by more than 2%, got {board.MaxDistortion:E3}");
        Assert.True(board.MaxDistortion < 0.15,
            $"the distortion {board.MaxDistortion:E3} is larger than the exp map's stated cap-scale figure");

        // A tangential trace near the edge carries the shrink into its OWN spacing (MinScale < 1).
        var ring = board.PlaceTrace("M", RingArc(2.6, -0.4, 0.4, 12), 0.2, "R");
        Assert.True(ring.MinScale < 0.99, $"a tangential edge trace should read MinScale < 1, got {ring.MinScale:F4}");
        Assert.True(ring.MinScale > 0.9, $"the shrink {ring.MinScale:F4} exceeds the exp map's ≤5% radial figure by a lot");
    }

    [Fact]
    public void SphereCap_APairThatPassesFlatIsRefusedWhereTheSurfaceShrinks()
    {
        const double clearance = 0.15;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance, MinTraceWidth = 0 };
        var (sphere, pole) = SphereCap(5);
        var sphereBoard = MidBoard.OnSurface(sphere, pole, Vector3d.UnitX, radius: 3.05);
        var (plane, seed) = Plane(40, 12);
        var flatBoard = MidBoard.OnSurface(plane, seed, Vector3d.UnitX, radius: 5.0);

        // Two different-net pads near the cap edge (high distortion), spaced just ABOVE the flat limit
        // in (u, v) but along the SHRINKING tangential direction, so the surface clearance could drop
        // below it. The same (u, v) on the plane passes cleanly.
        double land = 0.3;
        var c0 = new Vector2d(2.6, -0.5 * (land + 1.05 * clearance));
        var c1 = new Vector2d(2.6, 0.5 * (land + 1.05 * clearance));
        Populate2(sphereBoard, c0, c1, land);
        Populate2(flatBoard, c0, c1, land);

        var onFlat = Mid3dDrc.Check(flatBoard, rules);
        var onSphere = Mid3dDrc.Check(sphereBoard, rules);

        // The flat unrolled sheet certifies the pair clean — the parameter gap is above the limit.
        Assert.True(onFlat.Ok, "the flat board should pass: " + onFlat);
        // The sphere cannot: the shrink pulls the worst-case surface clearance into the band, so the DRC
        // REFUSES with the uncertainty stated rather than passing false-precise.
        Assert.False(onSphere.Ok);
        Assert.NotEmpty(onSphere.Uncertain);
        Assert.Contains(onSphere.Uncertain, u => u.Rule == MidDrcRule.Clearance && u.MinScale < 1);
    }

    [Fact]
    public void SphereCap_TraceWidthIsFoldedThroughTheShrink()
    {
        // A trace just wide enough on the flat sheet, laid tangentially where the surface shrinks:
        // its surface width band dips below the rule, so the DRC cannot certify it.
        const double minWidth = 0.15;
        var rules = DrcRuleSet.Default with { MinTraceWidth = minWidth, MinCopperClearance = 0 };
        var (sphere, pole) = SphereCap(5);
        var sphereBoard = MidBoard.OnSurface(sphere, pole, Vector3d.UnitX, radius: 3.05);
        var (plane, seed) = Plane(40, 12);
        var flatBoard = MidBoard.OnSurface(plane, seed, Vector3d.UnitX, radius: 6.0);

        sphereBoard.PlaceTrace("N", RingArc(2.6, -0.4, 0.4, 12), 0.16, "T");
        flatBoard.PlaceTrace("N", RingArc(2.6, -0.4, 0.4, 12), 0.16, "T");

        var onFlat = Mid3dDrc.Check(flatBoard, rules);
        var onSphere = Mid3dDrc.Check(sphereBoard, rules);
        // 0.16 mm clears the 0.15 mm rule on the flat sheet.
        Assert.Empty(onFlat.OfRule(MidDrcRule.TraceWidth));
        // On the surface the shrink pulls its worst-case surface width below the rule → refused.
        Assert.Contains(onSphere.Uncertain, u => u.Rule == MidDrcRule.TraceWidth && u.MinScale < 1);
    }

    private static void Populate2(MidBoard board, Vector2d c0, Vector2d c1, double land)
    {
        board.PlacePad("X", c0, land, "X");
        board.PlacePad("Y", c1, land, "Y");
    }

    private static Vector2d[] RingArc(double r, double a0, double a1, int n)
    {
        var pts = new Vector2d[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double a = a0 + (a1 - a0) * i / n;
            pts[i] = new Vector2d(r * Math.Cos(a), r * Math.Sin(a));
        }
        return pts;
    }

    // ---- pad-endpoint exactness ----------------------------------------------

    [Fact]
    public void TraceEndpointLandsExactlyOnItsPad()
    {
        var (tube, seed) = Tube();
        var board = MidBoard.OnSurface(tube, seed, Vector3d.UnitY, radius: 2.5);
        var pad = board.PlacePad("N", new Vector2d(0.9, 0.6), 0.3, "P");
        var trace = board.PlaceTrace("N", [new Vector2d(0.9, 0.6), new Vector2d(0.9, 1.4)], 0.2, "T");

        var padPoint = pad.SurfacePoint;
        var traceStart = trace.Runs[0].Points[0];
        // Same (u, v), same exp-map inverse (one map per board) → the same surface point, exactly.
        Assert.Equal(BitConverter.DoubleToInt64Bits(padPoint.X), BitConverter.DoubleToInt64Bits(traceStart.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(padPoint.Y), BitConverter.DoubleToInt64Bits(traceStart.Y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(padPoint.Z), BitConverter.DoubleToInt64Bits(traceStart.Z));
    }

    // ---- breaking past the map -----------------------------------------------

    [Fact]
    public void ATraceRunningOffTheMapBreaksAndIsCounted()
    {
        var (sphere, pole) = SphereCap(5);
        // A small map, so a trace reaching outward leaves it.
        var board = MidBoard.OnSurface(sphere, pole, Vector3d.UnitX, radius: 1.5);
        var trace = board.PlaceTrace("N",
            [new Vector2d(0.3, 0), new Vector2d(0.6, 0), new Vector2d(6, 0), new Vector2d(6.3, 0)], 0.2, "T");
        Assert.True(trace.UnmappedPoints > 0, "the far points should leave the map");
        Assert.Contains(trace.Runs, r => r.Parameters.Count >= 2);   // the on-map stretch survived
    }

    // ---- determinism ---------------------------------------------------------

    [Fact]
    public void Check_IsDeterministic()
    {
        const double clearance = 0.15;
        var rules = DrcRuleSet.Default with { MinCopperClearance = clearance };
        var (tube, seed) = Tube();
        var board = MidBoard.OnSurface(tube, seed, Vector3d.UnitY, radius: 2.5);
        Populate(board, clearance);
        var a = Mid3dDrc.Check(board, rules);
        var b = Mid3dDrc.Check(board, rules);
        Assert.Equal(a.Violations.Count, b.Violations.Count);
        for (int i = 0; i < a.Violations.Count; i++)
        {
            Assert.Equal(a.Violations[i].Message, b.Violations[i].Message);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.Violations[i].MeasuredParameter),
                BitConverter.DoubleToInt64Bits(b.Violations[i].MeasuredParameter));
        }
        Assert.Equal(a.Ratsnest, b.Ratsnest);
    }

    // ---- consuming SurfaceDecoration -----------------------------------------

    [Fact]
    public void SurfaceTrace_AgreesWithSurfaceDecorationsOwnLift()
    {
        var (tube, seed) = Tube();
        var board = MidBoard.OnSurface(tube, seed, Vector3d.UnitY, radius: 2.5);
        var trace = board.PlaceTrace("N",
            [new Vector2d(0.2, 0.1), new Vector2d(0.8, 0.4), new Vector2d(1.2, 1.0)], 0.2, "T");

        // The board's own inverse map and SurfaceDecoration.Wrap use the SAME MeshLocalParam
        // parameters, so the lifted centre-lines agree to arithmetic precision — the honest cross-check
        // that the MID board consumes the surface-decoration primitive rather than a private map.
        var curve = trace.AsSurfaceCurve();
        var boardRun = trace.Runs[0].Points;
        var decorationRun = curve.Runs[0];
        Assert.Equal(boardRun.Count, decorationRun.Count);
        for (int i = 0; i < boardRun.Count; i++)
            Assert.True(boardRun[i].DistanceTo(decorationRun[i]) < 1e-9,
                $"point {i}: board {boardRun[i]} vs decoration {decorationRun[i]}");
        // And the distortion the board measures matches the decoration's reported scale.
        Assert.Equal(curve.MinScale, trace.MinScale, 6);
        Assert.Equal(curve.MaxScale, trace.MaxScale, 6);
    }

    // ---- the exported conductor ----------------------------------------------

    [Fact]
    public void Conductor_IsAClosedSolidThatRoundTripsThroughStl()
    {
        var (tube, seed) = Tube();
        var board = MidBoard.OnSurface(tube, seed, Vector3d.UnitY, radius: 2.5);
        var trace = board.PlaceTrace("N",
            [new Vector2d(0, 0), new Vector2d(0.6, 0), new Vector2d(1.2, 0.4)], 0.3, "T");

        var conductor = trace.Conductor(thickness: 0.05);
        var mesh = conductor.ToMesh();
        Assert.Empty(mesh.BoundaryLoops());          // closed
        Assert.True(mesh.Volume() > 0, $"the conductor should have positive volume, got {mesh.Volume()}");

        // Round-trips through a real binary STL (the export the task asks for).
        using var stream = new MemoryStream();
        StlWriter.Write(mesh, stream);
        stream.Position = 0;
        var read = StlReader.Read(stream);
        Assert.NotNull(read.Mesh);
    }

    // ---- seating a catalogue component ---------------------------------------

    [Fact]
    public void Seat_PosesAComponentOnTheSurface()
    {
        var (tube, seed) = Tube();
        var board = MidBoard.OnSurface(tube, seed, Vector3d.UnitY, radius: 2.5);
        var uv = new Vector2d(0.5, 0.5);
        board.TryLift(uv, out var surface, out var normal);

        var screw = StandardComponents.CapScrew(6, 12);
        var seated = board.Seat(screw, uv);

        // The body is posed in the surface's own tangent frame: its seat origin sits on (or just below,
        // by SeatDepth) the surface point, along the surface normal.
        double along = (seated.SeatFrame.Origin - surface).Dot(normal);
        Assert.True(Math.Abs(along + screw.SeatDepth) < 1e-6,
            $"the seat should sit SeatDepth below the surface along its normal, got {along}");
        // The seat frame's Z is the surface normal (renormalized by FromZX, so equal to a tolerance).
        Assert.True((normal - seated.SurfaceFrame.Z).Length < 1e-9);
    }

    // ---- guards fire ---------------------------------------------------------

    [Fact]
    public void Guards_Fire()
    {
        var (sphere, pole) = SphereCap(5);
        var board = MidBoard.OnSurface(sphere, pole, Vector3d.UnitX, radius: 1.0);

        // A pad off the map is refused by name.
        var ex = Assert.Throws<ArgumentException>(() => board.PlacePad("N", new Vector2d(6, 0), 0.3, "P"));
        Assert.Contains("outside the exp map", ex.Message);
        // Placing by pin with no schematic is refused.
        var (plane, seed) = Plane();
        var noSchematic = MidBoard.OnSurface(plane, seed, Vector3d.UnitX, radius: 5.0);
        Assert.Throws<InvalidOperationException>(() =>
            noSchematic.PlacePin(default, new Vector2d(0, 0), 0.3));
        // Auto-routing runs on an INTRINSIC board — a global-chart board is refused BY NAME.
        var ex2 = Assert.Throws<ArgumentException>(() => MidRouting.Route(board));
        Assert.Contains("OnMesh", ex2.Message);
    }

    // ---- the one-declaration rule (a real schematic) -------------------------

    [Fact]
    public void PlacePin_ResolvesTheNetFromTheSchematic()
    {
        var sch = new Schematic("MID");
        var def = new PartDefinition("R", "R", [new Pin("1", "a", PinType.Passive), new Pin("2", "b", PinType.Passive)]);
        var r1 = sch.Add("R1", def);
        var r2 = sch.Add("R2", def);
        sch.Connect("SIG", r1.Pin("2"), r2.Pin("1"));

        var (plane, seed) = Plane();
        var board = MidBoard.OnSurface(plane, seed, Vector3d.UnitX, radius: 5.0, schematic: sch);
        var p1 = board.PlacePin(r1.Pin("2"), new Vector2d(0, 0), 0.3);
        var p2 = board.PlacePin(r2.Pin("1"), new Vector2d(1, 0), 0.3);
        // Both pads carry the resolved net — the pad's net IS its pin's net.
        Assert.Equal("SIG", p1.Net);
        Assert.Equal("SIG", p2.Net);
        // A same-net pair is never a short, even overlapping.
        var overlap = board.PlacePin(r1.Pin("1"), new Vector2d(0.05, 0), 0.3);  // NoConnect? actually no net → null
        Assert.Null(overlap.Net);   // R1.1 is on no net → its own net
    }
}
