using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Thread RUNOUT — the incomplete thread a die or a rolling head leaves where the thread
/// meets its shank — and the fact that made it cheap: the 45&#xB0; lead-in chamfer was
/// never a special shape, only the equal-drop member of a family of coaxial cones, and
/// <c>SurfaceIntersection</c>'s coaxial case cuts EVERY member of that family in an exact
/// conical <c>SpiralArc3d</c>. So a shallow cone stretched over two pitches is exactly as
/// B-Rep-native as a short steep one, and the runout needed a parameter rather than a
/// surface.
/// </summary>
public class ThreadRunoutTests
{
    private static readonly ThreadSpec M8 = StandardThreads.Metric(8);

    /// <summary>
    /// The chamfer tool IS the cone tool at equal drop, and it must stay so bit for bit:
    /// every committed chamfered thread rides on those coordinates.
    /// </summary>
    [Fact]
    public void TheChamferToolIsTheConeToolAtEqualDrop()
    {
        var chamfer = SolidFactory.MakeThreadEndChamferTool(4, 0.3, 6, true);
        var cone = SolidFactory.MakeThreadEndConeTool(4, 0.3, 0.3, 6, true);
        var a = BRepTessellator.Tessellate(chamfer, 32, 32);
        var b = BRepTessellator.Tessellate(cone, 32, 32);
        Assert.Equal(a.VertexCount, b.VertexCount);
        for (int i = 0; i < a.VertexCount; i++)
        {
            var pa = a.GetVertex(i).Position;
            var pb = b.GetVertex(i).Position;
            Assert.Equal(BitConverter.DoubleToInt64Bits(pa.X), BitConverter.DoubleToInt64Bits(pb.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(pa.Y), BitConverter.DoubleToInt64Bits(pb.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(pa.Z), BitConverter.DoubleToInt64Bits(pb.Z));
        }
    }

    /// <summary>A runout is Native in BOTH representations — which is the whole reason
    /// the implicit field had to learn the cone's slope at the same time.</summary>
    [Fact]
    public void ARunoutIsNativeInBothRepresentations()
    {
        var shape = Shape.ExternalThread(M8, 10, chamferLength: 0.4, runoutLength: 2.5);
        Assert.All(shape.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        Assert.All(shape.Explain(TargetRep.Implicit).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
    }

    /// <summary>
    /// The check with teeth, and the one a volume comparison cannot make: every vertex of
    /// the B-Rep tessellation reads zero against the thread's OWN implicit field. A runout
    /// modelled at the wrong slope, on the wrong end, or to the wrong diameter moves those
    /// vertices off the field while leaving the solid perfectly closed.
    ///
    /// <para><b>The control cannot be taken at the VERTICES</b>, and finding that out is
    /// worth as much as the assertion: the runout cone is a <c>RevolvedSurface</c> over a
    /// STRAIGHT generator, so its natural grid collapses v to one cell (a v-chord lies
    /// exactly on the surface) and the face contributes no interior vertex at all — every
    /// vertex it does contribute sits on its boundary spirals, which lie on the untouched
    /// thread flanks, or on the end plane, where the field is zero for either rod. So a
    /// vertex-only comparison against the UN-runout field reads 2.8e-15 and would have
    /// passed a runout that was never cut. A FACET CENTROID on that face is exactly on the
    /// cone for the same reason the face is ruled, and reads −0.19.</para>
    /// </summary>
    [Fact]
    public void TheBrepRunoutIsTheImplicitRunout()
    {
        var shape = Shape.ExternalThread(M8, 10, chamferLength: 0.4, runoutLength: 2.5);
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 96, 96);
        Assert.True(mesh.IsClosed);

        var field = shape.ToImplicit();
        double worst = 0;
        foreach (var vertex in mesh.Vertices)
            worst = Math.Max(worst, Math.Abs(field.Evaluate(vertex.Position)));
        Assert.True(worst < 1e-9, $"worst |sdf| at a B-Rep vertex is {worst:E3}");

        // The instrument, at the facet centroids: the runout's own field still reads near
        // zero (the only error is the chord sagitta of the CURVED bands, ~2e-3 at this
        // density) while the un-runout field reads the whole depth of the crest the runout
        // cut away.
        var plain = Shape.ExternalThread(M8, 10, chamferLength: 0.4).ToImplicit();
        double own = 0, against = 0;
        foreach (var face in mesh.Faces)
        {
            var centroid = Vector3d.Zero;
            int count = 0;
            foreach (var vertex in face.Vertices())
            {
                centroid += vertex.Position;
                count++;
            }
            centroid /= count;
            own = Math.Max(own, Math.Abs(field.Evaluate(centroid)));
            against = Math.Max(against, Math.Abs(plain.Evaluate(centroid)));
        }
        Assert.True(own < 0.01, $"the runout's own field reads {own:E3} at a facet centroid");
        Assert.True(against > 0.1, $"the un-runout field reads only {against:E3} — the fixture cannot see a slip");
    }

    /// <summary>
    /// The runout truncates the crests down to the PITCH diameter at the end face, which
    /// is what keeps it exact: a cone reaching the MINOR diameter is tangent to every root
    /// band along the end plane, the coincident curved-surface input the boolean refuses.
    /// Measured off the tessellation's own radii, not off the parameters that built it.
    /// </summary>
    [Fact]
    public void TheCrestIsTruncatedToThePitchDiameterAtTheEndFace()
    {
        var solid = Shape.ExternalThread(M8, 10, chamferLength: 0.4, runoutLength: 2.5).ToBrep();
        var mesh = BRepTessellator.Tessellate(solid, 128, 128);

        double atEnd = 0, atFullThread = 0;
        foreach (var vertex in mesh.Vertices)
        {
            var p = vertex.Position;
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            // Exactly the end plane: the cone rises 0.162 per unit of z, so even a 0.05
            // band would read the cone a twentieth of the way up rather than at its base.
            if (Math.Abs(p.Z) < 1e-9)
                atEnd = Math.Max(atEnd, r);
            if (p.Z is > 4 and < 6)
                atFullThread = Math.Max(atFullThread, r);
        }
        Assert.Equal(M8.PitchDiameter / 2, atEnd, 3);
        Assert.Equal(M8.MajorDiameter / 2, atFullThread, 3);
    }

    /// <summary>
    /// A longer runout removes strictly more material, and every length in between builds.
    /// The volumes are the tessellated ones at a fixed density, so what is asserted is the
    /// ORDER — a comparison of one discretization against itself, which is exact.
    /// </summary>
    [Fact]
    public void MoreRunoutRemovesMoreMaterial()
    {
        double previous = double.PositiveInfinity;
        foreach (double runout in (ReadOnlySpan<double>)[0, 1.25, 2.5, 3.75])
        {
            var solid = Shape.ExternalThread(M8, 10, chamferLength: 0.4, runoutLength: runout).ToBrep();
            solid.Validate();
            var mesh = BRepTessellator.Tessellate(solid, 96, 96);
            Assert.True(mesh.IsClosed);
            double volume = MeshMassProperties.Compute(mesh).Volume;
            Assert.True(volume < previous, $"runout {runout} measured {volume:F5} against {previous:F5}");
            previous = volume;
        }
    }

    /// <summary>An un-runout thread takes the path it always did, bit for bit.</summary>
    [Fact]
    public void AThreadWithNoRunoutIsUnchanged()
    {
        var withParameter = Shape.ExternalThread(M8, 10, chamferLength: 0.4, runoutLength: 0);
        var without = Shape.ExternalThread(M8, 10, chamferLength: 0.4);
        var a = BRepTessellator.Tessellate(withParameter.ToBrep(), 64, 64);
        var b = BRepTessellator.Tessellate(without.ToBrep(), 64, 64);
        Assert.Equal(a.VertexCount, b.VertexCount);
        for (int i = 0; i < a.VertexCount; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.GetVertex(i).Position.Z),
                BitConverter.DoubleToInt64Bits(b.GetVertex(i).Position.Z));
        }

        // The FIELD too — the slope branch is an exact-1 test precisely so that every
        // thread field already in the repository is untouched.
        var fieldA = withParameter.ToImplicit();
        var fieldB = without.ToImplicit();
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                -5 + 10.0 * (i % 13) / 12, -5 + 10.0 * (i % 7) / 6, 10.0 * (i % 17) / 16);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(fieldA.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(fieldB.Evaluate(p)));
        }
    }

    [Fact]
    public void ARunoutLongerThanItsThreadIsRefusedByName()
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => Shape.ExternalThread(M8, 5, runoutLength: 5));
        Assert.Contains("shorter than the thread", thrown.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.ExternalThread(M8, 10, runoutLength: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SolidFactory.MakeThreadEndConeTool(4, 4, 1, 0, true));
    }
}

