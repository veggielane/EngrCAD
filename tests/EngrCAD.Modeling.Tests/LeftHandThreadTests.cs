using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Left-hand threads. The profile and every derived diameter are identical to the
/// right-hand thread's — only the helix's sense changes — so a left-hand thread is the
/// exact mirror image of its right-hand twin, and is Native everywhere the right-hand one
/// is. That equivalence runs both ways: <c>Mirror(right-hand thread)</c> lowers as the
/// left-hand thread rather than being refused.
/// </summary>
public class LeftHandThreadTests
{
    private static ThreadSpec Right() => StandardThreads.Metric(8);
    private static ThreadSpec Left() => Right().LeftHanded();

    [Fact]
    public void TheSpecCarriesHandednessAndNothingElse()
    {
        var right = Right();
        var left = Left();
        Assert.False(right.LeftHand);
        Assert.True(left.LeftHand);
        // Every dimension is shared — handedness is not a different thread.
        Assert.Equal(right.MajorDiameter, left.MajorDiameter, 12);
        Assert.Equal(right.Pitch, left.Pitch, 12);
        Assert.Equal(right.MinorDiameter, left.MinorDiameter, 12);
        Assert.Equal(right.TapDrillDiameter, left.TapDrillDiameter, 12);

        Assert.Equal("M8×1.25", right.Designation);
        Assert.Equal("M8×1.25-LH", left.Designation);
        Assert.Equal("M8×1.25-LH ↧12", ThreadCallout.Text(left, 12));
        // Round trip, and asking for the handedness you already have is a no-op.
        Assert.Same(left, left.WithHandedness(true));
        Assert.False(left.WithHandedness(false).LeftHand);
    }

