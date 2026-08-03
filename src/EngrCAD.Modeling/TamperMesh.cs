using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>
/// An anti-drill **tamper-detection mesh**: a conductive serpentine laid over an enclosure
/// wall and wired into a continuity monitor, so that drilling through the wall to reach the
/// protected circuit severs a net and raises a tamper event. The defensive construction HSMs,
/// payment terminals and secure set-top boxes use.
///
/// <para>("Mesh" is the security-hardware term. Nothing here is a
/// <c>HalfEdgeMesh</c> — the deliverable is 2D line work you extrude as copper, cut into the
/// wall, or export to DXF for a flex circuit.)</para>
///
/// <para><b>The deliverable is a GUARANTEE, not a pattern.</b> Anyone can draw a squiggle.
/// What makes this engineering is <see cref="DrillGuarantee"/>: the largest drill that can
/// reach the inside without touching copper, and the smallest that must cut a net wherever it
/// lands. Both are DERIVED in closed form and then MEASURED on the geometry actually built —
/// see <see cref="TamperMeshLayout.Guarantee"/>.</para>
///
/// <para><b>The derivation.</b> Every cell of the lattice has the route running through its
/// centre, so no point of the footprint is further from the route than a cell's circumradius
/// <c>½·hypot(pitchX, pitchY)</c> — and that bound is ATTAINED, at the footprint's own four
/// corners always and at interior "blind" corners (a 2×2 block of cells no two of which are
/// consecutive on the route) from block order 3 upward. Writing <c>R</c> for that worst gap
/// and <c>w</c> for the trace width, a drill of diameter <c>d</c> centred at <c>c</c> misses
/// the copper exactly when <c>dist(c, route) &gt; d/2 + w/2</c>, so:</para>
/// <list type="bullet">
/// <item><b>Touch</b> — the largest drill that CAN pass is <c>2R − w</c>; anything at or above
/// that must hit copper somewhere.</item>
/// <item><b>Sever</b> — touching is not cutting. A drill only certainly cuts a net when its
/// disc spans the trace's full width at some point of the centreline, which needs
/// <c>d ≥ 2R + w</c>.</item>
/// </list>
/// <para>Between the two it is position-dependent, so the honest answer is a BAND, and the
/// design equation is the sever end: <c>pitch ≤ (d − w)/√2</c> (see
/// <see cref="PitchForDrill"/>). At a square cell <c>2R = √2·pitch</c>, giving the familiar
/// <c>√2·h − w</c> and <c>√2·h + w</c>.</para>
///
/// <para><b>Hilbert, and its locality is a LIABILITY here — stated, not sold.</b> The curve is
/// open, so it has two ends, which is what a continuity monitor needs (Moore's closed loop
/// would have to be cut); both ends sit on the footprint's outer boundary, where a connector
/// wants them. What Hilbert buys over a plain serpentine is ISOTROPY — its longest straight
/// run is bounded, so the free space between passes has no long straight channel a slot or a
/// saw can run down. What it COSTS is that points near in space are near in path order, so an
/// attacker who exposes a small window can bridge across a break with a short wire. That is
/// not fixed by the curve, and pretending otherwise would be the dishonest part: the
/// countermeasure is <b>two or more interleaved nets</b> monitored for continuity AND for
/// mutual isolation, so any conductive bridge wider than
/// <see cref="TamperMeshLayout.IsolationGap"/> shorts them together.</para>
///
/// <para><b>Scope, v1.</b> A PLANAR rectangular footprint, placed on a face with
/// <c>SketchPlane.On(face)</c>. Conformal placement on a doubly-curved wall is refused by
/// name rather than approximated: it is the still-open surface-decoration consumer, and
/// <c>MeshLocalParam</c>'s exp map carries 2–5% distortion, which would land directly in the
/// pitch and therefore in the guarantee.</para>
/// </summary>
public static class TamperMesh
{
    /// <summary>Largest number of lattice cells a layout may carry before
    /// <see cref="Over(Sketch, double, double, int, int, double?, int)"/> refuses.</summary>
    public const int DefaultMaxCells = 1 << 18;

    /// <summary>The block order used when the caller states none: the smallest at which the
    /// Hilbert pattern's isotropy is already saturated (its longest straight run is 3 cells at
    /// order 2 and at every order above), while a 4-cell fit granularity keeps the cells nearly
    /// square on an arbitrary rectangle. Raise it for a more isotropic route at a coarser fit;
    /// order 0 degenerates the tiling to a plain boustrophedon serpentine.</summary>
    public const int DefaultBlockOrder = 2;

