using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A twisted extrusion as EXACT B-Rep geometry, verified against closed forms.
///
/// <para><b>The headline is a volume IDENTITY, and it is what a wrong implementation
/// cannot fake</b>: every section of a twisted prism is the base section ROTATED, and a
/// rotation preserves area, so a pure twist has EXACTLY the untwisted volume A·h whatever
/// the twist angle is. A linear taper multiplies that by the frustum factor
/// (1 + s + s²)/3. Both are asserted on the exact solid (through mass properties) and then
/// APPROACHED by the tessellation quadratically — the second-order convergence the old
/// mesh route's first-order deficit is the comparison for.</para>
/// </summary>
public class TwistedExtrudeTests
{
    private const double Twist = Math.PI / 2;
    private const double Side = 20;
    private const double Height = 40;

    private static Profile Square(double side)
    {
        double h = side / 2;
        return Profile.FromPoints([
            new Vector3d(-h, -h, 0), new Vector3d(h, -h, 0),
            new Vector3d(h, h, 0), new Vector3d(-h, h, 0)]);
    }

    private static BrepSolid Twisted(double twist = Twist, double sx = 1, double sy = 1) =>
        SolidFactory.TwistExtrude(Square(Side), Frame3d.WorldXY, Height, twist, new Vector2d(sx, sy));

    /// <summary>The exact solid's volume, measured through the tessellate-then-Richardson
    /// route at a density where the identities below are the arithmetic rather than the
    /// discretization: measured 3.6e-8 / 4.6e-8 / 5.4e-8 relative for the pure twist, the
    /// 0.4 taper and the anisotropic taper at 128 segments per circle, against 2.6e-5 at the
    /// default 64. The identity is EXACT on the B-Rep; what is approximate is reading a
    /// volume off it, so the assertions carry 1e-6 relative and say so.</summary>
    private static double MeasuredVolume(BrepSolid solid) =>
        BrepMassProperties.Compute(solid, 1.0,
            new BrepMassPropertyOptions { SegmentsPerCircle = 128, CurveSamples = 24 }).Volume;

    /// <summary>Volume error at a density, plus the ratio to the previous — a ratio of 4
    /// is second order.</summary>
    private static (double[] Errors, double[] Ratios) Convergence(
        BrepSolid solid, double exact, int[] densities)
    {
        var errors = densities
            .Select(n => Math.Abs(BRepTessellator.Tessellate(solid, n, 24).Volume() - exact))
            .ToArray();
        var ratios = Enumerable.Range(1, errors.Length - 1)
            .Select(i => errors[i - 1] / errors[i])
            .ToArray();
        return (errors, ratios);
    }

    // ---- the volume identities ----

    [Fact]
    public void APureTwistHasExactlyTheUntwistedVolume()
    {
        // The identity: a rotation is area-preserving, so every section of a twisted prism
        // encloses the base area and Cavalieri gives A·h — for ANY twist. A construction
        // that shears, scales or double-counts the section cannot land on this number.
        double exact = Side * Side * Height;
        foreach (double twist in new[] { Math.PI / 6, Math.PI / 2, Math.PI, 4 * Math.PI })
        {
            Assert.Equal(exact, MeasuredVolume(Twisted(twist)), Math.Abs(exact) * 1e-6);
        }
    }

    [Fact]
    public void ALinearTaperMultipliesItByTheFrustumFactor()
    {
        // V = A·h·(1 + s + s²)/3 — the prismatoid rule, exact for a linear taper because
        // the section area is quadratic in the height.
        foreach (double s in new[] { 0.4, 0.75, 1.6 })
        {
            double exact = Side * Side * Height * (1 + s + s * s) / 3;
            Assert.Equal(exact, MeasuredVolume(Twisted(Twist, s, s)), Math.Abs(exact) * 1e-6);
        }
    }

    [Fact]
    public void AnAnisotropicTaperTakesTheProductOfItsTwoAxes()
    {
        // The per-axis case has no textbook name and IS a closed form: the section area
        // scales as sx(v)·sy(v), so V = A·h·∫lerp(1,sx,v)·lerp(1,sy,v) dv, whose integral
        // is (2 + sx + sy + 2·sx·sy)/6. That reduces to the frustum factor at sx = sy.
        const double sx = 0.5, sy = 1.5;
        double exact = Side * Side * Height * (2 + sx + sy + 2 * sx * sy) / 6;
        Assert.Equal(exact, MeasuredVolume(Twisted(Twist, sx, sy)), Math.Abs(exact) * 1e-6);
    }

