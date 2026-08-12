using System.Globalization;
using System.Text;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>The standard-aperture shapes an RS-274X aperture can take.</summary>
public enum GerberApertureShape
{
    /// <summary>A round aperture (`C`) — a diameter.</summary>
    Circle,

    /// <summary>A rectangular aperture (`R`) — a width × height.</summary>
    Rectangle,

    /// <summary>An obround / stadium aperture (`O`) — a width × height (the short side is rounded).</summary>
    Obround,

    /// <summary>A regular-polygon aperture (`P`) — a circumscribed diameter, a vertex count, and an
    /// optional rotation. Emitted only where a copper feature is a regular polygon (the model does
    /// not build one today); the reader carries it for a hand-written Gerber.</summary>
    Polygon,
}

/// <summary>
/// One RS-274X standard aperture. A value, so equal apertures dedupe to one `%ADD` D-code. `A` is the
/// diameter (circle/polygon) or width (rectangle/obround); `B` is the height (rectangle/obround).
/// </summary>
/// <param name="Shape">The aperture shape.</param>
/// <param name="A">Diameter or width (mm).</param>
/// <param name="B">Height (mm) for rectangle/obround; 0 otherwise.</param>
/// <param name="Vertices">Vertex count for a polygon aperture; 0 otherwise.</param>
/// <param name="Rotation">Rotation (degrees) for a polygon aperture; 0 otherwise.</param>
public readonly record struct GerberAperture(
    GerberApertureShape Shape, double A, double B = 0, int Vertices = 0, double Rotation = 0)
{
    /// <summary>A round aperture of the given diameter (mm).</summary>
    public static GerberAperture Circle(double diameter) => new(GerberApertureShape.Circle, diameter);

    /// <summary>A rectangular aperture of the given width × height (mm).</summary>
    public static GerberAperture Rectangle(double width, double height) =>
        new(GerberApertureShape.Rectangle, width, height);

    /// <summary>An obround aperture of the given width × height (mm).</summary>
    public static GerberAperture Obround(double width, double height) =>
        new(GerberApertureShape.Obround, width, height);

    /// <summary>A regular-polygon aperture (circumscribed diameter, vertex count, rotation°).</summary>
    public static GerberAperture Polygon(double diameter, int vertices, double rotation = 0) =>
        new(GerberApertureShape.Polygon, diameter, 0, vertices, rotation);
}

/// <summary>
/// The RS-274X coordinate format: how many integer and fractional digits a coordinate carries. Gerber
/// coordinates are INTEGERS with an implied decimal point (leading zeros omitted, absolute), so a
/// value in millimetres is written as <c>round(mm × 10^FracDigits)</c> and read back by dividing.
///
/// <para><see cref="For"/> derives the digit counts from a board's own coordinate magnitudes — so the
/// resolution stays a fixed fraction of the model whatever its scale (the epsilon-ladder property the
/// whole ECAD side rests on): a metre-scale board and a millimetre-scale one both round-trip to ≈1e-9
/// of their extent, not to a fixed absolute grid that would coarsen one and overflow the other.</para>
/// </summary>
/// <param name="IntDigits">Integer digit count (bounds the largest coordinate: max ≈ 10^IntDigits mm).</param>
/// <param name="FracDigits">Fractional digit count (the resolution: 10^-FracDigits mm).</param>
public readonly record struct GerberFormat(int IntDigits, int FracDigits)
{
    /// <summary>The scale a millimetre value is multiplied by to reach the integer form.</summary>
    public double Unit => Math.Pow(10, FracDigits);

    /// <summary>The integer form of a millimetre value (round-to-nearest, ties away from zero).</summary>
    public long Encode(double mm) => (long)Math.Round(mm * Unit, MidpointRounding.AwayFromZero);

    /// <summary>The millimetre value of an integer coordinate.</summary>
    public double Decode(long value) => value / Unit;

    /// <summary>A coordinate (or I/J offset) as its signed integer digit string.</summary>
    public string Coord(double mm) => Encode(mm).ToString(CultureInfo.InvariantCulture);

    /// <summary>An aperture parameter as a fixed-decimal string (no exponent — Gerber has none),
    /// carrying <see cref="FracDigits"/> fractional digits so it is exact to the coordinate grid.</summary>
    public string Decimal(double value) =>
        value.ToString("F" + FracDigits, CultureInfo.InvariantCulture);

    /// <summary>The `%FSLAX..Y..*%` format-specification line.</summary>
    public string FormatSpec => $"%FSLAX{IntDigits}{FracDigits}Y{IntDigits}{FracDigits}*%";

    /// <summary>
    /// Derives a format from a board's coordinate magnitudes: fractional digits ≈ 1e-9 of the largest
    /// coordinate (so resolution scales with the model), integer digits with a two-place guard so the
    /// largest coordinate never overflows. Total digits are capped so every integer form stays exactly
    /// representable in a <see cref="long"/>/double.
    /// </summary>
    public static GerberFormat For(IEnumerable<double> magnitudes)
    {
        ArgumentNullException.ThrowIfNull(magnitudes);
        double maxMag = 1e-9;
        foreach (var m in magnitudes)
        {
            double a = Math.Abs(m);
            if (double.IsFinite(a) && a > maxMag)
                maxMag = a;
        }
        // Each field of the `%FS` spec is a SINGLE digit, so both counts stay in [1, 9]; the pair is
        // anti-correlated (large coordinates → fewer fractional digits), so their sum stays ≈ 11–12,
        // well inside a long's exact-integer range.
        int exp = (int)Math.Floor(Math.Log10(maxMag));
        int frac = Math.Clamp(9 - exp, 4, 9);
        int intd = Math.Clamp(exp + 2, 2, 8);
        return new GerberFormat(intd, frac);
    }
}

