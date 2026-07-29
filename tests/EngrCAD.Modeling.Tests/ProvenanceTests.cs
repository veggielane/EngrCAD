using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Face provenance — the persistent half of topological naming, beside the semantic
/// selectors.
///
/// <para>These tests are as much a MEASUREMENT as a check: the value of a naming scheme is
/// exactly the set of operations it survives, so each one names an operation and asserts
/// what comes out the other side — including the operations where the tag is DROPPED, which
/// are pinned deliberately so the documented boundary cannot rot. The one property that
/// must never break is that the failure is one-sided: fewer faces, never a face from
/// somewhere else.</para>
/// </summary>
public class ProvenanceTests
{
    private static Shape Boss(string tag) =>
        Shape.Cylinder(8, 24).Translate(0, 0, 6).Tag(tag);

    private static Shape Plate() => Shape.Box(80, 60, 12);

    private static int TaggedFaceCount(BrepSolid solid, string tag) =>
        solid.Faces.Count(f => f.Provenance.Contains(tag));

    // ---- the tag is transparent -----------------------------------------

    [Fact]
    public void TaggingChangesNoGeometryInAnyRepresentation()
    {
        var plain = Plate() | Shape.Cylinder(8, 24).Translate(0, 0, 6);
        var tagged = Plate() | Boss("boss");

        Assert.Equal(plain.ToBrep().Faces.Count(), tagged.ToBrep().Faces.Count());
        Assert.Equal(
            EngrCAD.Mesh.MeshMassProperties.Compute(plain.ToMesh()).Volume,
            EngrCAD.Mesh.MeshMassProperties.Compute(tagged.ToMesh()).Volume,
            9);
        Assert.Equal(plain.ToImplicit().Evaluate((10, 10, 4)), tagged.ToImplicit().Evaluate((10, 10, 4)), 12);
    }

    [Fact]
    public void ExplainSaysNothingAboutTags()
    {
        // A tag is not an operation, so the conversion plan a user reads must not grow a
        // row for it.
        var plain = (Plate() | Shape.Cylinder(8, 24).Translate(0, 0, 6)).Explain(TargetRep.Brep);
        var tagged = (Plate() | Boss("boss")).Explain(TargetRep.Brep);
        Assert.Equal(plain.ToString(), tagged.ToString());
    }

    // ---- where it holds --------------------------------------------------

    [Fact]
    public void APrimitiveCarriesItsTagOnEveryFace()
    {
        var solid = Boss("boss").ToBrep();
        Assert.Equal(solid.Faces.Count(), TaggedFaceCount(solid, "boss"));
    }

