using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Whole-solid filleting: the exact convex opening, whose vertices are the spherical
/// corner patches rim surgery cannot build.
/// </summary>
public class FilletAllEdgesTests
{
    private static BrepSolid Box(double w = 4, double d = 3, double h = 2) =>
        SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h)));

    /// <summary>A convex polyhedron from positions and outward-CCW index loops.</summary>
    private static BrepSolid Polyhedron(IReadOnlyList<Vector3d> positions, IReadOnlyList<int[]> loops)
    {
        var vertices = positions.Select(p => new BrepVertex(p)).ToArray();
        var edges = new Dictionary<(int, int), BrepEdge>();
        var faces = new List<BrepFace>();
        foreach (var loop in loops)
        {
            var coedges = new List<BrepCoedge>(loop.Length);
            for (int i = 0; i < loop.Length; i++)
            {
                int a = loop[i], b = loop[(i + 1) % loop.Length];
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var edge))
                {
                    edge = new BrepEdge(
                        new Line3d(vertices[key.Item1].Position, vertices[key.Item2].Position),
                        Interval.Unit, vertices[key.Item1], vertices[key.Item2]);
                    edges[key] = edge;
                }
                coedges.Add(new BrepCoedge(edge, key.Item1 == a));
            }
            var p0 = positions[loop[0]];
            var normal = (positions[loop[1]] - p0).Cross(positions[loop[2]] - p0).Normalized();
            var x = (positions[loop[1]] - p0).Normalized();
            faces.Add(new BrepFace(new PlaneSurface(p0, x, normal.Cross(x)), [new BrepLoop(coedges)]));
        }
        return new BrepSolid([new BrepShell(faces)]);
    }

    [Fact]
    public void FilletedBox_HasSixFacesTwelveBandsAndEightSphericalPatches()
    {
        var rounded = Filleting.FilletAllEdges(Box(), 0.4);
        rounded.Validate();
        Assert.True(rounded.SatisfiesEulerFormula(genus: 0));

        Assert.Equal(26, rounded.Faces.Count());
        Assert.Equal(48, rounded.Edges.Count());
        Assert.Equal(24, rounded.Vertices.Count());
        Assert.Equal(6, rounded.Faces.Count(f => f.Surface is PlaneSurface));
        Assert.Equal(12, rounded.Faces.Count(f => f.Surface is ExtrudedSurface));
        Assert.Equal(8, rounded.Faces.Count(f => f.Surface is RevolvedSurface));
        // Each corner patch is a spherical triangle: three great-circle arcs.
        Assert.All(rounded.Faces.Where(f => f.Surface is RevolvedSurface),
            f => Assert.Equal(3, f.OuterLoop.Coedges.Count));
    }

    [Fact]
    public void CornerPatchesAreSpheresOfTheFilletRadiusOnTheErodedVertices()
    {
        double r = 0.4;
        var rounded = Filleting.FilletAllEdges(Box(4, 3, 2), r);
        var centres = new List<Vector3d>();
        foreach (var patch in rounded.Faces.Where(f => f.Surface is RevolvedSurface))
        {
            var revolved = (RevolvedSurface)patch.Surface;
            centres.Add(revolved.AxisOrigin);
            // Every boundary sample sits exactly one radius from the eroded vertex.
            foreach (var coedge in patch.OuterLoop.Coedges)
            {
                var domain = coedge.Edge.Domain;
                for (int i = 0; i <= 8; i++)
                {
                    var point = coedge.Edge.Curve.PointAt(domain.ParameterAt(i / 8.0));
                    Assert.Equal(r, point.DistanceTo(revolved.AxisOrigin), 12);
                }
            }
        }
        // The eroded vertices are the box corners pulled in by r along all three axes.
        Assert.Equal(8, centres.Count);
        foreach (var centre in centres)
        {
            Assert.True(Math.Abs(centre.X - r) < 1e-12 || Math.Abs(centre.X - (4 - r)) < 1e-12);
            Assert.True(Math.Abs(centre.Y - r) < 1e-12 || Math.Abs(centre.Y - (3 - r)) < 1e-12);
            Assert.True(Math.Abs(centre.Z - r) < 1e-12 || Math.Abs(centre.Z - (2 - r)) < 1e-12);
        }
    }

    [Fact]
    public void BandsAndPatchesShareTheirArcsExactly()
    {
        var rounded = Filleting.FilletAllEdges(Box(), 0.4);
        var arcs = rounded.Edges.Where(e => e.Curve.Underlying is Circle3d).ToList();
        Assert.Equal(24, arcs.Count); // three per corner

        foreach (var arc in arcs)
        {
            var faces = rounded.FacesOf(arc);
            Assert.Equal(2, faces.Count);
            // One cylindrical band and one spherical patch, and the arc lies on both.
            Assert.Single(faces, f => f.Surface is ExtrudedSurface);
            Assert.Single(faces, f => f.Surface is RevolvedSurface);
            for (int i = 0; i <= 8; i++)
            {
                var point = arc.Curve.PointAt(arc.Domain.ParameterAt(i / 8.0));
                foreach (var face in faces)
                {
                    Assert.True(face.Surface.TryProjectPoint(point, out var uv, 1e-9));
                    Assert.True(face.Surface.PointAt(uv.X, uv.Y).DistanceTo(point) < 1e-9);
                }
            }
        }
    }

    [Fact]
    public void ShearedBoxWorks_OneFacePerCornerIsStillPerpendicular()
    {
        var profile = Profile.FromLoop(
            [new Vector2d(0, 0), new Vector2d(3, 0), new Vector2d(3, 2), new Vector2d(0, 2)],
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var sheared = SolidFactory.Extrude(profile, new Vector3d(0.6, 0, 2));
        var rounded = Filleting.FilletAllEdges(sheared, 0.25);
        rounded.Validate();
        Assert.True(rounded.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(26, rounded.Faces.Count());
    }

    [Fact]
    public void EveryLoopPointProjectsInsideItsFaceDomain()
    {
        // The invariant tessellation and face splitting both rely on: a generated
        // surface's grid is domain-driven, so a loop that strays outside the domain
        // silently meshes the wrong region. Checked on the bands (extruded arcs) and the
        // corner patches (partial revolutions) alike.
        var rounded = Filleting.FilletAllEdges(Box(), 0.4);
        foreach (var face in rounded.Faces)
        {
            foreach (var coedge in face.OuterLoop.Coedges)
            {
                var domain = coedge.Edge.Domain;
                for (int i = 0; i <= 12; i++)
                {
                    var point = coedge.Edge.Curve.PointAt(domain.ParameterAt(i / 12.0));
                    Assert.True(
                        face.Surface.TryProjectPoint(point, out var uv, FaceGeometry.InverseEvaluationTolerance),
                        $"{face.Surface.GetType().Name}: {point} is not on the face's surface");
                    Assert.True(face.Surface.DomainU.Contains(uv.X, Tolerance.Default));
                    Assert.True(face.Surface.DomainV.Contains(uv.Y, Tolerance.Default));
                }
            }
        }
    }

    [Fact]
    public void ConcaveEdges_AreRefused()
    {
        // L-shaped prism: the reflex vertical edge cannot be rounded by an opening.
        var profile = Profile.FromLoop(
            [new Vector2d(0, 0), new Vector2d(3, 0), new Vector2d(3, 1),
             new Vector2d(1, 1), new Vector2d(1, 3), new Vector2d(0, 3)],
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var ell = SolidFactory.Extrude(profile, Vector3d.UnitZ);
        var exception = Assert.Throws<NotSupportedException>(() => Filleting.FilletAllEdges(ell, 0.2));
        Assert.Contains("not convex", exception.Message);
    }

    [Fact]
    public void GeneralTrihedralCorner_IsRefusedWithTheReason()
    {
        // A tetrahedron: convex, three-valent, all faces planar — but no face is
        // perpendicular to its two neighbours, so the spherical triangle is general.
        Vector3d[] positions = [(0, 0, 0), (2, 0, 0), (0, 2, 0), (0, 0, 2)];
        int[][] loops = [[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]];
        var tetrahedron = Polyhedron(positions, loops);
        tetrahedron.Validate();

        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletAllEdges(tetrahedron, 0.2));
        Assert.Contains("perpendicular", exception.Message);
    }

    [Fact]
    public void FourValentVertices_AreRefused()
    {
        // A square pyramid: its apex joins four faces.
        Vector3d[] positions = [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), (1, 1, 2)];
        int[][] loops = [[0, 3, 2, 1], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]];
        var pyramid = Polyhedron(positions, loops);
        pyramid.Validate();

        var exception = Assert.Throws<NotSupportedException>(() => Filleting.FilletAllEdges(pyramid, 0.2));
        Assert.Contains("three-valent", exception.Message);
    }

    [Fact]
    public void RadiusLargerThanTheSolid_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Filleting.FilletAllEdges(Box(4, 3, 2), 1.6));
        Assert.Throws<ArgumentOutOfRangeException>(() => Filleting.FilletAllEdges(Box(), -1));
    }
}
