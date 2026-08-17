using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Drafting a prism whose caps carry HOLES. A hole's walls are a closed RING of side faces
/// whose outward normals point INTO the hole, so the same "rotate about the neutral line"
/// rule applies to them verbatim — and the hole OPENS along the pull direction while the
/// outside closes, which is what releases a core pin.
///
/// <para>The exact positions live here; the volume identity — which is where the two rings'
/// second-order terms CANCEL — lives in Interop.Tests where a mesh is available.</para>
/// </summary>
public class HoledCapDraftTests
{
    private const double Ten = Math.PI / 18;
    private const double Tight = 1e-12;

    /// <summary>A 20 x 20 plate, 6 tall, with a 8 x 8 square hole; base at z = 0.</summary>
    private static BrepSolid HoledPlate(double outer = 20, double hole = 8, double height = 6)
    {
        double o = outer / 2, h = hole / 2;
        var plate = Profile.FromPoints([(-o, -o, 0), (o, -o, 0), (o, o, 0), (-o, o, 0)]);
        var bore = Profile.FromPoints([(-h, -h, 0), (h, -h, 0), (h, h, 0), (-h, h, 0)]);
        return SolidFactory.Extrude(plate, (0, 0, height), holes: [bore]);
    }

    [Fact]
    public void TheOutsideNarrowsAndTheHoleOPENSGoingAlongThePull()
    {
        // The whole point of drafting a hole: both walls lean so the MATERIAL narrows, which
        // means the hole itself grows. A rule that tapered the hole the same way as the
        // outside would trap the core pin.
        var drafted = Draft.Apply(HoledPlate(), Vector3d.Zero, Vector3d.UnitZ, Ten);
        drafted.Validate();
        Assert.True(drafted.SatisfiesEulerFormula(genus: 1));

        double inset = 6 * Math.Tan(Ten);
        foreach (var vertex in drafted.Vertices)
        {
            var p = vertex.Position;
            bool top = Math.Abs(p.Z - 6) < Tight;
            Assert.True(top || Math.Abs(p.Z) < Tight, $"unexpected z {p.Z}");
            double reach = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
            bool onHole = reach < 6;
            // Base corners are untouched (the neutral plane is the base); top corners moved
            // inward on the outside and OUTWARD on the hole, by exactly height*tan(angle).
            double expected = onHole
                ? 4 + (top ? inset : 0)
                : 10 - (top ? inset : 0);
            Assert.Equal(expected, reach, 9);
        }
    }

    [Fact]
    public void EverySideFaceLeansByExactlyTheDraftAngle()
    {
        // Measured off the rebuilt PLANES, both rings: the outward normal's component along
        // the pull is sin(angle) for the outside walls AND for the hole walls, which is the
        // statement that one rule served both.
        var drafted = Draft.Apply(HoledPlate(), Vector3d.Zero, Vector3d.UnitZ, Ten);
        var sides = drafted.Faces
            .Where(f => f.IsPlanar(out _, out var n) && Math.Abs(n.Normalized().Z) < 0.99)
            .ToList();
        Assert.Equal(8, sides.Count); // four outside, four around the hole
        foreach (var face in sides)
        {
            face.IsPlanar(out _, out var normal);
            Assert.Equal(Math.Sin(Ten), normal.Normalized().Z, 12);
        }
    }

    [Fact]
    public void TheCapsKeepTheirTwoLoopsAndTheSolidStaysAPrism()
    {
        var drafted = Draft.Apply(HoledPlate(), Vector3d.Zero, Vector3d.UnitZ, Ten);
        Assert.Equal(10, drafted.Faces.Count());       // 8 sides + 2 caps
        Assert.All(drafted.Faces, f => Assert.IsType<PlaneSurface>(f.Surface));
        var caps = drafted.Faces.Where(f => f.Loops.Count == 2).ToList();
        Assert.Equal(2, caps.Count);
        Assert.All(caps, c => Assert.All(c.Loops, l => Assert.Equal(4, l.Coedges.Count)));
    }

