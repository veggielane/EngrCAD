using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>How a screw head sits on the body it fastens.</summary>
public enum ScrewSeating
{
    /// <summary>The head stands proud on the face; the host gets a plain clearance hole.</summary>
    OnFace,

    /// <summary>The head is recessed into a counterbore (DIN 974 diameters via
    /// <see cref="StandardHoles.Counterbored"/>, 0.5 mm below flush).</summary>
    Counterbored,
}

/// <summary>
/// A hexagon socket head cap screw — ISO 4762 (DIN 912). The <b>preparation</b> is the
/// point: placing one drills its own ISO 273 clearance hole and, when
/// <see cref="Seating"/> is <see cref="ScrewSeating.Counterbored"/>, the DIN 974
/// counterbore that seats the head; anchoring one in a far body drills that body's
/// coarse-pitch tap-drill pilot.
///
/// <para><b>Fidelity (v1).</b> The body is a single exact solid of revolution: a
/// cylindrical head of diameter dk and height k on a plain cylindrical shank of the
/// nominal diameter. There is <b>no hex socket recess and no modeled thread</b> — model
/// real threads with <see cref="Shape.ExternalThread(ThreadSpec, double, double, bool)"/>
/// when you need them. The dimensions that matter for fit (clearance, counterbore, tap
/// drill, head diameter, head height, length) are the standard ones.</para>
/// </summary>
public sealed class SocketHeadCapScrew : HardwareComponent
{
    // ISO 4762 nominal head diameter dk. The head height is k = d exactly (that IS the
    // standard's rule for this family), so only dk needs a table.
    // VERIFY against your supplier's datasheet before production use — head diameters
    // vary between the ISO 4762 and older DIN 912 revisions for some sizes.
    private static readonly Dictionary<double, double> HeadDiameters = new()
    {
        [2.0] = 3.8,
        [2.5] = 4.5,
        [3.0] = 5.5,
        [4.0] = 7.0,
        [5.0] = 8.5,
        [6.0] = 10.0,
        [8.0] = 13.0,
        [10.0] = 16.0,
        [12.0] = 18.0,
    };

    private readonly HoleSpec _hostHole;

    /// <param name="size">Metric nominal size (4 = M4).</param>
    /// <param name="length">Shank length under the head — how ISO 4762 lengths are measured.</param>
    /// <param name="seating">Whether the head stands proud or sits in a counterbore.</param>
    /// <param name="fit">ISO 273 clearance fit of the through hole.</param>
    public SocketHeadCapScrew(
        double size, double length,
        ScrewSeating seating = ScrewSeating.Counterbored,
        ClearanceFit fit = ClearanceFit.Normal)
    {
        if (!HeadDiameters.TryGetValue(size, out double headDiameter))
            throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the ISO 4762 table (available: " +
                $"{string.Join(", ", HeadDiameters.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Size = size;
        Length = length;
        Seating = seating;
        Fit = fit;
        HeadDiameter = headDiameter;
        Thread = StandardThreads.Metric(size);
        _hostHole = seating == ScrewSeating.Counterbored
            ? StandardHoles.Counterbored(size, fit)
            : StandardHoles.Clearance(size, fit);
    }

    /// <summary>Metric nominal size (4 = M4).</summary>
    public double Size { get; }

    /// <summary>Shank length under the head.</summary>
    public double Length { get; }

    /// <summary>Whether the head stands proud or sits in a counterbore.</summary>
    public ScrewSeating Seating { get; }

    /// <summary>ISO 273 clearance fit of the through hole.</summary>
    public ClearanceFit Fit { get; }

    /// <summary>ISO 4762 head diameter dk.</summary>
    public double HeadDiameter { get; }

    /// <summary>ISO 4762 head height k, which equals the nominal diameter.</summary>
    public double HeadHeight => Size;

    /// <summary>The coarse-pitch thread this screw carries (ISO 261/262).</summary>
    public ThreadSpec Thread { get; }

    /// <summary>The hole recipe this screw cuts in the body it passes through.</summary>
    public HoleSpec HostHole => _hostHole;

    /// <summary>Catalogue designation, e.g. "ISO 4762 M4x16" (the separator is the
    /// multiplication sign, as in <see cref="ThreadSpec.Designation"/>).</summary>
    public override string Designation => $"ISO 4762 M{Size:g3}×{Length:g3}";

    public override double InsertedLength => Length;

    /// <summary>A counterbored head sits at the recess floor; a proud head bears on the face.</summary>
    public override double SeatDepth =>
        Seating == ScrewSeating.Counterbored ? _hostHole.CounterboreDepth : 0;

    protected override Shape BuildBody()
    {
        double headRadius = HeadDiameter / 2;
        double shankRadius = Size / 2;
        // One axis-touching full-turn revolve (exact in every representation): head
        // above the bearing face at z = 0, shank below it.
        var profile = Sketch.Polygon(
        [
            new(0, HeadHeight),
            new(headRadius, HeadHeight),
            new(headRadius, 0),
            new(shankRadius, 0),
            new(shankRadius, -Length),
            new(0, -Length),
        ]);
        return Shape.Revolve(profile);
    }

    /// <summary>Clearance hole (plus counterbore when counterbored), through the host.</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        double depth = site.Depth(site.ThroughDepth);
        if (depth <= SeatDepth)
            throw new ArgumentException(
                $"{Designation} needs {SeatDepth:g4} of counterbore but the host is only {depth:g4} deep " +
                "below the seating face — use ScrewSeating.OnFace, a smaller screw, or a thicker body.",
                nameof(site));
        return site.Body.Drill(_hostHole, site.Points, depth, site.Face);
    }

