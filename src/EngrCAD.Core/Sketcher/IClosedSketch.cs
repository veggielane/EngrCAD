using System.Collections.Generic;
using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public interface IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }


}