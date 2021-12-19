using System.Numerics;

namespace EngrCAD.Core.Nodes.Transformations;

public interface ITransformationNode : INode
{
    /// <summary>
    /// The Matrix equivalent to the transform
    /// </summary>
    public Matrix4x4 Matrix { get; }
}