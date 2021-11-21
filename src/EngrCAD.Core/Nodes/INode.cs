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
        NativeWrapper Wrap();
    }
    public interface INodeWithChildren: INode
    {
        /// <summary>
        /// List of child nodes
        /// </summary>
        List<INode> Children { get; }


    }
}
