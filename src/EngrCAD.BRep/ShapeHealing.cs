using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>What <see cref="ShapeHealing.Heal"/> is allowed to do, and how close counts as
/// coincident.</summary>
public sealed record ShapeHealingOptions
{
    /// <summary>
    /// How far apart two vertices — or two copies of one edge — may be and still be treated
    /// as the same thing. Defaults to the <b>1e-7 seam tier</b> of the epsilon ladder,
    /// which is exactly the case being repaired: geometry authored independently on the two
    /// sides of one boundary (a foreign STEP file writes each face's wire separately, so a
    /// shared edge arrives twice with the writer's own rounding between the copies). The
    /// 1e-9 weld tier is for geometry constructed to be identical, which imported data is
    /// not.
    /// <para>Note this is an absolute length in model units, so a file authored in metres
    /// wants a proportionally tighter value and one in microns a looser one; healing does
    /// not guess the model's scale.</para>
    /// </summary>
    public double GapTolerance { get; init; } = 1e-7;

    /// <summary>Edges shorter than this are collapsed; defaults to
    /// <see cref="GapTolerance"/> (an edge shorter than the distance at which two vertices
    /// are the same vertex has, by that same standard, no length).</summary>
    public double? SmallEdgeTolerance { get; init; }

    /// <summary>Unify vertices that coincide within <see cref="GapTolerance"/>.</summary>
    public bool MergeVertices { get; init; } = true;

    /// <summary>Collapse edges shorter than <see cref="SmallEdgeTolerance"/>.</summary>
    public bool RemoveSmallEdges { get; init; } = true;

    /// <summary>Drop faces that enclose nothing (see <see cref="SliverAreaRatio"/>).</summary>
    public bool RemoveDegenerateFaces { get; init; } = true;

    /// <summary>
    /// Merge singly-used edges that duplicate one another — the sewing step that turns a
    /// face soup into a solid.
    /// </summary>
    public bool SewEdges { get; init; } = true;

    /// <summary>Re-order and re-sense loop coedges so each ends where the next begins.</summary>
    public bool RepairLoops { get; init; } = true;

    /// <summary>
    /// Rebuild <b>straight</b> edges through their unified endpoints — the geometric half of
    /// gap closing, and the only part of it that needs no curve fitting, since a line is
    /// determined by its two endpoints and nothing else.
    /// <para>Why it exists: sewing makes two faces share one edge <i>object</i>, but the
    /// surviving curve still ends where its own copy ended, so consecutive edges of a wire
    /// can miss each other by up to <see cref="GapTolerance"/>. That is invisible to
    /// <see cref="BrepSolid.Validate"/> (which compares vertex references) and fatal to the
    /// tessellator (which welds at the 1e-9 tier and sees a crack). Refitting closes the
    /// wire exactly.</para>
    /// <para><b>Off by default</b>, because it is the one pass that moves geometry: an edge
    /// shifts by up to <see cref="GapTolerance"/>. That is right for imported soup and wrong
    /// for kernel output, whose vertices were already unified at the 1e-7 seam tier by
    /// boolean seam sealing — refitting there would drag exactly-constructed lines off their
    /// surfaces. Curved edges are never touched and are reported instead.</para>
    /// </summary>
    public bool RefitStraightEdges { get; init; }

    /// <summary>
    /// Re-trim <b>curved</b> edges so each ends at the parameter nearest its (unified)
    /// vertex — the curved half of geometric gap closing (OCCT
    /// <c>ShapeFix_Wire::FixGaps</c>'s parametric mode). A merged vertex can sit up to
    /// <see cref="GapTolerance"/> from where the curve's stored domain ends, mostly ALONG
    /// the curve; solving the foot-of-perpendicular parameter (local Gauss–Newton from the
    /// current end — the gap is sub-tolerance, so the solve is local by construction)
    /// removes the tangential part of the gap. What remains is the vertex's PERPENDICULAR
    /// distance to the curve, which no re-trim can remove: closing that would mean
    /// re-fitting or deforming the curve — a modelling operation, deliberately not
    /// attempted (OCCT's geometric mode inserts filler segments instead; we report).
    /// <para><b>Off by default</b> for the same reason as
    /// <see cref="RefitStraightEdges"/>: it changes edge domains, and kernel output's
    /// trims are exact. A residual above <see cref="GapTolerance"/> refuses the re-trim
    /// and is reported — the honest per-edge tolerance story, since this topology carries
    /// no per-entity tolerances to widen (and deliberately so: a per-edge tolerance is a
    /// license every downstream consumer must then honour).</para>
    /// </summary>
    public bool RetrimCurvedEdges { get; init; }

    /// <summary>
    /// Repair shell structure (OCCT <c>ShapeFix_Shell</c>): make face orientations
    /// consistent per connected component (a flood over shared-edge senses — the manifold
    /// invariant is two uses with OPPOSITE sense), vote each closed component's global
    /// side from the fan volume of its boundary loops (containment parity expects voids
    /// to point INTO the cavity), and re-partition shells so each connected component is
    /// one shell. On a well-formed solid all three find nothing to do.
    /// </summary>
    public bool RepairShells { get; init; } = true;

    /// <summary>
    /// Sliver threshold as area / perimeter² — <b>dimensionless on purpose</b>. An absolute
    /// area threshold is the scale-quadratic mistake this codebase has made repeatedly: it
    /// rejects a legitimate micron-scale face and accepts a metre-long zero-width sliver.
    /// A shape ratio does neither. The default 1e-10 corresponds to a rectangle of aspect
    /// ratio ~4e-10, i.e. a face with no interior at any scale.
    /// </summary>
    public double SliverAreaRatio { get; init; } = 1e-10;

    public static readonly ShapeHealingOptions Default = new();

    internal double ResolvedSmallEdgeTolerance => SmallEdgeTolerance ?? GapTolerance;
}

/// <summary>
/// Everything <see cref="ShapeHealing.Heal"/> changed, and everything it could not — a
/// <i>result</i>, in this codebase's convention, not a log line (compare
/// <c>MeshRepairReport</c> and <c>StepReadResult.Diagnostics</c>).
/// </summary>
public sealed record ShapeHealingReport
{
    /// <summary>Vertices that disappeared into a coincident neighbour.</summary>
    public int VerticesMerged { get; init; }

    /// <summary>Edges collapsed for being shorter than the small-edge tolerance.</summary>
    public int SmallEdgesRemoved { get; init; }