    /// <summary>The far body's coarse tap-drill pilot. The thread itself is not modeled;
    /// <see cref="Shape.ThreadedHole"/> does that when a design needs real geometry.</summary>
    public override Shape PrepareAnchor(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            HoleSpec.Simple(Thread.TapDrillDiameter),
            site.Points,
            site.Depth(Length * 1.05),
            site.Face);
    }

    /// <summary>Tapped holes are drilled deeper than the thread engagement — a tap
    /// cannot cut usable threads to the bottom of a blind hole. Two pitches of runout.</summary>
    public override double AnchorDepth(double engagement) => engagement + 2 * Thread.Pitch;
}

/// <summary>
/// A Tappex Trisert® self-tapping threaded insert for plastics: the host gets its pilot
/// bore, the insert supplies the metric thread. Pilot diameters and lengths come from
/// <see cref="StandardHoles"/>.
///
/// <para><b>Fidelity (v1).</b> The body is a plain sleeve — outside diameter and length
/// from the catalogue, bored to the thread's minor diameter — with no knurl, no flange
/// and no modeled internal thread. ⚠ The Trisert table is transcribed and flagged in
/// <see cref="StandardHoles"/>: verify against the current Tappex datasheet for your
/// insert type and host material before production use.</para>
/// </summary>
public sealed class ThreadedInsert : HardwareComponent
{
    /// <param name="size">Metric nominal thread size the insert provides (4 = M4).</param>
    public ThreadedInsert(double size)
    {
        Size = size;
        OutsideDiameter = StandardHoles.TrisertDiameter(size);
        BodyLength = StandardHoles.TrisertLength(size);
        Thread = StandardThreads.Metric(size);
    }

    /// <summary>Metric nominal thread size the insert provides.</summary>
    public double Size { get; }

    /// <summary>Insert outside diameter — also the host's pilot bore diameter.</summary>
    public double OutsideDiameter { get; }

    /// <summary>Insert body length.</summary>
    public double BodyLength { get; }

    /// <summary>The metric thread the installed insert provides.</summary>
    public ThreadSpec Thread { get; }

    public override string Designation => $"Tappex Trisert M{Size:g3}";

    public override double InsertedLength => BodyLength;

    public override PartColor Color => Palette.Brass;

    protected override Shape BuildBody()
    {
        // Datum = the insert's top face, flush with the host, so the body sits in
        // z ∈ [−BodyLength, 0]. Bored through with the thread's minor diameter (the
        // deepest the internal thread reaches); the bore overshoots so the boolean
        // never sees a coplanar bottom.
        var sleeve = Shape.Cylinder(OutsideDiameter / 2, BodyLength).Translate(0, 0, -BodyLength / 2);
        return sleeve.Drill(
            HoleSpec.Simple(Thread.MinorDiameter),
            [new Vector2d(0, 0)],
            depth: BodyLength * 1.1,
            SketchPlane.XY);
    }

    /// <summary>The host's pilot bore, at least deep enough for the insert plus bottom
    /// clearance (<see cref="StandardHoles.TrisertMinimumDepth"/>).</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            StandardHoles.Trisert(Size),
            site.Points,
            site.Depth(StandardHoles.TrisertMinimumDepth(Size)),
            site.Face);
    }
}

