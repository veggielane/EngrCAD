using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The two consumers of <see cref="BrepSilhouette"/> in the modelling layer:
/// <see cref="HiddenLineOptions.ExactSilhouettes"/> and
/// <see cref="Shape.SilhouetteCurves"/>. The claim that carries the feature is the
/// EXACT-vs-MESH comparison — a mesh silhouette is an inscribed polyline, so the exact
/// outline of a cylinder is measurably WIDER and lands on the closed form.
/// </summary>
public class ExactSilhouetteTests
{
    private static Scene CylinderScene()
    {
        var scene = new Scene();
        scene.Add(new Part("post", Shape.Cylinder(10, 30)));
        return scene;
    }

    [Fact]
    public void TheDefaultDrawingIsUnchangedRunForRun()
    {
        // Opt-in means opt-in: a caller that says nothing gets exactly the line work it
        // always did, asserted point-for-point against an explicit `false`.
        var scene = CylinderScene();
        var view = StandardViews.SheetFrame(-Vector3d.UnitY);
        var plain = HiddenLineRemoval.Project(scene, view);
        var stated = HiddenLineRemoval.Project(scene, view, new HiddenLineOptions { ExactSilhouettes = false });

        Assert.Equal(plain.Runs.Count, stated.Runs.Count);
        for (int i = 0; i < plain.Runs.Count; i++)
        {
            Assert.Equal(plain.Runs[i].Source, stated.Runs[i].Source);
            Assert.Equal(plain.Runs[i].Points.Count, stated.Runs[i].Points.Count);
            for (int j = 0; j < plain.Runs[i].Points.Count; j++)
            {
                Assert.Equal(plain.Runs[i].Points[j].X, stated.Runs[i].Points[j].X, 12);
                Assert.Equal(plain.Runs[i].Points[j].Y, stated.Runs[i].Points[j].Y, 12);
            }
        }
    }

    [Fact]
    public void TheExactOutlineIsWiderThanTheMeshOneAndLandsOnTheClosedForm()
    {
        // A cylinder seen from the side silhouettes to two rulings exactly a DIAMETER
        // apart, at ANY view angle off its axis. The mesh route returns its n-gon's own
        // projected width instead — 2r·cos(pi/n − phi) for a view phi off the nearest
        // vertex — a FLOOR rather than a tolerance.
        //
        // The view is deliberately turned HALF a facet: a UV cylinder's vertices lie ON
        // the exact circle, so a view lined up with one reads a width of exactly 2r and
        // the mesh route looks perfect. The difference this test exists to measure is
        // ALIGNMENT-dependent, and an axis-aligned fixture cannot see it at all.
        const double radius = 10;
        const int segments = 16;
        double half = Math.PI / segments;
        var scene = CylinderScene();
        var view = StandardViews.SheetFrame(new Vector3d(-Math.Sin(half), -Math.Cos(half), 0));
        var quality = new MeshQuality { SegmentsPerCircle = segments };

        double Width(bool exact)
        {
            var result = HiddenLineRemoval.Project(
                scene, view, new HiddenLineOptions { Quality = quality, ExactSilhouettes = exact });
            var silhouette = result.Runs.Where(r => r.Source == EdgeSource.Silhouette).ToList();
            Assert.NotEmpty(silhouette);
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var run in silhouette)
            {
                foreach (var p in run.Points)
                {
                    min = Math.Min(min, p.X);
                    max = Math.Max(max, p.X);
                }
            }
            return max - min;
        }

        double mesh = Width(exact: false);
        double exact = Width(exact: true);
        double inscribed = 2 * radius * Math.Cos(half);

        Assert.Equal(2 * radius, exact, 9);
        Assert.Equal(inscribed, mesh, 6);
        Assert.True(exact > mesh + 0.1, $"exact {exact:F6} against mesh {mesh:F6}");
    }

    [Fact]
    public void APartWithNoBRepFallsBackToTheMeshOutline()
    {
        // The all-or-nothing rule's safe direction: a part the kernel cannot lower keeps
        // the outline it always had rather than losing it.
        var scene = new Scene();
        scene.Add(new Part("blob", Shape.Sphere(8).SmoothUnion(Shape.Sphere(8).Translate(10, 0, 0), 3)));
        var view = StandardViews.SheetFrame(-Vector3d.UnitY);
        var result = HiddenLineRemoval.Project(scene, view, new HiddenLineOptions { ExactSilhouettes = true });
        Assert.Contains(result.Runs, r => r.Source == EdgeSource.Silhouette);
    }

    [Fact]
    public void ShapeSilhouetteCurvesReturnsTheExactOutline()
    {
        var plane = SketchPlane.At((0, 0, 0), Vector3d.UnitZ, Vector3d.UnitX);   // viewed along +Y
        var result = Shape.Cylinder(10, 30).SilhouetteCurves(plane);
        var rulings = result.Curves.Where(c => c.Face.Surface is not PlaneSurface).ToList();
        Assert.Equal(2, rulings.Count);
        Assert.True(result.MaxDeviation < 1e-9, $"{result.MaxDeviation:E3}");

        var a = rulings[0].Curve.PointAt(0.5);
        var b = rulings[1].Curve.PointAt(0.5);
        var offset = b - a;
        var d = plane.Frame.Z;
        Assert.Equal(20.0, (offset - d * offset.Dot(d)).Length, 9);
    }

    [Fact]
    public void ASphereSilhouetteCurveIsACircleOfExactlyItsOwnRadius()
    {
        // The headline: the mesh route returns the inscribed polygon of whatever
        // tessellation it was handed; the exact route returns the circle.
        const double radius = 12;
        var plane = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var result = Shape.Sphere(radius).SilhouetteCurves(plane);
        Assert.NotEmpty(result.Curves);
        foreach (var curve in result.Curves)
        {
            var domain = curve.Curve.Domain;
            for (int i = 0; i <= 64; i++)
                Assert.Equal(radius, curve.Curve.PointAt(domain.ParameterAt(i / 64.0)).Length, 9);
        }
    }

    [Fact]
    public void SilhouetteCurvesRefusesAShapeWithNoBRepForm()
    {
        var blob = Shape.Sphere(8).SmoothUnion(Shape.Sphere(8).Translate(10, 0, 0), 3);
        var plane = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        Assert.Throws<ShapeConversionException>(() => { blob.SilhouetteCurves(plane); });
    }
}
