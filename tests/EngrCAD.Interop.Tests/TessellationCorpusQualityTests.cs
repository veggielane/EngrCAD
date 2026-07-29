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
    /// periodic band with interior rows, pole fan, ear clip).
    /// </summary>
    public static TheoryData<string> Corpus =>
    [
        "drilled plate", "cross-drilled housing", "spherical cavity", "drilled sphere",
        "threaded rod", "threaded hole",
        "loft", "shelled tray", "shelled cup", "shelled cone", "shelled elbow",
        "drafted boss", "drafted cylinder",
        "filleted box", "filleted L", "filleted hexagon", "chamfered box", "variable chamfer",
        "variable fillet",
        "rounded box", "rounded tetrahedron", "partial fillet run",
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
                // SpherePiercingEverySide_HasNoFoldsAndABoundedResidual.
                return (Shape.Box(40, 40, 30) - Shape.Sphere(12).Translate((0, 0, 10))).ToBrep();
            case "drilled sphere":
                // A cylinder drilled straight through a sphere: the sphere face becomes a
                // two-ring periodic band spanning nearly pole to pole — the shape that
                // used to be CARRIED by refinement (43 948 facets, 12 folds, worst
                // −0.2022 at 32/24; refused outright at 128/96) and now takes the
                // natural grid's interior rows in its base triangulation.
                return (Shape.Sphere(10) - Shape.Cylinder(3, 40)).ToBrep();
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
            case "shelled cup":
            {
                // Curved-face shelling: a cylinder hollowed through its top. The cavity wall
                // is a REVERSED cylinder band, which is the tessellator's flip path over a
                // loop-driven carrier.
                var cylinder = SolidFactory.MakeCylinder(8, 20);
                var top = cylinder.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
                return Shelling.Shell(cylinder, 1.5, f => ReferenceEquals(f, top));
            }
            case "shelled cone":
            {
                // The case that forced the offset generator to be RE-TRIMMED: an inward offset
                // moves a cone's rims axially as well as radially, so the inner band's
                // domain-driven grid has to be shortened to its new loops or the trimmed
                // tessellator refuses the face outright.
                var cone = SolidFactory.MakeCone(12, 5, 16);
                var cap = cone.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
                return Shelling.Shell(cone, 1.5, f => ReferenceEquals(f, cap));
            }
            case "shelled elbow":
            {
                // A quarter-turn pipe: two TANGENT torus bands whose offsets are halves of one
                // concentric torus, opened at both ends into a genus-1 tube.
                var tubeCentre = new Vector3d(20, 0, 0);
                var outerArc = NurbsCurve.Arc(
                    tubeCentre, Vector3d.UnitX, Vector3d.UnitZ, 5, -Math.PI / 2, Math.PI / 2);
                var innerArc = NurbsCurve.Arc(
                    tubeCentre, Vector3d.UnitX, Vector3d.UnitZ, 5, Math.PI / 2, 3 * Math.PI / 2);
                var elbow = SolidFactory.Revolve(
                    new Profile([outerArc, innerArc]), Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);
                var caps = elbow.Faces.Where(f => f.Surface is PlaneSurface).ToList();
                return Shelling.Shell(elbow, 1, caps.Contains);
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
            case "drafted cylinder":
            {
                // Curved-face draft: a cylinder's band tapers to an exact cone, which is a
                // RevolvedSurface whose generator was re-trimmed to the drafted rims.
                var cylinder = SolidFactory.MakeCylinder(10, 20);
                var band = cylinder.Faces.Single(f => !f.IsPlanar(out _, out _));
                return Draft.Apply(cylinder, Vector3d.Zero, Vector3d.UnitZ, 8 * Math.PI / 180,
                    f => ReferenceEquals(f, band));
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
            case "variable fillet":
                // The same slot rim under a RADIUS law: ruled-skin bands on the straights
                // (whose cross-sections are true circles of the interpolated radius) and
                // exact torus bands on the end arcs, whose two corners carry equal values.
                return Shape.Extrude(Sketch.Slot(24, 8), 5)
                    .Fillet(p => 0.8 + 0.03 * (p.X + 12), s => s.PlanarFacesWithNormal(Vector3d.UnitZ))
                    .ToBrep();
            case "variable chamfer":
                // A slot rim under a setback law in x: tilted planar strips on the
                // straights, exact cone bands on the (locally constant) end arcs.
                return Shape.Extrude(Sketch.Slot(24, 8), 5)
                    .Chamfer(p => 0.8 + 0.03 * (p.X + 12), s => s.PlanarFacesWithNormal(Vector3d.UnitZ))
                    .ToBrep();
            case "rounded box":
                return Filleting.FilletAllEdges(SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))), 2);
            case "partial fillet run":
                // An L-shaped two-edge run: one mitered interior corner, two exact
                // setback terminations (planar cross-section faces).
                return Shape.Box(30, 20, 6)
                    .FilletEdges(2, s => s.PlanarFacesWithNormal(Vector3d.UnitZ)
                        .SelectMany(f => f.RimEdges())
                        .Where(e => e.IsLinear(out var a, out var b)
                            && (a.Y + b.Y < -19 || a.X + b.X > 29)))
                    .ToBrep();
            case "rounded tetrahedron":
                // Every corner is a GENERAL trihedral one: four trimmed spherical
                // corner patches through the pole-grid tier.
                return Filleting.FilletAllEdges(
                    FilletCornerVolumeTests.Polyhedron(
                        [(10, 10, 10), (10, -10, -10), (-10, 10, -10), (-10, -10, 10)],
                        [[0, 1, 2], [0, 3, 1], [0, 2, 3], [1, 3, 2]]),
                    2);
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
    /// <para><b>Empty, and the story of why is worth keeping.</b> This table used to carry
    /// <c>rounded box at 96/48 → (176 vertices, 34 facets)</c>: 176 vertex-instances on
    /// <see cref="Filleting.FilletAllEdges"/>'s spherical corner patches that the audit
    /// could not project onto the patch they were drawn on, measured at 2.6e-3 away — three
    /// decades past the 1e-6 inverse-evaluation tolerance. The diagnosis recorded here was
    /// that the shared edge curve was at fault, since orientation was unaffected and the
    /// mesh still welded closed. <b>That diagnosis was wrong.</b> The vertices were on the
    /// surface all along; <see cref="RevolvedSurface.TryProjectPoint"/> was returning the
    /// MIRRORED generator parameter, because a corner patch's great-circle generator folds
    /// in (radius, axial) and a single-seed 1D refinement descends to whichever branch its
    /// one seed happened to sit on. The 2.6e-3 was the distance to the wrong branch. Giving
    /// both swept surfaces the multi-seed rule <c>SweptSurface</c> already had took this
    /// entry to (0, 0) with the mesh — and every rendered docs PNG — unchanged, which is
    /// exactly the signature of an evaluation bug rather than a construction one.</para>
    /// <para>Two lessons the empty table is carrying: <b>a locked known-defect number is
    /// what makes a fix visible</b> (this one announced itself by failing the test), and
    /// <b>"the vertex is 2.6e-3 off the surface" and "the projection converged somewhere
    /// else" produce identical evidence</b> — distinguishing them needs a second seed, not
    /// a closer look at the geometry.</para>
    /// </summary>
    private static readonly Dictionary<(string Name, int SegmentsPerCircle), (int Vertices, int Facets)>
        KnownOffSurface = [];

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
    /// <b>No facet is degenerate.</b> A facet thinner than round-off carries no orientation
    /// — its normal direction is decided by the last bits of its vertices — which near a
    /// tangency makes it not merely useless but actively wrong.
    /// <para>Every trimmed tier and every grid path refuses to emit one by construction
    /// (<c>IsEar</c>, <c>AddOriented</c> and <c>ZipBand</c> all reject exactly-zero uv area).
    /// The planar earcut used to be the one exception, and this test used to pin that as a
    /// structural claim — "slivers only ever come from planar faces, never more than one per
    /// face" — because the defect was diagnosed as unfixable HERE: the middle sample of a
    /// nearly-collinear run belongs to no other triangle, so dropping the facet afterwards
    /// would open a T-junction against the neighbouring face.</para>
    /// <para>That diagnosis was right about the symptom and wrong about the options. Earcut
    /// filters EXACTLY collinear vertices, while boundary samples of a straight edge that
    /// arrived through a boolean are collinear only to a few ulps, so a run of them survives
    /// and one is eventually clipped as a sliver (measured: a Bézier-engraved plate emitted
    /// one facet of area 5.6e-17 in a face of area 165 at 32 and 48 segments; a three-hole
    /// drilled plate one at 96). The third option is neither clipping nor removing but
    /// <b>deferring</b>: <c>PolygonTriangulator</c> now prefers any other ear, and by the
    /// time the run's neighbours are consumed that same vertex is a corner of a FAT
    /// triangle. The diagonal is rerouted rather than the facet deleted, so nothing is
    /// dropped and the count is zero across the whole corpus at every density.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_EmitsNoDegenerateFacets(string name)
    {
        var solid = Build(name);
        foreach (var (segmentsPerCircle, curveSamples) in Densities)
        {
            var report = TessellationQuality.Audit(solid, segmentsPerCircle, curveSamples);
            string where = $"{name} at {segmentsPerCircle}/{curveSamples}: {report.Describe()}";
            Assert.Equal(0, report.Slivers);
            Assert.Equal(report.SliverFamilies, []);
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
            // The napkin ring: a sphere of radius R with a through-hole of radius r
            // keeps 4 pi/3 (R^2 - r^2)^(3/2).
            { "drilled sphere", 4 * Math.PI / 3 * Math.Pow(100 - 9, 1.5) },
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

    // ---- the one member still below the gate's floor ----

    /// <summary>
    /// <c>Box(20, 20, 20) − Sphere(12)</c> — a sphere larger than the box it is cut from,
    /// so the cavity breaks out of ALL SIX faces and what remains is a twelve-edge frame.
    /// The cavity wall splits into two periodic bands whose chains run pole to pole,
    /// scalloped by every hole rim they thread through — the hardest trimmed shape the
    /// suite knows, locked here rather than quietly dropped from <see cref="Corpus"/>.
    /// <para>This used to be the standing proof that refinement is not a convergence
    /// mechanism: with no interior rows the base facets spanned the band's whole height
    /// (~15 natural v steps) and midpoint bisection inverted the halves — 101 246 facets,
    /// 266 folded, worst agreement −0.2426 at 48/24, REFUSING outright at 96/48. Interior
    /// rows in the base triangulation (partial level paths threading between the
    /// scallops, plus seam-chord splits) fixed the mechanism: the counts below are ~22x
    /// smaller, fold-free at every density, and 96/48 now tessellates.</para>
    /// <para>What keeps it out of <see cref="Corpus"/> is the residual: at each hole
    /// rim's u-extreme the rim tangent goes vertical, no level path can anchor cleanly,
    /// and a narrow column a few natural steps tall remains for refinement to fill —
    /// worst agreement 0.7024 at 48/24 against the corpus floor of 0.9239. Honest but
    /// below the bar; filed in todo.md.</para>
    /// </summary>
    [Fact]
    public void SpherePiercingEverySide_HasNoFoldsAndABoundedResidual()
    {
        var solid = (Shape.Box(20, 20, 20) - Shape.Sphere(12)).ToBrep();

        // Committed baselines, not tolerances: a move in EITHER direction wants
        // understanding before the numbers are updated. Measured 796 / 4 608 / 15 400
        // facets at worst 0.8832 / 0.7024 / 0.9240.
        foreach (var (segmentsPerCircle, curveSamples, maxTriangles, worstFloor) in
            (ReadOnlySpan<(int, int, int, double)>)[(16, 8, 1_000, 0.85), (48, 24, 6_000, 0.70), (96, 48, 20_000, 0.90)])
        {
            var report = TessellationQuality.Audit(solid, segmentsPerCircle, curveSamples);
            string where = $"at {segmentsPerCircle}/{curveSamples}: {report.Describe()}";
            Assert.True(report.Folds == 0, $"facets face inward — {where}");
            Assert.Equal(0, report.Slivers);
            Assert.InRange(report.Triangles, 1, maxTriangles);
            Assert.True(report.WorstDot >= worstFloor, $"worst agreement regressed — {where}");

            var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples);
            mesh.Validate();
            Assert.True(mesh.IsClosed, $"welded open — {where}");
        }
    }
}
