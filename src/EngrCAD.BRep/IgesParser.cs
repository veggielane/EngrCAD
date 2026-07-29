using System.Globalization;
using System.Text;

namespace EngrCAD.BRep;

/// <summary>
/// The IGES (ASME Y14.26M / IGES 5.3) <b>record layer</b>: 80-column card images split
/// into the Start, Global, Directory Entry, Parameter Data and Terminate sections, with
/// the Global section's declared delimiters honoured and Hollerith strings decoded.
/// <para>This is the first column-oriented reader in the codebase, and it takes
/// <c>StlReader</c>'s rule: <b>validate the record structure up front and refuse by
/// name</b> rather than sniffing content. Column 73 is the section letter and columns
/// 74-80 the sequence number on EVERY card, so a file whose cards do not have that shape
/// is not an IGES file and is rejected before a single parameter is read.</para>
/// <para>Structure errors throw <see cref="FormatException"/>; unknown ENTITY types are
/// the reader's business, not the parser's, and are simply carried through as data — the
/// <c>StepParser</c>/<c>StepReader</c> split.</para>
/// </summary>
internal static class IgesParser
{
    public static IgesFile Parse(string text)
    {
        var cards = SplitCards(text);
        if (cards.Count == 0)
            throw new FormatException("The IGES file is empty.");

        var start = new StringBuilder();
        var globalText = new StringBuilder();
        var directoryCards = new List<Card>();
        var parameterCards = new List<Card>();
        bool sawTerminate = false;

        foreach (var card in cards)
        {
            switch (card.Section)
            {
                case 'S': start.Append(card.Data.TrimEnd()).Append('\n'); break;
                case 'G': globalText.Append(card.Data); break;
                case 'D': directoryCards.Add(card); break;
                case 'P': parameterCards.Add(card); break;
                case 'T': sawTerminate = true; break;
                default:
                    throw new FormatException(
                        $"IGES card {card.Sequence} has section letter '{card.Section}' in column 73; "
                        + "expected one of S, G, D, P, T.");
            }
        }

        // An EMPTY Directory Entry section is legal (a file may carry only its header), so
        // it is the reader's "nothing here" diagnostic rather than a structural failure.
        // An ODD number of cards is genuinely broken: every entity occupies exactly two.
        if (directoryCards.Count % 2 != 0)
        {
            throw new FormatException(
                $"The IGES Directory Entry section has {directoryCards.Count} cards; every entity "
                + "occupies exactly two, so the section is truncated.");
        }

        var global = ParseGlobal(globalText.ToString());
        var entities = ParseDirectory(directoryCards);
        AttachParameters(entities, parameterCards, global);

        return new IgesFile
        {
            StartSection = start.ToString().TrimEnd(),
            Global = global,
            Entities = entities,
            SawTerminate = sawTerminate,
        };
    }

    // ---- cards ----

    private readonly record struct Card(string Data, char Section, int Sequence, int Line);

    private static List<Card> SplitCards(string text)
    {
        var cards = new List<Card>();
        int line = 0;
        foreach (var raw in text.Split('\n'))
        {
            line++;
            string card = raw.TrimEnd('\r');
            if (card.Trim().Length == 0)
                continue;
            // Writers routinely emit short cards by trimming trailing blanks; pad rather
            // than refuse, since column 73 is what actually identifies the section and a
            // padded card cannot change any field's meaning.
            if (card.Length < 80)
                card = card.PadRight(80);
            if (card.Length > 80)
            {
                throw new FormatException(
                    $"IGES line {line} is {card.Length} characters; cards are 80 columns.");
            }

            char section = card[72];
            string sequenceField = card[73..80].Trim();
            if (!int.TryParse(sequenceField, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int sequence))
            {
                throw new FormatException(
                    $"IGES line {line} has '{sequenceField}' in columns 74-80 where a sequence "
                    + "number belongs. This is probably not an IGES file.");
            }
            cards.Add(new Card(card[..72], section, sequence, line));
        }
        return cards;
    }

    // ---- global section ----

    private static IgesGlobal ParseGlobal(string text)
    {
        // The delimiters are themselves the first two parameters, and they are Hollerith
        // strings, so the section has to be read with the DEFAULTS until they are known.
        char parameter = ',';
        char record = ';';
        if (text.StartsWith("1H", StringComparison.Ordinal) && text.Length > 2)
        {
            parameter = text[2];
            int next = 3;
            if (next < text.Length && text[next] == parameter)
                next++;
            if (text.AsSpan(next).StartsWith("1H") && text.Length > next + 2)
                record = text[next + 2];
        }

        var fields = SplitFields(text, parameter, record);
        return new IgesGlobal
        {
            ParameterDelimiter = parameter,
            RecordDelimiter = record,
            SendingSystem = Hollerith(Field(fields, 4)),
            FileName = Hollerith(Field(fields, 3)),
            ModelSpaceScale = Number(Field(fields, 12), 1.0),
            UnitFlag = (int)Number(Field(fields, 13), 1),
            UnitName = Hollerith(Field(fields, 14)),
            Author = Hollerith(Field(fields, 20)),
            Organization = Hollerith(Field(fields, 21)),
            Version = (int)Number(Field(fields, 22), 0),
            Fields = fields,
        };
    }

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index < fields.Count ? fields[index] : "";

