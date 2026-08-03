using System.Globalization;
using System.Text;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// Design-for-manufacture checks over a <see cref="Part"/>: draft angle against a mould
/// pull direction, overhang area against a print build direction, and wall thickness.
///
/// <para><b>Every check answers twice, at two fidelities, and says which is which.</b>
/// The <i>verdict</i> — does this part pass — is read from the most exact source the
/// part has (the B-Rep's own faces for draft, closed-form facet arithmetic for
/// overhangs, a ray against the display mesh for thickness). The <i>picture</i> is a
/// <see cref="MeshField"/> over the display mesh, which the existing
/// <see cref="FieldDisplay"/> machinery colours with no new rendering code at all:
/// attach it with <see cref="Part.AddResult"/> and set <see cref="Part.FieldDisplay"/>
/// from the report's own <c>Display</c>. A field is per-VERTEX and a mesh vertex is
/// shared by every facet touching it, so each vertex carries the <b>worst</b> reading
/// among its incident facets — which is what a check is for, and which means a whisker
/// of the neighbouring face is tinted along every sharp edge.</para>
///
/// <para><b>Thresholds are compared on the dot product, never on the derived angle.</b>
/// <c>asin</c> is monotone, so "the angle exceeds the threshold" and "the dot product
/// exceeds the threshold's sine" are the same statement — but the second carries one
/// fewer rounding, so it is the one the counts and the pass/fail come from while the
/// reported degrees exist for humans. The two can only disagree within a rounding step
/// of the threshold itself.</para>
/// </summary>
public static class Manufacturability
{
    /// <summary>The result names these checks publish under. Attaching a result of the
    /// same name REPLACES it, so re-running a check updates the display in place.</summary>
    public static class FieldNames
    {
        /// <summary>Signed draft angle in degrees: 0 = a wall parallel to the pull
        /// (the failure), +/-90 = a face square to it. The sign says which mould half.</summary>
        public const string DraftAngle = "draft angle";

        /// <summary>Overhang angle in degrees measured from the build direction: 0 = a
        /// vertical wall, +90 = a downward-facing ceiling, negative = upward-facing.</summary>
        public const string OverhangAngle = "overhang angle";

        /// <summary>Wall thickness in model units along the surface normal.</summary>
        public const string WallThickness = "wall thickness";
    }

    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    /// Relative guard for "this accumulated area-weighted normal is noise". The sum has
    /// units of AREA, so the comparison is against the incident area rather than against
    /// a length — the scale-free tier (an absolute epsilon on a cross product is an area
    /// threshold and fails quadratically with model scale).
    /// </summary>
    private const double RelativeDegeneracy = 1e-13;

    // ---------------------------------------------------------------- draft angle

