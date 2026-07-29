using EngrCAD.Core;

namespace EngrCAD.BRep;

// Boundary-representation topology: Solid → Shell → Face → Loop → Coedge → Edge → Vertex.
// Topology references geometry (curves/surfaces) but never owns evaluation logic.
// Conventions: face surfaces are constructed so their normal points out of the solid, and
// loops run counter-clockwise around that outward normal (holes clockwise).

public sealed class BrepVertex(Vector3d position)
{
    public Vector3d Position => position;

    public override string ToString() => $"vertex @ {Position}";
}

/// <summary>
/// An edge: a bounded piece of a curve. Closed edges (full circles etc.) have
/// <see cref="StartVertex"/> == <see cref="EndVertex"/>.
/// </summary>
public sealed class BrepEdge(Curve3d curve, Interval domain, BrepVertex startVertex, BrepVertex endVertex)
{
    public Curve3d Curve => curve;
    public Interval Domain => domain;

    /// <summary>Settable internally for seam sealing (vertex unification after booleans).</summary>
    public BrepVertex StartVertex { get; internal set; } = startVertex;

    public BrepVertex EndVertex { get; internal set; } = endVertex;
    public bool IsClosedEdge => ReferenceEquals(StartVertex, EndVertex);

    internal readonly List<BrepCoedge> UsesInternal = [];

    /// <summary>The coedges referencing this edge; exactly two with opposite sense in a closed manifold solid.</summary>
    public IReadOnlyList<BrepCoedge> Uses => UsesInternal;

    public Vector3d PointAt(double t) => Curve.PointAt(t);
}

/// <summary>One face's use of an edge; <see cref="SameSense"/> false walks the curve backwards.</summary>
public sealed class BrepCoedge
{
    public BrepEdge Edge { get; }
    public bool SameSense { get; }
    public BrepLoop Loop { get; internal set; } = null!;

    public BrepCoedge(BrepEdge edge, bool sameSense)
    {
        Edge = edge;
        SameSense = sameSense;
        edge.UsesInternal.Add(this);
    }

    public BrepVertex StartVertex => SameSense ? Edge.StartVertex : Edge.EndVertex;
    public BrepVertex EndVertex => SameSense ? Edge.EndVertex : Edge.StartVertex;

    /// <summary>The matching coedge on the neighboring face.</summary>
    public BrepCoedge? Partner => Edge.UsesInternal.FirstOrDefault(c => !ReferenceEquals(c, this));
}

public sealed class BrepLoop
{
    private readonly List<BrepCoedge> _coedges;

    public BrepFace Face { get; internal set; } = null!;
    public IReadOnlyList<BrepCoedge> Coedges => _coedges;

    public BrepLoop(IReadOnlyList<BrepCoedge> coedges)
    {
        if (coedges.Count == 0)
            throw new ArgumentException("A loop needs at least one coedge.");
        _coedges = [.. coedges];
        foreach (var coedge in _coedges)
            coedge.Loop = this;
    }

    /// <summary>Replaces one coedge in place with a chain (used by edge splitting).</summary>
    internal void ReplaceCoedge(BrepCoedge old, IReadOnlyList<BrepCoedge> replacements)
    {
        int index = _coedges.IndexOf(old);
        if (index < 0)
            throw new InvalidOperationException("Coedge is not part of this loop.");
        _coedges.RemoveAt(index);
        _coedges.InsertRange(index, replacements);
        foreach (var coedge in replacements)
            coedge.Loop = this;
        old.Edge.UsesInternal.Remove(old);
    }
}

public sealed class BrepFace
{
    public Surface Surface { get; }

    /// <summary>First loop is the outer boundary; the rest are holes.</summary>
    public IReadOnlyList<BrepLoop> Loops { get; }

    /// <summary>
    /// When true, the face's outward normal is the reverse of the surface normal
    /// (booleans produce such faces, e.g. the wall of a subtracted tool).
    /// </summary>
    public bool IsReversed { get; }

    public BrepLoop OuterLoop => Loops[0];
    public BrepShell Shell { get; internal set; } = null!;

