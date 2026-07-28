using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Hidden-line removal: the projection that turns 3D geometry into the classified line
/// work a drawing is made of. The assertions here are about the CLASSIFICATION rather
/// than about pixel counts — a run is hidden or visible, and where the boundary between
/// them falls.
/// </summary>
public class HiddenLineTests
{
    private static Frame3d Front => StandardViews.SheetFrame(StandardViews.DirectionFor("front")!.Value);
    private static Frame3d Top => StandardViews.SheetFrame(StandardViews.DirectionFor("top")!.Value);

    private static Scene SceneOf(params Part[] parts)
    {
        var scene = new Scene();
        foreach (var part in parts)
            scene.Add(part);
        return scene;
    }

    /// <summary>
    /// A solo box seen from a corner: the three edges meeting at the FAR corner are
    /// hidden by the box's own material and the other nine are not. That is the
    /// back-face stage of the probe on its own — both faces of those three edges point
    /// away from the viewer — and it is exact, needing no ray and no mesh.
    /// </summary>
    [Fact]
    public void SoloBox_CornerView_HidesTheThreeEdgesAtTheFarCorner()
    {
        var iso = StandardViews.SheetFrame(StandardViews.DirectionFor("iso")!.Value);
        var part = new Part("box", Shape.Box(40, 20, 10));
        var result = HiddenLineRemoval.Project(part, iso);

        // An axis-parallel edge of length L projects to L*sqrt(1 - (e.d)^2), and the
        // iso direction makes that sqrt(2/3) on all three axes. The far corner has one
        // edge of each length.
        double foreshortened = Math.Sqrt(2.0 / 3);
        double expected = (40 + 20 + 10) * foreshortened;
        double hidden = result.Hidden.Sum(r => r.Length);

        // Where a hidden edge meets a visible one, the corner neighbourhood is genuinely
        // ambiguous for one bias step: within it the probe's local-surface read picks up
        // the faces on the far side of the vertex. So the dashed total falls a little
        // SHORT of the analytic value and never over it, by at most one step per
        // junction — three of them here, on a body whose extent is the box's diagonal.
        double bias = new Vector3d(40, 20, 10).Length * HiddenLineOptions.DefaultBiasFraction;
        Assert.InRange(hidden, expected - 3 * bias * foreshortened, expected);

        // And the other nine edges are solid: nothing is lost or double-counted.
        Assert.Equal(4 * expected, hidden + result.Visible.Sum(r => r.Length), 9);
    }

    /// <summary>
    /// The classic occlusion case: a tall thin box standing in front of a fatter
    /// cylinder. The cylinder's rim lines run wider than the box, so each is split — the
    /// covered middle dashed, the ends outside the box solid — and the split lands on
    /// the box's own edges. Nothing hidden may lie outside the occluder's outline:
    /// occlusion cannot reach past the thing doing the occluding.
    /// </summary>
    [Fact]
    public void BoxInFrontOfCylinder_HidesExactlyWhatTheBoxCovers()
    {
        // Cylinder at the origin (radius 8, so its rims run to x = +-8); box pulled
        // forward (-Y is toward the viewer in a front view) and made taller than the
        // cylinder but narrower, so it covers |x| <= 5 of everything behind it.
        var cylinder = new Part("cylinder", Shape.Cylinder(8, 40));
        var box = new Part("box", Shape.Box(10, 6, 60).Translate(0, -20, 0));
        var result = HiddenLineRemoval.Project(SceneOf(cylinder, box), Front);

        var hiddenRuns = result.Hidden.ToList();
        Assert.NotEmpty(hiddenRuns);

        // The box projects to x in [-5, 5] on the sheet; nothing hidden may sit outside.
        foreach (var point in hiddenRuns.SelectMany(r => r.Points))
            Assert.InRange(point.X, -5 - 1e-3, 5 + 1e-3);

        // And the hidden runs reach the box's edges: the split is at x = +-5, not at
        // whatever sample happened to be nearest.
        double reach = hiddenRuns.SelectMany(r => r.Points).Max(p => Math.Abs(p.X));
        Assert.Equal(5, reach, 3);
    }

