// EngrCAD.Script — run a .csx model script through the standard EngrCad.Run front door.
//
//   dotnet run --project tools/EngrCAD.Script -- model.csx              live window
//   dotnet run --project tools/EngrCAD.Script -- model.csx --view      static window
//   dotnet run --project tools/EngrCAD.Script -- model.csx --export out.step|.stl|.obj
//   dotnet run --project tools/EngrCAD.Script -- model.csx --render out.png [--render-style s] [--section x|y|z o]
//
// The script contract is the SAME as the docs site's executable snippets (DocsGen):
// define `Scene scene = ...;` — or end with a Scene expression. In the live window,
// saving the .csx re-runs it and swaps the scene in place (camera preserved; a script
// error keeps the last good scene and shows in the overlay), which is the OpenSCAD-style
// loop with C# as the language. Compilation is Roslyn scripting, the DocsGen seam.

using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: EngrCAD.Script <model.csx> [--view | --export <file> | --render <file.png> ...]");
    Console.Error.WriteLine("The script must define `Scene scene = ...;` (or end with a Scene expression).");
    Console.Error.WriteLine("With no extra arguments a live window opens: saving the .csx re-applies it.");
    return 2;
}

string scriptPath = Path.GetFullPath(args[0]);
if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"script not found: {scriptPath}");
    return 2;
}
string[] rest = [.. args.Skip(1)];

// The same reference/import surface DocsGen gives doc snippets, so a fence that works
// on the docs site works here verbatim (and vice versa).
var options = ScriptOptions.Default
    .WithFilePath(scriptPath)
    .AddReferences(
        typeof(EngrCAD.Core.Vector3d).Assembly,
        typeof(EngrCAD.Mesh.HalfEdgeMesh).Assembly,
        typeof(EngrCAD.Implicit.Sdf).Assembly,
        typeof(EngrCAD.BRep.BrepSolid).Assembly,
        typeof(EngrCAD.Interop.BrepBoolean).Assembly,
        typeof(EngrCAD.Query.SpatialCollection<>).Assembly,
        typeof(Shape).Assembly,
        typeof(EngrCad).Assembly)
    .AddImports(
        "System", "System.IO", "System.Linq", "System.Collections.Generic",
        "EngrCAD.Core", "EngrCAD.Core.Geometry2", "EngrCAD.Mesh", "EngrCAD.Implicit",
        "EngrCAD.BRep", "EngrCAD.Interop", "EngrCAD.Query", "EngrCAD.Modeling",
        "EngrCAD.Viewer");

Scene Build()
{
    ScriptState<object> state;
    try
    {
        state = CSharpScript.RunAsync(File.ReadAllText(scriptPath), options)
            .GetAwaiter().GetResult();
    }
    catch (CompilationErrorException e)
    {
        // One flat message: ShowLive's overlay and the console both show it whole.
        throw new InvalidOperationException(
            $"{Path.GetFileName(scriptPath)} failed to compile: "
            + string.Join(" | ", e.Diagnostics.Select(d => d.ToString())));
    }

    return state.Variables.LastOrDefault(v => v.Name == "scene")?.Value as Scene
        ?? state.ReturnValue as Scene
        ?? throw new InvalidOperationException(
            $"{Path.GetFileName(scriptPath)} must define `Scene scene = ...;` "
            + "(or end with a Scene expression).");
}

// Fail fast on a broken script — better a compile error on stderr than an empty window.
try
{
    Build();
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

// Saving the .csx is this workflow's "hot reload": watch the file and poke the live
// window's reload path (debounce, keep-last-good-scene and camera preservation all come
// from the viewer side; a no-op for headless --export/--render runs, which exit anyway).
// Editors save via replace-rename as often as in-place writes, so watch both.
using var watcher = new FileSystemWatcher(
    Path.GetDirectoryName(scriptPath)!, Path.GetFileName(scriptPath))
{
    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
};
watcher.Changed += (_, _) => EngrCad.NotifySourceChanged();
watcher.Created += (_, _) => EngrCad.NotifySourceChanged();
watcher.Renamed += (_, _) => EngrCad.NotifySourceChanged();
watcher.EnableRaisingEvents = true;

return EngrCad.Configure()
    .WithTitle($"EngrCAD — {Path.GetFileName(scriptPath)}")
    .Run(rest, Build);
