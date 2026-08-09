using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A printing-clearance thread through the exact B-Rep route, held against the implicit
/// field it is supposed to BE.
/// <para>The clearance is a distance-field offset of the (radius, axial) profile, so the
/// only way to check the eroded solid is against that same field: an offset that mitered
/// the root corners instead of rounding them would still be a plausible thread, would still
/// close, and would still have a sensible volume — it would simply be a different geometry
/// from the one <c>Sdf.Thread</c> and every printed-fit figure describe.</para>
/// </summary>
public class ClearanceThreadGeometryTests
{
    private const double Pitch = 1.0;
    private const double MajorRadius = 3.0;
    private static readonly double MinorRadius = MajorRadius - 0.625 * (Math.Sqrt(3) / 2 * Pitch);
    private const double Length = 6.0;
    private const double CrestWidth = Pitch / 8;
    private const double RootWidth = Pitch / 4;

    private static Vector2d[] Basic() =>
    [
        new(MajorRadius, -Pitch / 16),
        new(MajorRadius, Pitch / 16),
        new(MinorRadius, 3 * Pitch / 8),
        new(MinorRadius, 5 * Pitch / 8),
    ];

    private static BrepSolid Rod(double clearance) => SolidFactory.MakeThreadedRod(
        SolidFactory.OffsetPitchProfile(Basic(), Pitch, -clearance), Pitch, Length);

    /// <summary>
    /// The oracle: every LATERAL tessellation vertex of the eroded rod reads zero against
    /// <c>Sdf.Thread</c>'s own clearance field, and the SAME vertices read the full
    /// clearance against the uncleared one. The control is what makes the first number mean
    /// something — a check that only bounded |sdf| would pass just as happily on a rod that
    /// had not been eroded at all.
    /// </summary>
    [Theory]
    [InlineData(0.02)]
    [InlineData(0.05)]
    [InlineData(0.1)]
    [InlineData(0.15)]
    [InlineData(0.25)]
    public void TheErodedRodIsTheFieldsOwnClearanceSurface(double clearance)
    {
        var mesh = BRepTessellator.Tessellate(Rod(clearance), 64, 24);
        Assert.True(mesh.IsClosed);

        var cleared = Sdf.Thread(
            MajorRadius, MinorRadius, Pitch, CrestWidth, RootWidth, Length, -clearance, 0, 0);
        var plain = Sdf.Thread(
            MajorRadius, MinorRadius, Pitch, CrestWidth, RootWidth, Length, 0, 0, 0);

        double worst = 0, control = 0;
        int sampled = 0;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.GetPosition(i);
            // The cap planes are exact in both, but a vertex ON one is a corner of the
            // field's own max() and says nothing about the lateral surface.
            if (p.Z < 1e-9 || p.Z > Length - 1e-9)
                continue;
            sampled++;
            worst = Math.Max(worst, Math.Abs(cleared.Evaluate(p)));
            control = Math.Max(control, Math.Abs(plain.Evaluate(p)));
        }

