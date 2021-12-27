using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using EngrCAD.Core.Nodes.Transformations;

namespace EngrCAD.Core.Nodes
{
    public abstract partial class Node
    {

        public Node Transform(Matrix4x4 matrix) => new Transform
        {
            Child = this,
            Matrix = matrix
        };

        public Node Translate(Vector3 position) => new Translate
        {
            Child = this,
            Position = position
        };

        public Node Centered()
        {
            var aabb = BoundingBox;
            return new Translate
            {
                Child = this,
                Position = -aabb.Center
            };
        }

        public Node Translate(float x, float y, float z) => Translate(new Vector3(x, y, z));

        public Node Rotate(float radians, Vector3 origin, Vector3 direction) => new Rotate()
        {
            Child = this,
            Radians = radians,
            Origin = origin,
            Direction = direction
        };

        public Node RotateX(float radians) => new Rotate()
        {
            Child = this,
            Radians = radians,
            Direction = Vector3.UnitX
        };
        public Node RotateY(float radians) => new Rotate()
        {
            Child = this,
            Radians = radians,
            Direction = Vector3.UnitY
        };
        public Node RotateZ(float radians) => new Rotate()
        {
            Child = this,
            Radians = radians,
            Direction = Vector3.UnitZ
        };
    }
}
