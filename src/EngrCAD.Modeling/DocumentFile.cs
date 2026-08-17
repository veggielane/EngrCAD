using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// A whole saved document: the <see cref="Modeling.Scene"/> (tabs, parts, assemblies,
/// annotations, results) plus the things a scene cannot own — the
/// <see cref="MateSet"/>s that constrain its assemblies.
///
/// <para><b>A document is its construction history, not its geometry.</b> Nothing exact
/// is stored: a part backed by a <see cref="FeatureHistory"/> saves that history
/// (<see cref="FeatureHistory.SaveHistory"/>) and REGENERATES on load, which is the whole
/// point of a parametric file. Geometry with no construction record — a raw
/// <see cref="HalfEdgeMesh"/>, an imported <c>.stl</c>, a <see cref="Shape"/> graph built
/// in code, an <see cref="EngrCAD.Implicit.Sdf"/> — has nothing to replay, and is
/// handled EXPLICITLY rather than dropped: its display mesh is embedded as a
/// <b>snapshot</b> (see <see cref="DocumentSaveOptions.EmbedGeometry"/>), and
/// <see cref="DocumentLoadResult.Snapshots"/> names every part that came back that way,
/// so a host can say "these parts are not parametric" instead of pretending they are.</para>
///
/// <para><b>Why embedded rather than an external reference.</b> A document that points at
/// files beside it is a manifest, not a document: the reference breaks the first time
/// someone moves, renames or emails the file, and the failure surfaces as missing
/// geometry rather than as a missing file. One file that reloads the model is the whole
/// value of an envelope. The case an external reference genuinely wins — a scan mesh
/// large enough that inlining it is absurd — is filed in todo.md rather than built,
/// because nothing in this repo produces such a part today and the resolver hook it needs
/// (path resolution relative to the document, a missing-file policy) is real design work
/// that should follow a real need.</para>
///
/// <para><b>Conventions, inherited from <see cref="FeatureHistory.LoadParameters"/> and
/// <see cref="MateSet.LoadMates"/>.</b> Loading returns warnings, never throws, for
/// anything the file describes that this build cannot rebuild (an opaque feature, a
/// lambda-backed annotation, a catalogue component); only a structurally invalid file —
/// bad JSON, a missing envelope, an unsupported version — throws. Saving is
/// DETERMINISTIC and <c>save -&gt; load -&gt; save</c> is a byte-identical fixed point
/// for everything that round-trips (a file carrying opaque records is smaller the second
/// time round, by exactly the records the load warned about).</para>
/// </summary>
public sealed class Document
{
    /// <summary>The file format's marker and version. Bump <see cref="Version"/> when the
    /// written shape changes incompatibly; <see cref="Load"/> refuses a version it does
    /// not know rather than guessing.</summary>
    public const string Format = "engrcad-document";

    /// <summary>The version this build writes (and the highest it reads).</summary>
    public const int Version = 1;

    /// <summary>Wraps a scene as a document. The scene is held by reference — edits to it
    /// are edits to the document.</summary>
    public Document(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene = scene;
    }

    /// <summary>The document's content: tabs, parts and assemblies.</summary>
    public Scene Scene { get; }

    /// <summary>
    /// The mate sets constraining this document's assemblies, in save order. A
    /// <see cref="MateSet"/> is not owned by its <see cref="Assembly"/> (it is a solver
    /// input, not assembly structure), so the document is where they belong; a set whose
    /// assembly is not in <see cref="Scene"/> is skipped with a warning on save.
    /// </summary>
    public IList<MateSet> Mates { get; } = new List<MateSet>();

    /// <summary>Serializes the whole document to JSON. See the class notes for what is
    /// stored as a recipe and what as a snapshot.</summary>
    public string Save(DocumentSaveOptions? options = null) =>
        DocumentWriter.Write(this, options ?? new DocumentSaveOptions());

    /// <summary>Rebuilds a document from <see cref="Save"/> JSON: histories are
    /// reconstructed through the feature registry and regenerated, snapshots come back as
    /// mesh parts, and anything this build cannot rebuild is reported in
    /// <see cref="DocumentLoadResult.Warnings"/>.</summary>
    /// <exception cref="FormatException">The JSON is not an EngrCAD document, or is a
    /// version this build does not read.</exception>
    public static DocumentLoadResult Load(string json, DocumentLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DocumentReader.Read(json, options ?? new DocumentLoadOptions());
    }

    /// <summary>Reads a document from a file (see <see cref="Load"/>).</summary>
    public static DocumentLoadResult LoadFile(string path, DocumentLoadOptions? options = null) =>
        Load(File.ReadAllText(path), options);

    /// <summary>Writes this document to a file (see <see cref="Save"/>).</summary>
    public void SaveFile(string path, DocumentSaveOptions? options = null) =>
        File.WriteAllText(path, Save(options));
}

/// <summary>Options for <see cref="Document.Save"/>.</summary>
public sealed class DocumentSaveOptions
{
    /// <summary>
    /// Whether parts with no construction history embed their display mesh as a snapshot
    /// (default true — see <see cref="Document"/>). False writes those parts as an
    /// explicit "no geometry" record naming the reason, which loads as a warning and no
    /// part: a recipe-only file that is honest about what it left out, rather than a file
    /// that silently lost bodies.
    /// </summary>
    public bool EmbedGeometry { get; set; } = true;

    /// <summary>Whether attached <see cref="Part.Results"/> (simulation fields) are
    /// written (default true). They are the largest thing a document can carry, so a
    /// geometry-only file turns them off.</summary>
    public bool IncludeResults { get; set; } = true;

