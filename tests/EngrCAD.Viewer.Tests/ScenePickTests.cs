using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Click picking, checked against geometry it did not get for free.
///
/// <para>A picker that always returned the first instance passes "something was picked",
/// so every assertion here pins a SECOND fact: the world point the ray reports must land
/// on the analytically known surface (a box's top face is exactly z = h), and it must
/// project back to the pixel that was clicked. Either one alone can be satisfied by a
/// subtly wrong ray; together they cannot.</para>
///
/// <para>Both front ends call <see cref="ScenePick"/> — the desktop viewport's
/// <c>HitTest</c> and the browser client's pick both reduce to it — so these are tests of
/// the answer both give.</para>
/// </summary>
public class ScenePickTests
{
    private const double Size = 10;
    private const double Height = 4;

    // A top-down view: eye above the origin looking straight down, up = +Y so LookAt's
    // basis stays non-degenerate (up = +Z would be parallel to the view direction).
    private const double Distance = 50;
    private const int Width = 200;
    private const int Pixels = 200;

    /// <summary>A box resting ON z = 0, so its top face is exactly z = <see cref="Height"/>
    /// — the analytic surface every hit is checked against. (The centred
    /// <c>Shape.Box(w, d, h)</c> overload would put it at h/2, which is a less obvious
    /// number to read an assertion against.)</summary>
    private static PickMesh Box() =>
        PickMesh.Build(RenderMesh.CreateFlat(
            new Part("box", Shape.Box(new Aabb((-Size / 2, -Size / 2, 0), (Size / 2, Size / 2, Height))))
                .GetMesh()));

    private static Matrix4d TopDown() =>
        CameraMath.Perspective(Math.PI / 4, 1.0, 1, 200)
        * CameraMath.LookAt((0, 0, Distance), Vector3d.Zero, Vector3d.UnitY);

    /// <summary>The centre pixel; the ray through it passes down the world Z axis.</summary>
    private const double Centre = Pixels / 2.0;

    [Fact]
    public void TheRayThroughACentrePixelPassesThroughTheCameraTarget()
    {
        Assert.True(ScenePick.TryRay(Centre, Centre, Width, Pixels, TopDown(), out var near, out var far));

        // Both ends sit on the world Z axis, and the near end is nearer the eye than the
        // far end — the sanity check that catches an inverted depth range.
        Assert.Equal(0, near.X, 9);
        Assert.Equal(0, near.Y, 9);
        Assert.Equal(0, far.X, 9);
        Assert.Equal(0, far.Y, 9);
        Assert.True(near.Z > far.Z, $"near {near.Z} should be above far {far.Z}");
    }

    [Fact]
    public void APixelAboveTheCentreUnprojectsUpwardsInY()
    {
        // Screen y counts DOWN from the top, world +Y is up in this view: a pixel above
        // the centre must map to a POSITIVE y. Getting this backwards is the classic
        // pick bug, and it is invisible on a symmetric model.
        Assert.True(ScenePick.TryRay(Centre, Centre - 40, Width, Pixels, TopDown(), out var near, out _));
        Assert.True(near.Y > 0, $"expected +Y, got {near.Y}");
    }

    [Fact]
    public void TheHitPointLiesOnTheBoxTopFace()
    {
        var instances = new[] { new PickInstance(Box(), Matrix4d.Identity) };

        var hit = ScenePick.Nearest(Centre, Centre, Width, Pixels, TopDown(), instances);

        Assert.True(hit.Hit);
        Assert.Equal(0, hit.Index);
        // The second fact: Shape.Box spans z in [0, Height], so a ray straight down from
        // above meets it at exactly z = Height. Nothing about the picker guarantees that
        // — a ray one pixel off, or a t returned in the wrong units, lands elsewhere.
        Assert.Equal(Height, hit.World.Z, 9);
        Assert.Equal(0, hit.World.X, 9);
        Assert.Equal(0, hit.World.Y, 9);
    }