/// <summary>
/// The low-level RS-274X (extended Gerber) writer: aperture definitions, flashes (`D03`), draws
/// (`D01`/`D02`), region fills (`G36`/`G37`) and dark/clear polarity (`%LPD%`/`%LPC%`). Deterministic
/// — apertures dedupe by value and are emitted in D-code assignment order, and objects are serialized
/// in the order they were added, so one board yields byte-identical Gerber.
///
/// <para>Objects are added in three PHASES by the caller so a UNION of copper is reproduced faithfully:
/// dark solids (pads / via pads / region fills), then clear holes (via drills / region holes), then
/// dark traces last — so a trace running over a via re-fills its drill exactly as the copper model's
/// union does.</para>
/// </summary>
internal sealed class GerberBuilder
{
    private readonly GerberFormat _format;
    private readonly string _comment;
    private readonly bool _x2;
    private readonly Dictionary<GerberAperture, int> _apertures = [];
    private readonly List<GObject> _objects = [];
    private int _nextDCode = 10;

    internal GerberBuilder(GerberFormat format, string comment, bool x2 = false)
    {
        _format = format;
        _comment = comment;
        _x2 = x2;
    }

    private int ApertureCode(GerberAperture aperture)
    {
        if (!_apertures.TryGetValue(aperture, out int code))
            _apertures[aperture] = code = _nextDCode++;
        return code;
    }

    /// <summary>A flash (`D03`) of an aperture at a point, in the given polarity. <paramref name="net"/>
    /// is the object's net for an X2 <c>%TO.N%</c> attribute (ignored unless the builder is in X2 mode).</summary>
    internal void Flash(GerberAperture aperture, in Vector2d center, bool dark = true, string? net = null) =>
        _objects.Add(GObject.Flash(ApertureCode(aperture), center, dark, net));

    /// <summary>A stroked polyline: a dark draw (`D01`) with a round aperture of the trace width — the
    /// Minkowski sum of the centre-line with a disc, exactly the copper model's trace stroke.</summary>
    internal void Draw(double width, IReadOnlyList<Vector2d> polyline, string? net = null) =>
        _objects.Add(GObject.Draw(ApertureCode(GerberAperture.Circle(width)), polyline, dark: true, net));

