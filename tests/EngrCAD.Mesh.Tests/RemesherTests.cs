using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class RemesherTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A flat n x n grid patch of triangles over [0,1]², an open mesh with one boundary loop.</summary>
    private static HalfEdgeMesh GridPatch(int n)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
                positions.Add(new Vector3d((double)i / n, (double)j / n, 0));
        }
        var faces = new List<int[]>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                faces.Add([a, b, d]);
                faces.Add([a, d, c]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static (double Min, double Max, double Mean) EdgeLengths(HalfEdgeMesh mesh)
    {
        double min = double.PositiveInfinity, max = 0, sum = 0;
        int count = 0;
        foreach (var edge in mesh.Edges)
        {
            double length = edge.Vector.Length;
            min = Math.Min(min, length);
            max = Math.Max(max, length);
            sum += length;
            count++;
        }
        return (min, max, sum / count);
    }

    /// <summary>Fraction of edges outside the [Min, Max] band the algorithm maintains — the
    /// honest convergence measure (mean alone hides a pinned sliver).</summary>
    private static double FractionOutsideBand(HalfEdgeMesh mesh, RemeshOptions options)
    {
        int outside = 0, count = 0;
        foreach (var edge in mesh.Edges)
        {
            double length = edge.Vector.Length;
            if (length < options.MinEdgeLength || length > options.MaxEdgeLength)
                outside++;
            count++;
        }
        return (double)outside / count;
    }

    private static void AssertAllTriangles(HalfEdgeMesh mesh)
    {
        foreach (var face in mesh.Faces)
            Assert.Equal(3, face.Degree);
    }

    // ---------------------------------------------------------------- convergence

    [Fact]
    public void CoarseSphere_RefinesTowardTargetEdgeLength()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 12, 8).Triangulated();
        var options = new RemeshOptions(0.25)
        {
            Iterations = 40,
            // A sphere has no creases; at this tessellation its facet dihedrals are ~30°, so
            // the default feature angle would pin much of it (see FeatureDetectionOnCoarse...).
            FeatureAngleDegrees = 0,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };
        double outsideBefore = FractionOutsideBand(sphere, options);

        var result = Remesher.Remesh(sphere, options);

        Assert.True(result.Splits > 0, "a coarse mesh must be split toward the target");
        Assert.True(result.Mesh.FaceCount > sphere.FaceCount);
        Assert.True(result.Mesh.IsClosed);
        AssertAllTriangles(result.Mesh);
        // 90% of the input's edges are outside the band; the remesh brings every one of them
        // inside it — that, not the mean, is the statement isotropic remeshing makes.
        Assert.True(outsideBefore > 0.85, $"the input should be far off target, got {outsideBefore:P1}");
        Assert.Equal(0, FractionOutsideBand(result.Mesh, options));
        Assert.InRange(EdgeLengths(result.Mesh).Mean, options.MinEdgeLength, options.MaxEdgeLength);
    }

    [Fact]
    public void FineGrid_CoarsensTowardTargetEdgeLength()
    {
        var grid = GridPatch(16); // edge length 1/16 = 0.0625
        var options = new RemeshOptions(0.2) { Iterations = 12 };

        var result = Remesher.Remesh(grid, options);

        Assert.True(result.Collapses > 0, "a fine mesh must be collapsed toward the target");
        Assert.True(result.Mesh.FaceCount < grid.FaceCount);
        Assert.Single(result.Mesh.BoundaryLoops());
        AssertAllTriangles(result.Mesh);
    }

    [Fact]
    public void RemeshedMeshIsManifoldAndKeepsItsTopology()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 10).Triangulated();
        var result = Remesher.Remesh(sphere, new RemeshOptions(0.3)
        {
            Iterations = 6,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        });

        // ToMesh goes through the manifold-validating Build, so reaching here is already the
        // structural check; Euler pins that no handle or hole was introduced.
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(2, result.Mesh.EulerCharacteristic);
    }

    // ---------------------------------------------------------------- shape preservation

    [Fact]
    public void ProjectionTargetKeepsTheShape_WithoutOneSmoothingShrinksIt()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 24, 16).Triangulated();
        double exact = 4.0 / 3.0 * Math.PI;
        double discrete = sphere.Volume();

        var projected = Remesher.Remesh(sphere, new RemeshOptions(0.25)
        {
            Iterations = 10,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        }).Mesh;
        var unprojected = Remesher.Remesh(sphere, new RemeshOptions(0.25) { Iterations = 10 }).Mesh;

        // Projected: within a couple of percent of the analytic sphere (the residual is the
        // polyhedral chord error of the target itself, which is the honest bound).
        Assert.InRange(projected.Volume(), 0.97 * discrete, 1.02 * exact);
        // Unprojected: Laplacian flow is curvature flow, so it demonstrably shrinks.
        Assert.True(unprojected.Volume() < 0.99 * projected.Volume(),
            $"unprojected {unprojected.Volume():F4} should shrink below projected {projected.Volume():F4}");
    }

    [Fact]
    public void FeatureAngleKeepsCubeCornersAndVolume()
    {
        var cube = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated();
        var result = Remesher.Remesh(cube, new RemeshOptions(0.3)
        {
            Iterations = 10,
            FeatureAngleDegrees = 30,
            ProjectionTarget = new MeshProjectionTarget(cube),
        });

        Assert.True(result.Splits > 0);
        Assert.True(result.Mesh.IsClosed);
        // Every original corner survives at its exact position, and the volume is unchanged:
        // pinned crease vertices never move and pinned edges never collapse or flip.
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            Assert.Contains(result.Mesh.Vertices, v => (v.Position - corner).Length < 1e-12);
        }
        Assert.Equal(1.0, result.Mesh.Volume(), 9);
    }

    [Fact]
    public void FeatureDetectionOnCoarseCurvatureIsConservative()
    {
        // Documented limitation, pinned here so it cannot change silently: feature detection
        // reads the dihedral of the mesh it is given, and a COARSE tessellation of a smooth
        // surface has large dihedrals. UvSphere(12, 8) facets meet at ~30°, so the default
        // 30° feature angle pins most of the sphere and the remesh barely moves. Splitting
        // does not help — the halves are coplanar with their parents, so the dihedral is
        // unchanged. Pass 0 (or a larger angle) when remeshing tessellated curvature.
        var sphere = MeshPrimitives.UvSphere(1.0, 12, 8).Triangulated();
        var options = new RemeshOptions(0.25)
        {
            Iterations = 40,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };

        var pinned = Remesher.Remesh(sphere, options).Mesh;
        var free = Remesher.Remesh(sphere, options with { FeatureAngleDegrees = 0 }).Mesh;

        Assert.True(FractionOutsideBand(pinned, options) > 0.05,
            "pinned facet creases keep edges outside the band no matter how many passes run");
        Assert.Equal(0, FractionOutsideBand(free, options));
    }

    [Fact]
    public void WithoutFeatureAngle_TheCubeIsRoundedOff()
    {
        var cube = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated();
        var rounded = Remesher.Remesh(cube, new RemeshOptions(0.3)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0, // features off
        }).Mesh;

        Assert.True(rounded.Volume() < 0.99, $"unconstrained smoothing must round the corners off, got {rounded.Volume():F4}");
    }

    // ---------------------------------------------------------------- constraints

    [Fact]
    public void BoundaryVerticesStayOnTheOriginalOutline()
    {
        var grid = GridPatch(8);
        var result = Remesher.Remesh(grid, new RemeshOptions(0.08) { Iterations = 8 });

        Assert.Single(result.Mesh.BoundaryLoops());
        // Every boundary vertex is on the unit square's outline: original vertices are
        // untouched, and split-created ones are midpoints of a boundary segment.
        foreach (var loop in result.Mesh.BoundaryLoops())
        {
            foreach (var he in loop)
            {
                var p = he.Origin.Position;
                bool onOutline =
                    Math.Abs(p.X) < 1e-12 || Math.Abs(p.X - 1) < 1e-12 ||
                    Math.Abs(p.Y) < 1e-12 || Math.Abs(p.Y - 1) < 1e-12;
                Assert.True(onOutline && Math.Abs(p.Z) < 1e-12, $"boundary vertex {p} left the outline");
            }
        }
    }

    [Fact]
    public void ExplicitlyFixedVerticesSurviveAtTheirExactPosition()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 10).Triangulated();
        int[] pins = [0, 5, 17, 42];
        var pinnedPositions = pins.Select(sphere.GetPosition).ToArray();

        var result = Remesher.Remesh(sphere, new RemeshOptions(0.3)
        {
            Iterations = 8,
            FixedVertices = pins,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        });

        foreach (var p in pinnedPositions)
            Assert.Contains(result.Mesh.Vertices, v => (v.Position - p).Length == 0);
    }

    [Fact]
    public void DisablingBoundaryPreservationLetsTheOutlineMove()
    {
        var grid = GridPatch(8);
        var kept = Remesher.Remesh(grid, new RemeshOptions(0.15) { Iterations = 8 }).Mesh;
        var free = Remesher.Remesh(grid, new RemeshOptions(0.15)
        {
            Iterations = 8,
            PreserveBoundary = false,
            FeatureAngleDegrees = 0,
        }).Mesh;

        Assert.Equal(1.0, kept.ComputeBounds().Size.X, 12);
        Assert.True(free.ComputeBounds().Size.X < 1.0 - 1e-6,
            "an unconstrained boundary shrinks inward under smoothing");
    }

    // ---------------------------------------------------------------- behaviour

    [Fact]
    public void RemeshIsDeterministic()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 14, 9).Triangulated();
        var options = new RemeshOptions(0.3)
        {
            Iterations = 6,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };

        var first = Remesher.Remesh(sphere, options);
        var second = Remesher.Remesh(sphere, options);

        Assert.Equal(first.Splits, second.Splits);
        Assert.Equal(first.Collapses, second.Collapses);
        Assert.Equal(first.Flips, second.Flips);
        Assert.Equal(first.Mesh.VertexCount, second.Mesh.VertexCount);
        for (int v = 0; v < first.Mesh.VertexCount; v++)
        {
            // Bit-identical, not merely close: nothing in the algorithm is order- or
            // randomness-dependent.
            var a = first.Mesh.GetPosition(v);
            var b = second.Mesh.GetPosition(v);
            Assert.True(BitConverter.DoubleToInt64Bits(a.X) == BitConverter.DoubleToInt64Bits(b.X) &&
                        BitConverter.DoubleToInt64Bits(a.Y) == BitConverter.DoubleToInt64Bits(b.Y) &&
                        BitConverter.DoubleToInt64Bits(a.Z) == BitConverter.DoubleToInt64Bits(b.Z),
                $"vertex {v}: {a} != {b}");
        }
    }

    [Fact]
    public void ZeroIterationsReturnsTheTriangulatedInputUnchanged()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var result = Remesher.Remesh(box, new RemeshOptions(0.5) { Iterations = 0 });

        Assert.Equal(box.Triangulated().FaceCount, result.Mesh.FaceCount);
        Assert.Equal(0, result.Splits + result.Collapses + result.Flips);
        Assert.Equal(1.0, result.Mesh.Volume(), 12);
    }

    [Fact]
    public void FlipsAloneImproveValenceWithoutMovingGeometry()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 10).Triangulated();
        var before = ValenceError(sphere);

        var result = Remesher.Remesh(sphere, new RemeshOptions(0.35)
        {
            Iterations = 4,
            EnableSplits = false,
            EnableCollapses = false,
            FeatureAngleDegrees = 0,
            Smoothing = RemeshSmoothing.None,
        });

        Assert.True(result.Flips > 0);
        Assert.Equal(sphere.VertexCount, result.Mesh.VertexCount);
        Assert.Equal(sphere.FaceCount, result.Mesh.FaceCount);
        Assert.True(ValenceError(result.Mesh) < before,
            $"valence error should fall: {before} -> {ValenceError(result.Mesh)}");
        // A flip moves no vertex — every original position is still there, bit-for-bit.
        // (The enclosed volume does shift a little, because retriangulating a NON-planar
        // quad the other way is a different surface; only the vertices are invariant.)
        foreach (var vertex in sphere.Vertices)
            Assert.Contains(result.Mesh.Vertices, v => (v.Position - vertex.Position).Length == 0);

        static int ValenceError(HalfEdgeMesh mesh)
        {
            int error = 0;
            foreach (var vertex in mesh.Vertices)
            {
                if (vertex.IsIsolated || vertex.IsBoundary)
                    continue;
                error += Math.Abs(vertex.OutgoingHalfEdges().Count() - 6);
            }
            return error;
        }
    }

    [Fact]
    public void CotangentSmoothingAlsoConverges()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 10).Triangulated();
        var result = Remesher.Remesh(sphere, new RemeshOptions(0.3)
        {
            Iterations = 8,
            Smoothing = RemeshSmoothing.Cotangent,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        });

        Assert.True(result.Mesh.IsClosed);
        Assert.InRange(EdgeLengths(result.Mesh).Mean, 0.66 * 0.3, 1.33 * 0.3);
    }

    [Fact]
    public void PolygonInputIsTriangulatedFirst()
    {
        var cylinder = MeshPrimitives.Cylinder(1, 2, 16); // n-gon caps
        var result = Remesher.Remesh(cylinder, new RemeshOptions(0.5)
        {
            Iterations = 6,
            ProjectionTarget = new MeshProjectionTarget(cylinder),
        });

        AssertAllTriangles(result.Mesh);
        Assert.True(result.Mesh.IsClosed);
    }

    [Fact]
    public void CancellationThrows()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 10).Triangulated();
        using var cts = new CancellationTokenSource();
        int passes = 0;
        var progress = new ProgressCancel(cts.Token, _ =>
        {
            if (++passes == 2)
                cts.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            Remesher.Remesh(sphere, new RemeshOptions(0.2) { Iterations = 20 }, progress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTargetLengthThrows(double target) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Remesher.Remesh(MeshPrimitives.Box(1, 1, 1), new RemeshOptions(target)));

    [Fact]
    public void OutOfRangeSmoothSpeedThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Remesher.Remesh(MeshPrimitives.Box(1, 1, 1), new RemeshOptions(0.5) { SmoothSpeed = 1.5 }));

    // ---------------------------------------------------------------- scale-freedom

    [Fact]
    public void WorksAtMicronScale()
    {
        // The whole algorithm is relative to the target edge length; a model at 1e-5 scale
        // must behave exactly like the unit-scale one. (An absolute area epsilon anywhere
        // would silently reject every operation here — the BSP failure mode.)
        const double scale = 1e-5;
        var sphere = MeshPrimitives.UvSphere(scale, 12, 8).Triangulated();
        var result = Remesher.Remesh(sphere, new RemeshOptions(0.25 * scale)
        {
            Iterations = 10,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        });

        Assert.True(result.Splits > 0);
        Assert.True(result.Mesh.IsClosed);
        Assert.InRange(EdgeLengths(result.Mesh).Mean, 0.66 * 0.25 * scale, 1.33 * 0.25 * scale);
    }

    // ---------------------------------------------------------------- projection target

    // ---------------------------------------------------------------- fuzz

    [Theory]
    [InlineData(0.5, 3)]
    [InlineData(0.31, 7)]
    [InlineData(0.17, 5)]
    [InlineData(1.7, 6)]
    public void FuzzOverPrimitives_StaysManifoldAndClosed(double target, int iterations)
    {
        // EditableMesh runs a full Validate() after every operator in DEBUG and ToMesh goes
        // through the manifold-validating Build, so simply completing is the structural
        // assertion; closedness and Euler pin that no hole or handle was opened.
        HalfEdgeMesh[] solids =
        [
            MeshPrimitives.Box(1, 2, 3).Triangulated(),
            MeshPrimitives.UvSphere(1.0, 13, 9).Triangulated(),
            MeshPrimitives.Cylinder(0.7, 2.0, 11).Triangulated(),
            MeshPrimitives.Cone(1.0, 0.3, 1.5, 9).Triangulated(),
            MeshPrimitives.Cone(1.0, 0.0, 1.5, 12).Triangulated(),
        ];
        foreach (var solid in solids)
        {
            var result = Remesher.Remesh(solid, new RemeshOptions(target)
            {
                Iterations = iterations,
                ProjectionTarget = new MeshProjectionTarget(solid),
            });
            Assert.True(result.Mesh.IsClosed);
            Assert.Equal(2, result.Mesh.EulerCharacteristic);
            AssertAllTriangles(result.Mesh);
            Assert.True(result.Mesh.Volume() > 0);
        }
    }

    [Fact]
    public void FuzzOnOpenSurfaces_KeepsExactlyItsBoundaryLoops()
    {
        HalfEdgeMesh[] patches = [GridPatch(5), GridPatch(11)];
        foreach (var patch in patches)
        {
            foreach (double target in new[] { 0.07, 0.13, 0.4 })
            {
                var result = Remesher.Remesh(patch, new RemeshOptions(target) { Iterations = 6 });
                Assert.Single(result.Mesh.BoundaryLoops());
                Assert.Equal(1, result.Mesh.EulerCharacteristic); // a disk
                AssertAllTriangles(result.Mesh);
            }
        }
    }

    // ---------------------------------------------------------------- scheduling

    [Fact]
    public void QueueScheduling_ConvergesAsWell()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 24, 16).Triangulated();
        var options = new RemeshOptions(0.15)
        {
            Iterations = 20,
            FeatureAngleDegrees = 0,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };

        var swept = Remesher.Remesh(sphere, options);
        var queued = Remesher.Remesh(sphere, options with { Scheduling = RemeshScheduling.Queue });

        queued.Mesh.Validate();
        Assert.True(queued.Mesh.IsClosed);
        Assert.Equal(2, queued.Mesh.EulerCharacteristic);
        AssertAllTriangles(queued.Mesh);

        // Not the same answer — a quiet region stops being smoothed — but the convergence
        // metric must land in the same place, or the scheduler is losing work rather than
        // skipping work that had nothing to do.
        double sweptOut = FractionOutsideBand(swept.Mesh, options);
        double queuedOut = FractionOutsideBand(queued.Mesh, options);
        Assert.True(queuedOut < 0.05, $"{queuedOut:P1} of edges outside the band");
        Assert.True(queuedOut < sweptOut + 0.03, $"sweep {sweptOut:P1} vs queue {queuedOut:P1}");
    }

    [Fact]
    public void QueueScheduling_IsDeterministic()
    {
        // FIFO queues seeded in the sweep's own stride order, no RNG: two runs must agree
        // bit for bit, as the sweep path does.
        var patch = GridPatch(9);
        var options = new RemeshOptions(0.08)
        {
            Iterations = 8,
            Scheduling = RemeshScheduling.Queue,
            FeatureAngleDegrees = 0,
        };

        var (p1, f1) = Remesher.Remesh(patch, options).Mesh.ToIndexed();
        var (p2, f2) = Remesher.Remesh(patch, options).Mesh.ToIndexed();

        Assert.Equal(p1, p2); // Vector3d equality is bitwise
        Assert.Equal(f1.Count, f2.Count);
        for (int i = 0; i < f1.Count; i++)
            Assert.Equal(f1[i], f2[i]);
    }

    [Fact]
    public void QueueScheduling_FirstPassMatchesTheSweep()
    {
        // The queues are seeded with the whole mesh in the sweep's own stride order, so a
        // single-pass remesh is identical either way; only the passes AFTER it differ.
        var patch = GridPatch(7);
        var options = new RemeshOptions(0.09) { Iterations = 1, FeatureAngleDegrees = 0 };

        var (sweptPositions, sweptFaces) = Remesher.Remesh(patch, options).Mesh.ToIndexed();
        var (queuedPositions, queuedFaces) =
            Remesher.Remesh(patch, options with { Scheduling = RemeshScheduling.Queue }).Mesh.ToIndexed();

        Assert.Equal(sweptPositions, queuedPositions);
        Assert.Equal(sweptFaces.Count, queuedFaces.Count);
        for (int i = 0; i < sweptFaces.Count; i++)
            Assert.Equal(sweptFaces[i], queuedFaces[i]);
    }

    [Fact]
    public void FastSplitPasses_SplitAndNothingElse()
    {
        var patch = GridPatch(4);   // edges of 0.25 and 0.354
        var options = new RemeshOptions(0.1) { Iterations = 0, FastSplitPasses = 3 };

        var result = Remesher.Remesh(patch, options);

        result.Mesh.Validate();
        Assert.Equal(0, result.Collapses);
        Assert.Equal(0, result.Flips);
        Assert.True(result.Splits > 0);
        Assert.True(result.Mesh.FaceCount > patch.FaceCount);
        // Split-only, so every original vertex is still exactly where it was: no smoothing
        // pass ran, and the prepass never moves anything.
        foreach (var vertex in patch.Vertices)
            Assert.Contains(result.Mesh.Vertices, v => v.Position == vertex.Position);
        // Three halvings take the long diagonal 0.354 below 1.33 x 0.1.
        Assert.True(EdgeLengths(result.Mesh).Max <= 0.133 + 1e-12);
    }

    [Fact]
    public void FastSplitPasses_StopEarlyWhenNothingIsTooLong()
    {
        // An over-generous count costs only the sweeps that find nothing, and the loop
        // breaks out rather than sweeping a converged mesh ninety more times.
        var patch = GridPatch(4);
        var few = Remesher.Remesh(patch, new RemeshOptions(0.1) { Iterations = 0, FastSplitPasses = 3 });
        var many = Remesher.Remesh(patch, new RemeshOptions(0.1) { Iterations = 0, FastSplitPasses = 90 });

        Assert.Equal(few.Splits, many.Splits);
        Assert.Equal(few.Mesh.FaceCount, many.Mesh.FaceCount);
    }

    [Fact]
    public void FastSplitPasses_RejectNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Remesher.Remesh(GridPatch(3), new RemeshOptions(0.1) { FastSplitPasses = -1 }));
    }

    // ---------------------------------------------------------------- projection target

    [Fact]
    public void MeshProjectionTargetFindsTheClosestSurfacePoint()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2))).Triangulated();
        var target = new MeshProjectionTarget(box);

        // Face region, edge region, vertex region, and a point already inside.
        Assert.Equal(new Vector3d(1, 1, 2), target.Project(new Vector3d(1, 1, 5)));
        Assert.Equal(new Vector3d(2, 2, 1), target.Project(new Vector3d(4, 4, 1)));
        Assert.Equal(new Vector3d(2, 2, 2), target.Project(new Vector3d(3, 3, 3)));
        var inside = target.Project(new Vector3d(1, 1, 1.9));
        Assert.Equal(new Vector3d(1, 1, 2), inside); // unsigned: nearest surface point
    }

    [Fact]
    public void MeshProjectionTargetIsIdempotentOnItsOwnVertices()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 10).Triangulated();
        var target = new MeshProjectionTarget(sphere);
        foreach (var vertex in sphere.Vertices)
            Assert.True((target.Project(vertex.Position) - vertex.Position).Length < 1e-12);
    }
}
