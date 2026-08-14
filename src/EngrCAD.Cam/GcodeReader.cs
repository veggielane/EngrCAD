using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Cam;

/// <summary>What one decoded move is doing.</summary>
public enum GcodeMoveKind
{
    /// <summary>An XY move that pushes filament — a deposition.</summary>
    Deposition,

    /// <summary>An XY (or Z) move with no extrusion.</summary>
    Travel,

    /// <summary>A stationary negative-E move (filament pulled back).</summary>
    Retract,

    /// <summary>A stationary positive-E move (the retraction undone).</summary>
    Unretract,
}

/// <summary>One decoded move: the endpoints, the filament pushed (signed), the feed rate
/// (mm/min) in force, and the classification.</summary>
public sealed record GcodeMove(
    Vector3d From, Vector3d To, double DeltaE, double Feed, GcodeMoveKind Kind)
{
    /// <summary>The XY length of the move (mm).</summary>
    public double XyLength =>
        new Vector2d(To.X - From.X, To.Y - From.Y).Length;
}

/// <summary>A decoded G-code program — the moves plus the derived quantities the verification
/// identities read.</summary>
public sealed record GcodeProgram(IReadOnlyList<GcodeMove> Moves, IReadOnlyList<string> Notes)
{
    /// <summary>Total XY length of the deposition moves (mm).</summary>
    public double DepositionLength =>
        Moves.Where(m => m.Kind == GcodeMoveKind.Deposition).Sum(m => m.XyLength);

    /// <summary>Total filament pushed by the deposition moves (mm) — the decoder's own
    /// re-derivation of what the writer's E bookkeeping claims.</summary>
    public double FilamentUsed =>
        Moves.Where(m => m.Kind == GcodeMoveKind.Deposition).Sum(m => m.DeltaE);

    /// <summary>How many retracts the program performs.</summary>
    public int RetractCount => Moves.Count(m => m.Kind == GcodeMoveKind.Retract);

    /// <summary>How many unretracts — a well-formed program matches every retract.</summary>
    public int UnretractCount => Moves.Count(m => m.Kind == GcodeMoveKind.Unretract);

    /// <summary>The distinct Z heights deposition happens at, in program order — the layers.</summary>
    public IReadOnlyList<double> LayerZs
    {
        get
        {
            var zs = new List<double>();
            foreach (var move in Moves)
                if (move.Kind == GcodeMoveKind.Deposition
                    && (zs.Count == 0 || zs[^1] != move.To.Z))
                    zs.Add(move.To.Z);
            return zs;
        }
    }
}

/// <summary>
/// The twin decoder for <see cref="GcodeWriter"/> — the house verification style: a structural
/// look at a G-code file proves nothing about its coordinates, so the reader recovers the MOVES
/// and the tests assert the writer's extrusion bookkeeping as arithmetic
/// (<c>FilamentUsed == deposition length × BeadArea / FilamentArea</c>, through the decoded
/// values).
///
/// <para>Scoped to what the writer emits, with the traps refused BY NAME rather than mis-read:
/// <c>G20</c> (inches — the unit trap), <c>G91</c> (relative coordinates) and <c>M83</c>
/// (relative extrusion) each refuse, because decoding them as their absolute siblings would
/// produce confidently wrong geometry; <c>G2</c>/<c>G3</c> arcs refuse by name (the FDM writer
/// emits line moves; arcs join with the CNC stages). Unknown M-codes and comments are dirt,
/// noted or skipped, never thrown.</para>
/// </summary>
public static class GcodeReader
{
    /// <summary>Decodes G-code text into its moves.</summary>
    /// <exception cref="FormatException">A mode this decoder must not guess about —
    /// inches, relative coordinates, relative extrusion, an arc — refused by name.</exception>
    public static GcodeProgram Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var notes = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) notes.Add(message); }

        var moves = new List<GcodeMove>();
        double x = 0, y = 0, z = 0, e = 0, feed = 0;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf(';');
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line.Length == 0)
                continue;

            var words = ParseWords(line, Note);
            if (words.Count == 0)
                continue;
            var (letter, code) = words[0];

            if (letter == 'G')
            {
                switch ((int)code)
                {
                    case 0 or 1:
                    {
                        double nx = x, ny = y, nz = z, ne = e;
                        foreach (var (w, v) in words.Skip(1))
                            switch (w)
                            {
                                case 'X': nx = v; break;
                                case 'Y': ny = v; break;
                                case 'Z': nz = v; break;
                                case 'E': ne = v; break;
                                case 'F': feed = v; break;
                                default:
                                    Note($"Word '{w}' on a G0/G1 was ignored.");
                                    break;
                            }
                        var from = new Vector3d(x, y, z);
                        var to = new Vector3d(nx, ny, nz);
                        double deltaE = ne - e;
                        bool movedXy = nx != x || ny != y;
                        var kind = deltaE > 0 && movedXy ? GcodeMoveKind.Deposition
                            : deltaE < 0 && !movedXy ? GcodeMoveKind.Retract
                            : deltaE > 0 ? GcodeMoveKind.Unretract
                            : GcodeMoveKind.Travel;
                        if (from != to || deltaE != 0)
                            moves.Add(new GcodeMove(from, to, deltaE, feed, kind));
                        (x, y, z, e) = (nx, ny, nz, ne);
                        break;
                    }
                    case 2 or 3:
                        throw new FormatException(
                            "G2/G3 arc moves are not decoded (the FDM writer emits line moves; "
                            + "arcs join with the CNC stages). Refused by name.");
                    case 20:
                        throw new FormatException(
                            "G20 declares INCHES — the unit trap. This decoder reads millimetre "
                            + "programs (G21) only.");
                    case 91:
                        throw new FormatException(
                            "G91 declares RELATIVE coordinates; decoding it as absolute would be "
                            + "confidently wrong. Only G90 programs are read.");
                    case 21 or 90 or 28:
                        break;                               // millimetres / absolute / home
                    case 92:
                        foreach (var (w, v) in words.Skip(1))
                            if (w == 'E')
                                e = v;
                        break;
                    default:
                        Note($"G{(int)code} was ignored.");
                        break;
                }
            }
            else if (letter == 'M')
            {
                if ((int)code == 83)
                    throw new FormatException(
                        "M83 declares RELATIVE extrusion; the E bookkeeping identity needs "
                        + "absolute E (M82). Refused by name.");
                // M82 / temperatures / fans / motors-off are modes and hardware, not geometry.
            }
            else
            {
                Note($"Line starting '{letter}' was ignored.");
            }
        }

        return new GcodeProgram(moves, notes);
    }

    /// <summary>Reads a <c>.gcode</c> file from disk.</summary>
    public static GcodeProgram ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllText(path));
    }

    private static List<(char Letter, double Value)> ParseWords(string line, Action<string> note)
    {
        var words = new List<(char, double)>();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (!char.IsAsciiLetter(c))
            {
                note($"Unparseable token near '{line[i..Math.Min(line.Length, i + 8)]}' ignored.");
                break;
            }
            int start = ++i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && !char.IsAsciiLetter(line[i]))
                i++;
            string number = line[start..i];
            if (number.Length == 0
                || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double value))
            {
                note($"Word '{c}{number}' has no number and was ignored.");
                continue;
            }
            words.Add((char.ToUpperInvariant(c), value));
        }
        return words;
    }
}