    /// <summary>A region fill (`G36`/`G37`) of ONE closed contour of lines and circular arcs, in the
    /// given polarity. A Bézier boundary is refused (Gerber region contours carry no cubic).</summary>
    internal void Contour(IReadOnlyList<CurvedEdge2d> loop, bool dark, string? net = null) =>
        _objects.Add(GObject.Region(loop, dark, net));

    /// <summary>Whether any object has been added.</summary>
    internal bool IsEmpty => _objects.Count == 0;

    /// <summary>Serializes the accumulated objects into an RS-274X Gerber file.</summary>
    internal string Finish()
    {
        var sb = new StringBuilder();
        sb.Append(_format.FormatSpec).Append('\n');
        sb.Append("%MOMM*%").Append('\n');
        // X2 file attributes: who made the file. Opt-in, so a non-X2 file is byte-identical (nothing
        // emitted). The net-compare value is the per-object %TO.N% attribute below.
        if (_x2)
            sb.Append("%TF.GenerationSoftware,EngrCAD,EngrCAD*%").Append('\n');
        sb.Append("G04 ").Append(_comment).Append("*").Append('\n');
        foreach (var (aperture, code) in _apertures.OrderBy(kv => kv.Value))
            sb.Append(ApertureDefinition(code, aperture)).Append('\n');
        sb.Append("G75*").Append('\n');   // multi-quadrant arcs (region contours may carry them)
        sb.Append("G01*").Append('\n');   // linear interpolation is the default mode

        bool dark = true;
        int currentAperture = -1;
        string mode = "G01";
        string? currentNet = null;
        foreach (var o in _objects)
        {
            if (o.Dark != dark)
            {
                sb.Append(o.Dark ? "%LPD*%" : "%LPC*%").Append('\n');
                dark = o.Dark;
            }
            // X2 object attribute: the net this object belongs to (%TO.N,<net>*%), set when it changes
            // and deleted (%TD.N*%) when an object carries no net. Off unless the builder is in X2 mode.
            if (_x2 && !string.Equals(o.Net, currentNet, StringComparison.Ordinal))
            {
                sb.Append(o.Net is null ? "%TD.N*%" : $"%TO.N,{EscapeAttr(o.Net)}*%").Append('\n');
                currentNet = o.Net;
            }
            switch (o.Kind)
            {
                case GObjectKind.Flash:
                    SelectAperture(sb, o.DCode, ref currentAperture);
                    sb.Append('X').Append(_format.Coord(o.Center.X))
                      .Append('Y').Append(_format.Coord(o.Center.Y)).Append("D03*\n");
                    break;

                case GObjectKind.Draw:
                    SelectAperture(sb, o.DCode, ref currentAperture);
                    SetMode(sb, "G01", ref mode);
                    var first = o.Polyline![0];
                    sb.Append('X').Append(_format.Coord(first.X))
                      .Append('Y').Append(_format.Coord(first.Y)).Append("D02*\n");
                    for (int i = 1; i < o.Polyline.Count; i++)
                    {
                        var p = o.Polyline[i];
                        sb.Append('X').Append(_format.Coord(p.X))
                          .Append('Y').Append(_format.Coord(p.Y)).Append("D01*\n");
                    }
                    break;

                case GObjectKind.Region:
                    sb.Append("G36*\n");
                    EmitContour(sb, o.Loop!, ref mode);
                    sb.Append("G37*\n");
                    break;
            }
        }
        sb.Append("M02*\n");
        return sb.ToString();
    }

    private static void SelectAperture(StringBuilder sb, int code, ref int current)
    {
        if (code == current)
            return;
        sb.Append('D').Append(code).Append("*\n");
        current = code;
    }

    private static void SetMode(StringBuilder sb, string wanted, ref string mode)
    {
        if (mode == wanted)
            return;
        sb.Append(wanted).Append("*\n");
        mode = wanted;
    }

