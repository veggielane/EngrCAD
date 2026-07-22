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