    [Fact]
    public void TheImplicitFieldIsTheExactMirrorOfTheRightHandOne()
    {
        // Bit-exact, not merely close: the only difference in ThreadSdf is the sign of
        // the pitch term in the helical phase, so reflecting y reproduces it term for
        // term. A tolerance here would hide a real drift.
        var right = Shape.ExternalThread(Right(), 8, chamferEnds: false).ToImplicit();
        var left = Shape.ExternalThread(Left(), 8, chamferEnds: false).ToImplicit();

        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var p = new Vector3d(
                random.NextDouble() * 12 - 6, random.NextDouble() * 12 - 6, random.NextDouble() * 8);
            Assert.Equal(right.Evaluate(p), left.Evaluate(new Vector3d(p.X, -p.Y, p.Z)));
        }
    }

    [Fact]
    public void ALeftHandStudIsBrepNativeAndClosed()
    {
        var stud = Shape.ExternalThread(Left(), 8, chamferEnds: false);
        Assert.Contains("Native", stud.Explain(TargetRep.Brep).ToString());

        var solid = stud.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.True(BRepTessellator.Tessellate(solid, 96, 24).IsClosed);
    }

    [Fact]
    public void HandednessDoesNotChangeTheVolume()
    {
        // A reflection is an isometry, so the two rods enclose the same volume; both
        // tessellations converge onto the Pappus value from below. They do NOT agree
        // exactly at a given density — the band's grid is sheared, and the shear runs
        // the other way — so this compares each against the analytic truth rather than
        // against the other.
        var right = Shape.ExternalThread(Right(), 8, chamferEnds: false).ToBrep();
        var left = Shape.ExternalThread(Left(), 8, chamferEnds: false).ToBrep();

        var spec = Right();
        double pitch = spec.Pitch, rMajor = spec.MajorDiameter / 2, rMinor = spec.MinorDiameter / 2;
        Vector2d[] profile =
        [
            new(rMajor, -pitch / 16), new(rMajor, pitch / 16),
            new(rMinor, 3 * pitch / 8), new(rMinor, 5 * pitch / 8),
        ];
        double perPitch = 0;
        for (int k = 0; k < profile.Length; k++)
        {
            var a = profile[k];
            var b = k + 1 < profile.Length
                ? profile[k + 1]
                : new Vector2d(profile[0].X, profile[0].Y + pitch);
            perPitch += 0.5 * (b.Y - a.Y) * (a.X * a.X + a.X * b.X + b.X * b.X) / 3;
        }
        double analytic = 8 / pitch * 2 * Math.PI * perPitch;

        foreach (var solid in (ReadOnlySpan<EngrCAD.BRep.BrepSolid>)[right, left])
        {
            double coarse = BRepTessellator.Tessellate(solid, 48, 24).Volume();
            double fine = BRepTessellator.Tessellate(solid, 96, 24).Volume();
            Assert.True(coarse < analytic && fine < analytic, "inscribed bands under-fill");
            // Quadratic: halving the step should quarter the deficit (allow 3x-5x).
            double ratio = (analytic - coarse) / (analytic - fine);
            Assert.InRange(ratio, 3.0, 5.0);
        }
    }

    [Fact]
    public void MirroringARightHandThreadLowersAsTheLeftHandOne()
    {
        // The identity that removes the old "a mirrored thread is left-handed" refusal:
        // a reflection across a plane containing the axis IS the handedness flip.
        var mirrored = Shape.ExternalThread(Right(), 8, chamferEnds: false).Mirror(Vector3d.UnitX);
        Assert.Contains("Native", mirrored.Explain(TargetRep.Brep).ToString());

        var solid = mirrored.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        // And it really is the mirrored geometry, not merely a valid solid of the right
        // shape. Cross-representation: every vertex the B-Rep tessellation produces is an
        // exact surface point, so the mirrored implicit field must read zero there. It
        // does, to 2.4e-15 over 59 136 vertices — while the UNmirrored right-hand field
        // reads up to 0.47 at the same points, which is what a phase or handedness slip
        // would look like.
        var field = mirrored.ToImplicit();
        var wrongHand = Shape.ExternalThread(Right(), 8, chamferEnds: false).ToImplicit();
        var mesh = BRepTessellator.Tessellate(solid, 96, 24);
        double worst = 0, wrongWorst = 0;
        foreach (var vertex in mesh.Vertices)
        {
            worst = Math.Max(worst, Math.Abs(field.Evaluate(vertex.Position)));
            wrongWorst = Math.Max(wrongWorst, Math.Abs(wrongHand.Evaluate(vertex.Position)));
        }
        Assert.True(worst < 1e-12, $"mirrored B-Rep is off its own field by {worst:E3}");
        Assert.True(wrongWorst > 0.1, "the check must be able to see a handedness slip");
    }

    [Fact]
    public void MirroringTwiceReturnsTheRightHandThread()
    {
        var twice = Shape.ExternalThread(Right(), 8, chamferEnds: false)
            .Mirror(Vector3d.UnitX).Mirror(Vector3d.UnitX);
        twice.ToBrep().Validate();
        Assert.Contains("Native", twice.Explain(TargetRep.Brep).ToString());
    }

    [Fact]
    public void ALeftHandThreadedHoleIsBrepNative()
    {
        var top = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
        var block = Shape.Box(30, 20, 12).ThreadedHole(Left(), [new(0, 0)], depth: 8, top);

        Assert.Contains("Native", block.Explain(TargetRep.Brep).ToString());
        var solid = block.ToBrep();
        solid.Validate();
        Assert.True(BRepTessellator.Tessellate(solid, 64, 24).IsClosed);
    }

    [Fact]
    public void AShearedPlacementIsStillRefused()
    {
        // The refusal that remains, and it is a different one: a non-uniform scale
        // cannot re-place a helix at all, mirrored or not.
        var sheared = Shape.ExternalThread(Right(), 8, chamferEnds: false)
            .Transform(Matrix4d.CreateScale(new Vector3d(1, 2, 1)));
        Assert.Contains("Impossible", sheared.Explain(TargetRep.Brep).ToString());
        Assert.DoesNotContain("left-handed", sheared.Explain(TargetRep.Brep).ToString());
    }
}
