using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The sheet standards a frame can opt into: ISO 5457's own per-size zone counts, the ISO 128
/// projection symbol, and the ISO 7200 title block.
///
/// <para>Two of the three are TRANSCRIPTIONS, so they are asserted in the standard's own terms
/// (a count per size, a field list) rather than against arithmetic that would agree with its own
/// mistake. The symbol is the opposite — it is DERIVED from the sheet's projection rule, so it
/// is asserted against that rule.</para>
/// </summary>
public class SheetStandardsTests
{
    // ---------------------------------------------------------------- ISO 5457 zone counts

    /// <summary>The transcription itself, stated as the standard states it: divisions along the
    /// long side and across the short one, per size.</summary>
    [Theory]
    [InlineData("A0", 1189.0, 841.0, 24, 16)]
    [InlineData("A1", 841.0, 594.0, 16, 12)]
    [InlineData("A2", 594.0, 420.0, 12, 8)]
    [InlineData("A3", 420.0, 297.0, 8, 6)]
    [InlineData("A4", 297.0, 210.0, 6, 4)]
    public void TheZoneTableIsTheStandardsOwn(string name, double lng, double shrt, int along, int across)
    {
        Assert.True(Iso5457Zones.TryFor(lng, shrt, out int a, out int b), name);
        Assert.Equal(along, a);
        Assert.Equal(across, b);

        // A landscape sheet reads (columns, rows) = (along, across); turned over it transposes.
        var zones = FrameStandards.Iso5457;
        Assert.Equal((along, across), zones.ZonesFor(lng, shrt, lng - 20, shrt - 20));
        Assert.Equal((across, along), zones.ZonesFor(shrt, lng, shrt - 20, lng - 20));
    }

    /// <summary>
    /// The property a mistyped row would break: ISO 5457 asks for a field of 25–75 mm, so every
    /// transcribed count must land inside that window once the border is taken off the paper.
    /// This is the check the transcription cannot do for itself.
    /// </summary>
    [Fact]
    public void EveryTranscribedCountGivesAFieldInsideTheStandardsOwnWindow()
    {
        foreach (var row in Iso5457Zones.Rows)
        {
            double along = (row.Long - 20) / row.Along;
            double across = (row.Short - 20) / row.Across;
            Assert.InRange(along, 25, 75);
            Assert.InRange(across, 25, 75);
        }
    }

    /// <summary>
    /// WHICH sheets move when the table replaces the nominal rounding — the report the change
    /// owes: A3 and A4 are unchanged (so every committed A3/A4 drawing is byte-identical), and
    /// A0, A1 and A2 each gain one division where the rounding fell short.
    /// </summary>
    [Fact]
    public void OnlyA0A1AndA2MoveWhenTheTableReplacesTheNominalRounding()
    {
        var zones = FrameStandards.Iso5457;
        (SheetFormat Format, bool Moves)[] cases =
        [
            (SheetFormat.A0, true), (SheetFormat.A1, true), (SheetFormat.A2, true),
            (SheetFormat.A3, false), (SheetFormat.A4, false),
        ];
        foreach (var (format, moves) in cases)
        {
            double borderW = format.Width - 20, borderH = format.Height - 20;
            var table = zones.ZonesFor(format.Width, format.Height, borderW, borderH);
            var nominal = (zones.Columns(borderW), zones.Rows(borderH));
            Assert.True(moves == (table != nominal),
                $"{format.Name}: table {table}, nominal {nominal}");
        }
    }

    /// <summary>A paper the standard does not tabulate falls back to the nominal field size —
    /// which is not a lesser answer, it IS what the standard says a field should be.</summary>
    [Fact]
    public void ACustomSheetFallsBackToTheNominalFieldSize()
    {
        var zones = FrameStandards.Iso5457;
        var custom = SheetFormat.Custom("wide", 500, 200);
        Assert.False(Iso5457Zones.TryFor(500, 200, out _, out _));
        Assert.Equal(
            (zones.Columns(480), zones.Rows(180)),
            zones.ZonesFor(custom.Width, custom.Height, 480, 180));
    }

