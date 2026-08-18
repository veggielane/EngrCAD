using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Selector-backed dimensions named through the typed <c>FaceRef</c>/<c>EdgeSetRef</c>
/// vocabulary instead of a lambda — and therefore PERSISTENT, which is the whole point:
/// a reference's <c>Descriptor</c> is already its cache key, so it is also its serialized
/// form, and a dimension becomes as durable as a feature already is.
///
/// <para>The strong assertions are the two a resemblance test cannot make: the file is a
/// <b>byte fixed point</b> under save → load → save, and the reference spelling measures
/// exactly what its lambda twin measures (the reduction claim — otherwise the two ways of
/// naming the same faces would be two dimensions).</para>
/// </summary>
public class AnnotationReferenceTests
{
    private static Part Plate()
    {
        // A drilled plate: two parallel planar faces to dimension between, two
        // non-parallel ones for an angle, and exactly one bore rim of a stated radius.
        var shape = Shape.Box(new Aabb((-20, -15, 0), (20, 15, 6)))
            - Shape.Cylinder(radius: 4, height: 20).Translate(0, 0, 3);
        return new Part("plate", shape, Palette.Steel);
    }

    /// <summary>The reference-typed constructors are private (they would collide with the
    /// point-form ones under a target-typed <c>new</c>), so a factory's result carries its
    /// placement by assignment — which is what `Label`/`Tolerance` being settable buys.</summary>
    private static T Placed<T>(
        T annotation, Vector3d offset = default, string? label = null,
        ToleranceSpec? tolerance = null) where T : Annotation
    {
        annotation.Offset = offset;
        annotation.Label = label;
        annotation.Tolerance = tolerance;
        return annotation;
    }

    private static Document DocumentWith(params Annotation[] annotations)
    {
        var scene = new Scene();
        var plate = Plate();
        foreach (var annotation in annotations)
            plate.Annotate(annotation);
        scene.Add(plate);
        return new Document(scene);
    }

    // ---- the fixed point ----

    [Fact]
    public void ReferenceBackedDimensions_AreAByteFixedPointAndWarnAboutNothing()
    {
        var document = DocumentWith(
            Placed(LinearDimension.BetweenFaces(FaceRef.Top, FaceRef.Bottom),
                offset: (0, 0, 12), tolerance: ToleranceSpec.Symmetric(0.1)),
            Placed(AngularDimension.BetweenFaces(FaceRef.Top,
                FaceRef.Extreme(FaceSetRef.PlanarWithNormal(Vector3d.UnitX), Vector3d.UnitX)),
                label: "90 NOM"),
            Placed(RadialDimension.OnEdge(EdgeSetRef.Circular(4), diameter: true),
                offset: (0, -10, 0)));

        string first = document.Save();
        var loaded = Document.Load(first);
        Assert.Empty(loaded.Warnings);   // nothing dropped: these are DATA now
        Assert.Equal(3, loaded.Document.Scene.AllParts.Single().Annotations.Count);
        Assert.Equal(first, loaded.Document.Save());
    }

    [Fact]
    public void ALambdaBackedDimension_StillSavesAsOpaqueAndWarns()
    {
        // The honesty is unchanged — what changed is that an ordinary semantic selection
        // no longer HAS to be opaque. Both halves are asserted, or "it round-trips" would
        // be a claim about a file that quietly dropped the hard case.
        var document = DocumentWith(LinearDimension.BetweenFaces(
            s => s.Faces.First(), s => s.Faces.Skip(1).First()));
        var loaded = Document.Load(document.Save());
        Assert.Empty(loaded.Document.Scene.AllParts.Single().Annotations);
        Assert.Contains(loaded.Warnings, w => w.Contains("selector"));
    }

    [Fact]
    public void ALambdaBackedREFERENCE_IsStillOpaque()
    {
        // A FaceRef built from a lambda prints opaque(label) and is IsSerializable=false,
        // so the reference-typed overload must fall back to the marker rather than
        // writing a descriptor that parses back to nothing.
        var opaque = FaceRef.One(FaceSetRef.From("firstTwo", s => s.Faces.Take(1)));
        Assert.False(opaque.IsSerializable);
        var document = DocumentWith(LinearDimension.BetweenFaces(opaque, FaceRef.Bottom));
        var loaded = Document.Load(document.Save());
        Assert.Empty(loaded.Document.Scene.AllParts.Single().Annotations);
        Assert.Contains(loaded.Warnings, w => w.Contains("selector"));
    }