    /// <summary>
    /// The control for the test above: the same cylinder with no occluder has nothing
    /// hidden at all, so a probe that dashed things at random would fail here.
    ///
    /// <para>It also pins the TANGENCY convention. Seen edge-on, a rim circle's far half
    /// projects onto its near half and the material between them is exactly the cap
    /// plane the ray runs along — a genuine tangency, which drafting resolves by drawing
    /// the coincident pair once, solid. The probe reaches the same answer structurally
    /// rather than by an epsilon: the cap's normal is exactly perpendicular to the view,
    /// so the back-face stage does not reject it and the ray steps off along that cap
    /// into clear air.</para>
    /// </summary>
    [Fact]
    public void CylinderAlone_FrontView_DrawsItsCoincidentRimsSolid()
    {
        var cylinder = new Part("cylinder", Shape.Cylinder(8, 40));
        var result = HiddenLineRemoval.Project(cylinder, Front);

        Assert.NotEmpty(result.Runs);
        Assert.Empty(result.Hidden);
    }

    /// <summary>
    /// A drilled plate seen along the bore axis: the bore's FAR rim is visible through
    /// its own hole. This is the case the two-stage probe exists for — the ray from a
    /// point on that rim runs parallel to the bore wall, so a probe that started on the
    /// exact surface would scrape along a wall it is tangent to and report the rim
    /// hidden in patches.
    /// </summary>
    [Fact]
    public void DrilledPlate_ViewedAlongTheBore_ShowsTheFarRim()
    {
        var top = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
        var plate = new Part("plate", Shape.Box(40, 40, 10)
            .Drill(HoleSpec.Simple(8), [new Vector2d(0, 0)], depth: 12, top));
        var result = HiddenLineRemoval.Project(plate, Top);

        // Both rims project onto the same circle of radius 4. Every point at that radius
        // must be visible: you can see straight through the hole.
        var onRim = result.Runs
            .SelectMany(r => r.Points.Select(p => (Run: r, Radius: p.Length)))
            .Where(x => Math.Abs(x.Radius - 4) < 0.05)
            .ToList();
        Assert.NotEmpty(onRim);
        Assert.All(onRim, x => Assert.Equal(EdgeVisibility.Visible, x.Run.Visibility));
    }

    /// <summary>
    /// A cylinder seen from the side has no MODELLED edge along its outline — the
    /// surface is smooth there. The mesh-derived silhouette supplies it, and this pins
    /// that it does (and that it is labelled as such, so the fidelity story survives
    /// into the output).
    /// </summary>
    [Fact]
    public void SmoothOutline_ComesFromTheMeshSilhouette_AndIsLabelled()
    {
        var cylinder = new Part("cylinder", Shape.Cylinder(8, 40));
        var withOutline = HiddenLineRemoval.Project(cylinder, Front);
        var without = HiddenLineRemoval.Project(
            cylinder, Front, new HiddenLineOptions { IncludeSilhouette = false });

        var silhouette = withOutline.Runs.Where(r => r.Source == EdgeSource.Silhouette).ToList();
        Assert.NotEmpty(silhouette);
        Assert.DoesNotContain(without.Runs, r => r.Source == EdgeSource.Silhouette);

        // The outline runs the cylinder's full height at both extremes of x.
        double tallest = silhouette.Max(r => r.Points.Max(p => p.Y) - r.Points.Min(p => p.Y));
        Assert.Equal(40, tallest, 1);
    }

    /// <summary>
    /// Bisection, not sampling, sets where a dashed run ends. Halving the split
    /// tolerance must move the boundary by less than the tolerance itself — which it can
    /// only do if the refinement is real.
    /// </summary>
    [Fact]
    public void RunBoundaries_AreRefinedToTheSplitTolerance()
    {
        var cylinder = new Part("cylinder", Shape.Cylinder(8, 40));
        var box = new Part("box", Shape.Box(10, 6, 80).Translate(0, -20, 0));
        var parts = SceneOf(cylinder, box);

        double Boundary(double splitFraction)
        {
            var result = HiddenLineRemoval.Project(parts, Front,
                new HiddenLineOptions { SplitFraction = splitFraction });
            // The rightmost x reached by any hidden run: the occluder's own right edge.
            return result.Hidden.SelectMany(r => r.Points).Max(p => p.X);
        }

        double coarse = Boundary(1e-3);
        double fine = Boundary(1e-6);
        // The box's right edge is at x = 5; both must land on it, the finer one tighter.
        Assert.Equal(5, fine, 3);
        Assert.True(Math.Abs(fine - 5) <= Math.Abs(coarse - 5) + 1e-9,
            $"refining should not move the boundary away from the truth (coarse {coarse}, fine {fine})");
    }

