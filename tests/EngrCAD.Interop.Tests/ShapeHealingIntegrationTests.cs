using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Healing judged by what comes out the other end: a repaired face soup must tessellate
/// into a closed mesh and measure as the same body it started as. Structural checks live
/// in <c>EngrCAD.BRep.Tests.ShapeHealingTests</c>; this is the geometric confirmation,
/// which needs the tessellator and so lives here.
/// </summary>
public class ShapeHealingIntegrationTests
{
    /// <summary>
    /// Gives every face private copies of its vertices and edges (shared within the wire,
    /// duplicated between wires) and jitters them — a foreign STEP import in miniature.
    /// Straight edges get a genuinely new <see cref="Line3d"/> through the jittered
    /// endpoints, so the two copies of a shared edge really are different geometry;
    /// otherwise the copies would sample bit-identically and there would be nothing to heal
    /// that a tessellator does not already paper over.
    /// </summary>
    private static BrepSolid Explode(BrepSolid solid, double jitter, int seed = 7)
    {
        var random = new Random(seed);
        Vector3d Jitter(in Vector3d p) => p + new Vector3d(
            random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5)
            .Normalized() * (jitter * random.NextDouble());

        var shells = new List<BrepShell>();
        foreach (var shell in solid.Shells)
        {
            var faces = new List<BrepFace>();
            foreach (var face in shell.Faces)
            {
                var copies = new Dictionary<BrepVertex, BrepVertex>();
                BrepVertex Copy(BrepVertex v)
                {
                    if (!copies.TryGetValue(v, out var copy))
                        copies[v] = copy = new BrepVertex(Jitter(v.Position));
                    return copy;
                }

                var loops = new List<BrepLoop>();
                foreach (var loop in face.Loops)
                {
                    var coedges = new List<BrepCoedge>();
                    foreach (var coedge in loop.Coedges)
                    {
                        var edge = coedge.Edge;
                        var start = Copy(edge.StartVertex);
                        var end = edge.IsClosedEdge ? start : Copy(edge.EndVertex);
                        var curve = edge.Curve is Line3d
                            ? new Line3d(start.Position, end.Position)
                            : edge.Curve;
                        var domain = edge.Curve is Line3d ? Interval.Unit : edge.Domain;
                        coedges.Add(new BrepCoedge(new BrepEdge(curve, domain, start, end), coedge.SameSense));
                    }
                    loops.Add(new BrepLoop(coedges));
                }
                faces.Add(new BrepFace(face.Surface, loops, face.IsReversed));
            }
            shells.Add(new BrepShell(faces));
        }
        return new BrepSolid(shells);
    }

    [Fact]
    public void HealedFaceSoup_ReproducesTheOriginalBodyExactly_WhenNothingWasDisplaced()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 5)));
        var expected = BrepMassProperties.Compute(box, 2.5);

        var healed = ShapeHealing.Heal(Explode(box, jitter: 0));
        Assert.True(healed.Report.IsManifold, healed.Report.ToString());

        var actual = BrepMassProperties.Compute(healed.Solid, 2.5);
        Assert.Equal(expected.Volume, actual.Volume, 12);
        Assert.Equal(expected.SurfaceArea, actual.SurfaceArea, 12);
        Assert.Equal(expected.Inertia.Zz, actual.Inertia.Zz, 12);
        Assert.True(expected.Centroid.DistanceTo(actual.Centroid) < 1e-12, "centroid moved");
        Assert.True(BRepTessellator.Tessellate(healed.Solid).IsClosed);
    }

    [Fact]
    public void HealedFaceSoup_LandsWithinTheGapToleranceOfTheOriginalBody()
    {
        const double jitter = 3e-8;
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 5)));
        var expected = BrepMassProperties.Compute(box, 1.0);

        var healed = ShapeHealing.Heal(Explode(box, jitter), new ShapeHealingOptions { RefitStraightEdges = true });
        Assert.True(healed.Report.IsManifold, healed.Report.ToString());
        Assert.True(healed.Report.EdgesRefit > 0, "the wires needed geometric closing, not just sewing");

        // Healing keeps ONE of each pair of duplicate curves and refits it through the
        // unified vertices, so the healed body is the soup's own geometry made consistent —
        // displaced from the ideal box by at most the jitter. The volume slack is therefore
        // bounded by jitter x surface area, not by zero, and claiming otherwise would be the
        // dishonest version of this test.
        var actual = BrepMassProperties.Compute(healed.Solid, 1.0);
        double slack = 2 * jitter * expected.SurfaceArea;
        Assert.True(Math.Abs(actual.Volume - expected.Volume) < slack,
            $"Healed volume {actual.Volume:G17} against {expected.Volume:G17}; slack {slack:G3}.");
        Assert.True(actual.Centroid.DistanceTo(expected.Centroid) < 2 * jitter, "centroid moved past the jitter");
        Assert.True(BRepTessellator.Tessellate(healed.Solid).IsClosed);
    }

    [Fact]
    public void UnhealedFaceSoup_TessellatesOpen()
    {
        // The motivation, stated as a test: the tessellator welds at the 1e-9 weld tier, so
        // a soup whose duplicate edges differ by 3e-8 comes out cracked. Healing is what
        // closes it — see the two tests above.
        var soup = Explode(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 5))), jitter: 3e-8);
        Assert.False(BRepTessellator.Tessellate(soup).IsClosed);
    }

    [Fact]
    public void HealedCurvedSoup_KeepsItsVolume()
    {
        const double r = 2, h = 7;
        var cylinder = SolidFactory.MakeCylinder(r, h);
        var soup = Explode(cylinder, jitter: 2e-8);

        var healed = ShapeHealing.Heal(soup);
        Assert.True(healed.Report.IsManifold, healed.Report.ToString());
        healed.Solid.Validate();

        var mp = BrepMassProperties.Compute(healed.Solid);
        double exact = Math.PI * r * r * h;
        Assert.True(Math.Abs(mp.Volume - exact) / exact < 1e-6,
            $"Healed cylinder volume {mp.Volume:G10} against {exact:G10}.");
        Assert.True(BRepTessellator.Tessellate(healed.Solid).IsClosed);
    }
}