    /// <summary>
    /// The pitch that defeats a drill of <paramref name="drillDiameter"/> — the design
    /// equation <c>pitch ≤ (d − w)/√2</c>, i.e. the SEVER end of the band, so a drill of that
    /// diameter must cut a net wherever it lands rather than merely graze one.
    /// </summary>
    /// <param name="drillDiameter">The drill the mesh must defeat.</param>
    /// <param name="traceWidth">The conductor width.</param>
    /// <exception cref="ArgumentOutOfRangeException">When the drill is no wider than the
    /// trace: the equation then has no positive solution, because a drill narrower than the
    /// conductor can pass through it without removing a full cross-section.</exception>
    public static double PitchForDrill(double drillDiameter, double traceWidth)
    {
        if (!(traceWidth > 0) || !double.IsFinite(traceWidth))
            throw new ArgumentOutOfRangeException(nameof(traceWidth), "The trace width must be positive and finite.");
        if (!(drillDiameter > traceWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(drillDiameter),
                $"A drill of diameter {drillDiameter} cannot be guaranteed to sever a trace "
                + $"{traceWidth} wide: the design equation pitch <= (d - w)/sqrt(2) has no positive "
                + "solution at or below that width, because a drill no wider than the conductor can "
                + "pass through it without removing a full cross-section. Use a narrower trace.");
        }
        return (drillDiameter - traceWidth) / Math.Sqrt(2);
    }

    /// <summary>
    /// Lays a tamper mesh over <paramref name="face"/>'s bounding rectangle.
    /// </summary>
    /// <param name="face">The wall outline. Its bounding rectangle is the footprint, and the
    /// face must CONTAIN that rectangle (eroded by <paramref name="clearance"/>) — see the
    /// refusals on <see cref="TamperMesh"/>.</param>
    /// <param name="pitch">The largest acceptable distance between neighbouring passes. The
    /// achieved pitch is reported per axis and is never coarser.</param>
    /// <param name="traceWidth">The conductor width.</param>
    /// <param name="nets">How many independent interleaved nets. 1 is the bare route; 2 or
    /// more spread evenly across each corridor, which is what makes a bridging attack risk
    /// shorting them.</param>
    /// <param name="blockOrder">The Hilbert block order — see <see cref="DefaultBlockOrder"/>.</param>
    /// <param name="clearance">How far inside the face outline the footprint must sit. Null
    /// takes one trace width.</param>
    /// <param name="maxCells">Refusal cap on the lattice size.</param>
    public static TamperMeshLayout Over(
        Sketch face, double pitch, double traceWidth,
        int nets = 2, int blockOrder = DefaultBlockOrder,
        double? clearance = null, int maxCells = DefaultMaxCells)
    {
        ArgumentNullException.ThrowIfNull(face);
        double keepOut = clearance ?? traceWidth;
        if (!(keepOut >= 0) || !double.IsFinite(keepOut))
            throw new ArgumentOutOfRangeException(nameof(clearance), "The wall clearance must be zero or positive.");

        // Region2d.FromLoops runs the simplicity guard, so a self-crossing outline is named
        // here with its crossing segments rather than silently filled by some fill rule.
        var polygons = face.ToRegions();
        if (polygons.Count == 0)
            throw new ArgumentException("The tamper-mesh face encloses no area.", nameof(face));

        var bounds = Aabb.Empty;
        foreach (var polygon in polygons)
            bounds = bounds.Union(polygon.Bounds);

        // The footprint is the face's bounding rectangle held back by the clearance — a mesh
        // is laid up to a stated keep-out from the wall's edge, never onto it.
        var footprint = new Aabb(
            new Vector3d(bounds.Min.X + keepOut, bounds.Min.Y + keepOut, 0),
            new Vector3d(bounds.Max.X - keepOut, bounds.Max.Y - keepOut, 0));
        if (!(footprint.Max.X > footprint.Min.X) || !(footprint.Max.Y > footprint.Min.Y))
        {
            throw new ArgumentException(
                $"A clearance of {keepOut} leaves nothing of this face's "
                + $"{bounds.Max.X - bounds.Min.X} by {bounds.Max.Y - bounds.Min.Y} bounding rectangle "
                + "to lay a mesh over.",
                nameof(face));
        }

        // And it must lie INSIDE the wall, or the mesh does not protect what it claims to. The
        // test is exact and polygonal: erode the face by the clearance and take the footprint
        // rectangle away from it, so a face that is not a rectangle is caught by the area it
        // fails to contain rather than by a shape heuristic.
        var room = keepOut > 0 ? Region2dOffset.Offset(polygons, -keepOut) : polygons;
        var rectangle = new Region2d(FootprintLoop(footprint));
        var escaped = room.Count == 0 ? [rectangle] : Region2dBoolean.Difference([rectangle], room);
        double escapedArea = escaped.Sum(r => r.Area);
        if (escapedArea > 1e-9 * rectangle.Area)
        {
            throw new ArgumentException(
                $"The mesh footprint ({footprint.Min.X}..{footprint.Max.X} by "
                + $"{footprint.Min.Y}..{footprint.Max.Y}) is not contained in this face at clearance "
                + $"{keepOut}: {escapedArea} of its area falls outside. A tamper mesh must cover the "
                + "whole wall it protects, so the face is required to contain its own bounding "
                + "rectangle — a non-rectangular wall, or one with a cutout, would break the route "
                + "into runs, and a broken net cannot be monitored for continuity. Fill a "
                + "non-rectangular outline with SpaceFillingInfill instead, which reports the runs "
                + "honestly.",
                nameof(face));
        }
        return Over(footprint, pitch, traceWidth, nets, blockOrder, maxCells);
    }