    // ---------------------------------------------------------------- projection symbol

    /// <summary>Off by default, twice over: the standards must ask for it AND the frame must
    /// know its angle. So an ISO 5457 frame that predates the symbol draws exactly what it did.</summary>
    [Fact]
    public void TheProjectionSymbolIsOffUnlessBothHalvesAskForIt()
    {
        var plain = new DrawingFrame { Format = SheetFormat.A3, Standards = FrameStandards.Iso5457 };
        var angleButNoSymbol = plain with { Projection = ProjectionAngle.Third };
        var symbolButNoAngle = plain with
        {
            Standards = FrameStandards.Iso5457 with { ProjectionSymbol = true },
        };
        int baseline = plain.Compute().Lines.Count;
        Assert.Equal(baseline, angleButNoSymbol.Compute().Lines.Count);
        Assert.Equal(baseline, symbolButNoAngle.Compute().Lines.Count);

        var both = symbolButNoAngle with { Projection = ProjectionAngle.Third };
        Assert.True(both.Compute().Lines.Count > baseline);
    }

    /// <summary>
    /// The symbol is a truncated cone in TWO VIEWS, so its two circles are exactly the frustum's
    /// two diameters — and which side they sit on is read off the sheet's own projection rule
    /// (third angle puts a view from the left on the left), never off a picture.
    /// </summary>
    [Theory]
    [InlineData(ProjectionAngle.Third, true)]
    [InlineData(ProjectionAngle.First, false)]
    public void TheProjectionSymbolIsTwoConeViewsPlacedByTheProjectionRule(
        ProjectionAngle angle, bool circlesLeft)
    {
        const double height = 9;
        var frame = new DrawingFrame
        {
            Format = SheetFormat.A3,
            Standards = new FrameStandards { ProjectionSymbol = true, SymbolHeight = height },
            Projection = angle,
        };
        var lines = frame.Compute().Lines
            .Where(l => l.Layer == SheetLayers.Border)
            .Skip(4)   // the border rectangle itself
            .ToList();
        Assert.NotEmpty(lines);

        var all = lines.SelectMany(l => new[] { l.A, l.B }).ToList();
        // The centre line reaches past BOTH views, so its two ends are the extreme x's — drop
        // them and what is left is exactly the two drawings.
        double axisLeft = all.Min(p => p.X), axisRight = all.Max(p => p.X);
        var points = all.Where(p => p.X > axisLeft && p.X < axisRight).ToList();
        double left = points.Min(p => p.X), right = points.Max(p => p.X);
        double mid = (left + right) / 2;

        // The circle view is 2 * CircleSegments chord ends; the trapezoid is four segments and
        // the axis one. So the half carrying the circles is the half carrying nearly every
        // point — a count, not a guess about which drawing is wider.
        var below = points.Where(p => p.X < mid).ToList();
        var above = points.Where(p => p.X > mid).ToList();
        var circleSide = circlesLeft ? below : above;
        var coneSide = circlesLeft ? above : below;
        Assert.True(circleSide.Count > 10 * coneSide.Count,
            $"{angle}: circle side {circleSide.Count}, cone side {coneSide.Count}");

        // Both circles are symmetric about one centre, so the centroid IS it — and the radii
        // that come back are exactly the frustum's two, which is the whole content of the view.
        var centre = new Vector2d(circleSide.Average(p => p.X), circleSide.Average(p => p.Y));
        double small = circleSide.Min(p => (p - centre).Length);
        double large = circleSide.Max(p => (p - centre).Length);
        Assert.Equal(height / 4, small, 9);
        Assert.Equal(height / 2, large, 9);

        // The trapezoid's own extent is the frustum's LARGE diameter too — it is the same cone.
        Assert.Equal(height, points.Max(p => p.Y) - points.Min(p => p.Y), 9);
    }

