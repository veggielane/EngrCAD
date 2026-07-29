using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// The hexagon socket recess — the assessed exception to the one-exact-revolve doctrine.
/// A hex is not a revolve, so a socket has to be a boolean, and the assessment is about
/// WHERE that boolean is exact: a hexagonal pocket whose rim lies in a PLANAR face is the
/// same exact case as sketch pockets (bounded-plane × plane intersections only; the tool
/// overshoots above the top so the boolean never sees coplanar faces).
///
/// <para>Three findings scope it to <see cref="SocketHeadCapScrew"/> alone.
/// (1) A full-turn REVOLVE's flat cap is not a plane to the kernel — it is a
/// <c>RevolvedSurface</c> with a pole at the axis, so the hex rim would wrap the pole,
/// which is band-wrap machinery the exact boolean does not have for cap faces; the
/// socketed cap screw is therefore REBUILT from cylinder primitives (planar caps) with
/// the shank overlapping into the head so every boolean stays transverse.
/// (2) A countersunk head has a planar top, but rebuilding it from primitives means a
/// cone frustum whose bottom rim IS the shank circle — cone and shank meet tangentially
/// along a shared rim, which the v1 boolean refuses; ISO 10642 sockets wait on tangent
/// unions (filed in todo.md). (3) A button head's socket rims on the DOME, so its rim is
/// a traced plane×sphere-band curve, not exact — no socket, documented.</para>
/// </summary>
internal static class HexSocketRecess
{
    /// <summary>Subtracts a hexagonal pocket of the given across-flats size and depth
    /// from a flat head top at <paramref name="topZ"/>. The tool overshoots the top by
    /// 10% of the depth (the drill-tool convention) so no coplanar faces ever meet.</summary>
    internal static Shape Cut(Shape body, double acrossFlats, double depth, double topZ)
    {
        double circumradius = acrossFlats / Math.Sqrt(3.0);
        var corners = new Vector2d[6];
        for (int i = 0; i < 6; i++)
        {
            double angle = i * Math.PI / 3;
            corners[i] = new(circumradius * Math.Cos(angle), circumradius * Math.Sin(angle));
        }
        var tool = Shape.Extrude(Sketch.Polygon(corners), depth * 1.1,
            SketchPlane.At(new Vector3d(0, 0, topZ - depth), Vector3d.UnitX, Vector3d.UnitY));
        return body - tool;
    }
}

/// <summary>
/// A hexagon socket button head screw — ISO 7380-1. Seats on the face (button heads are
/// not counterbored); the host gets an ISO 273 clearance hole and a far body the coarse
/// tap-drill pilot.
///
/// <para><b>Fidelity.</b> One exact axis-touching revolve: the button dome is the exact
/// spherical cap of diameter dk and height k (an arc in the profile — nothing is
/// faceted), on a plain shank of the nominal diameter. No hex socket: the socket rim
/// would lie in the DOME, not a plane, so the recess boolean would not be exact (see
/// <see cref="HexSocketRecess"/>). ⚠ Head dimensions transcribed from ISO 7380-1 —
/// verify against your supplier's datasheet before production use.</para>
/// </summary>
public sealed class ButtonHeadScrew : HardwareComponent
{
    // ISO 7380-1: head diameter dk, head height k. VERIFY against the datasheet.
    private sealed record Row(double HeadDiameter, double HeadHeight);

    private static readonly Dictionary<double, Row> Table = new()
    {
        [3.0] = new(5.7, 1.65),
        [4.0] = new(7.6, 2.2),
        [5.0] = new(9.5, 2.75),
        [6.0] = new(10.5, 3.3),
        [8.0] = new(14.0, 4.4),
        [10.0] = new(17.5, 5.5),
        [12.0] = new(21.0, 6.6),
    };

    private readonly HoleSpec _hostHole;

