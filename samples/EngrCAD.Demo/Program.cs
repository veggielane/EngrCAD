// The EngrCAD showcase, written the way any consumer writes a design: build a Scene
// from kernel geometry, then EngrCad.Show(scene). This is the "main method" pattern.
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

var scene = new Scene(new SceneOptions { SegmentsPerCircle = 48, SdfResolution = 64 });

// Front row: mesh-engine primitives and algorithms.
scene.Add("box", MeshPrimitives.Box(1.8, 1.8, 1.8), Palette.Sky,
    Matrix4d.CreateTranslation((-4.6, -1.9, 0)));
scene.Add("sphere", MeshPrimitives.UvSphere(1.05, segments: 48, rings: 24), Palette.Coral,
    Matrix4d.CreateTranslation((-1.5, -1.9, 0)));
scene.Add("cylinder", MeshPrimitives.Cylinder(0.85, 1.9, segments: 48), Palette.Sage,
    Matrix4d.CreateTranslation((1.5, -1.9, -0.95)));
scene.Add("subdivided box", LoopSubdivision.Subdivide(MeshPrimitives.Box(2.0, 2.0, 2.0).Triangulated(), 3),
    Palette.Plum, Matrix4d.CreateTranslation((4.6, -1.9, 0)));

// Middle row: mesh booleans and the implicit engine.
scene.Add("mesh boolean", MeshBoolean.Difference(
        MeshPrimitives.Box(1.8, 1.8, 1.8),
        MeshPrimitives.UvSphere(1.15, segments: 32, rings: 16)),
    Palette.Brass, Matrix4d.CreateTranslation((-4.6, 1.9, 0)));
scene.Add("smooth blend",
    Sdf.Sphere(0.72).Translate((-0.5, 0, 0)).SmoothUnion(Sdf.Sphere(0.72).Translate((0.5, 0, 0)), 0.45),
    Palette.Teal, Matrix4d.CreateTranslation((-1.5, 1.9, 0)));
scene.Add("torus", Sdf.Torus(0.85, 0.34), Palette.Rose, Matrix4d.CreateTranslation((1.5, 1.9, 0)));
scene.Add("gyroid lattice", Sdf.Sphere(1.1) & Sdf.Gyroid(0.75, 0.16), Palette.Slate,
    Matrix4d.CreateTranslation((4.6, 1.9, 0)));

// Back row: B-Rep modeling operations.
var bracket = SolidFactory.Extrude(
    Profile.FromPoints([(0, 0, 0), (1.6, 0, 0), (1.6, 0.5, 0), (0.5, 0.5, 0), (0.5, 1.6, 0), (0, 1.6, 0)]),
    (0, 0, 0.6));
scene.Add("bracket", bracket, Palette.Copper,
    Matrix4d.CreateTranslation((-5.4, 5.6, -0.3)) * Matrix4d.CreateRotationX(Math.PI / 2));

var pulley = SolidFactory.Revolve(
    Profile.FromPoints(
    [
        (0.55, 0, 0), (1.15, 0, 0), (1.15, 0, 0.22), (0.85, 0, 0.34),
        (0.85, 0, 0.56), (1.15, 0, 0.68), (1.15, 0, 0.9), (0.55, 0, 0.9),
    ]),
    Vector3d.Zero, Vector3d.UnitZ);
scene.Add("pulley", pulley, Palette.Steel, Matrix4d.CreateTranslation((-1.5, 5.6, -0.45)));

var tubePath = new NurbsCurve(2, [(0, 0, -1.1), (0, 0, 0.2), (0, 1.1, 1.3)], null, [0, 0, 0, 1, 1, 1]);
var tube = SolidFactory.Sweep(Profile.Circle((0, 0, -1.1), Vector3d.UnitX, Vector3d.UnitY, 0.35), tubePath);
scene.Add("swept tube", tube, Palette.Sage, Matrix4d.CreateTranslation((2.2, 5.6, 0)));

var block = SolidFactory.MakeBox(new Aabb((-0.9, -0.9, -0.55), (0.9, 0.9, 0.55)));
var boreTool = SolidFactory.Extrude(
    Profile.Circle((0.25, 0.25, -1), Vector3d.UnitX, Vector3d.UnitY, 0.4), (0, 0, 2));
scene.Add("drilled block", BrepBoolean.Difference(block, boreTool), Palette.Brass,
    Matrix4d.CreateTranslation((5.4, 5.6, 0)));

// Fourth row: the unified modeling API — ONE shape, lowered three ways at the end.
var model = Shape.Box(2.0, 1.4, 0.8)
    .SmoothUnion(Shape.Sphere(0.5).Translate(0, 0, 0.55), 0.25)
    - Shape.Cylinder(0.3, 3).Translate(0.55, 0, 0);

scene.Add("shape → B-Rep", (Shape.Box(2.0, 1.4, 0.8) - Shape.Cylinder(0.3, 3).Translate(0.55, 0, 0)).ToBrep(),
    Palette.Steel, Matrix4d.CreateTranslation((-4.6, 9.3, 0)));      // blend dropped: exact solid
scene.Add("shape → implicit", model.ToImplicit(), Palette.Teal,
    Matrix4d.CreateTranslation((-1.5, 9.3, 0)));                     // polygonized SDF, blend intact
scene.Add("shape → mesh", model, Palette.Coral,
    Matrix4d.CreateTranslation((1.5, 9.3, 0)));                      // Scene picks the best route

EngrCad.Show(scene, "EngrCAD demo");
