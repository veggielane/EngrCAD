using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Trimmed-face tessellation: faces on curved surfaces whose loops do not cover the
/// surface's natural grid domain (hand-built partial bands and
/// <see cref="FaceSplitter.SplitByCurve"/> fragments) must tessellate closed and welded,
/// with volumes bracketed by the inscribed chordal approximation below and the smooth
/// solid above (all mesh vertices lie exactly on the surfaces).
/// </summary>
public class TrimmedFaceTests
{
    /// <summary>Area of the polygon inscribed in a half-disc: k arc chords plus the diameter.</summary>
    private static double InscribedHalfDiscArea(double radius, int arcSegments) =>
        0.5 * radius * radius * arcSegments * Math.Sin(Math.PI / arcSegments);

    /// <summary>
    /// A hand-built half cylinder (radius 1, height 2, u ∈ [0, π]): the band face's loop
    /// is two arcs and two vertical lines — a trimmed cylinder face, not a two-ring band.
    /// </summary>
    private static BrepSolid HalfCylinder(double radius, double height)
    {
        var x = Vector3d.UnitX;
        var y = Vector3d.UnitY;
        var arcDomain = new Interval(0, Math.PI);

        var a0 = new BrepVertex((radius, 0, 0));
        var a1 = new BrepVertex((-radius, 0, 0));
        var b0 = new BrepVertex((radius, 0, height));
        var b1 = new BrepVertex((-radius, 0, height));

        var bottomArc = new BrepEdge(new Circle3d((0, 0, 0), x, y, radius), arcDomain, a0, a1);
        var topArc = new BrepEdge(new Circle3d((0, 0, height), x, y, radius), arcDomain, b0, b1);
        var line0 = new BrepEdge(new Line3d((radius, 0, 0), (radius, 0, height)), Interval.Unit, a0, b0);
        var line1 = new BrepEdge(new Line3d((-radius, 0, 0), (-radius, 0, height)), Interval.Unit, a1, b1);
        var bottomChord = new BrepEdge(new Line3d((radius, 0, 0), (-radius, 0, 0)), Interval.Unit, a0, a1);
        var topChord = new BrepEdge(new Line3d((radius, 0, height), (-radius, 0, height)), Interval.Unit, b0, b1);

        // Band: outward radial normal; CCW loop in (u, v): bottom arc +u, up, top arc −u, down.
        var band = new BrepFace(
            new CylinderSurface((0, 0, 0), x, y, radius),
            [new BrepLoop([
                new BrepCoedge(bottomArc, true),
                new BrepCoedge(line1, true),
                new BrepCoedge(topArc, false),
                new BrepCoedge(line0, false)])]);

        // Flat wall at y = 0, outward normal −Y (x-dir × y-dir = X × Z = −Y).
        var wall = new BrepFace(
            new PlaneSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitZ),
            [new BrepLoop([
                new BrepCoedge(bottomChord, false),
                new BrepCoedge(line0, true),
                new BrepCoedge(topChord, true),
                new BrepCoedge(line1, false)])]);

        // Bottom cap, outward normal −Z (X × (−Y) = −Z).
        var bottomCap = new BrepFace(
            new PlaneSurface((0, 0, 0), Vector3d.UnitX, -Vector3d.UnitY),
            [new BrepLoop([
                new BrepCoedge(bottomArc, false),
                new BrepCoedge(bottomChord, true)])]);

        // Top cap, outward normal +Z.
        var topCap = new BrepFace(
            new PlaneSurface((0, 0, height), Vector3d.UnitX, Vector3d.UnitY),
            [new BrepLoop([
                new BrepCoedge(topArc, true),
                new BrepCoedge(topChord, false)])]);

        return new BrepSolid([new BrepShell([band, wall, bottomCap, topCap])]);
    }

    [Fact]
    public void HalfCylinder_TrimmedBand_TessellatesClosedWithBracketedVolume()
    {
        const double radius = 1.0;
        const double height = 2.0;
        const int curveSamples = 24;
        var solid = HalfCylinder(radius, height);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: 32, curveSamples: curveSamples);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // All wall vertices lie on the cylinder, so the volume sits between the prism
        // over the arc polygon at the coarsest angular step present (boundary arcs use
        // curveSamples, interior refinement targets 2π/segmentsPerCircle) and the
        // smooth half cylinder.
        double inscribed = InscribedHalfDiscArea(radius, Math.Min(curveSamples, 32 / 2)) * height;
        double smooth = Math.PI * radius * radius / 2 * height;
        double volume = mesh.Volume();
        Assert.InRange(volume, inscribed - 1e-9, smooth + 1e-9);
    }

    [Fact]
    public void HalfCylinder_ScaledAndTransformed_StillWelds()
    {
        // A second configuration (different radius/height) guards against accidental
        // unit-scale assumptions in the jitter/refinement logic.
        var solid = HalfCylinder(0.35, 5.0);
        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: 48, curveSamples: 16);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        double inscribed = InscribedHalfDiscArea(0.35, Math.Min(16, 48 / 2)) * 5.0;
        double smooth = Math.PI * 0.35 * 0.35 / 2 * 5.0;
        Assert.InRange(mesh.Volume(), inscribed - 1e-9, smooth + 1e-9);
    }
}