    /// <param name="size">Metric nominal size (4 = M4).</param>
    /// <param name="length">Shank length under the head.</param>
    /// <param name="fit">ISO 273 clearance fit of the through hole.</param>
    public ButtonHeadScrew(double size, double length, ClearanceFit fit = ClearanceFit.Normal)
    {
        if (!Table.TryGetValue(size, out var row))
            throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the ISO 7380 table (available: " +
                $"{string.Join(", ", Table.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Size = size;
        Length = length;
        Fit = fit;
        HeadDiameter = row.HeadDiameter;
        HeadHeight = row.HeadHeight;
        Thread = StandardThreads.Metric(size);
        _hostHole = StandardHoles.Clearance(size, fit);
    }

    /// <summary>Metric nominal size (4 = M4).</summary>
    public double Size { get; }

    /// <summary>Shank length under the head.</summary>
    public double Length { get; }

    /// <summary>ISO 273 clearance fit of the through hole.</summary>
    public ClearanceFit Fit { get; }

    /// <summary>ISO 7380 head diameter dk.</summary>
    public double HeadDiameter { get; }

    /// <summary>ISO 7380 head height k (the dome's height).</summary>
    public double HeadHeight { get; }

    /// <summary>The coarse-pitch thread this screw carries (ISO 261/262).</summary>
    public ThreadSpec Thread { get; }

    /// <inheritdoc />
    public override ThreadSpec? CarriesThread => Thread;

    public override string Designation => $"ISO 7380 M{Size:g3}×{Length:g3}";

    public override double InsertedLength => Length;

    /// <summary>Through-hardened low-alloy steel: ISO 7380 button heads are supplied in
    /// property class 10.9, which is a strength grade rather than a substance (see
    /// <see cref="FastenerMaterials"/>).</summary>
    public override Material? Material => FastenerMaterials.AlloySteel;

    protected override Shape BuildBody()
    {
        double a = HeadDiameter / 2;             // dome base radius
        double k = HeadHeight;
        double shankRadius = Size / 2;
        // The spherical cap through (0, k) and (a, 0): R = (a² + k²) / 2k, centred on the
        // axis at z = k − R. Exact — the profile carries the arc, nothing is faceted.
        double sphereRadius = (a * a + k * k) / (2 * k);
        var profile = Sketch.Start(0, k)
            .ArcTo(new(a, 0), sphereRadius, clockwise: true)
            .LineTo(shankRadius, 0)
            .LineTo(shankRadius, -Length)
            .LineTo(0, -Length)
            .Close();
        return Shape.Revolve(profile);
    }

    /// <summary>Plain clearance hole through the host (button heads bear on the face).</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(_hostHole, site.Points, site.Depth(site.ThroughDepth), site.Face);
    }

    /// <summary>The far body's coarse tap-drill pilot (as <see cref="SocketHeadCapScrew"/>).</summary>
    public override Shape PrepareAnchor(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            HoleSpec.Simple(Thread.TapDrillDiameter),
            site.Points,
            site.Depth(Length * 1.05),
            site.Face);
    }

    /// <summary>Two pitches of tap runout beyond the engagement, as any tapped hole.</summary>
    public override double AnchorDepth(double engagement) => engagement + 2 * Thread.Pitch;
}

/// <summary>
/// A hexagon socket countersunk head screw — ISO 10642 (DIN 7991). The head sinks flush
/// into the 90° countersink that <see cref="StandardHoles.Countersunk"/> cuts; the head
/// diameter is DERIVED from that same table column, so screw and hole cannot drift apart.
///
/// <para><b>Seating.</b> The seating datum is the head's TOP face — flush with the host
/// face by definition of a countersunk screw — so <see cref="HardwareComponent.SeatDepth"/>
/// is 0 and lengths are overall, as ISO 10642 measures them.</para>
///
/// <para><b>Fidelity.</b> One exact axis-touching revolve: a sharp 90° conical head (head
/// height = (dk − d)/2 exactly, no cylindrical land) on a plain shank. No hex socket:
/// the head top IS planar, but a primitive rebuild would make the cone's bottom rim and
/// the shank tangent along a shared circle, which the v1 boolean refuses — see
/// <see cref="HexSocketRecess"/> for the assessment; filed in todo.md.</para>
/// </summary>
public sealed class CountersunkScrew : HardwareComponent
{
    // Only the sizes with a countersink column in StandardHoles' table are offered; the
    // head diameter itself is DERIVED from that column, never a second table.
    private static readonly double[] Sizes = [2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0];

    private readonly HoleSpec _hostHole;

