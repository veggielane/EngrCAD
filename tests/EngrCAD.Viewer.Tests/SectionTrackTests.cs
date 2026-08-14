using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The section track — the animation system's fourth track kind, and the material-addition /
/// material-removal player: a clip plane is shader state, so a print reveal animates with no
/// re-meshing. The tests are pure track arithmetic (no GL): the reveal's endpoints hide and show
/// the whole body, the step quantization completes whole layers, the sweep is monotone, the
/// timeline wiring (one track only, window clamp, null with no track) matches the other three
/// track kinds.
/// </summary>
public sealed class SectionTrackTests
{
    [Fact]
    public void TheReveal_HidesEverythingAtZero_AndShowsEverythingAtOne()
    {
        var bounds = new Aabb((0, 0, 0), (20, 10, 8));
        var track = SectionTracks.Reveal(bounds, Vector3d.UnitZ);

        // The renderer keeps dot(world, normal) <= offset, so an offset below the body hides
        // it whole and one above shows it whole.
        var start = track.SectionsAt(0).Single();
        var end = track.SectionsAt(1).Single();
        Assert.Equal(Vector3d.UnitZ, start.Normal);
        Assert.True(start.Offset < 0);
        Assert.True(end.Offset > 8);
    }

    [Fact]
    public void TheStepQuantization_CompletesWholeLayers()
    {
        // 4 steps over offsets [0, 8] (pad ignored by using Sweep directly): any t inside a
        // step shows that step COMPLETED — ceiling, the way a printer finishes a layer.
        var track = SectionTracks.Sweep(Vector3d.UnitZ, 0, 8, steps: 4);
        Assert.Equal(0.0, track.OffsetAt(0), 12);
        Assert.Equal(2.0, track.OffsetAt(0.10), 12);
        Assert.Equal(2.0, track.OffsetAt(0.25), 12);
        Assert.Equal(4.0, track.OffsetAt(0.26), 12);
        Assert.Equal(8.0, track.OffsetAt(1), 12);

        // Monotone: material only ever appears.
        double previous = double.NegativeInfinity;
        for (int i = 0; i <= 100; i++)
        {
            double offset = track.OffsetAt(i / 100.0);
            Assert.True(offset >= previous);
            previous = offset;
        }

        // The smooth sweep is the steps-0 member.
        var smooth = SectionTracks.Sweep(Vector3d.UnitZ, 0, 8);
        Assert.Equal(4.0, smooth.OffsetAt(0.5), 12);
    }

    [Fact]
    public void TheTimelineWiring_MatchesTheOtherTrackKinds()
    {
        // No track: the sample says nothing about sections.
        Assert.Null(new Animation(1).At(0.5).Sections);

        // With a track: the sample carries its planes, eased and windowed like every track.
        var track = SectionTracks.Sweep(Vector3d.UnitZ, 0, 10);
        track.Window(0.5, 1);
        var animation = new Animation(1).With(track);
        Assert.Equal(0.0, animation.At(0.25).Sections!.Single().Offset, 12);   // before the window
        Assert.Equal(10.0, animation.At(1).Sections!.Single().Offset, 12);

        // At most one section track, the pose-track argument in miniature.
        Assert.Throws<InvalidOperationException>(() =>
            new Animation(1)
                .With(SectionTracks.Sweep(Vector3d.UnitZ, 0, 1))
                .With(SectionTracks.Sweep(Vector3d.UnitZ, 0, 2)));

        // Refusals by name: a zero normal, a zero grow direction, negative steps.
        Assert.Throws<ArgumentException>(() => SectionTracks.Sweep(Vector3d.Zero, 0, 1));
        Assert.Throws<ArgumentException>(
            () => SectionTracks.Reveal(new Aabb((0, 0, 0), (1, 1, 1)), Vector3d.Zero));
        Assert.Throws<ArgumentException>(() => SectionTracks.Sweep(Vector3d.UnitZ, 0, 1, steps: -1));
    }
}
