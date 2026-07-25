using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// Mass properties (volume, area, centre of mass, inertia) of the document model, on top of
/// <c>BrepMassProperties</c> / <c>MeshMassProperties</c>.
/// </summary>
/// <remarks>
/// <para>These reuse the caches a <see cref="Part"/> already fills rather than lowering or
/// meshing again: a B-Rep-representable part measures the <b>one</b> solid
/// <see cref="Part.TryGetSolid"/> holds — the same solid the display mesh, the feature-edge
/// overlay, the annotations and STEP export all come from — and an SDF or mesh part
/// measures its cached display mesh. Calling this after the scene has been shown therefore
/// costs a tessellation, not a compile.</para>
/// <para>Density is the caller's: kg/mm³ for a model in millimetres (steel 7.85e-6,
/// aluminium 2.7e-6), or leave it at 1 to read mass as volume.</para>
/// </remarks>
public static class PartMassProperties
{
    /// <summary>
    /// This part's mass properties <b>in world coordinates</b> — its own geometry posed by
    /// <see cref="Part.Transform"/>.
    /// </summary>
    /// <param name="part">The part to measure.</param>
    /// <param name="density">Mass per unit volume in the caller's units.</param>
    /// <param name="options">Accuracy for the B-Rep route; ignored for mesh/SDF parts,
    /// which measure the display mesh they already have.</param>
    public static MassProperties MassProperties(
        this Part part, double density = 1.0, BrepMassPropertyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        return Posed(Local(part, density, options), part.Transform);
    }

    /// <summary>
    /// One assembly occurrence's mass properties in world coordinates.
    /// <see cref="PartInstance.World"/> already carries the part's own transform, so it
    /// replaces rather than composes with it.
    /// </summary>
    public static MassProperties MassProperties(
        this PartInstance instance, double density = 1.0, BrepMassPropertyOptions? options = null) =>
        Posed(Local(instance.Part, density, options), instance.World);

    /// <summary>
    /// Combined mass properties of a set of occurrences — the assembly figure. Pass
    /// <see cref="Assembly.Flatten()"/>, <see cref="Tab.Instances"/> or
    /// <see cref="Scene.AllInstances"/>; every occurrence counts, so N copies of one part
    /// contribute N times.
    /// </summary>
    /// <param name="instances">The occurrences to add up.</param>
    /// <param name="density">Per-part density, so an assembly of mixed materials comes out
    /// right; null means 1 for everything. The result's <c>Density</c> is then the bulk
    /// density (total mass / total volume).</param>
    /// <param name="options">Accuracy for the B-Rep route.</param>
    public static MassProperties MassProperties(
        this IEnumerable<PartInstance> instances,
        Func<Part, double>? density = null,
        BrepMassPropertyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return Mesh.MassProperties.Combine(
            instances.Select(i => i.MassProperties(density?.Invoke(i.Part) ?? 1.0, options)));
    }

    /// <summary>The part's geometry measured in its own coordinates, before any pose.</summary>
    private static MassProperties Local(Part part, double density, BrepMassPropertyOptions? options)
    {
        // The exact route first: TryGetSolid is cached, so this never re-lowers a Shape.
        if (part.TryGetSolid() is { } solid)
            return BrepMassProperties.Compute(solid, density, options);

        // SDF and mesh parts: the display mesh, also cached. requireClosed is relaxed for
        // the same reason the B-Rep route relaxes it — a polygonization or a boolean seam
        // can be geometrically watertight while carrying T-junctions.
        return MeshMassProperties.Compute(part.GetMesh(), density, requireClosed: false);
    }

    private static MassProperties Posed(in MassProperties local, in Matrix4d transform) =>
        // Exact-equality shortcut on the common case; Transformed is exact for the identity
        // anyway, this just avoids the similarity check on every untransformed part.
        transform == Matrix4d.Identity ? local : local.Transformed(transform);
}
