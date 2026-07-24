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

    [Fact]
    public void SplitConeBand_TwoTrimmedHalves_SolidStaysClosed()
    {
        // Frustum ring: trapezoid profile revolved a full turn. Splitting the outer cone
        // band along two surface generator lines produces two trimmed RevolvedSurface
        // faces; the ring splits propagate into the neighboring annular faces, which
        // also become trimmed (their rings turn into arcs).
        var frustum = SolidFactory.Revolve(
            Profile.FromPoints([(1, 0, 0), (2, 0, 0), (1.5, 0, 1), (1, 0, 1)]),
            Vector3d.Zero, Vector3d.UnitZ);
        frustum.Validate();
        double referenceVolume = BRepTessellator
            .Tessellate(frustum, segmentsPerCircle: 48, curveSamples: 24)
            .Volume();

        static double Radius(Vector3d p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
        var cone = frustum.Faces.Single(f =>
        {
            var rs = (RevolvedSurface)f.Surface;
            var g = rs.Generator;
            double r0 = Radius(g.PointAt(g.Domain.Start));
            double r1 = Radius(g.PointAt(g.Domain.End));
            return Math.Abs(Math.Min(r0, r1) - 1.5) < 1e-9 && Math.Abs(Math.Max(r0, r1) - 2.0) < 1e-9;
        });
        var surface = (RevolvedSurface)cone.Surface;

        // Surface generator lines at two angles, extended past both rings — the curve
        // only partially lies on the bounded surface, exercising the tolerant pullback.
        Line3d GeneratorLine(double u)
        {
            var p0 = surface.PointAt(u, surface.DomainV.Start);
            var p1 = surface.PointAt(u, surface.DomainV.End);
            var d = p1 - p0;
            return new Line3d(p0 - d, p1 + d);
        }

        IReadOnlyList<BrepFace> parts = [cone];
        foreach (double u in (double[])[0.7, 0.7 + Math.PI])
            parts = parts.SelectMany(f => FaceSplitter.SplitByCurve(f, GeneratorLine(u))).ToList();
        Assert.Equal(2, parts.Count);

        var faces = frustum.Faces.Where(f => !ReferenceEquals(f, cone)).Concat(parts).ToList();
        var solid = new BrepSolid([new BrepShell(faces)]);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 1)); // the ring keeps its bore

        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: 48, curveSamples: 24);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1

        // The split solid resamples its rings as arcs, so its volume differs from the
        // unsplit tessellation only by chordal discretization (~lateral area × sagitta).
        double sagitta = 2 * (1 - Math.Cos(Math.PI / 48));
        Assert.InRange(mesh.Volume(), referenceVolume - 30 * sagitta, referenceVolume + 30 * sagitta);
    }

    [Fact]
    public void Difference_SlotThroughBore_TrimmedBoreWallFragments()
    {
        // The roadmap's known-limitation scenario: drill a box, then subtract a slot
        // narrower than the bore that passes through it. The bore wall (a reversed
        // extruded band) is cut by the slot's side planes (vertical lines) and top and
        // bottom planes (wrap circles) into trimmed fragments.
        const int n = 32;
        const double r = 0.4;
        const double h = 0.15; // slot half-width < bore radius
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var drill = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, r),
            (0, 0, 3));
        var drilled = BrepBoolean.Difference(box, drill);

        var slab = SolidFactory.MakeBox(new Aabb((-2, -h, 0.3), (2, h, 0.7)));
        var result = BrepBoolean.Difference(drilled, slab);

        result.Validate();
        Assert.True(result.SatisfiesEulerFormula(genus: 3)); // bore + two slot arms

        var mesh = BRepTessellator.Tessellate(result, segmentsPerCircle: n);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(-4, mesh.EulerCharacteristic); // genus 3

        // Smooth-solid volume: box − bore − (slot inside box − slot∩bore).
        double boreArea = Math.PI * r * r;
        double chordArea = 2 * (h * Math.Sqrt(r * r - h * h) + r * r * Math.Asin(h / r));
        double expected = 4.0 - boreArea - (2.0 * 2 * h * 0.4 - 0.4 * chordArea);
        double sagitta = r * (1 - Math.Cos(Math.PI / n));
        Assert.InRange(mesh.Volume(), expected - 20 * sagitta, expected + 20 * sagitta);
    }
}