    [Fact]
    public void DraftingOnlyTheHole_LeavesTheOutsideExactlyWhereItWas()
    {
        // The per-face selector reaches a hole ring like any other: the outer walls keep
        // their own planes exactly (the corners on them do not move at all, because their
        // two neighbours did not move either).
        var plate = HoledPlate();
        var holeFaces = plate.Faces
            .Where(f => f.IsPlanar(out _, out var n)
                && Math.Abs(n.Normalized().Z) < 0.99
                && f.Bounds().Center.Length < 6)
            .ToHashSet();
        Assert.Equal(4, holeFaces.Count);

        var drafted = Draft.Apply(plate, Vector3d.Zero, Vector3d.UnitZ,
            f => holeFaces.Contains(f) ? Ten : null);
        drafted.Validate();

        double inset = 6 * Math.Tan(Ten);
        foreach (var vertex in drafted.Vertices)
        {
            var p = vertex.Position;
            double reach = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
            if (reach > 6)
                Assert.Equal(10, reach, 12);   // untouched, to the bit-ish
            else
                Assert.Equal(4 + (Math.Abs(p.Z - 6) < Tight ? inset : 0), reach, 9);
        }
    }

    [Fact]
    public void ATaperThatTurnsTheHOLEInsideOutIsRefusedByName()
    {
        // The fold check runs per RING against that ring's own winding, so a hole closing on
        // itself is caught exactly as an outer profile collapsing is. Drafting the hole the
        // WRONG way (negative) shrinks it, and 8 wide over 6 tall needs only ~34 degrees.
        var plate = HoledPlate();
        var holeFaces = plate.Faces
            .Where(f => f.IsPlanar(out _, out var n)
                && Math.Abs(n.Normalized().Z) < 0.99
                && f.Bounds().Center.Length < 6)
            .ToHashSet();
        Assert.Throws<ArgumentException>(() => Draft.Apply(plate, Vector3d.Zero, Vector3d.UnitZ,
            f => holeFaces.Contains(f) ? -0.9 * Math.PI / 2 : null));
    }

    [Fact]
    public void MismatchedCapLoopCountsAreRefusedByName()
    {
        // A prism's two caps bound the same rings; a solid whose caps disagree is not one,
        // and the message says so rather than silently pairing them by index.
        var exception = Assert.Throws<NotSupportedException>(() =>
            Draft.Apply(MismatchedCaps(), Vector3d.Zero, Vector3d.UnitZ, Ten));
        Assert.Contains("matching loop counts", exception.Message);
    }

    /// <summary>
    /// A hand-built body whose base cap carries a hole and whose top does not — enough to
    /// reach the loop-count gate, which fires before any geometry is read.
    /// </summary>
    private static BrepSolid MismatchedCaps()
    {
        // Two stacked prisms sharing a face would be a second shell; instead the mismatch is
        // stated directly by a solid whose top cap loop count differs. The simplest legal
        // input is a holed plate with one cap's loops re-declared, which cannot be built
        // through the factory — so the gate is reached via a plate whose caps genuinely
        // differ: a through hole on the base only is not a solid, so use two DIFFERENT
        // extrusions welded is also not one. Use the reachable case instead: a solid with
        // three caps is refused earlier, so drive the gate with a plate whose top cap has an
        // extra loop by construction.
        var plate = HoledPlate();
        var top = plate.Faces.First(f =>
            f.IsPlanar(out var o, out var n) && n.Normalized().Z > 0.9 && Math.Abs(o.Z - 6) < 1e-9);
        var faces = plate.Faces
            .Select(f => ReferenceEquals(f, top)
                ? new BrepFace(f.Surface, [f.Loops[0]], f.IsReversed)   // drop the hole loop
                : f)
            .ToList();
        return new BrepSolid([new BrepShell(faces)]);
    }
}
