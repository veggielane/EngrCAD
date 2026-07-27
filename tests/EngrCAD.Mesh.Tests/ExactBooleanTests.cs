using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The imprint boolean — the only boolean this engine has. Every case asserts the result
/// is a genuine solid — closed, manifold, right genus — as well as the right volume; a
/// boolean that gets the volume right but leaks boundary edges is not usable downstream.
/// <para>
/// The cases under "where the retired BSP path failed" are the measurements that settled
/// the question of maintaining two algorithms: they are kept because they are hostile
/// inputs worth pinning, not because anything still chooses between the two.
/// </para>
/// </summary>
public class ExactBooleanTests
{
    private static HalfEdgeMesh Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        MeshPrimitives.Box(new Aabb((x0, y0, z0), (x1, y1, z1)));

    private static void AssertSolid(HalfEdgeMesh mesh, double expectedVolume, int expectedEuler, double relativeTolerance = 1e-12)
    {
        mesh.Validate();
        Assert.True(mesh.IsClosed, "an exact boolean result must be closed");
        Assert.Equal(expectedEuler, mesh.EulerCharacteristic);
        double actual = mesh.SignedVolume();
        Assert.True(Math.Abs(actual - expectedVolume) <= relativeTolerance * Math.Max(1, Math.Abs(expectedVolume)),
            $"volume {actual} should be ≈ {expectedVolume}");
    }

    // ---------------------------------------------------------------- analytic solids

    [Fact]
    public void OverlappingBoxes_AllThreeOperations()
    {
        // A = [0,2]³, B = [1,3]³, overlap = [1,2]³.
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 1, 1, 3, 3, 3);

