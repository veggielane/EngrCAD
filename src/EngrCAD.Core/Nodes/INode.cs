using System;
using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes.Operations;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public interface INode
{
    ShapeWrapper Wrapper { get; }
    ShapeWrapper Generate();

    float Volume { get; }
    IAABB BoundingBox { get; }
    
    ShapeType ShapeType { get; }


    IEnumerable<Face> Faces { get; }
    IEnumerable<IEdge> Edges { get; }

}