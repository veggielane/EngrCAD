namespace EngrCAD.Ecad;

/// <summary>A board's surface finish — the plating a fabricator applies to the exposed copper. A
/// small named set plus an <see cref="Other"/> escape hatch (named by
/// <see cref="PcbFabricationSpec.SurfaceFinishOther"/>), so a spec can state a finish this enum
/// does not list without the drawing inventing one.</summary>
public enum PcbSurfaceFinish
{
    /// <summary>Electroless nickel / immersion gold.</summary>
    Enig,

    /// <summary>Hot-air solder levelling (leaded).</summary>
    Hasl,

    /// <summary>Lead-free hot-air solder levelling (HAL).</summary>
    HaslLeadFree,

    /// <summary>Organic solderability preservative.</summary>
    Osp,

    /// <summary>Immersion silver.</summary>
    ImmersionSilver,

    /// <summary>Immersion tin.</summary>
    ImmersionTin,

    /// <summary>A finish named by <see cref="PcbFabricationSpec.SurfaceFinishOther"/> (hard gold,
    /// ENEPIG, …).</summary>
    Other,
}

/// <summary>
/// A board's FABRICATION REQUIREMENTS — the fab-package fields a board house needs but the
/// geometry does not carry: the base material, the finished board thickness, the copper weight,
/// the surface finish, the mask and silkscreen colours, the IPC-6012 class, the minimum trace
/// width and clearance, and any free-form notes.
///
/// <para><b>Every field is OPTIONAL, and <c>null</c> (or an empty <see cref="Notes"/> list) is
/// "not stated"</b> — a spec states what it wants and leaves the rest to the fabricator's default,
/// exactly the write-only-when-stated convention the drawing's notes and the layout file already
/// use. So <see cref="Default"/> (an empty spec) is valid and states nothing, and the fabrication
/// drawing prints a note for a stated field and OMITS one for an unstated field — it never
/// invents a value.</para>
///
/// <para><b>It rides in the layout as LAYOUT TRUTH</b> (<see cref="PcbLayout.Fabrication"/>): it
/// is a fabrication PARAMETER of the board, the same KIND of thing the solder-mask / silkscreen /
/// paste settings are, so it lives on the layout beside them and persists write-only-when-stated
/// (a layout that states none saves byte-identically to a pre-spec one, and a stated spec is a
/// <c>save → load → save</c> fixed point).</para>
/// </summary>
public sealed record PcbFabricationSpec
{
    /// <summary>The base laminate material (e.g. <c>"FR-4"</c>, <c>"Rogers 4350B"</c>,
    /// <c>"Aluminium"</c>), or null for not stated. A free string, because the material space is
    /// open (FR-4, high-Tg, PTFE, flex polyimide, metal core, …) and a fabricator reads it
    /// verbatim.</summary>
    public string? BaseMaterial { get; init; }

    /// <summary>The finished board thickness (mm) — the delivered stackup thickness including
    /// copper and finish, which is what a fab house quotes to — or null for not stated. When
    /// stated it is the authoritative "finished board thickness" the drawing prints, over the
    /// modelled plate thickness (which is the bare geometry). Validated finite and positive when
    /// stated.</summary>
    public double? FinishedThicknessMm { get; init; }

    /// <summary>The outer-layer copper weight in ounces (1 oz ≈ 35 µm), or null for not stated.
    /// Ounces are the industry spelling of a copper spec; the drawing derives the µm for the note.
    /// Validated finite and positive when stated.</summary>
    public double? CopperWeightOz { get; init; }

    /// <summary>The surface finish (a named <see cref="PcbSurfaceFinish"/>, or
    /// <see cref="PcbSurfaceFinish.Other"/> named by <see cref="SurfaceFinishOther"/>), or null for
    /// not stated.</summary>
    public PcbSurfaceFinish? SurfaceFinish { get; init; }

    /// <summary>The finish name when <see cref="SurfaceFinish"/> is
    /// <see cref="PcbSurfaceFinish.Other"/> (required in that case; ignored otherwise).</summary>
    public string? SurfaceFinishOther { get; init; }

    /// <summary>The solder-mask colour (e.g. <c>"Green"</c>, <c>"Black"</c>, <c>"Blue"</c>), or
    /// null for not stated. A free string, because the fabricator's colour list is theirs.</summary>
    public string? SolderMaskColour { get; init; }

