using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Planar cross-sections (<c>projection(cut = true)</c>) through both routes. Curved
/// sections come out as INSCRIBED polygons, so every curved assertion brackets the analytic
/// value from below by the discretization rather than guessing a tolerance.
/// </summary>
public class PlanarSectionTests
{
    private static Frame3d PlaneAtZ(double z) =>
        Frame3d.FromOrthonormal((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);

    private static double TotalArea(IReadOnlyList<Region2d> regions) => regions.Sum(r => r.Area);

    /// <summary>Area of a regular polygon inscribed in a circle of the given radius whose
    /// chords stay within <paramref name="tolerance"/> of it — the most a section of that
    /// circle can lose. A chord of sagitta s on radius r sits at distance r − s, so the
    /// polygon contains the disk of radius r − s.</summary>
    private static double InscribedDiskAreaAtLeast(double radius, double tolerance) =>
        Math.PI * (radius - tolerance) * (radius - tolerance);

    // ---- straight sections are exact through the B-Rep route ----

    [Fact]
    public void BoxSection_IsTheExactRectangle()
    {
        var box = SolidFactory.MakeBox(new Aabb((-5, -3, -2), (5, 3, 2)));

        var section = PlanarSection.OfSolid(box, PlaneAtZ(0));

        var rectangle = Assert.Single(section);
        Assert.Equal(60.0, rectangle.Area, 12);
        Assert.Equal(4, rectangle.Outer.Count);     // four corners, not eight
        Assert.Empty(rectangle.Holes);
        Assert.Equal(-5.0, rectangle.Bounds.Min.X, 12);
        Assert.Equal(3.0, rectangle.Bounds.Max.Y, 12);
        Assert.True(rectangle.IsCounterClockwise);
    }

    [Fact]
    public void BoxSection_IsTheSameArrayFromTheMesh()
    {
        var box = SolidFactory.MakeBox(new Aabb((-5, -3, -2), (5, 3, 2)));

        var fromBrep = PlanarSection.OfSolid(box, PlaneAtZ(0.5));
        var fromMesh = PlanarSection.OfMesh(BRepTessellator.Tessellate(box), PlaneAtZ(0.5));

        Assert.Equal(60.0, TotalArea(fromBrep), 12);
        Assert.Equal(60.0, TotalArea(fromMesh), 12);   // planar faces tessellate exactly
    }

    [Fact]
    public void SectionOfATiltedPlane_UsesThePlanesOwnCoordinates()
    {
        // A unit cube cut by x + z = 0.1 (a 45-degree plane, deliberately off the corners
        // so no edge lies in it). The cut runs from (-0.4, 0.5) to (0.5, -0.4) in XZ, so
        // the section is a 0.9*sqrt(2) by 1 rectangle in the plane's own axes.
        var cube = SolidFactory.MakeBox(new Aabb((-0.5, -0.5, -0.5), (0.5, 0.5, 0.5)));
        var plane = Frame3d.FromOrthonormal(
            (0.1, 0, 0), Vector3d.UnitY, new Vector3d(1, 0, 1).Normalized());

        var section = Assert.Single(PlanarSection.OfSolid(cube, plane));

        Assert.Equal(0.9 * Math.Sqrt(2), section.Area, 9);
        Assert.Equal(4, section.Outer.Count);
    }

    // ---- curved sections: inscribed, bracketed by the chord tolerance ----

    [Fact]
    public void CylinderSection_IsAnInscribedCircle()
    {
        const double radius = 4, tolerance = 1e-3;
        var cylinder = SolidFactory.MakeCylinder(radius, 6);

        var section = Assert.Single(PlanarSection.OfSolid(cylinder, PlaneAtZ(3), tolerance));

        Assert.InRange(section.Area, InscribedDiskAreaAtLeast(radius, tolerance), Math.PI * radius * radius);
        Assert.Empty(section.Holes);
    }

    [Fact]
    public void CylinderSection_IsSmootherFromTheBRepThanFromTheMesh()
    {
        // The whole point of the B-Rep route: fidelity comes from the chord tolerance, not
        // from whatever tessellation the display happens to use.
        const double radius = 4;
        var cylinder = SolidFactory.MakeCylinder(radius, 6);
        var mesh = BRepTessellator.Tessellate(cylinder);

        var fromBrep = Assert.Single(PlanarSection.OfSolid(cylinder, PlaneAtZ(3), 1e-4));
        var fromMesh = Assert.Single(PlanarSection.OfMesh(mesh, PlaneAtZ(3)));

        double exact = Math.PI * radius * radius;
        Assert.True(exact - fromBrep.Area < exact - fromMesh.Area,
            "the exact route must lose less area than the tessellated one");
        Assert.True(fromBrep.Outer.Count > fromMesh.Outer.Count);
    }

    [Fact]
    public void SphereSection_WorksOnPoleBoundedFaces()
    {
        // The north hemisphere is one face whose only rim is BELOW the cut, so a one-sided
        // upward parity ray sees no crossing at all: this is the case that used to return
        // an empty section.
        const double radius = 5, height = 2, tolerance = 1e-3;
        var sphere = SolidFactory.MakeSphere(radius);
        double sectionRadius = Math.Sqrt(radius * radius - height * height);

        var section = Assert.Single(PlanarSection.OfSolid(sphere, PlaneAtZ(height), tolerance));

        Assert.InRange(section.Area,
            InscribedDiskAreaAtLeast(sectionRadius, tolerance), Math.PI * sectionRadius * sectionRadius);
    }

    // ---- nesting is detected, not declared ----

    [Fact]
    public void DrilledPlateSection_HasTheBoreAsAHole()
    {
        const double bore = 2.5, tolerance = 1e-3;
        var plate = SolidFactory.MakeBox(new Aabb((-10, -6, 0), (10, 6, 4)));
        var tool = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, bore), Vector3d.UnitZ * 6);
        var drilled = BrepBoolean.Difference(plate, tool);