    /// <summary>
    /// Which named construction steps this face descends from — the persistent half of
    /// topological naming, beside the semantic <c>BrepQueries</c> selectors.
    ///
    /// <para>A tag is stamped by the modelling layer (<c>Shape.Tag(name)</c> stamps every
    /// face of its child's lowering) and then <b>inherited</b> wherever a face is DERIVED
    /// from another — every fragment a boolean's face splitting produces, the reversed
    /// copies a subtracted tool contributes, and every face the rebuilding operations
    /// (<see cref="Draft"/>, <see cref="Shelling"/>, <see cref="Filleting"/>,
    /// <see cref="ShapeHealing"/>) re-emit from a positional parent. Faces built from
    /// scratch carry nothing, which is the honest answer: a bore wall did not descend from
    /// the plate it was cut into, and neither did a fillet band from the two faces it
    /// blends.</para>
    ///
    /// <para><b>The guarantee is set-valued and one-sided</b>, and both halves matter. A
    /// face can split into SEVERAL — and one face can generate several (shelling emits a
    /// wall AND its cavity twin from one parent) — so a tag names a set of faces and never
    /// "the" face. And a site that invents surface simply does not tag, so a query can come
    /// back with fewer faces than the author expected but never with a face from somewhere
    /// else. That direction is the whole safety argument: an incomplete answer is a
    /// nuisance, a wrong one is a silent misresolve. See <c>Shape.Tag</c> for what still
    /// carries no provenance.</para>
    /// </summary>
    public IReadOnlyList<string> Provenance { get; private set; } = [];

    public BrepFace(Surface surface, IReadOnlyList<BrepLoop> loops, bool isReversed = false)
    {
        if (loops.Count == 0)
            throw new ArgumentException("A face needs at least one loop.");
        Surface = surface;
        Loops = loops;
        IsReversed = isReversed;
        foreach (var loop in loops)
            loop.Face = this;
    }

    /// <summary>
    /// Marks this face as descending from <paramref name="parent"/> (chainable, so a
    /// derive site reads <c>new BrepFace(...).DescendsFrom(face)</c>). Provenance is a
    /// list of tag strings, so inheriting is a reference copy — the list is never
    /// mutated in place.
    /// </summary>
    public BrepFace DescendsFrom(BrepFace parent)
    {
        Provenance = parent.Provenance;
        return this;
    }

    /// <summary>Adds a construction-step tag (idempotent; order of first appearance is
    /// kept, so a nested <c>Shape.Tag</c> reads outermost-last).</summary>
    public BrepFace AddProvenance(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (!Provenance.Contains(tag, StringComparer.Ordinal))
            Provenance = [.. Provenance, tag];
        return this;
    }
}

public sealed class BrepShell
{
    public IReadOnlyList<BrepFace> Faces { get; }
    public BrepSolid Solid { get; internal set; } = null!;

    public BrepShell(IReadOnlyList<BrepFace> faces)
    {
        if (faces.Count == 0)
            throw new ArgumentException("A shell needs at least one face.");
        Faces = faces;
        foreach (var face in faces)
            face.Shell = this;
    }
}

public sealed class BrepSolid
{
    public IReadOnlyList<BrepShell> Shells { get; }

    public BrepSolid(IReadOnlyList<BrepShell> shells)
    {
        if (shells.Count == 0)
            throw new ArgumentException("A solid needs at least one shell.");
        Shells = shells;
        foreach (var shell in shells)
            shell.Solid = this;
    }

    public IEnumerable<BrepFace> Faces => Shells.SelectMany(s => s.Faces);
    public IEnumerable<BrepLoop> Loops => Faces.SelectMany(f => f.Loops);
    public IEnumerable<BrepCoedge> Coedges => Loops.SelectMany(l => l.Coedges);
    public IEnumerable<BrepEdge> Edges => Coedges.Select(c => c.Edge).Distinct();
    public IEnumerable<BrepVertex> Vertices =>
        Edges.SelectMany(e => new[] { e.StartVertex, e.EndVertex }).Distinct();

