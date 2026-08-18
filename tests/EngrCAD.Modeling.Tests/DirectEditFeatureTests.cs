using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Direct editing as parametric FEATURES — the half a `Shape` graph node could not give.
/// A graph composes and `Explain` reports it, but nothing could DRIVE the distance: a design
/// study, a configuration, the properties panel and `set_param` all write through the one
/// `SaveParameters` JSON seam, and reaching that seam is what a `Feature` wrapper buys.
/// </summary>
public class DirectEditFeatureTests
{
    private static readonly FaceSetRef Top = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    private static Aabb Bounds(Part part)
    {
        var bounds = Aabb.Empty;
        foreach (var vertex in part.TryGetSolid()!.Vertices)
            bounds = bounds.Union(vertex.Position);
        return bounds;
    }

    private static (Part Part, OffsetFacesFeature Offset) Plate(double distance)
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 10 });
        var offset = new OffsetFacesFeature { Distance = distance, Faces = Top };
        history.Add(offset);
        return (history.ToPart("plate").Of(Materials.Steel), offset);
    }

    // ---- the parameter really drives the geometry ----

    [Fact]
    public void TheOffsetDistanceIsAParameter_AndRegenerationFollowsIt()
    {
        var (part, offset) = Plate(4);
        Assert.Equal(14, Bounds(part).Size.Z, 9);

        // Through the SAME JSON seam a study, a configuration and set_param write through —
        // not a second way to apply a value.
        part.History!.LoadParameters(
            "{\"" + offset.Name + "\":{\"Distance\":9}}");
        var result = part.Regenerate();
        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(19, Bounds(part).Size.Z, 9);
    }

    [Fact]
    public void ADesignStudyDrivesTheOffsetDistance_AndLandsOnItsOwnClosedForm()
    {
        // The plate is 60 x 40 and the offset thickens it, so the mass is linear in the
        // distance and the answer is whatever mass target the constraint states — a closed
        // form the study cannot reach by luck.
        var (part, offset) = Plate(1);
        var distance = DesignVariable.On(offset, nameof(OffsetFacesFeature.Distance), min: 0.5, max: 20);

        const double targetGrams = 300;
        var result = DesignStudy.Minimize(
            part, [distance],
            p => -p.MassGrams()!.Value,   // grow it as far as the mass limit allows
            [StudyConstraint.AtMost("mass", p => p.MassGrams()!.Value, targetGrams)]);

        Assert.True(result.Succeeded && result.Feasible);
        // mass = 60*40*(10 + d) * density; solve for the stated limit.
        double density = Materials.Steel.Density;                       // tonne/mm^3
        double exact = targetGrams / (ModelUnits.MassToGrams(1) * density * 60 * 40) - 10;
        Assert.InRange(exact - result.ValueOf(distance), 0, result.OptimumTolerance[0]);

        // A study is an ANALYSIS: the part comes back at the value it started from.
        Assert.Equal(1, offset.Distance, 12);
    }

    [Fact]
    public void AConfigurationSwitchesTheOffsetDistance_AndBack()
    {
        var (part, offset) = Plate(4);
        part.Configurations!.Add("thick", (offset, nameof(OffsetFacesFeature.Distance), 9.0));
        part.Configurations.Add("thin", (offset, nameof(OffsetFacesFeature.Distance), 1.0));

        part.Configurations.Activate("thick");
        Assert.Equal(19, Bounds(part).Size.Z, 9);
        part.Configurations.Activate("thin");
        Assert.Equal(11, Bounds(part).Size.Z, 9);
        part.Configurations.Activate("thick");
        Assert.Equal(19, Bounds(part).Size.Z, 9);
    }

    // ---- persistence ----

    [Fact]
    public void EveryDirectEditFeature_RoundTripsAsAByteFixedPoint()
    {
        // All four in one history, so a selector, a vector, an axis and a bare face set are
        // each exercised through the SerializeValue/ApplyParameters seam.
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 10 });
        history.Add(new OffsetFacesFeature { Distance = 3, Faces = Top });
        history.Add(new MoveFacesFeature { Translation = new Vector3d(2, 0, 5), Faces = Top });
        history.Add(new RotateFacesFeature
        {
            AngleDegrees = 4,
            Axis = AxisRef.Of(new Vector3d(30, 0, -5), Vector3d.UnitY),
            Faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitX),
        });
        history.Add(new DeleteFacesFeature { Faces = FaceSetRef.Cylindrical() });

        string json = history.SaveHistory();
        var loaded = FeatureHistory.LoadHistory(json);
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));
        Assert.Equal(json, loaded.History.SaveHistory());

        // The values came back, not merely the records.
        var offset = Assert.IsType<OffsetFacesFeature>(loaded.History.Features[1]);
        Assert.Equal(3, offset.Distance, 12);
        Assert.Equal(Top.Descriptor, offset.Faces.Descriptor);
        var move = Assert.IsType<MoveFacesFeature>(loaded.History.Features[2]);
        Assert.True(move.Translation.AreEqual((2, 0, 5), new Tolerance(1e-12, 1e-12)));
        var rotate = Assert.IsType<RotateFacesFeature>(loaded.History.Features[3]);
        Assert.Equal(4, rotate.AngleDegrees, 12);
        Assert.Equal("axis([30,0,-5],[0,1,0])", rotate.Axis.Descriptor);
    }

    [Fact]
    public void EveryDirectEditFeature_IsInTheInsertionCatalogue()
    {
        // A parameterless-constructible public Feature is auto-registered, so this asserts
        // the property a UI depends on rather than a list someone maintains.
        foreach (string name in (string[])
                 ["OffsetFacesFeature", "MoveFacesFeature", "RotateFacesFeature", "DeleteFacesFeature"])
        {
            var info = FeatureRegistry.Default.Find(name);
            Assert.NotNull(info);
            Assert.True(info!.CanCreate, info.Reason);
            Assert.Contains(info.Parameters, p => p.Name == "Faces");
        }
    }

    // ---- a delete feature really heals ----

    [Fact]
    public void ADeleteFacesFeature_TakesAFilletBackOff()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 10 });
        history.Add(new FilletRimFeature { Radius = 3, Faces = Top });
        var part = history.ToPart("plate");
        double filleted = part.MassProperties().Volume;

        history.Add(new DeleteFacesFeature
        {
            Faces = FaceSetRef.Where("bands", f => !f.IsPlanar(out _, out _)),
        });
        var result = part.Regenerate();
        Assert.True(result.Succeeded, result.ToString());

        // The plate comes back — and the fillet had genuinely removed material, which is what
        // makes the recovery a statement rather than a coincidence.
        Assert.True(filleted < 60 * 40 * 10 - 100);
        Assert.Equal(60 * 40 * 10, part.MassProperties().Volume, 6);
    }
}
