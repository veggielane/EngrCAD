using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// The standard orthographic view directions — "front", "top", "right" and friends —
/// as vectors from the model toward the eye.
///
/// <para><b>Why this table lives in the modelling layer.</b> It started in the view
/// cube, because a widget was the first thing that needed it. But what "front" means is
/// a document convention, not a rendering one: a drawing sheet's front view and the
/// viewer's Front button must name the same direction, and a drawing is built without a
/// viewer in the room. So the table sits here, beneath both, and
/// <c>ViewCubeMath.DirectionFor</c> delegates to it — the same move the MCP server's
/// deleted <c>StandardViews.cs</c> made when its poses were routed through
/// <c>ViewCubeMath.PoseFor</c>. One table, three consumers, no way to disagree.</para>
///
/// <para>Directions point from the model TOWARD the eye, matching the orbit camera's
/// own convention (its <c>ViewDirection</c>) — so "front" is −Y, the direction you
/// stand in to look at the front of a part modelled facing you.</para>
/// </summary>
public static class StandardViews
{
    /// <summary>The standard view names every front end offers (viewer toolbar,
    /// <c>screenshot</c>'s named views, remote-control <c>set_view</c>, drawing
    /// views), in discovery order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["iso", "front", "back", "left", "right", "top", "bottom"];

    /// <summary>
    /// The view direction (model toward eye) of a standard view name, or null for an
    /// unknown name. "iso" is the front-right-top corner.
    /// </summary>
    public static Vector3d? DirectionFor(string view) => view.ToLowerInvariant() switch
    {
        "front" => new Vector3d(0, -1, 0),
        "back" => new Vector3d(0, 1, 0),
        "left" => new Vector3d(-1, 0, 0),
        "right" => new Vector3d(1, 0, 0),
        "top" => new Vector3d(0, 0, 1),
        "bottom" => new Vector3d(0, 0, -1),
        "iso" => new Vector3d(1, -1, 1),
        _ => null,
    };

    /// <summary>
    /// The sheet frame of an orthographic view along <paramref name="direction"/>: the
    /// frame's Z is the (normalized) view direction, X is sheet-right and Y sheet-up,
    /// so <c>frame.ToLocal(p)</c> gives (x, y) on the paper and z as depth toward the
    /// viewer. Projecting a model point is then one call and no bespoke matrix.
    ///
    /// <para>Sheet-up is world +Z for every horizontal view — the drafting convention
    /// that a part's height runs up the page — which leaves the two views looking along
    /// the Z axis to resolve: a TOP view takes world +Y up and a BOTTOM view −Y, which
    /// is exactly what puts the top view above the front view in third-angle projection
    /// with the part's far side at the top of the page. In every case sheet-right is
    /// <c>up × direction</c>, so the frame is right-handed by construction.</para>
    /// </summary>
    public static Frame3d SheetFrame(in Vector3d direction, in Vector3d origin = default)
    {
        var z = direction.Normalized(Tolerance.Default);
        // A view within a degree of the world Z axis has no usable world-up; the two
        // that matter (top, bottom) are exactly axial, and the threshold is a
        // near-degeneracy guard on a unit dot product, not a model tolerance.
        var up = Math.Abs(z.Z) > 0.999
            ? new Vector3d(0, Math.Sign(z.Z), 0)
            : Vector3d.UnitZ;
        var right = up.Cross(z).Normalized(Tolerance.Default);
        // Sheet-up re-derived as z x right rather than taken from the hint: right is
        // already unit and perpendicular to z, so this is exactly the hint's
        // perpendicular component and FromOrthonormal's validation passes without
        // Gram-Schmidt moving anything.
        return Frame3d.FromOrthonormal(origin, right, z.Cross(right));
    }
}
