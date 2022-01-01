using System.Linq;
using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Nodes.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EngrCAD.Client;

class Program
{
    static void Main()
    {


        var provider = Runner.Configure(collection => collection.AddSingleton<IPart,TestPart>());

        var t = provider.GetServices<IPart>();
    }
}