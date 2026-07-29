using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Face provenance through the KERNEL rebuild sites — <see cref="Draft"/>,
/// <see cref="Shelling"/>, <see cref="Filleting"/> and <see cref="ShapeHealing"/> — each of
/// which discards its input's face objects and constructs replacements.
///
/// <para>These are measurements rather than smoke tests. A rebuild site inherits soundly only
/// if it hands each new face its OWN positional parent, so every test here tags exactly one
/// face and then asserts <b>where the tag landed</b>, not merely that some face has it: an
/// off-by-one in a parent array would leave the count right and the meaning wrong, which is
/// the one failure a naming scheme must not have. The complementary half — that genuinely new
/// surface (a fillet band, a corner patch, a termination face) inherits NOTHING — is asserted
/// beside it, because that is what keeps the failure one-sided.</para>
///
/// <para>Faces are located by <c>Bounds().Center</c> throughout. <c>IsPlanar</c>'s origin is an
/// ARBITRARY in-plane point and a circular loop's face-frame origin is its single seam vertex,
/// so both read the rim rather than the middle — a recorded trap that bites an assertion
/// exactly as hard as it bites a query.</para>
/// </summary>
public class FaceProvenanceTests
{
    private const double Ten = Math.PI / 18;

    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((-10, -10, 0), (10, 10, 10)));

    /// <summary>Every face tagged, so a drop shows up as a missing face rather than a guess.</summary>
    private static BrepSolid TagAll(BrepSolid solid, string tag)
    {
        foreach (var face in solid.Faces)
            face.AddProvenance(tag);
        return solid;
    }

    private static int Tagged(BrepSolid solid, string tag) =>
        solid.Faces.Count(f => f.Provenance.Contains(tag));

    /// <summary>The one face carrying a tag — failing loudly on nought or several, which is
    /// the cardinality claim each "where did it land" test is making.</summary>
    private static BrepFace OnlyTagged(BrepSolid solid, string tag) =>
        Assert.Single(solid.Faces, f => f.Provenance.Contains(tag));

    private static BrepFace FaceFacing(BrepSolid solid, Vector3d direction) =>
        solid.PlanarFacesWithNormal(direction).Single();

    // ---- Draft: the planar prism rebuild -------------------------------------

    [Fact]
    public void Draft_PlanarPrism_CarriesEveryFacesTag()
    {
        var block = TagAll(Block(), "block");
        var bottom = FaceFacing(block, -Vector3d.UnitZ);
        var drafted = Draft.Apply(block, bottom, Ten);
        drafted.Validate();

        // BuildPrism rebuilds ALL six faces, so all six must inherit; the operation adds no
        // surface of its own.
        Assert.Equal(6, drafted.Faces.Count());
        Assert.Equal(6, Tagged(drafted, "block"));
    }

    [Fact]
    public void Draft_PlanarPrism_PutsEachTagOnTheWallItNamed()
    {
        // The assertion with teeth: one wall named, and the tag must land on the wall that
        // wall BECAME. A side face keeps its outward direction under a taper (it only tilts
        // by 10 degrees), so the +X wall is still the +X-most face.
        var block = Block();
        var plusX = block.Faces.Single(f =>
            f.IsPlanar(out _, out var n) && n.Normalized().Dot(Vector3d.UnitX) > 0.99);
        plusX.AddProvenance("east");
        var bottom = FaceFacing(block, -Vector3d.UnitZ);

        var drafted = Draft.Apply(block, bottom, Ten);
        var carrier = OnlyTagged(drafted, "east");
        Assert.True(carrier.IsPlanar(out _, out var normal));
        Assert.True(normal.Normalized().Dot(Vector3d.UnitX) > 0.9,
            "the tag must ride the wall it named, not a neighbour");
        // And it is the +X-most face by BOUNDS, which is what actually identifies it.
        Assert.Equal(
            drafted.Faces.Max(f => f.Bounds().Center.X), carrier.Bounds().Center.X, 9);
    }

    [Fact]
    public void Draft_PlanarPrism_KeepsTheCapsDistinctFromEachOther()
    {
        var block = Block();
        FaceFacing(block, Vector3d.UnitZ).AddProvenance("lid");
        var drafted = Draft.Apply(block, FaceFacing(block, -Vector3d.UnitZ), Ten);

        var lid = OnlyTagged(drafted, "lid");
        Assert.Equal(10, lid.Bounds().Center.Z, 9);   // the TOP cap, not the base
    }

    [Fact]
    public void Draft_CurvedPath_CarriesTagsThroughTheCarrierRebuild()
    {
        // The curved path is a different rebuild entirely (CarrierBody.Rebuild), so it needs
        // its own measurement: a cylinder drafts to a cone.
        var cylinder = TagAll(SolidFactory.MakeCylinder(5, 10), "shaft");
        var drafted = Draft.Apply(cylinder, (0, 0, 0), Vector3d.UnitZ, Ten);
        drafted.Validate();
        Assert.Equal(cylinder.Faces.Count(), drafted.Faces.Count());
        Assert.Equal(drafted.Faces.Count(), Tagged(drafted, "shaft"));
    }

    [Fact]
    public void Draft_CurvedPath_PutsTheWallsTagOnTheCone()
    {
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        cylinder.Faces.Single(f => f.Surface is CylinderSurface).AddProvenance("wall");
        var drafted = Draft.Apply(cylinder, (0, 0, 0), Vector3d.UnitZ, Ten);

        var carrier = OnlyTagged(drafted, "wall");
        // A drafted cylinder is EXACTLY a cone — a revolved slanted line — so the tag must be
        // on the curved face, never on a cap.
        Assert.False(carrier.IsPlanar(out _, out _));
    }

    // ---- Shelling: polyhedral and curved -------------------------------------

    [Fact]
    public void Offset_Polyhedral_CarriesEveryFacesTag()
    {
        var box = TagAll(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), "plate");
        var grown = Shelling.Offset(box, 0.5);
        grown.Validate();
        Assert.Equal(6, Tagged(grown, "plate"));
    }

    [Fact]
    public void Offset_Polyhedral_PutsTheTagOnTheFaceThatMoved()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4)));
        FaceFacing(box, Vector3d.UnitZ).AddProvenance("lid");
        var grown = Shelling.Offset(box, 0.5);

        var carrier = OnlyTagged(grown, "lid");
        Assert.Equal(4.5, carrier.Bounds().Center.Z, 9);   // the lid, offset outward
    }

    [Fact]
    public void Shell_Polyhedral_GivesTheWallAndItsCavityTwinTheSameTag()
    {
        // The documented one-parent-TWO-children case: a wall and its inward twin both
        // descend from the face that generated them. Provenance is a SET, so this is
        // representable; the count is a property of the operation, not of the tag.
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));
        FaceFacing(box, Vector3d.UnitZ).AddProvenance("lid");
        var shelled = Shelling.Shell(box, 2);
        shelled.Validate();

        var carriers = shelled.Faces.Where(f => f.Provenance.Contains("lid")).ToList();
        Assert.Equal(2, carriers.Count);
        // The outer lid stays at z = 10; its cavity twin sits one wall thickness below.
        Assert.Contains(carriers, f => Math.Abs(f.Bounds().Center.Z - 10) < 1e-9);
        Assert.Contains(carriers, f => Math.Abs(f.Bounds().Center.Z - 8) < 1e-9);
    }

    [Fact]
    public void Shell_Polyhedral_GivesAnOpeningsRimItsRemovedFacesTag()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));
        var lid = FaceFacing(box, Vector3d.UnitZ);
        lid.AddProvenance("lid");
        var tray = Shelling.Shell(box, 2, f => ReferenceEquals(f, lid));
        tray.Validate();

        // The lid became the RIM (an annulus in the lid's own plane), and nothing else.
        var carrier = OnlyTagged(tray, "lid");
        Assert.Equal(10, carrier.Bounds().Center.Z, 9);
        Assert.Equal(2, carrier.Loops.Count);
    }

    [Fact]
    public void Shell_Curved_CarriesTagsThroughTheCarrierRebuild()
    {
        var cylinder = TagAll(SolidFactory.MakeCylinder(5, 10), "cup");
        var shelled = Shelling.Shell(cylinder, 1);
        shelled.Validate();
        // Every face contributes an outer and an inner, so every one of them is tagged.
        Assert.Equal(shelled.Faces.Count(), Tagged(shelled, "cup"));
    }

    // ---- Filleting: whole-solid rounding -------------------------------------

    [Fact]
    public void FilletAllEdges_KeepsTagsOnTheShrunkFacesAndNotOnTheNewOnes()
    {
        // The shrunk originals inherit; the bands and corner patches are genuinely new
        // surface and must NOT, which is what keeps a query one-sided.
        var box = TagAll(SolidFactory.MakeBox(new Aabb((0, 0, 0), (4, 3, 2))), "block");
        var rounded = Filleting.FilletAllEdges(box, 0.5);
        rounded.Validate();

        Assert.Equal(26, rounded.Faces.Count());          // 6 shrunk + 12 bands + 8 corners
        Assert.Equal(6, Tagged(rounded, "block"));
        Assert.All(
            rounded.Faces.Where(f => f.Provenance.Contains("block")),
            f => Assert.IsType<PlaneSurface>(f.Surface));
    }

    [Fact]
    public void FilletAllEdges_PutsEachTagOnTheFaceItNamed()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (4, 3, 2)));
        FaceFacing(box, Vector3d.UnitZ).AddProvenance("lid");
        var rounded = Filleting.FilletAllEdges(box, 0.5);

        var carrier = OnlyTagged(rounded, "lid");
        Assert.Equal(2, carrier.Bounds().Center.Z, 9);    // still the lid's own plane
    }

    // ---- Filleting: rim surgery ----------------------------------------------

    [Fact]
    public void FilletRim_KeepsTheBlendedFacesTagAndLeavesTheBandsUntagged()
    {
        var box = TagAll(SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))), "body");
        var top = FaceFacing(box, Vector3d.UnitZ);
        var filleted = Filleting.FilletRim(box, top, 2);
        filleted.Validate();

        // Every original face survives tagged — the rewritten top and the trimmed
        // neighbours included — while each new blend band carries nothing.
        Assert.Equal(6, Tagged(filleted, "body"));
        Assert.True(filleted.Faces.Count() > 6, "the surgery must add bands");
        Assert.All(
            filleted.Faces.Where(f => !f.Provenance.Contains("body")),
            f => Assert.Empty(f.Provenance));
    }

    [Fact]
    public void FilletRim_PutsTheTagOnTheRewrittenFaceItself()
    {
        // The site the boundary used to stop at: rim surgery rebuilds the blended face on
        // fresh loops, so this is the assertion that says the parent is threaded.
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8)));
        var top = FaceFacing(box, Vector3d.UnitZ);
        top.AddProvenance("lid");
        var filleted = Filleting.FilletRim(box, top, 2);

        var carrier = OnlyTagged(filleted, "lid");
        Assert.Equal(8, carrier.Bounds().Center.Z, 9);
        // It really is the SHRUNK lid, not the original object handed through.
        Assert.False(ReferenceEquals(carrier, top));
        Assert.True(carrier.Bounds().Size.X < 20, "the blended face's rim moved inward");
    }

    [Fact]
    public void ChamferRim_CarriesTagsThroughTheTrimmedNeighbours()
    {
        // A cylinder's wall is an ExtrudedSurface, so TrimNeighborBand rebuilds it — a
        // separate derive site from the blended face, with its own parent.
        var cylinder = SolidFactory.MakeCylinder(6, 12);
        var wall = cylinder.Faces.Single(f => !f.IsPlanar(out _, out _));
        wall.AddProvenance("bore");
        var top = cylinder.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

        var chamfered = Filleting.ChamferRim(cylinder, top, 1.5);
        chamfered.Validate();
        var carrier = OnlyTagged(chamfered, "bore");
        Assert.False(carrier.IsPlanar(out _, out _),
            "the trimmed neighbour is still the curved wall");
    }

    // ---- ShapeHealing ---------------------------------------------------------

    [Fact]
    public void Healing_CarriesTagsThroughTheWorkFaceRebuild()
    {
        // Healing rebuilds every face through its own working copy. It only ever KILLS faces
        // or rewires their loops — it never fuses two into one — so the parent stays 1:1.
        var box = TagAll(SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10))), "import");
        var healed = ShapeHealing.Heal(box).Solid;
        Assert.Equal(healed.Faces.Count(), Tagged(healed, "import"));
    }

    [Fact]
    public void Healing_PutsTheTagOnTheSameFace()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));
        FaceFacing(box, Vector3d.UnitZ).AddProvenance("lid");
        var healed = ShapeHealing.Heal(box).Solid;

        var carrier = OnlyTagged(healed, "lid");
        Assert.Equal(10, carrier.Bounds().Center.Z, 9);
    }

    // ---- the guarantee itself -------------------------------------------------

    [Fact]
    public void AnUntaggedInputProducesNoTagsAnywhere()
    {
        // The floor under every site: inheritance COPIES a parent's list, so a solid nobody
        // named can never acquire a name. Stated once over every rebuild rather than left to
        // the tests above happening to cover each path.
        static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8)));
        static BrepFace Top(BrepSolid s) => s.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

        var forDraft = Box();
        var rebuilt = new List<BrepSolid>
        {
            Draft.Apply(forDraft, forDraft.PlanarFacesWithNormal(-Vector3d.UnitZ).Single(), Ten),
            Shelling.Offset(Box(), 0.5),
            Shelling.Shell(Box(), 2),
            Filleting.FilletAllEdges(Box(), 1),
            ShapeHealing.Heal(Box()).Solid,
        };
        var forRim = Box();
        rebuilt.Add(Filleting.FilletRim(forRim, Top(forRim), 2));
        var forCurved = SolidFactory.MakeCylinder(5, 10);
        rebuilt.Add(Draft.Apply(forCurved, (0, 0, 0), Vector3d.UnitZ, Ten));
        rebuilt.Add(Shelling.Shell(SolidFactory.MakeCylinder(5, 10), 1));

        foreach (var solid in rebuilt)
            Assert.All(solid.Faces, f => Assert.Empty(f.Provenance));
    }
}
