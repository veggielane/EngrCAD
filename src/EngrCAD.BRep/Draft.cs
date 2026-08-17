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
/// <para><b>Curved faces taper too, and exactly.</b> A face of revolution about the pull
/// direction drafts by rotating its GENERATOR in its own axial half-plane about the point
/// where that generator crosses the neutral plane — the same "rotate about the neutral line"
/// rule, one dimension down. So a drafted cylinder is exactly a cone, a drafted cone another
/// cone, and a drafted torus band another torus band; nothing is fitted. Rims are then
/// re-solved by <see cref="SurfaceCorner"/> through the shared <see cref="CarrierBody"/>
/// rebuild, which is the same machinery curved <see cref="Shelling"/> uses.</para>
///
/// <para><b>Caps carry HOLES.</b> A hole's walls are an ordinary closed RING of side faces
/// whose outward normals point into the hole, so the same "rotate about the neutral line" rule
/// applies to them verbatim and the hole OPENS along the pull direction while the outside
/// closes — which is exactly what releases a core pin. The only generalization it needed was
/// one ring per cap loop: the corner solve, the fold check and the winding rule are the outer
/// boundary's own, and a hole loop is already wound the other way in the cap it came from.</para>
///
/// <para><b>A reversed face drafts too.</b> A difference marks the subtracted tool's walls
/// <see cref="BrepFace.IsReversed"/>, so a bored body — the closest thing this kernel builds to
/// an imported one — arrives with its bore wall spelled backwards; the taper reads its lean off
/// the OUTWARD normal, which is the negation of that face's surface normal, so a cored hole
/// drafts open rather than plausibly shut.</para>
///
/// <para><b>What is rejected.</b> The planar path handles <b>planar-faced prisms</b>: two cap
/// faces perpendicular to the pull direction and four-sided planar side faces between them, one
/// closed ring per cap loop. The curved path handles solids whose curved faces are surfaces of
/// revolution about the PULL AXIS. Anything else — a curved face on some other axis, drafting a
/// cap, a NON-PLANAR neutral surface, a taper so large the profile folds — throws with a
/// message naming the problem rather than silently approximating. For organic parts the
/// implicit route (<c>Shape.ToImplicit</c>) remains available.</para>
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
        if (Math.Abs(angle) >= Math.PI / 2 - 1e-9)
            throw new ArgumentOutOfRangeException(nameof(angle), "The draft angle must be less than 90 degrees.");
        return ApplyCore(
            solid, neutralOrigin, pullDirection,
            faceSelector is null ? _ => angle : face => faceSelector(face) ? angle : null,
            // With no selector every SIDE face drafts and the caps are never "selected";
            // with one, naming a cap is an error the core detects.
            checkCaps: faceSelector is not null);
    }

    /// <summary>
    /// Per-face draft angles in ONE call: <paramref name="angleSelector"/> returns each
    /// side face's angle in radians, or null to leave the face alone. One call rather
    /// than a chain matters when adjacent faces get different angles — every corner is
    /// solved once from its two final planes, which is exactly what chaining computes
    /// too (the operation is closed-form and composable), just without re-recognizing
    /// the prism per angle.
    /// </summary>
    public static BrepSolid Apply(
        BrepSolid solid,
        in Vector3d neutralOrigin,
        in Vector3d pullDirection,
        Func<BrepFace, double?> angleSelector)
    {
        ArgumentNullException.ThrowIfNull(angleSelector);
        return ApplyCore(solid, neutralOrigin, pullDirection, angleSelector, checkCaps: true);
    }

    private static BrepSolid ApplyCore(
        BrepSolid solid,
        in Vector3d neutralOrigin,
        in Vector3d pullDirection,
        Func<BrepFace, double?> angleSelector,
        bool checkCaps)
    {
        ArgumentNullException.ThrowIfNull(solid);
        if (!pullDirection.TryNormalize(Tolerance.Default, out var pull))
            throw new ArgumentException("The pull direction must be non-zero.", nameof(pullDirection));

        if (solid.Faces.Any(f => !f.IsPlanar(out _, out _)))
            return ApplyCurved(solid, neutralOrigin, pull, angleSelector, checkCaps);

        var prism = Prism.Recognize(solid, pull);
        var origin = neutralOrigin;

        // A selected cap is always an error, never a silent no-op: caps are the parting
        // faces and cannot be tapered, so naming one means the selector is wrong.
        if (checkCaps && (angleSelector(prism.BaseCap) is not null || angleSelector(prism.TopCap) is not null))
            throw new ArgumentException(
                "Draft selected a cap face (one perpendicular to the pull direction). Caps are the " +
                "parting faces and stay put; select the side faces to taper.", nameof(angleSelector));

        // One plane array per RING — the outer boundary, then one per hole in a cap. A hole's
        // wall is an ordinary side face whose outward normal points INTO the hole, so the same
        // taper rule applies to it verbatim and the hole OPENS along the pull direction while
        // the outside closes: exactly what releases a core pin.
        var rings = prism.Rings;
        var planes = new (Vector3d Origin, Vector3d Normal)[rings.Length][];
        double largest = 0;
        bool anyDrafted = false;
        for (int r = 0; r < rings.Length; r++)
        {
            var sideFaces = rings[r].SideFaces;
            planes[r] = new (Vector3d, Vector3d)[sideFaces.Length];
            for (int i = 0; i < sideFaces.Length; i++)
            {
                var face = sideFaces[i];
                face.IsPlanar(out var faceOrigin, out var faceNormal);
                if (angleSelector(face) is not double faceAngle)
                {
                    planes[r][i] = (faceOrigin, faceNormal);
                    continue;
                }
                if (Math.Abs(faceAngle) >= Math.PI / 2 - 1e-9)
                    throw new ArgumentOutOfRangeException(nameof(angleSelector),
                        "A draft angle must be less than 90 degrees.");
                planes[r][i] = TaperPlane(faceOrigin, faceNormal, origin, pull, faceAngle);
                if (Math.Abs(faceAngle) > Math.Abs(largest))
                    largest = faceAngle;
                anyDrafted = true;
            }
        }

        if (!anyDrafted)
            throw new ArgumentException("Draft selected no side face of the solid.", nameof(angleSelector));
        double angle = largest; // for the fold-rejection messages below

        var baseCorners = new Vector3d[rings.Length][];
        var topCorners = new Vector3d[rings.Length][];
        for (int r = 0; r < rings.Length; r++)
        {
            int n = planes[r].Length;
            baseCorners[r] = new Vector3d[n];
            topCorners[r] = new Vector3d[n];
            for (int i = 0; i < n; i++)
            {
                // Corner i separates side face i − 1 from side face i, WITHIN this ring: a
                // hole's corners involve only that hole's own walls and the two caps.
                var previous = planes[r][(i + n - 1) % n];
                var current = planes[r][i];
                baseCorners[r][i] = IntersectPlanes(previous, current, prism.BasePlane, i);
                topCorners[r][i] = IntersectPlanes(previous, current, prism.TopPlane, i);
            }
            ValidateProfile(baseCorners[r], rings[r].TopCorners, pull, angle, "base");
            ValidateProfile(topCorners[r], rings[r].TopCorners, pull, angle, "top");
        }
        return BuildPrism(baseCorners, topCorners, planes, prism);
    }

    /// <summary>
    /// Drafting a solid with curved faces. The taper is expressed once — "rotate the carrier
    /// so its normal tilts toward the pull direction by the angle, about the neutral plane" —
    /// and applied per carrier family; everything after that (re-solving vertices, rebuilding
    /// rims, re-trimming domain-driven grids) is the shared <see cref="CarrierBody"/> rebuild.
    ///
    /// <para><b>A face of revolution drafts by rotating its GENERATOR, and that is the whole
    /// generalization.</b> Planar draft rotates a plane about its neutral LINE; a revolve's
    /// profile is one dimension down, so it rotates about the neutral POINT of its generator
    /// inside its own axial half-plane. A cylinder's vertical profile line tilts into a slanted
    /// one — exactly a cone — and a torus band's circular profile rotates rigidly, so it stays
    /// a circle and the band stays a torus. Nothing is fitted anywhere.</para>
    /// </summary>
    private static BrepSolid ApplyCurved(
        BrepSolid solid, in Vector3d neutralOrigin, in Vector3d pull,
        Func<BrepFace, double?> angleSelector, bool checkCaps)
    {
        var body = CarrierBody.Recognize(solid);
        var faces = solid.Faces.ToArray();
        var carriers = new Surface[faces.Length];
        var origin = neutralOrigin;
        var direction = pull;
        bool anyDrafted = false;

        for (int f = 0; f < faces.Length; f++)
        {
            carriers[f] = faces[f].Surface;

            // Caps are the parting faces and never taper, exactly as on the planar path: a
            // null selector means "every SIDE face", and naming a cap explicitly is an error
            // rather than a silent no-op.
            if (faces[f].IsPlanar(out _, out var faceNormal)
                && Math.Abs(faceNormal.Normalized().Dot(direction)) > 1 - NormalClassificationTolerance)
            {
                if (checkCaps && angleSelector(faces[f]) is not null)
                    throw new ArgumentException(
                        "Draft selected a cap face (one perpendicular to the pull direction). Caps are the " +
                        "parting faces and stay put; select the side faces to taper.", nameof(angleSelector));
                continue;
            }

            if (angleSelector(faces[f]) is not double angle)
                continue;
            if (Math.Abs(angle) >= Math.PI / 2 - 1e-9)
                throw new ArgumentOutOfRangeException(nameof(angleSelector),
                    "A draft angle must be less than 90 degrees.");
            // Exact-zero semantic test: a zero draft is the identity, and returning the very
            // same carrier keeps a no-op selector bit-identical.
            if (angle == 0)
                continue;
            carriers[f] = Taper(faces[f].Surface, faces[f].IsReversed, origin, direction, angle);
            anyDrafted = true;
        }
        if (!anyDrafted)
            throw new ArgumentException("Draft selected no face of the solid.", nameof(angleSelector));

        return body.Rebuild(carriers, "draft");
    }

    /// <summary>
    /// One carrier tapered: its normal tilted toward <paramref name="pull"/> by
    /// <paramref name="angle"/>, hinged on the neutral plane.
    ///
    /// <para>The rotation SENSE is chosen by measurement rather than derived: both candidates
    /// are built and the one whose normal actually leans further toward the pull direction
    /// wins. Deriving it instead would mean re-proving the half-plane's handedness against the
    /// generator's traversal and the revolve's own outward convention — three sign conventions
    /// that have each cost this kernel real geometry — for an answer one dot product settles.</para>
    ///
    /// <para><b>The measurement is of the OUTWARD normal, which is why
    /// <paramref name="reversed"/> is a parameter.</b> A taper is defined against the direction
    /// the material faces, and a reversed face's outward normal is the negation of its
    /// surface's — so a bore (whose walls a difference marks reversed) would otherwise taper
    /// the wrong way while looking perfectly plausible. The sign appears twice for the same
    /// reason: on the plane's own normal before the hinge, and on the candidate lean after it.
    /// A forward face multiplies by an exact <c>+1</c> in both places, so every all-forward
    /// body's carriers are bit-identical.</para>
    /// </summary>
    private static Surface Taper(
        Surface surface, bool reversed, in Vector3d neutralOrigin, in Vector3d pull, double angle)
    {
        double sense = reversed ? -1 : 1;
        if (surface is PlaneSurface plane)
        {
            // TaperPlane speaks OUTWARD normals in and out; Rebuild carries IsReversed over
            // verbatim, so the surface built here must carry the outward answer back through
            // the same sense it came in on.
            var (origin, outward) = TaperPlane(
                plane.Origin, plane.Normal.Normalized() * sense, neutralOrigin, pull, angle);
            var normal = outward * sense;
            var x = normal.ArbitraryPerpendicular(Tolerance.Default);
            return new PlaneSurface(origin, x, normal.Cross(x));
        }

        var revolved = AsRevolve(surface, pull)
            ?? throw new NotSupportedException(
                $"Draft can taper a plane or a surface of revolution about the pull direction; a " +
                $"{surface.GetType().Name} face is neither. Curved faces on other axes have no exact " +
                "taper — their drafted carrier is not a surface of any family this kernel builds.");

        var axis = revolved.AxisDirection;
        if (!HalfPlaneNormal(revolved, out var hinge))
            throw new NotSupportedException(
                "A drafted surface of revolution's generator lies on its own axis, so it has no profile " +
                "plane to tilt in.");

        // The generator's neutral point: where its profile crosses the neutral plane. Solved
        // on the EXACT generator by bisection on the axial coordinate, never by projection.
        double neutralHeight = (neutralOrigin - revolved.AxisOrigin).Dot(axis);
        if (!TryGeneratorAt(revolved, neutralHeight, out var pivot))
            throw new NotSupportedException(
                "A drafted surface of revolution does not cross the neutral plane, so it has no hinge; " +
                "move the neutral plane inside the face's own extent.");

        Surface? best = null;
        double bestLean = double.NegativeInfinity;
        foreach (double candidate in (ReadOnlySpan<double>)[angle, -angle])
        {
            var rotation = Matrix4d.CreateTranslation(pivot)
                * Matrix4d.CreateFromAxisAngle(hinge, candidate)
                * Matrix4d.CreateTranslation(-pivot);
            var tapered = new RevolvedSurface(
                Rotate(revolved.Generator, rotation), revolved.AxisOrigin, axis, revolved.Angle);
            double lean = Math.Sign(angle) * sense
                * tapered.NormalAt(tapered.DomainU.Mid, tapered.DomainV.Mid).Dot(pull);
            if (lean > bestLean)
            {
                bestLean = lean;
                best = tapered;
            }
        }
        return best!;
    }

    /// <summary>
    /// The surface as a revolve about the pull axis, or null. A cylinder has no generator of
    /// its own, so one is synthesized as a line in its own X half-plane — which is what makes
    /// a drafted cylinder come back as a cone on the SAME phase, its rim circles still landing
    /// on the carrier's grid. The synthesized extent is arbitrary because
    /// <see cref="CarrierBody"/> re-trims every domain-driven carrier to its own loops.
    ///
    /// <para>A cylinder arrives spelled two ways — as a <see cref="CylinderSurface"/> from the
    /// factory and as a full circle EXTRUDED along its own axis from a sketch, which is what
    /// <c>Shape.Cylinder</c> lowers to — and both must draft. The whole-turn check on the
    /// extruded form is load-bearing for the same reason it is in
    /// <c>SurfaceIntersection.Promote</c>: a rounded rectangle's corner is a QUARTER arc
    /// extruded, every point of which lies on the full cylinder, so a start-point-only guard
    /// would silently taper 270° of surface the face does not carry.</para>
    /// </summary>
    private static RevolvedSurface? AsRevolve(Surface surface, in Vector3d pull)
    {
        switch (surface)
        {
            case RevolvedSurface revolved
                when revolved.AxisDirection.IsParallelTo(pull, Tolerance.Default):
                return revolved;
            case CylinderSurface cylinder when cylinder.Axis.IsParallelTo(pull, Tolerance.Default):
                return AboutAxis(cylinder.Origin, cylinder.Axis, cylinder.XDirection, cylinder.Radius);
            case ExtrudedSurface extruded
                when extruded.Direction.IsParallelTo(pull, Tolerance.Default)
                    && CircularArc.TryFit(extruded.Generator, out var fit)
                    && fit.Circle.Axis.IsParallelTo(extruded.Direction, Tolerance.Default)
                    && Math.Abs(Math.Abs(fit.Sweep) - 2 * Math.PI) < Tolerance.Default.Angular:
                return AboutAxis(
                    fit.Circle.Center, extruded.Direction, fit.Circle.XDirection, fit.Circle.Radius);
            default:
                return null;
        }

        static RevolvedSurface AboutAxis(
            in Vector3d origin, in Vector3d axisDirection, in Vector3d radial, double radius)
        {
            var axis = axisDirection.Normalized();
            var start = origin + radial * radius;
            return new RevolvedSurface(new Line3d(start, start + axis), origin, axis);
        }
    }

    private static bool HalfPlaneNormal(RevolvedSurface revolved, out Vector3d normal)
    {
        normal = default;
        var axis = revolved.AxisDirection;
        var domain = revolved.Generator.Domain;
        double best = 0;
        for (int i = 0; i <= 16; i++)
        {
            var offset = revolved.Generator.PointAt(domain.ParameterAt(i / 16.0)) - revolved.AxisOrigin;
            var radial = offset - axis * offset.Dot(axis);
            if (radial.Length > best)
            {
                best = radial.Length;
                normal = axis.Cross(radial);
            }
        }
        return normal.TryNormalize(Tolerance.Default, out normal);
    }

    /// <summary>
    /// The generator point at a given axial height, found by bisection on the EXACT curve.
    /// A generator whose axial coordinate is monotone (every one this path accepts) has one
    /// crossing; the hinge must be exact, since every drafted point is measured from it.
    /// </summary>
    private static bool TryGeneratorAt(RevolvedSurface revolved, double height, out Vector3d point)
    {
        point = default;
        var axis = revolved.AxisDirection;
        var domain = revolved.Generator.Domain;
        double Axial(double t) => (revolved.Generator.PointAt(t) - revolved.AxisOrigin).Dot(axis);

        double lo = domain.Start, hi = domain.End;
        double fLo = Axial(lo) - height, fHi = Axial(hi) - height;
        if (fLo * fHi > 0)
        {
            // The neutral plane misses the generator's own extent. That is legal — the hinge
            // is a property of the CARRIER, not of the trim — so extrapolate along the chord
            // for a straight generator and refuse otherwise.
            if (Math.Abs(fHi - fLo) <= Tolerance.Default.Linear)
                return false;
            double t = -fLo / (fHi - fLo);
            var a = revolved.Generator.PointAt(domain.Start);
            var b = revolved.Generator.PointAt(domain.End);
            point = a + (b - a) * t;
            return Math.Abs((point - revolved.AxisOrigin).Dot(axis) - height) <= Tolerance.Default.Linear;
        }

        for (int step = 0; step < 80; step++)
        {
            double mid = (lo + hi) / 2;
            double fMid = Axial(mid) - height;
            if (fLo * fMid <= 0)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
                fLo = fMid;
            }
        }
        point = revolved.Generator.PointAt((lo + hi) / 2);
        return true;
    }

    /// <summary>
    /// A generator moved rigidly. A straight one is rebuilt as a fresh <see cref="Line3d"/> so
    /// downstream cone recognition and generator trimming keep working on a plain type;
    /// everything else goes through <see cref="Curve3d.Transformed"/>, which is exact AND
    /// parameterization-preserving for a rigid map — the same t evaluates to the same moved
    /// point, so an arc sampled at even angles still is.
    /// </summary>
    private static Curve3d Rotate(Curve3d generator, in Matrix4d rotation)
    {
        var domain = generator.Domain;
        var start = generator.PointAt(domain.Start);
        var end = generator.PointAt(domain.End);
        var chord = end - start;
        if (chord.TryNormalize(Tolerance.Default, out var tangent))
        {
            bool straight = true;
            for (int i = 1; i < 8 && straight; i++)
            {
                var offset = generator.PointAt(domain.ParameterAt(i / 8.0)) - start;
                straight = (offset - tangent * offset.Dot(tangent)).Length
                    <= Tolerance.Default.Linear * Math.Max(1, chord.Length);
            }
            if (straight)
                return new Line3d(rotation.TransformPoint(start), rotation.TransformPoint(end));
        }
        return generator.Transformed(rotation);
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
            throw new ArgumentException(
                "The neutral face must be planar. A non-planar neutral SURFACE gives every drafted face a " +
                "curved hinge instead of a straight neutral line, so the tapered carrier is a general ruled " +
                "surface rather than a plane, a cone or a torus band — no family this kernel builds, and " +
                "nothing an exact corner solve could re-intersect. Draft about a plane, or split the part at " +
                "the parting line and draft each side.", nameof(neutralFace));

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
    ///
    /// <para>The winding is compared against the ORIGINAL ring's own sign rather than
    /// required positive, because a hole ring is wound the other way by construction — and
    /// for the outer ring, whose original area is positive, that is the same predicate.</para>
    /// </summary>
    private static void ValidateProfile(
        Vector3d[] corners, Vector3d[] original, in Vector3d pull, double angle, string which)
    {
        int n = corners.Length;
        double area = 0, was = 0;
        for (int i = 0; i < n; i++)
        {
            area += corners[i].Cross(corners[(i + 1) % n]).Dot(pull);
            was += original[i].Cross(original[(i + 1) % n]).Dot(pull);
        }
        if (!(area * was > 0))
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
    ///
    /// <para><b>Every face here has a positional parent</b>, so provenance rides through:
    /// rebuilt side face <c>i</c> IS <c>prism.SideFaces[i]</c> tapered (the same wall, at a
    /// new angle) and the two caps keep their own planes entirely. The correspondence is the
    /// one <see cref="ApplyCore"/> already established when it built <c>planes[i]</c> from
    /// <c>SideFaces[i]</c> — it is not re-derived here, which is what keeps a tag from
    /// landing on the wrong wall.</para>
    /// </summary>
    private static BrepSolid BuildPrism(
        Vector3d[][] baseCorners, Vector3d[][] topCorners,
        (Vector3d Origin, Vector3d Normal)[][] sidePlanes,
        in Prism prism)
    {
        var basePlane = prism.BasePlane;
        var topPlane = prism.TopPlane;
        int ringCount = baseCorners.Length;
        var faces = new List<BrepFace>();
        var baseLoops = new List<BrepLoop>(ringCount);
        var topLoops = new List<BrepLoop>(ringCount);

        for (int r = 0; r < ringCount; r++)
        {
            int n = baseCorners[r].Length;
            var baseVertices = new BrepVertex[n];
            var topVertices = new BrepVertex[n];
            for (int i = 0; i < n; i++)
            {
                baseVertices[i] = new BrepVertex(baseCorners[r][i]);
                topVertices[i] = new BrepVertex(topCorners[r][i]);
            }

            var baseEdges = new BrepEdge[n];
            var topEdges = new BrepEdge[n];
            var rails = new BrepEdge[n];
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                baseEdges[i] = new BrepEdge(
                    new Line3d(baseCorners[r][i], baseCorners[r][next]), Interval.Unit,
                    baseVertices[i], baseVertices[next]);
                topEdges[i] = new BrepEdge(
                    new Line3d(topCorners[r][i], topCorners[r][next]), Interval.Unit,
                    topVertices[i], topVertices[next]);
                rails[i] = new BrepEdge(
                    new Line3d(baseCorners[r][i], topCorners[r][i]), Interval.Unit,
                    baseVertices[i], topVertices[i]);
            }

            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                // Plane axes: X along the base edge, Y completing the outward frame, so
                // X x Y is exactly the drafted normal.
                var x = (baseCorners[r][next] - baseCorners[r][i]).Normalized();
                var normal = sidePlanes[r][i].Normal;
                faces.Add(new BrepFace(
                    new PlaneSurface(baseCorners[r][i], x, normal.Cross(x)),
                    [
                        new BrepLoop(
                        [
                            new BrepCoedge(baseEdges[i], sameSense: true),
                            new BrepCoedge(rails[next], sameSense: true),
                            new BrepCoedge(topEdges[i], sameSense: false),
                            new BrepCoedge(rails[i], sameSense: false),
                        ]),
                    ]).DescendsFrom(prism.Rings[r].SideFaces[i]));
            }

            // The SAME winding rule serves both rings, which is what makes holes structural
            // rather than a second case: a hole loop is already wound the other way in the cap
            // it came from, so reversing the base and keeping the top reproduces exactly that.
            var baseLoop = new List<BrepCoedge>(n);
            for (int i = n - 1; i >= 0; i--)
                baseLoop.Add(new BrepCoedge(baseEdges[i], sameSense: false));
            var topLoop = new List<BrepCoedge>(n);
            for (int i = 0; i < n; i++)
                topLoop.Add(new BrepCoedge(topEdges[i], sameSense: true));
            baseLoops.Add(new BrepLoop(baseLoop));
            topLoops.Add(new BrepLoop(topLoop));
        }

        var baseX = (baseCorners[0][1] - baseCorners[0][0]).Normalized();
        var topX = (topCorners[0][1] - topCorners[0][0]).Normalized();
        faces.Add(new BrepFace(
            new PlaneSurface(basePlane.Origin, baseX, basePlane.Normal.Cross(baseX)), baseLoops)
            .DescendsFrom(prism.BaseCap));
        faces.Add(new BrepFace(
            new PlaneSurface(topPlane.Origin, topX, topPlane.Normal.Cross(topX)), topLoops)
            .DescendsFrom(prism.TopCap));
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

        /// <summary>
        /// One ring per cap loop: the outer boundary first, then one per HOLE. A hole's walls
        /// form a closed ring of side faces exactly as the outer boundary's do, and taper by
        /// the same rule, so the only thing multi-loop caps needed was the loop over rings.
        /// </summary>
        public required Ring[] Rings { get; init; }

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
            if (baseCap.Loops.Count != topCap.Loops.Count)
                throw new NotSupportedException(
                    $"Draft needs the two caps to have matching loop counts; the base has " +
                    $"{baseCap.Loops.Count} and the top {topCap.Loops.Count}, so they do not bound the same " +
                    "prism.");
            int total = topCap.Loops.Sum(l => l.Coedges.Count);
            if (total != sides.Count)
                throw new NotSupportedException(
                    $"Draft needs one side face per cap edge; the top cap has {total} edges (over " +
                    $"{topCap.Loops.Count} loop(s)) but the solid has {sides.Count} side faces.");
            if (baseCap.Loops.Sum(l => l.Coedges.Count) != total)
                throw new NotSupportedException("Draft needs the two caps to have matching edge counts.");

            var rings = new Ring[topCap.Loops.Count];
            var seen = new HashSet<BrepFace>();
            for (int r = 0; r < rings.Length; r++)
            {
                var loop = topCap.Loops[r];
                int n = loop.Coedges.Count;
                if (n < 3)
                    throw new NotSupportedException(
                        $"Draft needs at least three edges per cap loop; one has {n}.");
                var sideFaces = new BrepFace[n];
                var corners = new Vector3d[n];
                for (int i = 0; i < n; i++)
                {
                    var coedge = loop.Coedges[i];
                    corners[i] = coedge.StartVertex.Position;
                    var partner = coedge.Partner
                        ?? throw new NotSupportedException(
                            "Draft needs a closed manifold solid (an edge has one use).");
                    var face = partner.Loop.Face;
                    if (!seen.Add(face))
                        throw new NotSupportedException(
                            "Draft needs each side face to touch the top cap exactly once (a face used twice " +
                            "is not a simple prism side).");
                    if (face.Loops.Count != 1 || face.OuterLoop.Coedges.Count != 4)
                        throw new NotSupportedException(
                            "Draft needs four-sided single-loop side faces (base edge, two rails, top edge); " +
                            $"one side face has {face.Loops.Count} loop(s) and " +
                            $"{face.OuterLoop.Coedges.Count} edges.");
                    sideFaces[i] = face;
                }
                rings[r] = new Ring { SideFaces = sideFaces, TopCorners = corners };
            }

            return new Prism
            {
                BaseCap = baseCap,
                TopCap = topCap,
                BasePlane = basePlane,
                TopPlane = topPlane,
                Rings = rings,
            };
        }
    }

    /// <summary>
    /// One closed ring of side faces and the cap corners they span: the prism's outer
    /// boundary, or one of its holes.
    /// </summary>
    private readonly struct Ring
    {
        /// <summary>Side faces in the cap loop's order; face i spans corner i to i + 1.</summary>
        public required BrepFace[] SideFaces { get; init; }

        /// <summary>The original cap-loop corners, in the same order.</summary>
        public required Vector3d[] TopCorners { get; init; }
    }
}