    /// <summary>Duplicate edge copies merged into their twin (the sewing step).</summary>
    public int EdgesSewn { get; init; }

    /// <summary>Straight edges rebuilt through their unified endpoints
    /// (<see cref="ShapeHealingOptions.RefitStraightEdges"/>).</summary>
    public int EdgesRefit { get; init; }

    /// <summary>Curved edges whose domain was re-trimmed to end at the parameters nearest
    /// their unified vertices (<see cref="ShapeHealingOptions.RetrimCurvedEdges"/>).</summary>
    public int CurvedEdgesRetrimmed { get; init; }

    /// <summary>Faces flipped for orientation consistency or an inside-out component
    /// (<see cref="ShapeHealingOptions.RepairShells"/>).</summary>
    public int FacesFlipped { get; init; }

    /// <summary>Additional shells created by splitting a shell whose faces form several
    /// connected components (<see cref="ShapeHealingOptions.RepairShells"/>).</summary>
    public int ShellsSplit { get; init; }

    /// <summary>Loops whose coedge order changed so that they chain end-to-start.</summary>
    public int LoopsReordered { get; init; }

    /// <summary>Coedges whose sense was flipped while chaining a loop.</summary>
    public int CoedgesReversed { get; init; }

    /// <summary>Faces dropped for enclosing nothing (collapsed or sliver).</summary>
    public int DegenerateFacesRemoved { get; init; }

    /// <summary>Hole loops dropped from surviving faces for the same reason.</summary>
    public int DegenerateLoopsRemoved { get; init; }

    /// <summary>Shells left with no faces at all.</summary>
    public int EmptyShellsRemoved { get; init; }

    /// <summary>Edges not used by exactly two coedges, before healing. Non-zero means the
    /// input was not a closed manifold solid.</summary>
    public int NonManifoldEdgesBefore { get; init; }

    /// <summary>The same count after healing; zero is the goal.</summary>
    public int NonManifoldEdgesAfter { get; init; }

    /// <summary>Loops that did not chain end-to-start, before healing.</summary>
    public int OpenLoopsBefore { get; init; }

    /// <summary>The same count after healing.</summary>
    public int OpenLoopsAfter { get; init; }

    /// <summary>True when the result passes the structural checks
    /// <see cref="BrepSolid.Validate"/> makes (two uses per edge, every loop chained).</summary>
    public bool IsManifold => NonManifoldEdgesAfter == 0 && OpenLoopsAfter == 0;

    /// <summary>Anything healing had to leave alone, named.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when healing changed the topology in any way.</summary>
    public bool ChangedAnything =>
        VerticesMerged + SmallEdgesRemoved + EdgesSewn + EdgesRefit + CurvedEdgesRetrimmed +
        FacesFlipped + ShellsSplit + LoopsReordered +
        DegenerateFacesRemoved + DegenerateLoopsRemoved + EmptyShellsRemoved > 0;

    public override string ToString()
    {
        var parts = new List<string>();
        if (VerticesMerged > 0) parts.Add($"{VerticesMerged} vertices merged");
        if (SmallEdgesRemoved > 0) parts.Add($"{SmallEdgesRemoved} small edges removed");
        if (EdgesSewn > 0) parts.Add($"{EdgesSewn} edges sewn");
        if (EdgesRefit > 0) parts.Add($"{EdgesRefit} straight edges refit");
        if (CurvedEdgesRetrimmed > 0) parts.Add($"{CurvedEdgesRetrimmed} curved edges re-trimmed");
        if (FacesFlipped > 0) parts.Add($"{FacesFlipped} faces flipped");
        if (ShellsSplit > 0) parts.Add($"{ShellsSplit} shells split off");
        if (LoopsReordered > 0) parts.Add($"{LoopsReordered} loops reordered ({CoedgesReversed} coedges reversed)");
        if (DegenerateFacesRemoved > 0) parts.Add($"{DegenerateFacesRemoved} degenerate faces removed");
        if (DegenerateLoopsRemoved > 0) parts.Add($"{DegenerateLoopsRemoved} degenerate loops removed");
        if (EmptyShellsRemoved > 0) parts.Add($"{EmptyShellsRemoved} empty shells removed");
        if (parts.Count == 0) parts.Add("nothing to repair");
        string summary = string.Join(", ", parts);
        string state = IsManifold
            ? "result is a closed manifold"
            : $"result still has {NonManifoldEdgesAfter} non-manifold edges and {OpenLoopsAfter} open loops";
        return Notes.Count == 0 ? $"{summary}; {state}." : $"{summary}; {state}. " + string.Join(" ", Notes);
    }
}

/// <summary>The healed solid and the account of what changed.</summary>
public sealed record ShapeHealingResult(BrepSolid Solid, ShapeHealingReport Report);