    /// <summary>
    /// Draft angle against a mould pull direction. The draft at a point is
    /// <c>asin(n · pull)</c> for the outward normal <c>n</c>: a wall PARALLEL to the pull
    /// reads 0 (it cannot release), a face square to it reads +/-90, and the SIGN says
    /// which mould half the face belongs to. A face passes when its worst release angle
    /// — the smallest <c>|draft|</c> anywhere on it — reaches
    /// <paramref name="minimumAngleDegrees"/>.
    ///
    /// <para><b>Planar faces are exact and curved faces are sampled, and the report says
    /// so per face</b> (<see cref="DraftFaceCheck.Samples"/>). A plane has one normal, so
    /// its draft is one number with no discretization in it; a cylinder, cone or revolve
    /// band has a normal that varies, so it is read at
    /// <paramref name="curvedFaceSamples"/> squared points over the trimmed parameter
    /// domain plus every point of its pulled boundary loops. Sampling can miss an
    /// extremum between samples — raise the count where that matters.</para>
    ///
    /// <para><b>What this check cannot see is a global UNDERCUT.</b> The draft angle is a
    /// local property of a normal; a face can have ample draft and still be shadowed by
    /// material above it, so that no rigid pull frees it. Deciding that is a visibility
    /// question along +/-pull, not a normal question, and is deliberately not attempted
    /// here.</para>
    ///
    /// <para>A part with no B-Rep — a raw mesh, an import, an SDF — has no faces to list,
    /// so the verdict falls back to the display mesh's facets and
    /// <see cref="DraftReport.Note"/> says so. That reading is CONSERVATIVE on convex
    /// curved faces: an inscribed facet is steeper than the surface it approximates, so
    /// it reports slightly less draft than the surface has.</para>
    /// </summary>
    /// <param name="part">The part to check.</param>
    /// <param name="pull">Mould pull direction; need not be unit length.</param>
    /// <param name="minimumAngleDegrees">Required release angle, in degrees.</param>
    /// <param name="quality">Display-mesh quality for the published field.</param>
    /// <param name="curvedFaceSamples">Grid resolution per parameter direction on curved faces.</param>
    public static DraftReport CheckDraft(
        Part part,
        Vector3d pull,
        double minimumAngleDegrees = 1.0,
        MeshQuality? quality = null,
        int curvedFaceSamples = 24)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumAngleDegrees);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumAngleDegrees, 90);
        ArgumentOutOfRangeException.ThrowIfLessThan(curvedFaceSamples, 2);
        var direction = Unit(pull, nameof(pull));
        double minimumSine = Math.Sin(minimumAngleDegrees * DegreesToRadians);

        var mesh = part.GetMesh(quality);
        var facets = Facets(mesh);

        // Field: per vertex, the incident facet whose |draft| is smallest, keeping its
        // sign. A vertex touched only by degenerate facets takes the neutral best
        // reading rather than the worst, so an unmeasurable point never cries wolf.
        var worst = new double[mesh.VertexCount];
        Array.Fill(worst, double.NaN);
        foreach (var facet in facets)
        {
            double dot = facet.Normal.Dot(direction);
            Accumulate(worst, facet, dot, keepSmallerMagnitude: true);
        }
        var values = new double[mesh.VertexCount];
        for (int v = 0; v < values.Length; v++)
            values[v] = double.IsNaN(worst[v]) ? 90 : Math.Asin(Math.Clamp(worst[v], -1, 1)) * RadiansToDegrees;
        var field = MeshField.Scalar(FieldNames.DraftAngle, "deg", values);

        // Verdict: exact faces where the part has a B-Rep, facets otherwise.
        var solid = part.TryGetSolid();
        var faces = new List<DraftFaceCheck>();
        string? note = null;
        if (solid is null)
        {
            note = "no B-Rep: the verdict is read from the display mesh's facets, which " +
                   "under-report draft on convex curved faces by the inscribed-chord angle";
            double meshWorstSine = 1;
            foreach (var facet in facets)
                meshWorstSine = Math.Min(meshWorstSine, Math.Abs(facet.Normal.Dot(direction)));
            double failingArea = facets
                .Where(f => Math.Abs(f.Normal.Dot(direction)) < minimumSine)
                .Sum(f => f.Area);
            return new DraftReport(
                direction, minimumAngleDegrees, faces,
                Math.Asin(Math.Clamp(meshWorstSine, 0, 1)) * RadiansToDegrees,
                failingArea, field, note);
        }

        int index = 0;
        double failing = 0;
        foreach (var face in solid.Faces)
        {
            var check = CheckFace(face, index++, direction, minimumSine, curvedFaceSamples);
            faces.Add(check);
            if (!check.Passes)
                failing += check.Area;
        }
        return new DraftReport(
            direction, minimumAngleDegrees, faces,
            faces.Count == 0 ? 90 : faces.Min(f => Math.Abs(f.WorstReleaseDegrees)),
            failing, field, note);
    }

    private static DraftFaceCheck CheckFace(
        BrepFace face, int index, Vector3d pull, double minimumSine, int samples)
    {
        double area;
        try
        {
            area = face.Area();
        }
        catch
        {
            area = 0;   // an ordering-grade measure; a face it cannot integrate is not a verdict.
        }
        var location = face.Bounds().Center;   // a face is LOCATED by its bounds centre (never a plane origin).

        if (face.IsPlanar(out _, out var planeNormal))
        {
            // PlaneSurface.Normal is x cross y and is unit only when the axes are
            // orthonormal, which is true of everything the kernel builds — normalize
            // anyway, because the value is about to be compared against a sine.
            var n = Unit(planeNormal, "face normal");
            double sine = n.Dot(pull);
            double degrees = Math.Asin(Math.Clamp(sine, -1, 1)) * RadiansToDegrees;
            return new DraftFaceCheck(
                index, face.Kind(), area, degrees, degrees, degrees,
                Math.Abs(sine) >= minimumSine, 1, location);
        }

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        double worstAbs = double.PositiveInfinity, worstSigned = 0;
        int taken = 0;
        foreach (var (u, v) in CurvedSamples(face, samples))
        {
            var n = face.Surface.NormalAt(u, v);
            if (n.LengthSquared <= 0)
                continue;
            n /= n.Length;
            if (face.IsReversed)
                n = -n;                       // Surface.NormalAt knows nothing about reversal.
            double sine = Math.Clamp(n.Dot(pull), -1, 1);
            min = Math.Min(min, sine);
            max = Math.Max(max, sine);
            if (Math.Abs(sine) < worstAbs)
            {
                worstAbs = Math.Abs(sine);
                worstSigned = sine;
            }
            taken++;
        }
        if (taken == 0)
            return new DraftFaceCheck(index, face.Kind(), area, 90, 90, 90, true, 0, location);

        return new DraftFaceCheck(
            index, face.Kind(), area,
            Math.Asin(min) * RadiansToDegrees,
            Math.Asin(max) * RadiansToDegrees,
            Math.Asin(worstSigned) * RadiansToDegrees,
            worstAbs >= minimumSine, taken, location);
    }

    /// <summary>
    /// (u, v) samples covering a curved face: every point of its pulled boundary loops
    /// (certainly on the face) plus an interior grid over their bounding box, gated by
    /// even-odd parity against those loops. A face whose loops WRAP the periodic
    /// direction — a full band — has no parity to run, so the whole bounding box is
    /// taken, which is exactly the face's own domain there.
    /// </summary>
    private static List<(double U, double V)> CurvedSamples(BrepFace face, int samples)
    {
        var result = new List<(double, double)>();
        List<List<Vector2d>> loops;
        try
        {
            loops = FaceGeometry.PullLoops(face, 32);
        }
        catch
        {
            loops = [];
        }

        double uMin, uMax, vMin, vMax;
        if (loops.Count == 0 || loops.All(l => l.Count < 3))
        {
            uMin = face.Surface.DomainU.Start; uMax = face.Surface.DomainU.End;
            vMin = face.Surface.DomainV.Start; vMax = face.Surface.DomainV.End;
        }
        else
        {
            uMin = vMin = double.PositiveInfinity;
            uMax = vMax = double.NegativeInfinity;
            foreach (var loop in loops)
                foreach (var p in loop)
                {
                    uMin = Math.Min(uMin, p.X); uMax = Math.Max(uMax, p.X);
                    vMin = Math.Min(vMin, p.Y); vMax = Math.Max(vMax, p.Y);
                    result.Add((p.X, p.Y));
                }
        }
        if (!(uMax > uMin) || !(vMax > vMin))
            return result;

        // Cell centres, so no sample sits exactly on a domain edge (where the base
        // class's central-difference normal silently becomes one-sided).
        var interior = new List<(double, double)>();
        for (int i = 0; i < samples; i++)
            for (int j = 0; j < samples; j++)
            {
                double u = uMin + (i + 0.5) * (uMax - uMin) / samples;
                double v = vMin + (j + 0.5) * (vMax - vMin) / samples;
                if (loops.Count == 0 || Inside(loops, u, v))
                    interior.Add((u, v));
            }
        if (interior.Count == 0)
        {
            // Parity found nothing: the loops wrap rather than enclose. Take the box.
            for (int i = 0; i < samples; i++)
                for (int j = 0; j < samples; j++)
                    interior.Add((
                        uMin + (i + 0.5) * (uMax - uMin) / samples,
                        vMin + (j + 0.5) * (vMax - vMin) / samples));
        }
        result.AddRange(interior);
        return result;
    }

    private static bool Inside(List<List<Vector2d>> loops, double u, double v)
    {
        bool inside = false;
        foreach (var loop in loops)
        {
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                var a = loop[j];
                var b = loop[i];
                if ((a.X > u) != (b.X > u))
                {
                    double t = (u - a.X) / (b.X - a.X);
                    if (a.Y + t * (b.Y - a.Y) > v)
                        inside = !inside;
                }
            }
        }
        return inside;
    }

    // ------------------------------------------------------------------ overhangs

    /// <summary>
    /// Overhang area against a print build direction. A facet's overhang angle is
    /// <c>asin(-n · build)</c> for the outward normal <c>n</c>: a VERTICAL wall reads 0,
    /// a downward-facing ceiling +90, an upward-facing surface negative. A facet needs
    /// support when that angle EXCEEDS <paramref name="thresholdDegrees"/> — strictly, so
    /// a surface drawn at exactly the stated self-supporting angle is self-supporting,
    /// which is what a designer who states 45 and draws 45 means.
    ///
    /// <para>This leg is pure mesh arithmetic and exact for the mesh it is given, with
    /// the one caveat that a mesh is not the surface it approximates: an inscribed n-gon
    /// pyramid's lateral faces are steeper than the cone they came from —
    /// <c>atan(cos(pi/n))</c>, 44.97 degrees at 64 segments for a 45-degree cone — so a
    /// nominally-at-threshold curved surface reads a shade under it and passes for a
    /// reason that is about the tessellation rather than about the rule.</para>
    /// </summary>
    /// <param name="part">The part to check.</param>
    /// <param name="buildDirection">Build (layer-stacking) direction; need not be unit length.</param>
    /// <param name="thresholdDegrees">Self-supporting angle from vertical, in degrees.</param>
    /// <param name="quality">Display-mesh quality.</param>
    public static OverhangReport CheckOverhangs(
        Part part,
        Vector3d buildDirection,
        double thresholdDegrees = 45,
        MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdDegrees);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(thresholdDegrees, 90);
        var build = Unit(buildDirection, nameof(buildDirection));
        double thresholdSine = Math.Sin(thresholdDegrees * DegreesToRadians);

        var mesh = part.GetMesh(quality);
        var facets = Facets(mesh);

        double total = 0, overhang = 0, projected = 0, steepest = -1;
        int overhangFacets = 0;
        var worst = new double[mesh.VertexCount];
        Array.Fill(worst, double.NaN);
        foreach (var facet in facets)
        {
            double dot = facet.Normal.Dot(build);
            total += facet.Area;
            // The verdict is on the dot product, not on the derived angle.
            if (-dot > thresholdSine)
            {
                overhang += facet.Area;
                projected += facet.Area * -dot;
                overhangFacets++;
            }
            steepest = Math.Max(steepest, -dot);
            Accumulate(worst, facet, dot, keepSmallerMagnitude: false);
        }

        var values = new double[mesh.VertexCount];
        for (int v = 0; v < values.Length; v++)
            values[v] = double.IsNaN(worst[v]) ? -90 : Math.Asin(Math.Clamp(-worst[v], -1, 1)) * RadiansToDegrees;

        return new OverhangReport(
            build, thresholdDegrees, total, overhang, projected,
            facets.Count == 0 ? 0 : Math.Asin(Math.Clamp(steepest, -1, 1)) * RadiansToDegrees,
            facets.Count, overhangFacets,
            MeshField.Scalar(FieldNames.OverhangAngle, "deg", values));
    }

    // -------------------------------------------------------------- wall thickness

    /// <summary>
    /// Wall thickness by an OPPOSING-FACE ray cast, and the estimator's limits are part
    /// of its contract rather than a footnote. From every display-mesh vertex a ray runs
    /// INTO the material along the reversed vertex normal; the first surface it leaves
    /// through is the opposite wall, and the reported thickness is
    /// <c>t · |n · n_hit|</c> — the perpendicular distance from the vertex to the plane
    /// of the facet it hit, not the raw ray length.
    ///
    /// <para><b>Where it is exact</b>: wherever the opposing surface is PLANAR, because
    /// the perpendicular distance from a point to a plane is exactly the ray length times
    /// the cosine between the two normals. That covers plates, ribs, bosses, webs and
    /// shelled prisms — the geometry a thickness check is actually run on — and it means
    /// a tapered wall reads its true perpendicular thickness where the raw ray length
    /// would over-report it by <c>1/cos(taper)</c>.</para>
    ///
    /// <para><b>Where it lies</b>: against a CURVED opposing surface it measures to the
    /// tangent plane at the hit, which UNDER-reports where that surface is locally convex
    /// as seen from the vertex (the far side of a bore) and OVER-reports where it is
    /// locally concave (the outer wall of a shaft, read from the bore). Because every
    /// vertex of the whole surface is probed, a wall between a convex and a concave
    /// surface is measured from both sides and the conservative reading is the one the
    /// minimum keeps. It also measures ALONG THE SURFACE NORMAL, so it reports what a
    /// caliper on that normal reports and not the largest inscribed ball: at a fillet or
    /// an inside corner the medial-axis thickness is smaller.</para>
    ///
    /// <para><b>Where it declines</b>: a ray that never leaves the material — a rib end,
    /// a boss top over a through-hole — has no opposing face, and those vertices are
    /// COUNTED (<see cref="ThicknessReport.UnmeasuredCount"/>) and given the model's own
    /// diagonal in the field. That spelling is deliberate: <see cref="FieldRange"/> skips
    /// NaN when ranging but a NaN still paints as the colour map's bottom stop, which on
    /// a thickness plot is the colour of the thinnest wall in the part — so an
    /// unmeasurable point would be drawn as the exact defect the check exists to find.
    /// The conservative end of the scale plus a number in the report is honest; a
    /// convincing-looking lie in the picture is not.</para>
    ///
    /// <para>A wall thinner than <c>1e-7</c> of the model's diagonal is below the
    /// self-hit floor and is not measured — the seam tier expressed relatively, since the
    /// ray starts ON the surface it is measuring from.</para>
    /// </summary>
    /// <param name="part">The part to check.</param>
    /// <param name="minimumThickness">Required wall thickness, in model units.</param>
    /// <param name="quality">Display-mesh quality.</param>
    public static ThicknessReport CheckThickness(
        Part part, double minimumThickness, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumThickness);

        var mesh = part.GetMesh(quality);
        var facets = Facets(mesh);
        double diagonal = mesh.ComputeBounds().Size.Length;
        double floor = diagonal * 1e-7;          // seam tier, relative: the ray starts on the surface.

        var normals = VertexNormals(mesh, facets);
        var boxes = new Aabb[facets.Count];
        var positions = mesh.ToIndexed().Positions;
        for (int i = 0; i < facets.Count; i++)
        {
            Span<Vector3d> corners = [positions[facets[i].A], positions[facets[i].B], positions[facets[i].C]];
            boxes[i] = Aabb.FromPoints(corners);
        }
        var bvh = Bvh.Build(boxes);

        var values = new double[mesh.VertexCount];
        double minimum = double.PositiveInfinity;
        var minimumAt = Vector3d.Zero;
        int below = 0, unmeasured = 0;
        var candidates = new List<int>();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var n = normals[v];
            if (n.LengthSquared <= 0)
            {
                values[v] = diagonal;
                unmeasured++;
                continue;
            }
            var origin = positions[v];
            var ray = new Ray3d(origin, -n);
            candidates.Clear();
            bvh.Query(ray, candidates);

            double best = double.PositiveInfinity;
            var bestNormal = Vector3d.Zero;
            foreach (int index in candidates)
            {
                var facet = facets[index];
                // Only an EXITING facet can be the opposite wall; an entering one is a
                // near-self hit or noise (a closed solid cannot be re-entered first).
                if (facet.Normal.Dot(n) >= 0)
                    continue;
                if (!Intersect3d.RayTriangle(ray, positions[facet.A], positions[facet.B], positions[facet.C], out double t))
                    continue;
                if (t <= floor || t >= best)
                    continue;
                best = t;
                bestNormal = facet.Normal;
            }

            if (double.IsPositiveInfinity(best))
            {
                values[v] = diagonal;
                unmeasured++;
                continue;
            }
            double thickness = best * Math.Abs(n.Dot(bestNormal));
            values[v] = thickness;
            if (thickness < minimum)
            {
                minimum = thickness;
                minimumAt = origin;
            }
            if (thickness < minimumThickness)
                below++;
        }

        return new ThicknessReport(
            double.IsPositiveInfinity(minimum) ? double.NaN : minimum,
            minimumAt, minimumThickness, mesh.VertexCount, below, unmeasured,
            MeshField.Scalar(FieldNames.WallThickness, "mm", values));
    }

    // --------------------------------------------------------------------- shared

    private readonly record struct Facet(int A, int B, int C, Vector3d Normal, double Area);

    /// <summary>
    /// The mesh's facets, fanned exactly as every consumer fans them — a quality audit
    /// that triangulates an n-gon differently from the renderer audits a mesh nobody
    /// draws (the recorded <see cref="PolygonFan"/> rule).
    /// </summary>
    private static List<Facet> Facets(HalfEdgeMesh mesh)
    {
        var (positions, faces) = mesh.ToIndexed();
        var facets = new List<Facet>(faces.Count);
        foreach (var loop in faces)
        {
            int degree = loop.Length;
            if (degree < 3)
                continue;
            int apex = PolygonFan.Apex(loop, positions);
            for (int i = 1; i <= degree - 2; i++)
            {
                int a = loop[PolygonFan.Corner(apex, degree, 0)];
                int b = loop[PolygonFan.Corner(apex, degree, i)];
                int c = loop[PolygonFan.Corner(apex, degree, i + 1)];
                var cross = (positions[b] - positions[a]).Cross(positions[c] - positions[a]);
                double length = cross.Length;
                if (!(length > 0))
                    continue;
                facets.Add(new Facet(a, b, c, cross / length, 0.5 * length));
            }
        }
        return facets;
    }

    /// <summary>
    /// Area-weighted outward vertex normals, with a RELATIVE degeneracy guard.
    /// <see cref="HalfEdgeMesh.ComputeVertexNormals"/> normalizes through
    /// <c>Tolerance.Default</c>, an absolute 1e-9 applied to a sum of cross products —
    /// i.e. to an AREA — which the recorded lesson says fails quadratically with model
    /// scale, so this check computes its own.
    /// </summary>
    private static Vector3d[] VertexNormals(HalfEdgeMesh mesh, List<Facet> facets)
    {
        var sums = new Vector3d[mesh.VertexCount];
        var areas = new double[mesh.VertexCount];
        foreach (var facet in facets)
        {
            var weighted = facet.Normal * facet.Area;
            sums[facet.A] += weighted; areas[facet.A] += facet.Area;
            sums[facet.B] += weighted; areas[facet.B] += facet.Area;
            sums[facet.C] += weighted; areas[facet.C] += facet.Area;
        }
        for (int v = 0; v < sums.Length; v++)
        {
            double length = sums[v].Length;
            sums[v] = length > areas[v] * RelativeDegeneracy ? sums[v] / length : Vector3d.Zero;
        }
        return sums;
    }

    private static void Accumulate(double[] worst, in Facet facet, double dot, bool keepSmallerMagnitude)
    {
        Take(worst, facet.A, dot, keepSmallerMagnitude);
        Take(worst, facet.B, dot, keepSmallerMagnitude);
        Take(worst, facet.C, dot, keepSmallerMagnitude);
    }

    private static void Take(double[] worst, int vertex, double dot, bool keepSmallerMagnitude)
    {
        double current = worst[vertex];
        if (double.IsNaN(current)
            || (keepSmallerMagnitude ? Math.Abs(dot) < Math.Abs(current) : dot < current))
            worst[vertex] = dot;
    }

    private static Vector3d Unit(in Vector3d v, string name)
    {
        double length = v.Length;
        if (!(length > 0))
            throw new ArgumentException($"{name} must not be the zero vector.", name);
        return v / length;
    }

    internal static string Degrees(double value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);

    internal static string Number(double value) =>
        value.ToString("G6", CultureInfo.InvariantCulture);

    internal static string Point(in Vector3d p) =>
        $"({Number(p.X)}, {Number(p.Y)}, {Number(p.Z)})";
}

