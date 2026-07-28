using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Semantic surface classification of a face, as <see cref="BrepSelection.Kind"/> reports
/// it — the vocabulary behind <see cref="BrepSelection.FilterBy"/>. The classification
/// follows <see cref="BrepQueries"/> where a query exists (<c>IsPlanar</c>,
/// <c>IsCylindrical</c> — both recognize the generated forms booleans produce, not just
/// the named surface types), then falls back to the carrier surface type.
/// </summary>
public enum SurfaceKind
{
    /// <summary>A planar face (<see cref="BrepQueries.IsPlanar"/>: <see cref="PlaneSurface"/>
    /// or an extruded straight line — the side wall of every extrusion).</summary>
    Planar,

    /// <summary>A cylindrical face (<see cref="BrepQueries.IsCylindrical"/>:
    /// <see cref="CylinderSurface"/>, extruded circles, axis-parallel revolved lines —
    /// bores and bosses).</summary>
    Cylindrical,

    /// <summary>A revolved straight generator that is not axis-parallel — chamfer bands on
    /// circular rims, countersinks, <c>MakeCone</c> sides. A generator revolved exactly
    /// perpendicular to the axis (a flat annulus, the 90° half-angle case) also lands here,
    /// because <see cref="BrepQueries.IsPlanar"/> deliberately does not recognize it.</summary>
    Conical,

    /// <summary>A <see cref="SphereSurface"/> face, or a revolved circular-arc generator
    /// centred ON the axis — the sphere factory's hemispheres. Generators are classified
    /// by sampling, so it does not matter which curve type spelled the arc.</summary>
    Spherical,

    /// <summary>A revolved circular-arc generator centred OFF the axis — torus bands and
    /// fillet bands on circular rims (quarter-torus surfaces).</summary>
    Toroidal,

    /// <summary>Any other <see cref="RevolvedSurface"/> face (a revolved sketch's
    /// free-form runs).</summary>
    Revolved,

    /// <summary>Any other <see cref="ExtrudedSurface"/> face (extruded béziers — engraved
    /// text walls — and other curved generators).</summary>
    Extruded,

    /// <summary>A <see cref="SweptSurface"/> or <see cref="HelicalSurface"/> face (sweeps
    /// along paths; modeled threads).</summary>
    Swept,

    /// <summary>A <see cref="NurbsSurface"/> or <see cref="LoftedSurface"/> face.</summary>
    Nurbs,

    /// <summary>A surface type this classification does not name.</summary>
    Other,
}

/// <summary>
/// The ordering/grouping layer over face and edge sequences — the selection vocabulary
/// build123d spells <c>sort_by</c>/<c>group_by</c>/<c>filter_by</c> and CadQuery spells
/// with string selectors, expressed as LINQ-composable extension methods instead:
/// <c>solid.Faces.FilterBy(SurfaceKind.Planar).SortAlong(Vector3d.UnitZ)</c>,
/// <c>solid.Faces.GroupAlong(Vector3d.UnitZ)[1]</c>, <c>faces.LargestByArea()</c>.
/// String selectors are deliberately not imitated — a lambda over these methods is
/// type-safe and strictly more expressive.
///
/// <para><b>Determinism.</b> Every method is stable: sorting preserves input order among
/// ties, <c>Extreme</c> keeps the first of equal candidates (the same strictly-greater
/// rule <c>FaceRef.Extreme</c> uses), and groups appear in ascending rank order (or first
/// appearance, for <see cref="GroupByCoplanar"/>). Selection driven by these queries
/// therefore cannot flicker between regenerations.</para>
///
/// <para><b>Ranking rule.</b> A face's position along a direction is its plane's offset
/// when planar (so "the highest +Z face" is exactly the plane's z) and its
/// <see cref="BrepQueries.Bounds(BrepFace)"/> centre otherwise; an edge's is its curve
/// midpoint. These match the <c>FaceRef.Extreme</c> convention in EngrCAD.Modeling.</para>
/// </summary>
public static class BrepSelection
{
    // ---- classification ----

