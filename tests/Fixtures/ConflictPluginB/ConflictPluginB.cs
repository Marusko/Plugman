using ConflictDep;
using Plugman.Contracts;

namespace ConflictPluginB;

/// <summary>Loads its own private copy of ConflictDep v2.0.0.</summary>
public sealed class ConflictPluginB : ICommandPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.conflict.b",
        Name: "Conflict Plugin B",
        Version: "1.0.0",
        Description: "Carries ConflictDep v2.",
        Author: "tests");

    public IReadOnlyList<string> Commands => ["depversion"];

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct) =>
        Task.FromResult(command == "depversion" ? DepInfo.Describe() : "unknown");

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
