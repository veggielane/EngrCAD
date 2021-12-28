using EngrCAD.Core.Datums;

namespace EngrCAD.Core.Nodes.Features;

public class TappedHole : Hole
{
    protected TappedHole(float dia, float depth, ICSYS csys) : base(dia, depth, csys)
    {

    }

    public static TappedHole M1_6(ICSYS csys, float depth) => new (1.25f, depth, csys);
}