    /// <summary>The semantic <see cref="SurfaceKind"/> of a face — see the enum for the
    /// exact mapping. Classification precedence: planar, cylindrical, then the revolved
    /// sub-kinds, then the carrier surface type.</summary>
    public static SurfaceKind Kind(this BrepFace face)
    {
        if (face.IsPlanar(out _, out _))
            return SurfaceKind.Planar;
        if (face.IsCylindrical(out _, out _, out _))
            return SurfaceKind.Cylindrical;
        switch (face.Surface)
        {
            case SphereSurface:
                return SurfaceKind.Spherical;
            case RevolvedSurface revolved:
                return ClassifyRevolved(revolved);
            case ExtrudedSurface:
                return SurfaceKind.Extruded;
            case SweptSurface or HelicalSurface:
                return SurfaceKind.Swept;
            case NurbsSurface or LoftedSurface:
                return SurfaceKind.Nurbs;
            default:
                return SurfaceKind.Other;
        }
    }

    /// <summary>
    /// Classifies a revolved band by SAMPLING its generator rather than by curve type —
    /// the kernel builds circular generators three ways (<see cref="Circle3d"/>,
    /// <see cref="CurveSegment"/> over one, rational-arc <see cref="NurbsCurve"/> — the
    /// sphere and torus factories use the last), and a type switch would classify the
    /// same geometry differently depending on which constructor produced it. Guards are
    /// relative to the generator's extent (the scale-free tier).
    /// </summary>
    private static SurfaceKind ClassifyRevolved(RevolvedSurface revolved)
    {
        var generator = revolved.Generator;
        var domain = generator.Domain;
        Span<Vector3d> p = stackalloc Vector3d[5];
        for (int i = 0; i < 5; i++)
            p[i] = generator.PointAt(domain.ParameterAt(i / 4.0));

        double extent = 0;
        for (int i = 1; i < 5; i++)
            extent = Math.Max(extent, (p[i] - p[0]).Length);
        if (extent <= 0)
            return SurfaceKind.Revolved;
        double tolerance = 1e-9 * extent;

        // Straight generator → cone (axis-parallel is Cylindrical, decided earlier).
        var chord = p[4] - p[0];
        bool straight = true;
        for (int i = 1; i < 4 && straight; i++)
        {
            var offset = p[i] - p[0];
            straight = offset.Cross(chord).Length <= tolerance * extent;
        }
        if (straight)
            return SurfaceKind.Conical;

        // Circular-arc generator: circumcentre of three samples, other two on the circle.
        var ab = p[2] - p[0];
        var ac = p[4] - p[0];
        var cross = ab.Cross(ac);
        double crossSq = cross.LengthSquared;
        if (crossSq <= tolerance * tolerance * extent * extent)
            return SurfaceKind.Revolved;
        var toCentre = (cross.Cross(ab) * ac.LengthSquared + ac.Cross(cross) * ab.LengthSquared)
            / (2 * crossSq);
        var centre = p[0] + toCentre;
        double radius = toCentre.Length;
        bool circular = Math.Abs((p[1] - centre).Length - radius) <= tolerance
                     && Math.Abs((p[3] - centre).Length - radius) <= tolerance;
        if (!circular)
            return SurfaceKind.Revolved;

        var axis = revolved.AxisDirection.Normalized();
        var centreOffset = centre - revolved.AxisOrigin;
        double radial = (centreOffset - axis * centreOffset.Dot(axis)).Length;
        return radial <= tolerance ? SurfaceKind.Spherical : SurfaceKind.Toroidal;
    }

    /// <summary>Faces of the given <see cref="SurfaceKind"/> — build123d's
    /// <c>filter_by(GeomType...)</c>. Order is preserved.</summary>
    public static IEnumerable<BrepFace> FilterBy(this IEnumerable<BrepFace> faces, SurfaceKind kind) =>
        faces.Where(f => f.Kind() == kind);

