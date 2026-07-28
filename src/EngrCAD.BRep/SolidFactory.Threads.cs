using EngrCAD.Core;

namespace EngrCAD.BRep;

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
        if (!(pitch > 0) || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch), "Pitch must be positive (right-hand threads only).");
        if (!(length > 0) || !double.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(length));
        ArgumentNullException.ThrowIfNull(pitchProfile);
        int count = pitchProfile.Count;
        if (count < 2)
            throw new ArgumentException("A thread profile needs at least two corners per pitch.", nameof(pitchProfile));
        for (int k = 0; k < count; k++)
        {
            if (!(pitchProfile[k].X > 0))
                throw new ArgumentException("Profile radii must be positive (the generator stays off the axis).", nameof(pitchProfile));
            if (k > 0 && !(pitchProfile[k].Y > pitchProfile[k - 1].Y))
                throw new ArgumentException("Profile axial coordinates must be strictly increasing.", nameof(pitchProfile));
        }
        if (!(pitchProfile[^1].Y - pitchProfile[0].Y < pitch))
            throw new ArgumentException(
                "The profile must span less than one pitch (the closing segment wraps to the first corner + pitch).",
            nameof(pitchProfile));

        var f = frame ?? Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        // The ONE place handedness enters. Every formula below is written in the band's
        // own phase u with z = z_generator + rate·u, so a negative rate makes u descend
        // as the rod ascends and the arithmetic follows by itself.
        double signedPitch = leftHand ? -pitch : pitch;
        double rate = signedPitch / (2 * Math.PI);
        double turns = length / pitch;
        Vector2d Corner(int k) => k < count
            ? pitchProfile[k]
            : new Vector2d(pitchProfile[0].X, pitchProfile[0].Y + pitch);

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
            double alpha = (leftHand ? length - pitchProfile[k].Y : -pitchProfile[k].Y) / rate;
            var x = f.X * Math.Cos(alpha) + f.Y * Math.Sin(alpha);
            var railFrame = Frame3d.FromOrthonormal(railOrigin, x, f.Z.Cross(x));
            var rail = new Helix3d(railFrame, pitchProfile[k].X, signedPitch, turns);
            var atDomainStart = new BrepVertex(rail.PointAt(rail.Domain.Start));
            var atDomainEnd = new BrepVertex(rail.PointAt(rail.Domain.End));
            (startVertices[k], endVertices[k]) =
                leftHand ? (atDomainEnd, atDomainStart) : (atDomainStart, atDomainEnd);
            railEdges[k] = new BrepEdge(rail, rail.Domain, atDomainStart, atDomainEnd);
        }

        // Cap cuts: the spiral arc each cap plane cuts from band k, parameterized by the
        // band's own u (phase-aligned frames on the axis at each cap). With the
        // generator (r, z) = C_k + v·(dr, dz), v(u) = (z_cap − z_k − rate·u)/dz, so
        // r(u) = [r_k + dr·(z_cap − z_k)/dz] + [−dr·rate/dz]·u — the signed rate carries
        // the handedness. The v = 0 and v = 1 ends sit at u = (z_cap − z_k)/rate and
        // (z_cap − z_{k+1})/rate, whose ORDER flips with the rate's sign, so a left-hand
        // cut runs v 0→1 where a right-hand one runs v 1→0.
        var bottomCuts = new BrepEdge[count];
        var topCuts = new BrepEdge[count];
        var bottomFrame = f;
        var topFrame = Frame3d.FromOrthonormal(f.Origin + f.Z * length, f.X, f.Y);
        for (int k = 0; k < count; k++)
        {
            var (r0, z0) = (Corner(k).X, Corner(k).Y);
            var (r1, z1) = (Corner(k + 1).X, Corner(k + 1).Y);
            double dr = r1 - r0, dz = z1 - z0;
            double slope = -dr * rate / dz;

            var bottomSpiral = new SpiralArc3d(
                bottomFrame, r0 - dr * z0 / dz, slope,
                leftHand ? new Interval(-z0 / rate, -z1 / rate) : new Interval(-z1 / rate, -z0 / rate));
            bottomCuts[k] = leftHand
                ? new BrepEdge(bottomSpiral, bottomSpiral.Domain, startVertices[k], startVertices[(k + 1) % count])
                : new BrepEdge(bottomSpiral, bottomSpiral.Domain, startVertices[(k + 1) % count], startVertices[k]);

            var topSpiral = new SpiralArc3d(
                topFrame, r0 + dr * (length - z0) / dz, slope,
                leftHand
                    ? new Interval((length - z0) / rate, (length - z1) / rate)
                    : new Interval((length - z1) / rate, (length - z0) / rate));
            topCuts[k] = leftHand
                ? new BrepEdge(topSpiral, topSpiral.Domain, endVertices[k], endVertices[(k + 1) % count])
                : new BrepEdge(topSpiral, topSpiral.Domain, endVertices[(k + 1) % count], endVertices[k]);
        }

        // Band faces: the parallelogram in (u, v) between the two rails and the two cap
        // cuts. The generator ascends axially (dz > 0), so ∂u × ∂v has radial component
        // r·dz > 0 — outward for EITHER handedness — and the CCW loop in (u, v) is
        // always: v = 0 rail forward (+u), the u = max cut (v 0→1), v = 1 rail backward
        // (−u), the u = min cut (v 1→0). Only WHICH cap sits at u = max changes: for a
        // right-hand thread u climbs with z, so u = max is the top; for a left-hand one
        // it is the bottom.
        var faces = new List<BrepFace>(count + 2);
        for (int k = 0; k < count; k++)
        {
            double edgeA = -Corner(k + 1).Y / rate;
            double edgeB = (length - Corner(k).Y) / rate;
            var surface = new HelicalSurface(
                f, Corner(k), Corner(k + 1), signedPitch,
                new Interval(Math.Min(edgeA, edgeB), Math.Max(edgeA, edgeB)));
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
}
