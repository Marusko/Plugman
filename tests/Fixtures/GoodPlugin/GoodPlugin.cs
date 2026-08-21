using Microsoft.Extensions.Logging;
using Plugman.Contracts;

namespace GoodPlugin;

/// <summary>A well-behaved fixture plugin used by most tests.</summary>
public sealed class GoodPlugin : ICommandPlugin
{
    public static int InitializeCount;

    private IPluginContext? _context;

    public PluginMetadata Metadata { get; } = new(
        Id: "fixture.good",
        Name: "Good Fixture Plugin",
        Version: "1.2.3",
        Description: "Behaves.",
        Author: "tests");

    public IReadOnlyList<string> Commands => ["echo", "boom", "datadir"];

    public bool IsInitialized { get; private set; }

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        IsInitialized = true;
        Interlocked.Increment(ref InitializeCount);
        context.Logger.LogInformation("Good fixture plugin initialized.");
        return Task.CompletedTask;
    }

    public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct) =>
        command switch
        {
            "echo" => Task.FromResult(args.TryGetValue("text", out var text) ? text : string.Empty),
            "datadir" => Task.FromResult(_context?.PluginDataDirectory ?? string.Empty),
            "boom" => throw new InvalidOperationException("fixture command failure"),
            _ => Task.FromResult($"unknown:{command}")
        };

    public Task ShutdownAsync(CancellationToken ct)
    {
        _context = null;
        IsInitialized = false;
        return Task.CompletedTask;
    }
}
