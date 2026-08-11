using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// MULTI-SHELL MID/LDS — an outer moulded wall plus an inner shell offset inward by a dielectric
/// thickness, stitched by through-shell vias. The bar is higher than usual because ECAD fails plausibly.
/// The DECISIVE oracle is the developable one: on a cylinder the inward normal offset is an isometry, so
/// the inner shell is a concentric cylinder of radius <c>r − t</c> to round-off. Cross-shell connectivity
/// is verified by the via mutation (a via ties two shells' copper; remove it and the net splits), and the
/// self-intersection guard is verified by a fixture that trips it.
/// </summary>
public class MidMultiShellTests
{
    private readonly ITestOutputHelper _out;
    public MidMultiShellTests(ITestOutputHelper output) => _out = output;

    // ---- meshes --------------------------------------------------------------

    private static HalfEdgeMesh Tube(double radius, double height = 8, int around = 64, int along = 40)
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

    // ---- THE DEVELOPABLE ORACLE: inward offset is exact on a cylinder --------

    [Fact]
    public void Cylinder_InnerShell_IsConcentricAtRadiusMinusThickness_ToRoundOff()
    {
        double r = 6, t = 0.5;
        var stack = MidStack.TwoShell(Tube(r), t);
        Assert.Equal(2, stack.ShellCount);
        Assert.Equal(0.0, stack.Offset(0));
        Assert.Equal(t, stack.Offset(1), 1e-15);

        // The inward normal offset of a developable (cylinder) surface is an ISOMETRY, so the inner shell
        // is a concentric cylinder of radius r − t — every vertex, including the free rim, lands there to
        // round-off (the angle-weighted vertex normal is exactly radial by symmetry, even at the rim).
        var inner = stack.Inner.Mesh;
        double worst = 0;
        for (int i = 0; i < inner.VertexCount; i++)
        {
            var p = inner.GetPosition(i);
            double rad = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            worst = Math.Max(worst, Math.Abs(rad - (r - t)));
        }
        _out.WriteLine($"inner cylinder radius worst |err| = {worst:E3} (r - t = {r - t})");
        Assert.True(worst < 1e-11, $"the developable offset should be exact to round-off; worst {worst:E3}");
    }

    // ---- cross-shell connectivity: a via ties two shells ---------------------

    [Fact]
    public void CrossShellConnectivity_ViaTiesTwoShells_RemovingItSplitsTheNet()
    {
        double r = 6, t = 0.6;
        var world = new Vector3d(r, 0, 4);

        // WITH a via: an outer VOUT pad + an inner VOUT pad + a via tying them = ONE connected net.
        var stack = MidStack.TwoShell(Tube(r), t);
        stack.Outer.PlacePad("VOUT", world, 0.6, "VOUT.out");         // an outer TERMINAL
        var via = stack.AddVia("VOUT", world);                        // ties outer→inner at this point
        stack.Inner.PlacePad("VOUT", via.InnerPoint, 0.6, "VOUT.in"); // an inner TERMINAL at the via foot

        var connected = stack.Connectivity();
        _out.WriteLine($"with via: VOUT connected = {connected.Of("VOUT").IsConnected}, "
            + $"components = {connected.Of("VOUT").ComponentCount}");
        Assert.True(connected.Of("VOUT").IsConnected, "a via ties the two shells' VOUT copper into one net");
        Assert.Empty(connected.Unrouted);
        Assert.Empty(stack.Check().Ratsnest);

        // The MUTATION: the SAME layout without the via — the two shells' VOUT copper are two islands.
        var split = MidStack.TwoShell(Tube(r), t);
        split.Outer.PlacePad("VOUT", world, 0.6, "VOUT.out");
        split.Inner.PlacePad("VOUT", via.InnerPoint, 0.6, "VOUT.in");   // the same inner foot, no via
        var splitReport = split.Connectivity();
        _out.WriteLine($"no via: VOUT components = {splitReport.Of("VOUT").ComponentCount}");
        Assert.False(splitReport.Of("VOUT").IsConnected, "with no via the two shells are separate nets");
        Assert.Contains("VOUT", split.Check().Ratsnest);
    }

    // ---- the two-shell DRC ---------------------------------------------------

    [Fact]
    public void TwoShellDrc_FindsAnInnerShellClearanceViolation_AndACleanBoardIsClean()
    {
        double r = 6, t = 0.6, land = 0.4;
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.2, MinTraceWidth = 0 };