/// <summary>One face's row in a <see cref="DraftReport"/>.</summary>
/// <param name="Face">Index of the face in the solid's face order.</param>
/// <param name="Kind">What kind of surface it is.</param>
/// <param name="Area">Face area (exact for planar faces, ~1-2% quadrature for curved ones).</param>
/// <param name="MinAngleDegrees">Smallest signed draft angle found on the face.</param>
/// <param name="MaxAngleDegrees">Largest signed draft angle found on the face.</param>
/// <param name="WorstReleaseDegrees">The signed angle whose MAGNITUDE is smallest —
/// the face's worst release. Zero means some point of it is parallel to the pull.</param>
/// <param name="Passes">Whether the worst release reaches the stated minimum.</param>
/// <param name="Samples">1 for a planar face (exact), the sample count for a curved one.</param>
/// <param name="Location">The face's bounds centre — a face is located by its bounds,
/// never by a plane's stored origin (which is an arbitrary in-plane point).</param>
public sealed record DraftFaceCheck(
    int Face,
    SurfaceKind Kind,
    double Area,
    double MinAngleDegrees,
    double MaxAngleDegrees,
    double WorstReleaseDegrees,
    bool Passes,
    int Samples,
    Vector3d Location)
{
    /// <summary>True when the angle was measured rather than read off one exact normal.</summary>
    public bool Sampled => Samples != 1;
}

