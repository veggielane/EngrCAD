using EngrCAD.WebDemo.Pages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The whole app: one root component and no router. This is a viewer, not a site -- the
// template's routing and layout scaffolding would only be payload weight, and payload is
// one of the two risks this prototype exists to measure.
//
// The one HttpClient is for `?example=<id>`, which fetches the assembly the docs build
// emitted for one documentation example. It could have been a JS `fetch` through interop
// to save the reference, and was not: the browser's HttpClient IS `fetch` underneath, and
// the alternative would put a second policy-carrying function into a JavaScript file whose
// whole design rule is that it carries none. Measured rather than assumed -- the publish
// grew 58 KB brotli in total, of which System.Net.Http is 48.8 (it was in the assembly list
// already and trimmed to nothing; using it keeps the fetch path) and System.Private.CoreLib
// 7.6 (the Assembly.Load and reflection surface the loader needs). That is 2.1% of a
// 2.84 MB app, so the JS route would buy back under two percent.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.RootComponents.Add<Home>("#app");
await builder.Build().RunAsync();
