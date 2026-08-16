using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// VTK cell types, by their published numeric codes (the numbers ARE the format — a
/// reader switches on them, so they are transcribed, never renumbered). Only the ones
/// this kernel can currently produce are listed; the tet entries are here because
/// <see cref="VtuWriter"/>'s seam is <c>(points, cells, cell types)</c> and a volumetric
/// mesher plugs straight into it.
/// </summary>
public enum VtkCellType
{
    /// <summary>A single point.</summary>
    Vertex = 1,

    /// <summary>A two-point segment.</summary>
    Line = 3,

    /// <summary>Three points.</summary>
    Triangle = 5,

    /// <summary>An n-gon in boundary order (the exact route for a face this kernel did
    /// not triangulate).</summary>
    Polygon = 7,

    /// <summary>Four points, counter-clockwise.</summary>
    Quad = 9,

    /// <summary>Four points: the linear tetrahedron.</summary>
    Tetra = 10,

    /// <summary>Eight points: the trilinear hexahedron.</summary>
    Hexahedron = 12,

    /// <summary>Ten points: the quadratic (mid-edge-noded) tetrahedron.</summary>
    QuadraticTetra = 24,
}

/// <summary>
/// VTK XML unstructured-grid (<c>.vtu</c>) export — the ParaView interop route for
/// simulation results. Dependency-free: the file is plain XML written by hand, ASCII
/// data arrays, no VTK library and no XML framework beyond a
/// <see cref="TextWriter"/>.
///
/// <para><b>The seam is deliberately (points, cells, cell types, point data)</b> rather
/// than a mesh type. A surface result writes triangles today; a volumetric mesher writes
/// <see cref="VtkCellType.Tetra"/> through the SAME call with no change here, because
/// nothing in this file knows what a cell means. The <see cref="HalfEdgeMesh"/> overload
/// is a convenience over that seam, not a second implementation.</para>
///
/// <para><b>Point data only, in v1</b>, matching <see cref="MeshField"/>'s
/// vertex association. Cell data is a documented gap: a writer that accepted arrays
/// whose association it could not state would be the sort of half-support this codebase
/// refuses.</para>
///
/// <para><b>ASCII, not base64/appended.</b> The binary encodings are faster and smaller
/// and would need a header-type prefix per array; ASCII is what makes an exported file
/// diffable, testable by reading it, and independent of the byte order the header
/// claims. Re-visit when a result is large enough for it to matter.</para>
/// </summary>
public static class VtuWriter
{
    /// <summary>
    /// Writes an unstructured grid.
    /// </summary>
    /// <param name="points">Point coordinates, in index order.</param>
    /// <param name="cells">Per cell, the point indices in that cell's own order.</param>
    /// <param name="cellTypes">Per cell, its <see cref="VtkCellType"/> (same length as
    /// <paramref name="cells"/>).</param>
    /// <param name="pointData">Named arrays over <paramref name="points"/>; each field's
    /// <see cref="MeshField.Count"/> must equal the point count. A <c>NaN</c> value is
    /// written verbatim as <c>NaN</c>, which is VTK's own "no data here" and what a
    /// merged export uses for a part that lacks an array.</param>
    /// <param name="writer">Destination.</param>
    public static void Write(
        IReadOnlyList<Vector3d> points,
        IReadOnlyList<IReadOnlyList<int>> cells,
        IReadOnlyList<VtkCellType> cellTypes,
        IReadOnlyList<MeshField> pointData,
        TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(cellTypes);
        ArgumentNullException.ThrowIfNull(pointData);
        ArgumentNullException.ThrowIfNull(writer);
        if (cells.Count != cellTypes.Count)
            throw new ArgumentException(
                $"{cells.Count} cells but {cellTypes.Count} cell types.", nameof(cellTypes));
        foreach (var field in pointData)
        {
            if (field.Association == FieldAssociation.Cell)
            {
                if (field.Count != cells.Count)
                    throw new ArgumentException(
                        $"Cell-data array '{field.Name}' covers {field.Count} cells but the grid " +
                        $"has {cells.Count} cells.", nameof(pointData));
            }
            else if (field.Count != points.Count)
            {
                throw new ArgumentException(
                    $"Point-data array '{field.Name}' covers {field.Count} vertices but the grid " +
                    $"has {points.Count} points.", nameof(pointData));
            }
        }

        var culture = CultureInfo.InvariantCulture;
        writer.WriteLine("<?xml version=\"1.0\"?>");
        // byte_order is required by the schema even for ASCII payloads; header_type only
        // matters for appended data, and is stated so a strict reader has it.
        writer.WriteLine(
            "<VTKFile type=\"UnstructuredGrid\" version=\"1.0\" byte_order=\"LittleEndian\" header_type=\"UInt64\">");
        writer.WriteLine("  <UnstructuredGrid>");
        writer.WriteLine(string.Create(culture,
            $"    <Piece NumberOfPoints=\"{points.Count}\" NumberOfCells=\"{cells.Count}\">"));

        writer.WriteLine("      <Points>");
        writer.WriteLine(
            "        <DataArray type=\"Float64\" Name=\"Points\" NumberOfComponents=\"3\" format=\"ascii\">");
        var line = new StringBuilder();
        foreach (var p in points)
        {
            line.Clear();
            line.Append("          ");
            Append(line, p.X, culture);
            line.Append(' ');
            Append(line, p.Y, culture);
            line.Append(' ');
            Append(line, p.Z, culture);
            writer.WriteLine(line.ToString());
        }
        writer.WriteLine("        </DataArray>");
        writer.WriteLine("      </Points>");

        writer.WriteLine("      <Cells>");
        writer.WriteLine("        <DataArray type=\"Int64\" Name=\"connectivity\" format=\"ascii\">");
        foreach (var cell in cells)
        {
            line.Clear();
            line.Append("          ");
            for (int i = 0; i < cell.Count; i++)
            {
                if (i > 0)
                    line.Append(' ');
                line.Append(cell[i].ToString(culture));
            }
            writer.WriteLine(line.ToString());
        }
        writer.WriteLine("        </DataArray>");
        writer.WriteLine("        <DataArray type=\"Int64\" Name=\"offsets\" format=\"ascii\">");
        long offset = 0;
        line.Clear();
        line.Append("          ");
        for (int c = 0; c < cells.Count; c++)
        {
            offset += cells[c].Count;
            if (c > 0)
                line.Append(' ');
            line.Append(offset.ToString(culture));
        }
        writer.WriteLine(line.ToString());
        writer.WriteLine("        </DataArray>");
        writer.WriteLine("        <DataArray type=\"UInt8\" Name=\"types\" format=\"ascii\">");
        line.Clear();
        line.Append("          ");
        for (int c = 0; c < cellTypes.Count; c++)
        {
            if (c > 0)
                line.Append(' ');
            line.Append(((int)cellTypes[c]).ToString(culture));
        }
        writer.WriteLine(line.ToString());
        writer.WriteLine("        </DataArray>");
        writer.WriteLine("      </Cells>");

        var vertexFields = pointData.Where(f => f.Association == FieldAssociation.Vertex).ToList();
        var cellFields = pointData.Where(f => f.Association == FieldAssociation.Cell).ToList();
        WriteDataBlock(writer, line, culture, "PointData", vertexFields);
        WriteDataBlock(writer, line, culture, "CellData", cellFields);

        writer.WriteLine("    </Piece>");
        writer.WriteLine("  </UnstructuredGrid>");
        writer.WriteLine("</VTKFile>");
    }

