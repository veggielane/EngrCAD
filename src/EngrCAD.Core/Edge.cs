using EngrCADOCWrapper;

namespace EngrCAD.Core;

public class Edge
{
    public EdgeWrapper Wrapper { get; }

    internal Edge(EdgeWrapper wrapper)
    {
        Wrapper = wrapper;
    }

    public bool ParallelTo(Axis axis)
    {
        return false;
    }
}

public class Axis
{

}