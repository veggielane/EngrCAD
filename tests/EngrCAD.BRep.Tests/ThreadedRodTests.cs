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
}
