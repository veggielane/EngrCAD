using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>Which side wall of a box <see cref="Enclosure"/> a <see cref="PanelCutout"/> is
/// cut into. The four vertical walls of the interior cavity, named by the interior axis whose
/// extreme they sit at (the cavity spans x ∈ [−W/2, W/2], y ∈ [−D/2, D/2] about the interior
/// origin, so <see cref="MaxX"/> is the +X wall, and so on).</summary>
public enum PanelWall
{
    /// <summary>The −X wall (inner face at x = −W/2, outward normal −X).</summary>
    MinX,

    /// <summary>The +X wall (inner face at x = +W/2, outward normal +X).</summary>
    MaxX,

    /// <summary>The −Y wall (inner face at y = −D/2, outward normal −Y).</summary>
    MinY,

    /// <summary>The +Y wall (inner face at y = +D/2, outward normal +Y).</summary>
    MaxY,
}

/// <summary>
/// An opening cut through one side wall of a box <see cref="Enclosure"/> for a connector to
/// protrude through — a rectangular slot (USB, RJ45, a header) or a round bore (a barrel jack,
/// an LED). It is placed in the wall's own 2D frame: <see cref="CenterAlong"/> is the position
/// ALONG the wall (the interior x on a Y-wall, the interior y on an X-wall) and
/// <see cref="CenterZ"/> the height above the interior floor.
///
/// <para>The cutout optionally names the connector it serves (<see cref="For"/>). A connector
/// DECLARED panel-mount (<see cref="Enclosure.AddPanelConnector"/>) is checked against the
/// cutout whose <see cref="For"/> matches it: its body must reach the wall and its cross-section
/// must fit through the opening, or <see cref="EnclosureFit"/> names it (the offset, the reach
/// deficit). A cutout with no <see cref="For"/> is just an opening — it is subtracted from the
/// wall but never demands a connector.</para>
/// </summary>
public sealed class PanelCutout
{
    private PanelCutout(string name, PanelWall wall, double centerAlong, double centerZ,
        double halfAlong, double halfZ, bool round, string? forReference)
    {
        Name = name ?? "";
        Wall = wall;
        CenterAlong = centerAlong;
        CenterZ = centerZ;
        HalfAlong = halfAlong;
        HalfZ = halfZ;
        IsRound = round;
        For = forReference;
    }

    /// <summary>A rectangular opening (<paramref name="width"/> along the wall,
    /// <paramref name="height"/> in z), centred at (<paramref name="centerAlong"/>,
    /// <paramref name="centerZ"/>) in the wall's 2D frame.</summary>
    public static PanelCutout Rectangular(string name, PanelWall wall,
        double centerAlong, double centerZ, double width, double height, string? forReference = null)
    {
        if (!(width > 0) || !(height > 0))
            throw new ArgumentException(
                $"Cutout '{name}' needs positive dimensions (got {width:g6} x {height:g6}).");
        return new PanelCutout(name, wall, centerAlong, centerZ, width / 2, height / 2, round: false,
            forReference);
    }

    /// <summary>A round opening of <paramref name="diameter"/>, centred at
    /// (<paramref name="centerAlong"/>, <paramref name="centerZ"/>) in the wall's 2D frame.</summary>
    public static PanelCutout Round(string name, PanelWall wall,
        double centerAlong, double centerZ, double diameter, string? forReference = null)
    {
        if (!(diameter > 0))
            throw new ArgumentException($"Cutout '{name}' needs a positive diameter (got {diameter:g6}).");
        return new PanelCutout(name, wall, centerAlong, centerZ, diameter / 2, diameter / 2, round: true,
            forReference);
    }

    /// <summary>The cutout's name.</summary>
    public string Name { get; }

    /// <summary>Which side wall it is cut into.</summary>
    public PanelWall Wall { get; }

    /// <summary>The centre ALONG the wall (interior x on a Y-wall, interior y on an X-wall).</summary>
    public double CenterAlong { get; }

    /// <summary>The centre height above the interior floor (interior z).</summary>
    public double CenterZ { get; }

    /// <summary>Half the opening's extent along the wall (the radius for a round cutout).</summary>
    public double HalfAlong { get; }

    /// <summary>Half the opening's extent in z (the radius for a round cutout).</summary>
    public double HalfZ { get; }

    /// <summary>Whether the opening is round (else rectangular).</summary>
    public bool IsRound { get; }

    /// <summary>The reference designator of the connector this cutout serves, or null for a bare
    /// opening.</summary>
    public string? For { get; }
}

