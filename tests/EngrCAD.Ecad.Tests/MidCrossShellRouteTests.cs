using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// CROSS-SHELL MID/LDS AUTO-ROUTING — the surface analogue of the flat PCB router's layer-changing via. A
/// net whose pads are on DIFFERENT shells, or that must hop to the other shell to pass an obstacle, is
/// routed over the union of both shells' vertex graphs plus via edges, and a through-shell via is placed
/// where the route changes shell. The bar is the flat router's, lifted: every routed net CONNECTS across
/// the shells AND passes the exact multi-shell 3D DRC (clean), or it is reported UNROUTABLE by name — it
/// never ships a clearance-violating trace or via, and a partial result is still clean. The exact
/// <see cref="Mid3dDrc"/> is the source of truth; the vertex graph only accelerates.
/// </summary>
public sealed class MidCrossShellRouteTests
{
    private readonly ITestOutputHelper _out;
    public MidCrossShellRouteTests(ITestOutputHelper output) => _out = output;

    // Comfortable rules (the trace 0.4 is wider than the 0.2 minimum; pads/tracks clear the 0.3 minimum).
    private static readonly DrcRuleSet Rules = DrcRuleSet.Default with { MinCopperClearance = 0.3, MinTraceWidth = 0.2 };
    private static readonly SurfaceRouteOptions Opt = new() { TraceWidth = 0.4, Clearance = 0.3, ViaPadDiameter = 0.5 };
    private const double Land = 0.6;

    // ---- the verification-bar helpers ----------------------------------------

    private static void AssertClean(MidStack stack)
    {
        var report = stack.Check(Rules);
        Assert.True(report.Ok, "routed two-shell stack must be DRC-clean but had: "
            + string.Join("; ", report.Messages));
    }

    private static void AssertConnected(MidStack stack, params string[] nets)
    {
        var conn = stack.Connectivity();
        foreach (var net in nets)
            Assert.True(conn.Of(net).IsConnected, $"net '{net}' should be connected across the shells");
    }

    // ---- meshes --------------------------------------------------------------

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

    // ==== 1) a cross-shell 2-pin net routes with EXACTLY ONE via ===============

    [Fact]
    public void CrossShellTwoPin_RoutesWithExactlyOneVia_BothSegmentsCleanAndConnected()
    {
        double r = 4, t = 0.6;
        var stack = MidStack.TwoShell(Tube(r, 10, 96, 48), t);
        // An OUTER pad and an INNER pad of one net — the route must change shell, so exactly one via.
        stack.Outer.PlacePad("SIG", OnTube(r, 0.0, 4.0), Land, "SIG.out");
        stack.Inner.PlacePad("SIG", OnTube(r - t, 0.8, 6.0), Land, "SIG.in");

        var result = MidRouting.Route(stack, Rules, Opt);
        _out.WriteLine(result.ToString());

        Assert.True(result.FullyRouted, result.ToString());
        Assert.Equal(["SIG"], result.RoutedNets);
        Assert.Equal(1, result.ViasAdded);            // exactly one shell change
        Assert.True(result.TracesAdded >= 1);
        AssertClean(stack);
        AssertConnected(stack, "SIG");

        // The via lands ON the surface where the route transitions, with one pad on each shell it spans.
        var via = Assert.Single(result.Vias);
        Assert.Equal("SIG", via.Net);
        Assert.Equal(2, via.Pads.Count);
        // The via's outer/inner feet are corresponding radial points (the developable stack), so its outer
        // foot is on the outer wall and its inner foot on the inner wall.
        Assert.Equal(r, Math.Sqrt(via.OuterPoint.X * via.OuterPoint.X + via.OuterPoint.Y * via.OuterPoint.Y), 1e-6);
        Assert.Equal(r - t, Math.Sqrt(via.InnerPoint.X * via.InnerPoint.X + via.InnerPoint.Y * via.InnerPoint.Y), 1e-6);
    }

    // ==== 2) an obstacle hop: blocked on one shell, route through the other ====

