using EngrCAD.Implicit;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Part.RefineMesh"/> — the deliberate re-mesh entry point: finer only, and
/// only for geometry that HAS a finer tessellation.
/// </summary>
public class PartRefineMeshTests
{
    private static MeshQuality Adaptive(double deviation) => new()
    {
        Tessellation = new TessellationQuality { MaxChordDeviation = deviation, MinSegments = 8 },
    };

    [Fact]
    public void RefiningProducesAFinerMeshAndReportsIt()
    {
        var part = new Part("rim", Shape.Cylinder(radius: 100, height: 10));
        part.GetMesh(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12 });
        int coarse = part.GetMesh().FaceCount;
        Assert.Equal(16, part.MeshSegmentsPerCircle);

        Assert.True(part.RefineMesh(Adaptive(0.02)));

        Assert.True(part.MeshSegmentsPerCircle > 16);
        Assert.True(part.GetMesh().FaceCount > coarse,
            $"a refinement must add facets: {coarse} -> {part.GetMesh().FaceCount}");
    }

    [Fact]
    public void ARequestToCoarsenIsDeclinedAndTheMeshIsUntouched()
    {
        var part = new Part("rim", Shape.Cylinder(radius: 100, height: 10));
        part.GetMesh(new MeshQuality { SegmentsPerCircle = 96, CurveSamples = 72 });
        var before = part.GetMesh();

        // A chord deviation so loose the criterion would ask for the floor.
        Assert.False(part.RefineMesh(Adaptive(50)));

        Assert.Same(before, part.GetMesh());   // the ratchet: not merely equal, the SAME mesh
        Assert.Equal(96, part.MeshSegmentsPerCircle);
    }

    [Fact]
    public void RefiningToTheSameCountIsDeclined()
    {
        var part = new Part("rim", Shape.Cylinder(radius: 100, height: 10));
        var quality = Adaptive(0.02);
        part.GetMesh(quality);
        var before = part.GetMesh();

        Assert.False(part.RefineMesh(quality));
        Assert.Same(before, part.GetMesh());
    }

    [Fact]
    public void RepeatedRefinementIsMonotoneAndSettles()
    {
        var part = new Part("rim", Shape.Cylinder(radius: 100, height: 10));
        part.GetMesh(new MeshQuality { SegmentsPerCircle = 12, CurveSamples = 8 });

        int last = part.MeshSegmentsPerCircle;
        foreach (double deviation in new[] { 0.5, 0.1, 0.02, 0.004 })
        {
            part.RefineMesh(Adaptive(deviation));
            Assert.True(part.MeshSegmentsPerCircle >= last);
            last = part.MeshSegmentsPerCircle;
        }
        // Walking back out changes nothing at all.
        foreach (double deviation in new[] { 0.02, 0.1, 0.5, 5.0 })
            Assert.False(part.RefineMesh(Adaptive(deviation)));
        Assert.Equal(last, part.MeshSegmentsPerCircle);
    }

    [Fact]
    public void TheFeatureEdgeOverlayIsRebuiltAtTheRefinedQuality()
    {
        // One criterion drives fill AND overlay: a refinement that left the edges alone
        // would detach the smooth exact rim from the finer faceted fill.
        var part = new Part("rim", Shape.Cylinder(radius: 100, height: 10));
        var coarse = Adaptive(2.0);
        part.GetMesh(coarse);
        int before = part.GetFeatureEdges(coarse).Count;

        Assert.True(part.RefineMesh(Adaptive(0.01)));
        Assert.True(part.GetFeatureEdges(coarse).Count > before,
            "the overlay must follow the mesh, not the quality a later caller happens to pass");
    }

    [Fact]
    public void APartWithNoMeshYetRefinesNothing()
    {
        // Producing the FIRST mesh is the display loader's job at the session quality.
        var part = new Part("rim", Shape.Cylinder(radius: 100, height: 10));
        Assert.False(part.HasMesh);
        Assert.False(part.RefineMesh(Adaptive(0.01)));
        Assert.False(part.HasMesh);
    }

    [Fact]
    public void AMeshPartHasNoFinerFormAndIsDeclined()
    {
        var part = new Part("blob", MeshPrimitives.UvSphere(10, 12, 8));
        var before = part.GetMesh();
        Assert.False(part.RefineMesh(Adaptive(0.001)));
        Assert.Same(before, part.GetMesh());
    }

    [Fact]
    public void AnSdfPartIsDeclined()
    {
        // SdfResolution is a grid resolution, not a per-radius quantity — the same reason
        // TessellationQuality leaves it alone.
        var part = new Part("field", Sdf.Sphere(10));
        var before = part.GetMesh(new MeshQuality { SdfResolution = 24 });
        Assert.False(part.RefineMesh(new MeshQuality { SdfResolution = 96 }));
        Assert.Same(before, part.GetMesh());
    }

    [Fact]
    public void RegenerationRestartsTheRatchet()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Circle(100)) { Height = 10 });
        var part = new Part("rim", history);

        part.GetMesh(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12 });
        Assert.True(part.RefineMesh(Adaptive(0.02)));
        Assert.True(part.MeshSegmentsPerCircle > 16);

        part.Regenerate();
        Assert.Equal(0, part.MeshSegmentsPerCircle);
        Assert.False(part.HasMesh);
    }

    [Fact]
    public void RefinementIsGeometricallyFaithful()
    {
        // The refined mesh is a better cylinder, not a different solid: an inscribed
        // n-gon prism's volume rises toward pi*r^2*h and never past it.
        const double radius = 50, height = 20;
        double exact = Math.PI * radius * radius * height;
        var part = new Part("rim", Shape.Cylinder(radius, height));
        part.GetMesh(new MeshQuality { SegmentsPerCircle = 12, CurveSamples = 8 });

        double coarse = MeshMassProperties.Compute(part.GetMesh()).Volume;
        Assert.True(part.RefineMesh(Adaptive(0.01)));
        double fine = MeshMassProperties.Compute(part.GetMesh()).Volume;

        Assert.True(coarse < fine && fine < exact,
            $"inscribed and improving: {coarse} < {fine} < {exact}");

        // And it is exactly the prism the refined count asks for, not merely closer: the
        // discrete truth (n/2)r^2 sin(2pi/n) h is available in closed form precisely
        // because nothing about the tessellation is fitted.
        int n = part.MeshSegmentsPerCircle;
        double discrete = 0.5 * n * radius * radius * Math.Sin(2 * Math.PI / n) * height;
        Assert.Equal(discrete, fine, 6);
    }

    [Fact]
    public void ANullQualityIsRefusedByName()
    {
        var part = new Part("rim", Shape.Cylinder(10, 10));
        Assert.Throws<ArgumentNullException>(() => part.RefineMesh(null!));
    }
}
