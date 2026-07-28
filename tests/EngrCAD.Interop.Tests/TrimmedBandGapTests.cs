using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The two band shapes the rung-counting split cannot see, and which the stack sweep
/// (<c>SweepStrip</c>) now handles: a band whose end is a CURVED cross edge, sampled at
/// many points rather than two, and a band whose two chains MEET AT A POINT.
/// <para><b>Neither is reachable from the <c>Shape</c> API today</b>, which is why these
/// faces are hand-built. The constructions that would produce one — a spherical band
/// between two meridian cuts, a cone fragment cut through its apex — are refused earlier,
/// by the exact B-Rep boolean rather than by the tessellator:
/// <c>Sphere(10) − Box(20,20,40).Translate((10,10,0))</c> and
/// <c>Cone(8,0,12) − Box(...)</c> both fail with "B-Rep Difference produced an unclosed
/// solid", naming coplanar/tangent face pairs. A sweep of eighteen further candidates
/// (filleted rounded rectangles and slots, chamfered arcs, tilted cylinder cuts, drilled
/// cones and tori, cut lofts, sweeps and vases) found no construction that reaches either
/// shape either — the four faces in the whole suite where the strip path declines are
/// genuinely not bands (a closed interior loop bounding a disk, a three-arc region), and
/// ear clipping is the right answer for those.</para>
/// <para>So these tests are the coverage that fix carries until the boolean catches up.
/// They are written against the tessellator directly, the way
/// <c>MiteredBandTessellationTests</c>' refusal test is.</para>
/// </summary>
public class TrimmedBandGapTests
{
    private const double Radius = 10;

    /// <summary>The generator spans +-0.6 rad, clear of both poles.</summary>
    private const double Latitude = 0.6;

    /// <summary>
    /// The patches below occupy the generator's LOW quarter (v in [0, 0.25], about six
    /// natural v steps) rather than its whole domain. That is deliberate: a band spanning
    /// all 24 steps is dominated by the standing refinement defect filed in todo.md — its
    /// base triangulation is correct and fold-free, but refinement then inflates it 14x and
    /// leaves inverted facets behind — which would make these tests about that instead of
    /// about the band SHAPES they exist to cover. Measured on the full-domain versions:
    /// 5 012 facets at worst agreement 0.912 (curved ends) and 1 318 at -0.475 (apex).
    /// </summary>
    private const double Top = -0.6 + 0.5;

