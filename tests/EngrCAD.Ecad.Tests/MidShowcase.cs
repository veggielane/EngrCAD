using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The MID/LDS showcase: a MOULDED, doubly-curved wearable dome carrying its own circuit — an MCU, two
/// LEDs, a connector and passives seated on the shaped surface, wired by conductors routed as geodesics
/// ALONG the dome. It builds a <see cref="Scene"/> ready to render and verifies itself (the nets connect
/// and the 3D DRC is clean). This is the canonical intrinsic MID scene; the docs render the same layout.
/// </summary>
public static class MidShowcase
{
    private const double Radius = 10;
    private static readonly Vector3d Scale = new(1.5, 1.5, 0.62);   // a wide, low pebble dome

    private static readonly PartColor Housing = new(0.30f, 0.55f, 0.62f);
    private static readonly PartColor Chip = new(0.16f, 0.18f, 0.22f);
    private static readonly PartColor Connector = new(0.24f, 0.26f, 0.30f);
    private static readonly PartColor Passive = new(0.35f, 0.30f, 0.26f);

    /// <summary>Builds the showcase scene, reporting the verified DRC and the layout counts.</summary>
    public static Scene Build(out Mid3dDrcReport report, out int netCount, out int componentCount)
    {
        // The moulded shell — a wide, low ellipsoidal dome (a wearable puck), genuinely doubly-curved so
        // no single exp-map chart is an isometry: exactly what the intrinsic model exists for.
        var shell = MeshPrimitives.UvSphere(Radius, 200, 100)
            .Transformed(Matrix4d.CreateScale(Scale));
        var board = MidBoard.OnMesh(shell);

        const double land = 0.6, width = 0.35, copper = 0.06;

        // Component footprints (world x, y; z is snapped to the dome). A star topology around a central
        // MCU keeps the routed geodesics from crossing, so the board is legibly clean.
        var mcu = Body.Qfn();
        var conn = Body.Connector();
        var led = Body.Led();
        var res = Body.Passive();

        board.Seat(mcu, Above(0, 0));
        board.Seat(conn, Above(0, -9.5));
        board.Seat(led, Above(-9.5, 2.5));
        board.Seat(led, Above(9.5, 2.5));
        board.Seat(res, Above(-2.6, 4.8));
        board.Seat(res, Above(0, 5.2));
        board.Seat(res, Above(2.6, 4.8));
        int components = 7;

        // Pads and single-trace nets, radiating from the MCU: connector power/ground to the MCU's south
        // pads, and each LED to a side pad. One trace per net, so each net's copper is one piece.
        var nets = new (string Net, Vector3d A, Vector3d B)[]
        {
            ("5V",  Above(-1.7, -9.0), Above(-1.7, -3.2)),
            ("GND", Above( 1.7, -9.0), Above( 1.7, -3.2)),
            ("D1",  Above(-3.2,  0.9), Above(-8.8,  2.5)),
            ("D2",  Above( 3.2,  0.9), Above( 8.8,  2.5)),
        };

        foreach (var (net, a, b) in nets)
        {
            board.PlacePad(net, a, land, $"{net}.a");
            board.PlacePad(net, b, land, $"{net}.b");
        }

        // AUTO-ROUTE: the board routes itself — a DRC-clean geodesic per net over the mesh vertex graph.
        var routed = MidRouting.Route(board, null, new SurfaceRouteOptions { TraceWidth = width });
        var conductors = routed.Traces.Select(t => t.Conductor(copper)).ToList();

        report = MidRouting.Verify(board);
        netCount = nets.Length;
        componentCount = components;

        // The scene: the shell, the seated bodies, and the routed copper on the surface.
        var scene = new Scene();
        scene.Add(new Part("shell", shell, Housing));
        int c = 0;
        foreach (var seated in board.Seated)
            scene.Add(new Part($"cmp{c++}", seated.Body, ColorFor(seated)));
        for (int i = 0; i < conductors.Count; i++)
            scene.Add(new Part($"copper{i}", conductors[i], Palette.Brass));
        return scene;
    }

    private static PartColor ColorFor(MidSeatedComponent s)
    {
        // Colour by seat position: the connector at the front, the LEDs on the sides, everything else dark.
        var o = s.SurfaceFrame.Origin;
        if (o.Y < -6) return Connector;
        if (o.X < -6) return Palette.Coral;
        if (o.X > 6) return Palette.Sky;
        if (o.Y > 3) return Passive;
        return Chip;
    }

    /// <summary>The dome surface point above a footprint (x, y) — used to place a component or pad "here"
    /// on the wearable's top.</summary>
    private static Vector3d Above(double x, double y)
    {
        double rx = Radius * Scale.X, rz = Radius * Scale.Z;
        double t = 1 - (x * x + y * y) / (rx * rx);
        double z = rz * Math.Sqrt(Math.Max(t, 0));
        return new Vector3d(x, y, z + 0.01);
    }

    /// <summary>Small electronic-component bodies, modeled +Z out of the surface with their seating datum
    /// at the origin (so the seat convention drops them onto the dome pointing outward).</summary>
    private static class Body
    {
        public static Shape Qfn() =>
            Shape.Box(5, 5, 1.1).Translate(new Vector3d(0, 0, 0.55));

        public static Shape Connector() =>
            Shape.Box(6, 3.2, 2.6).Translate(new Vector3d(0, 0, 1.3));

        public static Shape Led() =>
            Shape.Cylinder(1.0, 0.7).Translate(new Vector3d(0, 0, 0.35))
                .Union(Shape.Sphere(1.0).Scale(1, 1, 0.7).Translate(new Vector3d(0, 0, 0.7)));

        public static Shape Passive() =>
            Shape.Box(1.6, 0.8, 0.45).Translate(new Vector3d(0, 0, 0.225));
    }
}
