using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The MCAD/ECAD boundary: does a placed board fit an enclosure? Every fixture is a small box
/// enclosure with closed-form clearances (interior 60 × 50 × 12, wall 2, board seated on
/// standoffs at z = 3, a 1.5 mm board — so a top part's body local origin lands at interior
/// z = 3 + 1.5 = 4.5), so every headroom / overhang / clearance-deficit is a hand-checkable
/// number and the guards are shown to fire.
/// </summary>
public class PcbEnclosureTests
{
    private const double Thickness = 1.5;
    private const double SeatZ = 3.0;
    private const double LidZ = 12.0;         // = interior height (lid rests on the walls)
    private const double ZBase = SeatZ + Thickness; // a top part's body-local origin, interior z

    private static Enclosure Enc() => new(60, 50, 12, wallThickness: 2, boardSeatZ: SeatZ);

    private static PcbBoard Board(double width = 50, double height = 40) => new(
        [(-width / 2, -height / 2), (width / 2, -height / 2),
         (width / 2, height / 2), (-width / 2, height / 2)], Thickness);

    private static PartDefinition Comp(string reference, Shape body)
    {
        string prefix = new string(reference.TakeWhile(char.IsLetter).ToArray());
        return new PartDefinition(reference, prefix.Length > 0 ? prefix : "U",
            [new Pin("1", PinType.Passive)], body: () => body);
    }

    /// <summary>Seats a board with the given parts in the enclosure (board frame = the enclosure
    /// seat, so the geometry is one declaration). Each part: reference, body (modelled +Z out of
    /// the board), board (x, y), rotation.</summary>
    private static PcbLayout Seat(Enclosure enclosure, PcbBoard board,
        params (string Reference, Shape Body, double X, double Y)[] parts)
    {
        var schematic = new Schematic("fit");
        foreach (var part in parts)
            schematic.Add(part.Reference, Comp(part.Reference, part.Body));
        var layout = new PcbLayout(schematic, board, enclosure.SeatFrame());
        foreach (var part in parts)
            layout.Place(part.Reference, part.X, part.Y);
        return layout;
    }

    private static Shape SmallPart() => Shape.Box(2, 1.25, 0.5).Translate(0, 0, 0.25);

    // ---- a clean fit reports OK with the measured headroom ------------------

    [Fact]
    public void CleanFit_IsOk_WithMeasuredHeadroom()
    {
        var enclosure = Enc();
        var r1 = Shape.Box(2, 1.25, 0.5).Translate(0, 0, 0.25);  // top local 0.5 -> interior 5.0
        var c1 = Shape.Box(3, 3, 2).Translate(0, 0, 1);          // top local 2.0 -> interior 6.5
        var layout = Seat(enclosure, Board(), ("R1", r1, 5, 0), ("C1", c1, -10, 5));

        var report = enclosure.Fit(layout);

        Assert.True(report.Ok, report.ToString());
        Assert.Equal("C1", report.TallestComponent);
        // Headroom = lid (12) - tallest top (ZBase + 2 = 6.5) = 5.5, closed form.
        Assert.Equal(LidZ - (ZBase + 2), report.Headroom, 6);
    }

    // ---- a part resting flush on the lid is NOT a clash (the seated rule) ----

    [Fact]
    public void PartFlushWithLid_IsNotAClash_HeadroomZero()
    {
        var enclosure = Enc();
        // Top local 7.5 -> interior 12.0 == the lid underside: touching, not interpenetrating.
        double h = LidZ - ZBase;
        var flush = Shape.Box(4, 4, h).Translate(0, 0, h / 2);
        var layout = Seat(enclosure, Board(), ("U1", flush, 0, 0));

        var report = enclosure.Fit(layout);

        Assert.False(report.Has(FitIssue.ComponentClashesLid));   // contact, not a crossing
        Assert.False(report.Has(FitIssue.ComponentClashesWall));  // seated inside the cavity
        Assert.Equal(0.0, report.Headroom, 6);
        Assert.True(report.Ok, report.ToString());
    }

