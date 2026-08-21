using System.Text.Json.Nodes;
using Plugman.Core;

namespace Plugman.Tests.Core;

public class HostCapabilityTests
{
    /// <summary>
    /// The fail-fast case from the design: a Blazor plugin dropped into a WPF-only host. The
    /// manifest alone is enough to reject it, so no assembly is touched and the host gets a
    /// descriptive error instead of a TypeLoadException from somewhere inside activation.
    /// </summary>
    [Fact]
    public async Task A_blazor_plugin_in_a_wpf_only_host_produces_a_clear_load_error()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["uiCapabilities"] = new JsonArray("blazor"));

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Wpf]);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.False(descriptor.IsLoaded);
        Assert.Equal(PluginLoadStage.HostCapability, descriptor.LoadError!.Stage);
        Assert.Contains("blazor", descriptor.LoadError.Message);
        Assert.Contains("This host supports: wpf", descriptor.LoadError.Message);
        Assert.Null(descriptor.LoadError.ExceptionType);
        Assert.Contains(("fixture.good", PluginState.Failed), host.Events);
    }

    [Fact]
    public async Task A_wpf_plugin_in_a_logic_only_host_is_rejected_with_a_readable_message()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["uiCapabilities"] = new JsonArray("wpf"));

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.Equal(PluginLoadStage.HostCapability, descriptor.LoadError!.Stage);
        Assert.Contains("none (logic plugins only)", descriptor.LoadError.Message);
    }

    [Fact]
    public async Task A_ui_plugin_loads_in_a_host_that_declares_the_capability()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin", configureManifest: m => m["uiCapabilities"] = new JsonArray("blazor"));

        await using var host = new TestHost(folder, hostUiCapabilities: [PluginUiCapability.Blazor]);
        await host.Manager.ScanAsync();

        var descriptor = await host.Manager.LoadAsync("fixture.good");

        Assert.True(descriptor.IsLoaded);
        Assert.Null(descriptor.LoadError);
        Assert.Equal(["blazor"], descriptor.UiCapabilities);
    }

    [Fact]
    public async Task A_logic_plugin_loads_in_every_host()
    {
        using var folder = new TestPluginsFolder();
        folder.AddFixture("GoodPlugin");

        await using var host = new TestHost(folder);
        await host.Manager.ScanAsync();

        Assert.True((await host.Manager.LoadAsync("fixture.good")).IsLoaded);
    }

    [Fact]
    public void The_default_probe_reports_no_ui_capabilities_in_a_plain_test_process()
    {
        var probe = new DefaultHostCapabilityProbe();

        // This test process references neither WPF nor Blazor.
        Assert.False(probe.Supports(PluginUiCapability.Wpf));
        Assert.False(probe.Supports(PluginUiCapability.Blazor));
        Assert.False(probe.Supports("winforms"));
    }

    [Fact]
    public void Probe_assembly_names_match_the_shared_assembly_lists()
    {
        Assert.Equal("PresentationFramework", SharedAssemblies.ProbeAssemblyFor(PluginUiCapability.Wpf));
        Assert.Equal("Microsoft.AspNetCore.Components", SharedAssemblies.ProbeAssemblyFor(PluginUiCapability.Blazor));

        Assert.Contains(SharedAssemblies.ProbeAssemblyFor(PluginUiCapability.Wpf), SharedAssemblies.Wpf);
        Assert.Contains(SharedAssemblies.ProbeAssemblyFor(PluginUiCapability.Blazor), SharedAssemblies.Blazor);
    }
}