    private void EmitContour(StringBuilder sb, IReadOnlyList<CurvedEdge2d> loop, ref string mode)
    {
        var start = loop[0].Start;
        SetMode(sb, "G01", ref mode);
        sb.Append('X').Append(_format.Coord(start.X))
          .Append('Y').Append(_format.Coord(start.Y)).Append("D02*\n");
        foreach (var edge in loop)
        {
            switch (edge.Kind)
            {
                case CurvedEdgeKind.Line:
                    SetMode(sb, "G01", ref mode);
                    sb.Append('X').Append(_format.Coord(edge.End.X))
                      .Append('Y').Append(_format.Coord(edge.End.Y)).Append("D01*\n");
                    break;

                case CurvedEdgeKind.Arc:
                    // A full circle is split into two arcs so start != end on each (unambiguous under
                    // multi-quadrant); an ordinary arc emits as one.
                    if (edge.IsFullCircle)
                    {
                        EmitArc(sb, edge.Sub(0, 0.5), ref mode);
                        EmitArc(sb, edge.Sub(0.5, 1), ref mode);
                    }
                    else
                    {
                        EmitArc(sb, edge, ref mode);
                    }
                    break;

                default:
                    throw new NotSupportedException(
                        "A copper region boundary carries a Bézier edge, which RS-274X region contours "
                        + "cannot represent (they carry only straight segments and circular arcs). "
                        + "Flatten the copper to lines and arcs before exporting Gerber.");
            }
        }
    }

    private void EmitArc(StringBuilder sb, CurvedEdge2d arc, ref string mode)
    {
        string g = arc.SweepAngle > 0 ? "G03" : "G02";   // CCW / CW
        SetMode(sb, g, ref mode);
        double i = arc.Center.X - arc.Start.X;            // offset from arc start to its centre
        double j = arc.Center.Y - arc.Start.Y;
        sb.Append('X').Append(_format.Coord(arc.End.X))
          .Append('Y').Append(_format.Coord(arc.End.Y))
          .Append('I').Append(_format.Coord(i))
          .Append('J').Append(_format.Coord(j)).Append("D01*\n");
    }

    private string ApertureDefinition(int code, GerberAperture a) => a.Shape switch
    {
        GerberApertureShape.Circle => $"%ADD{code}C,{_format.Decimal(a.A)}*%",
        GerberApertureShape.Rectangle => $"%ADD{code}R,{_format.Decimal(a.A)}X{_format.Decimal(a.B)}*%",
        GerberApertureShape.Obround => $"%ADD{code}O,{_format.Decimal(a.A)}X{_format.Decimal(a.B)}*%",
        GerberApertureShape.Polygon => a.Rotation != 0
            ? $"%ADD{code}P,{_format.Decimal(a.A)}X{a.Vertices}X{_format.Decimal(a.Rotation)}*%"
            : $"%ADD{code}P,{_format.Decimal(a.A)}X{a.Vertices}*%",
        _ => throw new NotSupportedException($"Unknown aperture shape {a.Shape}."),
    };

