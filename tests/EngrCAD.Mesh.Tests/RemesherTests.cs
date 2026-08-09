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

    /// <summary>
    /// Documented limitation, pinned here so it cannot change silently: feature detection reads
    /// the dihedral of the mesh it is given, and a COARSE tessellation of a smooth surface has
    /// large dihedrals. <c>UvSphere(12, 8)</c> facets meet at ~30°, so the default 30° feature
    /// angle pins most of the sphere. Splitting does not help — the halves are coplanar with
    /// their parents, so the dihedral is unchanged. Pass 0 (or a larger angle) when remeshing
    /// tessellated curvature.
    /// <para>
    /// <b>Which measure shows it moved when the flip guard became the default</b>, and that is
    /// the interesting half. It used to show up as edge LENGTH — 13.4% of edges out of band
    /// against 0% unpinned — and the guard takes that to 0.51%, so a length test would now
    /// read the limitation as almost gone. It is not: the pinned run's worst free triangle
    /// angle is <b>29.2° against 38.6°</b>, and its constrained triangle count 8 against 0.
    /// The limitation moved from the length distribution into the SHAPE, which is precisely
    /// what <see cref="TriangleQuality"/> exists to see and what a band fraction cannot.
    /// </para>
    /// </summary>
    [Fact]
    public void FeatureDetectionOnCoarseCurvatureIsConservative()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 12, 8).Triangulated();
        var options = new RemeshOptions(0.25)
        {
            Iterations = 40,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };

        var pinned = Remesher.Remesh(sphere, options);
        var free = Remesher.Remesh(sphere, options with { FeatureAngleDegrees = 0 });

        Assert.True(FractionOutsideBand(pinned.Mesh, options) > FractionOutsideBand(free.Mesh, options),
            "pinned facet creases still keep edges out of band, just far fewer of them");
        Assert.Equal(0, FractionOutsideBand(free.Mesh, options));
        Assert.True(free.Quality.MinAngleDegrees > pinned.Quality.MinAngleDegrees + 5,
            $"unpinned should be markedly better shaped: {free.Quality.MinAngleDegrees:F2} " +
            $"against {pinned.Quality.MinAngleDegrees:F2} degrees");
        Assert.Equal(0, free.Quality.ConstrainedCount);
        Assert.True(pinned.Quality.ConstrainedCount > 0);
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

    /// <summary>
    /// The queues are seeded with the whole mesh in the sweep's own stride order, so the first
    /// pass visits the same edges in the same order — and it produces the identical mesh
    /// <b>as long as no split re-canonicalizes an edge that has not been visited yet</b>.
    /// <para>
    /// That condition is not a formality, and it is stated here because the incumbent claim
    /// ("the seeded first pass is identical") was really a property of this fixture. The two
    /// paths handle a stale id differently by design: <c>SweepEdges</c> SKIPS a half-edge that
    /// is no longer the smaller of its pair, while <c>DrainEdgeQueue</c> re-canonicalizes and
    /// processes it (it must — a collapse merges edge pairs, so the survivor is usually named
    /// by the other half). A split renumbers twins, so after enough splits the queue processes
    /// edges the sweep declines. Measured on this fixture: with
    /// <see cref="RemeshOptions.PreventLongEdgeFlips"/> off, 126 splits and 35 flips and the
    /// two meshes agree bit for bit; with it on the guard turns flips into splits (149 and 14),
    /// the sweep and the queue take 14 and 12 flips respectively, and 8 of 213 vertices land
    /// in different places — same vertex count, same face count, same connectivity.
    /// </para>
    /// </summary>
    [Fact]
    public void QueueScheduling_FirstPassMatchesTheSweepUntilASplitRenumbersATwin()
    {
        var patch = GridPatch(7);
        var options = new RemeshOptions(0.09)
        {
            Iterations = 1, FeatureAngleDegrees = 0, PreventLongEdgeFlips = false,
        };

        var swept = Remesher.Remesh(patch, options);
        var queued = Remesher.Remesh(patch, options with { Scheduling = RemeshScheduling.Queue });
        var (sweptPositions, sweptFaces) = swept.Mesh.ToIndexed();
        var (queuedPositions, queuedFaces) = queued.Mesh.ToIndexed();

        Assert.Equal(sweptPositions, queuedPositions); // Vector3d equality is bitwise
        Assert.Equal(sweptFaces.Count, queuedFaces.Count);
        for (int i = 0; i < sweptFaces.Count; i++)
            Assert.Equal(sweptFaces[i], queuedFaces[i]);

        // And the condition, pinned so nobody re-derives the stronger claim: more splits reach
        // the re-canonicalization and the two diverge — in POSITIONS only, never in structure.
        var guardedSweep = Remesher.Remesh(patch, options with { PreventLongEdgeFlips = true });
        var guardedQueue = Remesher.Remesh(patch, options with
        {
            PreventLongEdgeFlips = true, Scheduling = RemeshScheduling.Queue,
        });
        Assert.True(guardedSweep.Splits > swept.Splits, "the guard should turn flips into splits");
        Assert.Equal(guardedSweep.Splits, guardedQueue.Splits);
        Assert.Equal(guardedSweep.Mesh.VertexCount, guardedQueue.Mesh.VertexCount);
        Assert.Equal(guardedSweep.Mesh.FaceCount, guardedQueue.Mesh.FaceCount);
        Assert.NotEqual(guardedSweep.Mesh.ToIndexed().Positions, guardedQueue.Mesh.ToIndexed().Positions);
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

    // ------------------------------------------- face-aligned projection under queue scheduling

    /// <summary>
    /// The mesh must be fine enough relative to its target that regions actually settle: on a
    /// COARSE fixture (a 20 × 13 sphere at a 0.22 target) the whole mesh stays awake every
    /// pass even at 60 passes — measured, 42 996 faces accumulated of a possible 42 996 — so
    /// the restriction never fires and any comparison against the unrestricted path is
    /// vacuous. This is the same fixture family as <c>RemesherSchedulingBenchmark</c>'s.
    /// </summary>
    private static HalfEdgeMesh FaceAlignedQueueSphere() =>
        MeshPrimitives.UvSphere(1.0, 48, 32).Triangulated();

    private static RemeshOptions FaceAlignedQueueFixture(HalfEdgeMesh source) => new(0.08)
    {
        Iterations = 40,
        FeatureAngleDegrees = 0,
        Scheduling = RemeshScheduling.Queue,
        Projection = RemeshProjection.FaceAligned,
        ProjectionTarget = new MeshProjectionTarget(source),
    };

    /// <summary>
    /// The restriction's correctness bar is not "it visits fewer faces" but "the accumulator
    /// state comes out the same for the vertices it actually moves" — so this asserts
    /// <b>bit-for-bit</b> equal positions against the unrestricted walk, on a run whose active
    /// set is a proper subset of the mesh. If they differ, the restriction is wrong, not
    /// merely faster.
    /// <para>
    /// Two things could break it and neither is visible in a tolerance comparison: dropping a
    /// face that does contribute to a moved vertex, and visiting the contributing faces in a
    /// different order (floating-point addition is not associative, which is why the
    /// accumulation keeps its ascending face scan rather than gathering a list). Both were
    /// checked by deliberately introducing them; the order break shows up in the last bits
    /// only, as <c>…4258002</c> against <c>…42581595</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void FaceAlignedProjection_RestrictedAccumulationIsBitIdenticalUnderQueueScheduling()
    {
        var sphere = FaceAlignedQueueSphere();
        var options = FaceAlignedQueueFixture(sphere);

        var restricted = Remesher.Remesh(sphere, options);
        var whole = Remesher.Remesh(sphere, options with { AccumulateOverEveryFace = true });

        // FIRST: prove the restriction actually fires, or everything below is vacuous. A
        // bit-identity assertion between two paths that turn out to be the same walk agrees
        // with a broken restriction as happily as with a correct one — measured, it did:
        // truncating the membership test to one of the triangle's three vertices (which drops
        // faces that genuinely contribute) passed on the first fixture tried, because that
        // fixture never let a single face be skipped. See FaceAlignedQueueSphere.
        Assert.True(restricted.FacesAccumulated < whole.FacesAccumulated,
            $"the restriction never skipped a face ({restricted.FacesAccumulated} of " +
            $"{whole.FacesAccumulated}) — this fixture cannot tell a correct restriction from a broken one");

        // THEN: the answer is bit-for-bit the whole-mesh answer. Not "close": dropping a face
        // that does contribute, or visiting the contributors in a different order (float
        // addition is not associative), both show up here and in no other assertion.
        Assert.Equal(whole.Splits, restricted.Splits);
        Assert.Equal(whole.Collapses, restricted.Collapses);
        Assert.Equal(whole.Flips, restricted.Flips);
        Assert.Equal(whole.Mesh.VertexCount, restricted.Mesh.VertexCount);
        Assert.Equal(whole.Mesh.FaceCount, restricted.Mesh.FaceCount);
        for (int v = 0; v < whole.Mesh.VertexCount; v++)
        {
            var a = restricted.Mesh.GetPosition(v);
            var b = whole.Mesh.GetPosition(v);
            Assert.True(BitConverter.DoubleToInt64Bits(a.X) == BitConverter.DoubleToInt64Bits(b.X) &&
                        BitConverter.DoubleToInt64Bits(a.Y) == BitConverter.DoubleToInt64Bits(b.Y) &&
                        BitConverter.DoubleToInt64Bits(a.Z) == BitConverter.DoubleToInt64Bits(b.Z),
                $"vertex {v}: restricted {a} != whole-mesh {b}");
        }
    }

    /// <summary>
    /// Sweep scheduling keeps the whole-mesh walk deliberately — and the reason is a
    /// measurement, not "every vertex is active under sweep, so there is nothing to skip".
    /// <para>
    /// There IS something to skip. Every vertex is a <i>candidate</i> under sweep, but a
    /// PINNED candidate is never written (the accumulation's read loop skips it), so a face
    /// with three pinned corners contributes to nothing and dropping it is bit-identical —
    /// built and measured, 0 differing vertices on eight fixtures. It does not pay, for a
    /// structural reason: a pinned set is a <b>one-dimensional</b> subcomplex of the surface
    /// (boundary loops and crease chains) and a face needs ALL THREE corners inside it, so
    /// the skippable share is a vanishing fraction of a two-dimensional mesh. Measured over
    /// fixtures chosen to maximise pinning it ran <b>0.23% to 9.17%</b> — the ceiling being
    /// this cylinder, whose n-gon caps have a fully pinned rim around a one-triangle-deep
    /// fan — against the cost of a test on every face, which no timing could separate from
    /// noise (a control arm skipping 0.00% of faces measured 0.795x, i.e. the harness's own
    /// band is wider than the whole effect).
    /// </para>
    /// <para>
    /// The backlog entry that filed this had the scoping backwards, which is the part worth
    /// keeping: it proposed the saving for "a small <c>FixedVertices</c> set", and a small
    /// pinned set is precisely the case that skips nothing. The saving needs a LARGE one, and
    /// even a maximal one reaches single-digit percent.
    /// </para>
    /// </summary>
    [Fact]
    public void FaceAlignedProjection_SweepSchedulingStillAccumulatesOverEveryFace()
    {
        // A CYLINDER at the default feature angle, not the queue fixture's featureless sphere:
        // that one pins nothing at all (closed, FeatureAngleDegrees = 0), so "sweep skips no
        // face" would hold there however the accumulation were written. The claim is only
        // testable on a mesh with pinned vertices to skip.
        var source = MeshPrimitives.Cylinder(1.0, 3.0, 32);
        var options = new RemeshOptions(0.25)
        {
            Iterations = 12,
            Scheduling = RemeshScheduling.Sweep,
            Projection = RemeshProjection.FaceAligned,
            ProjectionTarget = new MeshProjectionTarget(source),
        };

        var swept = Remesher.Remesh(source, options);
        var forced = Remesher.Remesh(source, options with { AccumulateOverEveryFace = true });

        // FIRST: the fixture must genuinely carry pinned triangles, or the assertion below is
        // vacuous in exactly the way the featureless sphere made it.
        Assert.True(swept.Quality.ConstrainedCount > 0,
            "this fixture pins nothing, so it cannot tell 'sweep walks every face' from " +
            "'sweep skips the faces it need not write'");

        Assert.Equal(forced.FacesAccumulated, swept.FacesAccumulated);
    }

    // ---------------------------------------------------------------- bounded maximum

    /// <summary>Smallest angle of any triangle, in degrees — the shape-quality counterweight
    /// to refusing valence flips (an edge-length band says nothing about slivers).</summary>
    private static double WorstAngleDegrees(HalfEdgeMesh mesh)
    {
        double worst = 180;
        foreach (var face in mesh.Faces)
        {
            var p = face.Vertices().Select(v => v.Position).ToArray();
            if (p.Length != 3)
                continue;
            for (int i = 0; i < 3; i++)
            {
                var u = p[(i + 1) % 3] - p[i];
                var w = p[(i + 2) % 3] - p[i];
                if (u.Length == 0 || w.Length == 0)
                    continue;
                double cos = Math.Clamp(u.Dot(w) / (u.Length * w.Length), -1, 1);
                worst = Math.Min(worst, Math.Acos(cos) * 180 / Math.PI);
            }
        }
        return worst;
    }

    /// <summary>
    /// The shared bounded-maximum fixture, with <see cref="RemeshOptions.PreventLongEdgeFlips"/>
    /// switched OFF: it is the default now, so a "baseline" has to say so explicitly.
    /// </summary>
    private static RemeshOptions BoundedFixture(HalfEdgeMesh source, int iterations = 14) =>
        new(2.0)
        {
            Iterations = iterations,
            PreventLongEdgeFlips = false,
            ProjectionTarget = new MeshProjectionTarget(source),
        };

    /// <summary>
    /// Pins the DIAGNOSIS, so it cannot rot: the edges a converged remesh leaves above the
    /// split threshold are put there by the FLIP stage. Switching flips off — and nothing
    /// else — takes the maximum to the threshold, while switching off the smoothing and
    /// projection stages, the other plausible suspects, leaves it exactly as bad.
    /// <para>
    /// A collapse can stretch a neighbouring edge too, but only by half the length of the
    /// edge it collapses, which is under <c>0.33 L</c> by the collapse threshold — bounded,
    /// and an order below what a flip does. (It cannot be isolated the same way either:
    /// disabling collapses stops the mesh coarsening at all, which is a different regime
    /// rather than the same run without one mechanism.)
    /// </para>
    /// </summary>
    [Fact]
    public void TheLongEdgesAConvergedRemeshLeavesComeFromFlips()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (20, 20, 20))).Triangulated();
        var options = BoundedFixture(box);

        double baseline = EdgeLengths(Remesher.Remesh(box, options).Mesh).Max;
        double withoutFlips = EdgeLengths(Remesher.Remesh(box, options with { EnableFlips = false }).Mesh).Max;
        double withoutGeometryStages = EdgeLengths(Remesher.Remesh(box, options with
        {
            Smoothing = RemeshSmoothing.None,
            ProjectionTarget = null,
        }).Mesh).Max;

        // Measured 2.65 L baseline, 1.36 L with no flips. The split stage already removes
        // everything above 1.33 L, so whatever is left over is what a flip put there.
        Assert.True(baseline > 2.0 * options.TargetEdgeLength,
            $"the baseline should churn well past the threshold, measured {baseline / options.TargetEdgeLength:F2} L");
        Assert.True(withoutFlips < 1.05 * options.MaxEdgeLength,
            $"with no flips the sweep should reach the threshold, measured {withoutFlips / options.TargetEdgeLength:F2} L");
        // The geometry stages are innocent: without them the maximum is no better.
        Assert.True(withoutGeometryStages > 2.0 * options.TargetEdgeLength,
            $"smoothing/projection are not the mechanism, measured {withoutGeometryStages / options.TargetEdgeLength:F2} L");
    }

    [Theory]
    [InlineData(0)] // MeshPrimitives.Box
    [InlineData(1)] // MeshPrimitives.Cylinder
    [InlineData(2)] // MeshPrimitives.UvSphere
    public void PreventLongEdgeFlips_BringsTheMaximumDownToTheThreshold(int fixtureId)
    {
        var source = fixtureId switch
        {
            0 => MeshPrimitives.Box(new Aabb((0, 0, 0), (20, 20, 20))).Triangulated(),
            1 => MeshPrimitives.Cylinder(10, 20, 32).Triangulated(),
            _ => MeshPrimitives.UvSphere(10, 24, 16).Triangulated(),
        };
        var options = BoundedFixture(source);

        var baseline = Remesher.Remesh(source, options);
        var bounded = Remesher.Remesh(source, options with { PreventLongEdgeFlips = true });

        double baselineMax = EdgeLengths(baseline.Mesh).Max;
        double boundedMax = EdgeLengths(bounded.Mesh).Max;

        // The claim is a bound approached under the sweep, not a hard cap at every pass: the
        // smoothing and projection stages run AFTER the sweep and move vertices by their own
        // damped step, so the end-of-pass maximum sits a little over the threshold. Measured
        // 1.31-1.34 L against a baseline of 1.83-2.65 L.
        Assert.True(boundedMax <= 1.05 * options.MaxEdgeLength,
            $"bounded maximum {boundedMax / options.TargetEdgeLength:F2} L should reach the threshold");
        Assert.True(boundedMax < baselineMax,
            $"bounded {boundedMax:F3} should beat the baseline {baselineMax:F3}");

        // And it is not bought from the distribution — the in-band share improves too.
        Assert.True(FractionOutsideBand(bounded.Mesh, options) <=
                    FractionOutsideBand(baseline.Mesh, options));
        // Nor from SHAPE, which is what settled making it the default. The SLIVER COUNT is the
        // general claim and the one asserted as such; the worst single angle also improves on
        // all three of these fixtures (0.14 -> 31.69 on the box, 0.20 -> 29.19 on the cylinder,
        // 5.21 -> 30.93 on the sphere) but is an EXTREMUM and does not generalize — on a
        // drilled plate, whose bore rims are pinned chains finer than the target and so cannot
        // be coarsened, the guard keeps a 0.01 degree triangle where the baseline reads 1.40
        // while cutting slivers 419 -> 157. Judge by the share, not the extremes; see
        // RemeshOptions.PreventLongEdgeFlips.
        Assert.True(bounded.Quality.SliverCount < baseline.Quality.SliverCount,
            $"slivers {bounded.Quality.SliverCount} should beat {baseline.Quality.SliverCount}");
        Assert.True(bounded.Quality.MinAngleDegrees > baseline.Quality.MinAngleDegrees,
            $"on these three fixtures the extremum improves too: {bounded.Quality.MinAngleDegrees:F2} " +
            $"against {baseline.Quality.MinAngleDegrees:F2} degrees");
        bounded.Mesh.Validate();
        AssertAllTriangles(bounded.Mesh);
    }

    /// <summary>
    /// The monotone clause earns its keep here. Refusing EVERY flip that would leave an
    /// out-of-band edge — the obvious form of the guard — strands the sliver whose only
    /// remedy was a flip from (say) 2.5 L to 1.5 L; measured on a box, that form reaches
    /// 99.7% of edges in band with a worst triangle angle of <b>0.02°</b>, which is what a
    /// length-only measure cannot see. Permitting a flip that strictly shortens the edge it
    /// replaces gives <b>31.7°</b> on this fixture, against the unguarded baseline's 0.14°.
    /// </summary>
    [Fact]
    public void PreventLongEdgeFlips_DoesNotStrandSlivers()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (20, 20, 20))).Triangulated();
        var options = BoundedFixture(box);

        var bounded = Remesher.Remesh(box, options with { PreventLongEdgeFlips = true });

        double worst = WorstAngleDegrees(bounded.Mesh);
        Assert.True(worst > 10, $"worst triangle angle {worst:F2} degrees — the guard stranded slivers");
        // It still flips: a guard that refused everything would trivially pass the line above.
        Assert.True(bounded.Flips > 0);
    }

    [Fact]
    public void PreventLongEdgeFlips_IsDeterministic()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 14, 9).Triangulated();
        var options = new RemeshOptions(0.3)
        {
            Iterations = 6,
            PreventLongEdgeFlips = true,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };

        var first = Remesher.Remesh(sphere, options);
        var second = Remesher.Remesh(sphere, options);

        Assert.Equal(first.Flips, second.Flips);
        Assert.Equal(first.Mesh.VertexCount, second.Mesh.VertexCount);
        for (int v = 0; v < first.Mesh.VertexCount; v++)
        {
            var a = first.Mesh.GetPosition(v);
            var b = second.Mesh.GetPosition(v);
            Assert.True(BitConverter.DoubleToInt64Bits(a.X) == BitConverter.DoubleToInt64Bits(b.X) &&
                        BitConverter.DoubleToInt64Bits(a.Y) == BitConverter.DoubleToInt64Bits(b.Y) &&
                        BitConverter.DoubleToInt64Bits(a.Z) == BitConverter.DoubleToInt64Bits(b.Z),
                $"vertex {v}: {a} != {b}");
        }
    }

    /// <summary>
    /// <b>On by default</b>, decided on measurement after shipping opt-in. Over a box, a
    /// cylinder, two UV spheres and an open height-field grid at 10 / 14 / 40 passes every
    /// measure improves — maximum, shortest edge, in-band share, worst free angle, worst
    /// radius ratio, sliver count and run time — and the filed counter-example (a cylinder's
    /// worst angle 0.89° → 0.58°) does not reproduce at 32, 48 or 64 segments at any of nine
    /// pass counts. On a heavily pinned model the worst single ANGLE can go the other way
    /// while every population figure improves (a drilled plate: 1.40° → 0.01° against slivers
    /// 419 → 157), which is the extremes-versus-share lesson, not a trade in the mesh's
    /// favour. The deciding argument is downstream: <c>TetMesher</c>'s
    /// boundary recovery needs a Delaunay-clean surface and a valence flip can replace a
    /// Delaunay diagonal with a longer one, so the old default produced surfaces the
    /// project's own tet mesher refuses.
    /// </summary>
    [Fact]
    public void PreventLongEdgeFlips_IsOnByDefault() =>
        Assert.True(new RemeshOptions(1.0).PreventLongEdgeFlips);

    // ---------------------------------------------------------------- face-aligned projection

    /// <summary>A target that deliberately does NOT override the oriented overload, so it
    /// answers through the interface's default and reports no normal.</summary>
    private sealed class UnorientedTarget(IProjectionTarget inner) : IProjectionTarget
    {
        public Vector3d Project(in Vector3d point) => inner.Project(point);
    }

    [Fact]
    public void MeshProjectionTarget_ReportsTheWinningTriangleNormal()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2))).Triangulated();
        var target = new MeshProjectionTarget(box);

        target.Project(new Vector3d(1, 1, 5), out var top);
        Assert.Equal(new Vector3d(0, 0, 1), top);
        target.Project(new Vector3d(1, -3, 1), out var front);
        Assert.Equal(new Vector3d(0, -1, 0), front);
    }

    [Fact]
    public void FaceAlignedProjection_KeepsSharpEdgesThatVertexProjectionRoundsOff()
    {
        // The whole point of the RZN flow. Feature pinning is switched OFF so the creases
        // have nothing protecting them but the projection itself: closest-point projection
        // pulls a vertex near an edge onto whichever face happens to be nearer and the box
        // erodes, while the face-aligned weighting lets the two flat groups either side pull
        // it back onto their intersection.
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2))).Triangulated();
        var options = new RemeshOptions(0.35)
        {
            Iterations = 12,
            FeatureAngleDegrees = 0,
            SmoothSpeed = 0.3,
            ProjectionTarget = new MeshProjectionTarget(box),
        };

        var rounded = Remesher.Remesh(box, options).Mesh;
        var sharp = Remesher.Remesh(box, options with { Projection = RemeshProjection.FaceAligned }).Mesh;

        sharp.Validate();
        Assert.True(sharp.IsClosed);
        Assert.Equal(2, sharp.EulerCharacteristic);

        double roundedLoss = 8 - rounded.Volume();
        double sharpLoss = 8 - sharp.Volume();
        Assert.True(roundedLoss > 0, "closest-point projection is expected to erode the corners");
        Assert.True(sharpLoss < roundedLoss / 2,
            $"face-aligned should lose far less volume: {sharpLoss} against {roundedLoss}");
    }

    [Fact]
    public void FaceAlignedProjection_DegradesToVertexProjectionOnAnUnorientedTarget()
    {
        // A target that answers only the unoriented overload reports a zero normal through the
        // interface's default, and every triangle then falls back to closest-point placement.
        // The mode must therefore still produce a sound mesh rather than refusing or drifting.
        var sphere = MeshPrimitives.UvSphere(1.0, 20, 14).Triangulated();
        var options = new RemeshOptions(0.2)
        {
            Iterations = 8,
            FeatureAngleDegrees = 0,
            Projection = RemeshProjection.FaceAligned,
            ProjectionTarget = new UnorientedTarget(new MeshProjectionTarget(sphere)),
        };

        var result = Remesher.Remesh(sphere, options);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        AssertAllTriangles(result.Mesh);
        Assert.True(Math.Abs(result.Mesh.Volume() - sphere.Volume()) / sphere.Volume() < 0.05);
    }

    [Fact]
    public void FaceAlignedProjection_KeepsASmoothSurfaceWhereItIs()
    {
        // It must not be a feature-only tool: on a curved surface with no creases at all, the
        // rigid per-triangle motion has to reproduce ordinary projection's job of holding the
        // shape against smoothing's shrinkage.
        var sphere = MeshPrimitives.UvSphere(1.0, 24, 16).Triangulated();
        var options = new RemeshOptions(0.18)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0,
            Projection = RemeshProjection.FaceAligned,
            ProjectionTarget = new MeshProjectionTarget(sphere),
        };

        var result = Remesher.Remesh(sphere, options);

        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        double worst = 0;
        foreach (var vertex in result.Mesh.Vertices)
            worst = Math.Max(worst, Math.Abs(vertex.Position.Length - 1));
        // Within the source tessellation's own sagitta of the unit sphere.
        Assert.True(worst < 0.03, $"worst radial deviation {worst}");
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
