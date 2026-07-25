using System.Diagnostics;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Mutable companion to <see cref="HalfEdgeMesh"/>: the same half-edge structure
/// (explicit boundary half-edges with <c>face = -1</c>, <c>Twin</c> always exists,
/// struct-of-arrays storage) plus guarded Euler operators — <see cref="SplitEdge"/>,
/// <see cref="FlipEdge"/>, <see cref="CollapseEdge"/>, <see cref="PokeFace"/>,
/// <see cref="MergeEdges"/>. <see cref="HalfEdgeMesh"/> stays immutable-after-build;
/// editing is: <see cref="FromMesh"/> → operators → <see cref="ToMesh"/> (compacted,
/// re-validated through the manifold-checking <c>Build</c>).
/// <para>
/// Deleted elements are recycled through free lists chained inside the dead slots
/// (g3 <c>RefCountVector</c> model); indices of live elements never move until
/// <see cref="ToMesh"/> compacts. Every operator refuses (returns a
/// <see cref="MeshOperationResult"/>) rather than corrupting: all guards run before the
/// first mutation. <see cref="Timestamp"/>/<see cref="ShapeTimestamp"/> increment on
/// every mutation so caches and spatial trees can invalidate.
/// </para>
/// <para>
/// When a change set is active (<see cref="BeginChangeSet"/>), each operation emits a
/// reversible <see cref="MeshChange"/> record; reverting restores the storage — arrays,
/// free lists, counters — bit-identically.
/// </para>
/// </summary>
public sealed partial class EditableMesh
{
    // Struct-of-arrays storage, same layout as HalfEdgeMesh plus alive flags.
    // Free lists are chained through the dead slots themselves (_vertexOut / _heNext /
    // _faceHe hold the next free index), so free-list state is ordinary slot state and
    // change records restore it exactly.
    private readonly List<Vector3d> _positions = [];
    private readonly List<int> _vertexOut = [];   // outgoing half-edge, -1 isolated; boundary vertices point at their boundary half-edge
    private readonly List<bool> _vertexAlive = [];
    private readonly List<int> _heOrigin = [];
    private readonly List<int> _heNext = [];
    private readonly List<int> _hePrev = [];
    private readonly List<int> _heTwin = [];
    private readonly List<int> _heFace = [];      // -1 for boundary half-edges
    private readonly List<bool> _heAlive = [];
    private readonly List<int> _faceHe = [];
    private readonly List<bool> _faceAlive = [];

    private int _vertexFreeHead = -1;
    private int _heFreeHead = -1;
    private int _faceFreeHead = -1;
    private int _aliveVertexCount;
    private int _aliveHeCount;
    private int _aliveFaceCount;

    // Change recording: scratch journals reused per operation, sliced into a MeshChange
    // at commit. Recording only happens while a change set is active.
    private readonly List<IntChange> _scratchInts = [];
    private readonly List<PositionChange> _scratchPositions = [];
    private bool _recording;
    private MeshChangeSet? _activeChangeSet;

    private EditableMesh()
    {
    }

    /// <summary>Number of live vertices.</summary>
    public int VertexCount => _aliveVertexCount;

    /// <summary>Number of live half-edges (always even; every half-edge has a twin).</summary>
    public int HalfEdgeCount => _aliveHeCount;

    /// <summary>Number of live undirected edges.</summary>
    public int EdgeCount => _aliveHeCount / 2;

    /// <summary>Number of live faces (boundary "faces" are not counted).</summary>
    public int FaceCount => _aliveFaceCount;

    /// <summary>Length of the vertex arrays (upper bound for vertex indices, dead slots included).</summary>
    public int VertexCapacity => _positions.Count;

    /// <summary>Length of the half-edge arrays (upper bound for half-edge indices, dead slots included).</summary>
    public int HalfEdgeCapacity => _heOrigin.Count;

    /// <summary>Length of the face arrays (upper bound for face indices, dead slots included).</summary>
    public int FaceCapacity => _faceHe.Count;

    /// <summary>V − E + F over live elements. 2 for a closed genus-0 mesh, 1 for a disk.</summary>
    public int EulerCharacteristic => VertexCount - EdgeCount + FaceCount;

    /// <summary>Incremented on every mutation (operators, <c>SetPosition</c>, change replay).</summary>
    public int Timestamp { get; private set; }

    /// <summary>
    /// Incremented whenever topology or any vertex position changes. Distinct from
    /// <see cref="Timestamp"/> for parity with g3 (attribute channels would bump only
    /// the former); with positions and topology as the only state they currently move
    /// together. Caches and spatial trees key on this.
    /// </summary>
    public int ShapeTimestamp { get; private set; }

