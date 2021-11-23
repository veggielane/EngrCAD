using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public interface INode
    {
        NativeWrapper Generate();

        public List<INode> Children { get; }
    }

    public interface INodeWithVolume:INode
    {

        float CalculateVolume();
    }
}