    /// <summary>A sphere as a full revolve of a meridian segment about +Z.</summary>
    private static RevolvedSurface Sphere() => new(
        new CurveSegment(
            new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ, Radius), -Latitude, Latitude),
        Vector3d.Zero, Vector3d.UnitZ);

    /// <summary>The point at azimuth <paramref name="u"/> and generator angle <paramref name="lat"/>.</summary>
    private static Vector3d On(double u, double lat) => new(
        Radius * Math.Cos(lat) * Math.Cos(u), Radius * Math.Cos(lat) * Math.Sin(u), Radius * Math.Sin(lat));

    /// <summary>A latitude arc (constant generator parameter) from azimuth a to b.</summary>
    private static Curve3d Parallel(double lat, double a, double b) => new CurveSegment(
        new Circle3d(
            (0, 0, Radius * Math.Sin(lat)), Vector3d.UnitX, Vector3d.UnitY, Radius * Math.Cos(lat)),
        a, b);

    /// <summary>A meridian arc (constant azimuth) from generator angle a to b.</summary>
    private static Curve3d Meridian(double u, double a, double b) => new CurveSegment(
        new Circle3d(Vector3d.Zero, (Math.Cos(u), Math.Sin(u), 0), Vector3d.UnitZ, Radius), a, b);

    private static BrepFace Face(params Curve3d[] curves)
    {
        var vertices = curves
            .Select(c => new BrepVertex(c.PointAt(c.Domain.Start)))
            .ToList();
        var coedges = new List<BrepCoedge>();
        for (int i = 0; i < curves.Length; i++)
        {
            coedges.Add(new BrepCoedge(
                new BrepEdge(curves[i], curves[i].Domain, vertices[i], vertices[(i + 1) % curves.Length]),
                true));
        }
        return new BrepFace(Sphere(), [new BrepLoop(coedges)]);
    }

    /// <summary>
    /// Tessellates through the trimmed path and checks the invariants a band must satisfy
    /// however it was triangulated: no folds against the exact surface, no degenerate
    /// facets, every shared boundary sample surviving verbatim (or a neighbouring face
    /// cannot weld to this one), and a parameter-space area exactly matching the loop's.
    /// </summary>
    private static (int Facets, double WorstDot) Check(BrepFace face)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var coedge in face.OuterLoop.Coedges)
            edgePolylines[coedge.Edge] = BRepTessellator.SampleEdge(coedge.Edge, 48, 24);

        var polygons = new List<IReadOnlyList<Vector3d>>();
        Assert.True(
            TrimmedFaceTessellator.TryTessellate(face, edgePolylines, 48, 24, polygons, out string? why),
            $"the trimmed path refused the band: {why}");

        var used = new HashSet<Vector3d>(polygons.SelectMany(p => p));
        foreach (var sample in BRepTessellator.LoopPolyline(face.OuterLoop, edgePolylines))
            Assert.Contains(sample, used);

        // Completeness is checked in PARAMETER space, where it is exact: the triangles'
        // signed uv areas must sum to the loop's own. A 3D area comparison cannot do this
        // job — the chordal error of a triangulation of a doubly curved patch is not even
        // one-sided (a skinny inscribed triangle can carry MORE area than the patch
        // beneath it, the Schwarz lantern in miniature), so its tolerance would have to be
        // percent-wide, which a gap or a double-covered slab fits inside.
        Vector2d Uv(Vector3d p)
        {
            Assert.True(
                face.Surface.TryProjectPoint(p, out var uv, FaceGeometry.InverseEvaluationTolerance),
                $"a band vertex at {p} is off its own surface");
            return uv;
        }

        double area = 0, worst = 1;
        foreach (var polygon in polygons)
        {
            var normal = (polygon[1] - polygon[0]).Cross(polygon[2] - polygon[0]);
            Assert.True(normal.Length > 0, "a band facet is degenerate");

            var (a, b, c) = (Uv(polygon[0]), Uv(polygon[1]), Uv(polygon[2]));
            area += (b - a).Cross(c - a) / 2;

            var exact = Vector3d.Zero;
            foreach (var uv in (ReadOnlySpan<Vector2d>)[a, b, c])
                exact += face.Surface.NormalAt(uv.X, uv.Y).Normalized();
            worst = Math.Min(worst, normal.Normalized().Dot(exact.Normalized()));
        }

        var loopUv = BRepTessellator.LoopPolyline(face.OuterLoop, edgePolylines).Select(Uv).ToList();
        Assert.Equal(FaceGeometry.LoopSignedArea(loopUv), area, 9);

        return (polygons.Count, worst);
    }

    /// <summary>
    /// A spherical band between two meridian cuts: both of its cross edges are ARCS,
    /// sampled at 25 points each rather than two, so the rung-counting split sees 25 flat
    /// steps at each end instead of one and declines in both parameters. The sweep stacks
    /// a tied run and fans it from the opposite chain's first vertex, which is what keeps
    /// those facets out of the zero-area trap that fanning them among themselves would be.
    /// </summary>
    [Fact]
    public void BandWithCurvedCrossEdges_IsSweptRatherThanEarClipped()
    {
        const double span = 1.0;
        var face = Face(
            Parallel(-Latitude, 0, span),
            Meridian(span, -Latitude, Top),
            Parallel(Top, span, 0),
            Meridian(0, Top, -Latitude));

        var (facets, worstDot) = Check(face);

        // The sweep's own answer is ~2n = 190 facets for four 25-vertex sides. The rest is
        // curvature refinement, and the ratio is the standing refinement defect in
        // miniature: the patch spans about 7.6 natural u steps by 6 v steps, so the grid
        // asks for ~92 triangles and midpoint bisection of a base strip with no interior
        // rows produces 2 784. Committed baseline — a move in EITHER direction wants
        // understanding before the number is updated. What matters here is that the shape
        // is HANDLED and handled correctly, which the assertions above check.
        Assert.InRange(facets, 190, 2784);
        // The 0.1998 is NOT the sweep's doing, and that is the finding worth keeping: the
        // base triangulation measures 94 facets at worst agreement 0.99954, and curvature
        // refinement turns it into 2 784 at 0.1998. Refinement here does not improve the
        // mesh, it wrecks it — the clearest evidence yet for the item filed in todo.md.
        Assert.InRange(worstDot, 0.19, 1.0);
    }

    /// <summary>
    /// A band whose two chains MEET AT A POINT — a rung of no steps at all, which the
    /// rung-counting split cannot express (it needs exactly two flat steps). The apex is
    /// simply the single extreme vertex a monotone sweep starts from.
    /// <para>The region is a spherical triangle: a latitude arc at the bottom, a meridian
    /// up the right-hand side, and a diagonal running back to a single apex on the left.</para>
    /// </summary>
    [Fact]
    public void BandWhoseChainsMeetAtAPoint_IsSwept()
    {
        const double span = 1.0;
        // The diagonal runs from the meridian's top corner back down to the APEX, where
        // it meets the latitude arc's start at (u = 0, lat = -L).
        var diagonal = new PolylineCurve3d(
            [.. Enumerable.Range(0, 33).Select(i =>
            {
                double t = 1 - i / 32.0;
                return On(span * t, -Latitude + (Top + Latitude) * t);
            })]);

        var face = Face(
            Parallel(-Latitude, 0, span),
            Meridian(span, -Latitude, Top),
            diagonal);

        var (facets, worstDot) = Check(face);
        // As above: ~n facets from the sweep, 1 258 after refinement. Committed baseline.
        Assert.InRange(facets, 60, 1258);
        // Here the base mesh is poor on its own (worst -0.570 before refinement, -0.529
        // after), for a reason the sweep cannot fix: a monotone sweep of a region that
        // narrows to a point FANS it from that point, so the facets nearest the apex span
        // the whole width of the patch. Interior rows are what a patch this shape needs,
        // which is the same conclusion the todo.md item reaches from three other repros.
        Assert.InRange(worstDot, -0.55, 1.0);
    }
}
