#:project ../src/EngrCAD.Viewer/EngrCAD.Viewer.csproj

using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
string fontPath = new[] { "arial.ttf", "segoeui.ttf", "verdana.ttf" }
    .Select(name => Path.Combine(fonts, name)).First(File.Exists);
var font = TrueTypeFont.Load(fontPath);

var top = SketchPlane.At((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);
var lettering = Shape.Text("ENGRCAD", font,
                           size: font.EmSizeForCapHeight(9),
                           height: 1.2, top,
                           new TextStyle { Align = TextAlign.Center });

var scene = new Scene();
scene.Add(new Part("plate", Shape.Box(70, 22, 4), Palette.Steel));
scene.Add(new Part("lettering", lettering, Palette.Brass));

Console.WriteLine($"offscreen available: {EngrCad.CanRenderToImage}");
if (EngrCad.CanRenderToImage)
{
    string path = Path.Combine(Path.GetTempPath(), "engrcad-text-render.png");
    EngrCad.RenderToImage(scene, path, 1000, 700);
    Console.WriteLine($"rendered {new FileInfo(path).Length} bytes to {path}");
}