    /// <summary>Splits a free-format section into fields, honouring Hollerith strings
    /// (whose contents may legally contain the delimiter) and stopping at the record
    /// delimiter.</summary>
    internal static List<string> SplitFields(string text, char parameter, char record)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == parameter)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            if (c == record)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                return fields;
            }
            // A Hollerith count followed by 'H' means the next N characters are literal,
            // delimiters included. Missing this is how a file with a comma in its author
            // field silently shifts every later parameter by one.
            if (c == 'H' && current.Length > 0
                && int.TryParse(current.ToString().Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int count)
                && count >= 0 && i + count < text.Length)
            {
                current.Append('H').Append(text.Substring(i + 1, count));
                i += count;
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            fields.Add(current.ToString().Trim());
        return fields;
    }

    internal static string Hollerith(string field)
    {
        int h = field.IndexOf('H');
        if (h <= 0 || !int.TryParse(field[..h], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int count))
        {
            return field;
        }
        string body = field[(h + 1)..];
        return body.Length <= count ? body : body[..count];
    }

    /// <summary>IGES numbers may use a <c>D</c> exponent (Fortran heritage) as well as
    /// <c>E</c>; an empty field means "take the default".</summary>
    internal static double Number(string field, double fallback)
    {
        string token = field.Trim();
        if (token.Length == 0)
            return fallback;
        token = token.Replace('D', 'E').Replace('d', 'e');
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new FormatException($"'{field}' is not an IGES number.");
    }

    // ---- directory entry section ----

    private static Dictionary<int, IgesEntity> ParseDirectory(List<Card> cards)
    {
        var entities = new Dictionary<int, IgesEntity>();
        for (int i = 0; i < cards.Count; i += 2)
        {
            var first = cards[i];
            var second = cards[i + 1];
            // The DE "pointer" every other entity uses IS the sequence number of the
            // entity's FIRST directory card, which is always odd. Keying on it rather than
            // on position is what makes cross-references work.
            int pointer = first.Sequence;

            var a = Fields(first.Data);
            var b = Fields(second.Data);
            int type = Integer(a[0], first.Line, "entity type number");
            if (Integer(b[0], second.Line, "entity type number") != type)
            {
                throw new FormatException(
                    $"IGES directory entry at sequence {pointer} declares entity type {type} on its "
                    + $"first card and {Integer(b[0], second.Line, "entity type number")} on its "
                    + "second; the two cards do not belong to one entity.");
            }

            entities[pointer] = new IgesEntity
            {
                Pointer = pointer,
                Type = type,
                ParameterPointer = Integer(a[1], first.Line, "parameter data pointer"),
                TransformPointer = Integer(a[6], first.Line, "transformation matrix pointer"),
                Status = a[8].Trim(),
                ParameterLineCount = Integer(b[3], second.Line, "parameter line count"),
                Form = Integer(b[4], second.Line, "form number"),
                Label = b[7].Trim(),
            };
        }
        return entities;
    }

    private static string[] Fields(string data)
    {
        var fields = new string[9];
        for (int i = 0; i < 9; i++)
            fields[i] = data.Substring(i * 8, 8);
        return fields;
    }

    private static int Integer(string field, int line, string what)
    {
        string token = field.Trim();
        if (token.Length == 0)
            return 0;
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new FormatException(
                $"IGES line {line}: '{token}' is not an integer where a {what} belongs.");
    }

    // ---- parameter data section ----

    private static void AttachParameters(
        Dictionary<int, IgesEntity> entities, List<Card> cards, IgesGlobal global)
    {
        // Every P card carries its owner's DE pointer in columns 66-72, so the section can
        // be grouped without trusting the DE's own parameter pointer — which some writers
        // get wrong. Text accumulates in card order.
        var text = new Dictionary<int, StringBuilder>();
        foreach (var card in cards)
        {
            string ownerField = card.Data.Length > 64 ? card.Data[64..].Trim() : "";
            if (!int.TryParse(ownerField, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int owner))
            {
                throw new FormatException(
                    $"IGES line {card.Line}: parameter card carries '{ownerField}' in columns 66-72 "
                    + "where its directory entry pointer belongs.");
            }
            if (!text.TryGetValue(owner, out var builder))
                text[owner] = builder = new StringBuilder();
            builder.Append(card.Data.Length > 64 ? card.Data[..64] : card.Data);
        }

        foreach (var (owner, builder) in text)
        {
            if (!entities.TryGetValue(owner, out var entity))
                continue; // an orphan parameter block; the reader reports it as a diagnostic
            entity.Parameters = SplitFields(
                builder.ToString(), global.ParameterDelimiter, global.RecordDelimiter);
        }
    }
}

/// <summary>One IGES entity: its directory-entry fields plus its parameter data.</summary>
internal sealed class IgesEntity
{
    public required int Pointer { get; init; }
    public required int Type { get; init; }
    public required int ParameterPointer { get; init; }
    public required int TransformPointer { get; init; }
    public required int ParameterLineCount { get; init; }
    public required int Form { get; init; }
    public required string Status { get; init; }
    public required string Label { get; init; }

    /// <summary>Parameter fields, INCLUDING field 0 (the entity type number, which IGES
    /// repeats as the first parameter) — so parameter P1 in the spec is index 1 here and
    /// the code reads like the standard.</summary>
    public IReadOnlyList<string> Parameters { get; set; } = [];

    public override string ToString() => $"type {Type} at DE {Pointer}";
}

/// <summary>The IGES Global section's fields.</summary>
internal sealed class IgesGlobal
{
    public required char ParameterDelimiter { get; init; }
    public required char RecordDelimiter { get; init; }
    public required string SendingSystem { get; init; }
    public required string FileName { get; init; }
    public required double ModelSpaceScale { get; init; }
    public required int UnitFlag { get; init; }
    public required string UnitName { get; init; }
    public required string Author { get; init; }
    public required string Organization { get; init; }
    public required int Version { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
}

internal sealed class IgesFile
{
    public required string StartSection { get; init; }
    public required IgesGlobal Global { get; init; }
    public required IReadOnlyDictionary<int, IgesEntity> Entities { get; init; }
    public required bool SawTerminate { get; init; }
}
