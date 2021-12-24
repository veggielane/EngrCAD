using EngrCADOCWrapper;

namespace EngrCAD.Core.Edges;

public interface IEdge
{
    public EdgeWrapper Wrapper { get; }
}

public abstract class Edge: IEdge
{
    public EdgeWrapper Wrapper { get; }

    internal Edge(EdgeWrapper wrapper)
    {
        Wrapper = wrapper;
    }

}