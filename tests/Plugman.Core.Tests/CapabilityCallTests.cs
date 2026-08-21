using Plugman.Contracts;
using Plugman.Core;

namespace Plugman.Tests.Core;

public class CapabilityCallTests
{
    [Fact]
    public async Task A_capability_call_that_throws_is_captured_at_the_manager_boundary()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        var result = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            "fixture.good",
            (plugin, ct) => plugin.ExecuteAsync("boom", new Dictionary<string, string>(), ct));

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal(PluginLoadStage.Capability, result.Error!.Stage);
        Assert.Contains("fixture command failure", result.Error.Message);

        // Reported on the descriptor, and the plugin stays loaded and usable.
        var descriptor = host.Manager.GetDescriptor("fixture.good")!;
        Assert.True(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Capability, descriptor.LoadError!.Stage);
        Assert.Contains(("fixture.good", PluginState.Failed), host.Events);

        var next = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            "fixture.good",
            (plugin, ct) => plugin.ExecuteAsync("echo", new Dictionary<string, string> { ["text"] = "still here" }, ct));

        Assert.True(next.Success);
        Assert.Equal("still here", next.Value);
    }

    [Fact]
    public async Task A_synchronous_capability_call_that_throws_is_captured_too()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        // Stands in for IWpfUiPlugin.CreateView throwing while building a view.
        var result = host.Manager.TryInvoke<ICommandPlugin, string>(
            "fixture.good",
            _ => throw new InvalidOperationException("view construction failed"));

        Assert.False(result.Success);
        Assert.Equal(PluginLoadStage.Capability, result.Error!.Stage);
        Assert.Contains("view construction failed", result.Error.Message);
    }

    [Fact]
    public async Task Invoking_a_plugin_that_is_not_loaded_returns_a_failure_rather_than_throwing()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var result = host.Manager.TryInvoke<ICommandPlugin, string>("fixture.good", p => p.Metadata.Name);

        Assert.False(result.Success);
        Assert.Contains("not loaded", result.Error!.Message);
    }

    [Fact]
    public async Task Invoking_a_capability_the_plugin_does_not_implement_returns_a_failure()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("DependentPlugin", configureManifest: m => m.Remove("dependencies"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.dependent");

        var result = host.Manager.TryInvoke<ICommandPlugin, string>("fixture.dependent", p => p.Metadata.Name);

        Assert.False(result.Success);
        Assert.Contains("does not implement ICommandPlugin", result.Error!.Message);
    }

    [Fact]
    public async Task GetPluginContext_hands_back_the_context_the_plugin_was_initialized_with()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        var context = host.Manager.GetPluginContext("fixture.good");
        Assert.NotNull(context);
        Assert.True(Directory.Exists(context!.PluginDataDirectory));

        var reported = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            "fixture.good",
            (plugin, ct) => plugin.ExecuteAsync("datadir", new Dictionary<string, string>(), ct));

        Assert.Equal(context.PluginDataDirectory, reported.Value);

        await host.Manager.UnloadAsync("fixture.good");
        Assert.Null(host.Manager.GetPluginContext("fixture.good"));
    }

    [Fact]
    public async Task A_state_changed_handler_that_throws_does_not_break_the_manager()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        host.Manager.PluginStateChanged += (_, _) => throw new InvalidOperationException("bad handler");

        await host.Manager.ScanAsync();
        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.True(descriptor.IsLoaded);
    }
}
