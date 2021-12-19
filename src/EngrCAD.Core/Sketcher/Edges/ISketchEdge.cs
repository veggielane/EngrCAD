using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher.Edges;

public interface ISketchEdge
{
    EdgeWrapper ToEdge();
}