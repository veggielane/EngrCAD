using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// B-Rep mass properties against closed forms. Planar-faced solids must be exact (the
/// tessellation covers a planar face exactly, so the divergence-theorem sum is an
/// identity); curved solids are held to a stated convergence rate and a measured error at
/// the default accuracy, because the route is tessellate-then-sum and says so.
/// </summary>
public class BrepMassPropertiesTests
{
    private static void AssertClose(double expected, double actual, double relative, string what)
    {
        double scale = Math.Max(Math.Abs(expected), 1e-300);
        Assert.True(Math.Abs(actual - expected) <= relative * scale,
            $"{what}: expected {expected:G17}, got {actual:G17} (relative error {Math.Abs(actual - expected) / scale:G3} > {relative:G3}).");
    }

    private static void AssertClose(in Vector3d expected, in Vector3d actual, double absolute, string what) =>
        Assert.True(expected.DistanceTo(actual) <= absolute,
            $"{what}: expected {expected}, got {actual} (distance {expected.DistanceTo(actual):G3}).");

    // ---- planar-faced solids: exact ----

    [Fact]
    public void Box_IsExact()
    {
        const double a = 4, b = 6, c = 10, density = 7.85e-6;   // steel in kg/mm³
        var solid = SolidFactory.MakeBox(new Aabb((0, 0, 0), (a, b, c)));
        var mp = BrepMassProperties.Compute(solid, density);

        double volume = a * b * c;
        AssertClose(volume, mp.Volume, 1e-12, "volume");
        AssertClose(2 * (a * b + b * c + c * a), mp.SurfaceArea, 1e-12, "area");
        AssertClose(density * volume, mp.Mass, 1e-12, "mass");
        AssertClose(new Vector3d(a / 2, b / 2, c / 2), mp.Centroid, 1e-11, "centroid");

        double mass = density * volume;
        var inertia = mp.Inertia;
        AssertClose(mass * (b * b + c * c) / 12, inertia.Xx, 1e-11, "Ixx");
        AssertClose(mass * (a * a + c * c) / 12, inertia.Yy, 1e-11, "Iyy");
        AssertClose(mass * (a * a + b * b) / 12, inertia.Zz, 1e-11, "Izz");
        Assert.True(Math.Abs(inertia.Xy) <= 1e-11 * inertia.Xx, $"Ixy should vanish, got {inertia.Xy:G6}.");
    }

