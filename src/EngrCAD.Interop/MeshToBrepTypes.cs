using EngrCAD.BRep;

namespace EngrCAD.Interop;

/// <summary>Which analytic surface a reconstructed region was recognised as.</summary>
public enum ReconstructedSurfaceKind
{
    /// <summary>A flat region — a <see cref="PlaneSurface"/>.</summary>
    Plane,
    /// <summary>A cylindrical region — a <see cref="CylinderSurface"/>.</summary>
    Cylinder,
    /// <summary>A spherical region — a <see cref="SphereSurface"/>.</summary>
    Sphere,
    /// <summary>No plane, cylinder or sphere fit the region within tolerance. v1 does not
    /// fit cones, tori or freeform surfaces, so such a region is reported rather than
    /// forced onto a surface it is not.</summary>
    Unfitted,
}

/// <summary>Controls for <see cref="MeshToBrep.Reconstruct"/>.</summary>
public sealed class MeshToBrepOptions
{
    /// <summary>
    /// The dihedral (face-normal) angle, in degrees, above which an edge is a sharp crease
    /// that separates two surfaces. The default (35°) treats a tessellation of ≥ 12 segments
    /// per circle as smooth while splitting genuine edges. Feature detection reads the MESH,
    /// not the surface — a very coarse tessellation whose facet dihedral exceeds this angle
    /// would be over-segmented — so the face count is the honest check on segmentation.
    /// </summary>
    public double FeatureAngleDegrees { get; init; } = 35.0;

    /// <summary>
    /// A region is accepted as a primitive when its worst residual is under this fraction of
    /// the mesh's bounding-box diagonal. A tessellation of exact CAD geometry has its
    /// vertices ON the surface, so the true residual is machine-epsilon-small and this
    /// bound is generous; a region exceeding it is reported <see cref="ReconstructedSurfaceKind.Unfitted"/>
    /// (which is how noisy / scan input surfaces, since it is not a tessellation of a
    /// primitive).
    /// </summary>
    public double FitTolerance { get; init; } = 1e-3;
}

/// <summary>What one region was recognised as, and how well it fit.</summary>
/// <param name="Index">The region's id.</param>
/// <param name="Kind">The analytic surface recognised.</param>
/// <param name="Residual">The worst distance from a region vertex to the fitted surface, in
/// model units (the chord error for a clean tessellation; large for a bad fit).</param>
/// <param name="TriangleCount">How many mesh triangles the region covers.</param>
/// <param name="Surface">The fitted surface, or null when <see cref="ReconstructedSurfaceKind.Unfitted"/>.</param>
public sealed record ReconstructedRegion(
    int Index,
    ReconstructedSurfaceKind Kind,
    double Residual,
    int TriangleCount,
    Surface? Surface);

/// <summary>The segmentation-and-fitting report, always produced even when assembly fails.</summary>
/// <param name="RegionCount">Distinct surfaces the mesh was segmented into — the FACE COUNT
/// the reconstruction is judged by (about seven for a drilled plate, not five thousand).</param>
/// <param name="FaceCount">Alias of <see cref="RegionCount"/>: one B-Rep face per region.</param>
/// <param name="Regions">Per-region fit results.</param>
/// <param name="Notes">Diagnostics from segmentation, fitting and assembly.</param>
public sealed record MeshToBrepReport(
    int RegionCount,
    int FaceCount,
    IReadOnlyList<ReconstructedRegion> Regions,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when every region was recognised as an analytic primitive.</summary>
    public bool AllFitted => Regions.All(r => r.Kind != ReconstructedSurfaceKind.Unfitted);
}

/// <summary>The outcome of <see cref="MeshToBrep.Reconstruct"/>.</summary>
/// <param name="Solid">The reconstructed parametric B-Rep, or null when a region did not fit
/// or the trimmed faces could not be assembled into a valid solid.</param>
/// <param name="Report">The segmentation-and-fitting report (always present).</param>
/// <param name="Succeeded">True when <see cref="Solid"/> is a valid reconstructed solid.</param>
/// <param name="FailureReason">Why no solid was produced, or null on success.</param>
public sealed record MeshToBrepResult(
    BrepSolid? Solid,
    MeshToBrepReport Report,
    bool Succeeded,
    string? FailureReason);
