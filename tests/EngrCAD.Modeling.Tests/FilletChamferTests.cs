using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class FilletChamferTests
{
    private static Func<BrepSolid, IEnumerable<BrepFace>> Top =>
        s => s.PlanarFacesWithNormal(Vector3d.UnitZ);

    [Fact]
    public void BoxTopRimChamfer_ExactVolume()
    {
        // 45° wedges of cross-section c²/2 along each top edge, minus the c³/3 overlap
        // at each corner (∫₀ᶜ (c−z)² dz): V = w·d·h − c²(w+d) + 4c³/3, exact because
        // every face is planar.
        double w = 4, d = 3, h = 2, c = 0.4;
        var shape = Shape.Box(w, d, h).Chamfer(c, Top);
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        double exact = w * d * h - c * c * (w + d) + 4 * c * c * c / 3;
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void CylinderTopRimChamferAndFillet_Exact()
    {
        int n = 256;
        // The tessellated solid of revolution is the inscribed n-gon version: its
        // volume is ∫ k·π·r(z)² dz with k = (n/2π)·sin(2π/n).
        double R = 1, h = 1, c = 0.25;
        double k = n / (2 * Math.PI) * Math.Sin(2 * Math.PI / n);

        var chamfered = Shape.Cylinder(R, h).Chamfer(c, Top).ToBrep();
        chamfered.Validate();
        var mesh = BRepTessellator.Tessellate(chamfered, n, 24);
        Assert.True(mesh.IsClosed);
        double kept = 0;
        int slices = 20000;
        for (int i = 0; i < slices; i++)
        {
            double z = (i + 0.5) / slices * h;
            double radius = z > h - c ? R - (z - (h - c)) : R;
            kept += Math.PI * radius * radius * (h / slices);
        }
        kept *= k;
        Assert.True(Math.Abs(mesh.Volume() - kept) / kept < 1e-4, $"chamfer {mesh.Volume()} vs {kept}");

        // Fillet (routes to the exact quarter-torus FilletEdge path, generalized to
        // extruded bands): keep the existing behavior working through Shape.
        var filleted = Shape.Cylinder(R, h).Fillet(0.3, Top).ToBrep();
        filleted.Validate();
        Assert.True(BRepTessellator.Tessellate(filleted, 128, 24).IsClosed);
    }

    [Fact]
    public void RoundedPlateTopRimFillet_MatchesSliceIntegral()
    {
        // Extruded rounded rectangle (G1 rim), filleted on top: slice at depth t has
        // straight boundaries inset δ(t) = r − √(r²−t²) and corner radius ρ₀ + (r−δ)
        // … with corner arcs the offset keeps G1, so the slice is the base rounded
        // rect shrunk by δ with corner radius ρ₀ − δ + … — for offset curves of a
        // rounded rectangle, area(δ) = A₀ − P₀·δ + π·δ² (offset-polygon formula with
        // rounded corners), giving an exact integral to compare against.
        double w = 4, d = 3, rho = 0.5, h = 1, r = 0.3;
        int n = 128;
        var sketch = Sketch.RoundedRectangle(w, d, rho);
        var shape = Shape.Extrude(sketch, h).Fillet(r, Top);
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, n, 48);
        Assert.True(mesh.IsClosed, "filleted plate should tessellate closed");

        // Tessellation-aware slice areas: straight runs exact, arcs inscribed. For an
        // inward offset δ of a rounded rectangle: straight length unchanged, corner
        // radius ρ−δ. A(δ) = (w−2δ)(d−2δ) − (4−π_n)(ρ−δ)² with π_n the inscribed
        // polygon factor for quarter arcs sampled by the tessellator. Using true π
        // keeps the comparison within tessellation error at n=128 (<0.2%).
        double kept = 0;
        int slices = 4000;
        for (int i = 0; i < slices; i++)
        {
            double z = (i + 0.5) / slices * h;
            double t = z - (h - r);
            double delta = t <= 0 ? 0 : r - Math.Sqrt(Math.Max(0, r * r - t * t));
            double cw = w - 2 * delta, cd = d - 2 * delta, cr = rho - delta;
            kept += (cw * cd - (4 - Math.PI) * cr * cr) * (h / slices);
        }
        Assert.True(Math.Abs(mesh.Volume() - kept) / kept < 0.005,
            $"fillet volume {mesh.Volume()} vs integral {kept}");
    }

    [Fact]
    public void SharpCornerFillet_MitersOnExactEllipses()
    {
        // The corners of a box top are sharp: the two quarter-cylinder bands miter on the
        // exact bicylinder ellipse. Volume by the offset-polygon law (see
        // Interop's FilletCornerVolumeTests for the derivation).
        double w = 2, d = 2, h = 1, r = 0.2;
        var solid = Shape.Box(w, d, h).Fillet(r, Top).ToBrep();
        solid.Validate();
        Assert.Equal(4, solid.Edges.Count(e => e.Curve is Ellipse3d));

        var mesh = BRepTessellator.Tessellate(solid, 64, 32);
        Assert.True(mesh.IsClosed);
        double exact = w * d * h
            - 2 * (w + d) * r * r * (1 - Math.PI / 4)
            + 4 * r * r * r * (5.0 / 3 - Math.PI / 2);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 3e-4,
            $"filleted box volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void SharpCornerAtAnArc_StillThrowsWithGuidance()
    {
        // A slot's straight side meets its end arc tangentially, so a slot fillets fine;
        // a sketch whose arc meets a line at an angle has no exact blend there.
        var sketch = Sketch.Start(0, 0).LineTo(2, 0).ArcTo(new Vector2d(0, 2), 2, clockwise: false).Close();
        var shape = Shape.Extrude(sketch, 1).Fillet(0.2, Top);
        var exception = Assert.Throws<NotSupportedException>(() => shape.ToBrep());
        Assert.Contains("sharp corner at an arc", exception.Message);
    }

    [Fact]
    public void ChamferAtAngle_ExactVolume()
    {
        // Setback a in the top face, angle θ from it ⇒ the neighbours drop b = a·tan θ.
        // Every face stays planar, so the slice integral is exact:
        // V = w·d·h − a·b·(w+d) + 4a²b/3.
        double w = 4, d = 3, h = 2, a = 0.5, degrees = 30;
        double b = a * Math.Tan(degrees * Math.PI / 180);
        var solid = Shape.Box(w, d, h).ChamferAtAngle(a, degrees, Top).ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        double exact = w * d * h - a * b * (w + d) + 4 * a * a * b / 3;
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void EdgeSelectedFillet_MatchesTheFaceSelector()
    {
        var byFace = Shape.Box(4, 3, 2).Fillet(0.4, Top);
        var byEdges = Shape.Box(4, 3, 2).FilletEdges(0.4, s => Top(s).SelectMany(f => f.RimEdges()));
        double faceVolume = BRepTessellator.Tessellate(byFace.ToBrep(), 64, 32).Volume();
        double edgeVolume = BRepTessellator.Tessellate(byEdges.ToBrep(), 64, 32).Volume();
        Assert.Equal(faceVolume, edgeVolume, 12);
    }

    [Fact]
    public void EdgeSelectedChamfer_BothRims_ExactVolume()
    {
        // Selecting edges from two rims at once: each removes a·b·(w+d) − 4a²b/3.
        double w = 4, d = 3, h = 2, c = 0.3;
        var shape = Shape.Box(w, d, h).ChamferEdges(c, s =>
            s.PlanarFacesWithNormal(Vector3d.UnitZ).Concat(s.PlanarFacesWithNormal(-Vector3d.UnitZ))
                .SelectMany(f => f.RimEdges()));
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        double exact = w * d * h - 2 * (c * c * (w + d) - 4 * c * c * c / 3);
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void EdgeSelection_ThatIsNotACompleteRim_Throws()
    {
        var shape = Shape.Box(2, 2, 1).FilletEdges(0.2, s => Top(s).SelectMany(f => f.RimEdges()).Take(1));
        var exception = Assert.Throws<NotSupportedException>(() => shape.ToBrep());
        Assert.Contains("complete rims", exception.Message);
    }

    [Fact]
    public void EmptySelector_Throws()
    {
        var shape = Shape.Box(1, 1, 1).Chamfer(0.1, s => []);
        Assert.Throws<InvalidOperationException>(() => shape.ToBrep());
    }

    [Fact]
    public void RimFeatures_AreBridgedToImplicit()
    {
        var shape = Shape.Box(2, 2, 1).Chamfer(0.2, Top);
        var report = shape.Explain(TargetRep.Implicit);
        Assert.True(report.IsConvertible);
        Assert.Contains(report.Entries, e => e.Support == NodeSupport.Bridged);

        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
    }
}
