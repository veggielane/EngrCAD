using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// Double-helical (herringbone) gears: two helical halves of OPPOSITE hand in one solid,
/// meeting at the mid-plane. The two halves' axial thrusts are equal and opposite, so
/// they cancel in the bearings — which is the whole reason the form exists, and which is
/// why the geometric statement behind it (equal-and-opposite helix angles, a solid
/// invariant under reflection in its own mid-plane) is what the tests assert.
/// </summary>
/// <remarks>
/// <para><b>The apex is a weld, not a boolean.</b> Both halves share the same transverse
/// section at the mid-plane — the twist law is Λ-shaped in z and reaches its extreme
/// there — so the mid-plane is a plane of EXACT mirror symmetry and the upper half is the
/// lower half reflected. The solid is therefore built by reflecting the lower half's mesh
/// and welding the two along that shared section BY INDEX: the apex ring's vertices are
/// fixed points of the reflection (z → 2·z_apex − z is exactly the identity at z_apex),
/// the two coincident cap facets are dropped, and the walls of both halves meet on one
/// ring of vertices with no tolerance anywhere. A union of two separately built halves
/// would hand a large coincident planar region to a boolean for an answer the symmetry
/// already gives.</para>
/// <para><b>The apex relief groove is NOT in v1, and the reason is a measurement.</b> A
/// hobbed double-helical gear cannot have a continuous apex — the hob has to run out — so
/// real ones carry a relief groove in the middle, and a groove is material genuinely
/// REMOVED rather than material never placed, which makes it a boolean rather than
/// another weld. That boolean does not survive gear geometry in either engine: subtracting
/// an axial band (an annular prism straddling the apex, or even a plain disc band) from a
/// herringbone fails in the exact mesh boolean's imprint — "flip recovery of the
/// intersection segment did not converge" — at every relief diameter, gap width and mesh
/// density tried, and the same band against an ordinary SPUR gear fails the B-Rep boolean
/// as an unclosed solid with 1522 unpaired edges. So it is not this form's weld that
/// cannot take it. What the groove wants instead is a mixed-section ring stack (a helical
/// toothed run, an annular transition face, a plain relief band, then the mirror), which
/// is a construction rather than a parameter; it is filed with these figures rather than
/// shipped as a call that throws from three stages away.</para>
/// <para><b>Representation support follows the twisted extrusion</b>: mesh only, since a
/// twisted extrude is B-Rep-Impossible and the implicit route is a mesh SDF of it. The
/// mesh is therefore built EAGERLY at <c>quality</c> (the <c>Shape.Bounds</c>/<c>Resized</c>
/// policy) and wrapped by <see cref="Shape.From(HalfEdgeMesh)"/>; ask for a finer quality
/// at the call rather than at the scene.</para>
/// </remarks>
public static class HerringboneGears
{
    /// <summary>
    /// A herringbone (double-helical) gear solid: two helical halves of opposite hand
    /// welded at the mid-plane.
    /// </summary>
    /// <param name="spec">The TRANSVERSE gear definition (module and pressure angle are
    /// transverse values; <see cref="HelicalGearGeometry.FromNormal"/> converts from the
    /// normal terms a cutter is ordered in).</param>
    /// <param name="faceWidth">Total face width across BOTH halves (&gt; 0): each half is
    /// <c>faceWidth/2</c> tall, and the mid-plane sits at <c>faceWidth/2</c>.</param>
    /// <param name="helixAngleDegrees">Helix angle magnitude at the pitch cylinder. The
    /// LOWER half takes this sign and the upper half its negation, so a positive value
    /// puts a right-hand helix below the apex and a left-hand helix above it. Zero is
    /// refused by name — that is a spur gear.</param>
    /// <param name="boreDiameter">Optional plain bore (0 = none); must clear the root circle.</param>
    /// <param name="fitTolerance">Flank fit tolerance, forwarded to <see cref="Gears.Spur"/>.</param>
    /// <param name="slicesPerHalf">Section rings per half for the mesh sweep; null sizes
    /// them from the twist and the quality's segments-per-circle.</param>
    /// <param name="quality">Mesh quality; the solid is meshed eagerly at it.</param>
    public static Shape Herringbone(
        GearSpec spec,
        double faceWidth,
        double helixAngleDegrees,
        double boreDiameter = 0,
        double? fitTolerance = null,
        int? slicesPerHalf = null,
        MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(faceWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(faceWidth), "Face width must be positive.");
        HelicalGearGeometry.RequireAngle(helixAngleDegrees);
        if (helixAngleDegrees == 0)
            throw new ArgumentOutOfRangeException(nameof(helixAngleDegrees),
                "A herringbone with a zero helix angle is a spur gear - use Gears.SpurGear.");
        if (slicesPerHalf is < 1)
            throw new ArgumentOutOfRangeException(nameof(slicesPerHalf));

        var profile = Gears.Spur(spec, fitTolerance);
        var sketch = profile.Sketch;
        if (boreDiameter > 0)
        {
            if (boreDiameter >= spec.RootDiameter)
                throw new ArgumentOutOfRangeException(nameof(boreDiameter),
                    $"Bore diameter {boreDiameter:0.###} reaches the root circle "
                    + $"(diameter {spec.RootDiameter:0.###}).");
            sketch = sketch.WithHole(Sketch.Circle(boreDiameter / 2));
        }

        double apexZ = faceWidth / 2;
        double twist = HelicalGearGeometry.Twist(spec.PitchDiameter / 2, apexZ, helixAngleDegrees);

        // The LOWER half, on the world XY plane, running from z = 0 (angle 0) to the apex
        // ring at z = apexZ (angle `twist`). Built through the ordinary twisted-extrusion
        // sweep, so the recorded twist-matched profile subdivision - without which a
        // twisted sweep converges only FIRST order in slices - is inherited rather than
        // restated.
        var lower = new TwistExtrudeShape(
            sketch, SketchPlane.XY, apexZ, twist, new Vector2d(1, 1), slicesPerHalf);
        return Shape.From(
            MirrorWeld(TwistedExtrusion.Build(lower, quality ?? MeshQuality.Default), apexZ));
    }

