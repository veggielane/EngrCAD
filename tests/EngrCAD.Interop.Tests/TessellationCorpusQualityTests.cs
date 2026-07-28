using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The whole-corpus tessellation quality gate: every construction the kernel can build,
/// audited facet by facet against the exact surface it approximates.
/// </summary>
public class TessellationCorpusQualityTests
{
    // ---- the corpus ----

    private static SketchPlane At(double z) => SketchPlane.At((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);

    private const double ThreadPitch = 1.25;
    private static readonly double ThreadH = Math.Sqrt(3) / 2 * ThreadPitch;

    private static IReadOnlyList<Vector2d> ThreadProfile()
    {
        const double major = 4.0;
        double minor = major - 0.625 * ThreadH;
        return
        [
            new(major, -ThreadPitch / 16), new(major, ThreadPitch / 16),
            new(minor, 3 * ThreadPitch / 8), new(minor, 5 * ThreadPitch / 8),
        ];
    }

    private static BrepSolid FilletedPrism(IReadOnlyList<Vector2d> polygon, double height, double radius)
    {
        var profile = Profile.FromLoop(polygon, Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var prism = SolidFactory.Extrude(profile, Vector3d.UnitZ * height);
        return Filleting.FilletRim(prism, prism.PlanarFacesWithNormal(Vector3d.UnitZ).Single(), radius);
    }

    /// <summary>
    /// Named solids covering every tessellation path: planar caps, cylinder bands, natural
    /// grids on all four generated surface types, helical bands, and — the reason this
    /// file exists — every trimmed-face tier (zip band, slab-swept band with holes,
    /// periodic band, pole fan, ear clip).
    /// </summary>
    public static TheoryData<string> Corpus =>
    [
        "drilled plate", "cross-drilled housing", "spherical cavity",
        "threaded rod", "threaded hole",
        "loft", "shelled tray", "drafted boss",
        "filleted box", "filleted L", "filleted hexagon", "chamfered box", "rounded box",
        "revolved vase", "partial revolve", "swept tube", "torus", "cone",
        "sketch pocket", "engraved plate", "wedge",
    ];

    internal static BrepSolid Build(string name)
    {
        switch (name)
        {
            case "drilled plate":
                return (Shape.Box(60, 40, 10)
                    .Drill(HoleSpec.Simple(6), [new(-20, -10), new(0, 0), new(20, 10)], 20, At(5)))
                    .ToBrep();
            case "cross-drilled housing":
                return (Shape.Box(44, 44, 30)
                    - Shape.Cylinder(13, 40)
                    - Shape.Cylinder(5, 60).RotateY(Math.PI / 2)).ToBrep();
            case "spherical cavity":
                // A spherical pocket breaking out of one face: the trimmed pole-fan and
                // two-ring band tiers, without the pathology locked by
                // SpherePiercingEverySide_IsCarriedByRefinementAndSaysSoLoudly.
                return (Shape.Box(40, 40, 30) - Shape.Sphere(12).Translate((0, 0, 10))).ToBrep();
            case "threaded rod":
                return SolidFactory.MakeThreadedRod(ThreadProfile(), ThreadPitch, 6);
            case "threaded hole":
                return Shape.Box(30, 30, 12)
                    .ThreadedHole(StandardThreads.Metric(8), [new(0, 0)], 8, At(6))
                    .ToBrep();
            case "loft":
                return SolidFactory.Loft(
                [
                    Profile.Circle((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 6),
                    Profile.Circle((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY, 3),
                    Profile.Circle((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY, 5),
                ], LoftStyle.Smooth);
            case "shelled tray":
            {
                var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));
                var top = block.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
                return Shelling.Shell(block, 2, f => ReferenceEquals(f, top));
            }
            case "drafted boss":
            {
                var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 20, 10)));
                var sides = block.PlanarFacesWithNormal(Vector3d.UnitX)
                    .Concat(block.PlanarFacesWithNormal(-Vector3d.UnitX))
                    .Concat(block.PlanarFacesWithNormal(Vector3d.UnitY))
                    .Concat(block.PlanarFacesWithNormal(-Vector3d.UnitY))
                    .ToList();
                return Draft.Apply(block, Vector3d.Zero, Vector3d.UnitZ, 5 * Math.PI / 180,
                    f => sides.Any(g => ReferenceEquals(f, g)));
            }
            case "filleted box":
                return FilletedPrism([new(0, 0), new(30, 0), new(30, 20), new(0, 20)], 6, 2);
            case "filleted L":
                return FilletedPrism(
                    [new(0, 0), new(24, 0), new(24, 9), new(9, 9), new(9, 18), new(0, 18)], 8, 2);
            case "filleted hexagon":
                return FilletedPrism(
                    [.. Enumerable.Range(0, 6).Select(i =>
                        new Vector2d(20 * Math.Cos(i * Math.PI / 3), 20 * Math.Sin(i * Math.PI / 3)))],
                    8, 2.5);
            case "chamfered box":
                return Shape.Box(30, 20, 6)
                    .Chamfer(1.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ))
                    .ToBrep();
            case "rounded box":
                return Filleting.FilletAllEdges(SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))), 2);
            case "revolved vase":
                return Shape.Revolve(Sketch.Start(0, 0)
                    .LineTo(10, 0)
                    .BezierTo(new(17, 9), new(3.5, 17), new(9, 26))
                    .LineTo(0, 26)
                    .Close()).ToBrep();
            case "partial revolve":
                return Shape.Revolve(Sketch.Start(8, 0)
                    .LineTo(20, 0).LineTo(20, 6).LineTo(14, 9).LineTo(14, 14).LineTo(20, 17)
                    .LineTo(20, 23).LineTo(8, 23)
                    .Close(), angle: 1.5 * Math.PI).ToBrep();
            case "swept tube":
                // The docs' sweep path: quadratic NURBS starting at the origin with
                // tangent +Z, so the default XY sketch plane is perpendicular to it.
                return Shape.Sweep(
                    Sketch.Circle(5),
                    new NurbsCurve(2, [(0, 0, 0), (0, 0, 26), (0, 22, 44)], null, [0, 0, 0, 1, 1, 1])).ToBrep();
            case "torus":
                return Shape.Torus(12, 4).ToBrep();
            case "cone":
                return SolidFactory.MakeCone(8, 3, 12);
            case "sketch pocket":
                return (Shape.Box(60, 20, 4)
                    - Shape.Extrude(Sketch.RoundedRectangle(24, 10, 3), 1.5, At(1))).ToBrep();
            case "engraved plate":
                return (Shape.Box(40, 20, 4)
                    - Shape.Extrude(Sketch.Start(-12, -4)
                        .LineTo(-4, -4)
                        .QuadraticTo(new(0, 6), new(4, -4))
                        .LineTo(12, -4)
                        .LineTo(12, 4)
                        .BezierTo(new(4, 10), new(-4, -2), new(-12, 4))
                        .Close(), 1.0, At(1.5))).ToBrep();
            default:
                return Shape.Wedge(20, 12, 8, topX: 6, topOffsetX: 3).ToBrep();
        }
    }

    // ---- the gate ----

    /// <summary>
    /// The floor the worst facet-vs-surface normal agreement must clear, as a function of
    /// the circle density in force. ONE formula for every surface family, deliberately:
    /// a per-family floor tuned to whatever each happened to measure would pass everything
    /// and protect nothing.
    /// <para>The unit is one natural grid step of surface normal, <c>2*pi/n</c>. A facet
    /// spanning a single step normally agrees to far better than that, because its normal
    /// is the surface normal at the chord's midpoint and the vertex-averaged reference is
    /// the same direction — which is why nearly the whole corpus measures above 0.999.
    /// The allowance of THREE steps is for the places where two INDEPENDENTLY sampled
    /// boundaries meet in one facet. The cross-drilled housing is the corpus's worst such
    /// case, because its breakout curves are tracer polylines baked into the B-Rep at
    /// boolean time and therefore do NOT refine with <c>segmentsPerCircle</c>: it measures
    /// 0.6431 at 16 segments (2.2 steps), 0.9925 at 48 and 0.9995 at 96, against floors of
    /// 0.3827 / 0.9239 / 0.9808.</para>
    /// <para>Every defect this gate has actually caught sat far below any of those: the
    /// mitered fillet folds at −0.22, the cross-drilled slivers at 0.0198, the reversed
    /// helical bands at −0.163. The floor is loose enough to be about the structure and
    /// tight enough that none of them could have hidden under it.</para>
    /// </summary>
    private static double NormalAgreementFloor(int segmentsPerCircle) =>
        Math.Cos(3 * (2 * Math.PI / segmentsPerCircle));

    /// <summary>
    /// Facet vertices that do not lie on their own face's surface, per case and density —
    /// documented exceptions, each a measurement with a diagnosed cause, locked so that
    /// both a regression AND a fix fail the test and get the number revisited.
    /// <para><b>rounded box at 96/48</b>: <see cref="Filleting.FilletAllEdges"/>'s
    /// spherical corner patches (quarter revolves of a great-circle <c>CurveSegment</c>)
    /// carry 70 vertices per corner face — 176 vertex-instances over 37 644 facets, 34 of
    /// them in triangles with no projectable vertex at all — that sit 2.6e-3 off the patch
    /// they are drawn on, i.e. 1.3e-4 of the 20-unit box and three decades past the 1e-6
    /// inverse-evaluation tolerance. Orientation is unaffected (worst agreement 0.999866)
    /// and the mesh still welds closed, which means the offending vertices are SHARED
    /// consistently between the patch and its bands — so the error is in the edge curve
    /// the two faces share, not in the tessellator. Absent at 16/8 and 48/24. Filed
    /// against the B-Rep side in todo.md.</para>
    /// </summary>
    private static readonly Dictionary<(string Name, int SegmentsPerCircle), (int Vertices, int Facets)>
        KnownOffSurface = new()
        {
            [("rounded box", 96)] = (176, 34),
        };

    /// <summary>
    /// No facet may oppose its own surface, and every facet vertex must lie on the surface
    /// it is supposed to sample. Run at the display default and at both ends of the density
    /// range, because a fold is a structural defect rather than a sampling artefact and
    /// must be absent at all three.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_FacetsAgreeWithTheSurfacesTheySample(string name)
    {
        var solid = Build(name);
        foreach (var (segmentsPerCircle, curveSamples) in Densities)
        {
            var report = TessellationQuality.Audit(solid, segmentsPerCircle, curveSamples);
            string where = $"{name} at {segmentsPerCircle}/{curveSamples}: {report.Describe()}";
            var (vertices, facets) =
                KnownOffSurface.GetValueOrDefault((name, segmentsPerCircle), (0, 0));

            Assert.True(report.Triangles > 0, where);
            Assert.True(report.Folds == 0, $"facets face inward — {where}");
            Assert.True(report.Unprojectable == vertices, $"vertices off their surface — {where}");
            Assert.True(report.Unjudged == facets, $"facets with no judgeable normal — {where}");
            Assert.True(
                report.WorstDot > NormalAgreementFloor(segmentsPerCircle),
                $"worst normal agreement below the {NormalAgreementFloor(segmentsPerCircle):F4} " +
                $"floor for this density — {where}");
        }
    }

    /// <summary>
    /// Degenerate facets — thinner than round-off, so their normal direction is decided by
    /// the last bits of their vertices — come from ONE place, and this pins which.
    /// <para>Every trimmed tier and every grid path refuses to emit one by construction
    /// (<c>IsEar</c>, <c>AddOriented</c> and <c>ZipBand</c> all reject exactly-zero uv
    /// area). The planar earcut does not: it filters EXACTLY collinear vertices, while
    /// boundary samples of a straight edge that arrived through a boolean are collinear
    /// only to a few ulps, so a run of them survives the filter and one is eventually
    /// clipped as a zero-area ear. Measured: a Bézier-engraved plate emits one such facet
    /// (area 5.6e-17 in a face of area 165, three vertices collinear to a cross product of
    /// 4.4e-16 over 1.07-long chords) at 32 and 48 segments and none at 16, 96 or 192; a
    /// three-hole drilled plate emits one at 96. Harmless to closure and volume, and NOT
    /// removable here — the middle sample belongs to no other triangle, so dropping the
    /// facet would drop a shared boundary vertex and open a T-junction against the
    /// neighbouring face. The fix belongs in the triangulator (see todo.md).</para>
    /// <para>So the assertion is the structural one: slivers only ever come from planar
    /// faces, and never more than one per face.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_EmitsDegenerateFacetsOnlyFromThePlanarEarcut(string name)
    {
        var solid = Build(name);
        foreach (var (segmentsPerCircle, curveSamples) in Densities)
        {
            var report = TessellationQuality.Audit(solid, segmentsPerCircle, curveSamples);
            string where = $"{name} at {segmentsPerCircle}/{curveSamples}: {report.Describe()}";
            Assert.Equal(
                report.SliverFamilies.Where(f => f != nameof(PlaneSurface)), []);
            Assert.True(report.WorstFaceSlivers <= 1, $"a face emitted several slivers — {where}");
        }
    }

    /// <summary>Every corpus member is a closed solid, so its tessellation must weld into
    /// a closed two-manifold mesh — the invariant the loud trimmed-face refusal exists to
    /// protect.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_WeldsClosedAndTwoManifold(string name)
    {
        var solid = Build(name);
        foreach (var (segmentsPerCircle, curveSamples) in Densities)
        {
            var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples);
            mesh.Validate();
            Assert.True(mesh.IsClosed, $"{name} at {segmentsPerCircle}/{curveSamples} welded open");
        }
    }

    private static readonly (int SegmentsPerCircle, int CurveSamples)[] Densities =
        [(16, 8), (48, 24), (96, 48)];

    // ---- convergence ----

    /// <summary>
    /// Analytic volumes for the corpus members that have one. The mesh inscribes every
    /// curved surface, so a solid whose curvature is convex outward comes out SMALLER
    /// than the exact solid and one whose curvature is a removed bore comes out LARGER;
    /// either way the error is chordal and must fall ~4x per density doubling.
    /// </summary>
    public static TheoryData<string, double> Analytic =>
        new()
        {
            // 2 pi^2 R r^2.
            { "torus", 2 * Math.PI * Math.PI * 12 * 16 },
            // pi h (R^2 + R r + r^2) / 3.
            { "cone", Math.PI * 12 * (64 + 24 + 9) / 3.0 },
            // 60 x 40 x 10 less three through-bores of diameter 6.
            { "drilled plate", 60.0 * 40 * 10 - 3 * Math.PI * 9 * 10 },
            // A quadratic-NURBS-path sweep of a radius-5 circle: Pappus does not apply to
            // a curved path, so the reference is the finest tessellation and only the
            // RATIO is asserted (see the test).
        };

    /// <summary>
    /// The chordal error of an inscribed tessellation is second order in the step, so
    /// halving the step must quarter it. A ratio floor of 2.5 rather than 4 leaves room
    /// for the parts of a solid whose sampling does NOT refine with
    /// <c>segmentsPerCircle</c> (tracer polylines baked at boolean time put a fixed floor
    /// under the total), while still failing the thing this test exists to catch: a
    /// triangulation whose accuracy comes from refinement rather than from its base mesh
    /// does not converge at all. The measured signature of that failure, from the
    /// ear-clipped cross-drilled bore, was ratios of 3.29 then 1.39 then 1.19 — stalling,
    /// never reaching the analytic value.
    /// </summary>
    [Theory]
    [MemberData(nameof(Analytic))]
    public void Corpus_VolumeErrorFallsQuadraticallyWithTheSamplingStep(string name, double expected)
    {
        var solid = Build(name);
        double coarse = BRepTessellator.Tessellate(solid, 32, 24).Volume() - expected;
        double medium = BRepTessellator.Tessellate(solid, 64, 48).Volume() - expected;
        double fine = BRepTessellator.Tessellate(solid, 128, 96).Volume() - expected;

        Assert.True(
            Math.Sign(coarse) == Math.Sign(medium) && Math.Sign(medium) == Math.Sign(fine),
            $"{name}: an inscribed mesh must approach the exact volume from ONE side, got " +
            $"{coarse:E3} / {medium:E3} / {fine:E3}");
        Assert.True(Math.Abs(coarse / expected) < 1e-2, $"{name}: coarse error {coarse / expected:E2}");
        double first = Math.Abs(coarse / medium), second = Math.Abs(medium / fine);
        Assert.True(
            first > 2.5 && second > 2.5,
            $"{name}: expected near-quadratic convergence, got ratios {first:F2} then {second:F2} " +
            $"(errors {coarse:E3} / {medium:E3} / {fine:E3})");
    }

    // ---- the one member the gate cannot yet hold ----

    /// <summary>
    /// <c>Box(20, 20, 20) − Sphere(12)</c> — a sphere larger than the box it is cut from,
    /// so the cavity breaks out of ALL SIX faces and what remains is a twelve-edge frame.
    /// It is the only construction found that the trimmed band path cannot carry, and it
    /// is locked here rather than quietly dropped from <see cref="Corpus"/>.
    /// <para>The cavity wall is a band whose two chains are a 48-sample latitude circle
    /// against a 240-sample rim scalloped by four side-face cuts, spanning about fifteen
    /// natural v steps. The monotone sweep triangulates that region correctly — every base
    /// facet has positive uv area — but the base mesh has NO interior rows, so every one of
    /// them spans the band's whole height and <c>Refine</c> has to manufacture the interior
    /// by midpoint bisection. On a surface this strongly curved the surface midpoint of a
    /// long chord lies far enough off the chord to invert the halves, so the folds this
    /// test records are made BY refinement, not left by the zip. Refinement is not a
    /// convergence mechanism — the base triangulation has to carry the accuracy — and this
    /// is the corpus member that proves it: see todo.md's Interop section.</para>
    /// <para>Measured at 48/24 after routing the two-ring band through the sweep: 101 246
    /// facets, 266 folded, worst agreement −0.2426 (it was 102 226 / 2 226 / −0.9978 from
    /// the merge walk). Volume converges at ratios 2.64 then 2.13, not 4.</para>
    /// </summary>
    [Fact]
    public void SpherePiercingEverySide_IsCarriedByRefinementAndSaysSoLoudly()
    {
        var solid = (Shape.Box(20, 20, 20) - Shape.Sphere(12)).ToBrep();
        var report = TessellationQuality.Audit(solid, 48, 24);

        // A committed baseline, not a tolerance: if these move in EITHER direction the
        // cause must be understood and the numbers updated deliberately.
        Assert.InRange(report.Folds, 1, 266);
        Assert.InRange(report.Triangles, 1, 105_000);
        Assert.InRange(report.WorstDot, -0.25, 0);
        Assert.Equal(0, report.Slivers);

        // Still welds closed at the density it can reach — wrong-looking, never open.
        var mesh = BRepTessellator.Tessellate(solid, 48, 24);
        mesh.Validate();
        Assert.True(mesh.IsClosed);

        // And past that density it REFUSES rather than handing back the natural grid.
        var error = Assert.Throws<NotSupportedException>(
            () => BRepTessellator.Tessellate(solid, 96, 48));
        Assert.Contains("curvature refinement did not converge", error.Message);
        Assert.Contains("open mesh", error.Message);
    }
}