/// <summary>
/// Cosmetic-thread annotation: a part that carries modelled threads says which threads
/// they are. The interesting decision is that the spec comes from the graph and the
/// ANCHOR from the geometry, matched on the two numbers a thread IS — its major diameter
/// and its pitch — rather than by pairing the n-th node with the n-th group of faces.
/// </summary>
public class ThreadAnnotationTests
{
    [Fact]
    public void AStudLabelsItselfOnItsOwnCrest()
    {
        var part = new Part("stud", Shape.ExternalThread(8, 20, chamferLength: 0.4));
        var sites = ThreadAnnotations.Sites(part);
        var site = Assert.Single(sites);
        Assert.Equal("M8×1.25", site.Callout);
        Assert.True(site.External);
        // ON the crest, at mid-length: the leader lands on material, not in the air.
        Assert.Equal(4.0, Math.Sqrt(site.Anchor.X * site.Anchor.X + site.Anchor.Y * site.Anchor.Y), 6);
        Assert.Equal(10.0, site.Anchor.Z, 6);
        Assert.Equal(1, ThreadAnnotations.AutoAttach(part));
        Assert.Single(part.Annotations);
    }

    /// <summary>A threaded hole's callout carries its DEPTH, as a drawing's does.</summary>
    [Fact]
    public void ThreadedHolesAreLabelledWithTheirDepth()
    {
        var part = new Part("plate",
            Shape.Box(40, 30, 12).ThreadedHole(StandardThreads.Metric(6), [(0, 0), (12, 0)], 8));
        var sites = ThreadAnnotations.Sites(part);
        Assert.Equal(2, sites.Count);
        Assert.All(sites, s =>
        {
            Assert.Equal("M6×1 ↧8", s.Callout);
            Assert.False(s.External);
        });
        // One per hole, each on its own axis.
        Assert.Equal(2, sites.Select(s => Math.Round(s.Axis.Origin.X, 6)).Distinct().Count());
    }