/// <summary>An interior keep-out volume the board and its parts must avoid — a mounting boss, a
/// battery holder, a lid rib — declared as a <see cref="Shape"/> in the enclosure's interior
/// coordinates (interior floor at z = 0, cavity centred on the interior origin). A component
/// whose body intersects it is named by <see cref="EnclosureFit"/>.</summary>
/// <param name="Name">The keep-out's name.</param>
/// <param name="Volume">The forbidden solid, in interior coordinates.</param>
public readonly record struct EnclosureKeepOut(string Name, Shape Volume);

/// <summary>
/// A box housing a <see cref="PcbLayout"/> is placed into — the MCAD/ECAD boundary. It is a
/// rectangular interior <b>cavity</b> (width × depth × height) with a stated <b>wall</b> and
/// <b>floor</b> thickness, a board <b>seating height</b> (the standoffs the board mounts on
/// above the interior floor), a <b>lid</b> at a stated height (the headroom ceiling), named
/// <b>panel cutouts</b> for protruding connectors and interior <b>keep-out</b> volumes.
///
/// <para><b>It is built from the existing <see cref="Shape"/> API, not a new solid type</b>
/// (<see cref="Housing"/> is a shelled box with the cutouts drilled; <see cref="Lid"/> is a
/// plate), so it is an exact B-Rep whose fit against the board is checked with the SAME clash
/// machinery a mechanism sweep uses (<see cref="MeshIntersection.Crosses"/> — transversal only,
/// so a seated part is not a clash).</para>
///
/// <para><b>Interior coordinate frame.</b> The cavity is centred on the interior origin and its
/// floor is at z = 0: x ∈ [−W/2, W/2], y ∈ [−D/2, D/2], z ∈ [0, <see cref="InteriorHeight"/>].
/// <see cref="Frame"/> places that interior frame in world (default world XY, so the interior
/// floor is the world z = 0 plane). <see cref="SeatFrame"/> is where the board mounts — pass it
/// as the layout's <c>boardFrame</c> so the board seats in the cavity and nothing drifts.</para>
/// </summary>
public sealed class Enclosure
{
    private readonly List<PanelCutout> _cutouts = [];
    private readonly List<EnclosureKeepOut> _keepOuts = [];
    private readonly List<string> _panelConnectors = [];

    /// <summary>Builds a box enclosure.</summary>
    /// <param name="interiorWidth">Interior cavity width (interior x), positive.</param>
    /// <param name="interiorDepth">Interior cavity depth (interior y), positive.</param>
    /// <param name="interiorHeight">Interior cavity height (interior z), positive.</param>
    /// <param name="wallThickness">Side-wall thickness, positive.</param>
    /// <param name="boardSeatZ">Standoff height — the interior z the board's underside seats at
    /// (0 ≤ seat &lt; height).</param>
    /// <param name="lidZ">The lid's underside height — the headroom ceiling — or null for the
    /// interior height (a lid resting on the walls). Must lie above the board seat.</param>
    /// <param name="floorThickness">Floor thickness, or null for <paramref name="wallThickness"/>.</param>
    /// <param name="lidThickness">Lid thickness, or null for <paramref name="wallThickness"/>.</param>
    /// <param name="frame">Where the interior frame sits in world (default world XY).</param>
    public Enclosure(
        double interiorWidth, double interiorDepth, double interiorHeight,
        double wallThickness, double boardSeatZ, double? lidZ = null,
        double? floorThickness = null, double? lidThickness = null, Frame3d? frame = null)
    {
        if (!(interiorWidth > 0) || !(interiorDepth > 0) || !(interiorHeight > 0))
            throw new ArgumentException(
                $"An enclosure needs a positive interior (got {interiorWidth:g6} x {interiorDepth:g6} "
                + $"x {interiorHeight:g6}).");
        if (!(wallThickness > 0))
            throw new ArgumentException($"Wall thickness must be positive (got {wallThickness:g6}).");
        if (boardSeatZ < 0 || boardSeatZ >= interiorHeight)
            throw new ArgumentException(
                $"The board seat {boardSeatZ:g6} must lie in [0, interior height {interiorHeight:g6}).");
        double lid = lidZ ?? interiorHeight;
        if (lid <= boardSeatZ)
            throw new ArgumentException(
                $"The lid height {lid:g6} must lie above the board seat {boardSeatZ:g6}.");

        InteriorWidth = interiorWidth;
        InteriorDepth = interiorDepth;
        InteriorHeight = interiorHeight;
        WallThickness = wallThickness;
        FloorThickness = floorThickness ?? wallThickness;
        LidThickness = lidThickness ?? wallThickness;
        if (!(FloorThickness > 0) || !(LidThickness > 0))
            throw new ArgumentException("Floor and lid thickness must be positive.");
        BoardSeatZ = boardSeatZ;
        LidZ = lid;
        Frame = frame ?? Frame3d.WorldXY;
    }