    /// <summary>
    /// Writes one mesh (n-gons kept as <see cref="VtkCellType.Polygon"/> — nothing is
    /// triangulated on the way out) with its per-vertex fields. Point indices are the
    /// mesh's own vertex indices, which is exactly what a <see cref="MeshField"/> is
    /// indexed by.
    /// </summary>
    public static void Write(
        HalfEdgeMesh mesh, IReadOnlyList<MeshField> fields, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var (positions, faces) = mesh.ToIndexed();
        var types = new VtkCellType[faces.Count];
        for (int f = 0; f < faces.Count; f++)
            types[f] = CellTypeFor(faces[f].Length);
        Write(positions, [.. faces], types, fields, writer);
    }

    /// <summary>
    /// Writes several posed meshes into ONE grid, each with its own fields — the
    /// scene/assembly export.
    /// <para><b>Arrays are the union of the parts' field names</b>, and a part that
    /// lacks one contributes <c>NaN</c> for its vertices. That is deliberate: dropping
    /// an array because one part has no stress result would silently lose the result
    /// that exists, and inventing zeros would show a fake safe region. NaN is what VTK
    /// itself means by "no value", and ParaView paints it in the map's NaN colour.</para>
    /// <para>Fields whose names collide but whose component counts differ are refused by
    /// name, since one array cannot be both.</para>
    /// </summary>
    public static void Write(
        IReadOnlyList<(HalfEdgeMesh Mesh, Matrix4d Transform, IReadOnlyList<MeshField> Fields)> parts,
        TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(parts);
        // No single-part fast path: transforming by the identity is exact (x*1 + 0 + 0 + 0),
        // so one part through this path is the same file the single-mesh overload writes,
        // and there is one merge implementation rather than two.
        //
        // Array shape first: name -> component count, refusing a name used two ways.
        var components = new Dictionary<string, (int Components, string Units)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (_, _, fields) in parts)
        {
            foreach (var field in fields)
            {
                if (components.TryGetValue(field.Name, out var existing))
                {
                    if (existing.Components != field.Components)
                        throw new ArgumentException(
                            $"Result '{field.Name}' is a {existing.Components}-component array on one " +
                            $"part and a {field.Components}-component array on another; one VTU array " +
                            "cannot be both.", nameof(parts));
                    continue;
                }
                components[field.Name] = (field.Components, field.Units);
                order.Add(field.Name);
            }
        }

        var points = new List<Vector3d>();
        var cells = new List<IReadOnlyList<int>>();
        var cellTypes = new List<VtkCellType>();
        var merged = order.ToDictionary(name => name, _ => new List<double>(), StringComparer.Ordinal);