    [Fact]
    public void ObstacleHop_BlockedOnOneShell_RoutesThroughTheOther_SingleShellIsUnroutable()
    {
        double r = 4, t = 0.6;
        var mesh = Tube(r, 8, 96, 48);
        const int ring = 20;

        // SIG's two pads on the OUTER shell, above and below a full ring of distinct-net pads at z = 4 that
        // encircles the tube and blocks every vertical outer path. The only route hops to the inner shell.
        static void Populate(MidBoard board, double radius)
        {
            board.PlacePad("SIG", OnTube(radius, 0.0, 2.0), Land, "SIG.lo");
            board.PlacePad("SIG", OnTube(radius, 0.0, 6.0), Land, "SIG.hi");
            for (int i = 0; i < ring; i++)
                board.PlacePad($"O{i}", OnTube(radius, 2 * Math.PI * i / ring, 4.0), Land, $"O{i}");
        }

        var stack = MidStack.TwoShell(mesh, t);
        Populate(stack.Outer, r);
        Assert.True(stack.Check(Rules).Ok, "the fixture must start DRC-clean");

        var result = MidRouting.Route(stack, Rules, Opt);
        _out.WriteLine(result.ToString());
        Assert.True(result.FullyRouted, "SIG should route by hopping shells: " + result);
        Assert.Equal(2, result.ViasAdded);            // out to the inner shell and back
        AssertClean(stack);
        AssertConnected(stack, "SIG");

        // The MUTATION that proves the cross-shell capability is what routed it: the SAME fixture on a
        // SINGLE shell — nothing to hop to — is unroutable.
        var single = MidBoard.OnMesh(mesh);
        Populate(single, r);
        var flat = MidRouting.Route(single, Rules, Opt);
        _out.WriteLine("single-shell: " + flat);
        Assert.Equal(["SIG"], flat.UnroutedNets);     // blocked on the one shell, nowhere to hop
    }

    // ==== 3) the DRC is truth: several cross-shell nets route clean, deterministically =

    [Fact]
    public void SeveralCrossShellNets_AllRouteCleanAndConnected_Deterministically()
    {
        double r = 4, t = 0.6;
        var mesh = Tube(r, 14, 96, 64);

        MidStack Make()
        {
            var s = MidStack.TwoShell(mesh, t);
            // Three well-separated nets, each with an OUTER pad and an INNER pad — each must change shell.
            for (int i = 0; i < 3; i++)
            {
                double z = 3 + 3.5 * i;
                double ang = 0.5 * i;
                s.Outer.PlacePad($"N{i}", OnTube(r, ang, z), Land, $"N{i}.out");
                s.Inner.PlacePad($"N{i}", OnTube(r - t, ang + 0.7, z + 1.2), Land, $"N{i}.in");
            }
            return s;
        }

        var stack = Make();
        var result = MidRouting.Route(stack, Rules, Opt);
        _out.WriteLine(result.ToString());
        Assert.True(result.FullyRouted, result.ToString());
        Assert.Equal(3, result.RoutedNets.Count);
        Assert.True(result.ViasAdded >= 3, "each cross-shell net needs at least one via");
        AssertClean(stack);                                       // every committed route + via is certified
        AssertConnected(stack, "N0", "N1", "N2");

        // Determinism: two runs place identical geometry, vertex for vertex.
        var a = Make();
        var ra = MidRouting.Route(a, Rules, Opt);
        var b = Make();
        var rb = MidRouting.Route(b, Rules, Opt);
        Assert.Equal(ra.RoutedNets, rb.RoutedNets);
        Assert.Equal(ra.UnroutedNets, rb.UnroutedNets);
        Assert.Equal(ra.ViasAdded, rb.ViasAdded);
        Assert.Equal(ra.TracesAdded, rb.TracesAdded);
        Assert.Equal(ra.RipUps, rb.RipUps);
        Assert.Equal(a.Outer.Traces.Count, b.Outer.Traces.Count);
        Assert.Equal(a.Inner.Traces.Count, b.Inner.Traces.Count);
        for (int shell = 0; shell < a.ShellCount; shell++)
        {
            var ta = a.Shell(shell).Traces;
            var tb = b.Shell(shell).Traces;
            for (int ti = 0; ti < ta.Count; ti++)
            {
                var p1 = ta[ti].Runs[0].Points;
                var p2 = tb[ti].Runs[0].Points;
                Assert.Equal(p1.Count, p2.Count);
                for (int i = 0; i < p1.Count; i++)
                    Assert.Equal(BitConverter.DoubleToInt64Bits(p1[i].X), BitConverter.DoubleToInt64Bits(p2[i].X));
            }
        }
    }

