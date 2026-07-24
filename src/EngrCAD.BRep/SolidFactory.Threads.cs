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
    /// Right-hand threads only (positive pitch, advancing along +Z). Counts: V = 2K
    /// (one rail start + end per corner), E = 3K (K rails + K spiral cuts per cap),
    /// F = K + 2, L = K + 2, so Euler–Poincaré gives
    /// V − E + F − (L − F) − 2(S − G) = 2K − 3K + (K + 2) − 0 − 2 = 0 (genus 0).
    /// </para>
    /// </summary>
    /// <param name="pitchProfile">(radius, axial) corners of one pitch, radii positive,
    /// axial strictly increasing, span &lt; pitch. Axial values may be negative (e.g. a
    /// crest-centered thread profile); phase 0 is the frame's X axis at z = 0.</param>
    /// <param name="pitch">Axial advance per turn; must be positive.</param>
    /// <param name="length">Rod length along the frame's Z axis; any positive value.</param>
    /// <param name="frame">Axis pose; defaults to the world frame (axis +Z from the origin).</param>
    public static BrepSolid MakeThreadedRod(
        IReadOnlyList<Vector2d> pitchProfile, double pitch, double length, Frame3d? frame = null)
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
        double rate = pitch / (2 * Math.PI);
        double turns = length / pitch;
        Vector2d Corner(int k) => k < count
            ? pitchProfile[k]
            : new Vector2d(pitchProfile[0].X, pitchProfile[0].Y + pitch);

        // Rails: one exact helix per profile corner, on the rod's own frame rotated to
        // the corner's phase (never an arbitrary perpendicular — phase alignment is the
        // weld rule). Every rail starts on the z = 0 cap plane and ends on z = length.
        var railEdges = new BrepEdge[count];
        var startVertices = new BrepVertex[count];
        var endVertices = new BrepVertex[count];
        for (int k = 0; k < count; k++)
        {
            double alpha = -pitchProfile[k].Y / rate; // phase where this corner's helix crosses z = 0
            var x = f.X * Math.Cos(alpha) + f.Y * Math.Sin(alpha);
            var railFrame = Frame3d.FromOrthonormal(f.Origin, x, f.Z.Cross(x));
            var rail = new Helix3d(railFrame, pitchProfile[k].X, pitch, turns);
            startVertices[k] = new BrepVertex(rail.PointAt(rail.Domain.Start));
            endVertices[k] = new BrepVertex(rail.PointAt(rail.Domain.End));
            railEdges[k] = new BrepEdge(rail, rail.Domain, startVertices[k], endVertices[k]);
        }

        // Cap cuts: the spiral arc each cap plane cuts from band k, parameterized by the
        // band's own u (phase-aligned frames on the axis at each cap). With the
        // generator (r, z) = C_k + v·(dr, dz), v(u) = (z_cap − z_k − rate·u)/dz, so
        // r(u) = [r_k + dr·(z_cap − z_k)/dz] + [−dr·rate/dz]·u. The parameter runs from
        // the v = 1 end (rail k+1) up to the v = 0 end (rail k).
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
                bottomFrame, r0 - dr * z0 / dz, slope, new Interval(-z1 / rate, -z0 / rate));
            bottomCuts[k] = new BrepEdge(
                bottomSpiral, bottomSpiral.Domain, startVertices[(k + 1) % count], startVertices[k]);

            var topSpiral = new SpiralArc3d(
                topFrame, r0 + dr * (length - z0) / dz, slope,
                new Interval((length - z1) / rate, (length - z0) / rate));
            topCuts[k] = new BrepEdge(
                topSpiral, topSpiral.Domain, endVertices[(k + 1) % count], endVertices[k]);
        }

        // Band faces: the parallelogram in (u, v) between the two rails and the two cap
        // cuts. The generator ascends axially (dz > 0), so ∂u × ∂v points outward and
        // the CCW loop is bottom rail (+u), top cut (v 0→1 = reversed), top rail (−u),
        // bottom cut (v 1→0 = forward).
        var faces = new List<BrepFace>(count + 2);
        for (int k = 0; k < count; k++)
        {
            var surface = new HelicalSurface(
                f, Corner(k), Corner(k + 1), pitch,
                new Interval(-Corner(k + 1).Y / rate, (length - Corner(k).Y) / rate));
            faces.Add(new BrepFace(surface,
            [
                new BrepLoop(
                [
                    new BrepCoedge(railEdges[k], sameSense: true),
                    new BrepCoedge(topCuts[k], sameSense: false),
                    new BrepCoedge(railEdges[(k + 1) % count], sameSense: false),
                    new BrepCoedge(bottomCuts[k], sameSense: true),
                ]),
            ]));
        }

        // Bottom cap: normal −Z (basis y, x); its loop runs clockwise seen from +Z,
        // which is each cut traversed against its parameter, chained k = 0..K−1.
        var bottomLoop = new BrepLoop(
            [.. Enumerable.Range(0, count).Select(k => new BrepCoedge(bottomCuts[k], sameSense: false))]);
        faces.Add(new BrepFace(new PlaneSurface(f.Origin, f.Y, f.X), [bottomLoop]));

        // Top cap: normal +Z (basis x, y); counter-clockwise from above is each cut
        // traversed forward, chained k = K−1..0.
        var topLoop = new BrepLoop(
            [.. Enumerable.Range(0, count).Select(k => new BrepCoedge(topCuts[count - 1 - k], sameSense: true))]);
        faces.Add(new BrepFace(new PlaneSurface(f.Origin + f.Z * length, f.X, f.Y), [topLoop]));

        return new BrepSolid([new BrepShell(faces)]);
    }
}