/// <summary>
/// The result of <see cref="Manufacturability.CheckDraft"/>: a per-face verdict plus a
/// per-vertex <see cref="MeshField"/> to colour the part with.
/// </summary>
/// <param name="Pull">The unit pull direction the angles are measured against.</param>
/// <param name="MinimumAngleDegrees">The required release angle.</param>
/// <param name="Faces">Per-face rows; empty when the part has no B-Rep (see <paramref name="Note"/>).</param>
/// <param name="WorstReleaseDegrees">The smallest release angle anywhere on the part, as a
/// MAGNITUDE — which mould half it belongs to is a per-face question, so the sign lives on
/// <see cref="DraftFaceCheck.WorstReleaseDegrees"/> and not here.</param>
/// <param name="FailingArea">Total area of the faces that do not reach the minimum.</param>
/// <param name="Field">Signed draft angle per display-mesh vertex, in degrees.</param>
/// <param name="Note">Non-null when the verdict came from somewhere other than exact faces.</param>
public sealed record DraftReport(
    Vector3d Pull,
    double MinimumAngleDegrees,
    IReadOnlyList<DraftFaceCheck> Faces,
    double WorstReleaseDegrees,
    double FailingArea,
    MeshField Field,
    string? Note = null)
{
    /// <summary>The faces that do not reach the minimum, worst first.</summary>
    public IReadOnlyList<DraftFaceCheck> Failing =>
        [.. Faces.Where(f => !f.Passes).OrderBy(f => Math.Abs(f.WorstReleaseDegrees))];

    /// <summary>True when nothing on the part is below the required release angle.</summary>
    public bool Passes => Faces.Count > 0
        ? Faces.All(f => f.Passes)
        : WorstReleaseDegrees >= MinimumAngleDegrees;

    /// <summary>
    /// A ready display for <see cref="Field"/>: the DIVERGING map over a range centred on
    /// zero and saturating at twice the minimum, so the two mould halves take the two
    /// colours and the neutral midpoint is exactly the vertical band the check is about.
    /// </summary>
    public FieldDisplay Display => new()
    {
        Field = Manufacturability.FieldNames.DraftAngle,
        ColorMap = FieldColorMap.Diverging,
        Range = MinimumAngleDegrees > 0
            ? new FieldRange(-2 * MinimumAngleDegrees, 2 * MinimumAngleDegrees)
            : null,
    };

    /// <summary>The report as an aligned monospace table.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"Draft against pull {Manufacturability.Point(Pull)}, " +
            $"minimum {Manufacturability.Degrees(MinimumAngleDegrees)} deg");
        if (Note is not null)
            builder.AppendLine("! " + Note);
        foreach (var face in Failing)
        {
            builder.AppendLine(
                $"! face {face.Face} ({face.Kind}) at {Manufacturability.Point(face.Location)}: " +
                $"{Manufacturability.Degrees(face.WorstReleaseDegrees)} deg over " +
                $"{Manufacturability.Number(face.Area)} area" +
                (face.Sampled ? $" ({face.Samples} samples)" : " (exact)"));
        }
        builder.AppendLine(Passes
            ? $"{Faces.Count} face(s), all clear; worst release {Manufacturability.Degrees(WorstReleaseDegrees)} deg."
            : $"{Failing.Count} of {Faces.Count} face(s) under {Manufacturability.Degrees(MinimumAngleDegrees)} deg, " +
              $"{Manufacturability.Number(FailingArea)} of area; worst {Manufacturability.Degrees(WorstReleaseDegrees)} deg.");
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// The result of <see cref="Manufacturability.CheckOverhangs"/>.
/// </summary>
/// <param name="BuildDirection">The unit build direction.</param>
/// <param name="ThresholdDegrees">The self-supporting angle; steeper facets need support.</param>
/// <param name="TotalArea">The part's whole surface area.</param>
/// <param name="OverhangArea">Area of the facets that exceed the threshold.</param>
/// <param name="ProjectedArea">Those facets projected onto the build plane —
/// <c>sum(area · |n · build|)</c>, the footprint support material would occupy
/// (stacked overhangs are counted once each, not merged).</param>
/// <param name="SteepestDegrees">The largest overhang angle found; negative when
/// nothing on the part faces downward at all.</param>
/// <param name="FacetCount">Facets examined.</param>
/// <param name="OverhangFacetCount">Facets exceeding the threshold.</param>
/// <param name="Field">Overhang angle per display-mesh vertex, in degrees.</param>
public sealed record OverhangReport(
    Vector3d BuildDirection,
    double ThresholdDegrees,
    double TotalArea,
    double OverhangArea,
    double ProjectedArea,
    double SteepestDegrees,
    int FacetCount,
    int OverhangFacetCount,
    MeshField Field)
{
    /// <summary>True when no facet needs support.</summary>
    public bool Passes => OverhangFacetCount == 0;

    /// <summary>The overhanging share of the surface.</summary>
    public double OverhangFraction => TotalArea > 0 ? OverhangArea / TotalArea : 0;

    /// <summary>
    /// A ready display for <see cref="Field"/>: Viridis from the threshold to 90 degrees,
    /// so everything self-supporting clamps to the bottom colour and only what needs
    /// support lights up.
    /// </summary>
    public FieldDisplay Display => new()
    {
        Field = Manufacturability.FieldNames.OverhangAngle,
        ColorMap = FieldColorMap.Viridis,
        Range = ThresholdDegrees < 90 ? new FieldRange(ThresholdDegrees, 90) : null,
    };

    /// <summary>The report as a short block of text.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"Overhangs against build {Manufacturability.Point(BuildDirection)}, " +
            $"threshold {Manufacturability.Degrees(ThresholdDegrees)} deg");
        builder.AppendLine(Passes
            ? $"{FacetCount} facet(s), none need support; steepest {Manufacturability.Degrees(SteepestDegrees)} deg."
            : $"{OverhangFacetCount} of {FacetCount} facet(s) need support: " +
              $"{Manufacturability.Number(OverhangArea)} of {Manufacturability.Number(TotalArea)} area " +
              $"({Manufacturability.Degrees(OverhangFraction * 100)}%), " +
              $"{Manufacturability.Number(ProjectedArea)} projected; " +
              $"steepest {Manufacturability.Degrees(SteepestDegrees)} deg.");
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// The result of <see cref="Manufacturability.CheckThickness"/>.
/// </summary>
/// <param name="Minimum">The thinnest measured wall, or NaN when nothing was measurable.</param>
/// <param name="MinimumAt">Where that reading was taken.</param>
/// <param name="MinimumRequired">The thickness that was asked for.</param>
/// <param name="VertexCount">Display-mesh vertices probed.</param>
/// <param name="BelowCount">Vertices reading under the required thickness.</param>
/// <param name="UnmeasuredCount">Vertices whose ray found no opposing surface. These
/// carry the model's diagonal in <paramref name="Field"/> — see the remarks on
/// <see cref="Manufacturability.CheckThickness"/> for why, and not NaN.</param>
/// <param name="Field">Wall thickness per display-mesh vertex, in model units.</param>
public sealed record ThicknessReport(
    double Minimum,
    Vector3d MinimumAt,
    double MinimumRequired,
    int VertexCount,
    int BelowCount,
    int UnmeasuredCount,
    MeshField Field)
{
    /// <summary>True when no probed point reads under the required thickness.</summary>
    public bool Passes => BelowCount == 0;

    /// <summary>
    /// A ready display for <see cref="Field"/>: Viridis from zero to twice the required
    /// thickness, so the required value sits at half saturation and anything thin is dark.
    /// </summary>
    public FieldDisplay Display => new()
    {
        Field = Manufacturability.FieldNames.WallThickness,
        ColorMap = FieldColorMap.Viridis,
        Range = new FieldRange(0, 2 * MinimumRequired),
    };

    /// <summary>The report as a short block of text.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Wall thickness, minimum {Manufacturability.Number(MinimumRequired)}");
        builder.AppendLine(Passes
            ? $"{VertexCount} point(s), all clear; thinnest {Manufacturability.Number(Minimum)} " +
              $"at {Manufacturability.Point(MinimumAt)}."
            : $"{BelowCount} of {VertexCount} point(s) under {Manufacturability.Number(MinimumRequired)}; " +
              $"thinnest {Manufacturability.Number(Minimum)} at {Manufacturability.Point(MinimumAt)}.");
        if (UnmeasuredCount > 0)
            builder.AppendLine($"  {UnmeasuredCount} point(s) had no opposing surface and were not measured.");
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => ToText();
}
