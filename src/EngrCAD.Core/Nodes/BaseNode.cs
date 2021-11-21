using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public abstract class BaseNode : INodeWithChildren
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


        public abstract NativeWrapper Wrap();
    }

    public class Sphere:BaseNode
    {
        public float Radius { get; init; } = 1f;


        public override NativeWrapper Wrap()
        {
            return NativeWrapper.Sphere(Radius);
        }
    }
}
