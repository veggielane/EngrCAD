using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Identifies one struct-of-arrays slot family inside an <see cref="EditableMesh"/>.
/// Change records address mesh state as (slot, index, old, new) tuples, so replaying a
/// record forwards or backwards restores the storage bit-identically — including
/// free-list chains and alive counters, which are ordinary slots.
/// </summary>
internal enum MeshSlot : byte
{
    VertexOut,
    VertexAlive,
    HeOrigin,
    HeNext,
    HePrev,
    HeTwin,
    HeFace,
    HeAlive,
    FaceHe,
    FaceAlive,
    VertexFreeHead,
    HalfEdgeFreeHead,
    FaceFreeHead,
    AliveVertexCount,
    AliveHalfEdgeCount,
    AliveFaceCount,
    /// <summary>Grew the vertex arrays by one default slot at <c>Index</c>.</summary>
    VertexGrow,
    /// <summary>Grew the half-edge arrays by one default slot at <c>Index</c>.</summary>
    HalfEdgeGrow,
    /// <summary>Grew the face arrays by one default slot at <c>Index</c>.</summary>
    FaceGrow,
}

/// <summary>One journaled integer-slot mutation (old → new).</summary>
internal readonly record struct IntChange(MeshSlot Slot, int Index, int OldValue, int NewValue);

/// <summary>One journaled vertex-position mutation (old → new).</summary>
internal readonly record struct PositionChange(int Index, Vector3d OldValue, Vector3d NewValue);

/// <summary>
/// A reversible record of one <see cref="EditableMesh"/> operation (the g3
/// <c>DMesh3Changes</c> pattern, adapted to journaled slot writes): every primitive
/// storage mutation the operation performed, in order, with its old and new value.
/// <see cref="Apply"/> replays the mutations forward, <see cref="Revert"/> replays them
/// backward; both verify each slot holds the expected value before writing, so applying
/// records out of order fails loudly instead of corrupting the mesh. Because the record
/// is the complete journal, do → revert restores the SoA storage (arrays, free lists,
/// counters) bit-identically. Timestamps are cache-invalidation counters, not state,
/// and are excluded.
/// </summary>
public sealed class MeshChange
{
    /// <summary>Name of the operation that produced this record (diagnostic).</summary>
    public string Operation { get; }

    private readonly IntChange[] _ints;
    private readonly PositionChange[] _positions;

    internal MeshChange(string operation, IntChange[] ints, PositionChange[] positions)
    {
        Operation = operation;
        _ints = ints;
        _positions = positions;
    }

    /// <summary>Number of primitive slot mutations in this record.</summary>
    public int MutationCount => _ints.Length + _positions.Length;

    /// <summary>
    /// Re-applies the operation to <paramref name="mesh"/>, which must be in the state
    /// the operation originally started from.
    /// </summary>
    public void Apply(EditableMesh mesh) => mesh.ApplyChange(_ints, _positions);

    /// <summary>
    /// Undoes the operation on <paramref name="mesh"/>, which must be in the state the
    /// operation originally produced. Restores storage bit-identically.
    /// </summary>
    public void Revert(EditableMesh mesh) => mesh.RevertChange(_ints, _positions);
}

/// <summary>
/// An ordered group of <see cref="MeshChange"/> records covering one edit session —
/// the transactional unit an editor's undo/redo stack stores. Obtain one from
/// <see cref="EditableMesh.BeginChangeSet"/>; every operation performed until
/// <see cref="EditableMesh.EndChangeSet"/> appends its record here.
/// </summary>
public sealed class MeshChangeSet
{
    private readonly List<MeshChange> _changes = [];

    /// <summary>The per-operation records, in the order they were performed.</summary>
    public IReadOnlyList<MeshChange> Changes => _changes;

    internal void Add(MeshChange change) => _changes.Add(change);

    /// <summary>Re-applies all changes in order (redo).</summary>
    public void Apply(EditableMesh mesh)
    {
        foreach (var change in _changes)
            change.Apply(mesh);
    }

    /// <summary>Undoes all changes in reverse order (undo). Bit-identical restore.</summary>
    public void Revert(EditableMesh mesh)
    {
        for (int i = _changes.Count - 1; i >= 0; i--)
            _changes[i].Revert(mesh);
    }
}
