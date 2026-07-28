using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// Heightmap terrain — OpenSCAD's <c>surface()</c>. A rectangular grid of heights
/// becomes a closed solid: the grid is the top surface (each cell two triangles), a
/// flat base closes the bottom, and perimeter walls join the two sharing the boundary
/// vertices, so the mesh is manifold by construction (no tolerance welding anywhere).
/// <para>Heights come from a <c>double[,]</c> you computed, an OpenSCAD-style
/// <c>.dat</c> text matrix (<see cref="ReadDat(string)"/>), or a grayscale PNG
/// (<see cref="ReadPng(string)"/> — 8/16-bit, hand-rolled and dependency free like
/// the TrueType reader, values normalized to 0..1). Feed the result to
/// <see cref="Shape.Heightmap(double[,], double, double, double, bool)"/> for the
/// unified API, where it is an ordinary mesh-backed shape (booleans, transforms and
/// the implicit route all work; B-Rep does not — meshes cannot be imported).</para>
/// <para>Grid convention: <c>heights[row, column]</c>, columns along +X and rows
/// along −Y (row 0 is the far edge) — the way an image reads, so a PNG heightmap
/// lands the way it looks.</para>
/// </summary>
public static class Heightmap
{
    /// <summary>
    /// Builds the closed terrain solid.
    /// </summary>
    /// <param name="heights">The height grid (at least 2×2); every value must lie
    /// strictly above <paramref name="baseLevel"/>.</param>
    /// <param name="cellSize">Grid spacing in model units (&gt; 0).</param>
    /// <param name="baseLevel">The z of the flat bottom face.</param>
    /// <param name="centered">Center the footprint on the origin (default); false
    /// puts the grid's last row's first column at the origin, growing +X/+Y.</param>
    public static HalfEdgeMesh Mesh(
        double[,] heights, double cellSize, double baseLevel = 0, bool centered = true)
    {
        ArgumentNullException.ThrowIfNull(heights);
        int rows = heights.GetLength(0);
        int cols = heights.GetLength(1);
        if (rows < 2 || cols < 2)
            throw new ArgumentException($"A heightmap needs at least 2×2 samples; got {rows}×{cols}.", nameof(heights));
        if (cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double h = heights[r, c];
                if (!double.IsFinite(h) || h <= baseLevel)
                    throw new ArgumentException(
                        $"Height [{r}, {c}] = {h:g6} must be finite and strictly above the base level {baseLevel:g6}.",
                        nameof(heights));
            }
        }

        double width = (cols - 1) * cellSize;
        double depth = (rows - 1) * cellSize;
        double x0 = centered ? -width / 2 : 0;
        double y0 = centered ? -depth / 2 : 0;

        // Vertices: the top grid (row-major), then a bottom copy of the PERIMETER
        // (walls and base share these, so the solid closes by index).
        var positions = new List<Vector3d>(rows * cols);
        for (int r = 0; r < rows; r++)
        {
            double y = y0 + (rows - 1 - r) * cellSize;      // row 0 = far edge (image order)
            for (int c = 0; c < cols; c++)
                positions.Add(new Vector3d(x0 + c * cellSize, y, heights[r, c]));
        }

        // Perimeter loop of grid indices, counter-clockwise seen from above:
        // last row left→right, right column bottom→top, first row right→left,
        // left column top→bottom.
        var perimeter = new List<int>(2 * (rows + cols) - 4);
        for (int c = 0; c < cols; c++)
            perimeter.Add((rows - 1) * cols + c);
        for (int r = rows - 2; r >= 0; r--)
            perimeter.Add(r * cols + (cols - 1));
        for (int c = cols - 2; c >= 0; c--)
            perimeter.Add(c);
        for (int r = 1; r <= rows - 2; r++)
            perimeter.Add(r * cols);

        int bottomStart = positions.Count;
        foreach (int index in perimeter)
        {
            var top = positions[index];
            positions.Add(new Vector3d(top.X, top.Y, baseLevel));
        }

        var faces = new List<IReadOnlyList<int>>(2 * (rows - 1) * (cols - 1) + 2 * perimeter.Count + 1);

        // Top: two CCW-from-above triangles per cell.
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                int nw = r * cols + c, ne = nw + 1;
                int sw = nw + cols, se = sw + 1;
                faces.Add([sw, se, ne]);
                faces.Add([sw, ne, nw]);
            }
        }

        // Walls: the perimeter runs CCW from above, so (top i, bottom i,
        // bottom i+1, top i+1) faces outward.
        int n = perimeter.Count;
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            faces.Add([perimeter[i], bottomStart + i, bottomStart + next, perimeter[next]]);
        }

        // Base: the whole bottom perimeter as ONE polygon, wound clockwise from above
        // (normal −z). It is a rectangle with collinear boundary vertices, which the
        // n-gon face keeps verbatim — every wall vertex stays referenced, no
        // T-junctions.
        var basePolygon = new int[n];
        for (int i = 0; i < n; i++)
            basePolygon[i] = bottomStart + (n - 1 - i);
        faces.Add(basePolygon);

        return HalfEdgeMesh.Build(positions, faces);
    }

    // ---- OpenSCAD .dat text matrices ----------------------------------------

    /// <summary>Reads an OpenSCAD-style <c>.dat</c> heightmap: one row of
    /// whitespace-separated numbers per line; blank lines and <c>#</c> comment lines
    /// are skipped. Every row must have the same number of columns.</summary>
    public static double[,] ReadDat(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var reader = new StreamReader(path);
        return ReadDat(reader);
    }

    /// <inheritdoc cref="ReadDat(string)"/>
    public static double[,] ReadDat(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = new List<double[]>();
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var row = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out row[i]))
                    throw new FormatException($"Line {lineNumber}: '{parts[i]}' is not a number.");
            }
            if (rows.Count > 0 && row.Length != rows[0].Length)
                throw new FormatException(
                    $"Line {lineNumber} has {row.Length} values; earlier rows have {rows[0].Length}.");
            rows.Add(row);
        }
        if (rows.Count == 0)
            throw new FormatException("The heightmap data contains no rows.");

        var heights = new double[rows.Count, rows[0].Length];
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[0].Length; c++)
                heights[r, c] = rows[r][c];
        return heights;
    }

    // ---- grayscale PNG -------------------------------------------------------

    /// <summary>Reads a grayscale PNG (8- or 16-bit, alpha channel ignored) into
    /// heights normalized to 0..1 (<c>value / (2^depth − 1)</c>), row 0 = the image's
    /// top row. Color, palette, low-bit-depth and interlaced PNGs are rejected with a
    /// message naming the limitation.</summary>
    public static double[,] ReadPng(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ReadPng(File.ReadAllBytes(path));
    }

    /// <inheritdoc cref="ReadPng(string)"/>
    public static double[,] ReadPng(ReadOnlySpan<byte> data) => PngGrayReader.Read(data);
}
