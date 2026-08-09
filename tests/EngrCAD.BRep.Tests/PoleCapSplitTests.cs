using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Splitting an axis-touching revolve's flat POLE CAP by a chord — the face-level case the
/// boolean meets every time a blind bore's flat bottom breaks out of a face, and which had
/// no face-level fixture because half of it did not work.
///
/// <para><b>The defect was the generator's DIRECTION, and nothing else.</b> The arrangement's
/// even-odd test fires one ray along +v, which is correct only where the trim CLOSES in that
/// direction — and a pole is the one place it does not. A profile leaving the axis puts the
/// rim at v = max, so the ray crosses it and the cap splits; one returning to the axis puts
/// the rim at v = min, the ray crosses nothing, every interior point reads as outside, and the
/// same cap comes back as a single fragment with its rim edge dutifully split and no interior
/// edge made. Measured before the fix: 56 of these 112 cases, and they were exactly the 56 on
/// the cap whose generator ends on the axis.</para>
///
/// <para>The two caps of ONE revolve differ in precisely that, which is what makes them a
/// controlled pair rather than two fixtures: same surface family, same radius, same chord, and
/// the only variable is which end of the generator the axis is at.</para>
/// </summary>
public class PoleCapSplitTests
{
    private const double Radius = 3, Height = 10;

    /// <summary>
    /// A cylinder built as ONE full-turn revolve, so BOTH caps are pole caps: the bottom's
    /// generator leaves the axis, the top's returns to it.
    /// </summary>
    private static BrepSolid Tool()
    {
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        var profile = Profile.FromLoop(
            [new Vector2d(0, 0), new Vector2d(Radius, 0), new Vector2d(Radius, Height), new Vector2d(0, Height)],
            frame);
        return SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ);
    }

    private static BrepFace CapAt(BrepSolid solid, double z) =>
        solid.Faces.First(f => f.Surface is RevolvedSurface && Math.Abs(f.Bounds().Center.Z - z) < 1e-9);

    /// <summary>The chord of the cap at <paramref name="z"/> whose perpendicular offset from
    /// the axis is <paramref name="offset"/>, both ends EXACTLY on the rim.</summary>
    private static (Vector3d A, Vector3d B) Chord(double offset, double azimuth, double z)
    {
        double half = Math.Sqrt(Radius * Radius - offset * offset);
        var n = new Vector3d(Math.Cos(azimuth), Math.Sin(azimuth), 0);
        var t = new Vector3d(-Math.Sin(azimuth), Math.Cos(azimuth), 0);
        return (n * offset - t * half + Vector3d.UnitZ * z, n * offset + t * half + Vector3d.UnitZ * z);
    }

    /// <summary>A planar face's area from its own boundary polyline, by the shoelace sum in
    /// the cap's plane. Exact for the straight chord and chordal on the rim arc, which is
    /// why the assertions below carry the inscribed-polygon deficit explicitly.</summary>
    private static double SampledArea(BrepFace face, int samplesPerCoedge = 512)
    {
        double twiceArea = 0;
        foreach (var coedge in face.Loops[0].Coedges)
        {
            var domain = coedge.Edge.Domain;
            var previous = Vector3d.Zero;
            for (int i = 0; i <= samplesPerCoedge; i++)
            {
                double fraction = coedge.SameSense ? (double)i / samplesPerCoedge : 1 - (double)i / samplesPerCoedge;
                var p = coedge.Edge.Curve.PointAt(domain.ParameterAt(fraction));
                if (i > 0)
                    twiceArea += previous.X * p.Y - p.X * previous.Y;
                previous = p;
            }
        }
        return Math.Abs(twiceArea) / 2;
    }

    public static TheoryData<double, double, double, bool> Cases()
    {
        var data = new TheoryData<double, double, double, bool>();
        foreach (double z in (double[])[0, Height])
        foreach (bool polyline in (bool[])[false, true])
        foreach (double offset in (double[])[0.05, 1.0, 2.0, 2.9])
        foreach (double azimuth in (double[])[0, 1.0, Math.PI, -0.7])
            data.Add(z, offset, azimuth, polyline);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AChordSplitsAPoleCapWhicheverWayItsGeneratorRuns(
        double capZ, double offset, double azimuth, bool polyline)
    {
        var cap = CapAt(Tool(), capZ);
        var (a, b) = Chord(offset, azimuth, capZ);
        Curve3d chord = polyline
            ? new PolylineCurve3d([.. Enumerable.Range(0, 25).Select(i => a + (b - a) * (i / 24.0))])
            : new Line3d(a, b);

        var pieces = FaceSplitter.SplitByCurve(cap, chord);

        Assert.Equal(2, pieces.Count);
        // The chord becomes ONE interior edge used by both pieces, which is what makes the
        // result two-manifold; a "split" that only cut the rim leaves them sharing nothing.
        var shared = pieces[0].Loops[0].Coedges.Select(c => c.Edge)
            .Intersect(pieces[1].Loops[0].Coedges.Select(c => c.Edge)).ToList();
        Assert.Single(shared);

        // The analytic partition: a chord at perpendicular offset d cuts a disc of radius R
        // into a minor segment of R^2 acos(d/R) - d sqrt(R^2 - d^2) and the rest. Both are
        // measured off the fragments' own boundary curves, so this says the split landed
        // where the chord is rather than merely that it produced two of something.
        //
        // The bound is DERIVED and ONE-SIDED rather than a tolerance: a shoelace over
        // sampled points inscribes the rim arc, so each area must come in at or under its
        // analytic value and no further under than the n-chord deficit R^2 T^3 / (12 n^2)
        // of a whole turn, 7.1e-4 here. A symmetric band would have hidden the direction.
        const int samples = 512;
        double deficit = Radius * Radius * Math.Pow(2 * Math.PI, 3) / (12.0 * samples * samples);
        double minor = Radius * Radius * Math.Acos(offset / Radius)
            - offset * Math.Sqrt(Radius * Radius - offset * offset);
        double[] areas = [.. pieces.Select(p => SampledArea(p, samples)).Order()];
        foreach (var (measured, exact) in
            (ReadOnlySpan<(double, double)>)[(areas[0], minor), (areas[1], Math.PI * Radius * Radius - minor)])
        {
            Assert.InRange(measured, exact - deficit, exact);
        }
    }

    /// <summary>
    /// The fixture is a controlled pair only if the two caps really do differ in the way the
    /// class doc claims, so that is asserted rather than assumed: one cap's surface wants the
    /// downward ray and the other does not.
    /// </summary>
    [Fact]
    public void TheTwoCapsGeneratorsRunOppositeWays()
    {
        var solid = Tool();
        Assert.False(FaceGeometry.ParityRayPointsDown(CapAt(solid, 0).Surface));
        Assert.True(FaceGeometry.ParityRayPointsDown(CapAt(solid, Height).Surface));

        // And the rule is about a POLE rather than about revolves: the cylindrical band
        // between them closes in both directions and keeps the incumbent upward ray.
        var band = solid.Faces.First(f => f.Loops.Count == 2);
        Assert.False(FaceGeometry.ParityRayPointsDown(band.Surface));
    }
}
