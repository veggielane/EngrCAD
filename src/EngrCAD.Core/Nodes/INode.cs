using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public interface INode
    {
        NativeWrapper Wrapper { get; }
        NativeWrapper Generate();

        public List<INode> Children { get; }
    }

    public interface INodeWithVolume:INode
    {

        float CalculateVolume();
    }
}