/// <summary>
/// Repairs the topology of a <see cref="BrepSolid"/> — OCCT's <c>ShapeFix</c>. The case it
/// exists for is third-party STEP: <see cref="StepReader"/> shares topology by entity
/// identity, which is right for files this kernel wrote, but foreign writers emit each
/// face's wire separately, so a solid arrives as a face soup whose shared edges are
/// duplicated and whose vertices merely coincide.
/// </summary>
/// <remarks>
/// <para>The passes, in the order they run (each individually switchable):</para>
/// <list type="number">
/// <item><description><b>Vertex merging</b> — coincident vertices become one.</description></item>
/// <item><description><b>Small-edge removal</b> — edges shorter than the tolerance collapse,
/// unifying their endpoints and vanishing from every loop that used them.</description></item>
/// <item><description><b>Degenerate-face removal</b> — faces that enclose nothing: a loop
/// collapsed to a point, or a sliver whose area/perimeter² falls below a
/// <i>dimensionless</i> threshold.</description></item>
/// <item><description><b>Edge sewing</b> — singly-used edges that duplicate one another
/// (same endpoints, coincident midpoints) merge, so the two faces share one edge and the
/// solid becomes manifold.</description></item>
/// <item><description><b>Straight-edge refitting</b> (opt-in) — a line is rebuilt through
/// its unified endpoints, closing the wire geometrically as well as topologically. See
/// <see cref="ShapeHealingOptions.RefitStraightEdges"/> for why this is the one pass that
/// is off by default.</description></item>
/// <item><description><b>Wire repair</b> — loop coedges are re-ordered and re-sensed until
/// each ends where the next begins.</description></item>
/// </list>
/// <para><b>By default healing never moves a coordinate.</b> Merging keeps one of the
/// coincident vertices verbatim and sewing keeps one of the duplicate curves verbatim,
/// exactly as <c>MeshRepair.MergeCoincidentEdges</c> does on the mesh side — so the residual
/// geometric discrepancy is bounded by <see cref="ShapeHealingOptions.GapTolerance"/> and
/// nothing is invented. Repairing a gap <i>larger</i> than the tolerance would mean
/// extending or re-fitting curves, which is a modelling operation and is deliberately not
/// attempted; such a gap is reported instead.</para>
/// <para><b>The lesson that cost the most here: a sewn solid is not automatically a
/// tessellatable one.</b> After sewing, two faces share one edge object and
/// <see cref="BrepSolid.Validate"/> is satisfied — it compares vertex <i>references</i> —
/// yet consecutive edges of a wire still end up to the gap tolerance apart, which the
/// tessellator (welding at the 1e-9 tier) sees as a crack and turns into a bow-tie vertex.
/// Topological repair and geometric repair are different jobs;
/// <see cref="ShapeHealingOptions.RefitStraightEdges"/> does the part of the second one
/// that needs no curve fitting, and imported polyhedral soup wants it on.</para>
/// <para>The input is never modified: healing builds a completely new topology graph over
/// the same (immutable) curves and surfaces.</para>
/// </remarks>
public static class ShapeHealing
{
    /// <summary>Heals <paramref name="solid"/> and reports every change. The input is left
    /// untouched.</summary>
    public static ShapeHealingResult Heal(BrepSolid solid, ShapeHealingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(solid);
        var o = options ?? ShapeHealingOptions.Default;
        if (o.GapTolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "GapTolerance must be positive.");

        var work = Work.Load(solid);
        var notes = new List<string>();
        int nonManifoldBefore = work.CountNonManifoldEdges();
        int openLoopsBefore = work.CountOpenLoops();

        int verticesMerged = o.MergeVertices ? work.MergeVertices(o.GapTolerance) : 0;
        int smallEdges = o.RemoveSmallEdges ? work.RemoveSmallEdges(o.ResolvedSmallEdgeTolerance, notes) : 0;
        var (facesRemoved, loopsRemoved) = o.RemoveDegenerateFaces
            ? work.RemoveDegenerateFaces(o.GapTolerance, o.SliverAreaRatio, notes)
            : (0, 0);
        int sewn = o.SewEdges ? work.SewDuplicateEdges(o.GapTolerance) : 0;
        int retrimmed = o.RetrimCurvedEdges ? work.RetrimCurvedEdges(o.GapTolerance, notes) : 0;
        int refit = o.RefitStraightEdges
            ? work.RefitStraightEdges(notes, curvedNotedElsewhere: o.RetrimCurvedEdges)
            : 0;
        var (loopsReordered, coedgesReversed) = o.RepairLoops ? work.RepairLoops(notes) : (0, 0);
        var (facesFlipped, shellsSplit) = o.RepairShells ? work.RepairShells(notes) : (0, 0);

        var (healed, emptyShells) = work.Build();
        int nonManifoldAfter = CountNonManifoldEdges(healed);
        int openLoopsAfter = CountOpenLoops(healed);
        if (nonManifoldAfter > 0)
            notes.Add($"{nonManifoldAfter} edges are still not shared by exactly two faces; " +
                      "the remaining gaps exceed GapTolerance or the shape genuinely is not closed.");

        var report = new ShapeHealingReport
        {
            VerticesMerged = verticesMerged,
            SmallEdgesRemoved = smallEdges,
            EdgesSewn = sewn,
            EdgesRefit = refit,
            CurvedEdgesRetrimmed = retrimmed,
            FacesFlipped = facesFlipped,
            ShellsSplit = shellsSplit,
            LoopsReordered = loopsReordered,
            CoedgesReversed = coedgesReversed,
            DegenerateFacesRemoved = facesRemoved,
            DegenerateLoopsRemoved = loopsRemoved,
            EmptyShellsRemoved = emptyShells,
            NonManifoldEdgesBefore = nonManifoldBefore,
            NonManifoldEdgesAfter = nonManifoldAfter,
            OpenLoopsBefore = openLoopsBefore,
            OpenLoopsAfter = openLoopsAfter,
            Notes = notes,
        };
        return new ShapeHealingResult(healed, report);
    }

    /// <summary>
    /// What healing <i>would</i> do, without taking the result: a dry run whose report names
    /// every defect found and every repair available. Implemented as a full heal whose solid
    /// is discarded — healing does not modify its input, so there is nothing to undo and no
    /// second detection path to drift out of step with the first.
    /// </summary>
    public static ShapeHealingReport Analyze(BrepSolid solid, ShapeHealingOptions? options = null) =>
        Heal(solid, options).Report;

    private static int CountNonManifoldEdges(BrepSolid solid) =>
        solid.Edges.Count(e => e.Uses.Count != 2);

    private static int CountOpenLoops(BrepSolid solid)
    {
        int open = 0;
        foreach (var loop in solid.Loops)
        {
            var coedges = loop.Coedges;
            for (int i = 0; i < coedges.Count; i++)
            {
                if (!ReferenceEquals(coedges[i].EndVertex, coedges[(i + 1) % coedges.Count].StartVertex))
                {
                    open++;
                    break;
                }
            }
        }
        return open;
    }

    // ---- the mutable working copy ----

    private sealed class WorkEdge(Curve3d curve, Interval domain, int start, int end)
    {
        public Curve3d Curve { get; set; } = curve;
        public Interval Domain { get; set; } = domain;
        public int Start { get; set; } = start;
        public int End { get; set; } = end;
        public bool Alive { get; set; } = true;
        public BrepEdge? Built { get; set; }
    }

    private sealed class WorkLoop
    {
        public List<(int Edge, bool SameSense)> Coedges { get; } = [];
        public bool Alive { get; set; } = true;
    }

    private sealed class WorkFace(Surface surface, bool isReversed, int shell)
    {
        public Surface Surface { get; } = surface;
        public bool IsReversed { get; set; } = isReversed;
        public int Shell { get; set; } = shell;
        public List<WorkLoop> Loops { get; } = [];
        public bool Alive { get; set; } = true;
    }

    private sealed class Work
    {
        private readonly List<Vector3d> _positions = [];
        private readonly List<int> _parent = [];
        private readonly List<WorkEdge> _edges = [];
        private readonly List<WorkFace> _faces = [];
        private int _shellCount;

