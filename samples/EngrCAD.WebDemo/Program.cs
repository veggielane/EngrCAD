using EngrCAD.WebDemo.Pages;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The whole app: one root component, no router, no HttpClient. This is a viewer, not a
// site -- the template's routing and layout scaffolding would only be payload weight,
// and payload is one of the two risks this prototype exists to measure.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Home>("#app");
await builder.Build().RunAsync();