    /// <summary>Interior cavity width (interior x).</summary>
    public double InteriorWidth { get; }

    /// <summary>Interior cavity depth (interior y).</summary>
    public double InteriorDepth { get; }

    /// <summary>Interior cavity height (interior z).</summary>
    public double InteriorHeight { get; }

    /// <summary>Side-wall thickness.</summary>
    public double WallThickness { get; }

    /// <summary>Floor thickness (below the interior z = 0 plane).</summary>
    public double FloorThickness { get; }

    /// <summary>Lid thickness (above <see cref="LidZ"/>).</summary>
    public double LidThickness { get; }

    /// <summary>The interior z the board's underside seats at (the standoff height).</summary>
    public double BoardSeatZ { get; }

    /// <summary>The lid's underside height — the headroom ceiling.</summary>
    public double LidZ { get; }

    /// <summary>Where the interior frame sits in world (default world XY).</summary>
    public Frame3d Frame { get; }

    /// <summary>The panel cutouts, in declaration order.</summary>
    public IReadOnlyList<PanelCutout> Cutouts => _cutouts;

    /// <summary>The interior keep-out volumes, in declaration order.</summary>
    public IReadOnlyList<EnclosureKeepOut> KeepOuts => _keepOuts;

    /// <summary>The reference designators declared panel-mount (each must protrude through a
    /// cutout serving it).</summary>
    public IReadOnlyList<string> PanelConnectors => _panelConnectors;

    /// <summary>Adds a panel cutout (subtracted from its wall; checked against its
    /// <see cref="PanelCutout.For"/> connector if any).</summary>
    public Enclosure AddCutout(PanelCutout cutout)
    {
        ArgumentNullException.ThrowIfNull(cutout);
        _cutouts.Add(cutout);
        return this;
    }

    /// <summary>Adds an interior keep-out volume (a <see cref="Shape"/> in interior coordinates).</summary>
    public Enclosure AddKeepOut(string name, Shape volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        _keepOuts.Add(new EnclosureKeepOut(name ?? "", volume));
        return this;
    }

    /// <summary>Declares a component panel-mount: it must protrude through the cutout whose
    /// <see cref="PanelCutout.For"/> names it, or <see cref="EnclosureFit"/> reports it has no
    /// aligned cutout.</summary>
    public Enclosure AddPanelConnector(string reference)
    {
        if (string.IsNullOrEmpty(reference))
            throw new ArgumentException("A panel connector needs a reference designator.", nameof(reference));
        if (!_panelConnectors.Contains(reference, StringComparer.Ordinal))
            _panelConnectors.Add(reference);
        return this;
    }

    /// <summary>The board's mounting frame — a seat at <see cref="BoardSeatZ"/> centred in the
    /// cavity, in world. Pass it as a <see cref="PcbLayout"/>'s <c>boardFrame</c> so the board
    /// seats in the enclosure and the fit check reads one geometry, not two hand-kept poses.</summary>
    public Frame3d SeatFrame() =>
        Frame3d.FromOrthonormal(new Vector3d(0, 0, BoardSeatZ), Vector3d.UnitX, Vector3d.UnitY).Then(Frame);

    // ---- geometry (interior coordinates) ------------------------------------

    /// <summary>The interior cavity as a box (interior coordinates), the containment volume the
    /// board must lie within.</summary>
    public Aabb Cavity() => new(
        new Vector3d(-InteriorWidth / 2, -InteriorDepth / 2, 0),
        new Vector3d(InteriorWidth / 2, InteriorDepth / 2, InteriorHeight));

    /// <summary>The housing — walls and floor (open top), with the panel cutouts drilled — as an
    /// exact B-Rep <see cref="Shape"/> in interior coordinates.</summary>
    public Shape Housing()
    {
        double w = InteriorWidth, d = InteriorDepth;
        // Outer prism: cavity grown by the wall thickness, from the floor's underside up to the
        // cavity top (open top — the lid is a separate part).
        var outer = Shape.Box(new Aabb(
            new Vector3d(-w / 2 - WallThickness, -d / 2 - WallThickness, -FloorThickness),
            new Vector3d(w / 2 + WallThickness, d / 2 + WallThickness, InteriorHeight)));
        // The cavity, punched open at the top so nothing caps it.
        var cavity = Shape.Box(new Aabb(
            new Vector3d(-w / 2, -d / 2, 0),
            new Vector3d(w / 2, d / 2, InteriorHeight + WallThickness)));
        var housing = outer.Subtract(cavity);
        foreach (var cutout in _cutouts)
            housing = housing.Subtract(CutoutTool(cutout));
        return housing;
    }

