using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The closed forms behind the adjacent-opening rim surgery, the holed drafted cap and the
/// sense-aware cavity twin. The BRep-side tests pin exact positions, loop content and every
/// refusal; these are the identities that need an integral — and for adjacent openings they
/// are the ONLY honest oracle, because a half-merged rim leaves a solid that validates and is
/// wrong (every edge used twice, every loop closed, and material where the rim should be open).
/// </summary>
public class ShellDraftVolumeTests
{
    private static readonly BrepMassPropertyOptions Fine = new() { SegmentsPerCircle = 96, CurveSamples = 48 };

    private static double Volume(BrepSolid solid) => BrepMassProperties.Compute(solid, options: Fine).Volume;

    /// <summary>
    /// A RELATIVE comparison, because a curved body's volume comes back at the extrapolated
    /// mass-properties grade (~1e-7 relative) and an absolute decimal count silently states a
    /// different bar at every model size — the recorded absolute-epsilon-on-an-integral trap.
    /// </summary>
    private static void EqualRelative(double expected, double actual, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance * Math.Abs(expected),
            $"expected {expected:R}, measured {actual:R} ({Math.Abs(actual - expected) / Math.Abs(expected):e2} relative)");

    private static BrepFace FaceWithNormal(BrepSolid solid, Vector3d normal) =>
        solid.PlanarFacesWithNormal(normal).Single();

    // ---- adjacent openings ----