    /// <summary>
    /// The matching rule, measured: a part carrying an M10 stud AND M6 tapped holes must
    /// put each callout on its own thread. Index pairing would be free to swap them, and
    /// the failure — a correct-looking M6 label on an M10 thread — is exactly the silent
    /// misresolve a naming scheme must not make.
    /// </summary>
    [Fact]
    public void TwoDifferentThreadsOnOnePartAreLabelledByGeometry()
    {
        var body = Shape.Box(40, 30, 12)
            .ThreadedHole(StandardThreads.Metric(6), [(-12, 0)], 8)
            .Union(Shape.ExternalThread(10, 15, chamferLength: 0.5).Translate((12, 0, 12)));
        var sites = ThreadAnnotations.Sites(new Part("boss", body));

        var external = Assert.Single(sites, s => s.External);
        Assert.Equal("M10×1.5", external.Callout);
        Assert.Equal(5.0, Math.Sqrt(
            (external.Anchor.X - 12) * (external.Anchor.X - 12) + external.Anchor.Y * external.Anchor.Y), 6);

        var tapped = Assert.Single(sites, s => !s.External);
        Assert.Equal("M6×1 ↧8", tapped.Callout);
        Assert.True(tapped.Anchor.X < 0, $"the tapped callout landed at {tapped.Anchor}");
    }

    [Fact]
    public void APartWithNoModelledThreadCarriesNoSites()
    {
        Assert.Empty(ThreadAnnotations.Sites(new Part("plate", Shape.Box(10, 10, 10))));
        Assert.Empty(ThreadAnnotations.Sites(new Part("mesh", MeshPrimitives.Box(1, 1, 1))));
    }
}