    // X2 attribute fields are comma-separated and terminated by `*%`, so a value carrying the field
    // separator or a control char must escape it as \uXXXX (the spec's rule). Net names here are
    // identifiers, so this is a robustness guard that rarely fires.
    private static string EscapeAttr(string value)
    {
        if (!value.AsSpan().ContainsAny(",*%\\"))
            return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            if (c is ',' or '*' or '%' or '\\')
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private enum GObjectKind { Flash, Draw, Region }

    private readonly record struct GObject(
        GObjectKind Kind, int DCode, Vector2d Center,
        IReadOnlyList<Vector2d>? Polyline, IReadOnlyList<CurvedEdge2d>? Loop, bool Dark, string? Net)
    {
        public static GObject Flash(int code, in Vector2d c, bool dark, string? net) =>
            new(GObjectKind.Flash, code, c, null, null, dark, net);

        public static GObject Draw(int code, IReadOnlyList<Vector2d> poly, bool dark, string? net) =>
            new(GObjectKind.Draw, code, default, poly, null, dark, net);

        public static GObject Region(IReadOnlyList<CurvedEdge2d> loop, bool dark, string? net) =>
            new(GObjectKind.Region, 0, default, null, loop, dark, net);
    }
}

/// <summary>
/// Composes a whole copper layer (and the board outline) into RS-274X Gerber. It renders PADS as
/// aperture FLASHES (a proper Gerber pad, a small file), TRACES as round-aperture DRAWS (the stroke a
/// round aperture sweeps is exactly the copper model's trace region), VIA pads as solid disc FLASHES,
/// and anything else — a rotated pad, a copper pour, an arbitrary region — as a region FILL
/// (`G36`/`G37`), which is exact for any shape.
///
/// <para><b>The imaging order is the faithfulness argument, and it reproduces a UNION exactly.</b> The
/// copper model's copper on a layer is a UNION of feature regions, so a via drill (or a pour hole) is
/// a hole in the copper ONLY where nothing else covers it — a trace running over a via, or a via
/// under a pad (via-in-pad), fills it. So the writer emits all DARK solids first (pads / via discs /
/// traces / pours), then clears exactly the HOLES OF THE FINAL UNION: a via disc becomes its annular
/// ring only where the drill is genuinely exposed, and a via-in-pad or a routed via stays solid,
/// matching the union set for set. The caller supplies those holes (it already computed the union).</para>
/// </summary>
public static class GerberWriter
{
    /// <summary>
    /// The Gerber for one copper layer. <paramref name="features"/> are the solid copper features on
    /// the layer (component pads, pours) — NOT vias (passed separately, as solid discs) and NOT trace
    /// centre-lines (passed as <paramref name="traces"/>, so they draw). <paramref name="clearHoles"/>
    /// are the holes of the layer's final copper UNION (the exposed via drills / pour holes), cleared
    /// after all solids so the annular rings survive the round trip. A layer with no copper still
    /// yields a well-formed (headers + `M02`) empty Gerber.
    /// </summary>
    public static string CopperLayer(
        string layerName,
        IEnumerable<CopperFeature> features,
        IEnumerable<PlacedVia> vias,
        IEnumerable<(IReadOnlyList<Vector2d> Points, double Width, string? Net)> traces,
        IEnumerable<CurvedRegion2d> clearAir,
        GerberFormat format,
        bool x2 = false)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(vias);
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(clearAir);

        var builder = new GerberBuilder(format, $"EngrCAD copper layer '{layerName}'", x2);

        // Dark solids — every feature's OUTER copper: pads / pours as flashes-or-region-fills, via
        // pads as solid discs, traces as round-aperture draws. Each carries its NET for the X2
        // %TO.N% object attribute (a fab's net-compare datum); ignored unless x2 is on.
        foreach (var f in features)
            EmitSolid(builder, f.Region, x2 ? f.Net : null);
        foreach (var v in vias)
            builder.Flash(GerberAperture.Circle(v.PadDiameter), v.Center, dark: true, net: x2 ? v.Net : null);
        foreach (var (points, width, net) in traces)
            builder.Draw(width, points, x2 ? net : null);

        // Clear the TRUE AIR of the final union — the air pockets (via drills, pour anti-pads). An
        // air pocket may be a RING: a pour's clearance hole with an other-net pad ISLAND sitting in it,
        // where the pad is copper and only the ring around it is air. Clearing the ring's outer loop
        // erases the pad (drawn above), so its inner loops are re-DARKENED, restoring the island. A via
        // drill has no island, so this reduces to the plain circle clear.
        foreach (var air in clearAir)
            EmitAirClear(builder, air);

        return builder.Finish();
    }

    private static void EmitAirClear(GerberBuilder builder, CurvedRegion2d air)
    {
        // A simple circular pocket (a via drill / mounting hole) clears as one circle flash.
        if (air.Holes.Count == 0 && air.Outer.Count == 1 && air.Outer[0].IsFullCircle)
        {
            builder.Flash(GerberAperture.Circle(2 * air.Outer[0].Radius), air.Outer[0].Center, dark: false);
            return;
        }
        // Otherwise clear the outer contour, then re-dark any copper islands sitting inside it.
        builder.Contour(air.Outer, dark: false);
        foreach (var island in air.Holes)
            builder.Contour(island, dark: true);
    }

