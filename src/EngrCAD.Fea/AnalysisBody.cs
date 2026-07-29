using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// One body of a multi-material analysis: a closed surface, what it is made of, and a
/// name to say it by. <b>This is the seam between the document model and the simulation
/// layer</b>, and it exists so that a region id never has to be restated.
///
/// <para><b>The problem it solves.</b> <see cref="TetMesher"/> tags each tetrahedron with
/// the INDEX of the body it filled, and <see cref="StructuralModel.SetMaterial"/> assigns
/// a material to a region id — a correspondence that is correct only as long as two
/// separately written lists stay in the same order. Passing ONE list of bodies to both
/// makes it a fact rather than a convention: <c>StructuralModel.For(mesh, bodies)</c>
/// reads the same list <c>TetMesher.Mesh(bodies)</c> read, so the materials cannot drift
/// from the geometry they belong to.</para>
///
/// <para><b>Why the pairing is stated here and not on <c>Part</c>.</b>
/// <c>EngrCAD.Fea</c> is a leaf — it depends on Core and Mesh and nothing depends on it,
/// which is what keeps a simulation stack out of every modelling consumer — so it cannot
/// see the document model's <c>Part</c>. What the two layers genuinely share is
/// <see cref="Material"/> (in Core, for exactly this reason) and
/// <see cref="HalfEdgeMesh"/>, and a body is those two together. From a document, one
/// expression builds the list:</para>
///
/// <code>
/// var bodies = parts
///     .Select(p =&gt; new AnalysisBody(p.GetMesh(), p.Material, p.Name))
///     .ToList();
/// var tets  = TetMesher.Mesh(bodies, options);
/// var model = StructuralModel.For(tets, bodies);
/// </code>
///
/// <para><b>A null material is legal here and refused at the point of use</b>, naming the
/// body — the same doctrine <see cref="Material"/>'s optional analysis properties follow.
/// Meshing needs no material at all, so a body may be built from a
/// <c>Part.Material</c> that nobody has stated yet and only the model complains.</para>
/// </summary>
/// <param name="Surface">The body's closed, outward-wound boundary mesh.</param>
/// <param name="Material">What it is made of, or null when nothing has said. Refused,
/// by name, by whichever analysis needs a property it does not carry.</param>
/// <param name="Name">A display name for refusals and reports; null falls back to the
/// body's index and, failing that, to its material's name.</param>
public sealed record AnalysisBody(HalfEdgeMesh Surface, Material? Material = null, string? Name = null)
{
    /// <summary>How a refusal or a report names this body: its own name when it has one,
    /// else its region index.</summary>
    public string Label(int region) =>
        Name is { Length: > 0 } name ? $"'{name}' (region {region})" : $"body {region}";
}

/// <summary>
/// The checks <see cref="StructuralModel.For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/>
/// and <see cref="ThermalModel.For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/> share, so
/// the two cannot disagree about what a well-formed body list is. Written once for the
/// same reason <c>FeaGuards</c> is: a restated precondition is a precondition that drifts.
/// </summary>
internal static class AnalysisBodies
{
    /// <summary>
    /// Refuses, before any material is attached, anything that would make the
    /// body-to-region correspondence a lie: a body with no material, a mesh region no body
    /// declares, and a declared body that contributed no elements. All three are modelling
    /// mistakes with the same cause — a list that does not match the mesh it was meshed
    /// from — and all three are cheap to name here and expensive to diagnose later.
    /// </summary>
    public static void Require(AnalysisMesh mesh, IReadOnlyList<AnalysisBody> bodies, string analysis)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count == 0)
            throw new FeaException($"A {analysis} model needs at least one body.");

        var used = new bool[bodies.Count];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            int region = mesh.RegionOf(e);
            if (region < 0 || region >= bodies.Count)
            {
                throw new FeaException(
                    $"The mesh carries region {region}, but only {bodies.Count} "
                    + $"{(bodies.Count == 1 ? "body was" : "bodies were")} supplied. Region ids ARE "
                    + "body indices, so pass the same list to TetMesher.Mesh and to this call - "
                    + "that is the whole point of AnalysisBody.");
            }
            used[region] = true;
        }

        for (int b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b] ?? throw new FeaException($"Body {b} is null.");
            if (!used[b])
            {
                throw new FeaException(
                    $"{body.Label(b)} contributed no elements: the mesh has regions "
                    + $"[{string.Join(", ", mesh.Regions)}] and this body's index is not among them. "
                    + "Either the body list is not the one the mesh was built from, or the body "
                    + "was swallowed by an overlapping neighbour.");
            }
            if (body.Material is null)
            {
                throw new FeaException(
                    $"{body.Label(b)} states no material, and a {analysis} model needs one for "
                    + "every region. A null Material is legal on an AnalysisBody (meshing does "
                    + "not need it, and it may have come straight from an unstated Part.Material) "
                    + "and is refused here, where the requirement is.");
            }
        }
    }
}
