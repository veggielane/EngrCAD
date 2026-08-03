using EngrCAD.WebDemo.Pages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The whole app: one root component and no router. This is a viewer, not a site -- the
// template's routing and layout scaffolding would only be payload weight, and payload is
// one of the two risks this prototype exists to measure.
//
// The one HttpClient is for `?example=<id>`, which fetches the assembly the docs build
// emitted for one documentation example. It could have been a JS `fetch` through interop
// to save the reference, and was not: the browser's HttpClient IS `fetch` underneath, the
// measured cost is small beside the runtime, and the alternative would put a second
// policy-carrying function into a JavaScript file whose whole design rule is that it
// carries none.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.RootComponents.Add<Home>("#app");
await builder.Build().RunAsync();
