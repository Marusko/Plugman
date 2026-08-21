using Plugman.Contracts;

namespace DependentPlugin;

/// <summary>Declares a dependency on another plugin; used to test load ordering.</summary>
public sealed class DependentPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.dependent",
        Name: "Dependent Fixture Plugin",
        Version: "1.0.0",
        Description: "Requires fixture.good.",
        Author: "tests",
        Dependencies: ["fixture.good"]);

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
