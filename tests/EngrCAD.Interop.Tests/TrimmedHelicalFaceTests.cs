using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A helical band trimmed by something other than its own cap planes — the shape a 45°
/// end chamfer leaves on every thread band. Until now <c>BRepTessellator</c> sent every
/// <see cref="HelicalSurface"/> face to the sheared full-band grid and threw for anything
/// that was not two rails plus two cap cuts; such faces now go to
/// <c>TrimmedFaceTessellator</c>, whose non-wrapping tiers apply because a helical band's
/// u is NOT periodic (z advances with every turn, so every loop has winding 0).
///
/// <para><b>These faces are hand-built, for the same reason
/// <see cref="TrimmedBandGapTests"/>' are</b>: the constructions that would produce one
/// are blocked upstream. The chamfered rod needs <c>FaceSplitter</c> to accept a curve
/// whose ends sit exactly ON the face boundary (the analytic cut is clipped to v ∈ [0,1],
/// so it terminates on the rails rather than crossing them), and a cross-drilled rod
/// needs the marching tracer to seed a band spanning thirteen turns — measured, it finds
/// one branch of the five on an M8 crest flat and stops up to 0.9 of the band's height
/// short of the rails. Both are filed in todo.md.</para>
///
/// <para>What is NOT hand-built is the geometry: the trimming curve comes from
/// <c>SurfaceIntersection</c>'s exact coaxial-cone case, so these faces are bounded by
/// the same curve objects a chamfer will hand them.</para>
/// </summary>
public class TrimmedHelicalFaceTests
{
    private const double Pitch = 1.25;
    private static double Rate => Pitch / (2 * Math.PI);

    /// <summary>One flank band of an M8-like thread, wound over three turns.</summary>
    private static HelicalSurface Band() => new(
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
        new Vector2d(3.3, 0), new Vector2d(4, 0.4), Pitch, new Interval(0, 6 * Math.PI));

    /// <summary>
    /// The curve a straight line v = alpha + beta·u in the band's parameter space traces
    /// on it, as the exact <see cref="SpiralArc3d"/> it is. Every boundary of a helical
    /// band region is one of these: a rail is beta = 0, a cap cut is beta = −rate/dz
    /// (which makes the axial rate cancel to zero, hence planar), a cone cut is the
    /// general case.
    /// </summary>
    private static SpiralArc3d Along(HelicalSurface band, double alpha, double beta, double uFrom, double uTo)
    {
        double r0 = band.ProfileStart.X, z0 = band.ProfileStart.Y;
        double dr = band.ProfileEnd.X - r0, dz = band.ProfileEnd.Y - z0;
        return new SpiralArc3d(
            band.Frame, r0 + dr * alpha, dr * beta,
            z0 + dz * alpha, dz * beta + band.AxialRate,
            new Interval(Math.Min(uFrom, uTo), Math.Max(uFrom, uTo)));
    }

    private static BrepFace Face(HelicalSurface band, params Curve3d[] curves)
    {
        var vertices = curves.Select(c => new BrepVertex(c.PointAt(c.Domain.Start))).ToList();
        var coedges = new List<BrepCoedge>();
        for (int i = 0; i < curves.Length; i++)
        {
            coedges.Add(new BrepCoedge(
                new BrepEdge(curves[i], curves[i].Domain, vertices[i], vertices[(i + 1) % curves.Length]),
                true));
        }
        return new BrepFace(band, [new BrepLoop(coedges)]);
    }

