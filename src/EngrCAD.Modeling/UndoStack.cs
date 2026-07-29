namespace EngrCAD.Modeling;

/// <summary>
/// The undo history: <see cref="DocumentEdit"/>s applied through <see cref="Do"/> stack up
/// here, <see cref="Undo"/> walks back and <see cref="Redo"/> walks forward.
///
/// <para><b>Session state, not document state.</b> An undo stack is not saved — a
/// <see cref="Document"/> is what the model IS, and a history of how it got there is a
/// property of this editing session. That is also why the stack holds edits rather than
/// document snapshots: an edit is a few captured values, where a snapshot of a scene with
/// meshed parts is not.</para>
///
/// <para><b>Grouping.</b> <see cref="Group"/> collects everything done inside it into one
/// <see cref="CompoundEdit"/>, so "place six fasteners" — which is six occurrence edits and
/// six host cuts — is ONE Ctrl+Z. Groups nest; only the outermost pushes.</para>
///
/// <para>Not thread-safe by design: it belongs to whichever thread owns the UI, the same
/// rule the document model's mutable state follows.</para>
/// </summary>
public sealed class UndoStack
{
    private readonly List<DocumentEdit> _done = [];
    private readonly List<DocumentEdit> _undone = [];
    private readonly Stack<OpenGroup> _groups = new();

    private sealed record OpenGroup(string Description, List<DocumentEdit> Edits);

    /// <summary>
    /// How many steps are kept (default 200; 0 or less means unlimited). Trimming drops the
    /// OLDEST steps, which are the ones a user is least likely to reach for, and it happens
    /// on push so the bound is real rather than aspirational.
    /// </summary>
    public int Limit { get; set; } = 200;

    /// <summary>The undoable steps, oldest first.</summary>
    public IReadOnlyList<DocumentEdit> Undoable => _done;

    /// <summary>The redoable steps, most recently undone LAST.</summary>
    public IReadOnlyList<DocumentEdit> Redoable => _undone;

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>True when there is something to redo.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>What <see cref="Undo"/> would undo ("Set plate.Height"), or null.</summary>
    public string? UndoDescription => _done.Count > 0 ? _done[^1].Description : null;

    /// <summary>What <see cref="Redo"/> would redo, or null.</summary>
    public string? RedoDescription => _undone.Count > 0 ? _undone[^1].Description : null;

    /// <summary>Raised after any change to the stack — a host repaints its Edit menu from
    /// <see cref="CanUndo"/>/<see cref="UndoDescription"/> here.</summary>
    public event Action? Changed;

    /// <summary>
    /// Applies an edit and records it. A throwing edit is NOT recorded and the redo stack
    /// is left alone — the edit's own contract is that a failure changed nothing, so a
    /// refused edit must not be able to lose a user's redo history either.
    /// </summary>
    public void Do(DocumentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        edit.Apply();
        if (_groups.Count > 0)
        {
            _groups.Peek().Edits.Add(edit);
            return;
        }
        Push(edit);
    }

    /// <summary>
    /// Records an edit that has ALREADY been applied — for a host whose action performed
    /// the change itself (a viewport drag that moved an occurrence live) and wants it
    /// undoable. The edit must have captured its old state, i.e. its <c>Apply</c> must
    /// already have run.
    /// </summary>
    public void Record(DocumentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_groups.Count > 0)
        {
            _groups.Peek().Edits.Add(edit);
            return;
        }
        Push(edit);
    }

    private void Push(DocumentEdit edit)
    {
        _done.Add(edit);
        // A new edit forks the timeline: what was undone is no longer reachable.
        _undone.Clear();
        if (Limit > 0 && _done.Count > Limit)
            _done.RemoveRange(0, _done.Count - Limit);
        Changed?.Invoke();
    }

    /// <summary>
    /// Opens a group: every <see cref="Do"/> until the returned handle is disposed becomes
    /// part of ONE undo step named <paramref name="description"/>. An empty group pushes
    /// nothing. Groups nest — inner ones just contribute to the outermost.
    /// </summary>
    public IDisposable Group(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _groups.Push(new OpenGroup(description, []));
        return new GroupScope(this);
    }

    private void CloseGroup()
    {
        var group = _groups.Pop();
        if (group.Edits.Count == 0)
            return;
        // A group of one is that one edit: wrapping it would put a generic label on a step
        // that already describes itself.
        var edit = group.Edits.Count == 1
            ? group.Edits[0]
            : new CompoundEdit(group.Description, group.Edits);
        if (_groups.Count > 0)
            _groups.Peek().Edits.Add(edit);
        else
            Push(edit);
    }

    private sealed class GroupScope(UndoStack stack) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            stack.CloseGroup();
        }
    }

    /// <summary>Undoes the most recent step. Does nothing when there is none.</summary>
    public void Undo()
    {
        RequireNoOpenGroup(nameof(Undo));
        if (_done.Count == 0)
            return;
        var edit = _done[^1];
        edit.Revert();
        _done.RemoveAt(_done.Count - 1);
        _undone.Add(edit);
        Changed?.Invoke();
    }

    /// <summary>Redoes the most recently undone step. Does nothing when there is none.</summary>
    public void Redo()
    {
        RequireNoOpenGroup(nameof(Redo));
        if (_undone.Count == 0)
            return;
        var edit = _undone[^1];
        edit.Apply();
        _undone.RemoveAt(_undone.Count - 1);
        _done.Add(edit);
        Changed?.Invoke();
    }

    /// <summary>Forgets all history (after loading a document, say) without touching the
    /// document itself.</summary>
    public void Clear()
    {
        if (_groups.Count > 0)
            throw new InvalidOperationException("Close the open edit group before clearing the undo stack.");
        if (_done.Count == 0 && _undone.Count == 0)
            return;
        _done.Clear();
        _undone.Clear();
        Changed?.Invoke();
    }

    private void RequireNoOpenGroup(string operation)
    {
        if (_groups.Count > 0)
            throw new InvalidOperationException(
                $"Cannot {operation} while an edit group ('{_groups.Peek().Description}') is open.");
    }
}
