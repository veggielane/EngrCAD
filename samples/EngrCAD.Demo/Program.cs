// The EngrCAD showcase, written the way any consumer writes a design: create Parts
// (name + geometry from any engine), group them into Scene tabs, EngrCad.Show.
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

var scene = new Scene(new MeshQuality { SegmentsPerCircle = 48, SdfResolution = 64 });

// ---- tab 1: feature modeling — queries, fillet/chamfer, patterns ----
var features = scene.AddTab("features");
var featurePlate = Shape.Extrude(Sketch.RoundedRectangle(3.6, 2.6, 0.5), 0.8)
    .Fillet(0.25, s => s.PlanarFacesWithNormal(Vector3d.UnitZ))      // smooth top rim
    .Chamfer(0.15, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ));   // beveled base
features.Add(new Part("fillet + chamfer", featurePlate, Palette.Steel,
    Matrix4d.CreateTranslation((-3.1, 0, -0.4))));

var carousel = Shape.Cylinder(2.0, 0.4)
    | Shape.Cylinder(0.3, 1.4).Translate(1.5, 0, 0.5).PatternCircular(6, Vector3d.Zero, Vector3d.UnitZ);
features.Add(new Part("circular pattern", carousel, Palette.Brass,
    Matrix4d.CreateTranslation((2.9, 0, 0))));

// ---- tab 2: sketching — lines, arcs, béziers in every representation ----
var sketches = scene.AddTab("sketch");
var plate = Sketch.Start(-2, -1.2)
    .LineTo(2, -1.2)
    .ArcTo(new(2, 1.2), 1.4, clockwise: false)                      // bulged arc end
    .LineTo(-2, 1.2)
    .BezierTo(new(-3.4, 0.7), new(-3.4, -0.7), new(-2, -1.2))       // bézier end
    .Close()
    .WithHole(Sketch.Circle(new(1.1, 0), 0.45))
    .WithHole(Sketch.Circle(new(-1.3, 0), 0.3));
sketches.Add(new Part("sketched plate", Shape.Extrude(plate, 0.5), Palette.Steel,
    Matrix4d.CreateTranslation((-3.4, 0, 0))));                     // exact B-Rep route

var vase = Sketch.Start(0, 0).LineTo(1, 0)
    .BezierTo(new(1.7, 0.9), new(0.35, 1.7), new(0.9, 2.6))
    .LineTo(0, 2.6).Close();
sketches.Add(new Part("revolved vase", Shape.Revolve(vase), Palette.Teal,
    Matrix4d.CreateTranslation((2.6, 0, -1.3))));                   // axis-touching: on-axis line drops, poles close it

// The hole feature with the standards catalog (dimensions in mm, scaled for display):
// M3 csk, M4 cbore, M5 clearance through-holes; blind M6 tap pilot and M4 Trisert.
var plateTop = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
var holePlate = Shape.Box(30, 20, 12)
    .Drill(StandardHoles.Countersunk(3), [new(-10, 5)], depth: 14, plateTop)
    .Drill(StandardHoles.Counterbored(4), [new(0, 5)], depth: 14, plateTop)
    .Drill(StandardHoles.Clearance(5), [new(10, 5)], depth: 14, plateTop)
    .Drill(StandardHoles.Tapped(6), [new(-5, -5)], depth: 10, plateTop)
    .Drill(StandardHoles.Trisert(4), [new(5, -5)], StandardHoles.TrisertMinimumDepth(4), plateTop);
sketches.Add(new Part("standard holes", holePlate, Palette.Brass,
    Matrix4d.CreateTranslation((0, -4.4, 0)) * Matrix4d.CreateScale(0.14)));

// ---- tab 2: the mesh engine ----
var meshes = scene.AddTab("mesh");
meshes.Add(new Part("box", MeshPrimitives.Box(1.8, 1.8, 1.8), Palette.Sky,
    Matrix4d.CreateTranslation((-4.6, 0, 0))));
meshes.Add(new Part("sphere", MeshPrimitives.UvSphere(1.05, segments: 48, rings: 24), Palette.Coral,
    Matrix4d.CreateTranslation((-1.5, 0, 0))));
meshes.Add(new Part("cylinder", MeshPrimitives.Cylinder(0.85, 1.9, segments: 48), Palette.Sage,
    Matrix4d.CreateTranslation((1.5, 0, -0.95))));
