using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Booleans against a rounded rectangle's corner — faces whose CARRIER meets the tool but
/// whose face domain does not. The reported case is a Ø8 counterbore drilled beside a
/// rounded-rect plate's Ø12 corner: geometrically a plain hole with 10 mm of clear material
/// between it and the fillet, which nonetheless threw <c>Open splitting curves must start and
/// end outside the face</c>.
///
/// <para><b>The cause was a promotion, not the prefilter.</b> A rounded rectangle's corner is
/// a QUARTER arc extruded, and <c>SurfaceIntersection.Promote</c> turned it into a whole
/// <c>CylinderSurface</c> because its start point lies on that cylinder — as every point of
/// every arc of that circle does. The fabricated 270° of carrier then genuinely met the tool,
/// the tracer traced it, and the resulting open curves had both endpoints strictly inside a
/// tool face, which is exactly what the face splitter refuses. Tightening the face-bounds
/// prefilter could not have helped: the corner's AABB and the tool's touch at a corner, and
/// the prefilter is deliberately conservative-over.</para>
///
/// <para><b>The blast radius was wider than the report.</b> On the unfixed kernel a THROUGH
/// counterbore anywhere in this plate — including dead centre, 27.8 mm from every corner —
/// failed the same way.</para>
/// </summary>
public class NearMissCarrierTests
{
    private const double CornerRadius = 6;
    private const double PlateWidth = 60, PlateDepth = 40, PlateThickness = 10;

    /// <summary>Rectangle less the four corner bites (a square corner minus its quarter disc).</summary>
    private const double PlateArea =
        PlateWidth * PlateDepth - (4 - Math.PI) * CornerRadius * CornerRadius;

    /// <summary>
    /// Inscribed-polygon slack. Every circle here tessellates as an inscribed n-gon, so the
    /// plate's four corner arcs cut a little too deep and the bore removes a little too
    /// little; measured together they are 5.2e-5 of the plate at the default density, and
    /// 1e-4 relative leaves room for a density change without hiding a boolean defect (the
    /// near-vs-far identity below is the tight assertion).
    /// </summary>
    private const double TessellationSlack = 1e-4 * PlateArea * PlateThickness;

    private static Shape Plate() =>
        Shape.Extrude(Sketch.RoundedRectangle(PlateWidth, PlateDepth, CornerRadius), PlateThickness, SketchPlane.XY);

    private static SketchPlane TopPlane() => SketchPlane.At((0, 0, PlateThickness), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>
    /// The corner arc centres sit at (±24, ±14). This hole centre is 4·√2 = 5.657 from the
    /// (24, 14) centre, so the radius-4 counterbore carrier and the radius-6 corner carrier DO
    /// cross (|6 − 4| &lt; 5.657 &lt; 6 + 4) — at 186° and 264° about the corner centre,
    /// outside the [0°, 90°] quarter the corner face actually covers.
    /// </summary>
    private static readonly Vector2d NearCorner = new(20, 10);

    /// <summary>Validate-clean, right genus, closed tessellation — then the volume.</summary>
    private static double SoundVolume(Shape shape, int genus)
    {
        var solid = shape.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus), $"result must satisfy Euler–Poincaré at genus {genus}");
        var mesh = shape.ToMesh();
        mesh.Validate();
        Assert.True(mesh.IsClosed, "result must tessellate closed");
        return mesh.Volume();
    }

    private static Shape Drilled(in Vector2d at, double depth) =>
        Plate().Drill(HoleSpec.Counterbore(5, 8, 4), [at], depth, TopPlane());

    [Fact]
    public void ThroughCounterboreBesideARoundedCorner_IsAPlainThroughHole()
    {
        // Counterbore Ø8 × 4 deep, then Ø5 through the remaining 6.
        double removed = Math.PI * 4 * 4 * 4 + Math.PI * 2.5 * 2.5 * 6;
        Assert.Equal(
            PlateArea * PlateThickness - removed,
            SoundVolume(Drilled(NearCorner, PlateThickness + 2), genus: 1),
            TessellationSlack);
    }

    [Fact]
    public void BlindCounterboreBesideARoundedCorner_IsUnaffected()
    {
        double removed = Math.PI * 4 * 4 * 4 + Math.PI * 2.5 * 2.5 * 2;
        Assert.Equal(
            PlateArea * PlateThickness - removed,
            SoundVolume(Drilled(NearCorner, 6), genus: 0),
            TessellationSlack);
    }

    /// <summary>
    /// The property the fix is really about, asserted without any discretization in it: the
    /// SAME tool in the SAME plate must remove the same material whether or not a corner
    /// happens to sit nearby. Both models tessellate identically (the bore's n-gon is
    /// phase-aligned to the tool's own frame), so this holds to ~1e-10.
    /// </summary>
    [Theory]
    [InlineData(PlateThickness + 2, 1)]
    [InlineData(6.0, 0)]
    public void ACornerNearbyChangesNothing(double depth, int genus)
    {
        double far = SoundVolume(Drilled(default, depth), genus);
        double near = SoundVolume(Drilled(NearCorner, depth), genus);
        Assert.Equal(far, near, 8);
    }

    /// <summary>
    /// The other side of the same corner: a bore centred on the corner arc's own centre, wide
    /// enough to swallow the fillet and break out through both adjacent straight walls. That
    /// used to be an unclosable boolean; snapping traced curves onto their exact boundary
    /// landing closed it, and the volume is analytic — the plate less the part of the bore
    /// disc inside it (the disc, minus the quadrant annulus outside the fillet, minus the two
    /// segments beyond the walls).
    /// </summary>
    [Fact]
    public void BoreSwallowingTheCornerItself_IsExact()
    {
        var breakout = Plate().Drill(HoleSpec.Simple(14), [new Vector2d(24, 14)], PlateThickness + 2, TopPlane());

        double removedArea = Math.PI * 49
            - Math.PI / 4 * (49 - CornerRadius * CornerRadius)
            - (49 * Math.Acos(CornerRadius / 7) - CornerRadius * Math.Sqrt(49 - CornerRadius * CornerRadius));
        double expected = PlateArea * PlateThickness - removedArea * PlateThickness;

        // Genus 0: the bore removes the corner rather than piercing the plate.
        Assert.Equal(expected, SoundVolume(breakout, genus: 0), TessellationSlack * 10);
    }
}
