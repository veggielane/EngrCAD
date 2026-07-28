using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A facet-vs-surface quality audit of a B-Rep tessellation, attributing every emitted
/// triangle to the face — and therefore the exact surface — it is meant to approximate.
/// <para>This exists because every other assertion the suite makes about a tessellation
/// is blind to orientation. The mitered rim fillets were closed, Euler-clean and within
/// volume tolerance while 808 of their 13 088 triangles faced INWARD; the cross-drilled
/// bore wall had zero inverted triangles both before and after its fix while carrying an
/// 88.9-degree sliver (dot 0.0198). So the metric here is the WORST normal dot, and the
/// fold count is reported alongside it rather than instead of it.</para>
/// <para>Three rules are load-bearing, each learned by getting it wrong:</para>
/// <list type="bullet">
/// <item>The reference normal is the mean of the surface normals at the triangle's three
/// VERTICES, never the normal at its centroid. A centroid sits a sagitta (order 1e-3 at
/// display quality) inside a curved surface, far past the 1e-6 inverse-evaluation
/// tolerance, so projecting it fails and a centroid-based assertion silently checks
/// nothing. The vertices are exact surface points by construction.</item>
/// <item>The audit runs on UNWELDED per-face polygons
/// (<see cref="BRepTessellator.TessellateByFace"/>), because welding is exactly what
/// destroys the triangle → face attribution this measurement needs.</item>
/// <item>Polygons are FANNED from vertex 0, because that is what the render mesh does
/// with a grid quad downstream. Auditing a quad as a unit would have missed the whole
/// reversed-face defect, whose entire mechanism is that reversing the vertex order moves
/// the fan diagonal.</item>
/// </list>
/// </summary>
internal static class TessellationQuality
{
    /// <summary>
    /// One face's audit. <see cref="WorstDot"/> is 1 for a face that emitted nothing
    /// judgeable, which cannot mask a defect because <see cref="Unjudged"/> and
    /// <see cref="Slivers"/> are asserted separately.
    /// </summary>
    internal readonly record struct FaceReport(
        BrepFace Face,
        int Triangles,
        int Folds,
        double WorstDot,
        Vector3d WorstAt,
        int Unprojectable,
        int Unjudged,
        int Slivers)
    {
        internal string Family => Face.Surface.GetType().Name;
    }

    /// <param name="Refusals">Faces the tessellation path under audit declined, each with
    /// the reason it gave. Empty for <see cref="Audit"/>, which routes through production
    /// and therefore throws rather than declining.</param>
    internal readonly record struct SolidReport(
        IReadOnlyList<FaceReport> Faces, IReadOnlyList<string> Refusals)
    {
        internal int Triangles => Faces.Sum(f => f.Triangles);

        /// <summary>Facets whose normal opposes their surface's — geometry that renders
        /// inside out and shades as a hole.</summary>
        internal int Folds => Faces.Sum(f => f.Folds);

        /// <summary>Triangle vertices that do not lie on their own face's surface.</summary>
        internal int Unprojectable => Faces.Sum(f => f.Unprojectable);

        /// <summary>Facets whose reference normal could not be established at all.</summary>
        internal int Unjudged => Faces.Sum(f => f.Unjudged);

        /// <summary>Facets too thin for their own normal to mean anything (see
        /// <see cref="AuditFace"/>).</summary>
        internal int Slivers => Faces.Sum(f => f.Slivers);

        /// <summary>The surface families of the faces that emitted degenerate facets.</summary>
        internal IEnumerable<string> SliverFamilies =>
            Faces.Where(f => f.Slivers > 0).Select(f => f.Family).Distinct().Order();

        /// <summary>The most degenerate facets any single face emitted.</summary>
        internal int WorstFaceSlivers => Faces.Count == 0 ? 0 : Faces.Max(f => f.Slivers);

        internal FaceReport Worst => Faces.Count == 0 ? default : Faces.MinBy(f => f.WorstDot);

        internal double WorstDot => Faces.Count == 0 ? 1 : Worst.WorstDot;

        internal string Describe()
        {
            var worst = Worst;
            return $"{Triangles} facet(s), {Folds} folded, worst normal agreement " +
                $"{worst.WorstDot:F6} on a {worst.Family}" +
                $"{(worst.Face?.IsReversed == true ? " (reversed)" : "")} at {worst.WorstAt}; " +
                $"{Unprojectable} vertex/vertices off their own surface, {Unjudged} unjudgeable, " +
                $"{Slivers} degenerate sliver(s)" +
                $"{(Refusals.Count == 0 ? "" : $", {Refusals.Count} refusal(s): {string.Join("; ", Refusals)}")}. " +
                "Per family: " + string.Join(", ", Faces
                    .GroupBy(f => f.Family)
                    .OrderBy(g => g.Key)
                    .Select(g => $"{g.Key} x{g.Count()} worst {g.Min(f => f.WorstDot):F6}"));
        }
    }

    /// <summary>
    /// Audits the tessellation production actually emits: every face routed exactly as
    /// <see cref="BRepTessellator.Tessellate"/> routes it, planar caps and natural grids
    /// included.
    /// </summary>
    internal static SolidReport Audit(BrepSolid solid, int segmentsPerCircle = 32, int curveSamples = 24)
    {
        var faces = BRepTessellator
            .TessellateByFace(solid, segmentsPerCircle, curveSamples)
            .Select(f => AuditFace(f.Face, f.Polygons))
            .ToList();
        return new SolidReport(faces, []);
    }