    /// <param name="size">Metric nominal size (4 = M4).</param>
    /// <param name="length">OVERALL length including the head — how ISO 10642 measures
    /// countersunk screws.</param>
    /// <param name="fit">ISO 273 clearance fit of the through hole.</param>
    public CountersunkScrew(
        double size, double length,
        ClearanceFit fit = ClearanceFit.Normal)
    {
        if (!Sizes.Contains(size))
            throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the ISO 10642 table (available: " +
                $"{string.Join(", ", Sizes.Select(k => $"M{k:g3}"))}).");
        HeadDiameter = StandardHoles.CountersunkHeadDiameter(size);
        HeadHeight = (HeadDiameter - size) / 2;   // the 90° cone, sharp
        if (length <= HeadHeight)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"An ISO 10642 M{size:g3} head is {HeadHeight:g4} tall; the overall length must exceed it.");

        Size = size;
        Length = length;
        Fit = fit;
        Thread = StandardThreads.Metric(size);
        _hostHole = StandardHoles.Countersunk(size, fit);
    }

    /// <summary>Metric nominal size (4 = M4).</summary>
    public double Size { get; }

    /// <summary>Overall length including the head.</summary>
    public double Length { get; }

    /// <summary>ISO 273 clearance fit of the through hole.</summary>
    public ClearanceFit Fit { get; }

    /// <summary>Head diameter dk — <see cref="StandardHoles.CountersunkHeadDiameter"/>,
    /// so the head fills its countersink exactly.</summary>
    public double HeadDiameter { get; }

    /// <summary>Head height (dk − d)/2: the sharp 90° cone.</summary>
    public double HeadHeight { get; }

    /// <summary>The coarse-pitch thread this screw carries (ISO 261/262).</summary>
    public ThreadSpec Thread { get; }

    /// <inheritdoc />
    public override ThreadSpec? CarriesThread => Thread;

    public override string Designation => $"ISO 10642 M{Size:g3}×{Length:g3}";

    /// <summary>Lengths are overall, and the head top is the (flush) seating datum, so
    /// the whole length reaches below it.</summary>
    public override double InsertedLength => Length;

    /// <summary>Through-hardened low-alloy steel: ISO 10642 countersunk screws are
    /// supplied in property class 10.9 (see <see cref="FastenerMaterials"/> for why the
    /// class does not get its own material).</summary>
    public override Material? Material => FastenerMaterials.AlloySteel;

    protected override Shape BuildBody()
    {
        double headRadius = HeadDiameter / 2;
        double shankRadius = Size / 2;
        // Datum = the flush head top at z = 0: cone down to the shank, shank to −Length.
        var profile = Sketch.Polygon(
        [
            new(0, 0),
            new(headRadius, 0),
            new(shankRadius, -HeadHeight),
            new(shankRadius, -Length),
            new(0, -Length),
        ]);
        return Shape.Revolve(profile);
    }

    /// <summary>The 90° countersunk clearance hole, through the host.</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(_hostHole, site.Points, site.Depth(site.ThroughDepth), site.Face);
    }

    /// <summary>The far body's coarse tap-drill pilot (as <see cref="SocketHeadCapScrew"/>).</summary>
    public override Shape PrepareAnchor(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            HoleSpec.Simple(Thread.TapDrillDiameter),
            site.Points,
            site.Depth(Length * 1.05),
            site.Face);
    }

    /// <summary>Two pitches of tap runout beyond the engagement, as any tapped hole.</summary>
    public override double AnchorDepth(double engagement) => engagement + 2 * Thread.Pitch;
}

/// <summary>
/// A hexagon nut — ISO 4032, style 1. A nut is a thread PROVIDER: place one on the far
/// face of a stack and a screw can anchor into it (the <c>anchorInto</c> overload of
/// <see cref="ComponentAssembly.PlaceThrough(HardwareComponent, IReadOnlyList{Vector2d}, SketchPlane, ComponentAssembly, SketchPlane, ComponentFeature)"/>),
/// which requires the screw to protrude through the nut's full height.
///
/// <para><b>Preparation.</b> Placing a nut drills the ISO 273 clearance hole through its
/// host — a nut on a face implies a bolt passing through there, and with a nut on the far
/// side BOTH bodies get clearance (nothing is tapped).</para>
///
/// <para><b>Fidelity.</b> A hexagonal prism (exact extrude) bored to the NOMINAL thread
/// diameter — no modeled thread, no washer-face, no 30° chamfer cones. ⚠ Width
/// across-flats and height transcribed from ISO 4032 — verify against the datasheet.</para>
/// </summary>
public sealed class HexNut : HardwareComponent
{
    // ISO 4032: width across flats s, nut height m. VERIFY against the datasheet.
    private sealed record Row(double AcrossFlats, double Height);