    /// <summary>The lid — a plate from <see cref="LidZ"/> to <c>LidZ + <see cref="LidThickness"/></c>
    /// spanning the outer footprint — as a <see cref="Shape"/> in interior coordinates. Its
    /// underside is the headroom ceiling the fit check measures against.</summary>
    public Shape Lid() => Shape.Box(new Aabb(
        new Vector3d(-InteriorWidth / 2 - WallThickness, -InteriorDepth / 2 - WallThickness, LidZ),
        new Vector3d(InteriorWidth / 2 + WallThickness, InteriorDepth / 2 + WallThickness,
            LidZ + LidThickness)));

    /// <summary>The tool that punches <paramref name="cutout"/> through its wall (interior
    /// coordinates). Spans the wall thickness with a small overshoot on both faces so a boolean
    /// never sees a coplanar face (the Drill overshoot doctrine).</summary>
    private Shape CutoutTool(PanelCutout cutout)
    {
        var (innerFace, outAxis, _, sign) = WallFrame(cutout.Wall);
        double over = WallThickness; // reach a full wall inside and outside — never coplanar
        double a = innerFace - sign * over;
        double b = innerFace + sign * (WallThickness + over);
        double lo = Math.Min(a, b), hi = Math.Max(a, b);

        if (cutout.IsRound)
        {
            // A cylinder along +Z, oriented so its axis is the wall's outward axis, centred on
            // the inner face at the cutout's 2D position.
            double length = hi - lo;
            var tool = Shape.Cylinder(cutout.HalfAlong, length);
            var orient = outAxis == 0 ? Matrix4d.CreateRotationY(Math.PI / 2)
                                      : Matrix4d.CreateRotationX(-Math.PI / 2);
            var center = InteriorPoint(outAxis, (lo + hi) / 2, cutout.CenterAlong, cutout.CenterZ);
            return tool.Transform(Matrix4d.CreateTranslation(center) * orient);
        }

        var min = InteriorPoint(outAxis, lo,
            cutout.CenterAlong - cutout.HalfAlong, cutout.CenterZ - cutout.HalfZ);
        var max = InteriorPoint(outAxis, hi,
            cutout.CenterAlong + cutout.HalfAlong, cutout.CenterZ + cutout.HalfZ);
        return Shape.Box(new Aabb(Vector3d.Min(min, max), Vector3d.Max(min, max)));
    }

    /// <summary>Builds an interior point from a value on the wall's outward axis (the other of
    /// x/y taking the along value) and a z.</summary>
    private static Vector3d InteriorPoint(int outAxis, double outValue, double alongValue, double z)
    {
        double x = outAxis == 0 ? outValue : alongValue;
        double y = outAxis == 0 ? alongValue : outValue;
        return new Vector3d(x, y, z);
    }

    /// <summary>The inner-face coordinate, the outward axis (0 = x, 1 = y), the along axis, and
    /// the outward sign of a wall.</summary>
    internal (double InnerFace, int OutAxis, int AlongAxis, double Sign) WallFrame(PanelWall wall) => wall switch
    {
        PanelWall.MinX => (-InteriorWidth / 2, 0, 1, -1.0),
        PanelWall.MaxX => (InteriorWidth / 2, 0, 1, 1.0),
        PanelWall.MinY => (-InteriorDepth / 2, 1, 0, -1.0),
        PanelWall.MaxY => (InteriorDepth / 2, 1, 0, 1.0),
        _ => throw new ArgumentOutOfRangeException(nameof(wall)),
    };

    // ---- convenience --------------------------------------------------------

    /// <summary>Runs the fit check of this enclosure against a placed layout — see
    /// <see cref="EnclosureFit.Check"/>.</summary>
    public FitReport Fit(PcbLayout layout, MeshQuality? quality = null) =>
        EnclosureFit.Check(this, layout, quality);