    // ---- convergence ----

    [Fact]
    public void TheTessellationConvergesQuadraticallyOntoTheExactVolume()
    {
        // Second order is the claim the twist-matched profile subdivision exists to make.
        // The mid-range ratios wobble because the u and v counts are INTEGERS and step at
        // their own densities; the asymptotic pair is where the order is read (the
        // last-pair rule), and it lands on 4.
        var (errors, ratios) = Convergence(Twisted(), Side * Side * Height, [16, 32, 64, 128, 256, 512]);
        Assert.True(ratios[^1] > 3.8 && ratios[^1] < 4.2,
            $"final ratio {ratios[^1]:F3}, errors [{string.Join(", ", errors.Select(e => e.ToString("E3")))}]");
        Assert.True(ratios[^2] > 3.8 && ratios[^2] < 4.2, $"second-to-last ratio {ratios[^2]:F3}");
        // ... and the whole run improves monotonically, which a stalled discretization
        // (a fixed sample count somewhere in the chain — the recorded FLOOR signature)
        // would not.
        for (int i = 1; i < errors.Length; i++)
            Assert.True(errors[i] < errors[i - 1], $"error rose at index {i}");
    }

    [Fact]
    public void AProfileSubdividedOnlyByItsOwnCurvatureWouldConvergeFirstOrder()
    {
        // The instrument that gives the previous test meaning. A straight side carries no
        // curvature, so a density rule that asks only the GENERATOR gives it two samples
        // at every density — and the wall quad's triangulating diagonal then misses the
        // twisting surface by ~½·dphi·L, FIRST order in the twist step. Measured here by
        // holding the profile at its curvature-only count while the twist rows refine:
        // the error falls by ~2 per doubling where the production rule gives ~4.
        var surface = (TwistedSurface)Twisted().Faces.First(f => f.Surface is TwistedSurface).Surface;
        double area = 0, unsubdivided = 0;
        foreach (int n in new[] { 64, 128 })
        {
            int rows = surface.NaturalVSegments(n);
            // One panel spanning the whole side, against the same side cut into the
            // production number of panels: the volume of the twisted prism each describes.
            double coarse = PrismVolume(1, rows);
            double fine = PrismVolume(surface.PanelSegments(n), rows);
            if (area == 0)
            {
                area = Math.Abs(coarse - Side * Side * Height);
                unsubdivided = Math.Abs(fine - Side * Side * Height);
            }
            else
            {
                double coarseRatio = area / Math.Abs(coarse - Side * Side * Height);
                double fineRatio = unsubdivided / Math.Abs(fine - Side * Side * Height);
                Assert.True(coarseRatio < 2.6,
                    $"a curvature-only profile should converge ~first order, ratio {coarseRatio:F3}");
                Assert.True(fineRatio > 3.2,
                    $"the twist-matched profile should converge ~second order, ratio {fineRatio:F3}");
            }
        }

        // The signed volume of the polyhedron a (columns x rows) grid of the four sides
        // makes, closed by its two flat caps — the same corners the tessellator emits.
        static double PrismVolume(int columns, int rows)
        {
            var solid = Twisted();
            var sides = solid.Faces.Select(f => f.Surface).OfType<TwistedSurface>().ToList();
            var polygons = new List<IReadOnlyList<Vector3d>>();
            var bottom = new List<Vector3d>();
            var top = new List<Vector3d>();
            foreach (var side in sides)
            {
                for (int j = 0; j < columns; j++)
                {
                    double u0 = side.DomainU.ParameterAt((double)j / columns);
                    double u1 = side.DomainU.ParameterAt((double)(j + 1) / columns);
                    for (int k = 0; k < rows; k++)
                    {
                        double v0 = (double)k / rows, v1 = (double)(k + 1) / rows;
                        polygons.Add([
                            side.PointAt(u0, v0), side.PointAt(u1, v0),
                            side.PointAt(u1, v1), side.PointAt(u0, v1)]);
                    }
                    bottom.Add(side.PointAt(u0, 0));
                    top.Add(side.PointAt(u0, 1));
                }
            }
            bottom.Reverse();
            polygons.Add(bottom);
            polygons.Add(top);
            return MeshWelder.WeldPolygons(polygons, tolerance: 1e-9, zipSeams: true).Volume();
        }
    }

