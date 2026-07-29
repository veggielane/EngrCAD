using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mcp;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

// A minimal design program in the shape every EngrCAD model program takes: build a
// Scene, hand it to the entry point. This one exists so the tests can drive a REAL
// process over stdio (`--mcp`) rather than only exercising the tool layer in-memory.
//
// Note the deliberate Console.WriteLine below: it is the exact bug --mcp mode must
// survive. A design program that prints while it builds would, without StdoutGuard,
// inject a non-JSON line into the protocol stream and break every client. The
// stdout-purity test asserts that this line lands on stderr instead.

var options = new EngrCadOptions { Title = "mcp test model" };

// --animate is this program's OWN switch (EngrCad.Run ignores arguments it does not
// know), so the windowed RPC test can drive a real transport while every existing stdio
// test keeps a scene with no animation and therefore identical framing. A turntable is
// the cheapest track that visibly moves a frame: it changes the camera and nothing else,
// so "the picture at t = 0.5 differs from the picture at t = 0" is a claim about the
// playback position rather than about geometry.
if (args.Contains("--animate", StringComparer.Ordinal))
    options.Animation = scene => new Animation(4).With(TurntableTrack.Around(scene));

return EngrCadMcp.Run(args, BuildScene, options);

static Scene BuildScene()
{
    Console.WriteLine("building the test model (this line must NOT reach stdout under --mcp)");

    // Coarse on purpose: these tests mesh for real, and nothing here is about fidelity.
    var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12, SdfResolution = 24 });

    var model = scene.AddTab("Model");
    model.Add(new Part("bracket",
        Shape.Box(20, 12, 6) | Shape.Cylinder(3, 10).Translate(6, 0, 3)));
    model.Add(new Part("pin", Shape.Cylinder(2, 14),
        transform: Matrix4d.CreateTranslation(new Vector3d(-6, 0, 7))));

    // A history-backed part so the write tools (set_param / suppress_feature) can be
    // driven over a REAL stdio session: a rectangle extrusion plus a boss.
    var history = new FeatureHistory();
    history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(20, 10)) { Name = "Base", Height = 6 });
    history.Add(new BooleanFeature(Shape.Box(4, 4, 40)) { Name = "Boss" });
    model.Add(history.ToPart("plate", transform: Matrix4d.CreateTranslation(new Vector3d(0, 20, 0))));

    var field = scene.AddTab("field");
    field.Add(new Part("blob", Sdf.Sphere(5)));

    return scene;
}
