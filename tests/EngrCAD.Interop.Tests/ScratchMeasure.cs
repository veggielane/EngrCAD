using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

public class ScratchMeasure(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_SCRATCH") is not (null or "");

    private static readonly SurfaceNetsOptions Plain = new() { SharpFeatures = false };

    [Fact]
    public void BoxEdgeRounding()
    {
        if (!Enabled)
            return;

        var box = Sdf.Box(10, 10, 10);
        var region = new Aabb((-7, -7, -7), (7, 7, 7));
        var corner = new Vector3d(5, 5, 5);
        var edgePt = new Vector3d(5, 5, 0);
        foreach (var (name, opt) in new (string, SurfaceNetsOptions)[]
                 { ("plain", Plain), ("sharp", SurfaceNetsOptions.Default),
                   ("sharp-noclamp", new SurfaceNetsOptions { ClampToCell = false }) })
        {
            output.WriteLine($"--- {name}");
            output.WriteLine(" res |    cell |   maxOff | corner miss |  edge miss |    volume |  vol err %");
            foreach (int res in new[] { 16, 24, 32, 48, 64, 96 })
            {
                var mesh = SurfaceNets.Polygonize(box, region, res, null, opt);
                mesh.Validate();
                double cell = 14.0 / res;
                double maxOff = 0, cornerMiss = double.MaxValue, edgeMiss = double.MaxValue;
                foreach (var v in mesh.Vertices)
                {
                    maxOff = Math.Max(maxOff, Math.Abs(box.Evaluate(v.Position)));
                    cornerMiss = Math.Min(cornerMiss, (v.Position - corner).Length);
                    edgeMiss = Math.Min(edgeMiss, (v.Position - edgePt).Length);
                }
                double vol = mesh.Volume();
                output.WriteLine(
                    $"{res,4} | {cell,7:F4} | {maxOff,10:0.###e+0} | {cornerMiss,11:0.###e+0} | {edgeMiss,10:0.###e+0} | {vol,9:F4} | {(vol - 1000) / 1000 * 100,9:F4}");
            }
        }
    }

    [Fact]
    public void OutOfCellStatistics()
    {
        if (!Enabled)
            return;

        var cases = new (string Name, Sdf Field, Aabb Region)[]
        {
            ("box", Sdf.Box(10, 10, 10), new Aabb((-7, -7, -7), (7, 7, 7))),
            ("sphere", Sdf.Sphere(5), new Aabb((-7, -7, -7), (7, 7, 7))),
            ("csg", (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3)).SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25),
                new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2))),
            ("shell", Sdf.Sphere(10).Shell(0.6), new Aabb((-12, -12, -12), (12, 12, 12))),
            ("gyroid", Sdf.Box(10, 10, 10) & Sdf.Gyroid(8, 0.2), new Aabb((-6, -6, -6), (6, 6, 6))),
        };
        output.WriteLine(
            "case   | res | verts | clamped | overshoot | folds plain/clamp/free | worstDot plain/clamp/free | pinch p/c/f");
        foreach (var (name, field, region) in cases)
        {
            foreach (int res in new[] { 44, 64 })
            {
                var plain = SurfaceNets.Polygonize(field, region, res, null, Plain);
                var clamped = SurfaceNets.Polygonize(field, region, res, null, SurfaceNetsOptions.Default);
                var free = SurfaceNets.Polygonize(
                    field, region, res, null, new SurfaceNetsOptions { ClampToCell = false });
                double cell = region.Size[region.LongestAxis] / res;
                int moved = 0;
                double worst = 0;
                var (a, _) = clamped.ToIndexed();
                var (b, _) = free.ToIndexed();
                for (int i = 0; i < a.Length; i++)
                {
                    double d = (a[i] - b[i]).Length;
                    if (d > 0)
                        moved++;
                    worst = Math.Max(worst, d / cell);
                }
                var (fp, dp) = Folds(field, plain);
                var (fc, dc) = Folds(field, clamped);
                var (ff, df) = Folds(field, free);
                output.WriteLine(
                    $"{name,-6} | {res,3} | {a.Length,5} | {moved,7} | {worst,9:F3} | {fp,6} {fc,6} {ff,6} | " +
                    $"{dp,7:F3} {dc,7:F3} {df,7:F3} | {plain.NonManifoldVertices().Count} " +
                    $"{clamped.NonManifoldVertices().Count} {free.NonManifoldVertices().Count}");
            }
        }

        // The repo's own tessellation-quality metric: a facet's normal against the field's
        // normal at its centroid. A fold is a facet facing the wrong way outright.
        static (int Folds, double WorstDot) Folds(Sdf field, HalfEdgeMesh mesh)
        {
            int folds = 0;
            double worst = 1;
            foreach (var face in mesh.Faces)
            {
                var vs = face.Vertices().Select(v => v.Position).ToArray();
                var normal = Vector3d.Zero;
                var centroid = Vector3d.Zero;
                for (int i = 0; i < vs.Length; i++)
                {
                    var p = vs[i];
                    var q = vs[(i + 1) % vs.Length];
                    normal += new Vector3d(
                        (p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y));
                    centroid += p;
                }
                if (!normal.TryNormalize(Tolerance.Default, out var n))
                    continue;
                double dot = n.Dot(field.Normal(centroid / vs.Length, 1e-7));
                worst = Math.Min(worst, dot);
                if (dot < 0)
                    folds++;
            }
            return (folds, worst);
        }
    }

    [Fact]
    public void SmoothFields()
    {
        if (!Enabled)
            return;

        output.WriteLine("field   | res |  plain maxOff | sharp maxOff | plain vol err % | sharp vol err %");
        var sphere = Sdf.Sphere(5);
        double sphereExact = 4.0 / 3.0 * Math.PI * 125;
        foreach (int res in new[] { 16, 32, 64, 128 })
        {
            var p = SurfaceNets.Polygonize(sphere, resolution: res, options: Plain);
            var s = SurfaceNets.Polygonize(sphere, resolution: res, options: SurfaceNetsOptions.Default);
            output.WriteLine(
                $"sphere  | {res,3} | {Off(sphere, p),13:0.###e+0} | {Off(sphere, s),12:0.###e+0} | " +
                $"{(p.Volume() - sphereExact) / sphereExact * 100,15:F4} | {(s.Volume() - sphereExact) / sphereExact * 100,15:F4}");
        }

        var torus = Sdf.Torus(5, 2);
        double torusExact = 2 * Math.PI * Math.PI * 5 * 4;
        foreach (int res in new[] { 32, 64, 128 })
        {
            var p = SurfaceNets.Polygonize(torus, resolution: res, options: Plain);
            var s = SurfaceNets.Polygonize(torus, resolution: res, options: SurfaceNetsOptions.Default);
            output.WriteLine(
                $"torus   | {res,3} | {Off(torus, p),13:0.###e+0} | {Off(torus, s),12:0.###e+0} | " +
                $"{(p.Volume() - torusExact) / torusExact * 100,15:F4} | {(s.Volume() - torusExact) / torusExact * 100,15:F4}");
        }

        static double Off(Sdf f, HalfEdgeMesh m)
        {
            double worst = 0;
            foreach (var v in m.Vertices)
                worst = Math.Max(worst, Math.Abs(f.Evaluate(v.Position)));
            return worst;
        }
    }

    [Fact]
    public void Cost()
    {
        if (!Enabled)
            return;

        var field = (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
            .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25);
        var region = new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2));

        var warm = Stopwatch.StartNew();
        do
        {
            SurfaceNets.Polygonize(field, region, 48);
            SurfaceNets.Polygonize(field, region, 48, null, Plain);
        }
        while (warm.ElapsedMilliseconds < 1500);

        output.WriteLine(" res |  plain ms | sharp ms | ratio");
        foreach (int res in new[] { 48, 96, 192, 256 })
        {
            double plain = double.MaxValue, sharp = double.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                var sw = Stopwatch.StartNew();
                SurfaceNets.Polygonize(field, region, res, null, Plain);
                plain = Math.Min(plain, sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                SurfaceNets.Polygonize(field, region, res);
                sharp = Math.Min(sharp, sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"{res,4} | {plain,9:F1} | {sharp,8:F1} | {sharp / plain,5:F2}");
        }
    }

    [Fact]
    public void Adaptive()
    {
        if (!Enabled)
            return;

        var cases = new (string Name, Sdf Field, Aabb Region, double Exact)[]
        {
            ("box", Sdf.Box(10, 10, 10), new Aabb((-7, -7, -7), (7, 7, 7)), 1000),
            ("sphere", Sdf.Sphere(5), new Aabb((-7, -7, -7), (7, 7, 7)), 4.0 / 3 * Math.PI * 125),
            ("csg", (Sdf.Box(6, 6, 6) - Sdf.Cylinder(2, 9)), new Aabb((-5, -5, -5), (5, 5, 5)), 216 - Math.PI * 4 * 6),
        };
        output.WriteLine("case   | res |  tol |  faces |  adapt faces | ratio |  vol err % | manifold");
        foreach (var (name, field, region, exact) in cases)
        {
            int res = 64;
            var full = SurfaceNets.Polygonize(field, region, res);
            double cell = region.Size[region.LongestAxis] / res;
            foreach (double tolCells in new[] { 0.01, 0.05, 0.2 })
            {
                var sw = Stopwatch.StartNew();
                var mesh = SurfaceNets.Polygonize(field, region, res, null,
                    new SurfaceNetsOptions { SimplifyTolerance = tolCells * cell });
                sw.Stop();
                mesh.Validate();
                output.WriteLine(
                    $"{name,-6} | {res,3} | {tolCells,4} | {full.FaceCount,6} | {mesh.FaceCount,12} | " +
                    $"{(double)full.FaceCount / mesh.FaceCount,5:F1} | {(mesh.Volume() - exact) / exact * 100,10:F4} | " +
                    $"{mesh.IsClosed} {mesh.NonManifoldVertices().Count} ({sw.ElapsedMilliseconds} ms)");
            }
        }
    }
}
