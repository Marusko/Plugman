using System.Reflection;
using Plugman.Contracts;
using Plugman.Core;

namespace Plugman.Tests.Core;

public class IsolationTests
{
    /// <summary>
    /// Two plugins, each carrying its own version of the same private dependency assembly.
    /// Without per-plugin load contexts this is the classic FileLoadException: same simple
    /// name, different versions, one process.
    /// </summary>
    [Fact]
    public async Task Two_plugins_with_conflicting_private_dependency_versions_coexist()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("ConflictPluginA");
        folder.AddFixture("ConflictPluginB");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();
        await host.Manager.LoadEnabledAsync();

        Assert.All(host.Manager.DiscoveredPlugins, p => Assert.Null(p.LoadError));

        var a = await DepVersionAsync(host, "fixture.conflict.a");
        var b = await DepVersionAsync(host, "fixture.conflict.b");

        Assert.Equal("ConflictDep 1.0.0 from 1.0.0.0", a);
        Assert.Equal("ConflictDep 2.0.0 from 2.0.0.0", b);
    }

    [Fact]
    public async Task Each_plugin_resolves_its_private_dependency_from_its_own_folder()
    {
        using var folder = new TestPluginsFolder();
        var pluginFolder = folder.AddFixture("ConflictPluginA");

        var context = new PluginLoadContext(
            "test",
            Path.Combine(pluginFolder, "ConflictPluginA.dll"),
            SharedAssemblies.Build());

        try
        {
            var resolved = context.ResolveAssemblyPath(new AssemblyName("ConflictDep"));

            Assert.NotNull(resolved);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(pluginFolder, "ConflictDep.dll")),
                Path.GetFullPath(resolved!));
        }
        finally
        {
            context.Unload();
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The fall-through rule: an assembly on the shared list is never resolved out of the
    /// plugin folder, even when the plugin folder physically contains it and its deps.json
    /// names it. Everything else keeps resolving privately.
    /// </summary>
    [Fact]
    public void The_shared_allow_list_is_checked_ahead_of_the_dependency_resolver()
    {
        using var folder = new TestPluginsFolder();
        var pluginFolder = folder.AddFixture("ConflictPluginA");
        var mainAssembly = Path.Combine(pluginFolder, "ConflictPluginA.dll");

        var isolating = new PluginLoadContext("isolating", mainAssembly, SharedAssemblies.Build());
        var sharing = new PluginLoadContext(
            "sharing",
            mainAssembly,
            SharedAssemblies.Build(additional: ["ConflictDep"]));

        try
        {
            // Not shared: resolved from the plugin folder.
            Assert.False(isolating.IsShared("ConflictDep"));
            Assert.NotNull(isolating.ResolveAssemblyPath(new AssemblyName("ConflictDep")));

            // Shared: the resolver is never consulted, so the host's copy is used instead.
            Assert.True(sharing.IsShared("ConflictDep"));
            Assert.Null(sharing.ResolveAssemblyPath(new AssemblyName("ConflictDep")));

            // Contracts are always shared.
            Assert.True(isolating.IsShared("Plugman.Contracts"));
            Assert.True(isolating.IsShared("plugman.contracts"));
            Assert.Null(isolating.ResolveAssemblyPath(new AssemblyName("Plugman.Contracts")));

            // Framework assemblies are not in the plugin's deps.json, so they fall through too.
            Assert.Null(isolating.ResolveAssemblyPath(new AssemblyName("System.Text.Json")));
        }
        finally
        {
            isolating.Unload();
            sharing.Unload();
        }
    }

    [Fact]
    public void The_always_shared_set_covers_the_contract_surface()
    {
        var shared = SharedAssemblies.Build();

        Assert.Contains("Plugman.Contracts", shared);
        Assert.Contains("Microsoft.Extensions.Logging.Abstractions", shared);
        Assert.DoesNotContain("PresentationFramework", shared);
        Assert.DoesNotContain("Microsoft.AspNetCore.Components", shared);
    }

    [Fact]
    public void Declaring_wpf_shares_the_wpf_assemblies_and_nothing_else()
    {
        var shared = SharedAssemblies.Build([PluginUiCapability.Wpf]);

        Assert.Contains("Plugman.Contracts", shared);
        Assert.Contains("Plugman.Contracts.Wpf", shared);
        Assert.Contains("PresentationFramework", shared);
        Assert.Contains("PresentationCore", shared);
        Assert.Contains("WindowsBase", shared);
        Assert.DoesNotContain("Microsoft.AspNetCore.Components", shared);
    }

    [Fact]
    public void Declaring_blazor_shares_the_blazor_assemblies_and_nothing_else()
    {
        var shared = SharedAssemblies.Build([PluginUiCapability.Blazor]);

        Assert.Contains("Plugman.Contracts.Blazor", shared);
        Assert.Contains("Microsoft.AspNetCore.Components", shared);
        Assert.Contains("Microsoft.AspNetCore.Components.Web", shared);
        Assert.DoesNotContain("PresentationFramework", shared);
    }

    [Fact]
    public void A_plugin_may_declare_both_ui_capabilities()
    {
        var shared = SharedAssemblies.Build([PluginUiCapability.Wpf, PluginUiCapability.Blazor]);

        Assert.Contains("PresentationFramework", shared);
        Assert.Contains("Microsoft.AspNetCore.Components", shared);
    }

    [Fact]
    public void Additional_host_assemblies_can_be_shared()
    {
        var shared = SharedAssemblies.Build(additional: ["Contoso.HostContracts"]);

        Assert.Contains("Contoso.HostContracts", shared);
    }

    [Fact]
    public void An_unknown_capability_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SharedAssemblies.Build(["winforms"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SharedAssemblies.For("winforms"));
    }

    private static async Task<string?> DepVersionAsync(TestHost host, string pluginId)
    {
        var result = await host.Manager.InvokeAsync<ICommandPlugin, string>(
            pluginId,
            (plugin, ct) => plugin.ExecuteAsync("depversion", new Dictionary<string, string>(), ct));

        Assert.True(result.Success, result.Error?.Message);
        return result.Value;
    }
}
