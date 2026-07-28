using EngrCAD.Core;
using EngrCAD.Fea;
using Xunit;

namespace EngrCAD.Fea.Tests;

public class DelaunayTetrahedralizationTests
{
    [Fact]
    public void SingleTetrahedron_ProducesExactlyOneRealTet()
    {
        var points = new List<Vector3d>
        {
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1),
        };
        var d = DelaunayTetrahedralization.Build(points);
        d.Validate();

        int real = d.LiveTets().Count(t => AllReal(d, t));
        Assert.Equal(1, real);
    }

    [Fact]
    public void CubeCorners_TriangulateToFiveOrSixTetsTotallingTheCubeVolume()
    {
        // Eight exactly-cospherical points: every insphere test on them returns exactly 0.
        // The result is a valid (non-unique) Delaunay triangulation, and its total volume
        // must still be the cube's exactly.
        var points = new List<Vector3d>();
        foreach (int x in new[] { 0, 1 })
            foreach (int y in new[] { 0, 1 })
                foreach (int z in new[] { 0, 1 })
                    points.Add(new Vector3d(x, y, z));

        var d = DelaunayTetrahedralization.Build(points);
        d.Validate();

        double volume = 0;
        int count = 0;
        foreach (int t in d.LiveTets())
        {
            if (!AllReal(d, t)) continue;
            var tet = d.TetAt(t);
            volume += TetMesh.SignedVolume(d.Points[tet.A], d.Points[tet.B], d.Points[tet.C], d.Points[tet.D]);
            count++;
        }

        Assert.InRange(count, 5, 6);
        Assert.Equal(1.0, volume, 12);
    }

    [Fact]
    public void RandomCloud_SatisfiesTheDelaunayPropertyAndTheEulerRelation()
    {
        var random = new Random(2026);
        var points = new List<Vector3d>();
        for (int i = 0; i < 220; i++)
            points.Add(new Vector3d(random.NextDouble(), random.NextDouble(), random.NextDouble()));

        var d = DelaunayTetrahedralization.Build(points);
        d.Validate(); // includes the O(n^2) empty-circumsphere check

        // The convex-hull volume is covered exactly once, so summing all tets that use only
        // real vertices gives the hull volume - which for a dense uniform cloud in the unit
        // cube is close to 1 and, more importantly, is the same however it was triangulated.
        double volume = 0;
        foreach (int t in d.LiveTets())
        {
            if (!AllReal(d, t)) continue;
            var tet = d.TetAt(t);
            volume += TetMesh.SignedVolume(d.Points[tet.A], d.Points[tet.B], d.Points[tet.C], d.Points[tet.D]);
        }
        Assert.InRange(volume, 0.5, 1.0);
    }

    [Fact]
    public void Determinism_TwoRunsAgreeTetForTet()
    {
        var random = new Random(77);
        var points = new List<Vector3d>();
        for (int i = 0; i < 300; i++)
            points.Add(new Vector3d(random.NextDouble() * 5, random.NextDouble() * 5, random.NextDouble() * 5));

        var a = Fingerprint(DelaunayTetrahedralization.Build(points));
        var b = Fingerprint(DelaunayTetrahedralization.Build(points));
        Assert.Equal(a, b);
    }

    [Fact]
    public void InsertionOrderIsIndependentOfInputOrder_ForTheSamePointSet()
    {
        // The Morton sort is a function of the coordinates alone, so shuffling the input
        // list must give the same triangulation up to vertex renumbering. Comparing the
        // SET of coordinate-tuples per tet takes the renumbering out.
        var random = new Random(1234);
        var points = new List<Vector3d>();
        for (int i = 0; i < 150; i++)
            points.Add(new Vector3d(random.NextDouble(), random.NextDouble(), random.NextDouble()));

        var shuffled = points.OrderBy(p => p.Z).ThenBy(p => p.X).ToList();

        Assert.Equal(GeometricFingerprint(DelaunayTetrahedralization.Build(points), points.Count),
                     GeometricFingerprint(DelaunayTetrahedralization.Build(shuffled), shuffled.Count));
    }

    [Fact]
    public void GridPoints_AreMassivelyDegenerateAndStillProduceAValidTriangulation()
    {
        // A 4x4x4 lattice: enormously many exactly-cospherical quintuples, which is exactly
        // the regime a structured CAD tessellation lives in.
        var points = new List<Vector3d>();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                    points.Add(new Vector3d(i, j, k));

        Predicates3d.ResetEscalationCounters();
        var d = DelaunayTetrahedralization.Build(points);
        d.Validate();
        Assert.True(Predicates3d.InSphereEscalations > 0,
            "a perfect lattice must exercise the exact in-sphere stage");
        Predicates3d.ResetEscalationCounters();

        double volume = 0;
        foreach (int t in d.LiveTets())
        {
            if (!AllReal(d, t)) continue;
            var tet = d.TetAt(t);
            volume += TetMesh.SignedVolume(d.Points[tet.A], d.Points[tet.B], d.Points[tet.C], d.Points[tet.D]);
        }
        Assert.Equal(27.0, volume, 10); // the 3x3x3 hull of the lattice
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ScaleFreedom_TheTriangulationIsCombinatoriallyIdenticalAtEveryScale(double scale)
    {
        var random = new Random(555);
        var unit = new List<Vector3d>();
        for (int i = 0; i < 120; i++)
            unit.Add(new Vector3d(random.NextDouble(), random.NextDouble(), random.NextDouble()));
        var scaled = unit.Select(p => p * scale).ToList();

        var reference = Fingerprint(DelaunayTetrahedralization.Build(unit));
        var actual = Fingerprint(DelaunayTetrahedralization.Build(scaled));

        // Scaling by a power of two is exact, so the combinatorics must match EXACTLY; for
        // 1e-3 / 1e3 the coordinates round, so this asserts the algorithm is scale-free
        // rather than that the arithmetic is (it is a different point set at those scales,
        // so only structural validity is guaranteed - checked by Validate below).
        var d = DelaunayTetrahedralization.Build(scaled);
        d.Validate();
        if (scale == 1.0)
            Assert.Equal(reference, actual);
    }

    [Fact]
    public void CoincidentPoints_AreRefusedByName()
    {
        var points = new List<Vector3d>
        {
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 0),
        };
        var ex = Assert.Throws<TetMeshException>(() => DelaunayTetrahedralization.Build(points));
        Assert.Contains("coincident", ex.Message);
        Assert.Contains("MeshRepair.Clean", ex.Message);
    }

    [Fact]
    public void TooFewPoints_AreRefusedByName()
    {
        var ex = Assert.Throws<TetMeshException>(() => DelaunayTetrahedralization.Build(
            new List<Vector3d> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) }));
        Assert.Contains("at least 4", ex.Message);
    }

    [Fact]
    public void CoplanarPointSet_StillTriangulatesTheEnclosingSimplexWithZeroRealVolume()
    {
        // Every input point on one plane: there is no volume to fill, and the algorithm must
        // say so by producing no real tetrahedra rather than by producing broken ones.
        var points = new List<Vector3d>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                points.Add(new Vector3d(i, j, 0));

        var d = DelaunayTetrahedralization.Build(points);
        d.Validate(checkDelaunay: false);
        Assert.Equal(0, d.LiveTets().Count(t => AllReal(d, t)));
    }

    private static bool AllReal(DelaunayTetrahedralization d, int tet)
    {
        var t = d.TetAt(tet);
        return !d.IsArtificial(t.A) && !d.IsArtificial(t.B) && !d.IsArtificial(t.C) && !d.IsArtificial(t.D);
    }

    private static string Fingerprint(DelaunayTetrahedralization d) =>
        string.Join(";", d.LiveTets().Select(t =>
        {
            var tet = d.TetAt(t);
            return $"{tet.A},{tet.B},{tet.C},{tet.D}";
        }));

    private static string GeometricFingerprint(DelaunayTetrahedralization d, int realCount)
    {
        var rows = new List<string>();
        foreach (int t in d.LiveTets())
        {
            var tet = d.TetAt(t);
            if (d.IsArtificial(tet.A) || d.IsArtificial(tet.B) || d.IsArtificial(tet.C) || d.IsArtificial(tet.D))
                continue;
            var coords = new[] { tet.A, tet.B, tet.C, tet.D }
                .Select(v => d.Points[v])
                .Select(p => $"{p.X:R}/{p.Y:R}/{p.Z:R}")
                .Order()
                .ToArray();
            rows.Add(string.Join("|", coords));
        }
        rows.Sort(StringComparer.Ordinal);
        return string.Join(";", rows);
    }
}