    /// <summary>
    /// The twist each half carries, radians — the transverse section's rotation from the
    /// end face to the apex. The two halves take <c>+</c> and <c>−</c> this value, which
    /// is the geometric statement behind thrust cancellation.
    /// </summary>
    public static double HalfTwist(GearSpec spec, double faceWidth, double helixAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return HelicalGearGeometry.Twist(spec.PitchDiameter / 2, faceWidth / 2, helixAngleDegrees);
    }

    /// <summary>
    /// The transverse section's rotation at height <paramref name="z"/> above the bottom
    /// face, radians: the Λ-shaped law <c>twist·(1 − |2z/W − 1|)</c>, which is a function
    /// of |z − W/2| and so is invariant under reflection in the mid-plane BY FORM, not by
    /// arithmetic that happens to land there.
    /// </summary>
    public static double SectionAngleAt(GearSpec spec, double faceWidth, double helixAngleDegrees, double z)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(faceWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(faceWidth));
        double half = faceWidth / 2;
        double twist = HelicalGearGeometry.Twist(spec.PitchDiameter / 2, half, helixAngleDegrees);
        return twist * (1 - Math.Abs(z - half) / half);
    }

    // ------------------------------------------------------------------ construction

    /// <summary>
    /// Reflects <paramref name="half"/> in the plane z = <paramref name="apexZ"/> and
    /// welds the two copies along it.
    /// </summary>
    /// <remarks>
    /// Three exact facts carry the weld and none of them is a tolerance. (a) The apex
    /// ring's vertices sit at exactly <paramref name="apexZ"/>: the sweep places ring r at
    /// <c>height·r/slices</c> and the last ring's fraction is exactly 1. (b) The
    /// reflection <c>z → 2a − z</c> fixes them BIT for bit (<c>2a</c> is exact, and
    /// <c>2a − a = a</c>). (c) The only facets lying entirely in that plane are the two
    /// caps, since every wall facet spans two rings — so "every vertex is at the apex" is
    /// an exact test for the facets to drop. The reflected faces keep vertex 0 in place
    /// (<c>[a, d, c, b]</c>, not <c>[d, c, b, a]</c>): reversing a polygon is free for
    /// ORIENTATION and not for GEOMETRY, because everything downstream fans from vertex 0
    /// and rotating the reversal is what keeps the cap's diagonals where the triangulator
    /// put them.
    /// </remarks>
    internal static HalfEdgeMesh MirrorWeld(HalfEdgeMesh half, double apexZ)
    {
        var (positions, faces) = half.ToIndexed();
        var vertices = new List<Vector3d>(positions.Length * 2);
        vertices.AddRange(positions);
        var result = new List<IReadOnlyList<int>>(faces.Count * 2);

        bool OnApex(int index) => positions[index].Z == apexZ;   // exact, per (a)/(b) above
        bool IsSharedCap(int[] face)
        {
            foreach (int v in face)
            {
                if (!OnApex(v))
                    return false;
            }
            return true;
        }

        foreach (var face in faces)
        {
            if (!IsSharedCap(face))
                result.Add(face);
        }

        var map = new int[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            if (OnApex(i))
            {
                map[i] = i;                                      // a fixed point: shared, not copied
                continue;
            }
            var p = positions[i];
            map[i] = vertices.Count;
            vertices.Add(new Vector3d(p.X, p.Y, 2 * apexZ - p.Z));
        }

        foreach (var face in faces)
        {
            if (IsSharedCap(face))
                continue;
            var mirrored = new int[face.Length];
            mirrored[0] = map[face[0]];
            for (int i = 1; i < face.Length; i++)
                mirrored[i] = map[face[face.Length - i]];
            result.Add(mirrored);
        }

        if (result.Count == faces.Count * 2)
            throw new InvalidOperationException(
                "The herringbone weld found no apex cap to drop - this is a bug, not a modelling error.");
        return HalfEdgeMesh.Build(vertices, result);
    }

}