    /// <summary>True when the mesh has no live boundary half-edges.</summary>
    public bool IsClosed
    {
        get
        {
            for (int he = 0; he < _heOrigin.Count; he++)
            {
                if (_heAlive[he] && _heFace[he] < 0)
                    return false;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------- construction

    /// <summary>Creates an editable copy of an immutable mesh (indices carried over verbatim).</summary>
    public static EditableMesh FromMesh(HalfEdgeMesh mesh)
    {
        var editable = new EditableMesh();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            editable._positions.Add(mesh.GetPosition(v));
            editable._vertexOut.Add(mesh.VertexOutgoing(v));
            editable._vertexAlive.Add(true);
        }
        for (int he = 0; he < mesh.HalfEdgeCount; he++)
        {
            editable._heOrigin.Add(mesh.HeOrigin(he));
            editable._heNext.Add(mesh.HeNext(he));
            editable._hePrev.Add(mesh.HePrev(he));
            editable._heTwin.Add(mesh.HeTwin(he));
            editable._heFace.Add(mesh.HeFace(he));
            editable._heAlive.Add(true);
        }
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            editable._faceHe.Add(mesh.FaceAnyHalfEdge(f));
            editable._faceAlive.Add(true);
        }
        editable._aliveVertexCount = mesh.VertexCount;
        editable._aliveHeCount = mesh.HalfEdgeCount;
        editable._aliveFaceCount = mesh.FaceCount;
        return editable;
    }

    /// <summary>
    /// Builds an editable mesh from shared positions and per-face index loops, with the
    /// same manifold validation as <see cref="HalfEdgeMesh.Build"/>.
    /// </summary>
    public static EditableMesh FromIndexed(IReadOnlyList<Vector3d> positions, IEnumerable<IReadOnlyList<int>> faces) =>
        FromMesh(HalfEdgeMesh.Build(positions, faces));

    /// <summary>
    /// Compacts live elements (dead slots removed, live order preserved) into an
    /// immutable <see cref="HalfEdgeMesh"/>. Goes through the manifold-validating
    /// <see cref="HalfEdgeMesh.Build"/> as a safety net, so the result is guaranteed
    /// genuinely manifold.
    /// </summary>
    public HalfEdgeMesh ToMesh()
    {
        var map = new int[_positions.Count];
        Array.Fill(map, -1);
        var positions = new List<Vector3d>(_aliveVertexCount);
        for (int v = 0; v < _positions.Count; v++)
        {
            if (!_vertexAlive[v])
                continue;
            map[v] = positions.Count;
            positions.Add(_positions[v]);
        }

        var faces = new List<int[]>(_aliveFaceCount);
        var loop = new List<int>();
        for (int f = 0; f < _faceHe.Count; f++)
        {
            if (!_faceAlive[f])
                continue;
            loop.Clear();
            int start = _faceHe[f];
            int he = start;
            do
            {
                loop.Add(map[_heOrigin[he]]);
                he = _heNext[he];
            } while (he != start);
            faces.Add([.. loop]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    // ---------------------------------------------------------------- queries

    /// <summary>True when <paramref name="vertex"/> is a live vertex index.</summary>
    public bool IsVertex(int vertex) => vertex >= 0 && vertex < _positions.Count && _vertexAlive[vertex];

    /// <summary>True when <paramref name="halfEdge"/> is a live half-edge index.</summary>
    public bool IsHalfEdge(int halfEdge) => halfEdge >= 0 && halfEdge < _heOrigin.Count && _heAlive[halfEdge];

    /// <summary>True when <paramref name="face"/> is a live face index.</summary>
    public bool IsFace(int face) => face >= 0 && face < _faceHe.Count && _faceAlive[face];

    /// <summary>Position of a live vertex.</summary>
    public Vector3d GetPosition(int vertex)
    {
        CheckVertex(vertex);
        return _positions[vertex];
    }

    /// <summary>
    /// Moves a live vertex (a recorded, timestamped operation — the mesh-deformation
    /// primitive change records capture alongside topology).
    /// </summary>
    public void SetPosition(int vertex, in Vector3d position)
    {
        CheckVertex(vertex);
        BeginOperation();
        SetPositionSlot(vertex, position);
        CommitOperation(nameof(SetPosition));
    }

    /// <summary>Origin vertex of a live half-edge.</summary>
    public int Origin(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heOrigin[halfEdge];
    }

    /// <summary>Destination vertex of a live half-edge (origin of its twin).</summary>
    public int Destination(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heOrigin[_heTwin[halfEdge]];
    }

    /// <summary>Next half-edge around the same face (or boundary loop).</summary>
    public int Next(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heNext[halfEdge];
    }

    /// <summary>Previous half-edge around the same face (or boundary loop).</summary>
    public int Prev(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _hePrev[halfEdge];
    }

    /// <summary>Opposite half-edge of the same undirected edge (always exists).</summary>
    public int Twin(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heTwin[halfEdge];
    }

    /// <summary>Face on the left of the half-edge, or -1 for boundary half-edges.</summary>
    public int FaceOf(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heFace[halfEdge];
    }

    /// <summary>An arbitrary half-edge of the face's loop.</summary>
    public int FaceHalfEdge(int face)
    {
        CheckFace(face);
        return _faceHe[face];
    }

    /// <summary>
    /// An outgoing half-edge of the vertex (-1 if isolated). For boundary vertices this
    /// is always the boundary half-edge, so <see cref="IsBoundaryVertex"/> is O(1).
    /// </summary>
    public int VertexOutgoing(int vertex)
    {
        CheckVertex(vertex);
        return _vertexOut[vertex];
    }

    /// <summary>True when the half-edge itself borders the void (its face is -1).</summary>
    public bool IsBoundaryHalfEdge(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heFace[halfEdge] < 0;
    }

    /// <summary>True when the undirected edge lies on the mesh boundary.</summary>
    public bool IsBoundaryEdge(int halfEdge)
    {
        CheckHalfEdge(halfEdge);
        return _heFace[halfEdge] < 0 || _heFace[_heTwin[halfEdge]] < 0;
    }

    /// <summary>True when the vertex lies on a mesh boundary.</summary>
    public bool IsBoundaryVertex(int vertex)
    {
        CheckVertex(vertex);
        int outgoing = _vertexOut[vertex];
        return outgoing >= 0 && _heFace[outgoing] < 0;
    }

    /// <summary>Number of half-edges in the face's loop.</summary>
    public int FaceDegree(int face)
    {
        CheckFace(face);
        int start = _faceHe[face];
        int he = start;
        int degree = 0;
        do
        {
            degree++;
            he = _heNext[he];
        } while (he != start);
        return degree;
    }

    /// <summary>The directed half-edge from → to, or -1 if the edge does not exist.</summary>
    public int FindHalfEdge(int from, int to)
    {
        if (!IsVertex(from) || !IsVertex(to))
            return -1;
        int start = _vertexOut[from];
        if (start < 0)
            return -1;
        int he = start;
        do
        {
            if (_heOrigin[_heTwin[he]] == to)
                return he;
            he = _heTwin[_hePrev[he]];
        } while (he != start);
        return -1;
    }

    /// <summary>Live vertex indices, ascending.</summary>
    public IEnumerable<int> VertexIndices()
    {
        for (int v = 0; v < _positions.Count; v++)
        {
            if (_vertexAlive[v])
                yield return v;
        }
    }

    /// <summary>Live half-edge indices, ascending.</summary>
    public IEnumerable<int> HalfEdgeIndices()
    {
        for (int he = 0; he < _heOrigin.Count; he++)
        {
            if (_heAlive[he])
                yield return he;
        }
    }

    /// <summary>Live face indices, ascending.</summary>
    public IEnumerable<int> FaceIndices()
    {
        for (int f = 0; f < _faceHe.Count; f++)
        {
            if (_faceAlive[f])
                yield return f;
        }
    }

    /// <summary>All half-edges leaving the vertex, one full turn around it.</summary>
    public IEnumerable<int> OutgoingHalfEdges(int vertex)
    {
        CheckVertex(vertex);
        int start = _vertexOut[vertex];
        if (start < 0)
            yield break;
        int he = start;
        int guard = _heOrigin.Count;
        do
        {
            yield return he;
            he = _heTwin[_hePrev[he]];
            if (guard-- < 0)
                throw new InvalidOperationException($"Vertex {vertex}: outgoing walk did not close (corrupt topology).");
        } while (he != start);
    }

    /// <summary>Vertex indices of the face's loop, in winding order.</summary>
    public IEnumerable<int> FaceVertices(int face)
    {
        CheckFace(face);
        int start = _faceHe[face];
        int he = start;
        do
        {
            yield return _heOrigin[he];
            he = _heNext[he];
        } while (he != start);
    }

    private void CheckVertex(int vertex)
    {
        if (!IsVertex(vertex))
            throw new ArgumentOutOfRangeException(nameof(vertex), $"{vertex} is not a live vertex.");
    }

    private void CheckHalfEdge(int halfEdge)
    {
        if (!IsHalfEdge(halfEdge))
            throw new ArgumentOutOfRangeException(nameof(halfEdge), $"{halfEdge} is not a live half-edge.");
    }

    private void CheckFace(int face)
    {
        if (!IsFace(face))
            throw new ArgumentOutOfRangeException(nameof(face), $"{face} is not a live face.");
    }

    // ---------------------------------------------------------------- change sets

    /// <summary>
    /// The change set currently receiving operation records, if any.
    /// </summary>
    public MeshChangeSet? ActiveChangeSet => _activeChangeSet;

    /// <summary>
    /// Starts recording: every subsequent operation appends a reversible
    /// <see cref="MeshChange"/> to the returned set until <see cref="EndChangeSet"/>.
    /// </summary>
    public MeshChangeSet BeginChangeSet()
    {
        if (_activeChangeSet is not null)
            throw new InvalidOperationException("A change set is already active; call EndChangeSet first.");
        _activeChangeSet = new MeshChangeSet();
        return _activeChangeSet;
    }

    /// <summary>Stops recording and returns the completed change set.</summary>
    public MeshChangeSet EndChangeSet()
    {
        var set = _activeChangeSet ?? throw new InvalidOperationException("No change set is active.");
        _activeChangeSet = null;
        return set;
    }

    private void BeginOperation()
    {
        if (_activeChangeSet is not null)
        {
            _scratchInts.Clear();
            _scratchPositions.Clear();
            _recording = true;
        }
    }

    private void CommitOperation(string name)
    {
        if (_recording)
        {
            _recording = false;
            _activeChangeSet!.Add(new MeshChange(name, [.. _scratchInts], [.. _scratchPositions]));
        }
        Timestamp++;
        ShapeTimestamp++;
        DebugValidate();
    }

    /// <summary>Full-structure validation after every mutation, DEBUG builds only.</summary>
    [Conditional("DEBUG")]
    private void DebugValidate() => Validate();

    // ---------------------------------------------------------------- journaled slot writes

    private void RecordInt(MeshSlot slot, int index, int oldValue, int newValue)
    {
        if (_recording)
            _scratchInts.Add(new IntChange(slot, index, oldValue, newValue));
    }

    private void SetVertexOut(int vertex, int value)
    {
        int old = _vertexOut[vertex];
        if (old == value)
            return;
        _vertexOut[vertex] = value;
        RecordInt(MeshSlot.VertexOut, vertex, old, value);
    }

    private void SetVertexAlive(int vertex, bool value)
    {
        bool old = _vertexAlive[vertex];
        if (old == value)
            return;
        _vertexAlive[vertex] = value;
        RecordInt(MeshSlot.VertexAlive, vertex, old ? 1 : 0, value ? 1 : 0);
    }

    private void SetHeOrigin(int he, int value)
    {
        int old = _heOrigin[he];
        if (old == value)
            return;
        _heOrigin[he] = value;
        RecordInt(MeshSlot.HeOrigin, he, old, value);
    }

    private void SetHeNext(int he, int value)
    {
        int old = _heNext[he];
        if (old == value)
            return;
        _heNext[he] = value;
        RecordInt(MeshSlot.HeNext, he, old, value);
    }

    private void SetHePrev(int he, int value)
    {
        int old = _hePrev[he];
        if (old == value)
            return;
        _hePrev[he] = value;
        RecordInt(MeshSlot.HePrev, he, old, value);
    }

    private void SetHeTwin(int he, int value)
    {
        int old = _heTwin[he];
        if (old == value)
            return;
        _heTwin[he] = value;
        RecordInt(MeshSlot.HeTwin, he, old, value);
    }

    private void SetHeFace(int he, int value)
    {
        int old = _heFace[he];
        if (old == value)
            return;
        _heFace[he] = value;
        RecordInt(MeshSlot.HeFace, he, old, value);
    }

    private void SetHeAlive(int he, bool value)
    {
        bool old = _heAlive[he];
        if (old == value)
            return;
        _heAlive[he] = value;
        RecordInt(MeshSlot.HeAlive, he, old ? 1 : 0, value ? 1 : 0);
    }

    private void SetFaceHe(int face, int value)
    {
        int old = _faceHe[face];
        if (old == value)
            return;
        _faceHe[face] = value;
        RecordInt(MeshSlot.FaceHe, face, old, value);
    }

    private void SetFaceAlive(int face, bool value)
    {
        bool old = _faceAlive[face];
        if (old == value)
            return;
        _faceAlive[face] = value;
        RecordInt(MeshSlot.FaceAlive, face, old ? 1 : 0, value ? 1 : 0);
    }

    private void SetVertexFreeHead(int value)
    {
        if (_vertexFreeHead == value)
            return;
        RecordInt(MeshSlot.VertexFreeHead, 0, _vertexFreeHead, value);
        _vertexFreeHead = value;
    }

    private void SetHeFreeHead(int value)
    {
        if (_heFreeHead == value)
            return;
        RecordInt(MeshSlot.HalfEdgeFreeHead, 0, _heFreeHead, value);
        _heFreeHead = value;
    }

    private void SetFaceFreeHead(int value)
    {
        if (_faceFreeHead == value)
            return;
        RecordInt(MeshSlot.FaceFreeHead, 0, _faceFreeHead, value);
        _faceFreeHead = value;
    }

    private void SetAliveVertexCount(int value)
    {
        RecordInt(MeshSlot.AliveVertexCount, 0, _aliveVertexCount, value);
        _aliveVertexCount = value;
    }

    private void SetAliveHeCount(int value)
    {
        RecordInt(MeshSlot.AliveHalfEdgeCount, 0, _aliveHeCount, value);
        _aliveHeCount = value;
    }

    private void SetAliveFaceCount(int value)
    {
        RecordInt(MeshSlot.AliveFaceCount, 0, _aliveFaceCount, value);
        _aliveFaceCount = value;
    }

    private void SetPositionSlot(int vertex, in Vector3d value)
    {
        var old = _positions[vertex];
        if (ExactlyEqual(old, value))
            return;
        _positions[vertex] = value;
        if (_recording)
            _scratchPositions.Add(new PositionChange(vertex, old, value));
    }

    // Bit-identity check for change journaling — deliberately bypasses Tolerance:
    // change records restore state exactly, so "same value" means the same bits.
    private static bool ExactlyEqual(in Vector3d a, in Vector3d b) =>
        a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z);

    // ---------------------------------------------------------------- allocation

    private int AllocVertex(in Vector3d position)
    {
        int v = _vertexFreeHead;
        if (v >= 0)
        {
            SetVertexFreeHead(_vertexOut[v]);
            SetVertexOut(v, -1);
        }
        else
        {
            v = _positions.Count;
            _positions.Add(default);
            _vertexOut.Add(-1);
            _vertexAlive.Add(false);
            RecordInt(MeshSlot.VertexGrow, v, 0, 1);
        }
        SetVertexAlive(v, true);
        SetPositionSlot(v, position);
        SetAliveVertexCount(_aliveVertexCount + 1);
        return v;
    }

    private int AllocHalfEdge()
    {
        int he = _heFreeHead;
        if (he >= 0)
        {
            SetHeFreeHead(_heNext[he]);
            SetHeNext(he, -1);
        }
        else
        {
            he = _heOrigin.Count;
            _heOrigin.Add(-1);
            _heNext.Add(-1);
            _hePrev.Add(-1);
            _heTwin.Add(-1);
            _heFace.Add(-1);
            _heAlive.Add(false);
            RecordInt(MeshSlot.HalfEdgeGrow, he, 0, 1);
        }
        SetHeAlive(he, true);
        SetAliveHeCount(_aliveHeCount + 1);
        return he;
    }

    private int AllocFace()
    {
        int f = _faceFreeHead;
        if (f >= 0)
        {
            SetFaceFreeHead(_faceHe[f]);
            SetFaceHe(f, -1);
        }
        else
        {
            f = _faceHe.Count;
            _faceHe.Add(-1);
            _faceAlive.Add(false);
            RecordInt(MeshSlot.FaceGrow, f, 0, 1);
        }
        SetFaceAlive(f, true);
        SetAliveFaceCount(_aliveFaceCount + 1);
        return f;
    }

    private void FreeVertex(int v)
    {
        SetVertexAlive(v, false);
        SetVertexOut(v, _vertexFreeHead);
        SetVertexFreeHead(v);
        SetAliveVertexCount(_aliveVertexCount - 1);
    }

    private void FreeHalfEdge(int he)
    {
        SetHeAlive(he, false);
        SetHeOrigin(he, -1);
        SetHePrev(he, -1);
        SetHeTwin(he, -1);
        SetHeFace(he, -1);
        SetHeNext(he, _heFreeHead);
        SetHeFreeHead(he);
        SetAliveHeCount(_aliveHeCount - 1);
    }

    private void FreeFace(int f)
    {
        SetFaceAlive(f, false);
        SetFaceHe(f, _faceFreeHead);
        SetFaceFreeHead(f);
        SetAliveFaceCount(_aliveFaceCount - 1);
    }

    // ---------------------------------------------------------------- change replay

    internal void ApplyChange(IntChange[] ints, PositionChange[] positions)
    {
        if (_activeChangeSet is not null)
            throw new InvalidOperationException("Cannot replay change records while a change set is being recorded.");
        foreach (var c in ints)
            ReplayInt(c.Slot, c.Index, expected: c.OldValue, value: c.NewValue, forward: true);
        foreach (var p in positions)
        {
            if (p.Index >= _positions.Count || !ExactlyEqual(_positions[p.Index], p.OldValue))
                throw new InvalidOperationException($"Change record mismatch at vertex {p.Index}: the mesh is not in the record's pre state.");
            _positions[p.Index] = p.NewValue;
        }
        Timestamp++;
        ShapeTimestamp++;
        DebugValidate();
    }

    internal void RevertChange(IntChange[] ints, PositionChange[] positions)
    {
        if (_activeChangeSet is not null)
            throw new InvalidOperationException("Cannot replay change records while a change set is being recorded.");
        for (int i = positions.Length - 1; i >= 0; i--)
        {
            var p = positions[i];
            if (p.Index >= _positions.Count || !ExactlyEqual(_positions[p.Index], p.NewValue))
                throw new InvalidOperationException($"Change record mismatch at vertex {p.Index}: the mesh is not in the record's post state.");
            _positions[p.Index] = p.OldValue;
        }
        for (int i = ints.Length - 1; i >= 0; i--)
        {
            var c = ints[i];
            ReplayInt(c.Slot, c.Index, expected: c.NewValue, value: c.OldValue, forward: false);
        }
        Timestamp++;
        ShapeTimestamp++;
        DebugValidate();
    }

    private void ReplayInt(MeshSlot slot, int index, int expected, int value, bool forward)
    {
        switch (slot)
        {
            case MeshSlot.VertexGrow:
                if (forward)
                {
                    Expect(_positions.Count == index, slot, index);
                    _positions.Add(default);
                    _vertexOut.Add(-1);
                    _vertexAlive.Add(false);
                }
                else
                {
                    Expect(_positions.Count == index + 1, slot, index);
                    _positions.RemoveAt(index);
                    _vertexOut.RemoveAt(index);
                    _vertexAlive.RemoveAt(index);
                }
                return;
            case MeshSlot.HalfEdgeGrow:
                if (forward)
                {
                    Expect(_heOrigin.Count == index, slot, index);
                    _heOrigin.Add(-1);
                    _heNext.Add(-1);
                    _hePrev.Add(-1);
                    _heTwin.Add(-1);
                    _heFace.Add(-1);
                    _heAlive.Add(false);
                }
                else
                {
                    Expect(_heOrigin.Count == index + 1, slot, index);
                    _heOrigin.RemoveAt(index);
                    _heNext.RemoveAt(index);
                    _hePrev.RemoveAt(index);
                    _heTwin.RemoveAt(index);
                    _heFace.RemoveAt(index);
                    _heAlive.RemoveAt(index);
                }
                return;
            case MeshSlot.FaceGrow:
                if (forward)
                {
                    Expect(_faceHe.Count == index, slot, index);
                    _faceHe.Add(-1);
                    _faceAlive.Add(false);
                }
                else
                {
                    Expect(_faceHe.Count == index + 1, slot, index);
                    _faceHe.RemoveAt(index);
                    _faceAlive.RemoveAt(index);
                }
                return;
        }

        // Bounds guard: a record replayed against the wrong state may reference slots
        // beyond the current arrays; fail loudly rather than with an index exception.
        int bound = slot switch
        {
            MeshSlot.VertexOut or MeshSlot.VertexAlive => _vertexOut.Count,
            MeshSlot.HeOrigin or MeshSlot.HeNext or MeshSlot.HePrev or MeshSlot.HeTwin
                or MeshSlot.HeFace or MeshSlot.HeAlive => _heOrigin.Count,
            MeshSlot.FaceHe or MeshSlot.FaceAlive => _faceHe.Count,
            _ => int.MaxValue, // free heads and counters use index 0
        };
        Expect(index < bound, slot, index);

        int current = slot switch
        {
            MeshSlot.VertexOut => _vertexOut[index],
            MeshSlot.VertexAlive => _vertexAlive[index] ? 1 : 0,
            MeshSlot.HeOrigin => _heOrigin[index],
            MeshSlot.HeNext => _heNext[index],
            MeshSlot.HePrev => _hePrev[index],
            MeshSlot.HeTwin => _heTwin[index],
            MeshSlot.HeFace => _heFace[index],
            MeshSlot.HeAlive => _heAlive[index] ? 1 : 0,
            MeshSlot.FaceHe => _faceHe[index],
            MeshSlot.FaceAlive => _faceAlive[index] ? 1 : 0,
            MeshSlot.VertexFreeHead => _vertexFreeHead,
            MeshSlot.HalfEdgeFreeHead => _heFreeHead,
            MeshSlot.FaceFreeHead => _faceFreeHead,
            MeshSlot.AliveVertexCount => _aliveVertexCount,
            MeshSlot.AliveHalfEdgeCount => _aliveHeCount,
            MeshSlot.AliveFaceCount => _aliveFaceCount,
            _ => throw new InvalidOperationException($"Unknown slot {slot}."),
        };
        Expect(current == expected, slot, index);

        switch (slot)
        {
            case MeshSlot.VertexOut: _vertexOut[index] = value; break;
            case MeshSlot.VertexAlive: _vertexAlive[index] = value != 0; break;
            case MeshSlot.HeOrigin: _heOrigin[index] = value; break;
            case MeshSlot.HeNext: _heNext[index] = value; break;
            case MeshSlot.HePrev: _hePrev[index] = value; break;
            case MeshSlot.HeTwin: _heTwin[index] = value; break;
            case MeshSlot.HeFace: _heFace[index] = value; break;
            case MeshSlot.HeAlive: _heAlive[index] = value != 0; break;
            case MeshSlot.FaceHe: _faceHe[index] = value; break;
            case MeshSlot.FaceAlive: _faceAlive[index] = value != 0; break;
            case MeshSlot.VertexFreeHead: _vertexFreeHead = value; break;
            case MeshSlot.HalfEdgeFreeHead: _heFreeHead = value; break;
            case MeshSlot.FaceFreeHead: _faceFreeHead = value; break;
            case MeshSlot.AliveVertexCount: _aliveVertexCount = value; break;
            case MeshSlot.AliveHalfEdgeCount: _aliveHeCount = value; break;
            case MeshSlot.AliveFaceCount: _aliveFaceCount = value; break;
        }
    }

    private static void Expect(bool condition, MeshSlot slot, int index)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"Change record mismatch at {slot}[{index}]: the mesh is not in the state this record expects (apply/revert order violated).");
    }

    // ---------------------------------------------------------------- validation

    /// <summary>
    /// Checks all structural invariants over live elements — twin involution, next/prev
    /// consistency, face loop closure, vertex ring closure and completeness, the
    /// boundary-preference rule for vertex outgoing half-edges, single boundary fan per
    /// vertex (no bow-ties), free-list integrity, and alive-count agreement. Throws
    /// <see cref="InvalidOperationException"/> with details on the first violation.
    /// </summary>
    public void Validate()
    {
        int aliveVertices = 0, aliveHes = 0, aliveFaces = 0;

        for (int he = 0; he < _heOrigin.Count; he++)
        {
            if (!_heAlive[he])
                continue;
            aliveHes++;
            int twin = _heTwin[he];
            if (twin < 0 || twin >= _heOrigin.Count || !_heAlive[twin] || _heTwin[twin] != he)
                throw new InvalidOperationException($"Half-edge {he}: twin is not a live involution.");
            if (twin == he)
                throw new InvalidOperationException($"Half-edge {he} is its own twin.");
            int next = _heNext[he];
            if (next < 0 || next >= _heOrigin.Count || !_heAlive[next] || _hePrev[next] != he)
                throw new InvalidOperationException($"Half-edge {he}: prev(next) does not return to it.");
            if (_heFace[next] != _heFace[he])
                throw new InvalidOperationException($"Half-edge {he}: next crosses into a different face.");
            if (_heOrigin[next] != _heOrigin[twin])
                throw new InvalidOperationException($"Half-edge {he}: destination disagrees between next and twin.");
            int origin = _heOrigin[he];
            if (origin < 0 || origin >= _positions.Count || !_vertexAlive[origin])
                throw new InvalidOperationException($"Half-edge {he}: origin {origin} is not a live vertex.");
            int face = _heFace[he];
            if (face >= 0 && (face >= _faceHe.Count || !_faceAlive[face]))
                throw new InvalidOperationException($"Half-edge {he}: face {face} is not a live face.");
        }

        for (int f = 0; f < _faceHe.Count; f++)
        {
            if (!_faceAlive[f])
                continue;
            aliveFaces++;
            int start = _faceHe[f];
            if (start < 0 || start >= _heOrigin.Count || !_heAlive[start])
                throw new InvalidOperationException($"Face {f}: anchor half-edge is not live.");
            int he = start;
            for (int steps = 0; ; steps++)
            {
                if (steps > _heOrigin.Count)
                    throw new InvalidOperationException($"Face {f}: next-cycle does not close.");
                if (_heFace[he] != f)
                    throw new InvalidOperationException($"Face {f}: contains half-edge {he} labeled with face {_heFace[he]}.");
                he = _heNext[he];
                if (he == start)
                    break;
            }
        }

        // Per-vertex: ring closes, covers every live half-edge with that origin, and the
        // outgoing pointer prefers the boundary half-edge (of which there is at most one
        // — two boundary fans at a vertex is a bow-tie).
        var originCounts = new int[_positions.Count];
        var boundaryOut = new int[_positions.Count];
        Array.Fill(boundaryOut, 0);
        for (int he = 0; he < _heOrigin.Count; he++)
        {
            if (!_heAlive[he])
                continue;
            originCounts[_heOrigin[he]]++;
            if (_heFace[he] < 0)
                boundaryOut[_heOrigin[he]]++;
        }

        for (int v = 0; v < _positions.Count; v++)
        {
            if (!_vertexAlive[v])
                continue;
            aliveVertices++;
            int start = _vertexOut[v];
            if (start < 0)
            {
                if (originCounts[v] != 0)
                    throw new InvalidOperationException($"Vertex {v}: marked isolated but has {originCounts[v]} outgoing half-edges.");
                continue;
            }
            if (start >= _heOrigin.Count || !_heAlive[start])
                throw new InvalidOperationException($"Vertex {v}: outgoing half-edge is not live.");
            if (boundaryOut[v] > 1)
                throw new InvalidOperationException($"Vertex {v}: has {boundaryOut[v]} boundary fans (bow-tie).");
            if (boundaryOut[v] == 1 && _heFace[start] >= 0)
                throw new InvalidOperationException($"Vertex {v}: lies on a boundary but its outgoing pointer is an interior half-edge.");
            int he = start;
            int ringLength = 0;
            for (int steps = 0; ; steps++)
            {
                if (steps > _heOrigin.Count)
                    throw new InvalidOperationException($"Vertex {v}: outgoing-cycle does not close.");
                if (_heOrigin[he] != v)
                    throw new InvalidOperationException($"Vertex {v}: outgoing walk reached half-edge {he} with origin {_heOrigin[he]}.");
                ringLength++;
                he = _heTwin[_hePrev[he]];
                if (he == start)
                    break;
            }
            if (ringLength != originCounts[v])
                throw new InvalidOperationException(
                    $"Vertex {v}: outgoing ring covers {ringLength} half-edges but {originCounts[v]} exist (disconnected fan).");
        }

        if (aliveVertices != _aliveVertexCount)
            throw new InvalidOperationException($"Vertex count {_aliveVertexCount} disagrees with alive flags ({aliveVertices}).");
        if (aliveHes != _aliveHeCount)
            throw new InvalidOperationException($"Half-edge count {_aliveHeCount} disagrees with alive flags ({aliveHes}).");
        if (aliveFaces != _aliveFaceCount)
            throw new InvalidOperationException($"Face count {_aliveFaceCount} disagrees with alive flags ({aliveFaces}).");

        ValidateFreeChain(_vertexFreeHead, _vertexOut, _vertexAlive, "vertex");
        ValidateFreeChain(_heFreeHead, _heNext, _heAlive, "half-edge");
        ValidateFreeChain(_faceFreeHead, _faceHe, _faceAlive, "face");
    }

    private static void ValidateFreeChain(int head, List<int> chain, List<bool> alive, string kind)
    {
        int deadCount = 0;
        for (int i = 0; i < alive.Count; i++)
        {
            if (!alive[i])
                deadCount++;
        }
        int visited = 0;
        int slot = head;
        while (slot >= 0)
        {
            if (slot >= alive.Count || alive[slot])
                throw new InvalidOperationException($"Free {kind} chain references live or out-of-range slot {slot}.");
            visited++;
            if (visited > deadCount)
                throw new InvalidOperationException($"Free {kind} chain is longer than the dead-slot population (cycle).");
            slot = chain[slot];
        }
        if (visited != deadCount)
            throw new InvalidOperationException($"Free {kind} chain covers {visited} slots but {deadCount} are dead.");
    }

    // ---------------------------------------------------------------- test hooks

    /// <summary>Full storage snapshot for bit-identity assertions in tests.</summary>
    internal (Vector3d[] Positions, int[] VertexOut, bool[] VertexAlive,
              int[] HeOrigin, int[] HeNext, int[] HePrev, int[] HeTwin, int[] HeFace, bool[] HeAlive,
              int[] FaceHe, bool[] FaceAlive,
              int VertexFreeHead, int HeFreeHead, int FaceFreeHead,
              int AliveVertices, int AliveHes, int AliveFaces) CaptureState() =>
        ([.. _positions], [.. _vertexOut], [.. _vertexAlive],
         [.. _heOrigin], [.. _heNext], [.. _hePrev], [.. _heTwin], [.. _heFace], [.. _heAlive],
         [.. _faceHe], [.. _faceAlive],
         _vertexFreeHead, _heFreeHead, _faceFreeHead,
         _aliveVertexCount, _aliveHeCount, _aliveFaceCount);
}
