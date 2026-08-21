using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugman.Contracts;
using Plugman.Core;
using Plugman.Samples;

namespace Plugman.Sample.ConsoleHost;

/// <summary>
/// Console host. Loads plugins the same way every other host does, and consumes them through
/// the non-UI <see cref="ICommandPlugin"/> and <see cref="IConsoleUiPlugin"/> capabilities.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var pluginsRoot = SamplePaths.ResolvePluginsRoot();

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            }));

        // Anything a plugin is allowed to resolve from IPluginContext.Services goes here.
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();

        await using var manager = new PluginManager(pluginsRoot, services, loggerFactory);

        manager.PluginStateChanged += (_, e) =>
            Console.WriteLine($"  [event] {e.PluginId} -> {e.State}");

        Console.WriteLine($"Plugman console host. Plugins root: {pluginsRoot}");
        Console.WriteLine($"Host UI capabilities: {Describe(manager.HostUiCapabilities)}");
        Console.WriteLine();

        await manager.ScanAsync().ConfigureAwait(false);
        await manager.LoadEnabledAsync().ConfigureAwait(false);

        if (args.Contains("--demo"))
            return await RunDemoAsync(manager).ConfigureAwait(false);

        PrintPlugins(manager);
        Console.WriteLine();
        PrintHelp();

        await RunReplAsync(manager).ConfigureAwait(false);
        return 0;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>
    /// A scripted end-to-end run: discover, invoke, survive a throwing plugin, disable with a
    /// real unload, and enable again.
    /// </summary>
    private static async Task<int> RunDemoAsync(PluginManager manager)
    {
        PrintPlugins(manager);

        // Deliberately work from descriptors, never from a plugin instance held in a local.
        // A local that still references a plugin keeps its collectible load context rooted for
        // the lifetime of the enclosing method (in a debug build the JIT will not shorten a
        // local's lifetime), and the unload below would silently never complete.
        var id = manager.DiscoveredPlugins.FirstOrDefault(p => p is { IsLoaded: true, UiCapabilities.Count: 0 })?.Id;
        if (id is null)
        {
            Console.WriteLine("No logic plugin was loaded; build the sample plugins first.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"--- invoking {id} ---");

        await ExecuteAsync(manager, id, "echo", "hello from the host").ConfigureAwait(false);
        await ExecuteAsync(manager, id, "time", null).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("--- a capability call that throws (the host must survive) ---");
        await ExecuteAsync(manager, id, "boom", null).ConfigureAwait(false);
        Console.WriteLine($"Host still running. Recorded error: {manager.GetDescriptor(id)?.LoadError}");

        Console.WriteLine();
        Console.WriteLine("--- disable, then prove the load context was collected ---");
        await manager.DisableAsync(id).ConfigureAwait(false);

        var collected = await manager.WaitForUnloadAsync(id, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Console.WriteLine($"Load context collected: {collected}");
        Console.WriteLine($"plugin.json now says enabled=false: {manager.GetDescriptor(id)!.IsEnabled == false}");

        Console.WriteLine();
        Console.WriteLine("--- enable again ---");
        var descriptor = await manager.EnableAsync(id).ConfigureAwait(false);
        Console.WriteLine($"{descriptor.Id} loaded={descriptor.IsLoaded} error={descriptor.LoadError?.Message ?? "none"}");

        return 0;
    }

    // ------------------------------------------------------------------ repl

    private static async Task RunReplAsync(PluginManager manager)
    {
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null)
                return;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "exit" or "quit":
                        return;

                    case "help":
                        PrintHelp();
                        break;

                    case "list":
                        PrintPlugins(manager);
                        break;

                    case "commands":
                        PrintCommands(manager);
                        break;

                    case "scan":
                        await manager.ScanAsync().ConfigureAwait(false);
                        PrintPlugins(manager);
                        break;

                    case "load" when parts.Length > 1:
                        Report(await manager.LoadAsync(parts[1]).ConfigureAwait(false));
                        break;

                    case "unload" when parts.Length > 1:
                        await manager.UnloadAsync(parts[1]).ConfigureAwait(false);
                        Console.WriteLine($"Collected: {await manager.WaitForUnloadAsync(parts[1], TimeSpan.FromSeconds(5)).ConfigureAwait(false)}");
                        break;

                    case "enable" when parts.Length > 1:
                        Report(await manager.EnableAsync(parts[1]).ConfigureAwait(false));
                        break;

                    case "disable" when parts.Length > 1:
                        Report(await manager.DisableAsync(parts[1]).ConfigureAwait(false));
                        break;

                    case "run" when parts.Length > 2:
                        await ExecuteAsync(
                            manager,
                            parts[1],
                            parts[2],
                            parts.Length > 3 ? string.Join(' ', parts[3..]) : null).ConfigureAwait(false);
                        break;

                    case "interactive" when parts.Length > 1:
                        await RunInteractiveAsync(manager, parts[1]).ConfigureAwait(false);
                        break;

                    default:
                        Console.WriteLine("Unrecognized. Type 'help'.");
                        break;
                }
            }
            catch (PluginNotFoundException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Note the shape: the host calls a plugin capability as an ordinary typed method call.
    /// No reflection, and the try/catch lives inside the manager.
    /// </summary>
    private static async Task ExecuteAsync(PluginManager manager, string pluginId, string command, string? text)
    {
        var args = text is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["text"] = text };

        var result = await manager.InvokeAsync<ICommandPlugin, string>(
            pluginId,
            (plugin, token) => plugin.ExecuteAsync(command, args, token)).ConfigureAwait(false);

        Console.WriteLine(result.Success
            ? $"{command}: {result.Value}"
            : $"{command} failed: {result.Error?.Message}");
    }

    private static async Task RunInteractiveAsync(PluginManager manager, string pluginId)
    {
        if (manager.GetPluginContext(pluginId) is not { } context)
        {
            Console.WriteLine($"Plugin '{pluginId}' is not loaded.");
            return;
        }

        var result = await manager.InvokeAsync<IConsoleUiPlugin, bool>(
            pluginId,
            async (plugin, token) =>
            {
                await plugin.RunInteractiveAsync(context, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        if (!result.Success)
            Console.WriteLine($"Interactive session failed: {result.Error?.Message}");
    }

    private static void PrintPlugins(PluginManager manager)
    {
        Console.WriteLine();
        Console.WriteLine($"{"ID",-32} {"VERSION",-9} {"ENABLED",-8} {"LOADED",-7} {"UI",-8} STATUS");
        Console.WriteLine(new string('-', 100));

        foreach (var plugin in manager.DiscoveredPlugins.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"{plugin.Id,-32} {plugin.Metadata.Version,-9} {plugin.IsEnabled,-8} {plugin.IsLoaded,-7} " +
                $"{Describe(plugin.UiCapabilities),-8} {plugin.LoadError?.Message ?? "ok"}");
        }
    }

    private static void PrintCommands(PluginManager manager)
    {
        foreach (var plugin in manager.GetPlugins<ICommandPlugin>())
            Console.WriteLine($"{plugin.Metadata.Id}: {string.Join(", ", plugin.Commands)}");
    }

    private static void Report(PluginDescriptor descriptor) =>
        Console.WriteLine($"{descriptor.Id}: loaded={descriptor.IsLoaded} enabled={descriptor.IsEnabled} error={descriptor.LoadError?.Message ?? "none"}");

    private static string Describe(IReadOnlyList<string> capabilities) =>
        capabilities.Count == 0 ? "-" : string.Join("+", capabilities);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Commands:
              list                       show every discovered plugin
              commands                   show commands offered by loaded ICommandPlugins
              scan                       rescan the plugins folder (drop a new plugin in and try it)
              load <id> / unload <id>    load or unload without changing the persisted state
              enable <id> / disable <id> persist to plugin.json and load/unload
              run <id> <command> [text]  invoke an ICommandPlugin capability
              interactive <id>           hand the console to an IConsoleUiPlugin
              exit
            """);
    }
}