    /// <summary>Mesh quality for snapshot meshing; null uses the scene's own
    /// (<see cref="Scene.ResolveQuality"/>). An already-meshed part ignores it — the
    /// first caller's quality won, as everywhere else.</summary>
    public MeshQuality? Quality { get; set; }
}

/// <summary>Options for <see cref="Document.Load"/>.</summary>
public sealed class DocumentLoadOptions
{
    /// <summary>The registry that reconstructs saved features (default
    /// <see cref="FeatureRegistry.Default"/>); register your own feature types on a copy
    /// to make them loadable.</summary>
    public FeatureRegistry? Registry { get; set; }

    /// <summary>The <see cref="FeatureHistory.LoadHistory"/> hook for features the
    /// registry cannot construct (lambdas, <see cref="Shape"/> tools, catalogue
    /// components). Receives the part name as well, since one hook serves the whole
    /// document.</summary>
    public Func<string, FeatureRecord, Feature?>? ResolveOpaqueFeature { get; set; }

    /// <summary>Whether a history part is regenerated at load (default true). False loads
    /// the histories without running them — the fast path for a host that only wants the
    /// document structure — and such parts carry the last body their history produced,
    /// which for a freshly loaded history is none, so they are skipped with a warning.</summary>
    public bool Regenerate { get; set; } = true;
}

/// <summary>
/// The outcome of <see cref="Document.Load"/>: the rebuilt document, one warning per
/// thing the file described that this build could not fully restore, and the names of the
/// parts that came back as geometry SNAPSHOTS rather than as construction histories.
/// </summary>
/// <param name="Document">The rebuilt document.</param>
/// <param name="Warnings">One message per record that could not be fully restored.</param>
/// <param name="Snapshots">Parts loaded from an embedded mesh — present and correct, but
/// not parametric — named <c>"tab/part"</c>, the qualified spelling every part-taking tool
/// accepts. Part names are unique per TAB rather than per document, so a bare name would be
/// ambiguous the moment two tabs shared one; a part that ended up in no tab at all keeps its
/// bare name, since there is no tab to name.</param>
public sealed record DocumentLoadResult(
    Document Document, IReadOnlyList<string> Warnings, IReadOnlyList<string> Snapshots)
{
    /// <summary>True when every record in the file was restored (snapshots count as
    /// restored — a snapshot is what the file said, not a failure).</summary>
    public bool Complete => Warnings.Count == 0;

    /// <summary>The rebuilt scene (shorthand for <c>Document.Scene</c>).</summary>
    public Scene Scene => Document.Scene;
}

// ---------------------------------------------------------------------------
// Binary payloads
// ---------------------------------------------------------------------------

/// <summary>
/// Base64 packing for the bulk arrays a document embeds — mesh vertices and face indices,
/// simulation field values. Little-endian IEEE doubles and int32s, so a payload is exact
/// (a snapshot must reload bit-for-bit or the fixed-point contract is a fiction) and about
/// a third the size of the equivalent JSON number array.
/// </summary>
internal static class Payload
{
    internal static string Doubles(IReadOnlyList<double> values)
    {
        var bytes = new byte[values.Count * sizeof(double)];
        for (int i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)), values[i]);
        return Convert.ToBase64String(bytes);
    }

    internal static string Points(IReadOnlyList<Vector3d> points)
    {
        var bytes = new byte[points.Count * 3 * sizeof(double)];
        for (int i = 0; i < points.Count; i++)
        {
            var span = bytes.AsSpan(i * 3 * sizeof(double));
            BinaryPrimitives.WriteDoubleLittleEndian(span, points[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(span[sizeof(double)..], points[i].Y);
            BinaryPrimitives.WriteDoubleLittleEndian(span[(2 * sizeof(double))..], points[i].Z);
        }
        return Convert.ToBase64String(bytes);
    }

    internal static string Ints(IReadOnlyList<int> values)
    {
        var bytes = new byte[values.Count * sizeof(int)];
        for (int i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), values[i]);
        return Convert.ToBase64String(bytes);
    }

    internal static double[] ReadDoubles(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(double) != 0)
            throw new FormatException("a double payload's length is not a multiple of 8 bytes");
        var values = new double[bytes.Length / sizeof(double)];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)));
        return values;
    }

    internal static Vector3d[] ReadPoints(string base64)
    {
        double[] flat = ReadDoubles(base64);
        if (flat.Length % 3 != 0)
            throw new FormatException("a point payload's length is not a multiple of 3 doubles");
        var points = new Vector3d[flat.Length / 3];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector3d(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
        return points;
    }

    internal static int[] ReadInts(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(int) != 0)
            throw new FormatException("an int payload's length is not a multiple of 4 bytes");
        var values = new int[bytes.Length / sizeof(int)];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * sizeof(int)));
        return values;
    }

    /// <summary>A mesh as (positions, packed corners, face starts) — the same three
    /// arrays <see cref="HalfEdgeMesh.Build(IReadOnlyList{Vector3d}, ReadOnlySpan{int},
    /// ReadOnlySpan{int})"/> takes, so a snapshot rebuilds with vertex and face indices in
    /// exactly the order they were written and a re-save reproduces the payload byte for
    /// byte.</summary>
    internal static JsonObject SaveMesh(HalfEdgeMesh mesh)
    {
        var (positions, faces) = mesh.ToIndexed();
        var corners = new List<int>();
        var starts = new List<int>(faces.Count + 1) { 0 };
        foreach (var face in faces)
        {
            corners.AddRange(face);
            starts.Add(corners.Count);
        }
        return new JsonObject
        {
            ["vertices"] = Points(positions),
            ["corners"] = Ints(corners),
            ["faceStarts"] = Ints(starts),
        };
    }

    internal static HalfEdgeMesh LoadMesh(JsonElement element) =>
        HalfEdgeMesh.Build(
            ReadPoints(element.GetProperty("vertices").GetString() ?? ""),
            ReadInts(element.GetProperty("corners").GetString() ?? ""),
            ReadInts(element.GetProperty("faceStarts").GetString() ?? ""));
}

