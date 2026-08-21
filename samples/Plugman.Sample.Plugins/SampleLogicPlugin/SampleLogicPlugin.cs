using System.Globalization;
using Microsoft.Extensions.Logging;
using Plugman.Contracts;

namespace SampleLogicPlugin;

/// <summary>
/// A pure logic plugin: <see cref="IPlugin"/> plus the <see cref="ICommandPlugin"/> capability.
/// It has no idea what kind of host it is running in.
/// </summary>
public sealed class SampleLogicPlugin : ICommandPlugin, IConsoleUiPlugin
{
    private IPluginContext? _context;
    private int _invocations;

    public PluginMetadata Metadata { get; } = new(
        Id: "com.plugman.sample.logic",
        Name: "Sample Logic Plugin",
        Version: "1.0.0",
        Description: "Demonstrates a UI-free command plugin: echo, time, count, state and a deliberate failure.",
        Author: "Plugman samples");

    public IReadOnlyList<string> Commands => ["echo", "time", "count", "remember", "recall", "boom"];

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        context.Logger.LogInformation(
            "Sample logic plugin initialized. Data directory: {DataDirectory}",
            context.PluginDataDirectory);

        return Task.CompletedTask;
    }

    public Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        _invocations++;
        var context = _context ?? throw new InvalidOperationException("InitializeAsync has not run.");

        return Task.FromResult(command.ToLowerInvariant() switch
        {
            "echo" => args.TryGetValue("text", out var text) ? text : "(nothing to echo)",
            "time" => DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            "count" => $"Executed {_invocations} command(s) since load.",
            "remember" => Remember(context, args),
            "recall" => Recall(context),

            // Deliberate: proves a throwing capability call is caught at the manager boundary
            // and surfaced as a PluginLoadError instead of taking the host down.
            "boom" => throw new InvalidOperationException("The sample plugin threw on purpose."),

            _ => $"Unknown command '{command}'. Try: {string.Join(", ", Commands)}"
        });
    }

    // --- IConsoleUiPlugin: the console's answer to a UI capability. It lives in the core
    // contracts because a console needs no UI framework, so a plugin can offer it without
    // taking on any extra package.

    public string ScreenTitle => "Sample plugin console";

    public Task RunInteractiveAsync(IPluginContext context, CancellationToken ct)
    {
        Console.WriteLine($"-- {ScreenTitle} -- (blank line to leave)");

        while (!ct.IsCancellationRequested)
        {
            Console.Write("sample> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                break;

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            var args = parts.Length > 1
                ? new Dictionary<string, string> { ["text"] = parts[1] }
                : new Dictionary<string, string>();

            try
            {
                Console.WriteLine(ExecuteAsync(parts[0], args, ct).GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _context?.Logger.LogInformation("Sample logic plugin shutting down after {Count} command(s).", _invocations);
        _context = null;
        return Task.CompletedTask;
    }

    // Shows the per-plugin data directory being used for plugin-owned state.
    private static string Remember(IPluginContext context, IReadOnlyDictionary<string, string> args)
    {
        var value = args.TryGetValue("text", out var text) ? text : string.Empty;
        var path = Path.Combine(context.PluginDataDirectory, "note.txt");
        File.WriteAllText(path, value);
        return $"Stored {value.Length} character(s) in {path}.";
    }

    private static string Recall(IPluginContext context)
    {
        var path = Path.Combine(context.PluginDataDirectory, "note.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "(nothing remembered yet)";
    }
}
