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
public static class DirectEdit
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
    /// <para><b>A curved face is refused by name.</b> Translating a cylinder moves its AXIS,
    /// and the rim reconstruction assumes an axis that stayed put (it rebuilds a rim as a
    /// circle concentric with the original's, which is exactly right for an offset and false
    /// for a move). Rather than let that hypothesis fail three stages downstream, the curved
    /// case is declined here with the reason.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The selector matched no face.</exception>
    /// <exception cref="NotSupportedException">A selected face is not planar.</exception>
    public static BrepSolid MoveFaces(BrepSolid solid, in Vector3d translation, Func<BrepFace, bool> selector)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(selector);
        var selected = Select(solid, selector, "move");

        var distances = new Dictionary<BrepFace, double>(selected.Count);
        foreach (var face in selected)
        {
            if (!face.IsPlanar(out _, out var normal) || !normal.TryNormalize(Tolerance.Default, out var unit))
                throw new NotSupportedException(
                    $"Moving a {face.Surface.GetType().Name} face is not supported: a translation moves a " +
                    "curved carrier's own axis, and the rim reconstruction rebuilds each edge as a circle " +
                    "concentric with the original — exactly right for an offset along the normal, and false " +
                    "for a translation. Use OffsetFaces for a curved face, or move the whole solid.");
            distances[face] = translation.Dot(unit);
        }
        return Shelling.Offset(solid, face => distances.TryGetValue(face, out double d) ? d : 0);
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

        // The heal: every wound edge must be a coedge of a complete HOLE loop of a kept face.
        var dropped = new HashSet<BrepLoop>();
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
                    throw new NotSupportedException(
                        $"Deleting these faces leaves a wound that runs only PART of the way round a " +
                        $"loop of a neighbouring {face.Surface.GetType().Name} face ({on} of " +
                        $"{loop.Coedges.Count} edges). Healing that needs the neighbours EXTENDED until " +
                        "they meet in a new edge, which is a different operation and can have no answer " +
                        "at all (a box's sides extended past a deleted top never meet). Delete a feature " +
                        "whose whole boundary is one interior loop of the face it stands on.");
                if (i == 0)
                    throw new NotSupportedException(
                        $"Deleting these faces would consume the OUTER boundary of a neighbouring " +
                        $"{face.Surface.GetType().Name} face, leaving it with no boundary at all. Include " +
                        "that face in the selection, or delete a feature bounded by an interior loop.");
                // A second loop is a HOLE only on a planar face. A plane is bounded by its outer
                // loop alone and triangulates from its loops, so dropping an interior one leaves
                // a face that still covers exactly the region it should. Every other family is
                // domain-driven or periodic, where a second loop is routinely the far END of a
                // band rather than a hole in it — a cylinder's two rings are the standard case,
                // and dropping one there opens the solid into an infinite tube that passes both
                // Validate() and Euler-Poincare, so nothing downstream would catch it.
                if (!face.IsPlanar(out _, out _))
                    throw new NotSupportedException(
                        $"Deleting these faces would drop an interior loop of a neighbouring " +
                        $"{face.Surface.GetType().Name} face, and a second loop on a non-planar face is " +
                        "not necessarily a hole — on a cylinder or an extruded band it is the far end of " +
                        "the band, and dropping it would leave the surface unbounded rather than fill a " +
                        "hole. Delete a feature that stands on a PLANAR face.");
                dropped.Add(loop);
                coveredEdges += loop.Coedges.Count(c => wound.Contains(c.Edge));
            }
        }
        if (coveredEdges != wound.Count)
            throw new NotSupportedException(
                $"{wound.Count - coveredEdges} of the {wound.Count} edges the deletion exposes lie on no " +
                "interior loop of a face that remains, so there is nothing to drop and the neighbours " +
                "would have to be extended to close the solid.");

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
