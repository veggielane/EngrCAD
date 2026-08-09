using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <see cref="DirectEdit"/> measured. The BRep-side tests pin exact positions, topology and
/// every refusal; this is where the identities that need an integral live — and where the
/// deletion meets geometry a BOOLEAN produced, which is the input direct editing exists for
/// (an imported body has no history, and boolean output is the closest thing the kernel can
/// build to one).
/// </summary>
public class DirectEditVolumeTests
{
    private static readonly BrepMassPropertyOptions Fine = new() { SegmentsPerCircle = 96, CurveSamples = 48 };

    private static double Volume(BrepSolid solid) => BrepMassProperties.Compute(solid, options: Fine).Volume;

    private static Func<BrepFace, bool> Normal(Vector3d direction) =>
        face => face.IsPlanar(out _, out var n) && n.Normalized().Dot(direction.Normalized()) > 1 - 1e-9;

    [Theory]
    [InlineData(3.0)]
    [InlineData(-2.5)]
    public void PushingABoxFaceChangesTheVolumeByExactlyAreaTimesDistance(double distance)
    {
        // The identity, and it is exact rather than a tolerance: every face adjoining the top
        // is PARALLEL to its normal, so the moved face slides without changing shape and the
        // swept volume is a prism. A tessellated volume of a planar solid is exact too (the
        // facets tile the boundary), so both sides of this comparison are closed forms.
        var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));
        var pushed = DirectEdit.OffsetFaces(block, distance, Normal(Vector3d.UnitZ));

        const double area = 20 * 30;
        Assert.Equal(area * distance, Volume(pushed) - Volume(block), 9);
    }

    [Fact]
    public void PushingAnObliqueFace_IntegratesTheMovingArea_NotAreaTimesDistance()
    {
        // The complement, which is what makes the identity above a statement about the
        // geometry rather than about the code: where a neighbour is OBLIQUE the moved face's
        // boundary grows as it slides, so the change is the integral of a growing area. A
        // right-triangular prism pushed on its hypotenuse is the closed form
        // (base area grows quadratically), so this is checked against the exact frustum
        // rather than against area*d — which it measurably is NOT.
        var wedge = SolidFactory.Extrude(
            Profile.FromPoints([(0, 0, 0), (10, 0, 0), (0, 0, 10)]), (0, 6, 0));
        var slant = wedge.Faces.Single(f => f.IsPlanar(out _, out var n)
            && n.Normalized().AreEqual(new Vector3d(1, 0, 1).Normalized(), new Tolerance(1e-9, 1e-9)));

        const double d = 1.5;
        const double depth = 6;
        var pushed = DirectEdit.OffsetFaces(wedge, d, f => ReferenceEquals(f, slant));

        // Both legs grow to 10 + d*sqrt(2), so the triangle's area is half the square of that.
        double leg = 10 + d * Math.Sqrt(2);
        double expected = 0.5 * leg * leg * depth;
        Assert.Equal(expected, Volume(pushed), 8);

        // And the naive area*d answer is measurably wrong: the hypotenuse's area is
        // 10*sqrt(2)*6 = 84.85, so area*d predicts 127.28 against a true 140.78 — the extra
        // 13.5 being the frustum term d^2*(perimeter cotangent sum), which is 10.6% of the
        // change and could not be dismissed as tessellation error.
        double naive = 10 * Math.Sqrt(2) * depth * d;
        double actual = Volume(pushed) - Volume(wedge);
        Assert.Equal(140.78, actual, 2);
        Assert.True(actual - naive > 13, $"expected the frustum term to show; naive {naive:F3}, actual {actual:F3}");
    }

    [Fact]
    public void PushingACylindersWall_GrowsTheVolumeByTheAnnulus()
    {
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var grown = DirectEdit.OffsetFaces(cylinder, 1.5, f => f.Surface is CylinderSurface);

        // Against the ANALYTIC annulus, not against an inscribed n-gon: BrepMassProperties
        // extrapolates by default, so the measured volume converges on pi*r^2*h rather than
        // on the discrete prism. (Written the other way round first, and the discrete oracle
        // duly disagreed with a correct answer by 0.39.)
        Assert.Equal(Math.PI * (6.5 * 6.5 - 5 * 5) * 10, Volume(grown) - Volume(cylinder), 4);
    }

    [Fact]
    public void MovingAPlanarFaceSideways_ChangesNoVolume()
    {
        // A plane slid along itself is the same plane. The volume identity says so with an
        // integral where the BRep-side test says so with coordinates.
        var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));
        var moved = DirectEdit.MoveFaces(block, (7, -3, 0), Normal(Vector3d.UnitZ));
        Assert.Equal(Volume(block), Volume(moved), 9);
    }

    [Fact]
    public void DeletingABossFromABooleanResult_RestoresThePlateExactly()
    {
        // The entry's verification, on the input the feature exists for: a boss UNIONED onto a
        // plate has no history, so the only handle on it is its faces. The boolean rebuilt the
        // plate's top face (it now carries the boss's rim as a hole loop), so this is not the
        // trivial case the BRep-side through-hole test covers.
        // Shape.Box and Shape.Cylinder are both CENTRED, so the plate spans z in [-4, 4] and
        // the boss z in [1.5, 6.5] — standing 2.5 proud of the plate's top face.
        var plate = Shape.Box(40, 30, 8);
        var boss = Shape.Cylinder(6, 5).Translate(0, 0, 4);
        var withBoss = (plate | boss).ToBrep();
        withBoss.Validate();

        // The boss is everything standing proud of the plate's top plane, located by
        // Bounds() rather than by IsPlanar's origin (which for the boss cap is its seam
        // VERTEX at (6, 0, 6.5), not its centre) and not by surface TYPE either: the lowered
        // boss's wall arrives as an ExtrudedSurface, not a CylinderSurface.
        // (Max rather than Min: the boss WALL starts exactly at the plate's top plane, so a
        // Min.Z test cannot separate it from the plate's own top face.)
        var bossFaces = new HashSet<BrepFace>(withBoss.Faces.Where(f => f.Bounds().Max.Z > 4 + 1e-9));
        Assert.Equal(2, bossFaces.Count);

        var restored = DirectEdit.DeleteFaces(withBoss, bossFaces.Contains);
        restored.Validate();
        Assert.True(restored.SatisfiesEulerFormula(genus: 0));

        // A plain 40x30x8 box, to the grade a planar tessellation measures (exact).
        Assert.Equal(6, restored.Faces.Count());
        Assert.Equal(40.0 * 30 * 8, Volume(restored), 9);
        Assert.All(restored.Faces, f => Assert.Single(f.Loops));

        // And it still tessellates closed — the check that separates "the loop reference is
        // gone" from "the solid is whole".
        var mesh = BRepTessellator.Tessellate(restored);
        Assert.True(mesh.IsClosed);
    }

    [Fact]
    public void DeletingAPocketFromABooleanResult_FillsIt()
    {
        // The subtractive twin: a blind pocket's walls and floor leave a hole loop in the face
        // they were cut through, so the same rule fills it back in.
        // The plate spans z in [-4, 4]; the tool z in [2, 6], so the pocket is 2 deep and
        // opens through the top face.
        var plate = Shape.Box(40, 30, 8);
        var pocket = Shape.Box(14, 10, 4).Translate(0, 0, 4);
        var pocketed = (plate - pocket).ToBrep();
        pocketed.Validate();
        double removed = 14.0 * 10 * 2;
        Assert.Equal(40.0 * 30 * 8 - removed, Volume(pocketed), 9);

        // The liner is every face contained in the tool's footprint: four walls and the
        // floor. Located by Bounds() — IsPlanar's origin is an arbitrary in-plane point and
        // the plate's own top face carries one inside the pocket too.
        var liner = new HashSet<BrepFace>(pocketed.Faces.Where(f =>
        {
            var b = f.Bounds();
            return b.Min.X > -7 - 1e-9 && b.Max.X < 7 + 1e-9
                && b.Min.Y > -5 - 1e-9 && b.Max.Y < 5 + 1e-9;
        }));
        Assert.Equal(5, liner.Count);

        var filled = DirectEdit.DeleteFaces(pocketed, liner.Contains);
        filled.Validate();
        Assert.Equal(6, filled.Faces.Count());
        Assert.Equal(40.0 * 30 * 8, Volume(filled), 9);
        Assert.True(BRepTessellator.Tessellate(filled).IsClosed);
    }

    [Theory]
    [InlineData(2.0, 7.0)]   // +2 grows the SOLID: the bore wall's outward normal points into
    [InlineData(-2.0, 11.0)] // the void, so a positive offset shrinks the bore (r9 -> r7).
    public void OffsettingACurvedFaceOfBOOLEANOutput_MovesTheBoreAlongItsOutwardNormal(
        double distance, double expectedBoreRadius)
    {
        // The residual the direct-editing scope test pinned as a known boundary: a difference
        // marks the subtracted tool's walls IsReversed, and CarrierBody used to refuse a
        // reversed face outright. A reversed face's OUTWARD normal is well-defined (it is the
        // negative of its surface's), so Lift offsets its surface by -distance and the bore a
        // boolean cut can now be pushed like any other face. This is the input direct editing
        // exists for — boolean output has no history, the closest the kernel builds to an
        // imported body.
        var housing = (Shape.Cylinder(20, 30) - Shape.Cylinder(9, 40)).ToBrep();
        housing.Validate();
        Assert.True(housing.SatisfiesEulerFormula(genus: 1));

        // Exactly one reversed cylindrical face at radius 9 — the bore wall.
        var boreWall = housing.Faces.Single(f =>
            f.IsReversed && f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 9) < 1e-6);

        var edited = DirectEdit.OffsetFaces(housing, distance, f => ReferenceEquals(f, boreWall));
        edited.Validate();
        Assert.True(edited.SatisfiesEulerFormula(genus: 1));

        // The bore wall is now a cylinder of the expected radius, still reversed, and the outer
        // wall (r20) did not move.
        Assert.Contains(edited.Faces, f =>
            f.IsReversed && f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - expectedBoreRadius) < 1e-6);
        Assert.Contains(edited.Faces, f =>
            f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 20) < 1e-6);

        // Volume change is the exact annulus swept by the moving wall over the 30-tall bore:
        // pi*(9^2 - r'^2)*30 (positive when the bore shrinks). Against the analytic value,
        // since BrepMassProperties extrapolates.
        double expectedChange = Math.PI * (9 * 9 - expectedBoreRadius * expectedBoreRadius) * 30;
        Assert.Equal(expectedChange, Volume(edited) - Volume(housing), 3);

        // And it still tessellates closed — the check that the edit produced a whole solid.
        Assert.True(BRepTessellator.Tessellate(edited, segmentsPerCircle: 64).IsClosed);
    }

    [Fact]
    public void AnEditedSolidSurvivesAFurtherBoolean()
    {
        // Direct editing hands its output back to the kernel, so the result has to be a solid
        // every downstream stage accepts — not merely one that validates.
        var plate = SolidFactory.MakeBox(new Aabb((0, 0, 0), (40, 30, 8)));
        var thicker = DirectEdit.OffsetFaces(plate, 4, Normal(Vector3d.UnitZ));

        var drilled = BrepBoolean.Difference(thicker, (Shape.Cylinder(5, 40).Translate(20, 15, 0)).ToBrep());
        drilled.Validate();
        Assert.True(drilled.SatisfiesEulerFormula(genus: 1));

        Assert.Equal(40.0 * 30 * 12 - Math.PI * 25 * 12, Volume(drilled), 4);
    }
}
