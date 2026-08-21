using Plugman.Contracts;

namespace HangingPlugin;

/// <summary>
/// Never returns from InitializeAsync and ignores its cancellation token — the worst-behaved
/// plugin the manager has to survive, since the call is made while its lock is held.
/// </summary>
public sealed class HangingInitPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.hanging.init",
        Name: "Hanging Init Fixture Plugin",
        Version: "1.0.0",
        Description: "Blocks forever in InitializeAsync.",
        Author: "tests");

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, CancellationToken.None);

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Initializes fine, then hangs on the way out.</summary>
public sealed class HangingShutdownPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.hanging.shutdown",
        Name: "Hanging Shutdown Fixture Plugin",
        Version: "1.0.0",
        Description: "Blocks forever in ShutdownAsync.",
        Author: "tests");

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, CancellationToken.None);
}
