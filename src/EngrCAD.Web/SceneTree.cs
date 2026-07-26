using EngrCAD.Modeling;

namespace EngrCAD.Web;

/// <summary>What a model-tree row stands for.</summary>
public enum SceneTreeRowKind
{
    /// <summary>An assembly header: hiding it hides the whole subtree.</summary>
    Assembly,

    /// <summary>One placed part — an occurrence, so the same <see cref="Part"/> may
    /// appear on several rows.</summary>
    Part,
}

/// <summary>
/// One row of the model tree: the hierarchy indented-flat, exactly as the desktop
/// <c>SceneHost</c> shows it.
/// </summary>
/// <param name="Index">Row index (its position in <see cref="SceneTree.Rows"/>).</param>
/// <param name="Label">The occurrence's own name (not the full path).</param>
/// <param name="Path">The occurrence path — "bolt", "stack/clamp.2/bolt". For a part row
/// this is <c>PartInstance.Path</c> verbatim, which is what makes tree rows and viewport
/// instances addressable by the same name as well as by the same index.</param>
/// <param name="Depth">Nesting depth, for indentation.</param>
/// <param name="Kind">Assembly header or placed part.</param>
/// <param name="InstanceIndex">Index into the viewport's instance list, or −1 for an
/// assembly header and for a part that failed to mesh (it has no instance to address).</param>
/// <param name="Part">The part this row places, or null for an assembly header.</param>
/// <param name="Failure">Why this part could not be meshed, or null.</param>
/// <param name="Ancestors">Row indices of the enclosing assembly headers, outermost
/// first — the chain <see cref="SceneTree.EffectiveVisibility"/> walks.</param>
public sealed record SceneTreeRow(
    int Index,
    string Label,
    string Path,
    int Depth,
    SceneTreeRowKind Kind,
    int InstanceIndex,
    Part? Part,
    string? Failure,
    IReadOnlyList<int> Ancestors)
{
    /// <summary>A stable key for this row's visibility state — stable across tree
    /// rebuilds (a tab revisit, a part that has since failed), which is why hidden rows
    /// are remembered by key rather than by index. Kind-qualified because an assembly
    /// header and a part row can share nothing else that distinguishes them.</summary>
    public string Key => $"{Kind}:{Path}";
}

/// <summary>
/// The model tree as a value: rows, their nesting, and the mapping from rows to the
/// viewport's instance indices.
///
/// <para><b>Effective visibility is own AND every ancestor.</b> Unchecking an assembly
/// hides its whole subtree without touching the children's own state, so re-checking it
/// restores exactly what was showing before — the desktop rule, transcribed. The result
/// is a <c>bool[]</c> indexed by INSTANCE, because that is what the viewport consumes.</para>
///
/// <para><b>Row order matches <see cref="Tab.Instances"/>.</b> Loose parts first, then
/// each assembly depth-first, and the running instance index advances exactly where that
/// walk produces an instance. A part that failed to mesh has no instance in the viewport,
/// so its row takes −1 and does <em>not</em> advance the counter — otherwise every row
/// after it would address the wrong instance and the tree would silently hide, select and
/// highlight the wrong geometry.</para>
///
/// <para>Pure: no UI, no GL, no component state. Which is the point — the correspondence
/// between rows and instances is the thing most likely to be quietly wrong, and it is
/// assertable here against <see cref="Tab.Instances"/> itself.</para>
/// </summary>
public sealed class SceneTree
{
    private readonly int[] _rowForInstance;

    private SceneTree(string tabName, IReadOnlyList<SceneTreeRow> rows, int instanceCount)
    {
        TabName = tabName;
        Rows = rows;
        InstanceCount = instanceCount;
        _rowForInstance = new int[instanceCount];
        Array.Fill(_rowForInstance, -1);
        foreach (var row in rows)
        {
            if (row.InstanceIndex >= 0)
                _rowForInstance[row.InstanceIndex] = row.Index;
        }
    }

    /// <summary>The tab this tree describes.</summary>
    public string TabName { get; }

