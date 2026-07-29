using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The anisotropic boundary layer: a graded stack marched inward from a tagged wall, with the
/// isotropic pipeline filling what is left.
///
/// <para>The oracle is the mesher's own: <b>the sum of the element volumes equals the input
/// surface's</b>, to relative round-off. It is a strong one here because the layer changes
/// the surface handed to the fill — offsetting the wall, trimming the flat faces the stack's
/// rim slides along, re-triangulating them — and any of those going wrong shows up in the
/// total immediately.</para>
/// </summary>
public class BoundaryLayerTests(ITestOutputHelper output)
{
    // ---- fixtures ----

    /// <summary>A duct: a circular bore with rings along it and flat ends. Tag 1 = wall,
    /// 2 = inlet, 3 = outlet — the shape a boundary layer exists for.</summary>
    internal static (HalfEdgeMesh Surface, int[] Tags) Duct(
        double radius, double length, int segments, int rings)
    {
        var positions = new List<Vector3d>();
        for (int k = 0; k <= rings; k++)
        {
            for (int i = 0; i < segments; i++)
            {
                double a = 2 * Math.PI * i / segments;
                positions.Add(new Vector3d(
                    radius * Math.Cos(a), radius * Math.Sin(a), length * k / rings));
            }
        }
        int bottom = positions.Count;
        positions.Add(new Vector3d(0, 0, 0));
        int top = positions.Count;
        positions.Add(new Vector3d(0, 0, length));

        var faces = new List<int[]>();
        var tags = new List<int>();
        for (int k = 0; k < rings; k++)
        {
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                int a = k * segments + i, b = k * segments + j;
                int c = (k + 1) * segments + j, d = (k + 1) * segments + i;
                faces.Add([a, b, c]);
                tags.Add(1);
                faces.Add([a, c, d]);
                tags.Add(1);
            }
        }
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % segments;
            faces.Add([bottom, j, i]);
            tags.Add(2);
            faces.Add([top, rings * segments + i, rings * segments + j]);
            tags.Add(3);
        }
        return (HalfEdgeMesh.Build(positions, faces), [.. tags]);
    }

    /// <summary>A prism over a counter-clockwise outline: side walls plus ear-clipped caps.</summary>
    internal static HalfEdgeMesh Prism(IReadOnlyList<Vector2d> outline, double height)
    {
        var positions = new List<Vector3d>();
        foreach (var p in outline)
            positions.Add(new Vector3d(p.X, p.Y, 0));
        foreach (var p in outline)
            positions.Add(new Vector3d(p.X, p.Y, height));

        int n = outline.Count;
        var faces = new List<int[]>();
        foreach (var (a, b, c) in PolygonTriangulator.Triangulate(outline))
        {
            faces.Add([a, c, b]);                   // bottom, wound -z
            faces.Add([n + a, n + b, n + c]);       // top, wound +z
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            faces.Add([i, j, n + j]);
            faces.Add([i, n + j, n + i]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>An L-shaped prism — one genuinely CONCAVE vertical edge.</summary>
    internal static HalfEdgeMesh LPrism(double height) => Prism(
        [new(0, 0), new(20, 0), new(20, 10), new(10, 10), new(10, 20), new(0, 20)], height);


    private static BoundaryLayerSpec Spec(
        Func<FacetRef, bool> wall, double first, int layers, double ratio) => new()
        {
            Wall = wall,
            FirstLayerThickness = first,
            LayerCount = layers,
            GrowthRatio = ratio,
        };

    private static HalfEdgeMesh Box(double size) =>
        MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(size, size, size)));

    // ---- the volume identity ----

    [Fact]
    public void WholeSurfaceWall_FillsExactlyTheInputVolume()
    {
        var mesh = TetMesher.Mesh(
            Box(20), new TetMeshOptions { BoundaryLayer = Spec(Facets.All, 0.5, 3, 1.2) },
            out var report);

        output.WriteLine(report.ToString());
        Assert.Equal(8000.0, mesh.Volume, 8000.0 * 1e-9);
        Assert.True(report.VolumeResidual < 1e-9, $"residual {report.VolumeResidual:E3}");
        Assert.Equal(108, report.BoundaryLayer!.Value.ElementCount);   // 12 wall tris x 3 layers x 3
    }

    [Fact]
    public void PartialWall_TheRimSlidesAlongTheFlatFacesAndTheVolumeStillCloses()
    {
        // The wall is one face of a box; the stack's rim slides down the four side walls,
        // which are trimmed and re-triangulated around it. Nothing may be lost or invented.
        var mesh = TetMesher.Mesh(
            Box(20),
            new TetMeshOptions
            {
                BoundaryLayer = Spec(Facets.FacingAlong(new Vector3d(0, 0, 1), 10), 0.5, 3, 1.2),
            },
            out var report);

        var layer = report.BoundaryLayer!.Value;
        output.WriteLine(report.ToString());
        Assert.Equal(8000.0, mesh.Volume, 8000.0 * 1e-9);
        Assert.Equal(2, layer.WallTriangles);
        Assert.Equal(4, layer.JunctionNodes);          // the top face's four corners
        Assert.Equal(4, layer.RetriangulatedPatches);  // the four side walls
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ScaleFreedom_TheVolumeIdentityHoldsOverSixDecades(double scale)
    {
        var box = MeshPrimitives.Box(new Aabb(
            new Vector3d(0, 0, 0), new Vector3d(2 * scale, 3 * scale, 5 * scale)));
        var mesh = TetMesher.Mesh(
            box,
            new TetMeshOptions { BoundaryLayer = Spec(Facets.All, 0.05 * scale, 3, 1.2) },
            out var report);

        double expected = 30 * scale * scale * scale;
        Assert.Equal(expected, mesh.Volume, Math.Abs(expected) * 1e-9);
        Assert.True(report.VolumeResidual < 1e-9,
            $"residual {report.VolumeResidual:E3} at scale {scale}");
    }

    [Fact]
    public void Duct_TheCfdShape_MeshesWithAGradedWallAndAnIsotropicCore()
    {
        var (surface, tags) = Duct(5.0, 20.0, 32, 12);
        var mesh = TetMesher.Mesh(
            surface,
            new TetMeshOptions
            {
                FacetTags = tags,
                RefineQuality = true,
                MaxElementSize = 2.0,
                BoundaryLayer = Spec(Facets.Tag(1), 0.15, 4, 1.3),
            },
            out var report);

        var layer = report.BoundaryLayer!.Value;
        output.WriteLine(report.ToString());
        output.WriteLine(TetQuality.Analyze(mesh).ToText());

        Assert.Equal(surface.Volume(), mesh.Volume, Math.Abs(surface.Volume()) * 1e-9);
        Assert.True(report.VolumeResidual < 1e-9, $"residual {report.VolumeResidual:E3}");
        Assert.Equal(768 * 4 * 3, layer.ElementCount);
        // Every rim node of both flat ends slides; nothing else does.
        Assert.Equal(64, layer.JunctionNodes);
        Assert.Equal(2, layer.RetriangulatedPatches);
        // The stack's own tags survive onto the finished mesh's facets.
        var facetTags = mesh.BoundaryFacets.Select(f => f.SourceTriangle).Distinct().Order().ToArray();
        Assert.Equal([1, 2, 3], facetTags);
    }

    // ---- the controls are reproduced exactly ----

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.2)]
    [InlineData(1.5)]
    public void LayerThicknessesAndGrowthAreReproducedExactly(double ratio)
    {
        const double first = 0.3;
        const int layers = 5;
        var spec = Spec(Facets.All, first, layers, ratio);
        var mesh = TetMesher.Mesh(
            Box(40), new TetMeshOptions { BoundaryLayer = spec }, out var report);

        var layer = report.BoundaryLayer!.Value;
        output.WriteLine($"first {layer.FirstLayerThickness:R}, worst ratio {layer.MeasuredGrowthRatio:R}, " +
                         $"total {layer.TotalThickness:R}");

        Assert.Equal(first, layer.FirstLayerThickness, first * 1e-12);
        Assert.Equal(ratio, layer.MeasuredGrowthRatio, ratio * 1e-12);
        Assert.Equal(layers, spec.Thicknesses.Count);
        Assert.Equal(first, spec.Thicknesses[0]);
        for (int i = 1; i < layers; i++)
            Assert.Equal(ratio, spec.Thicknesses[i] / spec.Thicknesses[i - 1], ratio * 1e-12);
        Assert.Equal(spec.Thicknesses.Sum(), spec.TotalThickness, spec.TotalThickness * 1e-12);
        Assert.True(mesh.TetCount > 0);
    }

    /// <summary>
    /// The march measured on the mesh itself: every column steps exactly the requested
    /// thickness, in the direction it started in. This is the check the report's own summary
    /// numbers are a shorthand for.
    /// </summary>
    [Fact]
    public void EveryColumnStepsItsRequestedThickness()
    {
        var (surface, tags) = Duct(5.0, 20.0, 24, 6);
        var spec = Spec(Facets.Tag(1), 0.2, 4, 1.25);
        var mesh = TetMesher.Mesh(
            surface,
            new TetMeshOptions { FacetTags = tags, BoundaryLayer = spec },
            out var report);

        // Group the layer nodes into columns by radius: a duct's march is exactly radial away
        // from the rim, so the levels sit at known radii and can be counted.
        double[] expected = new double[spec.LayerCount + 1];
        expected[0] = 5.0;
        for (int k = 1; k <= spec.LayerCount; k++)
            expected[k] = expected[k - 1] - spec.Thicknesses[k - 1];

        var radii = new HashSet<double>();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var p = mesh.Position(v);
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            foreach (double e in expected)
                if (Math.Abs(r - e) < 1e-9)
                    radii.Add(e);
        }
        output.WriteLine($"radii found: {string.Join(", ", radii.Order())}");
        Assert.Equal(expected.Length, radii.Count);
        Assert.True(report.VolumeResidual < 1e-9);
    }

    // ---- determinism ----

    [Fact]
    public void TwoRunsAreBitIdentical()
    {
        var options = new TetMeshOptions
        {
            RefineQuality = true,
            MaxElementSize = 6.0,
            BoundaryLayer = Spec(Facets.All, 0.4, 3, 1.25),
        };
        var a = TetMesher.Mesh(Box(20), options, out _);
        var b = TetMesher.Mesh(Box(20), options, out _);

        Assert.Equal(a.VertexCount, b.VertexCount);
        Assert.Equal(a.TetCount, b.TetCount);
        for (int v = 0; v < a.VertexCount; v++)
            Assert.Equal(a.Position(v), b.Position(v));
        for (int t = 0; t < a.TetCount; t++)
            Assert.Equal(a.GetTet(t), b.GetTet(t));
    }

    // ---- conformity: the property a non-conforming split would break ----

    /// <summary>
    /// Every internal face is used by exactly TWO elements and every boundary face by one.
    /// That is the whole statement of "the prisms conform to each other and to the fill" — a
    /// quad split one way by one prism and the other way by its neighbour leaves four faces
    /// used once each, sitting in the middle of the mesh, and this counts them.
    /// </summary>
    [Fact]
    public void EveryFaceIsUsedOnceOrTwice_AndTheBoundaryFacetsAreExactlyTheOnesUsedOnce()
    {
        var (surface, tags) = Duct(6.0, 15.0, 20, 8);
        var mesh = TetMesher.Mesh(
            surface,
            new TetMeshOptions { FacetTags = tags, BoundaryLayer = Spec(Facets.Tag(1), 0.2, 3, 1.4) },
            out var report);

        int[][] faceTable = [[1, 2, 3], [0, 3, 2], [0, 1, 3], [0, 2, 1]];
        var usage = new Dictionary<(int, int, int), int>();
        for (int t = 0; t < mesh.TetCount; t++)
        {
            var tet = mesh.GetTet(t);
            foreach (var f in faceTable)
            {
                var key = Sorted(tet[f[0]], tet[f[1]], tet[f[2]]);
                usage[key] = usage.GetValueOrDefault(key) + 1;
            }
        }

        int once = 0;
        foreach (var (key, count) in usage)
        {
            Assert.True(count is 1 or 2,
                $"face {key} is used by {count} elements; a conforming tetrahedralization uses " +
                "every face once (boundary) or twice (interior)");
            if (count == 1)
                once++;
        }

        Assert.Equal(once, mesh.BoundaryFacetCount);
        output.WriteLine($"{mesh.TetCount} elements, {usage.Count} distinct faces, {once} on the boundary");
        Assert.True(report.VolumeResidual < 1e-9);

        static (int, int, int) Sorted(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }
    }

    /// <summary>
    /// The stack's outer skin IS the input wall: its boundary facets carrying the wall's tag
    /// have exactly the input wall's total area. A stack that tore, or a rim that slid off the
    /// surface, would change it.
    /// </summary>
    [Fact]
    public void TheStacksOuterSkinHasExactlyTheWallsArea()
    {
        var (surface, tags) = Duct(5.0, 20.0, 32, 10);
        var triangles = surface.Triangulated();
        var (positions, faces) = triangles.ToIndexed();

        double wallArea = 0;
        for (int f = 0; f < faces.Count; f++)
        {
            if (tags[f] != 1)
                continue;
            var t = faces[f];
            wallArea += 0.5 * (positions[t[1]] - positions[t[0]])
                .Cross(positions[t[2]] - positions[t[0]]).Length;
        }

        var mesh = TetMesher.Mesh(
            surface,
            new TetMeshOptions { FacetTags = tags, BoundaryLayer = Spec(Facets.Tag(1), 0.2, 3, 1.2) },
            out _);

        double meshed = 0;
        foreach (var facet in mesh.BoundaryFacets)
        {
            if (facet.SourceTriangle != 1)
                continue;
            meshed += 0.5 * (mesh.Position(facet.V1) - mesh.Position(facet.V0))
                .Cross(mesh.Position(facet.V2) - mesh.Position(facet.V0)).Length;
        }

        output.WriteLine($"wall area {wallArea:G10}, meshed skin {meshed:G10}");
        Assert.Equal(wallArea, meshed, wallArea * 1e-9);
    }

    // ---- refusals ----

    [Fact]
    public void ConcaveCornerTighterThanTheStack_IsRefusedByName()
    {
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
            LPrism(20),
            new TetMeshOptions
            {
                BoundaryLayer = Spec(f => Math.Abs(f.Normal.Z) < 0.5, 2.0, 4, 1.4),
            },
            out _));

        output.WriteLine(ex.Message);
        Assert.Contains("folds the wall facet", ex.Message);
        Assert.Contains("layer 4 of 4", ex.Message);
        Assert.Contains("turns faster", ex.Message);
    }

    /// <summary>
    /// Which net catches which failure — the verdict is worth pinning, because the three are
    /// easy to confuse and only one of them is the one people reach for.
    ///
    /// <para><b>Nothing reachable from a real body exercises the global self-intersection
    /// test.</b> The reason is structural rather than lucky: two flat walls closing on each
    /// other never CROSS — they swap places, and two parallel sheets that have swapped places
    /// are still parallel — so the face between them turning inside out is what happens
    /// first; and where a wall is CURVED enough to make the offsets genuinely cross, the
    /// facets fold before they get there. The test stays as the backstop for a shape that has
    /// neither property (this is the <c>TrimmedFaceRefusalTests</c> pattern: lock the verdict
    /// so the finding cannot rot while nothing reaches the branch).</para>
    /// </summary>
    [Theory]
    [InlineData("fold", "folds the wall facet")]
    [InlineData("eaten", "eaten through the face")]
    [InlineData("consumed", "consumed body")]
    public void EachRefusalFamilyIsReachedByTheNetThatNamesIt(string family, string expected)
    {
        TetMeshException ex = family switch
        {
            // A convex corner with 10-long edges against a 14-tall stack.
            "fold" => Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
                LPrism(20),
                new TetMeshOptions
                { BoundaryLayer = Spec(f => Math.Abs(f.Normal.Z) < 0.5, 2.0, 4, 1.4) }, out _)),

            // Two flat walls swapping places across a 1-thick plate.
            "eaten" => Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
                MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(40, 40, 1))),
                new TetMeshOptions
                {
                    BoundaryLayer = Spec(
                        Facets.Or(Facets.FacingAlong(new Vector3d(0, 0, 1), 10),
                                  Facets.FacingAlong(new Vector3d(0, 0, -1), 10)), 0.4, 3, 1.2),
                }, out _)),

            // A stack that swallows a body with no flat face left to notice it first.
            _ => Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
                MeshPrimitives.UvSphere(3.0, 16, 8),
                new TetMeshOptions { BoundaryLayer = Spec(Facets.All, 1.6, 3, 1.0) }, out _)),
        };

        output.WriteLine($"{family}: {ex.Message}");
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void TheSameConcaveCornerAcceptsAStackThatFits()
    {
        var mesh = TetMesher.Mesh(
            LPrism(20),
            new TetMeshOptions { BoundaryLayer = Spec(f => Math.Abs(f.Normal.Z) < 0.5, 0.4, 3, 1.2) },
            out var report);

        Assert.Equal(6000.0, mesh.Volume, 6000.0 * 1e-9);
        Assert.True(report.VolumeResidual < 1e-9);
    }

    [Fact]
    public void TwoWallsClosingOnEachOther_AreRefusedByName()
    {
        // A plate 1 thick with 3 layers totalling 1.456 marched from BOTH flat faces (the four
        // sides are untagged, so nothing folds and every rim slides cleanly): the two stacks
        // swap places with EVERY element still perfectly well shaped. Note WHICH net catches
        // it — the two offset sheets are parallel and never cross, so the self-intersection
        // test sees nothing; what has happened is that the side faces between them turned
        // inside out.
        var plate = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(40, 40, 1)));
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
            plate,
            new TetMeshOptions
            {
                BoundaryLayer = Spec(
                    Facets.Or(Facets.FacingAlong(new Vector3d(0, 0, 1), 10),
                              Facets.FacingAlong(new Vector3d(0, 0, -1), 10)), 0.4, 3, 1.2),
            },
            out _));

        output.WriteLine(ex.Message);
        Assert.Contains("eaten through the face", ex.Message);
        Assert.Contains("turned inside out", ex.Message);
    }

    [Fact]
    public void TheSamePlate_AcceptsAStackThatFitsThroughIt()
    {
        var plate = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(40, 40, 1)));
        var mesh = TetMesher.Mesh(
            plate,
            new TetMeshOptions
            {
                BoundaryLayer = Spec(
                    Facets.Or(Facets.FacingAlong(new Vector3d(0, 0, 1), 10),
                              Facets.FacingAlong(new Vector3d(0, 0, -1), 10)), 0.1, 3, 1.2),
            },
            out var report);

        output.WriteLine(report.ToString());
        Assert.Equal(1600.0, mesh.Volume, 1600.0 * 1e-9);
    }

    [Fact]
    public void AWallEndingOnACurvedNeighbour_IsRefusedByName()
    {
        var sphere = MeshPrimitives.UvSphere(10.0, 24, 12);
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
            sphere,
            new TetMeshOptions { BoundaryLayer = Spec(f => f.Normal.Z > 0.3, 0.3, 2, 1.2) },
            out _));

        output.WriteLine(ex.Message);
        Assert.Contains("rim vertex", ex.Message);
    }

    [Fact]
    public void AWallSelectionThatMatchesNothing_IsRefusedRatherThanIgnored()
    {
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
            Box(10), new TetMeshOptions { BoundaryLayer = Spec(Facets.Tag(999), 0.2, 2, 1.2) }, out _));

        output.WriteLine(ex.Message);
        Assert.Contains("selected no wall facets", ex.Message);
    }

    [Theory]
    [InlineData(0, 1.2, 0.2, "LayerCount")]
    [InlineData(3, 1.2, 0.0, "FirstLayerThickness")]
    [InlineData(3, 0.0, 0.2, "GrowthRatio")]
    public void MalformedSpecs_AreRefusedNamingTheField(
        int layers, double ratio, double first, string field)
    {
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
            Box(10),
            new TetMeshOptions { BoundaryLayer = Spec(Facets.All, first, layers, ratio) },
            out _));

        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void AStackTallerThanTheBody_IsRefusedRatherThanInverted()
    {
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(
            Box(20), new TetMeshOptions { BoundaryLayer = Spec(Facets.All, 4.0, 4, 1.5) }, out _));

        output.WriteLine(ex.Message);
        Assert.True(
            ex.Message.Contains("folds") || ex.Message.Contains("consumed")
                || ex.Message.Contains("self-intersects"),
            ex.Message);
    }

    // ---- no layer means no change ----

    [Fact]
    public void WithoutASpec_TheMesherIsUntouched()
    {
        var box = Box(20);
        var plain = TetMesher.Mesh(box, null, out var plainReport);
        var explicitly = TetMesher.Mesh(box, new TetMeshOptions { BoundaryLayer = null }, out var report);

        Assert.Null(plainReport.BoundaryLayer);
        Assert.Equal(0, plainReport.RefinementBlockedByFrozenBoundary);
        Assert.Equal(plain.TetCount, explicitly.TetCount);
        for (int t = 0; t < plain.TetCount; t++)
            Assert.Equal(plain.GetTet(t), explicitly.GetTet(t));
        Assert.Null(report.BoundaryLayer);
    }
}