/// <summary>
/// A parallel dowel pin — ISO 2338, the m6 tolerance class used for location. Placing
/// one reams its hole; placing one <em>through</em> two bodies reams both, which is what
/// a locating pin is for.
///
/// <para><b>Fidelity (v1).</b> The body is an exact solid of revolution with 45° end
/// chamfers rather than the standard's crowned ends. The modeled bore is the
/// <b>nominal</b> diameter: the m6/H7 interference that actually makes the fit lives in
/// tolerances this kernel does not model, so an as-modeled pin and hole are the same
/// size.</para>
/// </summary>
public sealed class DowelPin : HardwareComponent
{
    /// <param name="diameter">Nominal pin diameter.</param>
    /// <param name="length">Overall pin length.</param>
    /// <param name="insertedLength">How much of the pin goes into the host body;
    /// defaults to half, the usual locating arrangement. Pass the full length for a pin
    /// that finishes flush.</param>
    public DowelPin(double diameter, double length, double? insertedLength = null)
    {
        if (diameter <= 0)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        double inserted = insertedLength ?? length / 2;
        if (inserted <= 0 || inserted > length)
            throw new ArgumentOutOfRangeException(nameof(insertedLength),
                "The inserted length must be positive and no longer than the pin.");

        Diameter = diameter;
        Length = length;
        Inserted = inserted;
        // ISO 2338 ends are chamfered/crowned at roughly 0.12·d; keep the chamfer clear
        // of the pin's own ends and well inside its radius.
        Chamfer = Math.Min(0.12 * diameter, 0.2 * length);
    }

    public double Diameter { get; }

    public double Length { get; }

    /// <summary>How much of the pin sits inside the host.</summary>
    public double Inserted { get; }

    /// <summary>45° end chamfer size.</summary>
    public double Chamfer { get; }

    public override string Designation => $"ISO 2338 Ø{Diameter:g3}×{Length:g3} m6";

    public override double InsertedLength => Inserted;

    protected override Shape BuildBody()
    {
        double r = Diameter / 2;
        double c = Chamfer;
        double top = Length - Inserted;   // protrusion above the datum (may be zero)
        double bottom = -Inserted;
        var profile = Sketch.Polygon(
        [
            new(0, top),
            new(r - c, top),
            new(r, top - c),
            new(r, bottom + c),
            new(r - c, bottom),
            new(0, bottom),
        ]);
        return Shape.Revolve(profile);
    }

    /// <summary>The reamed hole, just past the inserted length.</summary>
    public override Shape Prepare(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            HoleSpec.Simple(Diameter),
            site.Points,
            site.Depth(Inserted * 1.05),
            site.Face);
    }

    /// <summary>A dowel reams the far body of a stack exactly as it reams the near one.</summary>
    public override Shape PrepareAnchor(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Body.Drill(
            HoleSpec.Simple(Diameter),
            site.Points,
            site.Depth(Inserted * 1.05),
            site.Face);
    }

    /// <summary>A press-fit hole gets a little bottom clearance, no runout allowance.</summary>
    public override double AnchorDepth(double engagement) => engagement * 1.05;
}

/// <summary>
/// The standard component catalogue — real hardware whose placement both prepares the
/// host and assembles itself (see <see cref="HardwareComponent"/>). v1 is deliberately
/// small and correct rather than broad: one screw family, one insert, one pin. The
/// dimensions come from the same datasheet-driven tables as <see cref="StandardHoles"/>
/// and <see cref="StandardThreads"/>, and anything transcribed rather than derived from
/// a stated formula carries a verify-against-the-datasheet warning.
/// </summary>
public static class StandardComponents
{
    /// <summary>A hexagon socket head cap screw (ISO 4762 / DIN 912).</summary>
    public static SocketHeadCapScrew CapScrew(
        double size, double length,
        ScrewSeating seating = ScrewSeating.Counterbored,
        ClearanceFit fit = ClearanceFit.Normal) =>
        new(size, length, seating, fit);

    /// <summary>A Tappex Trisert® self-tapping threaded insert (⚠ verify the table).</summary>
    public static ThreadedInsert TrisertInsert(double size) => new(size);

    /// <summary>A parallel dowel pin (ISO 2338 m6).</summary>
    public static DowelPin Dowel(double diameter, double length, double? insertedLength = null) =>
        new(diameter, length, insertedLength);
}