    private static readonly Dictionary<double, Row> Table = new()
    {
        [2.0] = new(4.0, 1.6),
        [2.5] = new(5.0, 2.0),
        [3.0] = new(5.5, 2.4),
        [4.0] = new(7.0, 3.2),
        [5.0] = new(8.0, 4.7),
        [6.0] = new(10.0, 5.2),
        [8.0] = new(13.0, 6.8),
        [10.0] = new(16.0, 8.4),
        [12.0] = new(18.0, 10.8),
    };

    /// <param name="size">Metric nominal thread size (4 = M4).</param>
    /// <param name="fit">ISO 273 clearance fit of the hole the placement drills.</param>
    public HexNut(double size, ClearanceFit fit = ClearanceFit.Normal)
    {
        if (!Table.TryGetValue(size, out var row))
            throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the ISO 4032 table (available: " +
                $"{string.Join(", ", Table.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");
        Size = size;
        Fit = fit;
        AcrossFlats = row.AcrossFlats;
        Height = row.Height;
        Thread = StandardThreads.Metric(size);
    }

    /// <summary>Metric nominal thread size.</summary>
    public double Size { get; }

    /// <summary>ISO 273 clearance fit of the hole the placement drills.</summary>
    public ClearanceFit Fit { get; }

    /// <summary>Width across flats s (the spanner size).</summary>
    public double AcrossFlats { get; }

    /// <summary>Nut height m.</summary>
    public double Height { get; }

    /// <summary>The metric thread the nut provides.</summary>
    public ThreadSpec Thread { get; }

    public override string Designation => $"ISO 4032 M{Size:g3}";

    /// <summary>A nut bears ON the face; nothing goes into the host.</summary>
    public override double InsertedLength => 0;

    /// <summary>Plain carbon steel: ISO 4032 nuts are property class 8 as standard (the
    /// class-10 nut that pairs with a 12.9 bolt is alloy steel, and weighs the same —
    /// see <see cref="FastenerMaterials"/>).</summary>
    public override Material? Material => FastenerMaterials.CarbonSteel;

    /// <inheritdoc />
    public override ThreadSpec? ProvidesThread => Thread;

    /// <summary>A mating screw must engage the nut's full height — the standard's
    /// premise is that a bolt protrudes through its nut.</summary>
    public override double? MinimumEngagement => Height;

    protected override Shape BuildBody()
    {
        // Datum = the bearing face at z = 0; the nut stands z ∈ [0, Height] out of the
        // host. Bored to the nominal diameter (thread not modeled); the drill tool
        // overshoots below the bottom face so the boolean never sees coplanar faces.
        double circumradius = AcrossFlats / Math.Sqrt(3.0);
        var corners = new Vector2d[6];
        for (int i = 0; i < 6; i++)
        {
            double angle = i * Math.PI / 3;
            corners[i] = new(circumradius * Math.Cos(angle), circumradius * Math.Sin(angle));
        }
        var prism = Shape.Extrude(Sketch.Polygon(corners), Height);
        return prism.Drill(
            HoleSpec.Simple(Size),
            [new Vector2d(0, 0)],
            depth: Height * 1.05,
            SketchPlane.At(new Vector3d(0, 0, Height), Vector3d.UnitX, Vector3d.UnitY));
    }

    /// <summary>The bolt's clearance hole through the host — a nut implies a through
    /// bolt, and a nutted joint taps nothing.</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            StandardHoles.Clearance(Size, Fit),
            site.Points,
            site.Depth(site.ThroughDepth),
            site.Face);
    }
}

/// <summary>
/// A plain washer — ISO 7089 (normal series, 200 HV). A washer prepares NOTHING: the
/// clearance hole belongs to the screw whose stack it spaces, so
/// <see cref="Prepare"/> deliberately returns the host untouched and placing one only
/// adds the occurrence.
///
/// <para><b>Fidelity.</b> An exact annular disk (cylinder + bore). ⚠ Dimensions
/// transcribed from ISO 7089 — verify against the datasheet.</para>
/// </summary>
public sealed class PlainWasher : HardwareComponent
{
    // ISO 7089: clearance ID d1, OD d2, thickness h. VERIFY against the datasheet.
    private sealed record Row(double InnerDiameter, double OuterDiameter, double Thickness);

