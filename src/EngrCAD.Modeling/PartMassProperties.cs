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
/// <para><b>Density comes from the part's <see cref="Part.Material"/>, and the explicit
/// argument is an override.</b> Every entry point takes a nullable density: null (the
/// default) reads <c>part.Material?.Density</c>, and falls back to <b>1</b> for a part with
/// no material, which makes the returned mass a copy of the volume — the honest answer when
/// nobody has said what the part is made of. Passing a number overrides both, for a part
/// whose material is not modelled.</para>
/// <para><b>The unit is the repository's one convention, stated in
/// <see cref="ModelUnits"/>: mm / N / MPa / tonne.</b> A density is in tonne/mm³ (structural
/// steel 7.85e-9), so <see cref="MassProperties.Mass"/> comes back in <b>tonnes</b> and
/// <see cref="ModelUnits.MassToGrams"/> / <see cref="ModelUnits.MassToKilograms"/> are how a
/// report prints it. This used to be the one place in the repository that documented kg/mm³,
/// a factor of 1000 away from the simulation catalogue's tonne/mm³ — the same
/// <see cref="Material"/> now feeds both, so the two can no longer disagree.</para>
/// </remarks>
public static class PartMassProperties
{
    /// <summary>
    /// This part's mass properties <b>in world coordinates</b> — its own geometry posed by
    /// <see cref="Part.Transform"/>.
    /// </summary>
    /// <param name="part">The part to measure.</param>
    /// <param name="density">Mass per unit volume in tonne/mm³; null takes the part's
    /// <see cref="Part.Material"/>, or 1 when it has none.</param>
    /// <param name="options">Accuracy for the B-Rep route; ignored for mesh/SDF parts,
    /// which measure the display mesh they already have.</param>
    public static MassProperties MassProperties(
        this Part part, double? density = null, BrepMassPropertyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        return Posed(Local(part, DensityOf(part, density), options), part.Transform);
    }

    /// <summary>
    /// One assembly occurrence's mass properties in world coordinates.
    /// <see cref="PartInstance.World"/> already carries the part's own transform, so it
    /// replaces rather than composes with it.
    /// </summary>
    public static MassProperties MassProperties(
        this PartInstance instance, double? density = null, BrepMassPropertyOptions? options = null) =>
        Posed(Local(instance.Part, DensityOf(instance.Part, density), options), instance.World);

    /// <summary>
    /// Combined mass properties of a set of occurrences — the assembly figure. Pass
    /// <see cref="Assembly.Flatten()"/>, <see cref="Tab.Instances"/> or
    /// <see cref="Scene.AllInstances"/>; every occurrence counts, so N copies of one part
    /// contribute N times.
    /// <para>With materials on the parts this is the whole call:
    /// <c>scene.AllInstances.MassProperties()</c>.</para>
    /// </summary>
    /// <param name="instances">The occurrences to add up.</param>
    /// <param name="density">Per-part density override, so an assembly whose materials are
    /// not modelled still comes out right; null (the default) reads each part's own
    /// <see cref="Part.Material"/>. The result's <c>Density</c> is then the bulk density
    /// (total mass / total volume).</param>
    /// <param name="options">Accuracy for the B-Rep route.</param>
    public static MassProperties MassProperties(
        this IEnumerable<PartInstance> instances,
        Func<Part, double>? density = null,
        BrepMassPropertyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return Mesh.MassProperties.Combine(
            instances.Select(i => i.MassProperties(density?.Invoke(i.Part), options)));
    }

    /// <summary>
    /// This part's mass in <b>grams</b> — the readable unit for a part that weighs tens of
    /// them — or null when the part states no material, since a mass nobody has given a
    /// density for is not a small number, it is an unknown one.
    /// </summary>
    public static double? MassGrams(this Part part, BrepMassPropertyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Material is not { } material)
            return null;
        return ModelUnits.MassToGrams(part.MassProperties(material.Density, options).Mass);
    }

    /// <summary>
    /// The mass in grams of the part's <b>display mesh</b>, or null when the part states no
    /// material, has no mesh yet, or has an open one (a mass, like a volume, is not defined
    /// there).
    ///
    /// <para>This is the properties-panel number, and it is deliberately a different call
    /// from <see cref="MassGrams"/>: it reads only the mesh a viewer already has, so it can
    /// never lower a B-Rep or tessellate on the UI thread, and it is consistent by
    /// construction with the Volume the same panel prints one row above. Use
    /// <see cref="MassGrams"/> — the exact route through <c>BrepMassProperties</c> — when the
    /// number is the answer rather than a readout.</para>
    /// </summary>
    public static double? DisplayMassGrams(this Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Material is not { } material || !part.HasMesh)
            return null;
        var mesh = part.GetMesh();
        return mesh.IsClosed ? ModelUnits.MassToGrams(mesh.Volume() * material.Density) : null;
    }

    /// <summary>The density a call should use: the explicit override, else the part's
    /// material, else 1 (mass reads as volume).</summary>
    private static double DensityOf(Part part, double? density) =>
        density ?? part.Material?.Density ?? 1.0;

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
