using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// One piece of a thread's per-pitch generator in the (radius, axial) half-plane: either a
/// straight segment or a circular ARC through <see cref="ArcCenter"/>.
/// <para>The arc exists for one reason and it is not generality: a printing CLEARANCE is a
/// distance-field offset of the profile, and eroding a thread form miters its crest corners
/// (which stay straight) while ROUNDING its root corners into arcs of the clearance radius.
/// The sweep is not a separate parameter because it is not a free one — a centre and two
/// endpoints leave two arcs, and the axial coordinate must increase strictly along a thread
/// generator, so the MINOR arc is the only admissible one and it is derived.</para>
/// </summary>
/// <param name="Start">(radius, axial) at v = 0.</param>
/// <param name="End">(radius, axial) at v = 1; its axial coordinate must exceed
/// <paramref name="Start"/>'s.</param>
/// <param name="ArcCenter">The arc's centre, or null for a straight segment.</param>
public readonly record struct ThreadProfilePiece(Vector2d Start, Vector2d End, Vector2d? ArcCenter)
{
    /// <summary>A straight generator piece.</summary>
    public static ThreadProfilePiece Line(in Vector2d start, in Vector2d end) => new(start, end, null);

    /// <summary>The minor arc from <paramref name="start"/> to <paramref name="end"/> about
    /// <paramref name="center"/>.</summary>
    public static ThreadProfilePiece Arc(in Vector2d start, in Vector2d end, in Vector2d center) =>
        new(start, end, center);

    /// <summary>Whether this piece is an arc.</summary>
    public bool IsArc => ArcCenter.HasValue;
}

