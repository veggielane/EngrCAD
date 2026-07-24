using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// CPU-side section-isoline geometry (SectionContours — no GL, so no offscreen-gl
/// collection needed). Radius tolerances are derived from the sampling: the build
/// grid targets ~160 cells across the padded part bounds (cell ~ 0.08 for a
/// radius-5 sphere), and interpolation error is O(cell^2 * curvature), so 1e-2 is
/// two orders above the analytic bound plus float32 vertex rounding.
/// </summary>
public class SectionContourTests
{
    private const double RadiusTolerance = 1e-2;

    private static SectionContourGeometry BuildSingle(
        Part part, Frame3d plane, bool visible = true, Matrix4d? world = null,
        Dictionary<Part, Sdf?>? cache = null) =>
        SectionContours.Build(
            [new PartInstance(part, world ?? Matrix4d.Identity, part.Name)],
            [visible], plane, cache ?? []);

    [Fact]
    public void PlaneFrame_CardinalAxes_UseNaturalInPlaneAxes()
    {
        var z = SectionContours.PlaneFrame(Vector3d.UnitZ, 5);
        Assert.Equal(new Vector3d(0, 0, 5), z.Origin);
        Assert.Equal(Vector3d.UnitX, z.X);
        Assert.Equal(Vector3d.UnitY, z.Y);
        Assert.Equal(Vector3d.UnitZ, z.Z);

        var x = SectionContours.PlaneFrame(Vector3d.UnitX, 2);
        Assert.Equal(new Vector3d(2, 0, 0), x.Origin);
        Assert.Equal(Vector3d.UnitX, x.Z);

        var y = SectionContours.PlaneFrame(Vector3d.UnitY, -1);
        Assert.Equal(new Vector3d(0, -1, 0), y.Origin);
        Assert.Equal(Vector3d.UnitY, y.Z);
    }

    [Fact]
    public void Build_SdfSphere_ZeroContourIsTheSurfaceCrossSection()
    {
        var part = new Part("sphere", Sdf.Sphere(5));
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitZ, 0));

        Assert.Equal(1, geometry.PartCount);
        Assert.True(geometry.Spacing > 0);
        Assert.True(geometry.ZeroVertices.Length > 0);
        Assert.Equal(0, geometry.ZeroVertices.Length % 6);   // two xyz vertices per segment
        Assert.True(geometry.PositiveVertices.Length > 0);   // outside-field rings
        Assert.True(geometry.NegativeVertices.Length > 0);   // inside-material rings

        for (int v = 0; v < geometry.ZeroVertices.Length; v += 3)
        {
            double radius = Math.Sqrt(
                geometry.ZeroVertices[v] * (double)geometry.ZeroVertices[v]
                + geometry.ZeroVertices[v + 1] * (double)geometry.ZeroVertices[v + 1]);
            Assert.Equal(5, radius, RadiusTolerance);

            // Lines are lifted to the visible side of the clip (dot(p, axis) > offset
            // discards): strictly below the plane, by far less than one level spacing.
            double zVertex = geometry.ZeroVertices[v + 2];
            Assert.True(zVertex < 0);
            Assert.True(zVertex > -geometry.Spacing);
        }
    }

    [Fact]
    public void Build_TransformedInstance_ContoursFollowThePlacement()
    {
        var part = new Part("sphere", Sdf.Sphere(5));
        var world = Matrix4d.CreateTranslation(new Vector3d(10, 0, 0));
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitZ, 0), world: world);

        Assert.Equal(1, geometry.PartCount);
        for (int v = 0; v < geometry.ZeroVertices.Length; v += 3)
        {
            double dx = geometry.ZeroVertices[v] - 10.0;
            double dy = geometry.ZeroVertices[v + 1];
            Assert.Equal(5, Math.Sqrt(dx * dx + dy * dy), RadiusTolerance);
        }
    }

    [Fact]
    public void Build_PlaneMissingThePart_IsEmpty()
    {
        var part = new Part("sphere", Sdf.Sphere(5));
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitZ, 20));
        Assert.Equal(0, geometry.PartCount);
        Assert.Empty(geometry.ZeroVertices);
    }

    [Fact]
    public void Build_HiddenInstance_IsSkipped()
    {
        var part = new Part("sphere", Sdf.Sphere(5));
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitZ, 0), visible: false);
        Assert.Equal(0, geometry.PartCount);
    }

    [Fact]
    public void Build_MeshPart_HasNoImplicitRoute()
    {
        var part = new Part("box", Shape.Box(2, 2, 2).ToMesh());
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitZ, 0));
        Assert.Equal(0, geometry.PartCount);
    }

    [Fact]
    public void Build_ShapePart_UsesTheImplicitLowering()
    {
        var part = new Part("sphere", Shape.Sphere(5));
        var cache = new Dictionary<Part, Sdf?>();
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitZ, 0), cache: cache);

        Assert.Equal(1, geometry.PartCount);
        Assert.True(geometry.ZeroVertices.Length > 0);
        // The lowering happened once and is cached by part reference.
        Assert.NotNull(cache[part]);
        Assert.Same(cache[part], SectionContours.SdfRoute(part, cache));
    }

    [Fact]
    public void SdfRoute_LoweringFailure_IsReportedOnce()
    {
        // A Shape wrapping an OPEN mesh claims an implicit route (mesh -> implicit is
        // Bridged through MeshSdf) but the lowering throws (MeshSdf requires a closed
        // mesh). The failure must surface through the report sink — otherwise the
        // part is silently isoline-less — and the cached null keeps it to one report.
        var open = EngrCAD.Mesh.HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }]);
        var part = new Part("open", Shape.From(open));
        var cache = new Dictionary<Part, Sdf?>();
        var reports = new List<string>();

        Assert.Null(SectionContours.SdfRoute(part, cache, reports.Add));
        var message = Assert.Single(reports);
        Assert.Contains("'open'", message);
        Assert.Contains("lowering failed", message);

        // Cached: the second query neither re-lowers nor re-reports.
        Assert.Null(SectionContours.SdfRoute(part, cache, reports.Add));
        Assert.Single(reports);

        // And a whole Build over the part reports through the same sink without
        // producing geometry (primed via a fresh cache to exercise the Build path).
        reports.Clear();
        var geometry = SectionContours.Build(
            [new PartInstance(part, Matrix4d.Identity, part.Name)],
            [true], SectionContours.PlaneFrame(Vector3d.UnitZ, 0), [], reports.Add);
        Assert.Equal(0, geometry.PartCount);
        Assert.Single(reports);
    }

    [Fact]
    public void Build_XAxisSection_ProducesContoursOnTheYzPlane()
    {
        // The extraction is plane-general: an X-axis section of a sphere yields the
        // same circle, with vertices lifted along -X instead of -Z.
        var part = new Part("sphere", Sdf.Sphere(5));
        var geometry = BuildSingle(part, SectionContours.PlaneFrame(Vector3d.UnitX, 0));

        Assert.Equal(1, geometry.PartCount);
        for (int v = 0; v < geometry.ZeroVertices.Length; v += 3)
        {
            double dy = geometry.ZeroVertices[v + 1];
            double dz = geometry.ZeroVertices[v + 2];
            Assert.Equal(5, Math.Sqrt(dy * dy + dz * dz), RadiusTolerance);
            Assert.True(geometry.ZeroVertices[v] < 0);   // lifted to the kept side
        }
    }
}