    /// <summary>
    /// Lays a tamper mesh over an explicit rectangular footprint — the form that states the
    /// protected area directly rather than deriving it from a face outline.
    /// </summary>
    public static TamperMeshLayout Over(
        in Aabb footprint, double pitch, double traceWidth,
        int nets = 2, int blockOrder = DefaultBlockOrder, int maxCells = DefaultMaxCells)
    {
        if (footprint.IsEmpty)
            throw new ArgumentException("A tamper mesh needs a non-empty footprint.", nameof(footprint));
        double width = footprint.Max.X - footprint.Min.X;
        double height = footprint.Max.Y - footprint.Min.Y;
        if (!(width > 0) || !(height > 0))
        {
            throw new ArgumentException(
                $"A tamper mesh needs a footprint with area; this one measures {width} by {height}.",
                nameof(footprint));
        }
        if (!(pitch > 0) || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch), "The mesh pitch must be positive and finite.");
        if (!(traceWidth > 0) || !double.IsFinite(traceWidth))
            throw new ArgumentOutOfRangeException(nameof(traceWidth), "The trace width must be positive and finite.");
        if (nets < 1)
            throw new ArgumentOutOfRangeException(nameof(nets), "A tamper mesh needs at least one net.");
        if (blockOrder < 0 || blockOrder > 12)
            throw new ArgumentOutOfRangeException(nameof(blockOrder), "The Hilbert block order must be between 0 and 12.");
        if (maxCells < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCells), "The cell cap must be at least 1.");

        int m = 1 << blockOrder;
        int blocksX = BlocksFor(width, m, pitch);
        int blocksY = BlocksFor(height, m, pitch);
        long cellsX = (long)blocksX * m;
        long cellsY = (long)blocksY * m;
        if (cellsX * cellsY > maxCells)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pitch),
                $"A tamper mesh at pitch {pitch} over {width} by {height} needs {cellsX} x {cellsY} "
                + $"= {cellsX * cellsY} cells at block order {blockOrder}, past the {maxCells}-cell cap. "
                + $"The FINEST pitch this cap allows here is about "
                + $"{Math.Sqrt(width * height / maxCells)}; anything coarser costs less.");
        }

        double pitchX = width / cellsX;
        double pitchY = height / cellsY;
        double gap = Math.Min(pitchX, pitchY) / nets;
        if (traceWidth >= gap)
        {
            throw new ArgumentException(
                $"A {traceWidth}-wide trace does not fit: {nets} net(s) spread across a pitch of "
                + $"{Math.Min(pitchX, pitchY)} leave {gap} between neighbouring conductors, so the "
                + "copper would merge. That is an electrical short, not a tolerance question — "
                + $"reduce the trace width below {gap}, reduce the net count, or ask for a coarser pitch.",
                nameof(traceWidth));
        }

        var route = TiledHilbertRoute.Build(blockOrder, blocksX, blocksY);
        var origin = new Vector2d(footprint.Min.X, footprint.Min.Y);

        var built = new TamperNet[nets];
        for (int k = 0; k < nets; k++)
        {
            // Spread the nets evenly ACROSS the corridor and keep the pattern centred on the
            // route: net k sits at (k + 0.5)/N - 0.5 of a cell to the left of it. One net is
            // therefore the bare route (delta exactly 0), and every gap between neighbouring
            // conductors - within a corridor and across the boundary between two - is 1/N of
            // a cell, which is what makes IsolationGap a single number.
            double delta = (k + 0.5) / nets - 0.5;
            var path = Place(OffsetRoute(route, delta), origin, pitchX, pitchY);
            built[k] = new TamperNet(k, path, traceWidth);
        }
        return new TamperMeshLayout(
            footprint, blockOrder, blocksX, blocksY, (int)cellsX, (int)cellsY,
            pitch, pitchX, pitchY, traceWidth, gap, built, route);
    }

    /// <summary>The smallest whole number of blocks whose cells are at or under
    /// <paramref name="pitch"/>. Decided by the SAME comparison that defines it — no epsilon,
    /// the generator's rule — so a footprint landing exactly on a block boundary is honoured
    /// exactly.</summary>
    private static int BlocksFor(double extent, int cellsPerBlock, double pitch)
    {
        double perBlock = cellsPerBlock * pitch;
        int blocks = Math.Max(1, (int)Math.Ceiling(extent / perBlock));
        while (blocks * perBlock < extent)
            blocks++;
        while (blocks > 1 && (blocks - 1) * perBlock >= extent)
            blocks--;
        return blocks;
    }

    /// <summary>
    /// The route offset sideways by <paramref name="delta"/> cells, in LATTICE coordinates so
    /// the anisotropic placement below turns it into "a fraction of the local corridor" on
    /// both axes at once.
    /// <para>The miter divides by the direction sum's LengthSquared, never by its Length
    /// squared — the recorded trap (at a right angle those are exactly 2 and
    /// 2.0000000000000004). Here both normals are exact unit axis vectors, so the divisor is
    /// exactly 2 or 4 and the offset carries no round-off at all.</para>
    /// </summary>
    private static Vector2d[] OffsetRoute(IReadOnlyList<Vector2i> route, double delta)
    {
        var offset = new Vector2d[route.Count];
        if (delta == 0)
        {
            for (int i = 0; i < route.Count; i++)
                offset[i] = new Vector2d(route[i].X, route[i].Y);
            return offset;
        }
        for (int i = 0; i < route.Count; i++)
        {
            var here = new Vector2d(route[i].X, route[i].Y);
            var before = i > 0 ? LeftNormal(route[i - 1], route[i]) : LeftNormal(route[i], route[i + 1]);
            var after = i + 1 < route.Count ? LeftNormal(route[i], route[i + 1]) : before;
            var sum = before + after;
            offset[i] = here + sum * (2 * delta / sum.LengthSquared);
        }
        return offset;
    }

    private static Vector2d LeftNormal(in Vector2i from, in Vector2i to) =>
        new(-(to.Y - from.Y), to.X - from.X);

    /// <summary>Lattice coordinates to model space: site (i, j) is the centre of its cell.</summary>
    private static Vector2d[] Place(Vector2d[] lattice, in Vector2d origin, double pitchX, double pitchY)
    {
        var placed = new Vector2d[lattice.Length];
        for (int i = 0; i < lattice.Length; i++)
        {
            placed[i] = new Vector2d(
                origin.X + (lattice[i].X + 0.5) * pitchX,
                origin.Y + (lattice[i].Y + 0.5) * pitchY);
        }
        return placed;
    }

    internal static Vector2d[] FootprintLoop(in Aabb footprint) =>
    [
        new(footprint.Min.X, footprint.Min.Y),
        new(footprint.Max.X, footprint.Min.Y),
        new(footprint.Max.X, footprint.Max.Y),
        new(footprint.Min.X, footprint.Max.Y),
    ];
}

