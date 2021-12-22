using EngrCADOCWrapper;

namespace EngrCAD.Core;

public class Edge
{
    public EdgeWrapper Wrapper { get; }

    internal Edge(EdgeWrapper wrapper)
    {
        Wrapper = wrapper;
    }

}