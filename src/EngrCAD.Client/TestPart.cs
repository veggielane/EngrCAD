using System.Linq;
using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;

namespace EngrCAD.Client;

public class TestPart : Part<PartMetadata>
{
    public TestPart()
    {
        Metadata = new PartMetadata()
        {
            Name = "Test Part"
        };
    }

    public override Body Generate()
    {

        var a = new Box { Size = new Vector3(2, 2, 2) };
        var node =  a.Round(0.5f, edges => edges.OfType<EdgeLine>().Where(line => line.ParallelTo(Vector3.UnitZ)));
        return node.ToBody();
    }
}