    /// <summary>The board-outline (edge-cuts) Gerber: the closed outline polygon traced with a thin
    /// round aperture. It is not copper, so it is not part of the copper round trip.</summary>
    public static string Outline(IReadOnlyList<Vector2d> outline, GerberFormat format)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var builder = new GerberBuilder(format, "EngrCAD board outline (Edge_Cuts)");
        if (outline.Count >= 2)
        {
            var b = Aabb.Empty;
            foreach (var p in outline)
                b = b.Union(new Vector3d(p.X, p.Y, 0));
            double extent = Math.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y);
            double width = Math.Max(extent * 1e-6, extent * 0.001);
            var closed = new List<Vector2d>(outline) { outline[0] };
            builder.Draw(width, closed);
        }
        return builder.Finish();
    }

    /// <summary>
    /// The Gerber for one solder-mask layer. By the standard positive-openings convention, the mask
    /// Gerber images the WINDOWS (the pad openings where mask is removed) as DARK — the fabricator
    /// clears mask where the Gerber is dark and leaves it elsewhere — so a decoded mask Gerber recovers
    /// the openings, not the mask coverage. Each opening flashes (a disc / rect / obround pad window) or
    /// region-fills (a rounded or rotated one), so the decoder rebuilds the exact same window. A side
    /// with no openings still yields a well-formed empty Gerber.
    /// </summary>
    public static string MaskLayer(
        string layerName, IEnumerable<CurvedRegion2d> openings, GerberFormat format)
    {
        ArgumentNullException.ThrowIfNull(openings);
        var builder = new GerberBuilder(
            format, $"EngrCAD solder mask '{layerName}' (openings imaged dark)");
        foreach (var opening in openings)
            EmitSolid(builder, opening);
        return builder.Finish();
    }

    /// <summary>
    /// The Gerber for one solder-paste (stencil) layer. It images the stencil APERTURES (the openings
    /// through which paste is deposited onto the SMD pads) as DARK — the same positive-openings
    /// convention the solder mask uses — so the stencil is cut where the Gerber is dark, and a decoded
    /// paste Gerber recovers the apertures. Each aperture flashes (a disc / rect / obround) or
    /// region-fills (a rounded or rotated one), so the decoder rebuilds the exact same aperture. A side
    /// with no apertures still yields a well-formed empty Gerber.
    /// </summary>
    public static string PasteLayer(
        string layerName, IEnumerable<CurvedRegion2d> apertures, GerberFormat format)
    {
        ArgumentNullException.ThrowIfNull(apertures);
        var builder = new GerberBuilder(
            format, $"EngrCAD solder paste '{layerName}' (apertures imaged dark)");
        foreach (var aperture in apertures)
            EmitSolid(builder, aperture);
        return builder.Finish();
    }

    /// <summary>
    /// The Gerber for one silkscreen layer — the reference / value / outline line-work drawn with a
    /// round aperture of the pen <paramref name="lineWidth"/> (a `D01` draw, exactly as a trace draws),
    /// so the round-trip decoder strokes each run back to the same footprint. A side with no strokes
    /// still yields a well-formed empty Gerber.
    /// </summary>
    public static string Silkscreen(
        string layerName, IEnumerable<IReadOnlyList<Vector2d>> strokes, double lineWidth,
        GerberFormat format)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (!(lineWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "The silkscreen pen width must be positive.");
        var builder = new GerberBuilder(format, $"EngrCAD silkscreen '{layerName}'");
        foreach (var polyline in strokes)
            if (polyline.Count >= 2)
                builder.Draw(lineWidth, polyline);
        return builder.Finish();
    }

    private static void EmitSolid(GerberBuilder builder, CurvedRegion2d region, string? net = null)
    {
        if (GerberShapes.TryDisc(region, out var c, out double d))
            builder.Flash(GerberAperture.Circle(d), c, net: net);
        else if (GerberShapes.TryAxisAlignedRect(region, out c, out double w, out double h))
            builder.Flash(GerberAperture.Rectangle(w, h), c, net: net);
        else if (GerberShapes.TryAxisAlignedObround(region, out c, out w, out h))
            builder.Flash(GerberAperture.Obround(w, h), c, net: net);
        else
            builder.Contour(region.Outer, dark: true, net: net);
    }
}
