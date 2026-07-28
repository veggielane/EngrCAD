using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class MateTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name, double sx = 10, double sy = 6, double sz = 2) =>
        new(name, MeshPrimitives.Box(sx, sy, sz));

    /// <summary>A plate as an exact Shape so face selectors work.</summary>
    private static Part Plate(string name, double sx = 40, double sy = 30, double sz = 6) =>
        new(name, Shape.Box(sx, sy, sz));

    /// <summary>A plate with a bore, for concentric mates.</summary>
    private static Part BoredPlate(string name, double bore = 3) =>
        new(name, Shape.Box(40, 30, 6).Drill(
            HoleSpec.Simple(bore * 2), [new(0, 0)], 8,
            SketchPlane.At((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY)));

    private static double MaxResidual(MateSet set)
    {
        var probe = new MateSet(set.Assembly);
        foreach (var occurrence in set.Grounded)
            probe.Ground(occurrence);
        foreach (var mate in set.Mates)
            probe.Add(mate);
        // Grounding everything makes TrySolve a pure evaluation: nothing can move.
        foreach (var occurrence in set.Assembly.Occurrences)
            probe.Ground(occurrence);
        return probe.TrySolve().Residual;
    }

    // ---- coincident ----

    [Fact]
    public void CoincidentPoints_BringTheOccurrenceExactlyIntoPlace()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("arm"), At(37, -11, 4));

        var mates = new MateSet(rig)
            .Ground(fixedOne)
            .Add(Mate.Coincident(MateGeometry.Point(fixedOne, (5, 0, 1)), MateGeometry.Point(moving, (-5, 0, -1))));

        var result = mates.Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.True(result.Residual <= 1e-9, $"residual {result.Residual}");
        var world = moving.Frame.ToWorld((-5, 0, -1));
        Assert.Equal(5, world.X, 9);
        Assert.Equal(0, world.Y, 9);
        Assert.Equal(1, world.Z, 9);
        // A single point mate leaves the three rotations free, and says so.
        Assert.Equal(6, result.FreeDegreesOfFreedom);
        Assert.Equal(3, result.ConstrainedDegreesOfFreedom);
        Assert.True(result.IsUnderConstrained);
    }

    [Fact]
    public void CoincidentToAWorldPoint_NeedsNoGround()
    {
        var rig = new Assembly("rig");
        var moving = rig.Add(BoxPart("arm"), At(1, 2, 3));

        var result = new MateSet(rig)
            .Add(Mate.Coincident(MateGeometry.Point(moving, Vector3d.Zero), MateGeometry.World((7, 8, 9))))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(new Vector3d(7, 8, 9).X, moving.Frame.Origin.X, 9);
        Assert.Equal(9, moving.Frame.Origin.Z, 9);
    }

    // ---- planar ----

    [Fact]
    public void PlanarMate_SeatsTwoFacesFlushWithOpposedNormals()
    {
        var rig = new Assembly("rig");
        var lower = rig.Add(Plate("lower"));
        var upper = rig.Add(Plate("upper"), Frame3d.FromXY((13, -4, 40), Vector3d.UnitY, -Vector3d.UnitX));

        var result = new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
                MateGeometry.PlanarFace(upper, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First())))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        // The upper plate's bottom face now sits on z = 3 (the lower plate's top) with its
        // outward normal pointing back down.
        var bottom = upper.Frame.ToWorld((0, 0, -3));
        Assert.Equal(3, bottom.Z, 8);
        Assert.Equal(-1, upper.Frame.ToWorldVector(-Vector3d.UnitZ).Z, 8);
        // A plane pair pins one translation and two rotations; slide and spin stay free.
        Assert.Equal(3, result.ConstrainedDegreesOfFreedom);
    }

    [Fact]
    public void PlanarMate_HonoursAGap()
    {
        var rig = new Assembly("rig");
        var lower = rig.Add(Plate("lower"));
        var upper = rig.Add(Plate("upper"), At(0, 0, 30));

        new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
                MateGeometry.PlanarFace(upper, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First()),
                gap: 2.5))
            .Solve();

        Assert.Equal(5.5, upper.Frame.ToWorld((0, 0, -3)).Z, 8);
    }

    // ---- concentric ----

    [Fact]
    public void ConcentricMate_AlignsTwoBoreAxes()
    {
        var rig = new Assembly("rig");
        var lower = rig.Add(BoredPlate("lower"));
        var upper = rig.Add(BoredPlate("upper"),
            Frame3d.FromXY((17, 9, 25), Vector3d.UnitX, Vector3d.UnitY));

        var result = new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Concentric(
                MateGeometry.CylindricalFace(lower, Bore),
                MateGeometry.CylindricalFace(upper, Bore)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        // Both bores are on the part's local Z through the origin, so after the mate the
        // upper plate's axis passes through the world Z axis.
        var axisPoint = upper.Frame.ToWorld(Vector3d.Zero);
        Assert.Equal(0, axisPoint.X, 7);
        Assert.Equal(0, axisPoint.Y, 7);
        Assert.Equal(1, Math.Abs(upper.Frame.ToWorldVector(Vector3d.UnitZ).Z), 7);
        // Concentric pins 4 DOF: slide along the axis and spin about it stay free.
        Assert.Equal(4, result.ConstrainedDegreesOfFreedom);
        Assert.Equal(2, result.RemainingDegreesOfFreedom);

        static BrepFace Bore(BrepSolid solid) =>
            solid.Faces.First(f => f.IsCylindrical(out _, out _, out _));
    }

    [Fact]
    public void ConcentricPlusPlanar_FullySeatsAPartExceptItsSpin()
    {
        var rig = new Assembly("rig");
        var lower = rig.Add(BoredPlate("lower"));
        var upper = rig.Add(BoredPlate("upper"), At(21, -14, 33));

        var result = new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Concentric(
                MateGeometry.CylindricalFace(lower, Bore),
                MateGeometry.CylindricalFace(upper, Bore)))
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
                MateGeometry.PlanarFace(upper, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First())))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(5, result.ConstrainedDegreesOfFreedom);   // only the spin is left
        Assert.Equal(1, result.RemainingDegreesOfFreedom);
        Assert.Equal(3, upper.Frame.ToWorld((0, 0, -3)).Z, 7);
        Assert.Equal(0, upper.Frame.Origin.X, 7);

        static BrepFace Bore(BrepSolid solid) =>
            solid.Faces.First(f => f.IsCylindrical(out _, out _, out _));
    }

    // ---- distance / parallel / perpendicular / angle ----

    [Fact]
    public void DistanceMate_SeparatesTwoPointsExactly()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(3, 0, 0));

        var result = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Distance(MateGeometry.Point(a, Vector3d.Zero), MateGeometry.Point(b, Vector3d.Zero), 25))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(25, b.Frame.Origin.Length, 8);
        Assert.Equal(1, result.ConstrainedDegreesOfFreedom);
    }

    [Fact]
    public void ParallelAndPerpendicularMates_OrientWithoutTranslating()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), Frame3d.FromXY((4, 4, 4), (1, 1, 0), (-1, 1, 0)));
        var origin = b.Frame.Origin;

        var parallel = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Parallel(
                MateGeometry.Axis(a, Vector3d.Zero, Vector3d.UnitX),
                MateGeometry.Axis(b, Vector3d.Zero, Vector3d.UnitX)))
            .Solve();

        Assert.True(parallel.Converged, parallel.ToString());
        Assert.Equal(1, Math.Abs(b.Frame.ToWorldVector(Vector3d.UnitX).Dot(Vector3d.UnitX)), 8);
        Assert.Equal(origin, b.Frame.Origin);          // orientation only: nothing translated
        Assert.Equal(2, parallel.ConstrainedDegreesOfFreedom);

        var perpendicular = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Perpendicular(
                MateGeometry.Axis(a, Vector3d.Zero, Vector3d.UnitZ),
                MateGeometry.Axis(b, Vector3d.Zero, Vector3d.UnitX)))
            .Solve();

        Assert.True(perpendicular.Converged, perpendicular.ToString());
        Assert.Equal(0, b.Frame.ToWorldVector(Vector3d.UnitX).Dot(Vector3d.UnitZ), 8);
        Assert.Equal(1, perpendicular.ConstrainedDegreesOfFreedom);
    }

    [Fact]
    public void AngleMate_HitsTheRequestedAngle()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        // Start away from the parallel singularity (see the next test).
        var b = rig.Add(BoxPart("b"), Frame3d.FromXY((5, 0, 0), (1, 0.4, 0), (-0.4, 1, 0)));

        new MateSet(rig)
            .Ground(a)
            .Add(Mate.Angle(
                MateGeometry.Axis(a, Vector3d.Zero, Vector3d.UnitX),
                MateGeometry.Axis(b, Vector3d.Zero, Vector3d.UnitX), 30))
            .Solve();

        double cosine = b.Frame.ToWorldVector(Vector3d.UnitX).Dot(Vector3d.UnitX);
        Assert.Equal(Math.Cos(30 * Math.PI / 180), cosine, 8);
    }

    [Fact]
    public void AngleMateFromAnExactlyParallelStart_IsStationaryAndSaysSo()
    {
        // d/dθ cos θ = 0 at θ = 0, so an angle mate has NO first-order step when the two
        // directions start exactly parallel. Nudging and retrying would sometimes work,
        // which is the failure mode this solver is built to avoid.
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(5, 0, 0));
        var before = b.Frame;

        var result = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Angle(
                MateGeometry.Axis(a, Vector3d.Zero, Vector3d.UnitX),
                MateGeometry.Axis(b, Vector3d.Zero, Vector3d.UnitX), 30))
            .TrySolve();

        Assert.False(result.Converged);
        Assert.Equal(before, b.Frame);
        Assert.Contains(result.Diagnostics, d => d.Contains("stationary configuration"));
    }

    // ---- chains, sub-assemblies, and honesty ----

    [Fact]
    public void MatesChainThroughSeveralOccurrences()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(30, 5, 0));
        var c = rig.Add(BoxPart("c"), At(-8, 40, 12));

        var result = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Coincident(MateGeometry.Point(a, (5, 0, 0)), MateGeometry.Point(b, (-5, 0, 0))))
            .Add(Mate.Coincident(MateGeometry.Point(b, (5, 0, 0)), MateGeometry.Point(c, (-5, 0, 0))))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(12, result.FreeDegreesOfFreedom);
        Assert.Equal(5, b.Frame.ToWorld((-5, 0, 0)).X, 8);
        Assert.Equal(b.Frame.ToWorld((5, 0, 0)).X, c.Frame.ToWorld((-5, 0, 0)).X, 8);
    }

    [Fact]
    public void ASubAssemblyMovesAsOneRigidBody()
    {
        var inner = new Assembly("inner");
        inner.Add(BoxPart("x"), At(-4, 0, 0));
        inner.Add(BoxPart("y"), At(4, 0, 0));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        var sub = rig.Add(inner, At(20, 20, 20));

        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(MateGeometry.Point(ground, Vector3d.Zero), MateGeometry.Point(sub, Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        var instances = rig.Flatten();
        // Both children moved with the sub-assembly and kept their relative spacing.
        double dx = instances[2].World.TransformPoint(Vector3d.Zero).X
                  - instances[1].World.TransformPoint(Vector3d.Zero).X;
        Assert.Equal(8, dx, 8);
    }

    [Fact]
    public void OverConstrainedMatesRefuseLoudlyAndMoveNothing()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(3, 0, 0));
        var before = b.Frame;

        var mates = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Distance(
                MateGeometry.Point(a, Vector3d.Zero), MateGeometry.Point(b, Vector3d.Zero), 10, "ten"))
            .Add(Mate.Distance(
                MateGeometry.Point(a, Vector3d.Zero), MateGeometry.Point(b, Vector3d.Zero), 25, "twenty-five"));

        var result = mates.TrySolve();

        Assert.False(result.Converged);
        Assert.True(result.IsOverConstrained);
        Assert.Equal(before, b.Frame);                       // nothing moved
        Assert.Contains(result.Diagnostics, d => d.Contains("ten") || d.Contains("twenty-five"));
        Assert.Contains(result.Diagnostics, d => d.Contains("nothing was moved"));

        var thrown = Assert.Throws<MateSolveException>(() => mates.Solve());
        Assert.False(thrown.Result.Converged);
    }

    [Fact]
    public void UnderConstrainedIsReported_AndOptionallyRefused()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(3, 0, 0));

        var mates = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Coincident(MateGeometry.Point(a, Vector3d.Zero), MateGeometry.Point(b, Vector3d.Zero)));

        var lenient = mates.Solve();
        Assert.True(lenient.Converged);
        Assert.Equal(3, lenient.RemainingDegreesOfFreedom);
        Assert.Contains(lenient.Diagnostics, d => d.Contains("degrees of freedom remain"));

        Assert.Throws<MateSolveException>(
            () => mates.Solve(new MateSolverSettings { RequireFullyConstrained = true }));
    }

    [Fact]
    public void FullyConstrainedIsRecognized()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(9, 9, 9));

        var result = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Coincident(MateGeometry.Point(a, (1, 0, 0)), MateGeometry.Point(b, (0, 0, 0))))
            .Add(Mate.Coincident(MateGeometry.Point(a, (1, 2, 0)), MateGeometry.Point(b, (0, 2, 0))))
            .Add(Mate.Coincident(MateGeometry.Point(a, (1, 0, 3)), MateGeometry.Point(b, (0, 0, 3))))
            .Solve(new MateSolverSettings { RequireFullyConstrained = true });

        Assert.Equal(0, result.RemainingDegreesOfFreedom);
        Assert.Equal(6, result.ConstrainedDegreesOfFreedom);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("degrees of freedom remain"));
    }

    [Fact]
    public void UngroundedAssembliesReportSixFreeDegrees()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(3, 0, 0));

        var result = new MateSet(rig)
            .Add(Mate.Coincident(MateGeometry.Point(a, (1, 0, 0)), MateGeometry.Point(b, (0, 0, 0))))
            .Add(Mate.Coincident(MateGeometry.Point(a, (1, 2, 0)), MateGeometry.Point(b, (0, 2, 0))))
            .Add(Mate.Coincident(MateGeometry.Point(a, (1, 0, 3)), MateGeometry.Point(b, (0, 0, 3))))
            .Solve();

        // 12 unknowns, 6 pinned by the relative constraints, 6 of whole-assembly drift left.
        Assert.Equal(12, result.FreeDegreesOfFreedom);
        Assert.Equal(6, result.ConstrainedDegreesOfFreedom);
    }

    [Fact]
    public void UnmentionedOccurrencesAreNamedNotSilentlyCounted()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(3, 0, 0));
        rig.Add(BoxPart("orphan"), At(50, 0, 0));

        var result = new MateSet(rig)
            .Ground(a)
            .Add(Mate.Coincident(MateGeometry.Point(a, Vector3d.Zero), MateGeometry.Point(b, Vector3d.Zero)))
            .Solve();

        Assert.Equal(6, result.FreeDegreesOfFreedom);        // the orphan is NOT 6 more
        Assert.Contains(result.Diagnostics, d => d.Contains("orphan"));
    }

    [Fact]
    public void MatesMustReferenceDirectOccurrencesOfTheAssembly()
    {
        var inner = new Assembly("inner");
        var deep = inner.Add(BoxPart("deep"));
        var rig = new Assembly("rig");
        var top = rig.Add(BoxPart("top"));
        rig.Add(inner);

        var mates = new MateSet(rig);
        var bad = Mate.Coincident(MateGeometry.Point(top, Vector3d.Zero), MateGeometry.Point(deep, Vector3d.Zero));
        // A bare deep occurrence names a TYPE-level frame, not a placement; the fix is
        // the occurrence-path overloads, and the message says so.
        var exception = Assert.Throws<ArgumentException>(() => mates.Add(bad));
        Assert.Contains("occurrence path", exception.Message);
        Assert.Throws<ArgumentException>(() => mates.Ground(deep));
    }

    [Fact]
    public void DirectedMatesDemandDirections()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"));
        var pointOnly = MateGeometry.Point(a, Vector3d.Zero);
        var axis = MateGeometry.Axis(b, Vector3d.Zero, Vector3d.UnitZ);

        Assert.Throws<ArgumentException>(() => Mate.Concentric(pointOnly, axis));
        Assert.Throws<ArgumentException>(() => Mate.Planar(axis, pointOnly));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mate.Distance(pointOnly, pointOnly, -1));
    }

    [Fact]
    public void SelectorsNeedAnExactSolidAndTheRightFaceKind()
    {
        var rig = new Assembly("rig");
        var mesh = rig.Add(BoxPart("mesh"));   // a HalfEdgeMesh part: no B-Rep
        Assert.Throws<ArgumentException>(
            () => MateGeometry.PlanarFace(mesh, s => s.Faces.First()));

        var exact = rig.Add(BoredPlate("exact"));
        Assert.Throws<ArgumentException>(() => MateGeometry.PlanarFace(
            exact, s => s.Faces.First(f => f.IsCylindrical(out _, out _, out _))));
        Assert.Throws<ArgumentException>(() => MateGeometry.CylindricalFace(
            exact, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()));
    }

    [Fact]
    public void SolvingIsScaleFree()
    {
        // The same problem at 1000x: the solver must still reach the weld tolerance.
        foreach (double scale in new[] { 0.01, 1.0, 1000.0 })
        {
            var rig = new Assembly($"rig{scale}");
            var a = rig.Add(BoxPart("a", scale, scale, scale));
            var b = rig.Add(BoxPart("b", scale, scale, scale), At(31 * scale, -7 * scale, 5 * scale));

            var result = new MateSet(rig)
                .Ground(a)
                .Add(Mate.Coincident(
                    MateGeometry.Point(a, (scale, 0, 0)), MateGeometry.Point(b, (-scale, 0, 0))))
                .Add(Mate.Parallel(
                    MateGeometry.Axis(a, Vector3d.Zero, Vector3d.UnitZ),
                    MateGeometry.Axis(b, Vector3d.Zero, Vector3d.UnitZ)))
                .Solve();

            Assert.True(result.Converged, $"scale {scale}: {result}");
            Assert.Equal(scale, b.Frame.ToWorld((-scale, 0, 0)).X, 8);
        }
    }

    [Fact]
    public void SolvedFramesFlowStraightIntoTheFlattening()
    {
        var rig = new Assembly("rig");
        var ground = rig.Add(Plate("lower"));
        var lid = rig.Add(Plate("upper"), At(50, 50, 50));

        new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(ground, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
                MateGeometry.PlanarFace(lid, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First())))
            .Solve();

        // No second path: the mate wrote Occurrence.Frame and Flatten composed it.
        var instances = rig.Flatten();
        Assert.Equal(lid.Frame.ToMatrix(), instances[1].World);
    }

    [Fact]
    public void AMateSetWithNoMatesIsAVacuousSuccess()
    {
        var rig = new Assembly("rig");
        rig.Add(BoxPart("a"));
        var result = new MateSet(rig).TrySolve();

        Assert.True(result.Converged);
        Assert.Equal(0, result.FreeDegreesOfFreedom);
        Assert.Contains(result.Diagnostics, d => d.Contains("no mate mentions"));
    }

    [Fact]
    public void EverythingGroundedEvaluatesRatherThanPretending()
    {
        var rig = new Assembly("rig");
        var a = rig.Add(BoxPart("a"));
        var b = rig.Add(BoxPart("b"), At(3, 0, 0));

        var violated = new MateSet(rig)
            .Ground(a)
            .Ground(b)
            .Add(Mate.Coincident(MateGeometry.Point(a, Vector3d.Zero), MateGeometry.Point(b, Vector3d.Zero)))
            .TrySolve();

        Assert.False(violated.Converged);
        Assert.Equal(3, violated.Residual, 9);
        Assert.Equal(0, violated.FreeDegreesOfFreedom);
        Assert.Equal(0, MaxResidual(new MateSet(rig)));
    }
}