        foreach (var (mesh, transform, fields) in parts)
        {
            int offset = points.Count;
            var (positions, faces) = mesh.ToIndexed();
            foreach (var position in positions)
                points.Add(transform.TransformPoint(position));
            // A mirrored placement reverses winding, and the rotation keeps vertex 0
            // first so a consumer fanning the polygon picks the same diagonal (the
            // fan-diagonal lesson OffWriter/StlWriter follow).
            bool flip = transform.Determinant < 0;
            foreach (var face in faces)
            {
                var cell = new int[face.Length];
                for (int i = 0; i < face.Length; i++)
                    cell[i] = (flip && i > 0 ? face[face.Length - i] : face[i]) + offset;
                cells.Add(cell);
                cellTypes.Add(CellTypeFor(face.Length));
            }

            foreach (string name in order)
            {
                var values = merged[name];
                var field = fields.FirstOrDefault(f => f.Name == name);
                int width = components[name].Components;
                if (field is null)
                {
                    for (int i = 0; i < positions.Length * width; i++)
                        values.Add(double.NaN);
                    continue;
                }
                if (field.Count != positions.Length)
                    throw new ArgumentException(
                        $"Result '{name}' covers {field.Count} vertices but its mesh has " +
                        $"{positions.Length}.", nameof(parts));
                for (int i = 0; i < field.Values.Count; i++)
                    values.Add(field.Values[i]);
            }
        }

        var pointData = order
            .Select(name => new MeshField(name, components[name].Units, components[name].Components, merged[name]))
            .ToList();
        Write(points, cells, cellTypes, pointData, writer);
    }

    /// <summary><see cref="Write(HalfEdgeMesh, IReadOnlyList{MeshField}, TextWriter)"/> to a file.</summary>
    public static void WriteFile(HalfEdgeMesh mesh, IReadOnlyList<MeshField> fields, string path)
    {
        using var writer = new StreamWriter(path);
        Write(mesh, fields, writer);
    }

    /// <summary>The merged multi-part overload, to a file.</summary>
    public static void WriteFile(
        IReadOnlyList<(HalfEdgeMesh Mesh, Matrix4d Transform, IReadOnlyList<MeshField> Fields)> parts,
        string path)
    {
        using var writer = new StreamWriter(path);
        Write(parts, writer);
    }

    /// <summary>The cell type for a face of <paramref name="vertexCount"/> vertices:
    /// the dedicated triangle/quad types where they exist (readers special-case them),
    /// the general polygon otherwise.</summary>
    public static VtkCellType CellTypeFor(int vertexCount) => vertexCount switch
    {
        < 3 => throw new ArgumentOutOfRangeException(nameof(vertexCount),
            $"A face needs at least 3 vertices; got {vertexCount}."),
        3 => VtkCellType.Triangle,
        4 => VtkCellType.Quad,
        _ => VtkCellType.Polygon,
    };

    /// <summary>Round-trippable doubles, with the spellings VTK expects for the
    /// non-finite values ("NaN", "Infinity"; .NET's own "∞" would not parse).</summary>
    private static void WriteDataBlock(
        TextWriter writer, StringBuilder line, CultureInfo culture, string block,
        IReadOnlyList<MeshField> fields)
    {
        if (fields.Count == 0)
            return;
        // The Scalars/Vectors hints name the arrays ParaView selects by default; the
        // first of each kind is the honest choice (there is no "preferred result"
        // concept in a MeshField list).
        string? scalars = fields.FirstOrDefault(f => !f.IsVector)?.Name;
        string? vectors = fields.FirstOrDefault(f => f.IsVector)?.Name;
        line.Clear();
        line.Append("      <").Append(block);
        if (scalars is not null)
            line.Append(culture, $" Scalars=\"{Escape(scalars)}\"");
        if (vectors is not null)
            line.Append(culture, $" Vectors=\"{Escape(vectors)}\"");
        line.Append('>');
        writer.WriteLine(line.ToString());
        foreach (var field in fields)
        {
            writer.WriteLine(string.Create(culture,
                $"        <DataArray type=\"Float64\" Name=\"{Escape(field.Name)}\" " +
                $"NumberOfComponents=\"{field.Components}\" format=\"ascii\">"));
            for (int v = 0; v < field.Count; v++)
            {
                line.Clear();
                line.Append("          ");
                for (int c = 0; c < field.Components; c++)
                {
                    if (c > 0)
                        line.Append(' ');
                    Append(line, field.Values[v * field.Components + c], culture);
                }
                writer.WriteLine(line.ToString());
            }
            writer.WriteLine("        </DataArray>");
        }
        writer.WriteLine(string.Create(culture, $"      </{block}>"));
    }

    private static void Append(StringBuilder line, double value, CultureInfo culture)
    {
        if (double.IsNaN(value))
            line.Append("NaN");
        else if (double.IsPositiveInfinity(value))
            line.Append("Infinity");
        else if (double.IsNegativeInfinity(value))
            line.Append("-Infinity");
        else
            line.Append(value.ToString("R", culture));
    }

    /// <summary>XML attribute escaping for array names (a result may legitimately be
    /// called "S &amp; T" or carry quotes).</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
