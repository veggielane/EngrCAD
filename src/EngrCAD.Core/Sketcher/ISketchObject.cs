using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher;

public interface ISketchObject
{
    EdgeWrapper ToEdge();
}