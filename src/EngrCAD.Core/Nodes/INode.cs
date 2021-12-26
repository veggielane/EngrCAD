using System.Collections.Generic;
using EngrCAD.Core.Edges;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public partial interface INode
{
    ShapeWrapper Wrapper { get; }
    ShapeWrapper Generate();

    //public List<INode> Children { get; }
    float Volume { get; }
    IAABB BoundingBox { get; }


    ShapeType ShapeType { get; }


    IEnumerable<Face> Faces { get; }
    IEnumerable<IEdge> Edges { get; }

}