    // ==== 4) a walled-in pin blocked on BOTH shells — reported unroutable ======

    [Fact]
    public void WalledInPin_BlockedOnBothShells_ReportedUnroutable_RestRoutedAndClean()
    {
        double r = 4, t = 0.6;
        var stack = MidStack.TwoShell(Tube(r, 10, 96, 48), t);

        // BOXED has a pad caged on the OUTER shell whose corresponding INNER region is caged too, so it can
        // neither route out on its own shell nor escape by hopping (the via lands in the inner cage); its
        // other pad is clear but unreachable.
        stack.Outer.PlacePad("BOXED", OnTube(r, 0.0, 4.0), Land, "BOX.a");
        stack.Outer.PlacePad("BOXED", OnTube(r, Math.PI, 2.0), Land, "BOX.b");
        Cage(stack.Outer, r, 0.0, 4.0, "ocage");
        Cage(stack.Inner, r - t, 0.0, 4.0, "icage");

        // GOOD is a cross-shell net well clear of the cages — it routes.
        stack.Outer.PlacePad("GOOD", OnTube(r, Math.PI, 7.0), Land, "G.out");
        stack.Inner.PlacePad("GOOD", OnTube(r - t, Math.PI + 0.6, 8.5), Land, "G.in");

        Assert.True(stack.Check(Rules).Ok, "the fixture must start DRC-clean");

        var result = MidRouting.Route(stack, Rules, Opt);
        _out.WriteLine(result.ToString());
        Assert.Contains("BOXED", result.UnroutedNets);   // named, not faked
        Assert.Contains("GOOD", result.RoutedNets);       // the rest of the board still routed
        AssertClean(stack);                               // and the partial board is clean
        AssertConnected(stack, "GOOD");
        Assert.Empty(result.RoutedNets.Intersect(result.UnroutedNets));
    }

    /// <summary>Eight distinct-net obstacle pads sealing a box around the (angle, z) location on a tube of
    /// the given radius — tight enough that no gap fits a 0.4-wide trace with clearance.</summary>
    private static void Cage(MidBoard board, double radius, double angle, double z, string prefix)
    {
        var offs = new (double dAng, double dz)[]
        {
            (0.28, 0), (-0.28, 0), (0, 1.1), (0, -1.1),
            (0.28, 1.1), (0.28, -1.1), (-0.28, 1.1), (-0.28, -1.1),
        };
        for (int i = 0; i < offs.Length; i++)
            board.PlacePad($"{prefix}{i}", OnTube(radius, angle + offs[i].dAng, z + offs[i].dz), Land, $"{prefix}{i}");
    }

    // ==== 5) a same-shell net on a stack routes WITHOUT a via ==================

    [Fact]
    public void SameShellNet_OnAStack_RoutesWithoutAVia_WhenAClearPathExists()
    {
        double r = 4, t = 0.6;
        var stack = MidStack.TwoShell(Tube(r, 10, 96, 48), t);
        // Both pads on the OUTER shell with a clear geodesic between them — the via penalty keeps it on one
        // shell, so no via is placed.
        stack.Outer.PlacePad("N", OnTube(r, 0.0, 5.0), Land, "A");
        stack.Outer.PlacePad("N", OnTube(r, 1.2, 5.0), Land, "B");

        var result = MidRouting.Route(stack, Rules, Opt);
        _out.WriteLine(result.ToString());
        Assert.True(result.FullyRouted, result.ToString());
        Assert.Equal(0, result.ViasAdded);            // no shell change was needed
        AssertClean(stack);
        AssertConnected(stack, "N");
    }

