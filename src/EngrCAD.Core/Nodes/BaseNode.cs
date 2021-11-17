using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core.Nodes
{
    public abstract class BaseNode : INode
    {
        /// <summary>
        /// List of child nodes
        /// </summary>
        public List<INode> Children { get; protected set; } = new List<INode>();

        /// <summary>
        /// Default BaseNode Constructor
        /// </summary>
        protected BaseNode()
        {

        }

        /// <summary>
        /// BaseNode Constructor
        /// </summary>
        /// <param name="children">child nodes</param>
        protected BaseNode(IEnumerable<INode> children)
        {
            Children.AddRange(children);
        }
    }
}