        /// <summary>Number of samples used to measure a curve's length or a loop's shape.
        /// Not a tolerance — a sampling count, chosen so an arc is measured to better than
        /// a percent, which is far finer than any degeneracy test needs.</summary>
        private const int CurveSamples = 9;

        public static Work Load(BrepSolid solid)
        {
            var work = new Work();
            // Reference identity: BrepVertex/BrepEdge do not override Equals, so the default
            // comparer already keys on object identity — which is exactly the input's own
            // notion of "the same vertex", the one healing is about to widen.
            var vertexIndex = new Dictionary<BrepVertex, int>();
            var edgeIndex = new Dictionary<BrepEdge, int>();

            int Vertex(BrepVertex v)
            {
                if (vertexIndex.TryGetValue(v, out int existing))
                    return existing;
                int index = work._positions.Count;
                work._positions.Add(v.Position);
                work._parent.Add(index);
                vertexIndex[v] = index;
                return index;
            }

            int Edge(BrepEdge e)
            {
                if (edgeIndex.TryGetValue(e, out int existing))
                    return existing;
                int index = work._edges.Count;
                work._edges.Add(new WorkEdge(e.Curve, e.Domain, Vertex(e.StartVertex), Vertex(e.EndVertex)));
                edgeIndex[e] = index;
                return index;
            }

            work._shellCount = solid.Shells.Count;
            for (int s = 0; s < solid.Shells.Count; s++)
            {
                foreach (var face in solid.Shells[s].Faces)
                {
                    var wf = new WorkFace(face.Surface, face.IsReversed, s);
                    foreach (var loop in face.Loops)
                    {
                        var wl = new WorkLoop();
                        foreach (var coedge in loop.Coedges)
                            wl.Coedges.Add((Edge(coedge.Edge), coedge.SameSense));
                        wf.Loops.Add(wl);
                    }
                    work._faces.Add(wf);
                }
            }
            return work;
        }

        // ---- union-find over vertices ----

        private int Find(int v)
        {
            int root = v;
            while (_parent[root] != root)
                root = _parent[root];
            while (_parent[v] != root)
                (_parent[v], v) = (root, _parent[v]);
            return root;
        }

        /// <summary>Unions two vertex classes, keeping the LOWER index as representative so
        /// the surviving position is one the model already contained (healing never invents
        /// a coordinate).</summary>
        private bool Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb)
                return false;
            if (rb < ra)
                (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            return true;
        }

        private IEnumerable<(int Edge, bool SameSense)> AliveCoedges() =>
            _faces.Where(f => f.Alive).SelectMany(f => f.Loops).Where(l => l.Alive).SelectMany(l => l.Coedges);

        public int CountNonManifoldEdges()
        {
            var uses = new int[_edges.Count];
            foreach (var (edge, _) in AliveCoedges())
                uses[edge]++;
            int count = 0;
            for (int e = 0; e < _edges.Count; e++)
            {
                if (_edges[e].Alive && uses[e] != 2)
                    count++;
            }
            return count;
        }

        public int CountOpenLoops()
        {
            int open = 0;
            foreach (var face in _faces.Where(f => f.Alive))
            {
                foreach (var loop in face.Loops.Where(l => l.Alive))
                {
                    var c = loop.Coedges;
                    for (int i = 0; i < c.Count; i++)
                    {
                        var next = c[(i + 1) % c.Count];
                        if (EndOf(c[i]) != StartOf(next))
                        {
                            open++;
                            break;
                        }
                    }
                }
            }
            return open;
        }

        private int StartOf((int Edge, bool SameSense) c) =>
            Find(c.SameSense ? _edges[c.Edge].Start : _edges[c.Edge].End);

        private int EndOf((int Edge, bool SameSense) c) =>
            Find(c.SameSense ? _edges[c.Edge].End : _edges[c.Edge].Start);

        // ---- pass 1: vertex merging ----