    [Fact]
    public void AnOffsetPixelStillLandsOnTheTopFaceAtTheRightPlace()
    {
        var instances = new[] { new PickInstance(Box(), Matrix4d.Identity) };

        // 20 x 12 pixels off centre lands well inside the 10 x 10 footprint: at the top
        // face the half-height of the frustum is 46 * tan(22.5 deg) ~= 19 units, so a
        // much larger offset would slide off the top face onto a side.
        var hit = ScenePick.Nearest(Centre + 20, Centre + 12, Width, Pixels, TopDown(), instances);

        Assert.True(hit.Hit);
        Assert.Equal(Height, hit.World.Z, 9);
        // Perspective from above: the hit is right of and BELOW the centre in world terms
        // (screen y counts down), and inside the box's footprint.
        Assert.True(hit.World.X > 0 && hit.World.X < Size / 2, $"x = {hit.World.X}");
        Assert.True(hit.World.Y < 0 && hit.World.Y > -Size / 2, $"y = {hit.World.Y}");

        // And it projects back to the pixel that was clicked. This closes the loop the
        // z-assertion alone leaves open: a ray with the wrong field of view still hits
        // z = Height, just at the wrong x and y.
        var (px, py) = Project(hit.World, TopDown(), Width, Pixels);
        Assert.Equal(Centre + 20, px, 6);
        Assert.Equal(Centre + 12, py, 6);
    }

    [Fact]
    public void ARayThatMissesEverythingPicksNothing()
    {
        var instances = new[] { new PickInstance(Box(), Matrix4d.Identity) };

        // The top-left corner of a 200x200 viewport at 45 degrees is well outside a
        // 10x10 box 50 units away.
        Assert.False(ScenePick.Nearest(2, 2, Width, Pixels, TopDown(), instances).Hit);
    }

    [Fact]
    public void TheNearerOfTwoStackedInstancesWins()
    {
        var box = Box();
        var instances = new[]
        {
            new PickInstance(box, Matrix4d.Identity),                              // top at z = 4
            new PickInstance(box, Matrix4d.CreateTranslation((0, 0, 6))),          // top at z = 10
        };

        var hit = ScenePick.Nearest(Centre, Centre, Width, Pixels, TopDown(), instances);

        Assert.Equal(1, hit.Index);
        Assert.Equal(Height + 6, hit.World.Z, 9);
    }

    [Fact]
    public void HidingTheNearerInstanceLetsTheRayReachTheOneBehind()
    {
        var box = Box();
        var instances = new[]
        {
            new PickInstance(box, Matrix4d.Identity),
            new PickInstance(box, Matrix4d.CreateTranslation((0, 0, 6)), Visible: false),
        };

        var hit = ScenePick.Nearest(Centre, Centre, Width, Pixels, TopDown(), instances);

        // Not merely "something else was picked": the world point proves it is the LOWER
        // box's top face, so the hidden one was skipped rather than mis-transformed.
        Assert.Equal(0, hit.Index);
        Assert.Equal(Height, hit.World.Z, 9);
    }

    [Fact]
    public void InstancesShareOnePickMeshAndKeepTheirOwnPoses()
    {
        // One BVH, two poses — the property that makes an assembly of N bolts cost one
        // BVH. Each must still be picked in its own place.
        var box = Box();
        var instances = new[]
        {
            new PickInstance(box, Matrix4d.CreateTranslation((-8, 0, 0))),
            new PickInstance(box, Matrix4d.CreateTranslation((8, 0, 0))),
        };
        Assert.Same(instances[0].Mesh, instances[1].Mesh);

        var left = ScenePick.Nearest(Centre - 40, Centre, Width, Pixels, TopDown(), instances);
        var right = ScenePick.Nearest(Centre + 40, Centre, Width, Pixels, TopDown(), instances);

        Assert.Equal(0, left.Index);
        Assert.Equal(1, right.Index);
        Assert.True(left.World.X < 0 && right.World.X > 0);
        Assert.Equal(Height, left.World.Z, 9);
        Assert.Equal(Height, right.World.Z, 9);
    }