    // ---- the two representations are ONE geometry ----

    [Fact]
    public void EveryExactVertexLiesOnTheMeshRoutesOwnSurface()
    {
        // The cross-representation oracle. The B-Rep's vertices are EXACTLY on the twisted
        // surface, so their distance from the mesh route's field measures the MESH's own
        // faceting and nothing else — and it must fall quadratically as that mesh refines,
        // which is the statement that the two constructions describe one geometry rather
        // than two similar ones.
        var shape = Shape.Extrude(Sketch.Rectangle(Side, Side), Height, twist: Twist);
        var exact = BRepTessellator.Tessellate(shape.ToBrep(), 96, 24);

        var worst = new List<double>();
        foreach (int q in new[] { 64, 128, 256, 512 })
        {
            var field = new MeshSdf(shape.ToMesh(new MeshQuality { SegmentsPerCircle = q }));
            worst.Add(exact.Vertices.Max(v => Math.Abs(field.Evaluate(v.Position))));
        }
        for (int i = 1; i < worst.Count; i++)
        {
            double ratio = worst[i - 1] / worst[i];
            Assert.True(ratio > 3.4 && ratio < 4.6,
                $"residual ratio {ratio:F3} at step {i} — [{string.Join(", ", worst.Select(w => w.ToString("E3")))}]");
        }
        Assert.True(worst[^1] < 5e-4, $"finest residual {worst[^1]:E3}");
    }

    // ---- topology and downstream operations ----

    [Fact]
    public void ATwistedSolidTessellatesClosedAndSurvivesAFurtherBoolean()
    {
        // The transform work's rule: a solid that re-tessellates closed AND survives a
        // second boolean is what catches geometry that is merely near the right place.
        var shape = Shape.Extrude(Sketch.Rectangle(Side, Side), Height, twist: Twist);
        Assert.True(BRepTessellator.Tessellate(shape.ToBrep(), 64, 24).IsClosed);

        var bored = shape - Shape.Cylinder(4, 80).Translate(0, 0, Height / 2);
        var solid = bored.ToBrep();
        solid.Validate();

        double exact = (Side * Side - Math.PI * 16) * Height;
        var (errors, ratios) = Convergence(solid, exact, [32, 64, 128, 256]);
        Assert.True(BRepTessellator.Tessellate(solid, 64, 24).IsClosed);
        Assert.True(ratios[^1] > 3.7 && ratios[^1] < 4.3,
            $"bored ratio {ratios[^1]:F3}, errors [{string.Join(", ", errors.Select(e => e.ToString("E3")))}]");
    }

    [Fact]
    public void AnExactSectionOfATwistedPrismHasExactlyTheBaseArea()
    {
        // A twisted band has no closed-form plane intersection, so this runs on the
        // marching tracer — and it is only assemblable because the tracer TERMINATES its
        // branches on the band's own rails. The area is the identity again: a rotation
        // preserves it, so the section at ANY height encloses the base area.
        var shape = Shape.Extrude(Sketch.Rectangle(Side, Side), Height, twist: Twist);
        foreach (double z in new[] { 7.5, 20.0, 33.0 })
        {
            var regions = shape.Section(SketchPlane.At((0, 0, z), (1, 0, 0), (0, 1, 0)));
            var region = Assert.Single(regions);
            Assert.Equal(Side * Side, region.Area, 6);
        }
    }

    // ---- the default paths are untouched ----

