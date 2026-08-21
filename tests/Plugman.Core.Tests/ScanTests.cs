using Plugman.Core;

namespace Plugman.Tests.Core;

public class ScanTests
{
    [Fact]
    public async Task Scan_discovers_a_plugin_folder()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = Assert.Single(host.Manager.DiscoveredPlugins);
        Assert.Equal("fixture.good", descriptor.Id);
        Assert.True(descriptor.IsEnabled);
        Assert.False(descriptor.IsLoaded);
        Assert.Null(descriptor.LoadError);
        Assert.Contains(("fixture.good", PluginState.Discovered), host.Events);
    }

    [Fact]
    public async Task Scan_uses_manifest_values_for_plugins_that_are_not_loaded()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = Assert.Single(host.Manager.DiscoveredPlugins);
        Assert.Equal("Good Fixture Plugin", descriptor.Metadata.Name);
        Assert.Equal("1.2.3", descriptor.Metadata.Version);
    }

    [Fact]
    public async Task Malformed_manifest_is_reported_and_does_not_abort_the_scan()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");
        folder.AddRawFolder("BrokenPlugin", "{ \"id\": \"oops\", this is not json");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        Assert.Equal(2, host.Manager.DiscoveredPlugins.Count);

        // The good one still loads.
        var good = host.Manager.DiscoveredPlugins.Single(p => p.Id == "fixture.good");
        Assert.Null(good.LoadError);

        // The broken one is listed under its folder name, with the failure attached.
        var broken = host.Manager.DiscoveredPlugins.Single(p => p.Id == "BrokenPlugin");
        Assert.NotNull(broken.LoadError);
        Assert.Equal(PluginLoadStage.Manifest, broken.LoadError!.Stage);
        Assert.Contains("Malformed JSON", broken.LoadError.Details ?? broken.LoadError.Message);
        Assert.Contains(("BrokenPlugin", PluginState.Failed), host.Events);
    }

    [Fact]
    public async Task Manifest_missing_required_fields_is_reported_as_a_manifest_error()
    {
        using var folder = new TestPluginsFolder();
        folder.AddRawFolder("NoId", """{ "entryAssembly": "Whatever.dll" }""");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = Assert.Single(host.Manager.DiscoveredPlugins);
        Assert.Equal(PluginLoadStage.Manifest, descriptor.LoadError!.Stage);
        Assert.Contains("'id'", descriptor.LoadError.Details ?? descriptor.LoadError.Message);
    }

    [Fact]
    public async Task Unknown_ui_capability_is_reported_rather_than_silently_ignored()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["uiCapabilities"] = new System.Text.Json.Nodes.JsonArray("winforms"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = Assert.Single(host.Manager.DiscoveredPlugins);
        Assert.Equal(PluginLoadStage.Manifest, descriptor.LoadError!.Stage);
        Assert.Contains("winforms", descriptor.LoadError.Details ?? descriptor.LoadError.Message);
    }

    [Fact]
    public async Task Folder_without_a_manifest_is_skipped_silently()
    {
        using var folder = new TestPluginsFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "NotAPluginFolder"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        Assert.Empty(host.Manager.DiscoveredPlugins);
    }

    [Fact]
    public async Task A_plugin_dropped_in_at_runtime_is_discovered_by_a_rescan()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        Assert.Single(host.Manager.DiscoveredPlugins);

        // No restart: copy a second plugin into the running host's plugins root and rescan.
        folder.AddFixture("ThrowingPlugin");
        await host.Manager.ScanAsync();

        Assert.Equal(2, host.Manager.DiscoveredPlugins.Count);
        Assert.Contains(host.Manager.DiscoveredPlugins, p => p.Id == "fixture.throwing");
    }

    [Fact]
    public async Task A_folder_that_disappears_is_dropped_on_the_next_scan()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        folder.DeleteFolder("GoodPlugin");
        await host.Manager.ScanAsync();

        Assert.Empty(host.Manager.DiscoveredPlugins);
        Assert.Contains(("fixture.good", PluginState.Removed), host.Events);
    }

    [Fact]
    public async Task The_data_directory_is_not_mistaken_for_a_plugin()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");
        Directory.CreateDirectory(Path.Combine(folder.Root, ".data"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        Assert.Single(host.Manager.DiscoveredPlugins);
    }

    [Fact]
    public async Task Auto_load_option_loads_enabled_plugins_during_the_scan()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder, configure: o => o.AutoLoadEnabledPlugins = true);
        await host.Manager.ScanAsync();

        Assert.True(host.Manager.DiscoveredPlugins.Single().IsLoaded);
    }

    [Fact]
    public async Task Disabled_plugins_are_discovered_but_not_loaded()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["enabled"] = false);

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        var descriptor = Assert.Single(host.Manager.DiscoveredPlugins);
        Assert.False(descriptor.IsEnabled);
        Assert.False(descriptor.IsLoaded);
    }
}
