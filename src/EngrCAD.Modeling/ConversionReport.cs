using System.Text;

namespace EngrCAD.Modeling;

/// <summary>How one node of a shape graph is handled by a conversion.</summary>
public enum NodeSupport
{
    /// <summary>The target engine performs the operation exactly.</summary>
    Native,

    /// <summary>Converted through another representation (approximate but robust).</summary>
    Bridged,

    /// <summary>No route to the target exists; the conversion throws.</summary>
    Impossible,
}

public sealed record ConversionEntry(string Node, NodeSupport Support, string? Detail = null)
{
    public override string ToString() =>
        Detail is null ? $"{Node}: {Support}" : $"{Node}: {Support} — {Detail}";
}

/// <summary>Per-node plan for lowering a <see cref="Shape"/> to one representation.</summary>
public sealed class ConversionReport
{
    public TargetRep Target { get; }
    public IReadOnlyList<ConversionEntry> Entries { get; }

    public bool IsConvertible => Entries.All(e => e.Support != NodeSupport.Impossible);

    internal ConversionReport(TargetRep target, IReadOnlyList<ConversionEntry> entries)
    {
        Target = target;
        Entries = entries;
    }

    public override string ToString()
    {
        var text = new StringBuilder($"Conversion to {Target}:");
        foreach (var entry in Entries)
            text.Append("\n  ").Append(entry);
        return text.ToString();
    }
}

/// <summary>Thrown when a shape graph contains nodes with no route to the requested
/// representation; <see cref="Report"/> names them.</summary>
public sealed class ShapeConversionException(ConversionReport report)
    : InvalidOperationException(report.ToString())
{
    public ConversionReport Report { get; } = report;
}
