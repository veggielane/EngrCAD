using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshWindingNumberTests
{
    [Fact]
    public void Direct_SkipsTheHierarchyAndStillAnswersExactly()
    {
        // The hierarchy costs ~0.7 µs per triangle to build and an exact query ~20 ns per
        // triangle, so callers making a handful of queries — a boolean asks one per surface
        // patch — are better off without it. Skipping it must be a cost decision only: every
        // entry point has to keep answering, and answer at least as accurately.
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16).Triangulated();
        var fast = MeshWindingNumber.FromTriangulated(sphere);
        var direct = MeshWindingNumber.Direct(sphere);

        foreach (var p in new Vector3d[] { (0, 0, 0), (0.4, -0.2, 0.1), (0.99, 0, 0), (2, 0, 0), (0, 3, -1.5) })
        {
            Assert.Equal(fast.WindingNumber(p), direct.WindingNumber(p), 12);
            // FastWindingNumber falls through to the exact sum, so it is exact here, not zero.
            Assert.Equal(direct.WindingNumber(p), direct.FastWindingNumber(p), 12);
            Assert.Equal(fast.IsInside(p), direct.IsInside(p));
        }
    }

    [Fact]
    public void Direct_RejectsMeshesThatAreNotTriangulated()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))); // quad faces

        Assert.Throws<ArgumentException>(() => MeshWindingNumber.Direct(box));
    }

    [Fact]
    public void ClosedSphere_ExactWinding_IsOneInsideZeroOutside()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24);
        var winding = new MeshWindingNumber(sphere);

        // Deep interior wraps exactly once; far exterior wraps zero times.
        Assert.Equal(1.0, winding.WindingNumber(Vector3d.Zero), 6);
        Assert.Equal(1.0, winding.WindingNumber((0.4, -0.2, 0.1)), 6);
        Assert.Equal(0.0, winding.WindingNumber((2.0, 0, 0)), 6);
        Assert.Equal(0.0, winding.WindingNumber((0, 3.0, -1.5)), 6);

        Assert.True(winding.IsInside((0.5, 0.5, 0.2)));
        Assert.False(winding.IsInside((1.5, 0, 0)));
    }

    [Fact]
    public void ExactWinding_SignAgreesWithPseudonormalSdf_OnClosedMesh()
    {
        // The whole point of winding classification is to reproduce the true inside/outside
        // partition. Compare its sign against the analytic sphere everywhere off the surface.
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 40, rings: 20);
        var winding = new MeshWindingNumber(sphere);

        var rng = new Random(1);
        for (int i = 0; i < 3000; i++)
        {
            var p = new Vector3d(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            if (Math.Abs(p.Length - 1.0) < 0.05)
                continue; // skip the thin shell where tessellation disagrees with the ideal sphere
            bool trulyInside = p.Length < 1.0;
            Assert.Equal(trulyInside, winding.IsInside(p));
        }
    }

    [Fact]
    public void FastWinding_MatchesExact_OnThousandsOfPoints()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24);
        var winding = new MeshWindingNumber(sphere);

        var rng = new Random(7);
        double maxError = 0;
        for (int i = 0; i < 4000; i++)
        {
            var p = new Vector3d(rng.NextDouble() * 6 - 3, rng.NextDouble() * 6 - 3, rng.NextDouble() * 6 - 3);
            double exact = winding.WindingNumber(p);
            double fast = winding.FastWindingNumber(p);
            maxError = Math.Max(maxError, Math.Abs(exact - fast));
        }
        // Order-2 multipole with beta = 2: comfortably below the 0.5 decision threshold, so
        // fast and exact classification never disagree.
        Assert.True(maxError < 0.1, $"max |exact - fast| = {maxError:E3}");
    }

    [Fact]
    public void FastWinding_ClassificationNeverDisagreesWithExact()
    {
        var box = MeshPrimitives.Box(2, 2, 2);
        var winding = new MeshWindingNumber(box);

        var rng = new Random(11);
        for (int i = 0; i < 3000; i++)
        {
            var p = new Vector3d(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            Assert.Equal(winding.WindingNumber(p) > 0.5, winding.FastWindingNumber(p) > 0.5);
        }
    }

    [Fact]
    public void HigherBeta_DoesNotWorsenAccuracy()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        var loose = new MeshWindingNumber(sphere, beta: 1.5);
        var tight = new MeshWindingNumber(sphere, beta: 4.0);

        var rng = new Random(3);
        double looseMax = 0, tightMax = 0;
        for (int i = 0; i < 2000; i++)
        {
            var p = new Vector3d(rng.NextDouble() * 6 - 3, rng.NextDouble() * 6 - 3, rng.NextDouble() * 6 - 3);
            double exact = loose.WindingNumber(p);
            looseMax = Math.Max(looseMax, Math.Abs(exact - loose.FastWindingNumber(p)));
            tightMax = Math.Max(tightMax, Math.Abs(exact - tight.FastWindingNumber(p)));
        }
        // A larger far-field radius admits fewer approximations, so error cannot increase.
        Assert.True(tightMax <= looseMax + 1e-12, $"tight {tightMax:E3} > loose {looseMax:E3}");
    }

    [Fact]
    public void OpenMesh_SphereWithHole_ClassifiesSensiblyNearSurface()
    {
        // A closed sphere with a small cap of faces removed: the generalized winding number
        // still reads ~1 in the interior and ~0 outside, away from the hole — where a
        // pseudonormal/ray test would be undefined (the mesh is not watertight).
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24);
        var open = RemoveCap(sphere, holeCenter: Vector3d.UnitZ, cosThreshold: 0.9);
        Assert.False(open.IsClosed); // genuinely open

        var winding = new MeshWindingNumber(open);

        // Interior points far from the hole still classify inside.
        Assert.True(winding.WindingNumber((0, 0, -0.5)) > 0.9);
        Assert.True(winding.IsInside((0.3, 0.2, -0.4)));

        // Exterior points far from the hole still classify outside.
        Assert.True(Math.Abs(winding.WindingNumber((2.0, 0, 0))) < 0.1);
        Assert.False(winding.IsInside((0, 0, -1.6)));

        // Fast approximation tracks the exact value on the open mesh too.
        var rng = new Random(5);
        double maxError = 0;
        for (int i = 0; i < 2000; i++)
        {
            var p = new Vector3d(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            maxError = Math.Max(maxError, Math.Abs(winding.WindingNumber(p) - winding.FastWindingNumber(p)));
        }
        Assert.True(maxError < 0.15, $"open-mesh max error {maxError:E3}");
    }

    [Fact]
    public void SolidAngle_OfEnclosingMesh_SumsToFourPi()
    {
        // Van Oosterom–Strackee sanity: total solid angle from an interior point is 4π.
        var box = MeshPrimitives.Box(2, 2, 2).Triangulated();
        double sum = 0;
        foreach (var f in box.Faces)
        {
            var h = f.AnyHalfEdge;
            sum += MeshWindingNumber.TriangleSolidAngle(
                h.Origin.Position, h.Next.Origin.Position, h.Next.Next.Origin.Position, Vector3d.Zero);
        }
        Assert.Equal(4 * Math.PI, sum, 9);
    }

    /// <summary>
    /// Removes the fan of faces whose outward direction points within
    /// <paramref name="cosThreshold"/> of <paramref name="holeCenter"/>, leaving a boundary
    /// hole. Rebuilds from the surviving faces (isolated vertices are dropped by re-indexing).
    /// </summary>
    private static HalfEdgeMesh RemoveCap(HalfEdgeMesh mesh, Vector3d holeCenter, double cosThreshold)
    {
        var dir = holeCenter.Normalized();
        var (positions, faces) = mesh.ToIndexed();
        var kept = new List<int[]>();
        foreach (var face in faces)
        {
            var centroid = Vector3d.Zero;
            foreach (int v in face)
                centroid += positions[v];
            centroid /= face.Length;
            if (centroid.Normalized().Dot(dir) < cosThreshold)
                kept.Add(face);
        }

        // Re-index to drop now-isolated vertices so Build doesn't see them.
        var remap = new Dictionary<int, int>();
        var newPositions = new List<Vector3d>();
        var newFaces = new List<int[]>(kept.Count);
        foreach (var face in kept)
        {
            var nf = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
            {
                if (!remap.TryGetValue(face[i], out int idx))
                {
                    idx = newPositions.Count;
                    remap[face[i]] = idx;
                    newPositions.Add(positions[face[i]]);
                }
                nf[i] = idx;
            }
            newFaces.Add(nf);
        }
        return HalfEdgeMesh.Build(newPositions, newFaces);
    }
}
