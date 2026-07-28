using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class ThreadedRodTests
{
    // ISO 68-1 M8×1.25 basic profile, crest centered at phase 0 (matching Sdf.Thread):
    // crest flat P/8 at the major radius, root flat P/4 at the minor radius, 60° flanks
    // spanning 5P/16 axially each.
    private const double Pitch = 1.25;
    private static readonly double H = Math.Sqrt(3) / 2 * Pitch;
    private static readonly double MajorRadius = 4.0;
    private static readonly double MinorRadius = 4.0 - 0.625 * H;

    internal static IReadOnlyList<Vector2d> IsoProfile(double rMaj, double rMin, double pitch) =>
    [
        new(rMaj, -pitch / 16),          // crest flat start
        new(rMaj, pitch / 16),           // crest flat end → descending flank
        new(rMin, 3 * pitch / 8),        // root flat start
        new(rMin, 5 * pitch / 8),        // root flat end → ascending flank (wraps)
    ];

    [Fact]
    public void MakeThreadedRod_ValidatesAndSatisfiesEuler()
    {
        var rod = SolidFactory.MakeThreadedRod(IsoProfile(MajorRadius, MinorRadius, Pitch), Pitch, 10);
        rod.Validate();

        // K = 4 profile segments: V = 2K, E = 3K (K rails + K cuts per cap), F = K + 2,
        // one loop per face ⇒ V − E + F − (L − F) − 2(S − G) = 8 − 12 + 6 − 0 − 2 = 0.
        Assert.Equal(8, rod.Vertices.Count());
        Assert.Equal(12, rod.Edges.Count());
        Assert.Equal(6, rod.Faces.Count());
        Assert.Equal(6, rod.Loops.Count());
        Assert.True(rod.SatisfiesEulerFormula(genus: 0));

        Assert.Equal(4, rod.Faces.Count(f => f.Surface is HelicalSurface));
        Assert.Equal(2, rod.Faces.Count(f => f.Surface is PlaneSurface));
    }

    [Fact]
    public void MakeThreadedRod_FractionalTurnsAreValid()
    {
        // 8.24 turns: rails end at a different phase than they start — no whole-turn
        // constraint exists, the cap cuts just sit at rotated phases.
        var rod = SolidFactory.MakeThreadedRod(IsoProfile(MajorRadius, MinorRadius, Pitch), Pitch, 10.3);
        rod.Validate();
        Assert.True(rod.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void MakeThreadedRod_TriangularProfileWorks()
    {
        // A sharp-crest (K = 2) profile: root flat + two flanks meeting at a point.
        var rod = SolidFactory.MakeThreadedRod(
            [new Vector2d(3.2, 0.0), new Vector2d(3.2, 0.3), new Vector2d(4.0, 0.7)],
            Pitch, 6);
        rod.Validate();
        Assert.True(rod.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(6, rod.Vertices.Count());
        Assert.Equal(9, rod.Edges.Count());
        Assert.Equal(5, rod.Faces.Count());
    }

    [Fact]
    public void MakeThreadedRod_PlacedFrameBuildsExactlyInPlace()
    {
        var axis = new Vector3d(1, 2, 2).Normalized();
        var x = axis.ArbitraryPerpendicular(Tolerance.Default);
        var frame = Frame3d.FromOrthonormal(new Vector3d(5, -3, 1), x, axis.Cross(x));
        var rod = SolidFactory.MakeThreadedRod(
            IsoProfile(MajorRadius, MinorRadius, Pitch), Pitch, 10, frame);
        rod.Validate();
        Assert.True(rod.SatisfiesEulerFormula(genus: 0));

        // Every helical band sits on the placed frame, and rail start vertices lie on
        // the z = 0 cap plane through the frame origin.
        foreach (var face in rod.Faces)
        {
            if (face.Surface is not HelicalSurface helical)
                continue;
            Assert.True(helical.Frame.Origin.DistanceTo(frame.Origin) < 1e-12);
            foreach (var coedge in face.OuterLoop.Coedges)
            {
                if (coedge.Edge.Curve is Helix3d rail)
                {
                    double h = (rail.PointAt(0) - frame.Origin).Dot(frame.Z);
                    Assert.True(Math.Abs(h) < 1e-12, $"rail start off the cap plane by {h}");
                }
            }
        }
    }

    [Fact]
    public void MakeThreadedRod_ValidatesInput()
    {
        var profile = IsoProfile(MajorRadius, MinorRadius, Pitch);
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidFactory.MakeThreadedRod(profile, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidFactory.MakeThreadedRod(profile, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidFactory.MakeThreadedRod(profile, Pitch, 0));
        Assert.Throws<ArgumentException>(() => SolidFactory.MakeThreadedRod([new Vector2d(1, 0)], Pitch, 10));
        // Non-increasing axial coordinates.
        Assert.Throws<ArgumentException>(() => SolidFactory.MakeThreadedRod(
            [new Vector2d(1, 0.5), new Vector2d(2, 0.2)], Pitch, 10));
        // Profile spanning a whole pitch leaves no room for the wrap segment.
        Assert.Throws<ArgumentException>(() => SolidFactory.MakeThreadedRod(
            [new Vector2d(1, 0), new Vector2d(2, Pitch)], Pitch, 10));
        // Radius on the axis.
        Assert.Throws<ArgumentException>(() => SolidFactory.MakeThreadedRod(
            [new Vector2d(0, 0), new Vector2d(2, 0.5)], Pitch, 10));
    }

    // ---- left-hand threads ----

    [Fact]
    public void LeftHandRod_HasTheSameTopologyAndSatisfiesEuler()
    {
        var rod = SolidFactory.MakeThreadedRod(
            IsoProfile(MajorRadius, MinorRadius, Pitch), Pitch, 10, null, leftHand: true);
        rod.Validate();

        // Handedness changes no counts: the same K bands, K rails and 2K cap cuts.
        Assert.Equal(8, rod.Vertices.Count());
        Assert.Equal(12, rod.Edges.Count());
        Assert.Equal(6, rod.Faces.Count());
        Assert.True(rod.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void LeftHandRod_DescendsAsThePhaseAdvances()
    {
        var rod = SolidFactory.MakeThreadedRod(
            IsoProfile(MajorRadius, MinorRadius, Pitch), Pitch, 10, null, leftHand: true);
        var band = rod.Faces.Select(f => f.Surface).OfType<HelicalSurface>().First();

        Assert.Equal(-Pitch, band.Pitch, 12);
        // One full turn of phase drops the band by exactly one pitch — the definition.
        Assert.Equal(-Pitch, band.PointAt(2 * Math.PI, 0).Z - band.PointAt(0, 0).Z, 12);
    }

    [Fact]
    public void LeftHandRod_IsTheExactMirrorOfTheRightHandOne()
    {
        // The identity the whole construction rests on, and the one the Shape compiler
        // uses to lower Mirror(thread): reflecting across a plane CONTAINING the axis
        // maps phase u to −u, which is exactly what negating the axial rate does.
        var profile = IsoProfile(MajorRadius, MinorRadius, Pitch);
        var right = SolidFactory.MakeThreadedRod(profile, Pitch, 10);
        var left = SolidFactory.MakeThreadedRod(profile, Pitch, 10, null, leftHand: true);

        var rightBands = right.Faces.Where(f => f.Surface is HelicalSurface).ToList();
        var leftBands = left.Faces.Where(f => f.Surface is HelicalSurface).ToList();
        Assert.Equal(rightBands.Count, leftBands.Count);
        for (int b = 0; b < rightBands.Count; b++)
        {
            var a = (HelicalSurface)rightBands[b].Surface;
            var c = (HelicalSurface)leftBands[b].Surface;
            Assert.Equal(-a.DomainU.End, c.DomainU.Start, 12);
            Assert.Equal(-a.DomainU.Start, c.DomainU.End, 12);
            for (int i = 0; i <= 24; i++)
            {
                double u = a.DomainU.ParameterAt(i / 24.0);
                foreach (double v in (ReadOnlySpan<double>)[0, 0.5, 1])
                {
                    var p = a.PointAt(u, v);
                    // Bit-exact: the reflected right-hand point IS the left-hand
                    // evaluation, term for term — no tolerance is involved.
                    Assert.Equal(new Vector3d(p.X, -p.Y, p.Z), c.PointAt(-u, v));
                }
            }
        }
    }

    [Fact]
    public void LeftHandRod_CapCutsSpanTheSameArcsInReverse()
    {
        // The cap cuts are the other half of the mirror: each spiral runs over the
        // negated u-interval, so the chain that closes a cap traverses the same phases
        // the other way round. This is what forces the cap loops' chain ORDER to flip.
        var profile = IsoProfile(MajorRadius, MinorRadius, Pitch);
        var right = SolidFactory.MakeThreadedRod(profile, Pitch, 10);
        var left = SolidFactory.MakeThreadedRod(profile, Pitch, 10, null, leftHand: true);

        static List<SpiralArc3d> Cuts(BrepSolid solid, bool top) =>
            [.. solid.Faces
                .First(f => f.Surface is PlaneSurface plane && (plane.Origin.Z > 0) == top)
                .OuterLoop.Coedges.Select(c => c.Edge.Curve).OfType<SpiralArc3d>()];

        foreach (bool top in (ReadOnlySpan<bool>)[false, true])
        {
            var r = Cuts(right, top);
            var l = Cuts(left, top);
            Assert.Equal(4, r.Count);
            Assert.Equal(4, l.Count);
            // Same set of phase intervals, negated. Compare as sorted spans so the
            // chain order (which legitimately differs) does not enter.
            var rSpans = r.Select(a => (Math.Round(-a.Domain.End, 9), Math.Round(-a.Domain.Start, 9))).OrderBy(t => t).ToList();
            var lSpans = l.Select(a => (Math.Round(a.Domain.Start, 9), Math.Round(a.Domain.End, 9))).OrderBy(t => t).ToList();
            Assert.Equal(rSpans, lSpans);
        }
    }
}
