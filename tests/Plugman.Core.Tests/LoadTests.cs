using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Plugman.Contracts;
using Plugman.Core;

namespace Plugman.Tests.Core;

public class LoadTests
{
    [Fact]
    public async Task Loading_activates_and_initializes_the_plugin()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.True(descriptor.IsLoaded);
        Assert.Null(descriptor.LoadError);
        Assert.Equal("Good Fixture Plugin", descriptor.Metadata.Name);
        Assert.Contains(("fixture.good", PluginState.Loaded), host.Events);

        var result = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            "fixture.good",
            (plugin, ct) => plugin.ExecuteAsync("echo", new Dictionary<string, string> { ["text"] = "hi" }, ct));

        Assert.True(result.Success);
        Assert.Equal("hi", result.Value);
    }

    [Fact]
    public async Task The_plugin_assembly_is_loaded_into_its_own_collectible_context()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        var plugin = host.Manager.GetPlugin<IPlugin>("fixture.good");
        Assert.NotNull(plugin);

        var context = AssemblyLoadContext.GetLoadContext(plugin!.GetType().Assembly);
        Assert.NotNull(context);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.True(context!.IsCollectible);
        Assert.Equal("fixture.good", context.Name);
    }

    /// <summary>
    /// The cross-context type identity check. If the plugin had loaded its own copy of
    /// Plugman.Contracts, the interface it implements would be a different Type with the same
    /// name and every cast in the manager (and in the host) would throw InvalidCastException.
    /// </summary>
    [Fact]
    public async Task The_contract_interface_has_a_single_type_identity_across_contexts()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        var plugin = host.Manager.GetPlugin<IPlugin>("fixture.good");
        Assert.NotNull(plugin);

        // Plugin type comes from the isolated context...
        Assert.NotSame(typeof(IPlugin).Assembly, plugin!.GetType().Assembly);

        // ...but the contract it implements is literally the host's Type object.
        var implemented = plugin.GetType().GetInterfaces().Single(i => i.FullName == typeof(IPlugin).FullName);
        Assert.Same(typeof(IPlugin), implemented);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(implemented.Assembly));
    }

    [Fact]
    public async Task An_assembly_without_an_IPlugin_implementation_records_an_activation_error()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("NotAPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.notaplugin");

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.Activation, descriptor.LoadError!.Stage);
        Assert.Contains("does not implement IPlugin", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task An_assembly_with_no_plugin_type_at_all_is_reported_when_entryType_is_omitted()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("NotAPlugin", configureManifest: m => m.Remove("entryType"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.notaplugin");

        Assert.Equal(PluginLoadStage.Activation, descriptor.LoadError!.Stage);
        Assert.Contains("no public type implementing IPlugin", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task A_missing_entry_type_is_reported()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["entryType"] = "GoodPlugin.NoSuchType");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.Equal(PluginLoadStage.Activation, descriptor.LoadError!.Stage);
        Assert.Contains("NoSuchType", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task A_missing_entry_assembly_is_reported()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["entryAssembly"] = "Missing.dll");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.Equal(PluginLoadStage.Assembly, descriptor.LoadError!.Stage);
        Assert.Contains("Missing.dll", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task Entry_type_is_found_automatically_when_the_manifest_omits_it()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m.Remove("entryType"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        Assert.True((await host.Manager.LoadAsync("fixture.good")).IsLoaded);
    }

    [Fact]
    public async Task A_plugin_that_throws_from_InitializeAsync_does_not_crash_the_host()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("ThrowingPlugin");
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        var thrower = host.Manager.GetDescriptor("fixture.throwing")!;
        Assert.False(thrower.IsLoaded);
        Assert.Equal(PluginLoadStage.Initialize, thrower.LoadError!.Stage);
        Assert.Contains("fixture initialization failure", thrower.LoadError.Message);

        // The failed load is rolled back, and the healthy plugin is unaffected.
        Assert.Null(host.Manager.GetPlugin<IPlugin>("fixture.throwing"));
        Assert.True(host.Manager.GetDescriptor("fixture.good")!.IsLoaded);
    }

    [Fact]
    public async Task A_failed_initialization_releases_the_load_context()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("ThrowingPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.throwing");

        Assert.True(await host.Manager.WaitForUnloadAsync("fixture.throwing", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Loading_twice_is_a_no_op()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        await host.Manager.LoadAsync("fixture.good");
        var second = await host.Manager.LoadAsync("fixture.good");

        Assert.True(second.IsLoaded);
        Assert.Equal(1, host.Events.Count(e => e == ("fixture.good", PluginState.Loaded)));
    }

    [Fact]
    public async Task An_unknown_plugin_id_throws_PluginNotFound()
    {
        using var folder = new TestPluginsFolder();

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        await Assert.ThrowsAsync<PluginNotFoundException>(() => host.Manager.LoadAsync("nope"));
    }

    [Fact]
    public async Task The_manifest_id_wins_over_the_id_the_plugin_reports()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["id"] = "renamed.in.manifest");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("renamed.in.manifest");

        // Descriptor.Id must stay the key the manager is addressable by.
        Assert.True(descriptor.IsLoaded);
        Assert.Equal("renamed.in.manifest", descriptor.Id);
        Assert.NotNull(host.Manager.GetPlugin<IPlugin>("renamed.in.manifest"));
    }

    [Fact]
    public async Task Each_plugin_gets_its_own_data_directory_created_before_initialize()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadAsync("fixture.good");

        var result = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            "fixture.good",
            (plugin, ct) => plugin.ExecuteAsync("datadir", new Dictionary<string, string>(), ct));

        Assert.True(result.Success);
        Assert.True(Directory.Exists(result.Value));
        Assert.EndsWith("fixture.good", result.Value);
    }

    [Fact]
    public async Task GetPlugins_returns_only_loaded_plugins_of_the_requested_capability()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");
        folder.AddFixture("DependentPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        // DependentPlugin implements IPlugin only; GoodPlugin also implements ICommandPlugin.
        Assert.Equal(2, CountPlugins<IPlugin>(host));
        Assert.Equal(1, CountPlugins<ICommandPlugin>(host));
    }

    // Counting behind a NoInlining helper keeps plugin instances out of the calling test's
    // frame, so tests that assert on unloading later are not sabotaged by a lingering local.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountPlugins<T>(TestHost host) where T : class, IPlugin =>
        host.Manager.GetPlugins<T>().Count();
}