/// <summary>
/// One tamper mesh: the nets, the achieved geometry and the measurements that make the
/// guarantee believable — see <see cref="TamperMesh"/>.
/// </summary>
public sealed class TamperMeshLayout
{
    private DrillGuarantee? _guarantee;

    internal TamperMeshLayout(
        in Aabb footprint, int blockOrder, int blocksX, int blocksY, int cellsX, int cellsY,
        double requestedPitch, double pitchX, double pitchY, double traceWidth,
        double isolationGap, IReadOnlyList<TamperNet> nets, IReadOnlyList<Vector2i> route)
    {
        Footprint = footprint;
        BlockOrder = blockOrder;
        BlocksX = blocksX;
        BlocksY = blocksY;
        CellsX = cellsX;
        CellsY = cellsY;
        RequestedPitch = requestedPitch;
        PitchX = pitchX;
        PitchY = pitchY;
        TraceWidth = traceWidth;
        IsolationGap = isolationGap - traceWidth;
        Nets = nets;
        Route = route;
    }

    /// <summary>The integer lattice sites the route visits, in order — the half of this
    /// feature where all the exactness lives (<c>SpaceFillingCurve.Lattice</c>'s counterpart).
    /// Consecutive sites are exactly one cell apart and every cell is visited exactly once,
    /// which is what makes the guarantee's closed form a statement rather than a hope.</summary>
    public IReadOnlyList<Vector2i> Route { get; }

