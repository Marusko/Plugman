using System.Runtime.CompilerServices;
using Plugman.Contracts;
using Plugman.Core;

namespace Plugman.Tests.Core;

public class EnableDisableTests
{
    [Fact]
    public async Task Disable_persists_to_the_manifest_and_unloads()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        var descriptor = await host.Manager.DisableAsync("fixture.good");

        Assert.False(descriptor.IsEnabled);
        Assert.False(descriptor.IsLoaded);
        Assert.False(folder.ReadManifest("GoodPlugin")["enabled"]!.GetValue<bool>());
        Assert.Contains(("fixture.good", PluginState.Unloaded), host.Events);
        Assert.Contains(("fixture.good", PluginState.Disabled), host.Events);
    }

    [Fact]
    public async Task Enable_persists_to_the_manifest_and_loads()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["enabled"] = false);

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.EnableAsync("fixture.good");

        Assert.True(descriptor.IsEnabled);
        Assert.True(descriptor.IsLoaded);
        Assert.True(folder.ReadManifest("GoodPlugin")["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Enable_disable_round_trips()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        await host.Manager.EnableAsync("fixture.good");
        Assert.True(host.Manager.GetDescriptor("fixture.good")!.IsLoaded);

        await host.Manager.DisableAsync("fixture.good");
        Assert.False(host.Manager.GetDescriptor("fixture.good")!.IsLoaded);

        await host.Manager.EnableAsync("fixture.good");
        Assert.True(host.Manager.GetDescriptor("fixture.good")!.IsLoaded);
        Assert.True(folder.ReadManifest("GoodPlugin")["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_manifest_is_the_source_of_truth_on_the_next_start()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using (var first = new TestHost(folder))
        {
            await first.Manager.ScanAsync();
            await first.Manager.DisableAsync("fixture.good");
        }

        // A brand new manager over the same folder: the persisted state must survive.
        await using var second = new TestHost(folder);
        await second.Manager.ScanAsync();
        await second.Manager.LoadEnabledAsync();

        var descriptor = Assert.Single(second.Manager.DiscoveredPlugins);
        Assert.False(descriptor.IsEnabled);
        Assert.False(descriptor.IsLoaded);
    }

    [Fact]
    public async Task Persisting_the_enabled_flag_preserves_the_rest_of_the_manifest()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["customVendorKey"] = "keep me");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.DisableAsync("fixture.good");

        var manifest = folder.ReadManifest("GoodPlugin");
        Assert.Equal("keep me", manifest["customVendorKey"]!.GetValue<string>());
        Assert.Equal("GoodPlugin.dll", manifest["entryAssembly"]!.GetValue<string>());
        Assert.False(manifest["enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// The unload proof: after DisableAsync the plugin's collectible load context must actually
    /// be collected, not merely marked for unload.
    /// </summary>
    [Fact]
    public async Task Disable_really_unloads_the_assembly()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var probe = await LoadAndProbeAsync(host);
        Assert.True(probe.IsAlive);

        await host.Manager.DisableAsync("fixture.good");

        Assert.True(
            await host.Manager.WaitForUnloadAsync("fixture.good", TimeSpan.FromSeconds(10)),
            "The plugin's load context was still alive after disabling it.");

        Assert.False(probe.IsAlive, "The plugin assembly itself was not collected.");
    }

    [Fact]
    public async Task Unload_without_disabling_leaves_the_plugin_enabled()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");
        await host.Manager.UnloadAsync("fixture.good");

        var descriptor = host.Manager.GetDescriptor("fixture.good")!;
        Assert.True(descriptor.IsEnabled);
        Assert.False(descriptor.IsLoaded);
        Assert.True(folder.ReadManifest("GoodPlugin")["enabled"]!.GetValue<bool>());
        Assert.True(await host.Manager.WaitForUnloadAsync("fixture.good", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Disposing_the_manager_unloads_everything()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        await host.DisposeAsync();

        Assert.Contains(("fixture.good", PluginState.Unloaded), host.Events);
    }

    /// <summary>
    /// Loads the plugin and returns a weak reference to its assembly, deliberately leaving no
    /// strong reference behind in the caller's frame — a local holding a plugin instance is
    /// enough to keep a collectible context alive for the rest of the enclosing method.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndProbeAsync(TestHost host)
    {
        await host.Manager.LoadAsync("fixture.good");

        var plugin = host.Manager.GetPlugin<IPlugin>("fixture.good");
        Assert.NotNull(plugin);

        return new WeakReference(plugin!.GetType().Assembly, trackResurrection: true);
    }
}
