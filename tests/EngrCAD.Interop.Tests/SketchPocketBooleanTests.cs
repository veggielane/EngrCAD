using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Straight-edged sketch extrusions used as boolean tools: rectangular pockets, slots,
/// polygons, through-cuts and glyph-scale multi-pocket engraving. Every one of these
/// silently produced an OPEN mesh before the exact plane∩extrusion section landed (the
/// marching tracer stopped short of the generator's ends, so the pocket outline never
/// closed), and a through-cut on an extruded plate produced a CLOSED, Validate-clean
/// solid that had removed nothing at all. Volumes here are analytic.
/// </summary>
public class SketchPocketBooleanTests
{
    private const double PlateVolume = 60 * 20 * 4;

    /// <summary>Plate spanning z ∈ [−2, 2]; pockets are cut from its top face (z = 2).</summary>
    private static Shape Plate() => Shape.Box(60, 20, 4);

    /// <summary>A sketch plane at height z, axis-aligned.</summary>
    private static SketchPlane At(double z) => SketchPlane.At((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Sketch Rect(double width, double height, double cx = 0, double cy = 0) => Sketch.Polygon(
    [
        new(cx - width / 2, cy - height / 2), new(cx + width / 2, cy - height / 2),
        new(cx + width / 2, cy + height / 2), new(cx - width / 2, cy + height / 2),
    ]);

    private static Sketch Ngon(int sides, double radius) => Sketch.Polygon(
        [.. Enumerable.Range(0, sides).Select(i => new Vector2d(
            radius * Math.Cos(2 * Math.PI * i / sides), radius * Math.Sin(2 * Math.PI * i / sides)))]);

    /// <summary>Validate-clean, correct genus, closed tessellation — then the volume.</summary>
    private static double SoundVolume(Shape shape, int genus = 0)
    {
        var solid = shape.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus),
            $"result must satisfy Euler–Poincaré at genus {genus}");

        var mesh = shape.ToMesh();
        mesh.Validate();
        Assert.True(mesh.IsClosed, "result must tessellate closed");
        return mesh.Volume();
    }

    [Fact]
    public void RectangularPocket_ExactVolume()
    {
        // The reported repro. Pocket 10 × 5, floor at z = 1, so exactly 1 mm deep.
        var pocket = Plate() - Shape.Extrude(Rect(10, 5), 1.5, At(1));
        Assert.Equal(PlateVolume - 10 * 5 * 1, SoundVolume(pocket), 9);
    }

    [Fact]
    public void RectangularPocket_KeepsEveryWallAndTheFloor()
    {
        // 6 plate faces (the top one now carrying the pocket outline as a hole)
        // + 4 pocket walls + 1 pocket floor.
        var solid = (Plate() - Shape.Extrude(Rect(10, 5), 1.5, At(1))).ToBrep();
        Assert.Equal(11, solid.Faces.Count());
        Assert.Equal(2, solid.Faces.Single(f => f.Loops.Count == 2).Loops.Count); // the holed top
    }

    [Fact]
    public void PolygonPocket_ExactVolume()
    {
        var sketch = Ngon(6, 4);
        var pocket = Plate() - Shape.Extrude(sketch, 1.5, At(1));
        Assert.Equal(PlateVolume - sketch.Area() * 1, SoundVolume(pocket), 9);
    }

    [Fact]
    public void SlotPocket_VolumeWithinChordalError()
    {
        // Straight walls AND rational arcs in one profile: the section path translates
        // the generator whatever its type, so the arc walls stay exact arcs.
        var slot = Sketch.Slot(10, 4);
        var pocket = Plate() - Shape.Extrude(slot, 1.5, At(1));
        double volume = SoundVolume(pocket);

        // The only error is the tessellator chording the two semicircular ends. A
        // regular n-gon inscribed in radius r under-removes r²(π − (n/2)sin(2π/n)) per
        // full circle; at the default 32 segments per circle that is ~0.04 mm³ here.
        double exact = PlateVolume - slot.Area() * 1;
        double chordalDeficit = 4 * (Math.PI - 16 * Math.Sin(2 * Math.PI / 32)) * 1;
        Assert.InRange(volume, exact, exact + 2 * chordalDeficit);
    }

    [Fact]
    public void ThroughCut_IsGenusOneAndExact()
    {
        // The tool spans z ∈ [−3, 5] — clear of both plate faces, so both cuts are
        // transversal and the result is a rectangular tunnel.
        var cut = Plate() - Shape.Extrude(Rect(10, 5), 8, At(-3));
        Assert.Equal(PlateVolume - 10 * 5 * 4, SoundVolume(cut, genus: 1), 9);
    }

    [Fact]
    public void ThroughCut_OnAnExtrudedPlate_ActuallyRemovesMaterial()
    {
        // Regression with teeth: with an extruded (not boxed) plate BOTH operands have
        // extruded-line walls. This case used to return a CLOSED, Validate-clean solid
        // of the full plate volume — the cut silently did nothing at all.
        var plate = Shape.Extrude(Rect(60, 20), 4, At(-2));
        var cut = plate - Shape.Extrude(Rect(10, 5), 8, At(-3));
        Assert.Equal(PlateVolume - 10 * 5 * 4, SoundVolume(cut, genus: 1), 9);
    }

