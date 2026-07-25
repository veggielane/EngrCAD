using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Sharp-corner rim fillets: the two quarter-cylinder bands miter on an exact ellipse.
/// These are pure-geometry checks (no tessellation) — the weld-critical property is that
/// the junction curve lies on BOTH neighbouring band surfaces to the 1e-9 weld tier.
/// </summary>
public class FilletCornerTests
{
    private static BrepSolid FilletedBox(double w, double d, double h, double r)
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h)));
        var top = box.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        return Filleting.FilletRim(box, top, r);
    }

    [Fact]
    public void BoxTopRimFillet_HasFourMiterEllipsesAndValidTopology()
    {
        var solid = FilletedBox(4, 3, 2, 0.4);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        // bottom + 4 sides + shrunk top + 4 bands.
        Assert.Equal(10, solid.Faces.Count());
        var junctions = solid.Edges.Where(e => e.Curve is Ellipse3d).ToList();
        Assert.Equal(4, junctions.Count);

        foreach (var junction in junctions)
        {
            var ellipse = (Ellipse3d)junction.Curve;
            // Semi-axes: the fillet radius vertically, r / cos(turn/2) = r·sqrt(2) at a
            // right-angle corner, and exactly perpendicular (the drop is straight down
            // while the miter offset is horizontal).
            Assert.Equal(0.4, ellipse.SemiAxisX.Length, 12);
            Assert.Equal(0.4 * Math.Sqrt(2), ellipse.SemiAxisY.Length, 12);
            Assert.Equal(0, ellipse.SemiAxisX.Dot(ellipse.SemiAxisY), 12);
            Assert.Equal(0, junction.Domain.Start, 12);
            Assert.Equal(Math.PI / 2, junction.Domain.End, 12);
        }
    }

    [Fact]
    public void MiterEllipse_LiesOnBothBandSurfaces()
    {
        var solid = FilletedBox(4, 3, 2, 0.4);
        foreach (var junction in solid.Edges.Where(e => e.Curve is Ellipse3d))
        {
            var faces = solid.FacesOf(junction);
            Assert.Equal(2, faces.Count);
            Assert.All(faces, f => Assert.IsType<ExtrudedSurface>(f.Surface));

            for (int i = 0; i <= 16; i++)
            {
                var point = junction.Curve.PointAt(junction.Domain.ParameterAt(i / 16.0));
                foreach (var face in faces)
                {
                    Assert.True(face.Surface.TryProjectPoint(point, out var uv, 1e-9),
                        $"miter sample {point} left a band surface");
                    // Inside the surface's own domain: the band must SPAN the miter, or
                    // its domain-driven tessellation grid would stop short of it.
                    Assert.True(face.Surface.DomainU.Contains(uv.X, Tolerance.Default));
                    Assert.True(face.Surface.DomainV.Contains(uv.Y, Tolerance.Default));
                    Assert.True(face.Surface.PointAt(uv.X, uv.Y).DistanceTo(point) < 1e-9);
                }
            }
        }
    }

    [Fact]
    public void MiterCorner_JoinsTheTopAndBottomRimsExactly()
    {
        double r = 0.4;
        var solid = FilletedBox(4, 3, 2, r);
        foreach (var junction in solid.Edges.Where(e => e.Curve is Ellipse3d))
        {
            var top = junction.Curve.PointAt(junction.Domain.Start);
            var bottom = junction.Curve.PointAt(junction.Domain.End);
            Assert.True(top.DistanceTo(junction.StartVertex.Position) < 1e-12);
            Assert.True(bottom.DistanceTo(junction.EndVertex.Position) < 1e-12);
            // The top end sits in the shrunk top plane, the bottom end one radius below.
            Assert.Equal(2.0, top.Z, 12);
            Assert.Equal(2.0 - r, bottom.Z, 12);
            // Right-angle corner: the miter runs r·sqrt(2) horizontally.
            Assert.Equal(r * Math.Sqrt(2), new Vector3d(bottom.X - top.X, bottom.Y - top.Y, 0).Length, 12);
        }
    }

    [Fact]
    public void ReflexCorner_MitersToo()
    {
        // L-shaped plate: (0,0)-(3,0)-(3,1)-(1,1)-(1,3)-(0,3). The vertex at (1,1) turns
        // the wrong way, so the band must reach PAST its edge's end to meet the miter —
        // the extended-extent branch of the band surface.
        Vector2d[] corners =
        [
            new(0, 0), new(3, 0), new(3, 1), new(1, 1), new(1, 3), new(0, 3),
        ];
        var outer = Profile.FromLoop(corners,
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var plate = SolidFactory.Extrude(outer, Vector3d.UnitZ);
        var top = plate.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

        var solid = Filleting.FilletRim(plate, top, 0.2);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(6, solid.Edges.Count(e => e.Curve is Ellipse3d));

        foreach (var junction in solid.Edges.Where(e => e.Curve is Ellipse3d))
        {
            foreach (var face in solid.FacesOf(junction))
            {
                for (int i = 0; i <= 8; i++)
                {
                    var point = junction.Curve.PointAt(junction.Domain.ParameterAt(i / 8.0));
                    Assert.True(face.Surface.TryProjectPoint(point, out var uv, 1e-9));
                    Assert.True(face.Surface.DomainV.Contains(uv.Y, Tolerance.Default),
                        "a reflex-corner band must span its miter");
                    Assert.True(face.Surface.PointAt(uv.X, uv.Y).DistanceTo(point) < 1e-9);
                }
            }
        }
    }

    [Fact]
    public void SharpCornerAtAnArc_IsRejected()
    {
        // A quarter-round plate: two straight edges and one arc, meeting sharply. The
        // blend there would be a torus against a cylinder — not a conic.
        var arc = new CurveSegment(
            new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 2), 0, Math.PI / 2);
        var profile = new Profile(
        [
            new Line3d((0, 0, 0), (2, 0, 0)),
            arc,
            new Line3d((0, 2, 0), (0, 0, 0)),
        ]);
        var wedge = SolidFactory.Extrude(profile, Vector3d.UnitZ);
        var top = wedge.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

        var exception = Assert.Throws<NotSupportedException>(() => Filleting.FilletRim(wedge, top, 0.2));
        Assert.Contains("sharp corner at an arc", exception.Message);
    }

    [Fact]
    public void RadiusThatConsumesAnEdge_IsRejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => FilletedBox(4, 3, 2, 1.5));
        Assert.Contains("consumes the edge", exception.Message);
    }
}