    [Fact]
    public void ATrayOpenOnTopAndOneSide_RemovesExactlyTheCavityPrism()
    {
        // A 10-cube, 1 thick, open at z = 10 and at x = 10. Both opening planes stay put, so
        // the cavity is exactly [1, 10] x [1, 9] x [1, 10] and the shell is the difference of
        // two boxes — a closed form on both sides, and the number a half-merged rim misses
        // (leaving material across the opening) while still validating.
        var cube = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);
        var tray = Shelling.Shell(cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side));
        tray.Validate();

        const double cavity = 9 * 8 * 9;
        Assert.Equal(1000 - cavity, Volume(tray), 9);

        // ... and it tessellates closed, which is what separates "the loops are consistent"
        // from "the solid is whole".
        Assert.True(BRepTessellator.Tessellate(tray).IsClosed);
    }

    [Fact]
    public void OpeningTheSecondFaceRemovesEXACTLYTheStripTheFirstRimHeld()
    {
        // The mutation that proves the merge is doing work rather than merely validating:
        // opening the side as WELL as the top removes exactly the wall that was there, which
        // is the 1-thick slab over the cavity's own cross-section. Comparing against the
        // one-opening tray means a rim that silently kept its annulus is measurably heavy.
        var cube = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);

        double openTop = Volume(Shelling.Shell(cube, 1, f => ReferenceEquals(f, top)));
        double openBoth = Volume(Shelling.Shell(
            cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side)));

        // The wall at x in [9, 10] over the cavity's y in [1, 9] and z in [1, 10].
        Assert.Equal(1 * 8 * 9, openTop - openBoth, 9);
    }

    [Fact]
    public void TwoOppositeSharedEdges_CutTheRimIntoTwoFacesAndTheVolumeStillCloses()
    {
        // Open the top and BOTH x faces: the top rim's region is two disjoint strips, so it
        // comes back as two faces — and the cavity is still one closed-form prism.
        var cube = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var plusX = FaceWithNormal(cube, Vector3d.UnitX);
        var minusX = FaceWithNormal(cube, -Vector3d.UnitX);
        var shelled = Shelling.Shell(cube, 1,
            f => ReferenceEquals(f, top) || ReferenceEquals(f, plusX) || ReferenceEquals(f, minusX));
        shelled.Validate();

        const double cavity = 10 * 8 * 9;   // x spans the whole 10 now, y [1, 9], z [1, 10]
        Assert.Equal(1000 - cavity, Volume(shelled), 9);
        Assert.True(BRepTessellator.Tessellate(shelled).IsClosed);
    }

    [Fact]
    public void AMergedRimSolidSurvivesAFurtherBoolean()
    {
        // The surgery hands its result back to the kernel, so it has to be a solid every
        // downstream stage accepts rather than one that merely validates.
        var cube = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);
        var tray = Shelling.Shell(cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side));

        var drilled = BrepBoolean.Difference(
            tray, Shape.Cylinder(1.5, 20).Translate(3, 5, 0).ToBrep());
        drilled.Validate();

        // The bore passes through the 1-thick floor only (the cavity above it is already
        // empty), so it removes exactly one disc of material.
        EqualRelative(1000 - 9 * 8 * 9 - Math.PI * 1.5 * 1.5 * 1, Volume(drilled));
    }

    // ---- a holed drafted cap ----

    [Theory]
    [InlineData(10.0)]
    [InlineData(-6.0)]
    public void ADraftedHoledPlate_MatchesTheClosedFormWhoseQuadraticTermsCANCEL(double degrees)
    {
        // V = A0*h - tan(t)*h^2*(W + D + A + B). The z^2 terms cancel EXACTLY between the
        // shrinking outside and the growing hole, which is why this is a closed form at all
        // — and it is the identity that separates "the hole tapered" from "the hole tapered
        // the same way as the outside", since a hole drafted the wrong way flips the sign of
        // its own contribution.
        const double w = 20, d = 20, a = 8, b = 8, h = 6;
        double angle = degrees * Math.PI / 180;

        var plate = Profile.FromPoints([(-w / 2, -d / 2, 0), (w / 2, -d / 2, 0),
                                        (w / 2, d / 2, 0), (-w / 2, d / 2, 0)]);
        var bore = Profile.FromPoints([(-a / 2, -b / 2, 0), (a / 2, -b / 2, 0),
                                       (a / 2, b / 2, 0), (-a / 2, b / 2, 0)]);
        var solid = SolidFactory.Extrude(plate, (0, 0, h), holes: [bore]);
        var drafted = Draft.Apply(solid, Vector3d.Zero, Vector3d.UnitZ, angle);
        drafted.Validate();

        double expected = (w * d - a * b) * h - Math.Tan(angle) * h * h * (w + d + a + b);
        Assert.Equal(expected, Volume(drafted), 9);
        Assert.True(BRepTessellator.Tessellate(drafted).IsClosed);
    }

    // ---- a reversed curved face: shelling boolean output ----

    /// <summary>
    /// A tube whose bore came from a BOOLEAN, so its wall carries <see cref="BrepFace.IsReversed"/>
    /// — the input the sense-aware cavity twin exists for.
    /// </summary>
    private static BrepSolid BoredHousing()
    {
        var outer = SolidFactory.MakeCylinder(20, 30);
        var bore = SolidFactory.MakeCylinder(9, 40)
            .Transformed(Matrix4d.CreateTranslation((0, 0, -5)));
        var housing = BrepBoolean.Difference(outer, bore);
        housing.Validate();
        Assert.Contains(housing.Faces, f =>
            f.IsReversed && f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 9) < 1e-6);
        return housing;
    }

    [Fact]
    public void ShellingAHousingWhoseBoreCameFromABoolean_IsTheExactAnnularCavity()
    {
        // The residual the curved-shelling scope test pinned: a difference marks the
        // subtracted tool's walls IsReversed, and the cavity twin used to hard-code
        // IsReversed = true — right for a forward parent and inside out for this one. The
        // VOLUME is the oracle because a flipped cavity wall still validates: the flag does
        // not touch a loop, so no structural check can see it, and only an integral through
        // the tessellator (which reverses a reversed face's polygons) reads it back.
        var shelled = Shelling.Shell(BoredHousing(), 2);
        shelled.Validate();
        Assert.Equal(2, shelled.Shells.Count);

        // The cavity twin of the BORE wall must face +radial (material at 9 < r < 11 lies
        // inside it), which for a reversed parent means NOT reversed — the whole rule.
        var cavityBore = shelled.Shells[1].Faces.Single(f =>
            f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 11) < 1e-6);
        Assert.False(cavityBore.IsReversed);

        // Outer tube less the cavity tube: the cavity's outer wall shrank to 18, its bore
        // GREW to 11 (a bore's outward normal points into the void), and the caps moved in 2.
        double outer = Math.PI * (20 * 20 - 9 * 9) * 30;
        double cavity = Math.PI * (18 * 18 - 11 * 11) * 26;
        EqualRelative(outer - cavity, Volume(shelled));
        Assert.True(BRepTessellator.Tessellate(shelled, segmentsPerCircle: 64).IsClosed);
    }

    [Fact]
    public void ShellingABLINDBoredBodyThroughItsFlatFace_IsTheOpenCup()
    {
        // The same reversed bore, now with an OPENING: a cylinder bored from the top only, so
        // its bottom cap is a plain disc a rim can be built on (an ANNULAR opening face is
        // refused by name, since its rim would need one annulus per hole). Every one of the
        // five faces moves — including the bore's own FLOOR, whose outward normal points up
        // into the bore, so it moves DOWN — and the cavity is still a closed form.
        var body = BrepBoolean.Difference(
            SolidFactory.MakeCylinder(20, 30),
            SolidFactory.MakeCylinder(9, 25).Transformed(Matrix4d.CreateTranslation((0, 0, 10))));
        body.Validate();

        var bottom = body.Faces.Single(f =>
            f.IsPlanar(out var o, out var n) && n.Normalized().Z < -0.99 && Math.Abs(o.Z) < 1e-9);
        var cup = Shelling.Shell(body, 2, f => ReferenceEquals(f, bottom));
        cup.Validate();
        Assert.Single(cup.Shells);

        // Solid: a full disc below the bore plus an annulus above it.
        double solid = Math.PI * (20 * 20 * 10 + (20 * 20 - 9 * 9) * 20);
        // Cavity: r < 18 up to the moved bore floor at z = 8, then the annulus 11 < r < 18 up
        // to the moved top cap at z = 28. The opening plane at z = 0 does not move.
        double cavity = Math.PI * (18 * 18 * 8 + (18 * 18 - 11 * 11) * 20);
        EqualRelative(solid - cavity, Volume(cup));
        Assert.True(BRepTessellator.Tessellate(cup, segmentsPerCircle: 64).IsClosed);
    }

    [Fact]
    public void AnExtrudedCircleBoresShellIsExactButItsDISPLAYMeshIsARecordedGAP()
    {
        // The same housing spelled through the Shape API lowers its walls as EXTRUDED CIRCLES
        // rather than CylinderSurfaces, and its shell is geometrically exact — Validate-clean,
        // two shells, both cavity walls at their exact radii — but the offset carrier's
        // generator keeps the SOURCE generator's phase while the rebuilt rims keep the EDGE's,
        // and the tessellator's ring-paired-band gate reads those as unpaired. So the honest
        // statement is that the SOLID is right and the display mesh refuses BY NAME, which is
        // where the residual is filed (todo.md, the shelling entry).
        var housing = (Shape.Cylinder(20, 30) - Shape.Cylinder(9, 40)).ToBrep();
        var shelled = Shelling.Shell(housing, 2);
        shelled.Validate();
        Assert.Equal(2, shelled.Shells.Count);

        // Exact radii on both cavity walls, and the bore's twin not reversed — the sense rule
        // holds on this spelling too; only the tessellation tier declines.
        var radii = shelled.Shells[1].Faces
            .Where(f => f.IsCylindrical(out _, out _, out _))
            .Select(f => { f.IsCylindrical(out _, out _, out double r); return r; })
            .OrderBy(r => r)
            .ToList();
        Assert.Equal(2, radii.Count);
        Assert.Equal(11, radii[0], 9);
        Assert.Equal(18, radii[1], 9);

        var exception = Assert.Throws<NotSupportedException>(
            () => BRepTessellator.Tessellate(shelled, segmentsPerCircle: 64));
        Assert.Contains("trimmed face could not be tessellated", exception.Message);
    }

    [Fact]
    public void DraftingAHousingWhoseBoreCameFromABoolean_OpensTheBore()
    {
        // The taper reads its lean off the OUTWARD normal, so a bore a boolean cut drafts
        // OPEN going along the pull — the mould-release sense for a core pin. Both walls are
        // exact cones, so the volume is the frustum closed form.
        const double h = 30, half = h / 2;
        double angle = 8 * Math.PI / 180, t = Math.Tan(angle);
        var housing = (Shape.Cylinder(20, h) - Shape.Cylinder(9, 40)).ToBrep();
        var drafted = Draft.Apply(housing, (0, 0, -half), Vector3d.UnitZ, angle);
        drafted.Validate();

        // Outer radius 20 - t*z, bore radius 9 + t*z measured from the neutral base plane, so
        // the second-order terms cancel exactly as they do for the planar holed cap.
        double expected = Math.PI * ((400 - 81) * h - t * h * h * (20 + 9));
        EqualRelative(expected, Volume(drafted));
        Assert.True(BRepTessellator.Tessellate(drafted, segmentsPerCircle: 64).IsClosed);
    }
}
