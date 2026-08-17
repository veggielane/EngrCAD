using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Frames and weldments: profiles along a skeleton with exact bisector-plane miters,
/// cut lengths on the BOM.
///
/// The volume oracle throughout is the PRISM-CUT identity: a prism cut by planes at
/// both ends has volume A · (axial distance between the planes' crossings of the
/// CENTROID fiber) — exact for any section, because the crossing is affine over the
/// section and integrates to its centroid value. For sections centred on the run line
/// the centroid fiber IS the run, so a mitred member's volume is exactly A·L; the
/// entry's rectangular-wedge closed form is asserted as well and drops out as the
/// special case.
/// </summary>
public class WeldmentTests
{
    private static double BrepVolume(Shape shape) =>
        BrepMassProperties.Compute(shape.ToBrep()).Volume;

    // ---- miters ----

    [Fact]
    public void MiteredRectangularMembers_HaveExactCentroidRuleVolumes()
    {
        // Two 50-long runs of solid 20x10 bar meeting at 90 degrees in the XY plane.
        var flat = FrameProfile.Flat(20, 10);
        var frame = Weldment.Build(flat,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
        ]);

        Assert.Equal(2, frame.Members.Count);
        foreach (var member in frame.Members)
        {
            // Planar faces only, so tessellate-then-sum is exact as an identity: the
            // miter plane passes through the joint on the centroid fiber, so V = A·L.
            Assert.Equal(flat.Area * 50, BrepVolume(member.Shape), 1e-9 * flat.Area * 50);
            Assert.True(member.Shape.ToMesh().IsClosed);
        }
    }

    [Fact]
    public void MiterWedge_MatchesTheClosedForm()
    {
        // The entry's derivation, asserted: at a 90-degree joint the miter tilts 45
        // degrees across the section width w, so the stock prism of the cut length
        // exceeds the mitred member by exactly the triangular wedge (w^2/2)·tan(45°)·h.
        var flat = FrameProfile.Flat(20, 10);
        var frame = Weldment.Build(flat,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
        ]);

        var member = frame.Members[0];
        // Cut length: to the miter's longest point, L + (w/2)·tan(45°).
        Assert.Equal(60, member.CutLength, 1e-9);
        double stockPrism = flat.Area * member.CutLength;
        double wedge = 20 * 20 / 2.0 * Math.Tan(Math.PI / 4) * 10;
        Assert.Equal(stockPrism - wedge, BrepVolume(member.Shape), 1e-9 * stockPrism);
    }

    [Fact]
    public void NonPerpendicularMiter_StillSatisfiesTheCentroidRule()
    {
        // A 120-degree included angle: the miter tilts 30 degrees from perpendicular.
        var flat = FrameProfile.Flat(20, 10);
        var turn = new Vector3d(50 + 50 * Math.Cos(Math.PI / 3), 50 * Math.Sin(Math.PI / 3), 0);
        var frame = Weldment.Build(flat,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), turn),
        ]);

        foreach (var member in frame.Members)
            Assert.Equal(flat.Area * 50, BrepVolume(member.Shape), 1e-9 * flat.Area * 50);
        // Cut length: L + (w/2)·tan(tilt), tilt = 90° − half the included angle = 30°.
        double expected = 50 + 10 * Math.Tan(Math.PI / 6);
        Assert.Equal(expected, frame.Members[0].CutLength, 1e-9);
        Assert.Equal(expected, frame.Members[1].CutLength, 1e-9);
    }

    [Fact]
    public void ClosedRectangularFrame_TotalVolumeAndMass_MatchTheClosedForms()
    {
        // A welded picture frame: SHS 40x3 on a 500x300 centreline rectangle in the XZ
        // plane. Every miter plane passes through a centreline corner, so each member is
        // exactly A·L and the total is A·perimeter — the mass closed form through
        // Materials the entry asks for.
        var shs = FrameProfile.Shs(40, 3);
        var frame = Weldment.Path(shs,
            [new Vector3d(0, 0, 0), new Vector3d(500, 0, 0), new Vector3d(500, 0, 300), new Vector3d(0, 0, 300)],
            closed: true,
            new WeldmentOptions { Up = Vector3d.UnitY, Material = Materials.Steel });

        Assert.Equal(4, frame.Members.Count);
        double total = 0;
        foreach (var member in frame.Members)
        {
            double volume = BrepVolume(member.Shape);
            double expected = shs.Area * (member.End - member.Start).Length;
            Assert.Equal(expected, volume, 1e-9 * expected);
            total += volume;
        }
        double perimeter = 2 * (500 + 300);
        Assert.Equal(shs.Area * perimeter, total, 1e-9 * shs.Area * perimeter);

        // Cut lengths against the skeleton's own arithmetic: centreline side + one
        // section width (half at each mitred corner).
        Assert.Equal([540, 340, 540, 340],
            frame.Members.Select(m => Math.Round(m.CutLength, 9)).ToArray());

        // Mass through Materials: density · A · perimeter, in grams.
        double massGrams = frame.Members.Sum(m => m.Part.MassGrams() ?? 0);
        double expectedGrams = ModelUnits.MassToGrams(Materials.Steel.Density * shs.Area * perimeter);
        Assert.Equal(expectedGrams, massGrams, 1e-9 * expectedGrams);
    }

    [Fact]
    public void MiteredTube_ConvergesOnTheCentroidRule_AndCutLengthIsClosedForm()
    {
        // A mitred round tube's cut is the exact plane∩cylinder ellipse, so the
        // tessellated volume must CONVERGE on A·L (a tracer polyline would be a fixed
        // floor — the recorded side-face-bore lesson). Tolerances derive from the chord
        // sagitta: (2π/n)²/8 relative on the radius terms.
        var tube = FrameProfile.RoundTube(26.9, 2.6);
        var frame = Weldment.Build(tube,
        [
            (new Vector3d(0, 0, 0), new Vector3d(60, 0, 0)),
            (new Vector3d(60, 0, 0), new Vector3d(60, 60, 0)),
        ]);

        var solid = frame.Members[0].Shape.ToBrep();
        double exact = tube.Area * 60;
        double error64 = Math.Abs(Volume(solid, 64) - exact) / exact;
        double error256 = Math.Abs(Volume(solid, 256) - exact) / exact;
        Assert.True(error256 < 1e-4, $"error at 256 segments = {error256:E3}");
        Assert.True(error256 < error64, $"not converging: {error64:E3} -> {error256:E3}");

        // Cut length: the extreme fiber of the circle under a 45-degree plane reaches
        // L + R·tan(45°) — closed form, exact.
        Assert.Equal(60 + 26.9 / 2, frame.Members[0].CutLength, 1e-9);

        static double Volume(EngrCAD.BRep.BrepSolid solid, int segments) =>
            BrepMassProperties.Compute(solid, options: new BrepMassPropertyOptions
            {
                SegmentsPerCircle = segments, Extrapolate = false,
            }).Volume;
    }

    [Fact]
    public void AngleProfile_OffCentroidRunLine_StillSatisfiesTheCentroidRule()
    {
        // The angle's heel sits ON the run line, so its centroid does not — the prism-cut
        // identity must use the centroid fiber's plane crossing, which for the L 40x4 at
        // a 90-degree miter is 50 − x̄ with x̄ = (a² + at − t²)/(2(2a − t)). Everything is
        // rational here: A = 304, x̄ = 1744/152, V = 304·50 − 2·1744 = 11712 exactly.
        var angle = FrameProfile.EqualAngle(40, 4);
        Assert.Equal(304, angle.Area, 1e-12);
        var frame = Weldment.Build(angle,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
        ]);

        foreach (var member in frame.Members)
            Assert.Equal(11712, BrepVolume(member.Shape), 1e-9 * 11712);
    }

    [Fact]
    public void FreeEnds_AreTheExtrusionsOwnCaps()
    {
        var angle = FrameProfile.EqualAngle(40, 4);
        var frame = Weldment.Build(angle, [(new Vector3d(0, 0, 0), new Vector3d(0, 0, 100))]);
        var member = frame.Members[0];
        Assert.Equal(angle.Area * 100, BrepVolume(member.Shape), 1e-9 * angle.Area * 100);
        Assert.Equal(100, member.CutLength, 1e-12);
    }

    [Fact]
    public void VerticalMember_TakesTheStatedRollFallback()
    {
        // The documented rule: a run parallel to Up falls back to +Z, +Y, +X — so a
        // vertical member under the default up gets profile y on world Y, x on world X.
        var frame = Weldment.Build(FrameProfile.Flat(20, 10),
            [(new Vector3d(0, 0, 0), new Vector3d(0, 0, 100))]);
        Assert.Equal(Vector3d.UnitX, frame.Members[0].Frame.X);
        Assert.Equal(Vector3d.UnitY, frame.Members[0].Frame.Y);
    }

    // ---- butt joints ----

    [Fact]
    public void ButtJoint_TrimsTheLaterRunBackToTheThroughWall()
    {
        // Run 0 (through) keeps its square end; run 1 is trimmed back by half the
        // through member's section width (10 of its 20).
        var flat = FrameProfile.Flat(20, 10);
        var frame = Weldment.Build(flat,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
        ], new WeldmentOptions { JointStyle = FrameJointStyle.Butt });

        Assert.Equal(flat.Area * 50, BrepVolume(frame.Members[0].Shape), 1e-9 * flat.Area * 50);
        Assert.Equal(50, frame.Members[0].CutLength, 1e-12);
        Assert.Equal(flat.Area * 40, BrepVolume(frame.Members[1].Shape), 1e-9 * flat.Area * 40);
        Assert.Equal(40, frame.Members[1].CutLength, 1e-9);
    }

    // ---- the BOM as a cut list ----

    [Fact]
    public void Bom_CarriesCutLengths_AndRollsIdenticalMembersUpByItem()
    {
        var shs = FrameProfile.Shs(40, 3);
        var frame = Weldment.Path(shs,
            [new Vector3d(0, 0, 0), new Vector3d(500, 0, 0), new Vector3d(500, 0, 300), new Vector3d(0, 0, 300)],
            closed: true,
            new WeldmentOptions { Up = Vector3d.UnitY, Material = Materials.Steel });
        var bom = Bom.For(frame.ToAssembly());

        // Each member is its own Part (reference identity is the document model's own
        // notion of "the same thing"), so 4 lines — and identical members share the NAME
        // "designation x cut length", which is exactly ByItem's rollup key.
        Assert.True(bom.HasCutLengths);
        Assert.Equal(4, bom.LineCount);
        var items = bom.ByItem();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(2, item.Quantity));
        Assert.Contains(items, item => item.Item == "SHS 40x40x3 x 540");
        Assert.Contains(items, item => item.Item == "SHS 40x40x3 x 340");

        // The text report gains a CUT column and a total-stock footer; the CSV a
        // CutLengthMm column.
        string text = bom.ToText();
        Assert.Contains("CUT (mm)", text);
        Assert.Contains("1760 mm of stock", text);
        Assert.Contains("CutLengthMm", bom.ToCsv());
        Assert.Contains(",540,", bom.ToCsv());
    }

    [Fact]
    public void Bom_WithoutCutLengths_PrintsExactlyWhatItAlwaysDid()
    {
        // The MATERIAL-column rule: a column that would be empty on every row is not
        // printed, so scenes without frame members are byte-identical to before.
        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(10, 10, 2)));
        var bom = Bom.For(scene);
        Assert.False(bom.HasCutLengths);
        Assert.DoesNotContain("CUT", bom.ToText());
        Assert.DoesNotContain("CutLengthMm", bom.ToCsv());
        Assert.DoesNotContain("stock", bom.ToText());
    }

    [Fact]
    public void CutLength_RoundTripsThroughTheDocument()
    {
        var flat = FrameProfile.Flat(20, 10);
        var frame = Weldment.Build(flat, [(new Vector3d(0, 0, 0), new Vector3d(50, 0, 0))],
            new WeldmentOptions { Material = Materials.Steel });
        var scene = new Scene();
        scene.AddTab("Model").Add(frame.ToAssembly());

        string saved = new Document(scene).Save();
        var loaded = Document.Load(saved);
        var part = loaded.Document.Scene.AllParts.Single();
        Assert.Equal(50, part.CutLength!.Value, 1e-12);
        // The fixed point: a second save is byte-identical (the field round-trips).
        Assert.Equal(saved, loaded.Document.Save());
    }

    // ---- refusals, each by name ----

    [Fact]
    public void CopedJoints_AreRefusedWithTheTracerReason()
    {
        var tube = FrameProfile.RoundTube(26.9, 2.6);
        var refusal = Assert.Throws<NotSupportedException>(() => Weldment.Build(tube,
            [(new Vector3d(0, 0, 0), new Vector3d(50, 0, 0))],
            new WeldmentOptions { JointStyle = FrameJointStyle.Cope }));
        Assert.Contains("saddle", refusal.Message);
        Assert.Contains("tracer", refusal.Message);
    }

    [Fact]
    public void ButtOntoARoundTube_IsTheCopedCase_AndSaysSo()
    {
        var tube = FrameProfile.RoundTube(26.9, 2.6);
        var refusal = Assert.Throws<NotSupportedException>(() => Weldment.Build(tube,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
        ], new WeldmentOptions { JointStyle = FrameJointStyle.Butt }));
        Assert.Contains("coped", refusal.Message);
        Assert.Contains("ROUND wall", refusal.Message);
    }

    [Fact]
    public void ThreeMembersAtOnePoint_AreRefused()
    {
        var refusal = Assert.Throws<NotSupportedException>(() => Weldment.Build(FrameProfile.Flat(20, 10),
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, -50, 0)),
        ]));
        Assert.Contains("Three or more members", refusal.Message);
    }

    [Fact]
    public void TJoint_TrimsTheAbuttingMemberToTheThroughWall()
    {
        // A mid-rail butting the side of a through post: the through member keeps its
        // full length, the rail is trimmed back by HALF the through section (20 of 40).
        // The prism-cut identity makes both volumes exact planar identities.
        var shs = FrameProfile.Shs(40, 3);
        var frame = Weldment.Build(shs,
        [
            (new Vector3d(0, 0, 0), new Vector3d(200, 0, 0)),
            (new Vector3d(100, 150, 0), new Vector3d(100, 0, 0)),
        ]);

        Assert.Equal(shs.Area * 200, BrepVolume(frame.Members[0].Shape), 1e-9 * shs.Area * 200);
        Assert.Equal(shs.Area * 130, BrepVolume(frame.Members[1].Shape), 1e-9 * shs.Area * 130);
        Assert.Equal(130, frame.Members[1].CutLength, 1e-9);
    }

    [Fact]
    public void ObliqueTJoint_SatisfiesTheCentroidRule()
    {
        // The same wall plane, met at 45 degrees: the centroid fiber crosses y = +10 at
        // axial distance (100 - 10)*sqrt(2) from the far end, and that IS the volume.
        var flat = FrameProfile.Flat(20, 10);
        var frame = Weldment.Build(flat,
        [
            (new Vector3d(0, 0, 0), new Vector3d(200, 0, 0)),
            (new Vector3d(200, 100, 0), new Vector3d(100, 0, 0)),
        ], new WeldmentOptions { Up = Vector3d.UnitZ });

        double kept = 90 * Math.Sqrt(2);
        Assert.Equal(flat.Area * kept, BrepVolume(frame.Members[1].Shape), 1e-9 * flat.Area * kept);
    }

    [Fact]
    public void TJointRefusals_NameTheirShape()
    {
        var shs = FrameProfile.Shs(40, 3);
        // Collinear landing: the members overlap along one line.
        var collinear = Assert.Throws<NotSupportedException>(() => Weldment.Build(shs,
        [
            (new Vector3d(0, 0, 0), new Vector3d(200, 0, 0)),
            (new Vector3d(300, 0, 0), new Vector3d(100, 0, 0)),
        ]));
        Assert.Contains("COLLINEAR", collinear.Message);

        // An endpoint on TWO interiors: which wall trims is ambiguous.
        var ambiguous = Assert.Throws<NotSupportedException>(() => Weldment.Build(shs,
        [
            (new Vector3d(0, 0, 0), new Vector3d(200, 0, 0)),
            (new Vector3d(100, 0, -80), new Vector3d(100, 0, 80)),
            (new Vector3d(100, 150, 0), new Vector3d(100, 0, 0)),
        ]));
        Assert.Contains("ambiguous", ambiguous.Message);

        // A T onto a ROUND wall is the coped case, refused with the tracer reason.
        var round = Assert.Throws<NotSupportedException>(() => Weldment.Build(
            FrameProfile.RoundTube(26.9, 2.6),
        [
            (new Vector3d(0, 0, 0), new Vector3d(200, 0, 0)),
            (new Vector3d(100, 150, 0), new Vector3d(100, 0, 0)),
        ]));
        Assert.Contains("ROUND", round.Message);
    }

    [Fact]
    public void ZeroAngleJoints_AreRefused()
    {
        var refusal = Assert.Throws<NotSupportedException>(() => Weldment.Build(FrameProfile.Flat(20, 10),
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
        ]));
        Assert.Contains("zero-angle", refusal.Message);
    }

    [Fact]
    public void AMemberConsumedByItsOwnEndCuts_IsRefused()
    {
        // A zigzag whose middle member's two steep miters cross inside it.
        var refusal = Assert.Throws<NotSupportedException>(() => Weldment.Path(FrameProfile.Flat(20, 10),
            [new Vector3d(0, 30, 0), new Vector3d(100, 0, 0), new Vector3d(0, 0, 0), new Vector3d(100, 30, 0)]));
        Assert.Contains("cross inside", refusal.Message);
    }

    [Fact]
    public void BezierProfiles_TrimAtAJoint_IsRefused_ButFreeEndsWork()
    {
        // A profile with a Bézier outline extrudes fine (no boolean at a free end)…
        var blob = new FrameProfile("blob", Sketch.Start(0, -5)
            .LineTo(10, -5)
            .BezierTo(new(14, -2), new(14, 2), new(10, 5))
            .LineTo(0, 5)
            .Close());
        var free = Weldment.Build(blob, [(new Vector3d(0, 0, 0), new Vector3d(50, 0, 0))]);
        Assert.Single(free.Members);

        // …but a joint would cut its extruded Bézier wall along a curve with no
        // analytic form (the marching tracer's sampled-polyline floor), so it refuses.
        var refusal = Assert.Throws<NotSupportedException>(() => Weldment.Build(blob,
        [
            (new Vector3d(0, 0, 0), new Vector3d(50, 0, 0)),
            (new Vector3d(50, 0, 0), new Vector3d(50, 50, 0)),
        ]));
        Assert.Contains("BezierCurve2d", refusal.Message);
    }

    [Fact]
    public void ProfileFactories_HaveExactAreas()
    {
        Assert.Equal(200, FrameProfile.Flat(20, 10).Area, 1e-12);
        Assert.Equal(40 * 40 - 34 * 34, FrameProfile.Shs(40, 3).Area, 1e-9);
        Assert.Equal(50 * 30 - 44 * 24, FrameProfile.Rhs(50, 30, 3).Area, 1e-9);
        Assert.Equal(304, FrameProfile.EqualAngle(40, 4).Area, 1e-12);
        // Channel: web h·t plus two flanges (w − t)·t.
        Assert.Equal(50 * 3 + 2 * (25 - 3) * 3, FrameProfile.Channel(25, 50, 3).Area, 1e-12);
        double ro = 26.9 / 2, ri = ro - 2.6;
        Assert.Equal(Math.PI * (ro * ro - ri * ri), FrameProfile.RoundTube(26.9, 2.6).Area, 1e-9);
    }
}
