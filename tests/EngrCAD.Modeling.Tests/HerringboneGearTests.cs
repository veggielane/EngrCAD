using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class HerringboneGearTests
{
    // ---- the section angle law, as arithmetic by two routes ----

    [Fact]
    public void SectionAngleLaw_IsAFunctionOfDistanceFromTheMidPlane()
    {
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20, beta = 20;
        double half = width / 2;
        double twist = HerringboneGears.HalfTwist(spec, width, beta);

        // Route one: the helix definition, twist = height·tan β / r.
        double expected = half * Math.Tan(beta * Math.PI / 180) / (spec.PitchDiameter / 2);
        Assert.True(Math.Abs(twist - expected) < 1e-15, $"{twist} vs {expected}");

        // Route two: HelicalGearGeometry, which HerringboneGears delegates to.
        Assert.Equal(HelicalGearGeometry.Twist(spec.PitchDiameter / 2, half, beta), twist);

        // The law is Λ-shaped: 0 at both faces, `twist` at the apex, and — the property
        // that makes the mid-plane a mirror plane — equal at z and W − z BY FORM.
        Assert.True(Math.Abs(HerringboneGears.SectionAngleAt(spec, width, beta, 0)) < 1e-15);
        Assert.True(Math.Abs(HerringboneGears.SectionAngleAt(spec, width, beta, width)) < 1e-15);
        Assert.True(Math.Abs(HerringboneGears.SectionAngleAt(spec, width, beta, half) - twist) < 1e-15);
        for (double d = 0.5; d < half; d += 1.7)
        {
            // Not an exact comparison, and the reason is the TEST's arithmetic rather
            // than the law's: (half − d) − half and (half + d) − half differ in their
            // last bits whenever half ± d rounds, so the two |z − half| are not the same
            // double. The law is a function of |z − half| by FORM; this checks that the
            // form was not rewritten into something that merely looks symmetric.
            double below = HerringboneGears.SectionAngleAt(spec, width, beta, half - d);
            double above = HerringboneGears.SectionAngleAt(spec, width, beta, half + d);
            Assert.True(Math.Abs(below - above) < 1e-15, $"{below} vs {above} at d = {d}");
        }

        // A herringbone's own half is an ordinary helical gear over half the face width,
        // so the two factories must agree about how far its section has turned.
        Assert.Equal(HelicalGearGeometry.Twist(spec.PitchDiameter / 2, half, beta), twist);
    }

    // ---- the mirror identity: the solid IS its own reflection in its mid-plane ----

    [Fact]
    public void Herringbone_IsExactlyMirrorSymmetricAboutItsMidPlane()
    {
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20;
        var shape = HerringboneGears.Herringbone(spec, width, helixAngleDegrees: 20, boreDiameter: 10);
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);

        // BIT-exact, not within a tolerance: the apex ring's vertices are fixed points of
        // z → W − z and every other vertex is placed by that reflection, so the vertex set
        // is invariant exactly. A tolerance here would accept a weld that had drifted.
        var positions = mesh.Vertices.Select(v => v.Position).ToArray();
        var set = new HashSet<Vector3d>(positions);
        int fixedPoints = 0;
        foreach (var p in positions)
        {
            var mirrored = new Vector3d(p.X, p.Y, width - p.Z);
            Assert.True(set.Contains(mirrored), $"vertex {p} has no exact mirror {mirrored}");
            if (p.Z == width - p.Z)
                fixedPoints++;
        }
        // The apex ring is genuinely shared rather than duplicated: its vertices are the
        // fixed points, and there must be some (a solid welded from two separate copies
        // would have twice as many vertices there and none of them shared).
        Assert.True(fixedPoints > 0, "no vertex lies on the mid-plane");
        Assert.Equal(positions.Length, set.Count);   // no duplicate positions anywhere
    }

    // ---- volume: the weld adds and removes nothing ----

    [Fact]
    public void Herringbone_Volume_IsTheSectionAreaTimesTheFaceWidth()
    {
        var spec = new GearSpec(module: 2, teeth: 20, pressureAngleDegrees: 20, profileShift: 0.1);
        var profile = Gears.Spur(spec);
        const double width = 20, bore = 10;
        var mesh = HerringboneGears.Herringbone(spec, width, 20, boreDiameter: bore).ToMesh();

        Assert.True(mesh.IsClosed);
        double expected = (profile.ClosedFormArea - Math.PI * bore * bore / 4) * width;
        double relative = Math.Abs(mesh.Volume() - expected) / expected;
        // A twisted sweep preserves the section area exactly; the mesh's own deficit is
        // the arc-flattening chord error, the same band the helical gear is held to.
        Assert.True(relative < 0.02, $"volume {mesh.Volume()} vs {expected} ({relative:0.###e0} relative)");
    }

    [Fact]
    public void Herringbone_VolumeConvergesSecondOrderInSlices()
    {
        // The recorded twisted-extrude lesson: without twist-matched profile subdivision
        // a section sweep converges only FIRST order in the slice count. A gear tooth
        // profile is already finely segmented, so this MEASURES the order rather than
        // assuming the many segments make it moot.
        //
        // Measured on SUCCESSIVE DIFFERENCES rather than against the analytic area, and
        // that is not a convenience: the mesh also carries the arc-flattening deficit,
        // which is CONSTANT in the slice count, so an error measured against the exact
        // volume approaches that floor and its ratios sag however good the order is
        // (measured 3.41 then 2.97 that way). Differencing cancels any slice-independent
        // term.
        //
        // The threshold is 3.0 rather than 4.0 because the measured order is not a clean
        // 2 and the honest reason is stated rather than tuned around: swept at
        // SegmentsPerCircle 256 the difference ratios run 3.63 / 3.39 / 3.07 / 2.73 over
        // slices 2 → 64, and at 1024 they run 3.86 / 3.76 / 3.57 / 3.29 — the same
        // sequence, further from its floor. So the finer the flattening the closer to 4,
        // and this test reads the COARSE-slice end where the slice error dominates. What
        // it is here to catch is a regression to FIRST order (ratio ~2), which is what
        // losing the twist-matched profile subdivision would produce.
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20;
        var quality = new MeshQuality { SegmentsPerCircle = 256 };

        double Volume(int slices) => HerringboneGears
            .Herringbone(spec, width, 30, slicesPerHalf: slices, quality: quality)
            .ToMesh(quality).Volume();

        double v2 = Volume(2), v4 = Volume(4), v8 = Volume(8), v16 = Volume(16);
        double d1 = Math.Abs(v4 - v2), d2 = Math.Abs(v8 - v4), d3 = Math.Abs(v16 - v8);
        double r1 = d1 / d2, r2 = d2 / d3;
        Assert.True(r1 > 3.0 && r2 > 3.0,
            $"steps {d1:0.###e0} / {d2:0.###e0} / {d3:0.###e0}, ratios {r1:0.###} / {r2:0.###}");
    }

    // ---- the two halves' helix angles, measured off the solid ----

    [Fact]
    public void Herringbone_HalvesCarryEqualAndOppositeHelixAngles()
    {
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20, beta = 20;
        double half = width / 2, r = spec.PitchDiameter / 2;
        var shape = HerringboneGears.Herringbone(
            spec, width, beta, slicesPerHalf: 24,
            quality: new MeshQuality { SegmentsPerCircle = 256 });

        // Flank angular positions on the pitch circle, read off actual transverse
        // sections of the built solid. Differences between two heights cancel the
        // section polygon's own (systematic, rotation-invariant) chord bias, so the
        // measured rotation rate is the geometry's and not the mesh's.
        double[] heights = [1.3, 6.7, half + (half - 6.7), half + (half - 1.3)];
        var angles = heights
            .Select(z => MeasureFlankAngle(shape, spec, z,
                HerringboneGears.SectionAngleAt(spec, width, beta, z)))
            .ToArray();

        // Lower half: dθ/dz = tan β / r, positive (a right-hand helix).
        double lowerRate = (angles[1] - angles[0]) / (heights[1] - heights[0]);
        double upperRate = (angles[3] - angles[2]) / (heights[3] - heights[2]);
        double expected = Math.Tan(beta * Math.PI / 180) / r;
        Assert.True(Math.Abs(lowerRate - expected) < 2e-4 * Math.Abs(expected) + 1e-6,
            $"lower rate {lowerRate:0.######} vs {expected:0.######}");
        Assert.True(Math.Abs(upperRate + expected) < 2e-4 * Math.Abs(expected) + 1e-6,
            $"upper rate {upperRate:0.######} vs {-expected:0.######}");

        // Stated as helix ANGLES, which is what a thrust calculation reads: equal and
        // opposite, so the two axial components cancel.
        double lowerBeta = Math.Atan(lowerRate * r) * 180 / Math.PI;
        double upperBeta = Math.Atan(upperRate * r) * 180 / Math.PI;
        Assert.True(Math.Abs(lowerBeta - beta) < 0.01, $"lower helix angle {lowerBeta:0.####}");
        Assert.True(Math.Abs(upperBeta + beta) < 0.01, $"upper helix angle {upperBeta:0.####}");

        // Mirror symmetry again, this time as a MEASUREMENT rather than a vertex-set
        // identity: the two halves' sections at equal distances from the apex agree.
        Assert.True(Math.Abs(angles[0] - angles[3]) < 1e-6, $"{angles[0]} vs {angles[3]}");
        Assert.True(Math.Abs(angles[1] - angles[2]) < 1e-6, $"{angles[1]} vs {angles[2]}");
    }

    [Fact]
    public void SectionInstrument_ReadsTheSolidRatherThanItsSeed()
    {
        // The mutation check, and it is deliberately seeded WRONG: the tooth is located
        // from the 20-degree law while the solid is built at 30, so the seed picks the
        // right tooth and contributes nothing else. An instrument reporting its seed
        // would answer 20.
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20;
        var shape = HerringboneGears.Herringbone(
            spec, width, 30, slicesPerHalf: 24, quality: new MeshQuality { SegmentsPerCircle = 256 });
        double a1 = MeasureFlankAngle(shape, spec, 1.3, HerringboneGears.SectionAngleAt(spec, width, 20, 1.3));
        double a2 = MeasureFlankAngle(shape, spec, 6.7, HerringboneGears.SectionAngleAt(spec, width, 20, 6.7));
        double measured = Math.Atan((a2 - a1) / (6.7 - 1.3) * (spec.PitchDiameter / 2)) * 180 / Math.PI;
        Assert.True(Math.Abs(measured - 30) < 0.01, $"measured {measured:0.####} on a 30 degree gear");
        Assert.True(Math.Abs(measured - 20) > 5, "the instrument cannot tell 30 from 20");
    }

    // ---- the apex relief groove: why it is NOT a parameter yet ----

    [Fact]
    public void SubtractingAnAxialBandFromAHerringbone_StillFails()
    {
        // The apex relief groove a hobbed double-helical gear carries is material
        // genuinely REMOVED, so it wants a boolean rather than another weld — and the
        // boolean does not survive gear geometry. This pins the measurement so the filed
        // follow-up cannot rot into a guess, and so the day a boolean fix lands the
        // groove becomes available rather than forgotten.
        //
        // The B-Rep half of the same finding is NOT run here and is recorded in
        // HerringboneGears' remarks instead: the identical band against an ordinary SPUR
        // gear fails the B-Rep boolean as an unclosed solid with 1522 unpaired edges,
        // which is what shows this is gear geometry rather than the herringbone's weld.
        // It costs ~23 s in Release and several minutes in Debug, which is not a price a
        // unit suite should pay to re-learn a fact prose can carry.
        var spec = new GearSpec(module: 2, teeth: 20);
        var quality = new MeshQuality { SegmentsPerCircle = 32 };
        var band = SketchPlane.At(new Vector3d(0, 0, 8.5), Vector3d.UnitX, Vector3d.UnitY);
        var annulus = Shape.Extrude(Sketch.Circle(24).WithHole(Sketch.Circle(17)), 3, band);

        var herringbone = HerringboneGears.Herringbone(spec, 20, 20, quality: quality);
        var failure = Record.Exception(() => herringbone.Subtract(annulus).ToMesh(quality));
        Assert.NotNull(failure);
        Assert.Contains("Exact mesh boolean", failure.Message);
    }

    // ---- refusals, by name ----

    [Fact]
    public void ZeroHelixAngle_RefusedByName()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            HerringboneGears.Herringbone(new GearSpec(2, 20), 20, 0));
        Assert.Contains("spur gear", ex.Message);
        Assert.Contains("SpurGear", ex.Message);
    }

    [Fact]
    public void BoreReachingTheRootCircle_Refused()
    {
        var spec = new GearSpec(2, 20);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            HerringboneGears.Herringbone(spec, 20, 20, boreDiameter: spec.RootDiameter));
        Assert.Contains("root circle", ex.Message);
    }

    [Fact]
    public void HelixAngleBeyondTheAdmittedBand_Refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HerringboneGears.Herringbone(new GearSpec(2, 20), 20, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HerringboneGears.Herringbone(new GearSpec(2, 20), 20, -75));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The angular position of the +X tooth's counter-clockwise flank where it crosses
    /// the pitch circle, read off a real transverse section of the solid at height
    /// <paramref name="z"/> — the tooth-thickness bisection instrument, applied to a
    /// sectioned mesh instead of a sketch.
    /// </summary>
    private static double MeasureFlankAngle(Shape shape, GearSpec spec, double z, double seedAngle)
    {
        var plane = SketchPlane.At(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var regions = shape.Section(plane);
        var region = regions.OrderByDescending(x => Math.Abs(x.Area)).First();

        double r = spec.PitchDiameter / 2;
        double pitchAngle = 2 * Math.PI / spec.Teeth;

        // The seed only says WHICH tooth to read; the answer is bisected off the
        // polygon's own parity test.
        double centre = seedAngle;
        bool Inside(double theta) => region.Contains(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta)));

        // Locate a point genuinely inside the tooth (the seed may be a fraction of a
        // tooth off if the caller's helix angle differs from the seed's).
        if (!Inside(centre))
        {
            double found = double.NaN;
            for (int i = 1; i <= 400 && double.IsNaN(found); i++)
            {
                double step = i * pitchAngle / 200;
                if (Inside(centre + step))
                    found = centre + step;
                else if (Inside(centre - step))
                    found = centre - step;
            }
            Assert.False(double.IsNaN(found), $"no material found on the pitch circle at z = {z}");
            centre = found;
        }

        double inside = centre, outside = centre;
        while (Inside(outside))
        {
            inside = outside;
            outside += pitchAngle / 400;
            Assert.True(outside - centre < pitchAngle, $"no flank found at z = {z}");
        }
        for (int i = 0; i < 60; i++)
        {
            double mid = (inside + outside) / 2;
            if (Inside(mid))
                inside = mid;
            else
                outside = mid;
        }
        return (inside + outside) / 2;
    }
}