    /// <summary>The rectangle the mesh covers. Outside it there is no protection at all — the
    /// guarantee is a statement about this rectangle and nowhere else.</summary>
    public Aabb Footprint { get; }

    /// <summary>The Hilbert block order the route is tiled from.</summary>
    public int BlockOrder { get; }

    /// <summary>How many blocks across and up the footprint is tiled into. <c>1 × 1</c> is the
    /// plain Hilbert curve.</summary>
    public int BlocksX { get; }

    /// <inheritdoc cref="BlocksX"/>
    public int BlocksY { get; }

    /// <summary>Lattice cells across the footprint.</summary>
    public int CellsX { get; }

    /// <inheritdoc cref="CellsX"/>
    public int CellsY { get; }

    /// <summary>The pitch the caller asked for. Kept beside the achieved pair so a report can
    /// state both rather than implying they are one number.</summary>
    public double RequestedPitch { get; }

    /// <summary>The ACHIEVED distance between neighbouring passes along x. Never coarser than
    /// <see cref="RequestedPitch"/>.</summary>
    public double PitchX { get; }

    /// <inheritdoc cref="PitchX"/>
    public double PitchY { get; }

    /// <summary>How square the cells came out: <c>max/min</c> of the two pitches, 1 when the
    /// footprint tiles exactly. A long thin footprint at a high block order drives it up, which
    /// is why the block order is a caller's choice.</summary>
    public double Anisotropy => Math.Max(PitchX, PitchY) / Math.Min(PitchX, PitchY);

    /// <summary>The conductor width.</summary>
    public double TraceWidth { get; }

    /// <summary>The copper-to-copper gap between neighbouring conductors of DIFFERENT nets —
    /// <c>min(PitchX, PitchY)/Nets − TraceWidth</c>. Any conductive bridge that spans it shorts
    /// two nets, which is what an isolation monitor sees. Meaningless for a single net.</summary>
    public double IsolationGap { get; }

    /// <summary>The nets, in order across a corridor.</summary>
    public IReadOnlyList<TamperNet> Nets { get; }

    /// <summary>Total drawn conductor length over every net.</summary>
    public double Length => Nets.Sum(n => n.Length);

    /// <summary>The longest straight run of the route, in CELLS — the number that says a slot
    /// or a saw cut has no long channel to run down, and the honest reason to prefer Hilbert
    /// over a serpentine (which has the SAME drill guarantee and a channel the width of the
    /// wall). Measured off the lattice, so it is exact: 3 for a single order-2 block, 4 from
    /// order 3 up and wherever two tiled blocks hand over in line, and the whole row at block
    /// order 0. <c>SpaceFillingCurve</c>'s own saturation test counts STEPS, one fewer.</summary>
    public int LongestStraightRun
    {
        get
        {
            int longest = 1, run = 1;
            for (int i = 1; i < Route.Count; i++)
            {
                bool collinear = i >= 2
                    && (Route[i] - Route[i - 1]).X == (Route[i - 1] - Route[i - 2]).X
                    && (Route[i] - Route[i - 1]).Y == (Route[i - 1] - Route[i - 2]).Y;
                run = collinear ? run + 1 : 2;
                longest = Math.Max(longest, run);
            }
            return longest;
        }
    }

    /// <summary>
    /// The drill guarantee, MEASURED on the geometry that was built rather than taken from the
    /// closed form. Computed once and cached; see <see cref="DrillGuarantee"/> for what the
    /// measurement certifies.
    /// </summary>
    public DrillGuarantee Guarantee => _guarantee ??= Measure();

    /// <summary>
    /// The guarantee measured to a stated <paramref name="tolerance"/> on the gap radius.
    /// <see cref="Guarantee"/> uses a tolerance of 1e-9 of the pitch.
    /// </summary>
    public DrillGuarantee Measure(double? tolerance = null)
    {
        double tol = tolerance ?? Math.Min(PitchX, PitchY) * 1e-9;
        var (radius, at, uncertainty) = LargestEmptyGap.Measure(
            [.. Nets.Select(n => n.Path)], Footprint, tol);
        return new DrillGuarantee(radius, at, TraceWidth, uncertainty);
    }

    /// <summary>The whole mesh as one DXF document — one layer per net (<c>NET0</c>,
    /// <c>NET1</c>, …) carrying that net's conductor outline, plus the footprint on
    /// <c>FOOTPRINT</c>. Millimetres are declared, per <see cref="DxfDocument"/>'s rule that a
    /// file which does not say what its numbers mean leaves every reader to guess.</summary>
    public DxfDocument ToDxf()
    {
        var dxf = new DxfDocument();
        dxf.Add(new DxfPolyline(TamperMesh.FootprintLoop(Footprint), closed: true, "FOOTPRINT"));
        foreach (var net in Nets)
            dxf.Add(net.Outline, $"NET{net.Index}");
        return dxf;
    }
}