    /// <summary>The rows, indented-flat and in instance order.</summary>
    public IReadOnlyList<SceneTreeRow> Rows { get; }

    /// <summary>How many instances the viewport will show (rows minus assembly headers
    /// and failed parts).</summary>
    public int InstanceCount { get; }

    /// <summary>The empty tree, for a viewport with no tab.</summary>
    public static SceneTree Empty { get; } = new("", [], 0);

    /// <summary>
    /// Builds the tree for one tab.
    /// </summary>
    /// <param name="tab">The tab to walk.</param>
    /// <param name="failed">Parts that could not be meshed, mapped to the reason. They
    /// get a row (so the failure is visible and named) but no instance index.</param>
    public static SceneTree Build(Tab tab, IReadOnlyDictionary<Part, string>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var rows = new List<SceneTreeRow>();
        int next = 0;

        // The walk IS Tab.Instances(): loose parts first, then each assembly depth-first.
        foreach (var part in tab.Parts)
            AddPart(rows, failed, part.Name, part.Name, part, depth: 0, ancestors: [], ref next);
        foreach (var assembly in tab.Assemblies)
            AddAssembly(rows, failed, assembly, assembly.Name, assembly.Name, depth: 0, ancestors: [], ref next);

        return new SceneTree(tab.Name, rows, next);
    }

    private static void AddAssembly(
        List<SceneTreeRow> rows, IReadOnlyDictionary<Part, string>? failed,
        Assembly assembly, string label, string path, int depth,
        IReadOnlyList<int> ancestors, ref int next)
    {
        int header = rows.Count;
        rows.Add(new SceneTreeRow(
            header, label, path, depth, SceneTreeRowKind.Assembly, -1, null, null, ancestors));

        var inner = new List<int>(ancestors) { header };
        foreach (var occurrence in assembly.Occurrences)
        {
            string childPath = $"{path}/{occurrence.Name}";
            if (occurrence.Part is { } part)
                AddPart(rows, failed, occurrence.Name, childPath, part, depth + 1, inner, ref next);
            else
                AddAssembly(rows, failed, occurrence.SubAssembly!, occurrence.Name, childPath,
                    depth + 1, inner, ref next);
        }
    }

    private static void AddPart(
        List<SceneTreeRow> rows, IReadOnlyDictionary<Part, string>? failed,
        string label, string path, Part part, int depth,
        IReadOnlyList<int> ancestors, ref int next)
    {
        string? failure = null;
        failed?.TryGetValue(part, out failure);
        // A failed part consumes no instance index: see the class remarks.
        int index = failure is null ? next++ : -1;
        rows.Add(new SceneTreeRow(
            rows.Count, label, path, depth, SceneTreeRowKind.Part, index, part, failure, ancestors));
    }

    /// <summary>The row showing instance <paramref name="instanceIndex"/>, or −1 — the
    /// viewport-to-tree half of selection sync.</summary>
    public int RowForInstance(int instanceIndex) =>
        instanceIndex >= 0 && instanceIndex < _rowForInstance.Length
            ? _rowForInstance[instanceIndex]
            : -1;

    /// <summary>
    /// Per-instance visibility: a row is shown when its own checkbox is checked AND
    /// every enclosing assembly's is. <paramref name="hidden"/> holds the
    /// <see cref="SceneTreeRow.Key"/>s of unchecked rows, so the state survives a tree
    /// rebuild and an empty set means "everything visible".
    /// </summary>
    public bool[] EffectiveVisibility(IReadOnlySet<string>? hidden = null)
    {
        var visible = new bool[InstanceCount];
        Array.Fill(visible, true);
        if (hidden is null || hidden.Count == 0)
            return visible;

        foreach (var row in Rows)
        {
            if (row.InstanceIndex < 0)
                continue;
            bool shown = !hidden.Contains(row.Key);
            foreach (int ancestor in row.Ancestors)
                shown &= !hidden.Contains(Rows[ancestor].Key);
            visible[row.InstanceIndex] = shown;
        }
        return visible;
    }
}