    [Fact]
    public void PlanarFacedSolid_IsAccuracyIndependent()
    {
        var solid = SolidFactory.MakeBox(new Aabb((-1, -2, -3), (4, 5, 6)));
        var low = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 8, CurveSamples = 4, Extrapolate = false });
        var high = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 200, CurveSamples = 90, Extrapolate = false });

        // Same number to round-off: a planar face's triangulation covers it exactly, so
        // there is no discretization error to reduce.
        AssertClose(low.Volume, high.Volume, 1e-13, "volume is accuracy independent");
        AssertClose(low.SurfaceArea, high.SurfaceArea, 1e-13, "area is accuracy independent");
        AssertClose(low.Centroid, high.Centroid, 1e-11, "centroid is accuracy independent");
    }

    // ---- curved solids: the analytic limit and the convergence rate ----

    [Fact]
    public void Cylinder_ApproachesClosedFormAndConvergesQuadratically()
    {
        const double r = 3, h = 12, density = 2.7e-6;   // aluminium in kg/mm³
        var solid = SolidFactory.MakeCylinder(r, h);

        double exactVolume = Math.PI * r * r * h;
        var coarse = BrepMassProperties.Compute(solid, density, new BrepMassPropertyOptions { SegmentsPerCircle = 48, CurveSamples = 24, Extrapolate = false });
        var fine = BrepMassProperties.Compute(solid, density, new BrepMassPropertyOptions { SegmentsPerCircle = 96, CurveSamples = 48, Extrapolate = false });

        double coarseError = Math.Abs(coarse.Volume - exactVolume) / exactVolume;
        double fineError = Math.Abs(fine.Volume - exactVolume) / exactVolume;
        Assert.True(coarse.Volume < exactVolume && fine.Volume < exactVolume,
            "An inscribed tessellation must under-estimate a convex curved solid.");
        double ratio = coarseError / fineError;
        Assert.True(ratio > 3.5 && ratio < 4.5, $"Volume error ratio {ratio:G3} is not the O(h²) ~4.");
        // Measured 7.1e-4 at 96 segments/circle — exactly the 2π²/3n² chord deficit of an
        // inscribed n-gon, which is where the documented figures come from.
        Assert.True(fineError < 1e-3, $"Volume error at 96 segments/circle is {fineError:G3}, above the documented 1e-3.");

        AssertClose(2 * Math.PI * r * (r + h), fine.SurfaceArea, 1e-3, "cylinder area");
        AssertClose(new Vector3d(0, 0, h / 2), fine.Centroid, 1e-9, "cylinder centroid");

        double mass = density * exactVolume;
        AssertClose(0.5 * mass * r * r, fine.Inertia.Zz, 2e-3, "axial inertia");
        AssertClose(mass * (3 * r * r + h * h) / 12, fine.Inertia.Xx, 2e-3, "transverse inertia");
    }

    [Fact]
    public void Sphere_MatchesClosedForm()
    {
        const double r = 5;
        var solid = SolidFactory.MakeSphere(r, (1, 2, 3));
        var mp = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 128, CurveSamples = 64, Extrapolate = false });

        double exactVolume = 4.0 / 3.0 * Math.PI * r * r * r;
        AssertClose(exactVolume, mp.Volume, 1e-3, "sphere volume");
        AssertClose(4 * Math.PI * r * r, mp.SurfaceArea, 1e-3, "sphere area");
        AssertClose(new Vector3d(1, 2, 3), mp.Centroid, 1e-8, "sphere centroid");

        double expected = 0.4 * mp.Mass * r * r;
        AssertClose(expected, mp.Inertia.Xx, 2e-3, "sphere Ixx");
        AssertClose(expected, mp.Inertia.Zz, 2e-3, "sphere Izz");
    }

    [Fact]
    public void Torus_MatchesClosedForm()
    {
        const double major = 10, minor = 2;
        var solid = SolidFactory.MakeTorus(major, minor);
        var mp = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 128, CurveSamples = 128, Extrapolate = false });

        // Pappus: V = 2π²Rr², A = 4π²Rr.
        AssertClose(2 * Math.PI * Math.PI * major * minor * minor, mp.Volume, 1e-3, "torus volume");
        AssertClose(4 * Math.PI * Math.PI * major * minor, mp.SurfaceArea, 1e-3, "torus area");
        AssertClose(Vector3d.Zero, mp.Centroid, 1e-8, "torus centroid");

        // Solid torus about its axis: I_z = m(R² + ¾r²); about a diameter: half that plus
        // the axial-thickness term, I_x = m(½R² + ⅝r²).
        double mass = mp.Mass;
        AssertClose(mass * (major * major + 0.75 * minor * minor), mp.Inertia.Zz, 3e-3, "torus axial inertia");
        AssertClose(mass * (0.5 * major * major + 0.625 * minor * minor), mp.Inertia.Xx, 3e-3, "torus diametral inertia");
    }

    [Fact]
    public void Cone_MatchesClosedForm()
    {
        const double r = 4, h = 9;
        var solid = SolidFactory.MakeCone(r, 0, h);
        var mp = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 128, CurveSamples = 64, Extrapolate = false });

        double exactVolume = Math.PI * r * r * h / 3;
        AssertClose(exactVolume, mp.Volume, 5e-4, "cone volume");
        // Centroid is a quarter of the height above the base.
        AssertClose(new Vector3d(0, 0, h / 4), mp.Centroid, 1e-3, "cone centroid");
        AssertClose(0.3 * mp.Mass * r * r, mp.Inertia.Zz, 2e-3, "cone axial inertia");
    }

    [Fact]
    public void Extrapolate_ImprovesACurvedSolidByOrdersOfMagnitude()
    {
        const double r = 3, h = 12;
        var solid = SolidFactory.MakeCylinder(r, h);
        double exactVolume = Math.PI * r * r * h;

        var plain = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 48, CurveSamples = 24, Extrapolate = false });
        var richardson = BrepMassProperties.Compute(
            solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 48, CurveSamples = 24, Extrapolate = true });

        double plainError = Math.Abs(plain.Volume - exactVolume) / exactVolume;
        double richardsonError = Math.Abs(richardson.Volume - exactVolume) / exactVolume;
        Assert.True(richardsonError < plainError / 30,
            $"Extrapolation only improved the volume error from {plainError:G3} to {richardsonError:G3}.");
        Assert.True(richardsonError < 1e-5, $"Extrapolated volume error {richardsonError:G3} exceeds 1e-5.");
    }

    [Fact]
    public void DefaultOptions_ReachThePublishedAccuracyOnCurvedSolids()
    {
        // The claim the XML docs make: ~1e-7 relative on curved solids out of the box.
        var cylinder = BrepMassProperties.Compute(SolidFactory.MakeCylinder(3, 12));
        AssertClose(Math.PI * 9 * 12, cylinder.Volume, 1e-6, "default-accuracy cylinder volume");
        AssertClose(2 * Math.PI * 3 * (3 + 12), cylinder.SurfaceArea, 1e-6, "default-accuracy cylinder area");

        var sphere = BrepMassProperties.Compute(SolidFactory.MakeSphere(5));
        AssertClose(4.0 / 3.0 * Math.PI * 125, sphere.Volume, 1e-6, "default-accuracy sphere volume");
        AssertClose(0.4 * sphere.Mass * 25, sphere.Inertia.Zz, 1e-5, "default-accuracy sphere inertia");
    }

    [Fact]
    public void Extrapolate_LeavesAPlanarFacedSolidExact()
    {
        var solid = SolidFactory.MakeBox(new Aabb((0, 0, 0), (3, 5, 7)));
        var mp = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { Extrapolate = true });
        AssertClose(105, mp.Volume, 1e-12, "extrapolated box volume");
        AssertClose(new Vector3d(1.5, 2.5, 3.5), mp.Centroid, 1e-11, "extrapolated box centroid");
    }

    // ---- through the boolean pipeline ----

    [Fact]
    public void DrilledPlate_MatchesTheAnalyticDifference()
    {
        const double w = 40, d = 30, t = 8, bore = 5;
        // Shape.Cylinder is centred on the origin, so length t + 4 overshoots both faces
        // and the boolean never sees a coplanar cap.
        var drilled = Shape.Box(new Aabb((0, 0, 0), (w, d, t)))
                      - Shape.Cylinder(bore, t + 4).Translate((w / 2, d / 2, t / 2));
        var solid = drilled.ToBrep();

        var mp = BrepMassProperties.Compute(solid, 1.0, new BrepMassPropertyOptions { SegmentsPerCircle = 128, CurveSamples = 64, Extrapolate = false });

        double exactVolume = w * d * t - Math.PI * bore * bore * t;
        AssertClose(exactVolume, mp.Volume, 2e-4, "drilled plate volume");
        // Symmetry: the bore is centred, so the centroid stays at the plate centre.
        AssertClose(new Vector3d(w / 2, d / 2, t / 2), mp.Centroid, 1e-6, "drilled plate centroid");

        // Inertia = plate minus bore, both about the shared centroid.
        double plateMass = w * d * t, boreMass = Math.PI * bore * bore * t;
        double plateIzz = plateMass * (w * w + d * d) / 12;
        double boreIzz = 0.5 * boreMass * bore * bore;
        AssertClose(plateIzz - boreIzz, mp.Inertia.Zz, 1e-3, "drilled plate Izz");

        // Extrapolation holds up on a boolean result whose bore wall is a trimmed face —
        // the case where a non-smooth tessellation error would have broken it.
        var extrapolated = BrepMassProperties.Compute(solid);
        AssertClose(exactVolume, extrapolated.Volume, 1e-6, "extrapolated drilled plate volume");
    }
}
