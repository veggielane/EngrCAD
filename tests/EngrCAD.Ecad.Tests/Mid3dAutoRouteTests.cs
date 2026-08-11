using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The MID / LDS SURFACE AUTO-ROUTER — the geodesic analogue of the flat autorouter. The verification
/// bar is the flat router's, lifted onto the surface: every auto-routed net CONNECTS its pads along the
/// surface AND passes the exact 3D DRC (clean), or the router REPORTS the net UNROUTABLE by name — it
/// never ships a clearance-violating trace, and a partial result is still DRC-clean. The exact
/// <see cref="Mid3dDrc"/> is the source of truth; the mesh vertex graph only accelerates.
/// </summary>
public sealed class Mid3dAutoRouteTests
{
    private readonly ITestOutputHelper _out;
    public Mid3dAutoRouteTests(ITestOutputHelper output) => _out = output;

    // Comfortable rules (the trace 0.4 is wider than the 0.2 minimum; pads/tracks clear the 0.3 minimum).
    private static readonly DrcRuleSet Rules = DrcRuleSet.Default with { MinCopperClearance = 0.3, MinTraceWidth = 0.2 };
    private static readonly SurfaceRouteOptions Opt = new() { TraceWidth = 0.4, Clearance = 0.3 };
    private const double Land = 0.6;

    // ---- the verification-bar helpers ----------------------------------------

    /// <summary>Asserts the routed board is 3D-DRC-clean (no violations AND nothing left uncertain) — the
    /// never-a-silent-violation guarantee on the surface.</summary>
    private static void AssertClean(MidBoard board)
    {
        var report = MidRouting.Verify(board, Rules);
        Assert.True(report.Ok, "routed surface board must be DRC-clean but had: "
            + string.Join("; ", report.Messages));
    }

    /// <summary>Asserts each named net's copper is joined along the surface (not in the ratsnest) — the
    /// router connected what it was asked to.</summary>
    private static void AssertConnected(MidBoard board, params string[] nets)
    {
        var ratsnest = MidRouting.Verify(board, Rules).Ratsnest;
        foreach (var net in nets)
            Assert.DoesNotContain(net, ratsnest);
    }

    // ---- meshes --------------------------------------------------------------

    private static HalfEdgeMesh Plane(double size, int n)
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

