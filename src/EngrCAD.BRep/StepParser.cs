using System.Globalization;
using System.Text;

namespace EngrCAD.BRep;

/// <summary>Kinds of argument values in a STEP (ISO 10303-21 Part 21) physical file.</summary>
internal enum StepValueKind
{
    /// <summary>Integer or real literal (STEP does not distinguish them syntactically here).</summary>
    Number,

    /// <summary>Quoted string; embedded <c>''</c> pairs are decoded to a single apostrophe.</summary>
    Text,

    /// <summary>Enumeration literal such as <c>.T.</c> or <c>.MILLI.</c> (stored without the dots).</summary>
    Enumeration,

    /// <summary>Entity instance reference <c>#id</c>.</summary>
    Reference,

    /// <summary>Parenthesized aggregate <c>(a, b, …)</c>.</summary>
    List,

    /// <summary>Typed value such as <c>LENGTH_MEASURE(1.E-6)</c>: a keyword wrapping arguments.</summary>
    Typed,

    /// <summary>Unset value <c>$</c>.</summary>
    Null,

    /// <summary>Derived/redeclared value <c>*</c>.</summary>
    Derived,
}

/// <summary>One parsed argument value of a STEP entity record.</summary>
internal sealed class StepValue
{
    public StepValueKind Kind { get; init; }

    /// <summary>Numeric value for <see cref="StepValueKind.Number"/>.</summary>
    public double Number { get; init; }

    /// <summary>String content, enumeration name, or typed-value keyword.</summary>
    public string Text { get; init; } = "";

    /// <summary>Referenced entity id for <see cref="StepValueKind.Reference"/>.</summary>
    public int Reference { get; init; }

    /// <summary>Items of a list, or the arguments of a typed value.</summary>
    public IReadOnlyList<StepValue> Items { get; init; } = [];

    public double AsNumber() => Kind switch
    {
        StepValueKind.Number => Number,
        StepValueKind.Typed when Items.Count == 1 => Items[0].AsNumber(),
        _ => throw new FormatException($"Expected a number, found {Kind} '{Text}'."),
    };

    public int AsInt()
    {
        double value = AsNumber();
        int rounded = (int)Math.Round(value);
        if (Math.Abs(value - rounded) > 1e-9)
            throw new FormatException($"Expected an integer, found {value}.");
        return rounded;
    }

    public int AsReference() => Kind == StepValueKind.Reference
        ? Reference
        : throw new FormatException($"Expected an entity reference, found {Kind}.");

    public bool AsBool() => Kind == StepValueKind.Enumeration && Text is "T" or "F"
        ? Text == "T"
        : throw new FormatException($"Expected .T. or .F., found {Kind} '{Text}'.");

    public IReadOnlyList<StepValue> AsList() => Kind == StepValueKind.List
        ? Items
        : throw new FormatException($"Expected a list, found {Kind}.");

    public bool IsNull => Kind is StepValueKind.Null or StepValueKind.Derived;

    public override string ToString() => Kind switch
    {
        StepValueKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        StepValueKind.Text => $"'{Text}'",
        StepValueKind.Enumeration => $".{Text}.",
        StepValueKind.Reference => $"#{Reference}",
        StepValueKind.List => $"({string.Join(",", Items)})",
        StepValueKind.Typed => $"{Text}({string.Join(",", Items)})",
        StepValueKind.Null => "$",
        _ => "*",
    };
}

/// <summary>One <c>KEYWORD(arg, …)</c> record; a complex instance has several per entity.</summary>
internal sealed record StepRecord(string Keyword, IReadOnlyList<StepValue> Args);

/// <summary>A data-section entity instance <c>#id = …;</c> (simple or complex).</summary>
internal sealed class StepEntity
{
    public required int Id { get; init; }
    public required IReadOnlyList<StepRecord> Records { get; init; }

    public bool IsComplex => Records.Count > 1;

    /// <summary>Keyword of a simple entity, or the first record's keyword of a complex one.</summary>
    public string Keyword => Records[0].Keyword;

