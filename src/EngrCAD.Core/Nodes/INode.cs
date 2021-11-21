using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public interface INode
    {
        NativeWrapper Generate();
    }
}