        var section = Assert.Single(PlanarSection.OfSolid(drilled, PlaneAtZ(2), tolerance));

        Assert.Single(section.Holes);
        Assert.Equal(4, section.Outer.Count);
        // The boundary is exact; the bore is inscribed, so the plate keeps slightly MORE
        // area than the analytic value, by at most the hole's own flattening deficit.
        double exact = 20 * 12 - Math.PI * bore * bore;
        Assert.InRange(section.Area, exact, 20 * 12 - InscribedDiskAreaAtLeast(bore, tolerance));
    }

    [Fact]
    public void TorusSection_ThroughItsHole_IsAnAnnulus()
    {
        const double major = 6, minor = 2, tolerance = 1e-3;
        var torus = SolidFactory.MakeTorus(major, minor);

        var section = Assert.Single(PlanarSection.OfSolid(torus, PlaneAtZ(0.5), tolerance));

        // A plane perpendicular to the torus axis cuts an annulus; the radii come from the
        // minor circle at that height.
        double half = Math.Sqrt(minor * minor - 0.25);
        var hole = Assert.Single(section.Holes);
        Assert.True(hole.Count > 4);
        double exact = Math.PI * ((major + half) * (major + half) - (major - half) * (major - half));
        Assert.InRange(section.Area, exact * (1 - 1e-3), exact);
    }

    [Fact]
    public void SectionOfTwoSeparateBodies_IsTwoRegions()
    {
        var left = SolidFactory.MakeBox(new Aabb((-8, -2, -2), (-4, 2, 2)));
        var right = SolidFactory.MakeBox(new Aabb((4, -2, -2), (8, 2, 2)));
        var pair = BrepBoolean.Union(left, right);

        var section = PlanarSection.OfSolid(pair, PlaneAtZ(0));

        Assert.Equal(2, section.Count);
        Assert.Equal(2 * 4 * 4.0, TotalArea(section), 9);
    }

    // ---- misses and degeneracies ----

    [Fact]
    public void APlaneThatMissesTheSolid_ReturnsNothing()
    {
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, -1), (1, 1, 1)));

        Assert.Empty(PlanarSection.OfSolid(box, PlaneAtZ(5)));
        Assert.Empty(PlanarSection.OfMesh(BRepTessellator.Tessellate(box), PlaneAtZ(5)));
    }

    [Fact]
    public void APlaneFlushWithAFace_IsRefused()
    {
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, -1), (1, 1, 1)));

        var error = Assert.Throws<NotSupportedException>(() => PlanarSection.OfSolid(box, PlaneAtZ(1)));
        Assert.Contains("flush", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- silhouettes: projection(cut = false) ----

    [Fact]
    public void BoxSilhouette_IsTheExactFootprint()
    {
        var box = SolidFactory.MakeBox(new Aabb((-5, -3, -2), (5, 3, 2)));

        var outline = Assert.Single(
            PlanarSection.SilhouetteOfMesh(BRepTessellator.Tessellate(box), PlaneAtZ(0)));

        Assert.Equal(60.0, outline.Area, 12);
        Assert.Equal(4, outline.Outer.Count);   // the top face's two triangles merged back
        Assert.Empty(outline.Holes);
    }

    [Fact]
    public void SphereSilhouette_ConvergesToItsGreatCircleFromBelow()
    {
        const double radius = 5;
        var sphere = SolidFactory.MakeSphere(radius);
        double exact = Math.PI * radius * radius;

        var coarse = Assert.Single(
            PlanarSection.SilhouetteOfMesh(BRepTessellator.Tessellate(sphere, 16, 12), PlaneAtZ(0)));
        var fine = Assert.Single(
            PlanarSection.SilhouetteOfMesh(BRepTessellator.Tessellate(sphere, 64, 48), PlaneAtZ(0)));

        Assert.True(coarse.Area < fine.Area, "refining the mesh can only add outline area");
        Assert.True(fine.Area < exact, "an inscribed mesh never exceeds the true disk");
        Assert.InRange(fine.Area, exact * 0.998, exact);
        Assert.Equal(64, fine.Outer.Count);
    }

    [Fact]
    public void SilhouetteOfADrilledPlate_KeepsATHROUGHHoleAndDropsABlindOne()
    {
        var plate = SolidFactory.MakeBox(new Aabb((-10, -6, 0), (10, 6, 4)));
        var through = SolidFactory.Extrude(
            Profile.Circle((-5, 0, -1), Vector3d.UnitX, Vector3d.UnitY, 2), Vector3d.UnitZ * 6);
        var blind = SolidFactory.Extrude(
            Profile.Circle((5, 0, 2), Vector3d.UnitX, Vector3d.UnitY, 2), Vector3d.UnitZ * 3);
        var drilled = BrepBoolean.Difference(BrepBoolean.Difference(plate, through), blind);

        var outline = Assert.Single(
            PlanarSection.SilhouetteOfMesh(BRepTessellator.Tessellate(drilled), PlaneAtZ(0)));

        // Only the hole that goes all the way through interrupts the shadow.
        var hole = Assert.Single(outline.Holes);
        Assert.True(Region2d.SignedArea(hole) < 0);
        Assert.InRange(outline.Area, 20 * 12 - Math.PI * 4, 20 * 12);
        Assert.True(outline.Contains(new Vector2d(5, 0)), "the blind pocket casts no hole");
        Assert.False(outline.Contains(new Vector2d(-5, 0)));
    }

    [Fact]
    public void TorusSilhouette_AlongTheAxisIsAnAnnulus_AcrossTheAxisIsSolid()
    {
        const double major = 6, minor = 2;
        var torus = BRepTessellator.Tessellate(SolidFactory.MakeTorus(major, minor), 64, 48);

        var down = Assert.Single(PlanarSection.SilhouetteOfMesh(torus, PlaneAtZ(0)));
        var across = Assert.Single(PlanarSection.SilhouetteOfMesh(
            torus, Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ)));

        Assert.Single(down.Holes);
        double annulus = Math.PI * ((major + minor) * (major + minor) - (major - minor) * (major - minor));
        Assert.InRange(down.Area, annulus * 0.99, annulus);

        // Side on, the SMOOTH outline is {|z| <= r, |x| <= R + sqrt(r^2 - z^2)}: a rectangle
        // with two half-discs on the ends, and no hole at all. The tessellated body's own
        // shadow does have one, of ~2.4e-7 of the outline -- and it is REAL GEOMETRY, not
        // the boolean instability this was long filed as. In the band
        // |z| in [r*cos(pi/48), r] the discrete tube only reaches that height near its
        // minor-polygon vertices, and the major discretization breaks that thin band into
        // lenses that need not overlap. So the assertion is the strong one: whatever holes
        // come back are covered by NO facet, i.e. the boolean returned the correct union of
        // what it was given.
        double sideOn = 4 * major * minor + Math.PI * minor * minor;
        double pinholes = across.Holes.Sum(h => Math.Abs(Region2d.SignedArea(h)));
        Assert.True(pinholes < 1e-5 * across.Area, $"unexpected hole area {pinholes} in the side-on outline");
        Assert.InRange(across.Area, sideOn * 0.98, sideOn);

        var view = Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ);
        foreach (var hole in across.Holes)
            AssertUncoveredByEveryFacet(torus, view, hole);
    }

    /// <summary>
    /// Asserts that a silhouette hole is a genuine gap in the MESH's shadow: sampling its
    /// interior, no projected facet contains the sample — decided with the exact
    /// <see cref="Predicates2d.Orient2d"/>, over every triangle, front- and back-facing
    /// alike. That is what separates "the boolean lost material" from "the tessellation has
    /// no material there".
    /// </summary>
    private static void AssertUncoveredByEveryFacet(
        HalfEdgeMesh mesh, Frame3d view, IReadOnlyList<Vector2d> hole)
    {
        double x0 = hole.Min(p => p.X), x1 = hole.Max(p => p.X);
        double y0 = hole.Min(p => p.Y), y1 = hole.Max(p => p.Y);
        var projected = new List<(Vector2d A, Vector2d B, Vector2d C)>();
        foreach (var face in mesh.Faces)
        {
            var vs = face.Vertices().Select(v => view.ToLocal(v.Position)).ToArray();
            if (vs.Length != 3)
                continue;
            projected.Add((
                new Vector2d(vs[0].X, vs[0].Y),
                new Vector2d(vs[1].X, vs[1].Y),
                new Vector2d(vs[2].X, vs[2].Y)));
        }

        int probed = 0;
        for (int i = 1; i < 20; i++)
        for (int j = 1; j < 20; j++)
        {
            var p = new Vector2d(x0 + (x1 - x0) * i / 20.0, y0 + (y1 - y0) * j / 20.0);
            if (!Region2d.ParityInside(hole, p))
                continue;
            probed++;
            foreach (var (a, b, c) in projected)
            {
                int s0 = Math.Sign(Predicates2d.Orient2d(a, b, p));
                int s1 = Math.Sign(Predicates2d.Orient2d(b, c, p));
                int s2 = Math.Sign(Predicates2d.Orient2d(c, a, p));
                Assert.False(
                    (s0 >= 0 && s1 >= 0 && s2 >= 0) || (s0 <= 0 && s1 <= 0 && s2 <= 0),
                    $"the silhouette hole is covered by a facet at {p}: the boolean lost material");
            }
        }
        Assert.True(probed > 0, "the hole was too thin to probe");
    }

    [Fact]
    public void SilhouetteOfAnOpenMesh_KeepsEveryFace()
    {
        // Back-face dropping is exact only because a closed body's shadow is covered by its
        // front faces. An open mesh gets no such guarantee, so nothing is dropped: half a
        // box, open at the top, still casts the box's full footprint.
        var box = SolidFactory.MakeBox(new Aabb((-5, -3, -2), (5, 3, 2)));
        var openHalf = MeshPlaneCut.Cut(
            BRepTessellator.Tessellate(box), Vector3d.Zero, Vector3d.UnitZ, cap: false).Mesh;
        Assert.False(openHalf.IsClosed);

        var outline = Assert.Single(PlanarSection.SilhouetteOfMesh(openHalf, PlaneAtZ(0)));

        Assert.Equal(60.0, outline.Area, 12);
    }

    [Fact]
    public void SilhouetteOfTwoSeparateBodies_IsTwoRegions()
    {
        var pair = BrepBoolean.Union(
            SolidFactory.MakeBox(new Aabb((-8, -2, -2), (-4, 2, 2))),
            SolidFactory.MakeBox(new Aabb((4, -2, -2), (8, 2, 2))));

        var outline = PlanarSection.SilhouetteOfMesh(BRepTessellator.Tessellate(pair), PlaneAtZ(0));

        Assert.Equal(2, outline.Count);
        Assert.Equal(2 * 4 * 4.0, TotalArea(outline), 9);
    }

    [Fact]
    public void APlaneContainingAWholeEdge_IsRefused()
    {
        // The sphere's equator lies entirely in z = 0: the section would run along the
        // shared boundary of both hemispheres, where containment is a tie.
        var sphere = SolidFactory.MakeSphere(5);

        var error = Assert.Throws<NotSupportedException>(() => PlanarSection.OfSolid(sphere, PlaneAtZ(0)));
        Assert.Contains("edge", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