    /// <summary>The record with the given keyword, or null.</summary>
    public StepRecord? Find(string keyword) =>
        Records.FirstOrDefault(r => string.Equals(r.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A parsed STEP physical file: header records plus the data-section entity graph.</summary>
internal sealed class StepFile
{
    public required IReadOnlyList<StepRecord> Header { get; init; }
    public required IReadOnlyDictionary<int, StepEntity> Entities { get; init; }

    public StepEntity Entity(int id) => Entities.TryGetValue(id, out var entity)
        ? entity
        : throw new FormatException($"Unresolved entity reference #{id}.");
}

/// <summary>
/// ISO 10303-21 physical-file parser: tokenizes and builds the entity graph, including
/// complex (multi-record) instances. References may point forward; they are resolved
/// lazily through <see cref="StepFile.Entity"/>. Comments <c>/* … */</c> are skipped and
/// strings decode the <c>''</c> apostrophe escape. Malformed input throws
/// <see cref="FormatException"/> with the offending line.
/// </summary>
internal static class StepParser
{
    public static StepFile Parse(string text)
    {
        var scanner = new Scanner(text);
        var header = new List<StepRecord>();
        var entities = new Dictionary<int, StepEntity>();

        while (true)
        {
            scanner.SkipTrivia();
            if (scanner.AtEnd)
                break;

            if (scanner.Peek == '#')
            {
                int id = scanner.ReadReference();
                scanner.SkipTrivia();
                scanner.Expect('=');
                var records = ParseEntityBody(scanner);
                scanner.SkipTrivia();
                scanner.Expect(';');
                entities[id] = new StepEntity { Id = id, Records = records };
                continue;
            }

            string keyword = scanner.ReadKeyword();
            scanner.SkipTrivia();
            if (scanner.TryConsume(';'))
                continue; // section marker: ISO-10303-21, HEADER, DATA, ENDSEC, END-ISO-10303-21
            var args = ParseArgList(scanner);
            scanner.SkipTrivia();
            scanner.Expect(';');
            header.Add(new StepRecord(keyword, args));
        }

        return new StepFile { Header = header, Entities = entities };
    }

    private static IReadOnlyList<StepRecord> ParseEntityBody(Scanner scanner)
    {
        scanner.SkipTrivia();
        if (scanner.Peek != '(')
        {
            string keyword = scanner.ReadKeyword();
            return [new StepRecord(keyword, ParseArgList(scanner))];
        }

        // Complex instance: ( KEYWORD1(args) KEYWORD2(args) … ).
        scanner.Expect('(');
        var records = new List<StepRecord>();
        while (true)
        {
            scanner.SkipTrivia();
            if (scanner.TryConsume(')'))
                break;
            string keyword = scanner.ReadKeyword();
            records.Add(new StepRecord(keyword, ParseArgList(scanner)));
        }
        if (records.Count == 0)
            throw scanner.Error("Empty complex instance.");
        return records;
    }

    private static IReadOnlyList<StepValue> ParseArgList(Scanner scanner)
    {
        scanner.SkipTrivia();
        scanner.Expect('(');
        var args = new List<StepValue>();
        scanner.SkipTrivia();
        if (scanner.TryConsume(')'))
            return args;
        while (true)
        {
            args.Add(ParseValue(scanner));
            scanner.SkipTrivia();
            if (scanner.TryConsume(')'))
                return args;
            scanner.Expect(',');
        }
    }

    private static StepValue ParseValue(Scanner scanner)
    {
        scanner.SkipTrivia();
        if (scanner.AtEnd)
            throw scanner.Error("Unexpected end of file in an argument list.");
        char c = scanner.Peek;
        switch (c)
        {
            case '#':
                return new StepValue { Kind = StepValueKind.Reference, Reference = scanner.ReadReference() };
            case '\'':
                return new StepValue { Kind = StepValueKind.Text, Text = scanner.ReadString() };
            case '(':
                return new StepValue { Kind = StepValueKind.List, Items = ParseArgList(scanner) };
            case '$':
                scanner.Advance();
                return new StepValue { Kind = StepValueKind.Null };
            case '*':
                scanner.Advance();
                return new StepValue { Kind = StepValueKind.Derived };
            case '.':
                // '.5' style reals are not valid Part 21, but be permissive.
                return scanner.PeekAhead(1) is char n && (char.IsAsciiDigit(n))
                    ? new StepValue { Kind = StepValueKind.Number, Number = scanner.ReadNumber() }
                    : new StepValue { Kind = StepValueKind.Enumeration, Text = scanner.ReadEnumeration() };
        }
        if (c is '+' or '-' || char.IsAsciiDigit(c))
            return new StepValue { Kind = StepValueKind.Number, Number = scanner.ReadNumber() };
        if (char.IsAsciiLetter(c) || c == '_')
        {
            // Typed value: KEYWORD(args).
            string keyword = scanner.ReadKeyword();
            scanner.SkipTrivia();
            if (scanner.Peek == '(')
                return new StepValue { Kind = StepValueKind.Typed, Text = keyword, Items = ParseArgList(scanner) };
            // Bare identifier (nonstandard): surface it as an enumeration-like token.
            return new StepValue { Kind = StepValueKind.Enumeration, Text = keyword };
        }
        throw scanner.Error($"Unexpected character '{c}' in an argument list.");
    }

    private sealed class Scanner(string text)
    {
        private int _index;

        public bool AtEnd => _index >= text.Length;

        /// <summary>Current character, or '\0' at end of input — the sentinel matches no
        /// expected character, so truncated files fail through the normal error paths
        /// (FormatException) instead of indexing past the buffer.</summary>
        public char Peek => _index < text.Length ? text[_index] : '\0';

        public char? PeekAhead(int offset) =>
            _index + offset < text.Length ? text[_index + offset] : null;

        public void Advance() => _index++;

        public void SkipTrivia()
        {
            while (_index < text.Length)
            {
                char c = text[_index];
                if (char.IsWhiteSpace(c))
                {
                    _index++;
                    continue;
                }
                if (c == '/' && PeekAhead(1) == '*')
                {
                    int end = text.IndexOf("*/", _index + 2, StringComparison.Ordinal);
                    if (end < 0)
                        throw Error("Unterminated comment.");
                    _index = end + 2;
                    continue;
                }
                break;
            }
        }

        public bool TryConsume(char c)
        {
            if (!AtEnd && Peek == c)
            {
                _index++;
                return true;
            }
            return false;
        }

        public void Expect(char c)
        {
            if (AtEnd || Peek != c)
                throw Error($"Expected '{c}'.");
            _index++;
        }

        /// <summary>Keyword: letters, digits, underscores, and hyphens (for ISO-10303-21 markers).</summary>
        public string ReadKeyword()
        {
            int start = _index;
            while (_index < text.Length &&
                   (char.IsAsciiLetterOrDigit(text[_index]) || text[_index] is '_' or '-'))
                _index++;
            if (_index == start)
                throw Error("Expected a keyword.");
            return text[start.._index];
        }

        public int ReadReference()
        {
            Expect('#');
            int start = _index;
            while (_index < text.Length && char.IsAsciiDigit(text[_index]))
                _index++;
            if (_index == start)
                throw Error("Expected digits after '#'.");
            return int.Parse(text.AsSpan(start, _index - start), CultureInfo.InvariantCulture);
        }

        public string ReadString()
        {
            Expect('\'');
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("Unterminated string.");
                char c = text[_index++];
                if (c == '\'')
                {
                    if (!AtEnd && Peek == '\'')
                    {
                        sb.Append('\'');
                        _index++;
                        continue;
                    }
                    return sb.ToString();
                }
                sb.Append(c);
            }
        }

        public string ReadEnumeration()
        {
            Expect('.');
            int start = _index;
            while (_index < text.Length && text[_index] != '.')
            {
                if (text[_index] is ';' or ')' or ',' || char.IsWhiteSpace(text[_index]))
                    throw Error("Unterminated enumeration literal.");
                _index++;
            }
            if (AtEnd)
                throw Error("Unterminated enumeration literal.");
            string name = text[start.._index];
            _index++; // closing '.'
            return name;
        }

        /// <summary>Reads a Part 21 real/integer, including forms like <c>1.E-6</c> and <c>0.</c>.</summary>
        public double ReadNumber()
        {
            int start = _index;
            if (!AtEnd && (Peek is '+' or '-'))
                _index++;
            int digitsStart = _index;
            while (!AtEnd && char.IsAsciiDigit(Peek))
                _index++;
            string integerPart = text[digitsStart.._index];
            string fractionPart = "";
            if (!AtEnd && Peek == '.')
            {
                _index++;
                int fractionStart = _index;
                while (!AtEnd && char.IsAsciiDigit(Peek))
                    _index++;
                fractionPart = text[fractionStart.._index];
            }
            string exponent = "0";
            if (!AtEnd && (Peek is 'e' or 'E'))
            {
                _index++;
                int expStart = _index;
                if (!AtEnd && (Peek is '+' or '-'))
                    _index++;
                while (!AtEnd && char.IsAsciiDigit(Peek))
                    _index++;
                exponent = text[expStart.._index];
                if (exponent is "" or "+" or "-")
                    throw Error("Malformed exponent.");
            }
            if (integerPart.Length == 0 && fractionPart.Length == 0)
                throw Error("Malformed number.");

            string sign = text[start] == '-' ? "-" : "";
            string normalized =
                $"{sign}{(integerPart.Length > 0 ? integerPart : "0")}.{(fractionPart.Length > 0 ? fractionPart : "0")}E{exponent}";
            return double.Parse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public FormatException Error(string message)
        {
            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < _index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
            return new FormatException($"STEP parse error at line {line}, column {_index - lineStart + 1}: {message}");
        }
    }
}