    // ---- ranking along a direction ----

    /// <summary>
    /// A face's position along a direction. A planar face whose normal is parallel to the
    /// direction ranks by its plane's exact offset (so "the highest +Z face" is exactly
    /// the plane's z — the <c>FaceRef.Extreme</c> convention); every other face ranks by
    /// its <see cref="BrepQueries.Bounds(BrepFace)"/> centre. The parallel guard matters:
    /// a plane's stored origin is an arbitrary point IN the plane, so only its component
    /// along the plane's own normal is well-defined — a box's side wall must rank by its
    /// centre when grouped along Z, not by wherever its origin happens to sit.
    /// </summary>
    public static double RankAlong(this BrepFace face, in Vector3d direction)
    {
        if (face.IsPlanar(out var origin, out var normal))
        {
            double length = direction.Length;
            if (length > 0 && Math.Abs(normal.Dot(direction)) > (1 - 1e-9) * length)
                return origin.Dot(direction);
        }
        return face.Bounds().Center.Dot(direction);
    }

    /// <summary>An edge's position along a direction: its curve midpoint. The midpoint is
    /// an exact curve point, so ranks of exactly-constructed edges carry no sampling
    /// error.</summary>
    public static double RankAlong(this BrepEdge edge, in Vector3d direction) =>
        edge.Curve.PointAt(edge.Domain.Mid).Dot(direction);

    /// <summary>Faces sorted ascending along <paramref name="direction"/> (stable — ties
    /// keep input order). <c>faces.SortAlong(axis)[^1]</c> is the furthest face;
    /// <see cref="Extreme(IEnumerable{BrepFace}, in Vector3d)"/> spells that directly.</summary>
    public static IReadOnlyList<BrepFace> SortAlong(this IEnumerable<BrepFace> faces, Vector3d direction) =>
        [.. faces.OrderBy(f => f.RankAlong(direction))];

    /// <inheritdoc cref="SortAlong(IEnumerable{BrepFace}, Vector3d)"/>
    public static IReadOnlyList<BrepEdge> SortAlong(this IEnumerable<BrepEdge> edges, Vector3d direction) =>
        [.. edges.OrderBy(e => e.RankAlong(direction))];

