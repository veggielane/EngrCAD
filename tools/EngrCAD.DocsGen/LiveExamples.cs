// Compiles each documentation snippet a SECOND time -- against exactly the assemblies the
// WebAssembly viewer carries -- and emits it as a standalone assembly the browser loads on
// demand, so an example page can run the real kernel instead of only showing a screenshot
// of what it produced. See docs/writing-examples.md and design.md 8c.
//
// THE REFERENCE SET IS THE RULE. The live compilation sees Core / Mesh / Implicit / BRep /
// Interop / Modeling / Viewer.Core and nothing else, because that is the transitive closure
// EngrCAD.Web already ships. So "can this example run in a browser?" is answered by the C#
// compiler rather than by a list somebody maintains: a snippet reaching for EngrCAD.Fea, for
// the desktop viewer's EngrCad.RenderToImage, or for the docs-only `Scratch` directory simply
// does not compile here, and the refusal carries the compiler's own words. Nothing silently
// degrades, which is the property that matters -- a live button that throws in the reader's
// face is worse than no live button.
//
// The one thing a reference set cannot catch is code that compiles and then fails because the
// browser's filesystem is EMPTY (it is an in-memory Emscripten FS with only the app's own
// assets in it). That is a short, named list below, resolved through the SEMANTIC model rather
// than by scanning text -- heightmaps.md mentions `Heightmap.ReadPng` in a comment while being
// entirely procedural, and a substring scan refuses it wrongly.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;

namespace EngrCAD.DocsGen;

/// <summary>The outcome of preparing one snippet for the browser: an assembly, or a
/// refusal naming the reason.</summary>
/// <param name="Assembly">The emitted assembly bytes, or null when refused.</param>
/// <param name="Refusal">Why this example cannot run in a browser, or null when it can.</param>
public sealed record LiveExampleBuild(byte[]? Assembly, string? Refusal)
{
    /// <summary>Whether the example can run in the browser.</summary>
    public bool Live => Assembly is not null;
}

/// <summary>
/// Build-time half of the live documentation examples: compile a snippet against the
/// browser's own assembly set and emit it.
/// </summary>
public static class LiveExamples
{
    /// <summary>
    /// Types whose members read the machine the docs were BUILT on. The browser has an
    /// in-memory filesystem containing only the published app's assets, so a snippet using
    /// one of these compiles happily and then throws where a reader can see it.
    /// </summary>
    private static readonly Dictionary<string, string> HostOnlyTypes = new(StringComparer.Ordinal)
    {
        ["System.IO.File"] = "reads or writes files",
        ["System.IO.Directory"] = "reads or writes files",
        ["System.IO.FileInfo"] = "reads or writes files",
        ["System.IO.DirectoryInfo"] = "reads or writes files",
        ["System.IO.FileStream"] = "reads or writes files",
        ["System.Environment"] = "reads the build machine's environment",
    };

    /// <summary>
    /// The browser's compilation environment: the same imports the docs harness gives a
    /// snippet, minus the two namespaces whose assemblies the WebAssembly app does not carry
    /// (<c>EngrCAD.Fea</c>, <c>EngrCAD.Query</c>), and with no globals -- so the docs-only
    /// <c>Scratch</c> directory is refused by the compiler rather than by a rule.
    /// <para><b>EngrCAD.Viewer resolves to the Viewer.Core ASSEMBLY here</b>, which is the
    /// point of that assembly existing: <c>SectionPlane</c>, <c>CameraState</c>,
    /// <c>Animation</c> and the view/shading enums are in it, while <c>EngrCad.RenderToImage</c>
    /// and <c>ConstructionPreviewRequest</c> are not -- so a snippet declaring a camera is live
    /// and one asking for a construction preview is refused, both by name.</para>
    /// </summary>
    public static ScriptOptions BrowserOptions { get; } = ScriptOptions.Default
        .AddReferences(
            typeof(EngrCAD.Core.Vector3d).Assembly,
            typeof(EngrCAD.Mesh.HalfEdgeMesh).Assembly,
            typeof(EngrCAD.Implicit.Sdf).Assembly,
            typeof(EngrCAD.BRep.BrepSolid).Assembly,
            typeof(EngrCAD.Interop.BrepBoolean).Assembly,
            typeof(EngrCAD.Modeling.Shape).Assembly,
            typeof(EngrCAD.Viewer.CameraState).Assembly)
        .AddImports(
            "System", "System.IO", "System.Linq", "System.Collections.Generic",
            "EngrCAD.Core", "EngrCAD.Core.Geometry2", "EngrCAD.Mesh", "EngrCAD.Implicit",
            "EngrCAD.BRep", "EngrCAD.Interop", "EngrCAD.Modeling", "EngrCAD.Viewer");

    /// <summary>
    /// Compiles one snippet for the browser and emits it, or refuses by name.
    /// </summary>
    public static LiveExampleBuild Build(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        // GLOBALS = object, and that is not a formality. Roslyn puts a script's globals type's
        // members IN SCOPE, inherited ones included -- so a submission compiled with no globals
        // at all cannot see `object`'s statics, and `ReferenceEquals(a, b)` (used bare by
        // chamfer-fillet.md's drafted-block example, and legal in every ordinary C# class) fails
        // to compile with "the name does not exist in the current context". Handing over `object`
        // restores exactly that scope and nothing else: it adds no assembly reference the browser
        // does not already have, and the docs-only `Scratch` still does not exist, so the one
        // snippet that needs it is still refused for the reason it should be.
        var script = CSharpScript.Create(code, BrowserOptions, typeof(object));
        ImmutableArray<Diagnostic> diagnostics;
        Compilation compilation;
        try
        {
            diagnostics = script.Compile();
            compilation = script.GetCompilation();
        }
        catch (Exception e)
        {
            return new LiveExampleBuild(null, $"the browser build threw: {e.GetType().Name}: {e.Message}");
        }

        var error = diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null)
            return new LiveExampleBuild(null, $"does not compile against the browser's assemblies: {error.GetMessage()}");

        if (FindHostOnlyUse(compilation) is { } hostOnly)
            return new LiveExampleBuild(null, hostOnly);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            var first = emit.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            return new LiveExampleBuild(null, $"could not be emitted: {first?.GetMessage() ?? "unknown error"}");
        }

        return new LiveExampleBuild(stream.ToArray(), null);
    }

    /// <summary>
    /// The one check a reference set cannot make. Resolved through the semantic model, so a
    /// type NAMED in a comment or a string is not a use -- <c>heightmap-terrain</c> mentions
    /// <c>Heightmap.ReadPng</c> in prose while computing its grid, and a text scan refuses it.
    /// </summary>
    private static string? FindHostOnlyUse(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(node).Symbol;
                var owner = symbol as INamedTypeSymbol
                    ?? symbol?.ContainingType;
                if (owner is null)
                    continue;
                string name = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    .Replace("global::", "", StringComparison.Ordinal);
                if (HostOnlyTypes.TryGetValue(name, out string? why))
                    return $"{why} ({name}), and the browser's filesystem holds only the app's own assets";
            }
        }
        return null;
    }
}