    [Fact]
    public void AUnionKeepsTheTagOnTheSurvivingFacesOfTheTaggedOperand()
    {
        var solid = (Plate() | Boss("boss")).ToBrep();
        var tagged = solid.Faces.Where(f => f.Provenance.Contains("boss")).ToList();

        // The boss contributes its wall and its top; its bottom cap is swallowed. That is
        // the whole point of tagging a SET: the count is a property of the boolean, not of
        // the primitive.
        Assert.Equal(2, tagged.Count);
        Assert.Contains(tagged, f => f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 8) < 1e-9);
        Assert.Contains(tagged, f => f.IsPlanar(out var o, out var n)
            && Math.Abs(n.Z - 1) < 1e-9 && Math.Abs(o.Z - 18) < 1e-6);
        // And nothing of the plate leaked in.
        Assert.DoesNotContain(tagged, f => f.IsPlanar(out var o, out _) && Math.Abs(o.Z + 6) < 1e-9);
    }

    [Fact]
    public void TwoIdenticalBossesStayDistinguishable()
    {
        // The case a semantic query CANNOT answer: same shape, same height, same normals.
        var body = Plate()
            | Shape.Cylinder(6, 20).Translate(-24, 0, 6).Tag("left")
            | Shape.Cylinder(6, 20).Translate(24, 0, 6).Tag("right");
        var solid = body.ToBrep();

        Assert.Equal(2, TaggedFaceCount(solid, "left"));
        Assert.Equal(2, TaggedFaceCount(solid, "right"));
        Assert.DoesNotContain(solid.Faces, f =>
            f.Provenance.Contains("left") && f.Provenance.Contains("right"));

        // Each one's top face is reachable by name — impossible with PlanarWithNormal(Z),
        // which sees two identical candidates.
        var top = FaceRef.Extreme(
            FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Within(FaceSetRef.Tagged("left")),
            Vector3d.UnitZ).Resolve(solid, "top");
        // Locate it by BOUNDS, not by IsPlanar's origin and not by the face frame: a
        // plane stores an arbitrary in-plane point, and a circular loop's frame origin is
        // its single seam VERTEX — both read x = -18 here (the rim), not the centre. This
        // is the same trap BrepSelection documents for ranking, and it bites an assertion
        // exactly as hard.
        Assert.Equal(-24, top.Bounds().Center.X, 6);
    }

    [Fact]
    public void ADifferenceKeepsTheTagOnTheToolsWall()
    {
        // The subtracted tool's faces are re-wound copies; provenance rides through
        // BrepBoolean.ReverseFace.
        var solid = (Plate() - Shape.Cylinder(5, 40).Tag("bore")).ToBrep();
        var tagged = solid.Faces.Where(f => f.Provenance.Contains("bore")).ToList();
        Assert.Single(tagged);
        Assert.True(tagged[0].IsCylindrical(out _, out _, out double radius));
        Assert.Equal(5, radius, 9);
    }

    [Fact]
    public void TheHostKeepsItsTagThroughABoreThatSplitsAFace()
    {
        // The plate's caps are SPLIT by the bore (a face grows a hole loop), which is the
        // FaceSplitter path — the fragments must inherit.
        var solid = (Plate().Tag("plate") - Shape.Cylinder(5, 40)).ToBrep();
        Assert.Equal(solid.Faces.Count() - 1, TaggedFaceCount(solid, "plate"));   // all but the bore wall
        Assert.DoesNotContain(solid.Faces.Where(f => f.IsCylindrical(out _, out _, out _)),
            f => f.Provenance.Contains("plate"));
    }

    [Fact]
    public void DrillKeepsTheBodysTag()
    {
        var solid = Plate().Tag("plate")
            .Drill(HoleSpec.Simple(6), [new(-20, 0), new(20, 0)], 20,
                SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY))
            .ToBrep();
        int tagged = TaggedFaceCount(solid, "plate");
        Assert.True(tagged >= 6, $"the six plate walls must survive; found {tagged}");
        // The two bore walls came from the tools, not the plate.
        Assert.Equal(solid.Faces.Count() - 2, tagged);
    }

    [Fact]
    public void TagsNestOutermostLast()
    {
        var solid = (Plate() | Boss("boss")).Tag("assembly").ToBrep();
        var top = solid.Faces.Single(f => f.Provenance.Contains("boss")
            && f.IsPlanar(out _, out var n) && Math.Abs(n.Z - 1) < 1e-9);
        Assert.Equal(["boss", "assembly"], top.Provenance);
        // The outer tag reaches the plate's faces too — it named the whole step.
        Assert.Equal(solid.Faces.Count(), TaggedFaceCount(solid, "assembly"));
    }

    [Fact]
    public void ATransformAboveTheTagIsCarriedThrough()
    {
        var solid = (Plate() | Boss("boss")).Translate(100, 0, 0).Rotate(Vector3d.UnitZ, 0.7).ToBrep();
        Assert.Equal(2, TaggedFaceCount(solid, "boss"));
    }

    [Fact]
    public void PatternsCarryTheTagToEveryCopy()
    {
        var solid = (Plate() | Boss("boss")).PatternLinear(3, (0, 80, 0)).ToBrep();
        Assert.Equal(6, TaggedFaceCount(solid, "boss"));   // two faces per copy
    }

    [Fact]
    public void CloningASourceSolidKeepsProvenance()
    {
        // Shape.From(solid) clones at the source boundary; the clone must carry the tags
        // or a design that drops down to the B-Rep API and back loses its names.
        var tagged = Boss("boss").ToBrep();
        var round = Shape.From(tagged).ToBrep();
        Assert.Equal(tagged.Faces.Count(), TaggedFaceCount(round, "boss"));
    }

    // ---- the selector ----------------------------------------------------

    [Fact]
    public void TaggedResolvesAsAFaceSet_AndRefusesAnEmptyMatchByName()
    {
        var solid = (Plate() | Boss("boss")).ToBrep();
        Assert.Equal(2, FaceSetRef.Tagged("boss").Resolve(solid, "faces").Count);

        var exception = Assert.Throws<GeometryInputException>(() =>
            FaceSetRef.Tagged("flange").Resolve(solid, "faces"));
        Assert.Contains("flange", exception.Message);
    }

    [Fact]
    public void ATaggedSelectorRoundTripsThroughItsDescriptor()
    {
        var reference = FaceSetRef.Tagged("boss");
        Assert.Equal("tagged(boss)", reference.Descriptor);
        Assert.True(reference.IsSerializable);
        Assert.Equal("tagged(boss)", FaceSetRef.Parse(reference.Descriptor).Descriptor);

        // And nested inside another term, which is how a feature actually spells it.
        var one = FaceRef.Extreme(FaceSetRef.Tagged("boss"), Vector3d.UnitZ);
        Assert.Equal(one.Descriptor, FaceRef.Parse(one.Descriptor).Descriptor);
    }

    /// <summary>
    /// A tag lives in the descriptor grammar, which is parsed back by an identifier reader
    /// — so a tag it could not spell is REFUSED rather than sanitized. Sanitizing would
    /// turn "boss top" into a descriptor that silently resolves to nothing, which is the
    /// exact failure mode a naming scheme must not have.
    /// </summary>
    [Fact]
    public void ATagTheDescriptorCannotSpellIsRefusedWithASuggestion()
    {
        var exception = Assert.Throws<ArgumentException>(() => Shape.Box(1, 1, 1).Tag("boss top"));
        Assert.Contains("boss_top", exception.Message);
        Assert.Throws<ArgumentException>(() => FaceSetRef.Tagged("a,b"));
        Assert.Throws<ArgumentException>(() => Shape.Box(1, 1, 1).Tag("  "));
    }

    [Fact]
    public void WithinNarrowsATagToOneKindOfFace()
    {
        var solid = (Plate() | Boss("boss")).ToBrep();
        var top = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Within(FaceSetRef.Tagged("boss"));

        var faces = top.Resolve(solid, "top");
        Assert.Single(faces);
        Assert.True(faces[0].IsPlanar(out _, out var normal));
        Assert.Equal(1, normal.Z, 9);

        // Serializable and a fixed point, like every other term.
        Assert.Equal("within(planar([0,0,1]),tagged(boss))", top.Descriptor);
        Assert.Equal(top.Descriptor, FaceSetRef.Parse(top.Descriptor).Descriptor);

        // The plate's own top is NOT in the boss, so the scope really restricts.
        Assert.Equal(2, FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Resolve(solid, "all").Count);
    }

    [Fact]
    public void ATaggedRimFeedsARimFeature()
    {
        // End to end through the feature layer: fillet the rim of the face the DESIGN
        // named, not the face that happens to look right.
        var body = Plate() | Shape.Cylinder(10, 20).Translate(0, 0, 6).Tag("boss");
        // Composition matters: the tag names the boss's wall AND its top, and rim surgery
        // wants a planar face — Within is what narrows one to the other.
        var rim = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Within(FaceSetRef.Tagged("boss"));
        var filleted = body.Fillet(2, rim.AsSelector("Faces"));
        var solid = filleted.ToBrep();
        solid.Validate();
        Assert.True(solid.Faces.Count() > body.ToBrep().Faces.Count());
    }

    // ---- where it STOPS (pinned so the documented boundary cannot rot) ----

    /// <summary>
    /// Rim surgery rewrites the blended face on fresh loops, so its tag is dropped; the
    /// faces it does not touch keep theirs. Fewer, never wrong — asserted as such rather
    /// than asserted away.
    /// </summary>
    [Fact]
    public void ARimFilletDropsTheTagOnTheFaceItRewrites_ButNotOnTheRest()
    {
        var body = (Plate() | Boss("boss")).Tag("body");
        var solid = body.Fillet(1.5, FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).AsSelector("f")).ToBrep();

        int tagged = TaggedFaceCount(solid, "body");
        Assert.True(tagged > 0, "untouched faces must keep their tag");
        Assert.True(tagged < solid.Faces.Count(),
            "the rewritten top faces and the new blend bands carry no tag — if this ever " +
            "passes, provenance now survives rim surgery and the docs must say so");
    }

    [Fact]
    public void WholeSolidRoundingDropsProvenanceEntirely()
    {
        // FilletAllEdges rebuilds every face; nothing survives by reference.
        var solid = Shape.Box(20, 14, 8).Tag("block").RoundEdges(2).ToBrep();
        Assert.Equal(0, TaggedFaceCount(solid, "block"));
    }

    [Fact]
    public void DraftDropsProvenanceEntirely()
    {
        var solid = Shape.Box(30, 20, 10).Tag("block")
            .Draft(5, (0, 0, -5), Vector3d.UnitZ)
            .ToBrep();
        Assert.Equal(0, TaggedFaceCount(solid, "block"));
    }

    [Fact]
    public void ImplicitAndMeshLoweringsCarryNoProvenance()
    {
        // Nothing to assert about a distance field's faces, because it has none — the test
        // exists to state that this is by design and not an omission.
        var tagged = (Plate() | Boss("boss")).Tag("all");
        Assert.NotNull(tagged.ToImplicit());
        Assert.True(tagged.ToMesh().FaceCount > 0);
    }
}
