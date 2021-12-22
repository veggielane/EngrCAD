using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public interface INode
{
    ShapeWrapper Wrapper { get; }
    ShapeWrapper Generate();

    //public List<INode> Children { get; }
    float Volume { get; }

    ShapeType ShapeType { get; }


    IEnumerable<Face> Faces { get; }
    IEnumerable<Edge> Edges { get; }
}

public enum ShapeType
{
    Compound, 
    CompSolid, 
    Solid, 
    Shell,
    Face,
    Wire,
    Edge,
    Vertex,
    Shape
}