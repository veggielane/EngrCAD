namespace EngrCAD.Core.Nodes
{
    public interface INodeWithChild:INode
    {
        INode Child { get; }
    }
}