meshes.Add(new Part("subdivided box",
    LoopSubdivision.Subdivide(MeshPrimitives.Box(2.0, 2.0, 2.0).Triangulated(), 3), Palette.Plum,
    Matrix4d.CreateTranslation((4.6, 0, 0))));
meshes.Add(new Part("mesh boolean",
    MeshBoolean.Difference(MeshPrimitives.Box(1.8, 1.8, 1.8), MeshPrimitives.UvSphere(1.15, segments: 32, rings: 16)),
    Palette.Brass, Matrix4d.CreateTranslation((7.7, 0, 0))));

// ---- tab 2: the implicit engine ----
var implicits = scene.AddTab("implicit");
implicits.Add(new Part("smooth blend",
    Sdf.Sphere(0.72).Translate((-0.5, 0, 0)).SmoothUnion(Sdf.Sphere(0.72).Translate((0.5, 0, 0)), 0.45),
    Palette.Teal, Matrix4d.CreateTranslation((-3.1, 0, 0))));
implicits.Add(new Part("torus", Sdf.Torus(0.85, 0.34), Palette.Rose));
implicits.Add(new Part("gyroid lattice", Sdf.Sphere(1.1) & Sdf.Gyroid(0.75, 0.16), Palette.Slate,
    Matrix4d.CreateTranslation((3.1, 0, 0))));

// ---- tab 3: the B-Rep engine ----
var breps = scene.AddTab("B-Rep");
var bracket = SolidFactory.Extrude(
    Profile.FromPoints([(0, 0, 0), (1.6, 0, 0), (1.6, 0.5, 0), (0.5, 0.5, 0), (0.5, 1.6, 0), (0, 1.6, 0)]),
    (0, 0, 0.6));
breps.Add(new Part("bracket", bracket, Palette.Copper,
    Matrix4d.CreateTranslation((-5.4, 0, -0.3)) * Matrix4d.CreateRotationX(Math.PI / 2)));

var pulley = SolidFactory.Revolve(
    Profile.FromPoints(
    [
        (0.55, 0, 0), (1.15, 0, 0), (1.15, 0, 0.22), (0.85, 0, 0.34),
        (0.85, 0, 0.56), (1.15, 0, 0.68), (1.15, 0, 0.9), (0.55, 0, 0.9),
    ]),
    Vector3d.Zero, Vector3d.UnitZ);
breps.Add(new Part("pulley", pulley, Palette.Steel, Matrix4d.CreateTranslation((-1.5, 0, -0.45))));

var tubePath = new NurbsCurve(2, [(0, 0, -1.1), (0, 0, 0.2), (0, 1.1, 1.3)], null, [0, 0, 0, 1, 1, 1]);
var tube = SolidFactory.Sweep(Profile.Circle((0, 0, -1.1), Vector3d.UnitX, Vector3d.UnitY, 0.35), tubePath);
breps.Add(new Part("swept tube", tube, Palette.Sage, Matrix4d.CreateTranslation((2.2, 0, 0))));

var block = SolidFactory.MakeBox(new Aabb((-0.9, -0.9, -0.55), (0.9, 0.9, 0.55)));
var boreTool = SolidFactory.Extrude(
    Profile.Circle((0.25, 0.25, -1), Vector3d.UnitX, Vector3d.UnitY, 0.4), (0, 0, 2));
breps.Add(new Part("drilled block", BrepBoolean.Difference(block, boreTool), Palette.Brass,
    Matrix4d.CreateTranslation((5.4, 0, 0))));

// ---- tab 4: the unified Shape API — ONE model, lowered three ways ----
var shapes = scene.AddTab("shape");
var model = Shape.Box(2.0, 1.4, 0.8)
    .SmoothUnion(Shape.Sphere(0.5).Translate(0, 0, 0.55), 0.25)
    - Shape.Cylinder(0.3, 3).Translate(0.55, 0, 0);

shapes.Add(new Part("shape → B-Rep",
    (Shape.Box(2.0, 1.4, 0.8) - Shape.Cylinder(0.3, 3).Translate(0.55, 0, 0)).ToBrep(),
    Palette.Steel, Matrix4d.CreateTranslation((-3.1, 0, 0))));    // blend dropped: exact solid
shapes.Add(new Part("shape → implicit", model.ToImplicit(), Palette.Teal));
shapes.Add(new Part("shape → mesh", model, Palette.Coral,
    Matrix4d.CreateTranslation((3.1, 0, 0))));                    // Part picks the best route

EngrCad.Show(scene, "EngrCAD demo");
