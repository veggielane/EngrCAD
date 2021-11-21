using System.Collections.Generic;

namespace EngrCAD.Core.Nodes
{
    public interface INodeWithChildren: INode
    {
        /// <summary>
        /// List of child nodes
        /// </summary>
        List<INode> Children { get; }


    }
}