        public int MergeVertices(double tolerance)
        {
            // Spatial hash at exactly the tolerance, so two points within it always share a
            // cell or are in one of the 26 neighbours.
            var buckets = new Dictionary<(long, long, long), List<int>>();
            double toleranceSquared = tolerance * tolerance;
            int merged = 0;

            (long, long, long) Cell(in Vector3d p) => (
                (long)Math.Floor(p.X / tolerance),
                (long)Math.Floor(p.Y / tolerance),
                (long)Math.Floor(p.Z / tolerance));

            for (int v = 0; v < _positions.Count; v++)
            {
                var p = _positions[v];
                var (cx, cy, cz) = Cell(p);
                int match = -1;
                for (long dx = -1; dx <= 1 && match < 0; dx++)
                {
                    for (long dy = -1; dy <= 1 && match < 0; dy++)
                    {
                        for (long dz = -1; dz <= 1 && match < 0; dz++)
                        {
                            if (!buckets.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                                continue;
                            foreach (int candidate in bucket)
                            {
                                // Squared distance against a squared tolerance: both sides
                                // scale the same way, so this stays a length comparison.
                                if (_positions[candidate].DistanceSquaredTo(p) <= toleranceSquared)
                                {
                                    match = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (match >= 0)
                {
                    if (Union(match, v))
                        merged++;
                }
                else
                {
                    if (!buckets.TryGetValue((cx, cy, cz), out var bucket))
                        buckets[(cx, cy, cz)] = bucket = [];
                    bucket.Add(v);
                }
            }
            return merged;
        }

        // ---- pass 2: small edges ----

        private double EdgeLength(WorkEdge edge)
        {
            double length = 0;
            var previous = edge.Curve.PointAt(edge.Domain.Start);
            for (int i = 1; i < CurveSamples; i++)
            {
                var point = edge.Curve.PointAt(edge.Domain.ParameterAt(i / (double)(CurveSamples - 1)));
                length += previous.DistanceTo(point);
                previous = point;
            }
            return length;
        }

        public int RemoveSmallEdges(double tolerance, List<string> notes)
        {
            int removed = 0, refused = 0;
            for (int e = 0; e < _edges.Count; e++)
            {
                var edge = _edges[e];
                if (!edge.Alive || EdgeLength(edge) > tolerance)
                    continue;

                // A loop that would be left with nothing is a loop the collapse destroys;
                // refuse rather than produce an empty loop, and say so. (The degenerate-face
                // pass removes such faces on its own evidence, not as a side effect here.)
                bool wouldEmptyALoop = false;
                foreach (var face in _faces.Where(f => f.Alive))
                {
                    foreach (var loop in face.Loops.Where(l => l.Alive))
                    {
                        if (loop.Coedges.Count(c => c.Edge == e) == loop.Coedges.Count)
                            wouldEmptyALoop = true;
                    }
                }
                if (wouldEmptyALoop)
                {
                    refused++;
                    continue;
                }

                Union(edge.Start, edge.End);
                edge.Alive = false;
                foreach (var face in _faces)
                {
                    foreach (var loop in face.Loops)
                        loop.Coedges.RemoveAll(c => c.Edge == e);
                }
                removed++;
            }
            if (refused > 0)
                notes.Add($"{refused} sub-tolerance edges were kept because collapsing them would have emptied a loop.");
            return removed;
        }

        // ---- pass 3: degenerate faces ----

        /// <summary>Samples a loop into a 3D polyline (curved coedges included, so a face
        /// bounded by a single circle is measured as a polygon rather than as one point).</summary>
        private List<Vector3d> SampleLoop(WorkLoop loop)
        {
            var points = new List<Vector3d>();
            foreach (var (e, sameSense) in loop.Coedges)
            {
                var edge = _edges[e];
                for (int i = 0; i < CurveSamples - 1; i++)
                {
                    double t = i / (double)(CurveSamples - 1);
                    points.Add(edge.Curve.PointAt(edge.Domain.ParameterAt(sameSense ? t : 1 - t)));
                }
            }
            return points;
        }

        private static (double Area, double Perimeter, double Extent) LoopMetrics(List<Vector3d> points)
        {
            if (points.Count < 3)
                return (0, 0, 0);
            var box = Aabb.Empty;
            double nx = 0, ny = 0, nz = 0, perimeter = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var q = points[(i + 1) % points.Count];
                box = box.Union(p);
                perimeter += p.DistanceTo(q);
                nx += (p.Y - q.Y) * (p.Z + q.Z);
                ny += (p.Z - q.Z) * (p.X + q.X);
                nz += (p.X - q.X) * (p.Y + q.Y);
            }
            return (new Vector3d(nx, ny, nz).Length * 0.5, perimeter, box.Size.Length);
        }

        public (int Faces, int Loops) RemoveDegenerateFaces(double tolerance, double sliverRatio, List<string> notes)
        {
            int faces = 0, loops = 0, freedEdges = 0;
            foreach (var face in _faces.Where(f => f.Alive))
            {
                for (int l = 0; l < face.Loops.Count; l++)
                {
                    var loop = face.Loops[l];
                    if (!loop.Alive)
                        continue;
                    bool degenerate;
                    if (loop.Coedges.Count == 0)
                    {
                        degenerate = true;
                    }
                    else
                    {
                        var (area, perimeter, extent) = LoopMetrics(SampleLoop(loop));
                        // Two independent tests, both scale-honest: a length against a
                        // length tolerance, and a DIMENSIONLESS shape ratio. Never an
                        // absolute area threshold.
                        degenerate = extent <= tolerance ||
                                     (perimeter > 0 && area <= sliverRatio * perimeter * perimeter);
                    }
                    if (!degenerate)
                        continue;

                    if (l == 0)
                    {
                        // The outer loop encloses nothing, so neither does the face.
                        face.Alive = false;
                        foreach (var dead in face.Loops)
                        {
                            dead.Alive = false;
                            freedEdges += dead.Coedges.Count;
                        }
                        faces++;
                        break;
                    }
                    loop.Alive = false;
                    freedEdges += loop.Coedges.Count;
                    loops++;
                }
            }
            if (faces + loops > 0)
                notes.Add($"Removing {faces} degenerate faces and {loops} degenerate hole loops released " +
                          $"{freedEdges} edge uses; edges they were the second user of are now free.");
            return (faces, loops);
        }

        // ---- pass 4: sewing ----

        public int SewDuplicateEdges(double tolerance)
        {
            var uses = new int[_edges.Count];
            foreach (var (edge, _) in AliveCoedges())
                uses[edge]++;

            // Group the singly-used edges by their (unified) endpoint pair.
            var groups = new Dictionary<(int, int), List<int>>();
            for (int e = 0; e < _edges.Count; e++)
            {
                if (!_edges[e].Alive || uses[e] != 1)
                    continue;
                int a = Find(_edges[e].Start), b = Find(_edges[e].End);
                var key = a <= b ? (a, b) : (b, a);
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = [];
                list.Add(e);
            }

            var toleranceStruct = new Tolerance(tolerance, tolerance);
            int sewn = 0;
            foreach (var group in groups.Values)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    int keep = group[i];
                    if (!_edges[keep].Alive)
                        continue;
                    for (int j = i + 1; j < group.Count; j++)
                    {
                        int duplicate = group[j];
                        if (!_edges[duplicate].Alive || !SameCurveGeometry(keep, duplicate, toleranceStruct))
                            continue;

                        // Redirect the duplicate's single use onto the kept edge, choosing
                        // the sense by where the quarter point along the USE lands.
                        RedirectUse(duplicate, keep);
                        _edges[duplicate].Alive = false;
                        sewn++;
                        break;
                    }
                }
            }
            return sewn;
        }

        private Vector3d PointAlong(int edge, double t)
        {
            var e = _edges[edge];
            return e.Curve.PointAt(e.Domain.ParameterAt(t));
        }

        private bool SameCurveGeometry(int a, int b, in Tolerance tolerance)
        {
            // Endpoints already match (the caller grouped by them). Compare interior points
            // so that two different arcs between the same pair of vertices — the two halves
            // of a circle, a chord versus its arc — are never confused for one another.
            // The midpoint is direction-independent, so it is the one comparison that does
            // not need the reversal case; the quarter point then needs both.
            if (!PointAlong(a, 0.5).AreEqual(PointAlong(b, 0.5), tolerance))
                return false;
            var quarterA = PointAlong(a, 0.25);
            return quarterA.AreEqual(PointAlong(b, 0.25), tolerance) ||
                   quarterA.AreEqual(PointAlong(b, 0.75), tolerance);
        }

        private void RedirectUse(int from, int to)
        {
            foreach (var face in _faces.Where(f => f.Alive))
            {
                foreach (var loop in face.Loops.Where(l => l.Alive))
                {
                    for (int i = 0; i < loop.Coedges.Count; i++)
                    {
                        if (loop.Coedges[i].Edge != from)
                            continue;
                        bool sameSense = loop.Coedges[i].SameSense;
                        var quarterAlongUse = PointAlong(from, sameSense ? 0.25 : 0.75);
                        bool forward =
                            quarterAlongUse.DistanceSquaredTo(PointAlong(to, 0.25)) <=
                            quarterAlongUse.DistanceSquaredTo(PointAlong(to, 0.75));
                        loop.Coedges[i] = (to, forward);
                    }
                }
            }
        }

        // ---- pass 5a: curved-edge re-trimming (the parametric half of gap closing) ----

        public int RetrimCurvedEdges(double tolerance, List<string> notes)
        {
            int retrimmed = 0, residualLeft = 0;
            for (int e = 0; e < _edges.Count; e++)
            {
                var edge = _edges[e];
                if (!edge.Alive || edge.Curve.Underlying is Line3d)
                    continue; // straight edges belong to the refit pass (a line has no trim to adjust)

                var start = _positions[Find(edge.Start)];
                var end = _positions[Find(edge.End)];
                var atStart = edge.Curve.PointAt(edge.Domain.Start);
                var atEnd = edge.Curve.PointAt(edge.Domain.End);
                // Exact-equality: the question is "does the trim already end at the
                // vertex", not "is it close" — see RefitStraightEdges.
                if (atStart == start && atEnd == end)
                    continue;

                double t0 = edge.Domain.Start, t1 = edge.Domain.End;
                bool solved =
                    (atStart == start || TryFootParameter(edge.Curve, start, edge.Domain.Start, ref t0)) &&
                    (atEnd == end || TryFootParameter(edge.Curve, end, edge.Domain.End, ref t1)) &&
                    t1 > t0;
                double residual = solved
                    ? Math.Max(edge.Curve.PointAt(t0).DistanceTo(start), edge.Curve.PointAt(t1).DistanceTo(end))
                    : double.PositiveInfinity;

                if (residual > tolerance)
                {
                    // The vertex is off the curve by more than the gap tolerance even at
                    // the nearest parameter: closing it needs curve re-fitting, which is
                    // a modelling operation. Reported, never attempted.
                    residualLeft++;
                    continue;
                }
                if (t0 != edge.Domain.Start || t1 != edge.Domain.End)
                {
                    edge.Domain = new Interval(t0, t1);
                    retrimmed++;
                }
            }
            if (residualLeft > 0)
                notes.Add($"{residualLeft} curved edges still end away from their (unified) vertices " +
                          "beyond the gap tolerance even at the nearest curve parameter; closing those " +
                          "gaps needs curve re-fitting, which healing does not do.");
            return retrimmed;
        }

        /// <summary>Foot-of-perpendicular parameter near <paramref name="t"/> by local
        /// Gauss–Newton on the exact/virtual derivative. The gap is sub-tolerance, so the
        /// solve is local by construction — no global seeding, no period unwrapping.</summary>
        private static bool TryFootParameter(Curve3d curve, in Vector3d target, double seed, ref double t)
        {
            double current = seed;
            for (int i = 0; i < 30; i++)
            {
                var residual = curve.PointAt(current) - target;
                var derivative = curve.DerivativeAt(current);
                double denominator = derivative.Dot(derivative);
                if (denominator < 1e-30)
                    return false; // stationary parameterization: refuse (exact-zero division guard)
                double step = -residual.Dot(derivative) / denominator;
                // Break BEFORE applying a negligible step: an already-converged end must
                // come back bit-identical, or every heal would "re-trim" it by an ulp and
                // the report would never go quiet (idempotence).
                if (Math.Abs(step) < 1e-15 * Math.Max(1, Math.Abs(current)))
                    break;
                current += step;
            }
            if (!double.IsFinite(current))
                return false;
            t = current;
            return true;
        }

        // ---- pass 5b: straight-edge refitting (the geometric half of gap closing) ----

        public int RefitStraightEdges(List<string> notes, bool curvedNotedElsewhere = false)
        {
            int refit = 0, curvedLeft = 0;
            for (int e = 0; e < _edges.Count; e++)
            {
                var edge = _edges[e];
                if (!edge.Alive)
                    continue;

                var start = _positions[Find(edge.Start)];
                var end = _positions[Find(edge.End)];
                if (edge.Curve.Underlying is not Line3d)
                {
                    // A curved edge whose ends no longer sit on its curve needs the curve
                    // re-fitted, which is a modelling operation, not a repair. Count it.
                    if (edge.Curve.PointAt(edge.Domain.Start) != start ||
                        edge.Curve.PointAt(edge.Domain.End) != end)
                        curvedLeft++;
                    continue;
                }

                // Exact-equality tests, deliberately: the question is not "are these close"
                // (they are, by construction, within GapTolerance) but "is this edge already
                // the line through its own vertices", in which case rebuilding it would be a
                // pointless allocation and a spurious report entry.
                if (edge.Curve.PointAt(edge.Domain.Start) == start && edge.Curve.PointAt(edge.Domain.End) == end)
                    continue;
                if (start == end)
                    continue;   // a collapsed edge; the small-edge pass owns that case

                edge.Curve = new Line3d(start, end);
                edge.Domain = Interval.Unit;
                refit++;
            }
            if (curvedLeft > 0 && !curvedNotedElsewhere)
                notes.Add($"{curvedLeft} curved edges still end away from their (unified) vertices; " +
                          "closing those gaps needs curve re-fitting, which healing does not do " +
                          "(RetrimCurvedEdges removes the tangential part).");
            return refit;
        }

        // ---- pass 6: wire ordering ----

        public (int Loops, int Reversed) RepairLoops(List<string> notes)
        {
            int reordered = 0, reversed = 0, unrepairable = 0;
            foreach (var face in _faces.Where(f => f.Alive))
            {
                foreach (var loop in face.Loops.Where(l => l.Alive))
                {
                    if (loop.Coedges.Count < 2 || IsChained(loop))
                        continue;

                    var remaining = new List<(int Edge, bool SameSense)>(loop.Coedges);
                    var chain = new List<(int Edge, bool SameSense)> { remaining[0] };
                    remaining.RemoveAt(0);
                    int localReversals = 0;
                    bool stuck = false;

                    while (remaining.Count > 0)
                    {
                        int tail = EndOf(chain[^1]);
                        int pick = -1;
                        bool flip = false;
                        for (int i = 0; i < remaining.Count; i++)
                        {
                            if (StartOf(remaining[i]) == tail)
                            {
                                pick = i;
                                break;
                            }
                            if (pick < 0 && EndOf(remaining[i]) == tail)
                            {
                                pick = i;
                                flip = true;
                            }
                        }
                        if (pick < 0)
                        {
                            stuck = true;
                            break;
                        }
                        var next = remaining[pick];
                        remaining.RemoveAt(pick);
                        if (flip)
                        {
                            next = (next.Edge, !next.SameSense);
                            localReversals++;
                        }
                        chain.Add(next);
                    }

                    if (stuck)
                    {
                        unrepairable++;
                        continue;
                    }

                    bool changed = localReversals > 0;
                    for (int i = 0; i < chain.Count && !changed; i++)
                        changed = chain[i] != loop.Coedges[i];
                    if (!changed)
                        continue;

                    loop.Coedges.Clear();
                    loop.Coedges.AddRange(chain);
                    reordered++;
                    reversed += localReversals;
                }
            }
            if (unrepairable > 0)
                notes.Add($"{unrepairable} loops could not be chained end-to-start: their coedges do not " +
                          "form a single closed walk even after vertex merging.");
            return (reordered, reversed);
        }

        private bool IsChained(WorkLoop loop)
        {
            var c = loop.Coedges;
            for (int i = 0; i < c.Count; i++)
            {
                if (EndOf(c[i]) != StartOf(c[(i + 1) % c.Count]))
                    return false;
            }
            return true;
        }

        // ---- pass 7: shell repair (orientation flood + outward vote + repartition) ----

        /// <summary>
        /// The B-Rep counterpart of <c>MeshRepair</c>'s per-component winding flood, and
        /// OCCT's <c>ShapeFix_Shell</c>. Three steps, each a no-op on well-formed input:
        /// <list type="number">
        /// <item><description><b>Consistency flood</b> — the manifold invariant is two uses
        /// per edge with OPPOSITE sense, so crossing a shared edge whose two uses agree
        /// means one of the faces is flipped relative to the other; BFS propagates a flip
        /// bit and a contradiction means the component is non-orientable (reported, left
        /// untouched).</description></item>
        /// <item><description><b>Global side vote</b> — a consistent component can still be
        /// inside-out as a whole. The vote is the fan volume of the sampled boundary
        /// loops (exact in sign for polyhedra and boolean-style trimmed faces), compared
        /// against containment parity: an outermost component must enclose POSITIVE
        /// volume, a component nested inside another (a cavity) NEGATIVE, because
        /// material-outward normals point into a void. Pole-bounded and closed-band faces
        /// (spheres, tori) have boundary loops that enclose ~no volume, so their vote is
        /// honestly AMBIGUOUS: the component keeps its authored side, with a note only
        /// when the flood already had to flip something. Open components never vote.
        /// </description></item>
        /// <item><description><b>Repartition</b> — each connected component becomes one
        /// shell (splitting a shell whose faces are disconnected, merging shells one
        /// component spans). Skipped entirely when the existing partition already equals
        /// the component partition, so valid input is bit-stable.</description></item>
        /// </list>
        /// </summary>
        public (int Flipped, int Split) RepairShells(List<string> notes)
        {
            var faces = Enumerable.Range(0, _faces.Count).Where(f => _faces[f].Alive).ToList();
            if (faces.Count == 0)
                return (0, 0);

            // Edge -> its alive uses as (face, sense).
            var usesOf = new Dictionary<int, List<(int Face, bool Sense)>>();
            for (int f = 0; f < _faces.Count; f++)
            {
                if (!_faces[f].Alive)
                    continue;
                foreach (var loop in _faces[f].Loops.Where(l => l.Alive))
                {
                    foreach (var (edge, sense) in loop.Coedges)
                    {
                        if (!usesOf.TryGetValue(edge, out var list))
                            usesOf[edge] = list = [];
                        list.Add((f, sense));
                    }
                }
            }

            // BFS components + flip bits over 2-use edges joining two DIFFERENT faces.
            var component = new Dictionary<int, int>();
            var flip = new Dictionary<int, bool>();
            var componentFaces = new List<List<int>>();
            var orientable = new List<bool>();
            foreach (int seed in faces)
            {
                if (component.ContainsKey(seed))
                    continue;
                int id = componentFaces.Count;
                var members = new List<int>();
                bool consistent = true;
                var queue = new Queue<int>();
                component[seed] = id;
                flip[seed] = false;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    members.Add(f);
                    foreach (var loop in _faces[f].Loops.Where(l => l.Alive))
                    {
                        foreach (var (edge, sense) in loop.Coedges)
                        {
                            var uses = usesOf[edge];
                            if (uses.Count != 2)
                                continue;
                            var other = uses[0].Face == f && uses[0].Sense == sense ? uses[1] : uses[0];
                            if (other.Face == f)
                                continue; // a face meeting itself constrains nothing here
                            bool needed = flip[f] ^ (sense == other.Sense);
                            if (!component.TryGetValue(other.Face, out int oc))
                            {
                                component[other.Face] = id;
                                flip[other.Face] = needed;
                                queue.Enqueue(other.Face);
                            }
                            else if (oc == id && flip[other.Face] != needed)
                            {
                                consistent = false; // non-orientable (Möbius-like) gluing
                            }
                        }
                    }
                }
                componentFaces.Add(members);
                orientable.Add(consistent);
            }

            // Per-face fan volume of the sampled boundary loops + bounds, for the vote.
            var faceVolume = new Dictionary<int, double>();
            var faceBounds = new Dictionary<int, Aabb>();
            foreach (int f in faces)
            {
                double volume = 0;
                var box = Aabb.Empty;
                foreach (var loop in _faces[f].Loops.Where(l => l.Alive))
                {
                    var points = SampleLoop(loop);
                    for (int i = 1; i + 1 < points.Count; i++)
                        volume += points[0].Dot(points[i].Cross(points[i + 1])) / 6;
                    foreach (var p in points)
                        box = box.Union(p);
                }
                faceVolume[f] = volume;
                faceBounds[f] = box;
            }

            int flipped = 0;
            var componentBounds = new List<Aabb>();
            var componentClosed = new List<bool>();
            for (int c = 0; c < componentFaces.Count; c++)
            {
                var box = Aabb.Empty;
                bool closed = true;
                foreach (int f in componentFaces[c])
                {
                    box = box.Union(faceBounds[f]);
                    foreach (var loop in _faces[f].Loops.Where(l => l.Alive))
                    {
                        foreach (var (edge, _) in loop.Coedges)
                        {
                            if (usesOf[edge].Count != 2)
                                closed = false;
                        }
                    }
                }
                componentBounds.Add(box);
                componentClosed.Add(closed);
            }

            for (int c = 0; c < componentFaces.Count; c++)
            {
                if (!orientable[c])
                {
                    notes.Add($"A connected component of {componentFaces[c].Count} faces is not orientable " +
                              "(its shared-edge senses contradict); its orientation was left untouched.");
                    continue;
                }

                int pendingFlips = componentFaces[c].Count(f => flip[f]);
                if (componentClosed[c])
                {
                    double volume = componentFaces[c].Sum(f => flip[f] ? -faceVolume[f] : faceVolume[f]);
                    // Containment parity: nested components are voids and must point
                    // inward (negative enclosed volume).
                    int depth = 0;
                    for (int other = 0; other < componentFaces.Count; other++)
                    {
                        if (other != c && StrictlyContains(componentBounds[other], componentBounds[c]))
                            depth++;
                    }
                    bool expectPositive = depth % 2 == 0;
                    double extent = componentBounds[c].Size.Length;
                    // Ambiguity guard: a RATIO of volumes (scale-free tier). Boundary
                    // loops of pole-bounded/closed-band faces enclose ~nothing, and a
                    // vote read from noise would flip whole solids at random.
                    if (Math.Abs(volume) > 1e-9 * extent * extent * extent)
                    {
                        if (volume > 0 != expectPositive)
                        {
                            foreach (int f in componentFaces[c])
                                flip[f] = !flip[f];
                            pendingFlips = componentFaces[c].Count(f => flip[f]);
                        }
                    }
                    else if (pendingFlips > 0)
                    {
                        notes.Add("A component's global orientation vote was ambiguous (its boundary loops " +
                                  "enclose no measurable volume — pole-bounded or closed-band faces); its " +
                                  "authored side was kept.");
                    }
                }

                foreach (int f in componentFaces[c])
                {
                    if (!flip[f])
                        continue;
                    FlipFace(_faces[f]);
                    flipped++;
                }
            }

            // Repartition: one shell per component — but ONLY when the existing partition
            // differs, so healthy input is bit-stable.
            bool partitionMatches = true;
            var shellOfComponent = new int?[componentFaces.Count];
            var componentsInShell = new Dictionary<int, HashSet<int>>();
            foreach (int f in faces)
            {
                int c = component[f];
                int s = _faces[f].Shell;
                if (shellOfComponent[c] is { } known && known != s)
                    partitionMatches = false;
                shellOfComponent[c] = s;
                if (!componentsInShell.TryGetValue(s, out var set))
                    componentsInShell[s] = set = [];
                set.Add(c);
            }
            if (componentsInShell.Values.Any(set => set.Count > 1))
                partitionMatches = false;

            int split = 0;
            if (!partitionMatches)
            {
                int aliveShellsBefore = componentsInShell.Count;
                foreach (int f in faces)
                    _faces[f].Shell = component[f];
                _shellCount = componentFaces.Count;
                split = Math.Max(0, componentFaces.Count - aliveShellsBefore);
                if (componentFaces.Count < aliveShellsBefore)
                    notes.Add($"{aliveShellsBefore - componentFaces.Count} shells merged: one connected " +
                              "component spanned several shells.");
                else if (split > 0)
                    notes.Add($"{split} shells split off: a shell's faces formed several disconnected components.");
            }
            return (flipped, split);
        }

        /// <summary>Strict box containment with a hair of slack — an exact-boundary tie
        /// (two components sharing a bounding plane) must not count as nesting.</summary>
        private static bool StrictlyContains(in Aabb outer, in Aabb inner)
        {
            double slack = 1e-12 * outer.Size.Length; // scale-free tier
            return inner.Min.X > outer.Min.X + slack && inner.Max.X < outer.Max.X - slack
                && inner.Min.Y > outer.Min.Y + slack && inner.Max.Y < outer.Max.Y - slack
                && inner.Min.Z > outer.Min.Z + slack && inner.Max.Z < outer.Max.Z - slack;
        }

        /// <summary>Reverses a face's orientation: every loop re-winds (order and senses)
        /// and <see cref="WorkFace.IsReversed"/> toggles, so the outward normal flips while
        /// "loops run CCW about the outward normal" keeps holding.</summary>
        private static void FlipFace(WorkFace face)
        {
            foreach (var loop in face.Loops.Where(l => l.Alive))
            {
                loop.Coedges.Reverse();
                for (int i = 0; i < loop.Coedges.Count; i++)
                    loop.Coedges[i] = (loop.Coedges[i].Edge, !loop.Coedges[i].SameSense);
            }
            face.IsReversed = !face.IsReversed;
        }

        // ---- output ----

        public (BrepSolid Solid, int EmptyShells) Build()
        {
            var vertices = new Dictionary<int, BrepVertex>();
            BrepVertex Vertex(int v)
            {
                int root = Find(v);
                if (!vertices.TryGetValue(root, out var built))
                    vertices[root] = built = new BrepVertex(_positions[root]);
                return built;
            }

            BrepEdge Edge(int e)
            {
                var edge = _edges[e];
                return edge.Built ??= new BrepEdge(edge.Curve, edge.Domain, Vertex(edge.Start), Vertex(edge.End));
            }

            var shells = new List<BrepShell>();
            int emptyShells = 0;
            for (int s = 0; s < _shellCount; s++)
            {
                var faces = new List<BrepFace>();
                foreach (var face in _faces.Where(f => f.Alive && f.Shell == s))
                {
                    var loops = new List<BrepLoop>();
                    foreach (var loop in face.Loops.Where(l => l.Alive && l.Coedges.Count > 0))
                        loops.Add(new BrepLoop([.. loop.Coedges.Select(c => new BrepCoedge(Edge(c.Edge), c.SameSense))]));
                    if (loops.Count > 0)
                        faces.Add(new BrepFace(face.Surface, loops, face.IsReversed));
                }
                if (faces.Count > 0)
                    shells.Add(new BrepShell(faces));
                else
                    emptyShells++;
            }

            if (shells.Count == 0)
                throw new InvalidOperationException(
                    "Healing removed every face; the input had no non-degenerate geometry at this tolerance.");
            return (new BrepSolid(shells), emptyShells);
        }
    }
}
