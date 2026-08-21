using ConflictDep;
using Plugman.Contracts;

namespace ConflictPluginA;

/// <summary>Loads its own private copy of ConflictDep v1.0.0.</summary>
public sealed class ConflictPluginA : ICommandPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.conflict.a",
        Name: "Conflict Plugin A",
        Version: "1.0.0",
        Description: "Carries ConflictDep v1.",
        Author: "tests");

    public IReadOnlyList<string> Commands => ["depversion"];

    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct) =>
        Task.FromResult(command == "depversion" ? DepInfo.Describe() : "unknown");

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
