using System.Diagnostics;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// One-off measurement for the todo hypothesis "MaxElementSize may not bound element size in
/// the presence of a fine curved feature", on the exact fixture that raised it,
/// <c>Box(60, 20, 8) − Cylinder(4, 40)</c>. Inert unless <c>ENGRCAD_BENCH</c> is set.
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Fea.Tests -c Release --filter FullyQualifiedName~MaxElementSizeMeasurement -l "console;verbosity=detailed"
/// </code>
/// The experiment is the one the todo names: HOLD THE BORE FIXED (one surface tessellation,
/// reused across every point) and sweep <c>MaxElementSize</c>. If the count scales as h⁻³ the
/// size parameter is in control; if it plateaus, the bore-wall facet size is.
///
/// <para><b>Measured</b> (win-x64, i9-9900K, Release; 112-triangle default tessellation, Ø8
/// bore, <c>RefineQuality = true</c>):</para>
/// <code>
///   h    elements   bSteiner   qSteiner    count·h³
///  20.0    83 897      7 677          0    671 M
///  14.0    68 550     10 597          0    188 M
///  10.0   142 911     29 966          0    143 M
///   8.0    90 537     38 508         98     46 M
///   6.0   101 557     43 044        443     22 M
///   4.0   340 508    151 968      1 424     22 M
/// </code>
/// <para><b>Finding: MaxElementSize does not bound the element COUNT of a coarse request; the
/// bore-wall facet size floors it.</b> A size-bounded mesh has <c>count·h³</c> flat (count ∝
/// h⁻³); here it FALLS 671 M → 22 M and the count is non-monotone (84 k → 68 k → 143 k → 90 k →
/// 102 k) across h = 20…6 where a size-bounded mesher would answer that 3.3× drop in h with a
/// ~37× RISE in count. At the coarsest request (h = 20) the mesh is already 84 k elements — 66×
/// a uniform edge-20 mesh (~1 300) — because <c>RefineBoundaryToSize</c> only SPLITS facets
/// larger than the target and never coarsens the finely tessellated Ø8 bore wall (~0.5 mm
/// facets). Interior quality refinement (<c>qSteiner</c>) is 0 for h ≥ 10 and negligible even at
/// h = 6, so the count is BOUNDARY refinement, whose density the surface sets. Only below the
/// far-field facet scale (h ≲ 6) does <c>count·h³</c> flatten and MaxElementSize add elements in
/// the expected h⁻³ way. So MaxElementSize is a MINIMUM element size; it cannot make a mesh
/// coarser than its surface tessellation. The honest small change is a report field —
/// <see cref="TetMeshDiagnostics.MinBoundaryFacetSize"/> — that names the floor.</para>
/// </summary>
public class MaxElementSizeMeasurement(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private void Row(HalfEdgeMesh surface, double h)
    {
        var sw = Stopwatch.StartNew();
        var tets = TetMesher.Mesh(surface,
            new TetMeshOptions { RefineQuality = true, MaxElementSize = h }, out var diag);
        sw.Stop();
        // count·h³ is flat when the size parameter genuinely bounds (count ∝ h⁻³) and rises
        // steeply toward small h when a fixed facet floor dominates instead.
        output.WriteLine(
            $"{h,5:0.0} {tets.TetCount,10} {diag.BoundarySteinerPoints,9} " +
            $"{diag.QualitySteinerPoints,9} {sw.ElapsedMilliseconds,8} {tets.TetCount * h * h * h,13:0}");
    }

    [Fact]
    public void SweepA_DefaultTessellation_SweepMaxElementSize()
    {
        if (!Enabled) return;
        // The construction the todo names, at the DEFAULT tessellation the failing docs
        // snippet used (part.GetMesh(): 32 seg/circle) — the Ø8 bore is genuinely fine here.
        var part = new Part("bored", Shape.Box(60, 20, 8).Subtract(Shape.Cylinder(4, 40)));
        var surface = part.GetMesh();               // built ONCE, reused across every h below
        output.WriteLine($"=== default tessellation, {surface.FaceCount} surface triangles (bore Ø8) ===");
        output.WriteLine("    h   elements   bSteiner   qSteiner      ms       count*h^3");
        foreach (double h in new[] { 20.0, 14.0, 10.0, 8.0, 6.0 })
            Row(surface, h);
    }

    [Fact]
    public void SweepA_h4_TheExpensiveOne()
    {
        if (!Enabled) return;
        var part = new Part("bored", Shape.Box(60, 20, 8).Subtract(Shape.Cylinder(4, 40)));
        var surface = part.GetMesh();
        output.WriteLine($"=== the failing snippet's own point: h = 4 on {surface.FaceCount} triangles ===");
        output.WriteLine("    h   elements   bSteiner   qSteiner      ms       count*h^3");
        Row(surface, 4.0);
    }
}
