using System.Collections.Generic;
using System.Numerics;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations
{
    public class Transform : BaseNode, ITransformationNode
    {
        public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

        public Transform()
        {

        }

        public Transform(Matrix4x4 matrix, IEnumerable<INode> nodes) : base(nodes)
        {
            Matrix = matrix;
        }

        public Transform(Matrix4x4 matrix, params INode[] nodes) : this(matrix, (IEnumerable<INode>)nodes)
        {

        }

        public override NativeWrapper Generate()
        {
            throw new System.NotImplementedException();
        }
    }
}