    [Fact]
    public void PocketInAnExtrudedPlate_ExactVolume()
    {
        var plate = Shape.Extrude(Rect(60, 20), 4, At(-2));
        var pocket = plate - Shape.Extrude(Rect(10, 5), 1.5, At(1));
        Assert.Equal(PlateVolume - 10 * 5 * 1, SoundVolume(pocket), 9);
    }

    [Fact]
    public void PocketOnARotatedSketchPlane_ExactVolume()
    {
        // Non-axis-aligned frame: the generator points come through the plane matrix, so
        // the corner handover must still be exact.
        var rotated = SketchPlane.At((0, 0, 1),
            new Vector3d(1, 1, 0).Normalized(), new Vector3d(-1, 1, 0).Normalized());
        var pocket = Plate() - Shape.Extrude(Rect(10, 5), 1.5, rotated);
        Assert.Equal(PlateVolume - 10 * 5 * 1, SoundVolume(pocket), 9);
    }

    [Fact]
    public void AnnularPocket_HoleFollowsTheCorrectSide()
    {
        // A profile WITH a hole: the tool is a rectangular ring, so the pocket leaves an
        // island standing in its middle.
        var ring = Rect(12, 8).WithHole(Rect(6, 4));
        var pocket = Plate() - Shape.Extrude(ring, 1.5, At(1));
        Assert.Equal(PlateVolume - (12 * 8 - 6 * 4) * 1, SoundVolume(pocket), 9);
    }

    [Fact]
    public void FivePockets_ChainedAndAsOneBoolean_AgreeExactly()
    {
        Shape chained = Plate();
        for (int i = -2; i <= 2; i++)
            chained -= Shape.Extrude(Rect(4, 4, i * 8), 1.5, At(1));

        Shape tools = Shape.Extrude(Rect(4, 4, -16), 1.5, At(1));
        for (int i = -1; i <= 2; i++)
            tools |= Shape.Extrude(Rect(4, 4, i * 8), 1.5, At(1));

        double expected = PlateVolume - 5 * 4 * 4 * 1;
        Assert.Equal(expected, SoundVolume(chained), 9);
        Assert.Equal(expected, SoundVolume(Plate() - tools), 9);
    }

    [Fact]
    public void GlyphScale_ManySmallPocketsInOneBoolean_Exact()
    {
        // The trap the naive promote-to-plane fix fell into: 25 small straight-edged
        // pockets whose carrier planes all cross each other. Each wall's cut must be
        // clipped to its OWN extent or it slices through neighbouring pockets.
        Shape tools = Shape.Extrude(Rect(0.6, 3, -10), 1.2, At(1));
        int strokes = 1;
        for (int i = 0; i < 24; i++, strokes++)
            tools |= Shape.Extrude(Rect(0.6, 3, -10 + (i + 1) * 0.8, (i % 3 - 1) * 2.0), 1.2, At(1));

        Assert.Equal(PlateVolume - strokes * 0.6 * 3 * 1, SoundVolume(Plate() - tools), 9);
    }

    [Fact]
    public void SubMillimetreStrokes_OnASmallPlate_Exact()
    {
        // Real engraving scale: 0.25 mm strokes, 0.4 mm deep, on a 12 × 6 × 1.5 plate.
        var plate = Shape.Box(12, 6, 1.5);
        var surface = SketchPlane.At((0, 0, 0.35), Vector3d.UnitX, Vector3d.UnitY);
        Shape tools = Shape.Extrude(Rect(0.25, 1.2, -5), 1.0, surface);
        int strokes = 1;
        for (int i = 0; i < 30; i++, strokes++)
            tools |= Shape.Extrude(Rect(0.25, 1.2, -5 + (i + 1) * 0.35, (i % 4 - 1.5) * 0.9), 1.0, surface);

        Assert.Equal(12 * 6 * 1.5 - strokes * 0.25 * 1.2 * 0.4, SoundVolume(plate - tools), 9);
    }

    [Fact]
    public void CrossedExtrusions_UnionIntersectionDifference_AllExact()
    {
        // Both operands straight-walled extrusions, transversal in z.
        Shape TallBlock() => Shape.Extrude(Rect(10, 10), 4, At(-2));   // 10 × 10 × 4
        Shape Bar() => Shape.Extrude(Rect(4, 20), 2, At(-1));          // 4 × 20 × 2, right through

        Assert.Equal(100 * 4 + 80 * 2 - 40 * 2, SoundVolume(TallBlock() | Bar()), 9);
        Assert.Equal(40 * 2, SoundVolume(TallBlock() & Bar()), 9);
        Assert.Equal(100 * 4 - 40 * 2, SoundVolume(TallBlock() - Bar(), genus: 1), 9);
    }

    [Fact]
    public void PocketThenBore_CompoundFeaturesStayExact()
    {
        // A drilled bore through a pocket floor: the pocket's exact walls must survive a
        // second boolean whose curves are circles.
        var pocket = Plate() - Shape.Extrude(Rect(10, 5), 1.5, At(1));
        var drilled = pocket - Shape.Cylinder(1.5, 12).Translate(0, 0, 0);
        double bore = Math.PI * 1.5 * 1.5 * 3; // pocket floor z = 1 down to the plate's z = −2
        Assert.Equal(PlateVolume - 50 - bore, SoundVolume(drilled, genus: 1), 2);
    }
}
