using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Direct editing: change a solid by acting on its FACES rather than by changing a parameter.
///
/// <para><b>This is what makes an imported body editable.</b> A solid read from STEP or IGES has
/// no construction history, so there is no parameter to change — the only handle on it is the
/// geometry itself. <see cref="OffsetFaces"/> pushes a face along its own normal,
/// <see cref="MoveFaces"/> translates one, and <see cref="DeleteFaces"/> removes a feature and
/// heals the wound. A history-backed model is better edited through its history; these exist for
/// the models that have none.</para>
///
/// <para><b>The machinery is <see cref="Shelling"/>'s, under a SELECTIVE law.</b> Offsetting one
/// face and offsetting every face are the same algorithm — lift each carrier, re-solve every
/// vertex against its new carriers, rebuild every edge — differing only in how far each carrier
/// moves. <see cref="Shelling"/> already took a per-face wall thickness for
/// <see cref="Shelling.Shell(BrepSolid, Func{BrepFace, double}, Func{BrepFace, bool})"/> and
/// already offset its openings by zero, so the whole of face offsetting is the matching per-face
/// overload on <see cref="Shelling.Offset(BrepSolid, Func{BrepFace, double})"/> plus the policy
/// this class states. Every refusal the corner machinery already makes is inherited by name —
/// carriers with no same-family offset, non-circular curved edges, over-determined vertices —
/// rather than restated here.</para>
///
/// <para><b>A move of a PLANAR face is an offset, exactly.</b> A plane is invariant under
/// translation within itself, so displacing a planar face by <c>v</c> lands on the same plane an
/// offset of <c>v·n̂</c> reaches: the two are one operation, and <see cref="MoveFaces"/> is
/// implemented as that reduction rather than beside it. What follows is worth stating because it
/// surprises: moving a face SIDEWAYS does nothing at all (<c>v·n̂ = 0</c>), because a plane slid
/// along itself is the same plane. A CURVED face is genuinely different — translating a cylinder
/// moves its axis, which no offset can do — and is refused by name (see the remarks on
/// <see cref="MoveFaces"/>).</para>
/// </summary>
public static partial class DirectEdit
{
    /// <summary>
    /// Pushes the selected faces along their own outward normals by
    /// <paramref name="distance"/> (positive grows the solid) and re-solves every corner the
    /// move disturbs. Faces the selector does not name stay exactly where they are.
    /// </summary>
    /// <remarks>
    /// <para>The identity worth knowing: where every face adjoining the moved one is PARALLEL
    /// to its normal — a box's top face against its four sides — the moved face's boundary
    /// slides without changing shape, so the volume changes by exactly <c>area × distance</c>.
    /// Where a neighbour is oblique the boundary grows or shrinks as it slides and the change
    /// is the integral of the moving face's area, which is the ordinary frustum answer.</para>
    /// <para>All-planar solids take <see cref="Shelling"/>'s polyhedral path (three-plane
    /// corner solves, exact); a solid with any curved face takes the carrier path, where a
    /// cylinder offsets to a cylinder, a cone to a cone and a torus to a torus, and every rim
    /// is CONSTRUCTED then verified against both new carriers.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The selector matched no face.</exception>
    public static BrepSolid OffsetFaces(BrepSolid solid, double distance, Func<BrepFace, bool> selector)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(selector);
        var selected = Select(solid, selector, "offset");
        return Shelling.Offset(solid, face => selected.Contains(face) ? distance : 0);
    }

    /// <summary>
    /// Translates the selected PLANAR faces by <paramref name="translation"/> and re-solves
    /// every corner the move disturbs.
    /// </summary>
    /// <remarks>
    /// <para><b>This is <see cref="OffsetFaces"/> under another name, and deliberately so.</b>
    /// The plane a face lies in is unchanged by any translation within itself, so the plane
    /// reached by displacing it by <c>v</c> is exactly the plane reached by offsetting it by
    /// <c>v·n̂</c>; each selected face therefore takes its own projected distance and the
    /// incumbent offset path does the work. Two consequences follow rather than being
    /// arranged: a face moved parallel to itself does not move at all, and moving several
    /// faces by one vector moves each by a different amount.</para>
    /// <para><b>A CURVED face genuinely moves, and that is a different rebuild.</b> A
    /// translation moves a cylinder's AXIS, so the rim it shares with a neighbour is a circle
    /// about the new axis rather than about the old one — which is why this path asks
    /// <see cref="CarrierBody"/> to take every rebuilt rim's axis and phase from the NEW
    /// carrier instead of from a fit of the old edge. (The offset family never needed that:
    /// <see cref="SurfaceOffset"/> carries a frame over verbatim, so there the two sources are
    /// the same axis.) A mixed selection takes the same route; an all-planar one keeps the
    /// reduction above, so every move that worked before is unchanged.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The selector matched no face.</exception>
    /// <exception cref="NotSupportedException">
    /// A selected curved face's carrier has no rigid image, or a rebuilt corner or rim cannot
    /// be solved exactly.
    /// </exception>
    public static BrepSolid MoveFaces(BrepSolid solid, in Vector3d translation, Func<BrepFace, bool> selector)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(selector);
        var selected = Select(solid, selector, "move");

        var distances = new Dictionary<BrepFace, double>(selected.Count);
        foreach (var face in selected)
        {
            if (!face.IsPlanar(out _, out var normal) || !normal.TryNormalize(Tolerance.Default, out var unit))
                return Retarget(solid, selected, Matrix4d.CreateTranslation(translation), "move");
            distances[face] = translation.Dot(unit);
        }
        return Shelling.Offset(solid, face => distances.TryGetValue(face, out double d) ? d : 0);
    }

    /// <summary>
    /// Turns the selected faces about <paramref name="axis"/> by
    /// <paramref name="degrees"/> and re-solves every corner the turn disturbs — a draft
    /// angle put on an imported body, or a boss leaned over.
    /// </summary>
    /// <remarks>
    /// <para>This is <see cref="Draft"/> with an arbitrary hinge rather than a neutral plane's
    /// own line, and it fell out of the move: both are a RIGID image of the selected carriers
    /// with everything else held still, so one rebuild serves them. A face the axis lies IN is
    /// tilted about that line and keeps the points on it; a face the axis misses is swung
    /// bodily, which is legal and usually not what a drafting caller means — state the axis in
    /// the face if you want a hinge.</para>
    /// <para>The rotation is applied to the carrier, so a rotated plane is a plane and a
    /// rotated cylinder a cylinder about the turned axis: the result is exact, not fitted.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The selector matched no face, or the axis is degenerate.</exception>
    public static BrepSolid RotateFaces(
        BrepSolid solid, in Ray3d axis, double degrees, Func<BrepFace, bool> selector)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(selector);
        if (!axis.Direction.TryNormalize(Tolerance.Default, out var direction))
            throw new ArgumentException("The rotation axis has no direction.", nameof(axis));
        var selected = Select(solid, selector, "rotate");
        // Hinge about the axis LINE: translate its origin to zero, turn, translate back.
        var turn = Matrix4d.CreateTranslation(axis.Origin)
            * Matrix4d.CreateFromAxisAngle(direction, degrees * Math.PI / 180)
            * Matrix4d.CreateTranslation(-axis.Origin);
        return Retarget(solid, selected, turn, "rotation");
    }

    /// <summary>
    /// Swaps the selected faces' SURFACES for new carriers and re-solves the corners — OCCT's
    /// <c>BRepTools_ReShape</c> on a face: a flat boss top becomes a dome, a straight bore a
    /// taper, with the topology carried over verbatim.
    /// </summary>
    /// <remarks>
    /// <para><b>The geometry was already here; the question was what makes a swap SOUND.</b>
    /// <see cref="CarrierBody"/> already rebuilds a whole body from one carrier per face, so
    /// this is that call with the caller's surfaces in place of offset ones. Three things are
    /// checked, and each is the answer to a way a plausible-looking swap is wrong.</para>
    /// <para><b>(a) The new carrier must face the same way.</b> A cone whose normal runs
    /// inward would turn the solid inside out while leaving every count, every loop and
    /// <see cref="BrepSolid.Validate"/> perfectly happy, so the replacement's outward normal is
    /// measured against the original's at the face's own centre and an opposed one is refused
    /// by name. <b>(b) Every corner must still meet</b>, which the corner solve reports as a
    /// residual and refuses when it does not converge. <b>(c) Every rebuilt rim must lie on
    /// BOTH its carriers</b> — the construct-then-verify rule, so a rim the new surface does
    /// not actually carry is named rather than approximated.</para>
    /// <para>Returning null (or the face's own surface) keeps that face exactly as it was.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The replacement named no face.</exception>
    public static BrepSolid ReplaceFaceSurfaces(BrepSolid solid, Func<BrepFace, Surface?> replacement)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(replacement);
        var body = CarrierBody.Recognize(solid);
        var carriers = new Surface[body.Faces.Length];
        int replaced = 0;
        for (int f = 0; f < carriers.Length; f++)
        {
            var face = body.Faces[f];
            var surface = replacement(face) ?? face.Surface;
            carriers[f] = surface;
            if (ReferenceEquals(surface, face.Surface))
                continue;
            replaced++;
            RequireSameSide(face, surface);
        }
        if (replaced == 0)
            throw new ArgumentException(
                $"The replacement returned no new surface for any of the solid's {carriers.Length} faces, " +
                "so there is nothing to replace.", nameof(replacement));
        return body.Rebuild(carriers, "surface replacement", rimFromCarriers: true);
    }

    /// <summary>
    /// The orientation gate: a replacement whose outward normal opposes the original's turns
    /// the solid inside out, and NOTHING downstream can see it — the loops, the counts and
    /// Euler–Poincaré are all unchanged. Measured at the face's own centre, projected onto the
    /// replacement, so it is a statement about the surfaces rather than about their frames.
    /// </summary>
    private static void RequireSameSide(BrepFace face, Surface replacement)
    {
        var centre = face.Bounds().Center;
        if (!face.Surface.TryProjectPoint(centre, out var uv)
            || !replacement.TryProjectPoint(centre, out var newUv, 1e-3))
            return;
        var was = face.Surface.NormalAt(uv.X, uv.Y);
        var now = replacement.NormalAt(newUv.X, newUv.Y);
        if (!was.TryNormalize(Tolerance.Default, out var wasUnit)
            || !now.TryNormalize(Tolerance.Default, out var nowUnit))
            return;
        if (wasUnit.Dot(nowUnit) < 0)
            throw new NotSupportedException(
                $"The {replacement.GetType().Name} replacing a {face.Surface.GetType().Name} face faces the " +
                "opposite way, which would turn the solid inside out while leaving every loop, count and " +
                "Euler number unchanged. Reverse the replacement's own frame (swap its two in-plane " +
                "directions) so its normal points out of the material.");
    }

    /// <summary>
    /// The shared rebuild for a RIGID image of the selected carriers: one carrier per face,
    /// the selected ones transformed and the rest kept VERBATIM, handed to
    /// <see cref="CarrierBody"/> to re-solve every corner and rim. Reversed faces are admitted
    /// because an isometry says nothing about a face's sense — unlike an offset, whose
    /// direction is the outward normal.
    /// </summary>
    private static BrepSolid Retarget(
        BrepSolid solid, HashSet<BrepFace> selected, in Matrix4d transform, string what)
    {
        var body = CarrierBody.Recognize(solid);
        var carriers = new Surface[body.Faces.Length];
        for (int f = 0; f < carriers.Length; f++)
        {
            var face = body.Faces[f];
            carriers[f] = selected.Contains(face)
                ? GeometryTransform.Apply(face.Surface, transform)
                : face.Surface;
        }
        return body.Rebuild(carriers, what, rimFromCarriers: true);
    }

    /// <summary>
    /// Removes the selected faces and heals the wound by dropping the boundary they left in
    /// their neighbours — the way a boss, a pad or a pocket liner is taken off an imported
    /// body.
    /// </summary>
    /// <remarks>
    /// <para><b>What v1 heals, stated as a condition rather than as a list of shapes.</b> Call
    /// an edge <i>wound</i> when one of its two faces is deleted and the other is kept. The
    /// deletion heals by DROPPING loops exactly when every wound edge lies on a complete
    /// interior loop of a kept PLANAR face: the neighbours then already close without it, and
    /// the repair is to stop referencing it. That covers a boss (its cylinder and cap leave a
    /// circular hole loop in the face they stand on), a pad, a pocket (its walls and floor
    /// leave the hole they were cut through) and a counterbore's step — the features a reader
    /// of an imported model most often wants gone.</para>
    /// <para><b>The planar clause is the correctness condition, not a convenience.</b> A plane
    /// is bounded by its outer loop alone, so an interior loop really is a hole and dropping it
    /// leaves the face covering exactly the right region. On a cylinder or an extruded band a
    /// second loop is routinely the far END of the band, and dropping it leaves the surface
    /// unbounded — an open tube that satisfies both <see cref="BrepSolid.Validate"/> and
    /// Euler–Poincaré, so no downstream check would catch it.</para>
    /// <para><b>What it refuses, and why the refusal is honest rather than lazy.</b> A wound
    /// that only PARTLY bounds a kept loop — deleting a chamfer band, whose two neighbours must
    /// be EXTENDED until they meet each other in a new edge — is a different operation: it
    /// needs a new curve solved between surfaces that do not currently touch, and it can fail
    /// outright (a box's four sides extended past its deleted top never meet). That case is
    /// named, not attempted. Dropping a face's OUTER loop is refused for the same reason: the
    /// face would have no boundary left.</para>
    /// <para>Geometry is shared and topology rebuilt, so the input solid is left untouched and
    /// every surviving face keeps its own surface object — which is what lets a deletion
    /// restore the body a feature was added to rather than merely resemble it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The selector matched no face, or every face.</exception>
    /// <exception cref="NotSupportedException">The wound cannot be healed by dropping loops.</exception>
    public static BrepSolid DeleteFaces(BrepSolid solid, Func<BrepFace, bool> selector)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(selector);
        if (solid.Shells.Count != 1)
            throw new NotSupportedException(
                $"Deleting faces needs a single-shell solid; this one has {solid.Shells.Count} shells.");
        solid.Validate();

        var faces = solid.Faces.ToArray();
        var deleted = Select(solid, selector, "delete");
        if (deleted.Count == faces.Length)
            throw new ArgumentException(
                "Every face was selected for deletion; nothing would be left.", nameof(selector));

        // Which faces use each edge, in first-use order so a refusal names things
        // deterministically.
        var users = new Dictionary<BrepEdge, List<BrepFace>>();
        foreach (var coedge in solid.Coedges)
        {
            if (!users.TryGetValue(coedge.Edge, out var list))
                users[coedge.Edge] = list = [];
            if (!list.Contains(coedge.Loop.Face))
                list.Add(coedge.Loop.Face);
        }

        // A WOUND edge separates a deleted face from a kept one; an edge interior to the
        // deleted set simply goes, and one interior to the kept set is untouched.
        var wound = new HashSet<BrepEdge>();
        foreach (var (edge, list) in users)
        {
            int removed = list.Count(deleted.Contains);
            if (removed > 0 && removed < list.Count)
                wound.Add(edge);
        }
        if (wound.Count == 0)
            throw new NotSupportedException(
                "The selected faces touch none of the faces that would remain, so deleting them would " +
                "leave a separate body rather than heal a wound. Delete a feature that sits on the rest " +
                "of the solid.");

        // TWO heals, tried in that order, and the order is the compatibility guarantee: a
        // wound that DROPS closes without moving a single coordinate, so anything the v1 rule
        // covered still takes it and comes back bit for bit. Only what it cannot close reaches
        // the extension, which does move geometry.
        if (!TryDropLoops(faces, deleted, wound, out var dropped, out string? dropReason))
        {
            if (TryExtendNeighbours(solid, faces, deleted, users, wound, out var extended, out string? extendReason))
                return extended;
            throw new NotSupportedException(
                $"Deleting these faces leaves a wound neither heal can close. Dropping loops: {dropReason}. " +
                $"Extending the neighbours until they meet: {extendReason}.");
        }

        // Fresh topology over the SAME curves and surfaces: the input is left untouched, and a
        // surviving face keeps the very surface it had.
        var vertices = new Dictionary<BrepVertex, BrepVertex>();
        var edges = new Dictionary<BrepEdge, BrepEdge>();
        BrepVertex Vertex(BrepVertex vertex) =>
            vertices.TryGetValue(vertex, out var copy) ? copy : vertices[vertex] = new BrepVertex(vertex.Position);
        BrepEdge Edge(BrepEdge edge) =>
            edges.TryGetValue(edge, out var copy)
                ? copy
                : edges[edge] = new BrepEdge(
                    edge.Curve, edge.Domain, Vertex(edge.StartVertex), Vertex(edge.EndVertex));

        var kept = new List<BrepFace>(faces.Length - deleted.Count);
        foreach (var face in faces)
        {
            if (deleted.Contains(face))
                continue;
            var loops = new List<BrepLoop>(face.Loops.Count);
            foreach (var loop in face.Loops)
            {
                if (dropped.Contains(loop))
                    continue;
                var coedges = new List<BrepCoedge>(loop.Coedges.Count);
                foreach (var coedge in loop.Coedges)
                    coedges.Add(new BrepCoedge(Edge(coedge.Edge), coedge.SameSense));
                loops.Add(new BrepLoop(coedges));
            }
            // Face f out is face f in with fewer loops, so it inherits f's provenance: a tag
            // naming the plate still names the plate after the boss on it has gone.
            kept.Add(new BrepFace(face.Surface, loops, face.IsReversed).DescendsFrom(face));
        }

        var healed = new BrepSolid([new BrepShell(kept)]);
        healed.Validate();
        return healed;
    }

    /// <summary>
    /// The v1 heal, reported rather than thrown: every wound edge must be a coedge of a
    /// complete interior loop of a kept PLANAR face, and those loops are then dropped.
    ///
    /// <para>The planar clause is the correctness condition. A plane is bounded by its outer
    /// loop alone and triangulates from its loops, so dropping an interior one leaves a face
    /// that still covers exactly the region it should. Every other family is domain-driven or
    /// periodic, where a second loop is routinely the far END of a band rather than a hole in
    /// it — a cylinder's two rings are the standard case, and dropping one there opens the
    /// solid into an infinite tube that passes both <see cref="BrepSolid.Validate"/> and
    /// Euler–Poincaré, so nothing downstream would catch it.</para>
    /// </summary>
    private static bool TryDropLoops(
        BrepFace[] faces, HashSet<BrepFace> deleted, HashSet<BrepEdge> wound,
        out HashSet<BrepLoop> dropped, out string? reason)
    {
        dropped = [];
        reason = null;
        int coveredEdges = 0;
        foreach (var face in faces)
        {
            if (deleted.Contains(face))
                continue;
            for (int i = 0; i < face.Loops.Count; i++)
            {
                var loop = face.Loops[i];
                int on = loop.Coedges.Count(c => wound.Contains(c.Edge));
                if (on == 0)
                    continue;
                if (on < loop.Coedges.Count)
                {
                    reason = $"the wound runs only PART of the way round a loop of a neighbouring " +
                             $"{face.Surface.GetType().Name} face ({on} of {loop.Coedges.Count} edges), so " +
                             "there is no whole loop to stop referencing";
                    return false;
                }
                if (i == 0)
                {
                    reason = $"it would consume the OUTER boundary of a neighbouring " +
                             $"{face.Surface.GetType().Name} face, leaving it with no boundary at all";
                    return false;
                }
                if (!face.IsPlanar(out _, out _))
                {
                    reason = $"a second loop on the neighbouring {face.Surface.GetType().Name} face is not " +
                             "necessarily a hole — on a cylinder or an extruded band it is the far end of " +
                             "the band, and dropping it would leave the surface unbounded (a PLANAR " +
                             "neighbour is what makes an interior loop a hole)";
                    return false;
                }
                dropped.Add(loop);
                coveredEdges += on;
            }
        }
        if (coveredEdges != wound.Count)
        {
            reason = $"{wound.Count - coveredEdges} of the {wound.Count} exposed edges lie on no interior " +
                     "loop of a face that remains, so there is nothing to drop";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The faces a selector names, refused by name when it names none — the rim-surgery rule
    /// that a selection failure must be reported before any geometry moves.
    /// </summary>
    private static HashSet<BrepFace> Select(BrepSolid solid, Func<BrepFace, bool> selector, string what)
    {
        var selected = new HashSet<BrepFace>();
        foreach (var face in solid.Faces)
        {
            if (selector(face))
                selected.Add(face);
        }
        if (selected.Count == 0)
            throw new ArgumentException(
                $"The face selector matched none of the solid's {solid.Faces.Count()} faces, so there is " +
                $"nothing to {what}.", nameof(selector));
        return selected;
    }
}
