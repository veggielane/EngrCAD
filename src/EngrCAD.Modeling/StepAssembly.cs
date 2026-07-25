using EngrCAD.BRep;

namespace EngrCAD.Modeling;

/// <summary>
/// The result of turning a flattening into STEP instances: what will be written, and
/// what could not be (a part with no exact B-Rep — an SDF, a mesh, a lowering that
/// failed). Both halves are returned so a caller can report the skips instead of
/// silently shipping an incomplete assembly.
/// </summary>
/// <param name="Instances">The placed solids, in flattening order.</param>
/// <param name="Skipped">Parts with no exact solid, each with the occurrence paths lost
/// with them.</param>
public sealed record StepAssemblyPlan(
    IReadOnlyList<StepInstance> Instances,
    IReadOnlyList<(Part Part, IReadOnlyList<string> Paths)> Skipped)
{
    /// <summary>Distinct products (solids) the file will contain.</summary>
    public int ProductCount => Instances.Select(i => i.Solid).Distinct().Count();
}

/// <summary>
/// STEP assembly export from the document model: builds
/// <see cref="StepInstance"/>s out of the SAME <see cref="PartInstance"/> flattening the
/// viewer renders, so what is exported is what is shown — including exploded poses if
/// you flatten with a factor.
///
/// <code>
/// StepAssembly.WriteFile(tab, "gearbox.step");
/// </code>
///
/// <para>Each part's solid comes from <see cref="Part.TryGetSolid"/>, the cache the
/// display mesh, the edge overlay and the annotations already share — so exporting an
/// assembly lowers nothing a second time, and N placements of one part write ONE
/// product.</para>
/// </summary>
public static class StepAssembly
{
    /// <summary>Plans the export for any instance list — which solids, which poses, and
    /// what has no exact form. Does the B-Rep lowering (cached per part), so keep it off
    /// a render thread.</summary>
    public static StepAssemblyPlan Plan(IEnumerable<PartInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var placed = new List<StepInstance>();
        var skipped = new Dictionary<Part, List<string>>();
        var order = new List<Part>();
        foreach (var instance in instances)
        {
            if (instance.Part.TryGetSolid() is { } solid)
            {
                placed.Add(new StepInstance(instance.Part.Name, instance.Path, solid, instance.World));
                continue;
            }
            if (!skipped.TryGetValue(instance.Part, out var paths))
            {
                skipped[instance.Part] = paths = [];
                order.Add(instance.Part);
            }
            paths.Add(instance.Path);
        }
        return new StepAssemblyPlan(
            placed, [.. order.Select(part => (part, (IReadOnlyList<string>)skipped[part]))]);
    }

    /// <inheritdoc cref="Plan(IEnumerable{PartInstance})"/>
    public static StepAssemblyPlan Plan(Assembly assembly, double explode = 0) =>
        Plan(Required(assembly).Flatten(explode));

    /// <inheritdoc cref="Plan(IEnumerable{PartInstance})"/>
    public static StepAssemblyPlan Plan(Tab tab, double explode = 0) =>
        Plan(Required(tab).Instances(explode));

    /// <inheritdoc cref="Plan(IEnumerable{PartInstance})"/>
    public static StepAssemblyPlan Plan(Scene scene, double explode = 0) =>
        Plan(Required(scene).Instances(explode));

    /// <summary>The STEP assembly text for an instance list.</summary>
    public static string Write(IEnumerable<PartInstance> instances, string name = "EngrCAD assembly")
    {
        var plan = Plan(instances);
        if (plan.Instances.Count == 0)
            throw new InvalidOperationException(
                "Nothing to export: no part in this assembly has an exact B-Rep " +
                "(STEP is a B-Rep format — mesh and SDF parts have no exact form to write).");
        return StepWriter.WriteAssembly(plan.Instances, name);
    }

    /// <summary>Writes an assembly's STEP file; returns the plan so the caller can report
    /// what was skipped.</summary>
    public static StepAssemblyPlan WriteFile(Assembly assembly, string path, double explode = 0) =>
        WriteFile(Required(assembly).Flatten(explode), path, assembly.Name);

    /// <summary>Writes a tab's STEP file (loose parts and assemblies alike).</summary>
    public static StepAssemblyPlan WriteFile(Tab tab, string path, double explode = 0) =>
        WriteFile(Required(tab).Instances(explode), path, tab.Name);

    /// <summary>Writes a whole scene's STEP file.</summary>
    public static StepAssemblyPlan WriteFile(Scene scene, string path, string name = "EngrCAD assembly",
        double explode = 0) =>
        WriteFile(Required(scene).Instances(explode), path, name);

    /// <summary>Writes any instance list, returning what was written and what was not.</summary>
    public static StepAssemblyPlan WriteFile(
        IEnumerable<PartInstance> instances, string path, string name = "EngrCAD assembly")
    {
        var plan = Plan(instances);
        if (plan.Instances.Count == 0)
            throw new InvalidOperationException(
                "Nothing to export: no part in this assembly has an exact B-Rep " +
                "(STEP is a B-Rep format — mesh and SDF parts have no exact form to write).");
        StepWriter.WriteAssemblyFile(plan.Instances, path, name);
        return plan;
    }

    private static T Required<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
