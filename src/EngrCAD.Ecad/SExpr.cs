using System.Globalization;
using System.Text;

namespace EngrCAD.Ecad;

/// <summary>A node of a parsed S-expression: either an <see cref="SAtom"/> (a token) or an
/// <see cref="SList"/> (a parenthesised list).</summary>
internal abstract class SNode;

/// <summary>An atom — a bare token (<c>passive</c>, <c>-1.016</c>) or a quoted string
/// (<c>"R_0805"</c>). <see cref="Quoted"/> records which, for callers that must distinguish a
/// keyword from a same-spelled string (the readers do not, but it keeps the parse faithful).</summary>
internal sealed class SAtom(string text, bool quoted) : SNode
{
    internal string Text { get; } = text;
    internal bool Quoted { get; } = quoted;
    public override string ToString() => Quoted ? $"\"{Text}\"" : Text;
}

/// <summary>A parenthesised list <c>(head item item …)</c>. The typical KiCad shape is a
/// keyword head followed by positional atoms and nested lists.</summary>
internal sealed class SList(IReadOnlyList<SNode> items) : SNode
{
    internal IReadOnlyList<SNode> Items { get; } = items;

    /// <summary>The keyword head — the first item's text when it is a bare atom, else null.</summary>
    internal string? Head => Items.Count > 0 && Items[0] is SAtom a ? a.Text : null;

    /// <summary>All nested lists whose head matches, in order.</summary>
    internal IEnumerable<SList> Lists(string head) =>
        Items.OfType<SList>().Where(l => string.Equals(l.Head, head, StringComparison.Ordinal));

    /// <summary>The first nested list whose head matches, or null.</summary>
    internal SList? List(string head) => Lists(head).FirstOrDefault();

    /// <summary>The positional atoms after the head, in order (nested lists skipped).</summary>
    internal IReadOnlyList<SAtom> Args =>
        Items.Skip(1).OfType<SAtom>().ToList();

    /// <summary>The i-th positional atom's text after the head, or null.</summary>
    internal string? Arg(int i)
    {
        var args = Args;
        return i >= 0 && i < args.Count ? args[i].Text : null;
    }

    /// <summary>The numeric positional atoms after the head, in order (non-numbers stop the
    /// run — so <c>(drill oval 1 2)</c> yields nothing until the caller skips <c>oval</c>).</summary>
    internal IReadOnlyList<double> Numbers()
    {
        var numbers = new List<double>();
        foreach (var atom in Args)
        {
            if (!TryNumber(atom.Text, out double v))
                break;
            numbers.Add(v);
        }
        return numbers;
    }

    /// <summary>The numbers of a named child list (e.g. <c>(at x y a)</c> → x, y, a), or an
    /// empty list when the child is absent.</summary>
    internal IReadOnlyList<double> ChildNumbers(string head) =>
        List(head)?.Numbers() ?? [];

    /// <summary>Parses a token as an invariant-culture number.</summary>
    internal static bool TryNumber(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public override string ToString() => $"({string.Join(' ', Items)})";
}

/// <summary>
/// A hand-rolled, dependency-free S-expression reader for the KiCad interchange formats —
/// the <c>StepReader</c>/<c>IgesReader</c> ethos: structure validated up front, malformed
/// input refused BY NAME. It tokenises parentheses, double-quoted strings (with <c>\"</c>,
/// <c>\\</c>, <c>\n</c>, <c>\t</c> escapes) and bare tokens, and builds ONE top-level list —
/// a <c>.kicad_sym</c> or <c>.kicad_mod</c> is exactly one list. Unbalanced parentheses, an
/// unterminated string, a top-level atom, or trailing garbage after the list are each refused
/// by name.
/// </summary>
internal static class SExpr
{
    internal static SList Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int pos = 0;
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length)
            throw new FormatException("The S-expression is empty (expected a top-level '(...)').");
        if (text[pos] != '(')
            throw new FormatException(
                $"An S-expression must begin with '(', not '{text[pos]}' at offset {pos}.");
        var root = ParseList(text, ref pos);
        SkipWhitespace(text, ref pos);
        if (pos < text.Length)
            throw new FormatException(
                $"Trailing content after the top-level S-expression at offset {pos} "
                + $"('{Preview(text, pos)}').");
        return root;
    }

    private static SList ParseList(string text, ref int pos)
    {
        // Consumes the opening '(' through its matching ')'.
        pos++; // '('
        var items = new List<SNode>();
        while (true)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length)
                throw new FormatException("Unbalanced '(' — the S-expression ends inside a list.");
            char c = text[pos];
            if (c == ')')
            {
                pos++;
                return new SList(items);
            }
            if (c == '(')
                items.Add(ParseList(text, ref pos));
            else if (c == '"')
                items.Add(ParseString(text, ref pos));
            else
                items.Add(ParseToken(text, ref pos));
        }
    }

    private static SAtom ParseString(string text, ref int pos)
    {
        pos++; // opening quote
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos++];
            if (c == '"')
                return new SAtom(sb.ToString(), quoted: true);
            if (c == '\\' && pos < text.Length)
            {
                char e = text[pos++];
                sb.Append(e switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => e,
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        throw new FormatException("Unterminated quoted string in the S-expression.");
    }

    private static SAtom ParseToken(string text, ref int pos)
    {
        int start = pos;
        while (pos < text.Length && !IsDelimiter(text[pos]))
            pos++;
        return new SAtom(text[start..pos], quoted: false);
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
    }

    private static bool IsDelimiter(char c) => c == '(' || c == ')' || c == '"' || char.IsWhiteSpace(c);

    private static string Preview(string text, int pos)
    {
        int end = Math.Min(text.Length, pos + 20);
        return text[pos..end].Replace('\n', ' ').Replace('\r', ' ');
    }
}