    [Fact]
    public void ScalingAnInstanceMovesItsPickedSurfaceWithIt()
    {
        // The ray goes into LOCAL space and t comes back through the model matrix, so a
        // scaled instance must be picked at its scaled surface, not its unscaled one.
        var instances = new[]
        {
            new PickInstance(Box(), Matrix4d.CreateScale(2)),
        };

        var hit = ScenePick.Nearest(Centre, Centre, Width, Pixels, TopDown(), instances);

        Assert.Equal(2 * Height, hit.World.Z, 9);
    }

    // ---- the forward dependency: sections ----
    //
    // Sections are a later rung in the browser client, but the rule is already shared, so
    // the picker honors them today and these pin that it does. Without this, "picking is
    // section-aware" would be a claim about code nobody had run.

    [Fact]
    public void ASurfaceTheSectionPlaneRemovedCannotBePickedThrough()
    {
        var instances = new[] { new PickInstance(Box(), Matrix4d.Identity) };
        var planes = new[] { SectionPlane.On(SectionAxis.Z, 2) };   // keep z <= 2

        var hit = ScenePick.Nearest(
            Centre, Centre, Width, Pixels, TopDown(), instances,
            sectionEnabled: true, sectionPlanes: planes);

        // The top face at z = 4 is gone, so the ray carries on and lands on the inside of
        // the BOTTOM face at z = 0 — the interior the cut exposed, which is exactly what
        // the shader shows there.
        Assert.True(hit.Hit);
        Assert.Equal(0, hit.World.Z, 9);
    }

    [Fact]
    public void APartExemptFromSectioningIsStillPickedAtItsOwnSurface()
    {
        // ClippedBySection false is the drafting convention that a fastener is drawn
        // whole inside a cutaway; picking mirrors it, which is what keeps the clickable
        // surface and the visible one the same surface.
        var instances = new[]
        {
            new PickInstance(Box(), Matrix4d.Identity, Visible: true, ClippedBySection: false),
        };
        var planes = new[] { SectionPlane.On(SectionAxis.Z, 2) };

        var hit = ScenePick.Nearest(
            Centre, Centre, Width, Pixels, TopDown(), instances,
            sectionEnabled: true, sectionPlanes: planes);

        Assert.Equal(Height, hit.World.Z, 9);
    }

    [Fact]
    public void SectionPlanesAreIgnoredWhileSectionModeIsOff()
    {
        var instances = new[] { new PickInstance(Box(), Matrix4d.Identity) };
        var planes = new[] { SectionPlane.On(SectionAxis.Z, 2) };

        var hit = ScenePick.Nearest(
            Centre, Centre, Width, Pixels, TopDown(), instances,
            sectionEnabled: false, sectionPlanes: planes);

        Assert.Equal(Height, hit.World.Z, 9);
    }

    // ---- hover throttle ----

    [Fact]
    public void TheHoverThrottleSamplesOnlyAfterEnoughTravel()
    {
        var throttle = new HoverThrottle(HoverThrottle.DefaultThreshold);

        Assert.True(throttle.ShouldSample(100, 100));    // no previous sample
        Assert.False(throttle.ShouldSample(102, 100));   // 2 px: jitter
        Assert.True(throttle.ShouldSample(105, 100));    // 5 px from the last ACCEPTED
        Assert.False(throttle.ShouldSample(106, 101));

        throttle.Reset();
        Assert.True(throttle.ShouldSample(106, 101));    // forgetting re-picks immediately
    }

