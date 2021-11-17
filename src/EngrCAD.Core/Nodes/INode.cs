using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core.Nodes
{
    public interface INode
    {
        /// <summary>
        /// List of child nodes
        /// </summary>
        List<INode> Children { get; }
    }
}