    // ---------------------------------------------------------------- ISO 7200 title block

    /// <summary>The four data fields ISO 7200 makes MANDATORY are exactly the ones marked so,
    /// and they are the ones a layout must not silently drop.</summary>
    [Fact]
    public void TheMandatoryIso7200FieldsAreTheStandardsFour()
    {
        var mandatory = Iso7200TitleBlock.Fields.Where(f => f.Mandatory).Select(f => f.Caption).ToList();
        Assert.Equal(
            new[] { "LEGAL OWNER", "IDENTIFICATION NUMBER", "DATE OF ISSUE", "SHEET" }.Order(),
            mandatory.Order());
    }

    /// <summary>The layout prints exactly the transcribed field list — no caption it does not
    /// declare, and every mandatory one present. A transcription nobody reads back can drift.</summary>
    [Fact]
    public void TheIso7200LayoutPrintsExactlyItsTranscribedFields()
    {
        var title = new TitleBlock
        {
            Title = "GEAR HOUSING", Project = "PUMP SKID", DrawingNumber = "EC-7200",
            DocumentType = "DRAWING", Revision = "C", Author = "EngrCAD", ApprovedBy = "CJ",
            Date = "2026-08-17", Language = "en", Sheet = "1 / 3", Company = "ENGRCAD",
        };
        var frame = new DrawingFrame
        {
            Format = SheetFormat.A3, Title = title, Layout = Iso7200TitleBlock.Default,
        };
        var texts = frame.Compute().Texts.Select(t => t.Text).ToList();

        var declared = Iso7200TitleBlock.Fields.Select(f => f.Caption).ToHashSet();
        foreach (var field in Iso7200TitleBlock.Fields.Where(f => f.Mandatory))
            Assert.Contains(field.Caption, texts);

        // Every caption-looking text is a declared field (values are the title block's own).
        var values = new HashSet<string>(new[]
        {
            title.Title, title.Project, title.DrawingNumber, title.DocumentType, title.Revision,
            title.Author, title.ApprovedBy, title.Date, title.Language, title.Sheet, title.Company,
        });
        foreach (string text in texts)
            Assert.True(declared.Contains(text) || values.Contains(text), $"stray text '{text}'");

        // ... and every stated value reached the block.
        foreach (string value in values)
            Assert.Contains(value, texts);
    }

    /// <summary>An unstated field prints its caption and nothing else — what a blank form does,
    /// and honest about what the drawing has not said.</summary>
    [Fact]
    public void AnUnstatedIso7200FieldPrintsItsCaptionAlone()
    {
        var frame = new DrawingFrame
        {
            Format = SheetFormat.A4, Title = new TitleBlock { Title = "BLANK" },
            Layout = Iso7200TitleBlock.Default,
        };
        var texts = frame.Compute().Texts.Select(t => t.Text).ToList();
        Assert.Contains("APPROVED BY", texts);
        Assert.DoesNotContain("", texts);
    }

    /// <summary>A sheet that says nothing keeps the engineering block — the ISO 7200 layout is
    /// an override, so nothing that predates it moves.</summary>
    [Fact]
    public void TheEngineeringBlockStaysTheDefault()
    {
        var sheet = new DrawingSheet(SheetFormat.A3) { Title = new TitleBlock { Title = "X" } };
        Assert.Null(sheet.TitleBlockLayoutOverride);
        Assert.IsType<EngineeringTitleBlock>(sheet.Frame().Layout);

        string before = sheet.ToSvg();
        sheet.TitleBlockLayoutOverride = Iso7200TitleBlock.Default;
        Assert.NotEqual(before, sheet.ToSvg());
        sheet.TitleBlockLayoutOverride = null;
        Assert.Equal(before, sheet.ToSvg());
    }
}