    private static readonly Dictionary<double, Row> Table = new()
    {
        [2.0] = new(2.2, 5.0, 0.3),
        [2.5] = new(2.7, 6.0, 0.5),
        [3.0] = new(3.2, 7.0, 0.5),
        [4.0] = new(4.3, 9.0, 0.8),
        [5.0] = new(5.3, 10.0, 1.0),
        [6.0] = new(6.4, 12.0, 1.6),
        [8.0] = new(8.4, 16.0, 1.6),
        [10.0] = new(10.5, 20.0, 2.0),
        [12.0] = new(13.0, 24.0, 2.5),
    };

    /// <param name="size">Metric nominal fastener size the washer fits (4 = M4).</param>
    public PlainWasher(double size)
    {
        if (!Table.TryGetValue(size, out var row))
            throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the ISO 7089 table (available: " +
                $"{string.Join(", ", Table.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");
        Size = size;
        InnerDiameter = row.InnerDiameter;
        OuterDiameter = row.OuterDiameter;
        Thickness = row.Thickness;
    }

    /// <summary>Metric nominal fastener size the washer fits.</summary>
    public double Size { get; }

    /// <summary>Hole diameter d1.</summary>
    public double InnerDiameter { get; }

    /// <summary>Outside diameter d2.</summary>
    public double OuterDiameter { get; }

    /// <summary>Thickness h.</summary>
    public double Thickness { get; }

    public override string Designation => $"ISO 7089 M{Size:g3}";

    /// <summary>A washer bears ON the face; nothing goes into the host.</summary>
    public override double InsertedLength => 0;

    /// <summary>Plain carbon steel: ISO 7089 washers are supplied at 200 HV as standard
    /// (the A2/A4 stainless variants are in <see cref="FastenerMaterials"/>).</summary>
    public override Material? Material => FastenerMaterials.CarbonSteel;

    protected override Shape BuildBody()
    {
        // Datum = the bearing face at z = 0; the washer stands z ∈ [0, Thickness].
        var disk = Shape.Cylinder(OuterDiameter / 2, Thickness).Translate(0, 0, Thickness / 2);
        return disk.Drill(
            HoleSpec.Simple(InnerDiameter),
            [new Vector2d(0, 0)],
            depth: Thickness * 1.05,
            SketchPlane.At(new Vector3d(0, 0, Thickness), Vector3d.UnitX, Vector3d.UnitY));
    }

    /// <summary>Deliberately a no-op: the hole under a washer belongs to the screw whose
    /// stack it spaces (or to the nut on the other end). A washer only spaces and
    /// spreads load — it removes no material of its own.</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body;
    }
}

/// <summary>
/// A deep groove ball bearing — the 60x/600x miniature family (608 and its relatives),
/// simplified. Placing one cuts its press-fit housing pocket: a flat-bottomed bore of the
/// bearing's outside diameter, exactly as deep as the bearing is wide, so the bearing
/// seats flush (the nominal-size press fit convention — the interference lives in
/// tolerances this kernel does not model, exactly as <see cref="DowelPin"/> documents).
///
/// <para><b>Fidelity.</b> Two exact concentric rings (each a bored cylinder; the union is
/// disjoint, so the boolean is the trivial multi-shell case): the radial span splits into
/// thirds — inner ring, ball gap, outer ring. No balls, no cage, no shields, no fillets.
/// ⚠ Boundary dimensions transcribed from the common catalogue — verify against your
/// bearing maker's datasheet.</para>
/// </summary>
public sealed class DeepGrooveBearing : HardwareComponent
{
    // Designation → (bore d, outside diameter D, width B). VERIFY against the datasheet.
    private sealed record Row(double Bore, double OuterDiameter, double Width);

    private static readonly Dictionary<string, Row> Table = new()
    {
        ["603"] = new(3, 9, 5),
        ["604"] = new(4, 12, 4),
        ["605"] = new(5, 14, 5),
        ["606"] = new(6, 17, 6),
        ["607"] = new(7, 19, 6),
        ["608"] = new(8, 22, 7),
        ["609"] = new(9, 24, 7),
        ["6000"] = new(10, 26, 8),
        ["6001"] = new(12, 28, 8),
        ["6002"] = new(15, 32, 9),
    };

