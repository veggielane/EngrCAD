using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public abstract class BaseNode : INode
{
    private NativeWrapper _cached;
    public NativeWrapper Wrapper => _cached ??= Generate();
    public abstract NativeWrapper Generate();

    public float CalculateVolume() => Wrapper.CalculateVolume();
    public IEnumerable<Face> Faces => Wrapper.GetFaces().Select(wrapper => new Face(wrapper));
    public IEnumerable<Edge> Edges => Wrapper.GetEdges().Select(wrapper => new Edge(wrapper));
}