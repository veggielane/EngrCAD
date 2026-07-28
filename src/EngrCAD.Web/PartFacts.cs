using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Web;

/// <summary>
/// The properties panel's facts for one model-tree row, as a value — the browser's
/// mirror of the desktop <c>SceneHost.ShowProperties</c>, kept a pure function so the
/// row-to-facts mapping is testable instead of read off a rendered panel.
///
/// <para>The mesh-derived numbers (faces, closed, volume, area) deliberately gate on
/// <c>Part.HasMesh</c>, the desktop's rule: asking for the mesh here would compute one
/// for a part the loader is still working on (or was never queued), so an unprepared
/// part reports its status instead and the numbers appear when its load lands.</para>
/// </summary>
public static class PartFacts
{
    /// <summary>The facts for a tree row, in display order.</summary>
    /// <param name="row">The selected row (assembly headers get a count, parts the
    /// desktop's kind/faces/closed/volume/area/size facts).</param>
    /// <param name="instance">The placed instance the row addresses, or null when it
    /// has none (a failed part, an assembly header).</param>
    public static IReadOnlyList<(string Label, string Value)> For(
        SceneTreeRow row, PartInstance? instance)
    {
        ArgumentNullException.ThrowIfNull(row);
        var facts = new List<(string, string)>();

        if (row.Kind == SceneTreeRowKind.Assembly)
        {
            facts.Add(("Name", row.Path));
            facts.Add(("Kind", "assembly"));
            return facts;
        }

        var part = row.Part!;
        facts.Add(("Name", row.Path));
        if (row.Path != part.Name)
            facts.Add(("Part", part.Name));
        facts.Add(("Kind", part.Geometry switch
        {
            Shape => "Shape (unified)",
            BrepSolid => "B-Rep solid",
            HalfEdgeMesh => "mesh",
            Sdf => "implicit (SDF)",
            _ => part.Geometry.GetType().Name,
        }));
        facts.Add(("Display", part.DisplayMode.ToString().ToLowerInvariant()));

        if (row.Failure is { } failure)
        {
            facts.Add(("Status", $"failed to mesh — {failure}"));
            return facts;
        }
        if (!part.HasMesh)
        {
            facts.Add(("Status", "meshing..."));
            return facts;
        }

        var mesh = part.GetMesh();
        facts.Add(("Faces", mesh.FaceCount.ToString("N0")));
        facts.Add(("Closed", mesh.IsClosed ? "yes" : "no"));
        facts.Add(("Volume", mesh.IsClosed ? mesh.Volume().ToString("G6") : "— (open)"));
        facts.Add(("Area", mesh.SurfaceArea().ToString("G6")));
        if (instance is { } placed)
        {
            var size = placed.Bounds().Size;
            facts.Add(("Size", $"{size.X:G4} × {size.Y:G4} × {size.Z:G4}"));
            var position = placed.World.TransformPoint(Vector3d.Zero);
            facts.Add(("Position", $"{position.X:G4}, {position.Y:G4}, {position.Z:G4}"));
        }
        return facts;
    }
}