    /// <summary>The smallest box enclosure that fits a placed layout <b>in place</b> — the cavity
    /// is the layout's world footprint grown by <paramref name="clearance"/> on all sides, the
    /// board's underside seats on <paramref name="standoff"/> above the floor, and the lid clears
    /// the tallest part by <paramref name="headroom"/>. The enclosure's <see cref="Frame"/> is
    /// placed so the layout, unmoved, sits centred in the cavity — so <c>SmallestFor(layout,
    /// …).Fit(layout)</c> is clean by construction. A bounded helper (it measures the assembly's
    /// mesh bounds once); this is a FIT CHECK module, not an enclosure generator, so it is a
    /// starting point a caller refines, not a finished housing. It assumes the board lies roughly
    /// in the world XY plane (the default and the seated case).</summary>
    public static Enclosure SmallestFor(PcbLayout layout, double clearance, double standoff,
        double headroom, double wallThickness, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!(clearance >= 0) || !(standoff >= 0) || !(headroom > 0))
            throw new ArgumentException("Clearance/standoff must be non-negative and headroom positive.");
        var box = Aabb.Empty;
        foreach (var instance in layout.ToAssembly().Flatten())
            box = box.Union(instance.Bounds(quality));
        if (box.IsEmpty)
            throw new ArgumentException("The layout has no geometry to size an enclosure for.");

        // The board's underside is board-local z = 0; interior z = standoff lands there, so the
        // frame's z is chosen to map the board's world underside onto the seat.
        double boardUnderZ = layout.BoardFrame.ToWorld(Vector3d.Zero).Z;
        double partsHeight = Math.Max(box.Max.Z - boardUnderZ, box.Size.Z);
        double interiorHeight = standoff + partsHeight + headroom;
        double width = box.Size.X + 2 * clearance;
        double depth = box.Size.Y + 2 * clearance;
        var frame = Frame3d.FromOrthonormal(
            new Vector3d(box.Center.X, box.Center.Y, boardUnderZ - standoff),
            Vector3d.UnitX, Vector3d.UnitY);
        return new Enclosure(width, depth, interiorHeight, wallThickness,
            boardSeatZ: standoff, lidZ: interiorHeight, frame: frame);
    }
}

/// <summary>What kind of fit problem <see cref="EnclosureFit"/> found.</summary>
public enum FitIssue
{
    /// <summary>The board outline reaches past a side wall — it does not fit the cavity.</summary>
    BoardTooLarge,

    /// <summary>A component body interpenetrates the enclosure wall or floor.</summary>
    ComponentClashesWall,

    /// <summary>A component reaches past the lid's underside (too tall for the headroom).</summary>
    ComponentClashesLid,

    /// <summary>A component declared panel-mount has no cutout serving it, or is not placed.</summary>
    ConnectorNoCutout,

    /// <summary>A panel connector's cross-section does not fit through / is not centred in its
    /// cutout opening.</summary>
    ConnectorMisaligned,

    /// <summary>A panel connector's body does not reach the wall its cutout is in.</summary>
    ConnectorNotProtruding,

    /// <summary>A component body intersects an interior keep-out volume.</summary>
    KeepOutCollision,
}

/// <summary>
/// One fit problem — it NAMES its offender (<see cref="Subject"/>), reports the MEASURED value
/// against the REQUIRED one, and carries a human-readable <see cref="Message"/> (the
/// <c>DrcViolation</c> / <c>PcbLayoutCheck</c> house style — a report that only said "does not
/// fit" would be useless).
/// </summary>
/// <param name="Issue">The kind of problem.</param>
/// <param name="Subject">The offender — a reference designator, a wall name, a keep-out name.</param>
/// <param name="Message">A human-readable line.</param>
/// <param name="Measured">The value found (mm — an overhang, a top height, a centre offset).</param>
/// <param name="Required">The limit it should have met (mm).</param>
public readonly record struct FitProblem(
    FitIssue Issue, string Subject, string Message, double Measured, double Required)
{
    /// <summary>The problem as one line, prefixed by its issue and subject.</summary>
    public override string ToString() => $"{Issue} [{Subject}]: {Message}";
}