    /// <param name="code">Bearing designation, e.g. "608".</param>
    public DeepGrooveBearing(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!Table.TryGetValue(code, out var row))
            throw new ArgumentOutOfRangeException(nameof(code),
                $"'{code}' is not in the bearing table (available: " +
                $"{string.Join(", ", Table.Keys.OrderBy(k => k, StringComparer.Ordinal))}).");
        Code = code;
        Bore = row.Bore;
        OuterDiameter = row.OuterDiameter;
        Width = row.Width;
    }

    /// <summary>The catalogue designation ("608").</summary>
    public string Code { get; }

    /// <summary>Bore (shaft) diameter d.</summary>
    public double Bore { get; }

    /// <summary>Outside diameter D — also the housing pocket the placement cuts.</summary>
    public double OuterDiameter { get; }

    /// <summary>Width B — also the pocket depth, so the bearing seats flush.</summary>
    public double Width { get; }

    public override string Designation => $"Bearing {Code}";

    /// <summary>Pressed fully home: the whole width sits below the housing face.</summary>
    public override double InsertedLength => Width;

    /// <summary>
    /// <b>Deliberately none</b> — the one catalogue entry that declines to say what it is
    /// made of, and the reason is fidelity rather than ignorance. The v1 body is two rings
    /// with an empty gap where the balls and cage are, so density × volume is measurably
    /// LESS than the bearing's real mass; a stated material would report that shortfall as
    /// a confident number in a bill of materials, while an unstated one reports "unknown",
    /// which is what the BOM's own rule asks for (an unknown mass is an empty cell, never a
    /// zero, because a spreadsheet sums zeros silently). A design that would rather have
    /// the lower bound says <c>bearing.ToPart().Of(FastenerMaterials.BearingSteel)</c>.
    /// </summary>
    public override Material? Material => null;

    protected override Shape BuildBody()
    {
        // Datum = the bearing's outer face, flush with the housing face: z ∈ [−Width, 0].
        // The radial span (bore → OD) splits into thirds: inner ring, ball gap, outer
        // ring. The two rings are disjoint, so their union has no intersection curves.
        double inner = Bore / 2, outer = OuterDiameter / 2;
        double ringThickness = (outer - inner) / 3;
        var outerRing = Ring(outer - ringThickness, outer);
        var innerRing = Ring(inner, inner + ringThickness);
        return outerRing | innerRing;

        Shape Ring(double boreRadius, double outerRadius)
        {
            var sleeve = Shape.Cylinder(outerRadius, Width).Translate(0, 0, -Width / 2);
            return sleeve.Drill(
                HoleSpec.Simple(boreRadius * 2),
                [new Vector2d(0, 0)],
                depth: Width * 1.05,
                SketchPlane.XY);
        }
    }

    /// <summary>The flat-bottomed housing pocket: the outside diameter, one width deep,
    /// so the placed bearing sits flush with the face.</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            HoleSpec.Simple(OuterDiameter),
            site.Points,
            site.Depth(Width),
            site.Face);
    }
}

public static partial class StandardComponents
{
    /// <summary>A hexagon socket button head screw (ISO 7380-1).</summary>
    public static ButtonHeadScrew ButtonScrew(
        double size, double length, ClearanceFit fit = ClearanceFit.Normal) =>
        new(size, length, fit);

    /// <summary>A hexagon socket countersunk head screw (ISO 10642 / DIN 7991); lengths
    /// are OVERALL including the head.</summary>
    public static CountersunkScrew CskScrew(
        double size, double length, ClearanceFit fit = ClearanceFit.Normal) =>
        new(size, length, fit);

    /// <summary>A hexagon nut (ISO 4032); placing one drills the bolt's clearance hole.</summary>
    public static HexNut Nut(double size, ClearanceFit fit = ClearanceFit.Normal) =>
        new(size, fit);

    /// <summary>A plain washer (ISO 7089); placing one cuts nothing.</summary>
    public static PlainWasher Washer(double size) => new(size);

    /// <summary>A deep groove ball bearing ("608" and relatives, simplified); placing
    /// one cuts its flush press-fit housing pocket.</summary>
    public static DeepGrooveBearing Bearing(string code) => new(code);
}
