using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Draft angles (OCCT's <c>BRepOffsetAPI_DraftAngle</c>): the moulding/casting operation
/// that tapers selected faces about a <b>neutral plane</b> so the part releases from its
/// mould.
///
/// <para><b>What is exact.</b> Each selected face's plane is <i>rotated about its neutral
/// line</i> — the line where it meets the neutral plane — by the draft angle, toward the
/// pull direction; every corner is then the exact algebraic intersection of three planes.
/// Nothing is offset, projected or fitted, so a drafted box is exactly a frustum and its
/// faces are exactly at the requested angle to the pull direction. Faces the selector does
/// not name keep their planes exactly; their corners still move, because the drafted
/// neighbours they meet did.</para>
///
/// <para><b>What is rejected.</b> v1 handles <b>planar-faced prisms</b>: a solid with two
/// cap faces perpendicular to the pull direction, single-loop caps (no holes), and
/// four-sided planar side faces between them. Anything else — curved faces, holes, drafting
/// a cap, a taper so large the profile folds — throws with a message naming the problem
/// rather than silently approximating. The general case (face offset-and-reintersect
/// against curved neighbours) is future work; for organic parts the implicit route
/// (<c>Shape.ToImplicit</c>) remains available.</para>
/// </summary>
public static class Draft
{
    // Face classification tolerance (the scale PlanarFacesWithNormal uses), NOT a weld
    // tolerance: it decides which faces are caps and which are sides.
    private const double NormalClassificationTolerance = 1e-6;

    /// <summary>
    /// Tapers <paramref name="faceSelector"/>'s faces by <paramref name="angle"/> radians
    /// about the plane through <paramref name="neutralOrigin"/> perpendicular to
    /// <paramref name="pullDirection"/> (the mould-opening direction).
    ///
    /// <para>A positive angle narrows the solid as it moves along the pull direction — the
    /// classic release taper. A negative angle widens it. Geometry on the neutral plane
    /// does not move at all, which is what makes the neutral plane the parting line.</para>
    /// </summary>
    /// <param name="solid">A planar-faced prism about the pull direction.</param>
    /// <param name="neutralOrigin">Any point of the neutral plane.</param>
    /// <param name="pullDirection">The mould-opening direction (need not be unit).</param>
    /// <param name="angle">Draft angle in radians, |angle| &lt; π/2.</param>
    /// <param name="faceSelector">Which side faces to taper; null drafts them all.</param>
    public static BrepSolid Apply(
        BrepSolid solid,
        in Vector3d neutralOrigin,
        in Vector3d pullDirection,
        double angle,
        Func<BrepFace, bool>? faceSelector = null)
    {
        ArgumentNullException.ThrowIfNull(solid);
        if (Math.Abs(angle) >= Math.PI / 2 - 1e-9)
            throw new ArgumentOutOfRangeException(nameof(angle), "The draft angle must be less than 90 degrees.");
        if (!pullDirection.TryNormalize(Tolerance.Default, out var pull))
            throw new ArgumentException("The pull direction must be non-zero.", nameof(pullDirection));

        var prism = Prism.Recognize(solid, pull);
        var origin = neutralOrigin;

        // A selected cap is always an error, never a silent no-op: caps are the parting
        // faces and cannot be tapered, so naming one means the selector is wrong.
        if (faceSelector is not null && (faceSelector(prism.BaseCap) || faceSelector(prism.TopCap)))
            throw new ArgumentException(
                "Draft selected a cap face (one perpendicular to the pull direction). Caps are the " +
                "parting faces and stay put; select the side faces to taper.", nameof(faceSelector));

        var planes = new (Vector3d Origin, Vector3d Normal)[prism.SideFaces.Length];
        bool anyDrafted = false;
        for (int i = 0; i < planes.Length; i++)
        {
            var face = prism.SideFaces[i];
            face.IsPlanar(out var faceOrigin, out var faceNormal);
            if (faceSelector is not null && !faceSelector(face))
            {
                planes[i] = (faceOrigin, faceNormal);
                continue;
            }
            planes[i] = TaperPlane(faceOrigin, faceNormal, origin, pull, angle);
            anyDrafted = true;
        }

        if (faceSelector is not null && !anyDrafted)
            throw new ArgumentException("Draft selected no side face of the solid.", nameof(faceSelector));

        int n = planes.Length;
        var baseCorners = new Vector3d[n];
        var topCorners = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            // Corner i separates side face i − 1 from side face i.
            var previous = planes[(i + n - 1) % n];
            var current = planes[i];
            baseCorners[i] = IntersectPlanes(previous, current, prism.BasePlane, i);
            topCorners[i] = IntersectPlanes(previous, current, prism.TopPlane, i);
        }

        ValidateProfile(baseCorners, prism.TopCorners, pull, angle, "base");
        ValidateProfile(topCorners, prism.TopCorners, pull, angle, "top");
        return BuildPrism(baseCorners, topCorners, planes, prism.BasePlane, prism.TopPlane);
    }

    /// <summary>
    /// Tapers about the plane of <paramref name="neutralFace"/>, pulling <b>into</b> the
    /// solid (away from that face's outward normal), so drafting about the bottom face of a
    /// box narrows it going up — the intuitive mould-release sense.
    /// </summary>
    public static BrepSolid Apply(
        BrepSolid solid, BrepFace neutralFace, double angle, Func<BrepFace, bool>? faceSelector = null)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(neutralFace);
        if (!neutralFace.IsPlanar(out var origin, out var normal))
            throw new ArgumentException("The neutral face must be planar.", nameof(neutralFace));

        // Pull points from the neutral plane toward the bulk of the solid. For a boundary
        // cap that is simply the inward direction; for a face lying on a mid-plane it picks
        // the half that narrows (the other half widens, as a split mould does).
        var centroid = Vector3d.Zero;
        int count = 0;
        foreach (var vertex in solid.Vertices)
        {
            centroid += vertex.Position;
            count++;
        }
        centroid /= count;
        double side = (centroid - origin).Dot(normal);
        // Exact-zero semantic test: a neutral plane through the centroid has no "inward".
        if (side == 0)
            throw new ArgumentException(
                "The neutral face's plane passes through the solid's centroid, so the pull direction is " +
                "ambiguous; call the overload that takes an explicit pull direction.", nameof(neutralFace));
        return Apply(solid, origin, side > 0 ? normal : -normal, angle, faceSelector);
    }

    /// <summary>
    /// The face plane rotated about its neutral line by <paramref name="angle"/>, toward the
    /// pull direction. Both the rotated normal and the anchor point are closed-form: the
    /// rotation is in the plane spanned by the face normal and the pull direction (so the
    /// neutral line — perpendicular to both normals — is the rotation axis and stays fixed),
    /// and the anchor slides along the face's own steepest-ascent direction until it reaches
    /// the neutral plane. No projection, no iteration.
    /// </summary>
    private static (Vector3d Origin, Vector3d Normal) TaperPlane(
        in Vector3d faceOrigin, in Vector3d faceNormal, in Vector3d neutralOrigin, in Vector3d pull, double angle)
    {
        var ascent = pull - faceNormal * faceNormal.Dot(pull);
        if (!ascent.TryNormalize(Tolerance.Default, out var direction))
            throw new ArgumentException("A drafted face is perpendicular to the pull direction (it is a cap).");

        var normal = faceNormal * Math.Cos(angle) + direction * Math.Sin(angle);
        // Slide along `direction` (which lies in the face plane) onto the neutral plane:
        // the result is on BOTH the original face plane and the neutral plane, i.e. on the
        // neutral line, which the rotation leaves fixed.
        double travel = -(faceOrigin - neutralOrigin).Dot(pull) / direction.Dot(pull);
        return (faceOrigin + direction * travel, normal);
    }

    private static Vector3d IntersectPlanes(
        in (Vector3d Origin, Vector3d Normal) a,
        in (Vector3d Origin, Vector3d Normal) b,
        in (Vector3d Origin, Vector3d Normal) c,
        int corner)
    {
        var bc = b.Normal.Cross(c.Normal);
        double determinant = a.Normal.Dot(bc);
        // The normals are unit, so the determinant is a product of sines: a scale-free
        // near-degeneracy test for "these three planes do not meet in a point".
        if (Math.Abs(determinant) < 1e-12)
            throw new NotSupportedException(
                $"The faces meeting at corner {corner} are parallel or tangent after drafting, so the " +
                "corner is not a point. Draft smaller, or split the tangent junction first.");
        return (bc * a.Normal.Dot(a.Origin)
              + c.Normal.Cross(a.Normal) * b.Normal.Dot(b.Origin)
              + a.Normal.Cross(b.Normal) * c.Normal.Dot(c.Origin)) / determinant;
    }

    /// <summary>
    /// Rejects a taper that has folded the profile: the loop must keep its winding about
    /// the pull direction and every edge must still run the way it did.
    /// </summary>
    private static void ValidateProfile(
        Vector3d[] corners, Vector3d[] original, in Vector3d pull, double angle, string which)
    {
        int n = corners.Length;
        double area = 0;
        for (int i = 0; i < n; i++)
            area += corners[i].Cross(corners[(i + 1) % n]).Dot(pull);
        if (!(area > 0))
            throw new ArgumentException(
                $"A draft of {angle * 180 / Math.PI:0.###} degrees turns the {which} profile inside out; " +
                "the taper exceeds what the solid's height allows.", nameof(angle));

        for (int i = 0; i < n; i++)
        {
            var edge = corners[(i + 1) % n] - corners[i];
            var reference = original[(i + 1) % n] - original[i];
            if (edge.Dot(reference) <= 0)
                throw new ArgumentException(
                    $"A draft of {angle * 180 / Math.PI:0.###} degrees collapses edge {i} of the {which} " +
                    "profile; the taper exceeds what the solid's height allows.", nameof(angle));
        }
    }

    /// <summary>
    /// Rebuilds the prism from its new corner points, keeping every face an exact
    /// <see cref="PlaneSurface"/> — so the result is selectable by the same
    /// <see cref="BrepQueries"/> vocabulary (draft twice, then fillet) and STEP-exportable,
    /// which a ruled-loft rebuild would not be.
    /// </summary>
    private static BrepSolid BuildPrism(
        Vector3d[] baseCorners, Vector3d[] topCorners,
        (Vector3d Origin, Vector3d Normal)[] sidePlanes,
        (Vector3d Origin, Vector3d Normal) basePlane,
        (Vector3d Origin, Vector3d Normal) topPlane)
    {
        int n = baseCorners.Length;
        var baseVertices = new BrepVertex[n];
        var topVertices = new BrepVertex[n];
        for (int i = 0; i < n; i++)
        {
            baseVertices[i] = new BrepVertex(baseCorners[i]);
            topVertices[i] = new BrepVertex(topCorners[i]);
        }

        var baseEdges = new BrepEdge[n];
        var topEdges = new BrepEdge[n];
        var rails = new BrepEdge[n];
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            baseEdges[i] = new BrepEdge(
                new Line3d(baseCorners[i], baseCorners[next]), Interval.Unit, baseVertices[i], baseVertices[next]);
            topEdges[i] = new BrepEdge(
                new Line3d(topCorners[i], topCorners[next]), Interval.Unit, topVertices[i], topVertices[next]);
            rails[i] = new BrepEdge(
                new Line3d(baseCorners[i], topCorners[i]), Interval.Unit, baseVertices[i], topVertices[i]);
        }

        var faces = new List<BrepFace>(n + 2);
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            // Plane axes: X along the base edge, Y completing the outward frame, so
            // X x Y is exactly the drafted normal.
            var x = (baseCorners[next] - baseCorners[i]).Normalized();
            var normal = sidePlanes[i].Normal;
            faces.Add(new BrepFace(
                new PlaneSurface(baseCorners[i], x, normal.Cross(x)),
                [
                    new BrepLoop(
                    [
                        new BrepCoedge(baseEdges[i], sameSense: true),
                        new BrepCoedge(rails[next], sameSense: true),
                        new BrepCoedge(topEdges[i], sameSense: false),
                        new BrepCoedge(rails[i], sameSense: false),
                    ]),
                ]));
        }

        var baseLoop = new List<BrepCoedge>(n);
        for (int i = n - 1; i >= 0; i--)
            baseLoop.Add(new BrepCoedge(baseEdges[i], sameSense: false));
        var topLoop = new List<BrepCoedge>(n);
        for (int i = 0; i < n; i++)
            topLoop.Add(new BrepCoedge(topEdges[i], sameSense: true));

        var baseX = (baseCorners[1] - baseCorners[0]).Normalized();
        var topX = (topCorners[1] - topCorners[0]).Normalized();
        faces.Add(new BrepFace(
            new PlaneSurface(basePlane.Origin, baseX, basePlane.Normal.Cross(baseX)), [new BrepLoop(baseLoop)]));
        faces.Add(new BrepFace(
            new PlaneSurface(topPlane.Origin, topX, topPlane.Normal.Cross(topX)), [new BrepLoop(topLoop)]));
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// The prismatic structure draft needs: two caps perpendicular to the pull direction and
    /// a ring of four-sided planar side faces between them, recovered from the topology (so
    /// the side-face order is the solid's own, not a sort).
    /// </summary>
    private readonly struct Prism
    {
        public required BrepFace BaseCap { get; init; }
        public required BrepFace TopCap { get; init; }
        public required (Vector3d Origin, Vector3d Normal) BasePlane { get; init; }
        public required (Vector3d Origin, Vector3d Normal) TopPlane { get; init; }

        /// <summary>Side faces in the top cap's loop order; face i spans corner i to i + 1.</summary>
        public required BrepFace[] SideFaces { get; init; }

        /// <summary>The original top-loop corners, in the same order.</summary>
        public required Vector3d[] TopCorners { get; init; }

        public static Prism Recognize(BrepSolid solid, in Vector3d pull)
        {
            if (solid.Shells.Count != 1)
                throw new NotSupportedException(
                    $"Draft needs a single-shell solid; this one has {solid.Shells.Count} shells.");

            BrepFace? baseCap = null, topCap = null;
            (Vector3d Origin, Vector3d Normal) basePlane = default, topPlane = default;
            var sides = new List<BrepFace>();
            foreach (var face in solid.Faces)
            {
                if (!face.IsPlanar(out var origin, out var normal))
                    throw new NotSupportedException(
                        $"Draft needs an all-planar solid; face on {face.Surface.GetType().Name} is not planar. " +
                        "Curved-face draft (offset and re-intersect) is not implemented.");
                double alignment = normal.Dot(pull);
                if (Math.Abs(alignment) > 1 - NormalClassificationTolerance)
                {
                    if (alignment > 0)
                    {
                        if (topCap is not null)
                            throw new NotSupportedException(
                                "Draft found more than one face facing along the pull direction; the solid " +
                                "must be a prism with exactly two caps.");
                        topCap = face;
                        topPlane = (origin, normal);
                    }
                    else
                    {
                        if (baseCap is not null)
                            throw new NotSupportedException(
                                "Draft found more than one face facing against the pull direction; the solid " +
                                "must be a prism with exactly two caps.");
                        baseCap = face;
                        basePlane = (origin, normal);
                    }
                    continue;
                }
                sides.Add(face);
            }
            if (baseCap is null || topCap is null)
                throw new NotSupportedException(
                    "Draft needs two cap faces perpendicular to the pull direction (the parting faces).");
            if (baseCap.Loops.Count != 1 || topCap.Loops.Count != 1)
                throw new NotSupportedException(
                    "Draft v1 does not support caps with holes; the taper would have to be applied to the " +
                    "hole walls too, which needs a multi-loop rebuild.");

            var loop = topCap.OuterLoop;
            int n = loop.Coedges.Count;
            if (n != sides.Count || n < 3)
                throw new NotSupportedException(
                    $"Draft needs one side face per cap edge; the top cap has {n} edges but the solid has " +
                    $"{sides.Count} side faces.");
            if (baseCap.OuterLoop.Coedges.Count != n)
                throw new NotSupportedException("Draft needs the two caps to have matching edge counts.");

            var sideFaces = new BrepFace[n];
            var corners = new Vector3d[n];
            var seen = new HashSet<BrepFace>();
            for (int i = 0; i < n; i++)
            {
                var coedge = loop.Coedges[i];
                corners[i] = coedge.StartVertex.Position;
                var partner = coedge.Partner
                    ?? throw new NotSupportedException("Draft needs a closed manifold solid (an edge has one use).");
                var face = partner.Loop.Face;
                if (!seen.Add(face))
                    throw new NotSupportedException(
                        "Draft needs each side face to touch the top cap exactly once (a face used twice is " +
                        "not a simple prism side).");
                if (face.Loops.Count != 1 || face.OuterLoop.Coedges.Count != 4)
                    throw new NotSupportedException(
                        "Draft needs four-sided single-loop side faces (base edge, two rails, top edge); " +
                        $"one side face has {face.Loops.Count} loop(s) and {face.OuterLoop.Coedges.Count} edges.");
                sideFaces[i] = face;
            }

            return new Prism
            {
                BaseCap = baseCap,
                TopCap = topCap,
                BasePlane = basePlane,
                TopPlane = topPlane,
                SideFaces = sideFaces,
                TopCorners = corners,
            };
        }
    }
}