// ---------------------------------------------------------------------------
// Writer
// ---------------------------------------------------------------------------

internal static class DocumentWriter
{
    internal static string Write(Document document, DocumentSaveOptions options)
    {
        var scene = document.Scene;
        var quality = scene.ResolveQuality(options.Quality);

        // Reference tables. Parts and assemblies are SHARED (one part placed N times, one
        // sub-assembly nested in two parents), so they are written once under an id and
        // referenced from the tabs and occurrences. Both walks are deterministic — tab
        // order, then loose parts, then assemblies depth-first — so a document's ids are a
        // function of its structure and nothing else.
        // Part and Assembly are classes with no value equality, so the default comparer IS
        // reference identity — the same dedupe rule Scene.AllParts uses.
        var partIds = new Dictionary<Part, string>();
        var parts = new List<Part>();
        foreach (var part in scene.AllParts)
        {
            if (partIds.TryAdd(part, $"p{parts.Count}"))
                parts.Add(part);
        }

        var assemblyIds = new Dictionary<Assembly, string>();
        var assemblies = new List<Assembly>();
        foreach (var tab in scene.Tabs)
        {
            foreach (var assembly in tab.Assemblies)
                CollectAssemblies(assembly, assemblyIds, assemblies);
        }

        var root = new JsonObject
        {
            ["format"] = Document.Format,
            ["version"] = Document.Version,
        };
        if (scene.HasExplicitOptions)
            root["quality"] = SaveQuality(scene.Options);

        var partArray = new JsonArray();
        foreach (var part in parts)
            partArray.Add(SavePart(part, partIds[part], options, quality));
        root["parts"] = partArray;

        if (assemblies.Count > 0)
        {
            var assemblyArray = new JsonArray();
            foreach (var assembly in assemblies)
                assemblyArray.Add(SaveAssembly(assembly, assemblyIds, partIds));
            root["assemblies"] = assemblyArray;
        }

        var tabArray = new JsonArray();
        foreach (var tab in scene.Tabs)
        {
            var record = new JsonObject { ["name"] = tab.Name };
            if (tab.Parts.Count > 0)
                record["parts"] = new JsonArray([.. tab.Parts.Select(p => (JsonNode)partIds[p])]);
            if (tab.Assemblies.Count > 0)
                record["assemblies"] = new JsonArray([.. tab.Assemblies.Select(a => (JsonNode)assemblyIds[a])]);
            tabArray.Add(record);
        }
        root["tabs"] = tabArray;

        var mateArray = new JsonArray();
        foreach (var set in document.Mates)
        {
            if (!assemblyIds.TryGetValue(set.Assembly, out string? id))
                continue;   // a mate set whose assembly is not in this scene has nothing to name
            // The mate vocabulary stays in MatePersistence: parse its output back in
            // rather than restating it here, so the two files cannot spell a mate
            // differently (the same rule SaveParameters and SaveHistory follow).
            var mates = JsonNode.Parse(set.SaveMates())!.AsObject();
            mates["assembly"] = id;
            mateArray.Add(mates);
        }
        if (mateArray.Count > 0)
            root["mates"] = mateArray;

        return root.ToJsonString(FeatureHistory.JsonOptions);
    }

    private static void CollectAssemblies(
        Assembly assembly, Dictionary<Assembly, string> ids, List<Assembly> into)
    {
        if (!ids.TryAdd(assembly, $"a{into.Count}"))
            return;
        into.Add(assembly);
        foreach (var occurrence in assembly.Occurrences)
        {
            if (occurrence.SubAssembly is { } sub)
                CollectAssemblies(sub, ids, into);
        }
    }

    private static JsonObject SaveQuality(MeshQuality quality)
    {
        var json = new JsonObject
        {
            ["segmentsPerCircle"] = quality.SegmentsPerCircle,
            ["curveSamples"] = quality.CurveSamples,
            ["sdfResolution"] = quality.SdfResolution,
        };
        if (quality.Tessellation is { } adaptive)
        {
            var tessellation = new JsonObject
            {
                ["minSegments"] = adaptive.MinSegments,
                ["maxSegments"] = adaptive.MaxSegments,
            };
            if (adaptive.MaxAngleDegrees is { } angle)
                tessellation["maxAngleDegrees"] = angle;
            if (adaptive.MaxChordDeviation is { } chord)
                tessellation["maxChordDeviation"] = chord;
            json["tessellation"] = tessellation;
        }
        return json;
    }

    private static JsonObject SavePart(
        Part part, string id, DocumentSaveOptions options, MeshQuality quality)
    {
        var json = new JsonObject
        {
            ["id"] = id,
            ["name"] = part.Name,
        };
        if (part.Color is { } color)
            json["color"] = new JsonArray(color.R, color.G, color.B);
        if (!part.Transform.Equals(Matrix4d.Identity))
            json["transform"] = SaveMatrix(part.Transform);
        if (part.DisplayMode != DisplayMode.Shaded)
            json["displayMode"] = part.DisplayMode.ToString();
        if (!part.ClippedBySection)
            json["clippedBySection"] = false;
        if (part.Hidden)
            json["hidden"] = true;
        if (part.Ghost)
            json["ghost"] = true;
        if (part.Isolated)
            json["isolated"] = true;
        // A catalogue component is a code object with no serialized form; its DESIGNATION
        // is written so a reloaded BOM can still separate bought-in from made, and the
        // load warns that the component itself did not come back.
        if (part.Hardware is { } hardware)
            json["hardware"] = hardware.Designation;
        if (part.Material is { } material)
            json["material"] = SaveMaterial(material);
        if (part.CutLength is { } cutLength)
            json["cutLength"] = cutLength; // write-only-when-stated: files without frames stay byte-identical
        if (part.Configurations is { Count: > 0 } configurations)
            json["configurations"] = SaveConfigurations(configurations);

        json["geometry"] = SaveGeometry(part, options, quality);

        var annotations = SaveAnnotations(part);
        if (annotations.Count > 0)
            json["annotations"] = annotations;

        if (options.IncludeResults && part.Results.Count > 0)
        {
            var results = new JsonArray();
            foreach (var field in part.Results)
            {
                results.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["units"] = field.Units,
                    ["components"] = field.Components,
                    ["values"] = Payload.Doubles(field.Values),
                });
            }
            json["results"] = results;

