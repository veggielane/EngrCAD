using System.Numerics;
using EngrCAD.Core.Nodes.Transformations;
using EngrCAD.Core.Numerics;

namespace EngrCAD.Core.Nodes
{
    public abstract partial class Node
    {
        public Node Transform(Matrix4x4 matrix) => new Transform
        {
            Node = this,
            Matrix = matrix
        };

        public Node Translate(Vector3 position) => new Translate(this, position);
        public Node Center() => new Translate(this, -BoundingBox.Center);

        public Node Translate(float x, float y, float z) => Translate(new Vector3(x, y, z));

        public Node Rotate(Angle angle, Vector3 origin, Vector3 direction) => new Rotate(this, origin, direction, angle);
        public Node RotateX(Angle angle) => new Rotate(this, Vector3.UnitX, angle);
        public Node RotateY(Angle angle) => new Rotate(this, Vector3.UnitY, angle);
        public Node RotateZ(Angle angle) => new Rotate(this, Vector3.UnitZ, angle);
    }
}