    /// <summary>The silkscreen (legend) colour (e.g. <c>"White"</c>, <c>"Black"</c>), or null for
    /// not stated.</summary>
    public string? SilkscreenColour { get; init; }

    /// <summary>The IPC-6012 performance class the board is fabricated to (1, 2 or 3), or null for
    /// not stated. Validated to be 1, 2 or 3 when stated.</summary>
    public int? Ipc6012Class { get; init; }

    /// <summary>The minimum trace width (mm) the fabricator must hold, or null for not stated.
    /// Validated finite and positive when stated.</summary>
    public double? MinTraceWidthMm { get; init; }

    /// <summary>The minimum copper clearance (mm) the fabricator must hold, or null for not stated.
    /// Validated finite and positive when stated.</summary>
    public double? MinClearanceMm { get; init; }

    /// <summary>Free-form fabrication notes (each printed verbatim as its own drawing note), or an
    /// empty list for none. This is the escape hatch for anything the typed fields do not carry
    /// (impedance control, panelisation, a specific IPC standard, …).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The empty spec — every field not stated. Valid, and prints no fabrication notes
    /// (so it is indistinguishable from no spec at all, which is the point of write-only-when-
    /// stated).</summary>
    public static PcbFabricationSpec Default { get; } = new();

    /// <summary>Whether the spec states ANY field — false for <see cref="Default"/> (and for any
    /// all-unstated spec), which is why an empty spec produces no notes and is not persisted.</summary>
    public bool StatesAnything =>
        BaseMaterial is not null || FinishedThicknessMm is not null || CopperWeightOz is not null
        || SurfaceFinish is not null || SolderMaskColour is not null || SilkscreenColour is not null
        || Ipc6012Class is not null || MinTraceWidthMm is not null || MinClearanceMm is not null
        || Notes.Count > 0;

    /// <summary>The display label for <see cref="SurfaceFinish"/> in upper case (the drawing-note
    /// convention), or null when the finish is not stated. For <see cref="PcbSurfaceFinish.Other"/>
    /// it is <see cref="SurfaceFinishOther"/> upper-cased.</summary>
    public string? SurfaceFinishLabel => SurfaceFinish switch
    {
        null => null,
        PcbSurfaceFinish.Enig => "ENIG",
        PcbSurfaceFinish.Hasl => "HASL",
        PcbSurfaceFinish.HaslLeadFree => "LEAD-FREE HASL",
        PcbSurfaceFinish.Osp => "OSP",
        PcbSurfaceFinish.ImmersionSilver => "IMMERSION SILVER",
        PcbSurfaceFinish.ImmersionTin => "IMMERSION TIN",
        PcbSurfaceFinish.Other => (SurfaceFinishOther ?? "OTHER").ToUpperInvariant(),
        _ => SurfaceFinish.ToString()!.ToUpperInvariant(),
    };

    /// <summary>Validates every STATED field (an unstated field states nothing to validate) and
    /// throws by name on a bad one: a non-finite or non-positive thickness / copper weight /
    /// minimum trace / minimum clearance, an IPC class that is not 1, 2 or 3, or an
    /// <see cref="PcbSurfaceFinish.Other"/> finish with no <see cref="SurfaceFinishOther"/> name.</summary>
    internal void Validate()
    {
        RequirePositive(FinishedThicknessMm, "finished board thickness");
        RequirePositive(CopperWeightOz, "copper weight");
        RequirePositive(MinTraceWidthMm, "minimum trace width");
        RequirePositive(MinClearanceMm, "minimum clearance");
        if (Ipc6012Class is { } cls && cls is < 1 or > 3)
            throw new ArgumentException(
                $"A fabrication spec's IPC-6012 class must be 1, 2 or 3 (got {cls}).");
        if (SurfaceFinish == PcbSurfaceFinish.Other && string.IsNullOrWhiteSpace(SurfaceFinishOther))
            throw new ArgumentException(
                "A fabrication spec's surface finish is 'Other' but names no finish; set "
                + "SurfaceFinishOther, or use a named PcbSurfaceFinish.");
    }

    private static void RequirePositive(double? value, string what)
    {
        if (value is { } v && (!double.IsFinite(v) || v <= 0))
            throw new ArgumentException(
                $"A fabrication spec's {what}, when stated, must be finite and positive (got {v:g6}).");
    }
}