            // The time axes over those results, write-only-when-stated: a document
            // using no sequences is byte-identical to one saved before they existed.
            if (part.ResultSequences.Count > 0)
            {
                var sequences = new JsonArray();
                foreach (var sequence in part.ResultSequences)
                {
                    var steps = new JsonArray();
                    foreach (var (resultName, seconds) in sequence.Steps)
                    {
                        steps.Add(new JsonObject
                        {
                            ["result"] = resultName,
                            ["seconds"] = seconds,
                        });
                    }
                    sequences.Add(new JsonObject
                    {
                        ["name"] = sequence.Name,
                        ["steps"] = steps,
                    });
                }
                json["resultSequences"] = sequences;
            }
        }

        if (part.FieldDisplay is { } display)
        {
            var record = new JsonObject
            {
                ["field"] = display.Field,
                ["colorMap"] = display.ColorMap.ToString(),
            };
            if (display.Range is { } range)
                record["range"] = new JsonArray(range.Min, range.Max);
            if (display.Deform is { } deform)
            {
                record["deform"] = deform;
                record["deformScale"] = display.DeformScale;
                record["showUndeformed"] = display.ShowUndeformed;
            }
            if (display.LogScale)
                record["logScale"] = true;           // write-only-when-set
            json["fieldDisplay"] = record;
        }

        return json;
    }

    /// <summary>
    /// A part's named parameter sets. The values ride as a nested OBJECT rather than as an
    /// escaped string — one file, diffable, and it is what makes the fixed point trivial:
    /// <see cref="Configuration"/> normalizes whatever text it is handed through the shared
    /// JSON options, so the tree a second save writes is the tree the first one wrote,
    /// whatever indentation the document happened to give it.
    /// <para>Written ONLY when the part has configurations, so every document that uses none
    /// is byte-identical to what it always was.</para>
    /// </summary>
    private static JsonObject SaveConfigurations(ConfigurationSet configurations)
    {
        var record = new JsonObject();
        // The ACTIVE name is document state: it says which set the history's current values
        // came from, and a reload that dropped it would leave a model whose values exactly
        // match "M6" unable to say so. It round-trips, which is the whole test for whether
        // an informational field belongs in the file.
        if (configurations.Active is { } active)
            record["active"] = active;
        var items = new JsonArray();
        foreach (var configuration in configurations)
        {
            items.Add(new JsonObject
            {
                ["name"] = configuration.Name,
                ["parameters"] = JsonNode.Parse(configuration.Parameters),
            });
        }
        record["items"] = items;
        return record;
    }

    /// <summary>
    /// A material as JSON. Every property is written only when it is stated, which is what
    /// keeps the save-load-save fixed point byte-identical for the common document material
    /// (a name, a density and nothing else) — and what makes an unstated modulus survive the
    /// round trip as unstated rather than coming back as a zero that means the same thing but
    /// prints differently. Densities are in tonne/mm³, the one convention
    /// (<c>ModelUnits</c>).
    /// </summary>
    private static JsonObject SaveMaterial(Material material)
    {
        var json = new JsonObject { ["name"] = material.Name };
        if (material.Density != 0)
            json["density"] = material.Density;
        if (material.YoungsModulus != 0)
            json["youngsModulus"] = material.YoungsModulus;
        if (material.PoissonsRatio != 0)
            json["poissonsRatio"] = material.PoissonsRatio;
        if (material.ThermalConductivity != 0)
            json["thermalConductivity"] = material.ThermalConductivity;
        if (material.SpecificHeat != 0)
            json["specificHeat"] = material.SpecificHeat;
        if (material.ThermalExpansion != 0)
            json["thermalExpansion"] = material.ThermalExpansion;
        if (material.Color is { } color)
            json["color"] = new JsonArray(color.R, color.G, color.B);
        return json;
    }

    private static JsonObject SaveGeometry(
        Part part, DocumentSaveOptions options, MeshQuality quality)
    {
        if (part.History is { } history)
        {
            return new JsonObject
            {
                ["kind"] = "history",
                ["history"] = JsonNode.Parse(history.SaveHistory()),
            };
        }
        if (!options.EmbedGeometry)
        {
            return new JsonObject
            {
                ["kind"] = "none",
                ["reason"] = $"{part.Geometry.GetType().Name} geometry has no construction history, " +
                    "and this document was saved with EmbedGeometry off.",
            };
        }
        try
        {
            // "kind" goes in FIRST: JsonObject writes in insertion order, and a reader
            // (human or otherwise) should meet the discriminator before a kilobyte of
            // base64.
            //
            // There is deliberately NO "source" field naming the geometry type this
            // snapshot came from, tempting as it is: a BoxShape snapshot reloads as a
            // HalfEdgeMesh, so the writer would emit a different name on the second save
            // and the fixed point would fail on a field nothing reads. An informational
            // field either round-trips or does not belong in the file;
            // DocumentLoadResult.Snapshots carries the half that is actionable.
            var snapshot = new JsonObject { ["kind"] = "mesh" };
            foreach (var entry in Payload.SaveMesh(part.GetMesh(quality)))
                snapshot[entry.Key] = entry.Value?.DeepClone();
            return snapshot;
        }
        catch (Exception exception)
        {
            // A part whose geometry cannot even be meshed is recorded as such rather than
            // failing the whole save: the document still describes the model honestly, and
            // the load says which part has no body.
            return new JsonObject
            {
                ["kind"] = "none",
                ["reason"] = $"the display mesh could not be produced " +
                    $"({exception.GetType().Name}: {exception.Message})",
            };
        }
    }

    private static JsonArray SaveAnnotations(Part part)
    {
        var array = new JsonArray();
        foreach (var annotation in part.Annotations)
        {
            var record = annotation.SaveJson()?.AsObject();
            if (record is null)
            {
                // A selector-backed dimension measures through a lambda, which only its own
                // code can rebuild — the FeatureHistory/MateSet opaque convention.
                array.Add(new JsonObject
                {
                    ["kind"] = "opaque",
                    ["type"] = annotation.GetType().Name,
                });
                continue;
            }
            if (annotation.Label is { } label)
                record["label"] = label;
            if (annotation.Offset != Vector3d.Zero)
                record["offset"] = SaveVector(annotation.Offset);
            if (annotation.Tolerance is { } tolerance)
            {
                record["tolerance"] = new JsonArray(tolerance.Plus, tolerance.Minus);
            }
            array.Add(record);
        }
        return array;
    }

    private static JsonObject SaveAssembly(
        Assembly assembly, Dictionary<Assembly, string> assemblyIds, Dictionary<Part, string> partIds)
    {
        var occurrences = new JsonArray();
        foreach (var occurrence in assembly.Occurrences)
        {
            var record = new JsonObject { ["name"] = occurrence.Name };
            if (occurrence.Part is { } part)
                record["part"] = partIds[part];
            else
                record["assembly"] = assemblyIds[occurrence.SubAssembly!];
            var frame = occurrence.Frame;
            if (!frame.Origin.Equals(Vector3d.Zero) || !frame.X.Equals(Vector3d.UnitX) || !frame.Y.Equals(Vector3d.UnitY))
            {
                record["frame"] = new JsonObject
                {
                    ["origin"] = SaveVector(frame.Origin),
                    ["x"] = SaveVector(frame.X),
                    ["y"] = SaveVector(frame.Y),
                };
            }
            if (occurrence.ExplodeOffset is { } offset)
                record["explode"] = SaveVector(offset);
            // Written only when a dogleg was actually designed, so every existing file
            // stays byte-identical and the fixed point is untouched.
            if (occurrence.ExplodePath.Count > 0)
            {
                var waypoints = new JsonArray();
                foreach (var waypoint in occurrence.ExplodePath)
                    waypoints.Add(SaveVector(waypoint));
                record["explodePath"] = waypoints;
            }
            occurrences.Add(record);
        }
        return new JsonObject
        {
            ["id"] = assemblyIds[assembly],
            ["name"] = assembly.Name,
            ["occurrences"] = occurrences,
        };
    }

    internal static JsonArray SaveVector(in Vector3d v) => new(v.X, v.Y, v.Z);

    private static JsonArray SaveMatrix(in Matrix4d m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}