    /// <summary>
    /// A fresh, independent copy of the whole topology graph — new vertices, edges,
    /// coedges, loops, faces and shells, in the same order, with the same senses and
    /// <see cref="BrepFace.IsReversed"/> flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GEOMETRY IS SHARED, NOT COPIED. Curves and surfaces are immutable once
    /// constructed — trimming produces new <c>CurveSegment</c>s rather than editing the
    /// carrier — so sharing them is both correct and what makes a clone cheap. Only the
    /// topology is mutable: coedges get re-parented, edge use-lists are rewritten, and
    /// vertices are unified during seam sealing.
    /// </para>
    /// <para>
    /// This exists because B-Rep booleans CONSUME their inputs (the v1 contract in
    /// design.md §5). Any caller that hands the same solid to a boolean twice — a
    /// <c>Shape</c> graph lowered for a second representation, a re-render after a cache
    /// drop, two designs derived from one imported body — must clone first. The damage is
    /// silent otherwise: the counts survive, so the solid still looks intact, but the
    /// coedge back-pointers belong to the first boolean's faces and the second result is
    /// closed, valid-looking and WRONG.
    /// </para>
    /// </remarks>
    public BrepSolid Clone()
    {
        var vertices = new Dictionary<BrepVertex, BrepVertex>();
        var edges = new Dictionary<BrepEdge, BrepEdge>();

        BrepVertex CloneVertex(BrepVertex vertex)
        {
            if (!vertices.TryGetValue(vertex, out var copy))
                vertices[vertex] = copy = new BrepVertex(vertex.Position);
            return copy;
        }

        BrepEdge CloneEdge(BrepEdge edge)
        {
            if (!edges.TryGetValue(edge, out var copy))
            {
                // A closed edge's start and end are the SAME vertex object; the
                // dictionary preserves that identity, which IsClosedEdge depends on.
                edges[edge] = copy = new BrepEdge(
                    edge.Curve, edge.Domain, CloneVertex(edge.StartVertex), CloneVertex(edge.EndVertex));
            }
            return copy;
        }

        var shells = new List<BrepShell>(Shells.Count);
        foreach (var shell in Shells)
        {
            var faces = new List<BrepFace>(shell.Faces.Count);
            foreach (var face in shell.Faces)
            {
                var loops = new List<BrepLoop>(face.Loops.Count);
                foreach (var loop in face.Loops)
                    loops.Add(new BrepLoop([.. loop.Coedges.Select(c => new BrepCoedge(CloneEdge(c.Edge), c.SameSense))]));
                faces.Add(new BrepFace(face.Surface, loops, face.IsReversed).DescendsFrom(face));
            }
            shells.Add(new BrepShell(faces));
        }
        return new BrepSolid(shells);
    }

