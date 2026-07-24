using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class ThreadShapeTests
{
    private static readonly ThreadSpec M8 = StandardThreads.Metric(8);

    [Fact]
    public void ExternalThread_ExplainReportsTruthfully()
    {
        // Default (chamfered) studs still have no B-Rep form — the 45° chamfer cones
        // cutting the helical bands are future surface-intersection work — and the
        // report says so instead of silently dropping the chamfers.
        var chamfered = Shape.ExternalThread(M8, 12);
        var brep = chamfered.Explain(TargetRep.Brep);
        Assert.False(brep.IsConvertible);
        Assert.Contains(brep.Entries, e => e.Detail?.Contains("chamfer") == true);
        Assert.Throws<ShapeConversionException>(() => chamfered.ToBrep());

        // The unmodified basic profile is B-Rep-Native (boolean-free helical sweep).
        var plain = Shape.ExternalThread(M8, 12, chamferEnds: false);
        Assert.True(plain.Explain(TargetRep.Brep).IsConvertible);
        Assert.All(plain.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        // Printing clearance reshapes the profile as a distance field — honest Impossible.
        var cleared = Shape.ExternalThread(M8, 12, clearance: 0.2, chamferEnds: false);
        Assert.False(cleared.Explain(TargetRep.Brep).IsConvertible);
        Assert.Contains(cleared.Explain(TargetRep.Brep).Entries, e => e.Detail?.Contains("clearance") == true);

        // Implicit is native for all variants.
        var implicitReport = chamfered.Explain(TargetRep.Implicit);
        Assert.True(implicitReport.IsConvertible);
        Assert.All(implicitReport.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        // Chamfered/cleared threads mesh through Surface Nets — the printing route.
        var mesh = chamfered.Explain(TargetRep.Mesh);
        Assert.True(mesh.IsConvertible);
        Assert.Contains(mesh.Entries, e => e.Support == NodeSupport.Bridged);
    }

    [Fact]
    public void ExternalThread_UnchamferedLowersToExactBrep()
    {
        double length = 10;
        var brep = Shape.ExternalThread(M8, length, chamferEnds: false).ToBrep();
        brep.Validate();
        Assert.True(brep.SatisfiesEulerFormula(genus: 0));

        // Exact volume: V = L·(2π/P)·∫₀^P ½R²(s) ds over the ISO basic profile
        // (crest flat P/8 at the major radius, root flat P/4 at the minor, 5P/16
        // flanks); the tessellation inscribes chordally, ~0.6% low at 32 segments.
        double p = M8.Pitch, rMaj = M8.MajorDiameter / 2, rMin = M8.MinorDiameter / 2;
        double perPitch = 0.5 * rMaj * rMaj * (p / 8) + 0.5 * rMin * rMin * (p / 4)
            + 2 * (5 * p / 16) * (rMaj * rMaj + rMaj * rMin + rMin * rMin) / 6;
        double expected = length * (2 * Math.PI / p) * perPitch;

        var mesh = EngrCAD.Interop.BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.01,
            $"volume {mesh.Volume():g6} vs analytic {expected:g6}");

        // Rigid + uniform-scale placements bake into the construction frame exactly.
        var moved = Shape.ExternalThread(M8, length, chamferEnds: false)
            .RotateY(0.7).Translate(3, -2, 1).Scale(2);
        var movedBrep = moved.ToBrep();
        movedBrep.Validate();
        var movedMesh = EngrCAD.Interop.BRepTessellator.Tessellate(movedBrep);
        Assert.True(movedMesh.IsClosed);
        Assert.True(Math.Abs(movedMesh.Volume() - 8 * expected) / (8 * expected) < 0.01,
            $"scaled volume {movedMesh.Volume():g6} vs analytic {8 * expected:g6}");

        // A mirrored placement would be a left-hand thread: honest Impossible.
        var mirrored = Shape.ExternalThread(M8, length, chamferEnds: false)
            .Mirror(Vector3d.Zero, Vector3d.UnitZ);
        Assert.False(mirrored.Explain(TargetRep.Brep).IsConvertible);
    }

    [Fact]
    public void ExternalThread_MeshesToAClosedSolidWithinNominalBounds()
    {
        double length = 10;
        var stud = Shape.ExternalThread(M8, length);
        var mesh = stud.ToMesh(new MeshQuality { SdfResolution = 96 });
        Assert.True(mesh.IsClosed);

        // Chamfered ends never cut below the minor radius, so the volume stays between
        // the minor and major cylinders (with Surface Nets discretization slack).
        double core = Math.PI * Math.Pow(M8.MinorDiameter / 2, 2) * length;
        double major = Math.PI * Math.Pow(M8.MajorDiameter / 2, 2) * length;
        Assert.InRange(mesh.Volume(), core * 0.97, major * 1.01);

        // The chamfer removes material relative to the unchamfered stud.
        double plain = Shape.ExternalThread(M8, length, chamferEnds: false)
            .ToMesh(new MeshQuality { SdfResolution = 96 }).Volume();
        Assert.True(mesh.Volume() < plain, $"{mesh.Volume():g6} vs plain {plain:g6}");
    }

    [Fact]
    public void ExternalThread_ClearanceShrinksTheStud()
    {
        double length = 10;
        var quality = new MeshQuality { SdfResolution = 96 };
        double snug = Shape.ExternalThread(M8, length, clearance: 0).ToMesh(quality).Volume();
        double printed = Shape.ExternalThread(M8, length, clearance: 0.2).ToMesh(quality).Volume();
        Assert.True(printed < snug, $"clearance must shrink the external thread: {printed:g6} vs {snug:g6}");
    }

    [Fact]
    public void ExternalThread_TransformedStaysImplicitNativeAndPreservesVolume()
    {
        double length = 10;
        var quality = new MeshQuality { SdfResolution = 96 };
        var stud = Shape.ExternalThread(M8, length, chamferEnds: false);
        var moved = stud.RotateY(0.7).Translate(3, -2, 1);

        Assert.All(moved.Explain(TargetRep.Implicit).Entries,
            e => Assert.Equal(NodeSupport.Native, e.Support));

        // The rotated field polygonizes over a larger (conservative) bounds box with an
        // unaligned grid, so allow a few % of discretization drift.
        double v0 = stud.ToMesh(quality).Volume();
        double v1 = moved.ToMesh(quality).Volume();
        Assert.True(Math.Abs(v1 - v0) / v0 < 0.04, $"rigid motion changed the volume: {v1:g6} vs {v0:g6}");
    }

    [Fact]
    public void ThreadedHole_RemovesBetweenTapDrillAndMajorCylinders()
    {
        // 20×20×8 plate, top at z = 4; M6 through hole (depth past the far face is fine
        // — the thread void is SDF-only, no coplanarity constraint).
        var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var spec = StandardThreads.Metric(6);
        var plate = Shape.Box(20, 20, 8);
        var tapped = plate.ThreadedHole(spec, [new(0, 0)], depth: 10, top);

        // Zero-clearance threaded holes are B-Rep-Native (one clipped-profile tool per
        // point); nonzero clearance reports the distance-field blocker truthfully.
        Assert.True(tapped.Explain(TargetRep.Brep).IsConvertible);
        var cleared = plate.ThreadedHole(spec, [new(0, 0)], 10, top, clearance: 0.2);
        Assert.False(cleared.Explain(TargetRep.Brep).IsConvertible);
        Assert.Contains(cleared.Explain(TargetRep.Brep).Entries, e => e.Detail?.Contains("clearance") == true);
        Assert.True(tapped.Explain(TargetRep.Implicit).IsConvertible);

        var mesh = tapped.ToMesh(new MeshQuality { SdfResolution = 128 });
        Assert.True(mesh.IsClosed);

        // The void through the 8-thick plate is at least the tap-drill cylinder and at
        // most the major-diameter cylinder.
        double box = 20.0 * 20 * 8;
        double tapDrill = Math.PI * Math.Pow(spec.TapDrillDiameter / 2, 2) * 8;
        double majorBore = Math.PI * Math.Pow(spec.MajorDiameter / 2, 2) * 8;
        Assert.InRange(mesh.Volume(), box - majorBore * 1.05, box - tapDrill * 0.95);

        // Sign checks straight from the SDF: on the axis the hole is void; at the
        // major radius mid-plate the material between thread crests remains; past the
        // major radius it is always material.
        var sdf = tapped.ToImplicit();
        Assert.True(sdf.Evaluate((0, 0, 0)) > 0);
        Assert.True(sdf.Evaluate((spec.MajorDiameter / 2 + 0.3, 0, 0)) < 0);
    }

    [Fact]
    public void ThreadedHole_LowersToExactBrep()
    {
        // 20×20×8 plate, top at z = 4; two blind M8 holes, depth 6 (bottom at −2,
        // clear of the plate bottom at −4). Exercises: the clipped-profile combined
        // tool (pilot 6.8 > minor 6.65 — no coaxial pairs), two spiral-arc chains on
        // one plane face, and cascaded booleans (second hole cuts boolean output).
        var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var plate = Shape.Box(20, 20, 8);
        var tapped = plate.ThreadedHole(M8, [new(-4.5, 0), new(4.5, 0)], depth: 6, top);

        var brep = tapped.ToBrep();
        var mesh = EngrCAD.Interop.BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed, "tapped plate must tessellate closed");

        // Exact void volume per hole: depth · (2π/P)·∫ ½R'² over the clipped profile
        // R' = max(basic form, pilot radius) — crest flat P/8 at rMaj, pilot flat at
        // rPil between the exact flank lines (crossing at flankDrop below the crest).
        double p = M8.Pitch, rMaj = M8.MajorDiameter / 2, rMin = M8.MinorDiameter / 2;
        double rPil = M8.TapDrillDiameter / 2;
        double flankDrop = 5 * p / 16 * (rMaj - rPil) / (rMaj - rMin);
        double za = p / 16 + flankDrop, zb = 15 * p / 16 - flankDrop;
        double perPitch = 0.5 * rMaj * rMaj * (p / 8)
            + 0.5 * rPil * rPil * (zb - za)
            + 2 * (za - p / 16) * (rMaj * rMaj + rMaj * rPil + rPil * rPil) / 6;
        double voidVolume = 6 * (2 * Math.PI / p) * perPitch;
        double expected = 20.0 * 20 * 8 - 2 * voidVolume;
        // The tessellated void is chordally inscribed: ~1.9% of the void volume at 32
        // segments/circle, converging O(h²) to the analytic value (0.47% at 64, 0.12%
        // at 128 — verified), so the default-quality bound is 2.5% of the void.
        Assert.True(Math.Abs(mesh.Volume() - expected) < 0.025 * 2 * voidVolume,
            $"volume {mesh.Volume():g8} vs analytic {expected:g8}");
        var fine = EngrCAD.Interop.BRepTessellator.Tessellate(brep, segmentsPerCircle: 64, curveSamples: 24);
        Assert.True(fine.IsClosed);
        Assert.True(Math.Abs(fine.Volume() - expected) < 0.007 * 2 * voidVolume,
            $"64-segment volume {fine.Volume():g8} vs analytic {expected:g8}");

        // A depth landing the tool's flat bottom exactly on the plate's bottom face is
        // the unsupported coplanar case — rejected at lowering, like Drill.
        Assert.Throws<ArgumentException>(() =>
            plate.ThreadedHole(M8, [new(0, 0)], depth: 8, top).ToBrep());
    }

    [Fact]
    public void ThreadedHole_ClearanceGrowsTheVoid()
    {
        var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var spec = StandardThreads.Metric(8);
        var plate = Shape.Box(20, 20, 8);
        var quality = new MeshQuality { SdfResolution = 128 };

        double snug = plate.ThreadedHole(spec, [new(0, 0)], 10, top).ToMesh(quality).Volume();
        double printed = plate.ThreadedHole(spec, [new(0, 0)], 10, top, clearance: 0.2).ToMesh(quality).Volume();
        Assert.True(printed < snug,
            $"clearance must grow the internal void (less material): {printed:g6} vs snug {snug:g6}");
    }

    [Fact]
    public void ThreadedHole_And_ExternalThread_Validate()
    {
        var spec = StandardThreads.Metric(8);
        var plate = Shape.Box(20, 20, 8);

        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.ExternalThread(spec, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.ExternalThread(spec, 10, clearance: -0.1));
        // Half the thread depth (~0.34 for M8) caps the clearance before the profile degenerates.
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.ExternalThread(spec, 10, clearance: 0.4));

        Assert.Throws<ArgumentOutOfRangeException>(() => plate.ThreadedHole(spec, [new(0, 0)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plate.ThreadedHole(spec, [new(0, 0)], 10, clearance: 0.4));
        // Centers 8 apart == the major diameter: tangent, rejected.
        Assert.Throws<ArgumentException>(() => plate.ThreadedHole(spec, [new(0, 0), new(8, 0)], 10));

        // The metric convenience overload rejects non-catalog sizes.
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.ExternalThread(7.0, 10));
    }
}