/// <summary>
/// One conductive net of a <see cref="TamperMeshLayout"/>: a single unbroken route with two
/// terminals, which is exactly what a continuity monitor is wired to.
/// </summary>
public sealed class TamperNet
{
    private Region2d? _outline;

    internal TamperNet(int index, IReadOnlyList<Vector2d> path, double traceWidth)
    {
        Index = index;
        Path = path;
        TraceWidth = traceWidth;
        double length = 0;
        for (int i = 1; i < path.Count; i++)
            length += path[i].DistanceTo(path[i - 1]);
        Length = length;
    }

    /// <summary>Which net this is, counting across a corridor from one side.</summary>
    public int Index { get; }

    /// <summary>The conductor CENTRELINE, one unbroken polyline. Consecutive points are one
    /// pitch apart along whichever axis the step ran.</summary>
    public IReadOnlyList<Vector2d> Path { get; }

    /// <summary>The conductor width.</summary>
    public double TraceWidth { get; }

    /// <summary>Centreline length.</summary>
    public double Length { get; }

    /// <summary>Where the monitor's first wire lands. On the footprint's outer boundary.</summary>
    public Vector2d TerminalA => Path[0];

    /// <summary>Where the monitor's second wire lands. Also on the footprint's outer
    /// boundary, and for a single row of blocks or an even number of rows at the far end of
    /// the SAME edge — which is what a two-pin connector wants.</summary>
    public Vector2d TerminalB => Path[^1];

    /// <summary>
    /// The copper: the centreline swept at <see cref="TraceWidth"/>, as ONE simple polygon.
    ///
    /// <para><b>Built, not booleaned.</b> A stroke through <c>Region2dOffset.Stroke</c> unions
    /// a slab per segment, which is O(E²) in the arrangement and minutes of work at mesh
    /// scale. It is not needed: the route is a simple polyline whose neighbouring passes are a
    /// whole pitch apart while the half-width is under half a pitch, so the two ±w/2 mitered
    /// offsets are themselves simple and do not meet — the ribbon is exactly
    /// <c>[left offset forward] + [right offset back]</c> closed at the two butt ends. The
    /// simplicity is CHECKED rather than assumed: <c>Region2d</c>'s own guard runs on the loop
    /// and names any crossing.</para>
    ///
    /// <para>A mitered ribbon's area is exactly <c>Length × TraceWidth</c> — at every corner
    /// the outer miter triangle is congruent to the inner notch — which makes
    /// <see cref="CopperArea"/> an identity to check the construction against rather than a
    /// number to trust.</para>
    /// </summary>
    public Region2d Outline => _outline ??= BuildOutline();

    /// <summary>The copper area. Equals <c>Length × TraceWidth</c> exactly, for a mitered
    /// ribbon that does not overlap itself.</summary>
    public double CopperArea => Outline.Area;

    /// <summary>The conductor as a solid of the given thickness, placed on
    /// <paramref name="plane"/> (null = the world XY plane) — ready to union onto a substrate
    /// or subtract as a channel.</summary>
    public Shape Conductor(double thickness, SketchPlane? plane = null)
    {
        if (!(thickness > 0) || !double.IsFinite(thickness))
            throw new ArgumentOutOfRangeException(nameof(thickness), "The conductor thickness must be positive.");
        var frame = plane?.Frame ?? Frame3d.WorldXY;
        var (outer, holes) = Profile.FromRegion(Outline, frame);
        return Shape.Extrude(outer, frame.Z * thickness, holes);
    }

    private Region2d BuildOutline()
    {
        double half = TraceWidth / 2;
        var left = MiteredOffset(Path, half);
        var right = MiteredOffset(Path, -half);
        var loop = new List<Vector2d>(left.Length + right.Length);
        loop.AddRange(left);
        for (int i = right.Length - 1; i >= 0; i--)
            loop.Add(right[i]);

        var regions = Region2d.FromLoops([loop]);
        if (regions.Count != 1)
        {
            throw new InvalidOperationException(
                $"The conductor outline for net {Index} came back as {regions.Count} regions rather "
                + "than one simple ribbon. That should be impossible at a trace width under the "
                + "isolation gap; report the pitch, width and net count.");
        }
        return regions[0];
    }

