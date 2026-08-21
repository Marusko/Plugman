using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plugman.Core;

namespace Plugman.Tests;

/// <summary>
/// A manager wired up over a <see cref="TestPluginsFolder"/>, plus the recorded state-change
/// events so tests can assert on the lifecycle without polling.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;

    public TestHost(
        TestPluginsFolder folder,
        IReadOnlyList<string>? hostUiCapabilities = null,
        Action<PluginManagerOptions>? configure = null,
        ILoggerFactory? loggerFactory = null)
    {
        Folder = folder;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        Services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();

        // Explicit capabilities keep tests deterministic: they must not depend on whether the
        // test runner process happens to have WPF or ASP.NET Core assemblies loaded.
        var options = new PluginManagerOptions
        {
            HostUiCapabilities = hostUiCapabilities ?? [],
            PluginDataRootDirectory = Path.Combine(folder.Root, ".data")
        };

        configure?.Invoke(options);

        Manager = new PluginManager(folder.Root, Services, _loggerFactory, options);
        Manager.PluginStateChanged += (_, e) => Events.Add((e.PluginId, e.State));
    }

    public TestPluginsFolder Folder { get; }

    public PluginManager Manager { get; }

    public ServiceProvider Services { get; }

    public List<(string PluginId, PluginState State)> Events { get; } = [];

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Services.DisposeAsync();

        if (!ReferenceEquals(_loggerFactory, NullLoggerFactory.Instance))
            _loggerFactory.Dispose();
    }
}