        Assert.True(sampled > 1000, $"only {sampled} lateral vertices");
        Assert.True(worst < 1e-12, $"worst |sdf| against the cleared field {worst:e3}");
        // The control: the same points are the better part of a clearance away from the
        // UNcleared surface, so the check above can see an offset that did not happen.
        Assert.True(control > clearance * 0.9, $"control read only {control:e3}");
    }

    /// <summary>
    /// The eroded rod's volume converges on the exact Pappus integral of its own generator
    /// — <c>V = L·(2π/P)·∫½R² ds</c> over one pitch, arcs included — which is what says the
    /// arc bands sweep the solid they are supposed to and not merely a plausible one.
    /// </summary>
    [Fact]
    public void TheErodedRodsVolumeConvergesOnItsPappusIntegral()
    {
        const double clearance = 0.15;
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -clearance);

        // Pappus over the profile: the solid is r <= R(z) swept helically, so per pitch the
        // volume is the integral of pi*R^2 dz, and the whole rod is L/P of them. R(z) is
        // read off the pieces, exactly for a line and by the arc's own closed form.
        double perPitch = 0;
        const int steps = 2_000_000;
        double z0 = pieces[0].Start.Y;
        for (int i = 0; i < steps; i++)
        {
            double z = z0 + Pitch * (i + 0.5) / steps;
            perPitch += Math.PI * RadiusAtAxial(pieces, z) * RadiusAtAxial(pieces, z);
        }
        perPitch *= Pitch / steps;
        double expected = perPitch * Length / Pitch;

        var errors = new List<double>();
        foreach (int segments in (int[])[32, 64, 128, 256])
        {
            var mesh = BRepTessellator.Tessellate(Rod(clearance), segments, 24);
            errors.Add(Math.Abs(mesh.Volume() - expected) / expected);
        }
        // Inscribed, so one-signed, and QUADRATIC: measured 6.007e-3 / 1.512e-3 / 3.783e-4
        // / 9.562e-5 at 32/64/128/256, i.e. ratios 0.2517 / 0.2502 / 0.2528 — each doubling
        // quarters the error, which a fixed floor (a baked polyline, a mis-parameterized
        // cut) could not do.
        string measured = string.Join(", ", errors.Select(e => e.ToString("e3")));
        for (int i = 1; i < errors.Count; i++)
            Assert.True(errors[i] < errors[i - 1] * 0.28, $"errors {measured}");
        Assert.True(errors[^1] < 1.2e-4, $"errors {measured}");
    }

    /// <summary>The profile radius at an axial coordinate, from the pieces themselves.</summary>
    private static double RadiusAtAxial(IReadOnlyList<ThreadProfilePiece> pieces, double axial)
    {
        double z0 = pieces[0].Start.Y;
        double wrapped = z0 + (axial - z0) - Pitch * Math.Floor((axial - z0) / Pitch);
        foreach (var piece in pieces)
        {
            if (wrapped < piece.Start.Y - 1e-12 || wrapped > piece.End.Y + 1e-12)
                continue;
            if (piece.ArcCenter is not { } center)
            {
                double t = (wrapped - piece.Start.Y) / (piece.End.Y - piece.Start.Y);
                return piece.Start.X + (piece.End.X - piece.Start.X) * t;
            }
            // The arc keeps the branch its endpoints are on — its radius is on one side of
            // the centre throughout, which is what "axially monotone" buys.
            double radius = (piece.Start - center).Length;
            double dz = wrapped - center.Y;
            double dr = Math.Sqrt(Math.Max(0, radius * radius - dz * dz));
            return piece.Start.X < center.X ? center.X - dr : center.X + dr;
        }
        throw new InvalidOperationException($"axial {axial} is outside the profile");
    }

    /// <summary>
    /// The two representations are one geometry through the <c>Shape</c> API too, mirrored
    /// placement included — a mirrored thread IS the left-hand one, and the arc generator
    /// carries no handedness of its own.
    /// </summary>
    [Fact]
    public void AMirroredClearanceThreadIsTheLeftHandOne()
    {
        var spec = StandardThreads.Metric(6);
        var thread = Shape.ExternalThread(spec, 6, clearance: 0.15, chamferEnds: false);
        var mirrored = thread.Mirror((0, 0, 0), Vector3d.UnitX);

        var mesh = mirrored.ToMesh(new MeshQuality { SegmentsPerCircle = 48, CurveSamples = 24 });
        Assert.True(mesh.IsClosed);
        var field = mirrored.ToImplicit();
        var wrongHand = thread.ToImplicit();

        double worst = 0, control = 0;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.GetPosition(i);
            if (p.Z < 1e-9 || p.Z > 6 - 1e-9)
                continue;
            worst = Math.Max(worst, Math.Abs(field.Evaluate(p)));
            control = Math.Max(control, Math.Abs(wrongHand.Evaluate(p)));
        }
        Assert.True(worst < 1e-12, $"worst |sdf| against the mirrored field {worst:e3}");
        // The unmirrored field is the opposite handedness, which the same points are a long
        // way from — so the check can see a handedness slip rather than only a shape.
        Assert.True(control > 0.1, $"control read only {control:e3}");
    }
}
