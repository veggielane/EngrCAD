using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Shape healing on deliberately broken topology. The canonical defect is the one foreign
/// STEP produces: a face soup whose shared edges arrive duplicated and whose vertices
/// merely coincide — <see cref="Explode"/> manufactures exactly that from a good solid.
/// </summary>
public class ShapeHealingTests
{
    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 5)));

    /// <summary>
    /// Rebuilds a solid so every face owns private copies of its vertices and edges — the
    /// state a STEP writer that emits each wire separately leaves behind. Vertex positions
    /// are jittered by up to <paramref name="jitter"/> (curves are shared verbatim, so the
    /// jitter tests the vertex-merge tolerance rather than the sewing test).
    /// </summary>
    private static BrepSolid Explode(BrepSolid solid, double jitter = 0, int seed = 1, int shuffleFace = -1)
    {
        var random = new Random(seed);
        Vector3d Jitter(in Vector3d p) => jitter <= 0
            ? p
            : p + new Vector3d(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5)
                    .Normalized() * (jitter * random.NextDouble());

        int faceCounter = 0;
        var shells = new List<BrepShell>();
        foreach (var shell in solid.Shells)
        {
            var faces = new List<BrepFace>();
            foreach (var face in shell.Faces)
            {
                bool shuffle = faceCounter++ == shuffleFace;
                // Vertices are shared WITHIN a face and duplicated between faces: that is
                // what a per-wire STEP writer produces, and it means each wire is still
                // internally chained even though nothing is shared across the solid.
                var faceVertices = new Dictionary<BrepVertex, BrepVertex>();
                BrepVertex Copy(BrepVertex v)
                {
                    if (!faceVertices.TryGetValue(v, out var copy))
                        faceVertices[v] = copy = new BrepVertex(Jitter(v.Position));
                    return copy;
                }

                var loops = new List<BrepLoop>();
                foreach (var loop in face.Loops)
                {
                    var coedges = new List<BrepCoedge>();
                    foreach (var coedge in loop.Coedges)
                    {
                        var edge = coedge.Edge;
                        var start = Copy(edge.StartVertex);
                        var end = edge.IsClosedEdge ? start : Copy(edge.EndVertex);
                        // Straight edges get a genuinely new line through the jittered ends,
                        // so the two copies of a shared edge really are different geometry
                        // rather than the same object sampled twice.
                        bool straight = edge.Curve is Line3d;
                        var curve = straight ? new Line3d(start.Position, end.Position) : edge.Curve;
                        var domain = straight ? Interval.Unit : edge.Domain;
                        coedges.Add(new BrepCoedge(new BrepEdge(curve, domain, start, end), coedge.SameSense));
                    }
                    if (shuffle && coedges.Count >= 4)
                    {
                        // Swap two coedges and flip a third's sense: the loop no longer
                        // chains end-to-start, in both of the ways a wire can be wrong.
                        (coedges[1], coedges[2]) = (coedges[2], coedges[1]);
                        coedges[3] = new BrepCoedge(coedges[3].Edge, !coedges[3].SameSense);
                    }
                    loops.Add(new BrepLoop(coedges));
                }
                faces.Add(new BrepFace(face.Surface, loops, face.IsReversed));
            }
            shells.Add(new BrepShell(faces));
        }
        return new BrepSolid(shells);
    }

    /// <summary>A standalone planar triangular face. The plane normal is passed in rather
    /// than derived: these triangles are deliberately degenerate, and a cross product of
    /// their edges is exactly the zero-ish quantity the healing code refuses to threshold.</summary>
    private static BrepFace TriangleFace(Vector3d a, Vector3d b, Vector3d c, Vector3d? planeNormal = null)
    {
        var va = new BrepVertex(a);
        var vb = new BrepVertex(b);
        var vc = new BrepVertex(c);
        var normal = planeNormal ?? Vector3d.UnitZ;
        // Divide rather than Normalized(): these edges are shorter than the kernel's weld
        // tolerance on purpose, which is the whole point of the fixture.
        var x = (b - a) * (1 / (b - a).Length);
        var surface = new PlaneSurface(a, x, normal.Cross(x));
        var coedges = new List<BrepCoedge>
        {
            new(new BrepEdge(new Line3d(a, b), Interval.Unit, va, vb), true),
            new(new BrepEdge(new Line3d(b, c), Interval.Unit, vb, vc), true),
            new(new BrepEdge(new Line3d(c, a), Interval.Unit, vc, va), true),
        };
        return new BrepFace(surface, [new BrepLoop(coedges)]);
    }

    // ---- healthy input ----

    [Fact]
    public void HealthySolid_IsLeftAlone()
    {
        var box = Box();
        var result = ShapeHealing.Heal(box);

        Assert.False(result.Report.ChangedAnything);
        Assert.True(result.Report.IsManifold);
        Assert.Equal(0, result.Report.NonManifoldEdgesBefore);
        Assert.Equal(0, result.Report.OpenLoopsBefore);

        result.Solid.Validate();
        Assert.True(result.Solid.SatisfiesEulerFormula());
        Assert.Equal(6, result.Solid.Faces.Count());
        Assert.Equal(12, result.Solid.Edges.Count());
        Assert.Equal(8, result.Solid.Vertices.Count());
    }

    [Fact]
    public void Healing_NeverModifiesTheInput()
    {
        var box = Box();
        var exploded = Explode(box, jitter: 3e-8);
        int edgesBefore = exploded.Edges.Count();
        int vertexUsesBefore = exploded.Edges.Sum(e => e.Uses.Count);

        ShapeHealing.Heal(exploded);

        Assert.Equal(edgesBefore, exploded.Edges.Count());
        Assert.Equal(vertexUsesBefore, exploded.Edges.Sum(e => e.Uses.Count));
        Assert.Equal(24, exploded.Vertices.Count());   // 6 faces x 4 private corners
    }

    // ---- the face-soup case ----

    [Fact]
    public void ExplodedBox_IsSewnBackIntoAManifoldSolid()
    {
        var exploded = Explode(Box());
        Assert.Equal(24, exploded.Edges.Count());
        Assert.Equal(24, exploded.Vertices.Count());

        var result = ShapeHealing.Heal(exploded);
        var report = result.Report;

        Assert.Equal(24, report.NonManifoldEdgesBefore);   // every edge used exactly once
        Assert.Equal(0, report.OpenLoopsBefore);           // each wire is internally consistent
        Assert.Equal(16, report.VerticesMerged);           // 24 copies of 8 corners
        Assert.Equal(12, report.EdgesSewn);                // 24 copies of 12 edges
        Assert.True(report.IsManifold, report.ToString());

        result.Solid.Validate();
        Assert.True(result.Solid.SatisfiesEulerFormula());
        Assert.Equal(6, result.Solid.Faces.Count());
        Assert.Equal(12, result.Solid.Edges.Count());
        Assert.Equal(8, result.Solid.Vertices.Count());
    }

    [Fact]
    public void ExplodedBox_WithSubToleranceJitter_StillSews()
    {
        // Up to 4e-8 each way, so two copies of a corner are at most 8e-8 apart — inside the
        // 1e-7 tolerance pairwise, which is what the greedy clustering needs.
        var result = ShapeHealing.Heal(Explode(Box(), jitter: 4e-8));

        Assert.Equal(16, result.Report.VerticesMerged);
        Assert.Equal(12, result.Report.EdgesSewn);
        Assert.True(result.Report.IsManifold, result.Report.ToString());
        result.Solid.Validate();
    }

    [Fact]
    public void GapsWiderThanTheTolerance_AreReportedNotInvented()
    {
        // Jitter an order of magnitude past the tolerance: healing must refuse and say so
        // rather than silently dragging geometry together.
        var result = ShapeHealing.Heal(Explode(Box(), jitter: 1e-5), new ShapeHealingOptions { GapTolerance = 1e-7 });

        Assert.False(result.Report.IsManifold);
        Assert.True(result.Report.NonManifoldEdgesAfter > 0);
        Assert.Contains(result.Report.Notes, n => n.Contains("GapTolerance"));

        // Loosening the tolerance past the gap fixes it — the caller's decision, explicitly.
        var loose = ShapeHealing.Heal(Explode(Box(), jitter: 1e-5), new ShapeHealingOptions { GapTolerance = 1e-4 });
        Assert.True(loose.Report.IsManifold, loose.Report.ToString());
        loose.Solid.Validate();
    }

    [Fact]
    public void Healing_IsIdempotent()
    {
        var once = ShapeHealing.Heal(Explode(Box(), jitter: 3e-8));
        var twice = ShapeHealing.Heal(once.Solid);

        Assert.False(twice.Report.ChangedAnything);
        Assert.True(twice.Report.IsManifold);
        twice.Solid.Validate();
    }

    // ---- wire ordering ----

    [Fact]
    public void ScrambledWire_IsReorderedAndReSensed()
    {
        var exploded = Explode(Box(), shuffleFace: 2);
        Assert.Equal(1, ShapeHealing.Analyze(exploded, new ShapeHealingOptions { RepairLoops = false }).OpenLoopsAfter);

        var result = ShapeHealing.Heal(exploded);
        Assert.Equal(1, result.Report.OpenLoopsBefore);
        Assert.Equal(1, result.Report.LoopsReordered);
        Assert.True(result.Report.CoedgesReversed >= 1);
        Assert.Equal(0, result.Report.OpenLoopsAfter);
        result.Solid.Validate();
        Assert.True(result.Solid.SatisfiesEulerFormula());
    }

    // ---- small edges ----

    [Fact]
    public void SubToleranceEdge_IsCollapsed()
    {
        var box = Box();
        // Split one edge a hair from its start: the first fragment is 1e-9 long, which is
        // an edge no manufacturing process and no downstream algorithm wants to see.
        var victim = box.Edges.First();
        TopologyEditor.SplitEdge(victim, victim.Domain.Start + 1e-9);
        Assert.Equal(13, box.Edges.Count());
        Assert.Equal(9, box.Vertices.Count());

        var result = ShapeHealing.Heal(box, new ShapeHealingOptions
        {
            GapTolerance = 1e-7,
            SmallEdgeTolerance = 1e-8,
        });

        Assert.Equal(1, result.Report.SmallEdgesRemoved);
        Assert.True(result.Report.IsManifold, result.Report.ToString());
        Assert.Equal(12, result.Solid.Edges.Count());
        Assert.Equal(8, result.Solid.Vertices.Count());
        result.Solid.Validate();
        Assert.True(result.Solid.SatisfiesEulerFormula());
    }

    [Fact]
    public void SmallEdgeRemoval_CanBeTurnedOff()
    {
        var box = Box();
        var victim = box.Edges.First();
        TopologyEditor.SplitEdge(victim, victim.Domain.Start + 1e-9);

        var result = ShapeHealing.Heal(box, new ShapeHealingOptions
        {
            SmallEdgeTolerance = 1e-8,
            RemoveSmallEdges = false,
            // The split vertex sits 1e-9 from the corner, so vertex merging alone would
            // fuse them and leave a self-loop edge; keep it off to isolate the switch.
            MergeVertices = false,
        });

        Assert.Equal(0, result.Report.SmallEdgesRemoved);
        Assert.Equal(13, result.Solid.Edges.Count());
    }

    // ---- degenerate faces, and the scale-free sliver rule ----

    [Fact]
    public void CollapsedFace_IsRemovedAlongWithItsNowEmptyShell()
    {
        var box = Box();
        // A face whose whole outline fits inside the gap tolerance: it is a point.
        var speck = TriangleFace((0, 0, 0), (1e-12, 0, 0), (0, 1e-12, 0));
        var solid = new BrepSolid([box.Shells[0], new BrepShell([speck])]);

        var result = ShapeHealing.Heal(solid);

        Assert.Equal(1, result.Report.DegenerateFacesRemoved);
        Assert.Equal(1, result.Report.EmptyShellsRemoved);
        Assert.True(result.Report.IsManifold, result.Report.ToString());
        Assert.Equal(6, result.Solid.Faces.Count());
        result.Solid.Validate();
    }

    /// <summary>
    /// The lesson this project has learned five times: a degeneracy test on an AREA must be
    /// relative. A dimensionless area/perimeter² ratio removes a metre-long zero-width
    /// sliver and keeps a legitimate micron-scale face; any absolute area threshold gets
    /// both backwards, because the sliver's area is eight orders of magnitude LARGER.
    /// </summary>
    [Fact]
    public void SliverRemoval_IsScaleFree_NotAnAbsoluteAreaThreshold()
    {
        var sliver = TriangleFace((0, 0, 0), (1000, 0, 0), (500, 1e-7, 0));
        var tinyButReal = TriangleFace((0, 0, 0), (1e-6, 0, 0), (0, 5e-7, 0));

        double sliverArea = 0.5 * 1000 * 1e-7;         // 5e-5
        double tinyArea = 0.5 * 1e-6 * 5e-7;           // 2.5e-13
        Assert.True(sliverArea > tinyArea * 1e8,
            "The premise of the test: the degenerate face has the far larger absolute area.");

        var withSliver = ShapeHealing.Heal(new BrepSolid([Box().Shells[0], new BrepShell([sliver])]));
        Assert.Equal(1, withSliver.Report.DegenerateFacesRemoved);
        Assert.Equal(6, withSliver.Solid.Faces.Count());

        var withTiny = ShapeHealing.Heal(new BrepSolid([Box().Shells[0], new BrepShell([tinyButReal])]));
        Assert.Equal(0, withTiny.Report.DegenerateFacesRemoved);
        Assert.Equal(7, withTiny.Solid.Faces.Count());
        // It survives as an unsewn sheet, and healing says so rather than pretending.
        Assert.False(withTiny.Report.IsManifold);
        Assert.Equal(3, withTiny.Report.NonManifoldEdgesAfter);
    }

    [Fact]
    public void DegenerateFaceRemoval_CanBeTurnedOff()
    {
        var speck = TriangleFace((0, 0, 0), (1e-12, 0, 0), (0, 1e-12, 0));
        var solid = new BrepSolid([Box().Shells[0], new BrepShell([speck])]);

        // Small-edge collapse would eat the speck's edges anyway; switch it off too so the
        // assertion is about the degenerate-face switch alone.
        var result = ShapeHealing.Heal(solid, new ShapeHealingOptions
        {
            RemoveDegenerateFaces = false,
            RemoveSmallEdges = false,
            MergeVertices = false,
        });

        Assert.Equal(0, result.Report.DegenerateFacesRemoved);
        Assert.Equal(7, result.Solid.Faces.Count());
    }

    // ---- straight-edge refitting ----

    [Fact]
    public void RefitStraightEdges_IsOffByDefault_AndANoOpOnExactGeometry()
    {
        Assert.False(new ShapeHealingOptions().RefitStraightEdges);

        // A kernel-built box already has each line running exactly between its vertices, so
        // the pass must find nothing to do — refitting is repair, not re-derivation.
        var result = ShapeHealing.Heal(Box(), new ShapeHealingOptions { RefitStraightEdges = true });
        Assert.Equal(0, result.Report.EdgesRefit);
        Assert.False(result.Report.ChangedAnything);
    }

    [Fact]
    public void RefitStraightEdges_ClosesWiresThatSewingOnlyMadeTopologicallyShared()
    {
        var soup = Explode(Box(), jitter: 3e-8, seed: 5);

        var sewnOnly = ShapeHealing.Heal(soup);
        Assert.True(sewnOnly.Report.IsManifold);
        // Validate() compares vertex REFERENCES, so it passes even though consecutive edges
        // of a wire still miss each other geometrically. That is the trap.
        sewnOnly.Solid.Validate();
        Assert.True(WorstWireGap(sewnOnly.Solid) > 1e-9,
            "The fixture is supposed to leave a sub-tolerance wire gap behind.");

        var refit = ShapeHealing.Heal(Explode(Box(), jitter: 3e-8, seed: 5),
            new ShapeHealingOptions { RefitStraightEdges = true });
        Assert.True(refit.Report.EdgesRefit > 0);
        refit.Solid.Validate();
        // Round-off, not zero: Line3d.PointAt(1) evaluates start + (end - start), which is
        // the end point to within an ulp rather than bit-for-bit. Eleven orders of magnitude
        // inside the 1e-9 weld tier, which is all the tessellator asks.
        Assert.True(WorstWireGap(refit.Solid) < 1e-12,
            $"Refit wires still gap by {WorstWireGap(refit.Solid):G3}.");
    }

    /// <summary>Largest distance between where one coedge's curve ends and where the next
    /// one's begins — the geometric discontinuity <see cref="BrepSolid.Validate"/> cannot
    /// see.</summary>
    private static double WorstWireGap(BrepSolid solid)
    {
        static Vector3d Start(BrepCoedge c) =>
            c.Edge.Curve.PointAt(c.SameSense ? c.Edge.Domain.Start : c.Edge.Domain.End);
        static Vector3d End(BrepCoedge c) =>
            c.Edge.Curve.PointAt(c.SameSense ? c.Edge.Domain.End : c.Edge.Domain.Start);

        double worst = 0;
        foreach (var loop in solid.Loops)
        {
            var coedges = loop.Coedges;
            for (int i = 0; i < coedges.Count; i++)
                worst = Math.Max(worst, End(coedges[i]).DistanceTo(Start(coedges[(i + 1) % coedges.Count])));
        }
        return worst;
    }

    // ---- reporting ----

    [Fact]
    public void Analyze_ReportsWithoutTakingTheResult()
    {
        var exploded = Explode(Box(), jitter: 3e-8);
        var report = ShapeHealing.Analyze(exploded);

        Assert.Equal(24, report.NonManifoldEdgesBefore);
        Assert.Equal(12, report.EdgesSewn);
        Assert.True(report.IsManifold);
        // Untouched: the caller can still decide not to heal.
        Assert.Equal(24, exploded.Edges.Count());
    }

    [Fact]
    public void Report_ReadsAsASentence()
    {
        string text = ShapeHealing.Analyze(Explode(Box(), jitter: 3e-8)).ToString();
        Assert.Contains("16 vertices merged", text);
        Assert.Contains("12 edges sewn", text);
        Assert.Contains("closed manifold", text);

        Assert.Contains("nothing to repair", ShapeHealing.Analyze(Box()).ToString());
    }

    [Fact]
    public void EverythingDegenerate_FailsLoudly()
    {
        var speck = TriangleFace((0, 0, 0), (1e-12, 0, 0), (0, 1e-12, 0));
        var solid = new BrepSolid([new BrepShell([speck])]);
        var ex = Assert.Throws<InvalidOperationException>(() => ShapeHealing.Heal(solid));
        Assert.Contains("every face", ex.Message);
    }

    [Fact]
    public void NonPositiveTolerance_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ShapeHealing.Heal(Box(), new ShapeHealingOptions { GapTolerance = 0 }));
    }

    // ---- curved-edge re-trimming ----

    /// <summary>A quarter-pie face whose arc edge's domain starts a hair PAST the vertex —
    /// the state vertex merging leaves behind when the surviving vertex belonged to the
    /// neighbouring wire: the gap is tangential (along the curve), which is exactly what a
    /// re-trim can remove.</summary>
    private static BrepSolid PieWithShiftedArcTrim(double parameterShift)
    {
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1);
        var a = new BrepVertex((1, 0, 0));       // the arc's TRUE start (phase 0)
        var b = new BrepVertex((0, 1, 0));       // phase pi/2
        var center = new BrepVertex(Vector3d.Zero);
        var arc = new BrepEdge(circle, new Interval(parameterShift, Math.PI / 2), a, b);
        var inbound = new BrepEdge(new Line3d(b.Position, center.Position), Interval.Unit, b, center);
        var outbound = new BrepEdge(new Line3d(center.Position, a.Position), Interval.Unit, center, a);
        var loop = new BrepLoop([new(arc, true), new(inbound, true), new(outbound, true)]);
        return new BrepSolid([new BrepShell([
            new BrepFace(new PlaneSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), [loop])])]);
    }

    [Fact]
    public void RetrimCurvedEdges_ClosesTangentialArcGaps()
    {
        Assert.False(new ShapeHealingOptions().RetrimCurvedEdges); // opt-in, like refitting

        var withoutRetrim = ShapeHealing.Heal(PieWithShiftedArcTrim(5e-8));
        Assert.Equal(0, withoutRetrim.Report.CurvedEdgesRetrimmed);
        Assert.True(WorstWireGap(withoutRetrim.Solid) > 1e-9,
            "the fixture is supposed to leave a tangential wire gap");

        var retrimmed = ShapeHealing.Heal(PieWithShiftedArcTrim(5e-8),
            new ShapeHealingOptions { RetrimCurvedEdges = true });
        Assert.Equal(1, retrimmed.Report.CurvedEdgesRetrimmed);
        Assert.True(WorstWireGap(retrimmed.Solid) < 1e-12,
            $"re-trimmed wire still gaps by {WorstWireGap(retrimmed.Solid):G3}");

        // Idempotent: a second heal finds the trim already at the vertex.
        var again = ShapeHealing.Heal(retrimmed.Solid, new ShapeHealingOptions { RetrimCurvedEdges = true });
        Assert.Equal(0, again.Report.CurvedEdgesRetrimmed);
    }

    [Fact]
    public void RetrimCurvedEdges_ReportsAPerpendicularResidualInsteadOfInventingGeometry()
    {
        // Shift the whole circle off the vertices radially: no parameter ends nearer than
        // the perpendicular distance, so the re-trim must refuse and say so.
        var circle = new Circle3d((0, 0, 1e-4), Vector3d.UnitX, Vector3d.UnitY, 1);
        var a = new BrepVertex((1, 0, 0));
        var b = new BrepVertex((0, 1, 0));
        var center = new BrepVertex(Vector3d.Zero);
        var arc = new BrepEdge(circle, new Interval(0, Math.PI / 2), a, b);
        var inbound = new BrepEdge(new Line3d(b.Position, center.Position), Interval.Unit, b, center);
        var outbound = new BrepEdge(new Line3d(center.Position, a.Position), Interval.Unit, center, a);
        var loop = new BrepLoop([new(arc, true), new(inbound, true), new(outbound, true)]);
        var solid = new BrepSolid([new BrepShell([
            new BrepFace(new PlaneSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), [loop])])]);

        var result = ShapeHealing.Heal(solid, new ShapeHealingOptions { RetrimCurvedEdges = true });
        Assert.Equal(0, result.Report.CurvedEdgesRetrimmed);
        Assert.Contains(result.Report.Notes, n => n.Contains("curve re-fitting"));
    }

    // ---- shell repair: orientation flood, outward vote, repartition ----

    /// <summary>Rebuilds a solid with the faces in <paramref name="flipWhich"/> re-wound
    /// (loop order and senses reversed, IsReversed toggled) — a foreign writer's
    /// inconsistently oriented face, or with all faces flipped an inside-out component.</summary>
    private static BrepSolid WithFlippedFaces(BrepSolid solid, Func<int, bool> flipWhich)
    {
        var vertices = new Dictionary<BrepVertex, BrepVertex>();
        var edges = new Dictionary<BrepEdge, BrepEdge>();
        BrepVertex V(BrepVertex v) => vertices.TryGetValue(v, out var c) ? c : vertices[v] = new BrepVertex(v.Position);
        BrepEdge E(BrepEdge e) => edges.TryGetValue(e, out var c)
            ? c
            : edges[e] = new BrepEdge(e.Curve, e.Domain, V(e.StartVertex), V(e.EndVertex));

        int index = 0;
        var faces = new List<BrepFace>();
        foreach (var face in solid.Faces)
        {
            bool flip = flipWhich(index++);
            var loops = new List<BrepLoop>();
            foreach (var loop in face.Loops)
            {
                var coedges = loop.Coedges.Select(c => (Edge: E(c.Edge), c.SameSense)).ToList();
                if (flip)
                {
                    coedges.Reverse();
                    coedges = coedges.Select(c => (c.Edge, !c.SameSense)).ToList();
                }
                loops.Add(new BrepLoop(coedges.Select(c => new BrepCoedge(c.Edge, c.SameSense)).ToList()));
            }
            faces.Add(new BrepFace(face.Surface, loops, flip ? !face.IsReversed : face.IsReversed));
        }
        return new BrepSolid([new BrepShell(faces)]);
    }

    [Fact]
    public void RepairShells_FlipsOneInconsistentlyOrientedFace()
    {
        var broken = WithFlippedFaces(Box(), i => i == 3);
        // The defect: the flipped face's edges are used twice with EQUAL sense.
        Assert.Throws<InvalidOperationException>(broken.Validate);

        var result = ShapeHealing.Heal(broken);
        Assert.Equal(1, result.Report.FacesFlipped);
        result.Solid.Validate();
        Assert.True(result.Solid.SatisfiesEulerFormula());
        Assert.All(result.Solid.Faces, f => Assert.False(f.IsReversed));
    }

    [Fact]
    public void RepairShells_FlipsAnInsideOutComponentByTheOutwardVote()
    {
        // Every face flipped: pairwise CONSISTENT (both uses of every edge negated), so
        // only the global outward vote can catch it.
        var insideOut = WithFlippedFaces(Box(), _ => true);
        insideOut.Validate(); // structurally fine — that is what makes it dangerous

        var result = ShapeHealing.Heal(insideOut);
        Assert.Equal(6, result.Report.FacesFlipped);
        result.Solid.Validate();
        Assert.All(result.Solid.Faces, f => Assert.False(f.IsReversed));
    }

    [Fact]
    public void RepairShells_SplitsDisconnectedFacesIntoTheirOwnShells()
    {
        // Two disjoint boxes' faces dumped into ONE shell — a foreign writer's flat list.
        var a = Box();
        var b = SolidFactory.MakeBox(new Aabb((10, 0, 0), (12, 1, 1)));
        var mixed = new BrepSolid([new BrepShell([.. a.Faces, .. b.Faces])]);

        var result = ShapeHealing.Heal(mixed);
        Assert.Equal(1, result.Report.ShellsSplit);
        Assert.Equal(2, result.Solid.Shells.Count);
        result.Solid.Validate();
        Assert.True(result.Solid.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void RepairShells_LeavesVoidShellsPointingIntoTheirCavity()
    {
        // A sealed hollow box: the cavity shell's material-outward normals point INTO the
        // void, so its enclosed volume is negative BY DESIGN — the containment-parity
        // vote must not "fix" it.
        var hollow = Shelling.Shell(SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 8, 6))), 1);
        Assert.Equal(2, hollow.Shells.Count);

        var result = ShapeHealing.Heal(hollow);
        Assert.False(result.Report.ChangedAnything, result.Report.ToString());
        Assert.Equal(2, result.Solid.Shells.Count);
        result.Solid.Validate();
    }

    [Fact]
    public void RepairShells_LeavesAmbiguousVotesAlone()
    {
        // A sphere's two pole-bounded faces have ONE boundary loop each (the equator),
        // which encloses no volume — the vote is honestly ambiguous and the component
        // keeps its authored side rather than flipping on noise.
        var result = ShapeHealing.Heal(SolidFactory.MakeSphere(2));
        Assert.False(result.Report.ChangedAnything, result.Report.ToString());
        result.Solid.Validate();

        var torus = ShapeHealing.Heal(SolidFactory.MakeTorus(10, 3));
        Assert.False(torus.Report.ChangedAnything, torus.Report.ToString());
    }
}
