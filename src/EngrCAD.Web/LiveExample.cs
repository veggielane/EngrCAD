using System.Diagnostics.CodeAnalysis;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using BF = System.Reflection.BindingFlags;

namespace EngrCAD.Web;

/// <summary>
/// What a documentation example produced: the scene, plus the optional render inputs a
/// snippet may declare (<c>docs/writing-examples.md</c> lists them). Only the ones the
/// browser viewport can honour are read back — a <c>preview</c> request names a type that
/// lives in the desktop viewer, so a snippet declaring one never compiles for the browser
/// in the first place.
/// </summary>
/// <param name="Scene">The scene the snippet built.</param>
/// <param name="Animation">Its animation, when it declared one (an <c>animate:</c> fence
/// without one gets the docs default turntable, which is applied by the host).</param>
/// <param name="Camera">An explicit camera pose, when the snippet declared one.</param>
/// <param name="SectionPlanes">Explicit section planes, when the snippet declared them.</param>
/// <param name="SectionCombine">How several planes combine.</param>
/// <param name="Explode">The exploded-view factor the snippet asked for.</param>
/// <param name="Shading">How fills are lit.</param>
/// <param name="AnnotationDepth">How 3D annotations treat material in front of them.</param>
public sealed record LiveExampleScene(
    Scene Scene,
    Animation? Animation = null,
    CameraState? Camera = null,
    IReadOnlyList<SectionPlane>? SectionPlanes = null,
    SectionCombine SectionCombine = SectionCombine.Intersection,
    double Explode = 0,
    ShadingStyle Shading = ShadingStyle.Lit,
    AnnotationDepth AnnotationDepth = AnnotationDepth.AlwaysOnTop);

/// <summary>
/// Runtime half of the live documentation examples: loads one of the assemblies
/// <c>EngrCAD.DocsGen</c> emitted and runs it, giving back the very <see cref="Scene"/> the
/// committed screenshot was taken of.
///
/// <para><b>Why an assembly rather than source.</b> A browser cannot cheaply compile C# —
/// Roslyn in the payload is several megabytes — but the documentation build already compiles
/// every snippet, so it emits what it compiled. The kernel then genuinely RUNS: the reader is
/// looking at geometry built in their own tab, not at a mesh baked at docs-build time.</para>
///
/// <para><b>The submission ABI.</b> A snippet is a C# <em>script</em>, and Roslyn compiles one
/// into a type carrying a static <c>&lt;Factory&gt;(object[])</c> returning
/// <c>Task&lt;object&gt;</c>. The array is the submission state: slot 0 is the globals object
/// (there are none here, which is deliberate — see <c>LiveExamples.BrowserOptions</c>) and the
/// factory writes the submission INSTANCE into slot 1. Every top-level variable of the script
/// is a field on that instance, which is exactly how the docs harness reads <c>scene</c> back
/// out of a <c>ScriptState</c>; this reads the same fields without needing Roslyn to do it.</para>
/// </summary>
public static class LiveExample
{
    /// <summary>The submission state array a single-submission script needs: slot 0 for the
    /// globals it does not have, slot 1 for the instance the factory constructs.</summary>
    private const int SubmissionSlots = 2;

    /// <summary>
    /// Loads and runs one emitted example.
    /// </summary>
    /// <param name="assembly">The bytes of an assembly written by <c>EngrCAD.DocsGen</c>.</param>
    /// <exception cref="InvalidOperationException">The assembly is not one of ours, or the
    /// snippet defined no <c>scene</c>.</exception>
    [RequiresUnreferencedCode("Runs a documentation example compiled against this app's own "
        + "assemblies; those assemblies are rooted rather than trimmed so the example can call them.")]
    public static async Task<LiveExampleScene> RunAsync(byte[] assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var loaded = System.Reflection.Assembly.Load(assembly);
        var factory = FindFactory(loaded)
            ?? throw new InvalidOperationException(
                "This is not a documentation example: no script submission factory was found in "
                + $"'{loaded.GetName().Name}'.");

        var state = new object?[SubmissionSlots];
        // The snippet's own work happens inside this call. It is synchronous C# in a
        // single-threaded runtime, so the task is already complete when it returns -- the
        // await is for the shape of the contract, not for concurrency the browser has.
        var task = (Task<object>)factory.Invoke(null, [state])!;
        await task;

        object submission = state[1]
            ?? throw new InvalidOperationException("The example produced no submission state.");
        return Read(submission);
    }

    /// <summary>Reads the declared variables off a finished submission, by the same rule
    /// <c>DocsGen</c> reads them off a <c>ScriptState</c>: a top-level script variable is a
    /// field on the submission type, and a variable of the wrong type is ignored exactly as an
    /// absent one is — the docs build has already refused that case with an error.</summary>
    [RequiresUnreferencedCode("Reads the example's own top-level variables by reflection.")]
    private static LiveExampleScene Read(object submission)
    {
        var fields = submission.GetType()
            .GetFields(BF.Public | BF.NonPublic | BF.Instance);

        T? Value<T>(string name)
        {
            foreach (var field in fields)
                if (field.Name == name && field.GetValue(submission) is T typed)
                    return typed;
            return default;
        }

        var scene = Value<Scene>("scene")
            ?? throw new InvalidOperationException(
                "The example defined no `scene` — only render: and animate: snippets can run here.");

        return new LiveExampleScene(
            scene,
            Value<Animation>("animation"),
            Value<CameraState>("camera"),
            Value<IEnumerable<SectionPlane>>("sectionPlanes") is { } planes ? [.. planes] : null,
            Value<SectionCombine?>("sectionCombine") ?? SectionCombine.Intersection,
            Value<double?>("explode") ?? 0,
            Value<ShadingStyle?>("shading") ?? ShadingStyle.Lit,
            Value<AnnotationDepth?>("annotationDepth") ?? AnnotationDepth.AlwaysOnTop);
    }

    /// <summary>The script submission's entry point, found by SHAPE rather than by name:
    /// Roslyn calls the type <c>Submission#0</c> today, and a type name is a detail of the
    /// scripting layer while the factory's signature is the contract this depends on.</summary>
    [RequiresUnreferencedCode("Scans the loaded example assembly for its script entry point.")]
    private static System.Reflection.MethodInfo? FindFactory(System.Reflection.Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var method = type.GetMethod("<Factory>", BF.Static | BF.Public | BF.NonPublic);
            if (method is not null
                && method.ReturnType == typeof(Task<object>)
                && method.GetParameters() is [{ ParameterType.IsArray: true }])
                return method;
        }
        return null;
    }
}