    // ---- a board too big names the wall it hits, with the overhang -----------

    [Fact]
    public void BoardTooLarge_NamesTheWall_WithOverhang()
    {
        var enclosure = Enc();                     // cavity x in [-30, 30]
        var board = Board(width: 62, height: 40);  // board x in [-31, 31] -> overhang 1 on +X, -X
        var layout = Seat(enclosure, board, ("R1", SmallPart(), 0, 0));

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.BoardTooLarge));
        var walls = report.OfIssue(FitIssue.BoardTooLarge).ToList();
        Assert.Equal(2, walls.Count);                          // +X and -X
        Assert.All(walls, w => Assert.EndsWith("X", w.Subject));
        Assert.All(walls, w => Assert.Equal(1.0, w.Measured - w.Required, 6));  // overhang exactly 1
    }

    // ---- a tall part collides with the lid, named with its exact deficit -----

    [Fact]
    public void TallPart_HitsLid_WithClearanceDeficit()
    {
        var enclosure = Enc();
        var tall = Shape.Box(4, 4, 10).Translate(0, 0, 5);  // top local 10 -> interior 14.5
        var layout = Seat(enclosure, Board(), ("U1", tall, 0, 0));

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.ComponentClashesLid));
        var lid = Assert.Single(report.OfIssue(FitIssue.ComponentClashesLid));
        Assert.Equal("U1", lid.Subject);
        Assert.Equal(ZBase + 10, lid.Measured, 6);          // top = 14.5
        Assert.Equal(LidZ, lid.Required, 6);                // lid underside = 12
        Assert.Equal(LidZ - (ZBase + 10), report.Headroom, 6);  // -2.5
    }

    // ---- a component overhanging the cavity interpenetrates a wall -----------

    [Fact]
    public void ComponentOverhang_ClashesWall()
    {
        var enclosure = Enc();
        var bracket = Shape.Box(20, 5, 5).Translate(0, 0, 2.5);  // at x=25 -> interior x [15, 35]
        var layout = Seat(enclosure, Board(), ("U1", bracket, 25, 0));

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.ComponentClashesWall));
        Assert.Equal("U1", Assert.Single(report.OfIssue(FitIssue.ComponentClashesWall)).Subject);
    }

    // ---- a connector aligned with its panel cutout passes -------------------

    [Fact]
    public void PanelConnector_Aligned_Passes()
    {
        var enclosure = Enc()
            .AddPanelConnector("J2")
            .AddCutout(PanelCutout.Rectangular("J2_usb", PanelWall.MaxX,
                centerAlong: 0, centerZ: ZBase + 2.5, width: 10, height: 6, forReference: "J2"));
        // Body reaches from the board out through the +X wall (inner face x = 30).
        var conn = Shape.Box(24, 8, 5).Translate(0, 0, 2.5);  // at x=20 -> interior x [8, 32]
        var layout = Seat(enclosure, Board(), ("J2", conn, 20, 0));

        var report = enclosure.Fit(layout);

        Assert.False(report.Has(FitIssue.ConnectorNoCutout));
        Assert.False(report.Has(FitIssue.ConnectorMisaligned));
        Assert.False(report.Has(FitIssue.ConnectorNotProtruding));
        Assert.False(report.Has(FitIssue.ComponentClashesWall));  // a panel connector passes through
        Assert.True(report.Ok, report.ToString());
    }

    // ---- a connector off-centre in its cutout is named with the offset -------

    [Fact]
    public void PanelConnector_Misaligned_NamesOffset()
    {
        var enclosure = Enc()
            .AddPanelConnector("J2")
            .AddCutout(PanelCutout.Rectangular("J2_usb", PanelWall.MaxX,
                centerAlong: 0, centerZ: ZBase + 2.5, width: 10, height: 6, forReference: "J2"));
        var conn = Shape.Box(24, 8, 5).Translate(0, 0, 2.5);
        // Placed +2 in y — the connector's centre is 2 mm off the cutout's, past the 0.5 mm the
        // opening allows for its 8 mm width.
        var layout = Seat(enclosure, Board(), ("J2", conn, 20, 2));

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.ConnectorMisaligned));
        var m = Assert.Single(report.OfIssue(FitIssue.ConnectorMisaligned));
        Assert.Equal("J2", m.Subject);
        Assert.Equal(2.0, m.Measured, 6);   // centre offset
        Assert.Equal(0.5, m.Required, 6);   // the centre offset the opening still admits
    }

    // ---- a declared panel connector with no cutout is named ------------------

    [Fact]
    public void PanelConnector_NoCutout_IsNamed()
    {
        var enclosure = Enc().AddPanelConnector("J2");  // no cutout serves it
        var conn = Shape.Box(24, 8, 5).Translate(0, 0, 2.5);
        var layout = Seat(enclosure, Board(), ("J2", conn, 20, 0));

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.ConnectorNoCutout));
        Assert.Equal("J2", Assert.Single(report.OfIssue(FitIssue.ConnectorNoCutout)).Subject);
    }

    // ---- a connector that does not reach its wall is named -------------------

    [Fact]
    public void PanelConnector_NotProtruding_IsNamed()
    {
        var enclosure = Enc()
            .AddPanelConnector("J2")
            .AddCutout(PanelCutout.Rectangular("J2_usb", PanelWall.MaxX,
                centerAlong: 0, centerZ: ZBase + 2.5, width: 10, height: 6, forReference: "J2"));
        var conn = Shape.Box(8, 8, 5).Translate(0, 0, 2.5);  // at x=20 -> interior x [16, 24], short of 30
        var layout = Seat(enclosure, Board(), ("J2", conn, 20, 0));

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.ConnectorNotProtruding));
        var np = Assert.Single(report.OfIssue(FitIssue.ConnectorNotProtruding));
        Assert.Equal(24.0, np.Measured, 6);   // reaches x = 24
        Assert.Equal(30.0, np.Required, 6);   // the +X wall inner face
    }

    // ---- a round cutout builds and its connector aligns ---------------------

    [Fact]
    public void RoundCutout_Builds_AndConnectorAligns()
    {
        var enclosure = Enc()
            .AddPanelConnector("J3")
            .AddCutout(PanelCutout.Round("barrel", PanelWall.MinY,
                centerAlong: 0, centerZ: ZBase + 2.5, diameter: 8, forReference: "J3"));
        // The housing must mesh (a round tool through the wall is a real boolean).
        Assert.True(enclosure.Housing().ToMesh().IsClosed);

        var conn = Shape.Box(6, 20, 5).Translate(0, 0, 2.5);  // at y=-18 -> interior y [-28, -8]
        var layout = Seat(enclosure, Board(), ("J3", conn, 0, -18));

        var report = enclosure.Fit(layout);

        Assert.False(report.Has(FitIssue.ConnectorMisaligned));
        Assert.False(report.Has(FitIssue.ConnectorNotProtruding));
        Assert.True(report.Ok, report.ToString());
    }

    // ---- a keep-out collision is named; a clear part is not -----------------

    [Fact]
    public void KeepOut_Collision_IsNamed()
    {
        // A boss rising from the floor: interior x [12, 18], y [7, 13], z [0, 8].
        var enclosure = Enc().AddKeepOut("boss", Shape.Box(6, 6, 8).Translate(15, 10, 4));
        var layout = Seat(enclosure, Board(), ("R1", SmallPart(), 18, 10));  // straddles the boss face

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.KeepOutCollision));
        var collision = Assert.Single(report.OfIssue(FitIssue.KeepOutCollision));
        Assert.Equal("R1", collision.Subject);
        Assert.Contains("boss", collision.Message);
    }

    [Fact]
    public void KeepOut_Clear_IsNotFlagged()
    {
        var enclosure = Enc().AddKeepOut("boss", Shape.Box(6, 6, 8).Translate(15, 10, 4));
        var layout = Seat(enclosure, Board(), ("R1", SmallPart(), -18, -10));  // far from the boss

        var report = enclosure.Fit(layout);

        Assert.False(report.Has(FitIssue.KeepOutCollision));
        Assert.True(report.Ok, report.ToString());
    }

    // ---- a keep-out that fully contains a small part is still caught ---------

    [Fact]
    public void KeepOut_ContainingASmallPart_IsCaught()
    {
        // A large boss that a small part sits entirely inside — the surfaces never cross, so the
        // winding-number containment fallback is what catches it.
        var enclosure = Enc().AddKeepOut("battery", Shape.Box(20, 20, 9).Translate(0, 0, 4.5));
        var layout = Seat(enclosure, Board(), ("R1", SmallPart(), 0, 0));  // interior z [4.5, 5] inside [0, 9]

        var report = enclosure.Fit(layout);

        Assert.True(report.Has(FitIssue.KeepOutCollision));
        Assert.Equal("R1", Assert.Single(report.OfIssue(FitIssue.KeepOutCollision)).Subject);
    }

    // ---- the same enclosure + board gives the same report -------------------

    [Fact]
    public void Fit_IsDeterministic()
    {
        var enclosure = Enc()
            .AddPanelConnector("J2")
            .AddKeepOut("boss", Shape.Box(6, 6, 8).Translate(15, 10, 4));
        var layout = Seat(enclosure, Board(),
            ("R1", SmallPart(), 18, 10),
            ("U1", Shape.Box(4, 4, 10).Translate(0, 0, 5), 0, 0),
            ("J2", Shape.Box(8, 8, 5).Translate(0, 0, 2.5), 20, 0));

        var a = enclosure.Fit(layout);
        var b = enclosure.Fit(layout);

        Assert.Equal(a.Headroom, b.Headroom);
        Assert.Equal(a.TallestComponent, b.TallestComponent);
        Assert.Equal(a.Problems.Count, b.Problems.Count);
        for (int i = 0; i < a.Problems.Count; i++)
            Assert.Equal(a.Problems[i], b.Problems[i]);   // FitProblem is a value
    }

    // ---- the smallest-box helper produces an enclosure the layout fits -------

    [Fact]
    public void SmallestFor_FitsTheLayout()
    {
        var seed = Enc();
        var layout = Seat(seed, Board(40, 30),
            ("R1", SmallPart(), 5, 0),
            ("C1", Shape.Box(3, 3, 4).Translate(0, 0, 2), -5, 5));

        var enclosure = Enclosure.SmallestFor(layout,
            clearance: 3, standoff: 2, headroom: 3, wallThickness: 1.5);
        var report = enclosure.Fit(layout);

        Assert.True(report.Ok, report.ToString());
        Assert.Equal(3.0, report.Headroom, 4);   // the tallest part clears by exactly the requested headroom
    }

    // ---- guards fire ---------------------------------------------------------

    [Fact]
    public void Constructor_RefusesBadDimensions()
    {
        Assert.Throws<ArgumentException>(() => new Enclosure(60, 50, 12, 2, boardSeatZ: 12));   // seat >= height
        Assert.Throws<ArgumentException>(() => new Enclosure(60, 50, 12, 2, boardSeatZ: 3, lidZ: 2));  // lid <= seat
        Assert.Throws<ArgumentException>(() => new Enclosure(-1, 50, 12, 2, 3));  // non-positive interior
        Assert.Throws<ArgumentException>(() => new Enclosure(60, 50, 12, -2, 3)); // non-positive wall
    }

    [Fact]
    public void Cutout_And_PanelConnector_RefuseBadInput()
    {
        Assert.Throws<ArgumentException>(() => PanelCutout.Rectangular("c", PanelWall.MaxX, 0, 5, -1, 5));
        Assert.Throws<ArgumentException>(() => PanelCutout.Round("c", PanelWall.MaxX, 0, 5, 0));
        Assert.Throws<ArgumentException>(() => Enc().AddPanelConnector(""));
    }
}