// ---------------------------------------------------------------------------
// Reader
// ---------------------------------------------------------------------------

internal static class DocumentReader
{
    internal static DocumentLoadResult Read(string json, DocumentLoadOptions options)
    {
        using var jsonDocument = JsonDocument.Parse(json);
        var root = jsonDocument.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format)
            || format.GetString() != Document.Format)
            throw new FormatException(
                $"not an EngrCAD document (expected a JSON object with \"format\": \"{Document.Format}\")");
        int version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        if (version < 1 || version > Document.Version)
            throw new FormatException(
                $"document version {version} is not readable by this build (it reads 1..{Document.Version})");

        var warnings = new List<string>();
        // The PARTS that came back as snapshots, in file order; qualified with their tab
        // names once the tabs are wired (see Qualify, below).
        var snapshots = new List<Part>();

        var scene = root.TryGetProperty("quality", out var quality)
            ? new Scene(LoadQuality(quality))
            : new Scene();
        var document = new Document(scene);

        // Parts first (assemblies reference them), then assemblies, then tabs.
        var parts = new Dictionary<string, Part>(StringComparer.Ordinal);
        if (root.TryGetProperty("parts", out var partArray))
        {
            foreach (var element in partArray.EnumerateArray())
            {
                string id = element.GetProperty("id").GetString() ?? "";
                string name = element.GetProperty("name").GetString() ?? id;
                if (LoadPart(element, name, options, warnings, snapshots) is { } part)
                    parts[id] = part;
            }
        }

        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var assemblyRecords = new List<JsonElement>();
        if (root.TryGetProperty("assemblies", out var assemblyArray))
        {
            foreach (var element in assemblyArray.EnumerateArray())
            {
                string id = element.GetProperty("id").GetString() ?? "";
                assemblies[id] = new Assembly(element.GetProperty("name").GetString() ?? id);
                assemblyRecords.Add(element.Clone());
            }
            // Populate CHILDREN FIRST (the table is depth-first pre-order, so a nested
            // assembly always follows its parent): Assembly.Add's cycle check reads the
            // sub-assembly's own contents, and only a populated sub-assembly can answer.
            for (int i = assemblyRecords.Count - 1; i >= 0; i--)
                Populate(assemblyRecords[i], assemblies, parts, warnings);
        }

        if (root.TryGetProperty("tabs", out var tabArray))
        {
            foreach (var element in tabArray.EnumerateArray())
            {
                string name = element.GetProperty("name").GetString() ?? "Model";
                var tab = scene.AddTab(name);
                if (element.TryGetProperty("parts", out var tabParts))
                {
                    foreach (var reference in tabParts.EnumerateArray())
                    {
                        string id = reference.GetString() ?? "";
                        if (parts.TryGetValue(id, out var part))
                            tab.Add(part);
                        else
                            warnings.Add($"tab '{name}': part '{id}' did not load and was dropped");
                    }
                }
                if (element.TryGetProperty("assemblies", out var tabAssemblies))
                {
                    foreach (var reference in tabAssemblies.EnumerateArray())
                    {
                        string id = reference.GetString() ?? "";
                        if (assemblies.TryGetValue(id, out var assembly))
                            tab.Add(assembly);
                        else
                            warnings.Add($"tab '{name}': assembly '{id}' is not in the file");
                    }
                }
            }
        }

        if (root.TryGetProperty("mates", out var mateArray))
        {
            foreach (var element in mateArray.EnumerateArray())
            {
                string id = element.TryGetProperty("assembly", out var a) ? a.GetString() ?? "" : "";
                if (!assemblies.TryGetValue(id, out var assembly))
                {
                    warnings.Add($"mate set for assembly '{id}': no such assembly in the file");
                    continue;
                }
                var set = new MateSet(assembly);
                // Hand the record straight back to MatePersistence — one mate vocabulary.
                foreach (string warning in set.LoadMates(element.GetRawText()))
                    warnings.Add($"assembly '{assembly.Name}': {warning}");
                document.Mates.Add(set);
            }
        }

        return new DocumentLoadResult(document, warnings, [.. snapshots.Select(p => Qualify(scene, p))]);
    }

    /// <summary>
    /// How a load report names one part: <c>"tab/part"</c> — the qualified spelling every
    /// part-taking tool already accepts ("Part name, as reported by list_parts; 'tab/part'
    /// also works").
    /// <para>The bare name is not enough, because names are unique per TAB: a document with
    /// a "housing" in two tabs would report the same string twice and a host acting on it
    /// would edit whichever it found first. Qualifying is the whole reason the snapshot list
    /// is collected as <see cref="Part"/> references and resolved here rather than at load
    /// time — parts are read before the tabs that reference them, so at the point a
    /// snapshot is recorded its tab is not yet known.</para>
    /// <para>A part in NO tab (loaded, then referenced by nothing) keeps its bare name:
    /// there is no tab to name, and inventing one would be worse than saying less.</para>
    /// </summary>
    private static string Qualify(Scene scene, Part part)
    {
        // AllParts, not the tab's loose parts: a snapshot inside an assembly is still that
        // tab's part, and it is the same walk describe_part resolves a name through.
        foreach (var tab in scene.Tabs)
        {
            if (tab.AllParts.Contains(part))
                return $"{tab.Name}/{part.Name}";
        }
        return part.Name;
    }

    private static MeshQuality LoadQuality(JsonElement element)
    {
        var quality = new MeshQuality();
        if (element.TryGetProperty("segmentsPerCircle", out var segments))
            quality.SegmentsPerCircle = segments.GetInt32();
        if (element.TryGetProperty("curveSamples", out var curve))
            quality.CurveSamples = curve.GetInt32();
        if (element.TryGetProperty("sdfResolution", out var sdf))
            quality.SdfResolution = sdf.GetInt32();
        if (element.TryGetProperty("tessellation", out var tessellation))
        {
            var adaptive = new TessellationQuality();
            if (tessellation.TryGetProperty("minSegments", out var min))
                adaptive.MinSegments = min.GetInt32();
            if (tessellation.TryGetProperty("maxSegments", out var max))
                adaptive.MaxSegments = max.GetInt32();
            if (tessellation.TryGetProperty("maxAngleDegrees", out var angle))
                adaptive.MaxAngleDegrees = angle.GetDouble();
            if (tessellation.TryGetProperty("maxChordDeviation", out var chord))
                adaptive.MaxChordDeviation = chord.GetDouble();
            quality.Tessellation = adaptive;
        }
        return quality;
    }

    /// <summary>A material back from <c>DocumentWriter.SaveMaterial</c>. An absent property
    /// reads as zero, which is exactly what "not stated" means to <see cref="Material"/> —
    /// so the writer's write-only-when-stated rule and this are one convention, not two.</summary>
    private static Material LoadMaterial(JsonElement element) =>
        new(
            element.GetProperty("name").GetString() ?? "material",
            Number(element, "youngsModulus"),
            Number(element, "poissonsRatio"),
            Number(element, "density"),
            Number(element, "thermalConductivity"),
            Number(element, "specificHeat"),
            Number(element, "thermalExpansion"),
            element.TryGetProperty("color", out var c)
                ? new PartColor(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle())
                : null);

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetDouble() : 0;

    private static Part? LoadPart(
        JsonElement element, string name, DocumentLoadOptions options,
        List<string> warnings, List<Part> snapshots)
    {
        var color = element.TryGetProperty("color", out var c)
            ? new PartColor(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle())
            : (PartColor?)null;
        var transform = element.TryGetProperty("transform", out var t) ? LoadMatrix(t) : (Matrix4d?)null;

        var geometry = element.GetProperty("geometry");
        string kind = geometry.GetProperty("kind").GetString() ?? "none";
        Part part;
        switch (kind)
        {
            case "history":
            {
                var loaded = FeatureHistory.LoadHistory(
                    geometry.GetProperty("history").GetRawText(),
                    options.Registry,
                    options.ResolveOpaqueFeature is { } hook ? record => hook(name, record) : null);
                foreach (string warning in loaded.Warnings)
                    warnings.Add($"part '{name}': {warning}");
                if (!options.Regenerate)
                {
                    warnings.Add($"part '{name}': loaded without regenerating, so it has no body");
                    return null;
                }
                try
                {
                    part = new Part(name, loaded.History, color, transform);
                }
                catch (Exception exception)
                {
                    warnings.Add($"part '{name}': its history produced no body ({exception.Message})");
                    return null;
                }
                break;
            }
            case "mesh":
                try
                {
                    part = new Part(name, Payload.LoadMesh(geometry), color, transform);
                }
                catch (Exception exception)
                {
                    warnings.Add($"part '{name}': its embedded mesh could not be rebuilt ({exception.Message})");
                    return null;
                }
                // Recorded as the PART, not its name: the report qualifies each with its
                // tab, and parts load before the tabs that reference them.
                snapshots.Add(part);
                break;
            default:
                warnings.Add($"part '{name}': no geometry in the file" +
                    (geometry.TryGetProperty("reason", out var reason) ? $" — {reason.GetString()}" : ""));
                return null;
        }

        if (element.TryGetProperty("displayMode", out var mode))
            part.DisplayMode = Enum.Parse<DisplayMode>(mode.GetString() ?? "Shaded", ignoreCase: true);
        if (element.TryGetProperty("clippedBySection", out var clipped))
            part.ClippedBySection = clipped.GetBoolean();
        if (element.TryGetProperty("hidden", out var hidden))
            part.Hidden = hidden.GetBoolean();
        if (element.TryGetProperty("ghost", out var ghost))
            part.Ghost = ghost.GetBoolean();
        if (element.TryGetProperty("isolated", out var isolated))
            part.Isolated = isolated.GetBoolean();
        if (element.TryGetProperty("material", out var material))
            part.Material = LoadMaterial(material);
        if (element.TryGetProperty("cutLength", out var cutLength))
            part.CutLength = cutLength.GetDouble();
        if (element.TryGetProperty("configurations", out var configurations))
            LoadConfigurations(configurations, part, name, warnings);
        if (element.TryGetProperty("hardware", out var hardware))
        {
            warnings.Add($"part '{name}': it was placed from catalogue item " +
                $"'{hardware.GetString()}', which is a code object and did not come back " +
                "(the geometry did; the BOM will list it as manufactured)");
        }

        if (element.TryGetProperty("annotations", out var annotations))
        {
            foreach (var record in annotations.EnumerateArray())
            {
                if (LoadAnnotation(record, name, warnings) is { } annotation)
                    part.Annotate(annotation);
            }
        }

        if (element.TryGetProperty("results", out var results))
        {
            foreach (var record in results.EnumerateArray())
            {
                try
                {
                    part.AddResult(new MeshField(
                        record.GetProperty("name").GetString() ?? "field",
                        record.TryGetProperty("units", out var units) ? units.GetString() ?? "" : "",
                        record.TryGetProperty("components", out var components) ? components.GetInt32() : 1,
                        Payload.ReadDoubles(record.GetProperty("values").GetString() ?? "")));
                }
                catch (Exception exception)
                {
                    warnings.Add($"part '{name}': a result could not be rebuilt ({exception.Message})");
                }
            }
        }

        if (element.TryGetProperty("resultSequences", out var resultSequences))
        {
            foreach (var record in resultSequences.EnumerateArray())
            {
                try
                {
                    var steps = new List<(string, double)>();
                    foreach (var step in record.GetProperty("steps").EnumerateArray())
                    {
                        steps.Add((
                            step.GetProperty("result").GetString() ?? "",
                            step.GetProperty("seconds").GetDouble()));
                    }
                    part.RestoreResultSequence(new ResultSequence(
                        record.GetProperty("name").GetString() ?? "", steps));
                }
                catch (Exception exception)
                {
                    warnings.Add($"part '{name}': a result sequence could not be rebuilt ({exception.Message})");
                }
            }
        }

        if (element.TryGetProperty("fieldDisplay", out var display))
        {
            part.FieldDisplay = new FieldDisplay
            {
                Field = display.GetProperty("field").GetString() ?? "",
                ColorMap = display.TryGetProperty("colorMap", out var map)
                    ? Enum.Parse<FieldColorMap>(map.GetString() ?? "Viridis", ignoreCase: true)
                    : FieldColorMap.Viridis,
                Range = display.TryGetProperty("range", out var range)
                    ? new FieldRange(range[0].GetDouble(), range[1].GetDouble())
                    : null,
                Deform = display.TryGetProperty("deform", out var deform) ? deform.GetString() : null,
                DeformScale = display.TryGetProperty("deformScale", out var scale) ? scale.GetDouble() : 1,
                ShowUndeformed = !display.TryGetProperty("showUndeformed", out var undeformed)
                    || undeformed.GetBoolean(),
                LogScale = display.TryGetProperty("logScale", out var logScale)
                    && logScale.GetBoolean(),
            };
        }

        return part;
    }

    /// <summary>
    /// Configurations back onto a loaded part. Two rules carry it.
    /// <para><b>The active NAME is restored without applying anything.</b> The history was
    /// saved carrying that configuration's values, so re-applying would be a no-op that costs
    /// a regeneration — and it would also be WRONG for a document saved while modified
    /// (active "M6", one parameter since edited), which must come back modified rather than
    /// silently snapped back onto "M6".</para>
    /// <para><b>A configuration naming a feature this build cannot find is kept, not
    /// dropped.</b> Staleness is reported by <see cref="ConfigurationSet.Validate"/> and by
    /// <see cref="ConfigurationSet.Activate"/>, at the moment it matters; the feature may yet
    /// come back (an undone removal), and a file that quietly loses a variant is worse than
    /// one that reports it.</para>
    /// </summary>
    private static void LoadConfigurations(
        JsonElement element, Part part, string name, List<string> warnings)
    {
        if (part.Configurations is not { } set)
        {
            warnings.Add($"part '{name}': it carries configurations but came back without a "
                + "feature history, so there is nothing for them to drive");
            return;
        }
        if (element.TryGetProperty("items", out var items))
        {
            foreach (var record in items.EnumerateArray())
            {
                string configurationName = record.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "configuration"
                    : "configuration";
                try
                {
                    set.Add(new Configuration(
                        configurationName, record.GetProperty("parameters").GetRawText()));
                }
                catch (Exception exception)
                {
                    warnings.Add($"part '{name}': configuration '{configurationName}' "
                        + $"could not be rebuilt ({exception.Message})");
                }
            }
        }
        if (element.TryGetProperty("active", out var active) && active.GetString() is { } activeName)
        {
            if (set.Find(activeName) is null)
            {
                warnings.Add($"part '{name}': the active configuration '{activeName}' "
                    + "is not among the ones in the file");
            }
            else
            {
                set.SetActiveName(activeName);
            }
        }
    }

    private static Annotation? LoadAnnotation(JsonElement element, string partName, List<string> warnings)
    {
        string kind = element.GetProperty("kind").GetString() ?? "opaque";
        string? label = element.TryGetProperty("label", out var l) ? l.GetString() : null;
        var offset = element.TryGetProperty("offset", out var o) ? LoadVector(o) : Vector3d.Zero;
        ToleranceSpec? tolerance = null;
        if (element.TryGetProperty("tolerance", out var t))
        {
            double plus = t[0].GetDouble(), minus = t[1].GetDouble();
            // Symmetric() stores one value twice, and the Suffix rule reads bit-equality,
            // so rebuilding through the same factory is what keeps the text identical.
            tolerance = plus == minus ? ToleranceSpec.Symmetric(plus) : ToleranceSpec.Limits(plus, minus);
        }

        Annotation? annotation = kind switch
        {
            "linear" => new LinearDimension(
                LoadVector(element.GetProperty("a")), LoadVector(element.GetProperty("b")))
            { Label = label, Tolerance = tolerance },
            "angular" => new AngularDimension(
                LoadVector(element.GetProperty("vertex")),
                LoadVector(element.GetProperty("a")),
                LoadVector(element.GetProperty("b")))
            { Label = label, Tolerance = tolerance },
            "note" => new LeaderNote(
                LoadVector(element.GetProperty("anchor")), element.GetProperty("text").GetString() ?? "")
            { Label = label },
            "datum" => new DatumLabel(
                LoadVector(element.GetProperty("anchor")), element.GetProperty("letter").GetString() ?? "A")
            { Label = label },
            _ => null,
        };
        if (annotation is null)
        {
            warnings.Add($"part '{partName}': a " +
                (element.TryGetProperty("type", out var type) ? type.GetString() : kind) +
                " annotation measures through a selector, which only its own code can rebuild");
            return null;
        }
        annotation.Offset = offset;
        return annotation;
    }

    private static void Populate(
        JsonElement element, Dictionary<string, Assembly> assemblies,
        Dictionary<string, Part> parts, List<string> warnings)
    {
        var assembly = assemblies[element.GetProperty("id").GetString() ?? ""];
        foreach (var record in element.GetProperty("occurrences").EnumerateArray())
        {
            string occurrenceName = record.GetProperty("name").GetString() ?? "item";
            var frame = record.TryGetProperty("frame", out var f)
                ? Frame3d.FromOrthonormal(
                    LoadVector(f.GetProperty("origin")),
                    LoadVector(f.GetProperty("x")),
                    LoadVector(f.GetProperty("y")))
                : Frame3d.WorldXY;
            Occurrence? occurrence = null;
            if (record.TryGetProperty("part", out var partRef))
            {
                string id = partRef.GetString() ?? "";
                if (parts.TryGetValue(id, out var part))
                {
                    occurrence = assembly.Add(part, frame, occurrenceName);
                }
                else
                {
                    warnings.Add($"assembly '{assembly.Name}': occurrence '{occurrenceName}' " +
                        $"references part '{id}', which did not load");
                }
            }
            else if (record.TryGetProperty("assembly", out var assemblyRef))
            {
                string id = assemblyRef.GetString() ?? "";
                if (assemblies.TryGetValue(id, out var sub))
                {
                    occurrence = assembly.Add(sub, frame, occurrenceName);
                }
                else
                {
                    warnings.Add($"assembly '{assembly.Name}': occurrence '{occurrenceName}' " +
                        $"references assembly '{id}', which is not in the file");
                }
            }
            if (occurrence is not null && record.TryGetProperty("explode", out var explode))
                occurrence.ExplodeOffset = LoadVector(explode);
            if (occurrence is not null && record.TryGetProperty("explodePath", out var path))
            {
                foreach (var waypoint in path.EnumerateArray())
                    occurrence.ExplodePath.Add(LoadVector(waypoint));
            }
        }
    }

    internal static Vector3d LoadVector(JsonElement element) =>
        new(element[0].GetDouble(), element[1].GetDouble(), element[2].GetDouble());

    private static Matrix4d LoadMatrix(JsonElement e) => new(
        e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble(), e[3].GetDouble(),
        e[4].GetDouble(), e[5].GetDouble(), e[6].GetDouble(), e[7].GetDouble(),
        e[8].GetDouble(), e[9].GetDouble(), e[10].GetDouble(), e[11].GetDouble(),
        e[12].GetDouble(), e[13].GetDouble(), e[14].GetDouble(), e[15].GetDouble());
}