    /// <summary>
    /// This solid re-placed by a proper rigid motion: a fresh, independent graph (the
    /// <see cref="Clone"/> walk) whose geometry is moved <b>in its own family</b> — a moved
    /// cylinder is a <see cref="CylinderSurface"/>, a moved revolve a
    /// <see cref="RevolvedSurface"/> — rather than wrapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The result is a POSE, not a re-derivation, and rigidity is what buys that.</b>
    /// Every parameterization in this kernel is built out of lengths and angles, and an
    /// isometry preserves both — so edge trim domains, seam phases, revolve angles and the
    /// tessellator's grid samples are the ones the solid already had, carried over verbatim
    /// rather than recomputed. Shear, non-uniform scale, uniform scale and reflection are
    /// each refused BY NAME with the reason (see <see cref="GeometryTransform"/>); in
    /// particular a uniform scale is refused not because a surface family objects but
    /// because <see cref="PolylineCurve3d"/> is parameterized by cumulative chord length,
    /// so scaling one would silently desynchronize it from the trim domains stored on the
    /// edges that use it.
    /// </para>
    /// <para>
    /// Nothing internal needs this — the <c>Shape</c> compiler bakes transforms into
    /// construction inputs at lowering, which is exact for every operation and is why it
    /// stays the route a design should take. This exists for geometry that has no
    /// construction history to bake into: an imported STEP or IGES body being posed, or a
    /// solid handed between two coordinate frames.
    /// </para>
    /// <para>
    /// Provenance rides through, since each face is derived from exactly one parent — the
    /// same guarantee <see cref="Clone"/> gives, and what keeps a tagged import selectable
    /// after it is placed.
    /// </para>
    /// </remarks>
    /// <param name="transform">A proper rigid motion (rotation and translation).</param>
    public BrepSolid Transformed(in Matrix4d transform)
    {
        GeometryTransform.RequireRigid(transform, "Re-placing a solid");
        var m = transform;

        var vertices = new Dictionary<BrepVertex, BrepVertex>();
        var edges = new Dictionary<BrepEdge, BrepEdge>();
        // Curves are shared between edges (a seam curve backs two edges, a carrier backs
        // many), so they are mapped ONCE per object. Reference identity is the right key
        // for the same reason the archive uses it: two coincident-but-distinct curves are
        // not the same curve, and merging them here would change what the solid means.
        var curves = new Dictionary<Curve3d, Curve3d>(ReferenceEqualityComparer.Instance);
        var surfaces = new Dictionary<Surface, Surface>(ReferenceEqualityComparer.Instance);

        BrepVertex MoveVertex(BrepVertex vertex)
        {
            if (!vertices.TryGetValue(vertex, out var copy))
                vertices[vertex] = copy = new BrepVertex(m.TransformPoint(vertex.Position));
            return copy;
        }

        Curve3d MoveCurve(Curve3d curve)
        {
            if (!curves.TryGetValue(curve, out var moved))
                curves[curve] = moved = GeometryTransform.Apply(curve, m);
            return moved;
        }

        BrepEdge MoveEdge(BrepEdge edge)
        {
            if (!edges.TryGetValue(edge, out var copy))
            {
                // A closed edge's start and end are the SAME vertex object; the dictionary
                // preserves that identity, which IsClosedEdge depends on. The DOMAIN is
                // carried verbatim — an isometry does not move a parameter.
                edges[edge] = copy = new BrepEdge(
                    MoveCurve(edge.Curve), edge.Domain, MoveVertex(edge.StartVertex), MoveVertex(edge.EndVertex));
            }
            return copy;
        }

        var shells = new List<BrepShell>(Shells.Count);
        foreach (var shell in Shells)
        {
            var faces = new List<BrepFace>(shell.Faces.Count);
            foreach (var face in shell.Faces)
            {
                if (!surfaces.TryGetValue(face.Surface, out var moved))
                    surfaces[face.Surface] = moved = GeometryTransform.Apply(face.Surface, m);
                var loops = new List<BrepLoop>(face.Loops.Count);
                foreach (var loop in face.Loops)
                    loops.Add(new BrepLoop([.. loop.Coedges.Select(c => new BrepCoedge(MoveEdge(c.Edge), c.SameSense))]));
                faces.Add(new BrepFace(moved, loops, face.IsReversed).DescendsFrom(face));
            }
            shells.Add(new BrepShell(faces));
        }
        return new BrepSolid(shells);
    }

    /// <summary>Structural checks: loop closure and two-manifold edge use. Throws with details.</summary>
    public void Validate()
    {
        foreach (var loop in Loops)
        {
            var coedges = loop.Coedges;
            for (int i = 0; i < coedges.Count; i++)
            {
                var next = coedges[(i + 1) % coedges.Count];
                if (!ReferenceEquals(coedges[i].EndVertex, next.StartVertex))
                    throw new InvalidOperationException("Loop coedges do not chain end-to-start.");
            }
        }

        foreach (var edge in Edges)
        {
            if (edge.UsesInternal.Count != 2)
                throw new InvalidOperationException(
                    $"Edge is used by {edge.UsesInternal.Count} coedges; a manifold solid requires exactly 2.");
            if (edge.UsesInternal[0].SameSense == edge.UsesInternal[1].SameSense)
                throw new InvalidOperationException("An edge's two uses must have opposite sense.");
        }
    }

    /// <summary>
    /// Euler–Poincaré: V − E + F − (L − F) − 2(S − G) must be 0 for a valid closed solid
    /// of the given total genus.
    /// </summary>
    public bool SatisfiesEulerFormula(int genus = 0)
    {
        int v = Vertices.Count();
        int e = Edges.Count();
        int f = Faces.Count();
        int l = Loops.Count();
        int s = Shells.Count;
        return v - e + f - (l - f) - 2 * (s - genus) == 0;
    }
}
