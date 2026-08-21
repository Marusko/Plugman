using Microsoft.Extensions.Logging;
using Plugman.Core;
using Plugman.Sample.BlazorServerHost.Components;
using Plugman.Samples;

var builder = WebApplication.CreateBuilder(args);

// CreateBuilder only wires static web assets up automatically in Development. Doing it
// explicitly keeps this sample runnable in any environment: without it the framework's own
// _framework/blazor.web.js is not served, the circuit never starts, and every plugin panel
// renders as dead HTML.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Same three lines as every other host: a plugins root, the host's services, a logger factory.
// Blazor Server is a normal long-running .NET process, so the AssemblyLoadContext story here
// is identical to the console and WPF hosts.
builder.Services.AddSingleton(serviceProvider => new PluginManager(
    SamplePaths.ResolvePluginsRoot(),
    serviceProvider,
    serviceProvider.GetRequiredService<ILoggerFactory>()));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

// MapStaticAssets rather than UseStaticFiles: it serves the endpoint-based static assets in
// every environment, including the framework's own blazor.web.js. UseStaticFiles only picks up
// static web assets automatically in Development, which leaves the circuit unable to start
// (and every plugin panel inert) when the sample is run in Production.
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

var manager = app.Services.GetRequiredService<PluginManager>();
await manager.ScanAsync();
await manager.LoadEnabledAsync();

app.Lifetime.ApplicationStopping.Register(() => manager.DisposeAsync().AsTask().GetAwaiter().GetResult());

app.Run();
