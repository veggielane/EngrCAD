using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Shape.OffsetFaces(double, FaceSetRef)"/> and friends — the direct-editing
/// vocabulary reached through the graph, where a placement has to commute with the edit.
/// </summary>
public class DirectEditShapeTests
{
    private static readonly FaceSetRef Top = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    private static Aabb Bounds(Shape shape)
    {
        var bounds = Aabb.Empty;
        foreach (var vertex in shape.ToBrep().Vertices)
            bounds = bounds.Union(vertex.Position);
        return bounds;
    }

    [Fact]
    public void OffsetFaces_ThroughAFaceSetRef_PushesTheNamedFace()
    {
        var bounds = Bounds(Shape.Box(20, 30, 10).OffsetFaces(4, Top));
        Assert.True(bounds.Min.AreEqual((-10, -15, -5), new Tolerance(1e-9, 1e-9)));
        Assert.True(bounds.Max.AreEqual((10, 15, 9), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void OffsetFaces_ScalesItsDistanceWithAUniformPlacement()
    {
        // A distance is a LENGTH, so it rides the accumulated scale — the same rule a wall
        // thickness and a fillet radius follow. Doubling the shape must double the push.
        var edited = Shape.Box(20, 30, 10).OffsetFaces(4, Top).Scale(2);
        var bounds = Bounds(edited);
        Assert.Equal(-10, bounds.Min.Z, 9);
        Assert.Equal(18, bounds.Max.Z, 9);
    }

    [Fact]
    public void MoveFaces_UnderAMirror_KeepsTheProjectedDistance()
    {
        // The claim the Native classification rests on: the operation reduces to v.n, and a
        // reflection preserves dot products, so a mirrored move pushes by the same amount.
        // Mirroring across the x = 0 plane leaves a +Z face's projected distance alone.
        var moved = Shape.Box(20, 30, 10).MoveFaces(new Vector3d(3, -2, 4), Top);
        var mirrored = moved.Mirror(Vector3d.Zero, Vector3d.UnitX);

        var plain = Bounds(moved);
        var flipped = Bounds(mirrored);
        Assert.Equal(9, plain.Max.Z, 9);
        Assert.Equal(9, flipped.Max.Z, 9);
        Assert.Equal(-5, flipped.Min.Z, 9);
    }

    [Fact]
    public void MoveFaces_ParallelToItself_ChangesNothing()
    {
        var bounds = Bounds(Shape.Box(20, 30, 10).MoveFaces(new Vector3d(7, -3, 0), Top));
        Assert.True(bounds.Min.AreEqual((-10, -15, -5), new Tolerance(1e-9, 1e-9)));
        Assert.True(bounds.Max.AreEqual((10, 15, 5), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void DeleteFaces_TakesABossOffAnImportedStyleBody()
    {
        // The graph-level version of the Interop fixture: the union has no history to edit, so
        // the boss comes off by naming its faces.
        var withBoss = Shape.Box(40, 30, 8) | Shape.Cylinder(6, 5).Translate(0, 0, 4);
        var restored = withBoss.DeleteFaces(
            FaceSetRef.Where("boss", f => f.Bounds().Max.Z > 4 + 1e-9));

        var solid = restored.ToBrep();
        solid.Validate();
        Assert.Equal(6, solid.Faces.Count());
        var bounds = Bounds(restored);
        Assert.True(bounds.Max.AreEqual((20, 15, 4), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void EveryDirectEdit_ExplainsAsBRepNative()
    {
        foreach (var shape in (Shape[])
                 [
                     Shape.Box(20, 30, 10).OffsetFaces(4, Top),
                     Shape.Box(20, 30, 10).MoveFaces(new Vector3d(0, 0, 4), Top),
                 ])
        {
            var report = shape.Explain(TargetRep.Brep);
            Assert.All(report.Entries, e => Assert.NotEqual(NodeSupport.Impossible, e.Support));
            Assert.Contains(report.Entries, e => e.Node.Contains("Faces"));
        }
    }

    [Fact]
    public void ADirectEditUnderAShear_IsImpossibleByName()
    {
        var sheared = Shape.Box(20, 30, 10).OffsetFaces(4, Top).Scale(2, 1, 1);
        var report = sheared.Explain(TargetRep.Brep);
        Assert.Contains(report.Entries, e =>
            e.Support == NodeSupport.Impossible && e.Detail!.Contains("does not commute with a face edit"));
    }

    [Fact]
    public void ADirectEditWhoseTypedReferenceMatchesNothing_NamesTheINPUT()
    {
        // A FaceSetRef carries cardinality, so it refuses before the compiler's own check
        // and names the PARAMETER — which is the better message and the reason the typed
        // overloads pass the parameter's own name down.
        var shape = Shape.Box(20, 30, 10).OffsetFaces(4, FaceSetRef.Cylindrical());
        var error = Assert.Throws<GeometryInputException>(() => shape.ToBrep());
        Assert.Contains("faces:", error.Message);
        Assert.Contains("cylindrical face", error.Message);
    }

    [Fact]
    public void ADirectEditWhoseRawSelectorMatchesNothing_FailsAtLoweringByName()
    {
        // The lambda overload has no cardinality to declare, so the compiler's own gate is
        // what fires — and it must still say which node.
        var shape = Shape.Box(20, 30, 10).OffsetFaces(4, _ => []);
        var error = Assert.Throws<InvalidOperationException>(() => shape.ToBrep());
        Assert.Contains("matched nothing on the lowered solid", error.Message);
        Assert.Contains("OffsetFaces", error.Message);
    }

    // ---- rotate ----

    [Fact]
    public void RotateFaces_ThroughTheGraph_IsTheExactFrustum()
    {
        // A block's +X face hinged on its base line: the XZ section becomes a trapezoid, so
        // the volume is depth * height * (width + height*tan(theta)/2) — a closed form, not
        // the area-times-distance answer an offset would give.
        const double angle = 6;
        var leaned = Shape.Box(40, 30, 10)
            .RotateFaces(new Ray3d((20, 0, -5), Vector3d.UnitY), angle,
                FaceSetRef.PlanarWithNormal(Vector3d.UnitX));

        double lean = 10 * Math.Tan(angle * Math.PI / 180);
        var bounds = Bounds(leaned);
        Assert.Equal(20 + lean, bounds.Max.X, 9);
        Assert.Equal(-20, bounds.Min.X, 9);

        double expected = 30 * 10 * (40 + lean / 2);
        Assert.Equal(expected, new Part("x", leaned).MassProperties().Volume, 6);
    }

    [Fact]
    public void RotateFaces_UnderARigidPlacement_TurnsByTheSameANGLE()
    {
        // An angle is preserved by every isometry, so the placement moves the hinge and the
        // leaned face together and the volume is UNCHANGED — a claim a wrongly transported
        // axis breaks, since it would hinge about a line the body no longer has.
        const double angle = 6;
        var axis = new Ray3d(new Vector3d(20, 0, -5), Vector3d.UnitY);
        var faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitX);
        var plain = Shape.Box(40, 30, 10).RotateFaces(axis, angle, faces);
        var placed = plain.Translate(15, -7, 3);

        double lean = 10 * Math.Tan(angle * Math.PI / 180);
        double expected = 30 * 10 * (40 + lean / 2);
        Assert.Equal(expected, new Part("a", plain).MassProperties().Volume, 6);
        Assert.Equal(expected, new Part("b", placed).MassProperties().Volume, 6);

        // The hinge travelled with the body: the leaned edge is exactly where the placement
        // put it. Had the axis stayed behind, the face would hinge about a line the body no
        // longer has and the volume above would already be wrong.
        var bounds = Bounds(placed);
        Assert.Equal(15 + 20 + lean, bounds.Max.X, 8);
        Assert.Equal(3 - 5, bounds.Min.Z, 9);
    }

    [Fact]
    public void RotateFaces_IsCLASSIFIEDNativeUnderASimilarity()
    {
        // Deliberately a claim about SUPPORT rather than about geometry: an angle is preserved
        // by every similarity, so no node reports Impossible. Whether a PARTICULAR scaled body
        // lowers is a separate question the kernel answers at the rim solve — a scale can change
        // which surface family a wall lowers as, and two adjacent ExtrudedSurface neighbours then
        // want their generators lengthened in u (filed). The rigid twin above is the geometric
        // assertion; naming this one "IsNative" would have conflated the two.
        var report = Shape.Box(40, 30, 10)
            .RotateFaces(new Ray3d((20, 0, -5), Vector3d.UnitY), 6,
                FaceSetRef.PlanarWithNormal(Vector3d.UnitX))
            .Scale(2)
            .Explain(TargetRep.Brep);
        Assert.DoesNotContain(report.Entries, e => e.Support == NodeSupport.Impossible);
    }

    // ---- replace ----

    [Fact]
    public void ReplaceFaceSurfaces_TurnsACylinderIntoTheExactFrustum()
    {
        const double bottom = 6, top = 3, height = 12;
        var cone = new RevolvedSurface(
            new Line3d((bottom, 0, -height / 2), (top, 0, height / 2)),
            Vector3d.Zero, Vector3d.UnitZ);

        var frustum = Shape.Cylinder(bottom, height)
            .ReplaceFaceSurfaces(cone, FaceSetRef.Cylindrical(bottom));

        // Pappus' own closed form, matched at the tessellation grade.
        double expected = Math.PI * height * (bottom * bottom + bottom * top + top * top) / 3;
        double measured = new Part("frustum", frustum).MassProperties().Volume;
        Assert.Equal(expected, measured, 3);
    }

    [Fact]
    public void ReplaceFaceSurfaces_CarriesItsReplacementThroughThePlacement()
    {
        // The carrier is stated in MODEL coordinates, so a placement moves it with the body.
        // A translated frustum must measure exactly what the un-translated one does.
        const double bottom = 6, top = 3, height = 12;
        var cone = new RevolvedSurface(
            new Line3d((bottom, 0, -height / 2), (top, 0, height / 2)),
            Vector3d.Zero, Vector3d.UnitZ);
        var frustum = Shape.Cylinder(bottom, height)
            .ReplaceFaceSurfaces(cone, FaceSetRef.Cylindrical(bottom));

        double here = new Part("a", frustum).MassProperties().Volume;
        double there = new Part("b", frustum.Translate(40, -25, 7)).MassProperties().Volume;
        Assert.Equal(here, there, 9);

        var bounds = Bounds(frustum.Translate(40, -25, 7));
        Assert.Equal(40 + bottom, bounds.Max.X, 6);
        Assert.Equal(7 + height / 2, bounds.Max.Z, 9);
    }

    [Fact]
    public void MoveFaces_RelocatesABoreCutByABoolean()
    {
        // A difference marks the subtracted tool's walls REVERSED, so this is the sense-aware
        // path as well as the curved one. The oracle is that a move RELOCATES rather than
        // resizes: the volume is bit-identical to the concentric housing's (the bore is still
        // a Phi 6 cylinder through the full height), while the bore's own rims have moved.
        const double outer = 20, bore = 6, height = 30;
        var housing = Shape.Cylinder(outer, height) - Shape.Cylinder(bore, height + 10);
        var offCentre = housing.MoveFaces(new Vector3d(5, 0, 0), FaceSetRef.Cylindrical(bore));

        double before = new Part("a", housing).MassProperties().Volume;
        double after = new Part("b", offCentre).MassProperties().Volume;
        Assert.Equal(0, Math.Abs(after - before) / before, 11);

        // Every bore vertex sits at exactly the bore radius about the MOVED axis. Rebuilt
        // about the old one they would come back at |old centre - new corner|, not 6.
        var solid = offCentre.ToBrep();
        solid.Validate();
        var wall = solid.Faces.Single(
            f => f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - bore) < 1e-9);
        foreach (var vertex in wall.Edges().SelectMany(e => new[] { e.StartVertex, e.EndVertex }).Distinct())
        {
            var radial = vertex.Position - new Vector3d(5, 0, vertex.Position.Z);
            Assert.Equal(bore, radial.Length, 9);
        }
    }

    [Fact]
    public void DeleteFaces_TakesABlendBackOffThroughTheGraph()
    {
        // The docs page's own fixture, and its own claim: deleting the blend does not merely
        // resemble the unfilleted plate, it REPRODUCES it — every corner bit for bit against a
        // Shape.Box that never had the rim, because each is re-solved from the same three
        // planes. Pinned here because the page states it on THIS construction (Shape.Box +
        // Fillet), where the BRep fixture states it on SolidFactory.MakeBox.
        var rounded = Shape.Box(60, 40, 12).Fillet(3, Top);
        var sharp = rounded.DeleteFaces(
            FaceSetRef.Where("blend", face => !face.IsPlanar(out _, out _)));

        var healed = sharp.ToBrep();
        healed.Validate();
        Assert.Equal(6, healed.Faces.Count());

        static List<Vector3d> Corners(BrepSolid solid) => solid.Vertices
            .Select(v => v.Position)
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)
            .ToList();

        var got = Corners(healed);
        var want = Corners(Shape.Box(60, 40, 12).ToBrep());
        Assert.Equal(want.Count, got.Count);
        for (int i = 0; i < want.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(want[i].X), BitConverter.DoubleToInt64Bits(got[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(want[i].Y), BitConverter.DoubleToInt64Bits(got[i].Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(want[i].Z), BitConverter.DoubleToInt64Bits(got[i].Z));
        }
    }
}