    /// <summary>The polyline offset perpendicular by <paramref name="distance"/> (positive =
    /// left of travel), mitered at every joint. Same divisor rule as the net offsets: by the
    /// direction sum's LengthSquared, never by its Length squared.</summary>
    private static Vector2d[] MiteredOffset(IReadOnlyList<Vector2d> path, double distance)
    {
        var offset = new Vector2d[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            var before = i > 0 ? UnitLeftNormal(path[i - 1], path[i]) : UnitLeftNormal(path[i], path[i + 1]);
            var after = i + 1 < path.Count ? UnitLeftNormal(path[i], path[i + 1]) : before;
            var sum = before + after;
            offset[i] = path[i] + sum * (2 * distance / sum.LengthSquared);
        }
        return offset;
    }

    private static Vector2d UnitLeftNormal(in Vector2d from, in Vector2d to)
    {
        var d = to - from;
        var n = new Vector2d(-d.Y, d.X);
        return n / n.Length;
    }
}

/// <summary>
/// What a drill can and cannot do to a <see cref="TamperMeshLayout"/> — the deliverable, and a
/// MEASUREMENT rather than a formula.
///
/// <para><see cref="GapRadius"/> is the radius of the largest empty circle in the uncovered
/// space, found by branch and bound over the footprint: the distance to the nearest conductor
/// centreline is 1-Lipschitz, so a cell of half-diagonal r whose centre reads d can hold
/// nothing above d + r — the same argument <c>SurfaceCull</c> stands on, and the reason
/// <see cref="Uncertainty"/> is a CERTIFIED bracket rather than a sampling hope.</para>
/// </summary>
/// <param name="GapRadius">The largest distance from a point of the footprint to the nearest
/// conductor centreline, i.e. half the diameter of the largest circle that fits between the
/// traces plus half the trace width.</param>
/// <param name="WeakestPoint">Where that gap is. Read it: the footprint's own corners are
/// weak points by construction, and an interior one is a "blind" 2×2 block of cells.</param>
/// <param name="TraceWidth">The conductor width the diameters are computed at.</param>
/// <param name="Uncertainty">How far above <see cref="GapRadius"/> the true maximum could
/// still be — the branch-and-bound bracket, not a guess.</param>
public readonly record struct DrillGuarantee(
    double GapRadius, Vector2d WeakestPoint, double TraceWidth, double Uncertainty)
{
    /// <summary>The largest drill that can reach the inside without touching copper. Any drill
    /// at or above this diameter MUST hit a conductor wherever it is placed.</summary>
    public double TouchDiameter => 2 * GapRadius - TraceWidth;

    /// <summary>The smallest drill that must SEVER a net wherever it is placed — its disc spans
    /// the conductor's full width at some point of the centreline. Touching is not cutting, so
    /// this is the number a design is sized to.</summary>
    public double SeverDiameter => 2 * GapRadius + TraceWidth;

    /// <summary>Whether a drill of <paramref name="diameter"/> is certain to sever a net.</summary>
    public bool Defeats(double diameter) => diameter >= SeverDiameter;
}

/// <summary>
/// The largest empty circle in the space a set of polylines leaves — certified by branch and
/// bound rather than sampled. Distance to a polyline is 1-Lipschitz, so a cell's centre
/// distance plus its half-diagonal bounds everything inside it; a cell whose bound cannot beat
/// the incumbent is discarded whole.
/// </summary>
internal static class LargestEmptyGap
{
    public static (double Radius, Vector2d At, double Uncertainty) Measure(
        IReadOnlyList<IReadOnlyList<Vector2d>> paths, in Aabb footprint, double tolerance)
    {
        var index = new SegmentIndex(paths, footprint);
        double minX = footprint.Min.X, minY = footprint.Min.Y;
        double width = footprint.Max.X - minX, height = footprint.Max.Y - minY;

        // Seed with near-square cells so the first bound is already tight; the queue does the
        // rest. Sixteen across the short side costs nothing and prunes almost everything.
        double seed = Math.Min(width, height) / 16;
        int nx = Math.Max(1, (int)Math.Ceiling(width / seed));
        int ny = Math.Max(1, (int)Math.Ceiling(height / seed));

        var queue = new PriorityQueue<(double X, double Y, double HalfX, double HalfY), double>();
        double best = double.NegativeInfinity;
        var bestAt = new Vector2d(minX, minY);

        void Consider(double cx, double cy, double hx, double hy)
        {
            double d = index.Distance(new Vector2d(cx, cy));
            if (d > best)
            {
                best = d;
                bestAt = new Vector2d(cx, cy);
            }
            queue.Enqueue((cx, cy, hx, hy), -(d + Math.Sqrt(hx * hx + hy * hy)));
        }

        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            double hx = width / nx / 2, hy = height / ny / 2;
            Consider(minX + (2 * i + 1) * hx, minY + (2 * j + 1) * hy, hx, hy);
        }

