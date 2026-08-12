using System.Text.Json;
using System.Text.Json.Nodes;

namespace EngrCAD.Ecad;

/// <summary>
/// The Gerber Job File (<c>.gbrjob</c>) — the JSON "project file" a modern fab reads beside a Gerber X2
/// set to identify the whole package at once: the board size and thickness, how many copper layers, the
/// surface finish, and every file with its <c>FileFunction</c> (which Gerber is which). It is the
/// companion of the X2 <c>%TF.FileFunction%</c> attribute, one level up — the same layer roles, gathered
/// into one manifest.
///
/// <para><b>Deterministic by construction.</b> Two clock-salted fields the spec allows —
/// <c>CreationDate</c> and the project <c>GUID</c> — are OMITTED (both optional for a loader), so writing
/// the same board twice is byte-identical (the same reasoning that keeps <c>PdfDrawing</c> free of a
/// <c>/Info</c> date). What it carries is a pure function of the board and the file set.</para>
///
/// <para>This is a pure JSON FORMATTER over explicit inputs; <see cref="PcbGerberExport"/> owns the
/// mapping from a fabrication set to these inputs (the file names and their roles).</para>
/// </summary>
public static class GerberJobFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Builds the <c>.gbrjob</c> JSON text.</summary>
    /// <param name="projectName">The board / project name (<c>GeneralSpecs.ProjectId.Name</c>).</param>
    /// <param name="sizeX">The board bounding-box width (mm).</param>
    /// <param name="sizeY">The board bounding-box height (mm).</param>
    /// <param name="copperLayerCount">The number of copper layers (<c>GeneralSpecs.LayerNumber</c>).</param>
    /// <param name="boardThickness">The finished board thickness (mm).</param>
    /// <param name="finish">The surface finish name, or null to omit it.</param>
    /// <param name="files">Each Gerber file's name and its <c>FileFunction</c> (e.g.
    /// <c>("board-Top.gbr", "Copper,L1,Top")</c>), in write order.</param>
    public static string Build(
        string projectName, double sizeX, double sizeY, int copperLayerCount,
        double boardThickness, string? finish,
        IReadOnlyList<(string FileName, string FileFunction)> files)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        ArgumentNullException.ThrowIfNull(files);

        var general = new JsonObject
        {
            ["ProjectId"] = new JsonObject { ["Name"] = projectName, ["Revision"] = "1" },
            ["Size"] = new JsonObject { ["X"] = sizeX, ["Y"] = sizeY },
            ["LayerNumber"] = copperLayerCount,
            ["BoardThickness"] = boardThickness,
        };
        if (!string.IsNullOrEmpty(finish))
            general["Finish"] = finish;

        var fileArray = new JsonArray();
        foreach (var (fileName, fileFunction) in files)
            fileArray.Add(new JsonObject
            {
                ["Path"] = fileName,
                ["FileFunction"] = fileFunction,
                ["FilePolarity"] = "Positive",
            });

        var root = new JsonObject
        {
            // No CreationDate / GUID — both optional and clock/random-salted, so the file is a
            // deterministic function of the board (a byte fixed point, the repo's fab-file rule).
            ["Header"] = new JsonObject
            {
                ["GenerationSoftware"] = new JsonObject
                {
                    ["Vendor"] = "EngrCAD",
                    ["Application"] = "EngrCAD",
                },
            },
            ["GeneralSpecs"] = general,
            ["FilesAttributes"] = fileArray,
        };
        return root.ToJsonString(Options);
    }
}
