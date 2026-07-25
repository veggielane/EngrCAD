using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>How a loft blends between its sections.</summary>
public enum LoftStyle
{
    /// <summary>
    /// One surface through ALL sections, interpolating them with a B-spline of degree
    /// min(3, sections − 1). Intermediate sections leave no edge — the skin is smooth
    /// across them.
    /// </summary>
    Smooth,

    /// <summary>
    /// Straight (degree 1) between consecutive sections: one band of faces per interval,
    /// so every intermediate section is a real edge loop. With two sections this is
    /// identical to <see cref="Smooth"/>.
    /// </summary>
    Ruled,
}

// Loft / ThruSections: skin a solid through a list of planar sections.
public static partial class SolidFactory
{
    /// <summary>
    /// Skins a solid through a list of planar <paramref name="sections"/> (OCCT's
    /// <c>BRepOffsetAPI_ThruSections</c>): each corresponding pair of profile segments
    /// becomes one <see cref="LoftedSurface"/> strip, junctions become rail edges, and the
    /// first and last sections are capped with planar faces. The result is always a closed
    /// solid (open skins are not modeled — <see cref="BrepSolid.Validate"/> requires
    /// two-manifold edge use).
    ///
    /// <para><b>Compatibility.</b> Sections are matched by <i>segment index</i> and by
    /// <i>normalized curve parameter</i>, so they must already have the same number of
    /// segments — this factory does not degree-elevate or merge knot vectors, it rejects
    /// mismatched sections with a message saying so. (A section with too few segments can
    /// be split with <c>Sketch</c> or by <see cref="CurveSegment"/>.) The one automatic
    /// compatibility fix is representational, not geometric: where one section's segment is
    /// straight and another's is curved, the straight one is re-expressed as an exact
    /// degree-1 <see cref="NurbsCurve"/> so both sides sample at the same density and the
    /// tessellation welds.</para>
    ///
    /// <para><b>Alignment.</b> Sections are aligned before skinning, so the caller need not
    /// wind or start them consistently: (1) any section wound against the loft direction is
    /// reversed; (2) multi-segment sections are cyclically rotated to the segment pairing
    /// that minimizes the summed squared corner travel; (3) closed single-curve sections
    /// get a continuous seam shift (<see cref="PhaseShiftedCurve"/>) minimizing the same
    /// measure. All three are the "least twist" choice — the discrete analogue of the
    /// rotation-minimizing frames <see cref="Sweep"/> uses.</para>
    ///
    /// <para>The v parameterization is <b>global</b> (mean chord length between
    /// corresponding section points, shared by every strip): a per-strip parameterization
    /// would give the two faces meeting at a rail different v mappings, and the rail would
    /// crack.</para>
    /// </summary>
    /// <param name="sections">Two or more planar profiles, in loft order.</param>
    /// <param name="style">Smooth (one surface through all sections) or ruled.</param>
    public static BrepSolid Loft(IReadOnlyList<Profile> sections, LoftStyle style = LoftStyle.Smooth)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count < 2)
            throw new ArgumentException("A loft needs at least 2 sections.", nameof(sections));
        foreach (var section in sections)
        {
            if (section is null)
                throw new ArgumentException("Loft sections must not be null.", nameof(sections));
        }

        bool singleClosed = sections[0].IsSingleClosedCurve;
        int segmentCount = sections[0].Segments.Count;
        for (int k = 1; k < sections.Count; k++)
        {
            if (sections[k].IsSingleClosedCurve != singleClosed)
                throw new ArgumentException(
                    "Loft sections must be all single closed curves or all segment chains; " +
                    $"section 0 is {(singleClosed ? "a closed curve" : "a chain")} but section {k} is not.",
                    nameof(sections));
            if (sections[k].Segments.Count != segmentCount)
                throw new ArgumentException(
                    $"Loft sections must have the same segment count: section 0 has {segmentCount}, " +
                    $"section {k} has {sections[k].Segments.Count}. Split the coarser section so the " +
                    "segments correspond (this factory matches by segment index, it does not " +
                    "re-parameterize sections to make them compatible).",
                    nameof(sections));
        }

        var aligned = AlignLoftSections(sections);
        aligned = UnifyLoftSectionSampling(aligned);
        var parameters = LoftSectionParameters(aligned);
        return BuildLoftedSolid(aligned, parameters, style == LoftStyle.Ruled);
    }

    /// <summary>Two-section convenience overload.</summary>
    public static BrepSolid Loft(Profile first, Profile second, LoftStyle style = LoftStyle.Smooth) =>
        Loft([first, second], style);

    // ---- alignment ----

    /// <summary>
    /// Winds every section the same way about the loft direction and rotates its
    /// parameterization to minimize twist against its predecessor.
    /// </summary>
    private static Profile[] AlignLoftSections(IReadOnlyList<Profile> sections)
    {
        int m = sections.Count;
        var centroids = new Vector3d[m];
        for (int k = 0; k < m; k++)
        {
            var loop = sections[k].SampleLoop();
            var sum = Vector3d.Zero;
            foreach (var p in loop)
                sum += p;
            centroids[k] = sum / loop.Count;
        }

        var result = new Profile[m];
        for (int k = 0; k < m; k++)
        {
            // The local travel direction: a central difference in the interior so a curved
            // stack of sections is judged against its own axis, not the chord end to end.
            var direction = k == 0 ? centroids[1] - centroids[0]
                : k == m - 1 ? centroids[m - 1] - centroids[m - 2]
                : centroids[k + 1] - centroids[k - 1];
            double alignment = sections[k].Normal.Dot(direction);
            // Scale-relative degeneracy test (as in Extrude): the loft direction may not
            // lie in a section's plane, or the skin folds back on itself there.
            if (Math.Abs(alignment) < 1e-9 * direction.Length)
                throw new ArgumentException(
                    $"Loft section {k} is edge-on to the loft direction (its plane contains the " +
                    "travel direction); reorder or reposition the sections.", nameof(sections));
            result[k] = alignment < 0 ? sections[k].Reversed() : sections[k];
        }

        for (int k = 1; k < m; k++)
            result[k] = AlignToPredecessor(result[k], result[k - 1]);
        return result;
    }

    /// <summary>
    /// Rotates a section's parameterization (cyclic segment shift for chains, continuous
    /// seam shift for closed curves) to the least-twist match with its predecessor.
    ///
    /// <para>Both sections are compared <b>centroid-relative</b>: twist is a difference of
    /// orientation, not of position. Leaving the sections' separation in the objective
    /// makes it a large constant plus a small quadratic well, and a constant that size
    /// costs the minimizer eight digits of precision — measured: a continuous seam shift
    /// resolved to only ~3e-9, which is 3e-8 of positional twist, past the weld
    /// tolerance.</para>
    /// </summary>
    private static Profile AlignToPredecessor(Profile section, Profile previous)
    {
        if (!section.IsSingleClosedCurve)
        {
            int n = section.Segments.Count;
            if (n < 2)
                return section;

            var here = new Vector3d[n];
            var there = new Vector3d[n];
            for (int i = 0; i < n; i++)
            {
                here[i] = section.Segments[i].PointAt(section.Segments[i].Domain.Start);
                there[i] = previous.Segments[i].PointAt(previous.Segments[i].Domain.Start);
            }
            Recentre(here);
            Recentre(there);

            int best = 0;
            double bestCost = double.PositiveInfinity;
            for (int shift = 0; shift < n; shift++)
            {
                double cost = 0;
                for (int i = 0; i < n; i++)
                    cost += here[(i + shift) % n].DistanceSquaredTo(there[i]);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = shift;
                }
            }
            if (best == 0)
                return section;

            var rotated = new Curve3d[n];
            for (int i = 0; i < n; i++)
                rotated[i] = section.Segments[(i + best) % n];
            return new Profile(rotated);
        }

        // Closed single curve: a continuous seam shift. Scan, then refine by golden
        // section — the objective is smooth, and a shift quantized to the scan step would
        // leave a visible twist on a fine section.
        var curve = section.Segments[0];
        var reference = previous.Segments[0];
        const int scan = 64;
        var referencePoints = new Vector3d[scan];
        for (int i = 0; i < scan; i++)
            referencePoints[i] = reference.PointAt(reference.Domain.ParameterAt((double)i / scan));
        Recentre(referencePoints);

        var centre = Vector3d.Zero;
        for (int i = 0; i < scan; i++)
            centre += curve.PointAt(curve.Domain.ParameterAt((double)i / scan));
        centre /= scan;
        var sectionCentre = centre;

        double Cost(double shift)
        {
            double total = 0;
            var domain = curve.Domain;
            for (int i = 0; i < scan; i++)
            {
                double t = (double)i / scan + shift;
                t -= Math.Floor(t);
                total += (curve.PointAt(domain.ParameterAt(t)) - sectionCentre)
                    .DistanceSquaredTo(referencePoints[i]);
            }
            return total;
        }

        double bestShift = 0, lowest = Cost(0);
        for (int i = 1; i < scan; i++)
        {
            double shift = (double)i / scan;
            double cost = Cost(shift);
            if (cost < lowest)
            {
                lowest = cost;
                bestShift = shift;
            }
        }
        // An exactly-matching pair (the common case: identical or coaxial sections) needs
        // no wrapper at all — exact zero is a semantic test, not a tolerance.
        if (lowest > 0)
            bestShift = RefineShift(Cost, bestShift, 1.0 / scan);

        bestShift -= Math.Floor(bestShift);
        // Sub-ulp shifts would only add a wrapper without moving anything.
        if (bestShift < 1e-12 || bestShift > 1 - 1e-12)
            return section;
        return new Profile([new PhaseShiftedCurve(curve, bestShift * curve.Domain.Length)]);
    }

    /// <summary>Subtracts the mean in place, so comparisons measure shape and orientation only.</summary>
    private static void Recentre(Vector3d[] points)
    {
        var centre = Vector3d.Zero;
        foreach (var p in points)
            centre += p;
        centre /= points.Length;
        for (int i = 0; i < points.Length; i++)
            points[i] -= centre;
    }

    /// <summary>Golden-section minimization of a unimodal cost on [centre − h, centre + h].</summary>
    private static double RefineShift(Func<double, double> cost, double centre, double h)
    {
        double phi = (Math.Sqrt(5) - 1) / 2;
        double a = centre - h, b = centre + h;
        double c = b - phi * (b - a), d = a + phi * (b - a);
        double fc = cost(c), fd = cost(d);
        for (int i = 0; i < 60 && b - a > 1e-14; i++)
        {
            if (fc < fd)
            {
                (b, d, fd) = (d, c, fc);
                c = b - phi * (b - a);
                fc = cost(c);
            }
            else
            {
                (a, c, fc) = (c, d, fd);
                d = a + phi * (b - a);
                fd = cost(d);
            }
        }
        return (a + b) / 2;
    }

    // ---- compatibility ----

    /// <summary>
    /// Makes corresponding section curves sample at the same density. A tessellator samples
    /// a straight edge with one segment and a curved edge with many; where one section's
    /// strip is straight and another's is curved, the straight side is re-expressed as an
    /// exact degree-1 NURBS so both take the generic rule and the grid columns coincide
    /// with the shared edge polylines. Purely representational — the curve's points are
    /// unchanged.
    /// </summary>
    private static Profile[] UnifyLoftSectionSampling(Profile[] sections)
    {
        int m = sections.Length;
        int n = sections[0].Segments.Count;

        if (sections[0].IsSingleClosedCurve)
        {
            int circular = sections.Count(s => s.Segments[0].Underlying is Circle3d);
            if (circular != 0 && circular != m)
                throw new NotSupportedException(
                    "Lofting a circular closed section to a non-circular one is not supported: the two " +
                    "are tessellated at different densities and the skin would not weld. Rebuild the " +
                    "circle as a NURBS section (or split both sections into matching arc chains).");
            return sections;
        }

        var replacement = new Curve3d[m][];
        bool changed = false;
        for (int i = 0; i < n; i++)
        {
            int straight = 0;
            for (int k = 0; k < m; k++)
            {
                if (sections[k].Segments[i].Underlying is Line3d)
                    straight++;
            }
            if (straight == 0 || straight == m)
                continue;

            for (int k = 0; k < m; k++)
            {
                var segment = sections[k].Segments[i];
                if (segment.Underlying is not Line3d)
                    continue;
                replacement[k] ??= [.. sections[k].Segments];
                var domain = segment.Domain;
                replacement[k][i] = new NurbsCurve(
                    1, [segment.PointAt(domain.Start), segment.PointAt(domain.End)], null, [0, 0, 1, 1]);
                changed = true;
            }
        }
        if (!changed)
            return sections;

        var result = new Profile[m];
        for (int k = 0; k < m; k++)
            result[k] = replacement[k] is null ? sections[k] : new Profile(replacement[k]);
        return result;
    }

    /// <summary>
    /// The loft's v parameters: mean chord length between corresponding points of
    /// consecutive sections, accumulated and normalized. Computed once for the whole loft —
    /// every strip must share it or the rails between strips would disagree.
    /// </summary>
    private static double[] LoftSectionParameters(Profile[] sections)
    {
        const int samples = 8;
        int m = sections.Length;
        int n = sections[0].Segments.Count;
        var parameters = new double[m];
        double total = 0;
        for (int k = 1; k < m; k++)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var previous = sections[k - 1].Segments[i];
                var current = sections[k].Segments[i];
                for (int s = 0; s < samples; s++)
                {
                    double u = (double)s / samples;
                    sum += previous.PointAt(previous.Domain.ParameterAt(u))
                        .DistanceTo(current.PointAt(current.Domain.ParameterAt(u)));
                    count++;
                }
            }
            double chord = sum / count;
            if (!(chord > 0))
                throw new ArgumentException(
                    $"Loft sections {k - 1} and {k} coincide; remove the duplicate section.", nameof(sections));
            total += chord;
            parameters[k] = total;
        }
        for (int k = 1; k < m; k++)
            parameters[k] /= total;
        parameters[^1] = 1.0; // exact, despite the division round-off
        return parameters;
    }

    // ---- topology ----

    private static BrepSolid BuildLoftedSolid(Profile[] sections, double[] parameters, bool ruled)
    {
        int m = sections.Length;
        bool singleClosed = sections[0].IsSingleClosedCurve;
        int n = sections[0].Segments.Count;

        // Ruled lofts get a band of faces per interval (every intermediate section is a
        // real edge); a smooth loft is one face per strip across the whole stack.
        var spans = new List<(int Start, int End)>();
        if (ruled)
        {
            for (int k = 0; k + 1 < m; k++)
                spans.Add((k, k + 1));
        }
        else
        {
            spans.Add((0, m - 1));
        }

        var vertices = new Dictionary<int, BrepVertex[]>();
        var sectionEdges = new Dictionary<int, BrepEdge[]>();
        foreach (int k in spans.SelectMany<(int Start, int End), int>(s => [s.Start, s.End]).Distinct())
        {
            var segments = sections[k].Segments;
            if (singleClosed)
            {
                var curve = segments[0];
                var seam = new BrepVertex(curve.PointAt(curve.Domain.Start));
                vertices[k] = [seam];
                sectionEdges[k] = [new BrepEdge(curve, curve.Domain, seam, seam)];
                continue;
            }

            var loopVertices = new BrepVertex[n];
            for (int i = 0; i < n; i++)
                loopVertices[i] = new BrepVertex(segments[i].PointAt(segments[i].Domain.Start));
            var loopEdges = new BrepEdge[n];
            for (int i = 0; i < n; i++)
                loopEdges[i] = new BrepEdge(segments[i], segments[i].Domain, loopVertices[i], loopVertices[(i + 1) % n]);
            vertices[k] = loopVertices;
            sectionEdges[k] = loopEdges;
        }

        var faces = new List<BrepFace>();
        foreach (var (first, last) in spans)
        {
            int[] indices = ruled ? [first, last] : [.. Enumerable.Range(0, m)];
            double[] spanParameters = ruled ? [0.0, 1.0] : parameters;

            var surfaces = new LoftedSurface[n];
            for (int i = 0; i < n; i++)
            {
                var curves = new Curve3d[indices.Length];
                for (int k = 0; k < indices.Length; k++)
                    curves[k] = sections[indices[k]].Segments[i];
                surfaces[i] = new LoftedSurface(curves, spanParameters);
            }

            if (singleClosed)
            {
                // Band topology, exactly the cylinder side's: the lower rim follows +u,
                // the upper opposes it.
                faces.Add(new BrepFace(surfaces[0],
                [
                    new BrepLoop([new BrepCoedge(sectionEdges[first][0], sameSense: true)]),
                    new BrepLoop([new BrepCoedge(sectionEdges[last][0], sameSense: false)]),
                ]));
                continue;
            }

            // Rails evaluate the strip surface at u = 0 rather than re-interpolating the
            // junction points: the rail edge and the face's first grid column are then the
            // same arithmetic and cannot drift apart.
            var rails = new BrepEdge[n];
            for (int i = 0; i < n; i++)
            {
                rails[i] = new BrepEdge(
                    new LoftRailCurve(surfaces[i], surfaces[i].DomainU.Start),
                    Interval.Unit, vertices[first][i], vertices[last][i]);
            }

            for (int i = 0; i < n; i++)
            {
                faces.Add(new BrepFace(surfaces[i],
                [
                    new BrepLoop(
                    [
                        new BrepCoedge(sectionEdges[first][i], sameSense: true),
                        new BrepCoedge(rails[(i + 1) % n], sameSense: true),
                        new BrepCoedge(sectionEdges[last][i], sameSense: false),
                        new BrepCoedge(rails[i], sameSense: false),
                    ]),
                ]));
            }
        }

        var bottom = sections[0];
        var top = sections[m - 1];
        List<BrepCoedge> bottomCoedges = [];
        List<BrepCoedge> topCoedges = [];
        if (singleClosed)
        {
            bottomCoedges.Add(new BrepCoedge(sectionEdges[0][0], sameSense: false));
            topCoedges.Add(new BrepCoedge(sectionEdges[m - 1][0], sameSense: true));
        }
        else
        {
            for (int i = n - 1; i >= 0; i--)
                bottomCoedges.Add(new BrepCoedge(sectionEdges[0][i], sameSense: false));
            for (int i = 0; i < n; i++)
                topCoedges.Add(new BrepCoedge(sectionEdges[m - 1][i], sameSense: true));
        }

        // Bottom cap axes swapped so its normal points back along the loft direction.
        faces.Add(new BrepFace(new PlaneSurface(bottom.Origin, bottom.YAxis, bottom.XAxis), [new BrepLoop(bottomCoedges)]));
        faces.Add(new BrepFace(new PlaneSurface(top.Origin, top.XAxis, top.YAxis), [new BrepLoop(topCoedges)]));
        return new BrepSolid([new BrepShell(faces)]);
    }
}