        AssertSolid(MeshBoolean.Union(a, b), 15, expectedEuler: 2);
        AssertSolid(MeshBoolean.Intersection(a, b), 1, expectedEuler: 2);
        AssertSolid(MeshBoolean.Difference(a, b), 7, expectedEuler: 2);
    }

    [Fact]
    public void ContainedBox_DifferenceLeavesTwoShells()
    {
        var outer = Box(0, 0, 0, 2, 2, 2);
        var inner = Box(0.5, 0.5, 0.5, 1.5, 1.5, 1.5);

        // A hollow solid: outer shell plus an inverted inner shell — two genus-0 shells.
        AssertSolid(MeshBoolean.Difference(outer, inner), 7, expectedEuler: 4);
        AssertSolid(MeshBoolean.Union(outer, inner), 8, expectedEuler: 2);
        AssertSolid(MeshBoolean.Intersection(outer, inner), 1, expectedEuler: 2);
    }

    [Fact]
    public void DisjointBoxes_BehaveLikeSetOperations()
    {
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(5, 5, 5, 6, 6, 6);

        AssertSolid(MeshBoolean.Union(a, b), 9, expectedEuler: 4); // two bodies
        AssertSolid(MeshBoolean.Difference(a, b), 8, expectedEuler: 2);
        Assert.Equal(0, MeshBoolean.Intersection(a, b).FaceCount);
    }

    [Fact]
    public void BoxMinusThroughCylinder_IsGenusOneWithTheAnalyticVolume()
    {
        // Offset so no feature of either mesh lines up with the other.
        var box = Box(-1, -1, -1, 1, 1, 1);
        var cylinder = MeshPrimitives.Cylinder(0.4, 4, 24)
            .Transformed(Matrix4d.CreateTranslation((0.13, -0.07, -2)));
        // The bore is the cylinder's own 24-gon prism through 2 units of material.
        double bore = 0.5 * 24 * Math.Sin(2 * Math.PI / 24) * 0.4 * 0.4 * 2;

        var result = MeshBoolean.Difference(box, cylinder);

        AssertSolid(result, 8 - bore, expectedEuler: 0); // one through hole
    }

    [Fact]
    public void BoxMinusSphere_MatchesTheTessellatedSphereVolume()
    {
        var box = Box(-1.5, -1.5, -1.5, 1.5, 1.5, 1.5);
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);

        var result = MeshBoolean.Difference(box, sphere);

        // The cavity is exactly the tessellated sphere, so this is an identity, not an
        // approximation: two shells, volume difference to the last few ulps.
        AssertSolid(result, 27 - sphere.Volume(), expectedEuler: 4, relativeTolerance: 1e-12);
    }

    [Fact]
    public void BoxSphereIntersection_IsExactlyHalfTheSphere()
    {
        var box = Box(0, -2, -2, 2, 2, 2);
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);

        var result = MeshBoolean.Intersection(box, sphere);

        // The sphere's meridians land on the cutting plane, so the half is exact.
        AssertSolid(result, sphere.Volume() / 2, expectedEuler: 2, relativeTolerance: 1e-12);
    }

    [Fact]
    public void SphereCenteredOnABoxCorner_ReconcilesCrossingsThatAreVerticesOnBothMeshes()
    {
        // The sphere's centre sits ON the box's corner, so the curve runs through three of
        // the box's edges and the sphere's own meridian/parallel vertices land exactly on
        // the box's face planes. Those crossings are vertices of BOTH meshes at once, and
        // the two meshes must agree on one coordinate for them or the seam stops welding.
        var box = Box(0, 0, 0, 2, 2, 2);
        var sphere = MeshPrimitives.UvSphere(1.2, segments: 24, rings: 12);

        var result = MeshBoolean.Difference(box, sphere);

        // The sphere's centre is a box corner, so exactly one octant of it is removed and
        // the rest of the sphere never enters the box: the cavity is an eighth of the
        // tessellated sphere's volume, and that is an identity rather than an approximation
        // (both the meridians at u = 0/90 degrees and the equator lie in the box's planes).
        AssertSolid(result, 8 - sphere.Volume() / 8, expectedEuler: 2, relativeTolerance: 1e-12);
    }

    [Fact]
    public void CascadedDifferences_DrillThreeBoresIntoAPlate()
    {
        var plate = Box(0, 0, 0, 4, 4, 1);
        HalfEdgeMesh Tool(double x, double y) => MeshPrimitives.Cylinder(0.5, 3, 32)
            .Transformed(Matrix4d.CreateTranslation((x, y, -1)));
        double bore = 0.5 * 32 * Math.Sin(2 * Math.PI / 32) * 0.25;

        var result = MeshBoolean.Difference(
            MeshBoolean.Difference(
                MeshBoolean.Difference(plate, Tool(1, 1)), Tool(3, 1)), Tool(2, 3));

        // Booleans compose: the output of one is clean enough to be the input of the next.
        AssertSolid(result, 16 - 3 * bore, expectedEuler: -4); // genus 3
    }

    [Fact]
    public void CrossedCylinders_SatisfyTheInclusionExclusionIdentities()
    {
        // Curved surfaces crossing curved surfaces, with no analytic volume available for
        // the tessellated Steinmetz solid. The three operations still have to agree with
        // each other: |A u B| = |A| + |B| - |A n B| and |A - B| = |A| - |A n B| hold exactly
        // for the polyhedra the boolean actually produces, so they cross-check the
        // classification without a second algorithm to compare against.
        var vertical = MeshPrimitives.Cylinder(1.0, 6.0, 48).Transformed(Matrix4d.CreateTranslation((0, 0, -3)));
        var horizontal = vertical.Transformed(Matrix4d.CreateRotationX(Math.PI / 2));

        var union = MeshBoolean.Union(vertical, horizontal);
        var difference = MeshBoolean.Difference(vertical, horizontal);
        var intersection = MeshBoolean.Intersection(vertical, horizontal);

        foreach (var result in new[] { union, difference, intersection })
        {
            result.Validate();
            Assert.True(result.IsClosed);
        }

        double a = vertical.Volume(), b = horizontal.Volume(), both = intersection.SignedVolume();
        Assert.True(both > 0, "the crossing region is a solid, not an empty result");
        Assert.Equal(a + b - both, union.SignedVolume(), 9);
        Assert.Equal(a - both, difference.SignedVolume(), 9);
    }

    // ------------------------------------------------ where the retired BSP path failed

    [Fact]
    public void NearTangentBore_StaysAClosedSolid()
    {
        // The bore's flats stop one part in 1e9 short of the box's side planes. That gap is
        // exactly the absolute plane epsilon the retired BSP clipper used, so it classified
        // those facets as coincident and returned a shell with boundary edges — a hole in
        // the solid, useless for printing or FEA. The imprint path's guards are relative
        // (1e-13 of the extent), four decades clear of the gap.
        var box = Box(-1, -1, -1, 1, 1, 1);
        var cylinder = MeshPrimitives.Cylinder(0.999999999, 4, 64)
            .Transformed(Matrix4d.CreateTranslation((0, 0, -2)));

        var result = MeshBoolean.Difference(box, cylinder);

        double bore = 0.5 * 64 * Math.Sin(2 * Math.PI / 64) * 0.999999999 * 0.999999999 * 2;
        AssertSolid(result, 8 - bore, expectedEuler: 0);
    }

    [Fact]
    public void SmallModel_IsScaleFree()
    {
        // At 1e-5 scale a triangle's cross product is ~1e-10, below the absolute 1e-9 the
        // retired BSP path used to reject degenerate polygons: every face was discarded and
        // the union came back empty. Nothing here is absolute.
        const double s = 1e-5;
        var a = Box(0, 0, 0, 2 * s, 2 * s, 2 * s);
        var b = Box(s, s, s, 3 * s, 3 * s, 3 * s);

        AssertSolid(MeshBoolean.Union(a, b), 15 * s * s * s, expectedEuler: 2);
        AssertSolid(MeshBoolean.Intersection(a, b), s * s * s, expectedEuler: 2);
    }

    // ---------------------------------------------------------------- contracts

    [Fact]
    public void OpenMeshes_AreRejected()
    {
        var open = HalfEdgeMesh.Build([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [new[] { 0, 1, 2 }]);
        var box = Box(0, 0, 0, 1, 1, 1);

        Assert.Throws<ArgumentException>(() => MeshBoolean.Union(open, box));
        Assert.Throws<ArgumentException>(() => MeshBoolean.Difference(box, open));
    }

    // ------------------------------------------------------- flush-mating (coincident) parts

    [Fact]
    public void StackedBoxes_ShareAWholeFaceWithOpposingNormals()
    {
        // The canonical assembly: two parts mating face to face. The shared square is
        // interior to the union and to the difference's operand, and it bounds nothing at
        // all in the intersection — the winding number is exactly ½ on it and decides
        // nothing, so normal agreement decides instead.
        var lower = Box(0, 0, 0, 1, 1, 1);
        var upper = Box(0, 0, 1, 1, 1, 2);

        AssertSolid(MeshBoolean.Union(lower, upper), 2, expectedEuler: 2);
        AssertSolid(MeshBoolean.Difference(lower, upper), 1, expectedEuler: 2);
        // A ∩ B is the shared square: zero volume, so an empty solid rather than a sheet.
        Assert.Equal(0, MeshBoolean.Intersection(lower, upper).FaceCount);
    }

    [Fact]
    public void PartiallyOverlappingFlushFaces_KeepOnlyTheUnsharedRemainder()
    {
        // The mating square covers only part of each face, so the coincident region's rim
        // runs through the middle of both. Each operand keeps the part of its face that the
        // other does not cover, and they meet along that rim.
        var lower = Box(0, 0, 0, 2, 2, 1);
        var upper = Box(1, 0, 1, 3, 2, 2);

        AssertSolid(MeshBoolean.Union(lower, upper), 8, expectedEuler: 2);
        AssertSolid(MeshBoolean.Difference(lower, upper), 4, expectedEuler: 2);
    }

    [Fact]
    public void OverlappingSolidsSharingAPlane_KeepExactlyOneCopyOfTheSharedSurface()
    {
        // A = [0,2]³, B = [1,3]×[0,2]²: the solids interpenetrate AND their y = 0, y = 2,
        // z = 0 and z = 2 faces are coplanar with normals AGREEING over x ∈ [1,2]. Keeping
        // both copies would double the surface; keeping neither would open the result.
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 0, 0, 3, 2, 2);

        AssertSolid(MeshBoolean.Union(a, b), 12, expectedEuler: 2);
        AssertSolid(MeshBoolean.Intersection(a, b), 4, expectedEuler: 2);
        AssertSolid(MeshBoolean.Difference(a, b), 4, expectedEuler: 2);
    }

    [Fact]
    public void IdenticalOperands_AreTheDegenerateLimitOfCoincidence()
    {
        var mesh = MeshPrimitives.UvSphere(1.0, segments: 24, rings: 12);

        AssertSolid(MeshBoolean.Union(mesh, mesh), mesh.Volume(), expectedEuler: 2);
        AssertSolid(MeshBoolean.Intersection(mesh, mesh), mesh.Volume(), expectedEuler: 2);
        Assert.Equal(0, MeshBoolean.Difference(mesh, mesh).FaceCount);
    }

    [Fact]
    public void CurvedCoincidentRegion_CylinderStandingOnAPlate()
    {
        // The shared area is the cylinder's cap: its rim is a 32-gon cutting clean through
        // the plate's top face, and inside it the plate's surface disappears.
        var plate = Box(-2, -2, 0, 2, 2, 1);
        var post = MeshPrimitives.Cylinder(1, 2, 32).Transformed(Matrix4d.CreateTranslation((0, 0, 1)));
        double cap = 0.5 * 32 * Math.Sin(2 * Math.PI / 32);

        AssertSolid(MeshBoolean.Union(plate, post), 16 + 2 * cap, expectedEuler: 2);
        AssertSolid(MeshBoolean.Difference(plate, post), 16, expectedEuler: 2);
    }

    [Fact]
    public void ToolBottomFlushWithTheFarFace_StillBoresAThroughHole()
    {
        // The degenerate drilling case: the tool's flat bottom lands exactly ON the plate's
        // bottom face, so the bore's far rim is a coincident region rather than a crossing.
        var plate = Box(-2, -2, 0, 2, 2, 1);
        var tool = MeshPrimitives.Cylinder(0.5, 1, 32);
        double bore = 0.5 * 32 * Math.Sin(2 * Math.PI / 32) * 0.25;

        AssertSolid(MeshBoolean.Difference(plate, tool), 16 - bore, expectedEuler: 0);
    }

    [Fact]
    public void CoincidentSurface_IsScaleFree()
    {
        // Classification reads the SIGN of an area-weighted normal against a plane; the
        // moment it normalizes through Tolerance.Default's absolute 1e-9 it inherits the
        // BSP path's quadratic-in-scale failure and the mating faces stop being recognized
        // (measured: a non-manifold union at 1e-5).
        foreach (double s in new[] { 1e-5, 1.0, 1e5 })
        {
            var union = MeshBoolean.Union(Box(0, 0, 0, s, s, s), Box(0, 0, s, s, s, 2 * s));

            union.Validate();
            Assert.True(union.IsClosed, $"the union at scale {s} must be closed");
            Assert.Equal(2, union.EulerCharacteristic);
            // Relative, not the helper's absolute floor: at 1e-5 the volume is 2e-15 and an
            // absolute tolerance would accept an empty mesh.
            Assert.Equal(2 * s * s * s, union.SignedVolume(), 2 * s * s * s * 1e-12);
        }
    }

    [Fact]
    public void CoplanarCross_TheCaseTheBRepBooleanCannotClose()
    {
        // Two crossing slabs with IDENTICAL z-extents: both caps are coplanar overlaps.
        // BooleanFailureTests documents the B-Rep kernel refusing this shape; the mesh
        // kernel's exact path now produces it, so the implicit detour is not the only route.
        var plate = Box(-5, -5, -2, 5, 5, 2);
        var bar = Box(-2, -10, -2, 2, 10, 2);

        AssertSolid(MeshBoolean.Union(plate, bar), 400 + 320 - 160, expectedEuler: 2);
        AssertSolid(MeshBoolean.Intersection(plate, bar), 160, expectedEuler: 2);
        // The bar cuts the plate clean in two: a difference with two shells.
        AssertSolid(MeshBoolean.Difference(plate, bar), 240, expectedEuler: 4);
    }

    [Fact]
    public void CoincidentSurfaceSurvivesCascading()
    {
        // Each stage's output feeds the next, so a coincidence handled by inventing geometry
        // (rather than by dropping the duplicate) would compound.
        var l1 = Box(0, 0, 0, 3, 3, 1);
        var l2 = Box(0, 0, 1, 2, 2, 2);
        var l3 = Box(0, 0, 2, 1, 1, 3);

        var stack = MeshBoolean.Union(MeshBoolean.Union(l1, l2), l3);

        AssertSolid(stack, 9 + 4 + 1, expectedEuler: 2);
    }

    // ---------------------------------------------------------------- progress + cancel

    [Fact]
    public void Cancellation_StopsTheBooleanAndLeavesTheOperandsUntouched()
    {
        var box = Box(-1, -1, -1, 1, 1, 1);
        var cylinder = MeshPrimitives.Cylinder(0.4, 4, 64).Transformed(Matrix4d.CreateTranslation((0, 0, -2)));
        int boxFaces = box.FaceCount, cylinderFaces = cylinder.FaceCount;

        Assert.Throws<OperationCanceledException>(
            () => MeshBoolean.Difference(box, cylinder, new ProgressCancel(() => true)));

        // Kernel operations never return half-built geometry, and the inputs are immutable.
        Assert.Equal(boxFaces, box.FaceCount);
        Assert.Equal(cylinderFaces, cylinder.FaceCount);
    }

    [Fact]
    public void Progress_IsMonotoneAndReachesOne()
    {
        var reported = new List<double>();
        var box = Box(-1, -1, -1, 1, 1, 1);
        var cylinder = MeshPrimitives.Cylinder(0.4, 4, 64).Transformed(Matrix4d.CreateTranslation((0, 0, -2)));

        var result = MeshBoolean.Difference(box, cylinder, new ProgressCancel(reported.Add));

        Assert.NotEmpty(reported);
        Assert.Equal(1.0, reported[^1], 12);
        for (int i = 1; i < reported.Count; i++)
            Assert.True(reported[i] >= reported[i - 1], $"progress went backwards at {i}: {reported[i - 1]} → {reported[i]}");
        Assert.All(reported, f => Assert.InRange(f, 0, 1));
        result.Validate();
    }

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void RepeatedRuns_AreBitIdentical()
    {
        // No RNG and no scheduling-dependent ordering anywhere in the pipeline: the boolean
        // is a pure function of its operands, which is what lets downstream caches key on
        // the inputs and what makes a "bit-identical output" claim testable at all.
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 1, 1, 3, 3, 3);

        var (positions, faces) = MeshBoolean.Union(a, b).ToIndexed();
        var (againPositions, againFaces) = MeshBoolean.Union(a, b).ToIndexed();

        Assert.Equal(againPositions, positions);
        Assert.Equal(againFaces.Count, faces.Count);
        for (int f = 0; f < faces.Count; f++)
            Assert.Equal(againFaces[f], faces[f]);
    }
}
