using Microsoft.Extensions.Logging;
using Plugman.Contracts;
using Plugman.Contracts.Blazor;

namespace SampleBlazorPlugin;

/// <summary>
/// A plugin with a Blazor component: <see cref="IPlugin"/> plus the
/// <see cref="IBlazorUiPlugin"/> capability.
/// </summary>
public sealed class SampleBlazorPlugin : IBlazorUiPlugin
{
    private IPluginContext? _context;

    public PluginMetadata Metadata { get; } = new(
        Id: "com.plugman.sample.blazor",
        Name: "Sample Blazor Plugin",
        Version: "1.0.0",
        Description: "Ships a Razor component the host renders with DynamicComponent.",
        Author: "Plugman samples");

    public string ViewTitle => "Sample Blazor";

    /// <summary>
    /// The component type, resolved out of this plugin's load context. A host that caches it
    /// must drop the cached <see cref="Type"/> before disabling the plugin — a live Type object
    /// roots the load context just as firmly as a live instance does.
    /// </summary>
    public Type ComponentType => typeof(SamplePanel);

    public IReadOnlyDictionary<string, object?>? DefaultParameters { get; } = new Dictionary<string, object?>
    {
        [nameof(SamplePanel.Title)] = "Sample Blazor Plugin"
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        context.Logger.LogInformation("Sample Blazor plugin initialized.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _context?.Logger.LogInformation("Sample Blazor plugin shutting down.");
        _context = null;
        return Task.CompletedTask;
    }
}