    /// <summary>Projects a world point back to viewport pixels — the inverse of the
    /// unprojection under test, written independently of it (TransformPoint does the
    /// perspective divide, so this is the whole forward transform).</summary>
    private static (double X, double Y) Project(
        in Vector3d world, in Matrix4d viewProjection, double width, double height)
    {
        var ndc = viewProjection.TransformPoint(world);
        return ((ndc.X + 1) * width / 2, (1 - ndc.Y) * height / 2);
    }

    // ---- the deformed-ray correction ----
    //
    // A displaced part's pick BVH is built ONCE at the part's own DeformScale; at any
    // other animation factor ScenePick inflates the broad phase by the largest possible
    // vertex move (conservative) and tests the exactly-displaced triangles. These pin
    // both halves: the narrow phase's exactness, and the broad phase finding a part the
    // un-inflated query cannot see at all.

    /// <summary>A box whose every vertex is displaced by <paramref name="direction"/>
    /// (a constant unit field) at the given stated exaggeration.</summary>
    private static PickMesh DeformedBox(Vector3d direction, double scale)
    {
        var part = new Part("box", Shape.Box(
            new Aabb((-Size / 2, -Size / 2, 0), (Size / 2, Size / 2, Height))));
        var mesh = part.GetMesh();
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm", _ => direction));
        part.FieldDisplay = new FieldDisplay { Field = "u", Deform = "u", DeformScale = scale };
        var upload = PartUploads.Build(part, new PartUploadRequest { Fields = true, Pick = true });
        return upload.RequirePick;
    }

    [Fact]
    public void ADeformedPart_IsPickedWhereItIsDrawn_AtEveryFactor()
    {
        // Unit +Z displacement at 10x: the indexed top face is z = 4 + 10; at factor f
        // the DRAWN top is z = 4 + 10f, and the hit must land exactly there.
        var pick = DeformedBox(Vector3d.UnitZ, 10);
        Assert.Equal(10, pick.BuiltScale);
        Assert.Equal(1, pick.MaxDisplacement, 12);
        var instances = new[] { new PickInstance(pick, Matrix4d.Identity) };

        foreach (double factor in new[] { 1.0, 0.25, 0.0, 2.0 })
        {
            var hit = ScenePick.Nearest(
                Centre, Centre, Width, Pixels, TopDown(), instances, deformFactor: factor);
            Assert.True(hit.Hit, $"factor {factor} should hit");
            Assert.Equal(Height + 10 * factor, hit.World.Z, 9);
        }
    }

    [Fact]
    public void TheBroadPhase_FindsAPartTheIndexedBoxesCannotSee()
    {
        // Unit +X displacement at 10x: the INDEX holds the box at x in [5, 15]; at
        // factor 3 the drawn box sits at x in [25, 35]. A vertical ray at x = 30 misses
        // every indexed box, so only the inflated broad phase can find it — and at
        // factor 1 the same ray honestly misses.
        var pick = DeformedBox(Vector3d.UnitX, 10);
        var instances = new[] { new PickInstance(pick, Matrix4d.Identity) };

        var displaced = ScenePick.Nearest(
            (30, 0, 50), (30, 0, -50), instances, deformFactor: 3);
        Assert.True(displaced.Hit);
        Assert.Equal(30, displaced.World.X, 9);
        Assert.Equal(Height, displaced.World.Z, 9);

        var atOwnScale = ScenePick.Nearest((30, 0, 50), (30, 0, -50), instances);
        Assert.False(atOwnScale.Hit);
    }

    [Fact]
    public void AnUndeformedPart_IgnoresTheFactorBitForBit()
    {
        // A part with no displacement takes the incumbent path whatever the factor —
        // the exact-zero branch, asserted as identical results.
        var instances = new[] { new PickInstance(Box(), Matrix4d.Identity) };
        var plain = ScenePick.Nearest(Centre, Centre, Width, Pixels, TopDown(), instances);
        var factored = ScenePick.Nearest(
            Centre, Centre, Width, Pixels, TopDown(), instances, deformFactor: 7);
        Assert.Equal(plain, factored);
    }
}
