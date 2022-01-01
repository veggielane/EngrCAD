using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EngrCAD.Client;

public class Runner
{
    public static ServiceProvider Configure(Func<IServiceCollection, IServiceCollection> config)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(builder =>
        {
            builder.AddConsole();
        });
        return config(sc).BuildServiceProvider();
    }
}