    /// <summary>Same input, same output — bit for bit, run for run. A drawing that
    /// changed between builds would be worse than useless.</summary>
    [Fact]
    public void Projection_IsDeterministic()
    {
        var scene = SceneOf(
            new Part("cylinder", Shape.Cylinder(8, 40)),
            new Part("box", Shape.Box(10, 6, 12).Translate(0, -20, 0)));

        var first = HiddenLineRemoval.Project(scene, Front);
        var second = HiddenLineRemoval.Project(scene, Front);

        Assert.Equal(first.Runs.Count, second.Runs.Count);
        for (int i = 0; i < first.Runs.Count; i++)
        {
            Assert.Equal(first.Runs[i].Visibility, second.Runs[i].Visibility);
            Assert.Equal(first.Runs[i].Source, second.Runs[i].Source);
            Assert.Equal(first.Runs[i].Points.Count, second.Runs[i].Points.Count);
            for (int k = 0; k < first.Runs[i].Points.Count; k++)
            {
                Assert.Equal(first.Runs[i].Points[k].X, second.Runs[i].Points[k].X);
                Assert.Equal(first.Runs[i].Points[k].Y, second.Runs[i].Points[k].Y);
            }
        }
    }

    /// <summary>Turning hidden runs off leaves exactly the visible ones, unchanged.</summary>
    [Fact]
    public void IncludeHiddenFalse_LeavesTheVisibleRunsUntouched()
    {
        var iso = StandardViews.SheetFrame(StandardViews.DirectionFor("iso")!.Value);
        var part = new Part("box", Shape.Box(40, 20, 10));
        var full = HiddenLineRemoval.Project(part, iso);
        var visibleOnly = HiddenLineRemoval.Project(
            part, iso, new HiddenLineOptions { IncludeHidden = false });

        Assert.DoesNotContain(visibleOnly.Runs, r => r.Visibility == EdgeVisibility.Hidden);
        Assert.Equal(full.Visible.Count(), visibleOnly.Runs.Count);
        Assert.Equal(full.Visible.Sum(r => r.Length), visibleOnly.Runs.Sum(r => r.Length), 9);
    }

    /// <summary>A hidden part contributes nothing — not its lines and not its
    /// occlusion. Same rule an export follows.</summary>
    [Fact]
    public void HiddenParts_NeitherDrawNorOcclude()
    {
        var cylinder = new Part("cylinder", Shape.Cylinder(8, 40));
        var box = new Part("box", Shape.Box(10, 6, 60).Translate(0, -20, 0)) { Hidden = true };
        var result = HiddenLineRemoval.Project(SceneOf(cylinder, box), Front);

        // The occluder is gone AND it stopped occluding: same runs as the cylinder alone.
        var alone = HiddenLineRemoval.Project(cylinder, Front);
        Assert.Equal(alone.Runs.Count, result.Runs.Count);
        Assert.Empty(result.Hidden);
    }

    /// <summary>The sheet frame is right-handed and its Z is the view direction, so
    /// <c>ToLocal</c> gives (sheet x, sheet y, depth) — the property every projection
    /// here relies on.</summary>
    [Theory]
    [InlineData("front")]
    [InlineData("back")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("iso")]
    public void SheetFrame_IsRightHandedAndFacesTheViewer(string view)
    {
        var direction = StandardViews.DirectionFor(view)!.Value.Normalized();
        var frame = StandardViews.SheetFrame(direction);

        Assert.Equal(direction.X, frame.Z.X, 12);
        Assert.Equal(direction.Y, frame.Z.Y, 12);
        Assert.Equal(direction.Z, frame.Z.Z, 12);
        var cross = frame.X.Cross(frame.Y);
        Assert.Equal(1, cross.Dot(frame.Z), 12);
    }

    /// <summary>Third-angle convention checks on the two frames a drawing leans on:
    /// a front view has model +X to the right and +Z up; a top view has +X right and
    /// +Y up (which is what puts the part's far side at the top of the page).</summary>
    [Fact]
    public void FrontAndTopFrames_MatchTheDraftingConvention()
    {
        Assert.Equal(new Vector3d(1, 0, 0), Front.X);
        Assert.Equal(new Vector3d(0, 0, 1), Front.Y);
        Assert.Equal(new Vector3d(1, 0, 0), Top.X);
        Assert.Equal(new Vector3d(0, 1, 0), Top.Y);
    }
}
