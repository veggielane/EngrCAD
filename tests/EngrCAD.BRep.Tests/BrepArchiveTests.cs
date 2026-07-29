using System.Globalization;
using System.Text;
using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The native <c>.ecb</c> archive. Three things are being proved, and they need different
/// oracles:
/// <list type="bullet">
/// <item>every curve and surface type the kernel has round-trips as ITSELF, with its
/// defining data intact — a <see cref="GeometryGallery"/> puts one of each into a
/// structurally valid solid so the public <c>Write</c>/<c>Read</c> pair is what is
/// exercised, not an internal seam;</item>
/// <item>whole solids from the factories come back with the same counts, the same sampled
/// geometry, <c>Validate()</c> clean and their Euler genus intact;</item>
/// <item>topology SHARING survives — an edge used by two faces is still one edge object,
/// and a closed edge's two vertex references are still the same object, which is the
/// property <c>BrepEdge.IsClosedEdge</c> is defined by.</item>
/// </list>
/// </summary>
public class BrepArchiveTests
{
    // ---- the solid corpus ----

    public static TheoryData<string> Corpus =>
    [
        "box", "cylinder", "sphere", "torus", "cone-frustum", "cone-apex",
        "extrude-with-hole", "revolve-partial", "sweep", "loft", "threaded-rod",
        "rounded-box", "filleted-cylinder-rim", "chamfered-cylinder-rim",
    ];