    /// <summary>The face furthest along <paramref name="direction"/>; ties keep the first
    /// in input order (strictly-greater comparison, matching <c>FaceRef.Extreme</c>).
    /// Throws on an empty sequence.</summary>
    public static BrepFace Extreme(this IEnumerable<BrepFace> faces, in Vector3d direction)
    {
        BrepFace? best = null;
        double bestRank = double.NegativeInfinity;
        foreach (var face in faces)
        {
            double rank = face.RankAlong(direction);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = face;
            }
        }
        return best ?? throw new InvalidOperationException(
            "Extreme: the face sequence is empty — nothing to select from.");
    }

    /// <inheritdoc cref="Extreme(IEnumerable{BrepFace}, in Vector3d)"/>
    public static BrepEdge Extreme(this IEnumerable<BrepEdge> edges, in Vector3d direction)
    {
        BrepEdge? best = null;
        double bestRank = double.NegativeInfinity;
        foreach (var edge in edges)
        {
            double rank = edge.RankAlong(direction);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = edge;
            }
        }
        return best ?? throw new InvalidOperationException(
            "Extreme: the edge sequence is empty — nothing to select from.");
    }

    /// <summary>The face furthest along +Z (build123d's <c>faces().sort_by(Axis.Z)[-1]</c>).</summary>
    public static BrepFace Highest(this IEnumerable<BrepFace> faces) => faces.Extreme(Vector3d.UnitZ);

    /// <summary>The face furthest along −Z.</summary>
    public static BrepFace Lowest(this IEnumerable<BrepFace> faces) => faces.Extreme(-Vector3d.UnitZ);

    /// <inheritdoc cref="Highest(IEnumerable{BrepFace})"/>
    public static BrepEdge Highest(this IEnumerable<BrepEdge> edges) => edges.Extreme(Vector3d.UnitZ);

    /// <inheritdoc cref="Lowest(IEnumerable{BrepFace})"/>
    public static BrepEdge Lowest(this IEnumerable<BrepEdge> edges) => edges.Extreme(-Vector3d.UnitZ);

    // ---- grouping ----

    /// <summary>
    /// Faces grouped by their rank along <paramref name="direction"/>, groups ascending —
    /// build123d's <c>group_by(Axis...)</c>. <c>solid.Faces.GroupAlong(axis)[n]</c> is
    /// "the n-th level"; index from the end for "the second face from the top". Two ranks
    /// belong to one group when they differ by at most <paramref name="tolerance"/>
    /// (default: the 1e-9 weld tier — plane offsets of exactly-constructed geometry are
    /// exact, so this is an equality test, not a measurement band).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<BrepFace>> GroupAlong(
        this IEnumerable<BrepFace> faces, Vector3d direction, double tolerance = 1e-9) =>
        GroupByRank(faces, f => f.RankAlong(direction), tolerance);

    /// <inheritdoc cref="GroupAlong(IEnumerable{BrepFace}, Vector3d, double)"/>
    public static IReadOnlyList<IReadOnlyList<BrepEdge>> GroupAlong(
        this IEnumerable<BrepEdge> edges, Vector3d direction, double tolerance = 1e-9) =>
        GroupByRank(edges, e => e.RankAlong(direction), tolerance);

    /// <summary>
    /// Faces grouped by coplanarity: two planar faces share a group when their outward
    /// normals agree and their plane offsets match (both at the 1e-9 weld tier — planes of
    /// exactly-constructed geometry compare exactly). Non-planar faces are never grouped;
    /// each becomes its own singleton group. Groups appear in order of first appearance,
    /// so the result is deterministic and no face is dropped.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<BrepFace>> GroupByCoplanar(this IEnumerable<BrepFace> faces)
    {
        var groups = new List<List<BrepFace>>();
        var planes = new List<(Vector3d Normal, double Offset)?>();
        foreach (var face in faces)
        {
            if (face.IsPlanar(out var origin, out var normal))
            {
                double offset = origin.Dot(normal);
                int found = -1;
                for (int i = 0; i < groups.Count; i++)
                {
                    if (planes[i] is not { } plane)
                        continue;
                    if (plane.Normal.Dot(normal) > 1 - 1e-9 && Math.Abs(plane.Offset - offset) <= 1e-9)
                    {
                        found = i;
                        break;
                    }
                }
                if (found >= 0)
                {
                    groups[found].Add(face);
                    continue;
                }
                groups.Add([face]);
                planes.Add((normal, offset));
            }
            else
            {
                groups.Add([face]);
                planes.Add(null);
            }
        }
        return groups;
    }

    // ---- radius queries ----

    /// <summary>
    /// Cylindrical faces grouped by radius, radii ascending — non-cylindrical faces are
    /// ignored. Radii come off exactly-constructed surfaces, so grouping uses the 1e-9
    /// weld tier.
    /// </summary>
    public static IReadOnlyList<(double Radius, IReadOnlyList<BrepFace> Faces)> GroupByRadius(
        this IEnumerable<BrepFace> faces)
    {
        var cylindrical = new List<(double Radius, BrepFace Face)>();
        foreach (var face in faces)
        {
            if (face.IsCylindrical(out _, out _, out double radius))
                cylindrical.Add((radius, face));
        }
        return GroupMeasured(cylindrical);
    }

    /// <summary>Circular edges grouped by radius, radii ascending — non-circular edges
    /// are ignored.</summary>
    public static IReadOnlyList<(double Radius, IReadOnlyList<BrepEdge> Edges)> GroupByRadius(
        this IEnumerable<BrepEdge> edges)
    {
        var circular = new List<(double Radius, BrepEdge Edge)>();
        foreach (var edge in edges)
        {
            if (edge.IsCircular(out _, out _, out double radius))
                circular.Add((radius, edge));
        }
        return GroupMeasured(circular);
    }

    /// <summary>
    /// The cylindrical faces carrying the <paramref name="index"/>-th smallest distinct
    /// radius (0 = smallest; negative counts from the largest, so −1 is the largest).
    /// Fails loudly naming the radii that do exist when the index is out of range or no
    /// face is cylindrical.
    /// </summary>
    public static IReadOnlyList<BrepFace> NthByRadius(this IEnumerable<BrepFace> faces, int index)
    {
        var groups = faces.GroupByRadius();
        return NthGroup(groups.Select(g => (g.Radius, g.Faces)).ToList(), index, "cylindrical face");
    }

    /// <inheritdoc cref="NthByRadius(IEnumerable{BrepFace}, int)"/>
    public static IReadOnlyList<BrepEdge> NthByRadius(this IEnumerable<BrepEdge> edges, int index)
    {
        var groups = edges.GroupByRadius();
        return NthGroup(groups.Select(g => (g.Radius, g.Edges)).ToList(), index, "circular edge");
    }

    // ---- area ----

    /// <summary>
    /// The face's surface area.
    ///
    /// <para><b>Planar faces are exact</b> for straight edges and directly-parameterized
    /// circular arcs (<see cref="Circle3d"/> edges and <see cref="CurveSegment"/>s over
    /// one — everything box sides, caps, drilled rims and rim features produce): the area
    /// is the boundary integral ∮ ½ (p − o) × dp · n̂ with closed-form arc terms. Other
    /// curved boundary runs (bézier profiles) are chord-sampled at
    /// <paramref name="samplesPerCurve"/> points, inscribed O(h²).</para>
    ///
    /// <para><b>Curved faces are quadrature</b>: the pulled-back trim loops gate a
    /// <paramref name="quadrature"/>² midpoint grid over the parameter domain, weighted by
    /// the surface's area element (central differences). Boundary cells decide by their
    /// midpoint, so the error is O(perimeter · cell) — about 1–2% at the default 48. That
    /// is ordering-grade accuracy for <see cref="LargestByArea(IEnumerable{BrepFace})"/>
    /// and friends, not mass-property grade: use <c>BrepMassProperties</c> (Interop) when
    /// the number itself matters.</para>
    /// </summary>
    public static double Area(this BrepFace face, int samplesPerCurve = 64, int quadrature = 48)
    {
        if (face.IsPlanar(out var planePoint, out var normal))
            return PlanarArea(face, planePoint, normal, samplesPerCurve);
        return QuadratureArea(face, samplesPerCurve, quadrature);
    }

    /// <summary>The face of largest <see cref="Area"/>; ties keep the first in input
    /// order. Throws on an empty sequence. See <see cref="Area"/> for the accuracy
    /// contract — exact for planar faces, quadrature (~1–2%) for curved ones, which is
    /// what makes this an ordering query rather than a measurement.</summary>
    public static BrepFace LargestByArea(this IEnumerable<BrepFace> faces)
    {
        BrepFace? best = null;
        double bestArea = double.NegativeInfinity;
        foreach (var face in faces)
        {
            double area = face.Area();
            if (area > bestArea)
            {
                bestArea = area;
                best = face;
            }
        }
        return best ?? throw new InvalidOperationException(
            "LargestByArea: the face sequence is empty — nothing to select from.");
    }

    /// <summary>Faces sorted ascending by <see cref="Area"/> (stable).</summary>
    public static IReadOnlyList<BrepFace> SortByArea(this IEnumerable<BrepFace> faces) =>
        [.. faces.OrderBy(f => f.Area())];

    // ---- implementation ----

    private static IReadOnlyList<IReadOnlyList<T>> GroupByRank<T>(
        IEnumerable<T> items, Func<T, double> rank, double tolerance)
    {
        // Stable: sort (rank, input index), then split where the gap exceeds the
        // tolerance. Chained near-equal ranks merge transitively, which is the intent —
        // a group is a cluster, and exactly-constructed ranks cluster at exact equality.
        var ranked = items.Select((item, i) => (Item: item, Rank: rank(item), Index: i))
            .OrderBy(e => e.Rank).ThenBy(e => e.Index).ToList();
        var groups = new List<IReadOnlyList<T>>();
        int start = 0;
        for (int i = 1; i <= ranked.Count; i++)
        {
            if (i == ranked.Count || ranked[i].Rank - ranked[i - 1].Rank > tolerance)
            {
                groups.Add([.. ranked.Skip(start).Take(i - start).Select(e => e.Item)]);
                start = i;
            }
        }
        return groups;
    }

    private static IReadOnlyList<(double Radius, IReadOnlyList<T> Items)> GroupMeasured<T>(
        List<(double Radius, T Item)> measured)
    {
        measured.Sort((a, b) => a.Radius.CompareTo(b.Radius));
        var groups = new List<(double Radius, IReadOnlyList<T> Items)>();
        int start = 0;
        for (int i = 1; i <= measured.Count; i++)
        {
            // Weld tier: radii of exactly-constructed surfaces are exact.
            if (i == measured.Count || measured[i].Radius - measured[i - 1].Radius > 1e-9)
            {
                groups.Add((measured[start].Radius,
                    [.. measured.Skip(start).Take(i - start).Select(e => e.Item)]));
                start = i;
            }
        }
        return groups;
    }

    private static IReadOnlyList<T> NthGroup<T>(
        List<(double Radius, IReadOnlyList<T> Items)> groups, int index, string subject)
    {
        if (groups.Count == 0)
            throw new InvalidOperationException(
                $"NthByRadius: no {subject} in the selection — nothing has a radius.");
        int resolved = index < 0 ? groups.Count + index : index;
        if (resolved < 0 || resolved >= groups.Count)
            throw new InvalidOperationException(
                $"NthByRadius: index {index} is out of range — {groups.Count} distinct " +
                $"radius group(s) exist ({string.Join(", ", groups.Select(g => g.Radius.ToString("g6")))}).");
        return groups[resolved].Items;
    }

    private static double PlanarArea(
        BrepFace face, in Vector3d planePoint, in Vector3d normal, int samplesPerCurve)
    {
        // Signed boundary integral A = Σ_loops ∮ ½ (p − o) × dp · n̂ with one fixed anchor
        // o for every loop (anchor-independence holds only per closed loop with a FIXED
        // anchor, so mixing anchors between edges of one loop would be wrong). The
        // traversal orientation makes hole loops subtract automatically; |·| at the end
        // absorbs the face's outward-vs-parameter orientation.
        var o = planePoint;
        double total = 0;
        foreach (var loop in face.Loops)
        {
            double area = 0;
            foreach (var coedge in loop.Coedges)
            {
                var edge = coedge.Edge;
                var domain = edge.Domain;
                double tStart = coedge.SameSense ? domain.Start : domain.End;
                double tEnd = coedge.SameSense ? domain.End : domain.Start;
                var a = edge.Curve.PointAt(tStart);
                var b = edge.Curve.PointAt(tEnd);

                switch (edge.Curve)
                {
                    // Straight chord: exact.
                    case Line3d:
                    case { Underlying: Line3d }:
                        area += 0.5 * (a - o).Cross(b - a).Dot(normal);
                        break;

                    // Directly-parameterized circle: parameter IS the angle, so the arc's
                    // contribution about its own centre is ½ r² Δθ (ẑ·n̂), plus the anchor
                    // shift term. Exact.
                    case Circle3d circle:
                    {
                        double sweep = (domain.Length) * (coedge.SameSense ? 1 : -1);
                        area += 0.5 * circle.Radius * circle.Radius * sweep * circle.Axis.Dot(normal)
                              + 0.5 * (circle.Center - o).Cross(b - a).Dot(normal);
                        break;
                    }

                    // A [0,1]-reparameterized piece of a circle: the base sweep scales
                    // linearly, so this is exact too (bore rims after boolean splitting).
                    case CurveSegment segment when segment.Base is Circle3d circle:
                    {
                        double sweep = (segment.BaseEnd - segment.BaseStart) * domain.Length
                            * (coedge.SameSense ? 1 : -1);
                        area += 0.5 * circle.Radius * circle.Radius * sweep * circle.Axis.Dot(normal)
                              + 0.5 * (circle.Center - o).Cross(b - a).Dot(normal);
                        break;
                    }

                    // Any other curve: inscribed chord sampling, O(h²).
                    default:
                    {
                        var previous = a;
                        for (int i = 1; i <= samplesPerCurve; i++)
                        {
                            double t = tStart + (tEnd - tStart) * i / samplesPerCurve;
                            var p = edge.Curve.PointAt(t);
                            area += 0.5 * (previous - o).Cross(p - previous).Dot(normal);
                            previous = p;
                        }
                        break;
                    }
                }
            }
            total += area;
        }
        return Math.Abs(total);
    }

    private static double QuadratureArea(BrepFace face, int samplesPerCurve, int quadrature)
    {
        // Midpoint quadrature over the pulled-back trim region: loops are pulled ONCE
        // (FaceGeometry's exact-sample rules, periodic u unwrapped), then each grid cell's
        // midpoint decides membership by even-odd parity and contributes the surface's
        // area element |∂P/∂u × ∂P/∂v| du dv (central differences). Boundary cells decide
        // by midpoint, hence the documented O(perimeter · cell) error.
        var loops = FaceGeometry.PullLoops(face, samplesPerCurve);
        double period = FaceGeometry.PeriodU(face.Surface);

        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
        foreach (var loop in loops)
        {
            foreach (var p in loop)
            {
                minU = Math.Min(minU, p.X);
                maxU = Math.Max(maxU, p.X);
                minV = Math.Min(minV, p.Y);
                maxV = Math.Max(maxV, p.Y);
            }
        }
        if (!double.IsFinite(minU) || maxU - minU <= 0 || maxV - minV <= 0)
            return 0;

        var surface = face.Surface;
        double du = (maxU - minU) / quadrature;
        double dv = (maxV - minV) / quadrature;
        double hu = du * 1e-3, hv = dv * 1e-3;
        double area = 0;
        for (int i = 0; i < quadrature; i++)
        {
            double u = minU + (i + 0.5) * du;
            for (int j = 0; j < quadrature; j++)
            {
                double v = minV + (j + 0.5) * dv;
                if (!InsideLoops(loops, new Vector2d(u, v), period))
                    continue;
                var pu = surface.PointAt(u + hu, v) - surface.PointAt(u - hu, v);
                var pv = surface.PointAt(u, v + hv) - surface.PointAt(u, v - hv);
                area += pu.Cross(pv).Length / (4 * hu * hv) * du * dv;
            }
        }
        return area;
    }

    /// <summary>Even-odd parity of a uv point against pulled loops, with the same
    /// periodic-segment compaction <see cref="FaceGeometry.Contains"/> uses.</summary>
    private static bool InsideLoops(List<List<Vector2d>> loops, in Vector2d uv, double period)
    {
        int crossings = 0;
        foreach (var loop in loops)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (period > 0)
                {
                    b = new Vector2d(b.X + period * Math.Round((a.X - b.X) / period), b.Y);
                    double mid = (a.X + b.X) / 2;
                    double shift = period * Math.Round((uv.X - mid) / period);
                    a = new Vector2d(a.X + shift, a.Y);
                    b = new Vector2d(b.X + shift, b.Y);
                }
                bool straddles = a.X <= uv.X != b.X <= uv.X;
                if (!straddles)
                    continue;
                double t = (uv.X - a.X) / (b.X - a.X);
                if (a.Y + t * (b.Y - a.Y) > uv.Y)
                    crossings++;
            }
        }
        return (crossings & 1) == 1;
    }
}