    /// <summary>
    /// Audits every non-planar face driven through <see cref="TrimmedFaceTessellator"/>
    /// specifically, whatever the production router would have chosen. That is a stronger
    /// claim than <see cref="Audit"/> where the two agree and a different one where they
    /// do not, which is exactly what a test about the trimmed tiers wants: a face that
    /// quietly took the natural grid instead would otherwise pass by not being measured.
    /// <para>Reversed faces are flipped here as the tessellator flips them, so both entry
    /// points report the same convention.</para>
    /// </summary>
    internal static SolidReport AuditTrimmedPath(
        BrepSolid solid, int segmentsPerCircle, int curveSamples)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = BRepTessellator.SampleEdge(edge, segmentsPerCircle, curveSamples);

        var faces = new List<FaceReport>();
        var refusals = new List<string>();
        foreach (var face in solid.Faces)
        {
            if (face.Surface is PlaneSurface)
                continue;
            var polygons = new List<IReadOnlyList<Vector3d>>();
            if (!TrimmedFaceTessellator.TryTessellate(
                    face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out string? why))
            {
                refusals.Add($"{face.Surface.GetType().Name}: {why}");
                continue;
            }
            if (face.IsReversed)
            {
                for (int i = 0; i < polygons.Count; i++)
                    polygons[i] = [.. polygons[i].Reverse()];
            }
            faces.Add(AuditFace(face, polygons));
        }
        return new SolidReport(faces, refusals);
    }

    private static FaceReport AuditFace(BrepFace face, List<IReadOnlyList<Vector3d>> polygons)
    {
        // The tessellator reverses a reversed face's polygons, so the surface normal they
        // should agree with is reversed too.
        double sense = face.IsReversed ? -1 : 1;
        int triangles = 0, folds = 0, unprojectable = 0, unjudged = 0, slivers = 0;
        double worst = 1;
        var worstAt = Vector3d.Zero;

        // The face's own size, so the degeneracy test below is RELATIVE. An absolute one
        // applied to a triangle's height would be a model-unit tolerance inside a
        // measurement that has to work at any scale.
        var bounds = Aabb.Empty;
        foreach (var polygon in polygons)
        {
            foreach (var p in polygon)
                bounds = bounds.Union(p);
        }
        double extent = polygons.Count == 0 ? 0 : bounds.Size.Length;

        foreach (var polygon in polygons)
        {
            for (int k = 1; k + 1 < polygon.Count; k++)
            {
                var (a, b, c) = (polygon[0], polygon[k], polygon[k + 1]);
                var normal = (b - a).Cross(c - a);
                triangles++;

                // A facet thinner than round-off carries NO orientation: its normal
                // direction is decided by the last bits of its vertices. That is the
                // mechanism behind "a zero-area triangle near a tangency is actively
                // WRONG", so it is counted on its own rather than folded into the fold
                // count — "the normal is noise" and "the normal points the wrong way" are
                // different defects with different fixes, and both get asserted.
                // The test is a HEIGHT (twice the area over the longest edge) against the
                // face's own extent, at the scale-free 1e-13 tier.
                double longest = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
                if (longest <= 0 || normal.Length / longest <= 1e-13 * extent ||
                    !TryDirection(normal, out var facet))
                {
                    slivers++;
                    continue;
                }

                var reference = Vector3d.Zero;
                foreach (var p in (ReadOnlySpan<Vector3d>)[a, b, c])
                {
                    if (!face.Surface.TryProjectPoint(p, out var uv, FaceGeometry.InverseEvaluationTolerance))
                    {
                        unprojectable++;
                        continue;
                    }
                    // A pole has no tangent plane, so its normal is undefined (the surface
                    // reports that by refusing); the triangle's two ring-side vertices
                    // carry the orientation on their own.
                    if (TrySurfaceNormal(face.Surface, uv, out var n))
                        reference += n;
                }
                if (!TryDirection(reference, out var exact))
                {
                    unjudged++;
                    continue;
                }

                double dot = sense * facet.Dot(exact);
                if (dot < worst)
                {
                    worst = dot;
                    worstAt = a;
                }
                if (dot <= 0)
                    folds++;
            }
        }
        return new FaceReport(face, triangles, folds, worst, worstAt, unprojectable, unjudged, slivers);
    }

    /// <summary>
    /// The surface's unit normal, or false where the surface has no tangent plane — the
    /// pole of an axis-touching revolve, where both partial derivatives collapse and
    /// <see cref="Surface.NormalAt"/> refuses rather than inventing a direction. A pole
    /// vertex is a genuine surface point, so this is not an error to report; the
    /// triangle's other two vertices decide its orientation.
    /// </summary>
    private static bool TrySurfaceNormal(Surface surface, in Vector2d uv, out Vector3d normal)
    {
        try
        {
            return TryDirection(surface.NormalAt(uv.X, uv.Y), out normal);
        }
        catch (InvalidOperationException)
        {
            normal = Vector3d.Zero;
            return false;
        }
    }

    /// <summary>
    /// The unit vector, or false for an EXACTLY zero one. Deliberately exact rather than
    /// tolerance-based: a facet's orientation is determinate at any nonzero area, and the
    /// only facet whose direction is meaningless is the one with no area at all. An
    /// absolute linear tolerance applied to a cross product would be an AREA threshold
    /// and would fail quadratically with model scale — the trap documented repo-wide.
    /// </summary>
    private static bool TryDirection(in Vector3d v, out Vector3d unit)
    {
        double length = v.Length;
        if (!(length > 0))
        {
            unit = Vector3d.Zero;
            return false;
        }
        unit = v / length;
        return true;
    }
}