    private static BrepSolid Build(string name) => name switch
    {
        "box" => SolidFactory.MakeBox(new Aabb((-10, -6, -3), (10, 6, 3))),
        "cylinder" => SolidFactory.MakeCylinder(5, 14),
        "sphere" => SolidFactory.MakeSphere(7),
        "torus" => SolidFactory.MakeTorus(12, 3),
        "cone-frustum" => SolidFactory.MakeCone(8, 3, 10),
        "cone-apex" => SolidFactory.MakeCone(8, 0, 10),
        "extrude-with-hole" => SolidFactory.Extrude(
            RoundedRectangle(24, 16, 3), (0, 0, 9), [Disc(4)]),
        "revolve-partial" => SolidFactory.Revolve(
            new Profile([
                new Line3d((6, 0, 0), (12, 0, 0)),
                new Line3d((12, 0, 0), (12, 0, 5)),
                new Line3d((12, 0, 5), (6, 0, 5)),
                new Line3d((6, 0, 5), (6, 0, 0)),
            ]),
            (0, 0, 0), (0, 0, 1), 1.9),
        // An elbow: the path leaves the origin along +Z (so the XY-plane disc profile is
        // perpendicular to it) and bends. A CurveSegment path also puts that wrapper in
        // the corpus.
        "sweep" => SolidFactory.Sweep(
            Disc(2), new CurveSegment(new Circle3d((14, 0, 0), (-1, 0, 0), (0, 0, 1), 14), 0, 1.0)),
        "loft" => SolidFactory.Loft([Square(12), Square(6)]),
        // The M8 basic profile in (radius, axial) corners per pitch — the same shape
        // ThreadedRodTests builds; StandardThreads lives in Modeling, which BRep cannot
        // reference.
        "threaded-rod" => SolidFactory.MakeThreadedRod(
            [
                new Vector2d(4.0, -1.25 / 16),
                new Vector2d(4.0, 1.25 / 16),
                new Vector2d(3.2, 3 * 1.25 / 8),
                new Vector2d(3.2, 5 * 1.25 / 8),
            ],
            1.25, 6),
        "rounded-box" => Filleting.FilletAllEdges(
            SolidFactory.MakeBox(new Aabb((-8, -6, -4), (8, 6, 4))), 1.5),
        "filleted-cylinder-rim" => FilletTop(SolidFactory.MakeCylinder(6, 12), 1.5),
        "chamfered-cylinder-rim" => ChamferTop(SolidFactory.MakeCylinder(6, 12), 1.5),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown corpus member"),
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CorpusRoundTripsExactly(string name)
    {
        var original = Build(name);
        var archive = BrepArchive.Write(original, name);
        var result = BrepArchive.Read(archive);
        var restored = result.Single();

        Assert.Equal(name, result.Name);
        Assert.Equal(BrepArchive.FormatVersion, result.Version);
        Assert.Empty(result.Diagnostics);

        // Counts.
        Assert.Equal(original.Shells.Count, restored.Shells.Count);
        Assert.Equal(original.Faces.Count(), restored.Faces.Count());
        Assert.Equal(original.Loops.Count(), restored.Loops.Count());
        Assert.Equal(original.Coedges.Count(), restored.Coedges.Count());
        Assert.Equal(original.Edges.Count(), restored.Edges.Count());
        Assert.Equal(original.Vertices.Count(), restored.Vertices.Count());

        // Structure and geometry, sampled on the exact geometry rather than through a
        // tessellation: a fingerprint mismatch names the entity that moved.
        Assert.Equal(Fingerprint(original), Fingerprint(restored));

        restored.Validate();
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CorpusKeepsItsEulerGenus(string name)
    {
        var original = Build(name);
        // Whatever genus the original satisfies, the restored solid satisfies the same —
        // asked rather than assumed, so a corpus member's own genus need not be hardcoded.
        int genus = Enumerable.Range(0, 8).First(original.SatisfiesEulerFormula);
        var restored = BrepArchive.Read(BrepArchive.Write(original)).Single();
        Assert.True(
            restored.SatisfiesEulerFormula(genus),
            $"'{name}' satisfied Euler at genus {genus} before the round trip and not after.");
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void WritingIsAFixedPointUnderRoundTrip(string name)
    {
        // save -> load -> save is byte-identical. This is the strong form of "exact": it
        // catches an ulp lost to a re-normalizing constructor, which a tolerance-based
        // geometry comparison would sail past. (It is also why frames are rebuilt with
        // Frame3d.FromOrthonormal, the only factory that stores its axes verbatim.)
        string first = BrepArchive.Write(Build(name), name);
        string second = BrepArchive.Write(BrepArchive.Read(first).Single(), name);
        Assert.Equal(first, second);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TopologySharingSurvives(string name)
    {
        var original = Build(name);
        var restored = BrepArchive.Read(BrepArchive.Write(original)).Single();

        // An edge used by two faces is still ONE edge object. Counting distinct edges by
        // REFERENCE is the whole test: a serializer that wrote each face's edges
        // independently would produce twice as many and still tessellate, still validate
        // loop chaining, and crack at every seam.
        var edges = new HashSet<BrepEdge>(ReferenceEqualityComparer.Instance);
        foreach (var coedge in restored.Coedges)
            edges.Add(coedge.Edge);
        Assert.Equal(original.Edges.Count(), edges.Count);
        Assert.All(edges, e => Assert.Equal(2, e.Uses.Count));

        // And a closed edge's two vertex references are still the SAME object — the
        // property IsClosedEdge is literally defined by, and the reason the archive keys
        // its entity table on reference identity rather than on position.
        Assert.Equal(
            original.Edges.Count(e => e.IsClosedEdge),
            restored.Edges.Count(e => e.IsClosedEdge));

        // Vertices likewise: a shared corner must not become N coincident corners.
        Assert.Equal(original.Vertices.Count(), restored.Vertices.Count());
    }

    // ---- every geometry type, one at a time ----

    public static TheoryData<string> CurveTypes =>
    [
        "Line3d", "Circle3d", "Ellipse3d", "Parabola3d", "Hyperbola3d", "NurbsCurve",
        "PolylineCurve3d", "Helix3d", "SpiralArc3d", "OffsetCurve3d", "CurveSegment",
        "ReversedCurve", "TransformedCurve", "PhaseShiftedCurve", "LoftRailCurve",
        "SweptRailCurve",
    ];

    private static Curve3d MakeCurve(string name) => name switch
    {
        "Line3d" => new Line3d((1, 2, 3), (4, 6, 9)),
        "Circle3d" => new Circle3d((1, 1, 0), (1, 0, 0), (0, 1, 0), 4.25),
        "Ellipse3d" => new Ellipse3d((0, 0, 2), (5, 0, 0), (0, 3, 0)),
        "Parabola3d" => new Parabola3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 2.5, new Interval(-3, 3)),
        "Hyperbola3d" => new Hyperbola3d((1, 0, 0), (2, 0, 0), (0, 1.5, 0), new Interval(-1, 1)),
        "NurbsCurve" => NurbsCurve.Arc((0, 0, 0), (1, 0, 0), (0, 1, 0), 3, 0.2, 2.1),
        "PolylineCurve3d" => new PolylineCurve3d([(0, 0, 0), (1, 0.5, 0), (2, 0, 1), (3, 1, 1)]),
        "Helix3d" => new Helix3d(Frame3d.WorldXY, 4, 1.25, 2.5),
        "SpiralArc3d" => new SpiralArc3d(Frame3d.WorldXY, 5, 0.3, new Interval(0, 1.4)),
        "OffsetCurve3d" => new OffsetCurve3d(
            new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 6), (0, 0, 1), 1.5),
        "CurveSegment" => new CurveSegment(
            new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 5), 0.4, 2.9),
        "ReversedCurve" => new ReversedCurve(new Line3d((0, 0, 0), (2, 3, 4))),
        "TransformedCurve" => new TransformedCurve(
            new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 3),
            Matrix4d.CreateTranslation((5, 0, 1)) * Matrix4d.CreateRotationX(0.4)),
        "PhaseShiftedCurve" => new PhaseShiftedCurve(
            new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 3), 0.75),
        "LoftRailCurve" => new LoftRailCurve(MakeLoftedSurface(), 0.35),
        "SweptRailCurve" => new SweptRailCurve(MakeSweptSurface(), new Vector2d(0.6, -0.4)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string> SurfaceTypes =>
    [
        "PlaneSurface", "CylinderSurface", "SphereSurface", "NurbsSurface",
        "ExtrudedSurface", "RevolvedSurface", "SweptSurface", "HelicalSurface",
        "LoftedSurface",
    ];

    private static Surface MakeSurface(string name) => name switch
    {
        "PlaneSurface" => new PlaneSurface((1, 2, 3), (1, 0, 0), (0, 1, 0)),
        "CylinderSurface" => new CylinderSurface((0, 0, 0), (1, 0, 0), (0, 1, 0), 4.5),
        "SphereSurface" => new SphereSurface((2, 0, 1), 6),
        "NurbsSurface" => MakeNurbsSurface(),
        "ExtrudedSurface" => new ExtrudedSurface(
            new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 3), (0, 0, 8)),
        "RevolvedSurface" => new RevolvedSurface(
            new Line3d((4, 0, 0), (6, 0, 5)), (0, 0, 0), (0, 0, 1), 2.3),
        "SweptSurface" => MakeSweptSurface(),
        "HelicalSurface" => new HelicalSurface(
            Frame3d.WorldXY, new Vector2d(3, 0), new Vector2d(4, 0.6), 1.25, new Interval(0, 5)),
        "LoftedSurface" => MakeLoftedSurface(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(CurveTypes))]
    public void EveryCurveTypeRoundTripsAsItself(string name)
    {
        var solid = GeometryGallery([MakeCurve(name)], MakeSurface("PlaneSurface"));
        string archive = BrepArchive.Write(solid);
        var restored = BrepArchive.Read(archive).Single();

        var before = solid.Edges.First().Curve;
        var after = restored.Edges.First().Curve;
        Assert.Equal(before.GetType(), after.GetType());
        Assert.Equal(Sample(before, before.Domain), Sample(after, after.Domain));
        Assert.Equal(archive, BrepArchive.Write(restored));
    }