    [Fact]
    public void AZeroTwistExtrusionIsStillThePlainExtrusionBitForBit()
    {
        // The opt-in rule: a twist of zero and a unit scale is routed to the plain
        // extrusion node at the API, so nothing about this feature can move it.
        var plain = Shape.Extrude(Sketch.Rectangle(Side, Side), Height);
        var stated = Shape.Extrude(Sketch.Rectangle(Side, Side), Height, twist: 0, scale: 1);
        var a = BRepTessellator.Tessellate(plain.ToBrep(), 32, 24);
        var b = BRepTessellator.Tessellate(stated.ToBrep(), 32, 24);
        Assert.Equal(a.VertexCount, b.VertexCount);
        for (int i = 0; i < a.VertexCount; i++)
        {
            var p = a.Vertices.ElementAt(i).Position;
            var q = b.Vertices.ElementAt(i).Position;
            Assert.Equal(BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(q.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(q.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(p.Z), BitConverter.DoubleToInt64Bits(q.Z));
        }
    }

    [Fact]
    public void APureTaperStillLowersThroughTheRuledLoftAndIsExact()
    {
        // The taper path is the incumbent one (a two-section ruled loft) and stays that
        // way: exactly A·h·(1+s+s²)/3 at the COARSEST density, because a ruled surface's
        // v-chords lie on it.
        const double s = 0.5;
        var shape = Shape.Extrude(Sketch.Rectangle(Side, Side), Height, twist: 0, scale: s);
        double exact = Side * Side * Height * (1 + s + s * s) / 3;
        Assert.Equal(exact, BRepTessellator.Tessellate(shape.ToBrep(), 16, 8).Volume(), 9);
    }

    [Fact]
    public void TwistIsNativeInTheBRepAndTheReportSaysSo()
    {
        var shape = Shape.Extrude(Sketch.Rectangle(Side, Side), Height, twist: Twist);
        var report = shape.Explain(TargetRep.Brep);
        Assert.True(report.IsConvertible);
        Assert.Contains("twisted side surfaces", report.ToString());
    }

    [Fact]
    public void AMirroredTwistIsTheOppositeTwistAndTheSolidProvesIt()
    {
        // Shape.Mirror re-DECLARES the twist rather than re-placing it (a reflection
        // conjugates the rotation: F.Rot(d, t).F = Rot(F.d, t), and here F negates the axis),
        // so a mirrored twisted extrude must be the SAME solid as the reflected sketch
        // extruded the other way round. The profile is an L rather than a square, because a
        // section symmetric in the mirror plane cannot show that the SECTION was reflected
        // too — the recorded "a mirror-symmetric fixture can be secretly benign" trap.
        var original = Shape.Extrude(Ell(), Height, twist: Twist);
        var mirrored = original.Mirror((0, 0, 0), (0, 1, 0));
        var reference = Shape.Extrude(EllMirroredInY(), Height, twist: -Twist);

        var mirroredMesh = BRepTessellator.Tessellate(mirrored.ToBrep(), 64, 24);
        var referenceMesh = BRepTessellator.Tessellate(reference.ToBrep(), 64, 24);
        var originalMesh = BRepTessellator.Tessellate(original.ToBrep(), 64, 24);
        Assert.True(mirroredMesh.IsClosed);

        // The claim, on the point SET: a volume comparison passes a mirror that forgot the
        // sign, since a reflection preserves volume.
        var referencePoints = referenceMesh.Vertices.Select(v => v.Position).ToList();
        double toReference = Worst(mirroredMesh, referencePoints);
        Assert.True(toReference < 1e-9,
            $"mirrored vertices are {toReference:E3} from the reflected declaration's");

        // The mutation that proves it: forgetting to negate the twist leaves the ORIGINAL
        // solid (the reflection is spent once in the placement), 11.19 away from this one.
        var originalPoints = originalMesh.Vertices.Select(v => v.Position).ToList();
        Assert.True(Worst(mirroredMesh, originalPoints) > 1,
            "an un-negated twist would be indistinguishable from the original");
    }

    /// <summary>An L section: no mirror symmetry in y.</summary>
    private static Sketch Ell() => Sketch.Polygon([
        new Vector2d(-10, -10), new Vector2d(10, -10), new Vector2d(10, -2),
        new Vector2d(-2, -2), new Vector2d(-2, 10), new Vector2d(-10, 10)]);

    private static Sketch EllMirroredInY() => Sketch.Polygon([
        new Vector2d(-10, 10), new Vector2d(10, 10), new Vector2d(10, 2),
        new Vector2d(-2, 2), new Vector2d(-2, -10), new Vector2d(-10, -10)]);

    private static double Worst(HalfEdgeMesh mesh, IReadOnlyList<Vector3d> wanted) =>
        mesh.Vertices.Max(v => wanted.Min(w => w.DistanceTo(v.Position)));
}