    // ---- the reduction: two spellings, one measurement ----

    [Fact]
    public void AReferenceBackedDimension_MeasuresExactlyWhatItsLambdaTwinMeasures()
    {
        var plate = Plate();
        var byRef = LinearDimension.BetweenFaces(FaceRef.Top, FaceRef.Bottom);
        var byLambda = LinearDimension.BetweenFaces(
            s => FaceRef.Top.Resolve(s, "a"), s => FaceRef.Bottom.Resolve(s, "b"));
        plate.Annotate(byRef);
        plate.Annotate(byLambda);

        var resolved = plate.ResolveAnnotations();
        Assert.Equal(2, resolved.Count);
        Assert.Equal(resolved[1].Value, resolved[0].Value, 12);
        Assert.Equal(6.0, resolved[0].Value, 9);   // the plate's own thickness
    }

    [Fact]
    public void ARadialDimension_ReadsTheActualEdgeThroughItsReference()
    {
        // A cone with an apex has exactly ONE circular edge — its base rim — which is
        // what makes this a single-edge selection rather than an arbitrary pick among
        // two coaxial rims (the plate's bore has one at each face).
        var cone = new Part("cone", Shape.Cone(bottomRadius: 4, topRadius: 0, height: 10));
        cone.Annotate(RadialDimension.OnEdge(EdgeSetRef.Circular(4), diameter: true));
        var resolved = cone.ResolveAnnotations().Single();
        Assert.Equal(8.0, resolved.Value, 9);
        Assert.Equal("⌀" + "8", resolved.Text);   // diameter sign; source stays ASCII
    }

    // ---- the cardinality contract ----

    [Fact]
    public void ARadialDimensionOnAMultiEdgeSelection_RefusesByNameWithTheCount()
    {
        // An EdgeSetRef is set-valued and a radial dimension needs ONE edge, so the
        // cardinality is a claim checked where the reference resolves — the contract
        // FaceRef.One already states for faces.
        var plate = Plate();
        plate.Annotate(RadialDimension.OnEdge(EdgeSetRef.Circular()));
        var error = Assert.Throws<InvalidOperationException>(() => plate.ResolveAnnotations());
        Assert.Contains("expected exactly one edge", error.Message);
        Assert.Contains("found 2", error.Message);   // the bore's two rims
    }

    [Fact]
    public void ATolerancedReferenceDimension_SurvivesAsDataButNeedsAPartWithARecipe()
    {
        // The BOUNDARY, pinned rather than papered over. The reference now round-trips —
        // the tolerance and the descriptor are both data — but RESOLVING it after a load
        // needs the part to still be B-Rep-representable, and a code-built `Shape` part
        // has no recipe, so it reloads as a mesh SNAPSHOT and every selector-based
        // annotation refuses by name. That is the Shape-graph-serialization gap showing
        // through, not an annotation one: it is exactly what `DocumentLoadResult.Snapshots`
        // exists to report, and a history-backed part regenerates and resolves normally.
        var document = DocumentWith(Placed(
            LinearDimension.BetweenFaces(FaceRef.Top, FaceRef.Bottom),
            tolerance: ToleranceSpec.Limits(0.2, 0.1)));   // limits are MAGNITUDES

        // it measures before the round trip...
        var before = document.Scene.AllParts.Single().ResolveAnnotations().Single();
        Assert.Equal(6.0, before.Value, 9);
        Assert.Contains("+0.2", before.Text);

        // ...and the annotation itself survives, which is what this change bought
        string first = document.Save();
        var loaded = Document.Load(first);
        Assert.Single(loaded.Document.Scene.AllParts.Single().Annotations);
        Assert.Equal(first, loaded.Document.Save());
        // reported as a snapshot by its qualified "tab/part" name, not silently flattened
        Assert.Contains(loaded.Snapshots, s => s.EndsWith("plate", StringComparison.Ordinal));

        var error = Assert.Throws<InvalidOperationException>(
            () => loaded.Document.Scene.AllParts.Single().ResolveAnnotations());
        Assert.Contains("B-Rep-representable", error.Message);
    }
}
