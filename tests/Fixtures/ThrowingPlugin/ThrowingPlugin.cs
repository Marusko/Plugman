using Plugman.Contracts;

namespace ThrowingPlugin;

/// <summary>Fails during initialization; the manager must roll the load back and survive.</summary>
public sealed class ThrowingPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.throwing",
        Name: "Throwing Fixture Plugin",
        Version: "1.0.0",
        Description: "Throws from InitializeAsync.",
        Author: "tests");

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) =>
        throw new InvalidOperationException("fixture initialization failure");

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
