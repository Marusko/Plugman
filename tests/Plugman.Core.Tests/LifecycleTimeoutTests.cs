using System.Diagnostics;
using Plugman.Contracts;
using Plugman.Core;

namespace Plugman.Tests.Core;

/// <summary>
/// Lifecycle calls are made while the manager holds its lock, so a plugin that never returns
/// would take every later operation down with it. These tests use a fixture that blocks
/// forever and ignores its cancellation token.
/// </summary>
public class LifecycleTimeoutTests
{
    [Fact]
    public async Task A_plugin_that_never_returns_from_Initialize_is_abandoned()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("HangingPlugin");

        await using var host = new TestHost(folder, configure: o => o.InitializeTimeout = TimeSpan.FromSeconds(1));
        await host.Manager.ScanAsync();

        var stopwatch = Stopwatch.StartNew();
        var descriptor = await host.Manager.LoadAsync("fixture.hanging.init");
        stopwatch.Stop();

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Initialize, descriptor.LoadError!.Stage);
        Assert.Contains("did not complete", descriptor.LoadError.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Load took {stopwatch.Elapsed}, so the timeout did not hold.");
    }

    /// <summary>The point of the timeout: the manager is still usable afterwards.</summary>
    [Fact]
    public async Task The_manager_still_works_after_abandoning_a_hung_plugin()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("HangingPlugin");
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder, configure: o => o.InitializeTimeout = TimeSpan.FromSeconds(1));
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        Assert.False(host.Manager.GetDescriptor("fixture.hanging.init")!.IsLoaded);

        // The gate was released, so everything else keeps working.
        Assert.True(host.Manager.GetDescriptor("fixture.good")!.IsLoaded);

        var result = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            "fixture.good",
            (plugin, ct) => plugin.ExecuteAsync("echo", new Dictionary<string, string> { ["text"] = "alive" }, ct));

        Assert.Equal("alive", result.Value);

        await host.Manager.ScanAsync();
        await host.Manager.DisableAsync("fixture.good");
        Assert.False(host.Manager.GetDescriptor("fixture.good")!.IsEnabled);
    }

    [Fact]
    public async Task A_plugin_that_never_returns_from_Shutdown_does_not_block_the_unload()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("HangingPlugin", folderName: "HangingShutdown", configureManifest: manifest =>
        {
            manifest["id"] = "fixture.hanging.shutdown";
            manifest["entryType"] = "HangingPlugin.HangingShutdownPlugin";
        });

        await using var host = new TestHost(folder, configure: o => o.ShutdownTimeout = TimeSpan.FromSeconds(1));
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.hanging.shutdown");

        var stopwatch = Stopwatch.StartNew();
        var descriptor = await host.Manager.DisableAsync("fixture.hanging.shutdown");
        stopwatch.Stop();

        Assert.False(descriptor.IsLoaded);
        Assert.False(descriptor.IsEnabled);
        Assert.Equal(PluginLoadStage.Shutdown, descriptor.LoadError!.Stage);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Disable took {stopwatch.Elapsed}.");

        // The manifest was still persisted, so the plugin stays disabled across a restart.
        Assert.False(folder.ReadManifest("HangingShutdown")["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_well_behaved_plugin_is_not_delayed_by_the_timeout()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder, configure: o => o.InitializeTimeout = TimeSpan.FromSeconds(30));
        await host.Manager.ScanAsync();

        var stopwatch = Stopwatch.StartNew();
        var descriptor = await host.Manager.LoadAsync("fixture.good");
        stopwatch.Stop();

        Assert.True(descriptor.IsLoaded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Load took {stopwatch.Elapsed}; the timeout path is being waited on.");
    }
}
