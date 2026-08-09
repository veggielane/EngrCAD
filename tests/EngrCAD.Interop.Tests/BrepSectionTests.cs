using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <see cref="BrepBoolean.Section"/> — the curve-only intersection wire (OCCT's
/// BRepAlgoAPI_Section). The oracle is a closed form where the section is analytic: a plane
/// perpendicular to a cylinder's axis cuts it in an exact circle, so a drilled-through plate's
/// section is two circles, and "exact" is provable by the sampled points sitting on the circle
/// to the WELD tier rather than to a chord sagitta (a tracer polyline would miss by ~1e-3).
/// </summary>
public class BrepSectionTests
{
    private static BrepSolid Plate() => SolidFactory.MakeBox(new Aabb((-20, -15, 0), (20, 15, 10)));

    // A Ø10 cylinder passing right through the plate (z in [-5, 15] against the plate's [0, 10]).
    private static BrepSolid ThroughBore() =>
        SolidFactory.MakeCylinder(5, 20).Transformed(Matrix4d.CreateTranslation((0, 0, -5)));

    private static double Radius(in Vector3d p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);

    [Fact]
    public void SectionOfADrilledPlateIsTwoExactCircles()
    {
        var section = BrepBoolean.Section(Plate(), ThroughBore());

        // Exactly two curves: the cylinder wall meets the plate's top and bottom caps. Its own
        // caps (at z = -5 and 15) are outside the plate, and the plate's side walls miss the
        // interior bore, so nothing else is in the section.
        Assert.Equal(2, section.Count);

        var zLevels = new List<double>();
        foreach (var curve in section)
        {
            Assert.True(curve.IsClosed, "a plane-perpendicular section of a cylinder is a full circle");

            // Every sampled point is EXACTLY on the radius-5 circle at a single z — to the weld
            // tier, which is what proves the curve is the analytic circle rather than a chorded
            // tracer polyline (whose interior samples would be a sagitta off, ~1e-3 here).
            double z0 = curve.PointAt(curve.Domain.Start).Z;
            for (int i = 0; i <= 64; i++)
            {
                var p = curve.PointAt(curve.Domain.ParameterAt(i / 64.0));
                Assert.Equal(5.0, Radius(p), 9);
                Assert.Equal(z0, p.Z, 9);
            }
            zLevels.Add(z0);
        }

        zLevels.Sort();
        Assert.Equal(0.0, zLevels[0], 9);   // the bottom cap
        Assert.Equal(10.0, zLevels[1], 9);  // the top cap
    }

    [Fact]
    public void SectionLengthIsTheClosedFormPerimeter()
    {
        // Total length = two circumferences = 2 * 2*pi*5. Measured by chords, which UNDERESTIMATE
        // an analytic circle by ~pi^2/(6N^2) of its length — 3.9e-4 at N = 512 over the two
        // circles — so the comparison is at 2 decimals and the sign of the residual is asserted
        // to confirm the wire is the circle rather than a coarser polyline that could overshoot.
        var section = BrepBoolean.Section(Plate(), ThroughBore());
        double total = 0;
        foreach (var curve in section)
        {
            var previous = curve.PointAt(curve.Domain.Start);
            for (int i = 1; i <= 512; i++)
            {
                var p = curve.PointAt(curve.Domain.ParameterAt(i / 512.0));
                total += previous.DistanceTo(p);
                previous = p;
            }
        }
        double exact = 2 * 2 * Math.PI * 5;
        Assert.Equal(exact, total, 2);
        Assert.True(total <= exact, "a chord polyline of a circle cannot exceed the true perimeter");
    }

    [Fact]
    public void SectionOfDisjointSolidsIsEmpty()
    {
        // Nothing meets nothing: the section is a value, and the value is no curves.
        var far = SolidFactory.MakeBox(new Aabb((100, 100, 100), (110, 110, 110)));
        Assert.Empty(BrepBoolean.Section(Plate(), far));
    }

    [Fact]
    public void SectionDoesNotConsumeItsInputs()
    {
        // Unlike the boolean operations, Section only measures — so the same solids section
        // twice and give the same answer, where a boolean would have split their faces in place.
        var a = Plate();
        var b = ThroughBore();
        Assert.Equal(2, BrepBoolean.Section(a, b).Count);
        Assert.Equal(2, BrepBoolean.Section(a, b).Count);
    }
}
