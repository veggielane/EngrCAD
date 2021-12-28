using EngrCADOCWrapper;

namespace EngrCAD.Core.Edges;

public abstract class Edge: IEdge
{
    public EdgeWrapper Wrapper { get; }

    internal Edge(EdgeWrapper wrapper)
    {
        Wrapper = wrapper;
    }

}