    [Theory]
    [MemberData(nameof(SurfaceTypes))]
    public void EverySurfaceTypeRoundTripsAsItself(string name)
    {
        var solid = GeometryGallery([new Line3d((0, 0, 0), (1, 0, 0))], MakeSurface(name));
        string archive = BrepArchive.Write(solid);
        var restored = BrepArchive.Read(archive).Single();

        var before = solid.Faces.First().Surface;
        var after = restored.Faces.First().Surface;
        Assert.Equal(before.GetType(), after.GetType());
        Assert.Equal(Sample(before), Sample(after));
        Assert.Equal(archive, BrepArchive.Write(restored));
    }

    [Fact]
    public void ASharedCurveObjectIsWrittenOnceAndStaysShared()
    {
        // The point of an entity table: geometry referenced twice is stored once. A seam
        // curve shared by two edges must come back as ONE object, or the two edges are
        // free to drift apart under any later edit.
        var shared = new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 5);
        var solid = GeometryGallery([shared, shared], MakeSurface("PlaneSurface"));

        string archive = BrepArchive.Write(solid);
        Assert.Equal(1, CountEntities(archive, "Circle("));

        var edges = BrepArchive.Read(archive).Single().Edges.ToList();
        Assert.Same(edges[0].Curve, edges[1].Curve);
    }

    // ---- format contract ----

    [Fact]
    public void AnUnknownVersionIsRefusedByName()
    {
        string archive = BrepArchive.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))));
        string future = archive.Replace(
            $"ENGRCAD-BREP {BrepArchive.FormatVersion}", "ENGRCAD-BREP 99");

        var error = Assert.Throws<BrepArchiveException>(() => BrepArchive.Read(future));
        Assert.Contains("99", error.Message);
        Assert.Contains(BrepArchive.FormatVersion.ToString(CultureInfo.InvariantCulture), error.Message);
        Assert.Contains("newer", error.Message);
    }

    [Fact]
    public void ANonArchiveIsRefusedByName()
    {
        var error = Assert.Throws<BrepArchiveException>(() => BrepArchive.Read("ISO-10303-21;\nHEADER;"));
        Assert.Contains("ENGRCAD-BREP", error.Message);
    }

    [Fact]
    public void ForeignUnitsAreRefusedRatherThanMisScaled()
    {
        string archive = BrepArchive.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))))
            .Replace("UNITS MM", "UNITS INCH");
        var error = Assert.Throws<BrepArchiveException>(() => BrepArchive.Read(archive));
        Assert.Contains("INCH", error.Message);
        Assert.Contains("MM", error.Message);
    }

    [Fact]
    public void ADanglingReferenceIsRefusedNamingTheEntity()
    {
        string archive = BrepArchive.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))));
        var error = Assert.Throws<BrepArchiveException>(
            () => BrepArchive.Read(archive.Replace("#1,", "#9999,")));
        Assert.Contains("9999", error.Message);
    }

    [Fact]
    public void AnUnknownKeywordIsRefusedNamingIt()
    {
        string archive = BrepArchive.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))));
        var error = Assert.Throws<BrepArchiveException>(
            () => BrepArchive.Read(archive.Replace("= Plane(", "= Hyperplane(")));
        Assert.Contains("Hyperplane", error.Message);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var solid = SolidFactory.MakeCylinder(3, 7);
        string archive = BrepArchive.Write(solid);
        string annotated = "; a hand-written note\n\n" + archive.Replace("\n#1 ", "\n; another note\n\n#1 ");
        Assert.Equal(
            Fingerprint(solid),
            Fingerprint(BrepArchive.Read(annotated).Single()));
    }

    [Fact]
    public void SeveralSolidsRideInOneArchive()
    {
        var solids = new[]
        {
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2))),
            SolidFactory.MakeCylinder(1, 4),
            SolidFactory.MakeSphere(3),
        };
        var result = BrepArchive.Read(BrepArchive.Write(solids, "corpus"));

        Assert.Equal(3, result.Solids.Count);
        Assert.Equal("corpus", result.Name);
        for (int i = 0; i < solids.Length; i++)
            Assert.Equal(Fingerprint(solids[i]), Fingerprint(result.Solids[i]));
        Assert.Throws<BrepArchiveException>(() => result.Single());
    }

    [Fact]
    public void TheArchiveIsPlainReadableTextOneEntityPerLine()
    {
        // The format's whole justification is that a corpus file can be diffed by eye, so
        // that property gets an assertion rather than a comment.
        string archive = BrepArchive.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))), "unit cube");
        var lines = archive.Split('\n');

        Assert.StartsWith("ENGRCAD-BREP 1", lines[0]);
        Assert.Contains("NAME 'unit cube'", archive);
        Assert.All(
            lines.Where(l => l.StartsWith('#')),
            l => Assert.Matches(@"^#\d+ = [A-Za-z]+\(.*\)$", l));
        Assert.All(archive, c => Assert.True(c < 128, $"non-ASCII character '{c}' in the archive"));
    }

    [Fact]
    public void ANameWithAnApostropheSurvives()
    {
        string archive = BrepArchive.Write(
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))), "chris's plate");
        Assert.Equal("chris's plate", BrepArchive.Read(archive).Name);
    }

    [Fact]
    public void WriteFileAndReadFileRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + BrepArchive.Extension);
        try
        {
            var solid = SolidFactory.MakeTorus(9, 2);
            BrepArchive.WriteFile(solid, path, "ring");
            var result = BrepArchive.ReadFile(path);
            Assert.Equal("ring", result.Name);
            Assert.Equal(Fingerprint(solid), Fingerprint(result.Single()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- helpers ----

    /// <summary>
    /// A structurally valid two-face solid whose edges carry the given curves and whose
    /// faces carry the given surface — the harness that lets the PUBLIC Write/Read pair
    /// exercise one geometry type at a time. The two faces share every edge with opposite
    /// senses, which is all <see cref="BrepSolid.Validate"/> asks of a manifold; the shape
    /// is deliberately nonsense, because what is under test is the entity forms.
    /// </summary>
    private static BrepSolid GeometryGallery(IReadOnlyList<Curve3d> curves, Surface surface)
    {
        int n = Math.Max(curves.Count, 2);
        var vertices = Enumerable.Range(0, n)
            .Select(i => new BrepVertex((i, 0, 0)))
            .ToList();
        var edges = new List<BrepEdge>(n);
        for (int i = 0; i < n; i++)
        {
            var curve = i < curves.Count ? curves[i] : new Line3d((i, 0, 0), (i + 1, 0, 0));
            edges.Add(new BrepEdge(curve, curve.Domain, vertices[i], vertices[(i + 1) % n]));
        }

        var forward = new BrepLoop([.. edges.Select(e => new BrepCoedge(e, true))]);
        var backward = new BrepLoop(
            [.. Enumerable.Range(0, n).Select(i => new BrepCoedge(edges[n - 1 - i], false))]);
        return new BrepSolid([new BrepShell([
            new BrepFace(surface, [forward]),
            new BrepFace(new PlaneSurface((0, 0, 0), (1, 0, 0), (0, 1, 0)), [backward], true),
        ])]);
    }

    /// <summary>
    /// A canonical, textual description of a solid: structure walked in file order, every
    /// entity's TYPE recorded (so a curve that came back as a different type fails even if
    /// it happens to sample the same), and geometry sampled on the exact curves and
    /// surfaces rather than through a tessellation.
    /// </summary>
    private static string Fingerprint(BrepSolid solid)
    {
        var text = new StringBuilder();
        foreach (var shell in solid.Shells)
        {
            text.Append("shell\n");
            foreach (var face in shell.Faces)
            {
                text.Append("  face ").Append(face.Surface.GetType().Name)
                    .Append(face.IsReversed ? " reversed" : "").Append('\n');
                text.Append("    ").Append(Sample(face.Surface)).Append('\n');
                foreach (var loop in face.Loops)
                {
                    text.Append("    loop\n");
                    foreach (var coedge in loop.Coedges)
                    {
                        var edge = coedge.Edge;
                        text.Append("      coedge ").Append(coedge.SameSense ? '+' : '-')
                            .Append(' ').Append(edge.Curve.GetType().Name)
                            .Append(edge.IsClosedEdge ? " closed" : "")
                            .Append(' ').Append(N(edge.Domain.Start)).Append("..").Append(N(edge.Domain.End))
                            .Append('\n');
                        text.Append("        ").Append(Sample(edge.Curve, edge.Domain)).Append('\n');
                        text.Append("        v ").Append(P(edge.StartVertex.Position))
                            .Append(" -> ").Append(P(edge.EndVertex.Position)).Append('\n');
                    }
                }
            }
        }
        return text.ToString();
    }

    private static string Sample(Curve3d curve, Interval domain)
    {
        var text = new StringBuilder();
        for (int i = 0; i <= 6; i++)
            text.Append(P(curve.PointAt(domain.ParameterAt(i / 6.0)))).Append(' ');
        return text.ToString();
    }

    private static string Sample(Surface surface)
    {
        // Infinite domains (a plane's, a cylinder's v) clamp to a fixed window: the point
        // is to compare two surfaces, and both are clamped identically.
        var u = Finite(surface.DomainU);
        var v = Finite(surface.DomainV);
        var text = new StringBuilder();
        for (int i = 0; i <= 3; i++)
        {
            for (int j = 0; j <= 3; j++)
                text.Append(P(surface.PointAt(u.ParameterAt(i / 3.0), v.ParameterAt(j / 3.0)))).Append(' ');
        }
        return text.ToString();
    }

    private static Interval Finite(in Interval i) => new(
        double.IsFinite(i.Start) ? i.Start : -10,
        double.IsFinite(i.End) ? i.End : 10);

    private static string P(in Vector3d p) => $"({N(p.X)},{N(p.Y)},{N(p.Z)})";

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static int CountEntities(string archive, string keyword) =>
        archive.Split('\n').Count(l => l.StartsWith('#') && l.Contains(keyword, StringComparison.Ordinal));

    private static Profile Disc(double radius) => new(
    [
        NurbsCurve.Arc((0, 0, 0), (1, 0, 0), (0, 1, 0), radius, 0, Math.PI),
        NurbsCurve.Arc((0, 0, 0), (1, 0, 0), (0, 1, 0), radius, Math.PI, 2 * Math.PI),
    ]);

    private static Profile Square(double side) => new(
    [
        new Line3d((-side / 2, -side / 2, 0), (side / 2, -side / 2, 0)),
        new Line3d((side / 2, -side / 2, 0), (side / 2, side / 2, 0)),
        new Line3d((side / 2, side / 2, 0), (-side / 2, side / 2, 0)),
        new Line3d((-side / 2, side / 2, 0), (-side / 2, -side / 2, 0)),
    ]);

    private static Profile RoundedRectangle(double width, double depth, double radius)
    {
        double x = width / 2 - radius, y = depth / 2 - radius;
        return new Profile(
        [
            new Line3d((-x, -depth / 2, 0), (x, -depth / 2, 0)),
            NurbsCurve.Arc((x, -y, 0), (1, 0, 0), (0, 1, 0), radius, -Math.PI / 2, 0),
            new Line3d((width / 2, -y, 0), (width / 2, y, 0)),
            NurbsCurve.Arc((x, y, 0), (1, 0, 0), (0, 1, 0), radius, 0, Math.PI / 2),
            new Line3d((x, depth / 2, 0), (-x, depth / 2, 0)),
            NurbsCurve.Arc((-x, y, 0), (1, 0, 0), (0, 1, 0), radius, Math.PI / 2, Math.PI),
            new Line3d((-width / 2, y, 0), (-width / 2, -y, 0)),
            NurbsCurve.Arc((-x, -y, 0), (1, 0, 0), (0, 1, 0), radius, Math.PI, 3 * Math.PI / 2),
        ]);
    }

    private static BrepSolid FilletTop(BrepSolid solid, double radius) =>
        Filleting.FilletRim(solid, TopFace(solid), radius);

    private static BrepSolid ChamferTop(BrepSolid solid, double setback) =>
        Filleting.ChamferRim(solid, TopFace(solid), setback);

    private static BrepFace TopFace(BrepSolid solid) => solid.Faces
        .Where(f => f.IsPlanar(out _, out var n) && n.Dot(Vector3d.UnitZ) > 0.99)
        .OrderByDescending(f => f.Bounds().Center.Z)
        .First();

    private static NurbsSurface MakeNurbsSurface()
    {
        var points = new Vector3d[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
                points[i, j] = new Vector3d(i * 4, j * 3, (i - 1) * (j - 1));
        }
        var weights = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
                weights[i, j] = (i == 1 && j == 1) ? 0.75 : 1.0;
        }
        return new NurbsSurface(2, 2, points, weights, [0, 0, 0, 1, 1, 1], [0, 0, 0, 1, 1, 1]);
    }

    // The path starts at (10, 0, 0) heading +Y, so +Z is a legal seed X direction (a
    // startX parallel to the start tangent projects to zero and the constructor refuses).
    private static SweptSurface MakeSweptSurface() => new(
        new Line3d((0, -1, 0), (0, 1, 0)),
        new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 10),
        (0, 0, 1),
        24);

    private static LoftedSurface MakeLoftedSurface() => new(
    [
        new Circle3d((0, 0, 0), (1, 0, 0), (0, 1, 0), 5),
        new Circle3d((0, 0, 6), (1, 0, 0), (0, 1, 0), 3),
        new Circle3d((0, 0, 12), (1, 0, 0), (0, 1, 0), 4),
    ],
    [0, 0.55, 1]);
}