    /// <summary>
    /// The invariants any trimmed band must satisfy however it was triangulated — copied
    /// in spirit from <see cref="TrimmedBandGapTests"/>: the trimmed path must accept it,
    /// every shared boundary sample must survive verbatim (or a neighbour cannot weld),
    /// no facet may be degenerate or oppose the surface, and the triangles' signed uv
    /// areas must sum EXACTLY to the loop's — the only completeness check that is exact,
    /// since a chordal 3D area is not even one-sided on a doubly curved patch.
    /// </summary>
    private static (int Facets, double WorstDot) Check(BrepFace face, int segments = 48)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var coedge in face.OuterLoop.Coedges)
            edgePolylines[coedge.Edge] = BRepTessellator.SampleEdge(coedge.Edge, segments, segments / 2);

        var polygons = new List<IReadOnlyList<Vector3d>>();
        Assert.True(
            TrimmedFaceTessellator.TryTessellate(face, edgePolylines, segments, segments / 2, polygons, out string? why),
            $"the trimmed path refused the helical band: {why}");

        var used = new HashSet<Vector3d>(polygons.SelectMany(p => p));
        foreach (var sample in BRepTessellator.LoopPolyline(face.OuterLoop, edgePolylines))
            Assert.Contains(sample, used);

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
    /// The chamfered band: two rails, one cap cut, and one CONICAL spiral taken straight
    /// from <c>SurfaceIntersection</c>. The loop runs CCW in (u, v) — bottom rail forward,
    /// the cone cut up, top rail back, the cap cut down.
    /// </summary>
    private static BrepFace ChamferedBand(HelicalSurface band, out SpiralArc3d cut)
    {
        // r = 3.3 + (2 - z): the 45-degree cone whose radius reaches the root at z = 2.
        var cone = new RevolvedSurface(
            new Line3d((3.3, 0, 2), (5.3, 0, 0)), Vector3d.Zero, Vector3d.UnitZ);
        var curves = SurfaceIntersection.Intersect(band, cone, new Aabb((-20, -20, -20), (20, 20, 20)));
        cut = Assert.IsType<SpiralArc3d>(Assert.Single(curves));
        Assert.False(cut.IsPlanar);

        // The cut runs v = 1 -> v = 0 as u increases here, so u = cut.Domain.End is on the
        // bottom rail and u = cut.Domain.Start on the top one.
        double uBottomCut = cut.Domain.End, uTopCut = cut.Domain.Start;
        double dz = band.ProfileEnd.Y - band.ProfileStart.Y;
        // The cap cut's feet. It leans BACK in u as v rises (the shear), so the bottom
        // foot must sit a full generator rise inside the domain for the top one to stay in.
        double uStart = 2.5;
        double uTopStart = uStart - dz / Rate;

        return Face(band,
            Along(band, 0, 0, uStart, uBottomCut),                       // bottom rail, +u
            new ReversedCurve(cut),                                      // cone cut, v 0 -> 1
            new ReversedCurve(Along(band, 1, 0, uTopStart, uTopCut)),    // top rail, -u
            Along(band, 1 + Rate * uTopStart / dz, -Rate / dz, uTopStart, uStart)); // cap cut, v 1 -> 0
    }

    [Fact]
    public void AConeTrimmedHelicalBandTessellatesCleanly()
    {
        var band = Band();
        var face = ChamferedBand(band, out _);
        var (facets, worst) = Check(face);
        Assert.True(facets > 0);
        // Three natural u steps, the corpus gate's own floor: 2*pi/48 each.
        Assert.True(worst > Math.Cos(3 * (2 * Math.PI / 48)),
            $"worst facet-vs-surface agreement {worst:F6}");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(96)]
    public void ItStaysCleanAcrossTheDensityRange(int segments)
    {
        var band = Band();
        var face = ChamferedBand(band, out _);
        var (facets, worst) = Check(face, segments);
        Assert.True(facets > 0);
        Assert.True(worst > Math.Cos(3 * (2 * Math.PI / segments)),
            $"worst facet-vs-surface agreement {worst:F6} at {segments} segments");
    }

    [Fact]
    public void TheCapCutIsPlanarAndTheConeCutIsNot()
    {
        // The distinction IsFullHelicalBand turns on: both boundaries are SpiralArc3d, and
        // only the planar one is a cap cut. Counting spiral edges alone would send a
        // chamfered band down the full-band grid, which interpolates its columns between
        // the two cuts assuming they are the ends of u.
        var band = Band();
        var face = ChamferedBand(band, out var cut);
        Assert.False(cut.IsPlanar);
        var spirals = face.OuterLoop.Coedges
            .Select(c => c.Edge.Curve.Underlying)
            .OfType<SpiralArc3d>()
            .ToList();
        // Every boundary of a helical band region is a SpiralArc3d — the two rails are
        // the AxialRate = rate, Slope = 0 members, the cap cut the AxialRate = 0 one, and
        // only the cone cut varies both. So "is a spiral arc" cannot be the gate.
        Assert.Equal(4, spirals.Count);
        Assert.Single(spirals.Where(s => s.IsPlanar));
        Assert.Equal(2, spirals.Count(s => s.Slope == 0 && !s.IsPlanar));   // the rails
    }

    [Fact]
    public void AFullBandStillTakesTheShearedGridPath()
    {
        // The routing change must not move the bands MakeThreadedRod builds: a rod's
        // tessellation is unchanged, which is what the corpus member locks at scale.
        var profile = new Vector2d[]
        {
            new(4, -Pitch / 16), new(4, Pitch / 16),
            new(3.3, 3 * Pitch / 8), new(3.3, 5 * Pitch / 8),
        };
        var rod = SolidFactory.MakeThreadedRod(profile, Pitch, 8);
        var mesh = BRepTessellator.Tessellate(rod, 48, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(rod.Faces.Where(f => f.Surface is HelicalSurface).All(f => f.Loops.Count == 1));
    }
}