        // A same-shell clearance violation on the INNER shell (two different-net pads half a clearance
        // apart edge-to-edge) — the per-shell DRC on the inner shell must find it.
        var stack = MidStack.TwoShell(Tube(r), t);
        double innerR = r - t;
        double dphi = (land + 0.5 * rules.MinCopperClearance) / innerR;   // centre arc → edge gap = 0.5·c
        stack.Inner.PlacePad("N1", new Vector3d(innerR, 0, 4), land, "N1");
        stack.Inner.PlacePad("N2", new Vector3d(innerR * Math.Cos(dphi), innerR * Math.Sin(dphi), 4), land, "N2");
        var report = stack.Check(rules);
        _out.WriteLine("inner violation: " + report);
        Assert.Contains(report.Violations, v => v.Rule == MidDrcRule.Clearance
            && v.Message.Contains("[inner]") && (v.Message.Contains("N1") || v.Message.Contains("N2")));
        Assert.False(report.Ok);

        // A well-spaced two-shell board (copper on both shells, a via connecting one net) is CLEAN.
        var clean = MidStack.TwoShell(Tube(r), t);
        clean.Outer.PlacePad("A", new Vector3d(r, 0, 2), land, "A");
        clean.Outer.PlacePad("B", new Vector3d(0, r, 2), land, "B");
        clean.Inner.PlacePad("C", new Vector3d(innerR, 0, 6), land, "C");
        var v = clean.AddVia("A", new Vector3d(r, 0, 5));
        clean.Inner.PlacePad("A", v.InnerPoint, land, "A.in");
        var cleanReport = clean.Check(rules);
        _out.WriteLine("clean: " + cleanReport);
        Assert.True(cleanReport.Ok, "a well-spaced two-shell board should be clean: " + cleanReport);
    }

    [Fact]
    public void TwoShellDrc_FindsAViaTooCloseToOtherNetCopper_OnEachShell()
    {
        double r = 6, t = 0.6, land = 0.4, padD = 0.5;
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.25, MinTraceWidth = 0, MinViaToVia = 0 };
        var stack = MidStack.TwoShell(Tube(r), t);

        var via = stack.AddVia("SIG", new Vector3d(r, 0, 4), padD);
        // A different-net GND pad on the OUTER shell just too close to the via's outer pad — the per-shell
        // DRC on the outer shell must find it (a via pad IS copper on its shell → rule 1 falls out).
        double gap = 0.5 * rules.MinCopperClearance;
        double dphi = (padD / 2 + land / 2 + gap) / r;                 // centre arc → edge gap = 0.5·c
        stack.Outer.PlacePad("GND", new Vector3d(r * Math.Cos(dphi), r * Math.Sin(dphi), 4), land, "GND.out");

        var report = stack.Check(rules);
        _out.WriteLine("via clearance: " + report);
        Assert.Contains(report.Violations, x => x.Rule == MidDrcRule.Clearance
            && x.Message.Contains("[outer]") && x.Message.Contains("via0") && x.Message.Contains("GND"));
        Assert.False(report.Ok);

        // And the same via too close to other-net copper on the INNER shell is found by the inner DRC.
        var inner = MidStack.TwoShell(Tube(r), t);
        var via2 = inner.AddVia("SIG", new Vector3d(r, 0, 4), padD);
        double innerR = r - t;
        double dphi2 = (padD / 2 + land / 2 + gap) / innerR;
        var near = new Vector3d(via2.InnerPoint.X, via2.InnerPoint.Y, via2.InnerPoint.Z);
        // Place the GND pad an arc `dphi2` around the inner cylinder from the via's inner point.
        double baseAngle = Math.Atan2(near.Y, near.X);
        var gndInner = new Vector3d(innerR * Math.Cos(baseAngle + dphi2), innerR * Math.Sin(baseAngle + dphi2), near.Z);
        inner.Inner.PlacePad("GND", gndInner, land, "GND.in");
        var innerReport = inner.Check(rules);
        _out.WriteLine("inner via clearance: " + innerReport);
        Assert.Contains(innerReport.Violations, x => x.Rule == MidDrcRule.Clearance
            && x.Message.Contains("[inner]") && x.Message.Contains("via0") && x.Message.Contains("GND"));
    }

    [Fact]
    public void ViaToViaSpacing_TooClose_IsFound_RegardlessOfNet()
    {
        double r = 6, t = 0.6, padD = 0.5;
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0, MinTraceWidth = 0, MinViaToVia = 0.3 };
        var stack = MidStack.TwoShell(Tube(r), t);

        // Two vias of the SAME net (so the copper-clearance rule never fires — same-net), their barrels
        // closer than the via-to-via web: only the via-to-via rule catches it.
        double dphi = (padD + 0.5 * rules.MinViaToVia) / r;   // centre arc → web ≈ 0.5·MinViaToVia
        stack.AddVia("SIG", new Vector3d(r, 0, 4), padD);
        stack.AddVia("SIG", new Vector3d(r * Math.Cos(dphi), r * Math.Sin(dphi), 4), padD);

        var report = stack.Check(rules);
        _out.WriteLine("via-to-via: " + report);
        Assert.Contains(report.Violations, v => v.Rule == MidDrcRule.ViaSpacing
            && v.Message.Contains("via0") && v.Message.Contains("via1"));
    }

    // ---- single-shell stack is bit-identical to a plain board ----------------

    [Fact]
    public void SingleShellStack_DrcMatchesThePlainBoard_ByConstruction()
    {
        // A one-shell stack (no dielectric, no via) is a plain MidBoard, and its multi-shell DRC delegates
        // to Mid3dDrc.Check(the one shell) verbatim — same violations, same messages, same ratsnest.
        double major = 6, land = 0.4;
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.25, MinTraceWidth = 0.15 };
        var mesh = Tube(major);

        var stack = MidStack.Build(mesh, []);   // ZERO inner shells → single shell
        Assert.Equal(1, stack.ShellCount);
        double dphi = (land + 0.5 * rules.MinCopperClearance) / major;
        stack.Outer.PlacePad("A", new Vector3d(major, 0, 3), land, "A");
        stack.Outer.PlacePad("B", new Vector3d(major * Math.Cos(dphi), major * Math.Sin(dphi), 3), land, "B");

        var plain = MidBoard.OnMesh(mesh);
        plain.PlacePad("A", new Vector3d(major, 0, 3), land, "A");
        plain.PlacePad("B", new Vector3d(major * Math.Cos(dphi), major * Math.Sin(dphi), 3), land, "B");

        var multi = stack.Check(rules);
        var single = Mid3dDrc.Check(plain, rules);
        Assert.Equal(single.Violations.Count, multi.Violations.Count);
        for (int i = 0; i < single.Violations.Count; i++)
        {
            // The one shell is named "outer"; the multi-shell wrapper prefixes exactly that.
            Assert.Equal($"[outer] {single.Violations[i].Message}", multi.Violations[i].Message);
            Assert.Equal(single.Violations[i].Rule, multi.Violations[i].Rule);
            Assert.Equal(single.Violations[i].MeasuredParameter, multi.Violations[i].MeasuredParameter);
        }
        Assert.Equal(single.Uncertain.Count, multi.Uncertain.Count);
        Assert.Equal(single.Ok, multi.Ok);
    }

    // ---- the self-intersection guard fires -----------------------------------

    [Fact]
    public void SelfIntersectingInwardOffset_IsRefusedByName()
    {
        // A sphere offset inward by MORE than its radius: every vertex crosses the centre, so the inner
        // "shell" turns inside out and every offset triangle inverts — the fold guard fires, naming the
        // region and the thickness. A valid inner sphere (t < R) builds fine.
        var sphere = MeshPrimitives.UvSphere(10, 64, 40);
        var ok = MidStack.TwoShell(sphere, 3);   // r − t = 7, a valid concentric inner sphere
        Assert.Equal(2, ok.ShellCount);

        var ex = Assert.Throws<MidShellException>(() => MidStack.TwoShell(sphere, 12));
        _out.WriteLine(ex.Message);
        Assert.Contains("self-intersect", ex.Message);
        Assert.Contains("12", ex.Message);   // the thickness is named
    }

    // ---- via endpoints correspond; a non-corresponding via is refused --------

    [Fact]
    public void Via_EndpointsCorrespond_AndANonCorrespondingOneIsRefused()
    {
        double r = 6, t = 0.6;
        var stack = MidStack.TwoShell(Tube(r), t);
        var outerWorld = new Vector3d(r, 0, 4);
        var via = stack.AddVia("N", outerWorld);

        // The via's outer point is on the outer wall; the inner point is the corresponding point on the
        // inner shell — the same (face, barycentric) location offset inward by t (radially, on a tube).
        Assert.Equal(r, Math.Sqrt(via.OuterPoint.X * via.OuterPoint.X + via.OuterPoint.Y * via.OuterPoint.Y), 1e-9);
        Assert.Equal(r - t, Math.Sqrt(via.InnerPoint.X * via.InnerPoint.X + via.InnerPoint.Y * via.InnerPoint.Y), 1e-9);
        Assert.Equal(via.OuterPoint.Z, via.InnerPoint.Z, 1e-12);   // a radial offset keeps z

        // The validated overload REFUSES an inner endpoint that is not the corresponding point (here a
        // point on the opposite side of the tube).
        var stack2 = MidStack.TwoShell(Tube(r), t);
        var ex = Assert.Throws<MidShellException>(() =>
            stack2.AddVia("N", outerWorld, new Vector3d(-(r - t), 0, 4)));
        _out.WriteLine(ex.Message);
        Assert.Contains("corresponding", ex.Message);
        // But the validated overload ACCEPTS the true corresponding inner point.
        var good = stack2.AddVia("N", outerWorld, via.InnerPoint);
        Assert.Equal("via0", good.Source);
    }

    // ---- the via barrel is a closed solid ------------------------------------

    [Fact]
    public void ViaBarrel_IsAClosedSolid()
    {
        double r = 6, t = 0.6;
        var stack = MidStack.TwoShell(Tube(r), t);
        var via = stack.AddVia("N", new Vector3d(r, 0, 4));
        var barrel = via.Barrel(0.4).ToMesh();
        Assert.Empty(barrel.BoundaryLoops());   // watertight
        Assert.True(barrel.Volume() > 0, "the via barrel is a positive-volume closed solid");
    }

    // ---- determinism ---------------------------------------------------------

    [Fact]
    public void Build_And_Check_AreDeterministic()
    {
        double r = 6, t = 0.6, land = 0.4;
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.2, MinTraceWidth = 0 };
        MidStack Make()
        {
            var s = MidStack.TwoShell(Tube(r), t);
            s.Outer.PlacePad("A", new Vector3d(r, 0, 3), land, "A");
            s.Outer.PlacePad("B", new Vector3d(r, 0, 3.1), land, "B");   // very close, different net
            var via = s.AddVia("A", new Vector3d(r, 0, 5));
            s.Inner.PlacePad("A", via.InnerPoint, land, "A.in");
            return s;
        }
        var a = Make().Check(rules);
        var b = Make().Check(rules);
        Assert.Equal(a.Violations.Count, b.Violations.Count);
        for (int i = 0; i < a.Violations.Count; i++)
            Assert.Equal(a.Violations[i].Message, b.Violations[i].Message);
        Assert.Equal(a.Uncertain.Count, b.Uncertain.Count);
        Assert.Equal(a.Ratsnest, b.Ratsnest);
    }

    // ---- the developable connectivity showcase: route both shells, stitch ----

    [Fact]
    public void Developable_TwoShellBoard_RoutesBothShells_StitchedByAVia_VerifiesClean()
    {
        double r = 6, t = 0.6, width = 0.35;
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.2, MinTraceWidth = 0.15 };
        var stack = MidStack.TwoShell(Tube(r), t);

        // OUTER shell: a SIG net from a pad up to the via foot.
        var outFrom = stack.Outer.PlacePad("SIG", new Vector3d(r, 0, 2), 0.6, "SIG.src");
        var viaWorld = new Vector3d(r, 0, 6);
        var outTo = stack.Outer.PlacePad("SIG", viaWorld, 0.6, "SIG.via.out");
        MidRouting.Connect(stack.Outer, outFrom, outTo, width);

        // The via stitches SIG through the dielectric to the inner shell.
        var via = stack.AddVia("SIG", viaWorld);

        // INNER shell: SIG continues from the via foot to a sink pad.
        var inFrom = stack.Inner.PlacePad("SIG", via.InnerPoint, 0.6, "SIG.via.in");
        double innerR = r - t;
        var inTo = stack.Inner.PlacePad("SIG", new Vector3d(innerR * Math.Cos(0.6), innerR * Math.Sin(0.6), 2), 0.6, "SIG.sink");
        MidRouting.Connect(stack.Inner, inFrom, inTo, width);

        var report = stack.Check(rules);
        _out.WriteLine("two-shell routed: " + report);
        // SIG connects across BOTH shells through the via — no cross-shell ratsnest — and the board is clean.
        Assert.Empty(report.Ratsnest);
        Assert.True(report.Ok, "the two-shell board should verify clean: " + report);
    }

    // ---- guards fire ---------------------------------------------------------

    [Fact]
    public void Guards_Fire()
    {
        var stack = MidStack.TwoShell(Tube(6), 0.6);
        // A shell index out of range is refused by name.
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.Shell(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.Shell(-1));
        // A non-positive dielectric thickness is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => MidStack.TwoShell(Tube(6), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MidStack.TwoShell(Tube(6), -1));
        // A non-positive via pad diameter is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.AddVia("N", new Vector3d(6, 0, 4), 0));
    }
}