public static partial class SolidFactory
{
    /// <summary>
    /// A helically threaded rod: the solid whose lateral boundary is ONE co-rotating
    /// sweep of a per-pitch profile — z ∈ [0, <paramref name="length"/>] along the
    /// frame's Z axis, capped flat at both ends. This construction is boolean-free by
    /// design: winding a ridge onto a core cylinder would create a coaxial tangent seam
    /// (the unsupported coplanar boolean case), whereas here the root flats ARE part of
    /// the sweep, so no core cylinder exists.
    /// <para>
    /// <paramref name="pitchProfile"/> lists the (radius, axial) corners of ONE pitch of
    /// the profile in the axial half-plane at u = 0, with strictly increasing axial
    /// coordinates spanning less than one <paramref name="pitch"/>; the closing segment
    /// wraps to the first corner shifted up by one pitch. Because each profile segment
    /// repeated every pitch traces a single continuous helical band over ALL turns, the
    /// solid has exactly one <see cref="HelicalSurface"/> band per profile segment (K
    /// segments for K corners), adjacent bands sharing exact <see cref="Helix3d"/> rail
    /// edges built on the rod's own frame rotated to each corner's phase (rails start on
    /// the z = 0 cap plane). A cap plane cuts each band along a <see cref="SpiralArc3d"/>
    /// — for a linear generator, solving z(v) + pitch·u/2π = z<sub>cap</sub> makes v
    /// (hence the radius) linear in u — so each cap is a planar disk bounded by the
    /// closed chain of K spiral arcs covering one full turn. The length need not be a
    /// whole number of pitches: rails simply end at different phases on the cap planes.
    /// </para>
    /// <para>
    /// <paramref name="leftHand"/> winds the SAME profile the other way. It is not a
    /// separate construction: the axial rate becomes −p/2π, so a rail's phase u runs
    /// DOWN as z runs up, and every formula here already carries the signed rate. Three
    /// things follow mechanically and are the whole of the difference — a rail's helix is
    /// anchored on the TOP cap (t = 0 at z = length, descending), the u = min and u = max
    /// edges of a band swap roles (u = min is now the top cut), and both cap loops chain
    /// the other way round. Note a left-hand rod is NOT a right-hand rod on some other
    /// frame: every right-handed frame is a rotation of every other, so handedness cannot
    /// enter through the pose — it has to enter the arithmetic.
    /// </para>
    /// <para>
    /// Counts (either handedness): V = 2K (one rail start + end per corner), E = 3K
    /// (K rails + K spiral cuts per cap), F = K + 2, L = K + 2, so Euler–Poincaré gives
    /// V − E + F − (L − F) − 2(S − G) = 2K − 3K + (K + 2) − 0 − 2 = 0 (genus 0).
    /// </para>
    /// </summary>
    /// <param name="pitchProfile">(radius, axial) corners of one pitch, radii positive,
    /// axial strictly increasing, span &lt; pitch. Axial values may be negative (e.g. a
    /// crest-centered thread profile); phase 0 is the frame's X axis at z = 0.</param>
    /// <param name="pitch">Axial advance per turn; must be positive (pass
    /// <paramref name="leftHand"/> for the other handedness rather than a negative pitch,
    /// so the profile's own axial ordering never has to be reinterpreted).</param>
    /// <param name="length">Rod length along the frame's Z axis; any positive value.</param>
    /// <param name="frame">Axis pose; defaults to the world frame (axis +Z from the origin).</param>
    /// <param name="leftHand">Wind the thread left-handed (counter-clockwise advance
    /// seen along +Z); the default is the right-hand thread.</param>
    public static BrepSolid MakeThreadedRod(
        IReadOnlyList<Vector2d> pitchProfile, double pitch, double length, Frame3d? frame = null,
        bool leftHand = false)
    {
        // The pitch and the length are checked here as well as in the piece overload,
        // because the corner rules below (strictly increasing, span under one pitch) are
        // only meaningful once the pitch is.
        if (!(pitch > 0) || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch), "Pitch must be positive (right-hand threads only).");
        if (!(length > 0) || !double.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(length));
        ArgumentNullException.ThrowIfNull(pitchProfile);
        int corners = pitchProfile.Count;
        if (corners < 2)
            throw new ArgumentException("A thread profile needs at least two corners per pitch.", nameof(pitchProfile));
        for (int k = 1; k < corners; k++)
        {
            if (!(pitchProfile[k].Y > pitchProfile[k - 1].Y))
                throw new ArgumentException("Profile axial coordinates must be strictly increasing.", nameof(pitchProfile));
        }
        if (!(pitchProfile[^1].Y - pitchProfile[0].Y < pitch))
            throw new ArgumentException(
                "The profile must span less than one pitch (the closing segment wraps to the first corner + pitch).",
            nameof(pitchProfile));
        var straight = new ThreadProfilePiece[corners];
        for (int k = 0; k < corners; k++)
        {
            var next = k + 1 < corners
                ? pitchProfile[k + 1]
                : new Vector2d(pitchProfile[0].X, pitchProfile[0].Y + pitch);
            straight[k] = ThreadProfilePiece.Line(pitchProfile[k], next);
        }
        return MakeThreadedRod(straight, pitch, length, frame, leftHand);
    }

    /// <summary>
    /// The general form of <see cref="MakeThreadedRod(IReadOnlyList{Vector2d}, double, double, Frame3d?, bool)"/>,
    /// whose generator pieces may be straight segments OR circular arcs — which is what a
    /// distance-field CLEARANCE profile needs, its eroded root corners being arcs of the
    /// clearance radius. Everything else is unchanged and the corner overload delegates
    /// here, so an all-straight profile builds bit-for-bit the solid it always did.
    /// <para>The pieces chain nose to tail and the LAST one closes the pitch explicitly:
    /// its end must be the first piece's start raised by one <paramref name="pitch"/>.
    /// Stating the wrap rather than implying it is what lets the closing piece be an arc.
    /// An arc piece's cap cut is a <see cref="HelicalArcCut3d"/> where a straight piece's is
    /// a <see cref="SpiralArc3d"/> — the same closed-form family one generator up.</para>
    /// </summary>
    public static BrepSolid MakeThreadedRod(
        IReadOnlyList<ThreadProfilePiece> pitchProfile, double pitch, double length, Frame3d? frame = null,
        bool leftHand = false)
    {
        if (!(pitch > 0) || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch), "Pitch must be positive (right-hand threads only).");
        if (!(length > 0) || !double.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(length));
        ArgumentNullException.ThrowIfNull(pitchProfile);
        int count = pitchProfile.Count;
        if (count < 2)
            throw new ArgumentException("A thread profile needs at least two pieces per pitch.", nameof(pitchProfile));
        // Weld tier: these corners are exactly-constructed shared geometry — the piece's own
        // end IS the next piece's start and the rail vertex both bands meet at.
        const double weld = 1e-9;
        for (int k = 0; k < count; k++)
        {
            var piece = pitchProfile[k];
            if (!(piece.Start.X > 0) || !(piece.End.X > 0))
                throw new ArgumentException("Profile radii must be positive (the generator stays off the axis).", nameof(pitchProfile));
            if (!(piece.End.Y > piece.Start.Y))
                throw new ArgumentException("Profile axial coordinates must be strictly increasing.", nameof(pitchProfile));
            var expected = k + 1 < count
                ? pitchProfile[k + 1].Start
                : new Vector2d(pitchProfile[0].Start.X, pitchProfile[0].Start.Y + pitch);
            if ((piece.End - expected).Length > weld)
                throw new ArgumentException(
                    $"Profile piece {k} ends at {piece.End} but the next piece starts at {expected}; " +
                    "the pieces must chain, and the last must close the pitch (first start + pitch).",
                    nameof(pitchProfile));
            if (piece.ArcCenter is { } center &&
                Math.Abs((piece.Start - center).Length - (piece.End - center).Length) > weld)
                throw new ArgumentException(
                    $"Profile piece {k} is an arc whose ends are {(piece.Start - center).Length:g6} and " +
                    $"{(piece.End - center).Length:g6} from its centre; an arc's two ends share one radius.",
                    nameof(pitchProfile));
        }
        if (!(pitchProfile[^1].End.Y - pitchProfile[0].Start.Y <= pitch + weld))
            throw new ArgumentException(
                "The profile must span exactly one pitch (the last piece closes to the first corner + pitch).",
            nameof(pitchProfile));

        var f = frame ?? Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        // The ONE place handedness enters. Every formula below is written in the band's
        // own phase u with z = z_generator + rate·u, so a negative rate makes u descend
        // as the rod ascends and the arithmetic follows by itself.
        double signedPitch = leftHand ? -pitch : pitch;
        double rate = signedPitch / (2 * Math.PI);
        double turns = length / pitch;
        // The K corners plus the wrapped closing one, read off the pieces' own ends so a
        // corner and the geometry that meets there are the same numbers.
        var corner = new Vector2d[count + 1];
        for (int k = 0; k < count; k++)
            corner[k] = pitchProfile[k].Start;
        corner[count] = pitchProfile[^1].End;

        // Rails: one exact helix per profile corner, on the rod's own frame rotated to
        // the corner's phase (never an arbitrary perpendicular — phase alignment is the
        // weld rule). A right-hand rail is anchored on the z = 0 cap and climbs; a
        // left-hand rail is anchored on the z = length cap and descends, since Helix3d's
        // domain always starts at its frame's own plane. Either way the rail's phase u
        // INCREASES along its parameter, which is what keeps the band loops below
        // identical in form.
        var railEdges = new BrepEdge[count];
        var startVertices = new BrepVertex[count];   // on z = 0
        var endVertices = new BrepVertex[count];     // on z = length
        var railOrigin = leftHand ? f.Origin + f.Z * length : f.Origin;
        for (int k = 0; k < count; k++)
        {
            // Phase where this corner's helix crosses the anchoring cap plane.
            double alpha = (leftHand ? length - corner[k].Y : -corner[k].Y) / rate;
            var x = f.X * Math.Cos(alpha) + f.Y * Math.Sin(alpha);
            var railFrame = Frame3d.FromOrthonormal(railOrigin, x, f.Z.Cross(x));
            var rail = new Helix3d(railFrame, corner[k].X, signedPitch, turns);
            var atDomainStart = new BrepVertex(rail.PointAt(rail.Domain.Start));
            var atDomainEnd = new BrepVertex(rail.PointAt(rail.Domain.End));
            (startVertices[k], endVertices[k]) =
                leftHand ? (atDomainEnd, atDomainStart) : (atDomainStart, atDomainEnd);
            railEdges[k] = new BrepEdge(rail, rail.Domain, atDomainStart, atDomainEnd);
        }

        // Band surfaces: one per profile piece, over the u range where the piece's own
        // axial extremes — its two ends, the generator being axially monotone either way —
        // still lie between the two cap planes.
        var surfaces = new HelicalSurface[count];
        for (int k = 0; k < count; k++)
        {
            double edgeA = -corner[k + 1].Y / rate;
            double edgeB = (length - corner[k].Y) / rate;
            var domainU = new Interval(Math.Min(edgeA, edgeB), Math.Max(edgeA, edgeB));
            var piece = pitchProfile[k];
            if (piece.ArcCenter is not { } center)
            {
                surfaces[k] = new HelicalSurface(f, corner[k], corner[k + 1], signedPitch, domainU);
                continue;
            }
            double phi0 = Math.Atan2(corner[k].Y - center.Y, corner[k].X - center.X);
            double phi1 = Math.Atan2(corner[k + 1].Y - center.Y, corner[k + 1].X - center.X);
            double sweep = phi1 - phi0;
            sweep -= 2 * Math.PI * Math.Floor((sweep + Math.PI) / (2 * Math.PI));
            surfaces[k] = new HelicalSurface(
                f, center, (corner[k] - center).Length, phi0, sweep, signedPitch, domainU);
        }

        // Cap cuts: the curve each cap plane cuts from band k, parameterized by the band's
        // own u (phase-aligned frames on the axis at each cap). For a STRAIGHT generator
        // (r, z) = C_k + v·(dr, dz), v(u) = (z_cap − z_k − rate·u)/dz, so
        // r(u) = [r_k + dr·(z_cap − z_k)/dz] + [−dr·rate/dz]·u — the signed rate carries
        // the handedness. For an ARC generator the same substitution makes the generator
        // ANGLE an arc-cosine of an affine function of u (see HelicalArcCut3d), which is
        // the one thing that changes. Either way the v = 0 and v = 1 ends sit at
        // u = (z_cap − z_k)/rate and (z_cap − z_{k+1})/rate, whose ORDER flips with the
        // rate's sign, so a left-hand cut runs v 0→1 where a right-hand one runs v 1→0.
        var bottomCuts = new BrepEdge[count];
        var topCuts = new BrepEdge[count];
        var bottomFrame = f;
        var topFrame = Frame3d.FromOrthonormal(f.Origin + f.Z * length, f.X, f.Y);
        for (int k = 0; k < count; k++)
        {
            var (r0, z0) = (corner[k].X, corner[k].Y);
            var (r1, z1) = (corner[k + 1].X, corner[k + 1].Y);
            var bottomSpan = leftHand
                ? new Interval(-z0 / rate, -z1 / rate)
                : new Interval(-z1 / rate, -z0 / rate);
            var topSpan = leftHand
                ? new Interval((length - z0) / rate, (length - z1) / rate)
                : new Interval((length - z1) / rate, (length - z0) / rate);

            Curve3d bottomCurve, topCurve;
            if (pitchProfile[k].IsArc)
            {
                if (!HelicalArcCut3d.TryBuild(surfaces[k], 0, 1, 0, bottomSpan, out var bottomArcCut) ||
                    !HelicalArcCut3d.TryBuild(surfaces[k], 0, 1, length, topSpan, out var topArcCut))
                    throw new ArgumentException(
                        $"Profile piece {k}'s arc does not meet the cap planes in a single cut; " +
                        "its axial coordinate must be strictly monotone.", nameof(pitchProfile));
                (bottomCurve, topCurve) = (bottomArcCut, topArcCut);
            }
            else
            {
                double dr = r1 - r0, dz = z1 - z0;
                double slope = -dr * rate / dz;
                bottomCurve = new SpiralArc3d(bottomFrame, r0 - dr * z0 / dz, slope, bottomSpan);
                topCurve = new SpiralArc3d(topFrame, r0 + dr * (length - z0) / dz, slope, topSpan);
            }

            bottomCuts[k] = leftHand
                ? new BrepEdge(bottomCurve, bottomCurve.Domain, startVertices[k], startVertices[(k + 1) % count])
                : new BrepEdge(bottomCurve, bottomCurve.Domain, startVertices[(k + 1) % count], startVertices[k]);
            topCuts[k] = leftHand
                ? new BrepEdge(topCurve, topCurve.Domain, endVertices[k], endVertices[(k + 1) % count])
                : new BrepEdge(topCurve, topCurve.Domain, endVertices[(k + 1) % count], endVertices[k]);
        }

        // Band faces: the strip in (u, v) between the two rails and the two cap cuts. The
        // generator ascends axially (dz > 0), so ∂u × ∂v has radial component r·dz > 0 —
        // outward for EITHER handedness — and the CCW loop in (u, v) is always: v = 0 rail
        // forward (+u), the u = max cut (v 0→1), v = 1 rail backward (−u), the u = min cut
        // (v 1→0). Only WHICH cap sits at u = max changes: for a right-hand thread u climbs
        // with z, so u = max is the top; for a left-hand one it is the bottom.
        var faces = new List<BrepFace>(count + 2);
        for (int k = 0; k < count; k++)
        {
            var surface = surfaces[k];
            var (atMaxU, atMinU) = leftHand
                ? (new BrepCoedge(bottomCuts[k], sameSense: true),
                   new BrepCoedge(topCuts[k], sameSense: false))
                : (new BrepCoedge(topCuts[k], sameSense: false),
                   new BrepCoedge(bottomCuts[k], sameSense: true));
            faces.Add(new BrepFace(surface,
            [
                new BrepLoop(
                [
                    new BrepCoedge(railEdges[k], sameSense: true),
                    atMaxU,
                    new BrepCoedge(railEdges[(k + 1) % count], sameSense: false),
                    atMinU,
                ]),
            ]));
        }

        // Bottom cap: normal −Z (basis y, x); its loop runs clockwise seen from +Z, which
        // means visiting the rail vertices in order of DECREASING phase. Corner index
        // rises with axial coordinate, so that is increasing index for a right-hand
        // thread and decreasing index for a left-hand one.
        var bottomLoop = new BrepLoop(
        [
            .. Enumerable.Range(0, count).Select(k =>
                new BrepCoedge(bottomCuts[leftHand ? count - 1 - k : k], sameSense: false)),
        ]);
        faces.Add(new BrepFace(new PlaneSurface(f.Origin, f.Y, f.X), [bottomLoop]));

        // Top cap: normal +Z (basis x, y); counter-clockwise from above is increasing
        // phase — the mirror image of the bottom cap's ordering rule.
        var topLoop = new BrepLoop(
        [
            .. Enumerable.Range(0, count).Select(k =>
                new BrepCoedge(topCuts[leftHand ? k : count - 1 - k], sameSense: true)),
        ]);
        faces.Add(new BrepFace(new PlaneSurface(f.Origin + f.Z * length, f.X, f.Y), [topLoop]));

        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// The per-pitch generator of a thread whose profile has been offset as a DISTANCE
    /// FIELD by <paramref name="offset"/> — negative erodes the material (an external
    /// thread's printing clearance), positive grows it (the same clearance on the tool that
    /// cuts an internal thread, whose void is what has to grow). This is the input
    /// <see cref="MakeThreadedRod(IReadOnlyList{ThreadProfilePiece}, double, double, Frame3d?, bool)"/>
    /// needs to make a clearance thread exact.
    ///
    /// <para><b>Why a distance-field offset rather than a perpendicular one.</b> Offsetting
    /// every flat and flank perpendicular to itself and mitering all the corners needs no
    /// arcs at all and is a perfectly reasonable clearance convention — but
    /// <c>Sdf.Thread</c>'s clearance IS the distance-field offset, and two representations
    /// of one <c>ThreadShape</c> that disagree about the geometry is the one thing this
    /// kernel does not do. So the rule here is the erosion's own: at a corner the profile
    /// turns AWAY from the offset direction the two offset lines meet in a sharp MITER,
    /// and where it turns toward it the offset rounds into an ARC of the offset's own
    /// radius centred on the corner. On an eroded external thread that is a mitered crest
    /// and rounded roots; on a grown internal tool it is exactly the other way round, from
    /// the same expression, because the corner classification is
    /// <c>offset × turn</c>.</para>
    ///
    /// <para><b>A flat can vanish, and that is ordinary rather than an error.</b> A 60°
    /// crest flat loses <c>|offset|/tan(30°)</c> of width per side, so an M6×1's 0.125 mm
    /// crest is gone by a clearance of 0.108 — well inside the 0.1–0.25 mm an FDM printer
    /// wants — and the eroded thread is correctly a POINTED ridge where the two offset
    /// flanks cross. That segment's offset half-plane has simply become redundant, which is
    /// exactly what "its offset length went non-positive" measures, so it is dropped and
    /// its neighbours mitered directly. The drop is only sound where both of its corners
    /// miter (the region is locally convex there, so the erosion really is the intersection
    /// of the offset half-planes); anything else is refused by name rather than guessed
    /// at.</para>
    /// </summary>
    /// <param name="pitchProfile">The unoffset corners, exactly as the corner overload of
    /// <c>MakeThreadedRod</c> takes them.</param>
    /// <param name="pitch">Axial advance per turn.</param>
    /// <param name="offset">Signed distance-field offset; exactly zero returns the straight
    /// pieces unchanged.</param>
    public static IReadOnlyList<ThreadProfilePiece> OffsetPitchProfile(
        IReadOnlyList<Vector2d> pitchProfile, double pitch, double offset)
    {
        ArgumentNullException.ThrowIfNull(pitchProfile);
        if (!(pitch > 0) || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch));
        if (!double.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        int k = pitchProfile.Count;
        if (k < 2)
            throw new ArgumentException("A thread profile needs at least two corners per pitch.", nameof(pitchProfile));

        var corner = new Vector2d[k + 1];
        for (int i = 0; i < k; i++)
            corner[i] = pitchProfile[i];
        corner[k] = new Vector2d(pitchProfile[0].X, pitchProfile[0].Y + pitch);

        var pieces = new ThreadProfilePiece[k];
        // Deliberate exact-zero test: "no clearance requested" is a user-parameter
        // contract, and taking the straight path keeps an unoffset thread's solid
        // bit-for-bit what it has always been.
        if (offset == 0)
        {
            for (int i = 0; i < k; i++)
                pieces[i] = ThreadProfilePiece.Line(corner[i], corner[i + 1]);
            return pieces;
        }

        // Per segment: unit direction and the OUTWARD unit normal (the material is
        // r <= R(z), so the normal with the positive radial component points out).
        var direction = new Vector2d[k];
        var normal = new Vector2d[k];
        for (int i = 0; i < k; i++)
        {
            var d = corner[i + 1] - corner[i];
            double length = d.Length;
            if (!(length > 0))
                throw new ArgumentException("A thread profile segment has no length.", nameof(pitchProfile));
            direction[i] = d / length;
            normal[i] = new Vector2d(d.Y, -d.X) / length;
        }

        var active = new List<int>(Enumerable.Range(0, k));
        Vector2d[] enter = [], exit = [];
        bool[] rounds = [];
        while (true)
        {
            int n = active.Count;
            if (n < 2)
                throw new ArgumentException(
                    $"A distance-field offset of {offset:g6} consumes the whole thread profile.", nameof(offset));
            (enter, exit, rounds) = (new Vector2d[n], new Vector2d[n], new bool[n]);
            for (int i = 0; i < n; i++)
            {
                int a = active[i], p = active[(i + n - 1) % n];
                // A junction sits on an ORIGINAL corner only while its two segments are
                // still neighbours; a merged one has no corner to centre an arc on, and by
                // construction it can only ever be a miter.
                bool onCorner = (p + 1) % k == a;
                double turn = direction[p].Cross(direction[a]);
                rounds[i] = onCorner && offset * turn > 0;
                if (rounds[i])
                {
                    enter[i] = corner[a] + normal[p] * offset;
                    exit[i] = corner[a] + normal[a] * offset;
                }
                else
                {
                    // The wrap junction's incoming segment is stated one pitch UP (it ends
                    // at corner[0] + pitch), so its line is brought down to the pitch the
                    // junction is being emitted in. Without it the closing miter lands a
                    // whole pitch above the profile and every segment reads as consumed.
                    var previous = corner[p] - new Vector2d(0, p > a ? pitch : 0);
                    if (!TryMiter(previous, normal[p], corner[a], normal[a], offset, out var apex))
                        throw new ArgumentException(
                            $"A distance-field offset of {offset:g6} leaves two parallel profile segments " +
                            "with no miter point.", nameof(offset));
                    enter[i] = exit[i] = apex;
                }
            }

            int collapse = -1;
            for (int i = 0; i < n; i++)
            {
                var span = enter[(i + 1) % n] - exit[i];
                // The wrapped last segment's own end sits one pitch up.
                if (i == n - 1)
                    span += new Vector2d(0, pitch);
                if (span.Dot(direction[active[i]]) <= 0)
                {
                    collapse = i;
                    break;
                }
            }
            if (collapse < 0)
                break;
            if (rounds[collapse] || rounds[(collapse + 1) % n])
                throw new ArgumentException(
                    $"A distance-field offset of {offset:g6} consumes profile segment {active[collapse]}, " +
                    "which is bounded by a rounded corner: the offset region is not locally convex there, " +
                    "so there is no miter to replace it with. Use a smaller clearance.", nameof(offset));
            active.RemoveAt(collapse);
        }

        int count = active.Count;
        var result = new List<ThreadProfilePiece>(2 * count);
        for (int i = 0; i < count; i++)
        {
            if (rounds[i])
                result.Add(ThreadProfilePiece.Arc(enter[i], exit[i], corner[active[i]]));
            // The closing line's far end is the FIRST junction one pitch up, written as
            // exactly that sum so MakeThreadedRod's wrap check is an equality rather than
            // a near miss.
            var to = i + 1 < count ? enter[i + 1] : new Vector2d(enter[0].X, enter[0].Y + pitch);
            result.Add(ThreadProfilePiece.Line(exit[i], to));
        }
        return result;
    }

    /// <summary>
    /// Where two offset lines meet: each keeps its own normal and gains
    /// <paramref name="offset"/> of signed distance, so the apex solves the 2×2 system
    /// <c>p·n = c·n + offset</c> for both. False when the two normals are parallel.
    /// </summary>
    private static bool TryMiter(
        in Vector2d pointA, in Vector2d normalA, in Vector2d pointB, in Vector2d normalB,
        double offset, out Vector2d apex)
    {
        double determinant = normalA.Cross(normalB);
        // Scale-free: the normals are unit, so their cross IS the sine of the turn.
        if (!(Math.Abs(determinant) > 1e-12))
        {
            apex = default;
            return false;
        }
        double ca = pointA.Dot(normalA) + offset;
        double cb = pointB.Dot(normalB) + offset;
        apex = new Vector2d(
            (ca * normalB.Y - cb * normalA.Y) / determinant,
            (cb * normalA.X - ca * normalB.X) / determinant);
        return true;
    }

    /// <summary>
    /// The solid a 45° lead-in chamfer REMOVES from a threaded rod at one end: the region
    /// outside a coaxial cone that reaches <paramref name="majorRadius"/> at
    /// <paramref name="chamferLength"/> from the end face and shrinks by the same amount
    /// per unit of axial travel toward it. Subtract it from
    /// <see cref="MakeThreadedRod"/>'s output; the chamfer is the standard bolt-point
    /// lead-in, and its cut on every band is EXACT (a conical
    /// <see cref="SpiralArc3d"/> — see <c>SurfaceIntersection</c>'s coaxial family).
    /// <para><b>Every face of the tool clears the rod except the cone</b>, which is the
    /// whole reason it is shaped this way rather than as the cone alone. The cone's
    /// generator is extended past both ends by a quarter of the chamfer, so the flat that
    /// bounds it sits at a radius strictly outside the rod and the flat that caps it sits
    /// axially outside the rod's end face — the tool-overshoot rule <c>Drill</c> follows,
    /// which is what keeps every intersecting pair transversal. (The overshoot is belt
    /// and braces rather than load-bearing: a coaxial annulus meeting a helical band is
    /// itself an exact cut now. It was not always, and a tool whose flat rim lies ON the
    /// crest cylinder is exactly the input that found that gap.)</para>
    /// <para>A chamfer at or beyond the thread depth puts the cone tangent to every root
    /// band along the end plane — coincident curved-surface boolean input, which is
    /// refused one layer up rather than here: this factory builds the tool for whatever
    /// chamfer it is given.</para>
    /// </summary>
    /// <param name="majorRadius">The rod's major radius; the cone reaches it exactly
    /// <paramref name="chamferLength"/> from the end face.</param>
    /// <param name="chamferLength">Axial (and, at 45°, radial) depth of the chamfer.</param>
    /// <param name="endAxial">Axial coordinate of the rod's end face in
    /// <paramref name="frame"/> (0 for the bottom cap, the rod's length for the top).</param>
    /// <param name="atMaxAxial">True when the rod's material lies BELOW
    /// <paramref name="endAxial"/> (the top end), false for the bottom end.</param>
    /// <param name="frame">The rod's own axis pose; defaults to the world frame.</param>
    public static BrepSolid MakeThreadEndChamferTool(
        double majorRadius, double chamferLength, double endAxial, bool atMaxAxial,
        Frame3d? frame = null) =>
        MakeThreadEndConeTool(majorRadius, chamferLength, chamferLength, endAxial, atMaxAxial, frame);

    /// <summary>
    /// The general coaxial END CONE of which a 45° chamfer is the equal-drop member, and
    /// which is what a thread RUNOUT is: the cone reaches <paramref name="majorRadius"/>
    /// exactly <paramref name="axialLength"/> from the end face and has dropped
    /// <paramref name="radialDrop"/> by the time it gets there, so its half-angle is
    /// <c>atan(radialDrop / axialLength)</c> rather than a fixed 45°.
    /// <para>Nothing about the cut changes with the angle — it is still a coaxial
    /// straight-generator surface of revolution, so its intersection with every helical
    /// band is the exact conical <see cref="SpiralArc3d"/> of
    /// <c>SurfaceIntersection</c>'s coaxial family. A shallow, long cone is therefore
    /// exactly as B-Rep-native as a short 45° one, which is the whole reason a runout
    /// needs no new machinery: the thread's crests are progressively truncated over the
    /// runout length, which is what an incomplete (washed-out) thread IS.</para>
    /// <para>The overshoot that keeps every OTHER face of the tool clear of the rod is
    /// taken as a quarter of each extent separately — a quarter of the axial length
    /// axially, a quarter of the radial drop radially — which is the same number twice at
    /// 45° and therefore leaves a chamfer tool bit-for-bit what it always was.</para>
    /// </summary>
    /// <param name="majorRadius">The rod's major radius; the cone reaches it exactly
    /// <paramref name="axialLength"/> from the end face.</param>
    /// <param name="radialDrop">How much smaller the cone is at the end face than at
    /// <paramref name="majorRadius"/>; must be less than the major radius.</param>
    /// <param name="axialLength">Axial distance over which that drop happens.</param>
    /// <param name="endAxial">Axial coordinate of the rod's end face in
    /// <paramref name="frame"/> (0 for the bottom cap, the rod's length for the top).</param>
    /// <param name="atMaxAxial">True when the rod's material lies BELOW
    /// <paramref name="endAxial"/> (the top end), false for the bottom end.</param>
    /// <param name="frame">The rod's own axis pose; defaults to the world frame.</param>
    public static BrepSolid MakeThreadEndConeTool(
        double majorRadius, double radialDrop, double axialLength, double endAxial,
        bool atMaxAxial, Frame3d? frame = null)
    {
        if (!(majorRadius > 0) || !double.IsFinite(majorRadius))
            throw new ArgumentOutOfRangeException(nameof(majorRadius));
        if (!(radialDrop > 0) || !double.IsFinite(radialDrop))
            throw new ArgumentOutOfRangeException(nameof(radialDrop));
        if (!(axialLength > 0) || !double.IsFinite(axialLength))
            throw new ArgumentOutOfRangeException(nameof(axialLength));
        if (!double.IsFinite(endAxial))
            throw new ArgumentOutOfRangeException(nameof(endAxial));
        double axialOver = axialLength / 4;
        double over = radialDrop / 4;
        if (!(majorRadius - radialDrop - over > 0))
            throw new ArgumentOutOfRangeException(nameof(radialDrop),
                "The end cone would reach the axis; the radial drop must be less than the major radius.");

        var f = frame ?? Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        // In (radius, axial): the cone runs from the wide end, one overshoot outside the
        // rod, to the narrow end, one overshoot past the end face. `sign` turns the top
        // end's geometry into the bottom end's by reflecting the axial direction.
        double sign = atMaxAxial ? 1 : -1;
        double wideAxial = endAxial - sign * (axialLength + axialOver);
        double narrowAxial = endAxial + sign * axialOver;
        double wideRadius = majorRadius + over;
        double narrowRadius = majorRadius - radialDrop - over;
        double outerRadius = majorRadius + radialDrop + 2 * over;

        Vector3d At(double radius, double axial) => f.Origin + f.X * radius + f.Z * axial;
        var profile = Profile.FromPoints(
        [
            At(wideRadius, wideAxial),
            At(outerRadius, wideAxial),
            At(outerRadius, narrowAxial),
            At(narrowRadius, narrowAxial),
        ]);
        return Revolve(profile, f.Origin, f.Z);
    }
}