    /// <summary>A plane covering [x0, x1] × [y0, y1] — the unrolled cylinder for the cross-check.</summary>
    private static HalfEdgeMesh PlaneRect(double x0, double x1, double y0, double y1, int nx, int ny)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= ny; j++)
        for (int i = 0; i <= nx; i++)
            positions.Add(new Vector3d(x0 + (x1 - x0) * i / nx, y0 + (y1 - y0) * j / ny, 0));
        var faces = new List<int[]>();
        for (int j = 0; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
            int a = j * (nx + 1) + i;
            faces.Add([a, a + 1, a + nx + 2]);
            faces.Add([a, a + nx + 2, a + nx + 1]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static HalfEdgeMesh Tube(double radius, double height, int around, int along)
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

    private static Vector3d OnTube(double radius, double angle, double z) =>
        new(radius * Math.Cos(angle), radius * Math.Sin(angle), z);

    // ==== 1) a single 2-pin net on a cylinder ================================

    [Fact]
    public void TwoPinNet_OnCylinder_ConnectsAndIsDrcClean()
    {
        double r = 4;
        var board = MidBoard.OnMesh(Tube(r, 10, 128, 64));
        board.PlacePad("N", OnTube(r, 0.0, 5), Land, "A");
        board.PlacePad("N", OnTube(r, 1.2, 5), Land, "B");

        var result = MidRouting.Route(board, Rules, Opt);

        Assert.True(result.FullyRouted, result.ToString());
        Assert.Equal(["N"], result.RoutedNets);
        Assert.True(result.TracesAdded >= 1);
        AssertClean(board);
        AssertConnected(board, "N");
    }

    // ==== 2) a single 2-pin net on a sphere cap ==============================

    [Fact]
    public void TwoPinNet_OnSphereCap_ConnectsAndIsDrcClean()
    {
        double r = 5;
        var sphere = MeshPrimitives.UvSphere(r, 128, 64);
        var board = MidBoard.OnMesh(sphere);
        // Two pads near the pole, a short arc apart — a net routed over the cap.
        double lat = 0.35;   // central angle from the pole
        board.PlacePad("N", new Vector3d(r * Math.Sin(lat), 0, r * Math.Cos(lat)), Land, "A");
        board.PlacePad("N", new Vector3d(-r * Math.Sin(lat), 0, r * Math.Cos(lat)), Land, "B");

        var result = MidRouting.Route(board, Rules, Opt);

        Assert.True(result.FullyRouted, result.ToString());
        Assert.Equal(["N"], result.RoutedNets);
        AssertClean(board);
        AssertConnected(board, "N");
    }

    // ==== 3) several nets that must route AROUND each other ===================

    [Fact]
    public void SeveralNets_RouteAroundEachOther_AllCleanAndConnected()
    {
        var board = MidBoard.OnMesh(Plane(20, 80));
        // Net A is a horizontal wall across the middle; net B must cross it and so routes AROUND its end;
        // net C runs a clear diagonal.
        board.PlacePad("A", new Vector3d(-4, 0, 0), Land, "A0");
        board.PlacePad("A", new Vector3d(4, 0, 0), Land, "A1");
        board.PlacePad("B", new Vector3d(0, -4, 0), Land, "B0");
        board.PlacePad("B", new Vector3d(0, 4, 0), Land, "B1");
        board.PlacePad("C", new Vector3d(-7, -7, 0), Land, "C0");
        board.PlacePad("C", new Vector3d(7, 7, 0), Land, "C1");

        var result = MidRouting.Route(board, Rules, Opt);

        Assert.True(result.FullyRouted, result.ToString());
        Assert.Equal(["A", "B", "C"], result.RoutedNets.OrderBy(x => x).ToList());
        AssertClean(board);
        AssertConnected(board, "A", "B", "C");

        // B genuinely DETOURED around A rather than taking the straight 8-unit crossing: its surface
        // length is well over the direct pad-to-pad distance (8).
        var bTrace = result.Traces.First(t => t.Net == "B");
        _out.WriteLine($"B surface length {bTrace.SurfaceLength:F2} vs direct 8");
        Assert.True(bTrace.SurfaceLength > 8 * 1.2, $"B should detour around A, length {bTrace.SurfaceLength:F2}");
    }

    // ==== 4) a congested board that needs RIP-UP to complete =================

    [Fact]
    public void CongestedBoard_RipUpCompletesWhereGreedyFails()
    {
        // Net A spans the full interior width (boxing net B between it and the blocked rim); net B is a
        // short vertical that must cross A.
        MidBoard MakeBoard()
        {
            var b = MidBoard.OnMesh(Plane(12, 48));
            b.PlacePad("A", new Vector3d(-5, 0, 0), Land, "A0");
            b.PlacePad("A", new Vector3d(5, 0, 0), Land, "A1");
            b.PlacePad("B", new Vector3d(0, -3, 0), Land, "B0");
            b.PlacePad("B", new Vector3d(0, 3, 0), Land, "B1");
            return b;
        }

        // A pure greedy pass (no rip-up) routes A straight and leaves B unrouted — but is still clean.
        var greedyBoard = MakeBoard();
        var greedy = MidRouting.Route(greedyBoard, Rules, Opt with { MaxRipUpIterations = 0 });
        Assert.False(greedy.FullyRouted);
        Assert.Equal(["B"], greedy.UnroutedNets);
        AssertClean(greedyBoard);
        AssertConnected(greedyBoard, "A");

        // Rip-up-and-reroute completes it: B routes ACROSS A, A is ripped up and re-routed around B, and
        // both are clean.
        var ripBoard = MakeBoard();
        var routed = MidRouting.Route(ripBoard, Rules, Opt);
        _out.WriteLine(routed.ToString());
        Assert.True(routed.FullyRouted, routed.ToString());
        Assert.True(routed.RipUps >= 1, "the board should require at least one rip-up");
        AssertClean(ripBoard);
        AssertConnected(ripBoard, "A", "B");
    }

    // ==== 5) a pin boxed in by other-net copper — reported unroutable =========

    [Fact]
    public void BoxedPin_ReportedUnroutable_RestRoutedAndClean()
    {
        var board = MidBoard.OnMesh(Plane(16, 40));
        // N1 sits at the origin, walled in by a ring of other-net obstacle pads a unit away — a clean
        // fixture (edge-to-edge 0.4 > 0.3), but no 0.4-wide gap fits a 0.4-wide trace with clearance.
        board.PlacePad("BOXED", new Vector3d(0, 0, 0), Land, "N1");
        board.PlacePad("BOXED", new Vector3d(5, 5, 0), Land, "N2");
        var ring = new (double x, double y)[]
        {
            (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
        };
        for (int i = 0; i < ring.Length; i++)
            board.PlacePad($"O{i}", new Vector3d(ring[i].x, ring[i].y, 0), Land, $"O{i}");
        // A routable net well clear of the box.
        board.PlacePad("GOOD", new Vector3d(-5, -5, 0), Land, "G0");
        board.PlacePad("GOOD", new Vector3d(5, -5, 0), Land, "G1");

        Assert.True(MidRouting.Verify(board, Rules).Ok, "the fixture must start DRC-clean");

        var result = MidRouting.Route(board, Rules, Opt);

        Assert.Equal(["BOXED"], result.UnroutedNets);   // named, not faked
        Assert.Equal(["GOOD"], result.RoutedNets);      // the rest of the board still routed
        AssertClean(board);                             // and the partial board is clean
        AssertConnected(board, "GOOD");
    }

    // ==== 6) never a silent violation: a dense knot's partial is always clean ==

    [Fact]
    public void PartialResult_IsAlwaysClean_FailingNetsNamed()
    {
        // A dense knot of crossing nets on ONE surface (no vias/layers): whatever the router manages, it
        // is DRC-clean and the rest is named — never a hidden short.
        var board = MidBoard.OnMesh(Plane(12, 48));
        for (int i = 0; i < 5; i++)
        {
            double y = -4 + 2 * i;
            board.PlacePad($"H{i}", new Vector3d(-5, y, 0), Land, $"H{i}a");
            board.PlacePad($"H{i}", new Vector3d(5, y, 0), Land, $"H{i}b");
            double x = -4 + 2 * i;
            board.PlacePad($"V{i}", new Vector3d(x, -5, 0), Land, $"V{i}a");
            board.PlacePad($"V{i}", new Vector3d(x, 5, 0), Land, $"V{i}b");
        }

        var result = MidRouting.Route(board, Rules, Opt);
        _out.WriteLine(result.ToString());

        Assert.False(result.FullyRouted);                    // it genuinely cannot route everything
        AssertClean(board);                                  // yet the partial board is DRC-clean
        AssertConnected(board, [.. result.RoutedNets]);      // every routed net is genuinely connected
        Assert.NotEmpty(result.UnroutedNets);
        Assert.Empty(result.RoutedNets.Intersect(result.UnroutedNets));
    }

    // ==== 7) the cylinder-vs-flat cross-check =================================

    [Fact]
    public void Cylinder_RoutedConnectivity_MatchesTheUnrolledFlatBoard()
    {
        // On a developable cylinder the surface router must route the SAME net list as the unrolled flat
        // board does: same routed set, both DRC-clean, both fully connected. A search need not be
        // bit-identical (the two meshes differ), so the invariant is CONNECTIVITY + cleanliness, not the
        // exact geodesics — the routing analogue of the DRC's developable oracle.
        double r = 4;
        // Cylinder: net A a horizontal wall, net B crossing it (must route around).
        var cyl = MidBoard.OnMesh(Tube(r, 12, 160, 80));
        cyl.PlacePad("A", OnTube(r, 0.5, 6), Land, "A0");
        cyl.PlacePad("A", OnTube(r, 1.5, 6), Land, "A1");
        cyl.PlacePad("B", OnTube(r, 1.0, 4), Land, "B0");
        cyl.PlacePad("B", OnTube(r, 1.0, 8), Land, "B1");

        // The unrolled flat board: arc length x = r·θ, height y = z, same relative layout.
        var flat = MidBoard.OnMesh(PlaneRect(0, r * 2 * Math.PI, 0, 12, 200, 60));
        flat.PlacePad("A", new Vector3d(r * 0.5, 6, 0), Land, "A0");
        flat.PlacePad("A", new Vector3d(r * 1.5, 6, 0), Land, "A1");
        flat.PlacePad("B", new Vector3d(r * 1.0, 4, 0), Land, "B0");
        flat.PlacePad("B", new Vector3d(r * 1.0, 8, 0), Land, "B1");

        var onCyl = MidRouting.Route(cyl, Rules, Opt);
        var onFlat = MidRouting.Route(flat, Rules, Opt);

        // Same connectivity outcome on both.
        Assert.Equal(onFlat.RoutedNets.OrderBy(x => x), onCyl.RoutedNets.OrderBy(x => x));
        Assert.Equal(onFlat.UnroutedNets, onCyl.UnroutedNets);
        Assert.True(onCyl.FullyRouted && onFlat.FullyRouted, $"cyl {onCyl}; flat {onFlat}");
        // Both DRC-clean and both fully connected.
        AssertClean(cyl);
        AssertClean(flat);
        AssertConnected(cyl, "A", "B");
        AssertConnected(flat, "A", "B");
    }

    // ==== 8) determinism ======================================================

    [Fact]
    public void Deterministic_TwoRunsAreIdentical()
    {
        MidBoard MakeBoard()
        {
            var b = MidBoard.OnMesh(Plane(12, 48));
            b.PlacePad("A", new Vector3d(-5, 0, 0), Land, "A0");
            b.PlacePad("A", new Vector3d(5, 0, 0), Land, "A1");
            b.PlacePad("B", new Vector3d(0, -3, 0), Land, "B0");
            b.PlacePad("B", new Vector3d(0, 3, 0), Land, "B1");
            return b;
        }
        var b1 = MakeBoard();
        var b2 = MakeBoard();
        var r1 = MidRouting.Route(b1, Rules, Opt);
        var r2 = MidRouting.Route(b2, Rules, Opt);

        Assert.Equal(r1.RoutedNets, r2.RoutedNets);
        Assert.Equal(r1.UnroutedNets, r2.UnroutedNets);
        Assert.Equal(r1.RipUps, r2.RipUps);
        Assert.Equal(r1.TracesAdded, r2.TracesAdded);
        // The routed geometry is identical vertex for vertex — same mesh, same search, same order.
        Assert.Equal(b1.Traces.Count, b2.Traces.Count);
        for (int t = 0; t < b1.Traces.Count; t++)
        {
            var p1 = b1.Traces[t].Runs[0].Points;
            var p2 = b2.Traces[t].Runs[0].Points;
            Assert.Equal(p1.Count, p2.Count);
            for (int i = 0; i < p1.Count; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(p1[i].X), BitConverter.DoubleToInt64Bits(p2[i].X));
        }
    }

    // ==== 9) nothing to route → the board is unchanged ========================

    [Fact]
    public void NothingToRoute_LeavesTheBoardUnchanged()
    {
        var board = MidBoard.OnMesh(Plane(12, 48));
        // Two pads of one net so close they already touch → connected → nothing to route.
        board.PlacePad("N", new Vector3d(0, 0, 0), Land, "A");
        board.PlacePad("N", new Vector3d(0.2, 0, 0), Land, "B");
        int before = board.Traces.Count;

        var result = MidRouting.Route(board, Rules, Opt);

        Assert.Empty(result.RoutedNets);
        Assert.Empty(result.UnroutedNets);
        Assert.Equal(0, result.TracesAdded);
        Assert.Equal(before, board.Traces.Count);
    }

    // ==== 10) the headline: a moulded dome routes itself cleanly ==============

    [Fact]
    public void MouldedDome_AutoRoutesItselfCleanly()
    {
        // A wide, low ellipsoidal dome (a wearable puck) carrying a star of nets — the board a designer
        // would auto-route rather than place-and-verify. It routes fully, clean and connected.
        var dome = MeshPrimitives.UvSphere(10, 200, 100).Transformed(Matrix4d.CreateScale(new Vector3d(1.5, 1.5, 0.62)));
        var board = MidBoard.OnMesh(dome);

        Vector3d Above(double x, double y)
        {
            double rx = 10 * 1.5, rz = 10 * 0.62;
            double t = 1 - (x * x + y * y) / (rx * rx);
            return new Vector3d(x, y, rz * Math.Sqrt(Math.Max(t, 0)) + 0.01);
        }
        var nets = new (string Net, Vector3d A, Vector3d B)[]
        {
            ("5V",  Above(-1.7, -9.0), Above(-1.7, -3.2)),
            ("GND", Above( 1.7, -9.0), Above( 1.7, -3.2)),
            ("D1",  Above(-3.2,  0.9), Above(-8.8,  2.5)),
            ("D2",  Above( 3.2,  0.9), Above( 8.8,  2.5)),
        };
        foreach (var (net, a, b) in nets)
        {
            board.PlacePad(net, a, Land, $"{net}.a");
            board.PlacePad(net, b, Land, $"{net}.b");
        }

        var result = MidRouting.Route(board, Rules, Opt);
        _out.WriteLine(result.ToString());

        Assert.True(result.FullyRouted, "the dome should route fully: " + result);
        Assert.Equal(4, result.RoutedNets.Count);
        AssertClean(board);
        AssertConnected(board, "5V", "GND", "D1", "D2");
    }
}
