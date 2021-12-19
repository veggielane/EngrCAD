using System.Collections.Generic;
using EngrCAD.Core;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using Xunit;

namespace EngrCAD.Tests;

public class PartTests
{
    [Fact]
    public void Volume()
    {
        var part = new TestPart();
        Assert.Equal("Test Part", part.Name);

    }

    public class TestPart:Part<PartMetadata>
    {
        public TestPart()
        {
            Name = "Test Part";
        }

        public override IEnumerable<Body> Generate()
        {

            yield return new Box().ToBody();
        }
    }
}