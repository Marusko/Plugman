using System.Text.Json.Nodes;
using Plugman.Core;

namespace Plugman.Tests.Core;

public class DependencyTests
{
    [Fact]
    public async Task A_dependency_is_loaded_before_the_plugin_that_needs_it()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");
        folder.AddFixture("DependentPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        await host.Manager.LoadAsync("fixture.dependent");

        Assert.True(host.Manager.GetDescriptor("fixture.good")!.IsLoaded);
        Assert.True(host.Manager.GetDescriptor("fixture.dependent")!.IsLoaded);

        var loadOrder = host.Events.Where(e => e.State == PluginState.Loaded).Select(e => e.PluginId).ToArray();
        Assert.Equal(["fixture.good", "fixture.dependent"], loadOrder);
    }

    [Fact]
    public async Task A_missing_dependency_is_reported_and_the_plugin_stays_unloaded()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("DependentPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.dependent");

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Dependency, descriptor.LoadError!.Stage);
        Assert.Contains("fixture.good", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task A_dependency_that_fails_to_load_fails_its_dependents_too()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("ThrowingPlugin");
        folder.AddFixture("DependentPlugin", configureManifest: m => m["dependencies"] = new JsonArray("fixture.throwing"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.dependent");

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Dependency, descriptor.LoadError!.Stage);
        Assert.Contains("fixture.throwing", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task A_dependency_cycle_is_reported_instead_of_recursing_forever()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["dependencies"] = new JsonArray("fixture.dependent"));
        folder.AddFixture("DependentPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.dependent");

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Dependency, descriptor.LoadError!.Stage);
        Assert.Contains("cycle", descriptor.LoadError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadEnabled_loads_dependencies_regardless_of_folder_order()
    {
        using var folder = new TestPluginsFolder();

        // "A" sorts before "B", so the dependent is enumerated first and must pull its
        // dependency in itself rather than relying on scan order.
        folder.AddFixture("DependentPlugin", folderName: "A-Dependent");
        folder.AddFixture("GoodPlugin", folderName: "B-Good");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        Assert.All(host.Manager.DiscoveredPlugins, p => Assert.True(p.IsLoaded, p.LoadError?.Message));
    }
}