/// <summary>
/// The result of <see cref="EnclosureFit.Check"/> — whether a placed <see cref="PcbLayout"/>
/// fits an <see cref="Enclosure"/>, naming every problem. <see cref="Headroom"/> is always
/// reported (the tallest component's top vs the lid underside: positive clears, negative
/// collides), so a design has the one number it wants even when the fit is clean.
/// </summary>
/// <param name="Problems">Every fit problem, each naming and measuring its offender.</param>
/// <param name="Headroom">The lid underside less the tallest component's top (mm); positive =
/// clears, negative = a collision (and the offending part is also in <see cref="Problems"/>).</param>
/// <param name="TallestComponent">The reference designator of the tallest part, or null when the
/// layout has no placed bodies.</param>
public sealed record FitReport(
    IReadOnlyList<FitProblem> Problems, double Headroom, string? TallestComponent)
{
    /// <summary>True when nothing about the fit needs a second look.</summary>
    public bool Ok => Problems.Count == 0;

    /// <summary>The problems of one kind.</summary>
    public IEnumerable<FitProblem> OfIssue(FitIssue issue) => Problems.Where(p => p.Issue == issue);

    /// <summary>Whether any problem of a given kind was found.</summary>
    public bool Has(FitIssue issue) => Problems.Any(p => p.Issue == issue);

    /// <summary>A human-readable report — one line per problem, then a headroom summary.</summary>
    public override string ToString()
    {
        string head = TallestComponent is null
            ? $"headroom {Headroom:g4} mm (no placed bodies)"
            : $"headroom {Headroom:g4} mm (tallest {TallestComponent})";
        if (Ok)
            return $"fit OK — {head}";
        var lines = Problems.Select(p => p.ToString()).Append($"({Problems.Count} problem(s); {head})");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// The MCAD/ECAD fit check — does a placed <see cref="PcbLayout"/> fit an <see cref="Enclosure"/>?
/// It reuses the SAME clash machinery a mechanism sweep uses (<see cref="MeshIntersection.Crosses"/>
/// over an instance-bounds broad phase, transversal only — so a seated part resting flush is NOT a
/// clash), and reports closed-form numbers where the geometry is a plane or a rectangle (the board
/// outline against the cavity walls, a part's top against the lid).
///
/// <para><b>Everything is measured in the enclosure's interior frame.</b> The layout is flattened
/// to world <see cref="PartInstance"/>s and each body is pulled back into interior coordinates
/// (<see cref="Enclosure.Frame"/><c>.Inverse()</c>), so the board's own <see cref="PcbLayout.BoardFrame"/>
/// and the enclosure's placement need not be the same and nothing is assumed about where either
/// sits.</para>
/// </summary>
public static class EnclosureFit
{
    /// <summary>Checks a placed layout against an enclosure.</summary>
    /// <param name="enclosure">The housing.</param>
    /// <param name="layout">The placed board layout.</param>
    /// <param name="quality">Mesh quality for the clash tests (null = the parts' cached display
    /// meshes).</param>
    public static FitReport Check(Enclosure enclosure, PcbLayout layout, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(enclosure);
        ArgumentNullException.ThrowIfNull(layout);

        var problems = new List<FitProblem>();
        double scale = Math.Max(Math.Max(enclosure.InteriorWidth, enclosure.InteriorDepth),
            enclosure.InteriorHeight);
        double eps = 1e-9 * Math.Max(scale, 1);

        // Pull every placed body back into the enclosure's interior frame.
        var toInterior = enclosure.Frame.Inverse().ToMatrix();
        var instances = layout.ToAssembly().Flatten();
        var bodies = new List<Body>();
        foreach (var instance in instances)
        {
            string reference = LastSegment(instance.Path);
            if (reference == "board")
                continue; // the board's containment is the closed-form outline test below
            var mesh = instance.Part.GetMesh(quality).Transformed(toInterior * instance.World);
            bodies.Add(new Body(reference, mesh, mesh.ComputeBounds()));
        }

        CheckBoardFits(enclosure, layout, eps, problems);
        CheckWallClashes(enclosure, bodies, eps, quality, problems);
        double headroom = CheckLidAndHeadroom(enclosure, bodies, quality, out string? tallest, problems);
        CheckConnectors(enclosure, bodies, eps, problems);
        CheckKeepOuts(enclosure, bodies, quality, problems);

        problems.Sort((a, b) =>
        {
            int byIssue = a.Issue.CompareTo(b.Issue);
            if (byIssue != 0)
                return byIssue;
            int bySubject = string.CompareOrdinal(a.Subject, b.Subject);
            return bySubject != 0 ? bySubject : string.CompareOrdinal(a.Message, b.Message);
        });
        return new FitReport(problems, headroom, tallest);
    }

    /// <summary>One placed body pulled into interior coordinates.</summary>
    private readonly record struct Body(string Reference, HalfEdgeMesh Mesh, Aabb Bounds);

    // 1. The board outline must lie within the cavity's four side walls (closed form; a too-large
    // board NAMES the wall it reaches past and the overhang).
    private static void CheckBoardFits(Enclosure enclosure, PcbLayout layout, double eps,
        List<FitProblem> problems)
    {
        double halfW = enclosure.InteriorWidth / 2, halfD = enclosure.InteriorDepth / 2;
        double overMinX = 0, overMaxX = 0, overMinY = 0, overMaxY = 0;
        foreach (var p in layout.Board.OutlinePoints)
        {
            foreach (double z in new[] { 0.0, layout.Board.Thickness })
            {
                var world = layout.BoardFrame.ToWorld(new Vector3d(p.X, p.Y, z));
                var q = enclosure.Frame.ToLocal(world);
                overMaxX = Math.Max(overMaxX, q.X - halfW);
                overMinX = Math.Max(overMinX, -halfW - q.X);
                overMaxY = Math.Max(overMaxY, q.Y - halfD);
                overMinY = Math.Max(overMinY, -halfD - q.Y);
            }
        }
        Report(PanelWall.MaxX, overMaxX, halfW);
        Report(PanelWall.MinX, overMinX, halfW);
        Report(PanelWall.MaxY, overMaxY, halfD);
        Report(PanelWall.MinY, overMinY, halfD);

        void Report(PanelWall wall, double over, double half)
        {
            if (over <= eps)
                return;
            problems.Add(new FitProblem(FitIssue.BoardTooLarge, WallName(wall),
                $"board outline reaches {over:g4} mm past the {WallName(wall)} wall",
                Measured: half + over, Required: half));
        }
    }

    // 2. A component (other than a declared panel connector, which is EXPECTED to pass through a
    // wall) must not interpenetrate the housing — Bvh-broad-phase (bounds) then the transversal
    // MeshIntersection.Crosses narrow phase, so a part resting flush is not a clash.
    private static void CheckWallClashes(Enclosure enclosure, List<Body> bodies, double eps,
        MeshQuality? quality, List<FitProblem> problems)
    {
        var housing = enclosure.Housing().ToMesh(quality);
        var housingBounds = housing.ComputeBounds();
        var cavity = enclosure.Cavity();
        foreach (var body in bodies)
        {
            if (enclosure.PanelConnectors.Contains(body.Reference, StringComparer.Ordinal))
                continue;
            if (!body.Bounds.Intersects(housingBounds))
                continue;
            if (!MeshIntersection.Crosses(body.Mesh, housing))
                continue;
            double penetration = SideOverhang(body.Bounds, cavity);
            problems.Add(new FitProblem(FitIssue.ComponentClashesWall, body.Reference,
                $"body interpenetrates the enclosure wall/floor (about {Math.Max(penetration, 0):g4} mm)",
                Measured: Math.Max(penetration, 0), Required: 0));
        }
    }

    // 3. The tallest part vs the lid underside (closed-form headroom); a part actually crossing the
    // lid plate is a collision NAMED with its exact deficit (top − lid height).
    private static double CheckLidAndHeadroom(Enclosure enclosure, List<Body> bodies,
        MeshQuality? quality, out string? tallest, List<FitProblem> problems)
    {
        tallest = null;
        double tallestTop = double.NegativeInfinity;
        foreach (var body in bodies)
        {
            if (body.Bounds.Max.Z > tallestTop)
            {
                tallestTop = body.Bounds.Max.Z;
                tallest = body.Reference;
            }
        }
        double headroom = tallest is null ? enclosure.LidZ : enclosure.LidZ - tallestTop;

        var lid = enclosure.Lid().ToMesh(quality);
        var lidBounds = lid.ComputeBounds();
        foreach (var body in bodies)
        {
            if (!body.Bounds.Intersects(lidBounds))
                continue;
            if (!MeshIntersection.Crosses(body.Mesh, lid))
                continue;
            double deficit = body.Bounds.Max.Z - enclosure.LidZ;
            problems.Add(new FitProblem(FitIssue.ComponentClashesLid, body.Reference,
                $"top {body.Bounds.Max.Z:g4} exceeds the lid underside {enclosure.LidZ:g4} by {deficit:g4} mm",
                Measured: body.Bounds.Max.Z, Required: enclosure.LidZ));
        }
        return headroom;
    }

    // 4. Each declared panel connector must reach its cutout's wall AND fit through the opening
    // (centre offset + cross-section within the opening), or be NAMED.
    private static void CheckConnectors(Enclosure enclosure, List<Body> bodies, double eps,
        List<FitProblem> problems)
    {
        foreach (string reference in enclosure.PanelConnectors)
        {
            var cutout = enclosure.Cutouts.FirstOrDefault(c =>
                string.Equals(c.For, reference, StringComparison.Ordinal));
            if (cutout is null)
            {
                problems.Add(new FitProblem(FitIssue.ConnectorNoCutout, reference,
                    $"declared panel-mount but no cutout serves it", Measured: 0, Required: 0));
                continue;
            }

            Body? found = null;
            foreach (var b in bodies)
            {
                if (b.Reference == reference)
                {
                    found = b;
                    break;
                }
            }
            if (found is not { } body)
            {
                problems.Add(new FitProblem(FitIssue.ConnectorNotProtruding, reference,
                    "has no placed 3D body to reach the panel", Measured: 0, Required: 0));
                continue;
            }

            var (innerFace, outAxis, alongAxis, sign) = enclosure.WallFrame(cutout.Wall);
            double innerHalf = sign * innerFace; // the wall's half-extent (positive on either wall)
            // How far the body reaches toward the wall's outward face.
            double reach = sign > 0 ? Axis(body.Bounds.Max, outAxis) : -Axis(body.Bounds.Min, outAxis);
            double reachDeficit = innerHalf - reach;
            if (reachDeficit > eps)
            {
                problems.Add(new FitProblem(FitIssue.ConnectorNotProtruding, reference,
                    $"reaches only {reach:g4} of the {innerHalf:g4} wall face — short by {reachDeficit:g4} mm "
                    + $"of the {cutout.Name} cutout",
                    Measured: reach, Required: innerHalf));
            }

            double alongCentre = Axis(body.Bounds.Center, alongAxis);
            double dAlong = alongCentre - cutout.CenterAlong;
            double dZ = body.Bounds.Center.Z - cutout.CenterZ;
            double offset = Math.Sqrt(dAlong * dAlong + dZ * dZ);
            double connHalfAlong = 0.5 * Axis(body.Bounds.Size, alongAxis);
            double connHalfZ = 0.5 * body.Bounds.Size.Z;
            double clearAlong = cutout.HalfAlong - (Math.Abs(dAlong) + connHalfAlong);
            double clearZ = cutout.HalfZ - (Math.Abs(dZ) + connHalfZ);
            double permitted = Math.Min(cutout.HalfAlong - connHalfAlong, cutout.HalfZ - connHalfZ);
            if (Math.Min(clearAlong, clearZ) < -eps)
            {
                string why = permitted < 0
                    ? "cross-section is larger than the opening"
                    : $"centre offset {offset:g4} exceeds the {Math.Max(permitted, 0):g4} mm the opening allows";
                problems.Add(new FitProblem(FitIssue.ConnectorMisaligned, reference,
                    $"does not fit through the {cutout.Name} cutout — {why}",
                    Measured: offset, Required: Math.Max(permitted, 0)));
            }
        }
    }

    // 5. A component body intersecting an interior keep-out volume — surface crossing OR full
    // containment (a small part wholly inside the keep-out, or vice versa).
    private static void CheckKeepOuts(Enclosure enclosure, List<Body> bodies, MeshQuality? quality,
        List<FitProblem> problems)
    {
        foreach (var keepOut in enclosure.KeepOuts)
        {
            var koMesh = keepOut.Volume.ToMesh(quality);
            var koBounds = koMesh.ComputeBounds();
            MeshWindingNumber? koWinding = null;
            foreach (var body in bodies)
            {
                if (!body.Bounds.Intersects(koBounds))
                    continue;
                bool collide = MeshIntersection.Crosses(body.Mesh, koMesh);
                if (!collide)
                {
                    koWinding ??= new MeshWindingNumber(koMesh);
                    // Full containment either way: the part inside the keep-out, or the keep-out
                    // inside the part (a surface-crossing test alone is blind to both).
                    collide = koWinding.IsInside(body.Bounds.Center)
                        || new MeshWindingNumber(body.Mesh).IsInside(koBounds.Center);
                }
                if (!collide)
                    continue;
                problems.Add(new FitProblem(FitIssue.KeepOutCollision, body.Reference,
                    $"body intersects keep-out '{keepOut.Name}'", Measured: 0, Required: 0));
            }
        }
    }

    private static double SideOverhang(in Aabb bounds, in Aabb cavity) => Math.Max(
        Math.Max(bounds.Max.X - cavity.Max.X, cavity.Min.X - bounds.Min.X),
        Math.Max(Math.Max(bounds.Max.Y - cavity.Max.Y, cavity.Min.Y - bounds.Min.Y),
            cavity.Min.Z - bounds.Min.Z));

    private static double Axis(in Vector3d v, int axis) => axis == 0 ? v.X : v.Y;

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string WallName(PanelWall wall) => wall switch
    {
        PanelWall.MinX => "−X",
        PanelWall.MaxX => "+X",
        PanelWall.MinY => "−Y",
        PanelWall.MaxY => "+Y",
        _ => wall.ToString(),
    };
}