    // ==== 5b) a congested stack completes by RIP-UP when hopping is blocked too ====

    [Fact]
    public void CongestedStack_RipUpCompletes_WhenHoppingIsBlockedOnBothShells()
    {
        double t = 0.6;
        var mesh = Plane(12, 48);

        // Net A spans the outer width (boxing net B between it and the blocked rim); net B is a short
        // vertical that must cross A. The INNER shell carries a wall of distinct-net pads along y = 0 that
        // seals the hop, so the ONLY completion is to cross A on the outer and rip it up — a via hop would
        // land on the inner wall. (Rip-up is otherwise avoided on a stack because a hop is cheaper than
        // crossing; blocking both shells is what forces it.)
        MidStack Make()
        {
            var s = MidStack.TwoShell(mesh, t);
            s.Outer.PlacePad("A", new Vector3d(-5, 0, 0), Land, "A0");
            s.Outer.PlacePad("A", new Vector3d(5, 0, 0), Land, "A1");
            s.Outer.PlacePad("B", new Vector3d(0, -3, 0), Land, "B0");
            s.Outer.PlacePad("B", new Vector3d(0, 3, 0), Land, "B1");
            int w = 0;
            for (double x = -5.5; x <= 5.5001; x += 1.0)
                s.Inner.PlacePad($"W{w}", new Vector3d(x, 0, 0), Land, $"W{w++}");   // snaps onto the inner plane
            return s;
        }

        // A pure greedy pass (no rip-up) routes A straight and leaves B unrouted — but is still clean.
        var greedyStack = Make();
        var greedy = MidRouting.Route(greedyStack, Rules, Opt with { MaxRipUpIterations = 0 });
        Assert.Contains("B", greedy.UnroutedNets);
        AssertClean(greedyStack);

        // Rip-up-and-reroute completes it: B crosses A, A is ripped up and re-routed around B, both clean.
        var stack = Make();
        var routed = MidRouting.Route(stack, Rules, Opt);
        _out.WriteLine(routed.ToString());
        Assert.True(routed.FullyRouted, routed.ToString());
        Assert.True(routed.RipUps >= 1, "the doubly-congested stack should require at least one rip-up");
        AssertClean(stack);
        AssertConnected(stack, "A", "B");
    }

    // ==== 6) the single-shell path is unchanged; the entry refuses 1 and > 2 shells =

    [Fact]
    public void OneAndManyShellStacks_RefusedByName_AndTheSingleShellRouterStillRoutesClean()
    {
        double r = 4;
        var mesh = Tube(r, 10, 96, 48);

        // A one-shell stack has nothing to hop to — refused, pointing at the single-shell path.
        var single = MidStack.Build(mesh, []);
        Assert.Equal(1, single.ShellCount);
        var ex1 = Assert.Throws<ArgumentException>(() => MidRouting.Route(single, Rules, Opt));
        _out.WriteLine(ex1.Message);
        Assert.Contains("stack.Outer", ex1.Message);

        // A > 2 shell stack needs partial-span vias (filed) — refused by name.
        var three = MidStack.Build(mesh, [0.4, 0.4]);
        Assert.Equal(3, three.ShellCount);
        var ex3 = Assert.Throws<ArgumentException>(() => MidRouting.Route(three, Rules, Opt));
        _out.WriteLine(ex3.Message);
        Assert.Contains("PARTIAL-SPAN", ex3.Message);

        // The single-shell router itself is unchanged by the cross-shell addition — it still routes a plain
        // board clean and connected (the existing MID auto-route suite pins bit-identity; this is a smoke).
        var board = MidBoard.OnMesh(mesh);
        board.PlacePad("N", OnTube(r, 0.0, 4.0), Land, "A");
        board.PlacePad("N", OnTube(r, 1.2, 4.0), Land, "B");
        var flat = MidRouting.Route(board, Rules, Opt);
        Assert.True(flat.FullyRouted, flat.ToString());
        Assert.True(Mid3dDrc.Check(board, Rules).Ok);
    }
}