        while (queue.TryDequeue(out var cell, out double negativeBound))
        {
            double bound = -negativeBound;
            if (bound <= best + tolerance)
                return (best, bestAt, Math.Max(0, bound - best));
            double hx = cell.HalfX / 2, hy = cell.HalfY / 2;
            Consider(cell.X - hx, cell.Y - hy, hx, hy);
            Consider(cell.X + hx, cell.Y - hy, hx, hy);
            Consider(cell.X - hx, cell.Y + hy, hx, hy);
            Consider(cell.X + hx, cell.Y + hy, hx, hy);
        }
        return (best, bestAt, 0);
    }

    /// <summary>A uniform bucket grid over the conductor segments, with a ring search that
    /// stops only when no unvisited ring could hold anything closer — so the answer is the
    /// exact nearest distance, not a windowed approximation.</summary>
    private sealed class SegmentIndex
    {
        private readonly List<int>[] _buckets;
        private readonly Vector2d[] _from;
        private readonly Vector2d[] _to;
        private readonly double _cell;
        private readonly double _minX, _minY;
        private readonly int _nx, _ny;

        public SegmentIndex(IReadOnlyList<IReadOnlyList<Vector2d>> paths, in Aabb footprint)
        {
            var from = new List<Vector2d>();
            var to = new List<Vector2d>();
            foreach (var path in paths)
            {
                for (int i = 1; i < path.Count; i++)
                {
                    from.Add(path[i - 1]);
                    to.Add(path[i]);
                }
            }
            _from = [.. from];
            _to = [.. to];

            double width = footprint.Max.X - footprint.Min.X;
            double height = footprint.Max.Y - footprint.Min.Y;
            _cell = Math.Max(1e-300, Math.Sqrt(width * height / Math.Max(1, _from.Length)) * 2);
            _minX = footprint.Min.X - _cell;
            _minY = footprint.Min.Y - _cell;
            _nx = Math.Max(1, (int)Math.Ceiling((width + 4 * _cell) / _cell));
            _ny = Math.Max(1, (int)Math.Ceiling((height + 4 * _cell) / _cell));
            _buckets = new List<int>[_nx * _ny];

            for (int s = 0; s < _from.Length; s++)
            {
                int x0 = Clamp((int)Math.Floor((Math.Min(_from[s].X, _to[s].X) - _minX) / _cell), _nx);
                int x1 = Clamp((int)Math.Floor((Math.Max(_from[s].X, _to[s].X) - _minX) / _cell), _nx);
                int y0 = Clamp((int)Math.Floor((Math.Min(_from[s].Y, _to[s].Y) - _minY) / _cell), _ny);
                int y1 = Clamp((int)Math.Floor((Math.Max(_from[s].Y, _to[s].Y) - _minY) / _cell), _ny);
                for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    (_buckets[y * _nx + x] ??= []).Add(s);
            }
        }

        public double Distance(in Vector2d p)
        {
            int cx = Clamp((int)Math.Floor((p.X - _minX) / _cell), _nx);
            int cy = Clamp((int)Math.Floor((p.Y - _minY) / _cell), _ny);
            double best = double.PositiveInfinity;
            int span = Math.Max(_nx, _ny);
            for (int ring = 0; ring <= span; ring++)
            {
                // Everything beyond this ring is at least (ring - 1) cells away, since p sits
                // somewhere inside its own cell. Stop when that cannot beat what we have.
                if (ring > 1 && (ring - 1) * _cell >= best)
                    break;
                for (int x = cx - ring; x <= cx + ring; x++)
                for (int y = cy - ring; y <= cy + ring; y++)
                {
                    if (ring > 0 && Math.Abs(x - cx) != ring && Math.Abs(y - cy) != ring)
                        continue;
                    if (x < 0 || x >= _nx || y < 0 || y >= _ny)
                        continue;
                    var bucket = _buckets[y * _nx + x];
                    if (bucket is null)
                        continue;
                    foreach (int s in bucket)
                        best = Math.Min(best, PointToSegment(p, _from[s], _to[s]));
                }
            }
            return best;
        }

        private static double PointToSegment(in Vector2d p, in Vector2d a, in Vector2d b)
        {
            var ab = b - a;
            double lengthSquared = ab.LengthSquared;
            if (lengthSquared == 0)
                return p.DistanceTo(a);
            double t = Math.Clamp((p - a).Dot(ab) / lengthSquared, 0, 1);
            return p.DistanceTo(a + ab * t);
        }

        private static int Clamp(int value, int count) => Math.Clamp(value, 0, count - 1);
    }
}
